namespace Ledger.Domain.Queries;

/// <summary>Which direction of money a question is about.</summary>
public enum MoneyDirection
{
    /// <summary>Money that left the accounts.</summary>
    Out,

    /// <summary>Money that arrived.</summary>
    In,

    /// <summary>Both directions.</summary>
    Both
}

/// <summary>
/// What a totals question selects. Text terms within one list are alternatives; when both the counterparty selection and the
/// description list are given, a transaction must match both. The counterparty selection is the terms, the exact names and the
/// references together.
/// </summary>
/// <param name="Range">The inclusive days to cover.</param>
/// <param name="AccountKeys">Opaque account keys to narrow to; empty means every synced account.</param>
/// <param name="CounterpartyTerms">Substrings of the counterparty name, matched case-insensitively.</param>
/// <param name="CounterpartyNames">Exact counterparty names, as stored.</param>
/// <param name="DescriptionTerms">Substrings of the description, matched case-insensitively.</param>
/// <param name="Direction">Whether to count money out, money in or both.</param>
/// <param name="MinAmount">The smallest absolute amount to include, or null.</param>
/// <param name="MaxAmount">The largest absolute amount to include, or null.</param>
/// <param name="CounterpartyRefs">References to counterparties; the store resolves them to names inside the same snapshot.</param>
public record LedgerQueryFilter(
    DateRange Range,
    IReadOnlyList<string> AccountKeys,
    IReadOnlyList<string> CounterpartyTerms,
    IReadOnlyList<string> CounterpartyNames,
    IReadOnlyList<string> DescriptionTerms,
    MoneyDirection Direction,
    decimal? MinAmount,
    decimal? MaxAmount,
    IReadOnlyList<string>? CounterpartyRefs = null)
{
    /// <summary>Whether the counterparty selection has any term, name or reference, and so must be applied.</summary>
    public bool HasCounterpartySelection =>
        CounterpartyTerms.Count > 0 || CounterpartyNames.Count > 0 || (CounterpartyRefs?.Count ?? 0) > 0;
}
