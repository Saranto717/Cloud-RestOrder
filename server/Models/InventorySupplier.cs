namespace RestaurantOrders.Models;

public sealed class InventorySupplier
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? VatNumber { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}
