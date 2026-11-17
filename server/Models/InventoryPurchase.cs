namespace RestaurantOrders.Models;

public sealed class InventoryPurchase
{
    public int Id { get; set; }
    public int? SupplierId { get; set; }
    public InventorySupplier? Supplier { get; set; }
    public string? InvoiceNumber { get; set; }
    public DateTime PurchasedAt { get; set; } = DateTime.Now;
    public decimal TotalCost { get; set; }
    public string? Notes { get; set; }
    public int CreatedByUserId { get; set; }
    public List<InventoryPurchaseLine> Lines { get; set; } = new();
}
