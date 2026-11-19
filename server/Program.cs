using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Components;
using RestaurantOrders.Data;
using RestaurantOrders.Models;
using RestaurantOrders.Services;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using RestaurantOrders.Infrastructure;
using RestaurantOrders.Services.Fiscal;
using RestaurantOrders.Services.Fiscal.Provider;
using RestaurantOrders.Services.Orders;

if (InstalledApplication.HandleImport(args)) return;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()
        ? AppContext.BaseDirectory : null
});
var installedDataDirectory = InstalledApplication.Configure(builder);
var cloudWebMode = builder.Configuration.GetValue<bool>("CloudWeb:Enabled");
if (cloudWebMode)
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
builder.Services.AddDataProtection();

// Enable legacy code pages used by ESC/POS printers (e.g. Windows-1253).
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Επιτρέπει στην εφαρμογή να τρέχει σαν Windows Service (όταν εγκατασταθεί ως τέτοια),
// χωρίς να επηρεάζει καθόλου το κανονικό `dotnet run` σε development.
builder.Host.UseWindowsService(options => options.ServiceName = "RestaurantOrders");

builder.Services.AddSingleton(_ => new LicenseService(installedDataDirectory, builder.Configuration));
builder.Services.AddHttpClient<RemoteLicenseClient>(client => client.Timeout = TimeSpan.FromSeconds(8));
builder.Services.Configure<CloudSyncOptions>(builder.Configuration.GetSection("CloudSync"));
builder.Services.AddSingleton<CloudSyncRuntimeState>();
builder.Services.AddSingleton<CloudSyncCredentialProtector>();
builder.Services.AddSingleton<CloudSyncInitialSnapshotSeeder>();
if (cloudWebMode)
{
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<CloudTenantPostgresDatabase>();
    builder.Services.AddScoped<IDbContextFactory<AppDbContext>, CloudTenantDbContextFactory>();
    builder.Services.AddSingleton<CloudTenantDatabaseProvisioner>();
    builder.Services.AddSingleton<CloudWebInternalToken>();
    builder.Services.AddHostedService<CloudTenantSyncWorker>();
}
else
{
    builder.Services.AddDbContextFactory<AppDbContext>(options =>
        options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=restaurant.db"));
    builder.Services.AddHostedService<RemoteLicenseSyncWorker>();
    builder.Services.AddHttpClient(nameof(CloudSyncOutboxWorker), client => client.Timeout = TimeSpan.FromSeconds(20));
    builder.Services.AddHostedService<CloudSyncOutboxWorker>();
    builder.Services.AddSingleton<DatabaseBackupService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DatabaseBackupService>());
    builder.Services.AddSingleton<LocalDatabaseLocationService>();
}
builder.Services.AddScoped<PrinterService>();
builder.Services.AddScoped<DatabaseExplorerService>();
builder.Services.AddHttpClient("Parochos", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient("ePay-WebECR", client =>
{
    // Match the headers observed in the successful Postman request.
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PostmanRuntime/7.53.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.CacheControl =
        new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
    client.DefaultRequestHeaders.Connection.Add("keep-alive");
})
.ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler
{
    // Imperva may issue session cookies before allowing API requests.
    // Keep the cookies automatically; never hard-code session cookies from Postman.
    UseCookies = true,
    CookieContainer = new System.Net.CookieContainer(),
    AllowAutoRedirect = true
});
builder.Services.AddScoped<EpayWebEcrService>();
builder.Services.AddHttpClient("Mellon-WebECR");
builder.Services.AddScoped<MellonWebEcrService>();
builder.Services.AddHttpClient("Viva-Cloud-Terminal");
builder.Services.AddScoped<VivaCloudTerminalService>();
builder.Services.AddHttpClient("EDPS-WebECR");
builder.Services.AddScoped<EdpsWebEcrService>();
builder.Services.AddHttpClient("AADE-Registry");
if (cloudWebMode)
{
    builder.Services.AddScoped<FiscalProviderExchangeStore>();
    builder.Services.AddScoped<IParochosFiscalProvider, ParochosService>();
}
else
{
    builder.Services.AddSingleton<FiscalProviderExchangeStore>();
    builder.Services.AddSingleton<IParochosFiscalProvider, ParochosService>();
}
builder.Services.AddTransient<AadeRegistryService>();
builder.Services.AddScoped<IFiscalDocumentService, ParochosFiscalDocumentService>();
builder.Services.AddScoped<FiscalConfigurationService>();
builder.Services.AddScoped<FiscalCollisionInspector>();
builder.Services.AddScoped<FiscalCardPaymentService>();
builder.Services.AddScoped<FiscalCancellationService>();
builder.Services.AddScoped<FiscalOrderFormService>();
builder.Services.AddScoped<KitchenOrderDispatchService>();
builder.Services.AddScoped<TableTransferService>();
builder.Services.AddScoped<InventoryService>();


