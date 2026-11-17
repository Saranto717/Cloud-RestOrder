namespace RestaurantOrders.Models;

public class OrderPrintBatch
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    // Αύξων αριθμός παραγγελίας για όλα τα τραπέζια μέσα στο τρέχον ταμειακό/ημερήσιο κύκλο.
    public int Number { get; set; }

    // Ημερήσιος αύξων αριθμός που εμφανίζεται στο χαρτί παραγγελίας.
    public int DailyNumber { get; set; }

    // Durable notification of a newly accepted table batch, consumed by the fiscal workflow.
    public string? AcceptedLinesJson { get; set; }
    public string? AcceptedTableName { get; set; }
    public bool DispatchNotificationPending { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<OrderItem> Items { get; set; } = new();
}
