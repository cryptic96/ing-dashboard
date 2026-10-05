namespace Ledger.Domain.Banking;

/// <summary>The kind of a bank account.</summary>
public enum AccountKind
{
    /// <summary>A current (payment) account.</summary>
    Current,

    /// <summary>A savings account.</summary>
    Savings,

    /// <summary>A card account.</summary>
    Card,

    /// <summary>Any other kind of account.</summary>
    Other
}

/// <summary>The kind of a balance reported by a provider.</summary>
public enum BalanceKind
{
    /// <summary>The booked balance at the end of a period.</summary>
    ClosingBooked,

    /// <summary>The booked balance at an intermediate point in time.</summary>
    InterimBooked,

    /// <summary>The available balance at an intermediate point in time.</summary>
    InterimAvailable,

    /// <summary>The available balance at the end of a period.</summary>
    ClosingAvailable,

    /// <summary>The booked balance at the start of a period.</summary>
    OpeningBooked,

    /// <summary>A forecast balance including expected items.</summary>
    Expected,

    /// <summary>Any other balance type.</summary>
    Other
}

/// <summary>The provider-neutral status of a transaction.</summary>
public enum ProviderTransactionStatus
{
    /// <summary>Announced but not yet booked by the bank.</summary>
    Pending,

    /// <summary>Booked by the bank.</summary>
    Booked,

    /// <summary>Cancelled or rejected before booking.</summary>
    Cancelled,

    /// <summary>Any other status, such as a scheduled future item.</summary>
    Other
}

/// <summary>How far back a transaction fetch should reach.</summary>
public enum HistoryDepth
{
    /// <summary>Only from the query's start date onwards.</summary>
    Incremental,

    /// <summary>As far back as the bank will return.</summary>
    Longest
}

/// <summary>A request to start a consent: the one-time state to echo back and where the provider redirects afterwards.</summary>
public record AuthorizationRequest(string State, Uri RedirectUrl);

/// <summary>The URL the operator must open to approve a consent and the provider's identifier for the attempt.</summary>
public record AuthorizationStart(Uri AuthorizationUrl, string ProviderAuthorizationId);

/// <summary>An approved consent: its session id, validity end and the accounts it exposes.</summary>
public record ProviderSession(string SessionId, DateTimeOffset ValidUntil, IReadOnlyList<ProviderAccount> Accounts);

/// <summary>An account exposed by a session. The identification hash is stable across sessions; the uid is not.</summary>
public record ProviderAccount(
    string? Uid,
    string IdentificationHash,
    string? Iban,
    string? Name,
    string? Product,
    AccountKind Kind,
    string Currency);

/// <summary>Addresses one account within one session.</summary>
public record ProviderAccountRef(string SessionId, string AccountUid);

/// <summary>A balance reported by the provider.</summary>
public record ProviderBalance(
    BalanceKind Kind,
    string ProviderType,
    decimal Amount,
    string Currency,
    DateOnly? ReferenceDate);

/// <summary>
/// One transaction as observed at the provider, with the signed amount (negative for money leaving the account),
/// calendar dates as the bank reported them, and the untouched provider payload.
/// </summary>
public record ProviderTransaction(
    string? EntryReference,
    ProviderTransactionStatus Status,
    decimal Amount,
    string Currency,
    DateOnly? BookingDate,
    DateOnly? ValueDate,
    DateOnly? TransactionDate,
    string? CounterpartyName,
    string? CounterpartyIban,
    string? Description,
    string RawJson);

/// <summary>One page of a transaction fetch.</summary>
public record ProviderTransactionPage(IReadOnlyList<ProviderTransaction> Transactions);

/// <summary>Which transactions to fetch: from a date, or as far back as the bank allows.</summary>
public record TransactionQuery(DateOnly? DateFrom, HistoryDepth Depth);

/// <summary>The kinds of account-data call that count against a bank's call allowance.</summary>
public enum ProviderCallKind
{
    /// <summary>One page of a transaction fetch.</summary>
    Transactions,

    /// <summary>A balances read.</summary>
    Balances
}

/// <summary>
/// Counts and gates every account-data call. A provider must await it immediately before each request it sends to the bank,
/// so the call is recorded before it happens and a call over the allowance is never sent.
/// </summary>
public interface IProviderCallMeter
{
    /// <summary>Records the call that is about to be sent.</summary>
    /// <exception cref="CallBudgetExhaustedException">The call allowance for the account is used up, so the call must not be sent.</exception>
    ValueTask BeforeCallAsync(ProviderCallKind kind, CancellationToken cancellationToken);
}

/// <summary>Thrown by a call meter when a background call would exceed the account's allowance. Nothing was sent to the bank.</summary>
public class CallBudgetExhaustedException(string message) : Exception(message);

/// <summary>
/// Whether a fetch happens in the background or on behalf of a person who is present, and the meter that counts its calls.
/// </summary>
public record FetchContext(PsuContext? Psu, IProviderCallMeter? Meter = null)
{
    /// <summary>A fetch with no person present, as the scheduled sync does.</summary>
    public static FetchContext Background { get; } = new FetchContext((PsuContext?)null);

    /// <summary>Whether no person is present, which is what a bank treats as a background call.</summary>
    public bool IsBackground => Psu is null;
}

/// <summary>Details of the person present during a fetch, for providers that distinguish attended from unattended access.</summary>
public record PsuContext(string IpAddress, string UserAgent);
