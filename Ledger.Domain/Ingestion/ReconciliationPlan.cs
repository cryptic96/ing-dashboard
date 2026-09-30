using Ledger.Domain.Banking;

namespace Ledger.Domain.Ingestion;

/// <summary>Describes what a fetch covered, so the reconciler knows what absence from the feed means.</summary>
public record FetchCoverage(DateOnly? From, bool Complete, int ItemCount);

/// <summary>Tunable reconciliation behaviour.</summary>
public record ReconcilerOptions(int MatchWindowDays = 5);

/// <summary>A transaction to add, together with the provider reference it is remembered under.</summary>
public record PlannedInsert(ProviderTransaction Item, string Ref, MatchFlag Flag);

/// <summary>A fresh observation of a transaction that already exists, optionally promoting it from pending to booked.</summary>
public record PlannedUpdate(Guid TransactionId, ProviderTransaction Item, bool UpgradeToBooked);

/// <summary>Everything one account's fetch changes, to be applied in a single database transaction.</summary>
public record ReconciliationPlan(IReadOnlyList<PlannedInsert> Inserts, IReadOnlyList<PlannedUpdate> Updates);
