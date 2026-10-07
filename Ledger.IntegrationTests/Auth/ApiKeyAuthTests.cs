using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Ledger.Domain.Auth;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Repository;
using Ledger.Repository.Stores;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Auth;

/// <summary>Proves every REST endpoint requires a valid, hashed, revocable named key and never logs the presented value.</summary>
[Collection("Database")]
public partial class ApiKeyAuthTests(DatabaseFixture fixture)
{
    [Fact]
    [Trait("Category", "ApiAuth")]
    public async Task Every_mapped_endpoint_returns_401_without_a_key()
    {
        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var client = factory.CreateApiClient();
        await Wait.UntilReadyAsync(factory);

        var endpointDataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var routeEndpoints = endpointDataSource.Endpoints.OfType<RouteEndpoint>().ToList();

        routeEndpoints.Should().NotBeEmpty();

        foreach (var endpoint in routeEndpoints.Where(candidate => candidate.RoutePattern.RawText != AnonymousBankCallbackRoute))
        {
            var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
            var path = FillRoutePlaceholders(endpoint);

            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.ToString().Should().Contain(ApiKeyAuthenticationHandlerScheme);
        }
    }

    [Fact]
    [Trait("Category", "ApiAuth")]
    public async Task Malformed_empty_wrong_secret_oversized_or_duplicate_headers_return_401_never_500()
    {
        var (_, token, _) = await CreateActiveKeyAsync("malformed-test");
        ApiKeyToken.TryParse(token, out var keyId, out _);

        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var client = factory.CreateApiClient();
        await Wait.UntilReadyAsync(factory);

        await AssertUnauthorizedAsync(client, "");
        await AssertUnauthorizedAsync(client, "not-a-valid-token");
        await AssertUnauthorizedAsync(client, $"ldg_{keyId}_" + new string('A', 43));
        await AssertUnauthorizedAsync(client, new string('a', 10_000));
        await AssertUnauthorizedAsync(client, token, token);
    }

