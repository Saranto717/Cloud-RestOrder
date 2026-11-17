namespace RestaurantOrders.Models;

/// <summary>Quantity of one stock ingredient consumed by one catalog item.</summary>
public sealed class InventoryRecipeLine
{
    public int Id { get; set; }
    public int MenuItemId { get; set; }
    public MenuItem? MenuItem { get; set; }
    public int InventoryItemId { get; set; }
    public InventoryItem? InventoryItem { get; set; }
    public decimal QuantityPerProduct { get; set; }
}
