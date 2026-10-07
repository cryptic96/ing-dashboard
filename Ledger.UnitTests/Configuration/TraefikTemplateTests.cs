using System.Text.RegularExpressions;
using FluentAssertions;

namespace Ledger.UnitTests.Configuration;

/// <summary>
/// Pins every rule of the committed reverse-proxy template: which paths are public on the MCP hostname, which networks may reach
/// them, that the sign-in paths stay on the home and VPN allowlist, and that nothing in the file can widen the allowlist or hold
/// back streamed replies. The file is read as text; the routers are cut out of it by their indentation.
/// </summary>
public partial class TraefikTemplateTests
{
    private const string AnthropicRange = "160.79.104.0/21";
    private const string McpHost = "mcp.example.com";

    private static readonly string[] PublicPaths =
    [
        "/mcp",
        "/.well-known/oauth-protected-resource/mcp",
        "/.well-known/oauth-authorization-server",
        "/.well-known/openid-configuration",
        "/connect/token"
    ];

    private static readonly string[] DocumentationRanges = ["192.0.2.", "198.51.100.", "203.0.113."];

    [Fact]
    [Trait("Category", "Configuration")]
    public void The_committed_template_has_no_violations()
    {
        Violations(ReadTemplate()).Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void The_template_names_anthropics_current_range_and_no_phased_out_address()
    {
        var template = ReadTemplate();

        template.Should().Contain(AnthropicRange);
        template.Should().NotContain("34.162.");
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void The_mcp_allowlist_holds_only_anthropics_range_and_the_home_and_vpn_ranges()
    {
        var block = Block(ReadTemplate(), "ledger-mcp-allow", indent: 4);
        var ranges = Regex.Matches(block, "- \"([^\"]+)\"").Select(match => match.Groups[1].Value).ToList();

        ranges.Should().BeEquivalentTo([AnthropicRange, "192.0.2.0/24", "198.51.100.0/24"]);
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("          - \"160.79.104.0/21\"\n", "          - \"160.79.104.0/21\"\n          - \"203.0.114.0/24\"\n")]
    [InlineData("    ledger-security-headers:\n", "    ledger-security-headers:\n      ipStrategy:\n        depth: 1\n")]
    [InlineData("    ledger-security-headers:\n", "    ledger-security-headers:\n      buffering:\n        maxResponseBodyBytes: 1\n")]
    [InlineData("          - \"160.79.104.0/21\"\n", "          - \"160.79.104.0/21\"\n          - \"34.162.1.1/32\"\n")]
    [InlineData("Host(`ledger-api.example.com`)", "Host(`ledger-api.example.org`)")]
    [InlineData("Path(`/connect/authorize`) || PathPrefix(`/account/`)", "PathPrefix(`/connect/`) || PathPrefix(`/account/`)")]
    [InlineData("Path(`/connect/token`)", "PathPrefix(`/connect/`)")]
    [InlineData("Path(`/connect/token`)", "Path(`/connect/token`) || Path(`/api/v1/status`)")]
    [InlineData("        - ledger-mcp-allow\n", "        - ledger-lan-vpn-only\n")]
    [InlineData("      rule: \"Host(`ledger-api.example.com`) && PathPrefix(`/api/`)\"", "      rule: \"Host(`ledger-api.example.com`) && (PathPrefix(`/api/`) || Path(`/mcp`))\"")]
    [InlineData("      rule: \"Host(`grafana.example.com`)\"", "      rule: \"Host(`grafana.example.com`) || Host(`mcp.example.com`)\"")]
    public void A_weakened_template_is_reported(string original, string weakened)
    {
        var template = ReadTemplate();

        template.Should().Contain(original);

        Violations(template.Replace(original, weakened)).Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void A_sign_in_router_that_uses_the_public_allowlist_is_reported()
    {
        var template = ReadTemplate();
        var signIn = Block(template, "ledger-mcp-signin", indent: 4);
        var weakened = template.Replace(signIn, signIn.Replace("ledger-lan-vpn-only", "ledger-mcp-allow"));

        Violations(weakened).Should().NotBeEmpty();
    }

    private static List<string> Violations(string template)
    {
        var violations = new List<string>();

        AddAddressViolations(template, violations);
        AddPolicyViolations(template, violations);
        AddPublicRouterViolations(template, violations);
        AddSignInRouterViolations(template, violations);
        AddOtherRouterViolations(template, violations);

        return violations;
    }

    private static void AddAddressViolations(string template, List<string> violations)
    {
        if (!template.Contains(AnthropicRange))
        {
            violations.Add("the Anthropic outbound range is missing");
        }

        if (template.Contains("34.162."))
        {
            violations.Add("a phased-out Anthropic address is present");
        }

        foreach (Match host in HostMatcher().Matches(template))
        {
            if (!host.Groups[1].Value.EndsWith(".example.com", StringComparison.Ordinal))
            {
                violations.Add($"host {host.Groups[1].Value} is not an example.com placeholder");
            }
        }

        foreach (Match address in Ipv4Literal().Matches(template))
        {
            if (address.Value != "160.79.104.0" && !DocumentationRanges.Any(prefix => address.Value.StartsWith(prefix, StringComparison.Ordinal)))
            {
                violations.Add($"address {address.Value} is neither Anthropic's range nor a documentation range");
            }
        }

        if (Regex.Matches(template, Regex.Escape(AnthropicRange)).Count != Occurrences(Block(template, "ledger-mcp-allow", indent: 4), AnthropicRange))
        {
            violations.Add("Anthropic's range is used outside the ledger-mcp-allow middleware");
        }
    }

    private static void AddPolicyViolations(string template, List<string> violations)
    {
        if (Regex.IsMatch(template, "ipStrategy|buffering", RegexOptions.IgnoreCase))
        {
            violations.Add("the template sets a forwarded-header strategy or a buffering middleware");
        }

        var ranges = Regex.Matches(Block(template, "ledger-mcp-allow", indent: 4), "- \"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .Order()
            .ToList();

        if (!ranges.SequenceEqual(new[] { AnthropicRange, "192.0.2.0/24", "198.51.100.0/24" }.Order()))
        {
            violations.Add("the ledger-mcp-allow middleware holds a range other than Anthropic's and the home and VPN ranges");
        }
    }

    private static void AddPublicRouterViolations(string template, List<string> violations)
    {
        var router = Block(template, "ledger-mcp-public", indent: 4);
        var rule = Rule(router);

        if (Matchers(rule).Any(name => name is not ("Host" or "Path")))
        {
            violations.Add("the public MCP router uses a matcher other than Host and Path");
        }

        if (!MatcherArguments(rule, "Path").Order().SequenceEqual(PublicPaths.Order()))
        {
            violations.Add("the public MCP router does not match exactly the five public paths");
        }

        if (!MatcherArguments(rule, "Host").SequenceEqual([McpHost]))
        {
            violations.Add("the public MCP router does not name only the MCP host");
        }

        if (!Middlewares(router).Contains("ledger-mcp-allow"))
        {
            violations.Add("the public MCP router does not use ledger-mcp-allow");
        }
    }

    private static void AddSignInRouterViolations(string template, List<string> violations)
    {
        var router = Block(template, "ledger-mcp-signin", indent: 4);
        var rule = Rule(router);

        if (Matchers(rule).Any(name => name is not ("Host" or "Path" or "PathPrefix")))
        {
            violations.Add("the sign-in router uses an unexpected matcher");
        }

        if (!MatcherArguments(rule, "Path").SequenceEqual(["/connect/authorize"]))
        {
            violations.Add("the sign-in router does not match exactly /connect/authorize");
        }

        if (!MatcherArguments(rule, "PathPrefix").SequenceEqual(["/account/"]))
        {
            violations.Add("the sign-in router does not match exactly the /account/ prefix");
        }

        var middlewares = Middlewares(router);

        if (!middlewares.Contains("ledger-lan-vpn-only") || middlewares.Contains("ledger-mcp-allow"))
        {
            violations.Add("the sign-in router is not limited to the home and VPN allowlist");
        }
    }

    private static void AddOtherRouterViolations(string template, List<string> violations)
    {
        foreach (var name in RouterNames(template))
        {
            var router = Block(template, name, indent: 4);
            var rule = Rule(router);

            if (name is "ledger-mcp-public" or "ledger-mcp-signin")
            {
                continue;
            }

            if (rule.Contains(McpHost) && MatcherArguments(rule, "PathPrefix").Count > 0)
            {
                violations.Add($"router {name} uses a path prefix on the MCP host");
            }

            if (rule.Contains(McpHost))
            {
                violations.Add($"router {name} serves the MCP host");
            }

            if (rule.Contains("/mcp") || rule.Contains("/connect/") || rule.Contains("/account/"))
            {
                violations.Add($"router {name} carries an MCP or sign-in path");
            }

            if (!Middlewares(router).Contains("ledger-lan-vpn-only") || Middlewares(router).Contains("ledger-mcp-allow"))
            {
                violations.Add($"router {name} is not limited to the home and VPN allowlist");
            }
        }
    }

    private static int Occurrences(string text, string value) => Regex.Matches(text, Regex.Escape(value)).Count;

    private static IReadOnlyList<string> RouterNames(string template)
    {
        var routers = Block(template, "routers", indent: 2);

        return Regex.Matches(routers, @"^    ([A-Za-z0-9-]+):\s*$", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToList();
    }

    private static string Block(string template, string key, int indent)
    {
        var lines = template.Replace("\r\n", "\n").Split('\n');
        var header = new string(' ', indent) + key + ":";
        var start = Array.FindIndex(lines, line => line.TrimEnd() == header);

        if (start < 0)
        {
            return string.Empty;
        }

        var end = start + 1;
        while (end < lines.Length && (lines[end].Trim().Length == 0 || LeadingSpaces(lines[end]) > indent))
        {
            end++;
        }

        return string.Join('\n', lines[start..end]) + "\n";
    }

    private static int LeadingSpaces(string line) => line.Length - line.TrimStart(' ').Length;

    private static string Rule(string router)
    {
        var match = Regex.Match(router, "^      rule: \"(.*)\"\\s*$", RegexOptions.Multiline);

        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static List<string> Middlewares(string router)
    {
        var section = Regex.Match(router, @"^      middlewares:\n((?:        - .*\n)+)", RegexOptions.Multiline);

        return section.Success
            ? section.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim().TrimStart('-').Trim()).ToList()
            : [];
    }

    private static List<string> Matchers(string rule) =>
        Regex.Matches(rule, @"([A-Za-z]+)\(`").Select(match => match.Groups[1].Value).ToList();

    private static List<string> MatcherArguments(string rule, string matcher) =>
        Regex.Matches(rule, @"(?<![A-Za-z])" + matcher + @"\(`([^`]*)`\)").Select(match => match.Groups[1].Value).ToList();

    private static string ReadTemplate() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "deploy", "traefik", "ledger.yml.example"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ledger.Service", "Ledger.Service.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"Host\(`([^`]+)`\)")]
    private static partial Regex HostMatcher();

    [GeneratedRegex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b")]
    private static partial Regex Ipv4Literal();
}
