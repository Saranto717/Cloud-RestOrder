using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using RestaurantOrders.Cloud.Api;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 5 * 1024 * 1024);
var cloudWebEnabled = builder.Configuration.GetValue<bool>("CloudWeb:Enabled");
var cloudWebSyncToken = cloudWebEnabled
    ? ReadConfiguredSecret(builder.Configuration, "Cloud:WebSyncToken", "Cloud:WebSyncTokenFile")
    : null;
if (cloudWebSyncToken is not null && Encoding.UTF8.GetByteCount(cloudWebSyncToken) < 32)
    throw new InvalidOperationException("Cloud web sync token must contain at least 32 bytes of secret material.");
var cloudWebUpstream = builder.Configuration["Cloud:WebUpstream"] ?? "http://cloud-web:8080";

var connectionString = builder.Configuration.GetConnectionString("Cloud");
if (string.IsNullOrWhiteSpace(connectionString))
{
    var databasePassword = ReadConfiguredSecret(builder.Configuration, "Cloud:DatabasePassword", "Cloud:DatabasePasswordFile");
    if (Encoding.UTF8.GetByteCount(databasePassword) < 32)
        throw new InvalidOperationException("Cloud database password must contain at least 32 bytes of secret material.");

    var database = new NpgsqlConnectionStringBuilder
    {
        Host = builder.Configuration["Cloud:DatabaseHost"] ?? "postgres",
        Port = builder.Configuration.GetValue("Cloud:DatabasePort", 5432),
        Database = builder.Configuration["Cloud:DatabaseName"] ?? "restaurant_cloud",
        Username = builder.Configuration["Cloud:DatabaseUser"] ?? "restaurant_cloud",
        Password = databasePassword,
        Timeout = 15,
        CommandTimeout = 30,
        Pooling = true
    };
    connectionString = database.ConnectionString;
}

var licenseManagerPassword = ReadConfiguredSecret(builder.Configuration, "Cloud:LicenseManagerPassword", "Cloud:LicenseManagerPasswordFile");
if (Encoding.UTF8.GetByteCount(licenseManagerPassword) < 32)
    throw new InvalidOperationException("Cloud:LicenseManagerPassword must contain at least 32 bytes of secret material.");
var licenseManagerEmail = builder.Configuration["Cloud:LicenseManagerEmail"]?.Trim();
if (string.IsNullOrWhiteSpace(licenseManagerEmail) || licenseManagerEmail.Length > 254 || !licenseManagerEmail.Contains('@'))
    throw new InvalidOperationException("Cloud:LicenseManagerEmail must contain the manager's email address.");
var licenseSigningPrivateKeyPath = builder.Configuration["Cloud:LicenseSigningPrivateKeyFile"]
    ?? "/var/lib/restaurantorders/license-signing-private.pem";
var licenseSigningPrivateKey = LicenseSigner.LoadOrCreatePrivateKey(licenseSigningPrivateKeyPath);

builder.Services.AddRateLimiter(options => options.AddPolicy("license-manager-login", httpContext =>
    RateLimitPartition.GetFixedWindowLimiter(httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
        new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            AutoReplenishment = true
        })));

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<CloudDatabase>();
builder.Services.AddSingleton<CloudPosDatabase>();
builder.Services.AddSingleton<CloudSyncDeviceDatabase>();
builder.Services.AddSingleton<CloudLicenseDatabase>();
builder.Services.AddSingleton<CloudLicenseManagerDatabase>();
builder.Services.AddSingleton(new LicenseSigner(licenseSigningPrivateKey));
builder.Services.AddHttpClient("cloud-web-proxy", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        // The browser must receive the app's 302 and secure staff cookie,
        // then follow the redirect over the public HTTPS origin. Following it
        // here would use the internal HTTP upstream and lose that secure cookie.
        AllowAutoRedirect = false,
        UseCookies = false
    });

