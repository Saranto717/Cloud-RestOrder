using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace RestaurantOrders.Cloud.Api;

public sealed class CloudLicenseDatabase(NpgsqlDataSource dataSource)
{
    public async Task CreateLicenseManagerSessionAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_license_manager_sessions(token_sha256,expires_at_utc)
            VALUES(@hash,now()+interval '10 minutes')
            """, connection);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, tokenHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DateTimeOffset?> GetLicenseManagerSessionAsync(byte[] tokenHash, bool refresh, CancellationToken cancellationToken)
    {
        var sql = refresh ? """
            UPDATE cloud_license_manager_sessions
            SET last_activity_at_utc=now(),expires_at_utc=now()+interval '10 minutes'
            WHERE token_sha256=@hash AND revoked_at_utc IS NULL
              AND expires_at_utc>now() AND last_activity_at_utc>now()-interval '10 minutes'
            RETURNING expires_at_utc
            """ : """
            SELECT expires_at_utc FROM cloud_license_manager_sessions
            WHERE token_sha256=@hash AND revoked_at_utc IS NULL
              AND expires_at_utc>now() AND last_activity_at_utc>now()-interval '10 minutes'
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, tokenHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? reader.GetFieldValue<DateTimeOffset>(0) : null;
    }

    public async Task RevokeLicenseManagerSessionAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_license_manager_sessions SET revoked_at_utc=now()
            WHERE token_sha256=@hash AND revoked_at_utc IS NULL
            """, connection);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, tokenHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RecordLicenseRequestNonceAsync(string installationId, string nonce, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(installationId, out var parsedInstallationId)) return false;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_license_request_nonces(installation_id, nonce)
            VALUES(@installation_id, @nonce)
            ON CONFLICT DO NOTHING
            RETURNING installation_id
            """, connection);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, parsedInstallationId);
        command.Parameters.AddWithValue("nonce", NpgsqlDbType.Varchar, nonce);
        var inserted = await command.ExecuteScalarAsync(cancellationToken) is Guid;
        if (inserted)
        {
            await using var cleanup = new NpgsqlCommand("DELETE FROM cloud_license_request_nonces WHERE created_at_utc < now()-interval '10 minutes'", connection);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
        return inserted;
    }

    public async Task<bool> RegisterInstallationAsync(
        Guid installationId, string machineName, string macAddress, byte[] requestPublicKeyHash, CancellationToken cancellationToken)
    {
        var macHash = HashMac(macAddress);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_license_installations (installation_id, machine_name, mac_address_sha256, request_public_key_sha256)
            VALUES (@installation_id, @machine_name, @mac_hash, @public_key_hash)
            ON CONFLICT (installation_id) DO UPDATE
            SET last_seen_at_utc = now(),
                request_public_key_sha256 = COALESCE(cloud_license_installations.request_public_key_sha256, EXCLUDED.request_public_key_sha256)
            WHERE cloud_license_installations.machine_name = EXCLUDED.machine_name
              AND cloud_license_installations.mac_address_sha256 = EXCLUDED.mac_address_sha256
              AND (cloud_license_installations.request_public_key_sha256 IS NULL OR
                   cloud_license_installations.request_public_key_sha256 = EXCLUDED.request_public_key_sha256)
            RETURNING installation_id
            """, connection);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
        command.Parameters.AddWithValue("machine_name", NpgsqlDbType.Varchar, machineName);
        command.Parameters.AddWithValue("mac_hash", NpgsqlDbType.Bytea, macHash);
        command.Parameters.AddWithValue("public_key_hash", NpgsqlDbType.Bytea, requestPublicKeyHash);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid;
    }

    public async Task<LicenseActivationInfo?> GetActivationInfoAsync(Guid installationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT i.license_id, i.is_assigned,
                   c.shop_name, c.vat_number,
                   CASE WHEN c.is_active THEN l.status ELSE 'revoked' END, l.expires_at_utc
            FROM cloud_license_installations i
            LEFT JOIN cloud_licenses l ON l.id = i.license_id
            LEFT JOIN cloud_license_customers c ON c.id = l.customer_id
            WHERE i.installation_id = @installation_id
            """, connection);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new LicenseActivationInfo(
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? "" : reader.GetString(2),
            reader.IsDBNull(3) ? "" : reader.GetString(3),
            reader.IsDBNull(4) ? "" : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5));
    }

    public async Task<Guid> CreateLicenseAsync(
        string customerName, string vatNumber, DateTime? expiresAtUtc, int maxInstallations, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_licenses (id, customer_name, vat_number, expires_at_utc, max_installations)
            VALUES (@id, @customer_name, @vat_number, @expires_at_utc, @max_installations)
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
        command.Parameters.AddWithValue("customer_name", NpgsqlDbType.Varchar, customerName);
        command.Parameters.AddWithValue("vat_number", NpgsqlDbType.Varchar, vatNumber);
        AddTimestamp(command, "expires_at_utc", expiresAtUtc);
        command.Parameters.AddWithValue("max_installations", NpgsqlDbType.Integer, maxInstallations);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AddAuditAsync(id, null, "created", cancellationToken);
        return id;
    }

    public async Task<bool> RenewLicenseAsync(Guid licenseId, DateTime? expiresAtUtc, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_licenses
            SET expires_at_utc = @expires_at_utc, status = 'active', updated_at_utc = now()
            WHERE id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, licenseId);
        AddTimestamp(command, "expires_at_utc", expiresAtUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await AddAuditAsync(connection, transaction, licenseId, null, "renewed", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RevokeLicenseAsync(Guid licenseId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_licenses SET status = 'revoked', updated_at_utc = now() WHERE id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, licenseId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await AddAuditAsync(connection, transaction, licenseId, null, "revoked", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<AssignInstallationResult> AssignInstallationAsync(
        Guid installationId, Guid licenseId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var lockLicense = new NpgsqlCommand("""
            SELECT max_installations, status, expires_at_utc
            FROM cloud_licenses WHERE id = @license_id FOR UPDATE
            """, connection, transaction);
        lockLicense.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, licenseId);
        int maxInstallations;
        await using (var reader = await lockLicense.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return AssignInstallationResult.LicenseNotFound;
            maxInstallations = reader.GetInt32(0);
            var status = reader.GetString(1);
            DateTime? expiresAtUtc = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
            if (status != "active" || (expiresAtUtc is not null && expiresAtUtc <= DateTime.UtcNow))
                return AssignInstallationResult.LicenseInactive;
        }

        await using var lockInstallation = new NpgsqlCommand("""
            SELECT installation_id FROM cloud_license_installations WHERE installation_id = @installation_id FOR UPDATE
            """, connection, transaction);
        lockInstallation.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
        if (await lockInstallation.ExecuteScalarAsync(cancellationToken) is not Guid)
            return AssignInstallationResult.InstallationNotFound;

        await using var countCommand = new NpgsqlCommand("""
            SELECT count(*) FROM cloud_license_installations
            WHERE license_id = @license_id AND is_assigned = true AND installation_id <> @installation_id
            """, connection, transaction);
        countCommand.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, licenseId);
        countCommand.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
        var assignedCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        if (assignedCount >= maxInstallations) return AssignInstallationResult.InstallationLimitReached;

        await using var assign = new NpgsqlCommand("""
            UPDATE cloud_license_installations
            SET license_id = @license_id, is_assigned = true
            WHERE installation_id = @installation_id
            """, connection, transaction);
        assign.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, licenseId);
        assign.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
        await assign.ExecuteNonQueryAsync(cancellationToken);
        await AddAuditAsync(connection, transaction, licenseId, installationId, "assigned", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AssignInstallationResult.Success;
    }

    public async Task<bool> DetachInstallationAsync(Guid installationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid? licenseId;
        await using (var update = new NpgsqlCommand("""
            UPDATE cloud_license_installations SET is_assigned = false
            WHERE installation_id = @installation_id AND is_assigned = true
            RETURNING license_id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
            var value = await update.ExecuteScalarAsync(cancellationToken);
            if (value is not Guid id) return false;
            licenseId = id;
        }
        await AddAuditAsync(connection, transaction, licenseId, installationId, "detached", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<(IReadOnlyList<ManagedLicenseView> Licenses, IReadOnlyList<ManagedInstallationView> Installations)> GetDashboardAsync(
        CancellationToken cancellationToken)
    {
        var licenses = new List<ManagedLicenseView>();
        var installations = new List<ManagedInstallationView>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            SELECT l.id, l.customer_name, l.vat_number, l.status, l.expires_at_utc,
                   l.max_installations, count(i.installation_id) FILTER (WHERE i.is_assigned), l.created_at_utc
            FROM cloud_licenses l
            LEFT JOIN cloud_license_installations i ON i.license_id = l.id
            GROUP BY l.id
            ORDER BY l.created_at_utc DESC
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                licenses.Add(new ManagedLicenseView(reader.GetGuid(0), Guid.Empty, "", reader.GetString(1), "",
                    reader.GetString(2), null, null, reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                    reader.GetInt32(5), Convert.ToInt32(reader.GetInt64(6)), reader.GetDateTime(7), false, null));
        }

        await using (var command = new NpgsqlCommand("""
            SELECT i.installation_id, i.machine_name, i.first_seen_at_utc, i.last_seen_at_utc,
                   i.license_id, l.customer_name, l.status, i.is_assigned
            FROM cloud_license_installations i
            LEFT JOIN cloud_licenses l ON l.id = i.license_id
            ORDER BY i.is_assigned ASC, i.last_seen_at_utc DESC
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                installations.Add(new ManagedInstallationView(reader.GetGuid(0), reader.GetString(1),
                    reader.GetDateTime(2), reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), null,
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetBoolean(7), false));
        }

        return (licenses, installations);
    }

    private async Task AddAuditAsync(Guid? licenseId, Guid? installationId, string action, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await AddAuditAsync(connection, null, licenseId, installationId, action, cancellationToken);
    }

    private static async Task AddAuditAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid? licenseId, Guid? installationId, string action, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_license_audit (license_id, installation_id, action)
            VALUES (@license_id, @installation_id, @action)
            """, connection, transaction);
        command.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, licenseId is null ? DBNull.Value : licenseId.Value);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId is null ? DBNull.Value : installationId.Value);
        command.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddTimestamp(NpgsqlCommand command, string name, DateTime? value) =>
        command.Parameters.AddWithValue(name, NpgsqlDbType.TimestampTz, value is null ? DBNull.Value : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static byte[] HashMac(string macAddress)
    {
        var normalized = new string(macAddress.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        return SHA256.HashData(Encoding.ASCII.GetBytes(normalized));
    }
}

public sealed record LicenseActivationInfo(Guid? LicenseId, bool IsAssigned, string CustomerName,
    string VatNumber, string Status, DateTime? ExpiresAtUtc);

public enum AssignInstallationResult
{
    Success,
    LicenseNotFound,
    LicenseInactive,
    InstallationNotFound,
    InstallationLimitReached
}
