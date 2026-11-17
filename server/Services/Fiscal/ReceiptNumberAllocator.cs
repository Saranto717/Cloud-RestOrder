using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services.Fiscal;

public static class ReceiptNumberAllocator
{
    // Called under a SQLite write transaction. Never infer that an unknown number is free
    // from a duplicate-code response or from a missing order-form correlation.
    public static async Task<long> NextAsync(AppDbContext db, string series)
    {
        var counters = await db.InvoiceSeries.Where(s=>s.SeriesName==series)
            .Select(s=>(long?)s.NextNumber).MaxAsync() ?? 1;
        var last = new long[] {
            await db.InvoiceSeries.Where(s=>s.SeriesName==series).Select(s=>(long?)s.PortalLastNumber).MaxAsync() ?? 0,
            await db.Orders.Where(o=>o.InvoiceSeriesName==series).MaxAsync(o=>o.InvoiceNumber) ?? 0,
            await db.OrderPayments.Where(p=>p.SeriesName==series).MaxAsync(p=>p.InvoiceNumber) ?? 0,
            await db.FiscalOrderDocuments.Where(f=>f.SeriesName==series).MaxAsync(f=>(long?)f.Number) ?? 0,
            await db.FiscalOrderCancellations.Where(f=>f.SeriesName==series).MaxAsync(f=>f.Number) ?? 0,
            await db.ReceiptNumberHolds.Where(h=>h.SeriesName==series).MaxAsync(h=>(long?)h.Number) ?? 0
        }.Max();
        return Math.Max(counters,checked(last+1));
    }

    public static async Task<long> ReserveAsync(AppDbContext db, string series, string externalId, int orderId, long? pinned = null)
    {
        await using var tx = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync() : null;
        var held = await db.ReceiptNumberHolds.AsNoTracking().SingleOrDefaultAsync(h=>h.ExternalId==externalId);
        if (held != null)
        {
            if (held.SeriesName!=series || held.OrderId!=orderId || pinned.HasValue && held.Number!=pinned)
                throw new InvalidOperationException("Η υπάρχουσα δέσμευση δεν ταιριάζει με την πληρωμή.");
            return held.Number;
        }
        var number = pinned ?? await NextAsync(db,series);
        if(number < 1 || number >= int.MaxValue) throw new InvalidOperationException("Μη διαθέσιμος αριθμός παραστατικού.");
        if(pinned.HasValue && (
            await db.ReceiptNumberHolds.AnyAsync(h=>h.SeriesName==series && h.Number==number) ||
            await db.Orders.AnyAsync(o=>o.InvoiceSeriesName==series && o.InvoiceNumber==number && (o.Id!=orderId || o.ParochosExternalId!=externalId)) ||
            await db.OrderPayments.AnyAsync(p=>p.SeriesName==series && p.InvoiceNumber==number) ||
            await db.FiscalOrderDocuments.AnyAsync(f=>f.SeriesName==series && f.Number==number) ||
            await db.InvoiceSeries.AnyAsync(s=>s.SeriesName==series && s.PortalLastNumber>=number)))
            throw new InvalidOperationException("Ο ήδη υπογεγραμμένος αριθμός δεν είναι διαθέσιμος. Χρειάζεται ταυτοποίηση, όχι αλλαγή αριθμού.");
        db.ReceiptNumberHolds.Add(new() {SeriesName=series, Number=number, OrderId=orderId, ExternalId=externalId});
        int next=checked((int)number+1);
        await db.InvoiceSeries.Where(s=>s.SeriesName==series && s.NextNumber<next)
            .ExecuteUpdateAsync(u=>u.SetProperty(s=>s.NextNumber,next));
        await db.SaveChangesAsync();
        if(tx!=null) await tx.CommitAsync();
        return number;
    }

    public static async Task<int> ConfirmPortalLastAsync(AppDbContext db,int seriesId,int last)
    {
        if(last<1 || last>=int.MaxValue) throw new InvalidOperationException("Δώσε έγκυρο τελευταίο αριθμό από το portal.");
        await using var tx=await db.Database.BeginTransactionAsync();
        var s=await db.InvoiceSeries.SingleAsync(x=>x.Id==seriesId);
        if(!s.IsActive || s.Kind is not (FiscalDocumentKind.Receipt or FiscalDocumentKind.Invoice))
            throw new InvalidOperationException("Επίλεξε ενεργή σειρά αποδείξεων ή τιμολογίων.");
        s.PortalLastNumber=Math.Max(s.PortalLastNumber,last);
        s.NextNumber=checked((int)Math.Max(last+1L,await NextAsync(db,s.SeriesName)));
        await db.SaveChangesAsync();
        await tx.CommitAsync(); return s.NextNumber;
    }
}
