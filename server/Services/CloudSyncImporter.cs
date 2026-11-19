using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public sealed record CloudSyncEntity(long Cursor, string EntityType, string EntityId, bool IsDeleted,
    int SchemaVersion, DateTimeOffset UpdatedAtUtc, JsonElement Payload);

/// <summary>
/// Both replicas use the same importer. Receiving a page and advancing its cursor
/// is atomic; rows whose parents arrive on later pages remain in a durable inbox.
/// </summary>
public static class CloudSyncImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task ReceivePageAsync(IDbContextFactory<AppDbContext> factory, IReadOnlyList<CloudSyncEntity> entities,
        long nextCursor, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.SuppressCloudSyncCapture = true;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var settings = await db.CloudSyncSettings.SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (settings is null) db.CloudSyncSettings.Add(settings = new CloudSyncSettings { Id = 1 });
        if (nextCursor < settings.LastCloudCursor) throw new InvalidDataException("Cloud sync cursor moved backwards.");

        foreach (var entity in entities.OrderBy(item => item.Cursor))
        {
            if (entity.Cursor <= 0 || entity.Cursor > nextCursor || entity.SchemaVersion != 1 || entity.Payload.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid cloud sync envelope.");
            var metadata = ResolveType(db, entity.EntityType);
            var keys = ParseKeys(metadata, entity.EntityId);
            if (keys.Length == 1 && keys[0] is int cloudId && cloudId is < 0 and > -1_000_000_000)
            {
                // Reserve even tombstones and still-deferred rows from earlier
                // cloud versions so a newly created row can never reuse their IDs.
                var sequence = await db.Set<CloudHostedIdSequence>().FindAsync([metadata.Name], cancellationToken);
                if (sequence is null)
                    db.Add(new CloudHostedIdSequence { EntityType = metadata.Name, CurrentId = cloudId });
                else sequence.CurrentId = Math.Min(sequence.CurrentId, cloudId);
            }
            var entry = await db.CloudSyncInboxEntries.FindAsync([entity.EntityType, entity.EntityId], cancellationToken);
            if (entry is not null && entry.Cursor >= entity.Cursor) continue;
            if (entry is null)
                db.CloudSyncInboxEntries.Add(entry = new CloudSyncInboxEntry { EntityType = entity.EntityType, EntityId = entity.EntityId });
            entry.Cursor = entity.Cursor;
            entry.IsDeleted = entity.IsDeleted;
            entry.SchemaVersion = entity.SchemaVersion;
            entry.UpdatedAtUtc = entity.UpdatedAtUtc;
            entry.PayloadJson = entity.Payload.GetRawText();
        }

        settings.LastCloudCursor = nextCursor;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task<int> ApplyPendingAsync(IDbContextFactory<AppDbContext> factory, CancellationToken cancellationToken = default)
    {
        // Scan every page, including pages after blocked rows, then retry parents'
        // dependents. The bound yields to the next worker tick under continuous writes.
        for (var pass = 0; pass < 32; pass++)
        {
            var applied = 0;
            long after = 0;
            while (true)
            {
                List<CloudSyncInboxEntry> page;
                await using (var db = await factory.CreateDbContextAsync(cancellationToken))
                    page = await db.CloudSyncInboxEntries.AsNoTracking().Where(item => item.Cursor > after)
                        .OrderBy(item => item.Cursor).Take(100).ToListAsync(cancellationToken);
                if (page.Count == 0) break;
                foreach (var item in page)
                    if (await TryApplyAsync(factory, item.EntityType, item.EntityId, cancellationToken)) applied++;
                after = page[^1].Cursor;
            }
            if (applied == 0) break;
        }
        await using var remaining = await factory.CreateDbContextAsync(cancellationToken);
        return await remaining.CloudSyncInboxEntries.CountAsync(cancellationToken);
    }

    private static async Task<bool> TryApplyAsync(IDbContextFactory<AppDbContext> factory, string entityType, string entityId,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.SuppressCloudSyncCapture = true;
        db.ApplyingCloudSync = true;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var envelope = await db.CloudSyncInboxEntries.FindAsync([entityType, entityId], cancellationToken);
        if (envelope is null) return false;
        // Never replace a locally committed edit before its durable outbox has
        // been sent. Deletes also wait for dependent local edits to be delivered.
        if (await db.CloudSyncOutboxEvents.AnyAsync(item => item.AcknowledgedAtUtc == null &&
            (envelope.IsDeleted || item.EntityType == entityType && item.EntityId == entityId), cancellationToken)) return false;

        var metadata = ResolveType(db, entityType);
        var keys = ParseKeys(metadata, entityId);
        var existing = await FindAsync(db, metadata, keys, cancellationToken);
        if (envelope.IsDeleted)
        {
            if (existing is not null) Entry(db, metadata, existing).State = EntityState.Deleted;
        }
        else
        {
            var instance = existing ?? (metadata.HasSharedClrType ? new Dictionary<string, object>() : Activator.CreateInstance(metadata.ClrType)!);
            var target = Entry(db, metadata, instance);
            var keyProperties = metadata.FindPrimaryKey()!.Properties;
            for (var index = 0; index < keys.Length; index++) target.Property(keyProperties[index].Name).CurrentValue = keys[index];

            using var payload = JsonDocument.Parse(envelope.PayloadJson);
            CloudSyncEntityPolicy.TryGetProperties(metadata, out var allowed);
            foreach (var property in metadata.GetProperties().Where(property => allowed.Contains(property.Name) && !property.IsPrimaryKey()))
            {
                var supplied = payload.RootElement.EnumerateObject().FirstOrDefault(item => string.Equals(item.Name, property.Name, StringComparison.OrdinalIgnoreCase));
                if (supplied.Value.ValueKind == JsonValueKind.Undefined) continue;
                target.Property(property.Name).CurrentValue = JsonSerializer.Deserialize(supplied.Value.GetRawText(), property.ClrType, JsonOptions);
            }

            // An audit identity can never become a local passwordless account.
            if (instance is User user && user.Id <= -1_000_000_000) user.IsCloudIdentity = true;
            foreach (var foreignKey in metadata.GetForeignKeys())
            {
                var foreignValues = foreignKey.Properties.Select(property => target.Property(property.Name).CurrentValue).ToArray();
                if (foreignValues.Any(value => value is null)) continue;
                if (await FindAsync(db, foreignKey.PrincipalEntityType, foreignValues!, cancellationToken) is null) return false;
            }
            if (existing is null) target.State = EntityState.Added;
        }

        db.CloudSyncInboxEntries.Remove(envelope);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqliteException { SqliteExtendedErrorCode: 787 } or
            PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // A referenced delete or a racing dependency remains queued. The
            // enclosing transaction rolls back both data and inbox acknowledgement.
            return false;
        }
    }

    private static IEntityType ResolveType(AppDbContext db, string name)
    {
        var metadata = db.Model.GetEntityTypes().SingleOrDefault(type => CloudSyncEntityPolicy.EntityName(type) == name);
        if (metadata is null || !CloudSyncEntityPolicy.TryGetProperties(metadata, out _))
            throw new InvalidDataException($"Unsupported cloud sync entity: {name}.");
        return metadata;
    }

    private static object[] ParseKeys(IEntityType metadata, string id)
    {
        var properties = metadata.FindPrimaryKey()!.Properties;
        var parts = id.Split(':');
        if (parts.Length != properties.Count) throw new InvalidDataException("Invalid cloud sync key.");
        return properties.Select((property, index) => Convert.ChangeType(parts[index], property.ClrType, CultureInfo.InvariantCulture)).ToArray();
    }

    private static ValueTask<object?> FindAsync(AppDbContext db, IEntityType metadata, object[] keys, CancellationToken cancellationToken) =>
        metadata.HasSharedClrType ? FindJoinAsync(db, metadata.Name, keys, cancellationToken) : db.FindAsync(metadata.ClrType, keys, cancellationToken);

    private static async ValueTask<object?> FindJoinAsync(AppDbContext db, string name, object[] keys, CancellationToken cancellationToken) =>
        await db.Set<Dictionary<string, object>>(name).FindAsync(keys, cancellationToken);

    private static EntityEntry Entry(AppDbContext db, IEntityType metadata, object instance) => metadata.HasSharedClrType
        ? db.Set<Dictionary<string, object>>(metadata.Name).Entry((Dictionary<string, object>)instance)
        : db.Entry(instance);
}
