using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.Service.Ingestion;
using Ledger.Service.Ingestion.EnableBanking;

namespace Ledger.UnitTests.Ingestion.EnableBanking;

/// <summary>
/// Replays captured aggregator pages through the real adapter mapping and the real reconciler, day by day, and checks that
/// the ledger never duplicates, never loses a booked state and never keeps a ghost. The real captures stay outside the
/// repository: the replay reads them only when told where they are, and nothing it prints, asserts or writes is a captured
/// value. Only counts leave it.
/// </summary>
[Trait("Category", "Replay")]
public partial class CapturedPairReplayTests
{
    private const string CapturesVariable = "LEDGER_REPLAY_CAPTURES";
    private const string ReportVariable = "LEDGER_REPLAY_REPORT";
    private const string Marker = "ZZMARKER-REPLAY-7Q4X";

    [Fact]
    public void Synthetic_captures_replay_to_one_booked_pair_one_dropped_row_and_two_identical_payments()
    {
        using var directory = new TempDirectory();
        WriteSyntheticCaptureSet(directory.Path);
        var report = System.IO.Path.Combine(directory.Path, "report.txt");

        var result = CaptureReplay.Run(directory.Path, report);

        result.Captures.Should().Be(3);
        result.Rows.Should().Be(4);
        result.Merged.Should().Be(1);
        result.Dropped.Should().Be(1);
        result.LivePending.Should().Be(0);
        result.LiveBooked.Should().Be(3);
        result.ExcludedSandboxPages.Should().Be(1);
        result.ReapplyChanges.Should().Be(0);
        result.IncompleteCaptures.Should().Be(0);
    }

    [Fact]
    public void Synthetic_replay_keeps_both_references_on_the_merged_row()
    {
        using var directory = new TempDirectory();
        WriteSyntheticCaptureSet(directory.Path);

        var result = CaptureReplay.Run(directory.Path, null);

        result.RowsWithTwoReferences.Should().Be(1);
        result.ReferencesOnMoreThanOneRow.Should().Be(0);
    }

    [Fact]
    public void Captures_with_the_same_second_and_account_replay_in_label_order()
    {
        using var directory = new TempDirectory();
        var pending = EnableBankingFixtures.Transaction("synthetic-order", amount: "5.00", status: "PDNG", bookingDate: "2026-10-01");
        var booked = EnableBankingFixtures.Transaction("synthetic-order", amount: "5.00", status: "BOOK", bookingDate: "2026-10-01");
        WriteCapture(directory.Path, "20261001T080000Z", "b-second", 1, 1, Page(null, booked));
        WriteCapture(directory.Path, "20261001T080000Z", "a-first", 1, 1, Page(null, pending));

        var result = CaptureReplay.Run(directory.Path, null);

        result.Captures.Should().Be(2);
        result.Rows.Should().Be(1);
        result.Upgraded.Should().Be(1, "the pending capture sorts first by label, so the booked one upgrades it");
        result.LiveBooked.Should().Be(1);
    }

    [Fact]
    public void The_report_line_holds_only_labels_and_numbers()
    {
        using var directory = new TempDirectory();
        WriteSyntheticCaptureSet(directory.Path);
        var report = System.IO.Path.Combine(directory.Path, "report.txt");

        CaptureReplay.Run(directory.Path, report);

        var lines = File.ReadAllLines(report);
        lines.Should().ContainSingle();
        ReportLine().IsMatch(lines[0]).Should().BeTrue("the report line must be counts only");
    }

    [Fact]
    public void An_unmappable_item_fails_the_replay_without_echoing_any_captured_value()
    {
        using var directory = new TempDirectory();
        WriteCapture(directory.Path, "20261001T080000Z", "day-1", 1, 1, Page(null, ValidItem(), UnmappableItem()));

        var failure = Record.Exception(() => CaptureReplay.Run(directory.Path, null));

        failure.Should().BeOfType<ReplayFailedException>();
        AssertNoMarker(failure!);
    }

    [Fact]
    public void An_unparseable_amount_fails_the_replay_without_echoing_any_captured_value()
    {
        using var directory = new TempDirectory();
        WriteCapture(directory.Path, "20261001T080000Z", "day-1", 1, 1, Page(null, ValidItem(), UnparseableAmountItem()));

        var failure = Record.Exception(() => CaptureReplay.Run(directory.Path, null));

        failure.Should().BeOfType<ReplayFailedException>();
        AssertNoMarker(failure!);
    }

