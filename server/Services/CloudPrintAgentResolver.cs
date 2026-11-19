using Microsoft.AspNetCore.Components.Authorization;
using RestaurantOrders.Data;

namespace RestaurantOrders.Services;

/// <summary>Chooses between an already-paired local Windows printer and cloud-side TCP/Android printing.</summary>
public sealed class CloudPrintAgentResolver(IServiceProvider services, ILogger<CloudPrintAgentResolver> logger)
{
    public async Task<bool> ShouldQueueToWindowsAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (!db.IsCloudHosted) return false;

        try
        {
            var authentication = services.GetService<AuthenticationStateProvider>();
            var postgres = services.GetService<CloudTenantPostgresDatabase>();
            var state = authentication is null ? null : await authentication.GetAuthenticationStateAsync();
            var storeIdText = state?.User.FindFirst("cloud_store_id")?.Value;
            if (postgres is null || !Guid.TryParse(storeIdText, out var storeId))
            {
                logger.LogWarning("Could not determine the cloud store or print-agent client; keeping the print batch queued for Windows.");
                return true;
            }

            return await postgres.HasRegisteredPrintAgentAsync(storeId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fail closed: queueing is recoverable and avoids printing the same
            // ticket both from the cloud server and a Windows sync agent.
            logger.LogWarning(exception, "Could not check for a paired Windows print agent; keeping the cloud print batch queued.");
            return true;
        }
    }
}
