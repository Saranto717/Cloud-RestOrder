namespace RestaurantOrders.Models;

public enum InventoryMovementKind { Purchase = 0, Consumption = 1, CancellationReturn = 2, Stocktake = 3 }

/// <summary>Immutable audit record. QuantityDelta is positive for stock-in, negative for stock-out.</summary>
public sealed class InventoryMovement
{
    public int Id { get; set; }
    public int InventoryItemId { get; set; }
    public InventoryItem? InventoryItem { get; set; }
    public InventoryMovementKind Kind { get; set; }
    public decimal QuantityDelta { get; set; }
    public decimal UnitCost { get; set; }
    public int? OrderItemId { get; set; }
    public int? RelatedMovementId { get; set; }
    public int? InventoryPurchaseId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int CreatedByUserId { get; set; }
    public string? Note { get; set; }
}