var app = builder.Build();
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1
};
foreach (var proxyAddress in (builder.Configuration["Cloud:TrustedProxyAddresses"] ?? "")
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
{
    if (!IPAddress.TryParse(proxyAddress, out var parsedAddress))
        throw new InvalidOperationException("Cloud:TrustedProxyAddresses must contain comma-separated IP addresses.");
    forwardedOptions.KnownProxies.Add(parsedAddress);
}
app.UseForwardedHeaders(forwardedOptions);
app.UseRouting();
app.UseRateLimiter();
app.UseStaticFiles();
if (cloudWebEnabled)
    app.UseCloudWebProxy(cloudWebUpstream, cloudWebSyncToken!);

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<CloudDatabase>();
    await database.ApplyMigrationsAsync(CancellationToken.None);
}

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (CloudDatabase database, CancellationToken cancellationToken) =>
{
    var ready = await database.CanConnectAsync(cancellationToken);
    return ready ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapPost("/api/sync/v1/devices/register", async (
    HttpRequest request,
    RegisterSyncDeviceRequest? body,
    CloudDatabase database,
    CloudSyncDeviceDatabase deviceDatabase,
    CancellationToken cancellationToken) =>
{
    if (!request.IsHttps) return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
    var storeToken = ReadBearerToken(request.Headers.Authorization.ToString());
    if (storeToken is null) return Results.Unauthorized();
    var storeId = await database.FindActiveStoreIdAsync(StoreTokens.Hash(storeToken), cancellationToken);
    if (storeId is null) return Results.Unauthorized();
    if (!Guid.TryParse(body?.InstallationId, out var installationId) ||
        body?.DeviceSecret is not { Length: >= 32 and <= 256 } deviceSecret ||
        deviceSecret.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        return Results.BadRequest(new { error = "Τα στοιχεία της εγκατάστασης δεν είναι έγκυρα." });
    try
    {
        var registered = await deviceDatabase.RegisterAsync(storeId.Value, installationId,
            CloudSyncDeviceDatabase.HashDeviceSecret(deviceSecret), cancellationToken);
        if (!registered)
            return Results.Conflict(new { error = "Το κλειδί αυτής της εγκατάστασης είναι ήδη δεμένο. Χρειάζεται νέα σύζευξη από τον διαχειριστή." });
    }
    catch (Npgsql.PostgresException exception) when (exception.SqlState == "23505")
    {
        return Results.Conflict(new { error = "Αυτή η εγκατάσταση έχει ήδη συνδεθεί με άλλο κατάστημα." });
    }
    return Results.Ok(new { storeId, installationId, status = "registered" });
});

app.MapPost("/api/license/v1/check", async (
    HttpRequest request,
    CloudLicenseDatabase database,
    LicenseSigner signer,
    CancellationToken cancellationToken) =>
{
    if (!request.IsHttps) return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
    request.EnableBuffering();
    await using var bodyBuffer = new MemoryStream();
    await request.Body.CopyToAsync(bodyBuffer, cancellationToken);
    request.Body.Position = 0;
    var requestBody = bodyBuffer.ToArray();
    RemoteLicenseCheckRequest? body;
    try { body = JsonSerializer.Deserialize<RemoteLicenseCheckRequest>(requestBody, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
    catch (JsonException) { return Results.BadRequest(new { error = "Το αίτημα δεν είναι έγκυρο JSON." }); }
    if (!VerifyLicenseCheckRequest(request, body, requestBody))
        return Results.Unauthorized();
    var requestNonce = request.Headers["X-License-Nonce"].ToString();
    if (!await database.RecordLicenseRequestNonceAsync(body!.InstallationId, requestNonce, cancellationToken))
        return Results.Unauthorized();
    if (body is null || !Guid.TryParse(body.InstallationId, out var installationId) ||
        string.IsNullOrWhiteSpace(body.MachineName) || body.MachineName.Length > 120 ||
        body.MacAddress is null || body.MacAddress.Length > 64 ||
        body.MacAddress.Any(c => !Uri.IsHexDigit(c) && c is not ':' and not '-' and not ' '))
        return Results.BadRequest(new { error = "Τα στοιχεία εγκατάστασης δεν είναι έγκυρα." });

    var machineName = body.MachineName.Trim();
    var publicKeyBytes = Convert.FromBase64String(request.Headers["X-License-Public-Key"].ToString());
    if (!await database.RegisterInstallationAsync(installationId, machineName, body.MacAddress,
            SHA256.HashData(publicKeyBytes), cancellationToken))
        return Results.Conflict(new { error = "Το αναγνωριστικό εγκατάστασης έχει ήδη δεθεί με διαφορετικό υπολογιστή. Επικοινώνησε με τον προμηθευτή." });

    var activation = await database.GetActivationInfoAsync(installationId, cancellationToken);
    if (activation is null)
        return Results.NotFound(new { error = "Δεν βρέθηκε η εγκατάσταση." });
    if (activation.LicenseId is null)
        return Results.Ok(new RemoteLicenseCheckResponse
        {
            Status = "pending",
            Message = "Η εγκατάσταση καταχωρίστηκε και περιμένει ενεργοποίηση από τον License Manager."
        });

    var now = DateTime.UtcNow;
    var isActive = activation.IsAssigned && activation.Status == "active" &&
                   (activation.ExpiresAtUtc is null || activation.ExpiresAtUtc.Value > now);
    var status = isActive ? "active" : "revoked";
    var leaseExpiry = isActive
        ? (activation.ExpiresAtUtc is null || activation.ExpiresAtUtc.Value > now.AddDays(30)
            ? now.AddDays(30)
            : activation.ExpiresAtUtc.Value)
        : now;
    var payload = new CloudLicensePayload
    {
        LicenseId = activation.LicenseId.Value.ToString("D"),
        CustomerName = activation.CustomerName,
        VatNumber = activation.VatNumber,
        InstallationId = installationId.ToString("D"),
        MachineName = machineName,
        MacAddress = body.MacAddress,
        IssuedAtUtc = now,
        ExpiresAtUtc = activation.ExpiresAtUtc,
        RemoteStatus = status,
        LeaseExpiresAtUtc = leaseExpiry
    };

    return Results.Ok(new RemoteLicenseCheckResponse
    {
        Status = status,
        License = signer.Sign(payload),
        Message = isActive ? "Η άδεια χρήσης είναι ενεργή." : "Η άδεια ανακλήθηκε ή έληξε."
    });
});

const string licenseManagerSessionCookie = "restaurantorders_license_manager_session";
var managerAuth = app.MapGroup("/api/license-manager/v1/auth");
managerAuth.MapPost("/login", async (HttpContext context, CloudLicenseManagerLoginRequest? body,
    CloudLicenseDatabase database, CancellationToken cancellationToken) =>
{
    if (!context.Request.IsHttps) return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
    if (!IsSameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (body is null || body.Password is not { Length: <= 1024 } ||
        !string.Equals(body.Email?.Trim(), licenseManagerEmail, StringComparison.OrdinalIgnoreCase) ||
        !HasMatchingSecret(body.Password, licenseManagerPassword))
        return Results.Unauthorized();
    var token = StoreTokens.Create();
    await database.CreateLicenseManagerSessionAsync(SHA256.HashData(Encoding.UTF8.GetBytes(token)), cancellationToken);
    context.Response.Cookies.Append(licenseManagerSessionCookie, token, new CookieOptions
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromMinutes(10)
    });
    return Results.Ok(new { email = licenseManagerEmail, expiresInSeconds = 600 });
}).RequireRateLimiting("license-manager-login");
managerAuth.MapGet("/session", async (HttpContext context, CloudLicenseDatabase database, CancellationToken cancellationToken) =>
{
    var expiry = await GetLicenseManagerSessionExpiryAsync(context, database, refresh: false, cancellationToken);
    return expiry is null ? Results.Unauthorized() : Results.Ok(new { email = licenseManagerEmail, expiresAtUtc = expiry });
});
managerAuth.MapPost("/refresh", async (HttpContext context, CloudLicenseDatabase database, CancellationToken cancellationToken) =>
{
    if (!context.Request.IsHttps) return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
    if (!IsSameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var expiry = await GetLicenseManagerSessionExpiryAsync(context, database, refresh: true, cancellationToken);
    if (expiry is null) return Results.Unauthorized();
    context.Response.Cookies.Append(licenseManagerSessionCookie, context.Request.Cookies[licenseManagerSessionCookie]!, new CookieOptions
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromMinutes(10)
    });
    return Results.Ok(new { email = licenseManagerEmail, expiresAtUtc = expiry });
});
managerAuth.MapPost("/logout", async (HttpContext context, CloudLicenseDatabase database, CancellationToken cancellationToken) =>
{
    if (!IsSameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (context.Request.Cookies.TryGetValue(licenseManagerSessionCookie, out var token))
        await database.RevokeLicenseManagerSessionAsync(SHA256.HashData(Encoding.UTF8.GetBytes(token)), cancellationToken);
    context.Response.Cookies.Delete(licenseManagerSessionCookie, new CookieOptions
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/"
    });
    return Results.Ok(new { status = "signed-out" });
});

var licenseManager = app.MapGroup("/api/license-manager/v1");
licenseManager.AddEndpointFilter(async (context, next) =>
{
    var request = context.HttpContext.Request;
    if (!request.IsHttps) return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
    if ((HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method) || HttpMethods.IsDelete(request.Method)) && !IsSameOrigin(request))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var expiry = await GetLicenseManagerSessionExpiryAsync(context.HttpContext, context.HttpContext.RequestServices.GetRequiredService<CloudLicenseDatabase>(),
        refresh: false, context.HttpContext.RequestAborted);
    if (expiry is null) return Results.Unauthorized();
    return await next(context);
});

licenseManager.MapGet("/dashboard", async (CloudLicenseManagerDatabase database, CancellationToken cancellationToken) =>
{
    var dashboard = await database.GetDashboardAsync(cancellationToken);
    return Results.Ok(new
    {
        customers = dashboard.Customers,
        licenses = dashboard.Licenses,
        installations = dashboard.Installations,
        editions = dashboard.Editions
    });
});

licenseManager.MapPost("/customers", async (
    CreateCloudCustomerRequest? body,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var storeIdentifier = body?.StoreIdentifier?.Trim();
    var shopName = body?.ShopName?.Trim();
    var resellerName = body?.ResellerName?.Trim();
    var vatNumber = body?.VatNumber?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(storeIdentifier) || storeIdentifier.Length > 80 ||
        string.IsNullOrWhiteSpace(shopName) || shopName.Length > 160 ||
        string.IsNullOrWhiteSpace(resellerName) || resellerName.Length > 160 || vatNumber.Length > 32)
        return Results.BadRequest(new { error = "Συμπλήρωσε αναγνωριστικό καταστήματος, όνομα καταστήματος και μεταπωλητή. Το ΑΦΜ πρέπει να είναι έως 32 χαρακτήρες." });

    try
    {
        var customerId = await database.CreateCustomerAsync(storeIdentifier, shopName, resellerName, vatNumber, cancellationToken);
        return Results.Created($"/api/license-manager/v1/customers/{customerId}", new { customerId });
    }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { error = "Το αναγνωριστικό καταστήματος χρησιμοποιείται ήδη." });
    }
});

