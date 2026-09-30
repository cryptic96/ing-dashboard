using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.Domain.Security;
using Microsoft.Extensions.Options;

namespace Ledger.Service.Ingestion;

/// <summary>The kinds of failure a bank link operation reports to its caller.</summary>
public enum BankLinkFailure
{
    /// <summary>No bank provider or redirect address is configured.</summary>
    NotConfigured,

    /// <summary>The named connection does not exist.</summary>
    NotFound,

    /// <summary>The request is malformed or names something that does not belong together.</summary>
    Invalid,

    /// <summary>The connection is in a state that does not allow the operation.</summary>
    Conflict,

    /// <summary>The bank data provider failed or returned something unusable.</summary>
    Provider,

    /// <summary>The background sync queue is full.</summary>
    Busy
}

/// <summary>A bank link operation failed in a way the caller should be told about. The message is safe to return to the caller.</summary>
public class BankLinkException(BankLinkFailure failure, string message) : Exception(message)
{
    /// <summary>What kind of failure happened.</summary>
    public BankLinkFailure Failure { get; } = failure;
}

/// <summary>The address to open to approve a consent and when the request stops being accepted.</summary>
public record LinkStart(Uri AuthorizationUrl, DateTimeOffset ExpiresAt);

/// <summary>How a bank redirect ended.</summary>
public enum CallbackResult
{
    /// <summary>The consent was recorded.</summary>
    Completed,

    /// <summary>The consent was approved but exposes no accounts.</summary>
    NoAccounts,

    /// <summary>The redirect could not be accepted, for any reason.</summary>
    Failed
}

/// <summary>The outcome of a bank redirect, carrying only the number of accounts found.</summary>
public record CallbackOutcome(CallbackResult Result, int AccountCount);

/// <summary>A connection together with its derived consent state.</summary>
public record ConnectionOverview(ConnectionSummary Connection, ConsentSnapshot Consent);

/// <summary>The result of saving an account selection: the accounts afterwards and whether a first sync was queued.</summary>
public record SelectionResult(IReadOnlyList<LinkedAccount> Accounts, bool FirstSyncQueued);

