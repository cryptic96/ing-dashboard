using FluentAssertions;
using Ledger.Service.OAuth;

namespace Ledger.UnitTests.Mcp;

/// <summary>Verifies which spellings of the MCP address count as the protected resource and which never do.</summary>
[Trait("Category", "OAuth")]
public class ResourceIndicatorTests
{
    private const string Canonical = "https://mcp.example.com/mcp";

    [Theory]
    [InlineData("https://mcp.example.com/mcp")]
    [InlineData("HTTPS://MCP.Example.COM/mcp")]
    [InlineData("https://mcp.example.com:443/mcp")]
    [InlineData("https://mcp.example.com/mcp/")]
    public void Canonical_spellings_of_the_resource_match(string requested)
    {
        ResourceIndicator.IsCanonicalMatch(requested, Canonical).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://mcp.example.com/other")]
    [InlineData("https://mcp.example.com/MCP")]
    [InlineData("https://other.example.com/mcp")]
    [InlineData("http://mcp.example.com/mcp")]
    [InlineData("https://mcp.example.com:8443/mcp")]
    [InlineData("https://mcp.example.com/mcp?x=1")]
    [InlineData("https://mcp.example.com/mcp#fragment")]
    [InlineData("https://mcp.example.com/mcp?")]
    [InlineData("mcp.example.com/mcp")]
    [InlineData("")]
    public void Any_other_address_never_matches(string requested)
    {
        ResourceIndicator.IsCanonicalMatch(requested, Canonical).Should().BeFalse();
    }

    [Fact]
    public void Canonicalize_lowercases_scheme_and_host_and_drops_the_default_port_and_trailing_slash()
    {
        ResourceIndicator.Canonicalize("HTTPS://MCP.Example.COM:443/mcp/").Should().Be(Canonical);
    }

    [Fact]
    public void Options_publish_every_address_from_the_configured_base_only()
    {
        var options = new LedgerOAuthOptions { PublicBaseUrl = "https://MCP.example.com/" };

        options.ResourceUrl.Should().Be(Canonical);
        options.ResourceMetadataUrl.Should().Be("https://mcp.example.com/.well-known/oauth-protected-resource/mcp");
        options.IssuerText.Should().Be("https://mcp.example.com/");
        options.PublicHost.Should().Be("mcp.example.com");
        options.IsEnabled.Should().BeTrue();
        new LedgerOAuthOptions().IsEnabled.Should().BeFalse();
    }
}