    [Fact]
    public void A_page_that_is_not_json_makes_the_capture_incomplete_and_leaks_nothing()
    {
        using var directory = new TempDirectory();
        WriteCapture(directory.Path, "20261001T080000Z", "day-1", 1, 1, $"{{\"transactions\": [ {Marker} ] ");
        var report = System.IO.Path.Combine(directory.Path, "report.txt");

        var result = CaptureReplay.Run(directory.Path, report);

        result.UnreadablePages.Should().Be(1);
        result.IncompleteCaptures.Should().Be(1);
        File.ReadAllText(report).Should().NotContain(Marker);
    }

    [Fact]
    public void A_broken_invariant_fails_with_counts_only()
    {
        var ledger = new InMemoryLedger();
        var pending = Item("er-pending", ProviderTransactionStatus.Pending);
        var booked = Item("er-booked", ProviderTransactionStatus.Booked);
        ledger.Apply(
            new ReconciliationPlan(
                [new PlannedInsert(pending, "er:er-pending", MatchFlag.None)],
                [],
                [],
                [],
                []),
            DateTimeOffset.Parse("2026-10-01T08:00:00Z", CultureInfo.InvariantCulture));
        ledger.Apply(
            new ReconciliationPlan(
                [new PlannedInsert(booked, "er:er-booked", MatchFlag.None)],
                [],
                [],
                [],
                []),
            DateTimeOffset.Parse("2026-10-02T08:00:00Z", CultureInfo.InvariantCulture));

        var failure = Record.Exception(() => ReplayInvariants.Verify(ledger, [], 5));

        failure.Should().BeOfType<ReplayFailedException>();
        AssertNoMarker(failure!);
    }

    [Fact]
    public void A_reference_that_maps_to_two_rows_is_counted_and_fails_the_replay_naming_the_invariant()
    {
        var ledger = new InMemoryLedger();
        var observedAt = DateTimeOffset.Parse("2026-10-01T08:00:00Z", CultureInfo.InvariantCulture);
        ledger.Apply(
            new ReconciliationPlan([new PlannedInsert(Item("er-shared", ProviderTransactionStatus.Booked), "er:er-shared", MatchFlag.None)], [], [], [], []),
            observedAt);
        ledger.ReferencesOnMoreThanOneRow.Should().Be(0);

        ledger.Apply(
            new ReconciliationPlan([new PlannedInsert(Item("er-shared", ProviderTransactionStatus.Booked), "er:er-shared", MatchFlag.None)], [], [], [], []),
            observedAt);

        ledger.ReferencesOnMoreThanOneRow.Should().Be(1);
        var failure = Record.Exception(() => ReplayInvariants.Verify(ledger, [], 5));

        failure.Should().BeOfType<ReplayFailedException>();
        failure!.Message.Should().Be("Invariant broken: 1 references map to more than one row.");
        AssertNoMarker(failure);
    }

    [Fact]
    public void Real_captures_replay_without_a_duplicate_a_ghost_or_a_reference_on_two_rows()
    {
        var captures = Environment.GetEnvironmentVariable(CapturesVariable);
        if (string.IsNullOrWhiteSpace(captures))
        {
            Assert.Skip("Set " + CapturesVariable + " to a directory of decrypted capture files to replay real captures.");
        }

        var result = CaptureReplay.Run(captures!, Environment.GetEnvironmentVariable(ReportVariable));

        result.Captures.Should().BeGreaterThan(0, "the capture directory held no transaction pages");
        result.ReferencesOnMoreThanOneRow.Should().Be(0);
        result.ReapplyChanges.Should().Be(0);
    }

    [GeneratedRegex(@"^([a-z_]+=[0-9]+)( [a-z_]+=[0-9]+)*$")]
    private static partial Regex ReportLine();

    private static void AssertNoMarker(Exception failure)
    {
        failure.ToString().Should().NotContain(Marker, "no captured value may appear in the failure output");
        failure.InnerException.Should().BeNull();
    }

    private static ProviderTransaction Item(string reference, ProviderTransactionStatus status)
    {
        return new ProviderTransaction(
            reference,
            status,
            -25.00m,
            "EUR",
            new DateOnly(2026, 10, 1),
            null,
            null,
            Marker,
            null,
            Marker,
            "{}");
    }