/// <summary>
/// The application layer of the guided bank link: starting a consent, completing it from the bank redirect and choosing the
/// accounts to sync. Every bank endpoint goes through this class, and so will every later client.
/// </summary>
public class BankLinkService(
    IBankDataProvider provider,
    IBankConnectionStore connections,
    IBankAuthorizationStore authorizations,
    ISecretProtector secretProtector,
    ISyncDispatcher dispatcher,
    IOptions<BankLinkOptions> options,
    TimeProvider timeProvider,
    ILogger<BankLinkService> logger)
{
    private const int MaxCodeLength = 2048;
    private const int MaxDisplayNameLength = 40;

    /// <summary>Starts a consent for a first link and returns the address the operator must open to approve it.</summary>
    public async Task<LinkStart> StartLinkAsync(CancellationToken cancellationToken)
    {
        return await StartAsync(AuthorizationPurposes.Link, null, cancellationToken);
    }

    /// <summary>Completes a bank redirect. Every failure is reported identically, with no detail about why it failed.</summary>
    public async Task<CallbackOutcome> CompleteAsync(
        string? state,
        string? code,
        string? error,
        PsuContext? psu,
        CancellationToken cancellationToken)
    {
        if (!LinkStateToken.TryHash(state, out var stateSha256) || code is { Length: > MaxCodeLength })
        {
            return Reject("malformed request");
        }

        var consumed = await authorizations.TryConsumeAsync(stateSha256, timeProvider.GetUtcNow(), cancellationToken);

        if (consumed is null)
        {
            return Reject("unknown, used or expired state");
        }

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            return Reject("redirect without a code");
        }

        ProviderSession session;

        try
        {
            session = await provider.CompleteAuthorizationAsync(code, cancellationToken);
        }
        catch (BankProviderException exception)
        {
            return Reject($"code exchange failed ({exception.Kind})");
        }

        if (session.Accounts.Count == 0)
        {
            logger.LogWarning("Bank link callback finished with outcome {Outcome}.", CallbackResult.NoAccounts);
            return new CallbackOutcome(CallbackResult.NoAccounts, 0);
        }

        var linked = await connections.AddConnectionAsync(
            provider.Name,
            options.Value.AspspName,
            options.Value.AspspCountry,
            session,
            secretProtector.Protect(session.SessionId),
            timeProvider.GetUtcNow(),
            cancellationToken);

        logger.LogInformation("Bank link callback finished with outcome {Outcome}.", CallbackResult.Completed);
        return new CallbackOutcome(CallbackResult.Completed, linked.Accounts.Count);
    }

    /// <summary>Lists every connection with its derived consent state. No session material is ever part of the result.</summary>
    public async Task<IReadOnlyList<ConnectionOverview>> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var all = await connections.ListConnectionsAsync(cancellationToken);

        return all
            .Select(connection => new ConnectionOverview(
                connection,
                ConsentState.Derive(connection.Status, connection.ValidUntil, now)))
            .ToList();
    }

    /// <summary>Lists the accounts of a connection without any secret, with the IBAN left for the caller to mask.</summary>
    public async Task<IReadOnlyList<LinkedAccount>> ListAccountsAsync(string connectionKey, CancellationToken cancellationToken)
    {
        var connection = await FindAsync(connectionKey, cancellationToken);
        return await connections.ListAccountsAsync(connection.Id, cancellationToken);
    }

    /// <summary>
    /// Saves which accounts to sync and what to call them. When the connection has never synced and at least one account is
    /// enabled, the first sync with the longest available history is queued straight away.
    /// </summary>
    public async Task<SelectionResult> SelectAccountsAsync(
        string connectionKey,
        IReadOnlyList<AccountSelection> selections,
        PsuContext? psu,
        CancellationToken cancellationToken)
    {
        var connection = await FindAsync(connectionKey, cancellationToken);

        if (connection.Status != ConnectionStatus.Active)
        {
            throw new BankLinkException(BankLinkFailure.Conflict, "Accounts can only be selected on an active connection.");
        }

        var existing = await connections.ListAccountsAsync(connection.Id, cancellationToken);
        var validated = Validate(selections, existing);

        await connections.SetAccountSelectionAsync(connection.Id, validated, cancellationToken);

        var accounts = await connections.ListAccountsAsync(connection.Id, cancellationToken);
        var needsFirstSync = accounts.Any(account => account.SyncEnabled)
            && !await connections.HasAnySyncRunAsync(connection.Id, cancellationToken);

        if (needsFirstSync && !dispatcher.TryEnqueue(new SyncRequest(connection.Id, SyncTrigger.PostLink, new FetchContext(psu))))
        {
            throw new BankLinkException(BankLinkFailure.Busy, "The sync queue is full. Repeat the call shortly.");
        }

        return new SelectionResult(accounts, needsFirstSync);
    }

    /// <summary>Starts a consent with the provider and records the pending authorisation under the hash of a fresh state.</summary>
    private async Task<LinkStart> StartAsync(string purpose, Guid? connectionId, CancellationToken cancellationToken)
    {
        if (provider is DisabledBankDataProvider || !TryGetRedirectUrl(out var redirectUrl))
        {
            throw new BankLinkException(BankLinkFailure.NotConfigured, "Bank linking is not configured.");
        }

        var (state, stateSha256) = LinkStateToken.Generate();
        AuthorizationStart start;

        try
        {
            start = await provider.StartAuthorizationAsync(new AuthorizationRequest(state, redirectUrl), cancellationToken);
        }
        catch (BankProviderException exception)
        {
            logger.LogWarning("Starting a bank authorisation failed with {Kind}.", exception.Kind);
            throw new BankLinkException(BankLinkFailure.Provider, "The bank provider could not start the authorisation.");
        }

        if (!start.AuthorizationUrl.IsAbsoluteUri || start.AuthorizationUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new BankLinkException(BankLinkFailure.Provider, "The bank provider returned an unusable authorisation address.");
        }

        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(options.Value.StateLifetimeMinutes);

        await authorizations.CreateAsync(
            new PendingAuthorization(stateSha256, purpose, connectionId, start.ProviderAuthorizationId, now, expiresAt),
            cancellationToken);

        return new LinkStart(start.AuthorizationUrl, expiresAt);
    }

    /// <summary>Finds a connection by its opaque key or reports that it does not exist.</summary>
    private async Task<ConnectionSummary> FindAsync(string connectionKey, CancellationToken cancellationToken)
    {
        return await connections.FindConnectionAsync(connectionKey, cancellationToken)
            ?? throw new BankLinkException(BankLinkFailure.NotFound, "The connection does not exist.");
    }

    private CallbackOutcome Reject(string reason)
    {
        logger.LogWarning("Bank link callback finished with outcome {Outcome}: {Reason}.", CallbackResult.Failed, reason);
        return new CallbackOutcome(CallbackResult.Failed, 0);
    }

    private bool TryGetRedirectUrl(out Uri redirectUrl)
    {
        var configured = options.Value.RedirectUrl;

        if (Uri.TryCreate(configured, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps)
        {
            redirectUrl = parsed;
            return true;
        }

        redirectUrl = null!;
        return false;
    }

    private static List<AccountSelection> Validate(IReadOnlyList<AccountSelection> selections, IReadOnlyList<LinkedAccount> existing)
    {
        if (selections.Count == 0)
        {
            throw new BankLinkException(BankLinkFailure.Invalid, "At least one account must be given.");
        }

        var knownKeys = existing.Select(account => account.AccountKey).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<AccountSelection>(selections.Count);

        foreach (var selection in selections)
        {
            if (string.IsNullOrEmpty(selection.AccountKey)
                || !knownKeys.Contains(selection.AccountKey)
                || !seen.Add(selection.AccountKey))
            {
                throw new BankLinkException(BankLinkFailure.Invalid, "Every account key must belong to the connection and appear once.");
            }

            var displayName = selection.DisplayName?.Trim();

            if (string.IsNullOrEmpty(displayName)
                || displayName.Length > MaxDisplayNameLength
                || displayName.Any(char.IsControl))
            {
                throw new BankLinkException(
                    BankLinkFailure.Invalid,
                    $"Every display name must be 1 to {MaxDisplayNameLength} characters without control characters.");
            }

            validated.Add(selection with { DisplayName = displayName });
        }

        return validated;
    }
}
