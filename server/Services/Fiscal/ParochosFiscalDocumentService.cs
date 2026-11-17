using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RestaurantOrders.Data;
using RestaurantOrders.Models;
using RestaurantOrders.Services.Fiscal;
using RestaurantOrders.Services.Fiscal.Provider;

namespace RestaurantOrders.Services;

public sealed class FiscalIssueResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public bool ProviderUsed { get; init; } = true;
    public bool RequiresRecovery { get; init; }
    public bool OfflineIssued { get; init; }
    public bool NumberConflict { get; init; }
    public bool AlreadyPrinted { get; init; }
    public int? OrderFormId { get; init; }
    public int? CancelledOrderItemId { get; init; }
    public int? TransmissionFailure { get; init; }

    // Data returned by Parochos/ΑΑΔΕ for printing.
    public long? Mark { get; init; }
    public string? QrCode { get; init; }
    public string? SeriesName { get; init; }
    public long? InvoiceNumber { get; init; }
}

public sealed record PendingFiscalTransmission(
    int OrderId,
    string Document,
    string Table,
    DateTime? AttemptedAtUtc,
    decimal Amount,
    string State,
    int? TransmissionFailure,
    DateTime? OfflineReceiptPrintedAtUtc,
    string? ErrorMessage,
    int? FiscalOrderDocumentId = null,
    int? BatchId = null);

/// <summary>Αποτέλεσμα ασφαλούς εκτύπωσης ήδη εκδομένης offline απόδειξης.</summary>
public sealed record OfflineReceiptPrintResult(bool Success, bool WasAlreadyPrinted, string? ErrorMessage);

public sealed class CardPaymentPreparation
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string ExternalSystemId { get; init; } = "";
    public string SeriesName { get; init; } = "";
    public long InvoiceNumber { get; init; }
    public string Signature { get; init; } = "";
    public string? Uid { get; init; }
    public string TerminalId { get; init; } = "";
    public string NspCode { get; init; } = "";
    public decimal Amount { get; init; }
    public ProviderPaymentData? ProviderData { get; init; }
}

/// <summary>
/// Builds Parochos v3 source documents and coordinates transmission, EFT-POS and order forms.
/// </summary>
/// <summary>Parochos implementation of the application fiscal-document contract.</summary>
public sealed class ParochosFiscalDocumentService : IFiscalDocumentService
{
    private readonly IParochosFiscalProvider _parochos;
    private readonly FiscalConfigurationService _configuration;
    private readonly FiscalCardPaymentService _cardPayments;
    private readonly FiscalCancellationService _cancellations;
    private readonly PrinterService _printer;
    private readonly ILogger<ParochosFiscalDocumentService> _logger;

    public ParochosFiscalDocumentService(
        IParochosFiscalProvider parochos,
        FiscalConfigurationService configuration,
        FiscalCardPaymentService cardPayments,
        FiscalCancellationService cancellations,
        PrinterService printer,
        ILogger<ParochosFiscalDocumentService> logger)
    {
        _parochos = parochos;
        _configuration = configuration;
        _cardPayments = cardPayments;
        _cancellations = cancellations;
        _printer = printer;
        _logger = logger;
    }

    public Task<CardPaymentPreparation> PrepareCardPaymentAsync(
        AppDbContext db, Order order, FiscalDocumentKind kind, BankPosTerminalSettings? selectedPos = null, long? mark = null) =>
        _cardPayments.PrepareCardPaymentAsync(db, order, kind, selectedPos, mark);

    public async Task<ParochosSimpleResult> SubmitPreloadedPaymentAsync(
        AppDbContext db, Order order, decimal amount, string transactionId, string signature,
        string terminalId, string? paymentMethodInfo = null, int paymentType = 7)
    {
        if (order.FiscalStatus != FiscalStatus.Issued || order.ParochosMark is not > 0 ||
            string.IsNullOrWhiteSpace(order.ParochosExternalId))
            return new ParochosSimpleResult { Success = false, ErrorMessage = "Δεν υπάρχει ήδη εκδομένο παραστατικό με MARK για SubmitPayment." };
        var settings = await _configuration.GetSettingsAsync(db);
        if (!_configuration.ValidateSettings(settings, out var validationError))
            return new ParochosSimpleResult { Success = false, ErrorMessage = validationError };
        return await _parochos.SubmitPaymentAsync(settings!, order.ParochosExternalId!, order.ParochosMark.Value,
            amount, transactionId, signature, terminalId, paymentMethodInfo, paymentType);
    }

    public async Task<IReadOnlyList<PendingFiscalTransmission>> GetPendingTransmissionsAsync(AppDbContext db)
    {
        var orders = await db.Orders
            .Include(o => o.RestaurantTable)
            .Include(o => o.Items).ThenInclude(i => i.MenuItem).ThenInclude(m => m!.MenuCategory)
            .Where(o => !o.ParochosOutboxArchived && (o.ParochosTransmissionState == "Pending" ||
                         o.ParochosTransmissionState == "Sending" ||
                         o.ParochosTransmissionState == "NeedsReview" ||
                         o.ParochosTransmissionState == "OfflineIssued") &&
                        // A MARK with Issued is the authoritative local proof that
                        // this exact document has already been transmitted.  Do not
                        // leave it in the outbox merely because an older path did
                        // not clear ParochosTransmissionState.
                        !(o.FiscalStatus == FiscalStatus.Issued && o.ParochosMark != null) &&
                        o.ParochosInvoicePayloadJson != null && o.ParochosInvoicePayloadJson != "")
            .OrderBy(o => o.ParochosTransmissionAttemptedAtUtc)
            .ToListAsync();

        var pending = orders.Select(order => new PendingFiscalTransmission(
            order.Id,
            $"{order.InvoiceSeriesName ?? "Παραστατικό"} {order.InvoiceNumber?.ToString(CultureInfo.InvariantCulture) ?? "—"}",
            order.RestaurantTable?.Name ?? "Take Away",
            order.ParochosTransmissionAttemptedAtUtc,
            FiscalStoredPayload.Total(order.ParochosInvoicePayloadJson) ?? FiscalPayloadBuilder.CalculateTotals(order).Gross,
            order.ParochosTransmissionState ?? "Pending",
            order.ParochosTransmissionFailure,
            order.ParochosOfflineReceiptPrintedAtUtc,
            order.FiscalErrorMessage)).ToList();

        var orderForms = await db.FiscalOrderDocuments
            .Include(document => document.Order).ThenInclude(order => order!.RestaurantTable)
            .Include(document => document.Items).ThenInclude(item => item.OrderItem).ThenInclude(item => item!.MenuItem).ThenInclude(menuItem => menuItem!.MenuCategory)
            .Where(document =>
                !document.ParochosOutboxArchived && (document.ParochosTransmissionState == "Pending" ||
                 document.ParochosTransmissionState == "Sending" ||
                 document.ParochosTransmissionState == "NeedsReview" ||
                 document.ParochosTransmissionState == "Ready" ||
                 document.ParochosTransmissionState == "AwaitingOriginal" ||
                 document.ParochosTransmissionState == "OfflineIssued") &&
                document.Mark == null &&
                document.ParochosInvoicePayloadJson != null &&
                document.ParochosInvoicePayloadJson != "")
            .ToListAsync();

        pending.AddRange(orderForms.Select(document => new PendingFiscalTransmission(
            document.OrderId,
            $"{(document.IsCancellation ? "Ακυρωτικό δελτίο" : "Δελτίο παραγγελίας")} {document.SeriesName} {document.Number.ToString(CultureInfo.InvariantCulture)}",
            document.Order?.RestaurantTable?.Name ?? "Take Away",
            document.ParochosTransmissionAttemptedAtUtc,
            FiscalStoredPayload.Total(document.ParochosInvoicePayloadJson) ?? document.Items.Sum(item => item.Quantity * ((item.OrderItem?.MenuItem?.Price ?? 0m) + (item.OrderItem?.ExtrasPrice ?? 0m))),
            document.ParochosTransmissionState ?? "Pending",
            document.ParochosTransmissionFailure,
            document.PrintConfirmedAtUtc,
            document.FiscalErrorMessage,
            document.Id)));

        var batches = await db.OrderPrintBatches.AsNoTracking()
            .Include(b => b.Order).ThenInclude(o => o!.RestaurantTable)
            .Include(b => b.Items).ThenInclude(i => i.MenuItem)
            .Where(b => b.DispatchNotificationPending).ToListAsync();
        pending.AddRange(batches.Select(b => new PendingFiscalTransmission(b.OrderId,
            $"Δελτίο για παραγγελία Ν-{b.DailyNumber}", b.Order?.RestaurantTable?.Name ?? "-",
            b.CreatedAt.ToUniversalTime(), b.Items.Sum(i => i.Quantity * ((i.MenuItem?.Price ?? 0m) + i.ExtrasPrice)),
            "AwaitingIssue", null, null, null, BatchId: b.Id)));
        return pending
            .OrderBy(row => row.BatchId.HasValue || row.FiscalOrderDocumentId.HasValue ? 0 : 1)
            .ThenBy(row => row.AttemptedAtUtc ?? DateTime.MaxValue)
            .ToList();
    }

    // The waiter and cashier can check the same order concurrently. Keep lookup + send
    // serialized per order within this server, without holding SQLite across network I/O.
    private static readonly SemaphoreSlim[] ReceiptGates = Enumerable.Range(0,127).Select(_=>new SemaphoreSlim(1,1)).ToArray();
    public async Task<FiscalIssueResult> RetryPendingTransmissionAsync(AppDbContext db, int orderId)
    {
        var gate=ReceiptGates[(uint)orderId % ReceiptGates.Length];
        await gate.WaitAsync();
        try { return await RetryReceiptCoreAsync(db,orderId); }
        finally { gate.Release(); }
    }
    private async Task<FiscalIssueResult> RetryReceiptCoreAsync(AppDbContext db,int orderId)
    {
        var order = await db.Orders
            .Include(o => o.RestaurantTable)
            .Include(o => o.Items).ThenInclude(i => i.MenuItem).ThenInclude(m => m!.MenuCategory)
            .FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκε η παραγγελία." };

        await db.Entry(order).ReloadAsync();
        // A stale client can still have this row on screen after another check
        // completed it.  Never send it again; report success so the caller can
        // refresh its outbox and remove the row immediately.
        if (order.FiscalStatus == FiscalStatus.Issued && order.ParochosMark is > 0)
            return new FiscalIssueResult
            {
                Success = true,
                Mark = order.ParochosMark,
                QrCode = order.ParochosQrCode,
                SeriesName = order.InvoiceSeriesName,
                InvoiceNumber = order.InvoiceNumber,
                AlreadyPrinted = order.ParochosOfflineReceiptPrintedAtUtc.HasValue
            };

        if (order.ParochosOutboxArchived || order.ParochosTransmissionState is not ("Pending" or "Sending" or "NeedsReview" or "OfflineIssued") ||
            string.IsNullOrWhiteSpace(order.ParochosInvoicePayloadJson) ||
            string.IsNullOrWhiteSpace(order.ParochosExternalId))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν υπάρχει εκκρεμής αποστολή για ασφαλή επανέλεγχο." };

        var settings = await _configuration.GetSettingsAsync(db);
        if (!_configuration.ValidateSettings(settings, out var validationError))
            return new FiscalIssueResult { Success = false, ErrorMessage = validationError };

        return await RecoverTransmissionAsync(
            db, order, settings!, allowResendWhenNotFound: order.ParochosTransmissionState != "NeedsReview" && order.ParochosTransmissionFailure != 2);
    }

