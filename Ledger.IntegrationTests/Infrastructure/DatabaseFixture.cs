using Ledger.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>Provisions a throwaway database against the user's own local PostgreSQL container for one test collection.</summary>
public class DatabaseFixture : IAsyncLifetime
{
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

        await RunBootstrapRolesAsync();

        DatabaseName = await CreateBootstrappedDatabaseAsync();
        await MigrateAsync(DatabaseName);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
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

        if (failures.Count > 0)
        {
            throw new AggregateException("Dropping one or more throwaway databases failed; the others were still dropped.", failures);
        }
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
        var databaseName = $"ledger_it_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(_adminConnectionString))
        {
            await connection.OpenAsync();

            await using var createCommand = connection.CreateCommand();
            createCommand.CommandText = $"CREATE DATABASE {databaseName} OWNER ledger_migrator";
            await createCommand.ExecuteNonQueryAsync();
        }

        _createdDatabases.Add(databaseName);

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
