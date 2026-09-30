using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
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
        var createdAt = DateTimeOffset.UtcNow;

        var connection = new BankConnectionEntity
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

        dbContext.BankConnections.Add(connection);

        var accounts = new List<LedgerAccountEntity>();

        for (var index = 0; index < session.Accounts.Count; index++)
        {
            var providerAccount = session.Accounts[index];

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
                    Iban = providerAccount.Iban,
                    ProviderName = providerAccount.Name,
                    Product = providerAccount.Product,
                    Kind = providerAccount.Kind,
                    Currency = providerAccount.Currency,
                    SyncEnabled = false,
                    CreatedAt = createdAt + (CreationOrderStep * index)
                };

                dbContext.Accounts.Add(account);
            }

            account.BankConnectionId = connection.Id;
            account.ProviderAccountUid = providerAccount.Uid;
            accounts.Add(account);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return new LinkedConnection(
            connection.Id,
            connection.ConnectionKey,
            accounts.Select(ToLinkedAccount).ToList());
    }

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
