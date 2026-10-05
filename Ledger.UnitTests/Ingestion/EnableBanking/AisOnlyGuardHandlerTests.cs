using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Service.Ingestion.EnableBanking;

namespace Ledger.UnitTests.Ingestion.EnableBanking;

/// <summary>Proves that nothing but account-information reads can leave the process through the aggregator client.</summary>
[Trait("Category", "ReadOnlyGuard")]
public partial class AisOnlyGuardHandlerTests
{
    private static readonly string[] AllowedRoutes =
    [
        "GET /application",
        "GET /aspsps",
        "POST /auth",
        "POST /sessions",
        "DELETE /sessions/{id}",
        "GET /accounts/{id}/balances",
        "GET /accounts/{id}/transactions"
    ];

    [GeneratedRegex(@"/(accounts|sessions)/[A-Za-z0-9-]+")]
    private static partial Regex IdSegment();

    [Fact]
    public async Task A_full_recorded_run_uses_exactly_the_seven_account_information_routes()
    {
        using var harness = new ClientHarness();
        var handler = harness.Handler;
        handler.Respond("GET", "/application", HttpStatusCode.OK, EnableBankingFixtures.Application());
        handler.Respond("GET", "/aspsps", HttpStatusCode.OK, EnableBankingFixtures.Aspsps());
        handler.Respond("POST", "/auth", HttpStatusCode.OK, EnableBankingFixtures.Authorization());
        handler.Respond("POST", "/sessions", HttpStatusCode.OK, EnableBankingFixtures.Session());
        handler.Respond("GET", $"/accounts/{EnableBankingFixtures.AccountUid}/balances", HttpStatusCode.OK, EnableBankingFixtures.ExpectedBalanceOnly());
        handler.Respond("GET", $"/accounts/{EnableBankingFixtures.AccountUid}/transactions", HttpStatusCode.OK, EnableBankingFixtures.Page("k1", EnableBankingFixtures.Transaction("ref-1")));
        handler.Respond("GET", $"/accounts/{EnableBankingFixtures.AccountUid}/transactions", HttpStatusCode.OK, EnableBankingFixtures.Page("k2"));
        handler.Respond("GET", $"/accounts/{EnableBankingFixtures.AccountUid}/transactions", HttpStatusCode.OK, EnableBankingFixtures.Page(null, EnableBankingFixtures.Transaction("ref-2", "CRDT")));
        handler.Respond("DELETE", $"/sessions/{EnableBankingFixtures.SessionId}", HttpStatusCode.OK, "{\"message\":\"OK\"}");

        var psu = new FetchContext(new PsuContext("192.0.2.10", "Example Agent"));

        await harness.Client.StartAuthorizationAsync(new AuthorizationRequest("state-value", new Uri(EnableBankingFixtures.RedirectUrl)), CancellationToken.None);
        await harness.Client.CompleteAuthorizationAsync("code-value", CancellationToken.None);
        await harness.Client.GetBalancesAsync(ClientHarness.Account, psu, CancellationToken.None);

        await foreach (var page in harness.Client.GetTransactionsAsync(
                           ClientHarness.Account,
                           new TransactionQuery(null, HistoryDepth.Longest),
                           psu,
                           CancellationToken.None))
        {
            page.Should().NotBeNull();
        }

        await harness.Client.RevokeSessionAsync(EnableBankingFixtures.SessionId, CancellationToken.None);

        var used = handler.Requests.Select(request => request.Method + " " + IdSegment().Replace(request.Path, match => $"/{match.Groups[1].Value}/{{id}}")).Distinct().ToList();
        used.Should().BeEquivalentTo(AllowedRoutes);
    }

    [Theory]
    [InlineData("POST", "https://api.enablebanking.com/payments")]
    [InlineData("GET", "https://api.enablebanking.com/payments/abc")]
    [InlineData("POST", "https://api.enablebanking.com/payments/abc/submit")]
    [InlineData("GET", "https://api.enablebanking.com/payments/abc/transactions/def")]
    [InlineData("GET", "https://api.enablebanking.com/sessions/abc")]
    [InlineData("GET", "https://api.enablebanking.com/accounts/abc/details")]
    [InlineData("GET", "https://api.enablebanking.com/accounts/abc/transactions/extra")]
    [InlineData("GET", "https://api.enablebanking.com/accounts/%20/transactions")]
    [InlineData("PUT", "https://api.enablebanking.com/auth")]
    [InlineData("GET", "https://api.enablebanking.com/auth")]
    [InlineData("DELETE", "https://api.enablebanking.com/auth")]
    [InlineData("POST", "https://api.enablebanking.com/application")]
    [InlineData("DELETE", "https://api.enablebanking.com/sessions/abc/extra")]
    [InlineData("DELETE", "https://api.enablebanking.com/sessions")]
    [InlineData("POST", "https://api.enablebanking.com/sessions/abc")]
    [InlineData("GET", "https://example.org/application")]
    [InlineData("GET", "https://api.enablebanking.com.example.org/application")]
    [InlineData("GET", "http://api.enablebanking.com/application")]
    [InlineData("GET", "https://api.enablebanking.com:8443/application")]
    [InlineData("GET", "https://api.enablebanking.com@example.org/application")]
    public async Task Any_other_route_host_scheme_or_method_is_refused_and_reaches_no_inner_handler(string method, string url)
    {
        var inner = new RecordingHandler();
        using var invoker = new HttpMessageInvoker(new AisOnlyGuardHandler { InnerHandler = inner });
        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        var act = () => invoker.SendAsync(request, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Be(AisOnlyGuardHandler.RefusalMessage).And.NotContain("payments").And.NotContain("example.org");
        inner.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("GET", "https://api.enablebanking.com/application")]
    [InlineData("GET", "https://api.enablebanking.com/aspsps?country=NL&service=AIS")]
    [InlineData("POST", "https://api.enablebanking.com/auth")]
    [InlineData("POST", "https://api.enablebanking.com/sessions")]
    [InlineData("DELETE", "https://api.enablebanking.com/sessions/22222222-2222-2222-2222-222222222222")]
    [InlineData("GET", "https://api.enablebanking.com/accounts/11111111-1111-1111-1111-111111111111/balances")]
    [InlineData("GET", "https://api.enablebanking.com/accounts/11111111-1111-1111-1111-111111111111/transactions?strategy=longest")]
    public async Task The_seven_account_information_routes_pass_through(string method, string url)
    {
        var inner = new RecordingHandler();
        var uri = new Uri(url);
        inner.Respond(method, uri.AbsolutePath, HttpStatusCode.OK, "{}");
        using var invoker = new HttpMessageInvoker(new AisOnlyGuardHandler { InnerHandler = inner });
        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Requests.Should().ContainSingle();
    }

    [Fact]
    public void The_provider_interface_has_no_member_that_could_pay_transfer_or_initiate_anything()
    {
        var forbidden = new[] { "Pay", "Payment", "Transfer", "Initiate" };

        foreach (var type in new[] { typeof(IBankDataProvider), typeof(EnableBankingClient) })
        {
            var names = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(member => member.Name)
                .ToList();

            names.Should().NotBeEmpty();
            names.Should().NotContain(name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public async Task A_client_built_to_reach_a_payment_route_is_stopped_by_the_guard()
    {
        using var harness = new ClientHarness();
        using var http = new HttpClient(new AisOnlyGuardHandler { InnerHandler = harness.Handler }) { BaseAddress = EnableBankingOptions.BaseAddress };

        var act = () => http.PostAsync("/payments", new StringContent("{}"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        harness.Handler.Requests.Should().BeEmpty();
    }
}
