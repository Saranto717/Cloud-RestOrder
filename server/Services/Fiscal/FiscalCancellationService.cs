using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;
using RestaurantOrders.Services;
using RestaurantOrders.Services.Fiscal.Provider;

namespace RestaurantOrders.Services.Fiscal;

/// <summary>Provider-specific fiscal cancellations and close-out operations.</summary>
public sealed class FiscalCancellationService
{
    private readonly IParochosFiscalProvider _parochos;
    private readonly FiscalConfigurationService _configuration;
    private readonly ILogger<FiscalCancellationService> _logger;

    public FiscalCancellationService(IParochosFiscalProvider parochos, FiscalConfigurationService configuration, ILogger<FiscalCancellationService> logger)
    {
        _parochos = parochos;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<FiscalIssueResult> CancelSentOrderItemAsync(
        AppDbContext db,
        Order order,
        int orderItemId,
        string reason = "Ακύρωση είδους από τον σερβιτόρο",
        int? quantityToCancel = null)
    {
        if (await db.HasPendingTableTransferAsync(order.Id))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Υπάρχει εκκρεμής μεταφορά τραπεζιού. Ολοκλήρωσε τον επανέλεγχο πριν από φορολογική ενέργεια ή πληρωμή." };

        var settings = await _configuration.GetSettingsAsync(db);
        if (settings is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν έχουν ρυθμιστεί οι φορολογικές ρυθμίσεις." };

        var item = await db.OrderItems
            .Include(i => i.MenuItem).ThenInclude(i => i!.MenuCategory)
            .FirstOrDefaultAsync(i => i.Id == orderItemId && i.OrderId == order.Id);

        if (item is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Το είδος δεν βρέθηκε." };
        if (item.IsCancelled)
            return new FiscalIssueResult { Success = true };
        var cancelQuantity = quantityToCancel ?? item.Quantity;
        if (cancelQuantity < 1 || cancelQuantity > item.Quantity)
            return new FiscalIssueResult { Success = false, ErrorMessage = $"Η ποσότητα ακύρωσης πρέπει να είναι από 1 έως {item.Quantity}." };
        var isPartialCancellation = cancelQuantity < item.Quantity;

        // Local-only mode is allowed only when fiscal order forms are disabled
        // or the Parochos provider is not in use. IsPrinted is a local printing
        // flag and must NOT decide whether a provider-side fiscal ticket exists.
        // A fiscal order form can already have a MARK even if local printing failed.
        if (!settings.UseParochosProvider || !settings.IssueOrderFormsToAade)
        {
            var cancelledItemId = await ApplyItemCancellationAsync(db, item, cancelQuantity, reason);
            await db.SaveChangesAsync();
            return new FiscalIssueResult { Success = true, CancelledOrderItemId = cancelledItemId };
        }

        var allForms = await db.FiscalOrderDocuments
            .Include(f => f.Items)
            .Where(f => f.OrderId == order.Id && f.Mark != null && !f.IsCancelled)
            .OrderBy(f => f.Id)
            .ToListAsync();

        if (allForms.Count == 0)
        {
            // No fiscal ticket exists in the local fiscal-document ledger.
            // An unprinted item can be cancelled locally; a printed/sent item
            // must fail closed because its provider MARK cannot be identified.
            if (!item.IsPrinted)
            {
                var cancelledItemId = await ApplyItemCancellationAsync(db, item, cancelQuantity, reason);
                await db.SaveChangesAsync();
                return new FiscalIssueResult { Success = true, CancelledOrderItemId = cancelledItemId };
            }

            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Το είδος φαίνεται σταλμένο, αλλά δεν βρέθηκε ενεργό Δελτίο Παραγγελίας με MARK. Δεν έγινε τοπική ή φορολογική ακύρωση. Ελέγξτε τα δελτία στον Parochos πριν επαναλάβετε."
            };
        }

        // Prefer exact item-to-ticket mapping. Older records without item mappings are
        // only safe when there is exactly one active ticket for this order.
        var targetForms = allForms
            .Where(f => !f.IsDiscountReturn && f.Items.Any(fi => fi.OrderItemId == item.Id))
            .ToList();

        if (targetForms.Count == 0 && allForms.Count == 1 && allForms[0].Items.Count == 0)
        {
            var activeCount = await db.OrderItems.CountAsync(i => i.OrderId == order.Id && !i.IsCancelled);
            if (activeCount == 1)
                targetForms.Add(allForms[0]);
        }

        if (targetForms.Count == 0)
        {
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Δεν μπόρεσα να προσδιορίσω με ασφάλεια ποιο Δελτίο Παραγγελίας περιέχει το είδος. Δεν έγινε ακύρωση."
            };
        }

        var pointOfService = await _configuration.ResolvePointOfServiceAsync(db, order);
        if (string.IsNullOrWhiteSpace(pointOfService))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκε το Point of Service του τραπεζιού." };

        // Αν είναι το τελευταίο ενεργό είδος, η σωστή πράξη είναι ολική ακύρωση δελτίων.
        var otherActiveQuantity = await db.OrderItems
            .Where(i => i.OrderId == order.Id && !i.IsCancelled && i.Id != item.Id)
            .SumAsync(i => (int?)i.Quantity) ?? 0;
        if (!isPartialCancellation && otherActiveQuantity == 0)
            return await CancelWholeOrderAsync(db, order, reason);

        // ΑΑΔΕ/Parochos: ακύρωση είδους από ήδη διαβιβασμένο 8.6 = νέο 8.6 στο ίδιο
        // Point of Service με γραμμή recType = 7 (αρνητικό πρόσημο, θετικές αξίες)
        // και συσχέτιση με το MARK του αρχικού δελτίου. Το αρχικό δελτίο μένει ενεργό.
        var negativeLine = isPartialCancellation ? CopyOrderItem(item, cancelQuantity) : item;
        var negative = await IssueNegativeOrderFormAsync(db, order, pointOfService, new List<OrderItem> { negativeLine }, targetForms);
        if (!negative.Success)
            return negative;

        var cancelledId = await ApplyItemCancellationAsync(db, item, cancelQuantity, reason);
        await db.SaveChangesAsync();

        return new FiscalIssueResult
        {
            Success = true, Mark = negative.Mark, QrCode = negative.QrCode,
            SeriesName = negative.SeriesName, InvoiceNumber = negative.InvoiceNumber,
            CancelledOrderItemId = cancelledId
        };
    }

    private static async Task<int?> ApplyItemCancellationAsync(AppDbContext db, OrderItem item, int quantity, string reason)
    {
        var now = DateTime.Now;
        if (quantity >= item.Quantity)
        {
            item.IsCancelled = true;
            item.CancelledAt = now;
            item.CancelReason = reason;
            return item.Id;
        }

        var cancelled = CopyOrderItem(item, quantity);
        cancelled.IsCancelled = true;
        cancelled.CancelledAt = now;
        cancelled.CancelReason = reason;
        item.Quantity -= quantity;
        db.OrderItems.Add(cancelled);
        await db.SaveChangesAsync();
        return cancelled.Id;
    }

    private static OrderItem CopyOrderItem(OrderItem item, int quantity) => new()
    {
        OrderId = item.OrderId, MenuItemId = item.MenuItemId, MenuItem = item.MenuItem,
        OrderPrintBatchId = item.OrderPrintBatchId, Quantity = quantity, Notes = item.Notes,
        ExtrasPrice = item.ExtrasPrice, DiscountPercent = item.DiscountPercent,
        CreatedAt = item.CreatedAt, KitchenPrintUnconfirmed = item.KitchenPrintUnconfirmed,
        IsPrinted = item.IsPrinted, PrintedAt = item.PrintedAt
    };

    /// <summary>
    /// Balances the discount portion of already-issued 8.6 order forms by issuing
    /// another non-cancelled 8.6 with recType=7. This is deliberately separate from
    /// item/order cancellation: a discount return remains correlated and open until
    /// the final receipt closes the Point of Service.
    /// </summary>
    public async Task<FiscalIssueResult> ApplyDiscountWithReturnOrderFormsAsync(
        AppDbContext db,
        Order order,
        IReadOnlyCollection<int> orderItemIds)
    {
        if (orderItemIds.Count == 0) return new FiscalIssueResult { Success = true };
        if (await db.HasPendingTableTransferAsync(order.Id))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Υπάρχει εκκρεμής μεταφορά τραπεζιού." };

        var settings = await _configuration.GetSettingsAsync(db);
        if (settings is null || !settings.UseParochosProvider)
            return new FiscalIssueResult { Success = true, ProviderUsed = false };

        var items = await db.OrderItems
            .Include(i => i.MenuItem).ThenInclude(i => i!.MenuCategory)
            .Where(i => i.OrderId == order.Id && orderItemIds.Contains(i.Id) && !i.IsCancelled)
            .ToListAsync();
        var forms = await db.FiscalOrderDocuments
            .Include(f => f.Items)
            .Where(f => f.OrderId == order.Id && f.Mark != null && !f.IsCancelled)
            .OrderBy(f => f.Id)
            .ToListAsync();
        var sourceForms = forms.Where(f => !f.IsCancellation && !f.IsDiscountReturn).ToList();
        var returnForms = forms.Where(f => f.IsDiscountReturn).ToList();
        if (sourceForms.Count == 0) return new FiscalIssueResult { Success = true };

        var adjustments = new List<(int OriginalItemId, OrderItem Item, int Quantity, decimal Gross, List<FiscalOrderDocument> Sources)>();
        foreach (var item in items)
        {
            var mappings = sourceForms
                .SelectMany(f => f.Items.Where(x => x.OrderItemId == item.Id).Select(x => (Form: f, Mapping: x)))
                .ToList();

            // Legacy 8.6 records may lack item mappings. Only infer a mapping when
            // the association is unambiguous: a single form and a single active item.
            if (mappings.Count == 0 && sourceForms.Count == 1 && sourceForms[0].Items.Count == 0)
            {
                var activeCount = await db.OrderItems.CountAsync(i => i.OrderId == order.Id && !i.IsCancelled);
                if (activeCount == 1)
                    mappings.Add((sourceForms[0], new FiscalOrderDocumentItem
                    {
                        OrderItemId = item.Id,
                        Quantity = item.Quantity,
                        DiscountPercentAtIssue = 0m
                    }));
            }

            if (mappings.Count == 0) continue; // Unsent items need no provider-side return.

            var targetGross = 0m;
            var sourceQuantity = 0;
            foreach (var (form, mapping) in mappings)
            {
                var qty = Math.Max(0, Math.Min(mapping.Quantity, item.Quantity - sourceQuantity));
                if (qty == 0) continue;
                sourceQuantity += qty;
                var unitGross = (item.MenuItem?.Price ?? 0m) + item.ExtrasPrice;
                var originalGross = Math.Round(qty * unitGross * (1m - Math.Clamp(mapping.DiscountPercentAtIssue, 0m, 100m) / 100m), 2);
                var discountedGross = Math.Round(qty * unitGross * (1m - Math.Clamp(item.DiscountPercent, 0m, 100m) / 100m), 2);
                targetGross += Math.Max(0m, originalGross - discountedGross);
            }

            var alreadyReturned = returnForms
                .SelectMany(f => f.Items.Where(x => x.OrderItemId == item.Id))
                .Sum(x => x.DiscountReturnGrossAmount);
            var outstanding = Math.Round(targetGross - alreadyReturned, 2);
            if (outstanding < 0m)
                return new FiscalIssueResult
                {
                    Success = false,
                    ErrorMessage = $"Η έκπτωση στο είδος «{item.MenuItem?.Name ?? "Είδος"}» μειώθηκε κάτω από ποσό που έχει ήδη επιστραφεί στον πάροχο. Δεν έγινε αλλαγή."
                };
            if (outstanding <= 0m) continue;

            var matchingSources = mappings.Select(x => x.Form).DistinctBy(x => x.Id).ToList();
            // For one order item, the provider return line uses the same item name,
            // VAT and unit quantity while its value is precisely the outstanding
            // gross discount. The returned percentage is represented as a line
            // amount so cent rounding matches the issued 8.6 documents.
            var quantity = Math.Max(1, sourceQuantity);
            var synthetic = new OrderItem
            {
                Quantity = quantity,
                MenuItem = item.MenuItem,
                ExtrasPrice = (outstanding / quantity) - (item.MenuItem?.Price ?? 0m),
                DiscountPercent = 0m
            };
            adjustments.Add((item.Id, synthetic, quantity, outstanding, matchingSources));
        }

        if (adjustments.Count == 0) return new FiscalIssueResult { Success = true };
        var pointOfService = await _configuration.ResolvePointOfServiceAsync(db, order);
        if (string.IsNullOrWhiteSpace(pointOfService))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκε το Point of Service του τραπεζιού." };

        var totalGross = Math.Round(adjustments.Sum(a => a.Gross), 2);
        var lines = FiscalPayloadBuilder.BuildLines(adjustments.Select(a => a.Item));
        foreach (var line in lines.OfType<Dictionary<string, object?>>()) line["recType"] = 7;
        var totals = FiscalPayloadBuilder.CalculateTotals(adjustments.Select(a => a.Item));
        var sourceFormsForLink = adjustments.SelectMany(a => a.Sources).DistinctBy(f => f.Id).ToList();
        var keyMaterial = $"{order.Id}|{string.Join(";", adjustments.OrderBy(a => a.OriginalItemId).Select(a => $"{a.OriginalItemId}:{a.Quantity}:{a.Gross:0.00}"))}|{string.Join(",", sourceFormsForLink.Select(f => f.Mark).Order())}";
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(keyMaterial)))[..20];
        var externalSystemId = $"orderform-discount-return-{order.Id}-{digest}";
        var existing = await db.FiscalOrderDocuments.FirstOrDefaultAsync(f => f.ExternalSystemId == externalSystemId && f.Mark != null);
        if (existing is not null) return new FiscalIssueResult { Success = true, Mark = existing.Mark, QrCode = existing.QrCode };

