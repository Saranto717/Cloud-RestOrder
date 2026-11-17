using RestaurantOrders.Services;

namespace RestaurantOrders.Services.Fiscal;

/// <summary>Confirms that a completed lookup refers to the stored fiscal document.</summary>
public static class FiscalDocumentIdentity
{
    public static bool IsConfirmed(ParochosSendResult result, string expectedExternalId, string? expectedProcessId, string? expectedUid)
    {
        static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        if (Present(result.ExternalSystemId) && !string.Equals(result.ExternalSystemId, expectedExternalId, StringComparison.Ordinal)) return false;
        if (Present(expectedProcessId) && Present(result.ProcessId) && !Same(result.ProcessId, expectedProcessId)) return false;
        if (Present(expectedUid) && Present(result.Uid) && !Same(result.Uid, expectedUid)) return false;

        return Present(result.ExternalSystemId) && string.Equals(result.ExternalSystemId, expectedExternalId, StringComparison.Ordinal)
            || Present(expectedProcessId) && Present(result.ProcessId) && Same(result.ProcessId, expectedProcessId)
            || Present(expectedUid) && Present(result.Uid) && Same(result.Uid, expectedUid);
    }
}
