using Ledger.Domain.Banking;

namespace Ledger.Domain.Ingestion;

/// <summary>Describes what a fetch covered, so the reconciler knows what absence from the feed means.</summary>
public record FetchCoverage(DateOnly? From, bool Complete, int ItemCount);

/// <summary>
/// Tunable reconciliation behaviour: how many days apart a pending and a booked item may be to still match, and the calendar
/// zone in which the day a row was first seen is worked out when the bank gave it no date.
/// </summary>
public record ReconcilerOptions(int MatchWindowDays, TimeZoneInfo Zone);

/// <summary>A transaction to add, together with the provider reference it is remembered under.</summary>
public record PlannedInsert(ProviderTransaction Item, string Ref, MatchFlag Flag);

/// <summary>
/// A fresh observation of a transaction that already exists, optionally promoting it from pending to booked. When Restore is
/// true the row was dropped and the bank reports it again, so it returns with the status of the new observation.
/// </summary>
public record PlannedUpdate(Guid TransactionId, ProviderTransaction Item, bool UpgradeToBooked, bool Restore = false);

/// <summary>A booked item that is certainly the booked version of an existing pending row under a different reference.</summary>
public record PlannedMerge(Guid PendingTransactionId, ProviderTransaction BookedItem, string Ref);

/// <summary>
/// Everything one account's fetch changes, to be applied in a single database transaction. FlagAmbiguous lists pending rows
/// that plausibly match a booked item without certainty; Drops lists pending rows the bank no longer reports.
/// </summary>
public record ReconciliationPlan(
    IReadOnlyList<PlannedInsert> Inserts,
    IReadOnlyList<PlannedUpdate> Updates,
    IReadOnlyList<PlannedMerge> Merges,
    IReadOnlyList<Guid> FlagAmbiguous,
    IReadOnlyList<Guid> Drops);
