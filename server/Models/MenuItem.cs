namespace RestaurantOrders.Models;

public class MenuItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public decimal StaffConsumptionPrice { get; set; } = 0.10m;
    public bool IsAvailable { get; set; } = true;

    public int? MenuCategoryId { get; set; }
    public MenuCategory? MenuCategory { get; set; }

    // Προαιρετική παράκαμψη ΦΠΑ - αν είναι null, χρησιμοποιείται το ΦΠΑ της κατηγορίας.
    public decimal? VatPercentOverride { get; set; }
    public string? VatCategoryCodeOverride { get; set; }

    // myDATA 8.13: 1=Τεμάχια (σταθερή προεπιλογή), 2=Κιλά, 3=Λίτρα.
    public int MeasurementUnitCode { get; set; } = 1;
    public int DisplayOrder { get; set; }
    // 0=μικρό, 1=κανονικό, 2=μεγάλο. Τα νέα είδη ξεκινούν όλα στο ίδιο μέγεθος.
    public int TileSize { get; set; } = 1;
    public double TileScale { get; set; } = 1.0;
}
