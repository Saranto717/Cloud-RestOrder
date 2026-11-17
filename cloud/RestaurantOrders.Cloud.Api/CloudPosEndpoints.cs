using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace RestaurantOrders.Cloud.Api;

public sealed record CloudPosLoginRequest(string? Email, string? Password);
public sealed record CloudPosCreateUserRequest(string? Email, string? Password, string? Role);
public sealed record CloudPosCreateOrderRequest(Guid RequestId);
public sealed record CloudPosAddItemRequest(Guid RequestId, int MenuItemId, int Quantity, string? Notes);
public sealed record CreateManagedCloudStoreRequest(Guid LicenseId, string? Name, string? AdminEmail, string? AdminPassword);

public static class CloudPosEndpoints
{
    private const string CookieName = "restaurantorders_cloud_session";

    public static void MapCloudPosEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (HttpContext context, CloudPosLoginRequest? body,
            CloudPosDatabase database, CancellationToken cancellationToken) =>
        {
            if (!IsSameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!IsSecureRequest(context.Request))
                return Results.Json(new { error = "Η σύνδεση με email και κωδικό λειτουργεί μόνο μέσω HTTPS." }, statusCode: 426);
            var email = body?.Email?.Trim();
            var password = body?.Password;
            if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || string.IsNullOrEmpty(password) || password.Length > 1024)
                return Results.Unauthorized();
            var credentials = await database.FindCredentialsAsync(email, cancellationToken);
            if (credentials is null || !CloudPasswordHasher.Verify(password, credentials.PasswordHash))
                return Results.Unauthorized();

