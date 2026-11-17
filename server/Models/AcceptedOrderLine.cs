namespace RestaurantOrders.Models;

// Facts at the moment the waiter submitted the order; independent of printer and provider.
public sealed record AcceptedOrderLine(int ItemId, int MenuId, string Name, int Quantity, decimal Price,
    decimal Extras, decimal Discount, decimal VatPercent, string? VatCode, int Unit, string? Notes)
{
    public static AcceptedOrderLine From(OrderItem i) => new(i.Id, i.MenuItemId, i.MenuItem?.Name ?? "Είδος",
        i.Quantity, i.MenuItem?.Price ?? 0m, i.ExtrasPrice, i.DiscountPercent,
        i.MenuItem?.VatPercentOverride ?? i.MenuItem?.MenuCategory?.VatPercent ?? 24m,
        i.MenuItem?.VatCategoryCodeOverride ?? i.MenuItem?.MenuCategory?.VatCategoryCode,
        i.MenuItem?.MeasurementUnitCode ?? 1, i.Notes);

    // Detached copy: never revert the live catalog when materializing an older batch.
    public OrderItem ForDocument(OrderPrintBatch batch, MenuCategory? routing) => new() {
        Id = ItemId, OrderId = batch.OrderId, OrderPrintBatchId = batch.Id, MenuItemId = MenuId,
        Quantity = Quantity, ExtrasPrice = Extras, DiscountPercent = Discount, Notes = Notes, IsPrinted = true,
        MenuItem = new() { Id = MenuId, Name = Name, Price = Price, MeasurementUnitCode = Unit,
            VatPercentOverride = VatPercent, VatCategoryCodeOverride = VatCode, MenuCategory = routing }
    };
}
