using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services.Fiscal;

public sealed class FiscalProviderExchangeStore(IDbContextFactory<AppDbContext> factory, ILogger<FiscalProviderExchangeStore> logger)
{
    // Never retry a fiscal request because audit persistence failed.
    public async Task RecordAsync(FiscalProviderExchange entry)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(2);
            db.FiscalProviderExchanges.Add(entry);
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { logger.LogError(ex, "Unable to save Parochos exchange history"); }
    }
}