var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = cloudWebMode ? "CloudGatewayOrCookie" : CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = cloudWebMode ? "CloudGatewayOrCookie" : CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme;
});
authBuilder.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.LoginPath = "/";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        if (cloudWebMode)
        {
            options.Cookie.Name = "restaurantorders_staff_auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Events.OnValidatePrincipal = async context =>
            {
                var requestStoreId = context.HttpContext.Request.Headers["X-Cloud-Store-Id"].ToString();
                var cookieStoreId = context.Principal?.FindFirst("cloud_store_id")?.Value;
                if (Guid.TryParse(requestStoreId, out var requestStore) &&
                    Guid.TryParse(cookieStoreId, out var cookieStore) && requestStore == cookieStore)
                    return;

                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            };
        }
    });
if (cloudWebMode)
{
    authBuilder.AddScheme<AuthenticationSchemeOptions, CloudGatewayAuthenticationHandler>("CloudGateway", _ => { });
    authBuilder.AddPolicyScheme("CloudGatewayOrCookie", "Cloud gateway or local account", options =>
        options.ForwardDefaultSelector = context => context.Request.Headers.ContainsKey("X-Cloud-Store-Id") &&
            !context.Request.Cookies.ContainsKey("restaurantorders_staff_auth")
                ? "CloudGateway" : CookieAuthenticationDefaults.AuthenticationScheme);
}

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Το Blazor Server circuit έχει προεπιλεγμένο όριο μηνύματος 32KB - το μεγαλώνουμε
// ώστε να χωράνε ανεβάσματα αρχείων Excel (μαζική εισαγωγή καταλόγου).
builder.Services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(options =>
{
    options.MaximumReceiveMessageSize = 15 * 1024 * 1024; // 15MB
});

var app = builder.Build();

// Η εγκατάσταση Windows έχει μία τοπική βάση. Το cloud web προετοιμάζει
// ξεχωριστή βάση ανά store όταν το API gateway πιστοποιήσει τον χρήστη.
if (!cloudWebMode)
{
    using var scope = app.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = factory.CreateDbContext();
    DataSeeder.Seed(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();

// The SUNMI Capacitor app hosts this server in an iframe from its local
// http://localhost origin. ASP.NET Core emits X-Frame-Options: SAMEORIGIN for
// Razor component responses, which blocks that cross-origin LAN embedding.
// Replace that legacy header with a strict CSP allow-list: only this server's
// own origin and the local SUNMI app may frame it. This does not depend on
// Sec-Fetch or Referer headers, which are not consistently sent by WebViews.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.Remove("X-Frame-Options");
        context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self' http://localhost";
        return Task.CompletedTask;
    });
    await next();
});

if (cloudWebMode)
{
    app.Use(async (context, next) =>
    {
        var token = context.RequestServices.GetRequiredService<CloudWebInternalToken>();
        var storeText = context.Request.Headers["X-Cloud-Store-Id"].ToString();
        var userText = context.Request.Headers["X-Cloud-User-Id"].ToString();
        var roleText = context.Request.Headers["X-Cloud-Role"].ToString();
        if (!token.Matches(context.Request.Headers["X-Cloud-Internal-Token"].ToString()) ||
            !Guid.TryParse(storeText, out var storeId) || !int.TryParse(userText, out var cloudUserId) ||
            !Enum.TryParse<Role>(roleText, true, out var role))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var provisioner = context.RequestServices.GetRequiredService<CloudTenantDatabaseProvisioner>();
        await provisioner.EnsureInitializedAsync(storeId, cloudUserId, context.Request.Headers["X-Cloud-Email"].ToString(), role, context.RequestAborted);
        await next();
    });
}

