using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;
using RestaurantOrders.Services;

namespace RestaurantOrders.Services.Orders;

public sealed record KitchenOrderDispatchResult(
    bool HasItems,
    int BatchId,
    int DailyOrderNumber,
    IReadOnlyList<OrderItem> SubmittedItems,
    IReadOnlyList<string> SuccessfulStations,
    IReadOnlyList<string> FailedStations,
    IReadOnlyList<string> CategoriesWithoutPrinter);

/// <summary>
/// Owns kitchen/bar dispatch, print-batch numbering and the order items' sent state.
/// It deliberately has no fiscal-provider dependency.
/// </summary>
public sealed class KitchenOrderDispatchService(PrinterService printer)
{
    public async Task<KitchenOrderDispatchResult> DispatchPendingAsync(
        AppDbContext db,
        int orderId,
        string tableName,
        string waiterName,
        string orderComment,
        bool tableService = true)
    {
        var pending = await db.OrderItems
            .Include(item => item.MenuItem)
                .ThenInclude(menuItem => menuItem!.MenuCategory)
                    .ThenInclude(category => category!.PrinterStations)
            .Include(item => item.MenuItem).ThenInclude(menu => menu!.MenuCategory).ThenInclude(category => category!.PrinterStation)
            .Where(item => item.OrderId == orderId && !item.IsPrinted && !item.IsCancelled)
            .ToListAsync();

        if (pending.Count == 0)
            return new KitchenOrderDispatchResult(false, 0, 0, Array.Empty<OrderItem>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        var companySettings = await db.CompanySettings.FirstOrDefaultAsync();
        var skipKitchenPrint = !tableService && companySettings?.TakeAwayReceiptOnly == true;
        var withPrinter = skipKitchenPrint ? new List<OrderItem>() : pending.Where(item => GetCategoryPrinters(item.MenuItem?.MenuCategory).Count > 0).ToList();
        var withoutPrinter = skipKitchenPrint ? new List<OrderItem>() : pending.Where(item => GetCategoryPrinters(item.MenuItem?.MenuCategory).Count == 0).ToList();
        var dailyOrderNumber = await CashierService.ReserveNextOrderNumberAsync(db);
        var batchNumber = (await db.OrderPrintBatches
            .Where(batch => batch.OrderId == orderId)
            .MaxAsync(batch => (int?)batch.Number) ?? 0) + 1;

        var acceptedAt = DateTime.Now;
        foreach (var item in pending)
        {
            item.IsPrinted = true;
            item.PrintedAt = acceptedAt;
            item.KitchenPrintUnconfirmed = withPrinter.Contains(item);
        }
        var batch = new OrderPrintBatch
        {
            OrderId = orderId,
            Number = batchNumber,
            DailyNumber = dailyOrderNumber,
            CreatedAt = DateTime.Now,
            DispatchNotificationPending = tableService,
            AcceptedTableName = tableName,
            AcceptedLinesJson = System.Text.Json.JsonSerializer.Serialize(pending.Select(AcceptedOrderLine.From)),
            Items = pending
        };
        db.OrderPrintBatches.Add(batch);
        await db.SaveChangesAsync();

        var groups = withPrinter
            .SelectMany(item => GetCategoryPrinters(item.MenuItem?.MenuCategory)
                .Select(station => new { Station = station, Item = item }))
            .GroupBy(route => route.Station)
            .ToList();
        var successfulStations = new List<string>();
        var failedStations = new List<string>();
        var itemsWithFailedPrinter = new HashSet<int>();

        foreach (var group in groups)
        {
            var station = group.Key;
            var ticketItems = group.Select(route => route.Item)
                .Select(item => new KitchenTicketLine(
                    item.MenuItem?.Name ?? "Είδος",
                    item.Quantity,
                    item.Quantity * ((item.MenuItem?.Price ?? 0m) + item.ExtrasPrice),
                    item.Notes,
                    item.MenuItem?.VatPercentOverride ?? item.MenuItem?.MenuCategory?.VatPercent ?? 24m,
                    OrderPriority.Normalize(item.MenuItem?.MenuCategory?.OrderPriority)))
                .OrderBy(item => OrderPriority.Rank(item.Priority))
                .ToList();

            try
            {
                if (await printer.PrintKitchenTicketAsync(station, tableName, waiterName, ticketItems,
                        companySettings, dailyOrderNumber, orderComment))
                {
                    successfulStations.Add(station.Name);
                }
                else
                {
                    failedStations.Add(station.Name);
                    foreach (var route in group) itemsWithFailedPrinter.Add(route.Item.Id);
                }
            }
            catch
            {
                failedStations.Add(station.Name);
                foreach (var route in group) itemsWithFailedPrinter.Add(route.Item.Id);
            }
        }

        foreach (var item in withPrinter)
            item.KitchenPrintUnconfirmed = itemsWithFailedPrinter.Contains(item.Id);
        await db.SaveChangesAsync();

        var noPrinterCategories = withoutPrinter
            .Select(item => item.MenuItem?.MenuCategory?.Name ?? "χωρίς κατηγορία")
            .Distinct()
            .ToList();

        return new KitchenOrderDispatchResult(
            true,
            batch.Id,
            dailyOrderNumber,
            pending,
            successfulStations,
            failedStations,
            noPrinterCategories);
    }

    private static List<PrinterStation> GetCategoryPrinters(MenuCategory? category)
    {
        if (category is null) return new List<PrinterStation>();
        var stations = category.PrinterStations?.ToList() ?? new List<PrinterStation>();
        if (category.PrinterStation is not null && stations.All(station => station.Id != category.PrinterStation.Id))
            stations.Add(category.PrinterStation);
        return stations.Where(station => station.PrintOrders).DistinctBy(station => station.Id).ToList();
    }
}
