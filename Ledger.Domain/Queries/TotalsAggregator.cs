using System.Globalization;
using Ledger.Domain.Ingestion;

namespace Ledger.Domain.Queries;

/// <summary>How a totals result is cut up beyond the per-currency totals.</summary>
public enum TotalsGrouping
{
    /// <summary>No grouping; only the totals and the counterparty breakdown.</summary>
    None,

    /// <summary>One group per counterparty, capped, with a remainder.</summary>
    Counterparty,

    /// <summary>One group per calendar month.</summary>
    Month,

    /// <summary>One group per ISO week, starting on Monday.</summary>
    Week,

    /// <summary>One group per calendar day.</summary>
    Day,

    /// <summary>One group per synced account in scope.</summary>
    Account
}

/// <summary>
/// Booked transactions summed in the database at the grain of currency, period day, account and counterparty name. Money out and
/// money in are non-negative magnitudes.
/// </summary>
/// <param name="Currency">The three-letter currency code.</param>
/// <param name="PeriodDate">The day the transactions count on.</param>
/// <param name="AccountKey">The opaque key of the account.</param>
/// <param name="CounterpartyName">The counterparty name exactly as stored, or null.</param>
/// <param name="MoneyOut">The sum of negative amounts, as a positive number.</param>
/// <param name="MoneyIn">The sum of positive amounts.</param>
/// <param name="Count">The number of transactions, zero amounts included.</param>
public record TotalsRow(
    string Currency,
    DateOnly PeriodDate,
    string AccountKey,
    string? CounterpartyName,
    decimal MoneyOut,
    decimal MoneyIn,
    int Count);

/// <summary>What was left out of a total for one currency: how many transactions and how much went out and came in.</summary>
/// <param name="Currency">The three-letter currency code.</param>
/// <param name="Count">The number of transactions left out.</param>
/// <param name="MoneyOut">The money out of those transactions, as a positive number.</param>
/// <param name="MoneyIn">The money in of those transactions.</param>
public record ExcludedTotals(string Currency, int Count, decimal MoneyOut, decimal MoneyIn);

/// <summary>A synced account in the scope of a totals question.</summary>
/// <param name="AccountKey">The opaque key of the account.</param>
/// <param name="DisplayName">The name the household gave the account, or null.</param>
/// <param name="Currency">The account's currency.</param>
/// <param name="CreatedAt">When the account was created, which orders account groups.</param>
/// <param name="LastSuccessfulSync">The end of the account's latest successful sync, or null before the first.</param>
public record TotalsAccount(
    string AccountKey,
    string? DisplayName,
    string Currency,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSuccessfulSync);

/// <summary>Everything one totals question reads, taken from a single database snapshot.</summary>
/// <param name="Booked">The booked transactions that count, at the database grain.</param>
/// <param name="Pending">The pending transactions in the same filters and period, per currency, which are not counted.</param>
/// <param name="InternalTransfers">The booked transfers between synced accounts, per currency, which are not counted.</param>
/// <param name="Accounts">The synced accounts in scope, in creation order.</param>
public record TotalsData(
    IReadOnlyList<TotalsRow> Booked,
    IReadOnlyList<ExcludedTotals> Pending,
    IReadOnlyList<ExcludedTotals> InternalTransfers,
    IReadOnlyList<TotalsAccount> Accounts);

/// <summary>One counterparty in a breakdown, or the remainder row that merges the rest.</summary>
/// <param name="Name">The most frequent spelling, a fixed label for no name, or the remainder label.</param>
/// <param name="Ref">The counterparty reference, or null for no name and for the remainder.</param>
/// <param name="MoneyOut">The money out, as a positive number.</param>
/// <param name="MoneyIn">The money in.</param>
/// <param name="Count">The number of transactions.</param>
/// <param name="MergedCounterparties">How many counterparties a remainder row merges; 1 for an ordinary row.</param>
/// <param name="IsRemainder">Whether this is the remainder row.</param>
public record CounterpartyShare(
    string Name,
    string? Ref,
    decimal MoneyOut,
    decimal MoneyIn,
    int Count,
    int MergedCounterparties,
    bool IsRemainder);

