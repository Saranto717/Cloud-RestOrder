using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using RestaurantOrders.Data;

namespace RestaurantOrders.Services;

/// <summary>Routes cloud orders to a local print node instead of opening printer connections from the cloud host.</summary>
public sealed class CloudPrintAgentResolver(IServiceProvider services, ILogger<CloudPrintAgentResolver> logger)
{
    public async Task<bool> ShouldQueueToWindowsAsync(
        AppDbContext db,
        bool requiresWindowsUsbPrinter = false,
        CancellationToken cancellationToken = default)
    {
        if (!db.IsCloudHosted) return false;

        if (!requiresWindowsUsbPrinter)
        {
            try
            {
                var js = services.GetService<IJSRuntime>();
                if (js is not null && await js.InvokeAsync<bool>("restaurantOrdersAndroidPrinter.isAvailable", cancellationToken))
                {
                    logger.LogInformation("Using the current Android device as the local cloud printer node.");
                    return false;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "No current-device Android printer bridge is available; queueing the cloud print batch for a local POS node.");
            }
        }

        // Cloud-hosted code cannot reach a store's private LAN. Keep the accepted
        // batch durable for the paired local Windows POS/print node; never fall
        // back to sending raw TCP through the Docker host.
        logger.LogInformation("Queueing cloud print batch for a local POS node (Windows USB required: {RequiresWindowsUsbPrinter}).",
            requiresWindowsUsbPrinter);
        return true;
    }
}
