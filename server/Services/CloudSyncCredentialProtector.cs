using Microsoft.AspNetCore.DataProtection;

namespace RestaurantOrders.Services;

public sealed class CloudSyncCredentialProtector(IDataProtectionProvider dataProtectionProvider)
{
    public string Protect(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Το StoreToken είναι κενό.", nameof(token));
        return ProtectValue("StoreToken", token);
    }

    public string Unprotect(string protectedToken)
    {
        return UnprotectValue("StoreToken", protectedToken);
    }

    public string ProtectDeviceSecret(string secret) => ProtectValue("DeviceSecret", secret);
    public string UnprotectDeviceSecret(string protectedSecret) => UnprotectValue("DeviceSecret", protectedSecret);

    private string ProtectValue(string purpose, string value) => Convert.ToBase64String(
        dataProtectionProvider.CreateProtector($"RestaurantOrders.CloudSync.{purpose}.v1")
            .Protect(System.Text.Encoding.UTF8.GetBytes(value)));

    private string UnprotectValue(string purpose, string protectedValue)
    {
        var bytes = Convert.FromBase64String(protectedValue);
        var clear = dataProtectionProvider.CreateProtector($"RestaurantOrders.CloudSync.{purpose}.v1").Unprotect(bytes);
        return System.Text.Encoding.UTF8.GetString(clear);
    }
}