/// <summary>One group of a grouped result. Time groups carry their clipped days, account groups the account, counterparty groups the reference.</summary>
/// <param name="Label">The group's label: a month, an ISO week, a day, an account name or a counterparty name.</param>
/// <param name="From">The first day of a time group, or null.</param>
/// <param name="To">The last day of a time group, or null.</param>
/// <param name="AccountKey">The account's opaque key for an account group, or null.</param>
/// <param name="CounterpartyRef">The counterparty reference for a counterparty group, or null.</param>
/// <param name="MoneyOut">The money out, as a positive number.</param>
/// <param name="MoneyIn">The money in.</param>
/// <param name="Count">The number of transactions.</param>
/// <param name="MergedCounterparties">How many counterparties a remainder group merges; 1 otherwise.</param>
public record TotalsBucket(
    string Label,
    DateOnly? From,
    DateOnly? To,
    string? AccountKey,
    string? CounterpartyRef,
    decimal MoneyOut,
    decimal MoneyIn,
    int Count,
    int MergedCounterparties = 1)
{
    /// <summary>Money in minus money out.</summary>
    public decimal Net => MoneyIn - MoneyOut;
}

/// <summary>The totals of one currency with the breakdown that explains them.</summary>
/// <param name="Currency">The three-letter currency code.</param>
/// <param name="MoneyOut">The money out, as a positive number.</param>
/// <param name="MoneyIn">The money in.</param>
/// <param name="Count">The number of booked transactions.</param>
/// <param name="CounterpartyCount">The number of distinct counterparties.</param>
/// <param name="Counterparties">The top counterparties plus a remainder row; their parts add up to the totals.</param>
/// <param name="Groups">The requested grouping, with empty groups included; empty when no grouping was asked.</param>
public record CurrencyTotals(
    string Currency,
    decimal MoneyOut,
    decimal MoneyIn,
    int Count,
    int CounterpartyCount,
    IReadOnlyList<CounterpartyShare> Counterparties,
    IReadOnlyList<TotalsBucket> Groups)
{
    /// <summary>Money in minus money out.</summary>
    public decimal Net => MoneyIn - MoneyOut;
}

/// <summary>The rolled-up answer to a totals question: one entry per currency, never added together.</summary>
/// <param name="Currencies">The currencies, in ordinal order.</param>
public record TotalsAggregate(IReadOnlyList<CurrencyTotals> Currencies);

/// <summary>Rolls booked rows up into per-currency totals and a counterparty breakdown. Pure: no clock, no database.</summary>
public static class TotalsAggregator
{
    /// <summary>The fixed label for rows without a counterparty name.</summary>
    public const string NoCounterpartyLabel = "(no counterparty name)";

    /// <summary>How many counterparties the breakdown names before the remainder row.</summary>
    public const int BreakdownSize = 25;

    /// <summary>How many counterparties a counterparty grouping names before the remainder.</summary>
    public const int CounterpartyGroupSize = 100;

    /// <summary>The longest period, in days, that can be grouped by day.</summary>
    public const int MaxDaysForDayGrouping = 92;

    /// <summary>The longest period, in days, that can be grouped by week.</summary>
    public const int MaxDaysForWeekGrouping = 731;

    /// <summary>Sums the rows per currency and builds the breakdown and the requested grouping.</summary>
    /// <param name="data">What the store read.</param>
    /// <param name="grouping">How to cut the result up beyond the totals.</param>
    /// <param name="range">The inclusive days the result covers.</param>
    public static TotalsAggregate Aggregate(TotalsData data, TotalsGrouping grouping, DateRange range)
    {
        var currencies = data.Booked.Select(row => row.Currency)
            .Concat(data.Pending.Select(excluded => excluded.Currency))
            .Concat(data.InternalTransfers.Select(excluded => excluded.Currency))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (currencies.Count == 0)
        {
            currencies = data.Accounts.Select(account => account.Currency).Distinct(StringComparer.Ordinal).ToList();
        }

        var result = currencies
            .Order(StringComparer.Ordinal)
            .Select(currency => ForCurrency(currency, data, grouping, range))
            .ToList();

        return new TotalsAggregate(result);
    }

