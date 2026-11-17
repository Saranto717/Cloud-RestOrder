namespace RestaurantOrders.Models;

public sealed class InventoryPurchaseLine
{
    public int Id { get; set; }
    public int InventoryPurchaseId { get; set; }
    public InventoryPurchase? Purchase { get; set; }
    public int InventoryItemId { get; set; }
    public InventoryItem? InventoryItem { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }
}
