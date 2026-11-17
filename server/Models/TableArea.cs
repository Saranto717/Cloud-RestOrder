namespace RestaurantOrders.Models;

public class TableArea
{
    public int Id { get; set; }
    public string Name { get; set; } = ""; // π.χ. "Σάλα", "Πλατεία"

    // Προαιρετικό μέγεθος πλακιδίων/σειρές για την περιοχή. Null = ρύθμιση καταστήματος.
    public int? TileWidth { get; set; }
    public int? TileHeight { get; set; }
    public int? Rows { get; set; }
}
