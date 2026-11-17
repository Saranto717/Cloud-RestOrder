# RestaurantOrders Hybrid

Υβριδική εφαρμογή παραγγελιοληψίας:

- **Windows POS:** συνεχίζει να χρησιμοποιεί την τοπική SQLite και δουλεύει χωρίς Internet. Όταν επανέλθει η σύνδεση συγχρονίζει αυτόματα με το cloud.
- **Android tablets:** ανοίγουν το online POS στο `/pos.html`, χωρίς τοπική βάση. Οι παραγγελίες επιστρέφουν στο Windows POS για τοπική αποθήκευση και εκτύπωση.
- **License Manager:** πελάτες και καταστήματα, εκδόσεις/άδειες, εγκαταστάσεις, ανανέωση/ανάκληση, σύνδεση Cloud POS και οριστική διαγραφή.

## Ανάπτυξη

Η υπηρεσία cloud τρέχει ως .NET 10 API και PostgreSQL μέσω Docker Compose. Στο Dockhand δημιουργείται ξεχωριστό stack `cloud-restorder`, ώστε να μείνει ανέπαφο το ήδη υπάρχον `cloud`. Το νέο API δημοσιεύεται σταθερά στη θύρα `18081` και στη συνέχεια το υπάρχον OpenResty προωθεί το HTTPS domain σε αυτό. Οι οδηγίες είναι στο [README-DEPLOY-EL.md](README-DEPLOY-EL.md).

Το cloud API εξυπηρετεί:

- `/license-manager.html` — διαχείριση αδειών
- `/pos.html` — online POS για tablet
- `/health/live` και `/health/ready` — health checks

Οι λογαριασμοί και τα credentials ορίζονται στο Dockhand. Μην βάζεις secrets στο repository. Το ιδιωτικό κλειδί υπογραφής αδειών δημιουργείται αυτόματα και αποθηκεύεται σε persistent Docker volume.

## Εκδόσεις

Οι αλλαγές μπαίνουν στο `main`. Η δημιουργία tag, π.χ. `v0.2.0`, ενεργοποιεί GitHub Actions για build/publish του Docker image, GitHub Release με release notes και σήμα ενημέρωσης για το Dockhand.

