# Ανάπτυξη του νέου stack στο Dockhand

Ο κώδικας είναι στο δημόσιο repository [Saranto717/Cloud-RestOrder](https://github.com/Saranto717/Cloud-RestOrder). Αυτό το stack είναι ξεχωριστό από το παλιό `cloud`: έχει δική του βάση PostgreSQL και δικά του volumes. Μην αλλάξεις ή διαγράψεις το παλιό stack κατά τη δοκιμή.

## 1. Δημιουργία stack από Git

Στο Dockhand επίλεξε **Stacks → From Git** και συμπλήρωσε:

- **Stack name:** `cloud-restorder`
- **Repository:** `https://github.com/Saranto717/Cloud-RestOrder.git`
- **Branch:** `main`
- **Compose file:** `docker-compose.yml`
- **Scheduled sync:** ενεργό, κάθε 5 λεπτά

Το νέο stack χρησιμοποιεί το image `ghcr.io/saranto717/cloud-restorder:latest`. Το Compose έχει `pull_policy: always`. Το `postgres-data` και το `license-signing-data` θα δημιουργηθούν με πρόθεμα `cloud-restorder_`, χωριστά από τα volumes του παλιού stack.

## 2. Μεταβλητές περιβάλλοντος

Πριν κάνεις deploy, πρόσθεσε στο Environment του νέου stack:

| Όνομα | Τιμή |
|---|---|
| `LICENSE_MANAGER_EMAIL` | Το email με το οποίο θα συνδέεσαι στο License Manager. |
| `LICENSE_MANAGER_PASSWORD_SECRET` | Ισχυρός, μοναδικός κωδικός διαχειριστή, τουλάχιστον 32 χαρακτήρες. |
| `POSTGRES_ADMIN_PASSWORD_SECRET` | Διαφορετικός τυχαίος κωδικός για τον διαχειριστή PostgreSQL. |
| `CLOUD_APP_PASSWORD_SECRET` | Διαφορετικός τυχαίος κωδικός για τον χρήστη εφαρμογής PostgreSQL. |
| `CLOUD_TRUSTED_PROXY_ADDRESSES` | Άφησέ το κενό για την αρχική δοκιμή HTTP. Πριν από HTTPS, βάλε την IPv4 διεύθυνση από την οποία το OpenResty συνδέεται στο API. |
| `POSTGRES_DB` | `restaurant_cloud` |
| `POSTGRES_ADMIN_USER` | `restaurant_cloud_admin` |
| `CLOUD_APP_USER` | `restaurant_cloud_app` |

Τα τρία πεδία που τελειώνουν σε `_SECRET` τα περνά το Compose ως Docker secrets. Μη βάλεις κωδικούς, tokens ή ιδιωτικό κλειδί στο GitHub. Δεν χρειάζονται `LICENSE_SIGNING_PRIVATE_KEY_B64`, `CLOUD_ADMIN_KEY` ή PEM: ο server δημιουργεί το κλειδί υπογραφής μόνος του και το κρατά στο volume `cloud-restorder_license-signing-data`.

Κάνε deploy και δοκίμασε από υπολογιστή στο ίδιο δίκτυο:

- `http://192.168.1.250:18081/health/live`
- `http://192.168.1.250:18081/health/ready`

Πρέπει να επιστρέψουν `{"status":"live"}` και `{"status":"ready"}`. Το HTTP είναι μόνο για τον αρχικό έλεγχο· login, διαχείριση αδειών, cloud POS και sync απαιτούν HTTPS.

Το νέο API δημοσιεύεται σταθερά στη θύρα `18081` του host, ώστε να μην εξαρτάται από μεταβλητές Environment.

## 3. Ενεργοποίηση HTTPS μέσω του υπάρχοντος OpenResty

Οι θύρες 80 και 443 χρησιμοποιούνται ήδη από OpenResty, και το παλιό API βρίσκεται στο 18080. **Μην ενεργοποιήσεις δεύτερο Caddy ή proxy στο νέο stack.** Αφού περάσει ο έλεγχος HTTP:

1. Στο Dockhand → Containers → OpenResty container → Network σημείωσε την IPv4 που βλέπει το API ως απομακρυσμένη διεύθυνση. Όρισε αυτή την τιμή στο `CLOUD_TRUSTED_PROXY_ADDRESSES` του νέου stack και κάνε redeploy.
2. Στο υπάρχον OpenResty reverse proxy άλλαξε το upstream του `texn-support.ddns.net` σε `http://192.168.1.250:18081`.
3. Κράτησε το ήδη ενεργό TLS certificate και το HTTPS domain. Δοκίμασε `https://texn-support.ddns.net/health/live`, `https://texn-support.ddns.net/license-manager.html` και `https://texn-support.ddns.net/pos.html`.

Μέχρι να αλλάξεις το upstream, το παλιό domain θα συνεχίσει να δείχνει στο παλιό stack. Για επιστροφή αρκεί να επαναφέρεις το upstream στο `18080`. Μην προωθήσεις τη θύρα 18081 από τον router προς το Internet· η δημόσια πρόσβαση πρέπει να περνά από το HTTPS proxy.

Στο νέο License Manager συνδέσου με τα στοιχεία που όρισες παραπάνω. Δημιούργησε πελάτη, άδεια και cloud κατάστημα. Το Android tablet ανοίγει το `/pos.html`. Το Windows POS συνδέεται με το HTTPS URL και το Store API token του καταστήματος· κρατά την τοπική SQLite βάση και συνεχίζει να δουλεύει όταν πέσει το Internet.

## 4. Επόμενες εκδόσεις

Όλες οι αλλαγές κώδικα γίνονται στο `main`. Κάθε tag έκδοσης όπως `v0.2.1` δημιουργεί Docker image με tag έκδοσης και `latest`, GitHub Release με αυτόματα release notes και commit `.dockhand-release` στο `main`. Το προγραμματισμένο Git sync του Dockhand εντοπίζει το commit και αναπτύσσει το νέο image.

Μετά την πρώτη επιτυχημένη δημοσίευση, το GHCR package `cloud-restorder` πρέπει να είναι **Public** ώστε το Dockhand να τραβά την εικόνα χωρίς GitHub token. Το repository source είναι ήδη δημόσιο.

## Volumes

- `cloud-restorder_postgres-data`: πελάτες, άδειες, χρήστες, μενού, τραπέζια και cloud παραγγελίες.
- `cloud-restorder_license-signing-data`: ιδιωτικό κλειδί υπογραφής αδειών.

Τα volumes διατηρούνται στα redeploy. Μην χρησιμοποιήσεις `docker compose down -v` και ρύθμισε αντίγραφα ασφαλείας.
