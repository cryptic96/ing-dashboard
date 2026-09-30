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
    private static readonly ReconcilerOptions Options = new();

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
