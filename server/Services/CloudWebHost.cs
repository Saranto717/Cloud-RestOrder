using System.Globalization;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public sealed class CloudWebInternalToken(IConfiguration configuration)
{
    private readonly byte[] _hash = SHA256.HashData(Encoding.UTF8.GetBytes(Read(configuration)));

    public bool Matches(string supplied) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), _hash);

    private static string Read(IConfiguration configuration)
    {
        var path = configuration["CloudWeb:SyncTokenFile"];
        var value = string.IsNullOrWhiteSpace(path) ? configuration["CloudWeb:SyncToken"] : File.ReadAllText(path).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) < 32)
            throw new InvalidOperationException("Cloud web sync token must contain at least 32 bytes of secret material.");
        return value;
    }
}

public sealed class CloudTenantDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly string _dataDirectory;
    private readonly CloudSyncRuntimeState _syncState;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly IServiceProvider? _serviceProvider;

    public CloudTenantDbContextFactory(IHttpContextAccessor http, IConfiguration configuration,
        CloudSyncRuntimeState syncState, IServiceProvider serviceProvider)
    {
        _httpContextAccessor = http;
        _serviceProvider = serviceProvider;
        _dataDirectory = configuration["CloudWeb:DataDirectory"] ?? "/var/lib/restaurantorders/cloud-web";
        _syncState = syncState;
    }

    private CloudTenantDbContextFactory(Guid storeId, string dataDirectory, CloudSyncRuntimeState syncState)
    {
        _fixedStoreId = storeId;
        _dataDirectory = dataDirectory;
        _syncState = syncState;
    }

    private readonly Guid? _fixedStoreId;

    public static CloudTenantDbContextFactory ForStore(Guid storeId, string dataDirectory, CloudSyncRuntimeState syncState) =>
        new(storeId, dataDirectory, syncState);

    public AppDbContext CreateDbContext()
    {
        var storeId = ResolveStoreId();
        var storeDirectory = Path.Combine(_dataDirectory, storeId.ToString("N"));
        Directory.CreateDirectory(storeDirectory);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(storeDirectory, "restaurant.db")}")
            .Options;
        return new AppDbContext(options, Options.Create(new CloudSyncOptions { Enabled = true }), _syncState, cloudHosted: true);
    }

    public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        var storeId = await ResolveStoreIdAsync(cancellationToken);
        var storeDirectory = Path.Combine(_dataDirectory, storeId.ToString("N"));
        Directory.CreateDirectory(storeDirectory);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(storeDirectory, "restaurant.db")}")
            .Options;
        return new AppDbContext(options, Options.Create(new CloudSyncOptions { Enabled = true }), _syncState, cloudHosted: true);
    }

    private Guid ResolveStoreId()
    {
        if (_fixedStoreId is Guid fixedStore) return fixedStore;
        var headerValue = _httpContextAccessor?.HttpContext?.Request.Headers["X-Cloud-Store-Id"].ToString();
        if (Guid.TryParse(headerValue, out var fromHeader)) return fromHeader;
        var provider = _serviceProvider?.GetService<AuthenticationStateProvider>();
        var state = provider?.GetAuthenticationStateAsync().GetAwaiter().GetResult();
        var claimValue = state?.User.FindFirst("cloud_store_id")?.Value;
        return Guid.TryParse(claimValue, out var fromClaim) ? fromClaim :
            throw new InvalidOperationException("Το cloud store δεν έχει πιστοποιηθεί από το API gateway.");
    }

    private async Task<Guid> ResolveStoreIdAsync(CancellationToken cancellationToken)
    {
        if (_fixedStoreId is Guid fixedStore) return fixedStore;
        var headerValue = _httpContextAccessor?.HttpContext?.Request.Headers["X-Cloud-Store-Id"].ToString();
        if (Guid.TryParse(headerValue, out var fromHeader)) return fromHeader;
        var provider = _serviceProvider?.GetService<AuthenticationStateProvider>();
        if (provider is not null)
        {
            var state = await provider.GetAuthenticationStateAsync();
            var claimValue = state.User.FindFirst("cloud_store_id")?.Value;
            if (Guid.TryParse(claimValue, out var fromClaim)) return fromClaim;
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Το cloud store δεν έχει πιστοποιηθεί από το API gateway.");
    }
}

public sealed class CloudTenantDatabaseProvisioner(IConfiguration configuration, CloudSyncRuntimeState syncState)
{
    private readonly string _dataDirectory = configuration["CloudWeb:DataDirectory"] ?? "/var/lib/restaurantorders/cloud-web";
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> InitializedStores = new();

