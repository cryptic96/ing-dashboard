using Ledger.Domain.Banking;

namespace Ledger.Domain.Ingestion;

/// <summary>Keeps the append-only ledger of account-data calls made to the bank, which the call budget is computed from.</summary>
public interface IProviderCallStore
{
    /// <summary>Appends one call. Rows are never changed or removed afterwards.</summary>
    Task RecordAsync(ProviderCallRecord call, CancellationToken cancellationToken);

    /// <summary>Returns the times of the account's background calls at or after the given instant.</summary>
    Task<IReadOnlyList<DateTimeOffset>> ListBackgroundCallTimesAsync(
        Guid accountId,
        DateTimeOffset since,
        CancellationToken cancellationToken);
}

/// <summary>One account-data call: which account, which run, when, what kind and whether nobody was present.</summary>
public record ProviderCallRecord(
    Guid AccountId,
    Guid? SyncRunId,
    DateTimeOffset CalledAt,
    ProviderCallKind Kind,
    bool Background);
