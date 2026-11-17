using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public sealed class RemoteLicenseClient(HttpClient httpClient, LicenseService licenses)
{
    public bool IsConfigured => licenses.EnforcementEnabled && licenses.RemoteLicensingConfigured;

    public async Task<RemoteLicenseCheckResult> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (!licenses.EnforcementEnabled)
            return new(false, "disabled", "Η απομακρυσμένη ενεργοποίηση ισχύει μόνο σε εγκατεστημένη έκδοση.", licenses.GetStatus());
        if (!licenses.RemoteLicensingConfigured)
            return new(false, "not-configured", "Δεν έχει οριστεί η διεύθυνση του cloud server αδειών.", licenses.GetStatus());

        try
        {
            var endpoint = new Uri(new Uri(licenses.LicenseServerUrl + "/", UriKind.Absolute), "api/license/v1/check");
            var request = new RemoteLicenseCheckRequest(licenses.InstallationId, licenses.MachineName, licenses.MacAddress);
            var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var nonce = ToBase64Url(RandomNumberGenerator.GetBytes(24));
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(bodyBytes)
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            message.Headers.TryAddWithoutValidation("X-License-Public-Key", licenses.RequestPublicKeyBase64);
            message.Headers.TryAddWithoutValidation("X-License-Timestamp", timestamp);
            message.Headers.TryAddWithoutValidation("X-License-Nonce", nonce);
            message.Headers.TryAddWithoutValidation("X-License-Signature",
                licenses.SignLicenseCheckRequest(timestamp, nonce, bodyBytes));
            using var response = await httpClient.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                return new(true, "error", string.IsNullOrWhiteSpace(detail)
                    ? $"Ο server αδειών επέστρεψε HTTP {(int)response.StatusCode}."
                    : $"Ο server αδειών: {detail.Trim()}", licenses.GetStatus());
            }

            if (response.Headers.TryGetValues("X-License-Auth", out var authModes) &&
                authModes.Any(mode => string.Equals(mode, "signature-v1", StringComparison.OrdinalIgnoreCase)))
                licenses.RemoveLegacyBearerTokenFile();

            var result = await response.Content.ReadFromJsonAsync<RemoteLicenseCheckResponse>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Ο server αδειών επέστρεψε κενή απάντηση.");

            if (result.License is not null)
                await licenses.ApplyRemoteLicenseAsync(result.License,
                    allowFirstServerKeyPin: new Uri(licenses.LicenseServerUrl, UriKind.Absolute).Scheme == Uri.UriSchemeHttps,
                    cancellationToken);
            else if (string.Equals(result.Status, "pending", StringComparison.OrdinalIgnoreCase))
                await licenses.ClearCachedLicenseAsync(cancellationToken);

            return new(true, result.Status, result.Message, licenses.GetStatus());
        }
        catch (HttpRequestException)
        {
            return new(false, "offline", "Δεν υπάρχει σύνδεση με τον server αδειών. Χρησιμοποιείται η τελευταία έγκυρη άδεια.", licenses.GetStatus());
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "offline", "Έληξε ο χρόνος σύνδεσης με τον server αδειών. Χρησιμοποιείται η τελευταία έγκυρη άδεια.", licenses.GetStatus());
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException or UriFormatException)
        {
            return new(true, "error", $"Δεν ήταν δυνατή η επαλήθευση της απάντησης άδειας: {exception.Message}", licenses.GetStatus());
        }
        catch (UnauthorizedAccessException exception)
        {
            return new(false, "storage-error", $"Ο POS δεν έχει δικαίωμα αποθήκευσης του αρχείου άδειας. Λεπτομέρεια: {exception.Message}", licenses.GetStatus());
        }
        catch (IOException exception)
        {
            return new(false, "storage-error", $"Η άδεια δεν αποθηκεύτηκε. Έλεγξε τον ελεύθερο χώρο και τα δικαιώματα του φακέλου εγκατάστασης. Λεπτομέρεια: {exception.Message}", licenses.GetStatus());
        }
        catch (Exception exception)
        {
            return new(false, "error", $"Απέτυχε ο έλεγχος της άδειας ({exception.GetType().Name}): {exception.Message}", licenses.GetStatus());
        }
    }

    private static string ToBase64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}

public sealed class RemoteLicenseSyncWorker(
    RemoteLicenseClient remoteLicenses,
    LicenseService licenses,
    ILogger<RemoteLicenseSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!remoteLicenses.IsConfigured) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await remoteLicenses.CheckNowAsync(stoppingToken);
                if (!result.ContactedServer)
                    logger.LogDebug("Remote license check did not reach the server: {Message}", result.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Remote license check failed; local license remains available until its signed lease expires.");
            }

            var retryDelay = licenses.GetStatus().IsLicensed
                ? TimeSpan.FromMinutes(10)
                : TimeSpan.FromSeconds(10);
            try
            {
                await Task.Delay(retryDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