        var series = await _configuration.GetOrCreateSeriesAsync(db, FiscalDocumentKind.OrderForm, settings.OrderFormSeriesName);
        var thisNumber = series.NextNumber;
        ParochosSendResult result = new();
        for (var attempt = 0; attempt <= 5; attempt++)
        {
            var source = new Dictionary<string, object?>
            {
                ["invoice"] = new Dictionary<string, object?>
                {
                    ["issuer"] = FiscalPayloadBuilder.BuildIssuer(settings),
                    ["counterpart"] = new Dictionary<string, object?> { ["vatNumber"] = "", ["branch"] = 0, ["name"] = "Πελάτης Λιανικής", ["country"] = "GR" },
                    ["invoiceHeader"] = new Dictionary<string, object?>
                    {
                        ["series"] = series.SeriesName, ["aa"] = thisNumber,
                        ["issueDate"] = FiscalPayloadBuilder.IssueTimestampGreekLocal(), ["currency"] = "EUR",
                        ["invoiceType"] = settings.OrderFormInvoiceType, ["invoiceTypeUbl"] = "380",
                        ["OpenPointOfService"] = 0, ["ClosePointOfService"] = 0,
                        ["CancelConnected"] = 0, ["PointOfService"] = pointOfService
                    },
                    ["invoiceDetails"] = lines,
                    ["paymentMethods"] = new List<object?> { new Dictionary<string, object?> { ["type"] = 5, ["amount"] = 0m } },
                    ["invoiceSummary"] = new Dictionary<string, object?> { ["totalNetValue"] = totals.Net, ["totalVatAmount"] = totals.Vat, ["totalValue"] = totalGross },
                    ["correlatedInvoices"] = sourceFormsForLink.Where(f => f.Mark.HasValue)
                        .Select(f => (object?)new Dictionary<string, object?> { ["mark"] = f.Mark!.Value }).ToList()
                }
            };
            result = await _parochos.SendAndWaitAsync(settings, source, externalSystemId);
            if (result.Success || result.ErrorCode != "00010022") break;
            thisNumber++;
        }

