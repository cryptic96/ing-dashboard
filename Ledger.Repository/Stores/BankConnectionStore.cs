using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.Repository.Conventions;
using Ledger.Repository.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Repository.Stores;

/// <summary>Stores linked connections and their accounts, keeping session ids only in protected form.</summary>
public class BankConnectionStore(LedgerDbContext dbContext) : IBankConnectionStore
{
    private static readonly TimeSpan CreationOrderStep = TimeSpan.FromTicks(10);

    /// <inheritdoc />
    public async Task<LinkedConnection> AddConnectionAsync(
        string provider,
        string aspspName,
        string aspspCountry,
        ProviderSession session,
        string protectedSessionId,
        DateTimeOffset authorizedAt,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var createdAt = DateTimeOffset.UtcNow;
            var connection = NewConnection(provider, aspspName, aspspCountry, session, protectedSessionId, authorizedAt, createdAt);

            dbContext.BankConnections.Add(connection);
            var attached = await AttachAccountsAsync(connection.Id, provider, session, createdAt, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);

            if (attached.Mapped > 0)
            {
                await SupersedeConnectionsWithoutAccountsAsync(connection.Id, provider, createdAt, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return new LinkedConnection(
                connection.Id,
                connection.ConnectionKey,
                attached.Accounts.Select(ToLinkedAccount).ToList());
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Marks every other live connection of the provider that no longer owns an account as superseded by the new one. Linking
    /// accounts the ledger already knew moves them off their old connection, which would otherwise stay active with its own
    /// consent expiry and nothing to sync.
    /// </summary>
    private async Task SupersedeConnectionsWithoutAccountsAsync(
        Guid newConnectionId,
        string provider,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await dbContext.BankConnections
            .Where(candidate => candidate.Id != newConnectionId
                && candidate.Provider == provider
                && (candidate.Status == BankConnectionEntity.Statuses.Active
                    || candidate.Status == BankConnectionEntity.Statuses.ProviderExpired)
                && !dbContext.Accounts.Any(account => account.BankConnectionId == candidate.Id))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, BankConnectionEntity.Statuses.Superseded)
                    .SetProperty(candidate => candidate.SupersededById, (Guid?)newConnectionId)
                    .SetProperty(candidate => candidate.ClosedAt, (DateTimeOffset?)at),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RenewalResult> ApplyRenewalAsync(
        Guid supersededConnectionId,
        string provider,
        string aspspName,
        string aspspCountry,
        ProviderSession session,
        string protectedSessionId,
        DateTimeOffset authorizedAt,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var createdAt = DateTimeOffset.UtcNow;
            var connection = NewConnection(provider, aspspName, aspspCountry, session, protectedSessionId, authorizedAt, createdAt);

            dbContext.BankConnections.Add(connection);
            var attached = await AttachAccountsAsync(connection.Id, provider, session, createdAt, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            var changed = await dbContext.BankConnections
                .Where(candidate => candidate.Id == supersededConnectionId
                    && (candidate.Status == BankConnectionEntity.Statuses.Active
                        || candidate.Status == BankConnectionEntity.Statuses.ProviderExpired))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, BankConnectionEntity.Statuses.Superseded)
                        .SetProperty(candidate => candidate.SupersededById, (Guid?)connection.Id)
                        .SetProperty(candidate => candidate.ClosedAt, (DateTimeOffset?)createdAt),
                    cancellationToken);

            if (changed != 1)
            {
                throw new InvalidOperationException("The connection to renew cannot be renewed.");
            }

            await transaction.CommitAsync(cancellationToken);

            var linked = new LinkedConnection(
                connection.Id,
                connection.ConnectionKey,
                attached.Accounts.Select(ToLinkedAccount).ToList());

            return new RenewalResult(linked, attached.Mapped, attached.Created);
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task MarkStatusAsync(
        Guid connectionId,
        ConnectionStatus status,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var text = EnumText.ToText(status);
        var closing = status is ConnectionStatus.Revoked or ConnectionStatus.Superseded;
        var allowedFrom = StatusesFrom(status);

        await dbContext.BankConnections
            .Where(connection => connection.Id == connectionId && allowedFrom.Contains(connection.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(connection => connection.Status, text)
                    .SetProperty(connection => connection.ClosedAt, connection => closing ? connection.ClosedAt ?? at : connection.ClosedAt),
                cancellationToken);
    }

    private static string[] StatusesFrom(ConnectionStatus target)
    {
        return target switch
        {
            ConnectionStatus.Active => [BankConnectionEntity.Statuses.ProviderExpired],
            ConnectionStatus.ProviderExpired => [BankConnectionEntity.Statuses.Active],
            ConnectionStatus.Revoked =>
            [
                BankConnectionEntity.Statuses.Active,
                BankConnectionEntity.Statuses.ProviderExpired,
                BankConnectionEntity.Statuses.Superseded
            ],
            _ => [BankConnectionEntity.Statuses.Active, BankConnectionEntity.Statuses.ProviderExpired]
        };
    }

    private static BankConnectionEntity NewConnection(
        string provider,
        string aspspName,
        string aspspCountry,
        ProviderSession session,
        string protectedSessionId,
        DateTimeOffset authorizedAt,
        DateTimeOffset createdAt)
    {
        return new BankConnectionEntity
        {
            Id = Guid.CreateVersion7(),
            ConnectionKey = OpaqueKey.New(),
            Provider = provider,
            AspspName = aspspName,
            AspspCountry = aspspCountry,
            Status = BankConnectionEntity.Statuses.Active,
            SessionIdProtected = protectedSessionId,
            AuthorizedAt = authorizedAt,
            ValidUntil = session.ValidUntil,
            CreatedAt = createdAt
        };
    }

    private async Task<AttachedAccounts> AttachAccountsAsync(
        Guid connectionId,
        string provider,
        ProviderSession session,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var accounts = new List<LedgerAccountEntity>();
        var mapped = 0;
        var created = 0;

        var distinctAccounts = session.Accounts
            .GroupBy(providerAccount => providerAccount.IdentificationHash, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        for (var index = 0; index < distinctAccounts.Count; index++)
        {
            var providerAccount = distinctAccounts[index];

            var account = await dbContext.Accounts.SingleOrDefaultAsync(
                existing => existing.Provider == provider && existing.IdentificationHash == providerAccount.IdentificationHash,
                cancellationToken);

            if (account is null)
            {
                account = new LedgerAccountEntity
                {
                    Id = Guid.CreateVersion7(),
                    AccountKey = OpaqueKey.New(),
                    Provider = provider,
                    IdentificationHash = providerAccount.IdentificationHash,
                    Kind = providerAccount.Kind,
                    Currency = providerAccount.Currency,
                    SyncEnabled = false,
                    CreatedAt = createdAt + (CreationOrderStep * index)
                };

                dbContext.Accounts.Add(account);
                created++;
            }
            else
            {
                mapped++;
            }

            account.BankConnectionId = connectionId;
            account.ProviderAccountUid = providerAccount.Uid;
            account.Iban = providerAccount.Iban;
            account.ProviderName = providerAccount.Name;
            account.Product = providerAccount.Product;
            accounts.Add(account);
        }

        return new AttachedAccounts(accounts, mapped, created);
    }

    private sealed record AttachedAccounts(List<LedgerAccountEntity> Accounts, int Mapped, int Created);

    /// <inheritdoc />
    public async Task SetAccountSelectionAsync(
        Guid connectionId,
        IReadOnlyList<AccountSelection> selections,
        CancellationToken cancellationToken)
    {
        var accounts = await dbContext.Accounts
            .Where(account => account.BankConnectionId == connectionId)
            .ToListAsync(cancellationToken);

        foreach (var selection in selections)
        {
            var account = accounts.SingleOrDefault(candidate => candidate.AccountKey == selection.AccountKey)
                ?? throw new InvalidOperationException("The selection names an account that does not belong to the connection.");

            account.DisplayName = selection.DisplayName;
            account.SyncEnabled = selection.SyncEnabled;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SyncTarget?> GetSyncTargetAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await dbContext.BankConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == connectionId, cancellationToken);

        if (connection is null)
        {
            return null;
        }

        var accounts = await dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.BankConnectionId == connectionId
                && account.SyncEnabled
                && account.ProviderAccountUid != null)
            .OrderBy(account => account.CreatedAt)
            .ThenBy(account => account.Id)
            .Select(account => new SyncAccount(account.Id, account.AccountKey, account.ProviderAccountUid!))
            .ToListAsync(cancellationToken);

        return new SyncTarget(
            connection.Id,
            connection.ConnectionKey,
            connection.Provider,
            connection.SessionIdProtected,
            accounts);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectionSummary>> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        var connections = await dbContext.BankConnections
            .AsNoTracking()
            .OrderByDescending(connection => connection.CreatedAt)
            .ThenByDescending(connection => connection.Id)
            .ToListAsync(cancellationToken);

        return connections.Select(ToSummary).ToList();
    }

    /// <inheritdoc />
    public async Task<ConnectionSummary?> FindConnectionAsync(string connectionKey, CancellationToken cancellationToken)
    {
        var connection = await dbContext.BankConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.ConnectionKey == connectionKey, cancellationToken);

        return connection is null ? null : ToSummary(connection);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LinkedAccount>> ListAccountsAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var accounts = await dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.BankConnectionId == connectionId)
            .OrderBy(account => account.CreatedAt)
            .ThenBy(account => account.Id)
            .ToListAsync(cancellationToken);

        return accounts.Select(ToLinkedAccount).ToList();
    }

    /// <inheritdoc />
    public Task<bool> HasAnySyncRunAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        return dbContext.SyncRuns.AnyAsync(run => run.BankConnectionId == connectionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<string?> GetProtectedSessionIdAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        return dbContext.BankConnections
            .AsNoTracking()
            .Where(connection => connection.Id == connectionId)
            .Select(connection => (string?)connection.SessionIdProtected)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static ConnectionSummary ToSummary(BankConnectionEntity connection)
    {
        return new ConnectionSummary(
            connection.Id,
            connection.ConnectionKey,
            connection.Provider,
            EnumText.Parse<ConnectionStatus>(connection.Status),
            connection.AuthorizedAt,
            connection.ValidUntil);
    }

    private static LinkedAccount ToLinkedAccount(LedgerAccountEntity account)
    {
        return new LinkedAccount(
            account.Id,
            account.AccountKey,
            account.Iban,
            account.ProviderName,
            account.Kind,
            account.Currency,
            account.DisplayName,
            account.SyncEnabled);
    }
}