    [Fact]
    [Trait("Category", "ApiAuth")]
    public async Task Status_endpoint_returns_the_callers_name_and_running_version()
    {
        var (name, token, _) = await CreateActiveKeyAsync("status-test");

        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var client = factory.CreateApiClient();
        await Wait.UntilReadyAsync(factory);

        using var response = await SendStatusRequestAsync(client, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<StatusResponseModel>(TestContext.Current.CancellationToken);
        body.Should().NotBeNull();
        body!.Client.Should().Be(name);
        body.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    [Trait("Category", "ApiAuth")]
    public async Task Revoked_key_is_rejected_on_the_next_request()
    {
        var (name, token, _) = await CreateActiveKeyAsync("revoke-auth-test");

        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var client = factory.CreateApiClient();
        await Wait.UntilReadyAsync(factory);

        using (var beforeRevoke = await SendStatusRequestAsync(client, token))
        {
            beforeRevoke.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await RevokeKeyAsync(name);

        using var afterRevoke = await SendStatusRequestAsync(client, token);
        afterRevoke.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "ApiAuth")]
    public async Task Concurrent_requests_with_valid_revoked_and_malformed_keys_are_each_decided_independently()
    {
        var (_, validToken, _) = await CreateActiveKeyAsync("concurrent-valid");
        var (revokedName, revokedToken, _) = await CreateActiveKeyAsync("concurrent-revoked");
        await RevokeKeyAsync(revokedName);
        const string malformedToken = "not-a-valid-token";

        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var client = factory.CreateApiClient();
        await Wait.UntilReadyAsync(factory);

        var tokens = Enumerable.Range(0, 40)
            .Select(i => (i % 3) switch
            {
                0 => validToken,
                1 => revokedToken,
                _ => malformedToken
            })
            .ToList();

        var responses = await Task.WhenAll(tokens.Select(token => SendStatusRequestAsync(client, token)));

        try
        {
            var okCount = responses.Count(response => response.StatusCode == HttpStatusCode.OK);
            var expectedOkCount = tokens.Count(token => token == validToken);

            okCount.Should().Be(expectedOkCount);
            responses.Where(response => response.StatusCode != HttpStatusCode.OK)
                .Should().OnlyContain(response => response.StatusCode == HttpStatusCode.Unauthorized);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    [Trait("Category", "ApiAuth")]
    public async Task Health_and_metrics_are_not_served_on_the_api_port_even_with_a_valid_key()
    {
        var (_, token, _) = await CreateActiveKeyAsync("ops-surface-test");

        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var client = factory.CreateApiClient();
        await Wait.UntilReadyAsync(factory);

        using var healthRequest = new HttpRequestMessage(HttpMethod.Get, "/health");
        healthRequest.Headers.Add("X-Api-Key", token);
        using var healthResponse = await client.SendAsync(healthRequest, TestContext.Current.CancellationToken);
        healthResponse.StatusCode.Should().NotBe(HttpStatusCode.OK);

        using var metricsRequest = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        metricsRequest.Headers.Add("X-Api-Key", token);
        using var metricsResponse = await client.SendAsync(metricsRequest, TestContext.Current.CancellationToken);
        metricsResponse.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "ApiAuth")]
    public async Task Log_capture_never_contains_a_presented_token_or_its_secret()
    {
        var (_, validToken, _) = await CreateActiveKeyAsync("log-capture-valid");
        var (revokedName, revokedToken, _) = await CreateActiveKeyAsync("log-capture-revoked");
        await RevokeKeyAsync(revokedName);
        ApiKeyToken.TryParse(validToken, out _, out var validSecret);
        ApiKeyToken.TryParse(revokedToken, out _, out var revokedSecret);

        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var client = factory.CreateApiClient();
        await Wait.UntilReadyAsync(factory);

        using (await SendStatusRequestAsync(client, validToken))
        {
        }

        using (await SendStatusRequestAsync(client, revokedToken))
        {
        }

        using (await SendStatusRequestAsync(client, "not-a-valid-token"))
        {
        }

        var logText = string.Join('\n', factory.CapturedLogMessages);

        logText.Should().NotContain(validToken);
        logText.Should().NotContain(revokedToken);
        logText.Should().NotContain(validSecret);
        logText.Should().NotContain(revokedSecret);
    }

    private const string ApiKeyAuthenticationHandlerScheme = "ApiKey";

    private const string AnonymousBankCallbackRoute = "/api/v1/bank/callback";

    private async Task AssertUnauthorizedAsync(HttpClient client, params string[] headerValues)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        foreach (var value in headerValues)
        {
            request.Headers.Add("X-Api-Key", value);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpResponseMessage> SendStatusRequestAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        request.Headers.Add("X-Api-Key", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string FillRoutePlaceholders(RouteEndpoint endpoint)
    {
        var rawText = endpoint.RoutePattern.RawText ?? string.Empty;
        var filled = PlaceholderPattern().Replace(rawText, "placeholder");
        return filled.StartsWith('/') ? filled : "/" + filled;
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex PlaceholderPattern();

    private async Task<(string Name, string Token, string KeyId)> CreateActiveKeyAsync(string name)
    {
        await using var context = CreateRuntimeContext();
        var store = new ApiKeyStore(context);
        var created = await store.CreateAsync(name, TestContext.Current.CancellationToken);
        return (created.Name, created.Token, created.KeyId);
    }

    private async Task RevokeKeyAsync(string name)
    {
        await using var context = CreateRuntimeContext();
        var store = new ApiKeyStore(context);
        await store.RevokeAsync(name, TestContext.Current.CancellationToken);
    }

    private LedgerDbContext CreateRuntimeContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<LedgerDbContext>();
        optionsBuilder.UseNpgsql(fixture.ConnectionStringFor("ledger_runtime"));
        return new LedgerDbContext(optionsBuilder.Options);
    }

    private sealed record StatusResponseModel(string Version, string Client);
}
