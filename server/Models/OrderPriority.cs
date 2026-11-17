namespace RestaurantOrders.Models;

public static class OrderPriority
{
    public static string Normalize(string? priority) => priority is "ΠΡΩΤΑ" or "ΔΕΥΤΕΡΑ" or "ΤΡΙΤΑ"
        ? priority
        : "";

    public static int Rank(string? priority) => priority switch
    {
        "ΠΡΩΤΑ" => 1,
        "ΔΕΥΤΕΡΑ" => 2,
        "ΤΡΙΤΑ" => 3,
        _ => 4
    };
}