    private static string ValidItem()
    {
        return EnableBankingFixtures.Transaction(Marker + "-reference", remittance: $"[\"{Marker}\"]");
    }

    private static string UnmappableItem()
    {
        return $$"""
            {"entry_reference":"{{Marker}}","status":"BOOK","credit_debit_indicator":"{{Marker}}","transaction_amount":{"currency":"EUR","amount":"1.00"},"booking_date":"2026-09-30","creditor":{"name":"{{Marker}}"},"remittance_information":["{{Marker}}"]}
            """;
    }

    private static string UnparseableAmountItem()
    {
        return $$"""
            {"entry_reference":"{{Marker}}-amount","status":"BOOK","credit_debit_indicator":"DBIT","transaction_amount":{"currency":"EUR","amount":"{{Marker}}"},"booking_date":"2026-09-30","creditor":{"name":"{{Marker}}"},"remittance_information":["{{Marker}}"]}
            """;
    }

    private static string Page(string? continuationKey, params string[] items)
    {
        return EnableBankingFixtures.Page(continuationKey, items);
    }

    private static void WriteCapture(string directory, string timestamp, string label, int account, int page, string body)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"{timestamp}-{label}-a{account}-transactions-p{page}.json");
        File.WriteAllText(System.IO.Path.Combine(directory, name), body);
    }

    private static void WriteSyntheticCaptureSet(string directory)
    {
        var pendingPair = EnableBankingFixtures.Transaction("synthetic-pair-pending", amount: "25.00", status: "PDNG", bookingDate: "2026-10-01");
        var bookedPair = EnableBankingFixtures.Transaction("synthetic-pair-booked", amount: "25.00", status: "BOOK", bookingDate: "2026-10-02");
        var vanishing = EnableBankingFixtures.Transaction("synthetic-vanishing", amount: "7.00", status: "PDNG", bookingDate: "2026-10-01");
        var firstTwin = EnableBankingFixtures.Transaction("synthetic-twin-one", amount: "9.99", bookingDate: "2026-10-02");
        var secondTwin = EnableBankingFixtures.Transaction("synthetic-twin-two", amount: "9.99", bookingDate: "2026-10-02");

        WriteCapture(directory, "20261001T080000Z", "day-1", 1, 1, Page(null, pendingPair, vanishing));
        WriteCapture(directory, "20261002T080000Z", "day-2", 1, 1, Page(null, bookedPair, vanishing, firstTwin, secondTwin));
        WriteCapture(directory, "20261003T080000Z", "day-3", 1, 1, Page("continue"));
        WriteCapture(directory, "20261003T080000Z", "day-3", 1, 2, Page(null, bookedPair, firstTwin, secondTwin));
        WriteCapture(directory, "20261003T080000Z", "sandbox", 1, 1, Page(null, EnableBankingFixtures.Transaction("synthetic-sandbox-only", amount: "1.00")));
        File.WriteAllText(
            System.IO.Path.Combine(directory, "20261003T080000Z-day-3-a1-balances-p1.json"),
            EnableBankingFixtures.ExpectedBalanceOnly());
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ledger-replay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

/// <summary>A replay failure. Its message carries counts and fixed words only, never a captured value.</summary>
public sealed class ReplayFailedException(string message) : Exception(message);

/// <summary>What a replay did, as counts.</summary>
public sealed record ReplayResult(
    int Accounts,
    int Captures,
    int Pages,
    int Items,
    int Rows,
    int Inserted,
    int Merged,
    int Upgraded,
    int Flagged,
    int Dropped,
    int Restored,
    int LivePending,
    int LiveBooked,
    int RowsWithTwoReferences,
    int ReferencesOnMoreThanOneRow,
    int UnreadablePages,
    int IncompleteCaptures,
    int ExcludedSandboxPages,
    int ReapplyChanges)
{
    /// <summary>One line of labels and numbers.</summary>
    public string ToReportLine()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"accounts={Accounts} captures={Captures} pages={Pages} items={Items} rows={Rows} inserted={Inserted} merged={Merged} upgraded={Upgraded} flagged={Flagged} dropped={Dropped} restored={Restored} live_pending={LivePending} live_booked={LiveBooked} rows_with_two_references={RowsWithTwoReferences} references_on_more_than_one_row={ReferencesOnMoreThanOneRow} unreadable_pages={UnreadablePages} incomplete_captures={IncompleteCaptures} excluded_sandbox_pages={ExcludedSandboxPages} reapply_changes={ReapplyChanges}");
    }
}

