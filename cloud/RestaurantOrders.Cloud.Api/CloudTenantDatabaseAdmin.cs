using Npgsql;

namespace RestaurantOrders.Cloud.Api;

/// <summary>Creates one isolated PostgreSQL database for each active cloud store.</summary>
public sealed class CloudTenantDatabaseAdmin(string adminConnectionString, string databaseOwner)
{
    public async Task EnsureStoreDatabaseAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var name = CloudTenantDatabaseName.ForStore(storeId);
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection))
        {
            exists.Parameters.AddWithValue("name", name);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null) return;
        }

        // The database identifier is formed from a fixed prefix and a GUID.
        // CREATE DATABASE cannot run in a transaction or use parameters for names.
        var owner = databaseOwner.Replace("\"", "\"\"");
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" OWNER \"{owner}\"", connection);
        try
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            // A concurrent request or background worker created it first.
        }
    }
}

public static class CloudTenantDatabaseName
{
    public static string ForStore(Guid storeId) => $"restaurant_store_{storeId:N}";
}
