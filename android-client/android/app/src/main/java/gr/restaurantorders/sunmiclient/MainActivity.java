package gr.restaurantorders.sunmiclient;

import android.os.Bundle;
import android.webkit.WebView;

import com.getcapacitor.BridgeActivity;

public class MainActivity extends BridgeActivity {
    private SunmiNativePrinterBridge sunmiNativePrinterBridge;

    @Override
    public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        if (getBridge() != null) {
            WebView webView = getBridge().getWebView();
            sunmiNativePrinterBridge = new SunmiNativePrinterBridge(this, webView);
            webView.addJavascriptInterface(sunmiNativePrinterBridge, "RestaurantOrdersAndroidNative");
            // Keep the old name for existing Sunmi-specific page integrations.
            webView.addJavascriptInterface(sunmiNativePrinterBridge, "RestaurantOrdersSunmiNative");
        }
    }

    @Override
    public void onDestroy() {
        if (sunmiNativePrinterBridge != null) sunmiNativePrinterBridge.release();
        super.onDestroy();
    }
}
