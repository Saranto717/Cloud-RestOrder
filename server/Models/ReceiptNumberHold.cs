using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace RestaurantOrders.Models;

[Index(nameof(SeriesName), nameof(Number), IsUnique = true)]
public sealed class ReceiptNumberHold
{
    [Key] public string ExternalId { get; set; } = "";
    public int OrderId { get; set; }
    public string SeriesName { get; set; } = "";
    public long Number { get; set; }
}
