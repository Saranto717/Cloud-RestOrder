using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using RestaurantOrders.Models;
using RestaurantOrders.Services;

namespace RestaurantOrders.Data;

public class AppDbContext : DbContext
{
    private readonly bool _cloudSyncEnabled;
    private readonly bool _cloudHosted;

    private readonly CloudSyncRuntimeState? _cloudSyncRuntimeState;

    public AppDbContext(DbContextOptions<AppDbContext> options, IOptions<CloudSyncOptions>? cloudSyncOptions = null,
        CloudSyncRuntimeState? cloudSyncRuntimeState = null, bool cloudHosted = false) : base(options)
    {
        _cloudSyncEnabled = cloudSyncOptions?.Value.Enabled == true;
        _cloudSyncRuntimeState = cloudSyncRuntimeState;
        _cloudHosted = cloudHosted;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<RestaurantTable> Tables => Set<RestaurantTable>();
    public DbSet<TableArea> TableAreas => Set<TableArea>();
    public DbSet<PrinterStation> PrinterStations => Set<PrinterStation>();
    public DbSet<MenuCategory> MenuCategories => Set<MenuCategory>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderPrintBatch> OrderPrintBatches => Set<OrderPrintBatch>();
    public DbSet<OrderPayment> OrderPayments => Set<OrderPayment>();
    public DbSet<OrderPaymentItem> OrderPaymentItems => Set<OrderPaymentItem>();
    public DbSet<FiscalOrderDocumentItem> FiscalOrderDocumentItems => Set<FiscalOrderDocumentItem>();
    public DbSet<ExtraOption> ExtraOptions => Set<ExtraOption>();
    public DbSet<DayClose> DayCloses => Set<DayClose>();
    public DbSet<CompanySettings> CompanySettings => Set<CompanySettings>();
    public DbSet<RestaurantAppearance> RestaurantAppearances => Set<RestaurantAppearance>();
    public DbSet<BankPosTerminalSettings> BankPosTerminalSettings => Set<BankPosTerminalSettings>();
    public DbSet<ReceiptNumberHold> ReceiptNumberHolds => Set<ReceiptNumberHold>();
    public DbSet<FiscalProviderExchange> FiscalProviderExchanges => Set<FiscalProviderExchange>();
    public DbSet<InvoiceSeries> InvoiceSeries => Set<InvoiceSeries>();
    public DbSet<FiscalOrderDocument> FiscalOrderDocuments => Set<FiscalOrderDocument>();
    public DbSet<FiscalOrderCancellation> FiscalOrderCancellations => Set<FiscalOrderCancellation>();
    public DbSet<CancellationAuditEntry> CancellationAuditEntries => Set<CancellationAuditEntry>();
    public DbSet<TableTransfer> TableTransfers => Set<TableTransfer>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<InventoryRecipeLine> InventoryRecipeLines => Set<InventoryRecipeLine>();
    public DbSet<InventorySupplier> InventorySuppliers => Set<InventorySupplier>();
    public DbSet<InventoryPurchase> InventoryPurchases => Set<InventoryPurchase>();
    public DbSet<InventoryPurchaseLine> InventoryPurchaseLines => Set<InventoryPurchaseLine>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<CloudSyncOutboxEvent> CloudSyncOutboxEvents => Set<CloudSyncOutboxEvent>();
    public DbSet<CloudSyncSnapshotState> CloudSyncSnapshotStates => Set<CloudSyncSnapshotState>();
    public DbSet<CloudSyncSettings> CloudSyncSettings => Set<CloudSyncSettings>();
    public bool SuppressCloudSyncCapture { get; set; }

    // Only the transfer service may finalize the records locked by its own journal.
    internal bool FinalizingTableTransfer { get; set; }

    public Task<bool> HasPendingTableTransferAsync(int orderId) => Orders
        .Where(o => o.Id == orderId)
        .AnyAsync(o => TableTransfers.Any(t => t.Stage != TableTransferStage.Completed && t.Stage != TableTransferStage.Rejected &&
            (t.SourceTableId == o.RestaurantTableId || t.TargetTableId == o.RestaurantTableId)));

    private void CheckTransferLocks()
    {
        if (FinalizingTableTransfer) return;
        ChangeTracker.DetectChanges();
        var orders = new HashSet<int>();
        var tables = new HashSet<int>();
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Entity is Order order)
            {
                if (order.Id > 0) orders.Add(order.Id);
                tables.Add(order.RestaurantTableId);
                if (entry.State != EntityState.Added) tables.Add(entry.OriginalValues.GetValue<int>(nameof(Order.RestaurantTableId)));
            }
            else if (entry.Entity is RestaurantTable table && entry.State != EntityState.Added) tables.Add(table.Id);
            else if (entry.Entity is OrderItem item) orders.Add(item.OrderId);
            else if (entry.Entity is OrderPayment payment) orders.Add(payment.OrderId);
            else if (entry.Entity is OrderPrintBatch batch) orders.Add(batch.OrderId);
            else if (entry.Entity is FiscalOrderDocument document) orders.Add(document.OrderId);
            else if (entry.Entity is FiscalOrderCancellation cancellation) orders.Add(cancellation.OrderId);
            else if (entry.Entity is FiscalOrderDocumentItem documentItem)
            {
                var id = documentItem.OrderItemId;
                foreach (var orderId in OrderItems.Where(i => i.Id == id).Select(i => i.OrderId)) orders.Add(orderId);
            }
            else if (entry.Entity is OrderPaymentItem paymentItem)
            {
                foreach (var orderId in OrderPayments.Where(p => p.Id == paymentItem.OrderPaymentId).Select(p => p.OrderId)) orders.Add(orderId);
            }
            if (entry.State != EntityState.Added && entry.Metadata.FindProperty("OrderId")?.ClrType == typeof(int))
                orders.Add(entry.OriginalValues.GetValue<int>("OrderId"));
        }
        if (orders.Count == 0 && tables.Count == 0) return;
        foreach (var id in Orders.Where(o => orders.Contains(o.Id)).Select(o => o.RestaurantTableId)) tables.Add(id);
        if (TableTransfers.Any(t => t.Stage != TableTransferStage.Completed && t.Stage != TableTransferStage.Rejected &&
            (tables.Contains(t.SourceTableId) || tables.Contains(t.TargetTableId))))
            throw new InvalidOperationException("Υπάρχει εκκρεμής μεταφορά τραπεζιού. Ολοκλήρωσε τον επανέλεγχο πριν από αλλαγές ή πληρωμές.");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AssignCloudHostedIds();
        using var transaction = Database.CurrentTransaction is null ? Database.BeginTransaction() : null;
        CheckTransferLocks();
        var syncChanges = IsCloudSyncEnabled && !SuppressCloudSyncCapture && acceptAllChangesOnSuccess ? CaptureCloudSyncChanges() : [];
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        if (syncChanges.Count > 0)
        {
            CloudSyncOutboxEvents.AddRange(syncChanges.Select(CreateOutboxEvent));
            result += base.SaveChanges(acceptAllChangesOnSuccess);
        }
        transaction?.Commit();
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AssignCloudHostedIds();
        await using var transaction = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        CheckTransferLocks();
        var syncChanges = IsCloudSyncEnabled && !SuppressCloudSyncCapture && acceptAllChangesOnSuccess ? CaptureCloudSyncChanges() : [];
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (syncChanges.Count > 0)
        {
            CloudSyncOutboxEvents.AddRange(syncChanges.Select(CreateOutboxEvent));
            result += await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private List<PendingCloudSyncChange> CaptureCloudSyncChanges()
    {
        ChangeTracker.DetectChanges();
        var changes = new List<PendingCloudSyncChange>();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (!CloudSyncEntityPolicy.TryGetProperties(entry.Entity.GetType(), out var allowedProperties)) continue;
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.State == EntityState.Modified &&
                !entry.Properties.Any(property => property.IsModified && allowedProperties.Contains(property.Metadata.Name)))
                continue;

            var key = entry.Metadata.FindPrimaryKey()?.Properties.SingleOrDefault();
            if (key is null) continue;

            var eventType = entry.State == EntityState.Deleted ? "delete" : "upsert";
            var entityId = entry.State == EntityState.Deleted
                ? Convert.ToString(entry.Property(key.Name).OriginalValue, CultureInfo.InvariantCulture)
                : null;
            var deletedPayload = entry.State == EntityState.Deleted
                ? JsonSerializer.Serialize(CloudSyncEntityPolicy.ReadValues(entry, allowedProperties, useOriginalValues: true))
                : null;

            changes.Add(new PendingCloudSyncChange(entry, key.Name, allowedProperties, eventType, entityId, deletedPayload));
        }

