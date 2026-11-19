package gr.restaurantorders.sunmiclient;

import android.content.Context;
import android.net.Uri;
import android.webkit.JavascriptInterface;
import android.webkit.WebView;
import android.util.Base64;

import org.json.JSONObject;

import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
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
                return result(false, "Η διεύθυνση server μπορεί να οριστεί μόνο από την εφαρμογή RestaurantOrders.");

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

    /** Send raw ESC/POS bytes from this Android device to a printer on its local network. */
    @JavascriptInterface
    public String printTcpRawBase64(String ipAddress, int port, String base64, int copies) {
        try {
            requireTrustedServerPage();
            InetAddress address = parseLocalPrinterAddress(ipAddress);
            if (port < 1 || port > 65535) throw new IllegalArgumentException("Η θύρα εκτυπωτή δεν είναι έγκυρη.");

            byte[] bytes = Base64.decode(base64 == null ? "" : base64, Base64.DEFAULT);
            if (bytes.length == 0) throw new IllegalArgumentException("Δεν υπάρχουν δεδομένα για εκτύπωση.");

            int safeCopies = Math.max(1, Math.min(5, copies));
            try (Socket socket = new Socket()) {
                socket.connect(new InetSocketAddress(address, port), 5000);
                socket.setSoTimeout(5000);
                socket.setTcpNoDelay(true);
                OutputStream output = socket.getOutputStream();
                for (int i = 0; i < safeCopies; i++) output.write(bytes);
                output.flush();
                socket.shutdownOutput();
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
            throw new SecurityException("Η εκτύπωση επιτρέπεται μόνο από τον server που έχει δηλωθεί στην εφαρμογή RestaurantOrders.");
    }

    private static InetAddress parseLocalPrinterAddress(String value) throws Exception {
        String host = value == null ? "" : value.trim();
        String[] octets = host.split("\\.", -1);
        if (octets.length != 4) throw new IllegalArgumentException("Ο δικτυακός εκτυπωτής χρειάζεται τοπική IPv4 διεύθυνση.");

        byte[] addressBytes = new byte[4];
        for (int i = 0; i < octets.length; i++) {
            if (octets[i].isEmpty() || octets[i].length() > 3) throw new IllegalArgumentException("Η IP διεύθυνση του εκτυπωτή δεν είναι έγκυρη.");
            for (int j = 0; j < octets[i].length(); j++) {
                if (!Character.isDigit(octets[i].charAt(j))) throw new IllegalArgumentException("Η IP διεύθυνση του εκτυπωτή δεν είναι έγκυρη.");
            }
            int number = Integer.parseInt(octets[i]);
            if (number > 255) throw new IllegalArgumentException("Η IP διεύθυνση του εκτυπωτή δεν είναι έγκυρη.");
            addressBytes[i] = (byte) number;
        }

        InetAddress address = InetAddress.getByAddress(addressBytes);
        if (address.isAnyLocalAddress() || address.isLoopbackAddress() || address.isMulticastAddress()
                || (!address.isSiteLocalAddress() && !address.isLinkLocalAddress()))
            throw new IllegalArgumentException("Η IP πρέπει να ανήκει στο τοπικό δίκτυο του καταστήματος.");
        return address;
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
                ? "Αποτυχία επικοινωνίας με τον εκτυπωτή." : ex.getMessage();
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
