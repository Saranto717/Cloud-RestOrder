using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

const int maximumPayloadBytes = 256 * 1024;
const int maximumCopies = 5;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 512 * 1024);

var tokenFile = builder.Configuration["CloudPrintBridge:TokenFile"];
var token = !string.IsNullOrWhiteSpace(tokenFile) && File.Exists(tokenFile)
    ? File.ReadAllText(tokenFile).Trim()
    : builder.Configuration["CloudPrintBridge:Token"]?.Trim() ?? "";
if (Encoding.UTF8.GetByteCount(token) < 32)
    throw new InvalidOperationException("Cloud Print Bridge requires a token with at least 32 bytes.");

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapPost("/v1/print", async Task<IResult> (HttpContext context, PrintRequest request, ILogger<Program> logger) =>
{
    if (!HasValidBearerToken(context.Request.Headers.Authorization, token))
        return Results.Unauthorized();

    if (!IPAddress.TryParse(request.IpAddress?.Trim(), out var address) || !IsPrivateLanAddress(address))
        return Results.BadRequest(new PrintResponse(false, "Η IP πρέπει να είναι ιδιωτική διεύθυνση του τοπικού δικτύου."));
    if (request.Port is <= 0 or > 65535)
        return Results.BadRequest(new PrintResponse(false, "Η θύρα του εκτυπωτή δεν είναι έγκυρη."));
    if (request.Copies is < 1 or > maximumCopies)
        return Results.BadRequest(new PrintResponse(false, $"Τα αντίγραφα πρέπει να είναι από 1 έως {maximumCopies}."));
    if (string.IsNullOrWhiteSpace(request.DataBase64) || request.DataBase64.Length > ((maximumPayloadBytes + 2) / 3) * 4)
        return Results.BadRequest(new PrintResponse(false, "Το δελτίο εκτύπωσης είναι κενό ή υπερβαίνει το επιτρεπόμενο μέγεθος."));

    byte[] bytes;
    try
    {
        bytes = Convert.FromBase64String(request.DataBase64);
    }
    catch (FormatException)
    {
        return Results.BadRequest(new PrintResponse(false, "Τα δεδομένα εκτύπωσης δεν είναι έγκυρα."));
    }

    if (bytes.Length is 0 or > maximumPayloadBytes)
        return Results.BadRequest(new PrintResponse(false, "Το δελτίο εκτύπωσης είναι κενό ή υπερβαίνει το επιτρεπόμενο μέγεθος."));

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(address, request.Port, timeout.Token);
        await using var stream = client.GetStream();
        for (var copy = 0; copy < request.Copies; copy++)
            await stream.WriteAsync(bytes, timeout.Token);
        await stream.FlushAsync(timeout.Token);

        logger.LogInformation("Sent {ByteCount} ESC/POS bytes in {Copies} copy/copies to {PrinterIp}:{PrinterPort}",
            bytes.Length, request.Copies, address, request.Port);
        return Results.Ok(new PrintResponse(true, null));
    }
    catch (OperationCanceledException)
    {
        logger.LogWarning("Timed out sending ESC/POS data to {PrinterIp}:{PrinterPort}", address, request.Port);
        return Results.Json(new PrintResponse(false, $"Timeout σύνδεσης ή αποστολής στον {address}:{request.Port}."), statusCode: 504);
    }
    catch (SocketException ex)
    {
        logger.LogWarning(ex, "Could not connect to printer {PrinterIp}:{PrinterPort}", address, request.Port);
        return Results.Json(new PrintResponse(false,
            $"Δικτυακό σφάλμα ({ex.SocketErrorCode}) προς {address}:{request.Port}."), statusCode: 502);
    }
    catch (IOException ex)
    {
        logger.LogWarning(ex, "Could not send data to printer {PrinterIp}:{PrinterPort}", address, request.Port);
        return Results.Json(new PrintResponse(false, $"Αποτυχία αποστολής στον {address}:{request.Port}: {ex.Message}"), statusCode: 502);
    }
});

app.Run();

static bool HasValidBearerToken(string? authorization, string expectedToken)
{
    if (!AuthenticationHeaderValue.TryParse(authorization, out var parsed) ||
        !string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrEmpty(parsed.Parameter))
        return false;

    var submitted = Encoding.UTF8.GetBytes(parsed.Parameter);
    var expected = Encoding.UTF8.GetBytes(expectedToken);
    return submitted.Length == expected.Length && CryptographicOperations.FixedTimeEquals(submitted, expected);
}

static bool IsPrivateLanAddress(IPAddress address)
{
    if (address.IsIPv4MappedToIPv6)
        address = address.MapToIPv4();

    var bytes = address.GetAddressBytes();
    if (address.AddressFamily == AddressFamily.InterNetworkV6)
        return !IPAddress.IPv6Any.Equals(address) && !IPAddress.IPv6Loopback.Equals(address) && !address.IsIPv6Multicast &&
               (address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC);

    if (address.AddressFamily != AddressFamily.InterNetwork ||
        bytes[0] is 0 or 127 or >= 224 ||
        (bytes[0] == 169 && bytes[1] != 254))
        return false;

    return bytes[0] == 10 ||
           (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
           (bytes[0] == 192 && bytes[1] == 168) ||
           (bytes[0] == 169 && bytes[1] == 254);
}

sealed record PrintRequest(string IpAddress, int Port, string DataBase64, int Copies);
sealed record PrintResponse(bool Success, string? Error);
