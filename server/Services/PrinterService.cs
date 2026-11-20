using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public sealed record KitchenTicketLine(
    string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent, string Priority);

/// <summary>
/// Στέλνει "δελτία παραγγελίας" σε δικτυακούς θερμικούς εκτυπωτές ESC/POS (Epson/Star κ.λπ.)
/// μέσω απλού TCP socket στη θύρα 9100 (RAW printing - υποστηρίζεται από σχεδόν όλους
/// τους δικτυακούς εκτυπωτές κουζίνας/μπαρ).
/// </summary>
public class PrinterService
{
    private readonly ILogger<PrinterService> _logger;
    private readonly IJSRuntime _js;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly bool _cloudWebMode;
    private readonly string _cloudPrintBridgeUrl;
    private readonly string _cloudPrintBridgeToken;
    public string? LastTestError { get; private set; }
    /// <summary>
    /// Τελευταίο σφάλμα αποστολής προς εκτυπωτή στο τρέχον Blazor scope.
    /// Χρησιμοποιείται από την οθόνη έκδοσης ώστε μια αποτυχία εκτύπωσης να
    /// μην παρουσιάζεται ως ασαφές «δεν τυπώθηκε».
    /// </summary>
    public string? LastPrintError { get; private set; }
    private enum PrintJobKind { KitchenOrder, FiscalDocument, Other, Test }
    private static readonly CultureInfo GreekCulture = CultureInfo.GetCultureInfo("el-GR");

    // ESC/POS commands
    private static readonly byte[] Init = { 0x1B, 0x40 };                  // ESC @  -> reset
    private static readonly byte[] BoldOn = { 0x1B, 0x45, 0x01 };          // ESC E 1
    private static readonly byte[] BoldOff = { 0x1B, 0x45, 0x00 };         // ESC E 0
    private static readonly byte[] DoubleSizeOn = { 0x1D, 0x21, 0x11 };    // GS ! 0x11 (double W/H)
    private static readonly byte[] DoubleSizeOff = { 0x1D, 0x21, 0x00 };   // GS ! 0
    private static readonly byte[] AlignCenter = { 0x1B, 0x61, 0x01 };     // ESC a 1
    private static readonly byte[] AlignLeft = { 0x1B, 0x61, 0x00 };       // ESC a 0
    private static readonly byte[] Feed4 = { 0x0A, 0x0A, 0x0A, 0x0A };

    // Μία μόνο τυπική εντολή πλήρους κοπής (GS V 0). Πολλαπλές παραλλαγές μαζί προκαλούσαν
    // πολλαπλή κοπή σε εκτυπωτές που τις αναγνωρίζουν όλες.
    private static readonly byte[] CutSequence = { 0x1D, 0x56, 0x00 };

    // Το σύμβολο "€" δεν υπάρχει στα παλιά (προ-ευρώ) ελληνικά codepages όπως το PC737,
    // γι' αυτό βγαίνει "?". Λύση: το γράφουμε πάντα μέσω Windows-1253 (που το έχει σίγουρα
    // στη θέση 0x80), αλλάζοντας codepage στιγμιαία μόνο για αυτόν τον χαρακτήρα.
    private static readonly byte[] SelectCp1253 = { 0x1B, 0x74, 7 };
    private static readonly Encoding Cp1253Encoding;

