using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

/// <summary>Stock, recipes, receiving and immutable inventory audit trail.</summary>
public sealed class InventoryService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task AddIngredientAsync(string name, int unitCode, decimal openingQuantity, decimal minimumQuantity, decimal unitCost)
    {
        using var db = await dbFactory.CreateDbContextAsync();
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Συμπλήρωσε την ονομασία υλικού.");
        ValidateUnit(unitCode);
        if (openingQuantity < 0 || minimumQuantity < 0 || unitCost < 0) throw new InvalidOperationException("Οι ποσότητες και οι τιμές δεν μπορεί να είναι αρνητικές.");
        var item = new InventoryItem { Name = name, MeasurementUnitCode = unitCode, QuantityOnHand = openingQuantity, MinimumQuantity = minimumQuantity, UnitCost = unitCost };
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        if (openingQuantity > 0)
            db.InventoryMovements.Add(new InventoryMovement { InventoryItemId = item.Id, Kind = InventoryMovementKind.Stocktake, QuantityDelta = openingQuantity, UnitCost = unitCost, Note = "Αρχικό απόθεμα" });
        await db.SaveChangesAsync();
    }

    public async Task AddSupplierAsync(InventorySupplier supplier)
    {
        using var db = await dbFactory.CreateDbContextAsync();
        supplier.Name = supplier.Name.Trim();
        if (supplier.Name.Length == 0) throw new InvalidOperationException("Συμπλήρωσε την επωνυμία προμηθευτή.");
        db.InventorySuppliers.Add(supplier);
        await db.SaveChangesAsync();
    }

    public async Task SaveRecipeAsync(int menuItemId, IReadOnlyCollection<(int IngredientId, decimal Quantity)> lines)
    {
        using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.MenuItems.AnyAsync(x => x.Id == menuItemId)) throw new InvalidOperationException("Το προϊόν δεν βρέθηκε.");
        if (lines.Any(x => x.IngredientId == 0 || x.Quantity <= 0)) throw new InvalidOperationException("Κάθε υλικό συνταγής χρειάζεται θετική ποσότητα.");
        if (lines.GroupBy(x => x.IngredientId).Any(x => x.Count() > 1)) throw new InvalidOperationException("Το ίδιο υλικό υπάρχει δύο φορές στη συνταγή.");
        var ids = lines.Select(x => x.IngredientId).ToArray();
        if (ids.Length > 0 && await db.InventoryItems.CountAsync(x => ids.Contains(x.Id) && x.IsActive) != ids.Length)
            throw new InvalidOperationException("Ένα ή περισσότερα υλικά δεν είναι διαθέσιμα.");
        var existing = await db.InventoryRecipeLines.Where(x => x.MenuItemId == menuItemId).ToListAsync();
        db.InventoryRecipeLines.RemoveRange(existing);
        db.InventoryRecipeLines.AddRange(lines.Select(x => new InventoryRecipeLine { MenuItemId = menuItemId, InventoryItemId = x.IngredientId, QuantityPerProduct = x.Quantity }));
        await db.SaveChangesAsync();
    }

    public async Task ReceivePurchaseAsync(int? supplierId, string? invoiceNumber, DateTime purchasedAt,
        int ingredientId, decimal quantity, decimal unitCost, int userId, string? note)
    {
        using var db = await dbFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var ingredient = await db.InventoryItems.FirstOrDefaultAsync(x => x.Id == ingredientId && x.IsActive)
            ?? throw new InvalidOperationException("Το υλικό δεν βρέθηκε.");
        if (quantity <= 0 || unitCost < 0) throw new InvalidOperationException("Η ποσότητα πρέπει να είναι θετική και η τιμή μη αρνητική.");
        if (supplierId is not null && !await db.InventorySuppliers.AnyAsync(x => x.Id == supplierId && x.IsActive))
            throw new InvalidOperationException("Ο προμηθευτής δεν βρέθηκε.");
        var total = decimal.Round(quantity * unitCost, 4);
        var priorValue = ingredient.QuantityOnHand * ingredient.UnitCost;
        var newQuantity = ingredient.QuantityOnHand + quantity;
        ingredient.UnitCost = newQuantity == 0 ? unitCost : decimal.Round((priorValue + total) / newQuantity, 6);
        ingredient.QuantityOnHand = newQuantity;
        var purchase = new InventoryPurchase
        {
            SupplierId = supplierId, InvoiceNumber = string.IsNullOrWhiteSpace(invoiceNumber) ? null : invoiceNumber.Trim(),
            PurchasedAt = purchasedAt, TotalCost = total, CreatedByUserId = userId, Notes = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
        purchase.Lines.Add(new InventoryPurchaseLine { InventoryItemId = ingredientId, Quantity = quantity, UnitCost = unitCost, TotalCost = total });
        db.InventoryPurchases.Add(purchase);
        await db.SaveChangesAsync();
        db.InventoryMovements.Add(new InventoryMovement
        {
            InventoryItemId = ingredientId, Kind = InventoryMovementKind.Purchase, QuantityDelta = quantity,
            UnitCost = unitCost, InventoryPurchaseId = purchase.Id, CreatedByUserId = userId,
            CreatedAt = purchasedAt, Note = purchase.InvoiceNumber is null ? "Παραλαβή αγοράς" : $"Τιμολόγιο {purchase.InvoiceNumber}"
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task AdjustStockAsync(int ingredientId, decimal actualQuantity, int userId, string? note)
    {
        using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.InventoryItems.FirstOrDefaultAsync(x => x.Id == ingredientId && x.IsActive)
            ?? throw new InvalidOperationException("Το υλικό δεν βρέθηκε.");
        if (actualQuantity < 0) throw new InvalidOperationException("Το πραγματικό απόθεμα δεν μπορεί να είναι αρνητικό.");
        var delta = actualQuantity - item.QuantityOnHand;
        if (delta == 0) return;
        item.QuantityOnHand = actualQuantity;
        db.InventoryMovements.Add(new InventoryMovement
        {
            InventoryItemId = item.Id, Kind = InventoryMovementKind.Stocktake, QuantityDelta = delta,
            UnitCost = item.UnitCost, CreatedByUserId = userId,
            Note = string.IsNullOrWhiteSpace(note) ? "Απογραφή / διόρθωση αποθέματος" : note.Trim()
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Deducts ingredients after successful kitchen/bar dispatch. Unique movement index makes retries safe.</summary>
    public async Task<int> ConsumeForDispatchedItemsAsync(IEnumerable<int> orderItemIds, int userId)
    {
        var ids = orderItemIds.Where(x => x > 0).Distinct().ToArray();
        if (ids.Length == 0) return 0;
        using var db = await dbFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var orderItems = await db.OrderItems.Where(x => ids.Contains(x.Id) && x.IsPrinted && !x.IsCancelled).ToListAsync();
        var count = 0;
        foreach (var orderItem in orderItems)
        {
            var recipe = await db.InventoryRecipeLines.Where(x => x.MenuItemId == orderItem.MenuItemId).ToListAsync();
            foreach (var line in recipe)
            {
                if (await db.InventoryMovements.AnyAsync(x => x.OrderItemId == orderItem.Id && x.InventoryItemId == line.InventoryItemId && x.Kind == InventoryMovementKind.Consumption)) continue;
                var ingredient = await db.InventoryItems.FirstOrDefaultAsync(x => x.Id == line.InventoryItemId);
                if (ingredient is null) continue;
                var used = decimal.Round(line.QuantityPerProduct * orderItem.Quantity, 6);
                ingredient.QuantityOnHand -= used;
                db.InventoryMovements.Add(new InventoryMovement
                {
                    InventoryItemId = ingredient.Id, Kind = InventoryMovementKind.Consumption, QuantityDelta = -used,
                    UnitCost = ingredient.UnitCost, OrderItemId = orderItem.Id, CreatedByUserId = userId,
                    Note = $"Κατανάλωση συνταγής · είδος #{orderItem.Id}"
                });
                count++;
            }
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return count;
    }

    private static void ValidateUnit(int unitCode)
    {
        if (unitCode is not (1 or 2 or 3)) throw new InvalidOperationException("Οι μονάδες είναι τεμάχια, κιλά ή λίτρα.");
    }
}
