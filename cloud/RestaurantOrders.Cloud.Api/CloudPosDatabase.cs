using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace RestaurantOrders.Cloud.Api;

public sealed record CloudPosUser(int Id, Guid StoreId, string Email, string Role, bool IsActive);
public sealed record CloudPosSession(Guid StoreId, int UserId, string Email, string Role, DateTimeOffset ExpiresAtUtc);
public sealed record CloudPosCredentials(int Id, Guid StoreId, string Email, string PasswordHash, string Role, bool IsActive);
public sealed record CloudPosEntity(string EntityId, JsonElement Payload);
public sealed record CloudPosBootstrap(
    Guid StoreId,
    IReadOnlyList<CloudPosEntity> Tables,
    IReadOnlyList<CloudPosEntity> Categories,
    IReadOnlyList<CloudPosEntity> MenuItems,
    IReadOnlyList<CloudPosEntity> Orders,
    IReadOnlyList<CloudPosEntity> OrderItems);
public sealed record ManagedCloudStoreCreated(Guid StoreId, string Name, string ApiToken, int AdminUserId, string AdminEmail);

public sealed class CloudPosDatabase(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ManagedCloudStoreCreated> CreateStoreWithAdminAsync(
        Guid licenseId, string name, string email, string passwordHash, CancellationToken cancellationToken)
    {
        var storeId = Guid.NewGuid();
        var token = StoreTokens.Create();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var license = new NpgsqlCommand("""
            SELECT c.shop_name
            FROM cloud_licenses l
            JOIN cloud_license_customers c ON c.id=l.customer_id
            WHERE l.id=@license_id AND l.status='active' AND c.is_active=TRUE
              AND (l.expires_at_utc IS NULL OR l.expires_at_utc>now())
            FOR UPDATE OF l, c
            """, connection, transaction))
        {
            license.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, licenseId);
            if (await license.ExecuteScalarAsync(cancellationToken) is not string)
                throw new CloudPosConflictException("Η άδεια είναι ανενεργή, ληγμένη ή δεν υπάρχει.");
        }

        await using (var store = new NpgsqlCommand("INSERT INTO cloud_stores(id,name,api_key_sha256,license_id) VALUES(@id,@name,@hash,@license_id)", connection, transaction))
        {
            store.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, storeId);
            store.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, name);
            store.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, StoreTokens.Hash(token));
            store.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, licenseId);
            await store.ExecuteNonQueryAsync(cancellationToken);
        }

        int userId;
        await using (var user = new NpgsqlCommand("""
            INSERT INTO cloud_pos_users(store_id,email,password_hash,role)
            VALUES(@store_id,@email,@password_hash,'admin') RETURNING id
            """, connection, transaction))
        {
            user.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
            user.Parameters.AddWithValue("email", NpgsqlDbType.Varchar, email);
            user.Parameters.AddWithValue("password_hash", NpgsqlDbType.Varchar, passwordHash);
            userId = Convert.ToInt32(await user.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        await transaction.CommitAsync(cancellationToken);
        return new ManagedCloudStoreCreated(storeId, name, token, userId, email);
    }

    public async Task<CloudPosCredentials?> FindCredentialsAsync(string email, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT u.id,u.store_id,u.email,u.password_hash,u.role,u.is_active
            FROM cloud_pos_users u JOIN cloud_stores s ON s.id=u.store_id
            LEFT JOIN cloud_licenses l ON l.id=s.license_id
            LEFT JOIN cloud_license_customers c ON c.id=l.customer_id
            WHERE lower(u.email)=lower(@email) AND u.is_active=TRUE AND s.is_active=TRUE
              AND (s.license_id IS NULL OR (l.status='active' AND c.is_active=TRUE
                   AND (l.expires_at_utc IS NULL OR l.expires_at_utc>now())))
            ORDER BY u.created_at_utc LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("email", NpgsqlDbType.Varchar, email);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CloudPosCredentials(reader.GetInt32(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5))
            : null;
    }

    public async Task CreateSessionAsync(byte[] tokenHash, Guid storeId, int userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_pos_sessions(token_sha256,store_id,user_id,expires_at_utc)
            VALUES(@hash,@store_id,@user_id,now()+interval '10 minutes')
            """, connection);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, tokenHash);
        command.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Integer, userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CloudPosSession?> GetSessionAsync(byte[] tokenHash, bool refresh, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var sql = refresh ? """
            UPDATE cloud_pos_sessions s
            SET last_activity_at_utc=now(), expires_at_utc=now()+interval '10 minutes'
            FROM cloud_pos_users u, cloud_stores st
            LEFT JOIN cloud_licenses l ON l.id=st.license_id
            LEFT JOIN cloud_license_customers c ON c.id=l.customer_id
            WHERE s.token_sha256=@hash AND s.user_id=u.id AND s.store_id=st.id
              AND u.is_active=TRUE AND st.is_active=TRUE AND s.revoked_at_utc IS NULL
              AND (st.license_id IS NULL OR (l.status='active' AND c.is_active=TRUE
                   AND (l.expires_at_utc IS NULL OR l.expires_at_utc>now())))
              AND s.expires_at_utc>now() AND s.last_activity_at_utc>now()-interval '10 minutes'
            RETURNING s.store_id,s.user_id,u.email,u.role,s.expires_at_utc
            """ : """
            SELECT s.store_id,s.user_id,u.email,u.role,s.expires_at_utc
            FROM cloud_pos_sessions s JOIN cloud_pos_users u ON u.id=s.user_id
            JOIN cloud_stores st ON st.id=s.store_id
            LEFT JOIN cloud_licenses l ON l.id=st.license_id
            LEFT JOIN cloud_license_customers c ON c.id=l.customer_id
            WHERE s.token_sha256=@hash AND u.is_active=TRUE AND st.is_active=TRUE
              AND (st.license_id IS NULL OR (l.status='active' AND c.is_active=TRUE
                   AND (l.expires_at_utc IS NULL OR l.expires_at_utc>now())))
              AND s.revoked_at_utc IS NULL AND s.expires_at_utc>now()
              AND s.last_activity_at_utc>now()-interval '10 minutes'
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, tokenHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CloudPosSession(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4))
            : null;
    }

    public async Task RevokeSessionAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("UPDATE cloud_pos_sessions SET revoked_at_utc=now() WHERE token_sha256=@hash AND revoked_at_utc IS NULL", connection);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, tokenHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CloudPosUser>> GetUsersAsync(Guid storeId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id,store_id,email,role,is_active FROM cloud_pos_users
            WHERE store_id=@store_id ORDER BY created_at_utc,id
            """, connection);
        command.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var users = new List<CloudPosUser>();
        while (await reader.ReadAsync(cancellationToken))
            users.Add(new CloudPosUser(reader.GetInt32(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4)));
        return users;
    }

    public async Task<int> CreateUserAsync(Guid storeId, string email, string passwordHash, string role, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_pos_users(store_id,email,password_hash,role)
            VALUES(@store_id,@email,@password_hash,@role) RETURNING id
            """, connection);
        command.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
        command.Parameters.AddWithValue("email", NpgsqlDbType.Varchar, email);
        command.Parameters.AddWithValue("password_hash", NpgsqlDbType.Varchar, passwordHash);
        command.Parameters.AddWithValue("role", NpgsqlDbType.Varchar, role);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<bool> DeactivateUserAsync(Guid storeId, int userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_pos_users SET is_active=FALSE WHERE store_id=@store_id AND id=@user_id AND role<>'admin'
            """, connection);
        command.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Integer, userId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<CloudPosBootstrap> ReadBootstrapAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var entities = await ReadEntitiesAsync(storeId, "RestaurantTable", "MenuCategory", "MenuItem", "Order", "OrderItem", cancellationToken);
        return new CloudPosBootstrap(
            storeId,
            entities.Where(item => item.Type == "RestaurantTable" && !item.IsDeleted).Select(item => item.Entity).ToList(),
            entities.Where(item => item.Type == "MenuCategory" && !item.IsDeleted).Select(item => item.Entity).ToList(),
            entities.Where(item => item.Type == "MenuItem" && !item.IsDeleted).Select(item => item.Entity).ToList(),
            entities.Where(item => item.Type == "Order" && !item.IsDeleted && IsOpenOrder(item.Entity.Payload)).Select(item => item.Entity).ToList(),
            entities.Where(item => item.Type == "OrderItem" && !item.IsDeleted && item.Entity.Payload.TryGetProperty("OrderId", out _)).Select(item => item.Entity).ToList());
    }

    public async Task<int> CreateOrderAsync(Guid storeId, int userId, int tableId, Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var replay = await BeginCommandAsync(connection, transaction, storeId, userId, requestId, "Order", cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return int.Parse(replay, CultureInfo.InvariantCulture);
        }

        await using (var tableLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@scope,0))", connection, transaction))
        {
            tableLock.Parameters.AddWithValue("scope", NpgsqlDbType.Text, $"{storeId:D}:table:{tableId}");
            await tableLock.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var existingOrder = new NpgsqlCommand("""
            SELECT entity_id FROM cloud_sync_entities
            WHERE store_id=@store_id AND entity_type='Order' AND is_deleted=FALSE
              AND COALESCE((payload->>'RestaurantTableId')::integer,0)=@table_id
              AND COALESCE((payload->>'Status')::integer,0)=0
            ORDER BY cursor DESC LIMIT 1
            """, connection, transaction))
        {
            existingOrder.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
            existingOrder.Parameters.AddWithValue("table_id", NpgsqlDbType.Integer, tableId);
            var existingId = await existingOrder.ExecuteScalarAsync(cancellationToken) as string;
            if (int.TryParse(existingId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedExistingId))
            {
                await CompleteCommandAsync(connection, transaction, storeId, requestId, parsedExistingId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return parsedExistingId;
            }
        }

        await using (var table = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM cloud_sync_entities WHERE store_id=@store_id
              AND entity_type='RestaurantTable' AND entity_id=@table_id AND is_deleted=FALSE
              AND COALESCE((payload->>'IsActive')::boolean,TRUE)=TRUE)
            """, connection, transaction))
        {
            table.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
            table.Parameters.AddWithValue("table_id", NpgsqlDbType.Varchar, tableId.ToString(CultureInfo.InvariantCulture));
            if (await table.ExecuteScalarAsync(cancellationToken) is not true)
                throw new CloudPosConflictException("Δεν βρέθηκε ενεργό τραπέζι.");
        }

        var id = await NextEntityIdAsync(connection, transaction, cancellationToken);
        var now = DateTime.UtcNow;
        var payload = new Dictionary<string, object?>
        {
            ["RestaurantTableId"] = tableId, ["WaiterUserId"] = userId, ["CreatedAt"] = now,
            ["ClosedAt"] = null, ["Status"] = 0, ["PaymentMethod"] = 0
        };
        await AppendEntityEventAsync(connection, transaction, storeId, Guid.NewGuid(), "Order", id.ToString(CultureInfo.InvariantCulture), "upsert", payload, cancellationToken);
        await CompleteCommandAsync(connection, transaction, storeId, requestId, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<int> AddOrderItemAsync(Guid storeId, int userId, int orderId, int menuItemId,
        int quantity, string? notes, Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var replay = await BeginCommandAsync(connection, transaction, storeId, userId, requestId, "OrderItem", cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return int.Parse(replay, CultureInfo.InvariantCulture);
        }

        await using (var order = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM cloud_sync_entities WHERE store_id=@store_id
              AND entity_type='Order' AND entity_id=@order_id AND is_deleted=FALSE
              AND COALESCE((payload->>'Status')::integer,0)=0)
            """, connection, transaction))
        {
            order.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
            order.Parameters.AddWithValue("order_id", NpgsqlDbType.Varchar, orderId.ToString(CultureInfo.InvariantCulture));
            if (await order.ExecuteScalarAsync(cancellationToken) is not true)
                throw new CloudPosConflictException("Η παραγγελία δεν είναι ανοιχτή.");
        }

        await using (var menu = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM cloud_sync_entities WHERE store_id=@store_id
              AND entity_type='MenuItem' AND entity_id=@menu_id AND is_deleted=FALSE
              AND COALESCE((payload->>'IsAvailable')::boolean,TRUE)=TRUE)
            """, connection, transaction))
        {
            menu.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
            menu.Parameters.AddWithValue("menu_id", NpgsqlDbType.Varchar, menuItemId.ToString(CultureInfo.InvariantCulture));
            if (await menu.ExecuteScalarAsync(cancellationToken) is not true)
                throw new CloudPosConflictException("Το είδος δεν είναι διαθέσιμο.");
        }

        var id = await NextEntityIdAsync(connection, transaction, cancellationToken);
        var payload = new Dictionary<string, object?>
        {
            ["OrderId"] = orderId, ["MenuItemId"] = menuItemId, ["Quantity"] = quantity,
            ["Notes"] = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), ["ExtrasPrice"] = 0m,
            ["DiscountPercent"] = 0m, ["CreatedAt"] = DateTime.UtcNow,
            ["IsCancelled"] = false, ["CancelledAt"] = null, ["CancelReason"] = null
        };
        await AppendEntityEventAsync(connection, transaction, storeId, Guid.NewGuid(), "OrderItem", id.ToString(CultureInfo.InvariantCulture), "upsert", payload, cancellationToken);
        await CompleteCommandAsync(connection, transaction, storeId, requestId, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    private static bool IsOpenOrder(JsonElement payload) => payload.TryGetProperty("Status", out var status) &&
        status.ValueKind == JsonValueKind.Number && status.GetInt32() == 0;

    private async Task<List<(string Type, bool IsDeleted, CloudPosEntity Entity)>> ReadEntitiesAsync(
        Guid storeId, string table, string category, string menu, string order, string item, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT entity_type,entity_id,is_deleted,payload::text
            FROM cloud_sync_entities WHERE store_id=@store_id
              AND entity_type = ANY(@types)
            ORDER BY entity_type,entity_id
            """, connection);
        command.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
        command.Parameters.AddWithValue("types", NpgsqlDbType.Array | NpgsqlDbType.Text, new[] { table, category, menu, order, item });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<(string, bool, CloudPosEntity)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            using var json = JsonDocument.Parse(reader.GetString(3));
            list.Add((reader.GetString(0), reader.GetBoolean(2), new CloudPosEntity(reader.GetString(1), json.RootElement.Clone())));
        }
        return list;
    }

    private static async Task<int> NextEntityIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT nextval('cloud_pos_entity_id_seq')::integer", connection, transaction);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> BeginCommandAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid storeId, int userId, Guid requestId, string entityType, CancellationToken cancellationToken)
    {
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO cloud_pos_commands(store_id,request_id,user_id,entity_type)
            VALUES(@store_id,@request_id,@user_id,@type) ON CONFLICT DO NOTHING RETURNING request_id
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
            insert.Parameters.AddWithValue("request_id", NpgsqlDbType.Uuid, requestId);
            insert.Parameters.AddWithValue("user_id", NpgsqlDbType.Integer, userId);
            insert.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, entityType);
            if (await insert.ExecuteScalarAsync(cancellationToken) is not null) return null;
        }

        await using var replay = new NpgsqlCommand("""
            SELECT entity_type,entity_id,user_id FROM cloud_pos_commands
            WHERE store_id=@store_id AND request_id=@request_id FOR UPDATE
            """, connection, transaction);
        replay.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
        replay.Parameters.AddWithValue("request_id", NpgsqlDbType.Uuid, requestId);
        await using var reader = await replay.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != entityType || reader.GetInt32(2) != userId)
            throw new CloudPosConflictException("Το αίτημα επαναχρησιμοποιεί ήδη κλειδί άλλης ενέργειας.");
        return reader.IsDBNull(1) ? throw new CloudPosConflictException("Το αρχικό αίτημα είναι ακόμη σε εξέλιξη.") : reader.GetString(1);
    }

    private static async Task CompleteCommandAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid storeId, Guid requestId, int entityId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("UPDATE cloud_pos_commands SET entity_id=@id WHERE store_id=@store_id AND request_id=@request_id", connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, entityId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
        command.Parameters.AddWithValue("request_id", NpgsqlDbType.Uuid, requestId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AppendEntityEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid storeId, Guid eventId, string entityType, string entityId, string eventType,
        Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        long cursor;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO cloud_sync_events(store_id,event_id,entity_type,entity_id,event_type,schema_version,occurred_at_utc,payload)
            VALUES(@store_id,@event_id,@entity_type,@entity_id,@event_type,1,now(),@payload::jsonb) RETURNING id
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
            insert.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
            insert.Parameters.AddWithValue("entity_type", NpgsqlDbType.Varchar, entityType);
            insert.Parameters.AddWithValue("entity_id", NpgsqlDbType.Varchar, entityId);
            insert.Parameters.AddWithValue("event_type", NpgsqlDbType.Varchar, eventType);
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Text, json);
            cursor = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await using var upsert = new NpgsqlCommand("""
            INSERT INTO cloud_sync_entities(store_id,entity_type,entity_id,cursor,is_deleted,schema_version,updated_at_utc,payload)
            VALUES(@store_id,@entity_type,@entity_id,@cursor,FALSE,1,now(),@payload::jsonb)
            ON CONFLICT(store_id,entity_type,entity_id) DO UPDATE SET cursor=EXCLUDED.cursor,
              is_deleted=FALSE,schema_version=1,updated_at_utc=EXCLUDED.updated_at_utc,payload=EXCLUDED.payload
            WHERE EXCLUDED.cursor>cloud_sync_entities.cursor
            """, connection, transaction);
        upsert.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
        upsert.Parameters.AddWithValue("entity_type", NpgsqlDbType.Varchar, entityType);
        upsert.Parameters.AddWithValue("entity_id", NpgsqlDbType.Varchar, entityId);
        upsert.Parameters.AddWithValue("cursor", NpgsqlDbType.Bigint, cursor);
        upsert.Parameters.AddWithValue("payload", NpgsqlDbType.Text, json);
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class CloudPosConflictException(string message) : InvalidOperationException(message);
