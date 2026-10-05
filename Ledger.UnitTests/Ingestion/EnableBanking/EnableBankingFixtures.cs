using System.Security.Cryptography;
using System.Text.Json;
using Ledger.Domain.Banking;
using Ledger.Service.Ingestion.EnableBanking;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Ledger.UnitTests.Ingestion.EnableBanking;

/// <summary>
/// Synthetic aggregator responses shaped like the ones the bank sends: which fields are present, their types and their spelling.
/// Every value is invented; account numbers use the unassigned XX country code.
/// </summary>
public static class EnableBankingFixtures
{
    /// <summary>The redirect address registered in the fixtures.</summary>
    public const string RedirectUrl = "https://ledger-api.example.com/api/v1/bank/callback";

    /// <summary>A session-scoped account identifier.</summary>
    public const string AccountUid = "11111111-1111-1111-1111-111111111111";

    /// <summary>A session identifier.</summary>
    public const string SessionId = "22222222-2222-2222-2222-222222222222";

    /// <summary>The application as the aggregator reports it. The services array and the activity flag can be varied.</summary>
    public static string Application(string services = "[\"AIS\"]", bool active = true, string redirectUrl = RedirectUrl)
    {
        return $$"""
            {"kid":"00000000-0000-0000-0000-000000000001","environment":"PRODUCTION","active":{{(active ? "true" : "false")}},"services":{{services}},"redirect_urls":["{{redirectUrl}}"]}
            """;
    }

    /// <summary>The bank list for the country, with the bank's maximum consent validity and the PSU headers it requires.</summary>
    public static string Aspsps(int maximumValiditySeconds = 15552000, string requiredHeaders = "[\"psu-ip-address\"]")
    {
        return $$"""
            {"aspsps":[
              {"name":"Another Bank","country":"NL","maximum_consent_validity":86400,"required_psu_headers":[],"psu_types":["personal"]},
              {"name":"ING","country":"NL","maximum_consent_validity":{{maximumValiditySeconds}},"required_psu_headers":{{requiredHeaders}},"psu_types":["business","personal"]}
            ]}
            """;
    }

    /// <summary>The answer to starting an authorisation.</summary>
    public static string Authorization(string url = "https://auth.enablebanking.com/ais/start?id=synthetic")
    {
        return $$"""
            {"url":"{{url}}","authorization_id":"synthetic-authorization","psu_id_hash":"synthetic-hash"}
            """;
    }

    /// <summary>A session with one current account, one savings account, one card account and one account of another type.</summary>
    public static string Session()
    {
        return $$$"""
            {"session_id":"{{{SessionId}}}","aspsp":{"name":"ING","country":"NL"},"psu_type":"personal",
             "access":{"valid_until":"2027-03-29T10:15:30.123456+00:00"},
             "accounts":[
               {"uid":"{{{AccountUid}}}","identification_hash":"hash-current","account_id":{"iban":"XX00EXAM0000000001"},"name":"Example Current","product":"Example Current Product","currency":"EUR","cash_account_type":"CACC"},
               {"uid":"33333333-3333-3333-3333-333333333333","identification_hash":"hash-savings","account_id":{"iban":"XX00EXAM0000000002"},"name":"Example Savings","currency":"EUR","cash_account_type":"SVGS"},
               {"uid":"44444444-4444-4444-4444-444444444444","identification_hash":"hash-card","account_id":{"other":{"identification":"synthetic-card"}},"currency":"EUR","cash_account_type":"CARD"},
               {"identification_hash":"hash-other","currency":"EUR","cash_account_type":"OTHR"}
             ]}
            """;
    }

    /// <summary>A balances answer shaped like the one the bank sends: only the expected balance, with no reference date.</summary>
    public static string ExpectedBalanceOnly(string amount = "1234.56")
    {
        return $$"""
            {"balances":[{"name":"Expected","balance_amount":{"currency":"EUR","amount":"{{amount}}"},"balance_type":"XPCD"}]}
            """;
    }

