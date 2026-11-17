using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;
using RestaurantOrders.Services.Fiscal.Provider;
using RestaurantOrders.Services;

namespace RestaurantOrders.Services.Fiscal;

/// <summary>Coordinates provider payment signatures for supported connected POS terminals.</summary>
public sealed class FiscalCardPaymentService
{
    private readonly IParochosFiscalProvider _parochos;
    private readonly FiscalConfigurationService _configuration;
    private readonly ILogger<FiscalCardPaymentService> _logger;

    public FiscalCardPaymentService(IParochosFiscalProvider parochos, FiscalConfigurationService configuration, ILogger<FiscalCardPaymentService> logger)
    {
        _parochos = parochos;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CardPaymentPreparation> PrepareCardPaymentAsync(
        AppDbContext db,
        Order order,
        FiscalDocumentKind kind,
        BankPosTerminalSettings? selectedPos = null,
        long? mark = null)
    {
        var staffError = await FiscalConfigurationService.ValidateStaffConsumptionAsync(db, order, kind);
        if (staffError is not null) return new CardPaymentPreparation { Success = false, ErrorMessage = staffError };
        if (await db.HasPendingTableTransferAsync(order.Id))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Υπάρχει εκκρεμής μεταφορά τραπεζιού. Ολοκλήρωσε τον επανέλεγχο πριν από φορολογική ενέργεια ή πληρωμή." };

        var pos = selectedPos ?? await _configuration.GetDefaultWebEcrPosAsync(db);
        if (CommonWebEcrProviderProfile.IsCommonRest(pos))
        {
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            if (!MellonConnectionSettings.IsConnected(pos!) || string.IsNullOrWhiteSpace(pos!.ParochosNspCode))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Το Mellon POS χρειάζεται ενεργή σύνδεση, φυσικό TID και Parochos NSP Code." };
            return await PrepareProviderDataAsync(db, order,
                order.Items.Where(i => !i.IsCancelled && i.Quantity > 0).ToList(), kind,
                pos.ParochosNspCode.Trim(), pos.ProviderId > 0 ? pos.ProviderId : 19, pos.TerminalId.Trim(),
                useWebEcrSignatureForFiscalDocument: true, mark: mark);
        }
        if (IsEpayWebEcr(pos))
        {
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            var epayTerminalId = pos!.TerminalId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(epayTerminalId))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το φυσικό Terminal ID του ePay POS στην καρτέλα Τραπεζικά POS." };
            var epayNspCode = string.IsNullOrWhiteSpace(pos.ParochosNspCode) ? "2" : pos.ParochosNspCode.Trim();
            return await PrepareProviderDataAsync(db, order,
                order.Items.Where(i => !i.IsCancelled && i.Quantity > 0).ToList(), kind,
                epayNspCode, pos.ProviderId > 0 ? pos.ProviderId : 19, epayTerminalId,
                useWebEcrSignatureForFiscalDocument: true, mark: mark);
        }
        if (IsEdpsWebEcr(pos))
        {
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            var edpsNspCode = !string.IsNullOrWhiteSpace(pos!.ParochosNspCode)
                ? pos.ParochosNspCode.Trim()
                : "5";
            var edpsTerminalId = pos.TerminalId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(edpsNspCode))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το NSP Code του EDPS στην καρτέλα Τραπεζικά POS." };
            if (string.IsNullOrWhiteSpace(edpsTerminalId))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το φυσικό Terminal ID του EDPS POS στην καρτέλα Τραπεζικά POS." };
            return await PrepareProviderDataAsync(db, order,
                order.Items.Where(i => !i.IsCancelled && i.Quantity > 0).ToList(), kind,
                edpsNspCode, pos.ProviderId > 0 ? pos.ProviderId : 19, edpsTerminalId, mark: mark);
        }
        if (pos is { Provider: "Viva", Protocol: "Viva Cloud" })
        {
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            var vivaTerminalId = pos.TerminalId?.Trim() ?? "";
            var vivaNspCode = pos.ParochosNspCode?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(vivaTerminalId))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το φυσικό Terminal ID του Viva POS." };
            if (string.IsNullOrWhiteSpace(vivaNspCode))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το Parochos NSP Code στις ρυθμίσεις Viva." };
            return await PrepareProviderDataAsync(db, order,
                order.Items.Where(i => !i.IsCancelled && i.Quantity > 0).ToList(), kind,
                vivaNspCode, pos.ProviderId > 0 ? pos.ProviderId : 19, vivaTerminalId, mark: mark);
        }
        var settings = await _configuration.GetSettingsAsync(db);
        if (settings is { UseParochosProvider: false })
        {
            var localTotals = FiscalPayloadBuilder.CalculateTotals(order);
            return new CardPaymentPreparation { Success = true, Amount = localTotals.Gross };
        }
        if (!_configuration.ValidateSettings(settings, out var error))
            return new CardPaymentPreparation { Success = false, ErrorMessage = error };

        var terminalId = pos?.TerminalId?.Trim() ?? "";
        var nspCode = pos?.ParochosNspCode?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(terminalId) || string.IsNullOrWhiteSpace(nspCode))
        {
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = "Επίλεξε POS και συμπλήρωσε Terminal ID και Parochos NSP Code στην καρτέλα Τραπεζικά POS."
            };
        }

        if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };

        if (mark.HasValue && (order.FiscalStatus != FiscalStatus.Issued || order.ParochosMark != mark || string.IsNullOrWhiteSpace(order.ParochosExternalId)))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Το MARK του ήδη εκδομένου παραστατικού δεν συμφωνεί με την παραγγελία." };
        var series = await _configuration.GetOrCreateSeriesAsync(db, FiscalConfigurationService.SeriesKind(order, kind));


        var seriesName = order.InvoiceSeriesName ?? series.SeriesName;
        var externalSystemId = order.ParochosExternalId ?? $"order-{order.Id}-{kind}-{Guid.NewGuid():N}";
        var invoiceNumber = mark.HasValue || order.FiscalStatus == FiscalStatus.Issued ? order.InvoiceNumber ?? 0 :
            await ReceiptNumberAllocator.ReserveAsync(db,seriesName,externalSystemId,order.Id,order.InvoiceNumber);
        var totals = FiscalPayloadBuilder.CalculateTotals(order);
        var invoiceType = FiscalConfigurationService.InvoiceType(order, kind, settings);

        var payment = await _parochos.RequestPaymentAsync(
            settings,
            externalSystemId,
            settings.VatNumber,
            DateTimeOffset.Now,
            settings.Branch,
            invoiceType,
            seriesName,
            invoiceNumber,
            totals.Net,
            totals.Vat,
            totals.Gross,
            totals.Gross,
            terminalId,
            nspCode,
            mark);

        if (!payment.Success || string.IsNullOrWhiteSpace(payment.Signature))
        {
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = payment.ErrorMessage ?? "Ο Parochos δεν επέστρεψε payment signature."
            };
        }

        order.ParochosExternalId = externalSystemId;
        order.InvoiceSeriesName = seriesName;
        order.InvoiceNumber = invoiceNumber;
        order.ParochosPaymentSignature = payment.Signature;
        order.ParochosPaymentUid = payment.Uid;
        order.CardTerminalId = terminalId;
        order.CardNspCode = nspCode;
        if (!mark.HasValue) order.FiscalStatus = FiscalStatus.Pending;

        return new CardPaymentPreparation
        {
            Success = true,
            ExternalSystemId = externalSystemId,
            SeriesName = seriesName,
            InvoiceNumber = invoiceNumber,
            Signature = payment.Signature,
            Uid = payment.Uid,
            TerminalId = terminalId,
            NspCode = nspCode,
            Amount = totals.Gross
        };
    }


    public async Task<CardPaymentPreparation> PrepareSelectedCardPaymentAsync(
        AppDbContext db,
        Order order,
        IReadOnlyCollection<OrderItem> selectedItems,
        FiscalDocumentKind kind,
        BankPosTerminalSettings? selectedPos = null)
    {
        if (order.StaffConsumptionInvoiceType is "6.1" or "6.2")
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Η ιδιοκατανάλωση εκδίδεται για ολόκληρο το τραπέζι." };
        if (await db.HasPendingTableTransferAsync(order.Id))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Υπάρχει εκκρεμής μεταφορά τραπεζιού. Ολοκλήρωσε τον επανέλεγχο πριν από φορολογική ενέργεια ή πληρωμή." };

        var items = selectedItems.Where(i => !i.IsCancelled && i.Quantity > 0).ToList();
        var pos = selectedPos ?? await _configuration.GetDefaultWebEcrPosAsync(db);
        if (CommonWebEcrProviderProfile.IsCommonRest(pos))
        {
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            if (!MellonConnectionSettings.IsConnected(pos!) || string.IsNullOrWhiteSpace(pos!.ParochosNspCode))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Το Mellon POS χρειάζεται ενεργή σύνδεση, φυσικό TID και Parochos NSP Code." };
            return await PrepareProviderDataAsync(db, order, items, kind,
                pos.ParochosNspCode.Trim(), pos.ProviderId > 0 ? pos.ProviderId : 19, pos.TerminalId.Trim(),
                useWebEcrSignatureForFiscalDocument: true);
        }
        if (IsEpayWebEcr(pos))
        {
            if (items.Count == 0) return new CardPaymentPreparation { Success = false, ErrorMessage = "Δεν επιλέχθηκαν είδη." };
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            var epayTerminalId = pos!.TerminalId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(epayTerminalId))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το φυσικό Terminal ID του ePay POS στην καρτέλα Τραπεζικά POS." };
            var epayNspCode = string.IsNullOrWhiteSpace(pos.ParochosNspCode) ? "2" : pos.ParochosNspCode.Trim();
            return await PrepareProviderDataAsync(db, order, items, kind,
                epayNspCode, pos.ProviderId > 0 ? pos.ProviderId : 19, epayTerminalId,
                useWebEcrSignatureForFiscalDocument: true);
        }
        if (IsEdpsWebEcr(pos))
        {
            if (items.Count == 0) return new CardPaymentPreparation { Success = false, ErrorMessage = "Δεν επιλέχθηκαν είδη." };
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            var edpsNspCode = !string.IsNullOrWhiteSpace(pos!.ParochosNspCode)
                ? pos.ParochosNspCode.Trim()
                : "5";
            var edpsTerminalId = pos.TerminalId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(edpsNspCode))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το NSP Code του EDPS στην καρτέλα Τραπεζικά POS." };
            if (string.IsNullOrWhiteSpace(edpsTerminalId))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το φυσικό Terminal ID του EDPS POS στην καρτέλα Τραπεζικά POS." };
            return await PrepareProviderDataAsync(db, order, items, kind, edpsNspCode,
                pos.ProviderId > 0 ? pos.ProviderId : 19, edpsTerminalId);
        }
        if (pos is { Provider: "Viva", Protocol: "Viva Cloud" })
        {
            if (items.Count == 0) return new CardPaymentPreparation { Success = false, ErrorMessage = "Δεν επιλέχθηκαν είδη." };
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            var vivaTerminalId = pos.TerminalId?.Trim() ?? "";
            var vivaNspCode = pos.ParochosNspCode?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(vivaTerminalId))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το φυσικό Terminal ID του Viva POS." };
            if (string.IsNullOrWhiteSpace(vivaNspCode))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το Parochos NSP Code στις ρυθμίσεις Viva." };
            return await PrepareProviderDataAsync(db, order, items, kind,
                vivaNspCode, pos.ProviderId > 0 ? pos.ProviderId : 19, vivaTerminalId);
        }
        var settings = await _configuration.GetSettingsAsync(db);
        if (settings is { UseParochosProvider: false })
        {
            if (items.Count == 0) return new CardPaymentPreparation { Success = false, ErrorMessage = "Δεν επιλέχθηκαν είδη." };
            return new CardPaymentPreparation { Success = true, Amount = FiscalPayloadBuilder.CalculateTotals(items).Gross };
        }
        if (!_configuration.ValidateSettings(settings, out var error))
            return new CardPaymentPreparation { Success = false, ErrorMessage = error };
        if (items.Count == 0)
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Δεν επιλέχθηκαν είδη." };
        var terminalId = pos?.TerminalId?.Trim() ?? "";
        var nspCode = pos?.ParochosNspCode?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(terminalId) || string.IsNullOrWhiteSpace(nspCode))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Επίλεξε POS και συμπλήρωσε Terminal ID και Parochos NSP Code στην καρτέλα Τραπεζικά POS." };
        if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };

        var series = await _configuration.GetOrCreateSeriesAsync(db, FiscalConfigurationService.SeriesKind(order, kind));
        var externalSystemId = $"payment-{order.Id}-{Guid.NewGuid():N}";
        var invoiceNumber = await ReceiptNumberAllocator.ReserveAsync(db,series.SeriesName,externalSystemId,order.Id);
        var totals = FiscalPayloadBuilder.CalculateTotals(items);
        var invoiceType = FiscalConfigurationService.InvoiceType(order, kind, settings);

        var payment = await _parochos.RequestPaymentAsync(
            settings,
            externalSystemId,
            settings.VatNumber,
            DateTimeOffset.Now,
            settings.Branch,
            invoiceType,
            series.SeriesName,
            invoiceNumber,
            totals.Net,
            totals.Vat,
            totals.Gross,
            totals.Gross,
            terminalId,
            nspCode);

        if (!payment.Success || string.IsNullOrWhiteSpace(payment.Signature))
            return new CardPaymentPreparation { Success = false, ErrorMessage = payment.ErrorMessage ?? "Ο Parochos δεν επέστρεψε payment signature." };

        return new CardPaymentPreparation
        {
            Success = true,
            ExternalSystemId = externalSystemId,
            SeriesName = series.SeriesName,
            InvoiceNumber = invoiceNumber,
            Signature = payment.Signature,
            Uid = payment.Uid,
            TerminalId = terminalId,
            NspCode = nspCode,
            Amount = totals.Gross
        };
    }

    private static string NormalizeParochosSignatureForWebEcr(string signature)
    {
        var value = signature.Trim();
        if (value.Length > 0 && value.All(Uri.IsHexDigit))
            return value.ToUpperInvariant();

        var base64 = value.Replace('-', '+').Replace('_', '/');
        var remainder = base64.Length % 4;
        if (remainder == 1)
            throw new FormatException("Parochos signature is neither HEX nor valid Base64.");
        if (remainder != 0)
            base64 = base64.PadRight(base64.Length + (4 - remainder), '=');

        byte[] signatureBytes;
        try { signatureBytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new FormatException("Parochos signature is neither HEX nor valid Base64."); }

        // AADE provider signatures are ECDSA P-256 encoded as ASN.1/DER and then HEX.
        // A DER ECDSA signature is normally 70–72 bytes; a 96-character Base64 value
        // can represent the same 72-byte signature and must be converted to HEX for WebECR.
        if (signatureBytes.Length is < 70 or > 72 || signatureBytes[0] != 0x30 || signatureBytes[1] != signatureBytes.Length - 2)
            throw new FormatException("Parochos Base64 signature is not an ASN.1/DER ECDSA signature.");

        return Convert.ToHexString(signatureBytes);
    }

    private static bool TryParseSignedAmountToCents(string value, out int cents)
    {
        cents = 0;
        var text = value.Trim();
        if (text.Length == 0) return false;

        // The Parochos API example uses integer minor units, while some responses
        // have been observed with decimal euro values. Keep both representations
        // explicit so a decimal value is never accidentally treated as cents.
        var hasDecimalSeparator = text.Contains('.') || text.Contains(',');
        if (text.Contains(',') && !text.Contains('.')) text = text.Replace(',', '.');
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount))
            return false;

        try
        {
            cents = decimal.ToInt32(decimal.Round(hasDecimalSeparator ? amount * 100m : amount,
                0, MidpointRounding.AwayFromZero));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadSignedPaymentAmounts(
        string[]? parts,
        int expectedNetCents,
        int expectedVatCents,
        int expectedTotalCents,
        out int netCents,
        out int vatCents,
        out int totalCents,
        out int paymentCents,
        out string fieldOrder,
        out string signedTuple)
    {
        netCents = vatCents = totalCents = paymentCents = 0;
        fieldOrder = "unknown";
        signedTuple = parts is { Length: >= 7 }
            ? string.Join(";", parts.Skip(3).Take(4).Select(x => x.Trim()))
            : "<missing>";

        if (parts is not { Length: >= 8 }) return false;
        var values = parts.Skip(3).Take(4).Select(x => x.Trim()).ToArray();
        var parsed = new int[4];
        for (var i = 0; i < values.Length; i++)
            if (!TryParseSignedAmountToCents(values[i], out parsed[i])) return false;

        // Parochos API 3.0.29 response example: Net;VAT;Total;Payment.
        var parochosOrderMatches = parsed[0] == expectedNetCents &&
            parsed[1] == expectedVatCents && parsed[2] == expectedTotalCents &&
            parsed[3] == expectedTotalCents;
        // AADE token-crypto proposal: Payment;Net;VAT;Total.
        var aadeOrderMatches = parsed[0] == expectedTotalCents &&
            parsed[1] == expectedNetCents && parsed[2] == expectedVatCents &&
            parsed[3] == expectedTotalCents;

        if (parochosOrderMatches)
        {
            netCents = parsed[0]; vatCents = parsed[1]; totalCents = parsed[2]; paymentCents = parsed[3];
            fieldOrder = "Parochos Net;VAT;Total;Payment";
            return true;
        }
        if (aadeOrderMatches)
        {
            paymentCents = parsed[0]; netCents = parsed[1]; vatCents = parsed[2]; totalCents = parsed[3];
            fieldOrder = "AADE Payment;Net;VAT;Total";
            return true;
        }
        return false;
    }

    private async Task<CardPaymentPreparation> PrepareProviderDataAsync(
        AppDbContext db,
        Order order,
        IReadOnlyCollection<OrderItem> items,
        FiscalDocumentKind kind,
        string nspCode,
        int providerId,
        string terminalId,
        bool useWebEcrSignatureForFiscalDocument = false,
        long? mark = null)
    {
        var activeItems = items.Where(i => !i.IsCancelled && i.Quantity > 0).ToList();
        if (activeItems.Count == 0)
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Δεν υπάρχουν ενεργά είδη με ποσότητα για πληρωμή. Δεν στάλθηκε αίτημα στον Parochos." };

        // Some callers provide OrderItems without their MenuItem navigation loaded.
        // Resolve prices explicitly here so RequestPayment never receives zero totals
        // merely because the caller used Include(Items) without Include(MenuItem).
        var menuItemIds = activeItems.Select(i => i.MenuItemId).Distinct().ToArray();
        var menuItemsById = await db.MenuItems
            .Include(menuItem => menuItem.MenuCategory)
            .Where(menuItem => menuItemIds.Contains(menuItem.Id))
            .ToDictionaryAsync(menuItem => menuItem.Id);
        var missingMenuItemIds = activeItems
            .Where(item => !menuItemsById.ContainsKey(item.MenuItemId))
            .Select(item => item.MenuItemId)
            .Distinct()
            .ToArray();
        if (missingMenuItemIds.Length > 0)
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = $"Δεν βρέθηκαν τιμές καταλόγου για τα MenuItemId: {string.Join(", ", missingMenuItemIds)}. Δεν στάλθηκε αίτημα στον Parochos."
            };
        foreach (var item in activeItems) item.MenuItem = menuItemsById[item.MenuItemId];
        items = activeItems;

        var settings = await _configuration.GetSettingsAsync(db);
        if (!_configuration.ValidateSettings(settings, out var validationError))
            return new CardPaymentPreparation { Success = false, ErrorMessage = validationError };
        if (string.IsNullOrWhiteSpace(terminalId))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Για υπογραφή ProviderData απαιτείται το Terminal ID του επιλεγμένου POS." };
        if (string.IsNullOrWhiteSpace(nspCode))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Για υπογραφή ProviderData απαιτείται Parochos NSP Code." };
        if (providerId <= 0)
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Το ProviderId της υπογραφής Parochos πρέπει να είναι θετικός ακέραιος." };

        var totals = FiscalPayloadBuilder.CalculateTotals(items);
        if (totals.Gross <= 0m || totals.Net <= 0m)
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = "Δεν υπολογίστηκαν έγκυρες καθαρές και μικτές αξίες για την πληρωμή. Έλεγξε ότι φορτώθηκαν τα είδη και οι τιμές τους. Δεν ξεκίνησε χρέωση."
            };
        if (mark.HasValue && (order.FiscalStatus != FiscalStatus.Issued || order.ParochosMark != mark || string.IsNullOrWhiteSpace(order.ParochosExternalId)))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Το MARK του ήδη εκδομένου παραστατικού δεν συμφωνεί με την παραγγελία." };
        var series = await _configuration.GetOrCreateSeriesAsync(db, FiscalConfigurationService.SeriesKind(order, kind));
        var seriesName = mark.HasValue ? order.InvoiceSeriesName ?? series.SeriesName : series.SeriesName;
        var externalSystemId = mark.HasValue ? order.ParochosExternalId! : $"payment-{order.Id}-{Guid.NewGuid():N}";
        var invoiceNumber = mark.HasValue ? order.InvoiceNumber ?? 0 : await ReceiptNumberAllocator.ReserveAsync(db,seriesName,externalSystemId,order.Id);
        var invoiceType = FiscalConfigurationService.InvoiceType(order, kind, settings);
        var documentDate = order.ClosedAt ?? order.CreatedAt;
        var athensZone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "GTB Standard Time" : "Europe/Athens");
        var paymentIssueDate = mark.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(documentDate, DateTimeKind.Unspecified), athensZone.GetUtcOffset(documentDate))
            : DateTimeOffset.Now;
        var payment = await _parochos.RequestPaymentAsync(settings, externalSystemId, settings.VatNumber, paymentIssueDate,
            settings.Branch, invoiceType, seriesName, invoiceNumber, totals.Net, totals.Vat, totals.Gross, totals.Gross,
            terminalId.Trim(), nspCode.Trim(), mark);
        if (!payment.Success || string.IsNullOrWhiteSpace(payment.Signature) || string.IsNullOrWhiteSpace(payment.Uid) || payment.Timestamp is null)
            return new CardPaymentPreparation { Success = false, ErrorMessage = payment.ErrorMessage ?? "Ο Parochos δεν επέστρεψε πλήρη υπογραφή πληρωμής (UID/Signature/Timestamp)." };

        string webEcrSignature;
        try { webEcrSignature = NormalizeParochosSignatureForWebEcr(payment.Signature); }
        catch (FormatException ex)
        {
            return new CardPaymentPreparation { Success = false, ErrorMessage = ex.Message + " No charge was started." };
        }

        // WebECR requires yyyyMMddHHmmss in Greece local time. Normalize the timestamp
        // from Parochos signatureClearText without changing the represented instant.
        var signatureClearTextParts = payment.SignatureClearText?.Split(';');
        var signedTimestampText = signatureClearTextParts is { Length: >= 3 }
            ? signatureClearTextParts[2].Trim()
            : null;
        var athensTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "GTB Standard Time" : "Europe/Athens");
        DateTimeOffset? signedTimestamp = null;
        if (signedTimestampText is { Length: 14 } &&
                 DateTime.TryParseExact(signedTimestampText, "yyyyMMddHHmmss",
                     CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedCompactTimestamp))
        {
            var localTimestamp = DateTime.SpecifyKind(parsedCompactTimestamp, DateTimeKind.Unspecified);
            signedTimestamp = new DateTimeOffset(localTimestamp, athensTimeZone.GetUtcOffset(localTimestamp));
        }
        else if (!string.IsNullOrWhiteSpace(signedTimestampText) &&
                 DateTimeOffset.TryParse(signedTimestampText, CultureInfo.InvariantCulture,
                     DateTimeStyles.AllowWhiteSpaces, out var parsedIsoTimestamp))
        {
            signedTimestamp = parsedIsoTimestamp;
        }

        string? signatureTimestamp = null;
        if (signedTimestamp.HasValue &&
            Math.Abs((signedTimestamp.Value.UtcDateTime - payment.Timestamp.Value.UtcDateTime).TotalSeconds) <= 1.1)
        {
            signatureTimestamp = TimeZoneInfo.ConvertTime(signedTimestamp.Value, athensTimeZone)
                .ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }
        if (signatureTimestamp is null)
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = $"Could not match the signatureClearText timestamp to the Parochos response timestamp. Signed value: '{signedTimestampText ?? "<missing>"}', response timestamp: '{payment.Timestamp:O}'. No charge was started."
            };

        var expectedNetCents = decimal.ToInt32(decimal.Round(totals.Net * 100m, 0, MidpointRounding.AwayFromZero));
        var expectedVatCents = decimal.ToInt32(decimal.Round(totals.Vat * 100m, 0, MidpointRounding.AwayFromZero));
        var expectedTotalCents = decimal.ToInt32(decimal.Round(totals.Gross * 100m, 0, MidpointRounding.AwayFromZero));
        var providerTokenAmountCents = decimal.ToInt32(decimal.Round(payment.Amount * 100m, 0, MidpointRounding.AwayFromZero));
        if (providerTokenAmountCents != expectedTotalCents)
        {
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = $"Το ποσό που υπέγραψε ο Parochos ({providerTokenAmountCents} λεπτά) δεν ισούται με το σύνολο της παραγγελίας ({expectedTotalCents} λεπτά). Δεν ξεκίνησε χρέωση."
            };
        }

        if (!TryReadSignedPaymentAmounts(signatureClearTextParts, expectedNetCents, expectedVatCents,
                expectedTotalCents, out var signedNetCents, out var signedVatCents,
                out var signedTotalCents, out var signedPaymentCents, out var signedFieldOrder,
                out var signedAmountTuple))
        {
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = $"Τα ποσά που υπέγραψε ο Parochos δεν συμφωνούν με την παραγγελία ή δεν αναγνωρίζονται. " +
                    $"Υπογεγραμμένη τετράδα='{signedAmountTuple}', αναμενόμενα λεπτά ευρώ: καθαρή={expectedNetCents}, " +
                    $"ΦΠΑ={expectedVatCents}, σύνολο/πληρωμή={expectedTotalCents}. Δεν ξεκίνησε χρέωση."
            };
        }

        long? signedMark = null;
        var signatureDataMismatches = new List<string>();
        if (signatureClearTextParts is not { Length: >= 8 })
        {
            signatureDataMismatches.Add("signatureClearText has fewer than 8 fields");
        }
        else
        {
            if (!string.Equals(signatureClearTextParts[0].Trim(), payment.Uid, StringComparison.OrdinalIgnoreCase))
                signatureDataMismatches.Add("UID differs from signed UID");
            if (!string.IsNullOrWhiteSpace(signatureClearTextParts[1]))
            {
                if (long.TryParse(signatureClearTextParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMark))
                    signedMark = parsedMark;
                else
                    signatureDataMismatches.Add("signed MARK is not numeric");
            }
            if (!string.Equals(signatureClearTextParts[7].Trim(), terminalId.Trim(), StringComparison.OrdinalIgnoreCase))
                signatureDataMismatches.Add($"TerminalId in signed text is '{signatureClearTextParts[7]}' but selected POS TID is '{terminalId}'");
        }
        if (signatureDataMismatches.Count > 0)
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = "Parochos signatureClearText does not match the payment fields: " + string.Join("; ", signatureDataMismatches) + ". No charge was started."
            };
        if (mark.HasValue && signedMark != mark)
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Το MARK μέσα στην υπογραφή πληρωμής δεν ταιριάζει με το ήδη εκδομένο παραστατικό." };

        // Use the amounts proven by Parochos' signatureClearText. The parser above
        // has already checked them against the order, in either documented field order.
        var providerData = new ProviderPaymentData(payment.Uid!, signatureTimestamp,
            signedNetCents, signedVatCents, signedTotalCents, providerId, webEcrSignature, signedMark,
            signedAmountTuple, signedFieldOrder, payment.Signature.Trim());
        return new CardPaymentPreparation { Success = true, ExternalSystemId = externalSystemId, SeriesName = seriesName,
            InvoiceNumber = invoiceNumber, Signature = useWebEcrSignatureForFiscalDocument ? webEcrSignature : payment.Signature, Uid = payment.Uid, TerminalId = terminalId.Trim(),
            NspCode = nspCode.Trim(), Amount = signedPaymentCents / 100m, ProviderData = providerData };
    }

    /// <summary>
    /// Εκδίδει τελικό παραστατικό μόνο για τα επιλεγμένα είδη μιας παραγγελίας.
    /// Η μέθοδος δεν κλείνει το Point of Service όταν παραμένουν ενεργά είδη.
    /// </summary>

    private static bool IsEpayWebEcr(BankPosTerminalSettings? terminal) => terminal is not null &&
        (terminal.Provider.Contains("ePay", StringComparison.OrdinalIgnoreCase) ||
         terminal.Provider.Contains("Euronet", StringComparison.OrdinalIgnoreCase));

    private static bool IsEdpsWebEcr(BankPosTerminalSettings? terminal) => terminal is not null &&
        terminal.Provider.Equals("EDPS", StringComparison.OrdinalIgnoreCase) &&
        terminal.Protocol.Contains("WebECR API v6", StringComparison.OrdinalIgnoreCase);


}