    public async Task EnsureInitializedAsync(Guid storeId, int cloudUserId, string email, Role role, CancellationToken cancellationToken)
    {
        var tenantLock = Locks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));
        await tenantLock.WaitAsync(cancellationToken);
        try
        {
            var factory = CloudTenantDbContextFactory.ForStore(storeId, _dataDirectory, syncState);
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            if (!InitializedStores.ContainsKey(storeId))
            {
                DataSeeder.Seed(db, createDefaultAdmin: false);
                InitializedStores.TryAdd(storeId, 0);
            }
            var shadowUserId = GetShadowUserId(cloudUserId);
            var user = await db.Users.SingleOrDefaultAsync(item => item.Id == shadowUserId, cancellationToken);
            if (user is null)
            {
                user = new User
                {
                    Id = shadowUserId,
                    IsCloudIdentity = true
                };
                db.Users.Add(user);
            }
            user.FirstName = string.IsNullOrWhiteSpace(email) ? "Cloud χρήστης" : email;
            user.Role = role;
            user.IsActive = true;
            user.IsCloudIdentity = true;
            // Replicate audit identities before orders that reference them. Local
            // login explicitly rejects these identities, regardless of their role.
            await db.SaveChangesAsync(cancellationToken);
        }
        finally { tenantLock.Release(); }
    }

    internal static int GetShadowUserId(int cloudUserId)
    {
        if (cloudUserId <= 0 || cloudUserId > int.MaxValue - 1_000_000_000)
            throw new InvalidOperationException("Το cloud user id είναι εκτός ορίων.");
        return -1_000_000_000 - cloudUserId;
    }
}

