using System.Globalization;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;

namespace Ledger.UnitTests.Ingestion;

/// <summary>Verifies the pure reconciliation planner and the reference derivation it relies on.</summary>
[Trait("Category", "Reconciliation")]
public class TransactionReconcilerTests
{
    private static readonly FetchCoverage Coverage = new(null, true, 0);
    private static readonly ReconcilerOptions Options = new(5, TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"));

    [Fact]
    public void Unknown_booked_item_with_an_entry_reference_becomes_one_insert_under_that_reference()
    {
        var item = Item(entryReference: "entry-001", status: ProviderTransactionStatus.Booked);

        var plan = TransactionReconciler.Plan([], [item], Coverage, Options);

        plan.Updates.Should().BeEmpty();
        plan.Inserts.Should().ContainSingle()
            .Which.Should().Be(new PlannedInsert(item, "er:entry-001", MatchFlag.None));
    }

    [Fact]
    public void Known_reference_on_a_booked_row_becomes_an_update_without_an_upgrade()
    {
        var existing = State(LedgerTransactionStatus.Booked, "er:entry-001");
        var item = Item(entryReference: "entry-001", status: ProviderTransactionStatus.Booked);

        var plan = TransactionReconciler.Plan([existing], [item], Coverage, Options);

        plan.Inserts.Should().BeEmpty();
        plan.Updates.Should().ContainSingle()
            .Which.Should().Be(new PlannedUpdate(existing.Id, item, false));
    }

    [Fact]
    public void Pending_row_whose_reference_arrives_booked_becomes_an_update_with_an_upgrade()
    {
        var existing = State(LedgerTransactionStatus.Pending, "er:entry-001");
        var item = Item(entryReference: "entry-001", status: ProviderTransactionStatus.Booked);

        var plan = TransactionReconciler.Plan([existing], [item], Coverage, Options);

        plan.Inserts.Should().BeEmpty();
        plan.Updates.Should().ContainSingle()
            .Which.Should().Be(new PlannedUpdate(existing.Id, item, true));
    }

    [Fact]
    public void Incoming_pending_item_never_downgrades_a_booked_row()
    {
        var existing = State(LedgerTransactionStatus.Booked, "er:entry-001");
        var item = Item(entryReference: "entry-001", status: ProviderTransactionStatus.Pending);

        var plan = TransactionReconciler.Plan([existing], [item], Coverage, Options);

        plan.Inserts.Should().BeEmpty();
        plan.Updates.Should().BeEmpty();
    }

    [Fact]
    public void Two_identical_items_without_entry_references_get_occurrence_suffixes()
    {
        var first = Item(entryReference: null);
        var second = Item(entryReference: null);

        var references = TransactionRefs.For([first, second]);

        references.Should().HaveCount(2);
        references[0].Should().StartWith("fp:").And.EndWith(":0");
        references[1].Should().StartWith("fp:").And.EndWith(":1");
        references[0][..^2].Should().Be(references[1][..^2]);
    }

    [Fact]
    public void Fingerprint_is_identical_across_runs_and_machine_cultures()
    {
        var item = Item(entryReference: null, amount: -12.5m, counterparty: "Example  Grocer", description: "Weekly shop");
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariantReferences = TransactionRefs.For([item]);

            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            var dutchReferences = TransactionRefs.For([item]);

            dutchReferences.Should().Equal(invariantReferences);
            TransactionRefs.For([item]).Should().Equal(invariantReferences);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Fingerprint_ignores_case_and_spacing_in_counterparty_and_description()
    {
        var plain = Item(entryReference: null, counterparty: "Example Grocer", description: "Weekly shop");
        var messy = Item(entryReference: null, counterparty: "  example   GROCER ", description: "weekly  shop ");

        TransactionRefs.For([plain]).Should().Equal(TransactionRefs.For([messy]));
    }

    [Theory]
    [InlineData(ProviderTransactionStatus.Cancelled)]
    [InlineData(ProviderTransactionStatus.Other)]
    public void Cancelled_and_other_statuses_produce_no_insert(ProviderTransactionStatus status)
    {
        var item = Item(entryReference: "entry-002", status: status);

        var plan = TransactionReconciler.Plan([], [item], Coverage, Options);

        plan.Inserts.Should().BeEmpty();
        plan.Updates.Should().BeEmpty();
    }

    [Fact]
    public void Amount_with_five_decimals_is_rejected_as_malformed()
    {
        var item = Item(entryReference: "entry-003", amount: 1.23456m);

        var act = () => TransactionReconciler.Plan([], [item], Coverage, Options);

        act.Should().Throw<BankProviderException>().Which.Kind.Should().Be(ProviderErrorKind.MalformedData);
    }

    [Fact]
    public void Amount_with_sixteen_integer_digits_is_rejected_as_malformed()
    {
        var item = Item(entryReference: "entry-004", amount: 1_000_000_000_000_000m);

        var act = () => TransactionReconciler.Plan([], [item], Coverage, Options);

        act.Should().Throw<BankProviderException>().Which.Kind.Should().Be(ProviderErrorKind.MalformedData);
    }

    [Fact]
    public void Amount_with_fifteen_integer_digits_and_four_decimals_is_accepted()
    {
        var item = Item(entryReference: "entry-005", amount: -999_999_999_999_999.9999m);

        var plan = TransactionReconciler.Plan([], [item], Coverage, Options);

        plan.Inserts.Should().ContainSingle();
    }

    [Fact]
    public void Trailing_zero_decimals_are_not_treated_as_extra_precision()
    {
        var item = Item(entryReference: "entry-006", amount: 1.230000m);

        var plan = TransactionReconciler.Plan([], [item], Coverage, Options);

        plan.Inserts.Should().ContainSingle();
    }

    [Theory]
    [InlineData("eur")]
    [InlineData("EURO")]
    [InlineData("")]
    public void Invalid_currency_is_rejected_as_malformed(string currency)
    {
        var item = Item(entryReference: "entry-007", currency: currency);

        var act = () => TransactionReconciler.Plan([], [item], Coverage, Options);

        act.Should().Throw<BankProviderException>().Which.Kind.Should().Be(ProviderErrorKind.MalformedData);
    }

    [Fact]
    public void Payload_that_is_not_json_is_rejected_as_malformed()
    {
        var item = Item(entryReference: "entry-008") with { RawJson = "not json" };

        var act = () => TransactionReconciler.Plan([], [item], Coverage, Options);

        act.Should().Throw<BankProviderException>().Which.Kind.Should().Be(ProviderErrorKind.MalformedData);
    }

    [Fact]
    public void Repeated_reference_in_one_feed_is_planned_once_and_prefers_the_booked_version()
    {
        var pending = Item(entryReference: "entry-009", status: ProviderTransactionStatus.Pending);
        var booked = Item(entryReference: "entry-009", status: ProviderTransactionStatus.Booked);

        var plan = TransactionReconciler.Plan([], [pending, booked], Coverage, Options);

        plan.Inserts.Should().ContainSingle().Which.Item.Should().Be(booked);
    }

    [Fact]
    public void Opaque_keys_are_sixteen_lowercase_hex_characters_and_differ()
    {
        var first = OpaqueKey.New();
        var second = OpaqueKey.New();

        first.Should().MatchRegex("^[0-9a-f]{16}$");
        second.Should().MatchRegex("^[0-9a-f]{16}$");
        first.Should().NotBe(second);
    }

    [Fact]
    public void Matching_form_composes_collapses_whitespace_and_upper_cases_while_null_stays_null()
    {
        var decomposed = "Café   Example";

        TextNormalizer.ForMatching(decomposed).Should().Be("CAFÉ EXAMPLE");
        TextNormalizer.ForMatching("  \t ").Should().Be(string.Empty);
        TextNormalizer.ForMatching(null).Should().BeNull();
    }

    private static readonly DateOnly Day = new(2026, 9, 20);

    [Fact]
    public void Booked_item_with_a_new_reference_merges_into_the_single_certain_pending_row()
    {
        var pending = PendingState("er:A", Day);
        var booked = BookedItem("B", Day.AddDays(2));

        var plan = TransactionReconciler.Plan([pending], [booked], Coverage, Options);

        plan.Inserts.Should().BeEmpty();
        plan.FlagAmbiguous.Should().BeEmpty();
        plan.Merges.Should().ContainSingle()
            .Which.Should().Be(new PlannedMerge(pending.Id, booked, "er:B"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(-5, true)]
    [InlineData(-6, false)]
    public void Match_window_is_inclusive_at_five_days_and_closed_at_six(int offsetDays, bool merges)
    {
        var pending = PendingState("er:A", Day);
        var booked = BookedItem("B", Day.AddDays(offsetDays));

        var plan = TransactionReconciler.Plan([pending], [booked], Coverage, Options);

        plan.Merges.Should().HaveCount(merges ? 1 : 0);
        plan.Inserts.Should().HaveCount(merges ? 0 : 1);
        plan.FlagAmbiguous.Should().BeEmpty();
    }

    [Fact]
    public void Two_pending_candidates_for_one_booked_item_are_flagged_and_nothing_merges()
    {
        var first = PendingState("er:A1", Day);
        var second = PendingState("er:A2", Day.AddDays(1));
        var booked = BookedItem("B", Day.AddDays(2));

        var plan = TransactionReconciler.Plan([first, second], [booked], Coverage, Options);

        plan.Merges.Should().BeEmpty();
        plan.Inserts.Should().ContainSingle().Which.Flag.Should().Be(MatchFlag.None);
        plan.FlagAmbiguous.Should().BeEquivalentTo([first.Id, second.Id]);
    }

    [Fact]
    public void One_pending_candidate_for_two_booked_items_is_flagged_and_both_items_are_inserted()
    {
        var pending = PendingState("er:A", Day);
        var firstBooked = BookedItem("B1", Day.AddDays(1));
        var secondBooked = BookedItem("B2", Day.AddDays(2));

        var plan = TransactionReconciler.Plan([pending], [firstBooked, secondBooked], Coverage, Options);

        plan.Merges.Should().BeEmpty();
        plan.Inserts.Should().HaveCount(2);
        plan.FlagAmbiguous.Should().ContainSingle().Which.Should().Be(pending.Id);
    }

    [Fact]
    public void Pending_candidate_without_a_counterparty_is_flagged_and_never_merged()
    {
        var pending = PendingState("er:A", Day, counterparty: null);
        var booked = BookedItem("B", Day.AddDays(1));

        var plan = TransactionReconciler.Plan([pending], [booked], Coverage, Options);

        plan.Merges.Should().BeEmpty();
        plan.Inserts.Should().ContainSingle();
        plan.FlagAmbiguous.Should().ContainSingle().Which.Should().Be(pending.Id);
    }

    [Fact]
    public void Booked_item_without_a_counterparty_is_flagged_against_a_pending_candidate_and_never_merged()
    {
        var pending = PendingState("er:A", Day);
        var booked = BookedItem("B", Day.AddDays(1), counterparty: null);

        var plan = TransactionReconciler.Plan([pending], [booked], Coverage, Options);

        plan.Merges.Should().BeEmpty();
        plan.Inserts.Should().ContainSingle();
        plan.FlagAmbiguous.Should().ContainSingle().Which.Should().Be(pending.Id);
    }

    [Fact]
    public void Different_currency_or_amount_or_counterparty_is_no_candidate_at_all()
    {
        var pending = PendingState("er:A", Day);

        var otherCurrency = TransactionReconciler.Plan([pending], [BookedItem("B", Day, currency: "USD")], Coverage, Options);
        var otherAmount = TransactionReconciler.Plan([pending], [BookedItem("C", Day, amount: -12.51m)], Coverage, Options);
        var otherCounterparty = TransactionReconciler.Plan([pending], [BookedItem("D", Day, counterparty: "Example Bakery")], Coverage, Options);

        foreach (var plan in new[] { otherCurrency, otherAmount, otherCounterparty })
        {
            plan.Merges.Should().BeEmpty();
            plan.FlagAmbiguous.Should().BeEmpty();
            plan.Inserts.Should().ContainSingle();
        }
    }

    [Fact]
    public void Counterparty_comparison_ignores_case_and_spacing()
    {
        var pending = PendingState("er:A", Day, counterparty: "Example Grocer");
        var booked = BookedItem("B", Day, counterparty: "  example   GROCER ");

        var plan = TransactionReconciler.Plan([pending], [booked], Coverage, Options);

        plan.Merges.Should().ContainSingle();
    }

    [Fact]
    public void Pending_row_resolved_by_its_own_reference_is_never_a_merge_candidate()
    {
        var pending = PendingState("er:A", Day);
        var stillPending = BookedItem("A", Day, status: ProviderTransactionStatus.Pending);
        var booked = BookedItem("B", Day.AddDays(1));

        var plan = TransactionReconciler.Plan([pending], [stillPending, booked], Coverage, Options);

        plan.Merges.Should().BeEmpty();
        plan.FlagAmbiguous.Should().BeEmpty();
        plan.Inserts.Should().ContainSingle();
    }

    [Fact]
    public void Identical_booked_items_without_references_never_merge_with_each_other()
    {
        var first = BookedItem(null, Day);
        var second = BookedItem(null, Day);

        var plan = TransactionReconciler.Plan([], [first, second], Coverage, Options);

        plan.Merges.Should().BeEmpty();
        plan.Inserts.Should().HaveCount(2);
        plan.Inserts.Select(insert => insert.Ref).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void Unresolved_pending_row_inside_the_covered_range_is_dropped_after_a_complete_non_empty_fetch()
    {
        var pending = PendingState("er:A", Day);

        var plan = TransactionReconciler.Plan([pending], [], new FetchCoverage(Day, true, 1), Options);

        plan.Drops.Should().ContainSingle().Which.Should().Be(pending.Id);
    }

    [Fact]
    public void Pending_row_is_dropped_when_the_fetch_reached_back_without_a_start_date()
    {
        var pending = PendingState("er:A", Day);

        var plan = TransactionReconciler.Plan([pending], [], new FetchCoverage(null, true, 3), Options);

        plan.Drops.Should().ContainSingle().Which.Should().Be(pending.Id);
    }

    [Theory]
    [InlineData(false, 5, 0)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 5, 1)]
    public void Pending_row_is_never_dropped_on_a_partial_empty_or_later_starting_fetch(bool complete, int itemCount, int fromOffsetDays)
    {
        var pending = PendingState("er:A", Day);
        var coverage = new FetchCoverage(Day.AddDays(fromOffsetDays), complete, itemCount);

        var plan = TransactionReconciler.Plan([pending], [], coverage, Options);

        plan.Drops.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Europe/Amsterdam", 1)]
    [InlineData("UTC", 0)]
    public void The_day_a_dateless_pending_row_was_first_seen_is_worked_out_in_the_configured_zone(string zoneId, int expectedDrops)
    {
        var undated = PendingState("er:A", Day) with
        {
            TransactionDate = null,
            FirstSeenAt = new DateTimeOffset(2026, 9, 19, 22, 30, 0, TimeSpan.Zero)
        };
        var options = new ReconcilerOptions(5, TimeZoneInfo.FindSystemTimeZoneById(zoneId));

        var plan = TransactionReconciler.Plan([undated], [], new FetchCoverage(Day, true, 1), options);

        plan.Drops.Should().HaveCount(expectedDrops);
    }

    [Fact]
    public void Pending_row_present_in_the_feed_is_not_dropped()
    {
        var pending = PendingState("er:A", Day);
        var stillPending = BookedItem("A", Day, status: ProviderTransactionStatus.Pending);

        var plan = TransactionReconciler.Plan([pending], [stillPending], new FetchCoverage(null, true, 1), Options);

        plan.Drops.Should().BeEmpty();
    }

    [Fact]
    public void Merged_and_flagged_pending_rows_are_never_dropped()
    {
        var merged = PendingState("er:A", Day, amount: -5.00m);
        var firstDoubtful = PendingState("er:B1", Day, amount: -7.00m);
        var secondDoubtful = PendingState("er:B2", Day, amount: -7.00m);
        var alreadyFlagged = PendingState("er:C", Day, amount: -9.00m) with { Flag = MatchFlag.Ambiguous };
        var unrelated = PendingState("er:D", Day, amount: -11.00m);
        var incoming = new[]
        {
            BookedItem("N1", Day, amount: -5.00m),
            BookedItem("N2", Day, amount: -7.00m)
        };

        var plan = TransactionReconciler.Plan(
            [merged, firstDoubtful, secondDoubtful, alreadyFlagged, unrelated],
            incoming,
            new FetchCoverage(null, true, 2),
            Options);

        plan.Merges.Should().ContainSingle().Which.PendingTransactionId.Should().Be(merged.Id);
        plan.FlagAmbiguous.Should().BeEquivalentTo([firstDoubtful.Id, secondDoubtful.Id]);
        plan.Drops.Should().ContainSingle().Which.Should().Be(unrelated.Id);
    }

    [Fact]
    public void Cancelled_status_for_a_known_pending_reference_drops_the_row_once()
    {
        var pending = PendingState("er:A", Day);
        var cancelled = BookedItem("A", Day, status: ProviderTransactionStatus.Cancelled);

        var plan = TransactionReconciler.Plan([pending], [cancelled], new FetchCoverage(null, true, 1), Options);

        plan.Drops.Should().ContainSingle().Which.Should().Be(pending.Id);
        plan.Updates.Should().BeEmpty();
        plan.Inserts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProviderTransactionStatus.Cancelled)]
    [InlineData(ProviderTransactionStatus.Pending)]
    public void Cancelled_or_pending_observation_of_a_booked_row_changes_nothing(ProviderTransactionStatus status)
    {
        var booked = PendingState("er:A", Day) with { Status = LedgerTransactionStatus.Booked };
        var observation = BookedItem("A", Day, status: status);

        var plan = TransactionReconciler.Plan([booked], [observation], new FetchCoverage(null, true, 1), Options);

        plan.Drops.Should().BeEmpty();
        plan.Updates.Should().BeEmpty();
        plan.Inserts.Should().BeEmpty();
        plan.Merges.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProviderTransactionStatus.Pending, false)]
    [InlineData(ProviderTransactionStatus.Booked, true)]
    public void Dropped_row_whose_reference_is_seen_again_is_restored(ProviderTransactionStatus status, bool upgrade)
    {
        var dropped = PendingState("er:A", Day) with { Status = LedgerTransactionStatus.Dropped };
        var observation = BookedItem("A", Day, status: status);

        var plan = TransactionReconciler.Plan([dropped], [observation], new FetchCoverage(null, true, 1), Options);

        plan.Inserts.Should().BeEmpty();
        plan.Drops.Should().BeEmpty();
        plan.Updates.Should().ContainSingle()
            .Which.Should().Be(new PlannedUpdate(dropped.Id, observation, upgrade, true));
    }

    [Fact]
    public void Dropped_row_is_not_a_merge_candidate()
    {
        var dropped = PendingState("er:A", Day) with { Status = LedgerTransactionStatus.Dropped };
        var booked = BookedItem("B", Day);

        var plan = TransactionReconciler.Plan([dropped], [booked], new FetchCoverage(null, true, 1), Options);

        plan.Merges.Should().BeEmpty();
        plan.FlagAmbiguous.Should().BeEmpty();
        plan.Inserts.Should().ContainSingle();
    }

    [Fact]
    public void Flagged_pending_row_that_books_under_its_own_reference_is_upgraded()
    {
        var flagged = PendingState("er:A", Day) with { Flag = MatchFlag.Ambiguous };
        var booked = BookedItem("A", Day.AddDays(1));

        var plan = TransactionReconciler.Plan([flagged], [booked], new FetchCoverage(null, true, 1), Options);

        plan.Updates.Should().ContainSingle()
            .Which.Should().Be(new PlannedUpdate(flagged.Id, booked, true));
        plan.Drops.Should().BeEmpty();
        plan.FlagAmbiguous.Should().BeEmpty();
    }

    private static ProviderTransaction BookedItem(
        string? entryReference,
        DateOnly date,
        ProviderTransactionStatus status = ProviderTransactionStatus.Booked,
        decimal amount = -12.50m,
        string currency = "EUR",
        string? counterparty = "Example Grocer")
    {
        return new ProviderTransaction(
            entryReference,
            status,
            amount,
            currency,
            status == ProviderTransactionStatus.Booked ? date : null,
            status == ProviderTransactionStatus.Booked ? date : null,
            date,
            counterparty,
            counterparty is null ? null : "XX00SYNT0000000001",
            "Groceries",
            "{}");
    }

    private static LedgerTransactionState PendingState(
        string reference,
        DateOnly transactionDate,
        string? counterparty = "Example Grocer",
        decimal amount = -12.50m,
        string currency = "EUR")
    {
        return new LedgerTransactionState(
            Guid.CreateVersion7(),
            LedgerTransactionStatus.Pending,
            [reference],
            amount,
            currency,
            null,
            null,
            transactionDate,
            counterparty,
            "Groceries",
            MatchFlag.None,
            new DateTimeOffset(2026, 9, 20, 6, 30, 0, TimeSpan.Zero));
    }

    private static ProviderTransaction Item(
        string? entryReference,
        ProviderTransactionStatus status = ProviderTransactionStatus.Booked,
        decimal amount = -10.00m,
        string currency = "EUR",
        string? counterparty = "Example Grocer",
        string? description = "Groceries")
    {
        return new ProviderTransaction(
            entryReference,
            status,
            amount,
            currency,
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 29),
            counterparty,
            "XX00SYNT0000000001",
            description,
            "{}");
    }

    private static LedgerTransactionState State(LedgerTransactionStatus status, params string[] references)
    {
        return new LedgerTransactionState(
            Guid.CreateVersion7(),
            status,
            references,
            -10.00m,
            "EUR",
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 29),
            "Example Grocer",
            "Groceries",
            MatchFlag.None,
            new DateTimeOffset(2026, 9, 30, 6, 30, 0, TimeSpan.Zero));
    }
}
