using System.Text.Json;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.Service.Ingestion;

namespace Ledger.UnitTests.Configuration;

/// <summary>Proves every committed appsettings*.json parses and never carries a non-empty secret-shaped value.</summary>
public class CommittedConfigurationTests
{
    private static readonly string[] SecretMarkers = ["password", "secret", "token", "apikey"];

    [Fact]
    [Trait("Category", "Configuration")]
    public void Every_committed_appsettings_file_parses_and_has_no_non_empty_secret_values()
    {
        var serviceDirectory = FindLedgerServiceDirectory();
        var appsettingsFiles = Directory.GetFiles(serviceDirectory, "appsettings*.json", SearchOption.TopDirectoryOnly);

        appsettingsFiles.Should().NotBeEmpty();

        foreach (var path in appsettingsFiles)
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);

            var violations = new List<string>();
            CollectSecretViolations(document.RootElement, string.Empty, violations);

            violations.Should().BeEmpty($"file {Path.GetFileName(path)} must not carry a non-empty secret value");
        }
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

    private static void CollectSecretViolations(JsonElement element, string path, List<string> violations)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = path.Length == 0 ? property.Name : $"{path}:{property.Name}";

                    if (LooksLikeSecretKey(property.Name) && HasNonEmptyValue(property.Value))
                    {
                        violations.Add(propertyPath);
                    }

                    CollectSecretViolations(property.Value, propertyPath, violations);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    CollectSecretViolations(item, $"{path}[{index}]", violations);
                    index++;
                }

                break;
        }
    }

    private static bool LooksLikeSecretKey(string keyName) =>
        SecretMarkers.Any(marker => keyName.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool HasNonEmptyValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
            JsonValueKind.Null => false,
            _ => true
        };

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
