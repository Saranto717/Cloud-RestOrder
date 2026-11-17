namespace RestaurantOrders.Models;

/// <summary>
/// Συνδέει ένα Order Form 8.6 με τα συγκεκριμένα OrderItems που περιείχε.
/// Χρησιμοποιείται για split payment, ακυρώσεις και μεταφορές ειδών.
/// </summary>
public class FiscalOrderDocumentItem
{
    public int Id { get; set; }
    public int FiscalOrderDocumentId { get; set; }
    public FiscalOrderDocument? FiscalOrderDocument { get; set; }

    public int OrderItemId { get; set; }
    public OrderItem? OrderItem { get; set; }

    public int Quantity { get; set; }

    // Discount already included in this source 8.6 line, captured at issue time.
    public decimal DiscountPercentAtIssue { get; set; }

    // Gross value returned by a discount adjustment line (recType=7).
    public decimal DiscountReturnGrossAmount { get; set; }
}
