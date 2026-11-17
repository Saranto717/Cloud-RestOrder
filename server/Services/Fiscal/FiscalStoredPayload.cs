using System.Text.Json;

namespace RestaurantOrders.Services.Fiscal;

public static class FiscalStoredPayload
{
    // The persisted fiscal document is a snapshot; the live order may have changed.
    public static decimal? Total(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
                root = source;
            if (!root.TryGetProperty("invoice", out var invoice) || invoice.ValueKind != JsonValueKind.Object ||
                !invoice.TryGetProperty("invoiceSummary", out var summary) || summary.ValueKind != JsonValueKind.Object ||
                !summary.TryGetProperty("totalValue", out var total) || total.ValueKind != JsonValueKind.Number ||
                !total.TryGetDecimal(out var result)) return null;
            return result;
        }
        catch (JsonException) { return null; }
    }
}