licenseManager.MapPut("/customers/{customerId:guid}", async (
    Guid customerId,
    UpdateCloudCustomerRequest? body,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var storeIdentifier = body?.StoreIdentifier?.Trim();
    var shopName = body?.ShopName?.Trim();
    var resellerName = body?.ResellerName?.Trim();
    var vatNumber = body?.VatNumber?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(storeIdentifier) || storeIdentifier.Length > 80 ||
        string.IsNullOrWhiteSpace(shopName) || shopName.Length > 160 ||
        string.IsNullOrWhiteSpace(resellerName) || resellerName.Length > 160 || vatNumber.Length > 32)
        return Results.BadRequest(new { error = "Συμπλήρωσε αναγνωριστικό καταστήματος, όνομα καταστήματος και μεταπωλητή. Το ΑΦΜ πρέπει να είναι έως 32 χαρακτήρες." });

    try
    {
        var updated = await database.UpdateCustomerAsync(customerId, storeIdentifier, shopName, resellerName, vatNumber, cancellationToken);
        return updated ? Results.Ok(new { status = "updated" }) : Results.NotFound(new { error = "Δεν βρέθηκε ενεργός πελάτης." });
    }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { error = "Το αναγνωριστικό καταστήματος χρησιμοποιείται ήδη." });
    }
});

