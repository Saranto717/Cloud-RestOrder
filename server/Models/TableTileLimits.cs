namespace RestaurantOrders.Models;

/// <summary>Όρια και υπολογισμός του μεγέθους πλακιδίων τραπεζιών (κατάστημα → περιοχή → συσκευή).</summary>
public static class TableTileLimits
{
    public const int MinWidth = 70, MaxWidth = 360, MinHeight = 56, MaxHeight = 300, MaxRows = 12;
    public const int MinDeviceScale = 50, MaxDeviceScale = 200;

    public static int Width(int value) => Math.Clamp(value <= 0 ? 140 : value, MinWidth, MaxWidth);
    public static int Height(int value) => Math.Clamp(value <= 0 ? 110 : value, MinHeight, MaxHeight);
    public static int Rows(int value) => Math.Clamp(value <= 0 ? 3 : value, 1, MaxRows);
    public static int Scale(int percent) => Math.Clamp(percent <= 0 ? 100 : percent, MinDeviceScale, MaxDeviceScale);
}
