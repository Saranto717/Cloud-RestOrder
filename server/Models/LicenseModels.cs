namespace RestaurantOrders.Models;

public sealed class LicensePayload
{
    public int SchemaVersion { get; set; } = 1;
    public string LicenseId { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string VatNumber { get; set; } = "";
    public string InstallationId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public DateTime IssuedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string RemoteStatus { get; set; } = "active";
    public DateTime? LeaseExpiresAtUtc { get; set; }
}

public sealed class SignedLicense
{
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
    public string PublicKeyPem { get; set; } = "";
}

public sealed record LicenseStatus(bool IsLicensed, string InstallationId, string MachineName, string MacAddress,
    string? CustomerName, DateTime? ExpiresAtUtc, string? Error);

public sealed record RemoteLicenseCheckRequest(string InstallationId, string MachineName, string MacAddress);
public sealed class RemoteLicenseCheckResponse
{
    public string Status { get; set; } = "pending";
    public SignedLicense? License { get; set; }
    public string? Message { get; set; }
}
public sealed record RemoteLicenseCheckResult(bool ContactedServer, string Status, string? Message, LicenseStatus License);
