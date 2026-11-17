# Ανάπτυξη στο Dockhand

Ο πηγαίος κώδικας βρίσκεται στο δημόσιο repository [Saranto717/Cloud-RestOrder](https://github.com/Saranto717/Cloud-RestOrder). Μην αποθηκεύεις κωδικούς, tokens, ιδιωτικά κλειδιά ή αρχεία πελατών σε αυτό. Το Docker image δημοσιεύεται ξεχωριστά στο GHCR ως `cloud-restorder`.

## 1. Δημιουργία stack

Στο Dockhand επίλεξε Stacks → From Git και συμπλήρωσε:

- Repository: Saranto717/Cloud-RestOrder.
- Branch: main.
- Compose file: docker-compose.yml.
- Schedule sync: ενεργό, κάθε 5 λεπτά.

Το Docker image είναι `ghcr.io/saranto717/cloud-restorder:latest`. Στο πρώτο deploy θα χρειαστεί να έχει ολοκληρωθεί το release v0.2.0 και να είναι δημόσιο το GHCR package. Το Compose τραβά πάντα το πιο πρόσφατο image. Μην προσθέσεις signing key στο Dockhand· δημιουργείται από μόνο του.

## 2. Αρχικές μεταβλητές Dockhand

Στις μεταβλητές περιβάλλοντος του stack πρόσθεσε τις παρακάτω τιμές. Βάλε διαφορετικούς, τυχαίους κωδικούς τουλάχιστον 32 χαρακτήρων στα τρία secret πεδία. Μην τους βάλεις στο GitHub ή στο repository.

| Όνομα | Τιμή |
|---|---|
| LICENSE_MANAGER_EMAIL | Το email διαχειριστή που θα χρησιμοποιείς για σύνδεση. |
| LICENSE_MANAGER_PASSWORD_SECRET | Ισχυρός κωδικός για το License Manager. |
| POSTGRES_ADMIN_PASSWORD_SECRET | Διαφορετικός κωδικός για τον διαχειριστή της βάσης. |
| CLOUD_APP_PASSWORD_SECRET | Διαφορετικός κωδικός για τη σύνδεση του API με τη βάση. |

Δεν χρειάζονται LICENSE_SIGNING_PRIVATE_KEY_B64, CLOUD_ADMIN_KEY, αρχείο PEM, GitHub token ή SSH στοιχεία. Τα secrets περνούν στο container ως Docker secrets από το περιβάλλον του Dockhand.

Για τον πρώτο έλεγχο άφησε:

- CLOUD_API_BIND_ADDRESS=0.0.0.0
- CLOUD_API_PORT=18080
- COMPOSE_PROFILES κενό

Κάνε deploy και άνοιξε από υπολογιστή στο ίδιο δίκτυο:

- http://192.168.1.250:18080/health/live
- http://192.168.1.250:18080/health/ready

Αναμένονται απαντήσεις {"status":"live"} και {"status":"ready"}. Στο HTTP δοκιμαστικό στάδιο δεν λειτουργούν login, διαχείριση αδειών ή συγχρονισμός· αυτά απαιτούν HTTPS.

## 3. Ενεργοποίηση HTTPS

Στον router προώθησε τις δημόσιες θύρες 80 και 443 στον server Docker. Το DNS του texn-support.ddns.net πρέπει να δείχνει στη δημόσια IP σου και οι θύρες να μην χρησιμοποιούνται από άλλον proxy.

Έπειτα στις μεταβλητές του ίδιου stack:

1. Όρισε COMPOSE_PROFILES=https για να ξεκινήσει το Caddy.
2. Άλλαξε CLOUD_API_BIND_ADDRESS σε 127.0.0.1 και άφησε CLOUD_API_PORT=18080.
3. Κάνε Save and deploy.

Το Caddy ζητά και ανανεώνει αυτόματα πιστοποιητικό TLS για texn-support.ddns.net και προωθεί την κίνηση στο API. Έλεγξε:

- https://texn-support.ddns.net/health/live
- https://texn-support.ddns.net/license-manager.html
- https://texn-support.ddns.net/pos.html

Στο License Manager συνδέσου με το email και τον κωδικό που όρισες στο Dockhand. Πρώτα δημιούργησε πελάτη/άδεια και κατάστημα. Έπειτα σύνδεσε το Windows POS από Ρυθμίσεις → Σύνδεση Cloud με το HTTPS URL και το Store API token που εκδίδεται μία φορά.

## 4. Εκδόσεις και αυτόματη ενημέρωση

Όλες οι αλλαγές κώδικα μπαίνουν στο main. Push στο main χτίζει image :main, αλλά δεν αλλάζει το production :latest. Για έκδοση:

1. Όλες οι αλλαγές γίνονται στο `main`.
2. Για νέα έκδοση ανέβασε tag από commit του `main`, π.χ. `v0.2.0` ή `v0.2.1`.
3. Το GitHub Actions χτίζει `:v0.2.0`, ενημερώνει το `:latest` και δημιουργεί αυτόματα GitHub Release με release notes.
4. Μετά την επιτυχημένη δημοσίευση, το workflow ενημερώνει το `.dockhand-release` στο `main`.
5. Το προγραμματισμένο Git sync του Dockhand κάθε 5 λεπτά ανιχνεύει την αλλαγή και αναπτύσσει ξανά το stack, τραβώντας το νέο `:latest`.

Μετά την πρώτη επιτυχημένη ροή, όρισε το GHCR package `cloud-restorder` σε Public για να μπορεί να το τραβά το Dockhand χωρίς GitHub token. Το Git repository είναι ήδη δημόσιο και δεν χρειάζεται Git credential στο Dockhand.

## Δεδομένα και ανάκτηση

- postgres-data: πελάτες, άδειες, χρήστες, μενού, τραπέζια και cloud παραγγελίες.
- license-signing-data: ιδιωτικό κλειδί υπογραφής αδειών που φτιάχνει αυτόματα ο server.
- caddy-data / caddy-config: πιστοποιητικό HTTPS και ρυθμίσεις Caddy.

Τα named volumes δεν διαγράφονται σε απλό redeploy. Ρύθμισε αντίγραφα ασφαλείας για τα volumes postgres-data και license-signing-data. Μην χρησιμοποιήσεις docker compose down -v.

## Τι συγχρονίζεται

Το Windows POS συνεχίζει να γράφει στην τοπική SQLite και λειτουργεί όταν πέσει το Internet. Με ενεργό cloud sync, στέλνει αλλαγές όταν επανέλθει η σύνδεση και λαμβάνει cloud παραγγελίες tablet για αποθήκευση στη SQLite και εκτύπωση στους τοπικούς εκτυπωτές. Τα Android tablets ανοίγουν το pos.html μέσω HTTPS, χρησιμοποιούν cloud login και δεν έχουν τοπική βάση.