/// <summary>The checks a replay runs after every captured fetch. A failure names the check and carries counts only.</summary>
public static class ReplayInvariants
{
    /// <summary>
    /// Verifies the ledger after a fetch was applied: no reference on two rows, no booked row back to pending or dropped, and
    /// no live pending row that has a certain booked partner. The rows before the fetch let it see a status going backwards.
    /// </summary>
    /// <exception cref="ReplayFailedException">An invariant does not hold.</exception>
    public static void Verify(InMemoryLedger ledger, IReadOnlyList<LedgerTransactionState> before, int matchWindowDays)
    {
        var duplicated = ledger.ReferencesOnMoreThanOneRow;
        if (duplicated > 0)
        {
            throw new ReplayFailedException($"Invariant broken: {duplicated} references map to more than one row.");
        }

        var after = ledger.State.ToDictionary(state => state.Id);
        var regressed = before.Count(state =>
            state.Status == LedgerTransactionStatus.Booked
            && (!after.TryGetValue(state.Id, out var current) || current.Status != LedgerTransactionStatus.Booked));
        if (regressed > 0)
        {
            throw new ReplayFailedException($"Invariant broken: {regressed} booked rows are no longer booked.");
        }

        var partnered = PendingRowsWithCertainBookedPartner(ledger.State, matchWindowDays);
        if (partnered > 0)
        {
            throw new ReplayFailedException($"Invariant broken: {partnered} pending rows have a certain booked partner.");
        }
    }

    /// <summary>
    /// Counts live pending rows that a separate booked row first seen later matches on amount, currency, counterparty and date
    /// window, exactly one booked row for that pending row and exactly one pending row for that booked row.
    /// </summary>
    public static int PendingRowsWithCertainBookedPartner(IReadOnlyList<LedgerTransactionState> rows, int matchWindowDays)
    {
        var pending = rows.Where(row => row.Status == LedgerTransactionStatus.Pending).ToList();
        var booked = rows.Where(row => row.Status == LedgerTransactionStatus.Booked).ToList();

        var partners = pending.ToDictionary(
            row => row.Id,
            row => booked.Where(candidate => IsPartner(row, candidate, matchWindowDays)).ToList());

        return pending.Count(row =>
            partners[row.Id].Count == 1
            && pending.Count(other => partners[other.Id].Any(candidate => candidate.Id == partners[row.Id][0].Id)) == 1);
    }

    /// <summary>
    /// Counts live pending rows the fetch should have dropped: unflagged, inside the fetched window and absent from a complete,
    /// non-empty fetch.
    /// </summary>
    public static int GhostRows(IReadOnlyList<LedgerTransactionState> rows, ISet<string> fetchedReferences, FetchCoverage coverage)
    {
        if (!coverage.Complete || coverage.ItemCount <= 0)
        {
            return 0;
        }

        return rows.Count(row =>
            row.Status == LedgerTransactionStatus.Pending
            && row.Flag != MatchFlag.Ambiguous
            && !row.Refs.Any(fetchedReferences.Contains)
            && (coverage.From is not { } from || EffectiveDate(row) >= from));
    }

    /// <summary>The date the reconciler treats a stored row as having.</summary>
    public static DateOnly EffectiveDate(LedgerTransactionState state)
    {
        return state.TransactionDate
            ?? state.BookingDate
            ?? state.ValueDate
            ?? DateOnly.FromDateTime(state.FirstSeenAt.UtcDateTime);
    }

    private static bool IsPartner(LedgerTransactionState pending, LedgerTransactionState booked, int windowDays)
    {
        var pendingCounterparty = TextNormalizer.ForMatching(pending.CounterpartyName);
        var bookedCounterparty = TextNormalizer.ForMatching(booked.CounterpartyName);

        return booked.FirstSeenAt >= pending.FirstSeenAt
            && booked.Amount == pending.Amount
            && string.Equals(booked.Currency, pending.Currency, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(pendingCounterparty)
            && pendingCounterparty == bookedCounterparty
            && Math.Abs(EffectiveDate(booked).DayNumber - EffectiveDate(pending).DayNumber) <= windowDays;
    }
}

/// <summary>
/// Reads a directory of decrypted capture files, maps every transactions page through the real adapter mapping and replays
/// the captures per account in time order through the real reconciler and an in-memory ledger.
/// </summary>
public static partial class CaptureReplay
{
    private const string SandboxLabel = "sandbox";

