using Npgsql;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RestaurantOrders.Services;

/// <summary>
/// Provisions one PostgreSQL database per cloud store. The shared cloud API
/// database remains the control plane and sync journal; POS data lives in the
/// store-specific databases created here.
/// </summary>
public sealed class CloudTenantPostgresDatabase
{
    private readonly NpgsqlConnectionStringBuilder _baseConnection;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _syncBaseUrl;
    private readonly string _syncToken;
    private static readonly ConcurrentDictionary<Guid, string> ProvisionedStores = new();
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ProvisioningLocks = new();

    public CloudTenantPostgresDatabase(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        var passwordFile = configuration["CloudWeb:DatabasePasswordFile"];
        var password = string.IsNullOrWhiteSpace(passwordFile)
            ? configuration["CloudWeb:DatabasePassword"]
            : File.ReadAllText(passwordFile).TrimEnd('\r', '\n');

        _baseConnection = new NpgsqlConnectionStringBuilder
        {
            Host = configuration["CloudWeb:DatabaseHost"] ?? "postgres",
            Port = configuration.GetValue("CloudWeb:DatabasePort", 5432),
            Database = configuration["CloudWeb:DatabaseName"] ?? "restaurant_cloud",
            Username = configuration["CloudWeb:DatabaseUser"] ?? "restaurant_cloud_app",
            Password = password,
            Timeout = 15,
            CommandTimeout = 30,
            ApplicationName = "RestaurantOrders.CloudWeb"
        };

        if (string.IsNullOrWhiteSpace(_baseConnection.Password))
            throw new InvalidOperationException("Cloud web PostgreSQL password is not configured.");

        _syncBaseUrl = (configuration["CloudWeb:SyncBaseUrl"] ?? "http://cloud-api:8080/").TrimEnd('/') + "/";
        var syncTokenFile = configuration["CloudWeb:SyncTokenFile"];
        _syncToken = string.IsNullOrWhiteSpace(syncTokenFile)
            ? configuration["CloudWeb:SyncToken"] ?? ""
            : File.ReadAllText(syncTokenFile).TrimEnd('\r', '\n');
        if (Encoding.UTF8.GetByteCount(_syncToken) < 32)
            throw new InvalidOperationException("Cloud web sync token must contain at least 32 bytes of secret material.");
    }

    public string GetConnectionString(Guid storeId)
    {
        if (!ProvisionedStores.TryGetValue(storeId, out var databaseName))
            throw new InvalidOperationException("Το όνομα της cloud βάσης δεν έχει ανακτηθεί από τον server.");
        var builder = new NpgsqlConnectionStringBuilder(_baseConnection.ConnectionString)
        {
            Database = databaseName
        };
        return builder.ConnectionString;
    }

    public async Task<string> EnsureDatabaseExistsAsync(Guid storeId, CancellationToken cancellationToken = default)
    {
        if (ProvisionedStores.TryGetValue(storeId, out var existing)) return existing;
        var tenantLock = ProvisioningLocks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));
        await tenantLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ProvisionedStores.TryGetValue(storeId, out existing)) return existing;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(new Uri(_syncBaseUrl), $"api/internal/cloud-web/stores/{storeId:D}/database"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _syncToken);
            using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (detail.Length > 800) detail = detail[..800];
                throw new InvalidOperationException($"Cloud PostgreSQL database provisioning failed with HTTP {(int)response.StatusCode}: {detail}");
            }
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var databaseName = document.RootElement.TryGetProperty("database", out var databaseProperty)
                ? databaseProperty.GetString() : null;
            if (string.IsNullOrWhiteSpace(databaseName) || databaseName.Length > 63 ||
                databaseName.Any(character => !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
                throw new InvalidOperationException("Ο server επέστρεψε μη έγκυρο όνομα cloud βάσης.");
            ProvisionedStores[storeId] = databaseName;
            return databaseName;
        }
        finally { tenantLock.Release(); }
    }

    public async Task<bool> HasRegisteredPrintAgentAsync(Guid storeId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(_syncBaseUrl), $"api/internal/cloud-web/stores/{storeId:D}/print-agent"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _syncToken);
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (detail.Length > 800) detail = detail[..800];
            throw new InvalidOperationException($"Cloud print-agent lookup failed with HTTP {(int)response.StatusCode}: {detail}");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("hasWindowsPrintAgent", out var property) && property.GetBoolean();
    }

}
