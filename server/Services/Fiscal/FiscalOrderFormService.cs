using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services.Fiscal;

public sealed record FiscalOrderFormDispatchResult(FiscalIssueResult Issue, IReadOnlyList<string> PrintErrors)
{
    public bool Success => Issue.Success;
}

/// <summary>
/// Runs the provider's fiscal Order Form workflow and prints its accepted result.
/// Kept separate from kitchen order submission so provider outages cannot undo
/// or cause duplicate kitchen dispatches.
/// </summary>
public sealed class FiscalOrderFormService(IFiscalDocumentService fiscalDocuments, PrinterService printer)
{
    // Serialize consumption and retries in the local POS server. Reload inside the gate:
    // an old table/cashier screen must never start a second send or print.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<OfflineReceiptPrintResult> PrintExistingAsync(AppDbContext db, int id)
    {
        await Gate.WaitAsync();
        try { return await StoredOrderFormPrinter.PrintAsync(db, printer, id); }
        finally { Gate.Release(); }
    }

    public async Task<FiscalIssueResult> CheckAsync(AppDbContext db, PendingFiscalTransmission row)
    {
        if (row.BatchId is int batchId)
            return (await ConsumeBatchAsync(db, batchId)).Issue;
        await Gate.WaitAsync();
        try
        {
            db.ChangeTracker.Clear();
            return row.FiscalOrderDocumentId is int formId
                ? await fiscalDocuments.RetryPendingOrderFormTransmissionAsync(db, formId)
                : await fiscalDocuments.RetryPendingTransmissionAsync(db, row.OrderId);
        }
        finally { Gate.Release(); }
    }

    public async Task<FiscalOrderFormDispatchResult> ConsumeBatchAsync(AppDbContext db, int batchId)
    {
        await Gate.WaitAsync();
        try
        {
            db.ChangeTracker.Clear();
            var batch = await db.OrderPrintBatches
                .Include(b => b.Order).ThenInclude(o => o!.RestaurantTable)
                .Include(b => b.Order).ThenInclude(o => o!.WaiterUser)
                .Include(b => b.Items).ThenInclude(i => i.MenuItem).ThenInclude(m => m!.MenuCategory).ThenInclude(c => c!.PrinterStations)
                .Include(b => b.Items).ThenInclude(i => i.MenuItem).ThenInclude(m => m!.MenuCategory).ThenInclude(c => c!.PrinterStation)
                .SingleAsync(b => b.Id == batchId);
            if (!batch.DispatchNotificationPending)
                return new(new FiscalIssueResult { Success = true, ProviderUsed = false }, Array.Empty<string>());
            var order = batch.Order!;
            var settings = await db.CompanySettings.FirstOrDefaultAsync();
            var routes = batch.Items.ToDictionary(i => i.Id, i => i.MenuItem?.MenuCategory);
            var lines = batch.AcceptedLinesJson is null ? batch.Items :
                System.Text.Json.JsonSerializer.Deserialize<AcceptedOrderLine[]>(batch.AcceptedLinesJson)!
                    .Select(i => i.ForDocument(batch, routes.GetValueOrDefault(i.ItemId))).ToList();
            var result = await IssueAndPrintAsync(db, order, lines,
                batch.AcceptedTableName ?? order.RestaurantTable?.Name ?? "-", order.WaiterUser?.FullName ?? "-", settings);
            // Disabled fiscal mode has no document to commit the acknowledgement with.
            if (result.Issue.Success && !result.Issue.ProviderUsed)
            {
                batch.DispatchNotificationPending = false;
                await db.SaveChangesAsync();
            }
            return result;
        }
        finally { Gate.Release(); }
    }

    private async Task<FiscalOrderFormDispatchResult> IssueAndPrintAsync(
        AppDbContext db,
        Order order,
        IReadOnlyCollection<OrderItem> newlyAddedItems,
        string tableName,
        string waiterName,
        CompanySettings? companySettings)
    {
        var issue = await fiscalDocuments.IssueOrderFormAsync(db, order, newlyAddedItems.ToList());
        if (!issue.Success || !issue.ProviderUsed)
            return new FiscalOrderFormDispatchResult(issue, Array.Empty<string>());

        if (issue.OrderFormId is int id)
        {
            var print = await StoredOrderFormPrinter.PrintAsync(db, printer, id);
            return new(issue, print.Success ? Array.Empty<string>() : new[] { print.ErrorMessage ?? "εκτυπωτής δελτίων" });
        }
        var stations = newlyAddedItems
            .SelectMany(item => GetCategoryPrinters(item.MenuItem?.MenuCategory))
            .DistinctBy(station => station.Id)
            .ToList();
        if (stations.Count == 0)
        {
            var eligible = await db.PrinterStations.Where(p => p.IsActive && p.PrintFiscalDocuments).Take(2).ToListAsync();
            if (eligible.Count == 1) stations.Add(eligible[0]);
        }
        var printErrors = new List<string>();
        if (stations.Count == 0) printErrors.Add("δεν ορίστηκε εκτυπωτής παραστατικών");

        var printItems = newlyAddedItems.Select(item => (
            ItemName: item.MenuItem?.Name ?? "Είδος",
            Quantity: item.Quantity,
            LineTotal: item.Quantity * ((item.MenuItem?.Price ?? 0m) + item.ExtrasPrice),
            Notes: item.Notes,
            VatPercent: item.MenuItem?.VatPercentOverride ?? item.MenuItem?.MenuCategory?.VatPercent ?? 24m
        )).ToList();
        var documentTitle = issue.OfflineIssued
            ? $"ΔΕΛΤΙΟ ΠΑΡΑΓΓΕΛΙΑΣ — TRANSMISSION FAILURE_{issue.TransmissionFailure ?? 1}"
            : "ΔΕΛΤΙΟ ΠΑΡΑΓΓΕΛΙΑΣ";

        foreach (var station in stations)
        {
            try
            {
                var printed = await printer.PrintOrderFormAsync(
                    station,
                    tableName,
                    waiterName,
                    printItems,
                    issue.OfflineIssued ? null : issue.Mark,
                    issue.OfflineIssued ? null : issue.QrCode,
                    companySettings,
                    documentTitle,
                    issue.SeriesName,
                    issue.InvoiceNumber);

                if (!printed)
                    printErrors.Add(station.Name);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fiscal Order Form printer failed for {station.Name}: {ex}");
                printErrors.Add(station.Name);
            }
        }

        return new FiscalOrderFormDispatchResult(issue, printErrors);
    }

    private static List<PrinterStation> GetCategoryPrinters(MenuCategory? category)
    {
        if (category is null) return new List<PrinterStation>();

        var stations = category.PrinterStations?.ToList() ?? new List<PrinterStation>();
        if (category.PrinterStation is not null && stations.All(station => station.Id != category.PrinterStation.Id))
            stations.Add(category.PrinterStation);

        return stations.Where(station => station.IsActive && station.PrintFiscalDocuments).DistinctBy(station => station.Id).ToList();
    }
}
