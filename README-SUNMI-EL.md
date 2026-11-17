# RestaurantOrders SUNMI — δοκιμαστική έκδοση

Η κεντρική εφαρμογή RestaurantOrders παραμένει στον υπολογιστή Windows. Το APK είναι Android client/γέφυρα για να καλεί την υπηρεσία του ενσωματωμένου εκτυπωτή SUNMI V2/V2s. Το POS παραμένει η web εφαρμογή.

## Γρήγορη δοκιμή
1. Εγκατάστησε το `RestaurantOrders-SUNMI-debug.apk` στη SUNMI.
2. Σύνδεσε SUNMI και κεντρικό υπολογιστή στο ίδιο Wi‑Fi.
3. Άνοιξε την εφαρμογή και γράψε τη διεύθυνση του server, π.χ. `http://192.168.1.11:5080` (βάλε την πραγματική IP/θύρα του server).
4. Πάτησε «Σύνδεση» και συνδέσου. Η εφαρμογή θυμάται τη διεύθυνση.
5. Από τη διαχείριση Εκτυπωτών πρόσθεσε εκτυπωτή τύπου SUNMI. Ενεργοποίησε Παραγγελίες ή/και Παραστατικά. Για 58 mm βάλε περίπου 32 χαρακτήρες ανά γραμμή και κάνε δοκιμή από τη SUNMI.

Η σύζευξη Bluetooth από μόνη της δεν δίνει στον Chrome πρόσβαση στον εσωτερικό εκτυπωτή. Το APK χρησιμοποιεί τη SUNMI printer service / SDK και μεταφέρει τις εργασίες εκτύπωσης από την ιστοσελίδα στην τοπική υπηρεσία.

## Δημιουργία APK από source
Απαιτούνται Node.js/npm, JDK 17 και Android SDK (API 33, Build Tools 33.0.2). Ρύθμισε `JAVA_HOME` και `ANDROID_HOME`, μετά τρέξε το `Build-SUNMI-APK.ps1` με PowerShell. Εναλλακτικά εκτέλεσε:

```powershell
cd android-client
npm ci
npm run build
npx cap sync android
cd android
.\gradlew.bat assembleDebug
```

Έξοδος: `android-client\android\app\build\outputs\apk\debug\app-debug.apk`. Το APK που παρέχεται είναι debug-signed για εγκατάσταση/δοκιμή. Για εμπορική διανομή απαιτείται υπογεγραμμένο release APK με δικό σου keystore.

## Αρχεία
- `RestaurantOrders_Mobile_WiFi_Source`: ενημερωμένος source του Windows server, που περιλαμβάνει τη γέφυρα εκτύπωσης.
- `android-client`: source του Android client.
- `RestaurantOrders-SUNMI-debug.apk`: δοκιμαστικό APK.
