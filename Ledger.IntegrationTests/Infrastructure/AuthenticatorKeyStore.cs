using Npgsql;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>Reads and overwrites the stored authenticator secret of a login directly in the database, bypassing the login store.</summary>
public static class AuthenticatorKeyStore
{
    private const string TokenName = "AuthenticatorKey";

    /// <summary>The value stored for the login's authenticator secret, exactly as it sits in the database, or null when there is none.</summary>
    public static async Task<string?> ReadRawAsync(string connectionString, string userName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT t.value FROM identity_user_tokens t JOIN identity_users u ON u.id = t.user_id WHERE u.user_name = @name AND t.name = @token";
        command.Parameters.AddWithValue("name", userName);
        command.Parameters.AddWithValue("token", TokenName);

        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        return value as string;
    }

    /// <summary>Replaces the value stored for the login's authenticator secret, and returns how many rows changed.</summary>
    public static async Task<int> WriteRawAsync(string connectionString, string userName, string value)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE identity_user_tokens t SET value = @value FROM identity_users u WHERE u.id = t.user_id AND u.user_name = @name AND t.name = @token";
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("name", userName);
        command.Parameters.AddWithValue("token", TokenName);

        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Deletes the row that holds the login's authenticator secret, and returns how many rows were removed.</summary>
    public static async Task<int> DeleteRawAsync(string connectionString, string userName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM identity_user_tokens t USING identity_users u WHERE u.id = t.user_id AND u.user_name = @name AND t.name = @token";
        command.Parameters.AddWithValue("name", userName);
        command.Parameters.AddWithValue("token", TokenName);

        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The names of every token row stored for the login.</summary>
    public static async Task<IReadOnlyList<string>> TokenNamesAsync(string connectionString, string userName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT t.name FROM identity_user_tokens t JOIN identity_users u ON u.id = t.user_id WHERE u.user_name = @name";
        command.Parameters.AddWithValue("name", userName);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var names = new List<string>();

        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
