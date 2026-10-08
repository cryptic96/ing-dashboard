using System.Net;
using FluentAssertions;
using Ledger.Service.OAuth;
using Microsoft.Extensions.Configuration;

namespace Ledger.UnitTests.Mcp;

/// <summary>Verifies which clients may reach the sign-in pages, and that the resource address is judged the same way at every edge.</summary>
[Trait("Category", "OAuth")]
public class ResourceAndNetworkTests
{
    private const string Canonical = "https://mcp.example.com/mcp";

    [Theory]
    [InlineData("https://MCP.example.com/mcp")]
    [InlineData("https://mcp.example.com:443/mcp")]
    [InlineData("https://mcp.example.com/mcp/")]
    public void The_resource_is_accepted_when_only_the_host_case_a_default_port_or_one_trailing_slash_differ(string requested)
    {
        ResourceIndicator.IsCanonicalMatch(requested, Canonical).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://mcp.example.com/mcp2")]
    [InlineData("https://mcp.example.com/mcp/x")]
    [InlineData("http://mcp.example.com/mcp")]
    [InlineData("https://other.example.com/mcp")]
    [InlineData("https://mcp.example.com/mcp?x=1")]
    [InlineData("https://mcp.example.com/mcp#section")]
    public void The_resource_is_refused_for_any_other_path_scheme_host_query_or_fragment(string requested)
    {
        ResourceIndicator.IsCanonicalMatch(requested, Canonical).Should().BeFalse();
    }

    [Theory]
    [InlineData("192.0.2.0", true)]
    [InlineData("192.0.2.1", true)]
    [InlineData("192.0.2.255", true)]
    [InlineData("192.0.1.255", false)]
    [InlineData("192.0.3.0", false)]
    [InlineData("203.0.113.7", false)]
    [InlineData("::ffff:192.0.2.77", true)]
    [InlineData("::ffff:192.0.3.1", false)]
    [InlineData("2001:db8::1", false)]
    public void A_range_includes_both_of_its_ends_and_nothing_beyond_them(string address, bool expected)
    {
        SignInNetworks.Contains(["192.0.2.0/24"], IPAddress.Parse(address)).Should().Be(expected);
    }

    [Theory]
    [InlineData("2001:db8::", true)]
    [InlineData("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [InlineData("2001:db7:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("2001:db9::", false)]
    [InlineData("192.0.2.1", false)]
    public void An_IPv6_range_is_judged_on_its_own_prefix(string address, bool expected)
    {
        SignInNetworks.Contains(["2001:db8::/32"], IPAddress.Parse(address)).Should().Be(expected);
    }

    [Fact]
    public void Any_range_in_the_list_admits_an_address_and_an_empty_list_admits_nobody()
    {
        string[] ranges = ["192.0.2.0/24", "198.51.100.0/24"];

        SignInNetworks.Contains(ranges, IPAddress.Parse("198.51.100.9")).Should().BeTrue();
        SignInNetworks.Contains([], IPAddress.Parse("192.0.2.1")).Should().BeFalse();
        SignInNetworks.Contains(ranges, null).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-range")]
    [InlineData("192.0.2.0/33")]
    [InlineData("192.0.2.0/-1")]
    [InlineData("192.0.2.0/abc")]
    [InlineData("2001:db8::/129")]
    public void A_range_that_cannot_be_parsed_is_refused_and_admits_nobody(string range)
    {
        SignInNetworks.TryParse(range, out _).Should().BeFalse();
        SignInNetworks.Contains([range], IPAddress.Parse("192.0.2.1")).Should().BeFalse();
    }

    [Fact]
    public void Ranges_overlap_when_one_contains_the_other_and_not_when_they_are_adjacent()
    {
        SignInNetworks.TryParse("160.79.104.0/21", out var anthropic).Should().BeTrue();
        SignInNetworks.TryParse("160.79.104.0/22", out var inside).Should().BeTrue();
        SignInNetworks.TryParse("160.79.100.0/22", out var beneath).Should().BeTrue();
        SignInNetworks.TryParse("160.79.112.0/24", out var above).Should().BeTrue();
        SignInNetworks.TryParse("160.79.0.0/16", out var enclosing).Should().BeTrue();

        SignInNetworks.Overlaps(anthropic, inside).Should().BeTrue();
        SignInNetworks.Overlaps(anthropic, enclosing).Should().BeTrue();
        SignInNetworks.Overlaps(anthropic, beneath).Should().BeFalse();
        SignInNetworks.Overlaps(anthropic, above).Should().BeFalse();
    }

    [Fact]
    public void The_options_read_the_sign_in_networks_from_numbered_configuration_keys()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OAuth:SignInNetworks:0"] = "192.0.2.0/24",
                ["OAuth:SignInNetworks:1"] = "198.51.100.0/24"
            })
            .Build();

        var options = configuration.GetSection(LedgerOAuthOptions.SectionName).Get<LedgerOAuthOptions>();

        options!.SignInNetworks.Should().Equal("192.0.2.0/24", "198.51.100.0/24");
        new LedgerOAuthOptions().SignInNetworks.Should().BeEmpty();
    }
}
