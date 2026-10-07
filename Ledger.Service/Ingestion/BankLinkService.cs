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

/// <summary>
/// The outcome of a bank redirect, carrying only the number of accounts found and whether the sync that must follow a renewal
/// was queued. A first link queues no sync, because accounts are chosen afterwards.
/// </summary>
public record CallbackOutcome(CallbackResult Result, int AccountCount, bool SyncQueued = true);

/// <summary>A connection together with its derived consent state.</summary>
public record ConnectionOverview(ConnectionSummary Connection, ConsentSnapshot Consent);

/// <summary>The result of saving an account selection: the accounts afterwards and whether a first sync was queued.</summary>
public record SelectionResult(IReadOnlyList<LinkedAccount> Accounts, bool FirstSyncQueued);

/// <summary>What happened to a request to sync now.</summary>
public enum SyncNowResult
{
    /// <summary>The sync was queued.</summary>
    Queued,

    /// <summary>No bank provider is configured.</summary>
    NotConfigured,

    /// <summary>There is no active connection to sync.</summary>
    NoConnection,

    /// <summary>The connection has no selected account.</summary>
    NoAccountsSelected,

    /// <summary>A sync of the connection is already running.</summary>
    AlreadyRunning,

    /// <summary>A sync of the connection is already waiting to run.</summary>
    AlreadyQueued,

