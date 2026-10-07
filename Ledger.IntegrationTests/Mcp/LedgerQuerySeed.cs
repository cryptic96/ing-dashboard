using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Repository;
using Ledger.Repository.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledger.IntegrationTests.Mcp;

/// <summary>
/// Inserts a synthetic ledger through the runtime role: one bank connection with accounts, booked and pending transactions and
/// balance snapshots. Every name, account number and amount is made up.
/// </summary>
public static class LedgerQuerySeed
{
    /// <summary>The synthetic account number the seeded joint account carries; it must never appear in a tool result.</summary>
    public const string JointIban = "XX00SYNT0000000001";

    /// <summary>The synthetic account number the seeded savings account carries; it must never appear in a tool result.</summary>
    public const string SavingsIban = "XX00SYNT0000000002";

    /// <summary>The synthetic provider-side account name the seeded joint account carries; it must never appear in a tool result.</summary>
    public const string JointProviderName = "Synthetic Holder One and Two";

    /// <summary>Describes a seeded account so tests can compare results with it.</summary>
    public sealed record SeededAccount(string AccountKey, string DisplayName, string Iban);

    /// <summary>Opens a database context on the given connection string, for the runtime role.</summary>
    public static LedgerDbContext OpenContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options;

        return new LedgerDbContext(options);
    }

    /// <summary>
    /// Seeds a joint and a savings account that are synced, with booked and pending transactions, a balance snapshot each, and a
    /// third account that is not synced and must never be shown.
    /// </summary>
    public static async Task<IReadOnlyList<SeededAccount>> SeedStandardLedgerAsync(string connectionString)
    {
        await using var context = OpenContext(connectionString);
        var connection = await AddConnectionAsync(context);

        var joint = AddAccount(context, connection, "Joint", JointIban, AccountKind.Current, true, At(2026, 1, 1), JointProviderName);
        var savings = AddAccount(context, connection, "Savings", SavingsIban, AccountKind.Savings, true, At(2026, 1, 2), null);
        var hidden = AddAccount(context, connection, "Not synced", "XX00SYNT0000000003", AccountKind.Current, false, At(2026, 1, 3), null);

        var run = new SyncRunEntity
        {
            Id = Guid.NewGuid(),
            BankConnectionId = connection.Id,
            Trigger = SyncTrigger.Scheduled,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-9),
            Outcome = SyncOutcome.Succeeded
        };
        context.SyncRuns.Add(run);

        context.Transactions.AddRange(
            Booked(joint, new DateOnly(2026, 2, 3), -42.50m, "Example Grocer"),
            Booked(joint, new DateOnly(2026, 3, 15), -10m, "Example Shop"),
            Booked(joint, new DateOnly(2026, 9, 30), 2100m, "Example Employer"),
            Pending(joint, -5m, "Example Cafe"),
            Booked(savings, new DateOnly(2026, 6, 1), 100m, "Joint"),
            Booked(hidden, new DateOnly(2026, 1, 1), -1m, "Hidden"));

        context.BalanceSnapshots.AddRange(
            Snapshot(joint, run, new DateOnly(2026, 10, 6), 1234.5m, true),
            Snapshot(savings, run, new DateOnly(2026, 10, 6), 5000m, null));

        await context.SaveChangesAsync();

        return
        [
            new SeededAccount(joint.AccountKey, "Joint", JointIban),
            new SeededAccount(savings.AccountKey, "Savings", SavingsIban)
        ];
    }

    /// <summary>The instant the seeded totals scenario's successful sync finished.</summary>
    public static readonly DateTimeOffset TotalsSyncFinishedAt = new(2026, 10, 6, 7, 30, 0, TimeSpan.Zero);

    /// <summary>The account number the seeded totals scenario's unsynced own account carries.</summary>
    public const string OtherOwnIban = "XX00SYNT0000000004";

    /// <summary>
    /// Seeds the scenario the totals tests read: a joint and a savings account that are synced (the savings number is written in
    /// lower case with spaces), an own account that is not synced, and August 2026 transactions covering booked and pending rows
    /// on both sides of an Amsterdam midnight, a dropped row, a transfer between the synced accounts, a transfer to the unsynced
    /// own account, two identical rows, spellings that differ in case and spacing, and a row in another currency. All names,
    /// account numbers and amounts are made up.
    /// </summary>
    public static async Task<TotalsScenario> SeedTotalsScenarioAsync(string connectionString)
    {
        await using var context = OpenContext(connectionString);
        var connection = await AddConnectionAsync(context);

        var joint = AddAccount(context, connection, "Joint", JointIban, AccountKind.Current, true, At(2026, 1, 1), JointProviderName);
        var savings = AddAccount(context, connection, "Savings", "xx00 synt 0000 0000 02", AccountKind.Savings, true, At(2026, 1, 2), null);
        AddAccount(context, connection, "Other own", OtherOwnIban, AccountKind.Current, false, At(2026, 1, 3), null);

        context.SyncRuns.Add(new SyncRunEntity
        {
            Id = Guid.NewGuid(),
            BankConnectionId = connection.Id,
            Trigger = SyncTrigger.Scheduled,
            StartedAt = TotalsSyncFinishedAt.AddMinutes(-1),
            FinishedAt = TotalsSyncFinishedAt,
            Outcome = SyncOutcome.Succeeded
        });

        const string External = "XX00SYNT9999999999";
        var seen = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

        context.Transactions.AddRange(
            Row(joint, LedgerTransactionStatus.Booked, "2026-08-03", -40.00m, "EUR", "Example Market", External, "Weekly groceries", seen),
            Row(joint, LedgerTransactionStatus.Booked, "2026-08-10", -25.50m, "EUR", "EXAMPLE  market ", External, "Weekly groceries and bags", seen),
            Row(joint, LedgerTransactionStatus.Booked, "2026-08-17", -30.00m, "EUR", "Example Bakery", External, "Bread", seen),
            Row(joint, LedgerTransactionStatus.Booked, "2026-08-21", -5.00m, "EUR", "Example Shop", External, "Small purchase", seen),
            Row(joint, LedgerTransactionStatus.Booked, "2026-08-21", -5.00m, "EUR", "Example Shop", External, "Small purchase", seen),
            Row(joint, LedgerTransactionStatus.Booked, "2026-08-31", -8.00m, "EUR", "Example Shop", External, "Last day", seen),
            Row(joint, LedgerTransactionStatus.Booked, "2026-09-01", -9.00m, "EUR", "Example Shop", External, "First day of next month", seen),
            Row(joint, LedgerTransactionStatus.Booked, "2026-08-25", 2000.00m, "EUR", "Example Employer", External, "Salary", seen),
            Row(joint, LedgerTransactionStatus.Booked, "2026-08-12", -10.00m, "USD", "Example Market", External, "Travel purchase", seen),
            Row(joint, LedgerTransactionStatus.Booked, "2026-08-05", -300.00m, "EUR", "Savings", "XX00SYNT0000000002", "Move to savings", seen),
            Row(savings, LedgerTransactionStatus.Booked, "2026-08-05", 300.00m, "EUR", "Joint", "XX00 SYNT 0000 0000 01", "From joint", seen),
            Row(joint, LedgerTransactionStatus.Booked, "2026-08-06", -150.00m, "EUR", "Other own", OtherOwnIban, "Move to other own account", seen),
            Row(joint, LedgerTransactionStatus.Dropped, "2026-08-07", -99.00m, "EUR", "Example Market", External, "Never settled", seen),
            Row(joint, LedgerTransactionStatus.Booked, null, -3.00m, "EUR", "Example Cafe", External, "No bank date", new DateTimeOffset(2026, 8, 31, 21, 30, 0, TimeSpan.Zero)),
            Row(joint, LedgerTransactionStatus.Booked, null, -2.00m, "EUR", "Example Cafe", External, "Transaction date only", seen, transactionDate: "2026-08-30"),
            Row(joint, LedgerTransactionStatus.Pending, null, -12.50m, "EUR", "Example Bakery", External, "Pending before midnight", new DateTimeOffset(2026, 8, 31, 21, 30, 0, TimeSpan.Zero)),
            Row(joint, LedgerTransactionStatus.Pending, null, -7.00m, "EUR", "Example Bakery", External, "Pending after midnight", new DateTimeOffset(2026, 8, 31, 22, 30, 0, TimeSpan.Zero)));

        await context.SaveChangesAsync();

        return new TotalsScenario(joint.AccountKey, savings.AccountKey);
    }

    /// <summary>The opaque keys of the two synced accounts of the totals scenario.</summary>
    public sealed record TotalsScenario(string JointKey, string SavingsKey);

    private static LedgerTransactionEntity Row(
        LedgerAccountEntity account,
        LedgerTransactionStatus status,
        string? bookingDate,
        decimal amount,
        string currency,
        string counterparty,
        string counterpartyIban,
        string description,
        DateTimeOffset firstSeenAt,
        string? transactionDate = null)
    {
        var booking = bookingDate is null ? (DateOnly?)null : DateOnly.Parse(bookingDate, System.Globalization.CultureInfo.InvariantCulture);

        return new LedgerTransactionEntity
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Status = status,
            BookingDate = booking,
            ValueDate = booking,
            TransactionDate = transactionDate is null ? null : DateOnly.Parse(transactionDate, System.Globalization.CultureInfo.InvariantCulture),
            Amount = amount,
            Currency = currency,
            CounterpartyName = counterparty,
            CounterpartyIban = counterpartyIban,
            Description = description,
            FirstSeenAt = firstSeenAt,
            UpdatedAt = firstSeenAt,
            BookedAt = status == LedgerTransactionStatus.Booked ? firstSeenAt : null,
            DroppedAt = status == LedgerTransactionStatus.Dropped ? firstSeenAt : null
        };
    }

    /// <summary>Seeds synced accounts with the given display names, created in the order given, one hour apart.</summary>
    public static async Task<IReadOnlyList<SeededAccount>> SeedAccountsInOrderAsync(string connectionString, params string[] displayNames)
    {
        await using var context = OpenContext(connectionString);
        var connection = await AddConnectionAsync(context);
        var accounts = new List<SeededAccount>();
        var created = At(2026, 2, 1);

        for (var index = 0; index < displayNames.Length; index++)
        {
            var iban = $"XX00SYNT00000001{index:00}";
            var account = AddAccount(context, connection, displayNames[index], iban, AccountKind.Current, true, created.AddHours(index), null);
            accounts.Add(new SeededAccount(account.AccountKey, displayNames[index], iban));
        }

        await context.SaveChangesAsync();

        return accounts;
    }

    /// <summary>Creates an isolated, migrated database that is dropped with the collection, and returns its runtime connection string.</summary>
    public static async Task<string> CreateIsolatedDatabaseAsync(DatabaseFixture fixture)
    {
        var databaseName = await fixture.CreateBootstrappedDatabaseAsync();
        await fixture.MigrateAsync(databaseName);

        return fixture.ConnectionStringForDatabase(databaseName, "ledger_runtime");
    }

    private static DateTimeOffset At(int year, int month, int day) => new(year, month, day, 12, 0, 0, TimeSpan.Zero);

    private static async Task<BankConnectionEntity> AddConnectionAsync(LedgerDbContext context)
    {
        var connection = new BankConnectionEntity
        {
            Id = Guid.NewGuid(),
            ConnectionKey = RandomKey(),
            Provider = "synthetic",
            AspspName = "Synthetic Bank",
            AspspCountry = "XX",
            Status = BankConnectionEntity.Statuses.Active,
            SessionIdProtected = "protected-synthetic-session",
            AuthorizedAt = DateTimeOffset.UtcNow.AddDays(-5),
            ValidUntil = DateTimeOffset.UtcNow.AddDays(60),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-5)
        };

        context.BankConnections.Add(connection);
        await context.SaveChangesAsync();

        return connection;
    }

    private static LedgerAccountEntity AddAccount(
        LedgerDbContext context,
        BankConnectionEntity connection,
        string displayName,
        string iban,
        AccountKind kind,
        bool syncEnabled,
        DateTimeOffset createdAt,
        string? providerName)
    {
        var account = new LedgerAccountEntity
        {
            Id = Guid.NewGuid(),
            AccountKey = RandomKey(),
            BankConnectionId = connection.Id,
            Provider = "synthetic",
            IdentificationHash = Guid.NewGuid().ToString("N"),
            Iban = iban,
            ProviderName = providerName,
            DisplayName = displayName,
            Kind = kind,
            Currency = "EUR",
            SyncEnabled = syncEnabled,
            CreatedAt = createdAt
        };

        context.Accounts.Add(account);

        return account;
    }

    private static LedgerTransactionEntity Booked(LedgerAccountEntity account, DateOnly bookingDate, decimal amount, string counterparty)
    {
        return new LedgerTransactionEntity
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Status = LedgerTransactionStatus.Booked,
            BookingDate = bookingDate,
            ValueDate = bookingDate,
            Amount = amount,
            Currency = "EUR",
            CounterpartyName = counterparty,
            CounterpartyIban = "XX00SYNT9999999999",
            Description = "Synthetic booking",
            FirstSeenAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            BookedAt = DateTimeOffset.UtcNow
        };
    }

    private static LedgerTransactionEntity Pending(LedgerAccountEntity account, decimal amount, string counterparty)
    {
        return new LedgerTransactionEntity
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Status = LedgerTransactionStatus.Pending,
            TransactionDate = new DateOnly(2026, 10, 6),
            Amount = amount,
            Currency = "EUR",
            CounterpartyName = counterparty,
            Description = "Synthetic pending",
            FirstSeenAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static BalanceSnapshotEntity Snapshot(
        LedgerAccountEntity account,
        SyncRunEntity run,
        DateOnly date,
        decimal amount,
        bool? reconciled)
    {
        return new BalanceSnapshotEntity
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            SnapshotDate = date,
            Kind = BalanceKind.ClosingBooked,
            ProviderType = "synthetic",
            Amount = amount,
            Currency = "EUR",
            ReferenceDate = date,
            ExpectedAmount = reconciled is null ? null : amount,
            DriftAmount = reconciled is null ? null : 0m,
            Reconciled = reconciled,
            SyncRunId = run.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static string RandomKey() => Guid.NewGuid().ToString("N")[..12];
}
