namespace Ledger.Dashboards.Model;

/// <summary>Display configuration applied to the fields of a panel.</summary>
public sealed record FieldConfig(FieldDefaults Defaults, IReadOnlyList<FieldOverride> Overrides);

/// <summary>Configuration applied to every field of a panel.</summary>
public sealed record FieldDefaults(int? Decimals);

/// <summary>Configuration applied to the fields that match a matcher.</summary>
public sealed record FieldOverride(FieldMatcher Matcher, IReadOnlyList<OverrideProperty> Properties);

/// <summary>Selects fields, for example by their display name.</summary>
public sealed record FieldMatcher(string Id, string Options);

/// <summary>One property set by an override. The value is serialised by its runtime type.</summary>
public sealed record OverrideProperty(string Id, object Value);

/// <summary>A mapping that shows chosen text and colour for raw values.</summary>
public sealed record ValueMapping(string Type, IReadOnlyDictionary<string, ValueMappingOption> Options);

/// <summary>The text and optional colour shown for one raw value.</summary>
public sealed record ValueMappingOption(string Text, string? Color, int Index);

/// <summary>How a table cell is rendered.</summary>
public sealed record CellOptions(string Type);
