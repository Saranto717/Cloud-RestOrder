# Ανάπτυξη του νέου stack στο Dockhand

Ο κώδικας είναι στο δημόσιο repository [Saranto717/Cloud-RestOrder](https://github.com/Saranto717/Cloud-RestOrder). Αυτό το stack είναι ξεχωριστό από το παλιό `cloud`: έχει δική του βάση PostgreSQL και δικά του volumes. Μην αλλάξεις ή διαγράψεις το παλιό stack κατά τη δοκιμή.

## 1. Δημιουργία stack από Git

Στο Dockhand επίλεξε **Stacks → From Git** και συμπλήρωσε:

- **Stack name:** `cloud-restorder`
- **Repository:** `https://github.com/Saranto717/Cloud-RestOrder.git`
- **Branch:** `main`
- **Compose file:** `docker-compose.yml`
- **Scheduled sync:** απενεργοποιημένο μέχρι να δημοσιευτεί η έκδοση `v0.3.5` και να υπάρχουν και τα δύο νέα images στο GHCR. Μετά την επιτυχή πρώτη εγκατάσταση, ενεργοποίησέ το κάθε 5 λεπτά.

Το νέο stack χρησιμοποιεί τα images `ghcr.io/saranto717/cloud-restorder:latest` (API) και `ghcr.io/saranto717/cloud-restorder-web:latest` (πλήρης εφαρμογή POS). Το Compose έχει `pull_policy: always`. Η PostgreSQL κρατά ξεχωριστή βάση για κάθε cloud κατάστημα, μέσα στο volume `postgres-data`, χωριστά από τα volumes του παλιού stack. Το `cloud-web-data` διατηρεί μόνο παλιά αρχεία SQLite κατά τη μετάβαση στην PostgreSQL.

## 2. Μεταβλητές περιβάλλοντος

Πριν κάνεις deploy, πρόσθεσε στο Environment του νέου stack:

| Όνομα | Τιμή |
|---|---|
| `LICENSE_MANAGER_EMAIL` | Το email με το οποίο θα συνδέεσαι στο License Manager. |
| `LICENSE_MANAGER_PASSWORD_SECRET` | Ισχυρός, μοναδικός κωδικός διαχειριστή, τουλάχιστον 32 χαρακτήρες. |
| `POSTGRES_ADMIN_PASSWORD_SECRET` | Διαφορετικός τυχαίος κωδικός για τον διαχειριστή PostgreSQL. |
| `CLOUD_APP_PASSWORD_SECRET` | Διαφορετικός τυχαίος κωδικός για τον χρήστη εφαρμογής PostgreSQL. |
| `CLOUD_WEB_SYNC_TOKEN_SECRET` | Τυχαίο μυστικό τουλάχιστον 32 χαρακτήρων για την εσωτερική επικοινωνία API και POS. |
| `CLOUD_TRUSTED_PROXY_ADDRESSES` | Άφησέ το κενό για την αρχική δοκιμή HTTP. Πριν από HTTPS, βάλε την IPv4 διεύθυνση από την οποία το Nginx Proxy Manager συνδέεται στο API. |
| `POSTGRES_DB` | `restaurant_cloud` |
| `POSTGRES_ADMIN_USER` | `restaurant_cloud_admin` |
| `CLOUD_APP_USER` | `restaurant_cloud_app` |

Τα πεδία που τελειώνουν σε `_SECRET` τα περνά το Compose ως Docker secrets. Για το `CLOUD_WEB_SYNC_TOKEN_SECRET`, μπορείς να δημιουργήσεις τιμή στο PowerShell με:

```powershell
$bytes = New-Object byte[] 48
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
[BitConverter]::ToString($bytes).Replace('-', '')
```

Μη βάλεις κωδικούς, tokens ή ιδιωτικό κλειδί στο GitHub. Δεν χρειάζονται `LICENSE_SIGNING_PRIVATE_KEY_B64`, `CLOUD_ADMIN_KEY` ή PEM: ο server δημιουργεί το κλειδί υπογραφής μόνος του και το κρατά στο volume `cloud-restorder_license-signing-data`.

Κάνε deploy και δοκίμασε από υπολογιστή στο ίδιο δίκτυο:

- `http://192.168.1.250:18081/health/live`
- `http://192.168.1.250:18081/health/ready`

Πρέπει να επιστρέψουν `{"status":"live"}` και `{"status":"ready"}`. Το HTTP είναι μόνο για τον αρχικό έλεγχο· login, διαχείριση αδειών, cloud POS και sync απαιτούν HTTPS.

Το API δημοσιεύεται σταθερά στη θύρα `18081` του host, ώστε να μην εξαρτάται από μεταβλητές Environment. Η PostgreSQL μένει μόνο στο εσωτερικό network `database`. Το API και η εσωτερική εφαρμογή POS επικοινωνούν μέσω του `api-egress`. Το POS δεν έχει δημοσιευμένη θύρα και δεν είναι προσβάσιμο χωρίς έγκυρη συνεδρία καταστήματος.

## 3. Ενεργοποίηση HTTPS μέσω του υπάρχοντος Nginx Proxy Manager

Οι θύρες 80 και 443 χρησιμοποιούνται ήδη από το Nginx Proxy Manager, και το παλιό API βρίσκεται στο 18080. **Μην ενεργοποιήσεις δεύτερο Caddy ή proxy στο νέο stack.** Αφού περάσει ο έλεγχος HTTP:

1. Στο Dockhand → Containers → Nginx Proxy Manager container → Network σημείωσε την IPv4 που βλέπει το API ως απομακρυσμένη διεύθυνση. Όρισε αυτή την τιμή στο `CLOUD_TRUSTED_PROXY_ADDRESSES` του νέου stack και κάνε redeploy.
2. Στο υπάρχον Nginx Proxy Manager reverse proxy άλλαξε το upstream του `texn-support.ddns.net` σε `http://192.168.1.250:18081`.
3. Κράτησε το ήδη ενεργό TLS certificate και το HTTPS domain. Δοκίμασε `https://texn-support.ddns.net/health/live`, `https://texn-support.ddns.net/license-manager.html` και `https://texn-support.ddns.net/pos.html`.

Μέχρι να αλλάξεις το upstream, το παλιό domain θα συνεχίσει να δείχνει στο παλιό stack. Για επιστροφή αρκεί να επαναφέρεις το upstream στο `18080`. Μην προωθήσεις τη θύρα 18081 από τον router προς το Internet· η δημόσια πρόσβαση πρέπει να περνά από το HTTPS proxy.

Στο νέο License Manager συνδέσου με τα στοιχεία που όρισες παραπάνω. Δημιούργησε πελάτη, άδεια και cloud κατάστημα. Το `/pos.html` ζητά μία σύνδεση με τα στοιχεία του καταστήματος και μετά ανοίγει την ίδια αρχική οθόνη με το τοπικό πρόγραμμα. Οι εργαζόμενοι επιλέγουν τον χρήστη τους και ακολουθούν την ίδια ροή PIN/ρόλων που έχουν στα Windows· δεν χρειάζεται ξεχωριστό cloud email για κάθε σερβιτόρο. Κάθε κατάστημα αποθηκεύει τα δεδομένα του σε δική του βάση PostgreSQL και λειτουργεί χωρίς τοπική εγκατάσταση. Αν υπάρχει Windows POS, κρατά τη δική του τοπική SQLite ως συνδεδεμένο αντίγραφο: στην πρώτη σύζευξη κατεβάζει τα υπάρχοντα cloud δεδομένα· μόνο όταν το cloud είναι άδειο ανεβάζει το αρχικό τοπικό στιγμιότυπο. Μετά συγχρονίζει τις αλλαγές και προς τις δύο κατευθύνσεις. Όταν πέσει το Internet, το Windows POS συνεχίζει τοπικά και στέλνει τις αλλαγές όταν επανέλθει. Οι τοπικοί εκτυπωτές και τα συνδεδεμένα φορολογικά/τραπεζικά τερματικά συνεχίζουν να λειτουργούν από το Windows PC.

Ο λογαριασμός που δημιουργείται με το cloud κατάστημα χρησιμοποιείται μόνο για την αρχική σύνδεση του καταστήματος στο `/pos.html`. Μετά, οι εργαζόμενοι μπαίνουν από την αρχική οθόνη και επιλέγουν τον υπάρχοντα χρήστη/PIN της εφαρμογής. Από το **Χρήστες** διαχειρίζεσαι τους ίδιους τοπικούς λογαριασμούς, χωρίς ξεχωριστό email cloud για κάθε εργαζόμενο.

## 4. Επόμενες εκδόσεις

Όλες οι αλλαγές κώδικα γίνονται στο `main`. Κάθε tag έκδοσης όπως `v0.3.5` δημιουργεί Docker image με tag έκδοσης και `latest`, GitHub Release με αυτόματα release notes και commit `.dockhand-release` στο `main`. Το προγραμματισμένο Git sync του Dockhand εντοπίζει το commit και αναπτύσσει το νέο image.

Μετά την πρώτη επιτυχημένη δημοσίευση, και τα δύο GHCR packages (`cloud-restorder` και `cloud-restorder-web`) πρέπει να είναι **Public** ώστε το Dockhand να τραβά τις εικόνες χωρίς GitHub token. Το repository source είναι ήδη δημόσιο.

## Volumes

- `cloud-restorder_postgres-data`: κοινά στοιχεία λογαριασμών/αδειών και οι ξεχωριστές PostgreSQL βάσεις όλων των καταστημάτων.
- `cloud-restorder_license-signing-data`: ιδιωτικό κλειδί υπογραφής αδειών.
- `cloud-restorder_cloud-web-data`: παλιά SQLite αρχεία που κρατούνται ως αντίγραφα μετά τη μετάβαση στην PostgreSQL, μαζί με δείκτες μετάβασης.

Τα volumes διατηρούνται στα redeploy. Μην χρησιμοποιήσεις `docker compose down -v` και ρύθμισε αντίγραφα ασφαλείας.
