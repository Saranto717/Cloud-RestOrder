namespace RestaurantOrders.Models;

public class RestaurantTable
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    // Internal point of sale used to keep each Take Away basket isolated from dining tables.
    public bool IsTakeAway { get; set; }
    public int SortOrder { get; set; } = 0;

    public int? TableAreaId { get; set; }
    public TableArea? TableArea { get; set; }

    // Θέση στην ελεύθερη κάτοψη (ποσοστό 0-100 του πλάτους/ύψους του καμβά). Null = δεν έχει τοποθετηθεί ακόμα.
    public double? PosX { get; set; }
    public double? PosY { get; set; }

    public string TileBackgroundColor { get; set; } = "#ffffff";
    public string TileTextColor { get; set; } = "#1f2a44";
    public string TileBorderColor { get; set; } = "#d8dde6";
    public int TileFontSize { get; set; } = 16;
    public bool TileBold { get; set; } = true;
}
