using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace RestaurantOrders.Cloud.Api;

public sealed record RegisterSyncDeviceRequest(string? InstallationId, string? DeviceSecret);

public sealed class CloudSyncDeviceDatabase(NpgsqlDataSource dataSource)
{
    public async Task<Guid?> FindActiveStoreIdAsync(Guid installationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT d.store_id
            FROM cloud_sync_devices d
            JOIN cloud_stores s ON s.id=d.store_id
            LEFT JOIN cloud_licenses l ON l.id=s.license_id
            LEFT JOIN cloud_license_customers c ON c.id=l.customer_id
            WHERE d.installation_id=@installation_id AND d.is_active=TRUE AND s.is_active=TRUE
              AND (s.license_id IS NULL OR (l.status='active' AND c.is_active=TRUE
                   AND (l.expires_at_utc IS NULL OR l.expires_at_utc>now())))
            LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid storeId ? storeId : null;
    }

    public async Task<bool> RegisterAsync(Guid storeId, Guid installationId, byte[] signingKeyHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_sync_devices(store_id,installation_id,signing_key_sha256)
            VALUES(@store_id,@installation_id,@key_hash)
            ON CONFLICT(store_id,installation_id) DO UPDATE SET last_seen_at_utc=now()
            WHERE cloud_sync_devices.signing_key_sha256=EXCLUDED.signing_key_sha256
              AND cloud_sync_devices.is_active=TRUE
            RETURNING installation_id
            """, connection);
        command.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
        command.Parameters.AddWithValue("key_hash", NpgsqlDbType.Bytea, signingKeyHash);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid;
    }

    public async Task<bool> ValidateAndRecordAsync(Guid storeId, Guid installationId,
        string method, string pathAndQuery, string timestamp, string nonce,
        byte[] body, string suppliedBodyHash, string suppliedSignature, CancellationToken cancellationToken)
    {
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds) ||
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds) > 300 ||
            nonce.Length is < 24 or > 128 || nonce.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            return false;

        var actualBodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        if (!FixedEquals(actualBodyHash, suppliedBodyHash.ToLowerInvariant())) return false;

        byte[] signingKeyHash;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand("""
            SELECT signing_key_sha256 FROM cloud_sync_devices
            WHERE store_id=@store_id AND installation_id=@installation_id AND is_active=TRUE
            """, connection))
        {
            command.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
            command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not byte[] bytes) return false;
            signingKeyHash = bytes;
        }

        var canonical = $"{method.ToUpperInvariant()}\n{pathAndQuery}\n{timestamp}\n{nonce}\n{actualBodyHash}";
        byte[] supplied;
        try { supplied = FromBase64Url(suppliedSignature); }
        catch (FormatException) { return false; }
        using var hmac = new HMACSHA256(signingKeyHash);
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) return false;

        await using var nonceConnection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await nonceConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO cloud_sync_nonces(store_id,installation_id,nonce)
                VALUES(@store_id,@installation_id,@nonce)
                """, nonceConnection, transaction);
            insert.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
            insert.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
            insert.Parameters.AddWithValue("nonce", NpgsqlDbType.Varchar, nonce);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == "23505")
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE cloud_sync_devices SET last_seen_at_utc=now()
            WHERE store_id=@store_id AND installation_id=@installation_id
            """, nonceConnection, transaction))
        {
            update.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
            update.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var cleanup = new NpgsqlCommand("DELETE FROM cloud_sync_nonces WHERE created_at_utc < now()-interval '10 minutes'", nonceConnection, transaction))
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public static byte[] HashDeviceSecret(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.ASCII.GetBytes(left);
        var b = Encoding.ASCII.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }
}
