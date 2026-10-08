using System.Globalization;
using Ledger.Domain.Ingestion;
using Ledger.Domain.Queries;
using Ledger.Service.Ingestion;
using Microsoft.Extensions.Options;

namespace Ledger.Service.Queries;

/// <summary>
/// Answers the questions Claude asks of the ledger. It reads through the domain stores and shapes the result for a tool; it
/// never touches the database context and never lets a bank identifier into a result.
/// </summary>
public class LedgerQueryService(
    ILedgerQueryStore store,
    IIngestionStatusStore ingestionStatus,
    IOptions<IngestionOptions> ingestionOptions,
    TimeProvider timeProvider)
{
    private const string NothingSyncedNote = "No accounts are synced yet.";
    private const string GroupedByText = "counterparty name, not category";
    private const int MaxTerms = 10;
    private const int MaxTermLength = 100;
    private const int DefaultSearchLimit = 50;
    private const int MaxSearchLimit = 100;
    private const int DefaultCounterpartyLimit = 25;
    private const int MaxCounterpartyLimit = 100;
    private const int MinCounterpartyTextLength = 2;
    private const int MaxSpellings = 5;
    private const int MaxCounterpartyAccounts = 3;

    /// <summary>
    /// Lists every synced account in creation order with its name, latest balance and whether it reconciles, the last successful
    /// sync, how far booked history reaches and how many items are pending, plus today's date in the configured time zone.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<OverviewResult> OverviewAsync(CancellationToken cancellationToken)
    {
        var options = ingestionOptions.Value;
        var zone = QueryTimeZone.Resolve(options.TimeZone);
        var now = timeProvider.GetUtcNow();
        var today = SyncSchedule.LocalDate(now, zone);

        var accounts = await store.ReadOverviewAsync(cancellationToken);

        if (accounts.Count == 0)
        {
            return new OverviewResult(DateText(today), zone.Id, [], NothingSyncedNote);
        }

        var status = await ingestionStatus.ReadAsync(now, cancellationToken);
        var healthByKey = status.Accounts.ToDictionary(account => account.AccountKey, StringComparer.Ordinal);

        var shown = accounts
            .Select(account =>
            {
                var health = healthByKey.GetValueOrDefault(account.AccountKey);

                return new OverviewAccount(
                    account.DisplayName ?? "Account " + account.AccountKey,
                    account.AccountKey,
                    account.Kind,
                    account.Currency,
                    health?.LastSuccessAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    account.EarliestBookedDate is { } from ? DateText(from) : null,
                    account.LatestBookedDate is { } to ? DateText(to) : null,
                    account.PendingCount,
                    account.LatestBalance is { } balance
                        ? new OverviewBalance(MoneyText.Format(balance.Amount), balance.Currency, balance.Kind, DateText(balance.AppliesTo))
                        : null,
                    health?.LatestReconciled switch
                    {
                        true => "yes",
                        false => "no",
                        null => "unknown"
                    });
            })
            .ToList();

        return new OverviewResult(DateText(today), zone.Id, shown, null);
    }

    /// <summary>
    /// Answers a how-much question with exact server sums over booked transactions. Pending transactions and transfers between the
    /// household's own synced accounts are reported beside the total and never added to it, and currencies are never added together.
    /// </summary>
    /// <param name="request">The period, filters and grouping that Claude chose.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="LedgerQueryException">The request is invalid; the message says what to change.</exception>
    public async Task<TotalsResult> TotalsAsync(TotalsRequest request, CancellationToken cancellationToken)
    {
        var options = ingestionOptions.Value;
        var zone = QueryTimeZone.Resolve(options.TimeZone);

        var resolver = new PeriodResolver(timeProvider, zone);
        var period = resolver.Resolve(request.Period, ParseDate(request.FromDate), ParseDate(request.ToDate));

        var counterpartyTerms = Terms(request.Counterparty);
        var descriptionTerms = Terms(request.Description);
        var references = References(request.CounterpartyRef);
        var accountKeys = Terms(request.Accounts);
        var direction = ParseDirection(request.Direction);
        var grouping = ParseGrouping(request.GroupBy);
        CheckAmounts(request.MinAmount, request.MaxAmount);
        TotalsAggregator.CheckGrouping(grouping, period.Range);

        var filter = new LedgerQueryFilter(
            period.Range,
            accountKeys,
            counterpartyTerms,
            [],
            descriptionTerms,
            direction,
            request.MinAmount,
            request.MaxAmount,
            references);

        var data = await store.ReadTotalsAsync(filter, zone, cancellationToken);

        if (accountKeys.Count > 0 && data.Accounts.Count != accountKeys.Count)
        {
            throw new LedgerQueryException("An account key is not a synced account. Use the account_key values from ledger_overview.");
        }

        var aggregate = TotalsAggregator.Aggregate(data, grouping, period.Range);

        return Shape(request, period, filter, grouping, data, aggregate, zone.Id);
    }

    /// <summary>
    /// Shows one page of the transactions that match, newest first, as detail rows only. The page is capped, says how many rows
    /// matched in all and whether it was cut short, and carries a cursor for the next page when more rows exist.
    /// </summary>
    /// <param name="request">The period, filters, page size and cursor that Claude chose.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="LedgerQueryException">The request or the cursor is invalid; the message says what to change.</exception>
    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var options = ingestionOptions.Value;
        var zone = QueryTimeZone.Resolve(options.TimeZone);

        var resolver = new PeriodResolver(timeProvider, zone);
        var period = resolver.Resolve(request.Period, ParseDate(request.FromDate), ParseDate(request.ToDate));

        var accountKeys = Terms(request.Accounts);
        var status = ParseStatus(request.Status);
        CheckAmounts(request.MinAmount, request.MaxAmount);

        var filter = new LedgerQueryFilter(
            period.Range,
            accountKeys,
            Terms(request.Counterparty),
            [],
            Terms(request.Description),
            ParseDirection(request.Direction),
            request.MinAmount,
            request.MaxAmount,
            References(request.CounterpartyRef));

        var statusText = StatusText(status);
        var filterHash = SearchCursor.FilterHash(filter, statusText);
        var after = DecodeCursor(request.Cursor, filterHash);
        var limit = Math.Clamp(request.Limit ?? DefaultSearchLimit, 1, MaxSearchLimit);
        var limitClamped = request.Limit is { } asked && asked != limit;

        var page = await store.SearchAsync(filter, status, after, limit, zone, cancellationToken);

        if (accountKeys.Count > 0 && page.AccountsInScope != accountKeys.Count)
        {
            throw new LedgerQueryException("An account key is not a synced account. Use the account_key values from ledger_overview.");
        }

        var rows = page.Rows
            .Select(row => new SearchRow(
                DateText(row.PeriodDate),
                row.BookingDate is { } booking ? DateText(booking) : null,
                row.Status,
                MoneyText.Format(row.Amount),
                row.Amount < 0m ? "out" : "in",
                row.Currency,
                IbanText.MaskInText(row.CounterpartyName),
                CounterpartyRef.For(row.CounterpartyName),
                row.CounterpartyAccountMasked,
                IbanText.MaskInText(row.Description),
                row.AccountKey,
                row.AccountName ?? "Account " + row.AccountKey,
                row.InternalTransfer))
            .ToList();

        var nextCursor = page.HasMore && page.Rows.Count > 0
            ? SearchCursor.Encode(page.Rows[^1].PeriodDate, page.Rows[^1].FirstSeenAt, page.Rows[^1].Id, filterHash)
            : null;

        var note = $"Showing {rows.Count} of {page.MatchingTotal} matching transactions, newest first. "
            + "These rows are detail only: do not add them up; use money_totals for any total."
            + (nextCursor is null ? string.Empty : " More rows match: narrow the period or filters, or pass next_cursor for the next page.");

        return new SearchResult(
            new TotalsPeriod(DateText(period.Range.From), DateText(period.Range.To), period.Requested, zone.Id),
            new SearchFilters(
                filter.CounterpartyTerms,
                filter.CounterpartyRefs ?? [],
                filter.DescriptionTerms,
                filter.AccountKeys,
                DirectionText(filter.Direction),
                filter.MinAmount is { } min ? MoneyText.Format(min) : null,
                filter.MaxAmount is { } max ? MoneyText.Format(max) : null,
                statusText),
            rows,
            rows.Count,
            page.MatchingTotal,
            nextCursor is not null,
            nextCursor,
            limit,
            limitClamped,
            note);
    }

    /// <summary>
    /// Lists the counterparties whose name contains the text, with every spelling that differs only in case or spacing merged under
    /// one reference, so Claude can build exact filters for the other tools. The list is capped and says when it was cut short.
    /// </summary>
    /// <param name="request">The text, optional period and account keys, and page size that Claude chose.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="LedgerQueryException">The request is invalid; the message says what to change.</exception>
    public async Task<CounterpartiesResult> FindCounterpartiesAsync(CounterpartiesRequest request, CancellationToken cancellationToken)
    {
        var options = ingestionOptions.Value;
        var zone = QueryTimeZone.Resolve(options.TimeZone);

        var text = string.Join(' ', (request.Text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (text.Length is < MinCounterpartyTextLength or > MaxTermLength)
        {
            throw new LedgerQueryException("text must be between 2 and 100 characters.");
        }

        var hasPeriod = !string.IsNullOrWhiteSpace(request.Period)
            || !string.IsNullOrWhiteSpace(request.FromDate)
            || !string.IsNullOrWhiteSpace(request.ToDate);
        var range = hasPeriod
            ? new PeriodResolver(timeProvider, zone).Resolve(request.Period, ParseDate(request.FromDate), ParseDate(request.ToDate)).Range
            : null;

        var accountKeys = Terms(request.Accounts);
        var limit = Math.Clamp(request.Limit ?? DefaultCounterpartyLimit, 1, MaxCounterpartyLimit);
        var limitClamped = request.Limit is { } asked && asked != limit;

        var page = await store.FindCounterpartiesAsync(text, range, accountKeys, zone, cancellationToken);

        if (accountKeys.Count > 0 && page.AccountsInScope != accountKeys.Count)
        {
            throw new LedgerQueryException("An account key is not a synced account. Use the account_key values from ledger_overview.");
        }

        var merged = page.Rows
            .GroupBy(row => TextNormalizer.ForMatching(row.Name)!, StringComparer.Ordinal)
            .Select(MergeCounterparty)
            .OrderByDescending(entry => entry.TransactionCount)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();

        var shown = merged.Take(limit).ToList();
        var truncated = merged.Count > shown.Count;
        var periodText = range is null
            ? "all history"
            : $"{DateText(range.From)} to {DateText(range.To)} ({zone.Id})";

        var note = $"Showing {shown.Count} of {merged.Count} matching counterparties, most transactions first. "
            + "These are counterparty names, not categories. Pass counterpartyRef or the spellings to money_totals or search_transactions."
            + (truncated ? " More names match: use a longer text or a shorter period." : string.Empty);

        return new CounterpartiesResult(text, periodText, shown, shown.Count, merged.Count, truncated, limit, limitClamped, note);
    }

    private static CounterpartyEntry MergeCounterparty(IGrouping<string, CounterpartyData> group)
    {
        var spellings = group
            .GroupBy(row => row.Name, StringComparer.Ordinal)
            .Select(spelling => new { Name = spelling.Key, Count = spelling.Sum(row => row.Count) })
            .OrderByDescending(spelling => spelling.Count)
            .ThenBy(spelling => spelling.Name, StringComparer.Ordinal)
            .ToList();

        var currencies = group
            .GroupBy(row => row.Currency, StringComparer.Ordinal)
            .OrderBy(currency => currency.Key, StringComparer.Ordinal)
            .Select(currency => new CounterpartyCurrency(
                currency.Key,
                currency.Sum(row => row.Count),
                MoneyText.Format(currency.Sum(row => row.MoneyOut)),
                MoneyText.Format(currency.Sum(row => row.MoneyIn))))
            .ToList();

        var accounts = group
            .SelectMany(row => row.MaskedAccounts)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(MaxCounterpartyAccounts)
            .ToList();

        return new CounterpartyEntry(
            IbanText.MaskInText(spellings[0].Name)!,
            CounterpartyRef.For(spellings[0].Name)!,
            spellings.Take(MaxSpellings).Select(spelling => IbanText.MaskInText(spelling.Name)!).Distinct(StringComparer.Ordinal).ToList(),
            group.Sum(row => row.Count),
            currencies,
            DateText(group.Min(row => row.FirstDate)),
            DateText(group.Max(row => row.LastDate)),
            accounts);
    }

    private static SearchPosition? DecodeCursor(string? cursor, string filterHash)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        if (!SearchCursor.TryDecode(cursor.Trim(), out var position))
        {
            throw new LedgerQueryException("The cursor is not valid; repeat the search without a cursor.");
        }

        if (!string.Equals(position.FilterHash, filterHash, StringComparison.Ordinal))
        {
            throw new LedgerQueryException("This cursor belongs to a different search; repeat the search without a cursor.");
        }

        return position;
    }

    private static SearchStatus ParseStatus(string? status)
    {
        return status?.Trim().ToLower(CultureInfo.InvariantCulture) switch
        {
            null or "" or "both" => SearchStatus.Both,
            "booked" => SearchStatus.Booked,
            "pending" => SearchStatus.Pending,
            _ => throw new LedgerQueryException("status must be booked, pending or both.")
        };
    }

    private static string StatusText(SearchStatus status)
    {
        return status switch
        {
            SearchStatus.Booked => "booked",
            SearchStatus.Pending => "pending",
            _ => "both"
        };
    }

    private static TotalsResult Shape(
        TotalsRequest request,
        ResolvedPeriod period,
        LedgerQueryFilter filter,
        TotalsGrouping grouping,
        TotalsData data,
        TotalsAggregate aggregate,
        string timeZone)
    {
        var from = DateText(period.Range.From);
        var to = DateText(period.Range.To);

        var currencies = aggregate.Currencies
            .Select(currency => new TotalsCurrency(
                currency.Currency,
                MoneyText.Format(currency.MoneyOut),
                MoneyText.Format(currency.MoneyIn),
                MoneyText.Format(currency.Net),
                currency.Count,
                currency.CounterpartyCount,
                currency.Counterparties
                    .Select(share => new TotalsCounterparty(
                        IbanText.MaskInText(share.Name)!,
                        share.Ref,
                        MoneyText.Format(share.MoneyOut),
                        MoneyText.Format(share.MoneyIn),
                        share.Count,
                        share.IsRemainder ? share.MergedCounterparties : null))
                    .ToList(),
                currency.Groups
                    .Select(group => new TotalsGroup(
                        group.CounterpartyRef is null ? group.Label : IbanText.MaskInText(group.Label)!,
                        group.From is { } groupFrom ? DateText(groupFrom) : null,
                        group.To is { } groupTo ? DateText(groupTo) : null,
                        group.AccountKey,
                        group.CounterpartyRef,
                        MoneyText.Format(group.MoneyOut),
                        MoneyText.Format(group.MoneyIn),
                        MoneyText.Format(group.Net),
                        group.Count,
                        group.MergedCounterparties > 1 ? group.MergedCounterparties : null))
                    .ToList()))
            .ToList();

        var currencyCodes = aggregate.Currencies.Select(currency => currency.Currency).ToList();
        var pending = ExcludedFor(currencyCodes, data.Pending);
        var transfers = ExcludedFor(currencyCodes, data.InternalTransfers);

        var dataAsOf = data.Accounts
            .Select(account => new TotalsDataAsOf(
                account.AccountKey,
                account.DisplayName ?? "Account " + account.AccountKey,
                account.LastSuccessfulSync is { } sync ? Instant(sync) : null))
            .ToList();

        var summary = Summary(period.Range, aggregate, pending, transfers, data.Accounts, grouping, timeZone);

        return new TotalsResult(
            new TotalsPeriod(from, to, period.Requested, timeZone),
            new TotalsFilters(
                filter.CounterpartyTerms,
                filter.CounterpartyRefs ?? [],
                filter.DescriptionTerms,
                filter.AccountKeys,
                DirectionText(filter.Direction),
                filter.MinAmount is { } min ? MoneyText.Format(min) : null,
                filter.MaxAmount is { } max ? MoneyText.Format(max) : null,
                GroupingText(grouping)),
            BasisText(timeZone),
            currencies,
            pending.Select(ExcludedResult).ToList(),
            transfers.Select(ExcludedResult).ToList(),
            GroupedByText,
            dataAsOf,
            summary);
    }

    private static string BasisText(string timeZone)
    {
        return $"booked transactions by booking date (value date, transaction date or first-seen day where the bank gave none) in {timeZone}; "
            + "pending reported separately; "
            + "transfers between the household's own synced accounts excluded";
    }

    private static List<ExcludedTotals> ExcludedFor(IReadOnlyList<string> currencies, IReadOnlyList<ExcludedTotals> excluded)
    {
        return currencies
            .Select(currency => excluded.FirstOrDefault(item => item.Currency == currency) ?? new ExcludedTotals(currency, 0, 0m, 0m))
            .ToList();
    }

    private static TotalsExcluded ExcludedResult(ExcludedTotals excluded)
    {
        return new TotalsExcluded(
            excluded.Currency,
            excluded.Count,
            MoneyText.Format(excluded.MoneyOut),
            MoneyText.Format(excluded.MoneyIn));
    }

    private static string Summary(
        DateRange range,
        TotalsAggregate aggregate,
        IReadOnlyList<ExcludedTotals> pending,
        IReadOnlyList<ExcludedTotals> transfers,
        IReadOnlyList<TotalsAccount> accounts,
        TotalsGrouping grouping,
        string timeZone)
    {
        var from = DateText(range.From);
        var to = DateText(range.To);
        var sentences = new List<string>();
        var dataAsOf = DataAsOfSentence(accounts);
        var grouped = "Grouped by counterparty name, not by category.";

        foreach (var currency in aggregate.Currencies.Where(currency => currency.Count > 0))
        {
            var pendingFor = pending.First(item => item.Currency == currency.Currency);
            var transfersFor = transfers.First(item => item.Currency == currency.Currency);

            sentences.Add(
                $"{MoneyText.Format(currency.MoneyOut)} {currency.Currency} out, {MoneyText.Format(currency.MoneyIn)} {currency.Currency} in, "
                + $"net {MoneyText.Format(currency.Net)} {currency.Currency} across {Plural(currency.Count, "booked transaction")} "
                + $"at {Plural(currency.CounterpartyCount, "counterparty", "counterparties")}, {from} to {to} ({timeZone}). "
                + NotIncluded(pendingFor, transfersFor)
                + " "
                + grouped
                + " "
                + dataAsOf);
        }

        if (sentences.Count == 0)
        {
            sentences.Add($"No booked transactions matched between {from} and {to}.");

            foreach (var currency in aggregate.Currencies)
            {
                var pendingFor = pending.First(item => item.Currency == currency.Currency);
                var transfersFor = transfers.First(item => item.Currency == currency.Currency);

                if (pendingFor.Count > 0 || transfersFor.Count > 0)
                {
                    sentences.Add(NotIncluded(pendingFor, transfersFor));
                }
            }

            sentences.Add(dataAsOf);
        }

        return string.Join(" ", sentences);
    }

    private static string NotIncluded(ExcludedTotals pending, ExcludedTotals transfers)
    {
        var pendingText = pending.Count == 0
            ? "no pending transactions"
            : $"{Plural(pending.Count, "pending transaction")} ({AmountsText(pending)})";
        var transferText = transfers.Count == 0
            ? "no transfers between the household's own accounts"
            : $"{Plural(transfers.Count, "transfer")} between the household's own accounts";

        return $"Not included: {pendingText} and {transferText}.";
    }

    private static string AmountsText(ExcludedTotals excluded)
    {
        var parts = new List<string>();

        if (excluded.MoneyOut != 0m)
        {
            parts.Add($"{MoneyText.Format(excluded.MoneyOut)} {excluded.Currency} out");
        }

        if (excluded.MoneyIn != 0m)
        {
            parts.Add($"{MoneyText.Format(excluded.MoneyIn)} {excluded.Currency} in");
        }

        return parts.Count == 0 ? $"0.00 {excluded.Currency}" : string.Join(", ", parts);
    }

    private static string DataAsOfSentence(IReadOnlyList<TotalsAccount> accounts)
    {
        if (accounts.Count == 0)
        {
            return "Data as of: no synced accounts.";
        }

        if (accounts.Any(account => account.LastSuccessfulSync is null))
        {
            return "Data as of: unknown, because an account in scope has no successful sync yet.";
        }

        return "Data as of " + Instant(accounts.Min(account => account.LastSuccessfulSync!.Value)) + ".";
    }

    private static string Plural(int count, string singular, string? plural = null)
    {
        return count == 1 ? $"1 {singular}" : $"{count} {plural ?? singular + "s"}";
    }

    private static string Instant(DateTimeOffset instant)
    {
        return instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        throw new LedgerQueryException("Dates must be written as yyyy-MM-dd, for example 2026-08-31.");
    }

    private static List<string> Terms(IReadOnlyList<string>? terms)
    {
        if (terms is null || terms.Count == 0)
        {
            return [];
        }

        if (terms.Count > MaxTerms)
        {
            throw new LedgerQueryException("At most 10 values may be given in one list.");
        }

        var trimmed = terms.Select(term => string.Join(' ', (term ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).ToList();

        if (trimmed.Any(term => term.Length is < 1 or > MaxTermLength))
        {
            throw new LedgerQueryException("Each value must be between 1 and 100 characters.");
        }

        return trimmed.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string> References(IReadOnlyList<string>? references)
    {
        var terms = Terms(references);

        if (terms.Any(term => !CounterpartyRef.IsWellFormed(term)))
        {
            throw new LedgerQueryException("A counterparty reference is not valid. Use a counterparty_ref from an earlier result.");
        }

        return terms;
    }

    private static MoneyDirection ParseDirection(string? direction)
    {
        return direction?.Trim().ToLower(CultureInfo.InvariantCulture) switch
        {
            null or "" or "both" => MoneyDirection.Both,
            "out" => MoneyDirection.Out,
            "in" => MoneyDirection.In,
            _ => throw new LedgerQueryException("direction must be out, in or both.")
        };
    }

    private static TotalsGrouping ParseGrouping(string? groupBy)
    {
        return groupBy?.Trim().ToLower(CultureInfo.InvariantCulture) switch
        {
            null or "" or "none" => TotalsGrouping.None,
            "counterparty" => TotalsGrouping.Counterparty,
            "month" => TotalsGrouping.Month,
            "week" => TotalsGrouping.Week,
            "day" => TotalsGrouping.Day,
            "account" => TotalsGrouping.Account,
            _ => throw new LedgerQueryException("groupBy must be none, counterparty, month, week, day or account.")
        };
    }

    private static void CheckAmounts(decimal? minAmount, decimal? maxAmount)
    {
        if (minAmount < 0m || maxAmount < 0m)
        {
            throw new LedgerQueryException("minAmount and maxAmount must not be negative.");
        }

        if (minAmount > maxAmount)
        {
            throw new LedgerQueryException("minAmount must not be above maxAmount.");
        }
    }

    private static string DirectionText(MoneyDirection direction)
    {
        return direction switch
        {
            MoneyDirection.Out => "out",
            MoneyDirection.In => "in",
            _ => "both"
        };
    }

    private static string GroupingText(TotalsGrouping grouping)
    {
        return grouping switch
        {
            TotalsGrouping.Counterparty => "counterparty",
            TotalsGrouping.Month => "month",
            TotalsGrouping.Week => "week",
            TotalsGrouping.Day => "day",
            TotalsGrouping.Account => "account",
            _ => "none"
        };
    }

    private static string DateText(DateOnly date)
    {
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