            var token = StoreTokens.Create();
            var tokenBytes = Encoding.UTF8.GetBytes(token);
            await database.CreateSessionAsync(SHA256.HashData(tokenBytes), credentials.StoreId, credentials.Id, cancellationToken);
            SetSessionCookie(context.Response, token);
            return Results.Ok(new { email = credentials.Email, role = credentials.Role, expiresInSeconds = 600 });
        });

        app.MapGet("/api/auth/session", async (HttpContext context, CloudPosDatabase database, CancellationToken cancellationToken) =>
        {
            var session = await GetSessionAsync(context, database, refresh: false, cancellationToken);
            return session is null ? Results.Unauthorized() : Results.Ok(new { email = session.Email, role = session.Role, expiresAtUtc = session.ExpiresAtUtc });
        });

        app.MapPost("/api/auth/refresh", async (HttpContext context, CloudPosDatabase database, CancellationToken cancellationToken) =>
        {
            if (!IsSameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!IsSecureRequest(context.Request)) return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            var session = await GetSessionAsync(context, database, refresh: true, cancellationToken);
            if (session is null) return Results.Unauthorized();
            var token = context.Request.Cookies[CookieName]!;
            SetSessionCookie(context.Response, token);
            return Results.Ok(new { email = session.Email, role = session.Role, expiresAtUtc = session.ExpiresAtUtc });
        });

        app.MapPost("/api/auth/logout", async (HttpContext context, CloudPosDatabase database, CancellationToken cancellationToken) =>
        {
            if (context.Request.Cookies.TryGetValue(CookieName, out var token))
                await database.RevokeSessionAsync(SHA256.HashData(Encoding.UTF8.GetBytes(token)), cancellationToken);
            context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict });
            return Results.Ok(new { status = "signed-out" });
        });

        app.MapGet("/api/pos/v1/bootstrap", async (HttpContext context, CloudPosDatabase database, CancellationToken cancellationToken) =>
        {
            var session = await GetSessionAsync(context, database, refresh: false, cancellationToken);
            if (session is null) return Results.Unauthorized();
            return Results.Ok(await database.ReadBootstrapAsync(session.StoreId, cancellationToken));
        });

        app.MapPost("/api/pos/v1/tables/{tableId:int}/orders", async (int tableId, HttpContext context,
            CloudPosCreateOrderRequest? body, CloudPosDatabase database, CancellationToken cancellationToken) =>
        {
            if (!IsSameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var session = await GetSessionAsync(context, database, refresh: false, cancellationToken);
            if (session is null) return Results.Unauthorized();
            if (body is null || body.RequestId == Guid.Empty || tableId <= 0)
                return Results.BadRequest(new { error = "Το αίτημα δεν είναι έγκυρο." });
            try
            {
                var id = await database.CreateOrderAsync(session.StoreId, session.UserId, tableId, body.RequestId, cancellationToken);
                return Results.Ok(new { orderId = id });
            }
            catch (CloudPosConflictException exception) { return Results.Conflict(new { error = exception.Message }); }
        });

        app.MapPost("/api/pos/v1/orders/{orderId:int}/items", async (int orderId, HttpContext context,
            CloudPosAddItemRequest? body, CloudPosDatabase database, CancellationToken cancellationToken) =>
        {
            if (!IsSameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var session = await GetSessionAsync(context, database, refresh: false, cancellationToken);
            if (session is null) return Results.Unauthorized();
            if (body is null || body.RequestId == Guid.Empty || orderId >= 0 || body.MenuItemId <= 0 || body.Quantity is < 1 or > 99 || (body.Notes?.Length ?? 0) > 500)
                return Results.BadRequest(new { error = "Έλεγξε το είδος, την ποσότητα και τις σημειώσεις." });
            try
            {
                var id = await database.AddOrderItemAsync(session.StoreId, session.UserId, orderId, body.MenuItemId,
                    body.Quantity, body.Notes, body.RequestId, cancellationToken);
                return Results.Ok(new { orderItemId = id });
            }
            catch (CloudPosConflictException exception) { return Results.Conflict(new { error = exception.Message }); }
        });

        app.MapGet("/api/pos/v1/users", async (HttpContext context, CloudPosDatabase database, CancellationToken cancellationToken) =>
        {
            var session = await GetSessionAsync(context, database, refresh: false, cancellationToken);
            if (session is null) return Results.Unauthorized();
            if (session.Role != "admin") return Results.Forbid();
            return Results.Ok(await database.GetUsersAsync(session.StoreId, cancellationToken));
        });

        app.MapPost("/api/pos/v1/users", async (HttpContext context, CloudPosCreateUserRequest? body,
            CloudPosDatabase database, CancellationToken cancellationToken) =>
        {
            if (!IsSameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var session = await GetSessionAsync(context, database, refresh: false, cancellationToken);
            if (session is null) return Results.Unauthorized();
            if (session.Role != "admin") return Results.Forbid();
            var email = body?.Email?.Trim();
            var role = body?.Role?.Trim().ToLowerInvariant();
            if (!IsValidEmail(email) || body?.Password is not { Length: >= 12 and <= 1024 } password || role is not ("admin" or "waiter"))
                return Results.BadRequest(new { error = "Χρειάζεται έγκυρο email, κωδικός τουλάχιστον 12 χαρακτήρων και ρόλος." });
            try
            {
                var id = await database.CreateUserAsync(session.StoreId, email!, CloudPasswordHasher.Hash(password), role!, cancellationToken);
                return Results.Created($"/api/pos/v1/users/{id}", new { id, email, role });
            }
            catch (Npgsql.PostgresException exception) when (exception.SqlState == "23505")
            {
                return Results.Conflict(new { error = "Υπάρχει ήδη λογαριασμός με αυτό το email στο κατάστημα." });
            }
        });

        app.MapDelete("/api/pos/v1/users/{userId:int}", async (int userId, HttpContext context,
            CloudPosDatabase database, CancellationToken cancellationToken) =>
        {
            if (!IsSameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var session = await GetSessionAsync(context, database, refresh: false, cancellationToken);
            if (session is null) return Results.Unauthorized();
            if (session.Role != "admin") return Results.Forbid();
            return await database.DeactivateUserAsync(session.StoreId, userId, cancellationToken)
                ? Results.Ok(new { status = "deactivated" }) : Results.NotFound();
        });
    }

    public static void MapManagedCloudStoreCreation(this RouteGroupBuilder licenseManager)
    {
        licenseManager.MapPost("/cloud-stores", async (CreateManagedCloudStoreRequest? body,
            CloudPosDatabase database, CancellationToken cancellationToken) =>
        {
            var name = body?.Name?.Trim();
            var email = body?.AdminEmail?.Trim();
            if (body?.LicenseId == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 120 || !IsValidEmail(email) ||
                body?.AdminPassword is not { Length: >= 12 and <= 1024 } password)
                return Results.BadRequest(new { error = "Επίλεξε ενεργή άδεια και συμπλήρωσε όνομα, email και κωδικό τουλάχιστον 12 χαρακτήρων." });
            try
            {
                var created = await database.CreateStoreWithAdminAsync(body!.LicenseId, name!, email!, CloudPasswordHasher.Hash(password), cancellationToken);
                return Results.Created($"/api/license-manager/v1/cloud-stores/{created.StoreId}", created);
            }
            catch (CloudPosConflictException exception) { return Results.Conflict(new { error = exception.Message }); }
            catch (Npgsql.PostgresException exception) when (exception.SqlState == "23505")
            {
                return Results.Conflict(new { error = "Υπάρχει ήδη λογαριασμός με αυτό το email." });
            }
        });
    }

    private static async Task<CloudPosSession?> GetSessionAsync(HttpContext context, CloudPosDatabase database,
        bool refresh, CancellationToken cancellationToken)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrWhiteSpace(token) || token.Length > 256)
            return null;
        return await database.GetSessionAsync(SHA256.HashData(Encoding.UTF8.GetBytes(token)), refresh, cancellationToken);
    }

    private static void SetSessionCookie(HttpResponse response, string token) => response.Cookies.Append(CookieName, token,
        new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromMinutes(10) });

    private static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin) || Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSecureRequest(HttpRequest request) => request.IsHttps;

    private static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254) return false;
        try { return new MailAddress(email).Address == email; }
        catch (FormatException) { return false; }
    }
}
