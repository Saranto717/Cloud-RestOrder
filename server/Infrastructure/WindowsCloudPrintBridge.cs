using System.Net;
using System.Net.Sockets;
using RestaurantOrders.Services;

namespace RestaurantOrders.Infrastructure;

/// <summary>
/// Allows the Cloud POS page open in a Windows browser to use the already-installed
/// local POS service as its printer bridge. The bridge is loopback-only and accepts
/// requests only from configured HTTPS Cloud POS origins.
/// </summary>
public static class WindowsCloudPrintBridge
{
    private const string RoutePrefix = "/api/local-print";
    private const string DefaultCloudOrigin = "https://texn-support.ddns.net";
    private const int LocalPosPort = 5080;
    private const int MaxPayloadBytes = 512 * 1024;

    public sealed record PrintRequest(
        string ConnectionType,
        string? IpAddress,
        int Port,
        string? PrinterName,
        string DataBase64,
        int Copies);

    public static void UseWindowsCloudPrintBridge(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>("CloudWeb:Enabled"))
            return;

        var allowedOrigins = ReadAllowedOrigins(app.Configuration);

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(RoutePrefix, StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            var origin = context.Request.Headers["Origin"].ToString().TrimEnd('/');
            if (!IsLoopbackRequest(context) || !allowedOrigins.Contains(origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            context.Response.Headers["Access-Control-Max-Age"] = "300";
            context.Response.Headers["Vary"] = "Origin";
            context.Response.Headers["Cache-Control"] = "no-store";

            if (HttpMethods.IsOptions(context.Request.Method))
            {
                var requestedMethod = context.Request.Headers["Access-Control-Request-Method"].ToString();
                if (requestedMethod is not ("GET" or "POST"))
                {
                    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await next();
        });

        app.MapGet($"{RoutePrefix}/health", (HttpContext context) =>
        {
            if (!IsLoopbackRequest(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(new { available = true, service = "RestaurantOrders Windows print bridge" });
        }).AllowAnonymous();

        app.MapPost($"{RoutePrefix}/raw", SendRawAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxPayloadBytes * 2));
    }

    private static async Task<IResult> SendRawAsync(
        PrintRequest request,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        if (!IsLoopbackRequest(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(request.DataBase64) || request.DataBase64.Length > (MaxPayloadBytes * 4 / 3) + 8)
            return Results.BadRequest(new { ok = false, error = "Το δελτίο εκτύπωσης είναι κενό ή υπερβολικά μεγάλο." });

        byte[] data;
        try
        {
            data = Convert.FromBase64String(request.DataBase64);
        }
        catch (FormatException)
        {
            return Results.BadRequest(new { ok = false, error = "Τα δεδομένα ESC/POS δεν είναι έγκυρα." });
        }

        if (data.Length == 0 || data.Length > MaxPayloadBytes)
            return Results.BadRequest(new { ok = false, error = "Το δελτίο εκτύπωσης είναι κενό ή υπερβολικά μεγάλο." });

        try
        {
            if (string.Equals(request.ConnectionType, "Usb", StringComparison.OrdinalIgnoreCase))
            {
                WindowsRawPrinter.Send(request.PrinterName ?? string.Empty, data, request.Copies);
                return Results.Ok(new { ok = true });
            }

            if (!string.Equals(request.ConnectionType, "Tcp", StringComparison.OrdinalIgnoreCase) ||
                !IPAddress.TryParse(request.IpAddress?.Trim(), out var address) ||
                !IsPrivateLanAddress(address) || request.Port is <= 0 or > 65535)
            {
                return Results.BadRequest(new { ok = false, error = "Η IP/θύρα του εκτυπωτή δεν είναι έγκυρη ιδιωτική διεύθυνση." });
            }

            using var client = new TcpClient(address.AddressFamily);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(address, request.Port, timeout.Token);
            await using var stream = client.GetStream();
            for (var copy = 0; copy < Math.Clamp(request.Copies, 1, 5); copy++)
                await stream.WriteAsync(data, timeout.Token);
            await stream.FlushAsync(timeout.Token);

            return Results.Ok(new { ok = true });
        }
        catch (OperationCanceledException)
        {
            return Results.Json(new { ok = false, error = $"Timeout σύνδεσης στον εκτυπωτή {request.IpAddress}:{request.Port}." }, statusCode: 502);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("WindowsCloudPrintBridge")
                .LogWarning(exception, "Cloud browser print failed via the local Windows bridge.");
            var message = string.Equals(request.ConnectionType, "Usb", StringComparison.OrdinalIgnoreCase)
                ? exception.Message
                : $"Αποτυχία σύνδεσης στον εκτυπωτή {request.IpAddress}:{request.Port}: {exception.Message}";
            return Results.Json(new { ok = false, error = message }, statusCode: 502);
        }
    }

    private static HashSet<string> ReadAllowedOrigins(IConfiguration configuration)
    {
        var configured = configuration.GetSection("LocalPrinterBridge:AllowedOrigins").Get<string[]>() ?? [];
        return configured
            .Append(DefaultCloudOrigin)
            .Select(value => value.Trim().TrimEnd('/'))
            .Where(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                            uri.Scheme == Uri.UriSchemeHttps &&
                            string.IsNullOrWhiteSpace(uri.UserInfo) &&
                            uri.AbsolutePath == "/" &&
                            string.IsNullOrEmpty(uri.Query) &&
                            string.IsNullOrEmpty(uri.Fragment))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsLoopbackRequest(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
            return false;
        if (remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();
        if (!IPAddress.IsLoopback(remote))
            return false;

        var host = context.Request.Host;
        if (host.Port != LocalPosPort)
            return false;

        return string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               IPAddress.TryParse(host.Host, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 169 && bytes[1] == 254);
    }
}