    /// <summary>One transaction object. Direction, amount, status and the optional fields can be varied.</summary>
    public static string Transaction(
        string entryReference,
        string indicator = "DBIT",
        string amount = "12.50",
        string status = "BOOK",
        string bookingDate = "2026-09-30",
        string remittance = "[\"Example payment\"]")
    {
        var party = indicator == "DBIT"
            ? "\"creditor\":{\"name\":\"Example Utility\"},\"creditor_account\":{\"iban\":\"XX00EXAM0000000003\"}"
            : "\"debtor\":{\"name\":\"Example Employer\"},\"debtor_account\":{\"iban\":\"XX00EXAM0000000004\"}";

        return $$$"""
            {"entry_reference":"{{{entryReference}}}","status":"{{{status}}}","credit_debit_indicator":"{{{indicator}}}","transaction_amount":{"currency":"EUR","amount":"{{{amount}}}"},"booking_date":"{{{bookingDate}}}","value_date":"{{{bookingDate}}}",{{{party}}},"remittance_information":{{{remittance}}}}
            """;
    }

    /// <summary>A transactions page. A null continuation key ends the stream.</summary>
    public static string Page(string? continuationKey, params string[] transactions)
    {
        var key = continuationKey is null ? "null" : $"\"{continuationKey}\"";
        return $$$"""
            {"transactions":[{{{string.Join(",", transactions)}}}],"continuation_key":{{{key}}}}
            """;
    }

    /// <summary>The error body the aggregator sends: a message, a numeric code and the machine-readable error.</summary>
    public static string Error(string error, string message = "Something went wrong")
    {
        return $$"""
            {"message":"{{message}}","code":422,"error":"{{error}}"}
            """;
    }

    /// <summary>Parses a transaction fixture into the element the mapper takes.</summary>
    public static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// A client wired exactly like production, with the outbound guard in front of a recording handler, a throwaway key and a
/// controllable clock.
/// </summary>
public sealed class ClientHarness : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ledger-client-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http;

    /// <summary>Builds the harness. The options can be adjusted before the client is created.</summary>
    public ClientHarness(Action<EnableBankingOptions>? configure = null)
    {
        Directory.CreateDirectory(_directory);
        var keyPath = Path.Combine(_directory, "key.pem");

        using (var rsa = RSA.Create(2048))
        {
            File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        Options = new EnableBankingOptions { ApplicationId = "00000000-0000-0000-0000-000000000001", PrivateKeyPath = keyPath };
        configure?.Invoke(Options);

        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        Handler = new RecordingHandler();
        Minter = new EnableBankingTokenMinter(Microsoft.Extensions.Options.Options.Create(Options), Time);
        Cache = new AspspRequirementsCache();

        var guard = new AisOnlyGuardHandler { InnerHandler = Handler };
        _http = new HttpClient(guard) { BaseAddress = EnableBankingOptions.BaseAddress };

        Client = new EnableBankingClient(_http, Minter, Microsoft.Extensions.Options.Options.Create(Options), Time, Cache);
    }

    /// <summary>The options the client reads.</summary>
    public EnableBankingOptions Options { get; }

    /// <summary>The clock the client and the minter read.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>The scripted network behind the guard.</summary>
    public RecordingHandler Handler { get; }

    /// <summary>The token minter.</summary>
    public EnableBankingTokenMinter Minter { get; }

    /// <summary>The cache of what the bank requires.</summary>
    public AspspRequirementsCache Cache { get; }

    /// <summary>The client under test.</summary>
    public EnableBankingClient Client { get; }

    /// <summary>The account reference every fixture uses.</summary>
    public static ProviderAccountRef Account { get; } = new(EnableBankingFixtures.SessionId, EnableBankingFixtures.AccountUid);

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        Minter.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>A call meter that records the order of its calls into a shared log.</summary>
public sealed class LoggingMeter(List<string> log) : IProviderCallMeter
{
    /// <inheritdoc />
    public ValueTask BeforeCallAsync(ProviderCallKind kind, CancellationToken cancellationToken)
    {
        log.Add("meter " + kind);
        return ValueTask.CompletedTask;
    }
}
