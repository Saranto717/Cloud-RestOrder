using RestaurantOrders.Models;

namespace RestaurantOrders.Services.Fiscal;

/// <summary>Maps order data to myDATA invoice payloads and calculates document totals.</summary>
public static class FiscalPayloadBuilder
{
    public static Dictionary<string, object?> BuildInvoice(
        CompanySettings settings,
        Order order,
        FiscalDocumentKind kind,
        string seriesName,
        long invoiceNumber,
        (decimal Net, decimal Vat, decimal Gross) totals,
        string invoiceType,
        string? pointOfService,
        bool closePointOfService,
        List<FiscalOrderDocument> orderForms,
        bool openPointOfService = false,
        int? transmissionFailure = null)
    {
        var payment = new Dictionary<string, object?>
        {
            ["type"] = PaymentMethodInfo.GetMyDataCode(order.PaymentMethod),
            ["amount"] = totals.Gross
        };

        if (order.PaymentMethod is PaymentMethod.Card or PaymentMethod.Iris)
        {
            payment["signature"] = order.ParochosPaymentSignature;
            payment["transactionId"] = order.CardTransactionId;
            payment["terminalId"] = order.CardTerminalId;
            payment["tipAmount"] = 0m;
        }

        var invoiceHeader = new Dictionary<string, object?>
        {
            ["series"] = seriesName,
            ["aa"] = invoiceNumber,
            ["issueDate"] = IssueTimestampGreekLocal(),
            ["currency"] = "EUR",
            ["invoiceType"] = invoiceType,
            ["invoiceTypeUbl"] = "380",
            ["selfPricing"] = false,
            ["fuelInvoice"] = false
        };

        // These fields are only valid for the Parochos 8.6 order-form flow.
        // Receipts issued directly (e.g. Take Away) must omit them completely.
        if (!string.IsNullOrWhiteSpace(pointOfService))
        {
            invoiceHeader["OpenPointOfService"] = openPointOfService ? 1 : 0;
            invoiceHeader["ClosePointOfService"] = closePointOfService ? 1 : 0;
            invoiceHeader["PointOfService"] = pointOfService;
        }

        var invoice = new Dictionary<string, object?>
        {
            ["issuer"] = BuildIssuer(settings),
            ["counterpart"] = order.StaffConsumptionInvoiceType is "6.1" or "6.2" ? new Dictionary<string, object?> { ["vatNumber"] = settings.VatNumber, ["branch"] = settings.Branch, ["name"] = settings.CompanyName, ["country"] = settings.Country, ["city"] = settings.City, ["street"] = settings.Street, ["streetNumber"] = settings.StreetNumber, ["postalCode"] = settings.PostalCode } : kind == FiscalDocumentKind.Invoice ? BuildInvoiceCounterpart(order) : BuildRetailCounterpart(),
            ["invoiceHeader"] = invoiceHeader,
            ["invoiceDetails"] = BuildLines(
                order.Items.Where(i => !i.IsCancelled),
                order.StaffConsumptionInvoiceType is "6.1" or "6.2" ? "E3_595" : kind == FiscalDocumentKind.Invoice ? "E3_561_001" : "E3_561_003",
                order.StaffConsumptionInvoiceType is "6.1" or "6.2"),
            ["paymentMethods"] = new List<object?> { payment },
            ["invoiceSummary"] = new Dictionary<string, object?>
            {
                ["totalNetValue"] = totals.Net,
                ["totalVatAmount"] = totals.Vat,
                ["totalValue"] = totals.Gross
            }
        };

        // myDATA field on the invoice itself, not an option of the Parochos
        // transport envelope. It must be kept in the durable queued payload.
        if (transmissionFailure is 1 or 2)
            invoice["transmissionFailure"] = transmissionFailure;

        var correlatedInvoices = orderForms
            .Where(f => f.Mark.HasValue)
            .Select(f => (object?)new Dictionary<string, object?> { ["mark"] = f.Mark!.Value })
            .ToList();
        if (correlatedInvoices.Count > 0)
            invoice["correlatedInvoices"] = correlatedInvoices;

        // Order Forms (8.6) are also connected to the final Accounting Document
        // through Parochos /api/OrderForms/ConnectDocuments after the final
        // document receives its MARK.
        return invoice;
    }

    public static Dictionary<string, object?> BuildIssuer(CompanySettings settings) => new()
    {
        ["vatNumber"] = settings.VatNumber,
        ["branch"] = settings.Branch,
        ["country"] = settings.Country,
        ["city"] = settings.City,
        ["street"] = settings.Street,
        ["streetNumber"] = settings.StreetNumber,
        ["postalCode"] = settings.PostalCode,
        ["contact"] = new Dictionary<string, object?>
        {
            ["name"] = settings.CompanyName,
            ["telephone"] = settings.ContactPhone,
            ["electronicMail"] = settings.ContactEmail
        }
    };

    public static Dictionary<string, object?> BuildRetailCounterpart() => new()
    {
        ["vatNumber"] = "000000000",
        ["branch"] = 0,
        ["name"] = "Ιδιώτης πελάτης",
        ["country"] = "GR"
    };

    public static Dictionary<string, object?> BuildInvoiceCounterpart(Order order) => new()
    {
        ["vatNumber"] = order.CounterpartVatNumber,
        ["branch"] = 0,
        ["name"] = order.CounterpartName,
        ["country"] = "GR",
        ["city"] = order.CounterpartCity,
        ["street"] = order.CounterpartStreet,
        ["streetNumber"] = order.CounterpartStreetNumber,
        ["postalCode"] = order.CounterpartPostalCode
    };

