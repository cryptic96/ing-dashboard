using Npgsql;

namespace Ledger.Repository;

/// <summary>Validates that a connection string is a socket-only, passwordless connection scoped to the runtime role.</summary>
public static class LedgerConnectionStringRules
{
    private const string RequiredUsername = "ledger_runtime";

    /// <summary>Returns the name of every rule this connection string violates. Never includes the string's own values.</summary>
    public static IReadOnlyList<string> Problems(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return ["connection string is missing"];
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception)
        {
            return ["connection string could not be parsed"];
        }

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(builder.Host) || !builder.Host.StartsWith('/'))
        {
            problems.Add("host must be an absolute Unix socket directory");
        }

        if (!string.IsNullOrEmpty(builder.Password))
        {
            problems.Add("must not include a password");
        }

        if (!string.Equals(builder.Username, RequiredUsername, StringComparison.Ordinal))
        {
            problems.Add($"username must be {RequiredUsername}");
        }

        if (builder.IncludeErrorDetail)
        {
            problems.Add("must not enable Include Error Detail");
        }

        return problems;
    }
}