// An unlicensed installed server can only show the activation page and keep
// its Blazor circuit alive. Development runs are not license-gated.
app.Use(async (context, next) =>
{
    var licenseService = context.RequestServices.GetRequiredService<LicenseService>();
    var path = context.Request.Path;
    var activationPath = path.StartsWithSegments("/license") ||
                         path.StartsWithSegments("/_blazor") ||
                         path.StartsWithSegments("/_framework") ||
                         path.StartsWithSegments("/health/ready");
    if (!licenseService.EnforcementEnabled || licenseService.GetStatus().IsLicensed || activationPath)
    {
        await next();
        return;
    }

    if (HttpMethods.IsGet(context.Request.Method) && context.Request.Headers["Accept"].Any(x => x?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true))
    {
        context.Response.Redirect("/license");
        return;
    }
    context.Response.StatusCode = StatusCodes.Status423Locked;
    await context.Response.WriteAsync("Η εφαρμογή χρειάζεται ενεργοποίηση άδειας χρήσης.");
});

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

if (installedDataDirectory is not null)
    app.MapGet("/health/ready", () => Results.Text("ready"));

// ---- Login / Logout endpoints (πλαίσιο εκτός Blazor circuit) ----

async Task SignInUser(HttpContext http, User user)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.FullName),
        new(ClaimTypes.Role, user.Role.ToString())
    };
    if (cloudWebMode)
    {
        if (!Guid.TryParse(http.Request.Headers["X-Cloud-Store-Id"], out var storeId))
            throw new InvalidOperationException("Η συνεδρία καταστήματος δεν είναι έγκυρη.");
        claims.Add(new Claim("cloud_store_id", storeId.ToString("D")));
    }
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
}