licenseManager.MapDelete("/customers/{customerId:guid}", async (
    Guid customerId,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var deleted = await database.DeleteCustomerAsync(customerId, cancellationToken);
    return deleted
        ? Results.Ok(new { status = "deleted", message = "Ο πελάτης αρχειοθετήθηκε και οι άδειές του ανακλήθηκαν." })
        : Results.NotFound(new { error = "Δεν βρέθηκε ενεργός πελάτης." });
});

licenseManager.MapDelete("/customers/{customerId:guid}/permanent", async (
    Guid customerId,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var deleted = await database.PermanentlyDeleteCustomerAsync(customerId, cancellationToken);
    return deleted
        ? Results.Ok(new { status = "permanently-deleted", message = "Το κατάστημα, οι άδειες, οι εγκαταστάσεις και το σχετικό ιστορικό διαγράφηκαν οριστικά." })
        : Results.NotFound(new { error = "Δεν βρέθηκε το κατάστημα." });
});

licenseManager.MapPost("/customers/{customerId:guid}/restore", async (
    Guid customerId,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var restored = await database.RestoreCustomerAsync(customerId, cancellationToken);
    return restored
        ? Results.Ok(new { status = "restored", message = "Ο πελάτης επανενεργοποιήθηκε. Οι άδειες παραμένουν ανακλημένες μέχρι να ανανεωθούν." })
        : Results.NotFound(new { error = "Δεν βρέθηκε αρχειοθετημένος πελάτης." });
});

