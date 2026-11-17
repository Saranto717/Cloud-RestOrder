namespace RestaurantOrders.Models;

/// <summary>Stock item measured in myDATA units: 1=pieces, 2=kg, 3=litres.</summary>
public sealed class InventoryItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int MeasurementUnitCode { get; set; } = 1;
    public decimal QuantityOnHand { get; set; }
    public decimal MinimumQuantity { get; set; }
    public decimal UnitCost { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