if (cloudWebMode)
{
    app.MapPost("/api/bootstrap-admin", async (HttpContext http, IDbContextFactory<AppDbContext> dbFactory,
        IConfiguration configuration) =>
    {
        if (http.User.Identity?.IsAuthenticated != true || !http.User.IsInRole("Store") ||
            !string.Equals(http.User.FindFirst("cloud_user_role")?.Value, Role.Admin.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(http.Request.Headers["X-Cloud-Store-Id"], out var storeId) ||
            !string.Equals(http.User.FindFirst("cloud_store_id")?.Value, storeId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            return Results.Forbid();

        var storeDirectory = Path.Combine(configuration["CloudWeb:DataDirectory"] ?? "/var/lib/restaurantorders/cloud-web", storeId.ToString("N"));
        if (File.Exists(Path.Combine(storeDirectory, "restaurant.db")) &&
            !File.Exists(Path.Combine(storeDirectory, "postgres.migrated")))
            return Results.Conflict("Γίνεται μεταφορά των υπαρχόντων δεδομένων του καταστήματος. Περίμενε λίγο και ξαναδοκίμασε.");

        var origin = http.Request.Headers.Origin.ToString();
        var externalHost = http.Request.Headers["X-Cloud-External-Host"].ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || string.IsNullOrWhiteSpace(externalHost) ||
            !string.Equals(originUri.Host, externalHost, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("Η δημιουργία διαχειριστή πρέπει να ξεκινήσει από τη σελίδα του καταστήματος.");

        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var fullName = form["fullName"].ToString().Trim();
        var pin = form["pin"].ToString();
        var pinConfirmation = form["pinConfirmation"].ToString();
        if (fullName.Length is < 2 or > 120 || fullName.Any(char.IsControl) ||
            pin.Length != 4 || pin.Any(character => !char.IsAsciiDigit(character)) || pin != pinConfirmation)
            return Results.BadRequest("Συμπλήρωσε όνομα και ίδιο PIN τεσσάρων ψηφίων.");

        var tenantLock = CloudTenantDatabaseProvisioner.Locks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));
        await tenantLock.WaitAsync(http.RequestAborted);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(http.RequestAborted);
            if (await db.Users.AnyAsync(user => user.IsActive && !user.IsCloudIdentity && user.Role == Role.Admin,
                    http.RequestAborted))
                return Results.Conflict("Έχει ήδη δημιουργηθεί διαχειριστής για αυτό το κατάστημα.");

            var nameParts = fullName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var user = new User
            {
                FirstName = nameParts[0],
                LastName = nameParts.Length > 1 ? nameParts[1] : null,
                CodeHash = PasswordHasher.Hash(pin),
                Role = Role.Admin,
                IsActive = true
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(http.RequestAborted);
            await SignInUser(http, user);
            return Results.Redirect("/admin/tables");
        }
        finally { tenantLock.Release(); }
    });
}

// Σύνδεση διαχειριστή: πατάει την καρτέλα του (μόνο διαχειριστές εμφανίζονται) + δικός του κωδικός.
// Χρησιμοποιείται και για το flow "Προσθήκη χρηστών" μέσω του returnUrl.
app.MapPost("/api/login", async (HttpContext http, IDbContextFactory<AppDbContext> dbFactory) =>
{
    var form = await http.Request.ReadFormAsync();
    var returnUrl = form["returnUrl"].ToString();

    if (!int.TryParse(form["userId"], out var userId))
    {
        return Results.Redirect("/login");
    }
    var code = form["password"].ToString();

    using var db = await dbFactory.CreateDbContextAsync();
    var user = await db.Users.FindAsync(userId);

    var valid = user is not null && user.IsActive && !user.IsCloudIdentity && user.Role == Role.Admin
        && (!user.HasCode || PasswordHasher.Verify(code, user.CodeHash!));

    if (!valid)
    {
        var errorReturn = string.IsNullOrWhiteSpace(returnUrl) ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        return Results.Redirect($"/login?error=1{errorReturn}");
    }

    await SignInUser(http, user!);

    // Ασφαλές μόνο αν οδηγεί σε εσωτερική σελίδα διαχειριστή
    if (!string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith("/admin/"))
    {
        return Results.Redirect(returnUrl);
    }

    return Results.Redirect("/admin/tables");
});

// Σύνδεση για "Πάρτε παραγγελία": εμφανίζονται όλοι οι ενεργοί χρήστες, ο καθένας με τον δικό του κωδικό (αν έχει)
app.MapPost("/api/staff-login", async (HttpContext http, IDbContextFactory<AppDbContext> dbFactory) =>
{
    var form = await http.Request.ReadFormAsync();
    if (!int.TryParse(form["userId"], out var userId))
    {
        return Results.Redirect("/");
    }
    var code = form["pin"].ToString();

    using var db = await dbFactory.CreateDbContextAsync();
    var user = await db.Users.FindAsync(userId);

    var valid = user is not null && user.IsActive && !user.IsCloudIdentity
        && (!user.HasCode || PasswordHasher.Verify(code, user.CodeHash!));

    if (!valid)
    {
        return Results.Redirect($"/order-login?pinerror=1&uid={userId}");
    }

    await SignInUser(http, user!);

    return Results.Redirect("/waiter");
});

app.MapPost("/api/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

// Εξαγωγή αναφοράς πωλήσεων σε Excel (.xlsx) - μόνο για διαχειριστές
app.MapGet("/api/reports/export", async (HttpContext http, IDbContextFactory<AppDbContext> dbFactory, string? from, string? to) =>
{
    var fromDate = DateTime.TryParse(from, out var f) ? f : DateTime.Today;
    var toDate = DateTime.TryParse(to, out var t) ? t : DateTime.Today;

    using var db = await dbFactory.CreateDbContextAsync();
    var rows = await ReportService.GetSalesReport(db, fromDate, toDate);
    var bytes = ReportService.BuildExcel(rows, fromDate, toDate);

    var fileName = $"anafora-poliseon_{fromDate:yyyy-MM-dd}_{toDate:yyyy-MM-dd}.xlsx";
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
})
.RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { Roles = "Admin" });

// Εξαγωγή συγκεκριμένου κλεισίματος ταμείου σε PDF - μόνο για διαχειριστές
app.MapGet("/api/cashier/day-close-pdf", async (HttpContext http, IDbContextFactory<AppDbContext> dbFactory, int? id) =>
{
    if (id is null)
    {
        return Results.BadRequest("Λείπει το id του κλεισίματος.");
    }

    using var db = await dbFactory.CreateDbContextAsync();
    var record = await CashierService.GetClosingById(db, id.Value);
    if (record is null)
    {
        return Results.NotFound();
    }

    var bytes = CashierService.BuildDayClosePdf(record);
    var fileName = $"kleisimo-tameiou_{record.ClosedAt:yyyy-MM-dd_HHmm}.pdf";
    return Results.File(bytes, "application/pdf", fileName);
})
.RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { Roles = "Admin" });

if (!cloudWebMode)
{
    app.MapGet("/api/admin/backups/{fileName}", (string fileName, DatabaseBackupService backups) =>
    {
        var path = backups.GetBackupPath(fileName);
        return path is null
            ? Results.NotFound()
            : Results.File(path, "application/octet-stream", fileName);
    })
    .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { Roles = "Admin" });
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