licenseManager.MapPost("/customers/{customerId:guid}/licenses", async (
    Guid customerId,
    CreateCloudLicenseForCustomerRequest? body,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var expiresAtUtc = body?.ExpiresAtUtc?.UtcDateTime;
    if (body is null || body.EditionId == Guid.Empty || expiresAtUtc is null || expiresAtUtc <= DateTime.UtcNow)
        return Results.BadRequest(new { error = "Επίλεξε έκδοση άδειας και ημερομηνία λήξης στο μέλλον." });
    var (licenseId, error) = await database.CreateCustomerLicenseAsync(customerId, body.EditionId, expiresAtUtc.Value, cancellationToken);
    if (licenseId is null)
        return Results.Conflict(new { error = error == "customer-or-edition-not-found" ? "Ο πελάτης δεν είναι ενεργός ή η έκδοση άδειας δεν είναι διαθέσιμη." : "Δεν ήταν δυνατή η δημιουργία άδειας." });
    return Results.Created($"/api/license-manager/v1/licenses/{licenseId}", new { licenseId });
});

licenseManager.MapPost("/license-editions", async (
    UpsertCloudLicenseEditionRequest? body,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var name = body?.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 80 ||
        body!.MaxInstallations is < 1 or > 100 || body.DefaultDurationMonths is < 1 or > 120)
        return Results.BadRequest(new { error = "Συμπλήρωσε όνομα έκδοσης, 1–100 εγκαταστάσεις και διάρκεια 1–120 μηνών." });
    try
    {
        var editionId = await database.CreateLicenseEditionAsync(name, body.MaxInstallations, body.DefaultDurationMonths, cancellationToken);
        return Results.Created($"/api/license-manager/v1/license-editions/{editionId}", new { editionId });
    }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { error = "Υπάρχει ήδη έκδοση με αυτό το όνομα." });
    }
});

licenseManager.MapPut("/license-editions/{editionId:guid}", async (
    Guid editionId,
    UpsertCloudLicenseEditionRequest? body,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var name = body?.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 80 ||
        body!.MaxInstallations is < 1 or > 100 || body.DefaultDurationMonths is < 1 or > 120)
        return Results.BadRequest(new { error = "Συμπλήρωσε όνομα έκδοσης, 1–100 εγκαταστάσεις και διάρκεια 1–120 μηνών." });
    try
    {
        var updated = await database.UpdateLicenseEditionAsync(editionId, name, body.MaxInstallations,
            body.DefaultDurationMonths, body.IsActive, cancellationToken);
        return updated ? Results.Ok(new { status = "updated" }) : Results.NotFound(new { error = "Δεν βρέθηκε η έκδοση άδειας." });
    }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { error = "Υπάρχει ήδη έκδοση με αυτό το όνομα." });
    }
});

licenseManager.MapPost("/licenses", async (
    CreateCloudLicenseRequest? body,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var customerName = body?.CustomerName?.Trim();
    var vatNumber = body?.VatNumber?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(customerName) || customerName.Length > 160 || vatNumber.Length > 32)
        return Results.BadRequest(new { error = "Συμπλήρωσε επωνυμία έως 160 χαρακτήρες και ΑΦΜ έως 32 χαρακτήρες." });
    if (body!.MaxInstallations is < 1 or > 100)
        return Results.BadRequest(new { error = "Οι επιτρεπόμενες εγκαταστάσεις πρέπει να είναι από 1 έως 100." });
    var expiresAtUtc = body.ExpiresAtUtc?.UtcDateTime;
    if (expiresAtUtc is not null && expiresAtUtc <= DateTime.UtcNow)
        return Results.BadRequest(new { error = "Η ημερομηνία λήξης πρέπει να είναι στο μέλλον." });

    var licenseId = await database.CreateLicenseAsync(customerName, vatNumber, expiresAtUtc,
        body.MaxInstallations, cancellationToken);
    return Results.Created($"/api/license-manager/v1/licenses/{licenseId}", new { licenseId });
});

licenseManager.MapPut("/licenses/{licenseId:guid}/renew", async (
    Guid licenseId,
    RenewCloudLicenseRequest? body,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var expiresAtUtc = body?.ExpiresAtUtc?.UtcDateTime;
    if (expiresAtUtc is not null && expiresAtUtc <= DateTime.UtcNow)
        return Results.BadRequest(new { error = "Η ημερομηνία λήξης πρέπει να είναι στο μέλλον." });
    var renewed = await database.RenewLicenseAsync(licenseId, expiresAtUtc, cancellationToken);
    return renewed ? Results.Ok(new { status = "active" }) : Results.NotFound(new { error = "Δεν βρέθηκε η άδεια." });
});

