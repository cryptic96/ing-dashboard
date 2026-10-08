using System.Net;
using FluentAssertions;
using Ledger.Service.Hosting;
using Ledger.Service.OAuth;
using Microsoft.AspNetCore.Http;

namespace Ledger.UnitTests.Hosting;

/// <summary>Verifies the host guard treats every spelling of the public host name as the public host.</summary>
[Trait("Category", "OAuth")]
public class PublicHostGuardTests
{
    private const string PublicName = "mcp.household.test";

    [Theory]
    [InlineData("mcp.household.test")]
    [InlineData("mcp.household.test.")]
    [InlineData("MCP.Household.Test")]
    [InlineData("MCP.Household.Test.")]
    [InlineData("mcp.household.test:443")]
    [InlineData("mcp.household.test.:443")]
    public async Task Every_spelling_of_the_public_host_serves_only_the_mcp_and_oauth_surface(string host)
    {
        (await StatusAsync(host, "/api/v1/status")).Should().Be(StatusCodes.Status404NotFound);
        (await StatusAsync(host, "/metrics")).Should().Be(StatusCodes.Status404NotFound);
        (await StatusAsync(host, "/mcp")).Should().Be(StatusCodes.Status200OK);
        (await StatusAsync(host, "/.well-known/oauth-protected-resource/mcp")).Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("mcp.household.test.")]
    [InlineData("MCP.Household.Test")]
    [InlineData("mcp.household.test.:443")]
    public async Task Sign_in_pages_on_a_spelling_of_the_public_host_still_answer_only_to_the_sign_in_networks(string host)
    {
        (await StatusAsync(host, "/account/login", "203.0.113.9")).Should().Be(StatusCodes.Status404NotFound);
        (await StatusAsync(host, "/account/login", "192.0.2.50")).Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("ledger.household.test", "/api/v1/status", StatusCodes.Status200OK)]
    [InlineData("ledger.household.test.", "/api/v1/status", StatusCodes.Status200OK)]
    [InlineData("ledger.household.test", "/mcp", StatusCodes.Status404NotFound)]
    [InlineData("ledger.household.test", "/account/login", StatusCodes.Status404NotFound)]
    [InlineData("203.0.113.7", "/connect/token", StatusCodes.Status404NotFound)]
    public async Task Any_other_host_serves_everything_except_the_oauth_and_mcp_surface(string host, string path, int expected)
    {
        (await StatusAsync(host, path)).Should().Be(expected);
    }

    private static async Task<int> StatusAsync(string host, string path, string remoteAddress = "192.0.2.50")
    {
        var options = new LedgerOAuthOptions
        {
            PublicBaseUrl = "https://" + PublicName,
            SignInNetworks = ["192.0.2.0/24"]
        };
        var guard = new PublicHostGuard(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            new LedgerOAuthSurface(options));
        var context = new DefaultHttpContext();
        context.Request.Host = HostString.FromUriComponent(host);
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);

        await guard.InvokeAsync(context);

        return context.Response.StatusCode;
    }
}
