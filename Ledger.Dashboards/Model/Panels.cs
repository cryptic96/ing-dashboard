using System.Text.Json.Serialization;

namespace Ledger.Dashboards.Model;

/// <summary>
/// Common shape of every panel. Panels are serialised by their runtime type, so no type discriminator is emitted.
/// </summary>
[JsonDerivedType(typeof(TablePanel))]
public abstract record Panel(
    [property: JsonPropertyOrder(-5)] int Id,
    [property: JsonPropertyOrder(-4)] string Type,
    [property: JsonPropertyOrder(-3)] string Title,
    [property: JsonPropertyOrder(-2)] string? Description,
    [property: JsonPropertyOrder(-1)] GridPos GridPos);

/// <summary>A table panel fed by one or more SQL targets.</summary>
public sealed record TablePanel(
    int Id,
    string Title,
    string? Description,
    GridPos GridPos,
    DatasourceRef Datasource,
    IReadOnlyList<SqlTarget> Targets,
    FieldConfig FieldConfig,
    IReadOnlyList<Transformation> Transformations)
    : Panel(Id, "table", Title, Description, GridPos);

/// <summary>Position and size of a panel on the 24 column dashboard grid.</summary>
public sealed record GridPos(int H, int W, int X, int Y);

/// <summary>A raw SQL query against a SQL datasource.</summary>
public sealed record SqlTarget(
    DatasourceRef Datasource,
    string EditorMode,
    string Format,
    bool RawQuery,
    string RawSql,
    string RefId);

/// <summary>A panel data transformation, for example renaming columns.</summary>
public sealed record Transformation(string Id, TransformationOptions Options);

/// <summary>Options of the organize transformation.</summary>
public sealed record TransformationOptions(IReadOnlyDictionary<string, string> RenameByName);
