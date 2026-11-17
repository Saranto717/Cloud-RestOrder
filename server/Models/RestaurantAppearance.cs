namespace RestaurantOrders.Models;

public class RestaurantAppearance
{
    public int Id { get; set; } = 1;
    public string DisplayName { get; set; } = "Παραγγελιοληψία";
    public string? BackgroundImageDataUri { get; set; }
    public bool ShowOccupiedTableTimer { get; set; }

    // Ενιαίο μέγεθος πλακιδίων τραπεζιών για όλο το κατάστημα (px) και σειρές ανά περιοχή.
    // Κάθε περιοχή μπορεί να τα αλλάξει (TableArea) και κάθε συσκευή να τα κλιμακώσει τοπικά.
    public int TableTileWidth { get; set; } = 140;
    public int TableTileHeight { get; set; } = 110;
    public int TableRows { get; set; } = 3;
}
