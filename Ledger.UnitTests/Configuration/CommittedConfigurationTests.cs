using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.Service.Ingestion;

namespace Ledger.UnitTests.Configuration;

/// <summary>
/// Proves the committed configuration parses and never carries a secret: neither under a secret-looking key nor inside a value
/// such as a connection string, a PEM block or a long encoded blob.
/// </summary>
public partial class CommittedConfigurationTests
{
    private const string PemHeaderLine = "-----BEGIN " + "PRIVATE KEY-----";
    private const string PemJson = "{\"Service\":{\"Blob\":\"" + PemHeaderLine + "\"}}";

    [Fact]
    [Trait("Category", "Configuration")]
    public void Every_committed_appsettings_file_parses_and_has_no_non_empty_secret_values()
    {
        var serviceDirectory = FindLedgerServiceDirectory();
        var appsettingsFiles = Directory.GetFiles(serviceDirectory, "appsettings*.json", SearchOption.TopDirectoryOnly);

        appsettingsFiles.Should().NotBeEmpty();

        foreach (var path in appsettingsFiles)
        {
            CommittedSecretScanner.ScanJson(File.ReadAllText(path), checkKeyNames: true)
                .Should().BeEmpty($"file {Path.GetFileName(path)} must not carry a secret");
        }
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void Committed_provisioning_json_and_dashboard_translations_carry_no_secret_shaped_values()
    {
        var root = FindRepositoryRoot();
        var files = Directory.GetFiles(Path.Combine(root, "deploy", "provisioning"), "*.json", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "Ledger.Dashboards", "translations.json"))
            .ToList();

        files.Should().NotBeEmpty();

        foreach (var path in files)
        {
            CommittedSecretScanner.ScanJson(File.ReadAllText(path), checkKeyNames: true)
                .Should().BeEmpty($"file {Path.GetRelativePath(root, path)} must not carry a secret");
        }
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void Committed_example_environment_files_carry_only_placeholders()
    {
        var root = FindRepositoryRoot();
        var files = Directory.GetFiles(Path.Combine(root, "deploy"), "*.example", SearchOption.TopDirectoryOnly);

        files.Should().NotBeEmpty();

        foreach (var path in files)
        {
            CommittedSecretScanner.ScanEnvironmentText(File.ReadAllText(path))
                .Should().BeEmpty($"file {Path.GetRelativePath(root, path)} must stay placeholder-only");
        }
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("""{"ConnectionStrings":{"Ledger":"Host=db.example.com;Database=ledger;Username=app;Password=hunter2"}}""")]
    [InlineData("""{"ConnectionStrings":{"Ledger":"Host=db.example.com;Database=ledger;Username=app;pwd=hunter2;"}}""")]
    [InlineData("""{"Service":{"Url":"https://user:hunter2@example.com/path"}}""")]
    [InlineData("""{"Service":{"Note":"client_secret=abcdef"}}""")]
    [InlineData(PemJson)]
    [InlineData("""{"Service":{"Blob":"QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5YWJjZGVmZ2hpams="}}""")]
    [InlineData("""{"Service":{"ClientSecret":"abc"}}""")]
    [InlineData("""{"Service":{"PrivateKey":"abc"}}""")]
    [InlineData("""{"Service":{"Credentials":"abc"}}""")]
    [InlineData("""{"Outer":{"Inner":[{"DbPassword":"abc"}]}}""")]
    public void The_scanner_flags_secret_shaped_json_and_never_echoes_the_value(string json)
    {
        var violations = CommittedSecretScanner.ScanJson(json, checkKeyNames: true);

        violations.Should().NotBeEmpty();
        violations.Should().NotContain(violation => violation.Contains("hunter2", StringComparison.Ordinal));
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("""{"ConnectionStrings":{"Ledger":"Host=/var/run/postgresql;Database=ledger;Username=ledger_runtime"}}""")]
    [InlineData("""{"ConnectionStrings":{"Ledger":"Host=db.example.com;Database=ledger;Username=app;Password="}}""")]
    [InlineData("""{"ConnectionStrings":{"Ledger":"Host=db.example.com;Password=;Database=ledger"}}""")]
    [InlineData("""{"DataProtection":{"CertificatePassword":"","Token":null}}""")]
    [InlineData("""{"Kestrel":{"Endpoints":{"Api":{"Url":"http://0.0.0.0:5080"}}},"Logging":{"LogLevel":{"Default":"Information"}}}""")]
    public void The_scanner_accepts_empty_credentials_and_plain_settings(string json)
    {
        CommittedSecretScanner.ScanJson(json, checkKeyNames: true).Should().BeEmpty();
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("ConnectionStrings__Ledger=Host=db.example.com;Database=ledger;Password=hunter2")]
    [InlineData("# ConnectionStrings__Ledger=Host=db.example.com;Password=hunter2")]
    [InlineData("SERVICE_URL=https://user:hunter2@example.com/")]
    [InlineData("SERVICE_BLOB=QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5YWJjZGVmZ2hpams=")]
    [InlineData(PemHeaderLine)]
    public void The_scanner_flags_secret_shaped_environment_lines_and_never_echoes_the_value(string text)
    {
        var violations = CommittedSecretScanner.ScanEnvironmentText(text);

        violations.Should().NotBeEmpty();
        violations.Should().NotContain(violation => violation.Contains("hunter2", StringComparison.Ordinal));
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("# Provisioning generates the Data Protection certificate and its password; copy both to the password manager.")]
    [InlineData("DataProtection__CertificatePassword=generated-by-provisioning")]
    [InlineData("# EnableBanking__PrivateKeyPassword=generated-by-ledger-bank-key")]
    [InlineData("GF_SMTP_HOST=smtp-relay.example.com:25")]
    [InlineData("LEDGER_ALERT_EMAIL=operator@example.com")]
    public void The_scanner_accepts_placeholder_environment_lines(string text)
    {
        CommittedSecretScanner.ScanEnvironmentText(text).Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void Outbound_http_client_logging_is_at_warning_so_request_addresses_are_never_logged()
    {
        var path = Path.Combine(FindLedgerServiceDirectory(), "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var level = document.RootElement
            .GetProperty("Logging")
            .GetProperty("LogLevel")
            .GetProperty("System.Net.Http.HttpClient")
            .GetString();

        level.Should().Be("Warning");
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("OpenIddict")]
    [InlineData("ModelContextProtocol")]
    [InlineData("Microsoft.AspNetCore.Identity")]
    public void Authorization_and_sign_in_libraries_are_logged_at_warning_so_no_credential_can_reach_the_log(string category)
    {
        var path = Path.Combine(FindLedgerServiceDirectory(), "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var level = document.RootElement
            .GetProperty("Logging")
            .GetProperty("LogLevel")
            .GetProperty(category)
            .GetString();

        level.Should().Be("Warning");
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void Ingestion_defaults_are_the_values_measured_against_the_real_bank()
    {
        var options = new IngestionOptions();

        options.BackgroundCallsPerDay.Should().Be(12);
        options.QuotaWindow.Should().Be(QuotaWindow.Rolling24Hours);
        options.PsuHeadersOnOperatorSyncs.Should().BeTrue();
        options.ReconcileBalanceKinds.Should().Equal(BalanceKind.ClosingBooked, BalanceKind.InterimBooked, BalanceKind.Expected);
        options.ReconcileUndatedBalances.Should().BeTrue();
        options.MatchWindowDays.Should().Be(5);
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void Ci_and_release_workflows_run_the_whole_suite_with_the_migration_bundle()
    {
        var workflows = Path.Combine(FindRepositoryRoot(), ".github", "workflows");

        foreach (var name in new[] { "ci.yml", "release.yml" })
        {
            WholeSuiteStepViolations(File.ReadAllText(Path.Combine(workflows, name)), requireCiTrue: true)
                .Should().BeEmpty($"workflow {name} must run the whole suite with the migration bundle");
        }
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("dotnet test --solution Ledger.slnx --no-restore", "dotnet test --solution Ledger.slnx --no-restore --filter-not-trait \"Category=Integration\"")]
    [InlineData("dotnet test --solution Ledger.slnx --no-restore", "dotnet test --solution Ledger.slnx --no-restore --filter-trait \"Category=Unit\"")]
    [InlineData("dotnet test --solution Ledger.slnx --no-restore", "dotnet test --project Ledger.UnitTests --no-restore")]
    [InlineData("          LEDGER_EFBUNDLE:", "          LEDGER_EFBUNDLE_OFF:")]
    [InlineData("          CI: true", "          CI: false")]
    [InlineData("          CI: true", "          NOT_CI: true")]
    public void The_whole_suite_workflow_check_rejects_a_weakened_test_step(string original, string weakened)
    {
        var workflows = Path.Combine(FindRepositoryRoot(), ".github", "workflows");
        var yaml = File.ReadAllText(Path.Combine(workflows, "ci.yml"));

        yaml.Should().Contain(original);

        WholeSuiteStepViolations(yaml.Replace(original, weakened), requireCiTrue: true).Should().NotBeEmpty();
    }

    private static List<string> WholeSuiteStepViolations(string workflowYaml, bool requireCiTrue)
    {
        var violations = new List<string>();
        var lines = workflowYaml.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim() == "- name: Run tests");
        if (start < 0)
        {
            return ["no step named Run tests"];
        }

        var step = new List<string> { lines[start] };
        for (var i = start + 1; i < lines.Length && !lines[i].TrimStart().StartsWith("- name:", StringComparison.Ordinal); i++)
        {
            step.Add(lines[i]);
        }

        var runLines = step.Where(line => line.TrimStart().StartsWith("run:", StringComparison.Ordinal)).ToList();
        if (runLines.Count != 1 || runLines[0].Trim() != "run: dotnet test --solution Ledger.slnx --no-restore")
        {
            violations.Add("the test step must run exactly: dotnet test --solution Ledger.slnx --no-restore");
        }

        if (step.Any(line => line.Contains("filter", StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add("the test step must not filter tests");
        }

        if (!step.Any(line => line.Trim().StartsWith("LEDGER_EFBUNDLE:", StringComparison.Ordinal) && line.Trim().Length > "LEDGER_EFBUNDLE:".Length))
        {
            violations.Add("the test step must set a non-empty LEDGER_EFBUNDLE");
        }

        if (requireCiTrue && !step.Any(line => line.Trim() == "CI: true"))
        {
            violations.Add("the test step must set CI to true");
        }

        return violations;
    }

    private static string FindRepositoryRoot() => Path.GetDirectoryName(FindLedgerServiceDirectory())!;

    private static string FindLedgerServiceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Ledger.Service");
            if (File.Exists(Path.Combine(candidate, "Ledger.Service.csproj")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate Ledger.Service above the test output directory.");
    }
}

/// <summary>
/// Finds secret-shaped content in committed configuration. A finding names the place and the kind of secret, never the value.
/// </summary>
public static partial class CommittedSecretScanner
{
    private static readonly string[] SecretKeyMarkers =
    [
        "password", "passwd", "pwd", "secret", "token", "apikey", "privatekey", "credential", "accesskey", "signingkey"
    ];

    /// <summary>Scans every key and string value of a JSON document, optionally also judging non-empty values by their key name.</summary>
    public static IReadOnlyList<string> ScanJson(string json, bool checkKeyNames)
    {
        using var document = JsonDocument.Parse(json);
        var violations = new List<string>();
        Walk(document.RootElement, string.Empty, checkKeyNames, violations);
        return violations;
    }

    /// <summary>Scans KEY=value text, including commented-out assignments, for secret-shaped values.</summary>
    public static IReadOnlyList<string> ScanEnvironmentText(string text)
    {
        var violations = new List<string>();
        var lineNumber = 0;

        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            var assignment = AssignmentLine().Match(line);
            var candidate = assignment.Success ? assignment.Groups["value"].Value : line;

            foreach (var reason in ValueFindings(candidate, includeEmbeddedCredential: assignment.Success))
            {
                violations.Add($"line {lineNumber}: {reason}");
            }
        }

        return violations;
    }

    private static void Walk(JsonElement element, string path, bool checkKeyNames, List<string> violations)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = path.Length == 0 ? property.Name : $"{path}:{property.Name}";

                    if (checkKeyNames && LooksLikeSecretKey(property.Name) && HasNonEmptyValue(property.Value))
                    {
                        violations.Add($"{propertyPath}: a non-empty value under a secret-looking key");
                    }

                    Walk(property.Value, propertyPath, checkKeyNames, violations);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{path}[{index}]", checkKeyNames, violations);
                    index++;
                }

                break;
            case JsonValueKind.String:
                foreach (var reason in ValueFindings(element.GetString() ?? string.Empty, includeEmbeddedCredential: true))
                {
                    violations.Add($"{path}: {reason}");
                }

                break;
        }
    }

    private static IEnumerable<string> ValueFindings(string value, bool includeEmbeddedCredential)
    {
        if (includeEmbeddedCredential && EmbeddedCredential().IsMatch(value))
        {
            yield return "an embedded credential assignment such as a password inside a connection string";
        }

        if (PemHeader().IsMatch(value))
        {
            yield return "a PEM block header";
        }

        if (UrlCredentials().IsMatch(value))
        {
            yield return "credentials inside a URL";
        }

        if (LongEncodedBlob().IsMatch(value))
        {
            yield return "a long base64-like blob";
        }
    }

    private static bool LooksLikeSecretKey(string keyName) =>
        SecretKeyMarkers.Any(marker => keyName.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool HasNonEmptyValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
            JsonValueKind.Null => false,
            _ => true
        };

    [GeneratedRegex(@"(?i)(password|passwd|pwd|secret|client_?secret|token|api_?key|account_?key|shared_?access_?key)\s*=\s*[^\s;'""]")]
    private static partial Regex EmbeddedCredential();

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]+-----")]
    private static partial Regex PemHeader();

    [GeneratedRegex(@"://[^/\s:@]+:[^/\s@]+@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"[A-Za-z0-9+/]{40,}={0,2}")]
    private static partial Regex LongEncodedBlob();

    [GeneratedRegex(@"^\s*#?\s*[A-Za-z_][A-Za-z0-9_]*=(?<value>.*)$")]
    private static partial Regex AssignmentLine();
}
