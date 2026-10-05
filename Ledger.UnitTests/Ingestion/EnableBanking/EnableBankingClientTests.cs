using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Service.Ingestion.EnableBanking;

namespace Ledger.UnitTests.Ingestion.EnableBanking;

/// <summary>Verifies the aggregator client against scripted, synthetic responses.</summary>
[Trait("Category", "EnableBanking")]
public class EnableBankingClientTests
{
    private const string TransactionsPath = $"/accounts/{EnableBankingFixtures.AccountUid}/transactions";
    private const string BalancesPath = $"/accounts/{EnableBankingFixtures.AccountUid}/balances";

    private static readonly AuthorizationRequest Request = new("state-value", new Uri(EnableBankingFixtures.RedirectUrl));
    private static readonly FetchContext Attended = new(new PsuContext("192.0.2.10", "Example Agent"));

    private static void ScriptStart(ClientHarness harness, string? application = null, string? aspsps = null, string? authorization = null)
    {
        harness.Handler
            .Respond("GET", "/application", HttpStatusCode.OK, application ?? EnableBankingFixtures.Application())
            .Respond("GET", "/aspsps", HttpStatusCode.OK, aspsps ?? EnableBankingFixtures.Aspsps())
            .Respond("POST", "/auth", HttpStatusCode.OK, authorization ?? EnableBankingFixtures.Authorization());
    }

    private static async Task<List<ProviderTransactionPage>> FetchAsync(ClientHarness harness, TransactionQuery query, FetchContext? context = null)
    {
        var pages = new List<ProviderTransactionPage>();

        await foreach (var page in harness.Client.GetTransactionsAsync(ClientHarness.Account, query, context ?? FetchContext.Background, CancellationToken.None))
        {
            pages.Add(page);
        }

        return pages;
    }

    [Fact]
    public async Task Starting_a_consent_posts_only_the_expected_keys_for_a_personal_consent_in_the_configured_bank()
    {
        using var harness = new ClientHarness();
        ScriptStart(harness);

        var start = await harness.Client.StartAuthorizationAsync(Request, CancellationToken.None);

        start.AuthorizationUrl.Host.Should().Be("auth.enablebanking.com");
        start.ProviderAuthorizationId.Should().Be("synthetic-authorization");

        var post = harness.Handler.Requests.Single(request => request.Method == "POST");
        post.Headers["Authorization"].Should().StartWith("Bearer ");
        using var body = JsonDocument.Parse(post.Body);
        body.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("access", "aspsp", "state", "redirect_url", "psu_type");
        body.RootElement.GetProperty("access").EnumerateObject().Select(property => property.Name).Should().Equal("valid_until");
        body.RootElement.GetProperty("psu_type").GetString().Should().Be("personal");
        body.RootElement.GetProperty("state").GetString().Should().Be("state-value");
        body.RootElement.GetProperty("redirect_url").GetString().Should().Be(EnableBankingFixtures.RedirectUrl);
        body.RootElement.GetProperty("aspsp").GetProperty("name").GetString().Should().Be("ING");
        body.RootElement.GetProperty("aspsp").GetProperty("country").GetString().Should().Be("NL");
    }

