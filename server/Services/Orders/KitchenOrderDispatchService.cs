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
    IReadOnlyList<string> CategoriesWithoutPrinter,
    bool PendingOnWindows = false);

/// <summary>
/// Owns kitchen/bar dispatch, print-batch numbering and the order items' sent state.
/// It deliberately has no fiscal-provider dependency.
/// </summary>
public sealed class KitchenOrderDispatchService(PrinterService printer, CloudPrintAgentResolver printAgentResolver)
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
        var requiresWindowsUsbPrinter = withPrinter
            .SelectMany(item => GetCategoryPrinters(item.MenuItem?.MenuCategory))
            .Any(station => string.Equals(station.ConnectionType, "Usb", StringComparison.OrdinalIgnoreCase));
        var queueToWindows = await printAgentResolver.ShouldQueueToWindowsAsync(
            db, requiresWindowsUsbPrinter);
        var dailyOrderNumber = queueToWindows ? 0 : await CashierService.ReserveNextOrderNumberAsync(db);
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
            WindowsPrintPending = queueToWindows,
            AcceptedTableName = tableName,
            AcceptedOrderComment = orderComment,
            AcceptedLinesJson = System.Text.Json.JsonSerializer.Serialize(pending.Select(AcceptedOrderLine.From)),
            Items = pending
        };
        db.OrderPrintBatches.Add(batch);
        if (queueToWindows)
        {
            foreach (var item in pending)
            {
                item.IsPrinted = true;
                item.PrintedAt = acceptedAt;
                item.KitchenPrintUnconfirmed = true;
            }
            await db.SaveChangesAsync();
            return new KitchenOrderDispatchResult(true, batch.Id, 0, pending,
                Array.Empty<string>(), Array.Empty<string>(), withoutPrinter
                    .Select(item => item.MenuItem?.MenuCategory?.Name ?? "χωρίς κατηγορία")
                    .Distinct().ToList(), true);
        }

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

    /// <summary>
    /// Consumes one durable cloud-to-Windows kitchen print request. The claim and
    /// local ticket number are saved before talking to a printer, matching the
    /// local POS rule that printer failures are manually retryable, not duplicated
    /// automatically after an ambiguous network timeout.
    /// </summary>
    public async Task<KitchenOrderDispatchResult> PrintAcceptedCloudBatchAsync(AppDbContext db, int batchId)
    {
        if (db.IsCloudHosted)
            throw new InvalidOperationException("Cloud print requests are handled by the paired Windows POS.");

        var batch = await db.OrderPrintBatches
            .Include(item => item.Order).ThenInclude(order => order!.RestaurantTable)
            .Include(item => item.Order).ThenInclude(order => order!.WaiterUser)
            .SingleOrDefaultAsync(item => item.Id == batchId && item.WindowsPrintPending);
        if (batch?.Order is null)
            return new(false, 0, 0, Array.Empty<OrderItem>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        var accepted = string.IsNullOrWhiteSpace(batch.AcceptedLinesJson)
            ? Array.Empty<AcceptedOrderLine>()
            : System.Text.Json.JsonSerializer.Deserialize<AcceptedOrderLine[]>(batch.AcceptedLinesJson) ?? [];
        var acceptedIds = accepted.Select(line => line.ItemId).ToArray();
        var itemsById = await db.OrderItems.Where(item => acceptedIds.Contains(item.Id))
            .Include(item => item.MenuItem).ThenInclude(menu => menu!.MenuCategory).ThenInclude(category => category!.PrinterStations)
            .Include(item => item.MenuItem).ThenInclude(menu => menu!.MenuCategory).ThenInclude(category => category!.PrinterStation)
            .ToDictionaryAsync(item => item.Id);

        // A malformed/old request must not be acknowledged as an empty ticket.
        if (accepted.Length == 0 || accepted.Any(line => !itemsById.ContainsKey(line.ItemId)))
            throw new InvalidDataException($"Cloud kitchen batch {batchId} is missing accepted order lines.");

        var companySettings = await db.CompanySettings.FirstOrDefaultAsync();
        var routed = accepted.Select(line => new
            {
                Line = line,
                Item = itemsById[line.ItemId],
                Stations = GetCategoryPrinters(itemsById[line.ItemId].MenuItem?.MenuCategory)
            })
            .ToList();
        var withoutPrinter = routed.Where(route => route.Stations.Count == 0)
            .Select(route => route.Item.MenuItem?.MenuCategory?.Name ?? "χωρίς κατηγορία")
            .Distinct().ToList();
        var skipKitchenPrint = batch.Order.RestaurantTable?.IsTakeAway == true && companySettings?.TakeAwayReceiptOnly == true;
        var groups = routed.Where(route => !skipKitchenPrint && route.Stations.Count > 0)
            .SelectMany(route => route.Stations.Select(station => new { Station = station, route.Line, route.Item }))
            .GroupBy(route => route.Station).ToList();

        var dailyNumber = batch.DailyNumber > 0
            ? batch.DailyNumber
            : await CashierService.ReserveNextOrderNumberAsync(db);
        batch.DailyNumber = dailyNumber;
        batch.WindowsPrintPending = false;
        foreach (var route in routed) route.Item.KitchenPrintUnconfirmed = !skipKitchenPrint && route.Stations.Count > 0;
        await db.SaveChangesAsync();

        var successful = new List<string>();
        var failed = new List<string>();
        var failedItems = new HashSet<int>();
        foreach (var group in groups)
        {
            var ticketItems = group.Select(route => new KitchenTicketLine(
                    route.Line.Name, route.Line.Quantity,
                    route.Line.Quantity * (route.Line.Price + route.Line.Extras),
                    route.Line.Notes, route.Line.VatPercent,
                    OrderPriority.Normalize(route.Item.MenuItem?.MenuCategory?.OrderPriority)))
                .OrderBy(item => OrderPriority.Rank(item.Priority)).ToList();
            try
            {
                if (await printer.PrintKitchenTicketAsync(group.Key,
                        batch.AcceptedTableName ?? batch.Order.RestaurantTable?.Name ?? "-",
                        batch.Order.WaiterUser?.FullName ?? "-", ticketItems, companySettings, dailyNumber,
                        batch.AcceptedOrderComment ?? string.Empty))
                    successful.Add(group.Key.Name);
                else
                {
                    failed.Add(group.Key.Name);
                    foreach (var route in group) failedItems.Add(route.Item.Id);
                }
            }
            catch
            {
                failed.Add(group.Key.Name);
                foreach (var route in group) failedItems.Add(route.Item.Id);
            }
        }

        foreach (var route in routed) route.Item.KitchenPrintUnconfirmed = failedItems.Contains(route.Item.Id);
        await db.SaveChangesAsync();
        return new(true, batch.Id, dailyNumber, routed.Select(route => route.Item).ToArray(),
            successful, failed, withoutPrinter);
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
