using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace RestaurantOrders.Cloud.Api;

public static class CloudWebGateway
{
    private const string SessionCookie = "restaurantorders_cloud_session";
    private static readonly HashSet<string> ApiPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth", "/api/pos", "/api/license-manager", "/api/sync", "/api/license", "/api/internal", "/health"
    };
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Host", "Content-Length"
    };

    public static void MapInternalEndpoints(this WebApplication app, string sharedToken)
    {
        app.MapGet("/api/internal/cloud-web/auth", async (HttpContext context, CloudPosDatabase database) =>
        {
            if (!HasInternalBearer(context.Request, sharedToken)) return Results.Unauthorized();
            var session = await ReadSessionAsync(context, database, refresh: false, context.RequestAborted);
            if (session is null) return Results.Unauthorized();
            context.Response.Headers["X-Cloud-Store-Id"] = session.StoreId.ToString("D");
            context.Response.Headers["X-Cloud-User-Id"] = session.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-Cloud-Email"] = session.Email;
            context.Response.Headers["X-Cloud-Role"] = session.Role;
            return Results.NoContent();
        });

        app.MapGet("/api/internal/cloud-web/stores", async (HttpRequest request, CloudDatabase database, CancellationToken cancellationToken) =>
        {
            if (!HasInternalBearer(request, sharedToken)) return Results.Unauthorized();
            return Results.Ok(await database.ReadActiveStoreIdsAsync(cancellationToken));
        });

        app.MapPost("/api/internal/cloud-web/stores/{storeId:guid}/database", async (
            Guid storeId, HttpRequest request, CloudDatabase database, CloudTenantDatabaseAdmin tenantDatabase,
            CancellationToken cancellationToken) =>
        {
            if (!HasInternalBearer(request, sharedToken)) return Results.Unauthorized();
            var databaseName = await database.ReadStoreDatabaseNameAsync(storeId, cancellationToken);
            if (databaseName is null) return Results.NotFound();
            await tenantDatabase.EnsureStoreDatabaseAsync(databaseName, cancellationToken);
            return Results.Ok(new { database = databaseName, status = "ready" });
        });

        app.MapGet("/api/internal/cloud-web/stores/{storeId:guid}/entities", async (
            Guid storeId, long? afterId, int? limit, HttpRequest request, CloudDatabase database, CancellationToken cancellationToken) =>
        {
            if (!HasInternalBearer(request, sharedToken)) return Results.Unauthorized();
            var cursor = afterId ?? 0;
            var pageSize = limit ?? 100;
            if (cursor < 0 || pageSize is < 1 or > 100) return Results.BadRequest();
            var entities = await database.ReadEntitySnapshotsAfterAsync(storeId, cursor, pageSize, cancellationToken);
            var nextCursor = entities.Count == 0 ? cursor : entities[^1].Cursor;
            return Results.Ok(new SyncEntitySnapshotResponse(storeId, entities, nextCursor, entities.Count == pageSize));
        });

        app.MapPost("/api/internal/cloud-web/stores/{storeId:guid}/events", async (
            Guid storeId, SyncBatchRequest? body, HttpRequest request, CloudDatabase database, CancellationToken cancellationToken) =>
        {
            if (!HasInternalBearer(request, sharedToken)) return Results.Unauthorized();
            var events = body?.Events;
            if (events is null || events.Count is < 1 or > 100 || events.Any(item =>
                item is null || item.EventId == Guid.Empty || !IsSafeName(item.EntityType, 80) ||
                !IsSafeName(item.EventType, 40) || string.IsNullOrWhiteSpace(item.EntityId) || item.EntityId.Length > 160 ||
                item.SchemaVersion is < 1 or > 1000 || item.OccurredAtUtc == default || item.OccurredAtUtc.Offset != TimeSpan.Zero ||
                item.Payload.ValueKind != JsonValueKind.Object || JsonSerializer.SerializeToUtf8Bytes(item.Payload).Length > 3 * 1024 * 1024))
                return Results.BadRequest(new { error = "Το batch συγχρονισμού δεν είναι έγκυρο." });
            try
            {
                var (accepted, duplicates) = await database.InsertEventsAsync(storeId, events, cancellationToken);
                return Results.Ok(new SyncBatchResponse(storeId, accepted, duplicates));
            }
            catch (SyncEventConflictException) { return Results.Conflict(); }
            catch (Npgsql.PostgresException exception) when (exception.SqlState == "23503") { return Results.Conflict(); }
        });
    }

    public static IApplicationBuilder UseCloudWebProxy(this IApplicationBuilder app, string upstream, string sharedToken)
    {
        app.UseWebSockets();
        return app.Use(async (context, next) =>
        {
            if (!ShouldProxy(context.Request.Path))
            {
                await next();
                return;
            }

            var database = context.RequestServices.GetRequiredService<CloudPosDatabase>();
            var session = await ReadSessionAsync(context, database, refresh: false, context.RequestAborted);
            if (session is null)
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                context.Response.Redirect("/pos.html", permanent: false);
                return;
            }

            if (context.WebSockets.IsWebSocketRequest)
            {
                await ProxyWebSocketAsync(context, upstream, sharedToken, session);
                return;
            }

            var clientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
            await ProxyHttpAsync(context, clientFactory.CreateClient("cloud-web-proxy"), upstream, sharedToken, session);
        });
    }

    private static bool ShouldProxy(PathString path)
    {
        var value = path.Value ?? "/";
        if (value.Equals("/pos.html", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/license-manager.html", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/license-manager", StringComparison.OrdinalIgnoreCase) || value.Equals("/health/live", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/health/ready", StringComparison.OrdinalIgnoreCase)) return false;
        return !ApiPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task ProxyHttpAsync(HttpContext context, HttpClient client, string upstream, string sharedToken, CloudPosSession session)
    {
        var target = new Uri(new Uri(upstream.TrimEnd('/') + "/"), context.Request.Path + context.Request.QueryString);
        using var message = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            message.Content = new StreamContent(context.Request.Body);
        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key) || header.Key.StartsWith("X-Cloud-", StringComparison.OrdinalIgnoreCase)) continue;
            if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                // Keep the app's own staff/antiforgery cookies for the full
                // Windows UI, but never expose the store-session cookie to it.
                var forwardedCookies = context.Request.Cookies
                    .Where(cookie => !cookie.Key.Equals(SessionCookie, StringComparison.OrdinalIgnoreCase))
                    .Select(cookie => $"{cookie.Key}={cookie.Value}");
                var cookieHeader = string.Join("; ", forwardedCookies);
                if (cookieHeader.Length > 0) message.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                continue;
            }
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                message.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        SetCloudHeaders(message.Headers, sharedToken, session, context.Request.Host.Host);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers)
            if (!HopByHopHeaders.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
        foreach (var header in response.Content.Headers)
            if (!HopByHopHeaders.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
        context.Response.Headers.Remove("transfer-encoding");
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static async Task ProxyWebSocketAsync(HttpContext context, string upstream, string sharedToken, CloudPosSession session)
    {
        var targetBuilder = new UriBuilder(new Uri(new Uri(upstream.TrimEnd('/') + "/"), context.Request.Path + context.Request.QueryString))
        {
            Scheme = Uri.UriSchemeWs
        };
        using var client = new ClientWebSocket();
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols) client.Options.AddSubProtocol(protocol);
        client.Options.SetRequestHeader("X-Cloud-Store-Id", session.StoreId.ToString("D"));
        client.Options.SetRequestHeader("X-Cloud-User-Id", session.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        client.Options.SetRequestHeader("X-Cloud-Email", session.Email);
        client.Options.SetRequestHeader("X-Cloud-Role", session.Role);
        client.Options.SetRequestHeader("X-Cloud-Internal-Token", sharedToken);
        client.Options.SetRequestHeader("X-Cloud-External-Host", context.Request.Host.Host);
        var forwardedCookies = context.Request.Cookies
            .Where(cookie => !cookie.Key.Equals(SessionCookie, StringComparison.OrdinalIgnoreCase))
            .Select(cookie => $"{cookie.Key}={cookie.Value}");
        var cookieHeader = string.Join("; ", forwardedCookies);
        if (cookieHeader.Length > 0) client.Options.SetRequestHeader("Cookie", cookieHeader);
        await client.ConnectAsync(targetBuilder.Uri, context.RequestAborted);
        using var browser = await context.WebSockets.AcceptWebSocketAsync(client.SubProtocol);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var toUpstream = PumpAsync(browser, client, cts.Token);
        var toBrowser = PumpAsync(client, browser, cts.Token);
        await Task.WhenAny(toUpstream, toBrowser);
        cts.Cancel();
        try { await Task.WhenAll(toUpstream, toBrowser); } catch (OperationCanceledException) { }
    }

    private static async Task PumpAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested && source.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (destination.State == WebSocketState.Open)
                    await destination.CloseOutputAsync(source.CloseStatus ?? WebSocketCloseStatus.NormalClosure, source.CloseStatusDescription, cancellationToken);
                return;
            }
            await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken);
        }
    }

    private static void SetCloudHeaders(System.Net.Http.Headers.HttpRequestHeaders headers, string token, CloudPosSession session,
        string externalHost)
    {
        headers.TryAddWithoutValidation("X-Cloud-Internal-Token", token);
        headers.TryAddWithoutValidation("X-Cloud-Store-Id", session.StoreId.ToString("D"));
        headers.TryAddWithoutValidation("X-Cloud-User-Id", session.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        headers.TryAddWithoutValidation("X-Cloud-Email", session.Email);
        headers.TryAddWithoutValidation("X-Cloud-Role", session.Role);
        headers.TryAddWithoutValidation("X-Cloud-External-Host", externalHost);
    }

    private static async Task<CloudPosSession?> ReadSessionAsync(HttpContext context, CloudPosDatabase database, bool refresh, CancellationToken cancellationToken)
    {
        if (!context.Request.Cookies.TryGetValue(SessionCookie, out var token) || token.Length is < 32 or > 256) return null;
        return await database.GetSessionAsync(SHA256.HashData(Encoding.UTF8.GetBytes(token)), refresh, cancellationToken);
    }

    private static bool HasInternalBearer(HttpRequest request, string expected)
    {
        var actual = request.Headers["Authorization"].ToString();
        if (actual.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) actual = actual[7..].Trim();
        else actual = request.Headers["X-Cloud-Internal-Token"].ToString();
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(actual));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static bool IsSafeName(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);
}