    [Theory]
    [InlineData(180, 7776000, 90)]
    [InlineData(30, 15552000, 30)]
    [InlineData(180, 15552000, 180)]
    public async Task The_consent_lasts_the_smaller_of_the_configured_days_and_the_banks_maximum(int configuredDays, int maximumSeconds, int expectedDays)
    {
        using var harness = new ClientHarness(options => options.ConsentValidityDays = configuredDays);
        ScriptStart(harness, aspsps: EnableBankingFixtures.Aspsps(maximumSeconds));

        await harness.Client.StartAuthorizationAsync(Request, CancellationToken.None);

        using var body = JsonDocument.Parse(harness.Handler.Requests.Single(request => request.Method == "POST").Body);
        var validUntil = DateTimeOffset.Parse(
            body.RootElement.GetProperty("access").GetProperty("valid_until").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        validUntil.Should().Be(harness.Time.GetUtcNow().AddDays(expectedDays));
    }

    [Theory]
    [InlineData("[\"AIS\",\"PIS\"]", true, EnableBankingFixtures.RedirectUrl, "payment_service_enabled")]
    [InlineData("[\"PIS\"]", true, EnableBankingFixtures.RedirectUrl, "account_information_disabled")]
    [InlineData("[]", true, EnableBankingFixtures.RedirectUrl, "account_information_disabled")]
    [InlineData("[\"AIS\"]", false, EnableBankingFixtures.RedirectUrl, "application_inactive")]
    [InlineData("[\"AIS\"]", true, "https://other.example.com/callback", "redirect_not_registered")]
    public async Task Starting_a_consent_is_refused_when_the_application_is_not_a_read_only_registered_one(string services, bool active, string registeredRedirect, string expectedCode)
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("GET", "/application", HttpStatusCode.OK, EnableBankingFixtures.Application(services, active, registeredRedirect));

        var act = () => harness.Client.StartAuthorizationAsync(Request, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<BankProviderException>()).Which;
        thrown.Kind.Should().Be(ProviderErrorKind.ProviderAuth);
        thrown.ProviderCode.Should().Be(expectedCode);
        harness.Handler.Requests.Should().ContainSingle().Which.Path.Should().Be("/application");
    }

    [Theory]
    [InlineData("http://auth.enablebanking.com/ais/start")]
    [InlineData("https://auth.example.org/ais/start")]
    [InlineData("https://enablebanking.com.example.org/ais/start")]
    [InlineData("https://notenablebanking.com/ais/start")]
    [InlineData("not a url")]
    public async Task Starting_a_consent_is_refused_when_the_authorisation_address_is_not_https_on_an_allowed_host(string url)
    {
        using var harness = new ClientHarness();
        ScriptStart(harness, authorization: EnableBankingFixtures.Authorization(url));

        var act = () => harness.Client.StartAuthorizationAsync(Request, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<BankProviderException>()).Which;
        thrown.Kind.Should().Be(ProviderErrorKind.ProviderAuth);
        thrown.Message.Should().NotContain(url);
    }

    [Theory]
    [InlineData("https://auth.enablebanking.com/ais/start")]
    [InlineData("https://tilisy.enablebanking.com/ais/start")]
    [InlineData("https://tilisy-sandbox.enablebanking.com/ais/start")]
    [InlineData("https://enablebanking.com/ais/start")]
    public async Task The_aggregators_own_authorisation_hosts_are_accepted(string url)
    {
        using var harness = new ClientHarness();
        ScriptStart(harness, authorization: EnableBankingFixtures.Authorization(url));

        var start = await harness.Client.StartAuthorizationAsync(Request, CancellationToken.None);

        start.AuthorizationUrl.AbsoluteUri.Should().Be(url);
    }

    [Fact]
    public async Task Completing_a_consent_maps_the_session_and_every_account_kind()
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("POST", "/sessions", HttpStatusCode.OK, EnableBankingFixtures.Session());

        var session = await harness.Client.CompleteAuthorizationAsync("code-value", CancellationToken.None);

        session.SessionId.Should().Be(EnableBankingFixtures.SessionId);
        session.ValidUntil.Should().Be(new DateTimeOffset(2027, 3, 29, 10, 15, 30, TimeSpan.Zero).AddTicks(1234560));
        session.Accounts.Select(account => account.Kind).Should().Equal(AccountKind.Current, AccountKind.Savings, AccountKind.Card, AccountKind.Other);
        session.Accounts[0].Should().Be(new ProviderAccount(
            EnableBankingFixtures.AccountUid,
            "hash-current",
            "XX00EXAM0000000001",
            "Example Current",
            "Example Current Product",
            AccountKind.Current,
            "EUR"));
        session.Accounts[2].Iban.Should().BeNull();
        session.Accounts[3].Uid.Should().BeNull();

