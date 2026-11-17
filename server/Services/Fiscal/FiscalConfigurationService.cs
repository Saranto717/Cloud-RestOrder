using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services.Fiscal;

/// <summary>Loads fiscal configuration and owns invoice-series selection/numbering.</summary>
public sealed class FiscalConfigurationService
{
    public Task<BankPosTerminalSettings?> GetDefaultWebEcrPosAsync(AppDbContext db) =>
        db.BankPosTerminalSettings
            .Where(x => x.IsEnabled &&
                ((x.Provider.Contains("ePay") || x.Provider.Contains("Euronet") || x.Protocol.Contains("Common WebECR")) ||
                 (x.Provider == "EDPS" && x.Protocol.Contains("WebECR API v6"))))
            .OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync();

    public async Task<CompanySettings?> GetSettingsAsync(AppDbContext db) =>
        await db.CompanySettings.FirstOrDefaultAsync();

    public bool ValidateSettings(CompanySettings? settings, out string? error)
    {
        if (settings is null || string.IsNullOrWhiteSpace(settings.ParochosSubscriptionKey))
        {
            error = "Δεν έχουν ρυθμιστεί τα στοιχεία σύνδεσης με τον Parochos.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(settings.VatNumber))
        {
            error = "Δεν έχει οριστεί ΑΦΜ επιχείρησης.";
            return false;
        }
        error = null;
        return true;
    }

    public async Task<InvoiceSeries> GetOrCreateSeriesAsync(AppDbContext db, FiscalDocumentKind kind, string? preferredSeriesName = null)
    {
        // The active series selected in Admin > Fiscal Settings is authoritative.
        // The old implementation returned the first row for this kind, which could
        // be an inactive legacy series (e.g. "ΠΑΡ") even when another series was active.
        var series = await db.InvoiceSeries
            .Where(s => s.Kind == kind && s.IsActive)
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync();

        if (series is not null) return series;

        // If no active series exists, prefer the configured series name.
        if (!string.IsNullOrWhiteSpace(preferredSeriesName))
        {
            series = await db.InvoiceSeries.FirstOrDefaultAsync(
                s => s.Kind == kind && s.SeriesName == preferredSeriesName.Trim());
            if (series is not null)
            {
                series.IsActive = true;
                await db.SaveChangesAsync();
                return series;
            }
        }

        // Fall back to an existing series of this kind, activating it explicitly.
        series = await db.InvoiceSeries
            .Where(s => s.Kind == kind)
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync();

        if (series is not null)
        {
            series.IsActive = true;
            await db.SaveChangesAsync();
            return series;
        }

        var seriesName = !string.IsNullOrWhiteSpace(preferredSeriesName)
            ? preferredSeriesName.Trim()
            : kind switch
            {
                FiscalDocumentKind.Invoice => "ΤΙΜ",
                FiscalDocumentKind.OrderForm => "ΠΑΡ",
                FiscalDocumentKind.CreditReceipt => "ΠΣΛ",
                FiscalDocumentKind.CreditInvoice => "ΠΤΙΜ",
                FiscalDocumentKind.SelfSupply => "ΑΥΤ",
                FiscalDocumentKind.SelfUse => "ΙΔΧ",
                _ => "Α"
            };
        series = new InvoiceSeries { SeriesName = seriesName, Kind = kind, NextNumber = 1, IsActive = true };
        db.InvoiceSeries.Add(series);
        await db.SaveChangesAsync();
        return series;
    }

    public int ParsePaymentCode(string? value, int fallback) =>
        int.TryParse(value, out var code) ? code : fallback;

    public static FiscalDocumentKind SeriesKind(Order order, FiscalDocumentKind kind) =>
        kind == FiscalDocumentKind.Receipt ? order.StaffConsumptionInvoiceType switch
        { "6.1" => FiscalDocumentKind.SelfSupply, "6.2" => FiscalDocumentKind.SelfUse, _ => kind } : kind;

    public static string InvoiceType(Order order, FiscalDocumentKind kind, CompanySettings settings) =>
        kind == FiscalDocumentKind.Receipt && order.StaffConsumptionInvoiceType is "6.1" or "6.2"
            ? order.StaffConsumptionInvoiceType : kind == FiscalDocumentKind.Invoice ? settings.InvoiceInvoiceType : settings.ReceiptInvoiceType;

    public static async Task<string?> ValidateStaffConsumptionAsync(AppDbContext db, Order order, FiscalDocumentKind kind)
    {
        if (order.StaffConsumptionInvoiceType is not ("6.1" or "6.2")) return null;
        if (kind != FiscalDocumentKind.Receipt) return "Η ιδιοκατανάλωση απαιτεί το δικό της παραστατικό.";
        var items = await db.OrderItems.AsNoTracking().Include(i => i.MenuItem).ThenInclude(m => m!.MenuCategory)
            .Where(i => i.OrderId == order.Id && !i.IsCancelled && i.Quantity > 0).ToListAsync();
        if (items.Count == 0) return "Δεν βρέθηκαν είδη ιδιοκατανάλωσης.";
        foreach (var item in items)
        {
            if (item.MenuItem?.MenuCategory is null) return "Λείπει η κατηγορία του είδους.";
            if (item.MenuItem.MenuCategory.IncomeClassificationCategory is not ("category1_1" or "category1_2" or "category1_3")) return "Λείπει ο χαρακτηρισμός εμπορεύματος, προϊόντος ή υπηρεσίας.";
            var isService = item.MenuItem.MenuCategory.IncomeClassificationCategory == "category1_3";
            if ((order.StaffConsumptionInvoiceType == "6.2") != isService) return "Το παραστατικό περιέχει προϊόντα και υπηρεσίες διαφορετικού τύπου.";
            if (FiscalPayloadBuilder.LineGross(item) != Math.Round(item.Quantity * item.MenuItem.StaffConsumptionPrice, 2))
                return $"Η τιμή ιδιοκατανάλωσης του είδους «{item.MenuItem.Name}» άλλαξε. Εφάρμοσέ την ξανά πριν από την πληρωμή.";
        }
        return null;
    }

    public async Task<string?> ResolvePointOfServiceAsync(AppDbContext db, Order order)
    {
        if (!string.IsNullOrWhiteSpace(order.RestaurantTable?.Name))
            return order.RestaurantTable!.Name.Trim();

        if (order.RestaurantTableId > 0)
        {
            return await db.Tables
                .Where(t => t.Id == order.RestaurantTableId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync();
        }

        return null;
    }


}

