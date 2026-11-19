using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public class EmployeeSummary
{
    public int WaiterUserId { get; set; }
    public string EmployeeName { get; set; } = "";
    public decimal CashTotal { get; set; }
    public decimal CardTotal { get; set; }
    public Dictionary<PaymentMethod, decimal> OtherPaymentTotals { get; set; } = new();
    public int HitItemCount { get; set; }
    public decimal HitTotal { get; set; }
    public int CancelledItemCount { get; set; }
    public decimal CancelledTotal { get; set; }
    public int CashPaymentCount { get; set; }
    public int CardPaymentCount { get; set; }
    public int CreditPaymentCount { get; set; }
    public int IrisPaymentCount { get; set; }
    public int OrderCount { get; set; }
    public int StaffConsumptionCount { get; set; }
    public decimal StaffConsumptionTotal { get; set; }
    public List<StaffConsumptionMovement> StaffConsumptionMovements { get; set; } = new();
    public decimal GrandTotal => CashTotal + CardTotal + OtherPaymentTotals.Values.Sum();
}

public class StaffConsumptionMovement
{
    public string Table { get; set; } = "";
    public string Document { get; set; } = "";
    public string Items { get; set; } = "";
    public DateTime At { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>Ένα μεμονωμένο, ολοκληρωμένο "Κλείσιμο Ημέρας" - με τη δική του ώρα και ανάλυση ανά υπάλληλο.</summary>
public class DayCloseRecord
{
    public int Id { get; set; }
    public DateTime ClosedAt { get; set; }
    public string ClosedByName { get; set; } = "";
    public int PendingPreloadCount { get; set; }
    public decimal PendingPreloadAmount { get; set; }
    public List<EmployeeSummary> Employees { get; set; } = new();
    public decimal GrandTotal => Employees.Sum(e => e.GrandTotal);
}

public static class CashierService
{
    static CashierService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private static decimal OrderTotal(Order o) => o.Items.Where(i => !i.IsCancelled)
        .Sum(i => Math.Round(i.Quantity * ((i.MenuItem?.Price ?? 0m) + i.ExtrasPrice) * (1m - Math.Clamp(i.DiscountPercent, 0m, 100m) / 100m), 2));

    private static List<EmployeeSummary> Summarize(List<Order> orders)
    {
        return orders
            .GroupBy(o => new { o.WaiterUserId, Name = o.WaiterUser?.FullName ?? "(άγνωστος)" })
            .Select(g =>
            {
                var amounts = new List<(PaymentMethod Method, decimal Amount)>();
                foreach (var order in g.Where(o => !IsStaffConsumption(o)))
                {
                    var paid = order.Payments
                        .Where(p => p.FiscalStatus is FiscalStatus.Issued or FiscalStatus.PaidLocally)
                        .ToList();
                    if (paid.Count > 0)
                        amounts.AddRange(paid.Select(p => (NormalizePaymentMethod(p.PaymentMethod), p.Amount)));
                    else
                        amounts.Add((NormalizePaymentMethod(order.PaymentMethod), OrderTotal(order)));
                }

                var groupOrders = g.ToList();
                var paymentRows = groupOrders.Where(o => !IsStaffConsumption(o)).SelectMany(order =>
                {
                    var completed = order.Payments.Where(p => p.FiscalStatus is FiscalStatus.Issued or FiscalStatus.PaidLocally).ToList();
                    return completed.Count > 0
                        ? completed.Select(p => (Method: NormalizePaymentMethod(p.PaymentMethod), p.Amount))
                        : new[] { (Method: NormalizePaymentMethod(order.PaymentMethod), Amount: OrderTotal(order)) };
                }).ToList();
                var allItemsInGroup = groupOrders.Where(o => !IsStaffConsumption(o)).SelectMany(o => o.Items).ToList();

                return new EmployeeSummary
                {
                    WaiterUserId = g.Key.WaiterUserId,
                    EmployeeName = g.Key.Name,
                    StaffConsumptionCount = groupOrders.Count(IsStaffConsumption),
                    StaffConsumptionTotal = groupOrders.Where(IsStaffConsumption).Sum(OrderTotal),
                    StaffConsumptionMovements = groupOrders.Where(IsStaffConsumption).Select(o => new StaffConsumptionMovement {
                        Table = o.RestaurantTable?.Name ?? "—", Document = $"{o.InvoiceSeriesName} {o.InvoiceNumber}".Trim(),
                        Items = string.Join(", ", o.Items.Where(i => !i.IsCancelled).Select(i => $"{i.Quantity} × {i.MenuItem?.Name ?? "Είδος"}")),
                        At = o.ClosedAt ?? o.CreatedAt, Amount = OrderTotal(o) }).ToList(),
                    CashTotal = amounts.Where(x => x.Method == PaymentMethod.Cash).Sum(x => x.Amount),
                    CardTotal = amounts.Where(x => x.Method == PaymentMethod.Card).Sum(x => x.Amount),
                    OtherPaymentTotals = amounts
                        .Where(x => x.Method is not (PaymentMethod.Cash or PaymentMethod.Card))
                        .GroupBy(x => x.Method)
                        .ToDictionary(x => x.Key, x => x.Sum(y => y.Amount)),
                    OrderCount = g.Count(),
                    HitItemCount = allItemsInGroup.Sum(i => i.Quantity),
                    HitTotal = allItemsInGroup.Sum(i => Math.Round(i.Quantity * ((i.MenuItem?.Price ?? 0m) + i.ExtrasPrice) * (1m - Math.Clamp(i.DiscountPercent, 0m, 100m) / 100m), 2)),
                    CancelledItemCount = allItemsInGroup.Where(i => i.IsCancelled).Sum(i => i.Quantity),
                    CancelledTotal = allItemsInGroup.Where(i => i.IsCancelled).Sum(i => Math.Round(i.Quantity * ((i.MenuItem?.Price ?? 0m) + i.ExtrasPrice) * (1m - Math.Clamp(i.DiscountPercent, 0m, 100m) / 100m), 2)),
                    CashPaymentCount = paymentRows.Count(x => x.Method == PaymentMethod.Cash),
                    CardPaymentCount = paymentRows.Count(x => x.Method == PaymentMethod.Card),
                    CreditPaymentCount = paymentRows.Count(x => x.Method == PaymentMethod.Credit),
                    IrisPaymentCount = paymentRows.Count(x => x.Method == PaymentMethod.Iris)
                };
            })
            .OrderByDescending(x => x.GrandTotal)
            .ToList();
    }

    /// <summary>
    /// "Τρέχον ταμείο": κλεισμένες παραγγελίες που ΔΕΝ έχουν μπει ακόμα σε κανένα
    /// "Κλείσιμο Ημέρας". Μηδενίζεται (αδειάζει) κάθε φορά που γίνεται κλείσιμο.
    /// </summary>
    public static async Task<List<EmployeeSummary>> GetCurrentSummary(AppDbContext db)
    {
        var orders = await db.Orders
            .Where(o => o.Status == OrderStatus.Closed && o.DayCloseId == null)
            .Include(o => o.Items).ThenInclude(i => i.MenuItem)
            .Include(o => o.Payments)
            .Include(o => o.WaiterUser)
            .Include(o => o.RestaurantTable)
            .ToListAsync();

        return Summarize(orders);
    }

    /// <summary>
    /// Όλα τα ξεχωριστά "Κλεισίματα Ημέρας" που έγιναν μια συγκεκριμένη ημερομηνία —
    /// το καθένα με τη δική του ανάλυση, ΧΩΡΙΣ να αθροίζονται μεταξύ τους.
    /// </summary>
    public static async Task<List<DayCloseRecord>> GetClosingsForDate(AppDbContext db, DateTime date)
    {
        var fromInclusive = date.Date;
        var toExclusive = date.Date.AddDays(1);

        var closes = await db.DayCloses
            .Where(dc => dc.ClosedAt >= fromInclusive && dc.ClosedAt < toExclusive)
            .Include(dc => dc.ClosedByUser)
            .OrderBy(dc => dc.ClosedAt)
            .ToListAsync();

        var result = new List<DayCloseRecord>();
        foreach (var dc in closes)
        {
            var orders = await db.Orders
                .Where(o => o.DayCloseId == dc.Id)
                .Include(o => o.Items).ThenInclude(i => i.MenuItem)
                .Include(o => o.Payments)
                .Include(o => o.WaiterUser)
                .Include(o => o.RestaurantTable)
                .ToListAsync();

            var snapshot = DeserializeSummary(dc.EmployeeSummaryJson);
            result.Add(new DayCloseRecord
            {
                Id = dc.Id,
                ClosedAt = dc.ClosedAt,
                ClosedByName = dc.ClosedByUser?.FullName ?? "-",
                PendingPreloadCount = dc.PendingPreloadCount,
                PendingPreloadAmount = dc.PendingPreloadAmount,
                Employees = snapshot ?? Summarize(orders)
            });
        }

        return result;
    }

    /// <summary>Φέρνει ένα συγκεκριμένο ιστορικό κλείσιμο (για εκτύπωση/PDF).</summary>
    public static async Task<DayCloseRecord?> GetClosingById(AppDbContext db, int dayCloseId)
    {
        var dc = await db.DayCloses.Include(x => x.ClosedByUser).FirstOrDefaultAsync(x => x.Id == dayCloseId);
        if (dc is null) return null;

        var orders = await db.Orders
            .Where(o => o.DayCloseId == dc.Id)
            .Include(o => o.Items).ThenInclude(i => i.MenuItem)
            .Include(o => o.Payments)
            .Include(o => o.WaiterUser)
            .Include(o => o.RestaurantTable)
            .ToListAsync();

        var savedSummary = DeserializeSummary(dc.EmployeeSummaryJson);
        return new DayCloseRecord
        {
            Id = dc.Id,
            ClosedAt = dc.ClosedAt,
            ClosedByName = dc.ClosedByUser?.FullName ?? "-",
            PendingPreloadCount = dc.PendingPreloadCount,
            PendingPreloadAmount = dc.PendingPreloadAmount,
            Employees = savedSummary ?? Summarize(orders)
        };
    }

    /// <summary>
    /// Εκτελεί ένα νέο "Κλείσιμο Ημέρας": παγώνει το τρέχον ταμείο σε ένα μόνιμο,
    /// ξεχωριστό στιγμιότυπο και αδειάζει το τρέχον ταμείο για μετά.
    /// </summary>
    public static async Task<DayCloseRecord> PerformDayClose(AppDbContext db, int closedByUserId)
    {
        var dayClose = new DayClose { ClosedAt = DateTime.Now, ClosedByUserId = closedByUserId };
        db.DayCloses.Add(dayClose);
        await db.SaveChangesAsync();

        var pendingPreloads = await db.Orders
            .Where(o => o.DayCloseId == null && o.AadePreloadedStatus == "Pending" && o.FiscalStatus == FiscalStatus.Issued)
            .Include(o => o.Items).ThenInclude(i => i.MenuItem)
            .ToListAsync();
        dayClose.PendingPreloadCount = pendingPreloads.Count;
        dayClose.PendingPreloadAmount = pendingPreloads.Sum(o => OrderTotal(o));
        foreach (var pending in pendingPreloads)
            pending.DayCloseId = dayClose.Id;

        var uncounted = await db.Orders
            .Where(o => o.Status == OrderStatus.Closed && o.DayCloseId == null)
            .ToListAsync();

        foreach (var o in uncounted)
        {
            o.DayCloseId = dayClose.Id;
        }
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE OrderNumberSequence SET CurrentNumber = 0 WHERE Id = 1;");
        var record = (await GetClosingById(db, dayClose.Id))!;
        dayClose.EmployeeSummaryJson = JsonSerializer.Serialize(record.Employees);
        await db.SaveChangesAsync();
        return record;
    }

    public static async Task<int> ReserveNextOrderNumberAsync(AppDbContext db)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE OrderNumberSequence SET CurrentNumber = CurrentNumber + 1 WHERE Id = 1;");
        var number = await db.Database.SqlQueryRaw<long>(
            "SELECT CurrentNumber AS \"Value\" FROM OrderNumberSequence WHERE Id = 1").SingleAsync();
        await transaction.CommitAsync();
        return checked((int)number);
    }

    /// <summary>
    /// Εκτελεί κλείσιμο ταμείου για ΕΝΑΝ ΜΟΝΟ υπάλληλο (π.χ. σχόλασε νωρίτερα) - παγώνει
    /// μόνο τις δικές του εκκρεμείς παραγγελίες σε ξεχωριστό στιγμιότυπο, αφήνοντας τους
    /// υπόλοιπους υπαλλήλους ανεπηρέαστους στο "Τρέχον Ταμείο".
    /// </summary>
    public static async Task<DayCloseRecord> PerformEmployeeClose(AppDbContext db, int waiterUserId, int closedByUserId)
    {
        var dayClose = new DayClose { ClosedAt = DateTime.Now, ClosedByUserId = closedByUserId };
        db.DayCloses.Add(dayClose);
        await db.SaveChangesAsync();

        var uncounted = await db.Orders
            .Where(o => o.Status == OrderStatus.Closed && o.DayCloseId == null && o.WaiterUserId == waiterUserId)
            .ToListAsync();

        foreach (var o in uncounted)
        {
            o.DayCloseId = dayClose.Id;
        }
        await db.SaveChangesAsync();
        var record = (await GetClosingById(db, dayClose.Id))!;
        dayClose.EmployeeSummaryJson = JsonSerializer.Serialize(record.Employees);
        await db.SaveChangesAsync();
        return record;
    }

    /// <summary>Όμορφα δομημένο PDF για ένα συγκεκριμένο κλείσιμο.</summary>
    public static byte[] BuildDayClosePdf(DayCloseRecord record)
    {
        var rows = record.Employees;
        var cashTotal = rows.Sum(r => r.CashTotal);
        var cardTotal = rows.Sum(r => r.CardTotal);
        var otherPaymentTotals = rows.SelectMany(r => r.OtherPaymentTotals)
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
        var grandTotal = rows.Sum(r => r.GrandTotal);
        var orderCount = rows.Sum(r => r.OrderCount);
        var hitTotal = rows.Sum(r => r.HitTotal);
        var hitCount = rows.Sum(r => r.HitItemCount);
        var cancelledTotal = rows.Sum(r => r.CancelledTotal);
        var cancelledCount = rows.Sum(r => r.CancelledItemCount);
        var creditTotal = rows.Sum(r => AlternativePaymentAmount(r, PaymentMethod.Credit));
        var irisTotal = rows.Sum(r => AlternativePaymentAmount(r, PaymentMethod.Iris));

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                // Landscape leaves enough width for the per-operator payment breakdown.
                page.Size(PageSizes.A4.Landscape());
                page.Margin(32);
                page.DefaultTextStyle(x => x.FontSize(9).FontColor("#243247"));

                page.Header().Column(col =>
                {
                    var title = rows.Count == 1 ? $"Κλείσιμο Ταμείου - {rows[0].EmployeeName}" : "Κλείσιμο Ταμείου";
                    col.Item().Text(title).FontSize(22).Bold().FontColor("#183153");
                    col.Item().Text($"Ημερομηνία/Ώρα: {record.ClosedAt:dd/MM/yyyy HH:mm}").FontSize(12);
                    col.Item().Text($"Έγινε από: {record.ClosedByName}").FontSize(12);
                    col.Item().PaddingTop(5).Text($"Κινήσεις: {orderCount} παραγγελίες · {hitCount} τεμάχια · χτυπήθηκαν €{hitTotal:0.00} · ακυρώθηκαν {cancelledCount} είδη / €{cancelledTotal:0.00}").FontSize(10).Bold();
                    col.Item().PaddingTop(10).LineHorizontal(1);
                });

                page.Content().PaddingTop(15).Column(col =>
                {
                    col.Item().Text("ΑΝΑΛΥΣΗ ΑΝΑ ΧΕΙΡΙΣΤΗ").FontSize(13).Bold().FontColor("#183153");
                    col.Item().PaddingTop(8).Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(2.5f); c.RelativeColumn(1.2f); c.RelativeColumn(1.2f);
                            c.RelativeColumn(1.7f); c.RelativeColumn(1.2f); c.RelativeColumn(1.2f);
                            c.RelativeColumn(1.2f); c.RelativeColumn(1.2f); c.RelativeColumn(1.2f);
                            c.RelativeColumn(1.2f); c.RelativeColumn(1.2f);
                        });

                        table.Header(header =>
                        {
                            header.Cell().Background("#eaf0f7").Text("Χειριστής").FontSize(7).Bold();
                            header.Cell().Background("#eaf0f7").AlignRight().Text("Παραγγελίες").FontSize(7).Bold();
                            header.Cell().Background("#eaf0f7").AlignRight().Text("Τεμάχια").FontSize(7).Bold();
                            header.Cell().Background("#eaf0f7").AlignRight().Text("Χτυπήθηκαν").FontSize(7).Bold();
                            header.Cell().Background("#eaf0f7").AlignRight().Text("Ακυρώσεις").FontSize(7).Bold();
                            header.Cell().Background("#eaf0f7").AlignRight().Text("Μετρητά").FontSize(7).Bold();
                            header.Cell().Background("#eaf0f7").AlignRight().Text("Κάρτα").FontSize(7).Bold();
                            header.Cell().Background("#eaf0f7").AlignRight().Text("Πίστωση").FontSize(7).Bold();
                            header.Cell().Background("#eaf0f7").AlignRight().Text("IRIS").FontSize(7).Bold();
                            header.Cell().Background("#eaf0f7").AlignRight().Text("Λοιπά").FontSize(7).Bold();
                            header.Cell().Background("#eaf0f7").AlignRight().Text("Σύνολο").FontSize(7).Bold();
                        });

                        foreach (var row in rows)
                        {
                            table.Cell().Text(row.EmployeeName).FontSize(7).Bold();
                            table.Cell().AlignRight().Text(row.OrderCount.ToString()).FontSize(7);
                            table.Cell().AlignRight().Text(row.HitItemCount.ToString()).FontSize(7);
                            table.Cell().AlignRight().Text($"€{row.HitTotal:0.00}").FontSize(7);
                            table.Cell().AlignRight().Text($"{row.CancelledItemCount} / €{row.CancelledTotal:0.00}").FontSize(7);
                            table.Cell().AlignRight().Text($"€{row.CashTotal:0.00} ({row.CashPaymentCount})").FontSize(7);
                            table.Cell().AlignRight().Text($"€{row.CardTotal:0.00} ({row.CardPaymentCount})").FontSize(7);
                            table.Cell().AlignRight().Text($"€{AlternativePaymentAmount(row, PaymentMethod.Credit):0.00} ({row.CreditPaymentCount})").FontSize(7);
                            table.Cell().AlignRight().Text($"€{AlternativePaymentAmount(row, PaymentMethod.Iris):0.00} ({row.IrisPaymentCount})").FontSize(7);
                            table.Cell().AlignRight().Text(OtherPaymentSummary(row, PaymentMethod.Credit, PaymentMethod.Iris)).FontSize(7);
                            table.Cell().AlignRight().Text($"€{row.GrandTotal:0.00}").FontSize(7).Bold();
                        }
                    });

