using System.Security.Cryptography;
using System.Text.Json;

namespace RestaurantOrders.Cloud.Api;

public sealed class LicenseSigner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string privateKeyPem;
    private readonly string publicKeyPem;

    public LicenseSigner(string privateKeyPem)
    {
        this.privateKeyPem = privateKeyPem;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        if (rsa.KeySize < 2048)
            throw new InvalidOperationException("The remote license signing key must be at least 2048 bits.");
        publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
    }

    public SignedLicenseContract Sign(CloudLicensePayload payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new SignedLicenseContract
        {
            Payload = Convert.ToBase64String(payloadBytes),
            Signature = Convert.ToBase64String(signature),
            PublicKeyPem = publicKeyPem
        };
    }

    public static string LoadOrCreatePrivateKey(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath)) return File.ReadAllText(fullPath);

        using var rsa = RSA.Create(3072);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        var temporaryPath = fullPath + ".tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(pem);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try { File.Move(temporaryPath, fullPath, overwrite: false); }
        catch (IOException) when (File.Exists(fullPath)) { File.Delete(temporaryPath); }
        return File.ReadAllText(fullPath);
    }
}
