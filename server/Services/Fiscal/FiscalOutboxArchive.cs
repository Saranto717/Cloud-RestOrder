using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services.Fiscal;

/// <summary>Removes problem entries from the active outbox while preserving fiscal records.</summary>
public static class FiscalOutboxArchive
{
    public static async Task<bool> DeleteArchivedAsync(AppDbContext db, int orderId, int? documentId)
    {
        if (documentId is int id)
        {
            var document = await db.FiscalOrderDocuments
                .Include(d => d.Items)
                .FirstOrDefaultAsync(d => d.Id == id && d.OrderId == orderId && d.ParochosOutboxArchived &&
                    d.Mark == null && d.ParochosInvoicePayloadJson != null);
            if (document is null) return false;
            db.FiscalOrderDocumentItems.RemoveRange(document.Items);
            db.FiscalOrderDocuments.Remove(document);
            await db.SaveChangesAsync();
            return true;
        }

        // Keep the order/payment history, but permanently discard the unsent receipt payload.
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.ParochosOutboxArchived &&
            o.ParochosMark == null && o.ParochosInvoicePayloadJson != null);
        if (order is null) return false;
        order.ParochosInvoicePayloadJson = null;
        order.ParochosOutboxArchived = false;
        order.ParochosTransmissionState = "Deleted";
        order.ParochosTransmissionFailure = null;
        order.ParochosTransmissionAttemptedAtUtc = null;
        order.ParochosOfflineReceiptPrintedAtUtc = null;
        order.FiscalErrorMessage = null;
        await db.SaveChangesAsync();
        return true;
    }

    public static async Task<int> SetArchivedAsync(AppDbContext db, int orderId, int? documentId, bool archived)
    {
        if (documentId is int id)
            return await db.FiscalOrderDocuments.Where(d => d.Id == id && d.OrderId == orderId &&
                (d.ParochosTransmissionState == "NeedsReview" || d.ParochosTransmissionState == "AwaitingOriginal") && d.Mark == null &&
                d.ParochosInvoicePayloadJson != null && d.ParochosOutboxArchived != archived)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ParochosOutboxArchived, archived));
        return await db.Orders.Where(o => o.Id == orderId && o.ParochosTransmissionState == "NeedsReview" &&
            o.ParochosMark == null && o.ParochosInvoicePayloadJson != null && o.ParochosOutboxArchived != archived)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ParochosOutboxArchived, archived));
    }

    public static async Task<List<PendingFiscalTransmission>> ListArchivedAsync(AppDbContext db)
    {
        var receipts = await db.Orders.AsNoTracking().Include(o => o.RestaurantTable)
            .Where(o => o.ParochosOutboxArchived && o.ParochosMark == null && o.ParochosInvoicePayloadJson != null)
            .ToListAsync();
        var result = receipts.Select(o => new PendingFiscalTransmission(o.Id,
            $"{o.InvoiceSeriesName ?? "Παραστατικό"} {o.InvoiceNumber}", o.RestaurantTable?.Name ?? "Take Away",
            o.ParochosTransmissionAttemptedAtUtc, FiscalStoredPayload.Total(o.ParochosInvoicePayloadJson) ?? 0m,
            o.ParochosTransmissionState ?? "NeedsReview", o.ParochosTransmissionFailure,
            o.ParochosOfflineReceiptPrintedAtUtc, o.FiscalErrorMessage)).ToList();
        var forms = await db.FiscalOrderDocuments.AsNoTracking().Include(d => d.Order).ThenInclude(o => o!.RestaurantTable)
            .Where(d => d.ParochosOutboxArchived && d.Mark == null && d.ParochosInvoicePayloadJson != null).ToListAsync();
        result.AddRange(forms.Select(d => new PendingFiscalTransmission(d.OrderId,
            $"{(d.IsCancellation ? "Ακυρωτικό δελτίο" : "Δελτίο παραγγελίας")} {d.SeriesName} {d.Number}",
            d.Order?.RestaurantTable?.Name ?? "Take Away", d.ParochosTransmissionAttemptedAtUtc,
            FiscalStoredPayload.Total(d.ParochosInvoicePayloadJson) ?? 0m, d.ParochosTransmissionState ?? "NeedsReview",
            d.ParochosTransmissionFailure, d.PrintConfirmedAtUtc, d.FiscalErrorMessage, d.Id)));
        return result.OrderBy(r => r.AttemptedAtUtc).ToList();
    }
}
