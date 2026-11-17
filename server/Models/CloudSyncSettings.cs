namespace RestaurantOrders.Models;

/// <summary>Per-installation cloud pairing and durable inbound cursor.</summary>
public sealed class CloudSyncSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public string ApiBaseUrl { get; set; } = "https://texn-support.ddns.net/";
    public string ProtectedStoreToken { get; set; } = "";
    public string ProtectedDeviceSecret { get; set; } = "";
    public bool DeviceRegistered { get; set; }
    public long LastCloudCursor { get; set; }
    public DateTimeOffset? LastSuccessfulSyncAtUtc { get; set; }
    public string? LastSyncError { get; set; }
}
