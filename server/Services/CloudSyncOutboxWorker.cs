using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;
using RestaurantOrders.Services.Fiscal;
using RestaurantOrders.Services.Orders;

namespace RestaurantOrders.Services;

public sealed class CloudSyncOutboxWorker(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    CloudSyncCredentialProtector credentialProtector,
    CloudSyncInitialSnapshotSeeder initialSnapshotSeeder,
    LicenseService licenseService,
    CloudSyncRuntimeState runtimeState,
    IServiceScopeFactory scopeFactory,
    ILogger<CloudSyncOutboxWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int MaxSyncBatchBytes = 4 * 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await ReadSettingsAsync(stoppingToken);
                runtimeState.SetEnabled(settings?.Enabled == true);
                if (settings is { Enabled: true })
                {
                    try
                    {
                        var pending = await SynchronizeOnceAsync(settings, stoppingToken);
                        await SetSyncStatusAsync(pending == 0 ? null : $"{pending} αλλαγές περιμένουν τα συνδεδεμένα στοιχεία ή την αποστολή τοπικών αλλαγών.",
                            registered: null, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception exception)
                    {
                        var message = exception.Message.Length > 1500 ? exception.Message[..1500] : exception.Message;
                        await SetSyncStatusAsync(message, registered: null, stoppingToken);
                        logger.LogWarning(exception, "Cloud sync failed. Local POS data remains available and unsent changes remain queued.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not read the local cloud-sync configuration.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task<int> SynchronizeOnceAsync(CloudSyncSettings settings, CancellationToken cancellationToken)
    {
        if (!licenseService.EnforcementEnabled || !Guid.TryParse(licenseService.InstallationId, out var installationId))
            throw new InvalidOperationException("Η σύνδεση cloud είναι διαθέσιμη μόνο στην εγκατεστημένη έκδοση Windows.");
        if (!Uri.TryCreate(settings.ApiBaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(baseUri.UserInfo))
            throw new InvalidOperationException("Η διεύθυνση cloud πρέπει να είναι έγκυρη διεύθυνση HTTPS.");
        baseUri = new Uri(baseUri.ToString().TrimEnd('/') + "/", UriKind.Absolute);
        if (string.IsNullOrWhiteSpace(settings.ProtectedStoreToken))
            throw new InvalidOperationException("Βάλε το Store API token που δημιουργήθηκε από το License Manager.");

        var storeToken = credentialProtector.Unprotect(settings.ProtectedStoreToken);
        var deviceSecret = string.IsNullOrWhiteSpace(settings.ProtectedDeviceSecret)
            ? Base64Url(RandomNumberGenerator.GetBytes(32))
            : credentialProtector.UnprotectDeviceSecret(settings.ProtectedDeviceSecret);
        if (string.IsNullOrWhiteSpace(settings.ProtectedDeviceSecret))
        {
            settings.ProtectedDeviceSecret = credentialProtector.ProtectDeviceSecret(deviceSecret);
            await SaveDeviceSecretAsync(settings, cancellationToken);
        }

        var firstPairing = !settings.DeviceRegistered;
        if (firstPairing)
        {
            await RegisterDeviceAsync(baseUri, storeToken, installationId, deviceSecret, cancellationToken);
            settings.DeviceRegistered = true;
            await SetSyncStatusAsync(null, registered: true, cancellationToken);
        }

        if (firstPairing && await CloudHasStoreDataAsync(baseUri, installationId, deviceSecret, cancellationToken))
        {
            // The cloud database is the canonical store. A newly paired Windows
            // SQLite installation imports it instead of publishing its stale local
            // baseline over the cloud. Local rows remain available on that PC.
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await initialSnapshotSeeder.MarkInitialSnapshotAsCloudAuthoritativeAsync(db, cancellationToken);
        }
        else
        {
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
                await initialSnapshotSeeder.EnsureInitialSnapshotAsync(db, cancellationToken);

            await PushPendingEventsAsync(baseUri, installationId, deviceSecret, cancellationToken);
        }

        var pending = await PullAndApplyAsync(baseUri, installationId, deviceSecret, settings.LastCloudCursor, cancellationToken);
        await ProcessIncomingCloudDispatchAsync(cancellationToken);
        return pending;
    }

    private async Task RegisterDeviceAsync(Uri baseUri, string storeToken, Guid installationId, string deviceSecret,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(baseUri, "api/sync/v1/devices/register");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new RegisterDeviceRequest(installationId.ToString("D"), deviceSecret), options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", storeToken);
        using var response = await CreateClient().SendAsync(request, cancellationToken);
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Η αρχική σύζευξη συσκευής απέτυχε (HTTP {(int)response.StatusCode}): {ShortDetail(detail)}");
    }

    private async Task<bool> CloudHasStoreDataAsync(Uri baseUri, Guid installationId, string deviceSecret,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(baseUri, "api/sync/v1/entities?afterId=0&limit=1");
        using var request = CloudSyncRequestSigner.Create(HttpMethod.Get, endpoint, installationId, deviceSecret, []);
        using var response = await CreateClient().SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Έλεγχος αρχικών cloud δεδομένων: HTTP {(int)response.StatusCode}: {ShortDetail(responseText)}");
        var page = JsonSerializer.Deserialize<CloudSyncEntityPage>(responseText, JsonOptions)
            ?? throw new InvalidDataException("Το cloud επέστρεψε κενή αρχική σελίδα συγχρονισμού.");
        if (page.StoreId == Guid.Empty || page.NextCursor < 0)
            throw new InvalidDataException("Το cloud επέστρεψε αρχικά δεδομένα άλλου καταστήματος.");
        return page.Entities.Count > 0 || page.NextCursor > 0 || page.HasMore;
    }

    private async Task PushPendingEventsAsync(Uri baseUri, Guid installationId, string deviceSecret,
        CancellationToken cancellationToken)
    {
        List<CloudSyncOutboxEvent> batch;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            batch = await db.CloudSyncOutboxEvents.Where(item => item.AcknowledgedAtUtc == null)
                .OrderBy(item => item.Id).Take(100).ToListAsync(cancellationToken);
        }
        if (batch.Count == 0) return;

        var events = new List<CloudSyncPushEvent>(batch.Count);
        var body = JsonSerializer.SerializeToUtf8Bytes(new CloudSyncPushBatch(events), JsonOptions);
        foreach (var item in batch)
        {
            using var payload = JsonDocument.Parse(item.PayloadJson);
            var candidate = new CloudSyncPushEvent(item.EventId, item.EntityType, item.EntityId,
                item.EventType, item.SchemaVersion, item.OccurredAtUtc, payload.RootElement.Clone());
            var candidateEvents = events.Append(candidate).ToArray();
            var candidateBody = JsonSerializer.SerializeToUtf8Bytes(new CloudSyncPushBatch(candidateEvents), JsonOptions);
            if (candidateBody.Length > MaxSyncBatchBytes)
            {
                if (events.Count == 0)
                    throw new InvalidDataException("Μία εγγραφή cloud sync ξεπερνά το όριο των 4 MB.");
                break;
            }
            events.Add(candidate);
            body = candidateBody;
        }
        var endpoint = new Uri(baseUri, "api/sync/v1/events");
        using var request = CloudSyncRequestSigner.Create(HttpMethod.Post, endpoint, installationId, deviceSecret, body);
        using var response = await CreateClient().SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Αποστολή αλλαγών στο cloud: HTTP {(int)response.StatusCode}: {ShortDetail(responseText)}");

        var acknowledgement = JsonSerializer.Deserialize<CloudSyncPushAcknowledgement>(responseText, JsonOptions);
        if (acknowledgement is null || acknowledgement.Accepted + acknowledgement.Duplicates != events.Count)
            throw new InvalidDataException("Το cloud δεν επιβεβαίωσε όλα τα συμβάντα.");

        var eventIds = events.Select(item => item.EventId).ToHashSet();
        await using var updateDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var pending = await updateDb.CloudSyncOutboxEvents
            .Where(item => eventIds.Contains(item.EventId) && item.AcknowledgedAtUtc == null)
            .ToListAsync(cancellationToken);
        var acknowledgedAt = DateTimeOffset.UtcNow;
        foreach (var item in pending)
        {
            item.AttemptCount++;
            item.LastAttemptAtUtc = acknowledgedAt;
            item.AcknowledgedAtUtc = acknowledgedAt;
            item.LastError = null;
        }
        await updateDb.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> PullAndApplyAsync(Uri baseUri, Guid installationId, string deviceSecret,
        long currentCursor, CancellationToken cancellationToken)
    {
        var cursor = currentCursor;
        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            var endpoint = new Uri(baseUri, $"api/sync/v1/entities?afterId={cursor.ToString(CultureInfo.InvariantCulture)}&limit=100");
            using var request = CloudSyncRequestSigner.Create(HttpMethod.Get, endpoint, installationId, deviceSecret, []);
            using var response = await CreateClient().SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Λήψη αλλαγών από το cloud: HTTP {(int)response.StatusCode}: {ShortDetail(responseText)}");
            var page = JsonSerializer.Deserialize<CloudSyncEntityPage>(responseText, JsonOptions)
                ?? throw new InvalidDataException("Το cloud επέστρεψε κενή σελίδα συγχρονισμού.");
            if (page.NextCursor < cursor || page.NextCursor == cursor && page.Entities.Count > 0)
                throw new InvalidDataException("Ο δείκτης cloud sync δεν προχώρησε.");
            await CloudSyncImporter.ReceivePageAsync(dbFactory, page.Entities, page.NextCursor, cancellationToken);
            cursor = page.NextCursor;
            if (!page.HasMore || page.Entities.Count == 0) break;
        }
        return await CloudSyncImporter.ApplyPendingAsync(dbFactory, cancellationToken);
    }

    private async Task ProcessIncomingCloudDispatchAsync(CancellationToken cancellationToken)
    {
        await using var lookup = await dbFactory.CreateDbContextAsync(cancellationToken);
        var batchIds = await lookup.OrderPrintBatches
            .Where(batch => batch.WindowsPrintPending || batch.DispatchNotificationPending)
            .OrderBy(batch => batch.CreatedAt).Select(batch => batch.Id).ToListAsync(cancellationToken);
        foreach (var batchId in batchIds)
        {
            using var scope = scopeFactory.CreateScope();
            var printerDispatch = scope.ServiceProvider.GetRequiredService<KitchenOrderDispatchService>();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var batch = await db.OrderPrintBatches.AsNoTracking().SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken);
            if (batch is null) continue;
            if (batch.WindowsPrintPending)
                await printerDispatch.PrintAcceptedCloudBatchAsync(db, batchId);
            if (batch.DispatchNotificationPending)
            {
                var fiscalOrderForms = scope.ServiceProvider.GetRequiredService<FiscalOrderFormService>();
                await fiscalOrderForms.ConsumeBatchAsync(db, batchId);
            }
        }
    }

    private async Task<CloudSyncSettings?> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CloudSyncSettings.AsNoTracking().SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
    }

    private async Task SaveDeviceSecretAsync(CloudSyncSettings snapshot, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.SuppressCloudSyncCapture = true;
        var settings = await db.CloudSyncSettings.SingleAsync(item => item.Id == 1, cancellationToken);
        if (settings.ProtectedStoreToken != snapshot.ProtectedStoreToken || settings.ApiBaseUrl != snapshot.ApiBaseUrl)
            throw new InvalidOperationException("Οι ρυθμίσεις σύνδεσης άλλαξαν. Η σύζευξη θα επαναληφθεί.");
        settings.ProtectedDeviceSecret = snapshot.ProtectedDeviceSecret;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SetSyncStatusAsync(string? error, bool? registered, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.SuppressCloudSyncCapture = true;
        var settings = await db.CloudSyncSettings.SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (settings is null) return;
        settings.LastSyncError = error;
        if (registered is not null) settings.DeviceRegistered = registered.Value;
        if (error is null) settings.LastSuccessfulSyncAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private HttpClient CreateClient() => httpClientFactory.CreateClient(nameof(CloudSyncOutboxWorker));

    private static string ShortDetail(string text)
    {
        var safe = string.IsNullOrWhiteSpace(text) ? "χωρίς λεπτομέρειες" : text.Trim();
        return safe.Length > 800 ? safe[..800] : safe;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record RegisterDeviceRequest(string InstallationId, string DeviceSecret);
    private sealed record CloudSyncPushEvent(Guid EventId, string EntityType, string EntityId, string EventType,
        int SchemaVersion, DateTimeOffset OccurredAtUtc, JsonElement Payload);
    private sealed record CloudSyncPushBatch(IReadOnlyList<CloudSyncPushEvent> Events);
    private sealed record CloudSyncPushAcknowledgement(Guid StoreId, int Accepted, int Duplicates);
    private sealed record CloudSyncEntityPage(Guid StoreId, IReadOnlyList<CloudSyncEntity> Entities, long NextCursor, bool HasMore);
}

internal static class CloudSyncRequestSigner
{
    public static HttpRequestMessage Create(HttpMethod method, Uri endpoint, Guid installationId, string deviceSecret, byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = $"{method.Method.ToUpperInvariant()}\n{endpoint.PathAndQuery}\n{timestamp}\n{nonce}\n{bodyHash}";
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(deviceSecret));
        using var hmac = new HMACSHA256(key);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var request = new HttpRequestMessage(method, endpoint);
        if (method != HttpMethod.Get)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        request.Headers.TryAddWithoutValidation("X-Installation-Id", installationId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Cloud-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Cloud-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-Cloud-Content-SHA256", bodyHash);
        request.Headers.TryAddWithoutValidation("X-Cloud-Signature", signature);
        return request;
    }
}
