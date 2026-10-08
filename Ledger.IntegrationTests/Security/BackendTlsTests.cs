using System.Net;
using System.Text.Json;
using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;

namespace Ledger.IntegrationTests.Security;

/// <summary>
/// Proves the proxy-facing endpoint speaks only TLS from the configured certificate and key, that a client pinning that one
/// certificate gets through while every other client is refused, and that the discovery checks the host runs against itself work
/// over that connection without any forwarded header.
/// </summary>
[Collection("Database")]
public class BackendTlsTests(DatabaseFixture fixture)
{
    private const string PublicHost = "mcp.example.com";

    [Fact]
    [Trait("Category", "BackendTls")]
    public async Task The_api_endpoint_serves_https_to_a_client_that_pins_the_host_certificate_and_refuses_everyone_else()
    {
        using var certificate = new TestBackendCertificate();
        using var other = new TestBackendCertificate();
        await using var host = await McpTestHost.StartAsync(
            fixture.ConnectionStringFor("ledger_runtime"),
            backendCertificate: certificate);

        using var pinned = host.Factory.CreatePinnedHttpsApiClient(certificate.Certificate);
        using var response = await pinned.GetAsync("/api/v1/status", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the request got through TLS and reached the application, which wants an API key");

        using var impostor = host.Factory.CreatePinnedHttpsApiClient(other.Certificate);
        var rejected = async () => await impostor.GetAsync("/api/v1/status", TestContext.Current.CancellationToken);

        await rejected.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    [Trait("Category", "BackendTls")]
    public async Task The_api_endpoint_gives_no_application_answer_to_plain_http()
    {
        using var certificate = new TestBackendCertificate();
        await using var host = await McpTestHost.StartAsync(
            fixture.ConnectionStringFor("ledger_runtime"),
            backendCertificate: certificate);

        using var plain = host.Factory.CreateApiClient();
        var attempt = async () => await plain.GetAsync("/api/v1/status", TestContext.Current.CancellationToken);

        await attempt.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    [Trait("Category", "BackendTls")]
    public async Task The_discovery_document_and_the_challenge_are_served_over_the_pinned_connection_without_forwarded_headers()
    {
        using var certificate = new TestBackendCertificate();
        await using var host = await McpTestHost.StartAsync(
            fixture.ConnectionStringFor("ledger_runtime"),
            backendCertificate: certificate);
        using var client = host.Factory.CreatePinnedHttpsApiClient(certificate.Certificate);

        using var challenge = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        challenge.Headers.Host = PublicHost;
        using var challengeResponse = await client.SendAsync(challenge, TestContext.Current.CancellationToken);

        challengeResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        challengeResponse.Headers.WwwAuthenticate.ToString().Should()
            .Contain("resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource/mcp\"");

        using var document = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-protected-resource/mcp");
        document.Headers.Host = PublicHost;
        using var documentResponse = await client.SendAsync(document, TestContext.Current.CancellationToken);

        documentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await documentResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("resource").GetString().Should().Be("https://mcp.example.com/mcp");

        using var signIn = new HttpRequestMessage(HttpMethod.Get, "/account/login");
        signIn.Headers.Host = PublicHost;
        using var signInResponse = await client.SendAsync(signIn, TestContext.Current.CancellationToken);

        signInResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, "loopback is not one of the sign-in networks");
    }
}
