namespace RestaurantOrders.Services;

public enum PreloadedPosState { Pending, Paid, Cancelled, Unknown }

public sealed record PreloadedPosStatusResult(PreloadedPosState State, string Message,
    string? TransactionId = null, int? PaymentType = null,
    RestaurantOrders.Models.PaymentMethod? PaymentMethod = null);
