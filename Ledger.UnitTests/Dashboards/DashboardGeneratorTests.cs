using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Ledger.Dashboards;

namespace Ledger.UnitTests.Dashboards;

/// <summary>Guards the generated dashboards: no drift from the committed files, complete translations, language parity and read-only queries.</summary>
[Trait("Category", "Dashboards")]
public partial class DashboardGeneratorTests
{
    private const string ReportingDatasourceUid = "ledger-reporting";

    private static readonly string[] ExpectedFiles = ["ledger-sync-en.json", "ledger-sync-nl.json"];

    [GeneratedRegex(@"\b(?:FROM|JOIN)\s+([^\s,;()]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RelationReference();

    [Fact]
    public void Committed_files_match_the_generator_output_byte_for_byte()
    {
        var generated = new DashboardGenerator().Generate();
        var directory = Path.Combine(FindRepositoryRoot(), DashboardGenerator.OutputDirectory);

        generated.Keys.Should().BeEquivalentTo(ExpectedFiles);

        foreach (var (name, content) in generated)
        {
            var path = Path.Combine(directory, name);
            File.Exists(path).Should().BeTrue($"{name} must be committed; run dotnet run --project Ledger.Dashboards -- generate");

            var committed = File.ReadAllBytes(path);
            committed.Should().Equal(
                new UTF8Encoding(false).GetBytes(content),
                $"{name} is stale; run dotnet run --project Ledger.Dashboards -- generate");
        }
    }

    [Fact]
    public void Generating_twice_yields_identical_bytes_ending_in_one_line_feed()
    {
        var first = new DashboardGenerator().Generate();
        var second = new DashboardGenerator().Generate();

        second.Should().Equal(first);

        foreach (var content in first.Values)
        {
            content.Should().EndWith("}\n");
            content.Should().NotContain("\r");
        }
    }

    [Fact]
    public void Both_languages_define_the_same_non_empty_translation_keys()
    {
        var languages = Translator.Load().Languages;

        languages.Keys.Should().BeEquivalentTo("en", "nl");
        languages["nl"].Keys.Should().BeEquivalentTo(languages["en"].Keys);

        foreach (var (language, texts) in languages)
        {
            foreach (var (key, text) in texts)
            {
                text.Should().NotBeNullOrWhiteSpace($"{language}:{key} must have text");
            }
        }
    }

    [Fact]
    public void Every_definition_key_exists_and_every_translation_key_is_used()
    {
        var translator = Translator.Load();

        var generate = () => new DashboardGenerator(translator).Generate();

        generate.Should().NotThrow();
        translator.UsedKeys.Should().BeEquivalentTo(translator.Languages["en"].Keys);
    }

    [Fact]
    public void A_missing_translation_key_is_reported_by_name()
    {
        var translator = Translator.Parse("""{"en":{"a":"x"},"nl":{}}""");

        var lookup = () => translator.T("nl", "a");

        lookup.Should().Throw<InvalidOperationException>().WithMessage("*'a'*'nl'*");
    }

    [Fact]
    public void An_empty_translation_is_reported_by_name()
    {
        var translator = Translator.Parse("""{"en":{"a":"x"},"nl":{"a":"  "}}""");

        var lookup = () => translator.T("nl", "a");

        lookup.Should().Throw<InvalidOperationException>().WithMessage("*'a'*'nl'*");
    }

    [Fact]
    public void An_unknown_key_used_by_a_definition_fails_generation()
    {
        var translator = Translator.Parse("""{"en":{"dashboard.title":"x"},"nl":{"dashboard.title":"y"}}""");

        var generate = () => new DashboardGenerator(translator).Generate();

        generate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Languages_differ_only_in_text()
    {
        using var english = Parse("ledger-sync-en.json");
        using var dutch = Parse("ledger-sync-nl.json");

        var englishPanels = english.RootElement.GetProperty("panels").EnumerateArray().ToList();
        var dutchPanels = dutch.RootElement.GetProperty("panels").EnumerateArray().ToList();

        dutchPanels.Should().HaveCount(englishPanels.Count);

        for (var index = 0; index < englishPanels.Count; index++)
        {
            foreach (var property in new[] { "id", "type", "gridPos", "datasource", "targets", "fieldConfig.defaults" })
            {
                Structure(englishPanels[index], property).Should().Be(Structure(dutchPanels[index], property), $"panel {index} {property}");
            }
        }

        var englishVariables = english.RootElement.GetProperty("templating").GetProperty("list").EnumerateArray().ToList();
        var dutchVariables = dutch.RootElement.GetProperty("templating").GetProperty("list").EnumerateArray().ToList();

        dutchVariables.Should().HaveCount(englishVariables.Count);

        for (var index = 0; index < englishVariables.Count; index++)
        {
            foreach (var property in new[] { "name", "type", "datasource", "query", "refresh", "multi", "includeAll" })
            {
                Structure(englishVariables[index], property).Should().Be(Structure(dutchVariables[index], property), $"variable {index} {property}");
            }
        }

        english.RootElement.GetProperty("timezone").GetString().Should().Be(dutch.RootElement.GetProperty("timezone").GetString());
        Structure(english.RootElement, "time").Should().Be(Structure(dutch.RootElement, "time"));
    }

    [Fact]
    public void Dashboards_have_fixed_uids_and_cannot_be_edited_in_the_ui()
    {
        foreach (var (name, uid) in new[] { ("ledger-sync-en.json", "ledger-sync-en"), ("ledger-sync-nl.json", "ledger-sync-nl") })
        {
            using var document = Parse(name);

            document.RootElement.GetProperty("uid").GetString().Should().Be(uid);
            document.RootElement.GetProperty("editable").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public void Every_query_reads_only_reporting_relations_through_the_reporting_datasource()
    {
        foreach (var name in ExpectedFiles)
        {
            using var document = Parse(name);

            var datasources = new List<JsonElement>();
            CollectDatasources(document.RootElement, datasources);
            datasources.Should().NotBeEmpty();
            datasources.Select(datasource => datasource.GetProperty("uid").GetString())
                .Should().OnlyContain(uid => uid == ReportingDatasourceUid, name);

            var queries = Queries(document).ToList();
            queries.Should().NotBeEmpty();

            foreach (var query in queries)
            {
                var relations = RelationReference().Matches(query).Select(match => match.Groups[1].Value).ToList();
                relations.Should().NotBeEmpty(query);
                relations.Should().OnlyContain(relation => relation.StartsWith("reporting.", StringComparison.Ordinal), query);
            }
        }
    }

    /// <summary>Returns every panel target query and template variable query of a dashboard.</summary>
    private static IEnumerable<string> Queries(JsonDocument document)
    {
        foreach (var panel in document.RootElement.GetProperty("panels").EnumerateArray())
        {
            foreach (var target in panel.GetProperty("targets").EnumerateArray())
            {
                yield return target.GetProperty("rawSql").GetString()!;
            }
        }

        foreach (var variable in document.RootElement.GetProperty("templating").GetProperty("list").EnumerateArray())
        {
            yield return variable.GetProperty("query").GetString()!;
        }
    }

    private static void CollectDatasources(JsonElement element, List<JsonElement> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "datasource")
                    {
                        found.Add(property.Value);
                    }
                    else
                    {
                        CollectDatasources(property.Value, found);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectDatasources(item, found);
                }

                break;
        }
    }

    private static string Structure(JsonElement element, string path)
    {
        var current = element;
        foreach (var segment in path.Split('.'))
        {
            current = current.GetProperty(segment);
        }

        return current.GetRawText();
    }

    private static JsonDocument Parse(string fileName)
    {
        var path = Path.Combine(FindRepositoryRoot(), DashboardGenerator.OutputDirectory, fileName);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ledger.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate Ledger.slnx above the test output directory.");
    }
}