        if (!result.Success)
            return new FiscalIssueResult { Success = false, ErrorMessage = result.ErrorMessage };

        var returnDocument = new FiscalOrderDocument
        {
            OrderId = order.Id, ExternalSystemId = externalSystemId, SeriesName = series.SeriesName,
            Number = thisNumber, Mark = result.Mark, QrCode = result.QrCode, Uid = result.Uid,
            ProcessId = result.ProcessId, IsCancellation = false, IsDiscountReturn = true
        };
        db.FiscalOrderDocuments.Add(returnDocument);
        foreach (var adjustment in adjustments)
        {
            returnDocument.Items.Add(new FiscalOrderDocumentItem
            {
                OrderItemId = adjustment.OriginalItemId, Quantity = adjustment.Quantity,
                DiscountReturnGrossAmount = adjustment.Gross
            });
        }
        series.NextNumber = checked((int)Math.Max((long)series.NextNumber, thisNumber + 1L));

        if (!string.IsNullOrWhiteSpace(result.ProcessId))
        {
            var assign = await _parochos.AssignDocumentAsync(settings, pointOfService, result.ProcessId!, externalSystemId);
            if (!assign.Success && assign.ErrorCode != "00130002")
                _logger.LogWarning("Discount return 8.6 {ExternalSystemId} εκδόθηκε αλλά το AssignDocument απέτυχε: {Error}", externalSystemId, assign.ErrorMessage);
        }
        await db.SaveChangesAsync();
        return new FiscalIssueResult { Success = true, Mark = result.Mark, QrCode = result.QrCode };
    }

    /// <summary>8.6 με γραμμές recType = 7 (αρνητικό πρόσημο) για ακύρωση/μεταφορά ειδών.</summary>
    private async Task<FiscalIssueResult> IssueNegativeOrderFormAsync(
        AppDbContext db,
        Order order,
        string pointOfService,
        List<OrderItem> items,
        List<FiscalOrderDocument> correlatedForms)
    {
        if (await db.HasPendingTableTransferAsync(order.Id))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Υπάρχει εκκρεμής μεταφορά τραπεζιού. Ολοκλήρωσε τον επανέλεγχο πριν από φορολογική ενέργεια ή πληρωμή." };

        var settings = (await _configuration.GetSettingsAsync(db))!;
        var series = await _configuration.GetOrCreateSeriesAsync(db, FiscalDocumentKind.OrderForm, settings.OrderFormSeriesName);
        var thisNumber = series.NextNumber;
        var externalSystemId = $"orderform-neg-{order.Id}-{Guid.NewGuid():N}";
        var lines = FiscalPayloadBuilder.BuildLines(items);
        foreach (var line in lines.OfType<Dictionary<string, object?>>())
            line["recType"] = 7;
        var totals = FiscalPayloadBuilder.CalculateTotals(items);

        ParochosSendResult result = new();
        for (var attempt = 0; attempt <= 5; attempt++)
        {
            var source = new Dictionary<string, object?>
            {
                ["invoice"] = new Dictionary<string, object?>
                {
                    ["issuer"] = FiscalPayloadBuilder.BuildIssuer(settings),
                    ["counterpart"] = new Dictionary<string, object?>
                    {
                        ["vatNumber"] = "",
                        ["branch"] = 0,
                        ["name"] = "Πελάτης Λιανικής",
                        ["country"] = "GR"
                    },
                    ["invoiceHeader"] = new Dictionary<string, object?>
                    {
                        ["series"] = series.SeriesName,
                        ["aa"] = thisNumber,
                        ["issueDate"] = FiscalPayloadBuilder.IssueTimestampGreekLocal(),
                        ["currency"] = "EUR",
                        ["invoiceType"] = settings.OrderFormInvoiceType,
                        ["invoiceTypeUbl"] = "380",
                        ["OpenPointOfService"] = 0,
                        ["ClosePointOfService"] = 0,
                        ["CancelConnected"] = 0,
                        ["PointOfService"] = pointOfService
                    },
                    ["invoiceDetails"] = lines,
                    ["paymentMethods"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["type"] = 5, ["amount"] = 0m }
                    },
                    ["invoiceSummary"] = new Dictionary<string, object?>
                    {
                        ["totalNetValue"] = totals.Net,
                        ["totalVatAmount"] = totals.Vat,
                        ["totalValue"] = totals.Gross
                    },
                    ["correlatedInvoices"] = correlatedForms
                        .Where(f => f.Mark.HasValue)
                        .Select(f => (object?)new Dictionary<string, object?> { ["mark"] = f.Mark!.Value })
                        .ToList()
                }
            };

            result = await _parochos.SendAndWaitAsync(settings, source, externalSystemId);
            if (result.Success || result.ErrorCode != "00010022")
                break;
            thisNumber++;
        }

        if (!result.Success)
            return new FiscalIssueResult { Success = false, ErrorMessage = result.ErrorMessage };

        db.FiscalOrderDocuments.Add(new FiscalOrderDocument
        {
            OrderId = order.Id,
            ExternalSystemId = externalSystemId,
            SeriesName = series.SeriesName,
            Number = thisNumber,
            Mark = result.Mark,
            QrCode = result.QrCode,
            Uid = result.Uid,
            ProcessId = result.ProcessId,
            IsCancellation = true
        });
        series.NextNumber = checked((int)Math.Max((long)series.NextNumber, thisNumber + 1L));

        if (!string.IsNullOrWhiteSpace(result.ProcessId))
        {
            var assign = await _parochos.AssignDocumentAsync(settings, pointOfService, result.ProcessId!, externalSystemId);
            if (!assign.Success && assign.ErrorCode != "00130002")
                _logger.LogWarning("Αρνητικό 8.6 {ExternalSystemId}: AssignDocument απέτυχε: {Error}", externalSystemId, assign.ErrorMessage);
        }

        await db.SaveChangesAsync();
        return new FiscalIssueResult
        {
            Success = true,
            Mark = result.Mark,
            QrCode = result.QrCode
        };
    }

    private async Task<FiscalIssueResult> CancelOrderTicketsAsync(
        AppDbContext db,
        Order order,
        string pointOfService,
        List<FiscalOrderDocument> priorOrderForms,
        List<OrderItem> activeItems,
        string reason,
        bool cancelConnected = false)
    {
        var settings = await _configuration.GetSettingsAsync(db);
        if (settings is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκαν οι ρυθμίσεις Parochos." };

        if (priorOrderForms.Count == 0 || priorOrderForms.Any(f => !f.Mark.HasValue))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν υπάρχουν έγκυρα MARK για τα δελτία που ζητήθηκε να ακυρωθούν." };

        var series = await _configuration.GetOrCreateSeriesAsync(db, FiscalDocumentKind.OrderForm, settings.OrderFormSeriesName);
        var cancellationNumber = series.NextNumber;
        var externalSystemId = $"cancel-orderform-{order.Id}-{Guid.NewGuid():N}";
        var totals = FiscalPayloadBuilder.CalculateTotals(activeItems);

        var invoice = new Dictionary<string, object?>
        {
            ["issuer"] = FiscalPayloadBuilder.BuildIssuer(settings),
            ["counterpart"] = new Dictionary<string, object?>
            {
                ["vatNumber"] = "",
                ["branch"] = 0,
                ["name"] = "Πελάτης Λιανικής",
                ["country"] = "GR"
            },
            ["invoiceHeader"] = new Dictionary<string, object?>
            {
                ["CancelConnected"] = cancelConnected ? 1 : 0,
                ["PointOfService"] = pointOfService,
                ["series"] = series.SeriesName,
                ["aa"] = series.NextNumber,
                ["issueDate"] = FiscalPayloadBuilder.IssueTimestampGreekLocal(),
                ["invoiceType"] = settings.OrderFormInvoiceType,
                ["invoiceTypeUbl"] = "380",
                ["currency"] = "EUR",
                ["OpenPointOfService"] = 0,
                ["ClosePointOfService"] = 0
            },
            ["invoiceDetails"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["quantity"] = 1m,
                    ["entityName"] = string.IsNullOrWhiteSpace(reason) ? "Ακύρωση Δελτίου Παραγγελίας" : reason,
                    ["netValue"] = 0m,
                    ["totalValue"] = 0m,
                    ["vatCategory"] = 8,
                    ["vatCategoryUbl"] = "Z",
                    ["vatAmount"] = 0m,
                    ["vatPercent"] = 0m,
                    ["measurementUnit"] = 1,
                    ["measurementUnitUbl"] = "H87"
                }
            },
            ["invoiceSummary"] = new Dictionary<string, object?>
            {
                ["totalNetValue"] = 0m,
                ["totalVatAmount"] = 0m,
                ["totalValue"] = totals.Gross
            },
            ["correlatedInvoices"] = priorOrderForms
                .Where(f => f.Mark.HasValue)
                .Select(f => (object?)new Dictionary<string, object?> { ["mark"] = f.Mark!.Value })
                .ToList()
        };

        var source = new Dictionary<string, object?> { ["invoice"] = invoice };
        var result = await _parochos.SendAndWaitAsync(settings, source, externalSystemId, transmissionType: 5);
        if (!result.Success)
            return new FiscalIssueResult { Success = false, ErrorMessage = result.ErrorMessage };

        series.NextNumber++;
        db.FiscalOrderCancellations.Add(new FiscalOrderCancellation
        {
            OrderId = order.Id,
            ExternalSystemId = externalSystemId,
            ProcessId = result.ProcessId,
            Mark = result.Mark,
            DocumentKind = FiscalDocumentKind.CancelOrderForm,
            SeriesName = series.SeriesName,
            Number = cancellationNumber,
            QrCode = result.QrCode,
            Reason = reason
        });
        await db.SaveChangesAsync();

        return new FiscalIssueResult { Success = true, Mark = result.Mark, QrCode = result.QrCode };
    }

    public async Task<FiscalIssueResult> CancelWholeOrderAsync(
        AppDbContext db,
        Order order,
        string reason = "Ακύρωση ολόκληρης παραγγελίας από τον σερβιτόρο",
        bool closePointOfService = true)
    {
        var settings = await _configuration.GetSettingsAsync(db);
        if (settings is null)
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Δεν βρέθηκαν οι ρυθμίσεις Parochos."
            };

        var activeItems = await db.OrderItems
            .Include(i => i.MenuItem).ThenInclude(i => i!.MenuCategory)
            .Where(i => i.OrderId == order.Id && !i.IsCancelled)
            .ToListAsync();

        if (activeItems.Count == 0)
            return closePointOfService
                ? await CloseEmptyOrderAsync(db, order)
                : new FiscalIssueResult { Success = true };

        if (await db.HasPendingTableTransferAsync(order.Id))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Υπάρχει εκκρεμής μεταφορά τραπεζιού. Ολοκλήρωσε τον επανέλεγχο πριν από φορολογική ενέργεια ή πληρωμή." };

        // Χωρίς ενεργό πάροχο, η ακύρωση γίνεται μόνο τοπικά. Αν υπάρχουν ήδη
        // εκδομένα δελτία, ακυρώνονται φορολογικά ακόμη κι αν η έκδοση νέων 8.6
        // έχει απενεργοποιηθεί στις ρυθμίσεις.
        if (!settings.UseParochosProvider)
        {
            foreach (var item in activeItems)
            {
                item.IsCancelled = true;
                item.CancelledAt = DateTime.Now;
                item.CancelReason = reason;
            }

            await db.SaveChangesAsync();
            return new FiscalIssueResult { Success = true };
        }

        if (await db.OrderPrintBatches.AnyAsync(b => b.OrderId == order.Id && b.DispatchNotificationPending) ||
            await db.FiscalOrderDocuments.AnyAsync(d => d.OrderId == order.Id && d.Mark == null && d.ParochosInvoicePayloadJson != null))
            return new FiscalIssueResult { ErrorMessage = "Υπάρχει εκκρεμές δελτίο. Ακύρωσε τα είδη από το Χ ή ολοκλήρωσε τον έλεγχο πριν από ολική ακύρωση." };
        var priorOrderForms = await db.FiscalOrderDocuments
            .Include(f => f.Items)
            .Where(f => f.OrderId == order.Id && f.Mark != null && !f.IsCancelled)
            .OrderBy(f => f.Id)
            .ToListAsync();

        // Αν δεν υπάρχει φορολογικό Order Form, δεν υπάρχει κάτι να ακυρωθεί
        // στον Parochos. Ακυρώνουμε μόνο τοπικά.
        if (priorOrderForms.Count == 0)
        {
            foreach (var item in activeItems)
            {
                item.IsCancelled = true;
                item.CancelledAt = DateTime.Now;
                item.CancelReason = reason;
            }

            await db.SaveChangesAsync();
            return new FiscalIssueResult { Success = true };
        }

        var pointOfService = await _configuration.ResolvePointOfServiceAsync(db, order);
        if (string.IsNullOrWhiteSpace(pointOfService))
        {
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Δεν βρέθηκε το Point of Service του τραπεζιού."
            };
        }

        // Parochos v3.0.29 (Hospitality - Cancel Connected): ολική ακύρωση δελτίων 8.6
        // γίνεται με CancelConnected = 1 και correlatedInvoices τα MARK τους.
        // Επειδή αυτό αναιρεί τα ανοιχτά δελτία του Point of Service, όταν στο ίδιο
        // τραπέζι υπάρχει άλλη ενεργή παραγγελία χρησιμοποιούμε 8.6 με recType = 7.
        var otherOpenOrderOnSameTable = await db.Orders
            .Where(o => o.Id != order.Id && o.RestaurantTableId == order.RestaurantTableId &&
                        o.Status == OrderStatus.Open && o.FiscalStatus != FiscalStatus.Issued)
            .AnyAsync(o => db.OrderItems.Any(i => i.OrderId == o.Id && !i.IsCancelled));

        var cancel = otherOpenOrderOnSameTable
            ? await IssueNegativeOrderFormAsync(db, order, pointOfService, activeItems, priorOrderForms)
            : await CancelOrderTicketsAsync(db, order, pointOfService, priorOrderForms, activeItems, reason, cancelConnected: true);
        if (!cancel.Success)
            return cancel;

        foreach (var form in priorOrderForms)
        {
            form.IsCancelled = true;
            form.CancelledAt = DateTime.Now;
            form.CancellationMark = cancel.Mark;
        }
        foreach (var item in activeItems)
        {
            item.IsCancelled = true;
            item.CancelledAt = DateTime.Now;
            item.CancelReason = reason;
        }
        await db.SaveChangesAsync();
        return closePointOfService
            ? await CloseEmptyOrderAsync(db, order)
            : new FiscalIssueResult { Success = true, Mark = cancel.Mark, QrCode = cancel.QrCode };

    }

    public async Task<FiscalIssueResult> CloseEmptyOrderAsync(AppDbContext db, Order order)
    {
        var settings = await _configuration.GetSettingsAsync(db);
        if (settings is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκαν οι ρυθμίσεις Parochos." };
        if (!settings.UseParochosProvider)
            return new FiscalIssueResult { Success = true, ProviderUsed = false };

        var hasActiveItems = await db.OrderItems
            .AnyAsync(i => i.OrderId == order.Id && !i.IsCancelled);

        if (hasActiveItems)
        {
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Το τραπέζι δεν είναι άδειο. Υπάρχουν ενεργά είδη."
            };
        }

        // Αν δεν εκδόθηκε ποτέ Order Form για το συγκεκριμένο τραπέζι,
        // δεν υπάρχει Point of Service στον Parochos που να χρειάζεται κλείσιμο.
        var hadOrderForms = await db.FiscalOrderDocuments
            .AnyAsync(f => f.OrderId == order.Id && f.Mark != null);

        if (!hadOrderForms)
            return new FiscalIssueResult { Success = true };

        var pointOfService = await _configuration.ResolvePointOfServiceAsync(db, order);
        if (string.IsNullOrWhiteSpace(pointOfService))
        {
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Δεν βρέθηκε το Point of Service του τραπεζιού."
            };
        }

        // A Point of Service may contain multiple independent local orders/tickets.
        // Closing one empty order must not close the provider table while another
        // order for the same restaurant table still has active items.
        var otherOpenOrderOnSameTable = await db.Orders
            .Where(o => o.Id != order.Id && o.RestaurantTableId == order.RestaurantTableId &&
                        o.Status == OrderStatus.Open && o.FiscalStatus != FiscalStatus.Issued)
            .AnyAsync(o => db.OrderItems.Any(i => i.OrderId == o.Id && !i.IsCancelled));

        if (otherOpenOrderOnSameTable)
        {
            _logger.LogInformation(
                "Δεν κλείνει το PointOfService {PointOfService}: υπάρχει άλλη ενεργή τοπική παραγγελία στο ίδιο τραπέζι.",
                pointOfService);
            return new FiscalIssueResult { Success = true };
        }

        var close = await _parochos.ClosePointOfServiceAsync(settings, pointOfService);

        if (!close.Success)
        {
            var state = await _parochos.ListPointOfServiceDetailsAsync(settings, pointOfService);
            if (state.Success && state.PointOfService is null && state.Details.Count == 0)
                return new FiscalIssueResult { Success = true };
            _logger.LogWarning(
                "Parochos ClosePointOfService απέτυχε για άδειο τραπέζι {PointOfService}: {Error}",
                pointOfService,
                close.ErrorMessage);

            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Απέτυχε το κλείσιμο του τραπεζιού στον Parochos: " + close.ErrorMessage
            };
        }

        _logger.LogInformation(
            "Parochos PointOfService {PointOfService} έκλεισε επιτυχώς για τραπέζι χωρίς ενεργά είδη.",
            pointOfService);

        return new FiscalIssueResult { Success = true };
    }


}

