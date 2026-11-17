namespace RestaurantOrders.Models;

/// <summary>Immutable audit line for a cancellation or forced table release.</summary>
public class CancellationAuditEntry
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int? OrderItemId { get; set; }
    public int? UserId { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
    public string TableName { get; set; } = "";
    public string Action { get; set; } = "Ακύρωση είδους";
    public string Description { get; set; } = "";
    public string Reason { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
}
