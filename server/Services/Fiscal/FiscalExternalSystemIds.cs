namespace RestaurantOrders.Services.Fiscal;

/// <summary>Builds stable prefixes used to correlate fiscal records with credit notes.</summary>
public static class FiscalExternalSystemIds
{
    public static string CreditDocumentPrefix(int orderId, int? orderPaymentId) =>
        $"credit-{orderId}-{orderPaymentId ?? 0}-";
}
