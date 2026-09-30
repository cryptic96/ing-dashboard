using System.Text.Json;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.Domain.Security;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Ingestion;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>Helpers that link a synthetic consent, run syncs and read results back through real database roles.</summary>
public static class IngestionTestSupport
{
    /// <summary>
    /// Boots the host against the shared database with the synthetic provider registered as the only provider and a call
    /// budget large enough that reconciliation tests, which sync one account many times, are never limited by it.
    /// </summary>
    public static LedgerWebApplicationFactory CreateFactory(DatabaseFixture fixture, SyntheticBankScenario scenario)
    {
        return new LedgerWebApplicationFactory(
            fixture.ConnectionStringFor("ledger_runtime"),
            configureTestServices: services =>
                services.AddSingleton<IBankDataProvider>(new SyntheticBankDataProvider(scenario)),
            additionalConfiguration: new Dictionary<string, string?> { ["Ingestion:BackgroundCallsPerDay"] = "1000" });
    }

    /// <summary>
    /// Completes the scenario's consent, stores the connection with its protected session id and saves the account selection:
    /// every account, or only the first one when requested.
    /// </summary>
    public static async Task<LinkedConnection> LinkSyntheticAsync(
        LedgerWebApplicationFactory factory,
        SyntheticBankScenario scenario,
        bool selectFirstAccountOnly)
    {
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IBankDataProvider>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IBankConnectionStore>();

        var session = await provider.CompleteAuthorizationAsync(scenario.AuthorizationCode, CancellationToken.None);
        var connection = await store.AddConnectionAsync(
            provider.Name,
            "Synthetic Bank",
            "XX",
            session,
            protector.Protect(session.SessionId),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var selections = connection.Accounts
            .Select((account, index) => new AccountSelection(
                account.AccountKey,
                $"Synthetic account {index + 1}",
                !selectFirstAccountOnly || index == 0))
            .ToList();

        await store.SetAccountSelectionAsync(connection.Id, selections, CancellationToken.None);

        var linkedAccounts = connection.Accounts
            .Select((account, index) => account with
            {
                DisplayName = selections[index].DisplayName,
                SyncEnabled = selections[index].SyncEnabled
            })
            .ToList();

        return connection with { Accounts = linkedAccounts };
    }

    /// <summary>Runs one sync of the connection through the orchestrator in its own scope.</summary>
    public static async Task<SyncRunResult> SyncAsync(
        LedgerWebApplicationFactory factory,
        Guid connectionId,
        SyncTrigger trigger = SyncTrigger.Manual)
    {
        using var scope = factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<SyncOrchestrator>();

        return await orchestrator.SyncConnectionAsync(connectionId, trigger, FetchContext.Background, CancellationToken.None);
    }

    /// <summary>Builds a booked synthetic transaction with a valid payload.</summary>
    public static ProviderTransaction Booked(
        string? entryReference,
        decimal amount,
        DateOnly bookingDate,
        string? counterparty = "Example Grocer",
        string? description = "Groceries",
        string payloadNote = "original")
    {
        return Synthetic(entryReference, ProviderTransactionStatus.Booked, amount, bookingDate, counterparty, description, payloadNote);
    }

    /// <summary>Builds a pending synthetic transaction with a valid payload.</summary>
    public static ProviderTransaction Pending(
        string? entryReference,
        decimal amount,
        DateOnly transactionDate,
        string? counterparty = "Example Grocer",
        string? description = "Groceries",
        string payloadNote = "original")
    {
        return Synthetic(entryReference, ProviderTransactionStatus.Pending, amount, transactionDate, counterparty, description, payloadNote);
    }

    /// <summary>Reads the reporting view for one account as the Grafana reader role, in the view's own order.</summary>
    public static async Task<IReadOnlyList<ReportingTransaction>> ReadReportingTransactionsAsync(
        DatabaseFixture fixture,
        string accountKey)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("grafana_reader"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT transaction_id, account_key, effective_date, booking_date, value_date, amount, currency,
                   counterparty_name, description, status, match_flag
            FROM reporting.transactions
            WHERE account_key = @accountKey
            """;
        command.Parameters.AddWithValue("accountKey", accountKey);

        var rows = new List<ReportingTransaction>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ReportingTransaction(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDecimal(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return rows;
    }

    /// <summary>Reads the stored transaction rows of one account exactly as the database holds them.</summary>
    public static async Task<IReadOnlyList<StoredTransaction>> ReadStoredTransactionsAsync(
        DatabaseFixture fixture,
        string accountKey)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.status, t.booking_date, t.value_date, t.transaction_date, t.amount, t.currency,
                   t.counterparty_name, t.counterparty_iban, t.description, t.first_seen_at
            FROM public.transactions t
            JOIN public.accounts a ON a.id = t.account_id
            WHERE a.account_key = @accountKey
            ORDER BY t.first_seen_at, t.id
            """;
        command.Parameters.AddWithValue("accountKey", accountKey);

        var rows = new List<StoredTransaction>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new StoredTransaction(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateOnly>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateOnly>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4),
                reader.GetDecimal(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10)));
        }

        return rows;
    }

    /// <summary>Counts the transaction, reference and payload rows of one account.</summary>
    public static async Task<RowCounts> ReadCountsAsync(DatabaseFixture fixture, string accountKey)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT count(*) FROM public.transactions t JOIN public.accounts a ON a.id = t.account_id WHERE a.account_key = @accountKey),
              (SELECT count(*) FROM public.transaction_refs r JOIN public.accounts a ON a.id = r.account_id WHERE a.account_key = @accountKey),
              (SELECT count(*) FROM public.transaction_payloads p
                 JOIN public.transactions t ON t.id = p.transaction_id
                 JOIN public.accounts a ON a.id = t.account_id WHERE a.account_key = @accountKey)
            """;
        command.Parameters.AddWithValue("accountKey", accountKey);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return new RowCounts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    /// <summary>Reads how a run ended and what it counted.</summary>
    public static async Task<StoredRun> ReadRunAsync(DatabaseFixture fixture, Guid runId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT outcome, provider_error, calls_made, inserted, updated, finished_at IS NOT NULL
            FROM public.sync_runs WHERE id = @runId
            """;
        command.Parameters.AddWithValue("runId", runId);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return new StoredRun(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetBoolean(5));
    }

    /// <summary>Returns how many rows violate the identity model: transactions without a reference, ambiguous references and duplicated accounts.</summary>
    public static async Task<long> CountIdentityViolationsAsync(DatabaseFixture fixture)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT count(*) FROM public.transactions t
                 WHERE NOT EXISTS (SELECT 1 FROM public.transaction_refs r WHERE r.transaction_id = t.id))
              + (SELECT count(*) FROM (SELECT account_id, ref FROM public.transaction_refs
                   GROUP BY account_id, ref HAVING count(DISTINCT transaction_id) <> 1) ambiguous)
              + (SELECT count(*) FROM (SELECT provider, identification_hash FROM public.accounts
                   GROUP BY provider, identification_hash HAVING count(*) <> 1) duplicated)
            """;

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static ProviderTransaction Synthetic(
        string? entryReference,
        ProviderTransactionStatus status,
        decimal amount,
        DateOnly date,
        string? counterparty,
        string? description,
        string payloadNote)
    {
        var rawJson = JsonSerializer.Serialize(new
        {
            reference = entryReference,
            status = status.ToString(),
            amount,
            date,
            counterparty,
            description,
            note = payloadNote
        });

        return new ProviderTransaction(
            entryReference,
            status,
            amount,
            "EUR",
            status == ProviderTransactionStatus.Booked ? date : null,
            status == ProviderTransactionStatus.Booked ? date : null,
            date,
            counterparty,
            counterparty is null ? null : "XX00SYNT0000000042",
            description,
            rawJson);
    }
}

/// <summary>A row of the reporting transactions view.</summary>
public record ReportingTransaction(
    Guid TransactionId,
    string AccountKey,
    string EffectiveDate,
    string? BookingDate,
    string? ValueDate,
    decimal Amount,
    string Currency,
    string? CounterpartyName,
    string? Description,
    string Status,
    string? MatchFlag);

/// <summary>A transaction row exactly as stored.</summary>
public record StoredTransaction(
    Guid Id,
    string Status,
    DateOnly? BookingDate,
    DateOnly? ValueDate,
    DateOnly? TransactionDate,
    decimal Amount,
    string Currency,
    string? CounterpartyName,
    string? CounterpartyIban,
    string? Description,
    DateTimeOffset FirstSeenAt);

/// <summary>Row counts of one account.</summary>
public record RowCounts(long Transactions, long Refs, long Payloads);

/// <summary>A sync run as stored.</summary>
public record StoredRun(string? Outcome, string? ProviderError, int CallsMade, int Inserted, int Updated, bool Finished);
