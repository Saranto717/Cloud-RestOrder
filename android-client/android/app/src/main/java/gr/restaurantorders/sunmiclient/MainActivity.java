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
            webView.addJavascriptInterface(sunmiNativePrinterBridge, "RestaurantOrdersSunmiNative");
        }
    }

    @Override
    public void onDestroy() {
        if (sunmiNativePrinterBridge != null) sunmiNativePrinterBridge.release();
        super.onDestroy();
    }
}
