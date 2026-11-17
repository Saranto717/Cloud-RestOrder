using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public sealed class CashierActivityEntry
{
    public DateTime At { get; init; }
    public string Employee { get; init; } = "";
    public string Table { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Description { get; init; } = "";
    public string Status { get; init; } = "";
    public decimal Amount { get; init; }
    public int? OrderId { get; init; }
}

public sealed class CashierEmployeeDaySummary
{
    public int UserId { get; init; }
    public string Employee { get; init; } = "";
    public decimal HitTotal { get; set; }
    public decimal CancelledTotal { get; set; }
    public decimal PaidTotal { get; set; }
    public decimal PendingTotal { get; set; }
    public int HitCount { get; set; }
    public int PaymentCount { get; set; }
}

public sealed class CashierPendingOrder
{
    public int OrderId { get; init; }
    public string Employee { get; init; } = "";
    public string Table { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public decimal Total { get; init; }
    public decimal Paid { get; init; }
    public decimal Remaining => Math.Max(0, Total - Paid);
}

public sealed class CashierDailyActivity
{
    public List<CashierActivityEntry> Entries { get; init; } = new();
    public List<CashierEmployeeDaySummary> Employees { get; init; } = new();
    public List<CashierPendingOrder> PendingOrders { get; init; } = new();
    public decimal HitTotal => Employees.Sum(x => x.HitTotal);
    public decimal CancelledTotal => Employees.Sum(x => x.CancelledTotal);
    public decimal PaidTotal => Employees.Sum(x => x.PaidTotal);
    public decimal PendingTotal => PendingOrders.Sum(x => x.Remaining);
}

/// <summary>Ημερήσια καρτέλα ταμείου: χτυπήματα, ακυρώσεις, πληρωμές και τρέχοντα υπόλοιπα.</summary>
public static class CashierActivityService
{
    private static readonly HashSet<FiscalStatus> CompletedPaymentStatuses = [FiscalStatus.Issued, FiscalStatus.PaidLocally];

    public static async Task<CashierDailyActivity> GetForDate(AppDbContext db, DateTime date)
    {
        var from = date.Date;
        var to = from.AddDays(1);
        var orders = await db.Orders
            .Where(o => o.Status == OrderStatus.Open ||
                (o.CreatedAt >= from && o.CreatedAt < to) ||
                (o.ClosedAt != null && o.ClosedAt >= from && o.ClosedAt < to) ||
                o.Items.Any(i => (i.CreatedAt >= from && i.CreatedAt < to) ||
                    (i.CancelledAt != null && i.CancelledAt >= from && i.CancelledAt < to)) ||
                o.Payments.Any(p => p.CreatedAt >= from && p.CreatedAt < to) ||
                db.FiscalOrderCancellations.Any(c => c.OrderId == o.Id && c.CreatedAt >= from && c.CreatedAt < to))
            .Include(o => o.WaiterUser)
            .Include(o => o.RestaurantTable)
            .Include(o => o.Items).ThenInclude(i => i.MenuItem)
            .Include(o => o.Payments)
            .AsSplitQuery()
            .ToListAsync();

        var employees = new Dictionary<int, CashierEmployeeDaySummary>();
        var entries = new List<CashierActivityEntry>();
        CashierEmployeeDaySummary ForEmployee(Order order)
        {
            if (!employees.TryGetValue(order.WaiterUserId, out var summary))
            {
                summary = new CashierEmployeeDaySummary
                {
                    UserId = order.WaiterUserId,
                    Employee = order.WaiterUser?.FullName ?? "(άγνωστος χρήστης)"
                };
                employees.Add(order.WaiterUserId, summary);
            }
            return summary;
        }

        foreach (var order in orders)
        {
            var employee = ForEmployee(order);
            var staffConsumption = CashierService.IsStaffConsumption(order);
            var tableName = order.RestaurantTable?.Name ?? "—";
            foreach (var item in order.Items)
            {
                var unit = (item.MenuItem?.Price ?? 0m) + item.ExtrasPrice;
                var amount = Math.Round(unit * item.Quantity * (1m - Math.Clamp(item.DiscountPercent, 0m, 100m) / 100m), 2);
                var itemName = item.MenuItem?.Name ?? $"Είδος #{item.MenuItemId}";
                if (item.CreatedAt >= from && item.CreatedAt < to)
                {
                    if (!staffConsumption) { employee.HitTotal += amount; employee.HitCount++; }
                    entries.Add(new CashierActivityEntry
                    {
                        At = item.CreatedAt, Employee = employee.Employee, Table = tableName,
                        Kind = staffConsumption ? "Ιδιοκατανάλωση · είδος" : "Χτύπημα", Description = $"{item.Quantity} × {itemName}",
                        Status = item.IsCancelled ? "Ακυρώθηκε αργότερα" : order.Status == OrderStatus.Open ? "Εκκρεμεί στο τραπέζι" : "Καταχωρημένο",
                        Amount = amount, OrderId = order.Id
                    });
                }
                if (item.IsCancelled && item.CancelledAt is { } cancelledAt && cancelledAt >= from && cancelledAt < to)
                {
                    if (!staffConsumption) employee.CancelledTotal += amount;
                    entries.Add(new CashierActivityEntry
                    {
                        At = cancelledAt, Employee = employee.Employee, Table = tableName,
                        Kind = "Ακύρωση", Description = $"{item.Quantity} × {itemName}" + (string.IsNullOrWhiteSpace(item.CancelReason) ? "" : $" · {item.CancelReason}"),
                        Status = "Ακυρωμένο είδος", Amount = -amount, OrderId = order.Id
                    });
                }
            }

            foreach (var payment in order.Payments.Where(p => p.CreatedAt >= from && p.CreatedAt < to))
            {
                var paid = CompletedPaymentStatuses.Contains(payment.FiscalStatus);
                if (paid && !staffConsumption)
                {
                    employee.PaidTotal += payment.Amount;
                    employee.PaymentCount++;
                }
                entries.Add(new CashierActivityEntry
                {
                    At = payment.CreatedAt, Employee = employee.Employee, Table = tableName,
                    Kind = staffConsumption ? "Ιδιοκατανάλωση" : paid ? "Εξόφληση" : "Κίνηση πληρωμής",
                    Description = $"{PaymentMethodInfo.DisplayName(payment.PaymentMethod)} · {DocumentKindLabel(payment.DocumentKind)}",
                    Status = PaymentStatusLabel(payment.FiscalStatus), Amount = payment.Amount, OrderId = order.Id
                });
            }

            // Συμβατότητα με παλιές εγγραφές πριν από τον πίνακα OrderPayments.
            if (order.Status == OrderStatus.ForceClosed && order.ClosedAt is { } forcedAt && forcedAt >= from && forcedAt < to)
                entries.Add(new CashierActivityEntry { At = forcedAt, Employee = employee.Employee, Table = tableName, Kind = "Αναγκαστική αποδέσμευση", Description = (order.ForceCloseAction ?? "Κλείσιμο") + " · " + (order.ForceCloseResult ?? ""), Status = "Δεν αποτελεί είσπραξη", Amount = 0m, OrderId = order.Id });
            if (order.Status == OrderStatus.Closed && order.ClosedAt is { } closedAt && closedAt >= from && closedAt < to && order.Payments.Count == 0)
            {
                var wasCancelled = order.Items.Count > 0 && order.Items.All(i => i.IsCancelled);
                var total = order.Items.Where(i => !i.IsCancelled).Sum(i => Math.Round(((i.MenuItem?.Price ?? 0m) + i.ExtrasPrice) * i.Quantity * (1m - Math.Clamp(i.DiscountPercent, 0m, 100m) / 100m), 2));
                if (!wasCancelled && !staffConsumption) employee.PaidTotal += total;
                entries.Add(new CashierActivityEntry
                {
                    At = closedAt, Employee = employee.Employee, Table = tableName,
                    Kind = wasCancelled ? "Ακύρωση παραγγελίας" : staffConsumption ? "Ιδιοκατανάλωση" : "Εξόφληση",
                    Description = wasCancelled ? "Ακυρώθηκε ολόκληρη η παραγγελία" : PaymentMethodInfo.DisplayName(order.PaymentMethod),
                    Status = "Ολοκληρώθηκε", Amount = wasCancelled ? 0m : total, OrderId = order.Id
                });
            }
        }

        var fiscalCancellations = await db.FiscalOrderCancellations
            .Where(c => c.CreatedAt >= from && c.CreatedAt < to)
            .ToListAsync();
        foreach (var cancellation in fiscalCancellations)
        {
            var cancelledOrder = orders.FirstOrDefault(o => o.Id == cancellation.OrderId);
            if (cancelledOrder is null) continue;
            var employee = ForEmployee(cancelledOrder);
            entries.Add(new CashierActivityEntry
            {
                At = cancellation.CreatedAt, Employee = employee.Employee,
                Table = cancelledOrder.RestaurantTable?.Name ?? "—", Kind = "Ακύρωση παραστατικού",
                Description = $"{cancellation.DocumentKind?.ToString() ?? "Παραστατικού"} {cancellation.SeriesName} {cancellation.Number}".Trim(),
                Status = "Ακυρώθηκε στον πάροχο", Amount = 0m, OrderId = cancelledOrder.Id
            });
        }

        var pendingOrders = orders.Where(o => o.Status == OrderStatus.Open && !CashierService.IsStaffConsumption(o))
            .Select(o => new CashierPendingOrder
            {
                OrderId = o.Id,
                Employee = o.WaiterUser?.FullName ?? "(άγνωστος χρήστης)",
                Table = o.RestaurantTable?.Name ?? "—",
                CreatedAt = o.CreatedAt,
                Total = o.Items.Where(i => !i.IsCancelled).Sum(i => Math.Round(((i.MenuItem?.Price ?? 0m) + i.ExtrasPrice) * i.Quantity * (1m - Math.Clamp(i.DiscountPercent, 0m, 100m) / 100m), 2)),
                Paid = o.Payments.Where(p => CompletedPaymentStatuses.Contains(p.FiscalStatus)).Sum(p => p.Amount)
            })
            .Where(o => o.Remaining > 0.005m)
            .OrderByDescending(o => o.Remaining)
            .ToList();
        foreach (var pending in pendingOrders)
        {
            var source = orders.First(o => o.Id == pending.OrderId);
            ForEmployee(source).PendingTotal += pending.Remaining;
        }

        var dayCloses = await db.DayCloses.Include(x => x.ClosedByUser)
            .Where(x => x.ClosedAt >= from && x.ClosedAt < to).ToListAsync();
        entries.AddRange(dayCloses.Select(close => new CashierActivityEntry
        {
            At = close.ClosedAt,
            Employee = close.ClosedByUser?.FullName ?? "(άγνωστος χρήστης)",
            Table = "—",
            Kind = "Κλείσιμο ταμείου",
            Description = close.ClosedByUser is null ? "Ημερήσιο κλείσιμο" : $"Κλείσιμο που καταχωρίστηκε από {close.ClosedByUser.FullName}",
            Status = "Καταγράφηκε",
            Amount = 0m
        }));

        var transfers = await db.TableTransfers
            .Where(t => t.CreatedAt >= from && t.CreatedAt < to)
            .ToListAsync();
        var transferUserIds = transfers.Select(t => t.RequestedByUserId).Distinct().ToArray();
        var transferUsers = await db.Users.Where(u => transferUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);
        entries.AddRange(transfers.Select(t => new CashierActivityEntry
        {
            At = t.CreatedAt,
            Employee = transferUsers.GetValueOrDefault(t.RequestedByUserId, "(άγνωστος χρήστης)"),
            Table = t.SourceName,
            Kind = "Μεταφορά τραπεζιού",
            Description = $"{t.SourceName} → {t.TargetName}",
            Status = t.Stage.ToString(),
            Amount = 0m,
            OrderId = t.SourceOrderId
        }));

        return new CashierDailyActivity
        {
            Entries = entries.OrderByDescending(x => x.At).ToList(),
            Employees = employees.Values.OrderByDescending(x => x.HitTotal).ToList(),
            PendingOrders = pendingOrders
        };
    }

    private static string PaymentStatusLabel(FiscalStatus status) => status switch
    {
        FiscalStatus.Issued => "Εκδόθηκε",
        FiscalStatus.PaidLocally => "Πληρώθηκε τοπικά",
        FiscalStatus.Pending => "Σε εκκρεμότητα",
        FiscalStatus.Failed => "Απέτυχε",
        _ => "Χωρίς έκδοση"
    };

    private static string DocumentKindLabel(FiscalDocumentKind kind) => kind switch
    {
        FiscalDocumentKind.Receipt => "Απόδειξη",
        FiscalDocumentKind.Invoice => "Τιμολόγιο",
        FiscalDocumentKind.OrderForm => "Δελτίο παραγγελίας",
        FiscalDocumentKind.CancelOrderForm => "Ακυρωτικό δελτίο",
        FiscalDocumentKind.CreditReceipt => "Πιστωτικό λιανικής",
        FiscalDocumentKind.CreditInvoice => "Πιστωτικό τιμολόγιο",
        _ => kind.ToString()
    };
}