    public async Task<FiscalIssueResult> RetryPendingOrderFormTransmissionAsync(
        AppDbContext db,
        int fiscalOrderDocumentId)
    {
        var document = await db.FiscalOrderDocuments
            .Include(item => item.Order).ThenInclude(order => order!.RestaurantTable)
            .FirstOrDefaultAsync(item => item.Id == fiscalOrderDocumentId);
        if (document is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκε το δελτίο παραγγελίας." };

        if (document.Mark is > 0)
            return new FiscalIssueResult
            {
                Success = true,
                Mark = document.Mark,
                QrCode = document.QrCode,
                SeriesName = document.SeriesName,
                InvoiceNumber = document.Number
            };

        if (document.ParochosOutboxArchived) return new FiscalIssueResult { ErrorMessage = "Το παραστατικό βρίσκεται στο αρχείο καθαρισμού. Επανέφερέ το για έλεγχο." };

        if (document.ParochosTransmissionState == "AwaitingOriginal")
        {
            var originalMark = await db.FiscalOrderDocuments.Where(f => f.Id == document.CancellationSourceId)
                .Select(f => f.Mark).SingleOrDefaultAsync();
            if (originalMark is not > 0)
                return new FiscalIssueResult { ErrorMessage = "Πρώτα χρειάζεται επιβεβαίωση του αρχικού δελτίου.", RequiresRecovery = true };
            var payload = JsonNode.Parse(document.ParochosInvoicePayloadJson!)!;
            payload["invoice"]!["correlatedInvoices"] = new JsonArray(new JsonObject { ["mark"] = originalMark.Value });
            document.ParochosInvoicePayloadJson = payload.ToJsonString();
            // No previous send of this cancellation was possible without the original MARK.
            document.ParochosTransmissionState = "Ready";
            await db.SaveChangesAsync();
        }
        if (document.ParochosTransmissionState is not ("Ready" or "Pending" or "Sending" or "NeedsReview" or "OfflineIssued") ||
            string.IsNullOrWhiteSpace(document.ParochosInvoicePayloadJson) ||
            string.IsNullOrWhiteSpace(document.ExternalSystemId))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν υπάρχει εκκρεμής αποστολή για ασφαλή επανέλεγχο του δελτίου παραγγελίας." };

        var settings = await _configuration.GetSettingsAsync(db);
        if (!_configuration.ValidateSettings(settings, out var validationError))
            return new FiscalIssueResult { Success = false, ErrorMessage = validationError };

        return await RecoverOrderFormTransmissionAsync(
            db,
            document,
            settings!,
            allowResendWhenNotFound: document.ParochosTransmissionState == "Ready" || (document.ParochosTransmissionState != "NeedsReview" && document.ParochosTransmissionFailure != 2));
    }

    /// <summary>
    /// Τυπώνει μόνο μία offline απόδειξη που έχει ήδη εκδοθεί με Transmission Failure.
    /// Δεν καλεί τον Parochos και δεν μεταβάλλει το externalSystemId ή το payload,
    /// άρα είναι ασφαλές ανεξάρτητο βήμα από τον έλεγχο/τη διαβίβαση.
    /// </summary>
    public async Task<OfflineReceiptPrintResult> PrintPendingOfflineReceiptAsync(AppDbContext db, int orderId)
    {
        var order = await db.Orders
            .Include(o => o.RestaurantTable)
            .Include(o => o.WaiterUser)
            .Include(o => o.Payments)
            .Include(o => o.Items).ThenInclude(i => i.MenuItem).ThenInclude(m => m!.MenuCategory).ThenInclude(c => c!.PrinterStations)
            .Include(o => o.Items).ThenInclude(i => i.MenuItem).ThenInclude(m => m!.MenuCategory).ThenInclude(c => c!.PrinterStation)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order is null)
            return new OfflineReceiptPrintResult(false, false, "Δεν βρέθηκε η απόδειξη.");
        if (order.ParochosMark > 0 || (order.ParochosTransmissionState != "OfflineIssued" && order.ParochosTransmissionFailure is not (1 or 2)))
            return new OfflineReceiptPrintResult(false, false, "Η απόδειξη δεν είναι πλέον σε κατάσταση offline εκτύπωσης.");
        if (order.ParochosOfflineReceiptPrintedAtUtc.HasValue)
            return new OfflineReceiptPrintResult(true, true, "Η πρωτότυπη offline απόδειξη έχει ήδη σημειωθεί ως τυπωμένη.");

        var stations = new List<PrinterStation>();
        foreach (var item in order.Items.Where(i => !i.IsCancelled && i.Quantity > 0))
        {
            var category = item.MenuItem?.MenuCategory;
            if (category is null) continue;

            stations.AddRange(category.PrinterStations);
            if (category.PrinterStation is not null && stations.All(p => p.Id != category.PrinterStation.Id))
                stations.Add(category.PrinterStation);
        }

        stations = stations
            .Where(p => p.IsActive && p.PrintFiscalDocuments)
            .DistinctBy(p => p.Id)
            .ToList();

        // A common single-printer setup should not lose a receipt merely because
        // a category mapping was omitted. We deliberately do not guess when more
        // than one eligible fiscal printer exists, to avoid printing duplicates.
        if (stations.Count == 0)
        {
            var eligibleFallbacks = await db.PrinterStations.AsNoTracking()
                .Where(p => p.IsActive && p.PrintFiscalDocuments)
                .OrderBy(p => p.Name)
                .ToListAsync();
            if (eligibleFallbacks.Count == 1)
                stations.Add(eligibleFallbacks[0]);
        }

        if (stations.Count == 0)
            return new OfflineReceiptPrintResult(false, false,
                "Δεν βρέθηκε ενεργός εκτυπωτής παραστατικών. Σύνδεσέ τον στην κατηγορία των ειδών ή όρισε έναν μόνο ενεργό εκτυπωτή παραστατικών.");

        var items = order.Items.Where(i => !i.IsCancelled && i.Quantity > 0)
            .Select(i => (
                ItemName: i.MenuItem?.Name ?? "Είδος",
                Quantity: i.Quantity,
                LineTotal: i.Quantity * ((i.MenuItem?.Price ?? 0m) + i.ExtrasPrice),
                VatPercent: i.MenuItem?.VatPercentOverride ?? i.MenuItem?.MenuCategory?.VatPercent ?? 24m))
            .ToList();
        if (items.Count == 0)
            return new OfflineReceiptPrintResult(false, false, "Η απόδειξη δεν έχει ενεργές γραμμές προς εκτύπωση.");

        var settings = await db.CompanySettings.AsNoTracking().FirstOrDefaultAsync();
        var relatedDocuments = await db.FiscalOrderDocuments.AsNoTracking()
            .Where(f => f.OrderId == order.Id && f.Mark != null && !f.IsCancellation && !f.IsCancelled)
            .OrderBy(f => f.IssuedAt)
            .Select(f => $"Δελτίο παραγγελίας {f.SeriesName} {f.Number}")
            .ToListAsync();
        var transmissionFailure = order.ParochosTransmissionFailure is 1 or 2
            ? order.ParochosTransmissionFailure.Value
            : 1;
        var documentLabel = order.StaffConsumptionInvoiceType is "6.1" or "6.2" ? "ΣΤΟΙΧΕΙΟ ΙΔΙΟΚΑΤΑΝΑΛΩΣΗΣ" : order.DocumentKind == FiscalDocumentKind.Invoice
            ? "ΤΙΜΟΛΟΓΙΟ"
            : "ΑΠΟΔΕΙΞΗ ΛΙΑΝΙΚΗΣ";
        var failures = new List<string>();

        foreach (var station in stations)
        {
            var printed = await _printer.PrintFiscalReceiptAsync(
                station,
                order.RestaurantTable?.Name ?? "Take Away",
                order.WaiterUser?.FullName ?? "-",
                documentLabel,
                order.InvoiceSeriesName ?? "",
                order.InvoiceNumber ?? 0,
                mark: null,
                // Transmission Failure is printed as text, not as a fake AADE QR.
                qrCode: null,
                items: items,
                total: items.Sum(i => i.LineTotal),
                companySettings: settings,
                order: order,
                copyLabel: "ΠΡΩΤΟΤΥΠΟ",
                issuedAt: order.ClosedAt ?? order.CreatedAt,
                relatedDocuments: relatedDocuments,
                transmissionFailure: transmissionFailure);
            if (!printed)
                failures.Add(station.Name);
        }

        if (failures.Count > 0)
            return new OfflineReceiptPrintResult(false, false, $"Η απόδειξη αποθηκεύτηκε. Δεν επιβεβαιώθηκε η εκτύπωση σε: {string.Join(", ", failures)}.");

        order.ParochosOfflineReceiptPrintedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return new OfflineReceiptPrintResult(true, false, null);
    }

    private sealed record ReceiptScope(int[] FormIds, int[] ItemIds, int[] BatchIds, string? Table);
    private static ReceiptScope? Scope(Order order) => order.ReceiptScopeJson == null ? null :
        JsonSerializer.Deserialize<ReceiptScope>(order.ReceiptScopeJson) ?? throw new InvalidOperationException("Δεν αναγνώστηκε η σύνδεση με τα δελτία.");
    private static IQueryable<FiscalOrderDocument> ScopedForms(AppDbContext db,Order order)
    {
        var query=db.FiscalOrderDocuments.Where(f=>f.OrderId==order.Id && !f.IsCancelled);
        if(Scope(order) is not {} scope) return query; // legacy path; no retroactive inference of identity
        return query.Where(f=>scope.FormIds.Contains(f.Id) || (!f.IsCancellation && !f.IsDiscountReturn &&
            f.Items.Any(i=>scope.ItemIds.Contains(i.OrderItemId) && i.OrderItem!.OrderPrintBatchId.HasValue &&
                scope.BatchIds.Contains(i.OrderItem.OrderPrintBatchId.Value))));
    }

    private async Task<ParochosSendResult> LookupExactAsync(CompanySettings settings, string externalId, string? knownProcessId, string? knownUid = null)
    {
        // A successful lookup must contain at least one matching identifier; a MARK
        // alone cannot establish identity when the response omits those identifiers.
        bool Matches(ParochosSendResult p) => !(p.Success && p.Mark is > 0) ||
            FiscalDocumentIdentity.IsConfirmed(p, externalId, knownProcessId, knownUid);
        var result = await _parochos.GetDocumentByExternalSystemIdAsync(settings, externalId);
        if (!Matches(result)) return new ParochosSendResult { ErrorCode="00010022", ErrorMessage="Διαφορετικά αναγνωριστικά παραστατικού." };
        if (string.IsNullOrWhiteSpace(knownProcessId) || (result.Success && result.Mark > 0) ||
            !(result.IsNotFound || result.ErrorCode == "00010022")) return result;
        // Only a process ID already stored on THIS document may participate in identity recovery.
        try
        {
            var byProcess = await _parochos.GetDocumentByProcessIdAsync(settings, knownProcessId);
            if (!Matches(byProcess)) return new ParochosSendResult { ErrorCode="00010022", ErrorMessage="Διαφορετικά αναγνωριστικά παραστατικού." };
            if (byProcess.Success && byProcess.Mark > 0 && (string.IsNullOrWhiteSpace(byProcess.ProcessId) ||
                byProcess.ProcessId == knownProcessId)) return byProcess;
            if (result.IsNotFound && !byProcess.IsNotFound)
                return new ParochosSendResult { ErrorMessage = "Δεν επιβεβαιώθηκε η ταυτότητα του παραστατικού." };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fiscal lookup by stored process ID failed");
            return new ParochosSendResult { ErrorMessage = "Δεν ολοκληρώθηκε η ταυτοποίηση στον πάροχο." };
        }
        return result;
    }

