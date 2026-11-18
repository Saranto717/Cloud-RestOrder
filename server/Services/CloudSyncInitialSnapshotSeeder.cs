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
    private const int CurrentSnapshotVersion = 3;

    public async Task EnsureInitialSnapshotAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var state = await db.CloudSyncSnapshotStates.SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (state?.CompletedAtUtc is not null && state.SnapshotVersion >= CurrentSnapshotVersion)
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

        AddSnapshots(db, events, await db.Users.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.TableAreas.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.PrinterStations.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.Tables.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.MenuCategories.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.MenuItems.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.ExtraOptions.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.RestaurantAppearances.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.CompanySettings.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.BankPosTerminalSettings.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.InvoiceSeries.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.InventoryItems.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.InventorySuppliers.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.Orders.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.OrderItems.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.OrderPayments.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.OrderPaymentItems.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.OrderPrintBatches.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.DayCloses.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.FiscalOrderDocuments.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.FiscalOrderDocumentItems.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.FiscalOrderCancellations.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.CancellationAuditEntries.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.InventoryRecipeLines.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.InventoryPurchases.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.InventoryPurchaseLines.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.InventoryMovements.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.TableTransfers.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        AddSnapshots(db, events, await db.ReceiptNumberHolds.AsNoTracking().ToListAsync(cancellationToken), occurredAtUtc);
        foreach (var join in CloudSyncEntityPolicy.JoinKeys)
        {
            var rows = await db.Set<Dictionary<string, object>>(join.Key).AsNoTracking().ToListAsync(cancellationToken);
            foreach (var row in rows)
                events.Add(new CloudSyncOutboxEvent
                {
                    EntityType = join.Key,
                    EntityId = string.Join(":", join.Value.Select(key => Convert.ToString(row[key], CultureInfo.InvariantCulture))),
                    EventType = "upsert",
                    OccurredAtUtc = occurredAtUtc,
                    PayloadJson = JsonSerializer.Serialize(row)
                });
        }

        db.CloudSyncOutboxEvents.AddRange(events);
        state.CompletedAtUtc = occurredAtUtc;
        state.SnapshotVersion = CurrentSnapshotVersion;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private static void AddSnapshots<TEntity>(AppDbContext db,
        ICollection<CloudSyncOutboxEvent> target,
        IEnumerable<TEntity> source,
        DateTimeOffset occurredAtUtc) where TEntity : class
    {
        var entityType = typeof(TEntity);
        if (!CloudSyncEntityPolicy.SnapshotTypes.TryGetValue(entityType, out var allowedProperties))
            return;

        var keyName = db.Model.FindEntityType(entityType)?.FindPrimaryKey()?.Properties.SingleOrDefault()?.Name
            ?? throw new InvalidOperationException($"Cloud snapshot type {entityType.Name} has no single-column key.");
        var idProperty = entityType.GetProperty(keyName, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Cloud snapshot key {entityType.Name}.{keyName} was not found.");

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