public sealed class CloudGatewayAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    IDbContextFactory<AppDbContext> dbFactory,
    CloudWebInternalToken internalToken)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var storeId = Request.Headers["X-Cloud-Store-Id"].ToString();
        var cloudUserId = Request.Headers["X-Cloud-User-Id"].ToString();
        var email = Request.Headers["X-Cloud-Email"].ToString();
        var roleValue = Request.Headers["X-Cloud-Role"].ToString();
        if (!Guid.TryParse(storeId, out var parsedStore) || !int.TryParse(cloudUserId, out var parsedUser) ||
            !Enum.TryParse<Role>(roleValue, true, out var cloudRole) ||
            !internalToken.Matches(Request.Headers["X-Cloud-Internal-Token"].ToString()))
            return AuthenticateResult.NoResult();

        await using var db = await dbFactory.CreateDbContextAsync(Context.RequestAborted);
        var shadowUserId = CloudTenantDatabaseProvisioner.GetShadowUserId(parsedUser);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(
            item => item.Id == shadowUserId && item.IsCloudIdentity && item.IsActive && item.Role == cloudRole,
            Context.RequestAborted);
        if (user is null) return AuthenticateResult.Fail("Δεν υπάρχει ενεργός χρήστης για τον cloud ρόλο.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(email) ? user.FullName : email),
            new(ClaimTypes.Role, cloudRole.ToString()),
            new("cloud_store_id", parsedStore.ToString("D")),
            new("cloud_user_id", parsedUser.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Email, email)
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

public sealed class CloudTenantSyncWorker(
    IConfiguration configuration,
    CloudSyncRuntimeState syncState,
    ILogger<CloudTenantSyncWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxSyncBatchBytes = 4 * 1024 * 1024;
    private readonly string _dataDirectory = configuration["CloudWeb:DataDirectory"] ?? "/var/lib/restaurantorders/cloud-web";
    private readonly string _baseUrl = (configuration["CloudWeb:SyncBaseUrl"] ?? "http://cloud-api:8080/").TrimEnd('/') + "/";
    private readonly string _token = ReadSecret(configuration, "CloudWeb:SyncToken", "CloudWeb:SyncTokenFile");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        syncState.SetEnabled(true);
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl), Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var stores = await client.GetFromJsonAsync<Guid[]>("api/internal/cloud-web/stores", JsonOptions, stoppingToken) ?? [];
                foreach (var storeId in stores)
                {
                    try { await SyncStoreAsync(client, storeId, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception exception) { logger.LogWarning(exception, "Cloud application sync failed for store {StoreId}.", storeId); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Could not reach the cloud API for application sync."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task SyncStoreAsync(HttpClient client, Guid storeId, CancellationToken cancellationToken)
    {
        var tenantLock = CloudTenantDatabaseProvisioner.Locks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));
        await tenantLock.WaitAsync(cancellationToken);
        try
        {
            var factory = CloudTenantDbContextFactory.ForStore(storeId, _dataDirectory, syncState);
            await using (var initialization = await factory.CreateDbContextAsync(cancellationToken))
            {
                if (!CloudTenantDatabaseProvisioner.InitializedStores.ContainsKey(storeId))
                {
                    DataSeeder.Seed(initialization, createDefaultAdmin: false);
                    CloudTenantDatabaseProvisioner.InitializedStores.TryAdd(storeId, 0);
                }
            }
            await PushAsync(client, factory, storeId, cancellationToken);
            await PullAsync(client, factory, storeId, cancellationToken);
            var pending = await CloudSyncImporter.ApplyPendingAsync(factory, cancellationToken);
            if (pending > 0) logger.LogWarning("Cloud store {StoreId} has {Pending} received changes waiting for dependencies or local delivery.", storeId, pending);
        }
        finally { tenantLock.Release(); }
    }

    private static async Task PullAsync(HttpClient client, IDbContextFactory<AppDbContext> factory, Guid storeId, CancellationToken cancellationToken)
    {
        long cursor;
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
            cursor = (await db.CloudSyncSettings.AsNoTracking().SingleAsync(item => item.Id == 1, cancellationToken)).LastCloudCursor;

        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            var page = await client.GetFromJsonAsync<SyncPage>($"api/internal/cloud-web/stores/{storeId:D}/entities?afterId={cursor}&limit=100", JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("The cloud API returned an empty sync page.");
            if (page.StoreId != storeId || page.NextCursor < cursor)
                throw new InvalidDataException("The cloud API returned an invalid tenant or cursor.");
            await CloudSyncImporter.ReceivePageAsync(factory, page.Entities, page.NextCursor, cancellationToken);
            cursor = page.NextCursor;
            if (!page.HasMore || page.Entities.Count == 0) break;
        }
    }

    private static async Task PushAsync(HttpClient client, IDbContextFactory<AppDbContext> factory, Guid storeId, CancellationToken cancellationToken)
    {
        for (var batchNumber = 0; batchNumber < 10; batchNumber++)
        {
            List<CloudSyncOutboxEvent> pending;
            await using (var db = await factory.CreateDbContextAsync(cancellationToken))
                pending = await db.CloudSyncOutboxEvents.Where(item => item.AcknowledgedAtUtc == null).OrderBy(item => item.Id).Take(100).ToListAsync(cancellationToken);
            if (pending.Count == 0) break;
            var events = new List<PushEvent>(pending.Count);
            var body = JsonSerializer.SerializeToUtf8Bytes(new PushBatch(events), JsonOptions);
            foreach (var item in pending)
            {
                using var document = JsonDocument.Parse(item.PayloadJson);
                var candidate = new PushEvent(item.EventId, item.EntityType, item.EntityId, item.EventType, item.SchemaVersion, item.OccurredAtUtc, document.RootElement.Clone());
                var candidateBody = JsonSerializer.SerializeToUtf8Bytes(new PushBatch(events.Append(candidate).ToArray()), JsonOptions);
                if (candidateBody.Length > MaxSyncBatchBytes)
                {
                    if (events.Count == 0)
                        throw new InvalidDataException("A cloud sync event exceeds the 4 MB batch limit.");
                    break;
                }
                events.Add(candidate);
                body = candidateBody;
            }
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await client.PostAsync($"api/internal/cloud-web/stores/{storeId:D}/events", content, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Sync push failed with HTTP {(int)response.StatusCode}.");
            var result = await response.Content.ReadFromJsonAsync<PushAcknowledgement>(JsonOptions, cancellationToken);
            if (result is null || result.StoreId != storeId || result.Accepted + result.Duplicates != events.Count)
                throw new InvalidDataException("The cloud API did not acknowledge the complete batch for this store.");
            await using var update = await factory.CreateDbContextAsync(cancellationToken);
            update.SuppressCloudSyncCapture = true;
            var ids = events.Select(item => item.EventId).ToHashSet();
            var ack = await update.CloudSyncOutboxEvents.Where(item => ids.Contains(item.EventId)).ToListAsync(cancellationToken);
            foreach (var item in ack) item.AcknowledgedAtUtc = DateTimeOffset.UtcNow;
            await update.SaveChangesAsync(cancellationToken);
        }
    }

    private static string ReadSecret(IConfiguration configuration, string key, string fileKey)
    {
        var path = configuration[fileKey];
        var value = string.IsNullOrWhiteSpace(path) ? configuration[key] : File.ReadAllText(path).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) < 32)
            throw new InvalidOperationException("Cloud web sync token must contain at least 32 bytes of secret material.");
        return value;
    }

    private sealed record SyncPage(Guid StoreId, IReadOnlyList<CloudSyncEntity> Entities, long NextCursor, bool HasMore);
    private sealed record PushAcknowledgement(Guid StoreId, int Accepted, int Duplicates);
    private sealed record PushBatch(IReadOnlyList<PushEvent> Events);
    private sealed record PushEvent(Guid EventId, string EntityType, string EntityId, string EventType, int SchemaVersion, DateTimeOffset OccurredAtUtc, JsonElement Payload);
}