    private async Task<FiscalIssueResult> RecoverTransmissionAsync(
        AppDbContext db,
        Order order,
        CompanySettings settings,
        bool allowResendWhenNotFound)
    {
        var externalSystemId = order.ParochosExternalId!;
        var wasOfflineIssued = order.ParochosTransmissionFailure is 1 or 2 || order.ParochosTransmissionState == "OfflineIssued";
        var status = await LookupExactAsync(settings, externalSystemId, order.ParochosProcessId, order.ParochosUid);

        if (status.Success && status.Mark is > 0)
        {
            if (order.ParochosTransmissionState == "NeedsReview")
            {
                var storedSend = string.IsNullOrWhiteSpace(status.ProcessId) ? null : await db.FiscalProviderExchanges.AsNoTracking()
                    .Where(e => e.Operation == "/api/send" && e.ExternalSystemId == externalSystemId &&
                        e.ProcessId == status.ProcessId && e.HttpStatus >= 200 && e.HttpStatus < 300)
                    .OrderByDescending(e => e.Id).Select(e => e.RequestJson).FirstOrDefaultAsync();
                if (!FiscalCollisionInspector.MatchesRecordedReceipt(order.ParochosInvoicePayloadJson, storedSend))
                {
                    order.FiscalErrorMessage = "Ο Parochos επέστρεψε ΜΑΡΚ για το αναγνωριστικό, αλλά δεν υπάρχουν επαρκή ίδια αποθηκευμένα στοιχεία (τραπέζι, δελτία και ποσά) για ασφαλή ταυτοποίηση.";
                    await db.SaveChangesAsync();
                    return new FiscalIssueResult { RequiresRecovery = true, NumberConflict = true, ErrorMessage = order.FiscalErrorMessage,
                        SeriesName = order.InvoiceSeriesName, InvoiceNumber = order.InvoiceNumber };
                }
            }
            // The first send reached Parochos. Persist its result, then re-enter
            // the normal issued-document recovery branch to finish correlations
            // and Point of Service closing without transmitting a second invoice.
            order.FiscalStatus = FiscalStatus.Issued;
            order.ParochosTransmissionState = "Issued";
            order.ParochosProcessId = status.ProcessId;
            order.ParochosMark = status.Mark;
            order.ParochosUid = status.Uid;
            order.ParochosAuthenticationCode = status.AuthenticationCode;
            order.ParochosQrCode = status.QrCode;
            order.ParochosPdfUrl = status.PdfFileUrl;
            order.FiscalErrorMessage = null;
            await db.SaveChangesAsync();

            var completed = await IssueReceiptCoreAsync(db, order, order.DocumentKind ?? FiscalDocumentKind.Receipt);
            return completed.Success
                ? new FiscalIssueResult
                {
                    Success = true,
                    Mark = completed.Mark ?? status.Mark,
                    QrCode = completed.QrCode ?? status.QrCode,
                    SeriesName = completed.SeriesName ?? order.InvoiceSeriesName,
                    InvoiceNumber = completed.InvoiceNumber ?? order.InvoiceNumber,
                    AlreadyPrinted = order.ParochosOfflineReceiptPrintedAtUtc.HasValue
                }
                : new FiscalIssueResult
                {
                    Success = false,
                    Mark = status.Mark,
                    QrCode = status.QrCode,
                    SeriesName = order.InvoiceSeriesName,
                    InvoiceNumber = order.InvoiceNumber,
                    ErrorMessage = completed.ErrorMessage ?? "Το παραστατικό βρέθηκε στον Parochos, αλλά εκκρεμεί η ολοκλήρωση της αντιστοίχισης.",
                    RequiresRecovery = true
                };
        }

        // Never resend on a timeout, server error, in-progress state, or generic
        // lookup failure. Only Parochos' explicit 00010005 (not available) is
        // evidence that the original ExternalSystemId was not accepted.
        if (!status.IsNotFound)
        {
            if (order.ParochosTransmissionState != "NeedsReview")
                order.ParochosTransmissionState = wasOfflineIssued ? "OfflineIssued" : "Pending";
            order.FiscalStatus = FiscalStatus.Pending;
            if (status.ErrorCode == "00010022") order.ParochosTransmissionState = "NeedsReview";
            order.FiscalErrorMessage = status.ErrorCode == "00010022"
                ? "Ο αριθμός υπάρχει ήδη, αλλά δεν επιβεβαιώθηκε ότι ανήκει στην ίδια απόδειξη. Δεν έγινε νέα αποστολή."
                :
                (status.InProgress
                    ? "Το παραστατικό εξακολουθεί να ελέγχεται από τον Parochos. Δεν έγινε επαναποστολή."
                    : "Δεν επιβεβαιώθηκε η κατάσταση στον Parochos. Δεν έγινε επαναποστολή για αποφυγή διπλής έκδοσης.");
            await db.SaveChangesAsync();
            return new FiscalIssueResult
            {
                Success = wasOfflineIssued,
                NumberConflict = status.ErrorCode == "00010022",
                OfflineIssued = wasOfflineIssued,
                AlreadyPrinted = order.ParochosOfflineReceiptPrintedAtUtc.HasValue,
                ErrorMessage = order.FiscalErrorMessage,
                RequiresRecovery = true
            };
        }

        if (!allowResendWhenNotFound && order.ParochosTransmissionFailure == 2)
            return new FiscalIssueResult { Success = true, OfflineIssued = true, TransmissionFailure = 2, RequiresRecovery = true, ErrorMessage = "Αναμένεται επιβεβαίωση από τον πάροχο." };
        if (!allowResendWhenNotFound)
        {
            order.ParochosTransmissionState = "NeedsReview";
            order.FiscalStatus = FiscalStatus.Pending;
            order.FiscalErrorMessage = "Ο Parochos δεν βρήκε το παραστατικό. Η αποστολή χρειάζεται έλεγχο πριν από επανάληψη.";
            await db.SaveChangesAsync();
            return new FiscalIssueResult { Success = false, ErrorMessage = order.FiscalErrorMessage, RequiresRecovery = true };
        }

        try
        {
            using var payloadDocument = JsonDocument.Parse(order.ParochosInvoicePayloadJson!);
            var exactSource = PrepareRetryPayload(
                payloadDocument.RootElement,
                externalSystemId,
                order.ParochosTransmissionAttemptedAtUtc,
                transmissionFailure: wasOfflineIssued ? order.ParochosTransmissionFailure ?? 1 : null);
            // Only after an explicit lookup miss: add now-known MARKs for the frozen sale,
            // never rebuild prices, payment data, issue time, external ID or number.
            if (Scope(order) is {} scope)
            {
                var expected=await ScopedForms(db,order).Select(f=>new {f.Id,f.Mark}).ToListAsync();
                if (await db.OrderPrintBatches.AnyAsync(b=>scope.BatchIds.Contains(b.Id) && b.DispatchNotificationPending) ||
                    expected.Any(f=>f.Mark is not > 0) || scope.FormIds.Except(expected.Select(f=>f.Id)).Any())
                {
                    order.FiscalErrorMessage="Πρώτα πρέπει να επιβεβαιωθούν τα δελτία 8.6 αυτής της πώλησης. Δεν έγινε επαναποστολή απόδειξης.";
                    await db.SaveChangesAsync();
                    return new FiscalIssueResult { RequiresRecovery=true, ErrorMessage=order.FiscalErrorMessage,
                        SeriesName=order.InvoiceSeriesName, InvoiceNumber=order.InvoiceNumber };
                }
                var marks=expected.Select(f=>f.Mark!.Value).Distinct().ToList();
                if(marks.Count>0)
                {
                    var src=JsonNode.Parse(exactSource.GetRawText())!;
                    var invoice=src["invoice"]!;
                    var prior=invoice["correlatedInvoices"]?.AsArray().Select(v=>v!["mark"]!.GetValue<long>()) ?? Enumerable.Empty<long>();
                    invoice["correlatedInvoices"]=JsonSerializer.SerializeToNode(prior.Concat(marks).Distinct().Select(mark=>new { mark }).ToArray());
                    if(scope.Table != null && invoice["invoiceHeader"]!["PointOfService"] == null)
                    {
                        invoice["invoiceHeader"]!["PointOfService"]=scope.Table;
                        invoice["invoiceHeader"]!["OpenPointOfService"]=0;
                        invoice["invoiceHeader"]!["ClosePointOfService"]=0;
                    }
                    exactSource=JsonSerializer.SerializeToElement(src);
                }
            }
            order.ParochosInvoicePayloadJson = exactSource.GetRawText();
            order.ParochosTransmissionState = "Sending";
            order.ParochosTransmissionAttemptedAtUtc = DateTime.UtcNow;
            order.FiscalStatus = FiscalStatus.Pending;
            order.FiscalErrorMessage = null;
            await db.SaveChangesAsync();

            // Reuse the committed payload and the same ExternalSystemId. This
            // does not prepare or charge the POS again and keeps Parochos' send
            // idempotency key unchanged.
            var resent = await _parochos.SendAndWaitAsync(settings, exactSource, externalSystemId);
            if (resent.Success && !string.IsNullOrEmpty(resent.ExternalSystemId) && resent.ExternalSystemId != externalSystemId)
                resent = new ParochosSendResult { ErrorCode="00010022", ErrorMessage="Ασυμφωνία ταυτότητας παραστατικού." };

            // A duplicate invoice code can be the delayed final response of the
            // very same submission. Query the exact idempotency key once more
            // before treating it as a collision. A MARK returned for this key is
            // definitive proof that it is this receipt, not merely another
            // document with the same Series/AA.
            if (!resent.Success && resent.ErrorCode == "00010022")
            {
                var duplicateRecovery = await LookupExactAsync(settings, externalSystemId, order.ParochosProcessId, order.ParochosUid);
                if (duplicateRecovery.Success && duplicateRecovery.Mark is > 0)
                {
                    _logger.LogInformation(
                        "Recovered duplicate-code retry for {ExternalSystemId} with MARK {Mark}.",
                        externalSystemId,
                        duplicateRecovery.Mark);
                    resent = duplicateRecovery;
                }
                else
                {
                    // The API only exposes a document lookup by externalSystemId.
                    // A matching Series/AA text alone cannot safely prove that a
                    // different provider document is the same fiscal receipt.
                    order.ParochosTransmissionState = "NeedsReview";
                    order.FiscalStatus = FiscalStatus.Pending;
                    order.FiscalErrorMessage =
                        $"Ο Parochos αναφέρει ότι ο κωδικός {order.InvoiceSeriesName} {order.InvoiceNumber} υπάρχει ήδη " +
                        $"(00010022), αλλά δεν επέστρεψε MARK για το ίδιο ExternalSystemId {externalSystemId}. " +
                        "Η εφαρμογή σταμάτησε την επαναποστολή για να μην ταυτίσει λάθος παραστατικό. " +
                        "Χρειάζεται επιβεβαίωση από τον Parochos με τον κωδικό και το ExternalSystemId.";
                    await db.SaveChangesAsync();
                    return new FiscalIssueResult
                    {
                        Success = false,
                        ErrorMessage = order.FiscalErrorMessage,
                        RequiresRecovery = true,
                        AlreadyPrinted = order.ParochosOfflineReceiptPrintedAtUtc.HasValue
                    };
                }
            }

            if (!resent.Success || resent.Mark is not > 0)
            {
                var uncertain = resent.OutcomeUnknown || resent.InProgress || resent.Mark is > 0;
                var failure = DetermineTransmissionFailure(resent);
                if (failure is 1 or 2)
                {
                    order.ParochosTransmissionFailure = failure == 2 ? 2 : order.ParochosTransmissionFailure ?? 1;
                    wasOfflineIssued = true;
                }
                order.ParochosTransmissionState = wasOfflineIssued ? "OfflineIssued" : uncertain ? "Pending" : "NeedsReview";
                order.FiscalStatus = uncertain ? FiscalStatus.Pending : FiscalStatus.Failed;
                order.FiscalErrorMessage = resent.ErrorMessage ??
                    (resent.Mark is > 0
                        ? "Ο Parochos παρέλαβε το παραστατικό και η επεξεργασία συνεχίζεται."
                        : "Ο Parochos απέρριψε την αποστολή.");
                await db.SaveChangesAsync();
                return new FiscalIssueResult
                {
                    Success = wasOfflineIssued,
                    OfflineIssued = wasOfflineIssued,
                    AlreadyPrinted = order.ParochosOfflineReceiptPrintedAtUtc.HasValue,
                    ErrorMessage = order.FiscalErrorMessage,
                    RequiresRecovery = uncertain || wasOfflineIssued
                };
            }

            order.FiscalStatus = FiscalStatus.Issued;
            order.ParochosTransmissionState = "Issued";
            order.ParochosProcessId = resent.ProcessId;
            order.ParochosMark = resent.Mark;
            order.ParochosUid = resent.Uid;
            order.ParochosAuthenticationCode = resent.AuthenticationCode;
            order.ParochosQrCode = resent.QrCode;
            order.ParochosPdfUrl = resent.PdfFileUrl;
            order.FiscalErrorMessage = null;
            await db.SaveChangesAsync();

            var completed = await IssueReceiptCoreAsync(db, order, order.DocumentKind ?? FiscalDocumentKind.Receipt);
            return completed.Success
                ? new FiscalIssueResult
                {
                    Success = true,
                    Mark = completed.Mark ?? resent.Mark,
                    QrCode = completed.QrCode ?? resent.QrCode,
                    SeriesName = completed.SeriesName ?? order.InvoiceSeriesName,
                    InvoiceNumber = completed.InvoiceNumber ?? order.InvoiceNumber,
                    AlreadyPrinted = order.ParochosOfflineReceiptPrintedAtUtc.HasValue
                }
                : new FiscalIssueResult
                {
                    Success = false,
                    Mark = resent.Mark,
                    QrCode = resent.QrCode,
                    SeriesName = order.InvoiceSeriesName,
                    InvoiceNumber = order.InvoiceNumber,
                    ErrorMessage = completed.ErrorMessage ?? "Το παραστατικό εκδόθηκε, αλλά εκκρεμεί η ολοκλήρωση της αντιστοίχισης.",
                    RequiresRecovery = true
                };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Αποτυχία ασφαλούς επαναποστολής Parochos για {ExternalSystemId}", externalSystemId);
            order.ParochosTransmissionState = wasOfflineIssued ? "OfflineIssued" : "Pending";
            order.FiscalStatus = FiscalStatus.Pending;
            order.FiscalErrorMessage = "Η επαναποστολή δεν επιβεβαιώθηκε. Θα γίνει ξανά έλεγχος στον Parochos πριν από νέα προσπάθεια.";
            await db.SaveChangesAsync();
            return new FiscalIssueResult
            {
                Success = wasOfflineIssued,
                OfflineIssued = wasOfflineIssued,
                AlreadyPrinted = order.ParochosOfflineReceiptPrintedAtUtc.HasValue,
                ErrorMessage = order.FiscalErrorMessage,
                RequiresRecovery = true
            };
        }
    }

    private async Task<FiscalIssueResult> RecoverOrderFormTransmissionAsync(
        AppDbContext db,
        FiscalOrderDocument document,
        CompanySettings settings,
        bool allowResendWhenNotFound)
    {
        var externalSystemId = document.ExternalSystemId;
        var wasOfflineIssued = document.ParochosTransmissionState == "OfflineIssued";
        var status = await LookupExactAsync(settings, externalSystemId, document.ProcessId, document.Uid);

        if (status.Success && status.Mark is > 0)
            return await CompleteOrderFormTransmissionAsync(db, document, settings, status);

        if (!status.IsNotFound)
        {
            if (document.ParochosTransmissionState is not ("Ready" or "NeedsReview"))
                document.ParochosTransmissionState = wasOfflineIssued ? "OfflineIssued" : "Pending";
            if (status.ErrorCode == "00010022") document.ParochosTransmissionState = "NeedsReview";
            document.FiscalErrorMessage = status.ErrorCode == "00010022" ? "Ο αριθμός υπάρχει, αλλά δεν ταυτοποιήθηκε το ίδιο δελτίο. Δεν έγινε νέα αποστολή." : wasOfflineIssued
                ? GetOfflineTransmissionMessage(document.ParochosTransmissionFailure)
                : "Δεν επιβεβαιώθηκε ακόμη η κατάσταση του δελτίου παραγγελίας στον πάροχο. Δεν έγινε επαναποστολή για αποφυγή διπλής έκδοσης.";
            await db.SaveChangesAsync();
            return new FiscalIssueResult
            {
                Success = wasOfflineIssued,
                OfflineIssued = wasOfflineIssued,
                TransmissionFailure = document.ParochosTransmissionFailure,
                ErrorMessage = document.FiscalErrorMessage,
                RequiresRecovery = true,
                SeriesName = document.SeriesName,
                InvoiceNumber = document.Number
            };
        }

        if (!allowResendWhenNotFound && document.ParochosTransmissionFailure == 2)
            return new FiscalIssueResult { Success = true, OfflineIssued = true, TransmissionFailure = 2, RequiresRecovery = true, ErrorMessage = "Αναμένεται επιβεβαίωση από τον πάροχο." };
        if (!allowResendWhenNotFound)
        {
            document.ParochosTransmissionState = "NeedsReview";
            document.FiscalErrorMessage = "Το δελτίο παραγγελίας δεν βρέθηκε στον πάροχο και χρειάζεται έλεγχο πριν από νέα αποστολή.";
            await db.SaveChangesAsync();
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = document.FiscalErrorMessage,
                RequiresRecovery = true,
                SeriesName = document.SeriesName,
                InvoiceNumber = document.Number
            };
        }