licenseManager.MapPut("/licenses/{licenseId:guid}", async (
    Guid licenseId,
    UpdateManagedLicenseRequest? body,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var expiresAtUtc = body?.ExpiresAtUtc?.UtcDateTime;
    if (body is null || body.EditionId == Guid.Empty || expiresAtUtc is null || expiresAtUtc <= DateTime.UtcNow)
        return Results.BadRequest(new { error = "Επίλεξε έκδοση άδειας και ημερομηνία λήξης στο μέλλον." });
    var error = await database.UpdateLicenseTermsAsync(licenseId, body.EditionId, expiresAtUtc.Value, cancellationToken);
    return error switch
    {
        null => Results.Ok(new { status = "updated" }),
        "edition-not-found" => Results.Conflict(new { error = "Η έκδοση άδειας δεν είναι πλέον διαθέσιμη." }),
        "installation-limit" => Results.Conflict(new { error = "Η νέα έκδοση επιτρέπει λιγότερες εγκαταστάσεις από όσες είναι ήδη δεμένες." }),
        _ => Results.NotFound(new { error = "Δεν βρέθηκε ενεργή άδεια πελάτη." })
    };
});

licenseManager.MapPost("/licenses/{licenseId:guid}/revoke", async (
    Guid licenseId,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var revoked = await database.RevokeLicenseAsync(licenseId, cancellationToken);
    return revoked ? Results.Ok(new { status = "revoked" }) : Results.NotFound(new { error = "Δεν βρέθηκε η άδεια." });
});

licenseManager.MapPost("/installations/{installationId:guid}/assign", async (
    Guid installationId,
    AssignCloudLicenseRequest? body,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    if (body is null || body.LicenseId == Guid.Empty)
        return Results.BadRequest(new { error = "Επίλεξε έγκυρη άδεια." });
    var result = await database.AssignInstallationAsync(installationId, body.LicenseId, cancellationToken);
    return result switch
    {
        AssignInstallationResult.Success => Results.Ok(new { status = "assigned" }),
        AssignInstallationResult.LicenseNotFound => Results.NotFound(new { error = "Δεν βρέθηκε η άδεια." }),
        AssignInstallationResult.InstallationNotFound => Results.NotFound(new { error = "Δεν βρέθηκε η εγκατάσταση." }),
        AssignInstallationResult.LicenseInactive => Results.Conflict(new { error = "Η άδεια είναι ακυρωμένη ή έχει λήξει." }),
        _ => Results.Conflict(new { error = "Η άδεια έχει φτάσει το όριο ενεργών εγκαταστάσεων." })
    };
});

licenseManager.MapPost("/installations/{installationId:guid}/detach", async (
    Guid installationId,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var detached = await database.DetachInstallationAsync(installationId, cancellationToken);
    return detached ? Results.Ok(new { status = "detached" }) : Results.NotFound(new { error = "Η εγκατάσταση δεν έχει ενεργή άδεια." });
});

licenseManager.MapPost("/installations/{installationId:guid}/rebind", async (
    Guid installationId,
    CloudLicenseManagerDatabase database,
    CancellationToken cancellationToken) =>
{
    var requested = await database.RequestInstallationRebindAsync(installationId, cancellationToken);
    return requested
        ? Results.Ok(new { status = "rebind-pending" })
        : Results.NotFound(new { error = "Δεν βρέθηκε η εγκατάσταση." });
});

licenseManager.MapManagedCloudStoreCreation();

app.MapGet("/", () => Results.Redirect("/license-manager.html"));
app.MapGet("/license-manager", () => Results.Redirect("/license-manager.html"));

app.MapGet("/api/sync/v1/events", async (
    long? afterId,
    int? limit,
    HttpRequest request,
    CloudDatabase database,
    CloudSyncDeviceDatabase deviceDatabase,
    CancellationToken cancellationToken) =>
{
    var cursor = afterId ?? 0;
    var pageSize = limit ?? 100;
    if (cursor < 0)
        return Results.BadRequest(new { error = "Το afterId δεν μπορεί να είναι αρνητικό." });
    if (pageSize is < 1 or > 100)
        return Results.BadRequest(new { error = "Το limit πρέπει να είναι από 1 έως 100." });

    var signed = await AuthenticateSyncRequestAsync(request, deviceDatabase, cancellationToken);
    if (signed is null) return Results.Unauthorized();
    var events = await database.ReadEventsAfterAsync(signed.Value.StoreId, cursor, pageSize, cancellationToken);
    var nextCursor = events.Count == 0 ? cursor : events[^1].Cursor;
    return Results.Ok(new SyncPullResponse(signed.Value.StoreId, events, nextCursor, events.Count == pageSize));
});

