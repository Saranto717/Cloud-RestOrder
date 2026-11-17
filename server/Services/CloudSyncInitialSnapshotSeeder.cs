using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

/// <summary>
/// Queues the existing POS data once before normal event delivery starts. The
/// marker and snapshot rows are committed together so a restart cannot leave a
/// half-created baseline or skip data after a failed write.
/// </summary>
public sealed class CloudSyncInitialSnapshotSeeder
{
    public async Task EnsureInitialSnapshotAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var state = await db.CloudSyncSnapshotStates.SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (state?.CompletedAtUtc is not null)
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (state is null)
        {
            state = new CloudSyncSnapshotState { Id = 1 };
            db.CloudSyncSnapshotStates.Add(state);
        }

        var occurredAtUtc = DateTimeOffset.UtcNow;
        var events = new List<CloudSyncOutboxEvent>();

        AddSnapshots(events, await db.TableAreas.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(events, await db.Tables.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(events, await db.MenuCategories.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(events, await db.MenuItems.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(events, await db.ExtraOptions.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(events, await db.Orders.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(events, await db.OrderItems.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(events, await db.OrderPayments.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(events, await db.OrderPaymentItems.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);

        db.CloudSyncOutboxEvents.AddRange(events);
        state.CompletedAtUtc = occurredAtUtc;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private static void AddSnapshots<TEntity>(
        ICollection<CloudSyncOutboxEvent> target,
        IEnumerable<TEntity> source,
        DateTimeOffset occurredAtUtc) where TEntity : class
    {
        var entityType = typeof(TEntity);
        if (!CloudSyncEntityPolicy.SnapshotTypes.TryGetValue(entityType, out var allowedProperties))
            return;

        var idProperty = entityType.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
        if (idProperty is null)
            throw new InvalidOperationException($"Cloud snapshot type {entityType.Name} has no Id property.");

        var snapshotProperties = allowedProperties
            .Order(StringComparer.Ordinal)
            .Select(name => (Name: name, Property: entityType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException($"Cloud snapshot property {entityType.Name}.{name} was not found.")))
            .ToArray();

        foreach (var item in source)
        {
            var id = Convert.ToString(idProperty.GetValue(item), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(id) || id == "0")
                throw new InvalidOperationException($"Cloud snapshot type {entityType.Name} has an invalid Id.");

            var payload = new Dictionary<string, object?>(snapshotProperties.Length, StringComparer.Ordinal);
            foreach (var property in snapshotProperties)
                payload[property.Name] = property.Property.GetValue(item);

            target.Add(new CloudSyncOutboxEvent
            {
                EventId = Guid.NewGuid(),
                EntityType = entityType.Name,
                EntityId = id,
                EventType = "upsert",
                SchemaVersion = 1,
                OccurredAtUtc = occurredAtUtc,
                PayloadJson = JsonSerializer.Serialize(payload)
            });
        }

    }
}