    /// <summary>
    /// Replays the captures. Files that are not transaction pages are ignored and sandbox captures are left out. When a report
    /// path is given, one line of counts is written to it.
    /// </summary>
    /// <exception cref="ReplayFailedException">A capture cannot be mapped or an invariant does not hold; the message has no captured value.</exception>
    public static ReplayResult Run(string directory, string? reportPath)
    {
        try
        {
            var result = Replay(directory);

            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                File.WriteAllText(reportPath, result.ToReportLine() + "\n");
            }

            return result;
        }
        catch (ReplayFailedException)
        {
            throw;
        }
        catch (BankProviderException exception)
        {
            throw new ReplayFailedException($"A captured item could not be mapped: failure kind {exception.Kind}, code {SafeCode(exception.ProviderCode)}.");
        }
        catch (Exception exception)
        {
            throw new ReplayFailedException($"The replay stopped on an unexpected {exception.GetType().Name}; no captured value is shown.");
        }
    }

    private static ReplayResult Replay(string directory)
    {
        var options = new IngestionOptions();
        var reconcilerOptions = new ReconcilerOptions(options.MatchWindowDays, options.ResolveTimeZone());

        var pages = new List<CapturePage>();
        var excludedSandbox = 0;

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var match = PageName().Match(System.IO.Path.GetFileName(path));
            if (!match.Success)
            {
                continue;
            }

            if (string.Equals(match.Groups["label"].Value, SandboxLabel, StringComparison.Ordinal))
            {
                excludedSandbox++;
                continue;
            }

            pages.Add(new CapturePage(
                path,
                match.Groups["timestamp"].Value,
                match.Groups["label"].Value,
                int.Parse(match.Groups["account"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["page"].Value, CultureInfo.InvariantCulture)));
        }

        var ledgers = new Dictionary<int, InMemoryLedger>();
        var captureCount = 0;
        var itemCount = 0;
        var unreadable = 0;
        var incomplete = 0;
        var reapplyChanges = 0;

        var captures = pages
            .GroupBy(page => (page.Timestamp, page.Label, page.Account))
            .OrderBy(group => group.Key.Timestamp, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Account)
            .ThenBy(group => group.Key.Label, StringComparer.Ordinal);

        foreach (var capture in captures)
        {
            var read = ReadCapture(capture.OrderBy(page => page.Page).ToList());
            unreadable += read.UnreadablePages;
            incomplete += read.Complete ? 0 : 1;
            itemCount += read.Items.Count;
            captureCount++;

            if (!ledgers.TryGetValue(capture.Key.Account, out var ledger))
            {
                ledger = new InMemoryLedger();
                ledgers[capture.Key.Account] = ledger;
            }

            var observedAt = DateTimeOffset.ParseExact(
                capture.Key.Timestamp,
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

            var coverage = new FetchCoverage(FetchFrom(ledger, options.OverlapDays), read.Complete, read.Items.Count);
            var before = ledger.State;

            ledger.Apply(TransactionReconciler.Plan(before, read.Items, coverage, reconcilerOptions), observedAt);

            var fetched = TransactionRefs.For(read.Items).ToHashSet(StringComparer.Ordinal);
            ReplayInvariants.Verify(ledger, before, options.MatchWindowDays);

            var ghosts = ReplayInvariants.GhostRows(ledger.State, fetched, coverage);
            if (ghosts > 0)
            {
                throw new ReplayFailedException($"Invariant broken: {ghosts} pending rows were left live after a complete fetch.");
            }

            reapplyChanges += Reapply(ledger, read.Items, coverage, reconcilerOptions, observedAt);
        }

        return Summarise(ledgers.Values.ToList(), captureCount, pages.Count, itemCount, unreadable, incomplete, excludedSandbox, reapplyChanges);
    }

    private static int Reapply(
        InMemoryLedger ledger,
        IReadOnlyList<ProviderTransaction> items,
        FetchCoverage coverage,
        ReconcilerOptions reconcilerOptions,
        DateTimeOffset observedAt)
    {
        var before = ledger.State;
        var counters = Counters(ledger);

        ledger.Apply(TransactionReconciler.Plan(before, items, coverage, reconcilerOptions), observedAt);

        var after = ledger.State;
        var changed = Counters(ledger) - counters;
        var sameShape = before.Count == after.Count
            && before.Zip(after).All(pair =>
                pair.First.Id == pair.Second.Id
                && pair.First.Status == pair.Second.Status
                && pair.First.Refs.Count == pair.Second.Refs.Count
                && pair.First.Flag == pair.Second.Flag);

        if (changed != 0 || !sameShape)
        {
            throw new ReplayFailedException($"Invariant broken: applying the same fetch twice changed the ledger ({changed} counted changes).");
        }

        return changed;
    }

    private static int Counters(InMemoryLedger ledger)
    {
        return ledger.Inserted + ledger.Merged + ledger.Flagged + ledger.Dropped + ledger.Restored + ledger.Updated;
    }

    private static DateOnly? FetchFrom(InMemoryLedger ledger, int overlapDays)
    {
        var live = ledger.State.Where(state => state.Status != LedgerTransactionStatus.Dropped).ToList();
        if (live.Count == 0)
        {
            return null;
        }

        var from = live.Max(ReplayInvariants.EffectiveDate).AddDays(-Math.Max(0, overlapDays));
        var oldestPending = live
            .Where(state => state.Status == LedgerTransactionStatus.Pending)
            .Select(ReplayInvariants.EffectiveDate)
            .Cast<DateOnly?>()
            .Min();

        return oldestPending is { } pending && pending < from ? pending : from;
    }

    private static CaptureRead ReadCapture(IReadOnlyList<CapturePage> pages)
    {
        var items = new List<ProviderTransaction>();
        var unreadable = 0;
        var complete = pages.Select((page, index) => page.Page == index + 1).All(contiguous => contiguous);

        for (var index = 0; index < pages.Count; index++)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(pages[index].Path));
            }
            catch (JsonException)
            {
                unreadable++;
                complete = false;
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("transactions", out var transactions)
                    || transactions.ValueKind != JsonValueKind.Array)
                {
                    unreadable++;
                    complete = false;
                    continue;
                }

