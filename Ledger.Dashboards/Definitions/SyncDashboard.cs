using Ledger.Dashboards.Model;

namespace Ledger.Dashboards.Definitions;

/// <summary>Defines the bank sync dashboard once; it is built for each language from the translation file.</summary>
public static class SyncDashboard
{
    private const string AccountVariable = "account";

    private const string AccountQuery =
        "SELECT display_name AS __text, account_key AS __value FROM reporting.accounts ORDER BY display_name, account_key";

    private const string RecentTransactionsQuery =
        "SELECT effective_date, account_name, amount, currency, counterparty_name, description, status, match_flag\n"
        + "FROM reporting.transactions\n"
        + "WHERE account_key IN (${account:sqlstring}) AND $__timeFilter(effective_at)\n"
        + "ORDER BY effective_date DESC, first_seen_at DESC, transaction_id DESC\n"
        + "LIMIT 500";

    private static readonly DatasourceRef Reporting = new("grafana-postgresql-datasource", "ledger-reporting");

    /// <summary>The language codes a dashboard is generated for.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["en", "nl"];

    /// <summary>Builds the dashboard for one language.</summary>
    public static Dashboard Build(string language, Translator translator)
    {
        var otherLanguage = Languages.Single(candidate => candidate != language);
        string T(string key) => translator.T(language, key);

        return new Dashboard(
            Uid: UidFor(language),
            Title: T("dashboard.title"),
            Tags: ["household-ledger", language],
            Editable: false,
            SchemaVersion: 41,
            Timezone: "Europe/Amsterdam",
            Time: new TimeRange("now-30d", "now"),
            Links: [new DashboardLink(T("link.other_language"), "link", $"/d/{UidFor(otherLanguage)}")],
            Templating: new Templating([
                new QueryVariable(
                    Name: AccountVariable,
                    Label: T("variable.account"),
                    Type: "query",
                    Datasource: Reporting,
                    Query: AccountQuery,
                    Refresh: 1,
                    Multi: true,
                    IncludeAll: true,
                    Current: new VariableSelection("All", "$__all"))
            ]),
            Panels: [RecentTransactions(1, T)]);
    }

    /// <summary>The uid of the dashboard of one language.</summary>
    public static string UidFor(string language) => $"ledger-sync-{language}";

    private static TablePanel RecentTransactions(int id, Func<string, string> t)
    {
        var headers = new (string Column, string Header)[]
        {
            ("effective_date", t("column.date")),
            ("account_name", t("column.account")),
            ("amount", t("column.amount")),
            ("currency", t("column.currency")),
            ("counterparty_name", t("column.counterparty")),
            ("description", t("column.description")),
            ("status", t("column.status")),
            ("match_flag", t("column.match"))
        };

        var statusMapping = new ValueMapping("value", new SortedDictionary<string, ValueMappingOption>(StringComparer.Ordinal)
        {
            ["pending"] = new(t("status.pending"), "orange", 0),
            ["booked"] = new(t("status.booked"), null, 1)
        });

        var matchMapping = new ValueMapping("value", new SortedDictionary<string, ValueMappingOption>(StringComparer.Ordinal)
        {
            ["ambiguous"] = new(t("match.unclear"), "red", 0)
        });

        var coloredText = new CellOptions("color-text");

        return new TablePanel(
            Id: id,
            Title: t("panel.recent.title"),
            Description: t("panel.recent.description"),
            GridPos: new GridPos(H: 16, W: 24, X: 0, Y: 0),
            Datasource: Reporting,
            Targets:
            [
                new SqlTarget(Reporting, "code", "table", true, RecentTransactionsQuery, "A")
            ],
            FieldConfig: new FieldConfig(
                new FieldDefaults(Decimals: 2),
                [
                    new FieldOverride(
                        new FieldMatcher("byName", headers.Single(h => h.Column == "status").Header),
                        [new OverrideProperty("mappings", new[] { statusMapping }), new OverrideProperty("custom.cellOptions", coloredText)]),
                    new FieldOverride(
                        new FieldMatcher("byName", headers.Single(h => h.Column == "match_flag").Header),
                        [new OverrideProperty("mappings", new[] { matchMapping }), new OverrideProperty("custom.cellOptions", coloredText)])
                ]),
            Transformations:
            [
                new Transformation(
                    "organize",
                    new TransformationOptions(new SortedDictionary<string, string>(
                        headers.ToDictionary(h => h.Column, h => h.Header),
                        StringComparer.Ordinal)))
            ]);
    }
}