app.MapGet("/api/sync/v1/entities", async (
    long? afterId,
    int? limit,
    HttpRequest request,
    CloudDatabase database,
    CloudSyncDeviceDatabase deviceDatabase,
    CancellationToken cancellationToken) =>
{
    var cursor = afterId ?? 0;
    var pageSize = limit ?? 100;
    if (cursor < 0)
        return Results.BadRequest(new { error = "Το afterId δεν μπορεί να είναι αρνητικό." });
    if (pageSize is < 1 or > 100)
        return Results.BadRequest(new { error = "Το limit πρέπει να είναι από 1 έως 100." });

    var signed = await AuthenticateSyncRequestAsync(request, deviceDatabase, cancellationToken);
    if (signed is null) return Results.Unauthorized();
    var entities = await database.ReadEntitySnapshotsAfterAsync(signed.Value.StoreId, cursor, pageSize, cancellationToken);
    var nextCursor = entities.Count == 0 ? cursor : entities[^1].Cursor;
    return Results.Ok(new SyncEntitySnapshotResponse(signed.Value.StoreId, entities, nextCursor, entities.Count == pageSize));
});

app.MapPost("/api/sync/v1/events", async (
    HttpRequest request,
    CloudDatabase database,
    CloudSyncDeviceDatabase deviceDatabase,
    CancellationToken cancellationToken) =>
{
    var signed = await AuthenticateSyncRequestAsync(request, deviceDatabase, cancellationToken);
    if (signed is null) return Results.Unauthorized();

    SyncBatchRequest? body;
    try
    {
        body = await request.ReadFromJsonAsync<SyncBatchRequest>(cancellationToken);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Το σώμα του αιτήματος δεν είναι έγκυρο JSON." });
    }

    var events = body?.Events;
    if (events is null || events.Count is < 1 or > 100)
        return Results.BadRequest(new { error = "Κάθε αποστολή πρέπει να περιέχει από 1 έως 100 συμβάντα." });

    var validationError = ValidateEvents(events);
    if (validationError is not null)
        return Results.BadRequest(new { error = validationError });

    try
    {
        var (accepted, duplicates) = await database.InsertEventsAsync(signed.Value.StoreId, events, cancellationToken);
        return Results.Ok(new SyncBatchResponse(signed.Value.StoreId, accepted, duplicates));
    }
    catch (SyncEventConflictException)
    {
        return Results.Conflict(new { error = "Ένα EventId έχει ήδη χρησιμοποιηθεί με διαφορετικά δεδομένα." });
    }
});

app.MapCloudPosEndpoints();
if (cloudWebEnabled)
    app.MapInternalEndpoints(cloudWebSyncToken!);

app.Run();

static bool HasMatchingSecret(string supplied, string expected)
{
    var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
    var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
    return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
}

static bool IsSameOrigin(HttpRequest request)
{
    var origin = request.Headers.Origin.ToString();
    return string.IsNullOrEmpty(origin) || Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase);
}

static async Task<DateTimeOffset?> GetLicenseManagerSessionExpiryAsync(HttpContext context, CloudLicenseDatabase database,
    bool refresh, CancellationToken cancellationToken)
{
    const string cookieName = "restaurantorders_license_manager_session";
    if (!context.Request.Cookies.TryGetValue(cookieName, out var token) || token.Length is < 32 or > 256)
        return null;
    return await database.GetLicenseManagerSessionAsync(SHA256.HashData(Encoding.UTF8.GetBytes(token)), refresh, cancellationToken);
}

