using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;
using System.Text.Json;

namespace RestaurantOrders.Services.Fiscal;

// Print only: never creates a fiscal document and never contacts a provider.
internal static class StoredOrderFormPrinter
{
    public static async Task<OfflineReceiptPrintResult> PrintAsync(AppDbContext db, PrinterService printer, int id)
    {
        var d = await db.FiscalOrderDocuments
            .Include(f=>f.Order).ThenInclude(o=>o!.WaiterUser)
            .Include(f=>f.Items).ThenInclude(l=>l.OrderItem).ThenInclude(i=>i!.MenuItem).ThenInclude(m=>m!.MenuCategory).ThenInclude(c=>c!.PrinterStations)
            .Include(f=>f.Items).ThenInclude(l=>l.OrderItem).ThenInclude(i=>i!.MenuItem).ThenInclude(m=>m!.MenuCategory).ThenInclude(c=>c!.PrinterStation)
            .SingleOrDefaultAsync(f=>f.Id==id);
        if (d == null || d.ParochosInvoicePayloadJson == null || (d.Mark is not > 0 && d.ParochosTransmissionFailure is not (1 or 2)))
            return new(false,false,"Δεν υπάρχει εκδομένο δελτίο προς εκτύπωση.");
        if(d.PrintConfirmedAtUtc.HasValue) return new(true,true,null);
        using var doc = JsonDocument.Parse(d.ParochosInvoicePayloadJson);
        var invoice = doc.RootElement.GetProperty("invoice");
        var lines = invoice.GetProperty("invoiceDetails").EnumerateArray().Select(line=>(
            ItemName: line.GetProperty("entityName").GetString() ?? "Είδος",
            Quantity: (int)line.GetProperty("quantity").GetDecimal(),
            LineTotal: line.GetProperty("totalValue").GetDecimal(), Notes: (string?)null,
            VatPercent: line.TryGetProperty("vatPercent",out var vat)? vat.GetDecimal():0m)).ToArray();
        var stations = new Dictionary<int,PrinterStation>();
        foreach(var cat in d.Items.Select(i=>i.OrderItem?.MenuItem?.MenuCategory).OfType<MenuCategory>())
        {
            foreach(var station in cat.PrinterStations.Append(cat.PrinterStation).OfType<PrinterStation>().Where(p=>p.IsActive && p.PrintFiscalDocuments))
                stations[station.Id]=station;
        }
        if(stations.Count==0) {
            var fallback=await db.PrinterStations.Where(p=>p.IsActive && p.PrintFiscalDocuments).Take(2).ToListAsync();
            if(fallback.Count==1) stations.Add(fallback[0].Id,fallback[0]);
        }
        if(stations.Count==0) return new(false,false,"Δεν έχει οριστεί εκτυπωτής παραστατικών.");
        var done=(d.ConfirmedPrinterIds??"").Split(',',StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var failures=new List<string>();
        var settings=await db.CompanySettings.FirstOrDefaultAsync();
        var title=d.IsCancellation?"ΑΚΥΡΩΤΙΚΟ ΔΕΛΤΙΟ ΠΑΡΑΓΓΕΛΙΑΣ":"ΔΕΛΤΙΟ ΠΑΡΑΓΓΕΛΙΑΣ";
        if(d.Mark is not > 0) title+=$" — TRANSMISSION FAILURE_{d.ParochosTransmissionFailure}";
        foreach(var p in stations.Values.Where(p=>!done.Contains(p.Id.ToString()))) {
            try {
                if(await printer.PrintOrderFormAsync(p,invoice.GetProperty("invoiceHeader").GetProperty("PointOfService").GetString()??"-",
                    d.Order?.WaiterUser?.FullName??"-",lines,d.Mark,d.QrCode,settings,title,d.SeriesName,d.Number)) {
                    done.Add(p.Id.ToString()); d.ConfirmedPrinterIds=string.Join(",",done);
                    await db.SaveChangesAsync(); continue;
                }
            } catch(Exception ex){ Console.Error.WriteLine($"Order form {id}, printer {p.Id}: {ex}"); }
            failures.Add(p.Name);
        }
        if(failures.Count>0) return new(false,false,"Δεν επιβεβαιώθηκε η εκτύπωση σε: "+string.Join(", ",failures)+".");
        d.PrintConfirmedAtUtc=DateTime.UtcNow;await db.SaveChangesAsync();return new(true,false,null);
    }
}
