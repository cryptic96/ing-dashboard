namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>
/// Sets process environment variables for the lifetime of the instance and puts back exactly what was there before, including
/// the absence of a variable, so a test can never leak a value into the next one or destroy a value the developer already had.
/// </summary>
public sealed class EnvironmentOverride : IDisposable
{
    private readonly List<(string Name, string? Previous)> _previous = [];

    /// <summary>Applies the values; a null value removes the variable while the override is active.</summary>
    public EnvironmentOverride(IEnumerable<KeyValuePair<string, string?>> values)
    {
        foreach (var (name, value) in values)
        {
            _previous.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>Applies one value; a null value removes the variable while the override is active.</summary>
    public EnvironmentOverride(string name, string? value)
        : this([new KeyValuePair<string, string?>(name, value)])
    {
    }

    /// <summary>Restores every variable to the value it had before, in reverse order.</summary>
    public void Dispose()
    {
        for (var index = _previous.Count - 1; index >= 0; index--)
        {
            Environment.SetEnvironmentVariable(_previous[index].Name, _previous[index].Previous);
        }
    }
}