                    col.Item().PaddingTop(20).LineHorizontal(1);

                    col.Item().PaddingTop(10).Row(row =>
                    {
                        row.RelativeItem().Text("Σύνολο Μετρητών:");
                        row.RelativeItem().AlignRight().Text($"€{cashTotal:0.00}").Bold();
                    });
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text("Σύνολο Καρτών:");
                        row.RelativeItem().AlignRight().Text($"€{cardTotal:0.00}").Bold();
                    });
                    foreach (var payment in otherPaymentTotals.OrderBy(x => PaymentMethodInfo.GetMyDataCode(x.Key)))
                        AppendPaymentTotal(col, PaymentMethodInfo.DisplayName(payment.Key), payment.Value);
                    if (rows.Any(r => r.StaffConsumptionCount > 0))
                    {
                        col.Item().PaddingTop(8).Text("ΙΔΙΟΚΑΤΑΝΑΛΩΣΕΙΣ (εκτός εισπράξεων)").Bold();
                        foreach (var staff in rows.Where(r => r.StaffConsumptionCount > 0))
                        {
                            AppendPaymentTotal(col, $"{staff.EmployeeName} ({staff.StaffConsumptionCount})", staff.StaffConsumptionTotal);
                            foreach (var movement in staff.StaffConsumptionMovements)
                            {
                                AppendPaymentTotal(col, $"  {movement.At:HH:mm} · {movement.Table} · {movement.Document}", movement.Amount);
                                if (!string.IsNullOrWhiteSpace(movement.Items)) col.Item().Text($"    {movement.Items}").FontSize(8);
                            }
                        }
                    }
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text("Σύνολο Παραγγελιών:");
                        row.RelativeItem().AlignRight().Text(orderCount.ToString()).Bold();
                    });
                    col.Item().Row(row => { row.RelativeItem().Text($"Σύνολο τεμαχίων που χτυπήθηκαν: {hitCount}"); row.RelativeItem().AlignRight().Text($"€{hitTotal:0.00}").Bold(); });
                    col.Item().Row(row => { row.RelativeItem().Text($"Ακυρώσεις ειδών: {cancelledCount}"); row.RelativeItem().AlignRight().Text($"−€{cancelledTotal:0.00}").Bold(); });
                    col.Item().Row(row => { row.RelativeItem().Text($"Πιστώσεις: {rows.Sum(r => r.CreditPaymentCount)}"); row.RelativeItem().AlignRight().Text($"€{creditTotal:0.00}").Bold(); });
                    col.Item().Row(row => { row.RelativeItem().Text($"IRIS: {rows.Sum(r => r.IrisPaymentCount)} συναλλαγές"); row.RelativeItem().AlignRight().Text($"€{irisTotal:0.00}").Bold(); });
                    if (record.PendingPreloadCount > 0)
                    {
                        col.Item().PaddingTop(12).Background("#fff6df").Padding(10).Text($"ΕΚΚΡΕΜΕΙΣ ΠΡΟΦΟΡΤΩΣΕΙΣ: {record.PendingPreloadCount} συναλλαγές · €{record.PendingPreloadAmount:0.00}. Διατηρούνται για μεταγενέστερη τακτοποίηση.").Bold();
                    }
                    col.Item().PaddingTop(8).Row(row =>
                    {
                        row.RelativeItem().Text("ΓΕΝΙΚΟ ΣΥΝΟΛΟ:").FontSize(14).Bold();
                        row.RelativeItem().AlignRight().Text($"€{grandTotal:0.00}").FontSize(14).Bold();
                    });
                });

                page.Footer().AlignCenter().Text($"Εκτυπώθηκε: {DateTime.Now:dd/MM/yyyy HH:mm}").FontSize(9);
            });
        });

        return doc.GeneratePdf();
    }

    private static void AppendPaymentTotal(ColumnDescriptor column, string label, decimal amount)
    {
        column.Item().Row(row =>
        {
            row.RelativeItem().Text($"{label}:");
            row.RelativeItem().AlignRight().Text($"€{amount:0.00}").Bold();
        });
    }

    private static string OtherPaymentSummary(EmployeeSummary row) => string.Join("\n", row.OtherPaymentTotals
        .OrderBy(x => PaymentMethodInfo.GetMyDataCode(x.Key))
        .Select(x => $"{ShortPaymentMethodName(x.Key)} €{x.Value:0.00}"));

    private static string OtherPaymentSummary(EmployeeSummary row, params PaymentMethod[] excluded) => string.Join("\n", row.OtherPaymentTotals
        .Where(x => !excluded.Contains(x.Key))
        .OrderBy(x => PaymentMethodInfo.GetMyDataCode(x.Key))
        .Select(x => $"{ShortPaymentMethodName(x.Key)} €{x.Value:0.00}"));

    private static decimal AlternativePaymentAmount(EmployeeSummary row, PaymentMethod method) =>
        row.OtherPaymentTotals.TryGetValue(method, out var amount) ? amount : 0m;

    private static List<EmployeeSummary>? DeserializeSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<EmployeeSummary>>(json); }
        catch (JsonException) { return null; }
    }

    private static string ShortPaymentMethodName(PaymentMethod method) => method switch
    {
        PaymentMethod.DomesticBankAccount => "Κατάθεση",
        PaymentMethod.ForeignBankAccount => "Τράπεζα εξωτερικού",
        PaymentMethod.Check => "Επιταγή",
        PaymentMethod.Credit => "Επί πιστώσει",
        PaymentMethod.WebBanking => "Web Banking",
        PaymentMethod.Iris => "IRIS",
        _ => PaymentMethodInfo.DisplayName(method)
    };

    private static PaymentMethod NormalizePaymentMethod(PaymentMethod method) => method == PaymentMethod.None ? PaymentMethod.Cash : method;
    public static bool IsStaffConsumption(Order order) => order.StaffConsumptionInvoiceType is "6.1" or "6.2";
}
