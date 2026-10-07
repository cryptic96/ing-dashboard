using System.Globalization;
using System.Text.RegularExpressions;
using Ledger.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>Provisions a throwaway database against the user's own local PostgreSQL container for one test collection.</summary>
public partial class DatabaseFixture : IAsyncLifetime
{
    /// <summary>The age a throwaway database must exceed before a later run may sweep it away, far longer than any test run.</summary>
    public static readonly TimeSpan StaleDatabaseAge = TimeSpan.FromHours(2);

    private const string DatabaseTimestampFormat = "yyyyMMddHHmmss";

    private const string AdminConnectionStringEnvironmentVariable = "ConnectionStrings__TestAdmin";
    private const string AdminConnectionStringConfigurationKey = "ConnectionStrings:TestAdmin";

    /// <summary>Seconds a throwaway database drop may take; a forced drop waits for a checkpoint, which is slow on a busy server.</summary>
    private const int DropCommandTimeoutSeconds = 300;

    private readonly List<string> _createdDatabases = [];
    private string _adminConnectionString = string.Empty;
    private string _repositoryRoot = string.Empty;

    /// <summary>The primary throwaway database for this collection, already bootstrapped and migrated.</summary>
    public string DatabaseName { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _adminConnectionString = ResolveAdminConnectionString();
        _repositoryRoot = FindRepositoryRoot();

        try
        {
            await RunBootstrapRolesAsync();
            await TrySweepStaleDatabasesAsync();

            DatabaseName = await CreateBootstrappedDatabaseAsync();
            await MigrateAsync(DatabaseName);
        }
        catch
        {
            await DropCreatedDatabasesAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var failures = await DropCreatedDatabasesAsync();

        if (failures.Count > 0)
        {
            throw new AggregateException("Dropping one or more throwaway databases failed; the others were still dropped.", failures);
        }
    }

    /// <summary>
    /// Drops throwaway databases that an aborted run left behind. Only a database whose name matches the throwaway pattern,
    /// whose embedded creation time is older than the minimum age and that has no open connection is dropped; every other
    /// database is left alone, including one a concurrently running test run is using.
    /// </summary>
    /// <returns>The names that were dropped.</returns>
    public async Task<IReadOnlyList<string>> SweepStaleDatabasesAsync(DateTimeOffset now, TimeSpan minimumAge)
    {
        var candidates = new List<string>();

        await using (var connection = new NpgsqlConnection(AdminConnectionStringForDatabase("postgres")))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT d.datname FROM pg_database d
                WHERE d.datname ~ '^ledger_it_[0-9]{14}_[0-9a-f]{16}$'
                  AND NOT EXISTS (SELECT 1 FROM pg_stat_activity a WHERE a.datname = d.datname)
                """;

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                candidates.Add(reader.GetString(0));
            }
        }

        var dropped = new List<string>();

        foreach (var name in candidates.Where(name => CreatedAt(name) is { } created && now - created > minimumAge))
        {
            try
            {
                await using var connection = new NpgsqlConnection(AdminConnectionStringForDatabase("postgres"));
                await connection.OpenAsync();

                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP DATABASE IF EXISTS \"{name}\"";
                command.CommandTimeout = DropCommandTimeoutSeconds;
                await command.ExecuteNonQueryAsync();
                dropped.Add(name);
            }
            catch (PostgresException)
            {
            }
        }

        return dropped;
    }

    /// <summary>Builds the name of a new throwaway database, with its creation time embedded so a later run can judge its age.</summary>
    public static string NewDatabaseName(DateTimeOffset now) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"ledger_it_{now.UtcDateTime.ToString(DatabaseTimestampFormat, CultureInfo.InvariantCulture)}_{Guid.NewGuid().ToString("N")[..16]}");

    /// <summary>Builds an admin connection string against a database of this server, without switching role.</summary>
    public string AdminConnectionStringFor(string databaseName) => AdminConnectionStringForDatabase(databaseName);

    /// <summary>Remembers a database created by a test so it is dropped with the others when the collection ends.</summary>
    public void RegisterForCleanup(string databaseName)
    {
        if (!ThrowawayDatabaseName().IsMatch(databaseName))
        {
            throw new ArgumentException("Only a throwaway database name can be registered for cleanup.", nameof(databaseName));
        }

        _createdDatabases.Add(databaseName);
    }

    private static DateTimeOffset? CreatedAt(string databaseName)
    {
        var match = ThrowawayDatabaseName().Match(databaseName);

        return match.Success
            && DateTimeOffset.TryParseExact(
                match.Groups["created"].Value,
                DatabaseTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var created)
            ? created
            : null;
    }

    private async Task TrySweepStaleDatabasesAsync()
    {
        try
        {
            await SweepStaleDatabasesAsync(DateTimeOffset.UtcNow, StaleDatabaseAge);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
        }
    }

    private async Task<List<Exception>> DropCreatedDatabasesAsync()
    {
        var failures = new List<Exception>();
        foreach (var databaseName in _createdDatabases)
        {
            try
            {
                await DropDatabaseAsync(databaseName);
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    /// <summary>Builds a connection string against the primary database, connecting as the admin and switching to the given role.</summary>
    public string ConnectionStringFor(string role) => ConnectionStringForDatabase(DatabaseName, role);

    /// <summary>Builds a connection string against the given database, connecting as the admin and switching to the given role.</summary>
    public string ConnectionStringForDatabase(string databaseName, string role)
    {
        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = databaseName,
            Options = $"-c role={role}"
        };

        return builder.ConnectionString;
    }

    /// <summary>Creates a new throwaway database, bootstrapped through the same SQL the LXC uses, but not migrated.</summary>
    public async Task<string> CreateBootstrappedDatabaseAsync()
    {
        var databaseName = NewDatabaseName(DateTimeOffset.UtcNow);
        _createdDatabases.Add(databaseName);

        await using (var connection = new NpgsqlConnection(_adminConnectionString))
        {
            await connection.OpenAsync();

            await using var createCommand = connection.CreateCommand();
            createCommand.CommandText = $"CREATE DATABASE {databaseName} OWNER ledger_migrator";
            await createCommand.ExecuteNonQueryAsync();
        }

        var bootstrapDatabaseSql = await File.ReadAllTextAsync(
            Path.Combine(_repositoryRoot, "deploy", "sql", "bootstrap-database.sql"));

        await using var databaseConnection = new NpgsqlConnection(AdminConnectionStringForDatabase(databaseName));
        await databaseConnection.OpenAsync();

        await using (var bootstrapCommand = databaseConnection.CreateCommand())
        {
            bootstrapCommand.CommandText = bootstrapDatabaseSql;
            await bootstrapCommand.ExecuteNonQueryAsync();
        }

        NpgsqlConnection.ClearPool(databaseConnection);

        return databaseName;
    }

    /// <summary>Applies every migration to the given database, connected as the migrator role.</summary>
    public async Task MigrateAsync(string databaseName)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LedgerDbContext>();
        optionsBuilder.UseNpgsql(ConnectionStringForDatabase(databaseName, "ledger_migrator"));

        await using (var context = new LedgerDbContext(optionsBuilder.Options))
        {
            await context.Database.MigrateAsync();
        }

        await using var migratorConnection = new NpgsqlConnection(ConnectionStringForDatabase(databaseName, "ledger_migrator"));
        NpgsqlConnection.ClearPool(migratorConnection);
    }

    private async Task RunBootstrapRolesAsync()
    {
        var bootstrapRolesSql = await File.ReadAllTextAsync(
            Path.Combine(_repositoryRoot, "deploy", "sql", "bootstrap-roles.sql"));

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = bootstrapRolesSql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionStringForDatabase("postgres"));
        await connection.OpenAsync();

        await using var terminateCommand = connection.CreateCommand();
        terminateCommand.CommandText =
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @databaseName AND pid <> pg_backend_pid()";
        terminateCommand.Parameters.AddWithValue("databaseName", databaseName);
        terminateCommand.CommandTimeout = DropCommandTimeoutSeconds;
        await terminateCommand.ExecuteNonQueryAsync();

        await using var dropCommand = connection.CreateCommand();
        dropCommand.CommandText = $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE)";
        dropCommand.CommandTimeout = DropCommandTimeoutSeconds;
        await dropCommand.ExecuteNonQueryAsync();
    }

    private string AdminConnectionStringForDatabase(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = databaseName };
        return builder.ConnectionString;
    }

    [GeneratedRegex("^ledger_it_(?<created>[0-9]{14})_[0-9a-f]{16}$")]
    private static partial Regex ThrowawayDatabaseName();

    private static string ResolveAdminConnectionString()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(AdminConnectionStringEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(DatabaseFixture).Assembly, optional: true)
            .Build();

        var fromUserSecrets = configuration[AdminConnectionStringConfigurationKey];
        if (!string.IsNullOrWhiteSpace(fromUserSecrets))
        {
            return fromUserSecrets;
        }

        throw new InvalidOperationException(
            $"Missing {AdminConnectionStringConfigurationKey}. Set the {AdminConnectionStringEnvironmentVariable} environment variable or a dotnet user-secrets entry.");
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

/// <summary>Groups every test that needs the shared throwaway database, so xUnit runs them sequentially against it.</summary>
[CollectionDefinition("Database")]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture>;