    private static CurrencyTotals ForCurrency(string currency, TotalsData data, TotalsGrouping grouping, DateRange range)
    {
        var rows = data.Booked.Where(row => row.Currency == currency).ToList();
        var merged = MergeCounterparties(rows);

        var breakdown = Capped(merged, BreakdownSize);
        var groups = Groups(grouping, rows, merged, data.Accounts, currency, range);

        return new CurrencyTotals(
            currency,
            rows.Sum(row => row.MoneyOut),
            rows.Sum(row => row.MoneyIn),
            rows.Sum(row => row.Count),
            merged.Count,
            breakdown,
            groups);
    }

    private static List<CounterpartyShare> MergeCounterparties(IReadOnlyList<TotalsRow> rows)
    {
        return rows
            .GroupBy(row => TextNormalizer.ForMatching(row.CounterpartyName ?? string.Empty) ?? string.Empty, StringComparer.Ordinal)
            .Select(group =>
            {
                var spelling = group
                    .Where(row => !string.IsNullOrWhiteSpace(row.CounterpartyName))
                    .GroupBy(row => row.CounterpartyName!, StringComparer.Ordinal)
                    .Select(spellings => new { Name = spellings.Key, Count = spellings.Sum(row => row.Count) })
                    .OrderByDescending(candidate => candidate.Count)
                    .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
                    .FirstOrDefault();

                return new CounterpartyShare(
                    spelling?.Name ?? NoCounterpartyLabel,
                    spelling is null ? null : CounterpartyRef.For(spelling.Name),
                    group.Sum(row => row.MoneyOut),
                    group.Sum(row => row.MoneyIn),
                    group.Sum(row => row.Count),
                    1,
                    false);
            })
            .OrderByDescending(share => share.MoneyOut)
            .ThenByDescending(share => share.MoneyIn)
            .ThenBy(share => share.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static List<CounterpartyShare> Capped(List<CounterpartyShare> ordered, int size)
    {
        if (ordered.Count <= size)
        {
            return ordered;
        }

        var rest = ordered.Skip(size).ToList();
        var shown = ordered.Take(size).ToList();

        shown.Add(new CounterpartyShare(
            $"Other counterparties ({rest.Count})",
            null,
            rest.Sum(share => share.MoneyOut),
            rest.Sum(share => share.MoneyIn),
            rest.Sum(share => share.Count),
            rest.Count,
            true));

        return shown;
    }

    /// <summary>
    /// Refuses a grouping that would return too many groups for the period, with a fixed message that advises a shorter period or a
    /// coarser grouping.
    /// </summary>
    /// <param name="grouping">The requested grouping.</param>
    /// <param name="range">The inclusive days the result covers.</param>
    /// <exception cref="LedgerQueryException">The period holds too many days for the grouping.</exception>
    public static void CheckGrouping(TotalsGrouping grouping, DateRange range)
    {
        if (grouping == TotalsGrouping.Day && range.Days > MaxDaysForDayGrouping)
        {
            throw new LedgerQueryException("Grouping by day covers at most 92 days. Use a shorter period or group by week or month.");
        }

        if (grouping == TotalsGrouping.Week && range.Days > MaxDaysForWeekGrouping)
        {
            throw new LedgerQueryException("Grouping by week covers at most 731 days. Use a shorter period or group by month.");
        }
    }

    private static List<TotalsBucket> Groups(
        TotalsGrouping grouping,
        IReadOnlyList<TotalsRow> rows,
        List<CounterpartyShare> merged,
        IReadOnlyList<TotalsAccount> accounts,
        string currency,
        DateRange range)
    {
        CheckGrouping(grouping, range);

        return grouping switch
        {
            TotalsGrouping.Counterparty => CounterpartyGroups(merged),
            TotalsGrouping.Month => TimeGroups(rows, MonthSlices(range)),
            TotalsGrouping.Week => TimeGroups(rows, WeekSlices(range)),
            TotalsGrouping.Day => TimeGroups(rows, DaySlices(range)),
            TotalsGrouping.Account => AccountGroups(rows, accounts, currency),
            _ => []
        };
    }

    private static List<TotalsBucket> CounterpartyGroups(List<CounterpartyShare> merged)
    {
        return Capped(merged, CounterpartyGroupSize)
            .Select(share => new TotalsBucket(
                share.Name,
                null,
                null,
                null,
                share.Ref,
                share.MoneyOut,
                share.MoneyIn,
                share.Count,
                share.MergedCounterparties))
            .ToList();
    }

    private static List<TotalsBucket> AccountGroups(IReadOnlyList<TotalsRow> rows, IReadOnlyList<TotalsAccount> accounts, string currency)
    {
        return accounts
            .Where(account => account.Currency == currency || rows.Any(row => row.AccountKey == account.AccountKey))
            .OrderBy(account => account.CreatedAt)
            .ThenBy(account => account.AccountKey, StringComparer.Ordinal)
            .Select(account =>
            {
                var own = rows.Where(row => row.AccountKey == account.AccountKey).ToList();

                return new TotalsBucket(
                    account.DisplayName ?? "Account " + account.AccountKey,
                    null,
                    null,
                    account.AccountKey,
                    null,
                    own.Sum(row => row.MoneyOut),
                    own.Sum(row => row.MoneyIn),
                    own.Sum(row => row.Count));
            })
            .ToList();
    }

    private static List<TotalsBucket> TimeGroups(IReadOnlyList<TotalsRow> rows, IEnumerable<TimeSlice> slices)
    {
        return slices
            .Select(slice =>
            {
                var inSlice = rows.Where(row => row.PeriodDate >= slice.From && row.PeriodDate <= slice.To).ToList();

                return new TotalsBucket(
                    slice.Label,
                    slice.From,
                    slice.To,
                    null,
                    null,
                    inSlice.Sum(row => row.MoneyOut),
                    inSlice.Sum(row => row.MoneyIn),
                    inSlice.Sum(row => row.Count));
            })
            .ToList();
    }

    private static IEnumerable<TimeSlice> DaySlices(DateRange range)
    {
        for (var day = range.From; day <= range.To; day = day.AddDays(1))
        {
            yield return new TimeSlice(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), day, day);
        }
    }

    private static IEnumerable<TimeSlice> WeekSlices(DateRange range)
    {
        var monday = range.From.AddDays(-(((int)range.From.DayOfWeek + 6) % 7));

        for (; monday <= range.To; monday = monday.AddDays(7))
        {
            var moment = monday.ToDateTime(TimeOnly.MinValue);
            var label = string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(moment)}-W{ISOWeek.GetWeekOfYear(moment):00}");
            var from = monday < range.From ? range.From : monday;
            var sunday = monday.AddDays(6);
            var to = sunday > range.To ? range.To : sunday;

            yield return new TimeSlice(label, from, to);
        }
    }

    private static IEnumerable<TimeSlice> MonthSlices(DateRange range)
    {
        var first = new DateOnly(range.From.Year, range.From.Month, 1);

        for (var month = first; month <= range.To; month = month.AddMonths(1))
        {
            var last = month.AddMonths(1).AddDays(-1);
            var from = month < range.From ? range.From : month;
            var to = last > range.To ? range.To : last;

            yield return new TimeSlice(month.ToString("yyyy-MM", CultureInfo.InvariantCulture), from, to);
        }
    }

    private sealed record TimeSlice(string Label, DateOnly From, DateOnly To);
}