                foreach (var element in transactions.EnumerateArray())
                {
                    items.Add(EnableBankingJson.MapTransaction(element));
                }

                var lastPage = index == pages.Count - 1;
                if (lastPage && root.TryGetProperty("continuation_key", out var key) && key.ValueKind != JsonValueKind.Null)
                {
                    complete = false;
                }
            }
        }

        return new CaptureRead(items, complete, unreadable);
    }

    private static ReplayResult Summarise(
        IReadOnlyList<InMemoryLedger> ledgers,
        int captures,
        int pages,
        int items,
        int unreadable,
        int incomplete,
        int excludedSandbox,
        int reapplyChanges)
    {
        var rows = ledgers.SelectMany(ledger => ledger.State).ToList();

        return new ReplayResult(
            ledgers.Count,
            captures,
            pages,
            items,
            rows.Count,
            ledgers.Sum(ledger => ledger.Inserted),
            ledgers.Sum(ledger => ledger.Merged),
            ledgers.Sum(ledger => ledger.Upgraded),
            ledgers.Sum(ledger => ledger.Flagged),
            ledgers.Sum(ledger => ledger.Dropped),
            ledgers.Sum(ledger => ledger.Restored),
            ledgers.Sum(ledger => ledger.LivePending),
            ledgers.Sum(ledger => ledger.LiveBooked),
            rows.Count(row => row.Refs.Count > 1),
            ledgers.Sum(ledger => ledger.ReferencesOnMoreThanOneRow),
            unreadable,
            incomplete,
            excludedSandbox,
            reapplyChanges);
    }

    private static string SafeCode(string? code)
    {
        return code is not null && SafeCodePattern().IsMatch(code) ? code : "none";
    }

    [GeneratedRegex(@"^(?<timestamp>[0-9]{8}T[0-9]{6}Z)-(?<label>.+)-a(?<account>[0-9]+)-transactions-p(?<page>[0-9]+)\.json$")]
    private static partial Regex PageName();

    [GeneratedRegex(@"^[a-z_]{1,48}$")]
    private static partial Regex SafeCodePattern();

    private sealed record CapturePage(string Path, string Timestamp, string Label, int Account, int Page);

    private sealed record CaptureRead(IReadOnlyList<ProviderTransaction> Items, bool Complete, int UnreadablePages);
}
