using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;
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
                        await SynchronizeOnceAsync(settings, stoppingToken);
                        await SetSyncStatusAsync(null, registered: null, stoppingToken);
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

    private async Task SynchronizeOnceAsync(CloudSyncSettings settings, CancellationToken cancellationToken)
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
            await SaveSettingsAsync(settings, cancellationToken);
        }

        if (!settings.DeviceRegistered)
        {
            await RegisterDeviceAsync(baseUri, storeToken, installationId, deviceSecret, cancellationToken);
            settings.DeviceRegistered = true;
            await SetSyncStatusAsync(null, registered: true, cancellationToken);
        }

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            await initialSnapshotSeeder.EnsureInitialSnapshotAsync(db, cancellationToken);

        await PushPendingEventsAsync(baseUri, installationId, deviceSecret, cancellationToken);
        var nextCursor = await PullAndApplyAsync(baseUri, installationId, deviceSecret, settings.LastCloudCursor, cancellationToken);
        settings.LastCloudCursor = nextCursor;
        settings.LastSuccessfulSyncAtUtc = DateTimeOffset.UtcNow;
        settings.LastSyncError = null;
        await SaveSettingsAsync(settings, cancellationToken);
        await PrintIncomingTabletOrdersAsync(cancellationToken);
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

    private async Task PushPendingEventsAsync(Uri baseUri, Guid installationId, string deviceSecret,
        CancellationToken cancellationToken)
    {
        List<CloudSyncOutboxEvent> batch;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            batch = await db.CloudSyncOutboxEvents.Where(item => item.AcknowledgedAtUtc == null)
                .OrderBy(item => item.Id).Take(10).ToListAsync(cancellationToken);
        }
        if (batch.Count == 0) return;

        var events = new List<CloudSyncPushEvent>(batch.Count);
        foreach (var item in batch)
        {
            using var payload = JsonDocument.Parse(item.PayloadJson);
            events.Add(new CloudSyncPushEvent(item.EventId, item.EntityType, item.EntityId,
                item.EventType, item.SchemaVersion, item.OccurredAtUtc, payload.RootElement.Clone()));
        }
        var body = JsonSerializer.SerializeToUtf8Bytes(new CloudSyncPushBatch(events), JsonOptions);
        var endpoint = new Uri(baseUri, "api/sync/v1/events");
        using var request = CloudSyncRequestSigner.Create(HttpMethod.Post, endpoint, installationId, deviceSecret, body);
        using var response = await CreateClient().SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Αποστολή αλλαγών στο cloud: HTTP {(int)response.StatusCode}: {ShortDetail(responseText)}");

        var acknowledgement = JsonSerializer.Deserialize<CloudSyncPushAcknowledgement>(responseText, JsonOptions);
        if (acknowledgement is null || acknowledgement.Accepted + acknowledgement.Duplicates != batch.Count)
            throw new InvalidDataException("Το cloud δεν επιβεβαίωσε όλα τα συμβάντα.");

        var eventIds = batch.Select(item => item.EventId).ToHashSet();
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

    private async Task<long> PullAndApplyAsync(Uri baseUri, Guid installationId, string deviceSecret,
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
            await ApplyCloudEntitiesAsync(page.Entities, cancellationToken);
            cursor = page.NextCursor;
            await SaveCursorAsync(cursor, cancellationToken);
            if (!page.HasMore || page.Entities.Count == 0) break;
        }
        return cursor;
    }

    private async Task ApplyCloudEntitiesAsync(IReadOnlyList<CloudSyncEntity> entities, CancellationToken cancellationToken)
    {
        var incoming = entities.Where(entity => !entity.IsDeleted && int.TryParse(entity.EntityId, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var id) && id < 0 &&
            entity.EntityType is nameof(Order) or nameof(OrderItem)).ToList();
        if (incoming.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.SuppressCloudSyncCapture = true;
        var localWaiterId = await db.Users.Where(user => user.IsActive).OrderBy(user => user.Id)
            .Select(user => user.Id).FirstOrDefaultAsync(cancellationToken);
        if (localWaiterId <= 0)
            throw new InvalidOperationException("Δεν βρέθηκε ενεργός χρήστης στο Windows POS για τις παραγγελίες tablet.");

        foreach (var entity in incoming.OrderBy(item => item.Cursor))
        {
            var id = int.Parse(entity.EntityId, CultureInfo.InvariantCulture);
            if (entity.EntityType == nameof(Order))
            {
                if (await db.Orders.AnyAsync(order => order.Id == id, cancellationToken)) continue;
                var order = JsonSerializer.Deserialize<Order>(entity.Payload.GetRawText(), JsonOptions)
                    ?? throw new InvalidDataException("Το cloud έστειλε μη έγκυρη παραγγελία.");
                if (!await db.Tables.AnyAsync(table => table.Id == order.RestaurantTableId, cancellationToken))
                    throw new InvalidDataException($"Δεν υπάρχει τοπικά το τραπέζι {order.RestaurantTableId} που επιλέχθηκε στο tablet.");
                order.Id = id;
                order.WaiterUserId = localWaiterId;
                order.Status = OrderStatus.Open;
                db.Orders.Add(order);
            }
            else if (entity.EntityType == nameof(OrderItem))
            {
                if (await db.OrderItems.AnyAsync(item => item.Id == id, cancellationToken)) continue;
                var item = JsonSerializer.Deserialize<OrderItem>(entity.Payload.GetRawText(), JsonOptions)
                    ?? throw new InvalidDataException("Το cloud έστειλε μη έγκυρη γραμμή παραγγελίας.");
                if (!await db.Orders.AnyAsync(order => order.Id == item.OrderId, cancellationToken))
                    throw new InvalidDataException($"Δεν έχει ληφθεί πρώτα η παραγγελία {item.OrderId} από το tablet.");
                if (!await db.MenuItems.AnyAsync(menu => menu.Id == item.MenuItemId, cancellationToken))
                    throw new InvalidDataException($"Δεν υπάρχει τοπικά το είδος με κωδικό {item.MenuItemId} που επιλέχθηκε στο tablet.");
                item.Id = id;
                item.IsPrinted = false;
                item.PrintedAt = null;
                item.KitchenPrintUnconfirmed = false;
                db.OrderItems.Add(item);
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task PrintIncomingTabletOrdersAsync(CancellationToken cancellationToken)
    {
        await using var lookup = await dbFactory.CreateDbContextAsync(cancellationToken);
        var orderIds = await lookup.Orders.Where(order => order.Id < 0 && order.Status == OrderStatus.Open &&
                order.Items.Any(item => !item.IsPrinted && !item.IsCancelled))
            .OrderBy(order => order.CreatedAt).Select(order => order.Id).ToListAsync(cancellationToken);
        foreach (var orderId in orderIds)
        {
            using var scope = scopeFactory.CreateScope();
            var printerDispatch = scope.ServiceProvider.GetRequiredService<KitchenOrderDispatchService>();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var order = await db.Orders.Include(item => item.RestaurantTable).Include(item => item.WaiterUser)
                .FirstOrDefaultAsync(item => item.Id == orderId, cancellationToken);
            if (order?.RestaurantTable is null) continue;
            var tableService = !order.RestaurantTable.IsTakeAway;
            await printerDispatch.DispatchPendingAsync(db, orderId, order.RestaurantTable.Name,
                order.WaiterUser?.FullName ?? "Tablet", "", tableService);
        }
    }

    private async Task<CloudSyncSettings?> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CloudSyncSettings.AsNoTracking().SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
    }

    private async Task SaveSettingsAsync(CloudSyncSettings settings, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.SuppressCloudSyncCapture = true;
        db.CloudSyncSettings.Update(settings);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveCursorAsync(long cursor, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.SuppressCloudSyncCapture = true;
        var settings = await db.CloudSyncSettings.SingleAsync(item => item.Id == 1, cancellationToken);
        settings.LastCloudCursor = cursor;
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
    private sealed record CloudSyncEntity(long Cursor, string EntityType, string EntityId, bool IsDeleted,
        int SchemaVersion, DateTimeOffset UpdatedAtUtc, JsonElement Payload);
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