        return changes;
    }

    private bool IsCloudSyncEnabled => _cloudSyncRuntimeState?.Enabled ?? _cloudSyncEnabled;

    private void AssignCloudHostedIds()
    {
        if (!_cloudHosted) return;
        foreach (var entry in ChangeTracker.Entries().Where(item => item.State == EntityState.Added))
        {
            var key = entry.Metadata.FindPrimaryKey()?.Properties.SingleOrDefault();
            if (key?.ClrType != typeof(int) || entry.Property(key.Name).CurrentValue is not 0) continue;
            // New rows created in the cloud use negative IDs, leaving the positive
            // ID range used by an offline Windows database free from collisions.
            entry.Property(key.Name).CurrentValue = -RandomNumberGenerator.GetInt32(1, int.MaxValue);
        }
    }

    private static CloudSyncOutboxEvent CreateOutboxEvent(PendingCloudSyncChange change)
    {
        var entityId = change.DeletedEntityId ?? Convert.ToString(
            change.Entry.Property(change.KeyProperty).CurrentValue, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(entityId) || entityId == "0")
            throw new InvalidOperationException("A cloud sync event could not be created because its entity ID was not generated.");

        var payload = change.DeletedPayload ?? JsonSerializer.Serialize(
            CloudSyncEntityPolicy.ReadValues(change.Entry, change.AllowedProperties, useOriginalValues: false));

        return new CloudSyncOutboxEvent
        {
            EventId = Guid.NewGuid(),
            EntityType = change.Entry.Metadata.ClrType.Name,
            EntityId = entityId,
            EventType = change.EventType,
            SchemaVersion = 1,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            PayloadJson = payload
        };
    }

    private sealed record PendingCloudSyncChange(
        EntityEntry Entry,
        string KeyProperty,
        IReadOnlySet<string> AllowedProperties,
        string EventType,
        string? DeletedEntityId,
        string? DeletedPayload);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CloudSyncOutboxEvent>(entity =>
        {
            entity.ToTable("CloudSyncOutboxEvents");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.EventId).IsUnique();
            entity.HasIndex(item => new { item.AcknowledgedAtUtc, item.Id });
            entity.Property(item => item.EntityType).HasMaxLength(80).IsRequired();
            entity.Property(item => item.EntityId).HasMaxLength(160).IsRequired();
            entity.Property(item => item.EventType).HasMaxLength(40).IsRequired();
            entity.Property(item => item.PayloadJson).IsRequired();
            entity.Property(item => item.LastError).HasMaxLength(2000);
        });

        modelBuilder.Entity<CloudSyncSnapshotState>(entity =>
        {
            entity.ToTable("CloudSyncSnapshotStates");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<CloudSyncSettings>(entity =>
        {
            entity.ToTable("CloudSyncSettings");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.ApiBaseUrl).HasMaxLength(400).IsRequired();
            entity.Property(item => item.ProtectedStoreToken).HasMaxLength(8192).IsRequired();
            entity.Property(item => item.ProtectedDeviceSecret).HasMaxLength(8192).IsRequired();
            entity.Property(item => item.LastSyncError).HasMaxLength(2000);
        });

        modelBuilder.Entity<TableTransfer>().HasIndex(t => new { t.Stage, t.SourceTableId, t.TargetTableId });
        modelBuilder.Entity<MenuCategory>()
            .HasOne(c => c.PrinterStation)
            .WithMany()
            .HasForeignKey(c => c.PrinterStationId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<MenuCategory>()
            .HasMany(c => c.PrinterStations)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "MenuCategoryPrinterStation",
                right => right.HasOne<PrinterStation>()
                    .WithMany()
                    .HasForeignKey("PrinterStationId")
                    .OnDelete(DeleteBehavior.Cascade),
                left => left.HasOne<MenuCategory>()
                    .WithMany()
                    .HasForeignKey("MenuCategoryId")
                    .OnDelete(DeleteBehavior.Cascade),
                join =>
                {
                    join.ToTable("MenuCategoryPrinterStations");
                    join.HasKey("MenuCategoryId", "PrinterStationId");
                });

        modelBuilder.Entity<MenuItem>()
            .HasOne(i => i.MenuCategory)
            .WithMany()
            .HasForeignKey(i => i.MenuCategoryId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ExtraOption>()
            .HasMany(e => e.MenuCategories)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "ExtraOptionMenuCategory",
                right => right.HasOne<MenuCategory>().WithMany()
                    .HasForeignKey("MenuCategoryId").OnDelete(DeleteBehavior.Cascade),
                left => left.HasOne<ExtraOption>().WithMany()
                    .HasForeignKey("ExtraOptionId").OnDelete(DeleteBehavior.Cascade),
                join =>
                {
                    join.ToTable("ExtraOptionMenuCategories");
                    join.HasKey("ExtraOptionId", "MenuCategoryId");
                });

        modelBuilder.Entity<RestaurantTable>()
            .HasOne(t => t.TableArea)
            .WithMany()
            .HasForeignKey(t => t.TableAreaId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Order>()
            .HasOne(o => o.RestaurantTable)
            .WithMany()
            .HasForeignKey(o => o.RestaurantTableId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Order>()
            .HasOne(o => o.WaiterUser)
            .WithMany()
            .HasForeignKey(o => o.WaiterUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Order>()
            .HasOne(o => o.DayClose)
            .WithMany()
            .HasForeignKey(o => o.DayCloseId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<DayClose>()
            .HasOne(dc => dc.ClosedByUser)
            .WithMany()
            .HasForeignKey(dc => dc.ClosedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<FiscalOrderDocument>()
            .HasOne(f => f.Order)
            .WithMany()
            .HasForeignKey(f => f.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<FiscalOrderCancellation>()
            .HasOne<Order>()
            .WithMany()
            .HasForeignKey(c => c.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderItem>()
            .HasOne(oi => oi.Order)
            .WithMany(o => o.Items)
            .HasForeignKey(oi => oi.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderPrintBatch>()
            .HasOne(b => b.Order)
            .WithMany()
            .HasForeignKey(b => b.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderPrintBatch>()
            .HasIndex(b => new { b.OrderId, b.Number })
            .IsUnique();

        modelBuilder.Entity<OrderItem>()
            .HasOne(oi => oi.OrderPrintBatch)
            .WithMany(b => b.Items)
            .HasForeignKey(oi => oi.OrderPrintBatchId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<OrderItem>()
            .HasOne(oi => oi.MenuItem)
            .WithMany()
            .HasForeignKey(oi => oi.MenuItemId)
            .OnDelete(DeleteBehavior.Restrict);


        modelBuilder.Entity<OrderPayment>()
            .HasOne(p => p.Order)
            .WithMany(o => o.Payments)
            .HasForeignKey(p => p.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderPaymentItem>()
            .HasOne(pi => pi.OrderPayment)
            .WithMany(p => p.Items)
            .HasForeignKey(pi => pi.OrderPaymentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderPaymentItem>()
            .HasOne(pi => pi.OrderItem)
            .WithMany()
            .HasForeignKey(pi => pi.OrderItemId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ExtraOption>()
            .HasOne(e => e.MenuCategory)
            .WithMany()
            .HasForeignKey(e => e.MenuCategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<InventoryRecipeLine>()
            .HasOne(x => x.MenuItem).WithMany().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<InventoryRecipeLine>()
            .HasOne(x => x.InventoryItem).WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryRecipeLine>().HasIndex(x => new { x.MenuItemId, x.InventoryItemId }).IsUnique();
        modelBuilder.Entity<InventoryPurchase>()
            .HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<InventoryPurchaseLine>()
            .HasOne(x => x.Purchase).WithMany(x => x.Lines).HasForeignKey(x => x.InventoryPurchaseId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<InventoryPurchaseLine>()
            .HasOne(x => x.InventoryItem).WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryMovement>()
            .HasOne(x => x.InventoryItem).WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryMovement>().HasIndex(x => new { x.OrderItemId, x.InventoryItemId, x.Kind })
            .IsUnique().HasFilter("\"OrderItemId\" IS NOT NULL AND \"Kind\" = 1");
        modelBuilder.Entity<InventoryMovement>().HasIndex(x => x.RelatedMovementId)
            .IsUnique().HasFilter("\"RelatedMovementId\" IS NOT NULL AND \"Kind\" = 2");
    }
}

public static class CloudSyncEntityPolicy
{
    private static readonly string[] ExcludedProperties =
    [
        "AadeRegistryPassword", "ParochosSubscriptionKey", "ParochosPassword",
        "ApiKey", "ApiSecret", "Password", "RedeemCode", "Username"
    ];

    private static readonly Type[] EntityTypes =
    [
        typeof(User), typeof(TableArea), typeof(RestaurantTable), typeof(PrinterStation), typeof(MenuCategory),
        typeof(MenuItem), typeof(Order), typeof(OrderItem), typeof(OrderPrintBatch), typeof(OrderPayment),
        typeof(OrderPaymentItem), typeof(FiscalOrderDocumentItem), typeof(ExtraOption), typeof(DayClose),
        typeof(CompanySettings), typeof(RestaurantAppearance), typeof(BankPosTerminalSettings),
        typeof(ReceiptNumberHold), typeof(InvoiceSeries), typeof(FiscalOrderDocument),
        typeof(FiscalOrderCancellation), typeof(CancellationAuditEntry), typeof(TableTransfer),
        typeof(InventoryItem), typeof(InventoryRecipeLine), typeof(InventorySupplier),
        typeof(InventoryPurchase), typeof(InventoryPurchaseLine), typeof(InventoryMovement)
    ];

    private static readonly IReadOnlyDictionary<Type, IReadOnlySet<string>> Properties = EntityTypes
        .ToDictionary(type => type, type => (IReadOnlySet<string>)type.GetProperties()
            .Where(property => property.Name != "Id" && property.CanRead && property.CanWrite &&
                property.GetCustomAttributes(typeof(NotMappedAttribute), true).Length == 0 &&
                IsScalar(property.PropertyType) && !ExcludedProperties.Contains(property.Name, StringComparer.Ordinal))
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal));

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal) ||
            type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid) ||
            type == typeof(byte[]);
    }

    public static bool TryGetProperties(Type entityType, out IReadOnlySet<string> properties) =>
        Properties.TryGetValue(entityType, out properties!);

    public static IReadOnlyDictionary<Type, IReadOnlySet<string>> SnapshotTypes => Properties;

    public static Dictionary<string, object?> ReadValues(EntityEntry entry, IReadOnlySet<string> allowedProperties, bool useOriginalValues)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var propertyName in allowedProperties.Order(StringComparer.Ordinal))
        {
            var property = entry.Property(propertyName);
            values[propertyName] = useOriginalValues ? property.OriginalValue : property.CurrentValue;
        }
        return values;
    }
}