        try
        {
            using var storedPayload = JsonDocument.Parse(document.ParochosInvoicePayloadJson!);
            var exactSource = PrepareRetryPayload(
                storedPayload.RootElement,
                externalSystemId,
                document.ParochosTransmissionAttemptedAtUtc,
                wasOfflineIssued ? document.ParochosTransmissionFailure ?? 1 : null);
            document.ParochosInvoicePayloadJson = JsonSerializer.Serialize(JsonNode.Parse(exactSource.GetRawText()));
            document.ParochosTransmissionState = "Sending";
            document.ParochosTransmissionAttemptedAtUtc = DateTime.UtcNow;
            document.FiscalErrorMessage = null;
            await db.SaveChangesAsync();

            var resent = await _parochos.SendAndWaitAsync(settings, exactSource, externalSystemId);
            if (!resent.Success && resent.ErrorCode == "00010022")
            {
                var duplicateRecovery = await _parochos.GetDocumentByExternalSystemIdAsync(settings, externalSystemId);
                if (duplicateRecovery.Success && duplicateRecovery.Mark is > 0)
                {
                    resent = duplicateRecovery;
                }
                else
                {
                    document.ParochosTransmissionState = "NeedsReview";
                    document.FiscalErrorMessage =
                        $"Ο πάροχος αναφέρει ότι υπάρχει ήδη το δελτίο {document.SeriesName} {document.Number}, " +
                        "αλλά δεν επέστρεψε MARK για το ίδιο αναγνωριστικό. Η επαναποστολή σταμάτησε για αποφυγή διπλής έκδοσης.";
                    await db.SaveChangesAsync();
                    return new FiscalIssueResult
                    {
                        Success = false,
                        ErrorMessage = document.FiscalErrorMessage,
                        RequiresRecovery = true,
                        SeriesName = document.SeriesName,
                        InvoiceNumber = document.Number
                    };
                }
            }

            if (resent.Success && resent.Mark is > 0)
                return await CompleteOrderFormTransmissionAsync(db, document, settings, resent);

            var transmissionFailure = DetermineTransmissionFailure(resent);
            if (transmissionFailure is 1 or 2)
            {
                var offlineSource = PrepareRetryPayload(
                    exactSource,
                    externalSystemId,
                    document.ParochosTransmissionAttemptedAtUtc,
                    transmissionFailure);
                document.ParochosInvoicePayloadJson = JsonSerializer.Serialize(JsonNode.Parse(offlineSource.GetRawText()));
                document.ParochosTransmissionState = "OfflineIssued";
                document.ParochosTransmissionFailure = transmissionFailure;
                document.ParochosTransmissionAttemptedAtUtc = DateTime.UtcNow;
                document.FiscalErrorMessage = GetOfflineTransmissionMessage(transmissionFailure);
                await db.SaveChangesAsync();
                return new FiscalIssueResult
                {
                    Success = true,
                    OfflineIssued = true,
                    RequiresRecovery = true,
                    TransmissionFailure = transmissionFailure,
                    ErrorMessage = document.FiscalErrorMessage,
                    SeriesName = document.SeriesName,
                    InvoiceNumber = document.Number
                };
            }

            document.ParochosTransmissionState = "NeedsReview";
            document.FiscalErrorMessage = "Δεν επιβεβαιώθηκε η έκδοση του δελτίου παραγγελίας. Χρειάζεται έλεγχος στον πάροχο πριν από νέα αποστολή.";
            await db.SaveChangesAsync();
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = document.FiscalErrorMessage,
                RequiresRecovery = true,
                SeriesName = document.SeriesName,
                InvoiceNumber = document.Number
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Αποτυχία ασφαλούς επαναποστολής Order Form για {ExternalSystemId}", externalSystemId);
            document.ParochosTransmissionState = wasOfflineIssued ? "OfflineIssued" : "Pending";
            document.FiscalErrorMessage = wasOfflineIssued
                ? GetOfflineTransmissionMessage(document.ParochosTransmissionFailure)
                : "Δεν επιβεβαιώθηκε η επικοινωνία με τον πάροχο. Το δελτίο παραγγελίας παραμένει αποθηκευμένο για ασφαλή επανέλεγχο.";
            await db.SaveChangesAsync();
            return new FiscalIssueResult
            {
                Success = wasOfflineIssued,
                OfflineIssued = wasOfflineIssued,
                TransmissionFailure = document.ParochosTransmissionFailure,
                ErrorMessage = document.FiscalErrorMessage,
                RequiresRecovery = true,
                SeriesName = document.SeriesName,
                InvoiceNumber = document.Number
            };
        }
    }

    private static void MarkCancelled(IEnumerable<OrderItem> items, string? reason)
    {
        foreach (var i in items.Where(i => !i.IsCancelled))
        {
            i.IsCancelled = true; i.CancelledAt = DateTime.Now; i.CancelReason = reason ?? "Ακύρωση είδους";
        }
    }

    private async Task<FiscalIssueResult> CompleteOrderFormTransmissionAsync(
        AppDbContext db,
        FiscalOrderDocument document,
        CompanySettings settings,
        ParochosSendResult result)
    {
        if (result.Mark is not > 0)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Ο πάροχος δεν επέστρεψε MARK για το δελτίο παραγγελίας.", RequiresRecovery = true };

        if (document.IsCancellation && document.CancellationSourceId.HasValue)
            MarkCancelled(await db.OrderItems.Where(i => db.FiscalOrderDocumentItems.Any(link => link.FiscalOrderDocumentId == document.Id && link.OrderItemId == i.Id)).ToListAsync(), null);
        document.Mark = result.Mark;
        document.QrCode = result.QrCode;
        document.Uid = result.Uid;
        document.ProcessId = result.ProcessId ?? document.ProcessId;
        document.ParochosTransmissionState = "Issued";
        document.ParochosTransmissionFailure = null;
        document.FiscalErrorMessage = null;
        await db.SaveChangesAsync();

        var order = document.Order ?? await db.Orders
            .Include(item => item.RestaurantTable)
            .FirstOrDefaultAsync(item => item.Id == document.OrderId);
        if (!string.IsNullOrWhiteSpace(document.ProcessId) && order is not null)
        {
            var pointOfService = document.ParochosInvoicePayloadJson is string saved ?
                JsonNode.Parse(saved)?["invoice"]?["invoiceHeader"]?["PointOfService"]?.GetValue<string>() : null;
            pointOfService ??= await _configuration.ResolvePointOfServiceAsync(db, order);
            if (!string.IsNullOrWhiteSpace(pointOfService))
            {
                var assign = await _parochos.AssignDocumentAsync(
                    settings,
                    pointOfService,
                    document.ProcessId,
                    document.ExternalSystemId);
                if (!assign.Success && assign.ErrorCode != "00130002")
                    _logger.LogWarning("Order Form {ExternalSystemId} εκδόθηκε αλλά AssignDocument απέτυχε: {Error}", document.ExternalSystemId, assign.ErrorMessage);
            }
        }

        if (document.CancellationSourceId.HasValue && order?.Status == OrderStatus.Closed &&
            order.ParochosMark is not > 0 &&
            !await db.OrderItems.AnyAsync(i => i.OrderId == order.Id && !i.IsCancelled) &&
            !await db.FiscalOrderDocuments.AnyAsync(f => f.OrderId == order.Id && f.Mark == null))
        {
            // All cancellations are now confirmed. The close routine also checks for other
            // active orders, so an old cancellation cannot close a newly occupied table.
            var close = await _cancellations.CloseEmptyOrderAsync(db, order);
            if (!close.Success) _logger.LogWarning("Could not close empty point for {OrderId}: {Error}", order.Id, close.ErrorMessage);
        }

        // The final receipt may have been issued while this 8.6 was waiting
        // offline. Link the recovered Order Form now; this is a correlation of
        // two existing MARKs, never a second document issue.
        if (order?.FiscalStatus == FiscalStatus.Issued && order.ParochosMark is > 0 && await ScopedForms(db,order).AnyAsync(f=>f.Id==document.Id))
        {
            var connect = await _parochos.ConnectDocumentsAsync(
                settings,
                document.ProcessId,
                document.ExternalSystemId,
                new[] { order.ParochosMark.Value });
            if (!connect.Success)
                _logger.LogWarning(
                    "Recovered Order Form {ExternalSystemId} could not yet be linked to receipt MARK {ReceiptMark}: {Error}",
                    document.ExternalSystemId,
                    order.ParochosMark.Value,
                    connect.ErrorMessage);
        }

        return new FiscalIssueResult
        {
            Success = true,
            OrderFormId = document.Id,
            Mark = document.Mark,
            QrCode = document.QrCode,
            SeriesName = document.SeriesName,
            InvoiceNumber = document.Number
        };
    }

    private static string GetOfflineTransmissionMessage(int? transmissionFailure) => transmissionFailure == 2
        ? "Το δελτίο παραγγελίας εκδόθηκε κανονικά. Ο πάροχος δεν επικοινωνεί προσωρινά με την ΑΑΔΕ· η διαβίβαση θα ολοκληρωθεί μόλις αποκατασταθεί η επικοινωνία."
        : "Το δελτίο παραγγελίας εκδόθηκε κανονικά. Δεν υπάρχει προσωρινά επικοινωνία με τον πάροχο· η διαβίβαση θα ολοκληρωθεί μόλις αποκατασταθεί η σύνδεση.";

    private static JsonElement PrepareRetryPayload(
        JsonElement storedSource,
        string externalSystemId,
        DateTime? lastAttemptAtUtc,
        int? transmissionFailure = null)
    {
        var source = JsonNode.Parse(storedSource.GetRawText());
        if (source?["invoice"] is not JsonObject invoice)
            return storedSource.Clone();

        var issueDateText = invoice["invoiceHeader"]?["issueDate"]?.GetValue<string>();
        var issueDateUtc = ParseIssueDateUtc(issueDateText);
        var olderThan24Hours = issueDateUtc.HasValue
            ? DateTimeOffset.UtcNow - issueDateUtc.Value >= TimeSpan.FromHours(24)
            : lastAttemptAtUtc.HasValue && DateTime.UtcNow - lastAttemptAtUtc.Value >= TimeSpan.FromHours(24);

        // Never invent a delayed process code for a timely retry. If it was
        // already present or the retry is late, it must always equal the exact
        // ExternalSystemId of this same document.
        if (olderThan24Hours || invoice["delayedProcessCode"] is not null)
            invoice["delayedProcessCode"] = externalSystemId;

        // Legacy offline documents did not persist their original outage reason.
        // They are necessarily entity-to-provider failures (Failure_1).
        if (transmissionFailure is 1 or 2)
            invoice["transmissionFailure"] = transmissionFailure;

        using var preparedSource = JsonDocument.Parse(source.ToJsonString());
        return preparedSource.RootElement.Clone();
    }

    private static DateTimeOffset? ParseIssueDateUtc(string? issueDateText)
    {
        if (string.IsNullOrWhiteSpace(issueDateText))
            return null;

        var hasExplicitOffset = issueDateText.EndsWith('Z') ||
            issueDateText.EndsWith('z') ||
            (issueDateText.Length >= 6 && issueDateText[^6] is '+' or '-');
        if (hasExplicitOffset && DateTimeOffset.TryParse(
                issueDateText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var offsetTimestamp))
            return offsetTimestamp;

        // FiscalPayloadBuilder intentionally emits Greek local wall time without
        // a suffix, so interpret such timestamps in the Athens time zone.
        if (!DateTime.TryParse(issueDateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localTimestamp))
            return null;

        var zoneId = OperatingSystem.IsWindows() ? "GTB Standard Time" : "Europe/Athens";
        var athens = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var greekLocalTime = DateTime.SpecifyKind(localTimestamp, DateTimeKind.Unspecified);
        return new DateTimeOffset(greekLocalTime, athens.GetUtcOffset(greekLocalTime)).ToUniversalTime();
    }

    public async Task<FiscalIssueResult> IssueAsync(AppDbContext db, Order order, FiscalDocumentKind kind, string? cardTransactionId=null)
    {
        var gate=ReceiptGates[(uint)order.Id % ReceiptGates.Length];
        await gate.WaitAsync();
        try {
            // Do not replace an operator's unsaved checkout details on a fresh sale.
            // For an already persisted submission the DB, not a stale UI copy, owns identity.
            if(await db.Orders.AsNoTracking().AnyAsync(o=>o.Id==order.Id && (o.ParochosMark>0 ||
                o.ParochosInvoicePayloadJson!=null && o.ParochosTransmissionState!="Rejected")))
                await db.Entry(order).ReloadAsync();
            return await IssueReceiptCoreAsync(db,order,kind,cardTransactionId);
        } finally { gate.Release(); }
    }

    private async Task<FiscalIssueResult> IssueReceiptCoreAsync(
        AppDbContext db,
        Order order,
        FiscalDocumentKind kind,
        string? cardTransactionId = null)
    {
        if (await db.HasPendingTableTransferAsync(order.Id))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Υπάρχει εκκρεμής μεταφορά τραπεζιού. Ολοκλήρωσε τον επανέλεγχο πριν από φορολογική ενέργεια ή πληρωμή." };

        var settings = await _configuration.GetSettingsAsync(db);
        if (settings is { UseParochosProvider: false })
        {
            var localItems = await db.OrderItems.Where(i => i.OrderId == order.Id && !i.IsCancelled && i.Quantity > 0).ToListAsync();
            if (localItems.Count == 0)
                return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν υπάρχουν ενεργά είδη για εξόφληση." };
            var localTotals = FiscalPayloadBuilder.CalculateTotals(localItems);
            var localPayment = new OrderPayment
            {
                OrderId = order.Id, Amount = localTotals.Gross, PaymentMethod = order.PaymentMethod,
                DocumentKind = kind, FiscalStatus = FiscalStatus.PaidLocally,
                CardTransactionId = cardTransactionId ?? order.CardTransactionId
            };
            foreach (var item in localItems)
                localPayment.Items.Add(new OrderPaymentItem { OrderItemId = item.Id, Quantity = item.Quantity });
            db.OrderPayments.Add(localPayment);
            return new FiscalIssueResult { Success = true, ProviderUsed = false };
        }
        if (!_configuration.ValidateSettings(settings, out var validationError))
            return new FiscalIssueResult { Success = false, ErrorMessage = validationError };

        if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };

        if (order.ParochosOutboxArchived) return new FiscalIssueResult { ErrorMessage = "Το παραστατικό βρίσκεται στο αρχείο καθαρισμού. Επανέφερέ το για έλεγχο." };

        // The request body and externalSystemId were committed before the first
        // network call. Resolve any uncertain earlier attempt before composing
        // or submitting another invoice.
        if ((order.ParochosTransmissionState is "Pending" or "Sending" or "NeedsReview" or "OfflineIssued") &&
            !string.IsNullOrWhiteSpace(order.ParochosInvoicePayloadJson))
            return await RecoverTransmissionAsync(db, order, settings!, allowResendWhenNotFound: order.ParochosTransmissionState != "NeedsReview" && order.ParochosTransmissionFailure != 2);

        var staffError = await FiscalConfigurationService.ValidateStaffConsumptionAsync(db, order, kind);
        if (staffError is not null) return new FiscalIssueResult { Success = false, ErrorMessage = staffError };

        var series = await _configuration.GetOrCreateSeriesAsync(db, FiscalConfigurationService.SeriesKind(order, kind));
        var seriesName = order.InvoiceSeriesName ?? series.SeriesName;
        var externalSystemId = order.ParochosTransmissionState == "Rejected"
            ? $"order-{order.Id}-{Guid.NewGuid():N}"
            : order.ParochosExternalId ?? $"order-{order.Id}-{Guid.NewGuid():N}";
        var totals = FiscalPayloadBuilder.CalculateTotals(order);
        var invoiceType = FiscalConfigurationService.InvoiceType(order, kind, settings!);
        if (order.PaymentMethod is PaymentMethod.Card or PaymentMethod.Iris)
        {
            var signature = order.ParochosPaymentSignature;
            var transactionId = cardTransactionId ?? order.CardTransactionId;
            var hasEpayWebEcr = await db.BankPosTerminalSettings.AnyAsync(x => x.IsEnabled &&
                (x.Provider.Contains("ePay") || x.Provider.Contains("Euronet") || x.Protocol.Contains("Common WebECR")));
            if (string.IsNullOrWhiteSpace(transactionId))
                return new FiscalIssueResult { Success = false, ErrorMessage = "Λείπει το Transaction ID που επέστρεψε το POS." };
            if (!hasEpayWebEcr && string.IsNullOrWhiteSpace(signature))
                return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν υπάρχει payment signature. Πάτησε ξανά προετοιμασία πληρωμής κάρτας." };
            if (!hasEpayWebEcr && (string.IsNullOrWhiteSpace(order.CardTerminalId) || string.IsNullOrWhiteSpace(order.CardNspCode)))
                return new FiscalIssueResult { Success = false, ErrorMessage = "Λείπουν Terminal ID / NSP Code της κάρτας." };
            order.CardTransactionId = transactionId.Trim();
        }

        var priorOrderForms = await ScopedForms(db,order)
            .Where(f => f.Mark != null)
            .ToListAsync();
        // A Point of Service belongs to the 8.6 order-form lifecycle. A direct
        // receipt (for example Take Away) has no order form and must not send or
        // mutate any PointOfService state at Parochos.
        var pointOfService = priorOrderForms.Count > 0
            ? await _configuration.ResolvePointOfServiceAsync(db, order)
            : null;

        // ------------------------------------------------------------
        // RECOVERY:
        // If the final fiscal document was already issued, do NOT send
        // another receipt. Only finish the missing Order Form correlation
        // and close the Point of Service.
        // ------------------------------------------------------------
        if (order.FiscalStatus == FiscalStatus.Issued &&
            order.ParochosMark is not null)
        {
            // A receipt without order forms is already complete. Do not attempt
            // discount-return recovery, POS assignment, or POS close, and never
            // issue the receipt again when the user retries after a UI/network error.
            if (priorOrderForms.Count == 0)
            {
                return new FiscalIssueResult
                {
                    Success = true,
                    Mark = order.ParochosMark,
                    QrCode = order.ParochosQrCode,
                    SeriesName = order.InvoiceSeriesName,
                    InvoiceNumber = order.InvoiceNumber
                };
            }

            // Older releases could issue the discounted receipt without first
            // balancing the already-sent 8.6 order forms. Repair that sequence
            // with a correlated 8.6/recType=7 return; never reissue the receipt.
            var recoveryItemIds = await db.OrderItems
                .Where(i => i.OrderId == order.Id && !i.IsCancelled)
                .Select(i => i.Id)
                .ToListAsync();
            var discountReturn = await _cancellations.ApplyDiscountWithReturnOrderFormsAsync(db, order, recoveryItemIds);
            if (!discountReturn.Success)
                return new FiscalIssueResult
                {
                    Success = false,
                    ErrorMessage = "Η απόδειξη έχει ήδη εκδοθεί (ΜΑΡΚ: " + order.ParochosMark.Value +
                        "), αλλά δεν ολοκληρώθηκε το δελτίο επιστροφής της έκπτωσης: " + discountReturn.ErrorMessage +
                        " Δεν θα ξαναεκδοθεί απόδειξη."
                };

            priorOrderForms = await ScopedForms(db,order)
                .Where(f => f.Mark != null)
                .ToListAsync();

            if (!string.IsNullOrWhiteSpace(pointOfService) &&
                !string.IsNullOrWhiteSpace(order.ParochosProcessId))
            {
                var assignExisting = await _parochos.AssignDocumentAsync(
                    settings,
                    pointOfService,
                    order.ParochosProcessId!,
                    order.ParochosExternalId ?? $"order-{order.Id}");

                if (!assignExisting.Success && assignExisting.ErrorCode != "00130002")
                {
                    _logger.LogWarning(
                        "Recovery AssignDocument απέτυχε για {ExternalSystemId}: {Error}",
                        order.ParochosExternalId,
                        assignExisting.ErrorMessage);

                    return new FiscalIssueResult
                    {
                        Success = false,
                        ErrorMessage =
                            "Η απόδειξη έχει ήδη εκδοθεί (ΜΑΡΚ: " +
                            order.ParochosMark.Value +
                            "), αλλά δεν μπόρεσε να αντιστοιχιστεί στο τραπέζι στον Parochos: " +
                            assignExisting.ErrorMessage
                    };
                }

                if (!assignExisting.Success && assignExisting.ErrorCode == "00130002")
                {
                    _logger.LogInformation(
                        "Recovery AssignDocument: το παραστατικό {ExternalSystemId} είναι ήδη αντιστοιχισμένο στο PointOfService {PointOfService}; συνεχίζουμε με το κλείσιμο.",
                        order.ParochosExternalId,
                        pointOfService);
                }
            }

            var allFormsConnected = true;
            var recoveryConnectErrors = new List<string>();

            foreach (var form in priorOrderForms)
            {
                var connect = await _parochos.ConnectDocumentsAsync(
                    settings,
                    form.ProcessId,
                    form.ExternalSystemId,
                    new[] { order.ParochosMark.Value });

                if (!connect.Success)
                {
                    allFormsConnected = false;
                    recoveryConnectErrors.Add($"Δελτίο {form.SeriesName} {form.Number}: {connect.ErrorMessage ?? "άγνωστο σφάλμα"}" + (string.IsNullOrWhiteSpace(connect.ErrorCode) ? "" : $" (κωδικός {connect.ErrorCode})"));

                    _logger.LogWarning(
                        "Recovery ConnectDocuments απέτυχε για Order Form {ExternalSystemId}: Status failure, ConnectedCount={ConnectedCount}, ErrorCode={ErrorCode}, Error={Error}",
                        form.ExternalSystemId,
                        connect.ConnectedCount,
                        connect.ErrorCode,
                        connect.ErrorMessage);
                }
                else if (connect.ConnectedCount == 0)
                {
                    _logger.LogInformation(
                        "Recovery ConnectDocuments ολοκληρώθηκε χωρίς νέες συνδέσεις για {ExternalSystemId}. Η απόδειξη περιέχει correlatedInvoices από την αρχική αποστολή. ReceiptMark={ReceiptMark}",
                        form.ExternalSystemId,
                        order.ParochosMark.Value);
                }
            }

            if (!allFormsConnected)
            {
                return new FiscalIssueResult
                {
                    Success = false,
                    ErrorMessage =
                        "Η απόδειξη έχει ήδη εκδοθεί (ΜΑΡΚ: " +
                        order.ParochosMark.Value +
                        "), αλλά δεν ολοκληρώθηκε η σύνδεση όλων των Δελτίων Παραγγελίας. " +
                        "Δεν θα ξαναεκδοθεί απόδειξη. " + string.Join("; ", recoveryConnectErrors)
                };
            }

            if (!string.IsNullOrWhiteSpace(pointOfService) &&
                settings.IssueOrderFormsToAade)
            {
                var closeExisting = await _parochos.ClosePointOfServiceAsync(
                    settings,
                    pointOfService);

                if (!closeExisting.Success)
                {
                    var balanceDetails = await GetOpenBalanceDiagnosticAsync(settings, pointOfService);
                    return new FiscalIssueResult
                    {
                        Success = false,
                        ErrorMessage =
                            "Η απόδειξη έχει ήδη εκδοθεί και τα Δελτία Παραγγελίας έχουν συνδεθεί, " +
                            "αλλά απέτυχε το κλείσιμο του τραπεζιού στον Parochos: " +
                            closeExisting.ErrorMessage + balanceDetails
                    };
                }
            }

            return new FiscalIssueResult
            {
                Success = true,
                Mark = order.ParochosMark,
                QrCode = order.ParochosQrCode
            };
        }

        await using var reservationTx = await db.Database.BeginTransactionAsync();
        var saved = await db.Orders.AsNoTracking().Where(o=>o.Id==order.Id)
            .Select(o=>new { o.ParochosTransmissionState,o.ParochosMark }).SingleAsync();
        if (saved.ParochosMark > 0 || saved.ParochosTransmissionState is "Sending" or "NeedsReview" or "OfflineIssued" or "Pending")
            return new FiscalIssueResult { RequiresRecovery=true, ErrorMessage="Υπάρχει ήδη έκδοση για αυτή την παραγγελία. Έλεγξε τις εκκρεμότητες." };
        var pinned = order.PaymentMethod is PaymentMethod.Card or PaymentMethod.Iris || order.AadePreloadedStatus=="Pending"
            ? order.InvoiceNumber : null;
        var invoiceNumber = await ReceiptNumberAllocator.ReserveAsync(db,seriesName,externalSystemId,order.Id,pinned);
        var itemIds=order.Items.Where(i=>!i.IsCancelled).Select(i=>i.Id).Distinct().ToArray();
        var capturedForms=await db.FiscalOrderDocuments.Where(f=>f.OrderId==order.Id && !f.IsCancelled &&
            (!f.Items.Any() || f.IsCancellation || f.IsDiscountReturn || f.Items.Any(i=>itemIds.Contains(i.OrderItemId))))
            .ToListAsync();
        var batchIds=await db.OrderPrintBatches.Where(b=>b.OrderId==order.Id && b.DispatchNotificationPending &&
            b.Items.Any(i=>itemIds.Contains(i.Id))).Select(b=>b.Id).ToArrayAsync();
        order.ReceiptScopeJson=JsonSerializer.Serialize(new ReceiptScope(capturedForms.Select(f=>f.Id).ToArray(),
            itemIds,batchIds,await _configuration.ResolvePointOfServiceAsync(db,order)));
        priorOrderForms=capturedForms.Where(f=>f.Mark>0).ToList();
        pointOfService=priorOrderForms.Count>0 ? Scope(order)!.Table : null;

        var invoiceDict = FiscalPayloadBuilder.BuildInvoice(
            settings!, order, kind, seriesName, invoiceNumber, totals, invoiceType, pointOfService,
            closePointOfService: false,
            orderForms: priorOrderForms);

        var source = new Dictionary<string, object?> { ["invoice"] = invoiceDict };

        // Persist the exact payload before contacting Parochos. A process crash
        // or lost HTTP response must not lose the idempotency key or invoice body.
        order.ParochosExternalId = externalSystemId;
        order.DocumentKind = kind;
        order.InvoiceSeriesName = seriesName;
        order.InvoiceNumber = invoiceNumber;
        order.ParochosInvoicePayloadJson = JsonSerializer.Serialize(source);
        order.ParochosTransmissionState = "Sending";
        order.ParochosTransmissionAttemptedAtUtc = DateTime.UtcNow;
        order.FiscalStatus = FiscalStatus.Pending;
        await db.SaveChangesAsync();

        await reservationTx.CommitAsync();

        // An already allocated receipt identity is immutable, including cash sales.
        ParochosSendResult result = new();

        var pointOfServiceOpenRetryUsed = false;

        do
        {
            order.ParochosInvoicePayloadJson = JsonSerializer.Serialize(source);
            order.ParochosTransmissionState = "Sending";
            order.ParochosTransmissionAttemptedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            result = await _parochos.SendAndWaitAsync(settings, source, externalSystemId);

            if (result.Success)
                break;

            // 00010014 = the Point of Service is not currently available.
            // The final accounting document must not be reissued blindly. Instead,
            // open the table in Parochos once and retry the SAME accounting document.
            if (priorOrderForms.Count > 0 &&
                result.ErrorCode == "00010014" &&
                !pointOfServiceOpenRetryUsed &&
                !string.IsNullOrWhiteSpace(pointOfService))
            {
                pointOfServiceOpenRetryUsed = true;

                var openForFinal = await _parochos.OpenPointOfServiceAsync(
                    settings,
                    pointOfService);

                if (openForFinal.Success)
                {
                    _logger.LogInformation(
                        "Parochos PointOfService {PointOfService} ήταν μη διαθέσιμο. Άνοιξε επιτυχώς και γίνεται retry της ίδιας απόδειξης.",
                        pointOfService);

                    invoiceDict = FiscalPayloadBuilder.BuildInvoice(
                        settings!, order, kind, seriesName, invoiceNumber, totals, invoiceType, pointOfService,
                        closePointOfService: false,
                        orderForms: priorOrderForms,
                        openPointOfService: true);
                    source["invoice"] = invoiceDict;

                    order.ParochosInvoicePayloadJson = JsonSerializer.Serialize(source);
                    order.ParochosTransmissionState = "Sending";
                    order.ParochosTransmissionAttemptedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync();

                    result = await _parochos.SendAndWaitAsync(settings, source, externalSystemId);
                    if (result.Success)
                        break;
                }
                else
                {
                    _logger.LogWarning(
                        "Parochos OpenPointOfService απέτυχε για {PointOfService}: {Error}. Δεν θα γίνει τυφλό retry.",
                        pointOfService,
                        openForFinal.ErrorMessage);
                }
            }

        } while (false);

        if (result.Success && !string.IsNullOrWhiteSpace(result.ExternalSystemId) && result.ExternalSystemId != externalSystemId)
            result = new ParochosSendResult { ErrorCode="00010022", ErrorMessage="Ασυμφωνία ταυτότητας παραστατικού." };
        var requiresCardReceiptRecovery = false;
        string? cardReceiptRecoveryMessage = null;
        if (!result.Success && result.ErrorCode == "00010022")
        {
            // A duplicate AA can mean the previous /api/send succeeded but its
            // response was lost. Query the same externalSystemId before treating
            // it as a new failure. This never calls the POS or changes signed AA.
            var existingDocument = await LookupExactAsync(settings!, externalSystemId, order.ParochosProcessId, order.ParochosUid);
            if (existingDocument.Success && existingDocument.Mark is > 0)
            {
                _logger.LogInformation(
                    "Recovered already transmitted card receipt for {ExternalSystemId} with MARK {Mark}.",
                    externalSystemId, existingDocument.Mark);
                result = existingDocument;
            }
            else
            {
                // Keep the approved transaction's original signed AA intact, but
                // move the active series forward so the next payment starts at
                // the next unused number. The current transaction needs provider
                // reconciliation and must not be retried with a different AA.
                requiresCardReceiptRecovery = true;
                cardReceiptRecoveryMessage = $"Ο αριθμός {seriesName} {invoiceNumber} υπάρχει ήδη. Δεν ταυτοποιήθηκε η ίδια απόδειξη. Δεν έγινε νέα έκδοση.";
            }
        }

        if (!result.Success || result.Mark is not > 0)
        {
            var uncertain = result.Success || result.OutcomeUnknown || result.InProgress || requiresCardReceiptRecovery;
            var transmissionFailure = DetermineTransmissionFailure(result);
            if (!requiresCardReceiptRecovery && result.ErrorCode != "00010022" && !string.IsNullOrWhiteSpace(result.ProcessId))
                order.ParochosProcessId ??= result.ProcessId;
            if (!requiresCardReceiptRecovery && transmissionFailure is not null)
            {
                // A provider/network/AADE transport outage must not prevent the
                // cashier from printing a local provisional receipt. First look
                // up the idempotency key: if the provider already signed it,
                // continue through the normal issued-document path. If lookup is
                // unavailable or explicitly says not found, keep the exact
                // payload in the outbox and never create a second invoice.
                var lookup = await LookupExactAsync(settings!, externalSystemId, order.ParochosProcessId, order.ParochosUid);
                if (lookup.Success && lookup.Mark is > 0)
                {
                    result = lookup;
                }
                else
                {
                    // The receipt is issued locally now. Put the statutory
                    // failure value in the same immutable source that will be
                    // retried, always with this document's original ID.
                    _logger.LogWarning(
                        "Issued local receipt pending transmission. ExternalSystemId={ExternalSystemId}; TransmissionFailure={TransmissionFailure}; SendError={SendError}; LookupError={LookupError}",
                        externalSystemId,
                        transmissionFailure.Value,
                        result.ErrorMessage,
                        lookup.ErrorMessage);
                    invoiceDict["transmissionFailure"] = transmissionFailure.Value;
                    source["invoice"] = invoiceDict;
                    order.ParochosInvoicePayloadJson = JsonSerializer.Serialize(source);
                    order.ParochosTransmissionFailure = transmissionFailure.Value;
                    order.FiscalStatus = FiscalStatus.Pending;
                    order.ParochosTransmissionState = "OfflineIssued";
                    order.ParochosTransmissionAttemptedAtUtc = DateTime.UtcNow;
                    // Keep raw provider/network diagnostics in application logs.
                    // The outbox is visible to cashiers, so use a short actionable
                    // message instead of exposing a transport exception or API body.
                    order.FiscalErrorMessage = transmissionFailure.Value == 2
                        ? "Η απόδειξη εκδόθηκε κανονικά. Ο πάροχος δεν επικοινωνεί προσωρινά με την ΑΑΔΕ· η διαβίβαση θα ολοκληρωθεί μόλις αποκατασταθεί η επικοινωνία."
                        : "Η απόδειξη εκδόθηκε κανονικά. Δεν υπάρχει προσωρινά επικοινωνία με τον πάροχο· η διαβίβαση θα ολοκληρωθεί μόλις αποκατασταθεί η σύνδεση.";
                    // Number already committed before the network call.
                    await db.SaveChangesAsync();
                    return new FiscalIssueResult
                    {
                        Success = true,
                        OfflineIssued = true,
                        RequiresRecovery = true,
                        TransmissionFailure = transmissionFailure,
                        ErrorMessage = order.FiscalErrorMessage,
                        SeriesName = seriesName,
                        InvoiceNumber = invoiceNumber
                    };
                }
            }
        }

        if (!result.Success)
        {
            var uncertain = result.OutcomeUnknown || result.InProgress || requiresCardReceiptRecovery;
            order.FiscalStatus = uncertain ? FiscalStatus.Pending : FiscalStatus.Failed;
            order.ParochosTransmissionState = requiresCardReceiptRecovery ? "NeedsReview" : uncertain ? "Pending" : "Rejected";
            order.FiscalErrorMessage = cardReceiptRecoveryMessage ?? result.ErrorMessage;
            await db.SaveChangesAsync();
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = order.FiscalErrorMessage,
                ErrorCode = result.ErrorCode,
                NumberConflict = requiresCardReceiptRecovery,
                RequiresRecovery = requiresCardReceiptRecovery
            };
        }

        order.InvoiceNumber = invoiceNumber;
        order.FiscalStatus = FiscalStatus.Issued;
        order.ParochosProcessId = result.ProcessId;
        order.ParochosMark = result.Mark;
        order.ParochosUid = result.Uid;
        order.ParochosAuthenticationCode = result.AuthenticationCode;
        order.ParochosQrCode = result.QrCode;
        order.ParochosPdfUrl = result.PdfFileUrl;
        order.FiscalErrorMessage = null;
        order.ParochosTransmissionState = "Issued";

        // Do not write a stale series counter after an asynchronous send.
        await db.SaveChangesAsync();

        if (priorOrderForms.Count > 0 &&
            !string.IsNullOrWhiteSpace(pointOfService) &&
            !string.IsNullOrWhiteSpace(result.ProcessId))
        {
            var assign = await _parochos.AssignDocumentAsync(settings, pointOfService, result.ProcessId!, externalSystemId);
            if (!assign.Success && assign.ErrorCode != "00130002")
            {
                _logger.LogWarning("Parochos AssignDocument απέτυχε για {ExternalSystemId}: {Error}", externalSystemId, assign.ErrorMessage);

                return new FiscalIssueResult
                {
                    Success = false,
                    ErrorMessage =
                        "Η απόδειξη εκδόθηκε με ΜΑΡΚ " +
                        result.Mark +
                        ", αλλά δεν μπόρεσε να αντιστοιχιστεί στο τραπέζι στον Parochos: " +
                        assign.ErrorMessage +
                        ". Δεν θα ξαναεκδοθεί· η επόμενη προσπάθεια θα κάνει recovery."
                };
            }

            if (!assign.Success && assign.ErrorCode == "00130002")
            {
                _logger.LogInformation(
                    "Parochos AssignDocument: το παραστατικό {ExternalSystemId} είναι ήδη αντιστοιχισμένο στο PointOfService {PointOfService}; συνεχίζουμε.",
                    externalSystemId,
                    pointOfService);
            }

            _logger.LogInformation(
                "Parochos AssignDocument ολοκληρώθηκε για {ExternalSystemId} στο PointOfService {PointOfService}.",
                externalSystemId,
                pointOfService);
        }

        var finalAllFormsConnected = true;
        var finalConnectErrors = new List<string>();

        if (priorOrderForms.Any() && result.Mark is not null)
        {
            foreach (var form in priorOrderForms)
            {
                var connect = await _parochos.ConnectDocumentsAsync(
                    settings,
                    form.ProcessId,
                    form.ExternalSystemId,
                    new[] { result.Mark.Value });

                _logger.LogInformation(
                    "PAROCHOS ORDER FORM CORRELATION >>> OrderId={OrderId} | OrderFormMark={OrderFormMark} | OrderFormExternalSystemId={OrderFormExternalSystemId} | ReceiptMark={ReceiptMark} | Success={Success} | ConnectedCount={ConnectedCount} | ErrorCode={ErrorCode} | Error={Error}",
                    order.Id,
                    form.Mark,
                    form.ExternalSystemId,
                    result.Mark.Value,
                    connect.Success,
                    connect.ConnectedCount,
                    connect.ErrorCode,
                    connect.ErrorMessage);

                if (!connect.Success)
                {
                    finalAllFormsConnected = false;
                    finalConnectErrors.Add($"Δελτίο {form.SeriesName} {form.Number}: {connect.ErrorMessage ?? "άγνωστο σφάλμα"}" + (string.IsNullOrWhiteSpace(connect.ErrorCode) ? "" : $" (κωδικός {connect.ErrorCode})"));

                    _logger.LogWarning(
                        "Parochos ConnectDocuments απέτυχε για OrderForm {ExternalSystemId}: ConnectedCount={ConnectedCount}, ErrorCode={ErrorCode}, Error={Error}",
                        form.ExternalSystemId,
                        connect.ConnectedCount,
                        connect.ErrorCode,
                        connect.ErrorMessage);
                }
                else if (connect.ConnectedCount == 0)
                {
                    _logger.LogInformation(
                        "Parochos ConnectDocuments ολοκληρώθηκε χωρίς νέες συνδέσεις για {ExternalSystemId}. Το MARK {ReceiptMark} είχε ήδη σταλεί στο correlatedInvoices της απόδειξης.",
                        form.ExternalSystemId,
                        result.Mark.Value);
                }
            }
        }

        if (!finalAllFormsConnected)
        {
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage =
                    "Η απόδειξη εκδόθηκε με ΜΑΡΚ " +
                    result.Mark +
                    ", αλλά δεν ολοκληρώθηκε η σύνδεση όλων των Δελτίων Παραγγελίας. " +
                    "Δεν θα ξαναεκδοθεί από επανάληψη· η επόμενη προσπάθεια θα κάνει μόνο recovery. " + string.Join("; ", finalConnectErrors)
            };
        }

        // Κλείσιμο Point of Service ΜΟΝΟ αφού συνδεθούν όλα τα Order Forms
        // με το τελικό accounting document.
        if (!string.IsNullOrWhiteSpace(pointOfService) &&
            settings.IssueOrderFormsToAade)
        {
            var close = await _parochos.ClosePointOfServiceAsync(
                settings,
                pointOfService);

            if (!close.Success)
            {
                var balanceDetails = await GetOpenBalanceDiagnosticAsync(settings, pointOfService);
                return new FiscalIssueResult
                {
                    Success = false,
                    ErrorMessage =
                        "Η απόδειξη εκδόθηκε (ΜΑΡΚ: " +
                        result.Mark +
                        ") και τα Δελτία Παραγγελίας συνδέθηκαν, " +
                        "αλλά απέτυχε το κλείσιμο του τραπεζιού στον Parochos: " +
                        close.ErrorMessage + balanceDetails
                };
            }
        }

        return new FiscalIssueResult
        {
            Success = true,
            SeriesName = seriesName, InvoiceNumber = invoiceNumber,
            Mark = result.Mark,
            QrCode = result.QrCode
        };
    }

    private static int? DetermineTransmissionFailure(ParochosSendResult result)
    {
        if (result.ErrorCode == "00010032") return 2; // provider identifies a TA connection failure
        if (result.InProgress) return null;
        if (result.ErrorCategory == "NETWORK" || (result.ErrorCategory == "HTTP" && result.OutcomeUnknown)) return 1;
        return null; // credentials, validation and duplicate-number errors are not outages
    }

    public async Task<FiscalIssueResult> CancelSentOrderItemAsync(AppDbContext db, Order order, int orderItemId, string reason = "Ακύρωση είδους από τον σερβιτόρο", int? quantityToCancel = null)
    {
        var item = await db.OrderItems.Include(i => i.MenuItem).ThenInclude(m => m!.MenuCategory)
            .SingleOrDefaultAsync(i => i.Id == orderItemId && i.OrderId == order.Id);
        if (item is not null && quantityToCancel is int requestedQuantity &&
            (requestedQuantity < 1 || requestedQuantity > item.Quantity))
            return new FiscalIssueResult { ErrorMessage = $"Η ποσότητα ακύρωσης πρέπει να είναι από 1 έως {item.Quantity}." };
        var prior = await db.FiscalOrderDocuments.Include(d => d.Items)
            .Where(d => d.OrderId == order.Id && !d.IsCancelled && !d.IsDiscountReturn &&
                !d.IsCancellation && d.Items.Any(i => i.OrderItemId == orderItemId)).ToListAsync();
        if (item == null || prior.Count == 0)
            return await _cancellations.CancelSentOrderItemAsync(db, order, orderItemId, reason, quantityToCancel);
        if (item.IsCancelled) return new FiscalIssueResult { Success = true };
        if (prior.Count != 1) return new FiscalIssueResult { ErrorMessage = "Δεν ταυτοποιήθηκε ένα μοναδικό αρχικό δελτίο. Δεν έγινε ακύρωση." };
        if (quantityToCancel is int quantity && quantity < item.Quantity)
        {
            if (quantity < 1)
                return new FiscalIssueResult { ErrorMessage = "Η ποσότητα ακύρωσης πρέπει να είναι τουλάχιστον 1." };

            var cancelledLine = new OrderItem
            {
                OrderId = item.OrderId, MenuItemId = item.MenuItemId, MenuItem = item.MenuItem,
                OrderPrintBatchId = item.OrderPrintBatchId, Quantity = quantity, Notes = item.Notes,
                ExtrasPrice = item.ExtrasPrice, DiscountPercent = item.DiscountPercent,
                CreatedAt = item.CreatedAt, KitchenPrintUnconfirmed = item.KitchenPrintUnconfirmed,
                IsPrinted = item.IsPrinted, PrintedAt = item.PrintedAt
            };
            item.Quantity -= quantity;
            db.OrderItems.Add(cancelledLine);
            var partialResult = await CreateOrderFormAsync(db, order, new() { cancelledLine }, prior[0], reason);
            if (partialResult.Success || partialResult.RequiresRecovery)
            {
                cancelledLine.IsCancelled = true;
                cancelledLine.CancelledAt = DateTime.Now;
                cancelledLine.CancelReason = reason;
                await db.SaveChangesAsync();
                return new FiscalIssueResult
                {
                    Success = partialResult.Success, ErrorMessage = partialResult.ErrorMessage,
                    ErrorCode = partialResult.ErrorCode, ProviderUsed = partialResult.ProviderUsed,
                    RequiresRecovery = partialResult.RequiresRecovery, OfflineIssued = partialResult.OfflineIssued,
                    NumberConflict = partialResult.NumberConflict, AlreadyPrinted = partialResult.AlreadyPrinted,
                    OrderFormId = partialResult.OrderFormId, TransmissionFailure = partialResult.TransmissionFailure,
                    Mark = partialResult.Mark, QrCode = partialResult.QrCode,
                    SeriesName = partialResult.SeriesName, InvoiceNumber = partialResult.InvoiceNumber,
                    CancelledOrderItemId = cancelledLine.Id
                };
            }

            item.Quantity += quantity;
            db.OrderItems.Remove(cancelledLine);
            await db.SaveChangesAsync();
            return partialResult;
        }
        var existing = await db.FiscalOrderDocuments.SingleOrDefaultAsync(d => d.CancellationSourceId == prior[0].Id &&
            d.Items.Any(i => i.OrderItemId == orderItemId));
        if (existing != null)
            return await RetryPendingOrderFormTransmissionAsync(db, existing.Id);
        return await CreateOrderFormAsync(db, order, new() { item }, prior[0], reason);
    }

    public Task<FiscalIssueResult> ApplyDiscountWithReturnOrderFormsAsync(AppDbContext db, Order order, IReadOnlyCollection<int> orderItemIds) =>
        _cancellations.ApplyDiscountWithReturnOrderFormsAsync(db, order, orderItemIds);

    private async Task<string> GetOpenBalanceDiagnosticAsync(CompanySettings settings, string? pointOfService)
    {
        if (string.IsNullOrWhiteSpace(pointOfService)) return string.Empty;

        var details = await _parochos.ListPointOfServiceDetailsAsync(settings, pointOfService);
        if (!details.Success) return string.Empty;

        var documents = details.Details
            .Select(d => $"{d.InvoiceCode ?? d.InvoiceType ?? "Παραστατικό"}: €{(d.TransactionAmount ?? d.TotalAmount ?? 0m):0.00}")
            .ToArray();
        var documentSummary = documents.Length == 0 ? string.Empty : " Έγγραφα παρόχου: " + string.Join(", ", documents) + ".";
        _logger.LogWarning(
            "Parochos PointOfService balance after close failure: POS={PointOfService}, Balance={Balance}, Documents={Documents}",
            pointOfService,
            details.Balance,
            string.Join(" | ", documents));

        return details.Balance is decimal balance && balance != 0m
            ? $" Υπόλοιπο που αναφέρει τώρα ο πάροχος: €{balance:0.00}.{documentSummary}"
            : documentSummary;
    }

    public Task<FiscalIssueResult> CancelWholeOrderAsync(AppDbContext db, Order order, string reason = "Ακύρωση ολόκληρης παραγγελίας από τον σερβιτόρο", bool closePointOfService = true) =>
        _cancellations.CancelWholeOrderAsync(db, order, reason, closePointOfService);

    public Task<FiscalIssueResult> CloseEmptyOrderAsync(AppDbContext db, Order order) =>
        _cancellations.CloseEmptyOrderAsync(db, order);
    public Task<CardPaymentPreparation> PrepareSelectedCardPaymentAsync(
        AppDbContext db, Order order, IReadOnlyCollection<OrderItem> selectedItems,
        FiscalDocumentKind kind, BankPosTerminalSettings? selectedPos = null) =>
        _cardPayments.PrepareSelectedCardPaymentAsync(db, order, selectedItems, kind, selectedPos);
    public async Task<FiscalIssueResult> IssueSelectedItemsPaymentAsync(
        AppDbContext db,
        Order order,
        IReadOnlyCollection<OrderItem> selectedItems,
        PaymentMethod paymentMethod,
        FiscalDocumentKind kind,
        string? cardTransactionId = null,
        string? cardSignature = null,
        string? cardTerminalId = null,
        string? cardNspCode = null,
        string? paymentExternalSystemId = null,
        string? paymentSeriesName = null,
        long? paymentInvoiceNumber = null)
    {
        if (order.StaffConsumptionInvoiceType is "6.1" or "6.2")
            return new FiscalIssueResult { Success = false, ErrorMessage = "Η ιδιοκατανάλωση εκδίδεται για ολόκληρο το τραπέζι." };
        if (await db.HasPendingTableTransferAsync(order.Id))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Υπάρχει εκκρεμής μεταφορά τραπεζιού. Ολοκλήρωσε τον επανέλεγχο πριν από φορολογική ενέργεια ή πληρωμή." };

        var items = selectedItems.Where(i => !i.IsCancelled && i.Quantity > 0).ToList();
        if (items.Count == 0)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν επιλέχθηκαν ενεργά είδη για πληρωμή." };

        var settings = await _configuration.GetSettingsAsync(db);
        if (settings is { UseParochosProvider: false })
        {
            var localTotals = FiscalPayloadBuilder.CalculateTotals(items);
            var localPayment = new OrderPayment
            {
                OrderId = order.Id, Amount = localTotals.Gross, PaymentMethod = paymentMethod,
                DocumentKind = kind, FiscalStatus = FiscalStatus.PaidLocally,
                CardTransactionId = cardTransactionId, CardTerminalId = cardTerminalId,
                CardNspCode = cardNspCode, PaymentSignature = cardSignature
            };
            foreach (var item in items)
                localPayment.Items.Add(new OrderPaymentItem { OrderItemId = item.Id, Quantity = item.Quantity });
            db.OrderPayments.Add(localPayment);
            await db.SaveChangesAsync();
            return new FiscalIssueResult { Success = true, ProviderUsed = false };
        }
        if (!_configuration.ValidateSettings(settings, out var validationError))
            return new FiscalIssueResult { Success = false, ErrorMessage = validationError };

        if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };

        if ((paymentMethod is PaymentMethod.Card or PaymentMethod.Iris) &&
            (string.IsNullOrWhiteSpace(paymentExternalSystemId) ||
             string.IsNullOrWhiteSpace(paymentSeriesName) ||
             paymentInvoiceNumber is null or <= 0))
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Λείπουν τα στοιχεία της αρχικής υπογραφής Parochos (ExternalSystemId/σειρά/ΑΑ). Δεν στάλθηκε παραστατικό."
            };

        var series = await _configuration.GetOrCreateSeriesAsync(db, kind);
        var seriesName = paymentMethod is PaymentMethod.Card or PaymentMethod.Iris
            ? paymentSeriesName!.Trim()
            : series.SeriesName;
        var externalSystemId = paymentMethod is PaymentMethod.Card or PaymentMethod.Iris
            ? paymentExternalSystemId!.Trim()
            : $"payment-{order.Id}-{Guid.NewGuid():N}";
        var pinnedNumber = paymentMethod is PaymentMethod.Card or PaymentMethod.Iris ? paymentInvoiceNumber : null;
        var invoiceNumber = await ReceiptNumberAllocator.ReserveAsync(db,seriesName,externalSystemId,order.Id,pinnedNumber);
        var totals = FiscalPayloadBuilder.CalculateTotals(items);
        var pointOfService = await _configuration.ResolvePointOfServiceAsync(db, order);
        if (string.IsNullOrWhiteSpace(pointOfService))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκε το Point of Service του τραπεζιού." };

        var payment = new Dictionary<string, object?>
        {
            ["type"] = PaymentMethodInfo.GetMyDataCode(paymentMethod),
            ["amount"] = totals.Gross
        };
        if (paymentMethod is PaymentMethod.Card or PaymentMethod.Iris)
        {
            payment["signature"] = cardSignature;
            payment["transactionId"] = cardTransactionId;
            payment["terminalId"] = cardTerminalId;
            payment["tipAmount"] = 0m;
        }

        // Always correlate split receipts to the ORIGINAL Order Form for this table.
        // Do not create or select a new Order Form for each split payment.
        var originalOrderForm = await db.FiscalOrderDocuments
            .Where(f => f.OrderId == order.Id && f.Mark != null && !f.IsCancelled)
            .OrderBy(f => f.Id)
            .FirstOrDefaultAsync();
        if (originalOrderForm is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκε το αρχικό δελτίο παραγγελίας του τραπεζιού. Δεν εκδόθηκε απόδειξη ώστε να αποφευχθεί ασύνδετο παραστατικό." };

        // A merged party has original tickets from more than one table. Preserve
        // the established single-table split behavior, but correlate ALL transferred originals.
        var originalForms = await db.TableTransfers.AnyAsync(t => t.Stage == TableTransferStage.Completed && t.TargetOrderId == order.Id)
            ? await db.FiscalOrderDocuments.Where(f => f.OrderId == order.Id && f.Mark != null && !f.IsCancelled).OrderBy(f => f.Id).ToListAsync()
            : new List<FiscalOrderDocument> { originalOrderForm };

        var invoice = new Dictionary<string, object?>
        {
            ["issuer"] = FiscalPayloadBuilder.BuildIssuer(settings!),
            ["counterpart"] = kind == FiscalDocumentKind.Invoice ? FiscalPayloadBuilder.BuildInvoiceCounterpart(order) : FiscalPayloadBuilder.BuildRetailCounterpart(),
            ["invoiceHeader"] = new Dictionary<string, object?>
            {
                ["series"] = seriesName,
                ["aa"] = invoiceNumber,
                ["issueDate"] = FiscalPayloadBuilder.IssueTimestampGreekLocal(),
                ["currency"] = "EUR",
                ["invoiceType"] = kind == FiscalDocumentKind.Invoice ? settings.InvoiceInvoiceType : settings.ReceiptInvoiceType,
                ["invoiceTypeUbl"] = "380",
                ["selfPricing"] = false,
                ["fuelInvoice"] = false,
                ["OpenPointOfService"] = 0,
                ["ClosePointOfService"] = 0,
                ["PointOfService"] = pointOfService
            },
            ["invoiceDetails"] = FiscalPayloadBuilder.BuildLines(
                items,
                kind == FiscalDocumentKind.Invoice ? "E3_561_001" : "E3_561_003"),
            ["paymentMethods"] = new List<object?> { payment },
            ["invoiceSummary"] = new Dictionary<string, object?>
            {
                ["totalNetValue"] = totals.Net,
                ["totalVatAmount"] = totals.Vat,
                ["totalValue"] = totals.Gross
            },
            ["correlatedInvoices"] = originalForms.Select(f => (object?)new Dictionary<string, object?> { ["mark"] = f.Mark!.Value }).ToList()
        };

        _logger.LogInformation(
            "SPLIT RECEIPT CORRELATION >>> OrderId={OrderId}, OriginalOrderFormId={OrderFormId}, OrderFormMark={OrderFormMark}, ReceiptExternalSystemId={ReceiptExternalSystemId}, Amount={Amount}",
            order.Id, originalOrderForm.Id, originalOrderForm.Mark, externalSystemId, totals.Gross);

        var source = new Dictionary<string, object?> { ["invoice"] = invoice };
        var result = await _parochos.SendAndWaitAsync(settings, source, externalSystemId);
        if (!result.Success)
            return new FiscalIssueResult { Success = false, ErrorMessage = result.ErrorMessage };

        var assign = await _parochos.AssignDocumentAsync(settings, pointOfService, result.ProcessId ?? "", externalSystemId);
        if (!assign.Success && assign.ErrorCode != "00130002")
        {
            return new FiscalIssueResult
            {
                Success = false,
                Mark = result.Mark,
                QrCode = result.QrCode,
                ErrorMessage = "Το παραστατικό εκδόθηκε με MARK " + result.Mark + " αλλά απέτυχε το AssignDocument: " + assign.ErrorMessage
            };
        }

        // Connect every split receipt to the SAME original Order Form, not to a newly
        // generated form and not only to forms whose item IDs happen to match.
        var previousPaymentMarks = await db.OrderPayments
            .Where(p => p.OrderId == order.Id && p.FiscalStatus == FiscalStatus.Issued && p.Mark != null)
            .Select(p => p.Mark!.Value)
            .ToListAsync();
        var marksToConnect = previousPaymentMarks
            .Append(result.Mark!.Value)
            .Distinct()
            .ToList();

        foreach (var originalForm in originalForms)
        {
            var connect = await _parochos.ConnectDocumentsAsync(
                settings,
                originalForm.ProcessId,
                originalForm.ExternalSystemId,
                marksToConnect);

            _logger.LogInformation(
                "SPLIT RECEIPT CONNECT RESULT >>> OrderFormMark={OrderFormMark}, ReceiptMark={ReceiptMark}, SubmittedMarks={SubmittedMarks}, Success={Success}, ErrorCode={ErrorCode}, Error={Error}",
                originalForm.Mark, result.Mark, string.Join(",", marksToConnect), connect.Success, connect.ErrorCode, connect.ErrorMessage);

            if (!connect.Success)
            {
                return new FiscalIssueResult
                {
                    Success = false,
                    Mark = result.Mark,
                    QrCode = result.QrCode,
                    ErrorMessage = "Η απόδειξη εκδόθηκε (MARK " + result.Mark + "), αλλά η σύνδεση με το ΑΡΧΙΚΟ δελτίο παραγγελίας (MARK " + originalForm.Mark + ") απέτυχε: " + connect.ErrorMessage
                };
            }

        }

        var paymentRecord = new OrderPayment
        {
            OrderId = order.Id,
            Amount = totals.Gross,
            PaymentMethod = paymentMethod,
            DocumentKind = kind,
            FiscalStatus = FiscalStatus.Issued,
            ExternalSystemId = externalSystemId,
            ProcessId = result.ProcessId,
            Mark = result.Mark,
            Uid = result.Uid,
            QrCode = result.QrCode,
            AuthenticationCode = result.AuthenticationCode,
            PdfUrl = result.PdfFileUrl,
            SeriesName = seriesName,
            InvoiceNumber = invoiceNumber,
            CardTransactionId = cardTransactionId,
            CardTerminalId = cardTerminalId,
            CardNspCode = cardNspCode,
            PaymentSignature = cardSignature
        };

        foreach (var item in items)
        {
            paymentRecord.Items.Add(new OrderPaymentItem
            {
                OrderItemId = item.Id,
                Quantity = item.Quantity
            });
        }
        db.OrderPayments.Add(paymentRecord);
        // Reservation is durable before send.
        await db.SaveChangesAsync();

        var remaining = await db.OrderItems
            .Where(i => i.OrderId == order.Id && !i.IsCancelled)
            .ToListAsync();
        var paidQuantities = items
            .GroupBy(i => i.Id)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        var remainingItems = remaining
            .Where(i => !paidQuantities.TryGetValue(i.Id, out var paid) || paid < i.Quantity)
            .ToList();

        if (remainingItems.Count == 0)
        {
            var close = await _parochos.ClosePointOfServiceAsync(settings, pointOfService);
            if (!close.Success)
            {
                return new FiscalIssueResult
                {
                    Success = false,
                    Mark = result.Mark,
                    QrCode = result.QrCode,
                    ErrorMessage = "Το παραστατικό εκδόθηκε, αλλά απέτυχε το κλείσιμο του τραπεζιού: " + close.ErrorMessage
                };
            }
        }

        return new FiscalIssueResult { Success = true, Mark = result.Mark, QrCode = result.QrCode };
    }

    public Task<FiscalIssueResult> IssueOrderFormAsync(AppDbContext db, Order order, List<OrderItem> newlyAddedItems) =>
        CreateOrderFormAsync(db, order, newlyAddedItems, null, null);

    private async Task<FiscalIssueResult> CreateOrderFormAsync(AppDbContext db, Order order, List<OrderItem> newlyAddedItems,
        FiscalOrderDocument? original, string? reason)
    {
        if (await db.HasPendingTableTransferAsync(order.Id))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Υπάρχει εκκρεμής μεταφορά τραπεζιού. Ολοκλήρωσε τον επανέλεγχο πριν από φορολογική ενέργεια ή πληρωμή." };

        var settings = await _configuration.GetSettingsAsync(db);
        if (settings == null) return new FiscalIssueResult { ErrorMessage = "Δεν υπάρχουν φορολογικές ρυθμίσεις. Η παραγγελία διατηρείται για έκδοση." };
        if (!settings.UseParochosProvider || !settings.IssueOrderFormsToAade)
            return new FiscalIssueResult { Success = true, ProviderUsed = false };
        if (string.IsNullOrWhiteSpace(settings.OrderFormInvoiceType))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν έχει οριστεί ο κωδικός Order Form. Ο Parochos v3 ορίζει το 8.6 για Order Forms." };
        if (string.IsNullOrWhiteSpace(settings.VatNumber))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν έχει οριστεί ΑΦΜ επιχείρησης." };
        if (newlyAddedItems.Count == 0)
            return new FiscalIssueResult { Success = true };

        await using var reservation = await db.Database.BeginTransactionAsync();
        var series = await _configuration.GetOrCreateSeriesAsync(db, FiscalDocumentKind.OrderForm, settings.OrderFormSeriesName);
        await db.Entry(series).ReloadAsync();
        var thisNumber = series.NextNumber;
        var batchId = original == null ? newlyAddedItems[0].OrderPrintBatchId : null;
        var batch = batchId.HasValue ? await db.OrderPrintBatches.FindAsync(batchId.Value) : null;
        var isFirstOrderForm = !await db.FiscalOrderDocuments.AnyAsync(f => f.OrderId == order.Id && !f.IsCancelled);
        var externalSystemId = $"orderform-{order.Id}-{Guid.NewGuid():N}";
        var pointOfService = batch?.AcceptedTableName ?? (original?.ParochosInvoicePayloadJson is string originalJson
            ? JsonNode.Parse(originalJson)?["invoice"]?["invoiceHeader"]?["PointOfService"]?.GetValue<string>()
            : null) ?? await _configuration.ResolvePointOfServiceAsync(db, order);
        if (string.IsNullOrWhiteSpace(pointOfService))
        {
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Δεν βρέθηκε το Point of Service (τραπέζι) της παραγγελίας."
            };
        }

        // Open with the original document even when it is first delivered after an outage.
        var openPointOfService = isFirstOrderForm ? 1 : 0;

        var valuedItems = newlyAddedItems;
        if (original != null && newlyAddedItems.Count == 1 && newlyAddedItems[0].OrderPrintBatchId is int sourceBatchId)
        {
            var sourceBatch = await db.OrderPrintBatches.FindAsync(sourceBatchId);
            var captured = sourceBatch?.AcceptedLinesJson is string json ? JsonSerializer.Deserialize<AcceptedOrderLine[]>(json)?
                .SingleOrDefault(l => l.ItemId == newlyAddedItems[0].Id) : null;
            if (captured != null) valuedItems = new() { (captured with { Quantity = newlyAddedItems[0].Quantity,
                Discount = newlyAddedItems[0].DiscountPercent }).ForDocument(sourceBatch!, newlyAddedItems[0].MenuItem?.MenuCategory) };
        }
        var lines = FiscalPayloadBuilder.BuildLines(valuedItems);
        var totals = FiscalPayloadBuilder.CalculateTotals(valuedItems);
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
                    ["selfPricing"] = false,
                    ["fuelInvoice"] = false,
                    ["OpenPointOfService"] = openPointOfService,
                    ["ClosePointOfService"] = 0,
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
                }
            }
        };

        var invoiceBody = (Dictionary<string, object?>)source["invoice"]!;
        if (original != null)
        {
            foreach (var line in lines.OfType<Dictionary<string, object?>>()) line["recType"] = 7;
            invoiceBody["correlatedInvoices"] = original.Mark > 0 ? new[] { new { mark = original.Mark.Value } } : Array.Empty<object>();
        }

        // Persist before any provider call.  Kitchen dispatch has already been
        // committed, so a connectivity failure must leave a durable 8.6 outbox
        // row that represents exactly those submitted items.
        var document = new FiscalOrderDocument
        {
            OrderId = order.Id,
            Order = order,
            ExternalSystemId = externalSystemId,
            IsCancellation = original != null,
            CancellationSourceId = original?.Id,
            SeriesName = series.SeriesName,
            Number = thisNumber,
            ParochosInvoicePayloadJson = JsonSerializer.Serialize(source),
            ParochosTransmissionState = "Sending",
            ParochosTransmissionAttemptedAtUtc = DateTime.UtcNow
        };
        db.FiscalOrderDocuments.Add(document);
        if (batch is not null) batch.DispatchNotificationPending = false;

        // Κρατάμε ακριβώς ποια είδη περιείχε το συγκεκριμένο Order Form.
        // Αυτό είναι απαραίτητο για split payments και για μεταφορά επιλεγμένων ειδών.
        foreach (var item in newlyAddedItems.Where(i => !i.IsCancelled))
        {
            var documentItem = new FiscalOrderDocumentItem
            {
                OrderItemId = item.Id,
                Quantity = item.Quantity,
                DiscountPercentAtIssue = item.DiscountPercent
            };
            // New partial-cancellation rows need their temporary generated key
            // fixed up by EF. Existing/disconnected order items must stay as FK-only
            // links to avoid attaching a duplicate instance with the same key.
            if (db.Entry(item).State == EntityState.Added)
                documentItem.OrderItem = item;
            document.Items.Add(documentItem);
        }

        if (original != null && original.Mark is not > 0)
        {
            document.ParochosTransmissionState = "AwaitingOriginal";
            document.ParochosTransmissionFailure = original.ParochosTransmissionFailure;
            if (document.ParochosTransmissionFailure is 1 or 2)
                invoiceBody["transmissionFailure"] = document.ParochosTransmissionFailure;
            document.ParochosInvoicePayloadJson = JsonSerializer.Serialize(source);
            document.FiscalErrorMessage = "Η ακύρωση καταχωρήθηκε. Αναμένεται το ΜΑΡΚ του αρχικού δελτίου.";
            MarkCancelled(newlyAddedItems, reason);
        }
        // Reserve the number while the document is pending so a subsequent
        // kitchen dispatch cannot reuse the same 8.6 code.
        series.NextNumber = checked((int)Math.Max((long)series.NextNumber, thisNumber + 1L));
        await db.SaveChangesAsync();

        await reservation.CommitAsync();
        if (document.ParochosOutboxArchived) return new FiscalIssueResult { ErrorMessage = "Το παραστατικό βρίσκεται στο αρχείο καθαρισμού. Επανέφερέ το για έλεγχο." };

        if (document.ParochosTransmissionState == "AwaitingOriginal")
            return new FiscalIssueResult { OrderFormId = document.Id, Success = true, OfflineIssued = document.ParochosTransmissionFailure is 1 or 2,
                TransmissionFailure = document.ParochosTransmissionFailure, RequiresRecovery = true, ErrorMessage = document.FiscalErrorMessage,
                SeriesName = document.SeriesName, InvoiceNumber = document.Number };

        _logger.LogInformation(
            "PAROCHOS ORDER FORM SEND >>> ExternalSystemId={ExternalSystemId} | Series={Series} | AA={AA}",
            externalSystemId,
            series.SeriesName,
            thisNumber);
        LogFiscalIssueDate("ORDER_FORM", externalSystemId, source);

        var result = await _parochos.SendAndWaitAsync(settings, source, externalSystemId);
        if (result.Success && result.Mark is > 0)
            return await CompleteOrderFormTransmissionAsync(db, document, settings, result);

        // A duplicate code may be the delayed reply to this exact request. The
        // external-system ID is the only safe identity check; a matching AA by
        // itself must never be treated as the same Order Form.
        if (!result.Success && result.ErrorCode == "00010022")
        {
            var duplicateRecovery = await _parochos.GetDocumentByExternalSystemIdAsync(settings, externalSystemId);
            if (duplicateRecovery.Success && duplicateRecovery.Mark is > 0)
                return await CompleteOrderFormTransmissionAsync(db, document, settings, duplicateRecovery);

            document.ParochosTransmissionState = "NeedsReview";
            document.FiscalErrorMessage =
                $"Ο πάροχος αναφέρει ότι υπάρχει ήδη το δελτίο {document.SeriesName} {document.Number}, " +
                "αλλά δεν επέστρεψε MARK για το ίδιο αναγνωριστικό. Η επαναποστολή σταμάτησε για αποφυγή διπλής έκδοσης.";
            await db.SaveChangesAsync();
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = document.FiscalErrorMessage,
                RequiresRecovery = true,
                SeriesName = document.SeriesName,
                InvoiceNumber = document.Number
            };
        }

        document.ProcessId ??= result.ProcessId;
        var transmissionFailure = DetermineTransmissionFailure(result);
        if (transmissionFailure is 1 or 2)
        {
            // Check the exact idempotency key once before recording a local
            // offline issue. A lost response may still have created the 8.6.
            var lookup = await _parochos.GetDocumentByExternalSystemIdAsync(settings, externalSystemId);
            if (lookup.Success && lookup.Mark is > 0)
                return await CompleteOrderFormTransmissionAsync(db, document, settings, lookup);

            if (source["invoice"] is Dictionary<string, object?> invoice)
                invoice["transmissionFailure"] = transmissionFailure.Value;
            document.ParochosInvoicePayloadJson = JsonSerializer.Serialize(source);
            document.ParochosTransmissionState = "OfflineIssued";
            document.ParochosTransmissionFailure = transmissionFailure;
            document.ParochosTransmissionAttemptedAtUtc = DateTime.UtcNow;
            document.FiscalErrorMessage = GetOfflineTransmissionMessage(transmissionFailure);
            if (original != null) MarkCancelled(newlyAddedItems, reason);
            _logger.LogWarning(
                "Issued local Order Form pending transmission. ExternalSystemId={ExternalSystemId}; TransmissionFailure={TransmissionFailure}; SendError={SendError}; LookupError={LookupError}",
                externalSystemId,
                transmissionFailure,
                result.ErrorMessage,
                lookup.ErrorMessage);
            await db.SaveChangesAsync();
            return new FiscalIssueResult
            {
                Success = true,
                OrderFormId = document.Id,
                OfflineIssued = true,
                RequiresRecovery = true,
                TransmissionFailure = transmissionFailure,
                ErrorMessage = document.FiscalErrorMessage,
                SeriesName = document.SeriesName,
                InvoiceNumber = document.Number
            };
        }

        _logger.LogError(
            "PAROCHOS ORDER FORM FAILED >>> ExternalSystemId={ExternalSystemId} | Series={Series} | AA={AA} | ErrorCode={ErrorCode} | Error={Error}",
            externalSystemId,
            series.SeriesName,
            thisNumber,
            result.ErrorCode,
            result.ErrorMessage);
        document.ParochosTransmissionState = "NeedsReview";
        document.FiscalErrorMessage = "Δεν επιβεβαιώθηκε η έκδοση του δελτίου παραγγελίας. Παραμένει αποθηκευμένο για έλεγχο στον πάροχο.";
        await db.SaveChangesAsync();
        return new FiscalIssueResult
        {
            Success = false,
            ErrorMessage = document.FiscalErrorMessage,
            RequiresRecovery = true,
            SeriesName = document.SeriesName,
            InvoiceNumber = document.Number
        };
    }


    /// <summary>
    /// Ακύρωση εκδοθείσας απόδειξης/τιμολογίου. Ο Parochos /api/Cancel δέχεται μόνο
    /// δελτία αποστολής/παραλαβής (9.x/10.x), οπότε η ακύρωση παραστατικού αξίας γίνεται
    /// με έκδοση πιστωτικού (11.4 για απόδειξη, 5.1 για τιμολόγιο) συσχετισμένου με το MARK.
    /// </summary>
    public async Task<FiscalIssueResult> CreditIssuedDocumentAsync(
        AppDbContext db, int orderId, int? orderPaymentId, string reason)
    {
        var settings = await _configuration.GetSettingsAsync(db);
        if (!_configuration.ValidateSettings(settings, out var error))
            return new FiscalIssueResult { Success = false, ErrorMessage = error };
        if (!settings!.UseParochosProvider)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Ο πάροχος δεν είναι ενεργός." };

        var order = await db.Orders
            .Include(o => o.Items).ThenInclude(i => i.MenuItem).ThenInclude(m => m!.MenuCategory)
            .Include(o => o.Payments).ThenInclude(p => p.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Η παραγγελία δεν βρέθηκε." };

        if (order.StaffConsumptionInvoiceType is "6.1" or "6.2")
            return new FiscalIssueResult { Success = false, ErrorMessage = "Η ακύρωση στοιχείου αυτοπαράδοσης ή ιδιοχρησιμοποίησης δεν υποστηρίζεται ακόμη από αυτή τη διαδικασία πιστωτικού." };

        var prefix = FiscalExternalSystemIds.CreditDocumentPrefix(orderId, orderPaymentId);
        if (await db.FiscalOrderCancellations.AnyAsync(c => c.OrderId == orderId && c.ExternalSystemId.StartsWith(prefix) && c.Mark != null))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Έχει ήδη εκδοθεί πιστωτικό για αυτό το παραστατικό." };

        long? originalMark;
        FiscalDocumentKind kind;
        List<OrderItem> items;
        if (orderPaymentId is int pid)
        {
            var payment = order.Payments.FirstOrDefault(p => p.Id == pid);
            if (payment?.Mark is null)
                return new FiscalIssueResult { Success = false, ErrorMessage = "Η πληρωμή δεν έχει MARK." };
            originalMark = payment.Mark;
            kind = payment.DocumentKind;
            items = payment.Items
                .Select(pi => (Src: order.Items.FirstOrDefault(i => i.Id == pi.OrderItemId), pi.Quantity))
                .Where(x => x.Src != null && x.Quantity > 0)
                .Select(x => new OrderItem { MenuItem = x.Src!.MenuItem, Quantity = x.Quantity, ExtrasPrice = x.Src.ExtrasPrice })
                .ToList();
        }
        else
        {
            if (order.FiscalStatus != FiscalStatus.Issued || order.ParochosMark is null)
                return new FiscalIssueResult { Success = false, ErrorMessage = "Η παραγγελία δεν έχει εκδοθεί με MARK." };
            originalMark = order.ParochosMark;
            kind = order.DocumentKind ?? FiscalDocumentKind.Receipt;
            items = order.Items.Where(i => !i.IsCancelled && i.Quantity > 0).ToList();
        }

        if (items.Count == 0)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκαν είδη για το πιστωτικό." };
        if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Λείπει ο ΑΦΜ πελάτη του αρχικού τιμολογίου." };

        var isInvoice = kind == FiscalDocumentKind.Invoice;
        var creditKind = isInvoice ? FiscalDocumentKind.CreditInvoice : FiscalDocumentKind.CreditReceipt;
        var series = await _configuration.GetOrCreateSeriesAsync(db, creditKind);
        var number = (long)series.NextNumber;
        var externalSystemId = prefix + Guid.NewGuid().ToString("N");
        var totals = FiscalPayloadBuilder.CalculateTotals(items);

        ParochosSendResult result = new();
        for (var attempt = 0; attempt <= 5; attempt++)
        {
            var invoice = new Dictionary<string, object?>
            {
                ["issuer"] = FiscalPayloadBuilder.BuildIssuer(settings),
                ["counterpart"] = isInvoice ? FiscalPayloadBuilder.BuildInvoiceCounterpart(order) : FiscalPayloadBuilder.BuildRetailCounterpart(),
                ["invoiceHeader"] = new Dictionary<string, object?>
                {
                    ["series"] = series.SeriesName,
                    ["aa"] = number,
                    ["issueDate"] = FiscalPayloadBuilder.IssueTimestampGreekLocal(),
                    ["currency"] = "EUR",
                    ["invoiceType"] = isInvoice ? "5.1" : "11.4",
                    ["invoiceTypeUbl"] = "381",
                    ["selfPricing"] = false,
                    ["fuelInvoice"] = false
                },
                ["invoiceDetails"] = FiscalPayloadBuilder.BuildLines(items, isInvoice ? "E3_561_001" : "E3_561_003"),
                // Επιστροφή χρημάτων: μετρητά. Επιστροφή σε κάρτα (type 7) απαιτεί νέο
                // requestPayment/υπογραφή στο EFT-POS και δεν γίνεται από εδώ.
                ["paymentMethods"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = _configuration.ParsePaymentCode(settings.CashPaymentTypeCode, 3),
                        ["amount"] = totals.Gross
                    }
                },
                ["invoiceSummary"] = new Dictionary<string, object?>
                {
                    ["totalNetValue"] = totals.Net,
                    ["totalVatAmount"] = totals.Vat,
                    ["totalValue"] = totals.Gross
                },
                ["correlatedInvoices"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["mark"] = originalMark!.Value }
                }
            };

            result = await _parochos.SendAndWaitAsync(settings, new Dictionary<string, object?> { ["invoice"] = invoice }, externalSystemId);
            if (result.Success || result.ErrorCode != "00010022")
                break;
            number++;
        }

        if (!result.Success)
            return new FiscalIssueResult { Success = false, ErrorMessage = result.ErrorMessage };

        series.NextNumber = checked((int)Math.Max((long)series.NextNumber, number + 1L));
        db.FiscalOrderCancellations.Add(new FiscalOrderCancellation
        {
            OrderId = orderId,
            ExternalSystemId = externalSystemId,
            ProcessId = result.ProcessId,
            Mark = result.Mark,
            DocumentKind = creditKind,
            SeriesName = series.SeriesName,
            Number = number,
            QrCode = result.QrCode,
            PaymentId = orderPaymentId,
            Reason = $"{series.SeriesName} {number} | {reason}"
        });
        await db.SaveChangesAsync();

        return new FiscalIssueResult
        {
            Success = true,
            Mark = result.Mark,
            QrCode = result.QrCode,
            SeriesName = series.SeriesName,
            InvoiceNumber = number
        };
    }

    private void LogFiscalIssueDate(string kind, string externalSystemId, Dictionary<string, object?> source)
    {
        try
        {
            string? issueDate = null;

            if (source.TryGetValue("invoice", out var invoiceObj) &&
                invoiceObj is Dictionary<string, object?> invoice &&
                invoice.TryGetValue("invoiceHeader", out var headerObj) &&
                headerObj is Dictionary<string, object?> header &&
                header.TryGetValue("issueDate", out var dateObj))
            {
                issueDate = dateObj?.ToString();
            }

            _logger.LogInformation(
                "PAROCHOS ISSUE DATE DEBUG >>> Kind={Kind} | ExternalSystemId={ExternalSystemId} | IssueDateSent={IssueDateSent} | LocalNow={LocalNow} | UtcNow={UtcNow}",
                kind,
                externalSystemId,
                issueDate ?? "<NULL>",
                DateTimeOffset.Now.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PAROCHOS ISSUE DATE DEBUG failed for {ExternalSystemId}", externalSystemId);
        }
    }

}






