using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Dashboards.Definitions;

namespace Ledger.Dashboards;

/// <summary>Generates the dashboards of every language deterministically from their definitions and the translation file.</summary>
public sealed class DashboardGenerator(Translator translator)
{
    /// <summary>Repository relative directory that holds the generated dashboards Grafana provisions.</summary>
    public const string OutputDirectory = "deploy/provisioning/grafana/provisioning/dashboards/json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Creates a generator that reads the embedded translation file.</summary>
    public DashboardGenerator() : this(Translator.Load())
    {
    }

    /// <summary>Returns the full JSON text of every dashboard keyed by file name, each ending in a single line feed.</summary>
    public IReadOnlyDictionary<string, string> Generate()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var language in SyncDashboard.Languages)
        {
            var dashboard = SyncDashboard.Build(language, translator);
            files[$"{dashboard.Uid}.json"] = JsonSerializer.Serialize(dashboard, SerializerOptions) + "\n";
        }

        return files;
    }
}
