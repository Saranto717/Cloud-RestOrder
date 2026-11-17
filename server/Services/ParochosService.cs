using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RestaurantOrders.Models;
using RestaurantOrders.Services.Fiscal.Provider;
using RestaurantOrders.Services.Fiscal;

namespace RestaurantOrders.Services;

public sealed class ParochosSendResult
{
    public string? ExternalSystemId { get; init; }
    public bool Success { get; init; }
    public bool InProgress { get; init; }
    public string? ProcessId { get; init; }
    public long? Mark { get; init; }
    public string? Uid { get; init; }
    public string? AuthenticationCode { get; init; }
    public string? QrCode { get; init; }
    public string? PdfFileUrl { get; init; }
    public string? ErrorCategory { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public bool OutcomeUnknown { get; init; }
    // Parochos has returned both codes for an explicit lookup miss. 00080003
    // is the current BED response observed from /api/get ("Το παραστατικό δεν
    // βρέθηκε."). Only these explicit responses permit retrying the exact same
    // payload and ExternalSystemId; timeouts and all other errors remain safe
    // no-resend cases.
    public bool IsNotFound => ErrorCode == "00010005" ||
        (ErrorCategory == "BED" && ErrorCode == "00080003");
}

public sealed class ParochosPaymentResult
{
    public bool Success { get; init; }
    public string? ExternalSystemId { get; init; }
    public string? ProcessId { get; init; }
    public string? Uid { get; init; }
    public string? Signature { get; init; }
    public string? SignatureClearText { get; init; }
    public decimal Amount { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public string? ErrorCategory { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ParochosSimpleResult
{
    public bool Success { get; init; }
    public bool InProgress { get; init; }
    public int? ConnectedCount { get; init; }
    public string? ErrorCategory { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ParochosPointOfServiceDetail
{
    public string? ExternalSystemId { get; init; }
    public long? Mark { get; init; }
    public string? InvoiceType { get; init; }
    public decimal? TotalAmount { get; init; }
    public decimal? TransactionAmount { get; init; }
    public string? InvoiceCode { get; init; }
    public int? OrderFormStatus { get; init; }
}

public sealed class ParochosPointOfServiceResult
{
    public bool Success { get; init; }
    public string? PointOfService { get; init; }
    public decimal? Balance { get; init; }
    public int? RefNumber { get; init; }
    public List<ParochosPointOfServiceDetail> Details { get; init; } = new();
    public string? ErrorCategory { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Parochos API v3 client. Uses the url1 returned by login and always sends X-Version: 3.0.
/// The API documentation explicitly requires the url1 to be used dynamically and not hard-coded.
/// </summary>
public sealed class ParochosService : IParochosFiscalProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<ParochosService> _logger;
    private readonly FiscalProviderExchangeStore _exchangeStore;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private string? _jwt;
    private DateTime _jwtExpirationUtc;
    private string? _refreshToken;
    private DateTime _refreshExpirationUtc;
    private string? _baseUrl;
    private string? _sessionKey;

    public ParochosService(IHttpClientFactory httpClientFactory, ILogger<ParochosService> logger, FiscalProviderExchangeStore exchangeStore)
    {
        _http = httpClientFactory.CreateClient("Parochos");
        _logger = logger;
        _exchangeStore = exchangeStore;
    }

    private static string AccountBaseUrl(bool production) =>
        production ? "https://account.parochos.gr" : "https://beta-account.parochos.gr";

    public async Task<(bool Success, string? Error)> TestConnectionAsync(CompanySettings settings)
    {
        try
        {
            await LoginAsync(settings);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Αποτυχία δοκιμαστικής σύνδεσης με Parochos");
            return (false, ex.Message);
        }
    }

    private string BuildSessionKey(CompanySettings settings) =>
        $"{settings.ParochosUseProduction}|{settings.ParochosSubscriptionKey}|{settings.ParochosUsername}";

    private async Task EnsureAuthenticatedAsync(CompanySettings settings)
    {
        var sessionKey = BuildSessionKey(settings);

        await _authLock.WaitAsync();

        try
        {
            if (_sessionKey != sessionKey)
            {
                _jwt = null;
                _refreshToken = null;
                _baseUrl = null;
                _sessionKey = sessionKey;

                _jwtExpirationUtc = DateTime.MinValue;
                _refreshExpirationUtc = DateTime.MinValue;
            }

            // Αν έχουμε έγκυρο JWT, το χρησιμοποιούμε.
            if (!string.IsNullOrWhiteSpace(_jwt) &&
                _jwtExpirationUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return;
            }

            // Parochos docs (Refresh Token): όταν λήξει το JWT καλείται /api/token/Refresh
            // με το ληγμένο JWT και το refresh token. Αν αποτύχει ή έχει λήξει -> νέο login.
            if (!string.IsNullOrWhiteSpace(_jwt) &&
                !string.IsNullOrWhiteSpace(_refreshToken) &&
                !string.IsNullOrWhiteSpace(_baseUrl) &&
                _refreshExpirationUtc > DateTime.UtcNow.AddMinutes(1))
            {
                try
                {
                    await RefreshAsync(settings);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Parochos refresh απέτυχε. Γίνεται νέο login.");
                }
            }

            await LoginAsync(settings);
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task LoginAsync(CompanySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ParochosSubscriptionKey) ||
            string.IsNullOrWhiteSpace(settings.ParochosUsername) ||
            string.IsNullOrWhiteSpace(settings.ParochosPassword))
        {
            throw new InvalidOperationException(
                "Συμπλήρωσε Subscription Key, API User και API Password του Parochos.");
        }

        var url =
            $"{AccountBaseUrl(settings.ParochosUseProduction)}/api/account/loginToSubscription";

        var payload = new
        {
            subscriptionKey = settings.ParochosSubscriptionKey.Trim(),
            email = settings.ParochosUsername.Trim(),
            password = settings.ParochosPassword
        };

        using var resp = await _http.PostAsJsonAsync(url, payload, JsonOpts);

        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Parochos login: HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        _jwt =
            root.GetProperty("jwt").GetString();

        _jwtExpirationUtc =
            root.GetProperty("jwtExpiration")
                .GetDateTime()
                .ToUniversalTime();

        _refreshToken =
            root.GetProperty("jwtRefreshToken").GetString();

        _refreshExpirationUtc =
            root.GetProperty("jwtRefreshTokenExpiration")
                .GetDateTime()
                .ToUniversalTime();

        // ΠΟΛΥ ΣΗΜΑΝΤΙΚΟ:
        // Το Parochos επιστρέφει το base URL στο login response.
        _baseUrl =
            root.GetProperty("url1")
                .GetString()
                ?.TrimEnd('/');

        _logger.LogInformation(
            "PAROCHOS LOGIN OK - JWT expiration: {Expiration}, BaseUrl: {BaseUrl}",
            _jwtExpirationUtc,
            _baseUrl);

        if (string.IsNullOrWhiteSpace(_jwt) ||
            string.IsNullOrWhiteSpace(_baseUrl))
        {
            throw new InvalidOperationException(
                "Ο Parochos επέστρεψε επιτυχή σύνδεση χωρίς jwt ή url1.");
        }
    }

    private async Task RefreshAsync(CompanySettings settings)
    {
        var url = $"{AccountBaseUrl(settings.ParochosUseProduction)}/api/token/Refresh";
        var payload = new { token = _jwt, refreshToken = _refreshToken };

        using var resp = await _http.PostAsJsonAsync(url, payload, JsonOpts);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Parochos refresh απέτυχε (HTTP {(int)resp.StatusCode}). {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        _jwt = root.GetProperty("jwt").GetString();
        _jwtExpirationUtc = root.GetProperty("jwtExpiration").GetDateTime().ToUniversalTime();
        _refreshToken = root.GetProperty("jwtRefreshToken").GetString();
        _refreshExpirationUtc = root.GetProperty("jwtRefreshTokenExpiration").GetDateTime().ToUniversalTime();
    }

    public async Task<ParochosSendResult> SendAndWaitAsync(
        CompanySettings settings,
        object source,
        string externalSystemId,
        int? transmissionType = null)
    {
        try
        {
            var payload = new
            {
                externalSystemId,
                identifier = "Parochos",
                transmissionType = transmissionType ?? settings.ParochosTransmissionType,
                source
            };

            // DEBUG: log the exact invoice header that is sent to Parochos.
            // This lets us verify the actual series/AA in the final JSON payload.
            try
            {
                if (source is Dictionary<string, object?> sourceDict &&
                    sourceDict.TryGetValue("invoice", out var invoiceObj) &&
                    invoiceObj is Dictionary<string, object?> invoiceDict &&
                    invoiceDict.TryGetValue("invoiceHeader", out var headerObj) &&
                    headerObj is Dictionary<string, object?> headerDict)
                {
                    headerDict.TryGetValue("series", out var sentSeries);
                    headerDict.TryGetValue("aa", out var sentAa);

                    _logger.LogWarning(
                        "PAROCHOS SEND DEBUG >>> ExternalSystemId={ExternalSystemId} | Series={Series} | AA={AA}",
                        externalSystemId,
                        sentSeries,
                        sentAa);
                }
            }
            catch (Exception debugEx)
            {
                _logger.LogWarning(debugEx, "PAROCHOS SEND DEBUG failed");
            }

            var result = await PostSignedAsync(settings, "/api/send", payload, ParochosResponseParser.ParseSendResponse);
            if (!result.InProgress)
                return result;

            for (var i = 0; i < settings.ParochosPollingAttempts; i++)
            {
                await Task.Delay(settings.ParochosPollingDelayMs);
                result = await PostSignedAsync(
                    settings,
                    "/api/get",
                    new { externalSystemId },
                    ParochosResponseParser.ParseSendResponse);

                if (!result.InProgress)
                    return result;
            }

            return new ParochosSendResult
            {
                Success = false,
                InProgress = true,
                ErrorMessage = "Ο Parochos δεν ολοκλήρωσε την επεξεργασία μέσα στο επιτρεπόμενο χρονικό διάστημα."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Σφάλμα αποστολής παραστατικού στον Parochos");
            var transport = ex is OperationCanceledException || ex is HttpRequestException http &&
                (http.StatusCode is null || (int)http.StatusCode >= 500 || (int)http.StatusCode == 408);
            return new ParochosSendResult { Success = false, OutcomeUnknown = transport,
                ErrorCategory = transport ? "NETWORK" : "LOCAL", ErrorMessage = "Δεν επιβεβαιώθηκε η αποστολή στον πάροχο." };
        }
    }

    /// <summary>
    /// Checks whether Parochos already has the document for this external ID.
    /// Used to recover a card receipt after a duplicate invoice-number response
    /// without submitting the signed document or charging the POS again.
    /// </summary>
    public Task<ParochosSendResult> GetDocumentByProcessIdAsync(CompanySettings settings, string processId) =>
        PostSignedAsync(settings, "/api/get", new { processId }, ParochosResponseParser.ParseSendResponse);

    public async Task<ParochosSendResult> GetDocumentByExternalSystemIdAsync(
        CompanySettings settings,
        string externalSystemId)
    {
        try
        {
            var result = await PostSignedAsync(
                settings,
                "/api/get",
                new { externalSystemId },
                ParochosResponseParser.ParseSendResponse);
            if (!result.InProgress)
                return result;

            for (var i = 0; i < settings.ParochosPollingAttempts; i++)
            {
                await Task.Delay(settings.ParochosPollingDelayMs);
                result = await PostSignedAsync(
                    settings,
                    "/api/get",
                    new { externalSystemId },
                    ParochosResponseParser.ParseSendResponse);
                if (!result.InProgress)
                    return result;
            }

            return new ParochosSendResult
            {
                InProgress = true,
                ProcessId = result.ProcessId,
                ErrorMessage = "Το παραστατικό παραμένει υπό επεξεργασία στον Parochos."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Αποτυχία ανάκτησης παραστατικού Parochos για ExternalSystemId {ExternalSystemId}",
                externalSystemId);
            return new ParochosSendResult
            {
                Success = false,
                OutcomeUnknown = true,
                ErrorMessage = "Δεν ήταν δυνατός ο έλεγχος κατάστασης του παραστατικού στον Parochos."
            };
        }
    }

    /// <summary>
    /// Creates the provider payment signature. For EFT-POS (type 7) this must happen before Send.
    /// The returned signature then has to be committed to the POS terminal by the POS integration.
    /// </summary>
    public async Task<ParochosPaymentResult> RequestPaymentAsync(
        CompanySettings settings,
        string externalSystemId,
        string issuerVatNumber,
        DateTimeOffset invoiceIssueDate,
        int companyBranch,
        string invoiceType,
        string invoiceSeries,
        long invoiceNumber,
        decimal netValue,
        decimal vatAmount,
        decimal totalValue,
        decimal paymentAmount,
        string terminalId,
        string nspCode,
        long? mark = null)
    {
        try
        {
            await EnsureAuthenticatedAsync(settings);

            var payload = new Dictionary<string, object?>
            {
                ["externalSystemId"] = externalSystemId,
                ["issuerVatNumber"] = issuerVatNumber,
                ["invoiceIssueDate"] = invoiceIssueDate.ToString("yyyy-MM-dd'T'00:00:00"),
                ["companyBranch"] = companyBranch.ToString(),
                ["invoiceType"] = invoiceType,
                ["invoiceSeries"] = invoiceSeries,
                ["invoiceAA"] = invoiceNumber.ToString(),
                ["netValue"] = netValue,
                ["vatAmount"] = vatAmount,
                ["totalValue"] = totalValue,
                ["paymentAmount"] = paymentAmount,
                ["terminalId"] = terminalId,
                ["specialInvoiceCategory"] = settings.ParochosSpecialInvoiceCategory,
                ["nspCode"] = nspCode
            };
            if (mark.HasValue) payload["mark"] = mark.Value;

            return await PostSignedAsync(
                settings,
                "/api/requestPayment",
                payload,
                ParochosResponseParser.ParsePaymentResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Σφάλμα requestPayment στον Parochos");
            return new ParochosPaymentResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Registers an EFT-POS transaction after the fiscal document already has a MARK.</summary>
    public async Task<ParochosSimpleResult> SubmitPaymentAsync(
        CompanySettings settings,
        string externalSystemId,
        long mark,
        decimal amount,
        string transactionId,
        string signature,
        string terminalId,
        string? paymentMethodInfo = null,
        int paymentType = 7)
    {
        if (mark <= 0 || amount <= 0m || string.IsNullOrWhiteSpace(externalSystemId) ||
            string.IsNullOrWhiteSpace(transactionId) || string.IsNullOrWhiteSpace(signature) ||
            string.IsNullOrWhiteSpace(terminalId))
            return new ParochosSimpleResult { Success = false, ErrorMessage = "Λείπουν υποχρεωτικά στοιχεία SubmitPayment (MARK, ποσό, ExternalSystemId, συναλλαγή, υπογραφή ή TID)." };

        try
        {
            await EnsureAuthenticatedAsync(settings);
            var existing = await GetPaymentSubmissionAsync(settings, externalSystemId, transactionId, signature);
            if (existing is null)
                return new ParochosSimpleResult { Success = false, ErrorMessage = "Δεν ήταν δυνατός ο έλεγχος της κατάστασης πληρωμής στον πάροχο. Δεν έγινε νέα αποστολή, ώστε να αποφευχθεί διπλή καταχώριση." };
            if (existing.Status == 1)
                return new ParochosSimpleResult { Success = true };
            if (existing.Status == 3 ||
                (existing.Status == 0 && string.Equals(existing.TransactionId, transactionId, StringComparison.Ordinal)))
            {
                var settled = await WaitForSubmittedPaymentAsync(settings, externalSystemId, transactionId, signature);
                if (settled?.Status == 1) return new ParochosSimpleResult { Success = true };
                return new ParochosSimpleResult { Success = false, InProgress = true, ErrorMessage = "Η καταχώριση της πληρωμής παραμένει σε εξέλιξη στον πάροχο. Έλεγξε ξανά την κατάσταση· δεν στάλθηκε δεύτερη πληρωμή." };
            }
            if (existing.Status is 2 or 10)
                return new ParochosSimpleResult { Success = false, ErrorMessage = existing.Status == 2
                    ? "Η υπογραφή πληρωμής έχει ακυρωθεί στον πάροχο. Δεν υποβλήθηκε η πληρωμή."
                    : "Η υπογραφή πληρωμής έχει λήξει. Δεν υποβλήθηκε η πληρωμή." };

            var paymentInfo = new Dictionary<string, object?>
            {
                ["type"] = paymentType is 5 or 7 or 8 ? paymentType : 7,
                ["amount"] = amount,
                ["transactionId"] = transactionId.Trim(),
                ["signature"] = signature.Trim(),
                ["terminalId"] = terminalId.Trim(),
                ["tipAmount"] = 0m
            };
            if (!string.IsNullOrWhiteSpace(paymentMethodInfo))
                paymentInfo["paymentMethodInfo"] = paymentMethodInfo.Trim();
            var payload = new
            {
                externalSystemId,
                mark,
                paymentInfo = new[] { paymentInfo }
            };
            var submission = await PostSignedAsync(settings, "/api/submitPayment", payload, ParochosResponseParser.ParseSimpleResult);
            if (submission.Success) return submission;
            if (submission.InProgress)
            {
                var settled = await WaitForSubmittedPaymentAsync(settings, externalSystemId, transactionId, signature);
                if (settled?.Status == 1) return new ParochosSimpleResult { Success = true };
                return new ParochosSimpleResult { Success = false, InProgress = true, ErrorMessage = "Ο πάροχος παρέλαβε την πληρωμή και συνεχίζει την καταχώριση. Δεν χρειάζεται νέα χρέωση." };
            }

            // The HTTP response may have been lost after AADE accepted the payment.
            // Reconcile by the exact signature and POS transaction before reporting failure.
            var reconciled = await GetPaymentSubmissionAsync(settings, externalSystemId, transactionId, signature);
            if (reconciled?.Status == 1) return new ParochosSimpleResult { Success = true };
            if (reconciled?.Status == 3)
                return new ParochosSimpleResult { Success = false, InProgress = true, ErrorMessage = "Ο πάροχος έχει παραλάβει την πληρωμή και η καταχώριση εκκρεμεί. Δεν χρειάζεται νέα χρέωση." };
            return submission;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Σφάλμα submitPayment στον Parochos για MARK {Mark}", mark);
            return new ParochosSimpleResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private sealed record PaymentSubmissionStatus(int Status, string? TransactionId, string? Signature);

    private async Task<PaymentSubmissionStatus?> GetPaymentSubmissionAsync(
        CompanySettings settings, string externalSystemId, string transactionId, string signature)
    {
        try
        {
            await EnsureAuthenticatedAsync(settings);
            var url = $"{_baseUrl}/api/getPayments/{Uri.EscapeDataString(externalSystemId)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
            request.Headers.Add("X-Version", "3.0");
            using var response = await _http.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _jwtExpirationUtc = DateTime.MinValue;
                await EnsureAuthenticatedAsync(settings);
                using var retry = new HttpRequestMessage(HttpMethod.Get, url);
                retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
                retry.Headers.Add("X-Version", "3.0");
                using var retryResponse = await _http.SendAsync(retry);
                if (!retryResponse.IsSuccessStatusCode) return null;
                return ParsePaymentSubmissionStatus(await retryResponse.Content.ReadAsStringAsync(), transactionId, signature);
            }
            if (!response.IsSuccessStatusCode) return null;
            return ParsePaymentSubmissionStatus(await response.Content.ReadAsStringAsync(), transactionId, signature);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Δεν ήταν δυνατή η ανάκτηση πληρωμών Parochos για ExternalSystemId {ExternalSystemId}", externalSystemId);
            return null;
        }
    }

    private static PaymentSubmissionStatus? ParsePaymentSubmissionStatus(string json, string transactionId, string signature)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("payments", out var payments) || payments.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var payment in payments.EnumerateArray())
        {
            var paymentTransaction = payment.TryGetProperty("transactionId", out var tx) ? tx.GetString() : null;
            var paymentSignature = payment.TryGetProperty("signature", out var sig) ? sig.GetString() : null;
            var sameTransaction = string.Equals(paymentTransaction, transactionId, StringComparison.Ordinal);
            var sameSignature = string.Equals(paymentSignature, signature, StringComparison.OrdinalIgnoreCase);
            if (!sameTransaction && !sameSignature) continue;
            var status = payment.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var parsed)
                ? parsed : -1;
            return new PaymentSubmissionStatus(status, paymentTransaction, paymentSignature);
        }
        // No matching transaction means the signature is still available for its first submission.
        return new PaymentSubmissionStatus(0, null, null);
    }

    private async Task<PaymentSubmissionStatus?> WaitForSubmittedPaymentAsync(
        CompanySettings settings, string externalSystemId, string transactionId, string signature)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        PaymentSubmissionStatus? status = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            status = await GetPaymentSubmissionAsync(settings, externalSystemId, transactionId, signature);
            if (status is null || status.Status is 1 or 2 or 10) return status;
        }
        return status;
    }

    public async Task<ParochosSimpleResult> SetPointOfServiceStateAsync(
        CompanySettings settings,
        string pointOfService,
        int actionKind,
        string? targetPointOfService = null)
    {
        try
        {
            await EnsureAuthenticatedAsync(settings);
            var payload = new
            {
                companyBranchCode = settings.Branch.ToString(),
                pointOfService,
                targetPointOfService = targetPointOfService ?? "",
                actionKind
            };
            return await PostSignedAsync(settings, "/api/pointOfService/SetState", payload, ParochosResponseParser.ParseSimpleResult);
        }
        catch (Exception ex)
        {
            return new ParochosSimpleResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public Task<ParochosSimpleResult> OpenPointOfServiceAsync(CompanySettings settings, string pointOfService) =>
        SetPointOfServiceStateAsync(settings, pointOfService, 0);

    public Task<ParochosSimpleResult> ClosePointOfServiceAsync(CompanySettings settings, string pointOfService) =>
        SetPointOfServiceStateAsync(settings, pointOfService, 1);

    /// <summary>
    /// Reads the current Point of Service details from Parochos.
    /// The official v3 API returns the current balance plus the documents
    /// contributing to that Point of Service.
    /// </summary>
    public async Task<ParochosPointOfServiceResult> ListPointOfServiceDetailsAsync(
        CompanySettings settings,
        string pointOfService,
        int filter = 0)
    {
        try
        {
            await EnsureAuthenticatedAsync(settings);

            var payload = new
            {
                companyBranchCode = settings.Branch.ToString(),
                refNumber = 0,
                pointOfService,
                filter
            };

            return await PostSignedAsync(
                settings,
                "/api/pointOfService/ListDetails",
                payload,
                json => ParochosResponseParser.ParsePointOfServiceDetailsResult(json, pointOfService));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Σφάλμα ListDetails για PointOfService {PointOfService}",
                pointOfService);

            return new ParochosPointOfServiceResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<ParochosSimpleResult> AssignDocumentAsync(
        CompanySettings settings,
        string pointOfService,
        string processId,
        string externalSystemId)
    {
        try
        {
            await EnsureAuthenticatedAsync(settings);
            var payload = new
            {
                companyBranchCode = settings.Branch.ToString(),
                pointOfService,
                processId,
                externalSystemId
            };
            return await PostSignedAsync(settings, "/api/pointOfService/AssignDocument", payload, ParochosResponseParser.ParseSimpleResult);
        }
        catch (Exception ex)
        {
            return new ParochosSimpleResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<ParochosSimpleResult> ConnectDocumentsAsync(
        CompanySettings settings,
        string? orderFormProcessId,
        string? orderFormExternalSystemId,
        IEnumerable<long> documentMarks)
    {
        try
        {
            await EnsureAuthenticatedAsync(settings);
            var marks = documentMarks.Where(x => x > 0).Select(x => x.ToString()).ToList();
            if (string.IsNullOrWhiteSpace(orderFormProcessId) && string.IsNullOrWhiteSpace(orderFormExternalSystemId))
                return new ParochosSimpleResult { Success = false, ErrorMessage = "Λείπει processId ή externalSystemId του Order Form." };
            if (marks.Count == 0)
                return new ParochosSimpleResult { Success = false, ErrorMessage = "Δεν υπάρχει MARK τελικού παραστατικού για σύνδεση." };

            var payload = new
            {
                processId = orderFormProcessId,
                externalSystemId = orderFormExternalSystemId,
                documentMarks = marks
            };
            return await PostSignedAsync(settings, "/api/OrderForms/ConnectDocuments", payload, ParochosResponseParser.ParseConnectResult);
        }
        catch (Exception ex)
        {
            return new ParochosSimpleResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private async Task<T> PostSignedAsync<T>(
        CompanySettings settings,
        string path,
        object payload,
        Func<string, T> parser)
    {
        try { await EnsureAuthenticatedAsync(settings); }
        catch
        {
            if (path.Equals("/api/send", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/get", StringComparison.OrdinalIgnoreCase))
                await SaveFiscalExchangeAsync(settings, path, DateTime.UtcNow, JsonSerializer.Serialize(payload, JsonOpts), null, null, "NotSentAuthentication");
            throw;
        }
        var (status, body) = await PostOnceAsync(settings, path, payload);

        // 401 = JWT invalid/expired: ξανά αυθεντικοποίηση και μία επανάληψη.
        if (status == 401)
        {
            _jwtExpirationUtc = DateTime.MinValue;
            await EnsureAuthenticatedAsync(settings);
            (status, body) = await PostOnceAsync(settings, path, payload);
        }

        if (status < 200 || status > 299)
        {
            return parser(JsonSerializer.Serialize(new
            {
                status = 2,
                success = false,
                errorCategory = "HTTP",
                errorCode = status.ToString(),
                errorMessage = body,
                outcomeUnknown = status >= 500 || status == 408
            }));
        }

        return parser(body);
    }

    private async Task<(int Status, string Body)> PostOnceAsync(CompanySettings settings, string path, object payload)
    {
        var url = $"{_baseUrl}{path}";
        var recordExchange = path.Equals("/api/send", StringComparison.OrdinalIgnoreCase) ||
                             path.Equals("/api/get", StringComparison.OrdinalIgnoreCase);
        var requestJson = recordExchange ? JsonSerializer.Serialize(payload, JsonOpts) : null;
        var attemptedAt = DateTime.UtcNow;
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = requestJson is null ? JsonContent.Create(payload, options: JsonOpts) :
                new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
        request.Headers.Add("X-Version", "3.0");

        try
        {
            using var resp = await _http.SendAsync(request);
            var body = await resp.Content.ReadAsStringAsync();
            if (recordExchange)
                await SaveFiscalExchangeAsync(settings, path, attemptedAt, requestJson!, (int)resp.StatusCode, body, null);
            _logger.LogInformation("PAROCHOS RESPONSE - URL: {Url} - HTTP: {StatusCode}", url, (int)resp.StatusCode);
            return ((int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            if (recordExchange)
                await SaveFiscalExchangeAsync(settings, path, attemptedAt, requestJson!, null, null, ex.GetType().Name);
            throw;
        }
    }

    private async Task SaveFiscalExchangeAsync(CompanySettings settings, string path, DateTime attemptedAt,
        string requestJson, int? status, string? responseBody, string? transportError)
    {
        static string? ReadId(string? json, string name)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
            }
            catch (JsonException) { return null; }
        }
        await _exchangeStore.RecordAsync(new FiscalProviderExchange
        {
            AttemptedAtUtc = attemptedAt, Operation = path, Production = settings.ParochosUseProduction,
            ExternalSystemId = ReadId(requestJson, "externalSystemId") ?? ReadId(responseBody, "externalSystemId"),
            ProcessId = ReadId(requestJson, "processId") ?? ReadId(responseBody, "processId"),
            RequestJson = requestJson, HttpStatus = status, ResponseBody = responseBody, TransportError = transportError
        });
    }
}
