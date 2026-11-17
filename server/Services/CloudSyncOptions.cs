namespace RestaurantOrders.Services;

public sealed class CloudSyncOptions
{
    public bool Enabled { get; set; }
    public string ApiBaseUrl { get; set; } = "https://texn-support.ddns.net/";
    public string StoreTokenProtected { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 15;

    public bool IsConfigured => Enabled &&
        Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        StoreTokenProtected.Length is >= 32 and <= 8192;
}