    /// <summary>The sync would use the last background call of the day for an account, and nobody is present to make it a foreground call.</summary>
    WouldUseLastCall
}

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
    ISyncRunStore runs,
    IProviderCallStore calls,
    IOptions<BankLinkOptions> options,
    IOptions<IngestionOptions> ingestionOptions,
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

    /// <summary>
    /// Starts a consent that renews an existing connection through the same redirect path as a first link. A connection whose
    /// consent has ended can be renewed; a revoked or superseded one cannot.
    /// </summary>
    public async Task<LinkStart> StartRenewAsync(string connectionKey, CancellationToken cancellationToken)
    {
        var connection = await FindAsync(connectionKey, cancellationToken);

        if (!IsRenewable(connection.Status))
        {
            throw new BankLinkException(BankLinkFailure.Conflict, "The connection was revoked or replaced and cannot be renewed.");
        }

        return await StartAsync(AuthorizationPurposes.Renew, connection.Id, cancellationToken);
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

        if (consumed.Purpose == AuthorizationPurposes.Renew && !await IsStillRenewableAsync(consumed.ConnectionId, cancellationToken))
        {
            return Reject("connection can no longer be renewed");
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
            await EndUnrecordedSessionAsync(session);
            logger.LogWarning("Bank link callback finished with outcome {Outcome}.", CallbackResult.NoAccounts);
            return new CallbackOutcome(CallbackResult.NoAccounts, 0);
        }

        return consumed.Purpose == AuthorizationPurposes.Renew
            ? await CompleteRenewalAsync(consumed.ConnectionId!.Value, session, psu)
            : await CompleteLinkAsync(session);
    }

    /// <summary>
    /// Records the session as a new connection. Once the provider has created the session, recording it is not tied to the
    /// request, so a browser that disconnects cannot strand a consent the ledger never recorded.
    /// </summary>
    private async Task<CallbackOutcome> CompleteLinkAsync(ProviderSession session)
    {
        LinkedConnection linked;

        try
        {
            linked = await connections.AddConnectionAsync(
                provider.Name,
                options.Value.AspspName,
                options.Value.AspspCountry,
                session,
                secretProtector.Protect(session.SessionId),
                timeProvider.GetUtcNow(),
                CancellationToken.None);
        }
        catch (Exception)
        {
            await EndUnrecordedSessionAsync(session);
            throw;
        }

        logger.LogInformation("Bank link callback finished with outcome {Outcome}.", CallbackResult.Completed);
        return new CallbackOutcome(CallbackResult.Completed, linked.Accounts.Count);
    }

    /// <summary>
    /// Ends a session the provider created but the ledger did not record, so no consent stays live at the bank that the
    /// operator can neither see nor end from here. A failure to end it is logged by kind only and never replaces the original
    /// failure.
    /// </summary>
    private async Task EndUnrecordedSessionAsync(ProviderSession session)
    {
        try
        {
            await provider.RevokeSessionAsync(session.SessionId, CancellationToken.None);
            logger.LogInformation("A bank session that could not be recorded was ended at the bank.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "A bank session that could not be recorded could not be ended at the bank ({Kind}). End it in the aggregator's control panel.",
                exception is BankProviderException providerException ? providerException.Kind.ToString() : exception.GetType().Name);
        }
    }

    /// <summary>
    /// Ends a connection: the provider closes the session first, and only then is the connection marked revoked, so a provider
    /// failure changes nothing.
    /// </summary>
    public async Task RevokeAsync(string connectionKey, CancellationToken cancellationToken)
    {
        var connection = await FindAsync(connectionKey, cancellationToken);

        if (!IsRenewable(connection.Status))
        {
            throw new BankLinkException(BankLinkFailure.Conflict, "The connection was already revoked or replaced.");
        }

        if (provider is DisabledBankDataProvider)
        {
            throw new BankLinkException(BankLinkFailure.NotConfigured, "Bank linking is not configured.");
        }

        var protectedSessionId = await connections.GetProtectedSessionIdAsync(connection.Id, cancellationToken)
            ?? throw new BankLinkException(BankLinkFailure.NotFound, "The connection does not exist.");

        try
        {
            await provider.RevokeSessionAsync(secretProtector.Unprotect(protectedSessionId), cancellationToken);
        }
        catch (BankProviderException exception)
        {
            logger.LogWarning("Revoking a bank session failed with {Kind}.", exception.Kind);
            throw new BankLinkException(BankLinkFailure.Provider, "The bank provider could not end the session.");
        }

        await connections.MarkStatusAsync(connection.Id, ConnectionStatus.Revoked, timeProvider.GetUtcNow(), cancellationToken);
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

        if (needsFirstSync
            && dispatcher.TryEnqueue(new SyncRequest(connection.Id, SyncTrigger.PostLink, new FetchContext(psu))) == EnqueueResult.Full)
        {
            throw new BankLinkException(BankLinkFailure.Busy, "The sync queue is full. Repeat the call shortly.");
        }

        return new SelectionResult(accounts, needsFirstSync);
    }

    /// <summary>
    /// Queues a sync of a connection right now. Without the operator's presence details the sync is a background one and is
    /// refused when it would use the last remaining background call of any selected account for the day; with them it is an
    /// attended sync and spends none of that allowance.
    /// </summary>
    /// <param name="connectionKey">The connection to sync, or null for the only active connection.</param>
    /// <param name="psu">The operator's presence details, or null when they are not sent to the bank.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="BankLinkException">The key names no connection, the connection cannot be synced, or the choice is ambiguous.</exception>
    public async Task<SyncNowResult> SyncNowAsync(string? connectionKey, PsuContext? psu, CancellationToken cancellationToken)
    {
        if (provider is DisabledBankDataProvider)
        {
            return SyncNowResult.NotConfigured;
        }

        var connection = await ResolveSyncConnectionAsync(connectionKey, cancellationToken);

        if (connection is null)
        {
            return SyncNowResult.NoConnection;
        }

        var selected = (await connections.ListAccountsAsync(connection.Id, cancellationToken))
            .Where(account => account.SyncEnabled)
            .ToList();

        if (selected.Count == 0)
        {
            return SyncNowResult.NoAccountsSelected;
        }

        if (await runs.HasUnfinishedRunAsync(connection.Id, cancellationToken))
        {
            return SyncNowResult.AlreadyRunning;
        }

        if (psu is null && await WouldUseLastCallAsync(selected, cancellationToken))
        {
            return SyncNowResult.WouldUseLastCall;
        }

        switch (dispatcher.TryEnqueue(new SyncRequest(connection.Id, SyncTrigger.Manual, new FetchContext(psu))))
        {
            case EnqueueResult.Full:
                throw new BankLinkException(BankLinkFailure.Busy, "The sync queue is full. Repeat the call shortly.");
            case EnqueueResult.AlreadyQueued:
                return SyncNowResult.AlreadyQueued;
        }

        logger.LogInformation("A sync of connection {ConnectionKey} was queued on request.", connection.ConnectionKey);
        return SyncNowResult.Queued;
    }

    private async Task<ConnectionSummary?> ResolveSyncConnectionAsync(string? connectionKey, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (connectionKey is not null)
        {
            var named = await FindAsync(connectionKey, cancellationToken);

            if (named.Status != ConnectionStatus.Active
                || ConsentState.Derive(named.Status, named.ValidUntil, now).State == ConsentView.Expired)
            {
                throw new BankLinkException(BankLinkFailure.Conflict, "Only a connection with a valid consent can be synced. Renew the consent first.");
            }

            return named;
        }

        var active = (await connections.ListConnectionsAsync(cancellationToken))
            .Where(candidate => candidate.Status == ConnectionStatus.Active
                && ConsentState.Derive(candidate.Status, candidate.ValidUntil, now).State != ConsentView.Expired)
            .ToList();

        if (active.Count > 1)
        {
            throw new BankLinkException(BankLinkFailure.Conflict, "More than one connection is active. Give the connectionKey of the one to sync.");
        }

        return active.SingleOrDefault();
    }

    private async Task<bool> WouldUseLastCallAsync(IReadOnlyList<LinkedAccount> accounts, CancellationToken cancellationToken)
    {
        var settings = ingestionOptions.Value;
        var zone = settings.ResolveTimeZone();
        var now = timeProvider.GetUtcNow();
        var since = CallBudget.WindowStart(now, settings.QuotaWindow, zone);

        foreach (var account in accounts)
        {
            var earlier = await calls.ListBackgroundCallTimesAsync(account.Id, since, cancellationToken);

            if (CallBudget.Remaining(settings.BackgroundCallsPerDay, earlier, now, settings.QuotaWindow, zone) <= 1)
            {
                return true;
            }
        }

        return false;
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

    private async Task<CallbackOutcome> CompleteRenewalAsync(
        Guid supersededConnectionId,
        ProviderSession session,
        PsuContext? psu)
    {
        RenewalResult renewal;

        try
        {
            renewal = await connections.ApplyRenewalAsync(
                supersededConnectionId,
                provider.Name,
                options.Value.AspspName,
                options.Value.AspspCountry,
                session,
                secretProtector.Protect(session.SessionId),
                timeProvider.GetUtcNow(),
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            await EndUnrecordedSessionAsync(session);
            return Reject("connection can no longer be renewed");
        }
        catch (Exception)
        {
            await EndUnrecordedSessionAsync(session);
            throw;
        }

        var syncQueued = dispatcher.TryEnqueue(new SyncRequest(renewal.Connection.Id, SyncTrigger.PostLink, new FetchContext(psu)))
            != EnqueueResult.Full;

        if (!syncQueued)
        {
            logger.LogError(
                "The sync after the renewal of connection {ConnectionKey} could not be queued because the queue is full. Start it with a sync request now, because the bank returns the full history only shortly after approval.",
                renewal.Connection.ConnectionKey);
        }

        logger.LogInformation("Bank link callback finished with outcome {Outcome}.", CallbackResult.Completed);
        return new CallbackOutcome(CallbackResult.Completed, renewal.MappedAccounts + renewal.NewAccounts, syncQueued);
    }

    private async Task<bool> IsStillRenewableAsync(Guid? connectionId, CancellationToken cancellationToken)
    {
        if (connectionId is null)
        {
            return false;
        }

        var all = await connections.ListConnectionsAsync(cancellationToken);
        var connection = all.FirstOrDefault(candidate => candidate.Id == connectionId);

        return connection is not null && IsRenewable(connection.Status);
    }

    private static bool IsRenewable(ConnectionStatus status)
    {
        return status is ConnectionStatus.Active or ConnectionStatus.ProviderExpired;
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
