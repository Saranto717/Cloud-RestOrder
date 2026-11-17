package gr.restaurantorders.sunmiclient;

import android.content.Context;
import android.net.Uri;
import android.webkit.JavascriptInterface;
import android.webkit.WebView;
import android.util.Base64;

import org.json.JSONObject;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

import dev.duma.android.sunmi.printerstatusbroadcastreceiver.IPrinterStatusBroadcastReceiver.PrinterStatusCallback;
import dev.duma.capacitor.sunmiprinter.SunmiPrinter;
import dev.duma.capacitor.sunmiprinter.SunmiCallbackHelper;

/** Native printer bridge for the top-level RestaurantOrders WebView page. */
public final class SunmiNativePrinterBridge {
    private final Context context;
    private final WebView webView;
    private final SunmiPrinter printer;
    private volatile String allowedServerOrigin = "";
    private volatile boolean registered;

    public SunmiNativePrinterBridge(Context context, WebView webView) {
        this.context = context.getApplicationContext();
        this.webView = webView;
        this.printer = new SunmiPrinter(this.context, new NoopPrinterStatusCallback());
    }

    /** Called while the trusted Capacitor connection form is still open. */
    @JavascriptInterface
    public String configureServer(String serverUrl) {
        try {
            Uri caller = Uri.parse(currentPageUrl());
            if (!"localhost".equalsIgnoreCase(caller.getHost()))
                return result(false, "Η διεύθυνση server μπορεί να οριστεί μόνο από την εφαρμογή SUNMI.");

            Uri target = Uri.parse(serverUrl == null ? "" : serverUrl.trim());
            String scheme = target.getScheme();
            if (!("http".equalsIgnoreCase(scheme) || "https".equalsIgnoreCase(scheme))
                    || target.getHost() == null || target.getUserInfo() != null)
                return result(false, "Η διεύθυνση server δεν είναι έγκυρη.");

            allowedServerOrigin = origin(target);
            return result(true, "");
        } catch (Exception ex) {
            return result(false, "Η διεύθυνση server δεν είναι έγκυρη.");
        }
    }

    @JavascriptInterface
    public String prepare() {
        try {
            requireTrustedServerPage();
            ensurePrinterReady();
            return result(true, "");
        } catch (Exception ex) {
            return result(false, message(ex));
        }
    }

    @JavascriptInterface
    public String printRawBase64(String base64, int copies) {
        try {
            requireTrustedServerPage();
            ensurePrinterReady();
            byte[] bytes = Base64.decode(base64 == null ? "" : base64, Base64.DEFAULT);
            if (bytes.length == 0) throw new IllegalArgumentException("Δεν υπάρχουν δεδομένα για εκτύπωση.");

            int safeCopies = Math.max(1, Math.min(5, copies));
            for (int i = 0; i < safeCopies; i++) {
                CountDownLatch done = new CountDownLatch(1);
                AtomicBoolean succeeded = new AtomicBoolean(false);
                printer.getEscPosCommands().sendRAWData(bytes, printer.getCallbackHelper().make(ok -> {
                    succeeded.set(ok);
                    done.countDown();
                }));
                if (!done.await(45, TimeUnit.SECONDS)) throw new IllegalStateException("Έληξε ο χρόνος αναμονής του εκτυπωτή SUNMI.");
                if (!succeeded.get()) throw new IllegalStateException("Ο εκτυπωτής SUNMI απέτυχε να εκτυπώσει.");
            }
            return result(true, "");
        } catch (Exception ex) {
            if (ex instanceof InterruptedException) Thread.currentThread().interrupt();
            return result(false, message(ex));
        }
    }

    public void release() {
        if (!registered) return;
        try { printer.unregister(); } catch (Exception ignored) { }
        registered = false;
    }

    private synchronized void ensurePrinterReady() throws Exception {
        if (!registered) {
            printer.register();
            registered = true;
        }
        CountDownLatch initialized = new CountDownLatch(1);
        AtomicBoolean succeeded = new AtomicBoolean(false);
        printer.getInitializationAndSettings().printerInit(printer.getCallbackHelper().make(ok -> {
            succeeded.set(ok);
            initialized.countDown();
        }));
        if (!initialized.await(12, TimeUnit.SECONDS))
            throw new IllegalStateException("Έληξε ο χρόνος αρχικοποίησης του εκτυπωτή SUNMI.");
        if (!succeeded.get()) throw new IllegalStateException("Δεν έγινε αρχικοποίηση του εκτυπωτή SUNMI.");
    }

    private void requireTrustedServerPage() {
        String current = currentPageUrl();
        if (allowedServerOrigin.isEmpty() || current == null || !allowedServerOrigin.equals(origin(Uri.parse(current))))
            throw new SecurityException("Η εκτύπωση επιτρέπεται μόνο από τον server που έχει δηλωθεί στην εφαρμογή SUNMI.");
    }

    private String currentPageUrl() {
        AtomicReference<String> current = new AtomicReference<>(null);
        CountDownLatch read = new CountDownLatch(1);
        webView.post(() -> {
            current.set(webView.getUrl());
            read.countDown();
        });
        try {
            if (!read.await(5, TimeUnit.SECONDS)) return null;
            return current.get();
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
            return null;
        }
    }

    private static String origin(Uri uri) {
        String scheme = uri.getScheme();
        String host = uri.getHost();
        if (scheme == null || host == null) return "";
        String value = scheme.toLowerCase() + "://" + host.toLowerCase();
        int port = uri.getPort();
        if (port >= 0 && !("http".equalsIgnoreCase(scheme) && port == 80)
                && !("https".equalsIgnoreCase(scheme) && port == 443)) value += ":" + port;
        return value;
    }

    private static String result(boolean ok, String error) {
        JSONObject json = new JSONObject();
        try { json.put("ok", ok); json.put("error", error == null ? "" : error); }
        catch (Exception ignored) { return "{\"ok\":false,\"error\":\"Bridge error\"}"; }
        return json.toString();
    }

    private static String message(Exception ex) {
        return ex.getMessage() == null || ex.getMessage().trim().isEmpty()
                ? "Αποτυχία επικοινωνίας με τον εκτυπωτή SUNMI." : ex.getMessage();
    }

    private static final class NoopPrinterStatusCallback implements PrinterStatusCallback {
        @Override public void onInit() { }
        @Override public void onNormal() { }
        @Override public void onError() { }
        @Override public void onOutOfPaper() { }
        @Override public void onOverheating() { }
        @Override public void onNormalHeating() { }
        @Override public void onCoverOpen() { }
        @Override public void onCoverError() { }
        @Override public void onCutterStuck() { }
        @Override public void onCutterError() { }
        @Override public void onFirmwareUpdate() { }
        @Override public void onFirmwareFailure() { }
        @Override public void onPrinterUndetected() { }
        @Override public void onBlackMarkUndetected() { }
    }
}