    public static List<object?> BuildLines(IEnumerable<OrderItem> items, string? classificationType = null, bool selfConsumption = false)
    {
        return items.Where(i => !i.IsCancelled).Select((oi, idx) =>
        {
            var gross = LineGross(oi);
            var vatPercent = oi.MenuItem?.VatPercentOverride ?? oi.MenuItem?.MenuCategory?.VatPercent ?? 24m;
            // myDATA vatCategory is the AADE code, not the VAT percentage. Resolve it
            // from the effective percentage so legacy rows with the old default code
            // (1) cannot send 24% for a 13% or 6% line.
            var configuredVatCode = oi.MenuItem?.VatCategoryCodeOverride ?? oi.MenuItem?.MenuCategory?.VatCategoryCode;
            var vatCategoryCode = ResolveVatCategoryCode(vatPercent, configuredVatCode);
            var net = LineNet(oi);
            var vat = Math.Round(gross - net, 2);
            var measurementUnit = oi.MenuItem?.MeasurementUnitCode is >= 1 and <= 3
                ? oi.MenuItem.MeasurementUnitCode
                : 1;
            var measurementUnitUbl = measurementUnit switch { 2 => "KGM", 3 => "LTR", _ => "H87" };

            var line = new Dictionary<string, object?>
            {
                ["lineNumber"] = idx,
                ["invoiceDetailType"] = 0,
                ["quantity"] = oi.Quantity,
                ["entityName"] = oi.MenuItem?.Name ?? "Είδος",
                ["netValue"] = net,
                ["totalValue"] = gross,
                ["vatCategory"] = vatCategoryCode,
                ["vatCategoryUbl"] = vatCategoryCode == 7 ? "Z" : vatCategoryCode == 8 ? "E" : "S",
                ["vatAmount"] = vat,
                ["vatPercent"] = vatPercent,
                ["measurementUnit"] = measurementUnit,
                ["measurementUnitUbl"] = measurementUnitUbl
            };

            if (!string.IsNullOrWhiteSpace(classificationType))
            {
                var incomeCategory = selfConsumption ? "category1_6" : oi.MenuItem?.MenuCategory?.IncomeClassificationCategory;
                if (incomeCategory is not ("category1_1" or "category1_2" or "category1_3" or "category1_6"))
                    throw new InvalidOperationException($"Δεν έχει οριστεί έγκυρος χαρακτηρισμός εσόδων για το είδος '{oi.MenuItem?.Name ?? "Είδος"}'. Επίλεξε Εμπορεύματα, Προϊόντα ή Υπηρεσίες στην κατηγορία του.");
                line["classificationCategory"] = incomeCategory;
                line["classificationType"] = classificationType;
            }

            return (object?)line;
        }).ToList();
    }

    public static int ResolveVatCategoryCode(decimal vatPercent, string? configuredCode)
    {
        // If a special 4% AADE category (code 10) was explicitly configured, keep it.
        if (vatPercent == 4m && int.TryParse(configuredCode, out var explicitCode) && explicitCode == 10)
            return explicitCode;

        var mapped = vatPercent switch
        {
            24m => 1,
            13m => 2,
            6m => 3,
            17m => 4,
            9m => 5,
            4m => 6,
            3m => 9,
            0m => 7,
            _ => 0
        };

        if (mapped != 0) return mapped;

        if (int.TryParse(configuredCode, out var configured))
        {
            var configuredPercent = configured switch
            {
                1 => 24m,
                2 => 13m,
                3 => 6m,
                4 => 17m,
                5 => 9m,
                6 or 10 => 4m,
                7 or 8 => 0m,
                9 => 3m,
                _ => -1m
            };
            if (configuredPercent == vatPercent) return configured;
        }

        throw new InvalidOperationException($"Ο συντελεστής ΦΠΑ {vatPercent}% δεν έχει έγκυρη αντιστοίχιση myDATA. Επίλεξε έγκυρο συντελεστή στην κατηγορία ή στο είδος.");
    }

    public static (decimal Net, decimal Vat, decimal Gross) CalculateTotals(Order order) => CalculateTotals(order.Items.Where(i => !i.IsCancelled));

    public static (decimal Net, decimal Vat, decimal Gross) CalculateTotals(IEnumerable<OrderItem> items)
    {
        items = items.Where(i => !i.IsCancelled);
        var gross = Math.Round(items.Sum(LineGross), 2);
        var net = Math.Round(items.Sum(LineNet), 2);
        var vat = Math.Round(gross - net, 2);
        return (net, vat, gross);
    }


    public static decimal LineGross(OrderItem item) =>
        Math.Round(item.Quantity * ((item.MenuItem?.Price ?? 0m) + item.ExtrasPrice) * (1m - Math.Clamp(item.DiscountPercent, 0m, 100m) / 100m), 2);

    private static decimal LineNet(OrderItem item)
    {
        var vatPercent = item.MenuItem?.VatPercentOverride ?? item.MenuItem?.MenuCategory?.VatPercent ?? 24m;
        return Math.Round(LineGross(item) / (1 + vatPercent / 100m), 2);
    }

    /// <summary>
    /// Parochos expects the invoice issue timestamp as local Greek wall time.
    /// Its API examples omit a timezone suffix for issueDate (unlike UTC response
    /// timestamps). Sending a Z suffix makes the provider display Greece's time
    /// two hours earlier during winter time.
    /// </summary>
    public static string IssueTimestampGreekLocal()
    {
        var timeZoneId = OperatingSystem.IsWindows() ? "GTB Standard Time" : "Europe/Athens";
        var athensTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var greekNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, athensTimeZone);
        return greekNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
    }
}