static bool VerifyLicenseCheckRequest(HttpRequest request, RemoteLicenseCheckRequest? body, byte[] requestBody)
{
    if (body is null || !Guid.TryParse(body.InstallationId, out var installationId)) return false;
    var publicKeyHeader = request.Headers["X-License-Public-Key"].ToString();
    var timestamp = request.Headers["X-License-Timestamp"].ToString();
    var nonce = request.Headers["X-License-Nonce"].ToString();
    var signatureHeader = request.Headers["X-License-Signature"].ToString();
    if (publicKeyHeader.Length is < 32 or > 4096 || nonce.Length is < 24 or > 128 ||
        nonce.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
        !long.TryParse(timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var unixSeconds) ||
        Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds) > 300)
        return false;

    byte[] publicKey;
    byte[] signature;
    try
    {
        publicKey = Convert.FromBase64String(publicKeyHeader);
        signature = Convert.FromBase64String(signatureHeader);
    }
    catch (FormatException) { return false; }

    var bodyHash = Convert.ToHexString(SHA256.HashData(requestBody)).ToLowerInvariant();
    var canonical = $"POST\n/api/license/v1/check\n{installationId:D}\n{timestamp}\n{nonce}\n{bodyHash}";
    try
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
        return bytesRead == publicKey.Length && ecdsa.KeySize == 256 &&
            ecdsa.VerifyData(Encoding.UTF8.GetBytes(canonical), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
    catch (CryptographicException) { return false; }
}

static string? ReadBearerToken(string authorization)
{
    const string prefix = "Bearer ";
    if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        return null;

    var token = authorization[prefix.Length..].Trim();
    return token.Length is >= 32 and <= 256 ? token : null;
}

static async Task<(Guid StoreId, byte[] Body)?> AuthenticateSyncRequestAsync(
    HttpRequest request, CloudSyncDeviceDatabase deviceDatabase, CancellationToken cancellationToken)
{
    if (!Guid.TryParse(request.Headers["X-Installation-Id"].ToString(), out var installationId)) return null;
    var storeId = await deviceDatabase.FindActiveStoreIdAsync(installationId, cancellationToken);
    if (storeId is null) return null;
    var timestamp = request.Headers["X-Cloud-Timestamp"].ToString();
    var nonce = request.Headers["X-Cloud-Nonce"].ToString();
    var bodyHash = request.Headers["X-Cloud-Content-SHA256"].ToString();
    var signature = request.Headers["X-Cloud-Signature"].ToString();
    request.EnableBuffering();
    await using var memory = new MemoryStream();
    await request.Body.CopyToAsync(memory, cancellationToken);
    request.Body.Position = 0;
    var body = memory.ToArray();
    var valid = await deviceDatabase.ValidateAndRecordAsync(storeId.Value, installationId,
        request.Method, $"{request.Path}{request.QueryString}", timestamp, nonce, body, bodyHash, signature, cancellationToken);
    return valid ? (storeId.Value, body) : null;
}

static string? ValidateEvents(IReadOnlyList<SyncEventRequest?> events)
{
    var ids = new HashSet<Guid>();
    foreach (var item in events)
    {
        if (item is null)
            return "Η λίστα περιέχει κενό συμβάν.";
        if (item.EventId == Guid.Empty || !ids.Add(item.EventId))
            return "Κάθε συμβάν χρειάζεται μοναδικό, μη κενό EventId.";
        if (!MatchesName(item.EntityType, 80) || !MatchesName(item.EventType, 40))
            return "Το EntityType ή το EventType έχει μη έγκυρη μορφή.";
        if (string.IsNullOrWhiteSpace(item.EntityId) || item.EntityId.Length > 160)
            return "Το EntityId πρέπει να έχει 1 έως 160 χαρακτήρες.";
        if (item.SchemaVersion is < 1 or > 1000)
            return "Το SchemaVersion πρέπει να είναι από 1 έως 1000.";
        if (item.OccurredAtUtc == default)
            return "Το OccurredAtUtc είναι υποχρεωτικό.";
        if (item.OccurredAtUtc.Offset != TimeSpan.Zero)
            return "Το OccurredAtUtc πρέπει να αποστέλλεται σε UTC.";
        if (item.Payload.ValueKind != JsonValueKind.Object)
            return "Το Payload κάθε συμβάντος πρέπει να είναι αντικείμενο JSON.";
        if (JsonSerializer.SerializeToUtf8Bytes(item.Payload).Length > 3 * 1024 * 1024)
            return "Το Payload ενός συμβάντος δεν μπορεί να ξεπερνά τα 3 MB.";
    }

    return null;
}

static bool MatchesName(string? value, int maxLength) =>
    !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength &&
    Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

static string ReadConfiguredSecret(IConfiguration configuration, string directKey, string fileKey)
{
    var secretPath = configuration[fileKey];
    var value = !string.IsNullOrWhiteSpace(secretPath)
        ? File.ReadAllText(secretPath).TrimEnd('\r', '\n')
        : configuration[directKey];
    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"{directKey} or {fileKey} must be configured.")
        : value;
}


