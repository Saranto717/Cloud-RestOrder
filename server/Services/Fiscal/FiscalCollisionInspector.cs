using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Services.Fiscal.Provider;

namespace RestaurantOrders.Services.Fiscal;

/// <summary>Read-only comparison. Matching content alone is not proof of fiscal identity.</summary>
public sealed class FiscalCollisionInspector(IParochosFiscalProvider provider, FiscalConfigurationService config)
{
    private sealed record Snapshot(string? Series, string? Number, string? Type, string? Issuer, string? Branch,
        decimal? Total, decimal? Net, decimal? Vat, string? Point, string? IssueDate, string? Items, string? Related, string? Payments);
    private sealed record Candidate(string? ExternalId, long? Mark, string? Payload, string Source);

    public async Task<string> InspectAsync(AppDbContext db, int orderId)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null || order.ParochosMark > 0 || order.ParochosTransmissionState != "NeedsReview" ||
            string.IsNullOrWhiteSpace(order.InvoiceSeriesName) || order.InvoiceNumber is not > 0 ||
            string.IsNullOrWhiteSpace(order.ParochosInvoicePayloadJson))
            return "Δεν υπάρχει κατάλληλη εκκρεμής απόδειξη για σύγκριση.";
        var series = order.InvoiceSeriesName; var number = order.InvoiceNumber;
        var orders = await db.Orders.AsNoTracking().Where(o => o.Id != orderId && o.InvoiceSeriesName == series &&
            o.InvoiceNumber == number && o.ParochosMark > 0)
            .Select(o => new { o.ParochosExternalId, o.ParochosMark, o.ParochosInvoicePayloadJson }).Take(20).ToListAsync();
        var payments = await db.OrderPayments.AsNoTracking().Where(p => p.SeriesName == series && p.InvoiceNumber == number && p.Mark > 0)
            .Select(p => new { p.ExternalSystemId, p.Mark }).Take(20).ToListAsync();
        var candidates = orders.Select(o => new Candidate(o.ParochosExternalId, o.ParochosMark, o.ParochosInvoicePayloadJson, "τοπικό αντίγραφο απόδειξης"))
            .Concat(payments.Select(p => new Candidate(p.ExternalSystemId, p.Mark, null, "τοπική καταχώριση πληρωμής")))
            .GroupBy(c => (c.ExternalId, c.Mark)).Select(g => g.First()).ToList();
        var target = Read(order.ParochosInvoicePayloadJson);
        var settings = await config.GetSettingsAsync(db);
        var configured = config.ValidateSettings(settings, out _);
        var messages = new List<string>();
        if (configured && !string.IsNullOrWhiteSpace(order.ParochosExternalId))
        {
            try
            {
                var own = await provider.GetDocumentByExternalSystemIdAsync(settings!, order.ParochosExternalId);
                if (own.Success && own.Mark is > 0 && string.Equals(own.ExternalSystemId, order.ParochosExternalId, StringComparison.Ordinal))
                {
                    var text = $"Ο Parochos επέστρεψε ΜΑΡΚ {own.Mark} για το ίδιο εξωτερικό αναγνωριστικό.";
                    string? ownSent = null;
                    if (!string.IsNullOrWhiteSpace(own.ProcessId))
                        ownSent = await db.FiscalProviderExchanges.AsNoTracking().Where(e => e.Operation == "/api/send" &&
                            e.ExternalSystemId == order.ParochosExternalId && e.ProcessId == own.ProcessId &&
                            e.HttpStatus >= 200 && e.HttpStatus < 300).OrderByDescending(e => e.Id)
                            .Select(e => e.RequestJson).FirstOrDefaultAsync();
                    text += ownSent == null ? " Δεν βρέθηκε αποθηκευμένο αίτημα με ίδιο processId για σύγκριση ποσών, δελτίων και τραπεζιού."
                        : " Σύγκριση με το αποθηκευμένο αίτημα αυτού του processId: " + Compare(target, Read(ownSent));
                    messages.Add(text);
                }
                else if (own.IsNotFound) messages.Add("Ο Parochos δεν βρήκε το εξωτερικό αναγνωριστικό της εκκρεμούς απόδειξης.");
                else messages.Add("Δεν επιβεβαιώθηκε επιτυχώς η κατάσταση της εκκρεμούς απόδειξης στον Parochos.");
            }
            catch { messages.Add("Δεν ήταν δυνατός ο έλεγχος της εκκρεμούς απόδειξης στον Parochos."); }
        }
        if (candidates.Count == 0)
        {
            messages.Add($"Δεν βρέθηκε στη βάση άλλη εκδομένη απόδειξη {series} {number}. Για αναζήτηση άλλης απόδειξης στον Parochos χρειάζεται το externalSystemId ή το processId της.");
            return string.Join("\n\n", messages) + "\n\nΟ έλεγχος δεν αλλάζει αριθμό και δεν διαγράφει ή διαβιβάζει παραστατικά.";
        }
        foreach (var candidate in candidates)
        {
            var providerText = "Δεν επιβεβαιώθηκε στον πάροχο.";
            var comparisonPayload = candidate.Payload;
            var payloadSource = "το τοπικό αντίγραφο του παραστατικού";
            if (configured && !string.IsNullOrWhiteSpace(candidate.ExternalId))
            {
                try
                {
                    var found = await provider.GetDocumentByExternalSystemIdAsync(settings!, candidate.ExternalId);
                    if (found.Success && found.Mark is > 0 && found.Mark == candidate.Mark &&
                        string.Equals(found.ExternalSystemId, candidate.ExternalId, StringComparison.Ordinal))
                    {
                        providerText = $"Ο πάροχος επιβεβαίωσε το άλλο αναγνωριστικό με ΜΑΡΚ {found.Mark}.";
                        if (!string.IsNullOrWhiteSpace(found.ProcessId))
                        {
                            var sent = await db.FiscalProviderExchanges.AsNoTracking().Where(e =>
                                e.Operation == "/api/send" && e.ExternalSystemId == candidate.ExternalId &&
                                e.ProcessId == found.ProcessId && e.HttpStatus >= 200 && e.HttpStatus < 300)
                                .OrderByDescending(e => e.Id).Select(e => e.RequestJson).FirstOrDefaultAsync();
                            if (!string.IsNullOrWhiteSpace(sent))
                            {
                                comparisonPayload = sent;
                                payloadSource = "το αποθηκευμένο /api/send με το ίδιο processId";
                            }
                        }
                    }
                    else if (found.IsNotFound) providerText = "Ο πάροχος δεν βρήκε το αναγνωριστικό της υποψήφιας απόδειξης.";
                    else providerText = "Ο πάροχος δεν επιβεβαίωσε την υποψήφια απόδειξη και το ΜΑΡΚ της.";
                }
                catch { providerText = "Δεν ήταν δυνατός ο έλεγχος της υποψήφιας απόδειξης στον πάροχο."; }
            }
            var differences = Compare(target, Read(comparisonPayload));
            var identity = string.Equals(order.ParochosExternalId, candidate.ExternalId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(candidate.ExternalId)
                ? "Τα εξωτερικά αναγνωριστικά ταιριάζουν."
                : "Τα εξωτερικά αναγνωριστικά είναι διαφορετικά ή ελλιπή.";
            messages.Add($"Βρέθηκε {candidate.Source}, ΜΑΡΚ {candidate.Mark}. {providerText} {identity} Σύγκριση με {payloadSource}. {differences}");
        }
        return string.Join("\n\n", messages) + "\n\nΗ σύγκριση χρησιμοποιεί αποθηκευμένα δεδομένα αποστολής. Δεν αλλάζει αριθμό, δεν διαβιβάζει και δεν διαγράφει παραστατικά.";
    }

    private static string Compare(Snapshot? a, Snapshot? b)
    {
        if (a == null || b == null) return "Δεν υπάρχουν επαρκή αποθηκευμένα στοιχεία για σύγκριση περιεχομένου.";
        var differences = new List<string>(); var unknown = new List<string>();
        void Field(string name, string? x, string? y)
        { if (x == null || y == null) unknown.Add(name); else if (!string.Equals(x,y,StringComparison.Ordinal)) differences.Add(name); }
        Field("σειρά", a.Series, b.Series); Field("αριθμός", a.Number, b.Number);
        Field("τύπος", a.Type, b.Type); Field("ΑΦΜ εκδότη", a.Issuer, b.Issuer); Field("υποκατάστημα", a.Branch, b.Branch);
        Field("τραπέζι", a.Point, b.Point); Field("ημερομηνία έκδοσης", a.IssueDate, b.IssueDate);
        Field("σύνολο", a.Total?.ToString("G29", CultureInfo.InvariantCulture), b.Total?.ToString("G29", CultureInfo.InvariantCulture));
        Field("καθαρή αξία", a.Net?.ToString("G29", CultureInfo.InvariantCulture), b.Net?.ToString("G29", CultureInfo.InvariantCulture));
        Field("σύνολο ΦΠΑ", a.Vat?.ToString("G29", CultureInfo.InvariantCulture), b.Vat?.ToString("G29", CultureInfo.InvariantCulture));
        Field("είδη και αξίες", a.Items, b.Items); Field("συσχετισμένα δελτία", a.Related, b.Related); Field("τρόποι πληρωμής", a.Payments, b.Payments);
        var result = differences.Count > 0 ? "Διαφέρουν: " + string.Join(", ", differences) + "."
            : "Όσα στοιχεία συγκρίθηκαν ταιριάζουν, αλλά αυτό δεν αποδεικνύει ότι είναι η ίδια πώληση.";
        if (a.Total.HasValue && b.Total.HasValue)
            result += $" Ποσό εκκρεμούς: {a.Total.Value.ToString("0.00", CultureInfo.InvariantCulture)} €, εκδομένης: {b.Total.Value.ToString("0.00", CultureInfo.InvariantCulture)} €.";
        if (a.Point != null && b.Point != null)
            result += $" Τραπέζι εκκρεμούς: {a.Point}· εκδομένης: {b.Point}.";
        if (a.Related != null && b.Related != null)
            result += $" ΜΑΡΚ δελτίων εκκρεμούς: {(a.Related.Length == 0 ? "κανένα" : a.Related.Replace("|", ", "))}· εκδομένης: {(b.Related.Length == 0 ? "κανένα" : b.Related.Replace("|", ", "))}.";
        if (unknown.Count > 0) result += " Μη διαθέσιμα προς σύγκριση: " + string.Join(", ", unknown) + ".";
        return result;
    }

    // An absent field never counts as proof that two receipts are identical.
    public static bool MatchesRecordedReceipt(string? pendingJson, string? sentJson)
    {
        var a = Read(pendingJson); var b = Read(sentJson);
        if (a == null || b == null) return false;
        static bool Same(string? x, string? y) => x != null && y != null && string.Equals(x, y, StringComparison.Ordinal);
        return Same(a.Series,b.Series) && Same(a.Number,b.Number) && Same(a.Type,b.Type) &&
            Same(a.Issuer,b.Issuer) && Same(a.Branch,b.Branch) && Same(a.Point,b.Point) && Same(a.IssueDate,b.IssueDate) &&
            a.Total.HasValue && b.Total.HasValue && a.Total == b.Total && a.Net.HasValue && b.Net.HasValue && a.Net == b.Net &&
            a.Vat.HasValue && b.Vat.HasValue && a.Vat == b.Vat && Same(a.Items,b.Items) &&
            Same(a.Related,b.Related) && Same(a.Payments,b.Payments);
    }

    private static Snapshot? Read(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var json = JsonDocument.Parse(raw); var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object) root = source;
            if (!root.TryGetProperty("invoice", out var invoice) || invoice.ValueKind != JsonValueKind.Object) return null;
            static JsonElement Prop(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) ? v : default;
            static string? Value(JsonElement e) => e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : e.ToString().Trim();
            static decimal? Money(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var v) ? v : null;
            static string? Collection(JsonElement e, Func<JsonElement,string?> transform)
            {
                if (e.ValueKind != JsonValueKind.Array) return null;
                var values = new List<string>();
                foreach (var item in e.EnumerateArray()) { var value = transform(item); if (value == null) return null; values.Add(value); }
                values.Sort(StringComparer.Ordinal); return string.Join("|", values);
            }
            var header=Prop(invoice,"invoiceHeader"); var issuer=Prop(invoice,"issuer"); var summary=Prop(invoice,"invoiceSummary");
            var items=Collection(Prop(invoice,"invoiceDetails"), x => {
                var name=Value(Prop(x,"entityName")); var qty=Money(Prop(x,"quantity")); var total=Money(Prop(x,"totalValue"));
                var vat=Value(Prop(x,"vatCategory"));
                return name == null || qty == null || total == null || vat == null ? null :
                    JsonSerializer.Serialize(new { name, qty = qty.Value.ToString("G29", CultureInfo.InvariantCulture), total = total.Value.ToString("G29", CultureInfo.InvariantCulture), vat });
            });
            var related=Collection(Prop(invoice,"correlatedInvoices"), x => Value(Prop(x,"mark")));
            var payments=Collection(Prop(invoice,"paymentMethods"), x => {
                var type=Value(Prop(x,"type")); var amount=Money(Prop(x,"amount"));
                return type == null || amount == null ? null : JsonSerializer.Serialize(new { type, amount = amount.Value.ToString("G29", CultureInfo.InvariantCulture) });
            });
            return new Snapshot(Value(Prop(header,"series")), Value(Prop(header,"aa")), Value(Prop(header,"invoiceType")),
                Value(Prop(issuer,"vatNumber")), Value(Prop(issuer,"branch")), Money(Prop(summary,"totalValue")),
                Money(Prop(summary,"totalNetValue")), Money(Prop(summary,"totalVatAmount")),
                Value(Prop(header,"PointOfService")) ?? Value(Prop(header,"pointOfService")), Value(Prop(header,"issueDate")), items, related, payments);
        }
        catch (JsonException) { return null; }
    }
}
