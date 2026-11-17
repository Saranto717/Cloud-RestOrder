using System.Text.Json;

namespace RestaurantOrders.Services;

/// <summary>Parses Parochos wire responses into application-facing result objects.</summary>
public static class ParochosResponseParser
{
    public static ParochosSendResult ParseSendResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = GetInt(root, "status") ?? 0;
        var signing = root.TryGetProperty("signing", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;

        long? mark = GetLong(signing, "mark");
        return new ParochosSendResult
        {
            ExternalSystemId = GetString(root, "externalSystemId"),
            Success = status == 1,
            InProgress = status == 0,
            ProcessId = GetString(root, "processId"),
            Mark = mark,
            Uid = GetString(signing, "uid"),
            AuthenticationCode = GetString(signing, "authenticationCode"),
            QrCode = GetString(signing, "qrCode") ?? GetString(signing, "qrUrlAADE"),
            PdfFileUrl = GetString(signing, "pdfFileUrl"),
            ErrorCategory = GetString(root, "errorCategory"),
            ErrorCode = GetString(root, "errorCode"),
            ErrorMessage = status == 2 ? GetString(root, "errorMessage") ?? "Το παραστατικό απορρίφθηκε από τον Parochos/ΑΑΔΕ." : null,
            OutcomeUnknown = GetBool(root, "outcomeUnknown") ?? false
        };
    }

    public static ParochosPaymentResult ParsePaymentResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = GetInt(root, "status") ?? 0;
        var token = root.TryGetProperty("paymentToken", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        return new ParochosPaymentResult
        {
            Success = status == 1,
            ExternalSystemId = GetString(root, "externalSystemId"),
            ProcessId = GetString(root, "processId"),
            Uid = GetString(root, "uid"),
            Signature = GetString(token, "signature"),
            SignatureClearText = GetString(token, "signatureClearText"),
            Amount = GetDecimal(token, "amount") ?? 0m,
            Timestamp = GetDateTimeOffset(token, "timestamp"),
            ErrorCategory = GetString(root, "errorCategory"),
            ErrorCode = GetString(root, "errorCode"),
            ErrorMessage = status == 2 ? GetString(root, "errorMessage") ?? "Το requestPayment απέτυχε." : null
        };
    }

    public static ParochosSimpleResult ParseSimpleResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = GetInt(root, "status");
        return new ParochosSimpleResult
        {
            Success = GetBool(root, "success") ?? (status == 1),
            InProgress = status == 0,
            ErrorCategory = GetString(root, "errorCategory"),
            ErrorCode = GetString(root, "errorCode"),
            ErrorMessage = GetString(root, "errorMessage")
        };
    }

    public static ParochosSimpleResult ParseConnectResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = GetInt(root, "status");
        var connectedCount = GetInt(root, "connectedCount");
        return new ParochosSimpleResult
        {
            // Parochos defines status=1 as Completed. A zero connectedCount
            // does not by itself mean that the request failed: the accounting
            // document may already have been correlated during /api/send via
            // invoice.correlatedInvoices, so ConnectDocuments has nothing new
            // to connect. Keep the count for diagnostics, but use the documented
            // completion status to decide whether the API operation failed.
            Success = status == 1,
            ConnectedCount = connectedCount,
            ErrorCategory = GetString(root, "errorCategory"),
            ErrorCode = GetString(root, "errorCode"),
            ErrorMessage = GetString(root, "errorMessage")
        };
    }

    public static ParochosPointOfServiceResult ParsePointOfServiceDetailsResult(string json, string? expectedPointOfService = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new ParochosPointOfServiceResult
        {
            Success = GetBool(root, "success") ?? false,
            ErrorCategory = GetString(root, "errorCategory"),
            ErrorCode = GetString(root, "errorCode"),
            ErrorMessage = GetString(root, "errorMessage")
        };

        if (!result.Success)
            return result;

        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return new ParochosPointOfServiceResult { Success = false, ErrorMessage = "Ο Parochos δεν επέστρεψε έγκυρη λίστα στοιχείων τραπεζιού." };

        var first = data.EnumerateArray().FirstOrDefault(row =>
            expectedPointOfService is null || string.Equals(GetString(row, "pointOfService")?.Trim(), expectedPointOfService.Trim(), StringComparison.OrdinalIgnoreCase));
        if (first.ValueKind != JsonValueKind.Object && data.GetArrayLength() > 0)
            return new ParochosPointOfServiceResult { Success = false, ErrorMessage = "Η λίστα Parochos δεν περιέχει το ζητούμενο τραπέζι. Δεν επιβεβαιώνεται κλείσιμο ή μεταφορά." };
        if (first.ValueKind == JsonValueKind.Object)
        {
            result = new ParochosPointOfServiceResult
            {
                Success = true,
                PointOfService = GetString(first, "pointOfService"),
                Balance = GetDecimal(first, "balance"),
                RefNumber = GetInt(first, "refNumber"),
                ErrorCategory = GetString(root, "errorCategory"),
                ErrorCode = GetString(root, "errorCode"),
                ErrorMessage = GetString(root, "errorMessage")
            };

            if (first.TryGetProperty("details", out var details) &&
                details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    result.Details.Add(new ParochosPointOfServiceDetail
                    {
                        ExternalSystemId = GetString(detail, "externalSystemId"),
                        Mark = GetLong(detail, "mark"),
                        InvoiceType = GetString(detail, "invoiceType"),
                        TotalAmount = GetDecimal(detail, "totalAmount"),
                        TransactionAmount = GetDecimal(detail, "transactionAmount"),
                        InvoiceCode = GetString(detail, "invoiceCode"),
                        OrderFormStatus = GetInt(detail, "orderFormStatus")
                    });
                }
            }
        }

        return result;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private static long? GetLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : null;

    private static decimal? GetDecimal(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var v) ? v : null;

    private static bool? GetBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string name)
    {
        var s = GetString(element, name);
        return DateTimeOffset.TryParse(s, out var value) ? value : null;
    }
}