    static PrinterService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1253Encoding = Encoding.GetEncoding(1253);
    }

    public PrinterService(ILogger<PrinterService> logger, IJSRuntime js, IConfiguration configuration)
        : this(logger, js, configuration, null)
    {
    }

    public PrinterService(ILogger<PrinterService> logger, IJSRuntime js, IConfiguration configuration,
        IHttpClientFactory? httpClientFactory)
    {
        _logger = logger;
        _js = js;
        _httpClientFactory = httpClientFactory;
        _cloudWebMode = configuration.GetValue<bool>("CloudWeb:Enabled");
        _cloudPrintBridgeUrl = configuration["CloudWeb:PrintBridgeUrl"]?.Trim() ?? "";
        var tokenFile = configuration["CloudWeb:SyncTokenFile"];
        try
        {
            _cloudPrintBridgeToken = !string.IsNullOrWhiteSpace(tokenFile) && File.Exists(tokenFile)
                ? File.ReadAllText(tokenFile).Trim()
                : configuration["CloudWeb:SyncToken"]?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            _cloudPrintBridgeToken = "";
            _logger.LogError(ex, "Δεν ήταν δυνατή η ανάγνωση του μυστικού token για το Cloud Print Bridge.");
        }
    }

    /// <summary>Επιστρέφει (byte κωδικού για ESC t n, .NET Encoding) ανάλογα με την επιλογή codepage του εκτυπωτή.</summary>
    private static (byte EscTValue, Encoding Encoding) ResolveCodepage(string codepage)
    {
        return codepage?.Trim().ToUpperInvariant() switch
        {
            "1253" => (7, Cp1253Encoding),
            "UTF-8" or "UTF8" => (0xFF, Encoding.UTF8),
            _ => (11, Encoding.GetEncoding(737))
        };
    }

    /// <summary>Τυπώνει ένα δελτίο παραγγελίας (κουζίνα/μπαρ) σε συγκεκριμένο σταθμό.</summary>
    public Task<bool> PrintTicketAsync(
        PrinterStation station,
        string tableName,
        string waiterName,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent)> items,
        CompanySettings? companySettings = null,
        int? orderNumber = null,
        string? orderComment = null)
    {
        var bytes = BuildOrderTicket(station, tableName, waiterName, items, companySettings, orderNumber, orderComment);
        return SendAsync(station, bytes, PrintJobKind.KitchenOrder);
    }

    public Task<bool> PrintKitchenTicketAsync(
        PrinterStation station,
        string tableName,
        string waiterName,
        IEnumerable<KitchenTicketLine> items,
        CompanySettings? companySettings,
        int orderNumber,
        string? orderComment)
    {
        var lines = items.ToList();
        var values = lines.Select(x => (x.ItemName, x.Quantity, x.LineTotal, x.Notes, x.VatPercent)).ToList();
        var priorities = lines.Select(x => (string?)x.Priority).ToList();
        var bytes = BuildOrderTicket(station, tableName, waiterName, values, companySettings, orderNumber, orderComment, priorities);
        return SendAsync(station, bytes, PrintJobKind.KitchenOrder);
    }

    /// <summary>Τυπώνει φορολογικό/ηλεκτρονικό Δελτίο Παραγγελίας σε συγκεκριμένο σταθμό.</summary>
    public Task<bool> PrintOrderFormAsync(
        PrinterStation station,
        string tableName,
        string waiterName,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent)> items,
        long? mark,
        string? qrCode,
        CompanySettings? companySettings = null,
        string documentTitle = "ΔΕΛΤΙΟ ΠΑΡΑΓΓΕΛΙΑΣ",
        string? seriesName = null,
        long? documentNumber = null)
    {
        var bytes = BuildOrderFormTicket(
            station,
            tableName,
            waiterName,
            items,
            mark,
            qrCode,
            companySettings,
            documentTitle,
            seriesName,
            documentNumber);

        return SendAsync(station, bytes, PrintJobKind.FiscalDocument);
    }

    /// <summary>Τυπώνει αναλυτικό λογαριασμό (όλα τα είδη του τραπεζιού + σύνολο).</summary>
    public Task<bool> PrintBillAsync(
        PrinterStation station,
        string tableName,
        string waiterName,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent)> items,
        decimal total,
        CompanySettings? companySettings = null,
        Order? order = null,
        string? documentLabel = null)
    {
        var bytes = BuildBillTicket(station, tableName, waiterName, items, total, companySettings, order, documentLabel);
        return SendAsync(station, bytes, PrintJobKind.FiscalDocument);
    }

    /// <summary>
    /// Τυπώνει το τελικό φορολογικό παραστατικό με MARK και πραγματικό QR code ESC/POS.
    /// </summary>
    public Task<bool> PrintFiscalReceiptAsync(
        PrinterStation station,
        string tableName,
        string waiterName,
        string documentLabel,
        string seriesName,
        long invoiceNumber,
        long? mark,
        string? qrCode,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, decimal VatPercent)> items,
        decimal total,
        CompanySettings? companySettings = null,
        Order? order = null,
        int? paymentId = null,
        string copyLabel = "ΠΡΩΤΟΤΥΠΟ",
        DateTime? issuedAt = null,
        IEnumerable<string>? relatedDocuments = null,
        int? transmissionFailure = null)
    {
        var bytes = BuildFiscalReceiptTicket(
            station, tableName, waiterName, documentLabel, seriesName, invoiceNumber, mark, qrCode, items, total, companySettings, order, paymentId, copyLabel, issuedAt ?? DateTime.Now, relatedDocuments, transmissionFailure);
        return SendAsync(station, bytes, PrintJobKind.FiscalDocument);
    }

    /// <summary>Τυπώνει τη σύνοψη ενός συγκεκριμένου κλεισίματος ταμείου σε συγκεκριμένο εκτυπωτή.</summary>
    public Task<bool> PrintDayCloseAsync(PrinterStation station, DayCloseRecord record)
    {
        var bytes = BuildDayCloseTicket(station, record);
        return SendAsync(station, bytes, PrintJobKind.Other);
    }

    public Task<bool> PrintTestTicketAsync(PrinterStation station, string tableName, string waiterName,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent)> items)
    {
        LastTestError = null;
        var bytes = BuildOrderTicket(station, tableName, waiterName, items, null, null, null);
        return SendAsync(station, bytes, PrintJobKind.Test);
    }

    private async Task<bool> SendAsync(PrinterStation station, byte[] bytes, PrintJobKind jobKind)
    {
        LastPrintError = null;

        if (station is null)
        {
            LastPrintError = "Δεν ορίστηκε εκτυπωτής.";
            if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
            return false;
        }

        if (string.Equals(station.ConnectionType, "Sunmi", StringComparison.OrdinalIgnoreCase))
        {
            if (jobKind == PrintJobKind.KitchenOrder && !station.PrintOrders) return true;
            if (jobKind == PrintJobKind.FiscalDocument && !station.PrintFiscalDocuments) return true;
            if (jobKind == PrintJobKind.Other)
            {
                _logger.LogWarning("Ο σταθμός Sunmi {Name} δεν χρησιμοποιείται για αυτή την εκτύπωση.", station.Name);
                return false;
            }

            try
            {
                return await _js.InvokeAsync<bool>("sunmiPrinter.printRaw", Convert.ToBase64String(bytes), Math.Clamp(station.Copies, 1, 5));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Αποτυχία τοπικής εκτύπωσης Sunmi στον σταθμό {Name}", station.Name);
                LastPrintError = $"Sunmi: {ex.Message}";
                if (jobKind == PrintJobKind.Test) LastTestError = ex.Message;
                return false;
            }
        }

        if (string.Equals(station.ConnectionType, "Usb", StringComparison.OrdinalIgnoreCase))
        {
            if (jobKind == PrintJobKind.KitchenOrder && !station.PrintOrders) return true;
            if (jobKind == PrintJobKind.FiscalDocument && !station.PrintFiscalDocuments) return true;

            if (_cloudWebMode)
            {
                LastPrintError = "Η εκτύπωση USB χρειάζεται τοπική εφαρμογή Windows με εγκατεστημένη ουρά εκτυπωτή.";
                if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
                return false;
            }

            try
            {
                WindowsRawPrinter.Send(station.WindowsPrinterName, bytes, station.Copies);
                _logger.LogInformation("Η εκτύπωση {Kind} στάλθηκε μέσω USB ESC/POS στην ουρά Windows {PrinterName} ({Name})",
                    jobKind, station.WindowsPrinterName, station.Name);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Αποτυχία USB ESC/POS εκτύπωσης στον σταθμό {Name} / ουρά {PrinterName}",
                    station.Name, station.WindowsPrinterName);
                LastPrintError = ex.Message;
                if (jobKind == PrintJobKind.Test) LastTestError = ex.Message;
                return false;
            }
        }

        if (!IPAddress.TryParse(station.IpAddress?.Trim(), out var printerAddress) || station.Port is <= 0 or > 65535)
        {
            LastPrintError = "Η διεύθυνση IP ή η θύρα του δικτυακού εκτυπωτή δεν είναι έγκυρη.";
            _logger.LogWarning("Μη έγκυρες ρυθμίσεις εκτυπωτή {Name}: {Ip}:{Port}", station.Name, station.IpAddress, station.Port);
            if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
            return false;
        }

        if (_cloudWebMode && !IsPrivateLanAddress(printerAddress))
        {
            LastPrintError = "Στο Cloud POS η διεύθυνση δικτυακού εκτυπωτή πρέπει να είναι ιδιωτική IP του καταστήματος.";
            _logger.LogWarning("Απορρίφθηκε μη τοπική IP εκτυπωτή {Ip} για τον σταθμό {Name}.", station.IpAddress, station.Name);
            if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
            return false;
        }

        // A normal browser cannot open raw TCP sockets. The RestaurantOrders
        // Android app can, and it is in the shop LAN even when its page is served
        // by Cloud POS. Prefer that device-side path before the cloud-host relay.
        var androidResult = await TrySendViaAndroidPrinterAsync(station, bytes, jobKind);
        if (androidResult.HasValue)
            return androidResult.Value;

        // A cloud host/container is not a print node and must not open a socket
        // to a printer on the store LAN. Cloud orders are queued by
        // CloudPrintAgentResolver; a generic browser cannot run this test print.
        if (_cloudWebMode)
        {
            LastPrintError = "Η σελίδα Cloud δεν έχει πρόσβαση στο τοπικό Wi‑Fi. Άνοιξε την εφαρμογή POS στη συσκευή που είναι συνδεδεμένη με τον εκτυπωτή.";
            if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
            _logger.LogWarning("Η εκτύπωση {Kind} στον {Name} δεν στάλθηκε: δεν υπάρχει τοπική γέφυρα εκτύπωσης στον client.",
                jobKind, station.Name);
            return false;
        }

        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(station.IpAddress, station.Port, timeout.Token);

            using var stream = client.GetStream();
            var copies = Math.Max(1, station.Copies);
            for (var i = 0; i < copies; i++)
            {
                await stream.WriteAsync(bytes);
            }
            await stream.FlushAsync();
            _logger.LogInformation("Η εκτύπωση {Kind} στάλθηκε στον {Name} ({Ip}:{Port})", jobKind, station.Name, station.IpAddress, station.Port);
            return true;
        }
        catch (OperationCanceledException)
        {
            LastPrintError = $"Timeout σύνδεσης στον {station.IpAddress}:{station.Port}.";
            _logger.LogWarning("{Error} Εκτυπωτής: {Name}", LastPrintError, station.Name);
            if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Αποτυχία εκτύπωσης στον εκτυπωτή {Name} ({Ip}:{Port})", station.Name, station.IpAddress, station.Port);
            LastPrintError = ex is SocketException socketError
                ? $"Δικτυακό σφάλμα ({socketError.SocketErrorCode}) προς {station.IpAddress}:{station.Port}."
                : ex.Message;
            if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
            return false;
        }
    }

    /// <summary>
    /// Sends network ESC/POS from the current Android app when available.
    /// Returns null when there is no native Android bridge, allowing the caller
    /// to use its normal local or cloud-host transport.
    /// </summary>
    private async Task<bool?> TrySendViaAndroidPrinterAsync(PrinterStation station, byte[] bytes, PrintJobKind jobKind)
    {
        bool available;
        try
        {
            available = await _js.InvokeAsync<bool>("restaurantOrdersAndroidPrinter.isAvailable");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Δεν είναι διαθέσιμο το Android print bridge για τον σταθμό {Name}.", station.Name);
            return null;
        }

        if (!available)
            return null;

        try
        {
            var clientResult = await _js.InvokeAsync<string>("restaurantOrdersAndroidPrinter.printTcpRaw",
                station.IpAddress, station.Port, Convert.ToBase64String(bytes), Math.Clamp(station.Copies, 1, 5));
            if (string.Equals(clientResult, "printed", StringComparison.Ordinal))
            {
                _logger.LogInformation("Η εκτύπωση {Kind} στάλθηκε από Android στη {Name} ({Ip}:{Port})",
                    jobKind, station.Name, station.IpAddress, station.Port);
                return true;
            }

            LastPrintError = $"Android: δεν ολοκληρώθηκε η τοπική εκτύπωση στον {station.IpAddress}:{station.Port}.";
            if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
            return false;
        }
        catch (Exception ex)
        {
            // The device may have printed before its acknowledgement was lost.
            // Do not resend through the cloud relay and risk a duplicate ticket.
            _logger.LogError(ex, "Αποτυχία τοπικής Android εκτύπωσης στον {Name} ({Ip}:{Port})",
                station.Name, station.IpAddress, station.Port);
            LastPrintError = $"Android: {ex.Message}";
            if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
            return false;
        }
    }

    private async Task<bool> SendViaCloudPrintBridgeAsync(
        PrinterStation station, IPAddress printerAddress, byte[] bytes, PrintJobKind jobKind)
    {
        if (_httpClientFactory is null || string.IsNullOrWhiteSpace(_cloudPrintBridgeUrl))
        {
            LastPrintError = "Δεν έχει ρυθμιστεί το Cloud Print Bridge στον server.";
            if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
            return false;
        }

        if (string.IsNullOrWhiteSpace(_cloudPrintBridgeToken))
        {
            LastPrintError = "Δεν είναι διαθέσιμο το κλειδί ασφαλείας του Cloud Print Bridge.";
            if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
            return false;
        }

        try
        {
            var baseUrl = _cloudPrintBridgeUrl.EndsWith('/') ? _cloudPrintBridgeUrl : _cloudPrintBridgeUrl + "/";
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "v1/print"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cloudPrintBridgeToken);
            request.Content = JsonContent.Create(new CloudPrintBridgeRequest(
                printerAddress.ToString(), station.Port, Convert.ToBase64String(bytes), Math.Clamp(station.Copies, 1, 5)));

            var client = _httpClientFactory.CreateClient("CloudPrintBridge");
            using var response = await client.SendAsync(request);
            var result = await response.Content.ReadFromJsonAsync<CloudPrintBridgeResponse>();
            if (!response.IsSuccessStatusCode || result?.Success != true)
            {
                LastPrintError = result?.Error ?? $"Το Cloud Print Bridge επέστρεψε HTTP {(int)response.StatusCode}.";
                _logger.LogWarning("Αποτυχία relay εκτύπωσης {Kind} προς {Name} ({Ip}:{Port}): {Error}",
                    jobKind, station.Name, printerAddress, station.Port, LastPrintError);
                if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
                return false;
            }

            _logger.LogInformation("Η εκτύπωση {Kind} στάλθηκε μέσω Cloud Print Bridge στον {Name} ({Ip}:{Port})",
                jobKind, station.Name, printerAddress, station.Port);
            return true;
        }
        catch (OperationCanceledException)
        {
            LastPrintError = "Έληξε ο χρόνος απόκρισης του Cloud Print Bridge.";
            _logger.LogWarning("Timeout relay εκτύπωσης προς {Name} ({Ip}:{Port})", station.Name, printerAddress, station.Port);
            if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
            return false;
        }
        catch (Exception ex)
        {
            LastPrintError = $"Αποτυχία επικοινωνίας με το Cloud Print Bridge: {ex.Message}";
            _logger.LogError(ex, "Αποτυχία relay εκτύπωσης {Kind} προς {Name} ({Ip}:{Port})",
                jobKind, station.Name, printerAddress, station.Port);
            if (jobKind == PrintJobKind.Test) LastTestError = LastPrintError;
            return false;
        }
    }

    private sealed record CloudPrintBridgeRequest(string IpAddress, int Port, string DataBase64, int Copies);
    private sealed record CloudPrintBridgeResponse(bool Success, string? Error);

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    /// <summary>Μορφοποιεί ένα ποσό ως "12.34€" (χρησιμοποιείται σαν δεξιά στήλη).</summary>
    private static string Amount(decimal value) => $"{value:0.00}€";

    /// <summary>Δύο στήλες σε μία γραμμή σταθερού πλάτους (αριστερά/δεξιά στοίχιση).</summary>
    private static string TwoColumns(string left, string right, int width)
    {
        left ??= ""; right ??= "";
        if (left.Length + right.Length + 1 >= width)
        {
            return left.Length >= width ? left[..width] : left + " " + right;
        }
        var spaces = width - left.Length - right.Length;
        return left + new string(' ', spaces) + right;
    }

    /// <summary>Τρεις στήλες σε μία γραμμή σταθερού πλάτους: περιγραφή / ΦΠΑ / ποσό.</summary>
    private static string ThreeColumns(string left, string mid, string right, int width)
    {
        left ??= ""; mid ??= ""; right ??= "";
        var rightWidth = Math.Max(right.Length, 8);
        var midWidth = Math.Max(mid.Length, 6);
        var leftWidth = Math.Max(6, width - midWidth - rightWidth - 2);

        var l = left.Length > leftWidth ? left[..leftWidth] : left.PadRight(leftWidth);
        var m = mid.PadLeft(midWidth);
        var r = right.PadLeft(rightWidth);
        return $"{l} {m} {r}";
    }

    private static void WriteVatAnalysis(
        IEnumerable<(decimal Amount, decimal VatPercent)> source,
        int width,
        Action<string> writeLine,
        Action<string> writeEuroLine)
    {
        var rows = source.GroupBy(x => x.VatPercent).OrderBy(g => g.Key).Select(g =>
        {
            var gross = g.Sum(x => x.Amount);
            var net = g.Key <= -100m ? gross : gross / (1m + g.Key / 100m);
            return (Rate: g.Key, Net: net, Vat: gross - net);
        }).ToList();
        if (rows.Count == 0) return;
        writeLine("Ανάλυση Φ.Π.Α.");
        writeLine(ThreeColumns("", "Αξία €", "Φ.Π.Α. €", width));
        foreach (var row in rows)
        {
            writeLine($"{row.Rate:0.#}%");
            writeEuroLine(ThreeColumns("", Amount(row.Net), Amount(row.Vat), width));
        }
        var totalVat = rows.Sum(x => x.Vat);
        var totalNet = rows.Sum(x => x.Net);
        writeEuroLine(TwoColumns("Σύνολο Φ.Π.Α.", Amount(totalVat), width));
        writeEuroLine(TwoColumns("Καθαρή αξία", Amount(totalNet), width));
    }

    private static string PaymentMethodName(PaymentMethod method) => PaymentMethodInfo.DisplayName(method);

    private static IEnumerable<string> IssuerDetails(CompanySettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CompanyName)) yield return settings.CompanyName;
        if (!string.IsNullOrWhiteSpace(settings.VatNumber)) yield return $"ΑΦΜ: {settings.VatNumber}";
        if (settings.Branch > 0) yield return $"Υποκατάστημα: {settings.Branch}";
        if (!string.IsNullOrWhiteSpace(settings.TaxOffice)) yield return $"ΔΟΥ: {settings.TaxOffice}";
        if (!string.IsNullOrWhiteSpace(settings.MainActivity)) yield return settings.MainActivity;
        var address = string.Join(" ", new[] { settings.Street, settings.StreetNumber, settings.PostalCode, settings.City }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (!string.IsNullOrWhiteSpace(address)) yield return address;
        if (!string.IsNullOrWhiteSpace(settings.Country))
            yield return settings.Country.Equals("GR", StringComparison.OrdinalIgnoreCase) ? "Ελλάδα" : settings.Country;
        if (!string.IsNullOrWhiteSpace(settings.ContactPhone)) yield return $"Τηλ: {settings.ContactPhone}";
        if (!string.IsNullOrWhiteSpace(settings.ContactEmail)) yield return $"Email: {settings.ContactEmail}";
    }

    private byte[] BuildOrderTicket(
        PrinterStation station,
        string tableName,
        string waiterName,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent)> items,
        CompanySettings? companySettings,
        int? orderNumber,
        string? orderComment,
        IReadOnlyList<string?>? priorities = null)
    {
        var (escT, enc) = ResolveCodepage(station.Codepage);
        var width = station.PaperWidthChars > 0 ? station.PaperWidthChars : 42;
        using var ms = new MemoryStream();

        void Write(byte[] b) => ms.Write(b, 0, b.Length);
        void WriteText(string s) { var b = enc.GetBytes(s); ms.Write(b, 0, b.Length); }
        void WriteLine(string s = "") { WriteText(s + "\n"); }

        void WriteEuroLine(string lineEndingInEuro)
        {
            if (station.ConnectionType == "Sunmi") { WriteLine(lineEndingInEuro); return; }
            if (lineEndingInEuro.Length == 0 || lineEndingInEuro[^1] != '€')
            {
                WriteLine(lineEndingInEuro);
                return;
            }
            WriteText(lineEndingInEuro[..^1]);
            Write(SelectCp1253);
            var eb = Cp1253Encoding.GetBytes("€");
            ms.Write(eb, 0, eb.Length);
            Write(new byte[] { 0x1B, 0x74, escT });
            WriteText("\n");
        }

        Write(Init);
        if (station.ConnectionType == "Sunmi")
            Write(new byte[] { 0x1C, 0x26, 0x1C, 0x43, 0xFF }); // SUNMI multibyte UTF-8 mode
        else
            Write(new byte[] { 0x1B, 0x74, escT });

        Write(AlignCenter);
        Write(BoldOn);
        if (companySettings is not null)
        {
            if (!string.IsNullOrWhiteSpace(companySettings.CompanyName)) WriteLine(companySettings.CompanyName);
            if (!string.IsNullOrWhiteSpace(companySettings.VatNumber)) WriteLine($"ΑΦΜ: {companySettings.VatNumber}");
            if (!string.IsNullOrWhiteSpace(companySettings.TaxOffice)) WriteLine($"ΔΟΥ: {companySettings.TaxOffice}");
            if (!string.IsNullOrWhiteSpace(companySettings.MainActivity)) WriteLine(companySettings.MainActivity);
            var address = string.Join(" ", new[] { companySettings.Street, companySettings.StreetNumber }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(address) || !string.IsNullOrWhiteSpace(companySettings.PostalCode) || !string.IsNullOrWhiteSpace(companySettings.City))
                WriteLine(string.Join(" ", new[] { address, companySettings.PostalCode, companySettings.City }.Where(x => !string.IsNullOrWhiteSpace(x))));
            if (!string.IsNullOrWhiteSpace(companySettings.ContactPhone)) WriteLine($"Τηλ: {companySettings.ContactPhone}");
        }
        Write(BoldOff);
        WriteLine(new string('-', width));
        Write(AlignLeft);
        Write(DoubleSizeOn);
        Write(BoldOn);
        WriteLine($"ΤΡΑΠΕΖΙ: {tableName}");
        if (orderNumber.HasValue)
        {
            Write(DoubleSizeOn);
            Write(BoldOn);
            WriteLine($"Αρ. Παρ.: {orderNumber.Value}");
            Write(DoubleSizeOff);
            Write(BoldOff);
        }
        Write(DoubleSizeOff);
        Write(BoldOff);

        WriteLine($"Service: {waiterName}");
        WriteLine(DateTime.Now.ToString("dddd, dd/MM/yyyy HH:mm", GreekCulture));
        WriteLine(TwoColumns("Περιγραφή είδους", "Κόστος", width));
        WriteLine(new string('-', width));

        var orderItems = items.ToList();
        string? lastPriority = null;
        for (var itemIndex = 0; itemIndex < orderItems.Count; itemIndex++)
        {
            var item = orderItems[itemIndex];
            var priority = priorities is not null && itemIndex < priorities.Count ? priorities[itemIndex] : null;
            if (!string.IsNullOrWhiteSpace(priority) && !string.Equals(priority, lastPriority, StringComparison.OrdinalIgnoreCase))
            {
                WriteLine();
                Write(AlignCenter);
                Write(BoldOn);
                WriteLine($"---- {priority} ----");
                Write(BoldOff);
                Write(AlignLeft);
            }
            lastPriority = priority;
            Write(BoldOn);
            WriteEuroLine(ThreeColumns($"{item.Quantity} x {item.ItemName}", $"{item.VatPercent:0.#}%", Amount(item.LineTotal), width));
            Write(BoldOff);
            if (!string.IsNullOrWhiteSpace(item.Notes))
            {
                WriteLine($"   * {item.Notes}");
            }
        }

        WriteLine(new string('-', width));
        WriteEuroLine(TwoColumns("Πληρωτέο ποσό", Amount(orderItems.Sum(x => x.LineTotal)), width));

        if (!string.IsNullOrWhiteSpace(orderComment))
        {
            WriteLine(new string('-', width));
            Write(BoldOn);
            WriteLine("ΣΧΟΛΙΟ ΠΑΡΑΓΓΕΛΙΑΣ:");
            Write(BoldOff);
            WriteLine(orderComment.Trim());
        }

        WriteLine(new string('-', width));

        Write(Feed4);
        Write(CutSequence);

        return ms.ToArray();
    }

    private byte[] BuildOrderFormTicket(
        PrinterStation station,
        string tableName,
        string waiterName,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent)> items,
        long? mark,
        string? qrCode,
        CompanySettings? companySettings,
        string documentTitle,
        string? seriesName,
        long? documentNumber)
    {
        var (escT, enc) = ResolveCodepage(station.Codepage);
        var width = station.PaperWidthChars > 0 ? station.PaperWidthChars : 42;
        using var ms = new MemoryStream();

        void Write(byte[] b) => ms.Write(b, 0, b.Length);
        void WriteText(string s)
        {
            var b = enc.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }
        void WriteLine(string s = "") => WriteText(s + "\n");
        void WriteEuroLine(string line)
        {
            if (station.ConnectionType == "Sunmi") { WriteLine(line); return; }
            if (line.Length == 0 || line[^1] != '€') { WriteLine(line); return; }
            WriteText(line[..^1]);
            Write(SelectCp1253);
            var eb = Cp1253Encoding.GetBytes("€");
            ms.Write(eb, 0, eb.Length);
            Write(new byte[] { 0x1B, 0x74, escT });
            WriteText("\n");
        }

        Write(Init);
        if (station.ConnectionType == "Sunmi")
            Write(new byte[] { 0x1C, 0x26, 0x1C, 0x43, 0xFF }); // SUNMI multibyte UTF-8 mode
        else
            Write(new byte[] { 0x1B, 0x74, escT });

        Write(AlignCenter);
        Write(BoldOn);
        if (companySettings is not null)
        {
            if (!string.IsNullOrWhiteSpace(companySettings.CompanyName)) WriteLine(companySettings.CompanyName);
            if (!string.IsNullOrWhiteSpace(companySettings.VatNumber)) WriteLine($"ΑΦΜ: {companySettings.VatNumber}");
            if (!string.IsNullOrWhiteSpace(companySettings.TaxOffice)) WriteLine($"ΔΟΥ: {companySettings.TaxOffice}");
            if (!string.IsNullOrWhiteSpace(companySettings.MainActivity)) WriteLine(companySettings.MainActivity);
            var address = string.Join(" ", new[] { companySettings.Street, companySettings.StreetNumber }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(address) || !string.IsNullOrWhiteSpace(companySettings.PostalCode) || !string.IsNullOrWhiteSpace(companySettings.City))
                WriteLine(string.Join(" ", new[] { address, companySettings.PostalCode, companySettings.City }.Where(x => !string.IsNullOrWhiteSpace(x))));
            if (!string.IsNullOrWhiteSpace(companySettings.ContactPhone)) WriteLine($"Τηλ: {companySettings.ContactPhone}");
        }
        Write(BoldOff);
        WriteLine(new string('-', width));
        Write(DoubleSizeOn);
        Write(BoldOn);
        WriteLine(documentTitle);
        Write(DoubleSizeOff);
        Write(BoldOff);

        if (!string.IsNullOrWhiteSpace(seriesName) || documentNumber.HasValue)
            WriteLine($"Σειρά {seriesName}  ΑΑ {documentNumber?.ToString() ?? "-"}");

        WriteLine($"ΤΡΑΠΕΖΙ: {tableName}");
        WriteLine(TwoColumns("Σερβιτόρος", waiterName, width));
        WriteLine($"Ημερομηνία: {DateTime.Now.ToString("dddd, dd/MM/yyyy HH:mm:ss", GreekCulture)}");
        WriteLine(new string('-', width));

        Write(AlignLeft);

        var orderItems = items.ToList();
        WriteLine("Περιγραφή");
        WriteLine(ThreeColumns("Ποσότητα / Τιμή", "Φ.Π.Α.", "Αξία", width));
        foreach (var item in orderItems)
        {
            Write(BoldOn);
            WriteLine(item.ItemName);
            Write(BoldOff);
            var unitPrice = item.Quantity == 0 ? 0m : item.LineTotal / item.Quantity;
            WriteEuroLine(ThreeColumns($"{item.Quantity:0.00} τεμ  {unitPrice:0.00}", $"{item.VatPercent:0.#}%", Amount(item.LineTotal), width));
        }

        WriteLine(new string('-', width));
        WriteLine($"Σύνολο ποσότητας: {orderItems.Sum(x => x.Quantity):0.000}");
        WriteVatAnalysis(orderItems.Select(x => (x.LineTotal, x.VatPercent)), width, WriteLine, WriteEuroLine);
        WriteEuroLine(TwoColumns("ΣΥΝΟΛΟ", Amount(orderItems.Sum(x => x.LineTotal)), width));
        WriteLine("ΠΡΩΤΟΤΥΠΟ");

        if (mark.HasValue)
        {
            Write(AlignCenter);
            Write(BoldOn);
            WriteLine($"ΜΑΡΚ: {mark.Value}");
            Write(BoldOff);
        }

        if (!string.IsNullOrWhiteSpace(qrCode))
        {
            WriteLine();
            Write(AlignCenter);
            WriteLine("QR ΑΑΔΕ");

            var qrData = Encoding.UTF8.GetBytes(qrCode.Trim());

            if (qrData.Length <= 65532)
            {
                // QR Model 2
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00 });

                // Module size = 6
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 0x06 });

                // Error correction = M
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31 });

                // Store QR data
                var storeLen = qrData.Length + 3;
                var pL = (byte)(storeLen & 0xFF);
                var pH = (byte)((storeLen >> 8) & 0xFF);

                Write(new byte[]
                {
                    0x1D, 0x28, 0x6B,
                    pL, pH,
                    0x31, 0x50, 0x30
                });

                Write(qrData);

                // Print QR
                Write(new byte[]
                {
                    0x1D, 0x28, 0x6B,
                    0x03, 0x00,
                    0x31, 0x51, 0x30
                });
            }
            else
            {
                _logger.LogWarning(
                    "Το QR code του Order Form είναι πολύ μεγάλο: {Length} bytes",
                    qrData.Length);

                WriteLine(qrCode);
            }

            WriteLine();
        }

        if (companySettings?.UseParochosProvider == true)
        {
            if (!string.IsNullOrWhiteSpace(companySettings.FiscalProviderName)) WriteLine($"Πάροχος: {companySettings.FiscalProviderName}");
            if (!string.IsNullOrWhiteSpace(companySettings.FiscalProviderLicense)) WriteLine($"Άδεια καταλληλότητας: {companySettings.FiscalProviderLicense}");
            if (!string.IsNullOrWhiteSpace(companySettings.FiscalSoftwareInfo)) WriteLine(companySettings.FiscalSoftwareInfo);
        }

        Write(Feed4);
        Write(CutSequence);

        return ms.ToArray();
    }

    private byte[] BuildBillTicket(
        PrinterStation station,
        string tableName,
        string waiterName,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent)> items,
        decimal total,
        CompanySettings? companySettings,
        Order? order,
        string? documentLabel)
    {
        var (escT, enc) = ResolveCodepage(station.Codepage);
        var width = station.PaperWidthChars > 0 ? station.PaperWidthChars : 42;
        using var ms = new MemoryStream();

        void Write(byte[] b) => ms.Write(b, 0, b.Length);
        void WriteText(string s) { var b = enc.GetBytes(s); ms.Write(b, 0, b.Length); }
        void WriteLine(string s = "") { WriteText(s + "\n"); }

        void WriteEuroLine(string lineEndingInEuro)
        {
            if (station.ConnectionType == "Sunmi") { WriteLine(lineEndingInEuro); return; }
            if (lineEndingInEuro.Length == 0 || lineEndingInEuro[^1] != '€')
            {
                WriteLine(lineEndingInEuro);
                return;
            }
            WriteText(lineEndingInEuro[..^1]);
            Write(SelectCp1253);
            var eb = Cp1253Encoding.GetBytes("€");
            ms.Write(eb, 0, eb.Length);
            Write(new byte[] { 0x1B, 0x74, escT });
            WriteText("\n");
        }

        Write(Init);
        if (station.ConnectionType == "Sunmi")
            Write(new byte[] { 0x1C, 0x26, 0x1C, 0x43, 0xFF }); // SUNMI multibyte UTF-8 mode
        else
            Write(new byte[] { 0x1B, 0x74, escT });

        Write(AlignCenter);
        Write(BoldOn);
        if (companySettings is not null)
        {
            if (!string.IsNullOrWhiteSpace(companySettings.CompanyName)) WriteLine(companySettings.CompanyName);
            if (!string.IsNullOrWhiteSpace(companySettings.VatNumber)) WriteLine($"ΑΦΜ: {companySettings.VatNumber}");
            if (!string.IsNullOrWhiteSpace(companySettings.TaxOffice)) WriteLine($"ΔΟΥ: {companySettings.TaxOffice}");
            if (!string.IsNullOrWhiteSpace(companySettings.MainActivity)) WriteLine(companySettings.MainActivity);
            var address = string.Join(" ", new[] { companySettings.Street, companySettings.StreetNumber }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(address) || !string.IsNullOrWhiteSpace(companySettings.PostalCode) || !string.IsNullOrWhiteSpace(companySettings.City))
                WriteLine(string.Join(" ", new[] { address, companySettings.PostalCode, companySettings.City }.Where(x => !string.IsNullOrWhiteSpace(x))));
            if (!string.IsNullOrWhiteSpace(companySettings.ContactPhone)) WriteLine($"Τηλ: {companySettings.ContactPhone}");
        }
        Write(BoldOff);
        WriteLine(new string('-', width));
        Write(DoubleSizeOn);
        Write(BoldOn);
        WriteLine(documentLabel ?? "ΛΟΓΑΡΙΑΣΜΟΣ");
        Write(DoubleSizeOff);
        Write(BoldOff);
        Write(AlignLeft);

        WriteLine($"ΤΡΑΠΕΖΙ: {tableName}");
        WriteLine(TwoColumns("", waiterName, width));
        WriteLine($"Ώρα: {DateTime.Now:HH:mm:ss} {DateTime.Now:dd/MM/yyyy}");
        if (order is not null && !string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
        {
            WriteLine(new string('-', width));
            Write(BoldOn);
            WriteLine("ΣΤΟΙΧΕΙΑ ΠΕΛΑΤΗ");
            Write(BoldOff);
            if (!string.IsNullOrWhiteSpace(order.CounterpartName)) WriteLine($"Επωνυμία: {order.CounterpartName}");
            WriteLine($"ΑΦΜ: {order.CounterpartVatNumber}");
            if (!string.IsNullOrWhiteSpace(order.CounterpartTaxOffice)) WriteLine($"ΔΟΥ: {order.CounterpartTaxOffice}");
            if (!string.IsNullOrWhiteSpace(order.CounterpartMainActivity)) WriteLine($"Δραστηριότητα: {order.CounterpartMainActivity}");
            var customerAddress = string.Join(" ", new[] { order.CounterpartStreet, order.CounterpartStreetNumber, order.CounterpartPostalCode, order.CounterpartCity }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(customerAddress)) WriteLine($"Διεύθυνση: {customerAddress}");
        }
        WriteLine(new string('-', width));

        foreach (var item in items)
        {
            WriteEuroLine(ThreeColumns($"{item.Quantity} x {item.ItemName}", $"{item.VatPercent:0.#}%", Amount(item.LineTotal), width));
        }

        WriteLine(new string('-', width));
        Write(BoldOn);
        WriteEuroLine(TwoColumns("ΣΥΝΟΛΟ", Amount(total), width));
        Write(BoldOff);
        WriteLine(new string('-', width));

        Write(Feed4);
        Write(CutSequence);

        return ms.ToArray();
    }

    private byte[] BuildFiscalReceiptTicket(
        PrinterStation station,
        string tableName,
        string waiterName,
        string documentLabel,
        string seriesName,
        long invoiceNumber,
        long? mark,
        string? qrCode,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, decimal VatPercent)> items,
        decimal total,
        CompanySettings? companySettings,
        Order? order,
        int? paymentId,
        string copyLabel,
        DateTime issuedAt,
        IEnumerable<string>? relatedDocuments,
        int? transmissionFailure)
    {
        var (escT, enc) = ResolveCodepage(station.Codepage);
        var width = station.PaperWidthChars > 0 ? station.PaperWidthChars : 42;
        using var ms = new MemoryStream();

        void Write(byte[] b) => ms.Write(b, 0, b.Length);
        void WriteText(string s)
        {
            var b = enc.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }
        void WriteLine(string s = "") => WriteText(s + "\n");

        void WriteEuroLine(string line)
        {
            if (station.ConnectionType == "Sunmi") { WriteLine(line); return; }
            if (line.Length == 0 || line[^1] != '€')
            {
                WriteLine(line);
                return;
            }
            WriteText(line[..^1]);
            Write(SelectCp1253);
            var eb = Cp1253Encoding.GetBytes("€");
            ms.Write(eb, 0, eb.Length);
            Write(new byte[] { 0x1B, 0x74, escT });
            WriteText("\n");
        }

        Write(Init);
        if (station.ConnectionType == "Sunmi")
            Write(new byte[] { 0x1C, 0x26, 0x1C, 0x43, 0xFF }); // SUNMI multibyte UTF-8 mode
        else
            Write(new byte[] { 0x1B, 0x74, escT });

        Write(AlignCenter);
        Write(BoldOn);
        if (companySettings is not null)
            foreach (var issuerLine in IssuerDetails(companySettings)) WriteLine(issuerLine);
        Write(BoldOff);
        WriteLine(new string('-', width));
        Write(DoubleSizeOn);
        Write(BoldOn);
        WriteLine(documentLabel.ToUpperInvariant());
        Write(DoubleSizeOff);
        Write(BoldOff);
        WriteLine($"Σειρά {seriesName}  ΑΑ {invoiceNumber}");
        WriteLine($"Τραπέζι: {tableName}");
        WriteLine($"Ημερομηνία: {issuedAt.ToString("dddd, dd/MM/yyyy HH:mm:ss", GreekCulture)}");
        WriteLine(TwoColumns("Χειριστής", waiterName, width));
        var isInvoiceDocument = documentLabel.Contains("ΤΙΜΟΛΟΓΙΟ", StringComparison.OrdinalIgnoreCase);
        if (isInvoiceDocument && order is not null &&
            (!string.IsNullOrWhiteSpace(order.CounterpartName) || !string.IsNullOrWhiteSpace(order.CounterpartVatNumber) ||
             !string.IsNullOrWhiteSpace(order.CounterpartStreet) || !string.IsNullOrWhiteSpace(order.CounterpartStreetNumber) ||
             !string.IsNullOrWhiteSpace(order.CounterpartPostalCode) || !string.IsNullOrWhiteSpace(order.CounterpartCity) ||
             !string.IsNullOrWhiteSpace(order.CounterpartTaxOffice) || !string.IsNullOrWhiteSpace(order.CounterpartMainActivity)))
        {
            WriteLine(new string('-', width));
            Write(BoldOn);
            WriteLine("ΣΤΟΙΧΕΙΑ ΠΕΛΑΤΗ");
            Write(BoldOff);
            if (!string.IsNullOrWhiteSpace(order.CounterpartName)) WriteLine($"Επωνυμία: {order.CounterpartName}");
            if (!string.IsNullOrWhiteSpace(order.CounterpartVatNumber)) WriteLine($"ΑΦΜ: {order.CounterpartVatNumber}");
            if (!string.IsNullOrWhiteSpace(order.CounterpartTaxOffice)) WriteLine($"ΔΟΥ: {order.CounterpartTaxOffice}");
            if (!string.IsNullOrWhiteSpace(order.CounterpartMainActivity)) WriteLine($"Δραστηριότητα: {order.CounterpartMainActivity}");
            var customerAddress = string.Join(" ", new[] { order.CounterpartStreet, order.CounterpartStreetNumber, order.CounterpartPostalCode, order.CounterpartCity }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(customerAddress)) WriteLine($"Διεύθυνση: {customerAddress}");
        }
        Write(AlignLeft);
        WriteLine(new string('-', width));

        var receiptItems = items.ToList();
        WriteLine("Περιγραφή");
        WriteLine(ThreeColumns("Ποσότητα / Τιμή", "Φ.Π.Α.", "Αξία", width));
        foreach (var item in receiptItems)
        {
            WriteLine(item.ItemName);
            var unitPrice = item.Quantity == 0 ? 0m : item.LineTotal / item.Quantity;
            WriteEuroLine(ThreeColumns($"{item.Quantity:0.00} τεμ  {unitPrice:0.00}", $"{item.VatPercent:0.#}%", Amount(item.LineTotal), width));
        }

        WriteLine(new string('-', width));
        WriteLine($"Σύνολο ποσότητας: {receiptItems.Sum(x => x.Quantity):0.000}");
        WriteVatAnalysis(receiptItems.Select(x => (x.LineTotal, x.VatPercent)), width, WriteLine, WriteEuroLine);
        Write(BoldOn);
        WriteEuroLine(TwoColumns("ΣΥΝΟΛΟ", Amount(total), width));
        Write(BoldOff);
        WriteLine(new string('-', width));

        var payments = order?.Payments.AsEnumerable() ?? Enumerable.Empty<OrderPayment>();
        if (paymentId.HasValue) payments = payments.Where(p => p.Id == paymentId.Value);
        var paymentRows = payments.GroupBy(p => p.PaymentMethod).Select(g => (Method: g.Key, Amount: g.Sum(p => p.Amount))).ToList();
        if (paymentRows.Count == 0 && order is not null && order.PaymentMethod != PaymentMethod.None)
            paymentRows.Add((order.PaymentMethod, total));
        if (paymentRows.Count > 0)
        {
            WriteLine("Τρόποι πληρωμής:");
            var paymentSign = total < 0 ? -1m : 1m;
            foreach (var payment in paymentRows)
                WriteEuroLine(TwoColumns(PaymentMethodName(payment.Method), Amount(paymentSign * payment.Amount), width));
        }
        var related = relatedDocuments?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
        if (related.Count > 0)
        {
            WriteLine("Σχετικά παραστατικά:");
            foreach (var reference in related) WriteLine(reference);
        }
        if (order?.Payments.Any(p => p.Id == paymentId) == true)
        {
            var selectedPayment = order.Payments.First(p => p.Id == paymentId);
            if (!string.IsNullOrWhiteSpace(selectedPayment.PaymentSignature)) WriteLine($"Υπογραφή πληρωμής: {selectedPayment.PaymentSignature}");
            if (!string.IsNullOrWhiteSpace(selectedPayment.PaymentUid)) WriteLine($"Ταυτότητα πληρωμής: {selectedPayment.PaymentUid}");
        }
        else if (order is not null)
        {
            if (!string.IsNullOrWhiteSpace(order.ParochosPaymentSignature)) WriteLine($"Υπογραφή πληρωμής: {order.ParochosPaymentSignature}");
            if (!string.IsNullOrWhiteSpace(order.ParochosPaymentUid)) WriteLine($"Ταυτότητα πληρωμής: {order.ParochosPaymentUid}");
        }
        Write(AlignCenter);
        WriteLine(copyLabel);

        if (mark.HasValue)
        {
            Write(AlignCenter);
            Write(BoldOn);
            WriteLine($"ΜΑΡΚ: {mark.Value}");
            Write(BoldOff);
        }
        if (!string.IsNullOrWhiteSpace(order?.ParochosAuthenticationCode)) WriteLine($"Κωδ. αυθεντικοποίησης: {order.ParochosAuthenticationCode}");
        if (!string.IsNullOrWhiteSpace(order?.ParochosUid)) WriteLine($"UID: {order.ParochosUid}");

        if (!string.IsNullOrWhiteSpace(qrCode))
        {
            WriteLine();
            Write(AlignCenter);
            WriteLine("QR ΑΑΔΕ");
            var qrData = Encoding.UTF8.GetBytes(qrCode.Trim());
            if (qrData.Length <= 65532)
            {
                // Model 2
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00 });
                // Module size = 6
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 0x06 });
                // Error correction = M (48)
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31 });
                // Store QR data
                var storeLen = qrData.Length + 3;
                var pL = (byte)(storeLen & 0xFF);
                var pH = (byte)((storeLen >> 8) & 0xFF);
                Write(new byte[] { 0x1D, 0x28, 0x6B, pL, pH, 0x31, 0x50, 0x30 });
                Write(qrData);
                // Print QR
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 });
            }
            else
            {
                _logger.LogWarning("Το QR code είναι πολύ μεγάλο για ESC/POS: {Length} bytes", qrData.Length);
                WriteLine(qrCode);
            }
            WriteLine();
        }

        if (companySettings?.UseParochosProvider == true)
        {
            if (!string.IsNullOrWhiteSpace(companySettings.FiscalProviderName)) WriteLine($"Πάροχος: {companySettings.FiscalProviderName}");
            if (!string.IsNullOrWhiteSpace(companySettings.FiscalProviderLicense)) WriteLine($"Άδεια καταλληλότητας: {companySettings.FiscalProviderLicense}");
            if (!string.IsNullOrWhiteSpace(companySettings.FiscalSoftwareInfo)) WriteLine(companySettings.FiscalSoftwareInfo);
        }

        if (transmissionFailure is 1 or 2)
        {
            Write(new byte[] { 0x1B, 0x4D, 0x01 }); // ESC/POS font B
            WriteLine($"TRANSMISSION FAILURE_{transmissionFailure}");
            Write(new byte[] { 0x1B, 0x4D, 0x00 });
        }
        Write(Feed4);
        Write(CutSequence);
        return ms.ToArray();
    }


    private byte[] BuildDayCloseTicket(PrinterStation station, DayCloseRecord record)
    {
        var rows = record.Employees;
        var (escT, enc) = ResolveCodepage(station.Codepage);
        var width = station.PaperWidthChars > 0 ? station.PaperWidthChars : 42;
        using var ms = new MemoryStream();

        void Write(byte[] b) => ms.Write(b, 0, b.Length);
        void WriteText(string s) { var b = enc.GetBytes(s); ms.Write(b, 0, b.Length); }
        void WriteLine(string s = "") { WriteText(s + "\n"); }

        void WriteEuroLine(string lineEndingInEuro)
        {
            if (station.ConnectionType == "Sunmi") { WriteLine(lineEndingInEuro); return; }
            if (lineEndingInEuro.Length == 0 || lineEndingInEuro[^1] != '€')
            {
                WriteLine(lineEndingInEuro);
                return;
            }
            WriteText(lineEndingInEuro[..^1]);
            Write(SelectCp1253);
            var eb = Cp1253Encoding.GetBytes("€");
            ms.Write(eb, 0, eb.Length);
            Write(new byte[] { 0x1B, 0x74, escT });
            WriteText("\n");
        }

        Write(Init);
        if (station.ConnectionType == "Sunmi")
            Write(new byte[] { 0x1C, 0x26, 0x1C, 0x43, 0xFF }); // SUNMI multibyte UTF-8 mode
        else
            Write(new byte[] { 0x1B, 0x74, escT });

        Write(AlignCenter);
        Write(DoubleSizeOn);
        Write(BoldOn);
        WriteLine(rows.Count == 1 ? $"ΚΛΕΙΣΙΜΟ - {rows[0].EmployeeName.ToUpperInvariant()}" : "ΚΛΕΙΣΙΜΟ ΤΑΜΕΙΟΥ");
        Write(DoubleSizeOff);
        Write(BoldOff);
        WriteLine(record.ClosedAt.ToString("dd/MM/yyyy HH:mm"));
        WriteLine($"Από: {record.ClosedByName}");
        Write(AlignLeft);
        WriteLine(new string('-', width));

        foreach (var row in rows)
        {
            Write(BoldOn);
            WriteEuroLine(TwoColumns(row.EmployeeName, Amount(row.GrandTotal), width));
            Write(BoldOff);
            WriteLine($"Παραγγελίες: {row.OrderCount} | Τεμάχια: {row.HitItemCount}");
            WriteEuroLine(TwoColumns($"Χτυπήθηκαν: {row.HitTotal:0.00}€", $"Ακυρώσεις: {row.CancelledItemCount}", width));
            WriteEuroLine(TwoColumns($"Αξία ακυρώσεων: {row.CancelledTotal:0.00}€", $"Μετρητά: {row.CashTotal:0.00}€ ({row.CashPaymentCount})", width));
            WriteEuroLine(TwoColumns($"Κάρτα: {row.CardTotal:0.00}€ ({row.CardPaymentCount})", $"Πίστωση: {row.OtherPaymentTotals.GetValueOrDefault(PaymentMethod.Credit):0.00}€ ({row.CreditPaymentCount})", width));
            WriteEuroLine(TwoColumns($"IRIS: {row.OtherPaymentTotals.GetValueOrDefault(PaymentMethod.Iris):0.00}€ ({row.IrisPaymentCount})", "", width));
            foreach (var other in row.OtherPaymentTotals.Where(x => x.Key is not (PaymentMethod.Credit or PaymentMethod.Iris)).OrderBy(x => PaymentMethodInfo.GetMyDataCode(x.Key)))
                WriteEuroLine(TwoColumns($"{PaymentMethodInfo.DisplayName(other.Key)}:", Amount(other.Value), width));
            if (row.StaffConsumptionCount > 0)
            {
                WriteEuroLine(TwoColumns($"Ιδιοκατανάλωση ({row.StaffConsumptionCount}):", Amount(row.StaffConsumptionTotal), width));
                foreach (var movement in row.StaffConsumptionMovements)
                {
                    WriteEuroLine(TwoColumns($"{movement.At:HH:mm} {movement.Table} {movement.Document}", Amount(movement.Amount), width));
                    if (!string.IsNullOrWhiteSpace(movement.Items)) WriteLine(movement.Items);
                }
            }
            WriteLine(new string('-', width));
        }

        WriteLine(new string('-', width));
        WriteEuroLine(TwoColumns("Σύνολο Μετρητών:", Amount(rows.Sum(r => r.CashTotal)), width));
        WriteEuroLine(TwoColumns("Σύνολο Καρτών:", Amount(rows.Sum(r => r.CardTotal)), width));
        WriteEuroLine(TwoColumns("Σύνολο Πιστώσεων:", Amount(rows.Sum(r => r.OtherPaymentTotals.GetValueOrDefault(PaymentMethod.Credit))), width));
        WriteEuroLine(TwoColumns("Σύνολο IRIS:", Amount(rows.Sum(r => r.OtherPaymentTotals.GetValueOrDefault(PaymentMethod.Iris))), width));
        if (rows.Any(r => r.StaffConsumptionCount > 0))
            WriteEuroLine(TwoColumns("Ιδιοκατανάλωση:", Amount(rows.Sum(r => r.StaffConsumptionTotal)), width));
        WriteLine($"Ακυρώσεις ειδών: {rows.Sum(r => r.CancelledItemCount)} / {rows.Sum(r => r.CancelledTotal):0.00}€");
        if (record.PendingPreloadCount > 0)
        {
            WriteLine(new string('-', width));
            Write(BoldOn);
            WriteLine($"ΕΚΚΡΕΜΕΙΣ ΠΡΟΦΟΡΤΩΣΕΙΣ: {record.PendingPreloadCount}");
            WriteEuroLine(TwoColumns("Για τακτοποίηση:", Amount(record.PendingPreloadAmount), width));
            Write(BoldOff);
        }
        Write(BoldOn);
        WriteEuroLine(TwoColumns("ΣΥΝΟΛΟ", Amount(rows.Sum(r => r.GrandTotal)), width));
        Write(BoldOff);
        WriteLine(new string('-', width));
        WriteLine($"Εκτυπώθηκε: {DateTime.Now:HH:mm:ss dd/MM/yyyy}");

        Write(Feed4);
        Write(CutSequence);

        return ms.ToArray();
    }
}