        using var body = JsonDocument.Parse(harness.Handler.Requests.Single().Body);
        body.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal("code");
    }

    [Fact]
    public async Task A_rejected_code_fails_as_a_rejected_consent_without_echoing_the_code_or_the_body()
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("POST", "/sessions", HttpStatusCode.UnprocessableEntity, EnableBankingFixtures.Error("WRONG_AUTHORIZATION_CODE", "the code SENTINEL-CODE-VALUE was wrong"));

        var act = () => harness.Client.CompleteAuthorizationAsync("SENTINEL-CODE-VALUE", CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<BankProviderException>()).Which;
        thrown.Kind.Should().Be(ProviderErrorKind.ConsentRejected);
        thrown.Message.Should().NotContain("SENTINEL");
        thrown.ProviderCode.Should().Be("WRONG_AUTHORIZATION_CODE");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, null, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.OK, "ASPSP_RATE_LIMIT_EXCEEDED", ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, "EXPIRED_SESSION", ProviderErrorKind.ConsentRejected)]
    [InlineData(HttpStatusCode.Unauthorized, "CLOSED_SESSION", ProviderErrorKind.ConsentRejected)]
    [InlineData(HttpStatusCode.Unauthorized, "REVOKED_SESSION", ProviderErrorKind.ConsentRejected)]
    [InlineData(HttpStatusCode.NotFound, "SESSION_DOES_NOT_EXIST", ProviderErrorKind.ConsentRejected)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "WRONG_SESSION_STATUS", ProviderErrorKind.ConsentRejected)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "NO_ACCOUNTS_ADDED", ProviderErrorKind.ConsentRejected)]
    [InlineData(HttpStatusCode.Unauthorized, "UNAUTHORIZED_ACCESS", ProviderErrorKind.ProviderAuth)]
    [InlineData(HttpStatusCode.Forbidden, "UNAUTHORIZED_IP", ProviderErrorKind.ProviderAuth)]
    [InlineData(HttpStatusCode.Forbidden, "ACCESS_DENIED", ProviderErrorKind.ProviderAuth)]
    [InlineData(HttpStatusCode.Unauthorized, "AUTHORIZATION_NOT_PROVIDED", ProviderErrorKind.ProviderAuth)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "REDIRECT_URI_NOT_ALLOWED", ProviderErrorKind.ProviderAuth)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "PSU_HEADER_NOT_PROVIDED", ProviderErrorKind.ProviderAuth)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "PSU_HEADER_INVALID", ProviderErrorKind.ProviderAuth)]
    [InlineData(HttpStatusCode.InternalServerError, null, ProviderErrorKind.Transient)]
    [InlineData(HttpStatusCode.BadGateway, null, ProviderErrorKind.Transient)]
    [InlineData(HttpStatusCode.RequestTimeout, null, ProviderErrorKind.Transient)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "ASPSP_ERROR", ProviderErrorKind.Transient)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "ASPSP_TIMEOUT", ProviderErrorKind.Transient)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "WRONG_CONTINUATION_KEY", ProviderErrorKind.Transient)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "WRONG_TRANSACTIONS_PERIOD", ProviderErrorKind.Transient)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "SOMETHING_NEW_AND_UNKNOWN", ProviderErrorKind.Transient)]
    public void Failures_are_classified_for_the_scheduler_by_error_code_first_and_status_second(HttpStatusCode status, string? code, ProviderErrorKind expected)
    {
        EnableBankingErrors.Map(status, code).Should().Be(expected);
    }

    [Fact]
    public async Task A_response_body_never_reaches_the_exception_and_an_unexpected_error_code_is_dropped()
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("GET", BalancesPath, HttpStatusCode.InternalServerError, EnableBankingFixtures.Error("not a code SENTINEL-BODY", "SENTINEL-MESSAGE"));
        harness.Handler.Respond("GET", BalancesPath, HttpStatusCode.UnprocessableEntity, EnableBankingFixtures.Error("ASPSP_ERROR", "SENTINEL-MESSAGE"));

        var first = () => harness.Client.GetBalancesAsync(ClientHarness.Account, FetchContext.Background, CancellationToken.None);
        var dropped = (await first.Should().ThrowAsync<BankProviderException>()).Which;
        dropped.ProviderCode.Should().Be("http_500");
        dropped.Message.Should().NotContain("SENTINEL");

        var second = () => harness.Client.GetBalancesAsync(ClientHarness.Account, FetchContext.Background, CancellationToken.None);
        var kept = (await second.Should().ThrowAsync<BankProviderException>()).Which;
        kept.ProviderCode.Should().Be("ASPSP_ERROR");
        kept.Kind.Should().Be(ProviderErrorKind.Transient);
        kept.Message.Should().NotContain("SENTINEL");
    }

    [Fact]
    public async Task A_timeout_and_a_lost_connection_are_temporary_failures_without_the_request_address()
    {
        using var harness = new ClientHarness();
        harness.Handler.Throw("GET", BalancesPath, new TaskCanceledException("timed out", new TimeoutException()));
        harness.Handler.Throw("GET", BalancesPath, new HttpRequestException("failed for https://api.enablebanking.com/accounts/SENTINEL-ACCOUNT"));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var act = () => harness.Client.GetBalancesAsync(ClientHarness.Account, FetchContext.Background, CancellationToken.None);

            var thrown = (await act.Should().ThrowAsync<BankProviderException>()).Which;
            thrown.Kind.Should().Be(ProviderErrorKind.Transient);
            thrown.Message.Should().NotContain("SENTINEL").And.NotContain("enablebanking");
        }
    }

    [Fact]
    public async Task A_cancelled_request_is_not_reported_as_a_provider_failure()
    {
        using var harness = new ClientHarness();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        harness.Handler.Throw("GET", BalancesPath, new OperationCanceledException(cancellation.Token));

        var act = () => harness.Client.GetBalancesAsync(ClientHarness.Account, FetchContext.Background, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_failed_request_is_sent_once_with_no_automatic_retry()
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("GET", BalancesPath, HttpStatusCode.InternalServerError, "{}");
        harness.Handler.Respond("GET", BalancesPath, HttpStatusCode.OK, EnableBankingFixtures.ExpectedBalanceOnly());

        var act = () => harness.Client.GetBalancesAsync(ClientHarness.Account, FetchContext.Background, CancellationToken.None);

        await act.Should().ThrowAsync<BankProviderException>();
        harness.Handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Balances_without_a_reference_date_keep_a_null_date_and_the_expected_balance_is_mapped()
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("GET", BalancesPath, HttpStatusCode.OK, EnableBankingFixtures.ExpectedBalanceOnly("-5.25"));

        var balances = await harness.Client.GetBalancesAsync(ClientHarness.Account, FetchContext.Background, CancellationToken.None);

        balances.Should().ContainSingle().Which.Should().Be(new ProviderBalance(BalanceKind.Expected, "XPCD", -5.25m, "EUR", null));
    }

    [Fact]
    public void Balance_types_map_to_their_kinds_and_a_reference_date_is_kept()
    {
        var closing = EnableBankingFixtures.ParseElement("""{"balance_amount":{"currency":"EUR","amount":"10.00"},"balance_type":"CLBD","reference_date":"2026-09-30"}""");
        var other = EnableBankingFixtures.ParseElement("""{"balance_amount":{"currency":"EUR","amount":"10.00"},"balance_type":"VALU"}""");

        EnableBankingJson.MapBalance(closing).Should().Be(new ProviderBalance(BalanceKind.ClosingBooked, "CLBD", 10.00m, "EUR", new DateOnly(2026, 9, 30)));
        EnableBankingJson.MapBalance(other).Kind.Should().Be(BalanceKind.Other);
    }

    [Theory]
    [InlineData("""{"balance_amount":{"currency":"EUR","amount":"--5.00"},"balance_type":"CLBD"}""")]
    [InlineData("""{"balance_amount":{"currency":"EUR","amount":"1e3"},"balance_type":"CLBD"}""")]
    [InlineData("""{"balance_amount":{"currency":"EUR"},"balance_type":"CLBD"}""")]
    [InlineData("""{"balance_amount":{"currency":"EUR","amount":5},"balance_type":"CLBD"}""")]
    [InlineData("""{"balance_amount":{"currency":"EUR","amount":"5.00"},"balance_type":"CLBD","reference_date":"30-09-2026"}""")]
    public void Malformed_balances_fail_as_malformed_data(string json)
    {
        var act = () => EnableBankingJson.MapBalance(EnableBankingFixtures.ParseElement(json));

        act.Should().Throw<BankProviderException>().Which.Kind.Should().Be(ProviderErrorKind.MalformedData);
    }

    [Fact]
    public async Task Every_balances_and_transactions_request_is_metered_immediately_before_it_is_sent()
    {
        using var harness = new ClientHarness();
        var log = new List<string>();
        harness.Handler.EventLog = log;
        var context = new FetchContext(null, new LoggingMeter(log));
        harness.Handler.Respond("GET", BalancesPath, HttpStatusCode.OK, EnableBankingFixtures.ExpectedBalanceOnly());
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page("k1", EnableBankingFixtures.Transaction("ref-1")));
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page(null));

        await FetchAsync(harness, new TransactionQuery(null, HistoryDepth.Longest), context);
        await harness.Client.GetBalancesAsync(ClientHarness.Account, context, CancellationToken.None);

        log.Should().Equal(
            "meter Transactions",
            "request GET " + TransactionsPath,
            "meter Transactions",
            "request GET " + TransactionsPath,
            "meter Balances",
            "request GET " + BalancesPath);
    }

    [Fact]
    public async Task An_empty_page_with_a_continuation_key_is_followed_and_every_other_parameter_stays_identical()
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page("k1"));
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page("k2"));
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page(null, EnableBankingFixtures.Transaction("ref-1")));

        var pages = await FetchAsync(harness, new TransactionQuery(null, HistoryDepth.Longest));

        pages.Select(page => page.Transactions.Count).Should().Equal(0, 0, 1);
        var requests = harness.Handler.Requests;
        requests.Should().HaveCount(3);
        requests.Select(request => request.Parameter("continuation_key")).Should().Equal(null, "k1", "k2");
        requests.Select(request => request.FixedQuery()).Distinct().Should().ContainSingle().Which.Should().Be("strategy=longest");
    }

    [Fact]
    public async Task A_lost_continuation_fails_the_fetch_as_temporary_instead_of_restarting_inside_the_stream()
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page("k1", EnableBankingFixtures.Transaction("ref-1")));
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.UnprocessableEntity, EnableBankingFixtures.Error("WRONG_CONTINUATION_KEY"));
        var received = new List<ProviderTransactionPage>();

        var act = async () =>
        {
            await foreach (var page in harness.Client.GetTransactionsAsync(ClientHarness.Account, new TransactionQuery(null, HistoryDepth.Longest), FetchContext.Background, CancellationToken.None))
            {
                received.Add(page);
            }
        };

        var thrown = (await act.Should().ThrowAsync<BankProviderException>()).Which;
        thrown.Kind.Should().Be(ProviderErrorKind.Transient);
        received.Should().ContainSingle();
        harness.Handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_stream_that_never_ends_is_cut_off_as_a_temporary_failure()
    {
        using var harness = new ClientHarness();

        for (var index = 0; index < 600; index++)
        {
            harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page("again"));
        }

        var act = () => FetchAsync(harness, new TransactionQuery(null, HistoryDepth.Longest));

        var thrown = (await act.Should().ThrowAsync<BankProviderException>()).Which;
        thrown.Kind.Should().Be(ProviderErrorKind.Transient);
        harness.Handler.Requests.Count.Should().BeLessThan(600);
    }

    [Theory]
    [InlineData(89, "date_from=2026-07-08")]
    [InlineData(0, "date_from=2026-10-05")]
    [InlineData(90, "date_from=2026-07-07&strategy=longest")]
    [InlineData(400, "date_from=2025-09-01&strategy=longest")]
    public async Task An_incremental_query_older_than_eighty_nine_days_switches_to_the_longest_strategy_with_that_lower_bound(int daysBack, string expectedQuery)
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page(null));
        var today = DateOnly.FromDateTime(harness.Time.GetUtcNow().UtcDateTime);
        var dateFrom = daysBack == 400 ? new DateOnly(2025, 9, 1) : today.AddDays(-daysBack);

        await FetchAsync(harness, new TransactionQuery(dateFrom, HistoryDepth.Incremental));

        harness.Handler.Requests.Single().FixedQuery().Should().Be(expectedQuery);
    }

    [Fact]
    public async Task The_longest_depth_sends_the_longest_strategy_and_no_start_date()
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page(null));

        await FetchAsync(harness, new TransactionQuery(new DateOnly(2026, 1, 1), HistoryDepth.Longest));

        var query = harness.Handler.Requests.Single();
        query.Parameter("strategy").Should().Be("longest");
        query.Parameter("date_from").Should().BeNull();
    }

    [Fact]
    public async Task An_attended_fetch_sends_the_operators_address_and_agent_when_the_bank_requires_only_those()
    {
        using var harness = new ClientHarness();
        harness.Cache.Store(["psu-ip-address"], harness.Time.GetUtcNow());
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page(null));
        harness.Handler.Respond("GET", BalancesPath, HttpStatusCode.OK, EnableBankingFixtures.ExpectedBalanceOnly());

        await FetchAsync(harness, new TransactionQuery(null, HistoryDepth.Longest), Attended);
        await harness.Client.GetBalancesAsync(ClientHarness.Account, Attended, CancellationToken.None);

        foreach (var request in harness.Handler.Requests)
        {
            request.Headers["Psu-Ip-Address"].Should().Be("192.0.2.10");
            request.Headers["Psu-User-Agent"].Should().Be("Example Agent");
        }
    }

    [Fact]
    public async Task A_background_fetch_sends_neither_header_and_does_not_ask_the_bank_list()
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page(null));

        await FetchAsync(harness, new TransactionQuery(null, HistoryDepth.Longest));

        var request = harness.Handler.Requests.Single();
        request.Headers.Should().NotContainKey("Psu-Ip-Address");
        request.Headers.Should().NotContainKey("Psu-User-Agent");
    }

    [Fact]
    public async Task A_fetch_sends_no_header_at_all_when_the_bank_requires_one_the_ledger_cannot_supply()
    {
        using var harness = new ClientHarness();
        harness.Cache.Store(["psu-ip-address", "psu-referer"], harness.Time.GetUtcNow());
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page(null));

        await FetchAsync(harness, new TransactionQuery(null, HistoryDepth.Longest), Attended);

        var request = harness.Handler.Requests.Single();
        request.Headers.Should().NotContainKey("Psu-Ip-Address");
        request.Headers.Should().NotContainKey("Psu-User-Agent");
    }

    [Fact]
    public async Task What_the_bank_requires_is_learned_from_the_bank_list_once_and_then_remembered()
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("GET", "/aspsps", HttpStatusCode.OK, EnableBankingFixtures.Aspsps(requiredHeaders: "[\"psu-ip-address\"]"));
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page(null));
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page(null));

        await FetchAsync(harness, new TransactionQuery(null, HistoryDepth.Longest), Attended);
        await FetchAsync(harness, new TransactionQuery(null, HistoryDepth.Longest), Attended);

        harness.Handler.Requests.Count(request => request.Path == "/aspsps").Should().Be(1);
        harness.Handler.Requests.Where(request => request.Path == TransactionsPath).Should().OnlyContain(request => request.Headers.ContainsKey("Psu-Ip-Address"));
    }

    [Fact]
    public async Task Starting_a_consent_remembers_what_the_bank_requires_for_the_fetches_that_follow()
    {
        using var harness = new ClientHarness();
        ScriptStart(harness, aspsps: EnableBankingFixtures.Aspsps(requiredHeaders: "[\"psu-ip-address\",\"psu-referer\"]"));
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, EnableBankingFixtures.Page(null));

        await harness.Client.StartAuthorizationAsync(Request, CancellationToken.None);
        await FetchAsync(harness, new TransactionQuery(null, HistoryDepth.Longest), Attended);

        harness.Handler.Requests.Count(request => request.Path == "/aspsps").Should().Be(1);
        harness.Handler.Requests.Last().Headers.Should().NotContainKey("Psu-Ip-Address");
    }

    [Fact]
    public void A_credit_maps_to_a_positive_amount_with_the_debtor_and_a_debit_to_a_negative_one_with_the_creditor()
    {
        var credit = EnableBankingJson.MapTransaction(EnableBankingFixtures.ParseElement(EnableBankingFixtures.Transaction("ref-credit", "CRDT")));
        var debit = EnableBankingJson.MapTransaction(EnableBankingFixtures.ParseElement(EnableBankingFixtures.Transaction("ref-debit", "DBIT")));

        credit.Amount.Should().Be(12.50m);
        credit.CounterpartyName.Should().Be("Example Employer");
        credit.CounterpartyIban.Should().Be("XX00EXAM0000000004");
        credit.EntryReference.Should().Be("ref-credit");
        credit.Status.Should().Be(ProviderTransactionStatus.Booked);
        credit.BookingDate.Should().Be(new DateOnly(2026, 9, 30));
        credit.Currency.Should().Be("EUR");

        debit.Amount.Should().Be(-12.50m);
        debit.CounterpartyName.Should().Be("Example Utility");
        debit.CounterpartyIban.Should().Be("XX00EXAM0000000003");
    }

    [Fact]
    public void Remittance_lines_join_with_one_space_and_the_raw_text_is_kept_untouched()
    {
        var json = EnableBankingFixtures.Transaction("ref-lines", remittance: "[\"First line\",\"Second line\"]");
        var element = EnableBankingFixtures.ParseElement(json);

        var mapped = EnableBankingJson.MapTransaction(element);

        mapped.Description.Should().Be("First line Second line");
        mapped.RawJson.Should().Be(element.GetRawText());
        JsonDocument.Parse(mapped.RawJson).RootElement.GetProperty("entry_reference").GetString().Should().Be("ref-lines");
    }

    [Fact]
    public void Missing_optional_fields_stay_null_and_statuses_map_to_their_kinds()
    {
        var minimal = EnableBankingFixtures.ParseElement("""{"entry_reference":"ref-min","status":"PDNG","credit_debit_indicator":"CRDT","transaction_amount":{"currency":"EUR","amount":"1.00"}}""");

        var mapped = EnableBankingJson.MapTransaction(minimal);

        mapped.Status.Should().Be(ProviderTransactionStatus.Pending);
        mapped.BookingDate.Should().BeNull();
        mapped.ValueDate.Should().BeNull();
        mapped.TransactionDate.Should().BeNull();
        mapped.CounterpartyName.Should().BeNull();
        mapped.CounterpartyIban.Should().BeNull();
        mapped.Description.Should().BeNull();

        EnableBankingJson.MapStatus("CNCL").Should().Be(ProviderTransactionStatus.Cancelled);
        EnableBankingJson.MapStatus("RJCT").Should().Be(ProviderTransactionStatus.Cancelled);
        EnableBankingJson.MapStatus("HOLD").Should().Be(ProviderTransactionStatus.Other);
        EnableBankingJson.MapStatus(null).Should().Be(ProviderTransactionStatus.Other);
    }

    [Theory]
    [InlineData("""{"status":"BOOK","credit_debit_indicator":"CRDT","transaction_amount":{"currency":"EUR","amount":"1.00"},"booking_date":"2026-9-30"}""")]
    [InlineData("""{"status":"BOOK","credit_debit_indicator":"CRDT","transaction_amount":{"currency":"EUR","amount":"1.00"},"booking_date":20260930}""")]
    [InlineData("""{"status":"BOOK","credit_debit_indicator":"CRDT","transaction_amount":{"currency":"EUR","amount":"1.23456"}}""")]
    [InlineData("""{"status":"BOOK","credit_debit_indicator":"CRDT","transaction_amount":{"currency":"EUR","amount":"-1.00"}}""")]
    [InlineData("""{"status":"BOOK","credit_debit_indicator":"CRDT","transaction_amount":{"currency":"EUR"}}""")]
    [InlineData("""{"status":"BOOK","transaction_amount":{"currency":"EUR","amount":"1.00"}}""")]
    [InlineData("""{"status":"BOOK","credit_debit_indicator":"CRDT","transaction_amount":{"amount":"1.00"}}""")]
    [InlineData("""{"status":"BOOK","credit_debit_indicator":"CRDT","transaction_amount":{"currency":"EUR","amount":"1.00"},"remittance_information":"not a list"}""")]
    [InlineData("""{"status":"BOOK","credit_debit_indicator":"CRDT","transaction_amount":{"currency":"EUR","amount":"1.00"},"debtor":"not an object"}""")]
    [InlineData("""[]""")]
    public void Malformed_transactions_fail_as_malformed_data_instead_of_being_stored(string json)
    {
        var act = () => EnableBankingJson.MapTransaction(EnableBankingFixtures.ParseElement(json));

        var thrown = act.Should().Throw<BankProviderException>().Which;
        thrown.Kind.Should().Be(ProviderErrorKind.MalformedData);
        thrown.Message.Should().NotContain("1.23456");
    }

    [Fact]
    public async Task A_page_without_a_transactions_list_fails_as_malformed_data()
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("GET", TransactionsPath, HttpStatusCode.OK, "{\"continuation_key\":null}");

        var act = () => FetchAsync(harness, new TransactionQuery(null, HistoryDepth.Longest));

        (await act.Should().ThrowAsync<BankProviderException>()).Which.Kind.Should().Be(ProviderErrorKind.MalformedData);
    }

    [Fact]
    public async Task A_response_that_is_not_json_fails_as_malformed_data_without_echoing_it()
    {
        using var harness = new ClientHarness();
        harness.Handler.Respond("GET", BalancesPath, HttpStatusCode.OK, "<html>SENTINEL-HTML</html>");

        var act = () => harness.Client.GetBalancesAsync(ClientHarness.Account, FetchContext.Background, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<BankProviderException>()).Which;
        thrown.Kind.Should().Be(ProviderErrorKind.MalformedData);
        thrown.Message.Should().NotContain("SENTINEL");
    }

    [Fact]
    public async Task Ending_a_session_that_is_already_gone_succeeds_and_any_other_failure_is_reported()
    {
        using var harness = new ClientHarness();
        var path = $"/sessions/{EnableBankingFixtures.SessionId}";
        harness.Handler.Respond("DELETE", path, HttpStatusCode.OK, "{\"message\":\"OK\"}");
        harness.Handler.Respond("DELETE", path, HttpStatusCode.Unauthorized, EnableBankingFixtures.Error("EXPIRED_SESSION"));
        harness.Handler.Respond("DELETE", path, HttpStatusCode.NotFound, EnableBankingFixtures.Error("SESSION_DOES_NOT_EXIST"));
        harness.Handler.Respond("DELETE", path, HttpStatusCode.InternalServerError, "{}");
        harness.Handler.Respond("DELETE", path, HttpStatusCode.Forbidden, EnableBankingFixtures.Error("ACCESS_DENIED"));

        await harness.Client.RevokeSessionAsync(EnableBankingFixtures.SessionId, CancellationToken.None);
        await harness.Client.RevokeSessionAsync(EnableBankingFixtures.SessionId, CancellationToken.None);
        await harness.Client.RevokeSessionAsync(EnableBankingFixtures.SessionId, CancellationToken.None);

        var transient = () => harness.Client.RevokeSessionAsync(EnableBankingFixtures.SessionId, CancellationToken.None);
        (await transient.Should().ThrowAsync<BankProviderException>()).Which.Kind.Should().Be(ProviderErrorKind.Transient);

        var denied = () => harness.Client.RevokeSessionAsync(EnableBankingFixtures.SessionId, CancellationToken.None);
        (await denied.Should().ThrowAsync<BankProviderException>()).Which.Kind.Should().Be(ProviderErrorKind.ProviderAuth);
    }
}
