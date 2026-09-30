namespace Ledger.Dashboards.Model;

/// <summary>A Grafana dashboard document, serialised in declaration order.</summary>
public sealed record Dashboard(
    string Uid,
    string Title,
    IReadOnlyList<string> Tags,
    bool Editable,
    int SchemaVersion,
    string Timezone,
    TimeRange Time,
    IReadOnlyList<DashboardLink> Links,
    Templating Templating,
    IReadOnlyList<Panel> Panels);

/// <summary>A link shown in the dashboard header.</summary>
public sealed record DashboardLink(string Title, string Type, string Url);

/// <summary>The default time range of a dashboard.</summary>
public sealed record TimeRange(string From, string To);

/// <summary>The template variables of a dashboard.</summary>
public sealed record Templating(IReadOnlyList<QueryVariable> List);

/// <summary>A template variable whose options come from a query on a datasource.</summary>
public sealed record QueryVariable(
    string Name,
    string Label,
    string Type,
    DatasourceRef Datasource,
    string Query,
    int Refresh,
    bool Multi,
    bool IncludeAll,
    VariableSelection Current);

/// <summary>The option a variable has selected by default.</summary>
public sealed record VariableSelection(string Text, string Value);

/// <summary>A reference to a provisioned datasource by type and uid.</summary>
public sealed record DatasourceRef(string Type, string Uid);
