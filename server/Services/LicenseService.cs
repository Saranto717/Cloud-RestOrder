using System.Security.Cryptography;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public sealed class LicenseService : IDisposable
{
    private const string LegacyPublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAmBtfa486IU7LCw3LEpNX
fT82m4MXICfTPdCy7Dlb+tLseHNyRF/25lmIDWtnb7vCHVR83eleUc+Im56kHHmw
CB35+J/J7gB9+gUAqF6vQhp5t0qMPcE+AjEtZx/uKwhFwfN6GXjQ/qhjoTGkY1iS
Ddl70/bw0equntvlknVEMcntTcqj+k6HH9kBjAeqUH1U8WkCUuRe1914EjPpINeY
fcn/6TrFlL/STMfFlfMDQO895caIdLFrFJGCreUkBPmyExobWPCqss0Xv+NKV2Ed
qZOY/AJsXbyeQ8GRKhICVKnePNGHTC0OOOmXSLrTt0aOjoA5r+JnMDPTpvDPfm/c
WunvGD66HvAxVPl6Rlj1t6YcqKvR8KTU3/GAejzKSJU4pLZRHzCG4oNWMNulpYvV
jd9sN99z4AB9QKzdhv/OZUYIwLssw9Ao74yqUwGIgq2N3lfpXJedHbopDvLNYTJS
Fhz8tMU5ym5dWfMCwYemlhuA1rcdiHjmz6lOciqV8llxAgMBAAE=
-----END PUBLIC KEY-----
""";
    private const string RemoteSigningPublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAuJkOO1HIuJ6siuh0jg9s
pnle22spjhysPCXx6eucTLQYzo3MEZAb4vti6pOL6eWTwePPoFGZWKJOnLpf2mXM
VYpG2mJr/0CxmGXY5/3gL2FYb3TZR1vHY/7GDsqOklfNVEXwtxf1aNq+QZPHba3R
sTm5Si4f4vH6elEbCgcsTDX8U4+NVSGrBIrRwqf8jlaVNIQIWtYJ+oyTBa3Lny2y
dM5m6o78tFq1kZBt9vA1+P/BvnCteT3t8aCOcU8y+1ytCHqdfzg9fwEfUiH7fxpd
2tTUP4y83I39dO+2mCuHxCtlvRYPnscYUMKZT07j3HMWtVAkGKQv2xKU2kB+n1wc
j8EfsTO+NkhitRrPfLKJZtI6VlHflgcOnpQJTr7SGcQ9JGihZ1PTLyW6iXTr8fSj
i+E6qcglsw1/v+/2m31O5Ffvk+deBrkF3uEge5gBrSutpHZbB692xZpGWfxTSP0K
3FMUwfcP17w9ZI7aMwViF09nwi4bn613aKLlGoJ+W+c7AgMBAAE=
-----END PUBLIC KEY-----
""";
    private const string NewRemoteSigningPublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAx3KQM3nBGDTMXZlhAVNu
/TP6PeXNProNPGm2QgnRmGf8zWKpzftLsiLtWAiEqBmahVN8PkosP7E1eAl24hiz
cAdZ2ezXullicKq2MM+BvZ2lgcwxsMlYHjU713rxGJ0HoPaaPPPAIT9UE7FOKGat
ORIM0a6PqVwieKonEXsHXF+g+hApCYQvGjtTlKB95Y7ab1aMt83ucXUBjcf9NLNY
RJFktkmKdVlGqkb3a4ttBRDq9cyAc0aNOiWWtGHNklxwwoKmwRgEiL87HfDbnsFU
GVD77hQPNRnj9z7uulhRIpHTte9fXcduclhhbo+J59kL+IZTUMR7GDt80J6i/1Md
cmq88QMkXS0E6nr/kUekubpBTYZo4IkxlfFnErnZekFFu8yGRY5A6J6aYg0+vHZu
umbGpGj8H1hguSKNVPM4vNA0c3t7ALGywjgK3q1u+if4PEIDt8ZstN89uUG1zrbi
JWVTs6Z9vUYkn/tTY2kwUDNonypY+n5UGoZTxfvBxch5AgMBAAE=
-----END PUBLIC KEY-----
""";
    private static readonly string[] PublicKeyPems = [LegacyPublicKeyPem, RemoteSigningPublicKeyPem, NewRemoteSigningPublicKeyPem];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string? dataDirectory;
    private readonly string installationId;
    private readonly ECDsa requestSigningKey;
    private readonly string requestPublicKeyBase64;
    private readonly string licensePath;
    private readonly string? licenseServerPublicKeyPath;
    private readonly string machineName;
    private readonly string macAddress;
    private readonly string licenseServerUrl;

    public bool EnforcementEnabled => !string.IsNullOrWhiteSpace(dataDirectory);
    public string InstallationId => installationId;
    public string RequestPublicKeyBase64 => requestPublicKeyBase64;
    public string MachineName => machineName;
    public string MacAddress => macAddress;
    public string LicenseServerUrl => licenseServerUrl;
    public bool RemoteLicensingConfigured => IsAllowedLicenseServerUrl(licenseServerUrl);

    public LicenseService(string? installedDataDirectory, IConfiguration? configuration = null)
    {
        licenseServerUrl = configuration?["License:ServiceUrl"]?.Trim().TrimEnd('/') ?? "https://texn-support.ddns.net";
        dataDirectory = string.IsNullOrWhiteSpace(installedDataDirectory) ? null : Path.GetFullPath(installedDataDirectory);
        if (dataDirectory is null)
        {
            installationId = "DEVELOPMENT";
            requestSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            requestPublicKeyBase64 = Convert.ToBase64String(requestSigningKey.ExportSubjectPublicKeyInfo());
            licensePath = "";
            licenseServerPublicKeyPath = null;
            machineName = Environment.MachineName;
            macAddress = GetStableMacAddress();
            return;
        }

        Directory.CreateDirectory(dataDirectory);
        var idPath = Path.Combine(dataDirectory, "installation.id");
        if (!File.Exists(idPath))
            File.WriteAllText(idPath, Guid.NewGuid().ToString("D"));
        installationId = File.ReadAllText(idPath).Trim();
        if (!Guid.TryParse(installationId, out _))
        {
            installationId = Guid.NewGuid().ToString("D");
            File.WriteAllText(idPath, installationId);
        }
        requestSigningKey = LoadOrCreateRequestSigningKey(dataDirectory);
        requestPublicKeyBase64 = Convert.ToBase64String(requestSigningKey.ExportSubjectPublicKeyInfo());
        licensePath = Path.Combine(dataDirectory, "license.json");
        licenseServerPublicKeyPath = Path.Combine(dataDirectory, "license-server-public-key.pem");
        machineName = Environment.MachineName;
        macAddress = GetStableMacAddress();
    }

    public string SignLicenseCheckRequest(string timestamp, string nonce, byte[] bodyBytes)
    {
        var installationGuid = Guid.Parse(installationId);
        var bodyHash = Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();
        var canonical = $"POST\n/api/license/v1/check\n{installationGuid:D}\n{timestamp}\n{nonce}\n{bodyHash}";
        lock (requestSigningKey)
        {
            var signature = requestSigningKey.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return Convert.ToBase64String(signature);
        }
    }

    public void RemoveLegacyBearerTokenFile()
    {
        if (dataDirectory is null) return;
        try
        {
            File.Delete(Path.Combine(dataDirectory, "license-client.token"));
        }
        catch (IOException)
        {
            // The signing key is already active; a stale compatibility token does not affect licensing.
        }
        catch (UnauthorizedAccessException)
        {
            // The service may not be able to remove an old file created with different permissions.
        }
    }

    private static ECDsa LoadOrCreateRequestSigningKey(string dataDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Τα κλειδιά εγκατάστασης αδειών δημιουργούνται σε Windows.");

        var keyPath = Path.Combine(dataDirectory, "license-client-key.dpapi");
        if (File.Exists(keyPath))
        {
            var encrypted = File.ReadAllBytes(keyPath);
            var privateKeyBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
            try
            {
                var key = ECDsa.Create();
                key.ImportPkcs8PrivateKey(privateKeyBytes, out var bytesRead);
                if (bytesRead != privateKeyBytes.Length || key.KeySize != 256)
                {
                    key.Dispose();
                    throw new InvalidDataException("Το αποθηκευμένο κλειδί εγκατάστασης δεν είναι έγκυρο.");
                }
                return key;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKeyBytes);
            }
        }

        var createdKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rawPrivateKey = createdKey.ExportPkcs8PrivateKey();
        try
        {
            var encrypted = ProtectedData.Protect(rawPrivateKey, null, DataProtectionScope.LocalMachine);
            using var stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(encrypted);
            stream.Flush(flushToDisk: true);
            return createdKey;
        }
        catch
        {
            createdKey.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawPrivateKey);
        }
    }

    public LicenseStatus GetStatus()
    {
        if (!EnforcementEnabled)
            return new(true, installationId, machineName, macAddress, "Development", null, null);
        if (!File.Exists(licensePath))
            return new(false, installationId, machineName, macAddress, null, null, "Δεν έχει ενεργοποιηθεί άδεια χρήσης.");

        try
        {
            var signed = JsonSerializer.Deserialize<SignedLicense>(File.ReadAllText(licensePath), JsonOptions)
                ?? throw new InvalidDataException("Το αρχείο άδειας είναι κενό.");
            var payload = Verify(signed);
            var now = DateTime.UtcNow;
            if (!string.Equals(payload.InstallationId, installationId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Η άδεια έχει εκδοθεί για διαφορετική εγκατάσταση.");
            if (!string.Equals(payload.MachineName, machineName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Άλλαξε το όνομα του υπολογιστή. Χρειάζεται νέα άδεια για αυτόν τον υπολογιστή.");
            if (!string.Equals(NormalizeMac(payload.MacAddress), NormalizeMac(macAddress), StringComparison.Ordinal))
                throw new InvalidDataException("Άλλαξε η MAC address του υπολογιστή. Χρειάζεται νέα άδεια για αυτή τη συσκευή.");
            if (payload.IssuedAtUtc > now.AddMinutes(5))
                throw new InvalidDataException("Η ημερομηνία έκδοσης της άδειας είναι στο μέλλον.");
            if (payload.ExpiresAtUtc is not null && now > payload.ExpiresAtUtc.Value)
                throw new InvalidDataException("Η άδεια χρήσης έχει λήξει.");
            if (payload.SchemaVersion >= 2)
            {
                if (!string.Equals(payload.RemoteStatus, "active", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Η άδεια χρήσης έχει ανακληθεί από τον διαχειριστή. Επικοινώνησε με τον προμηθευτή.");
                if (payload.LeaseExpiresAtUtc is null || now > payload.LeaseExpiresAtUtc.Value)
                    throw new InvalidDataException("Χρειάζεται σύνδεση στο Internet για ανανέωση της άδειας χρήσης.");
            }
            return new(true, installationId, machineName, macAddress, payload.CustomerName, payload.ExpiresAtUtc, null);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or InvalidDataException or FormatException or ArgumentException)
        {
            return new(false, installationId, machineName, macAddress, null, null, ex.Message);
        }
    }

    public async Task<LicenseStatus> ApplyRemoteLicenseAsync(SignedLicense signed, bool allowFirstServerKeyPin = false,
        CancellationToken cancellationToken = default)
    {
        if (!EnforcementEnabled) throw new InvalidOperationException("Η απομακρυσμένη άδεια ισχύει μόνο σε εγκατεστημένη έκδοση.");
        var payload = Verify(signed, allowFirstServerKeyPin && IsHttpsLicenseServer(licenseServerUrl));
        if (payload.SchemaVersion < 2)
            throw new InvalidDataException("Ο server αδειών επέστρεψε παλιά μορφή άδειας.");
        ValidateForThisInstallation(payload);
        var tempPath = licensePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(signed, JsonOptions), cancellationToken);
        File.Move(tempPath, licensePath, overwrite: true);
        return GetStatus();
    }

    public Task<LicenseStatus> ClearCachedLicenseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!EnforcementEnabled)
            throw new InvalidOperationException("Η απομακρυσμένη άδεια ισχύει μόνο σε εγκατεστημένη έκδοση.");

        File.Delete(licensePath);
        return Task.FromResult(GetStatus());
    }

    private void ValidateForThisInstallation(LicensePayload payload)
    {
        if (!string.Equals(payload.InstallationId, installationId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Αυτή η άδεια προορίζεται για διαφορετική εγκατάσταση.");
        if (!string.Equals(payload.MachineName, machineName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Το όνομα υπολογιστή της άδειας δεν ταιριάζει.");
        if (!string.Equals(NormalizeMac(payload.MacAddress), NormalizeMac(macAddress), StringComparison.Ordinal))
            throw new InvalidDataException("Η MAC address της άδειας δεν ταιριάζει.");
    }

    private LicensePayload Verify(SignedLicense signed, bool allowFirstServerKeyPin = false)
    {
        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Convert.FromBase64String(signed.Payload);
            signature = Convert.FromBase64String(signed.Signature);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Μη έγκυρη μορφή αρχείου άδειας.", ex);
        }

        var publicKeys = new List<string>(PublicKeyPems);
        if (!string.IsNullOrWhiteSpace(licenseServerPublicKeyPath) && File.Exists(licenseServerPublicKeyPath))
            publicKeys.Add(File.ReadAllText(licenseServerPublicKeyPath));

        var validSignature = false;
        foreach (var publicKeyPem in publicKeys)
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(publicKeyPem);
                if (rsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    validSignature = true;
                    break;
                }
            }
            catch (CryptographicException)
            {
                // A different key size can reject the signature length; try the next trusted public key.
            }
        }
        if (!validSignature && allowFirstServerKeyPin &&
            (string.IsNullOrWhiteSpace(licenseServerPublicKeyPath) || !File.Exists(licenseServerPublicKeyPath)) &&
            !string.IsNullOrWhiteSpace(signed.PublicKeyPem))
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(signed.PublicKeyPem);
                if (rsa.KeySize >= 2048 && rsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    SavePinnedServerKey(signed.PublicKeyPem);
                    validSignature = true;
                }
            }
            catch (CryptographicException)
            {
                // The first HTTPS response must carry a valid RSA public key and signature.
            }
        }
        if (!validSignature)
            throw new InvalidDataException("Η υπογραφή της άδειας δεν είναι έγκυρη.");

        var payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes, JsonOptions)
            ?? throw new InvalidDataException("Λείπουν τα στοιχεία της άδειας.");
        if (payload.SchemaVersion is not (1 or 2) || string.IsNullOrWhiteSpace(payload.LicenseId) || string.IsNullOrWhiteSpace(payload.CustomerName))
            throw new InvalidDataException("Το αρχείο άδειας δεν περιέχει έγκυρα στοιχεία.");
        return payload;
    }

    private void SavePinnedServerKey(string publicKeyPem)
    {
        if (licenseServerPublicKeyPath is null) throw new InvalidOperationException("Δεν είναι διαθέσιμη η διαδρομή αποθήκευσης κλειδιού.");
        var tempPath = licenseServerPublicKeyPath + ".tmp";
        File.WriteAllText(tempPath, publicKeyPem);
        File.Move(tempPath, licenseServerPublicKeyPath, overwrite: false);
    }

    private static string NormalizeMac(string value) => new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool IsAllowedLicenseServerUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (uri.Scheme == Uri.UriSchemeHttps)
            return string.Equals(uri.Host, "texn-support.ddns.net", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme != Uri.UriSchemeHttp)
            return false;
        if (uri.IsLoopback)
            return true;
        if (!IPAddress.TryParse(uri.Host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private static bool IsHttpsLicenseServer(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "texn-support.ddns.net", StringComparison.OrdinalIgnoreCase);

    private static string GetStableMacAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .Select(n => new { n.Id, n.Name, n.Description, Mac = n.GetPhysicalAddress().GetAddressBytes() })
            .Where(n => n.Mac.Length == 6 && n.Mac.Any(b => b != 0) && (n.Mac[0] & 1) == 0)
            .OrderBy(n => IsVirtualAdapter(n.Name, n.Description) ? 1 : 0)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return candidates is null ? "" : string.Join(":", candidates.Mac.Select(b => b.ToString("X2")));
    }

    private static bool IsVirtualAdapter(string name, string description)
    {
        var text = $"{name} {description}";
        return new[] { "virtual", "vmware", "hyper-v", "virtualbox", "tap-", "vpn", "wsl", "docker", "loopback" }
            .Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => requestSigningKey.Dispose();
}
