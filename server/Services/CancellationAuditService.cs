using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public sealed class CancellationReportRow
{
    public DateTime At { get; init; }
    public string Employee { get; init; } = "";
    public string Table { get; init; } = "";
    public string Action { get; init; } = "";
    public string Description { get; init; } = "";
    public string Reason { get; init; } = "";
    public int Quantity { get; init; }
    public decimal Amount { get; init; }
}

public static class CancellationAuditService
{
    public static async Task<List<CancellationReportRow>> GetReportAsync(AppDbContext db, DateTime from, DateTime to)
    {
        var start = from.Date;
        var end = to.Date.AddDays(1);
        var entries = await db.CancellationAuditEntries.AsNoTracking()
            .Where(x => x.At >= start && x.At < end)
            .OrderByDescending(x => x.At)
            .ToListAsync();
        var auditedItemIds = entries.Where(x => x.OrderItemId.HasValue).Select(x => x.OrderItemId!.Value).ToArray();
        var ids = entries.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().ToArray();
        var users = await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.FullName);
        var result = entries.Select(x => new CancellationReportRow
        {
            At = x.At,
            Employee = x.UserId is int id ? users.GetValueOrDefault(id, "(ανενεργός χρήστης)") : "(δεν καταγράφηκε)",
            Table = x.TableName,
            Action = x.Action,
            Description = x.Description,
            Reason = x.Reason,
            Quantity = x.Quantity,
            Amount = x.Amount
        }).ToList();

        var legacyItems = await db.OrderItems.AsNoTracking()
            .Include(i => i.MenuItem)
            .Include(i => i.Order).ThenInclude(o => o!.RestaurantTable)
            .Include(i => i.Order).ThenInclude(o => o!.WaiterUser)
            .Where(i => i.IsCancelled && i.CancelledAt >= start && i.CancelledAt < end &&
                !auditedItemIds.Contains(i.Id))
            .ToListAsync();
        result.AddRange(legacyItems.Select(i => new CancellationReportRow
        {
            At = i.CancelledAt!.Value,
            Employee = i.Order?.WaiterUser?.FullName ?? "(παλιά εγγραφή)",
            Table = i.Order?.RestaurantTable?.Name ?? "—",
            Action = "Ακύρωση είδους",
            Description = $"{i.Quantity} × {i.MenuItem?.Name ?? "Είδος"}",
            Reason = string.IsNullOrWhiteSpace(i.CancelReason) ? "Παλιά εγγραφή · εμφανίζεται ο υπεύθυνος της παραγγελίας" : i.CancelReason,
            Quantity = i.Quantity,
            Amount = Math.Round(i.Quantity * ((i.MenuItem?.Price ?? 0m) + i.ExtrasPrice) *
                (1m - Math.Clamp(i.DiscountPercent, 0m, 100m) / 100m), 2)
        }));

        var auditedOrderIds = entries.Select(x => x.OrderId).Distinct().ToArray();
        var legacyForceCloses = await db.Orders.AsNoTracking()
            .Include(o => o.RestaurantTable)
            .Where(o => o.Status == OrderStatus.ForceClosed && o.ClosedAt >= start && o.ClosedAt < end &&
                !auditedOrderIds.Contains(o.Id))
            .ToListAsync();
        var forceUserIds = legacyForceCloses.Where(o => o.ForceClosedByUserId.HasValue)
            .Select(o => o.ForceClosedByUserId!.Value).Distinct().ToArray();
        var forceUsers = await db.Users.AsNoTracking().Where(u => forceUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);
        result.AddRange(legacyForceCloses.Select(o => new CancellationReportRow
        {
            At = o.ClosedAt!.Value,
            Employee = o.ForceClosedByUserId is int id ? forceUsers.GetValueOrDefault(id, "(ανενεργός χρήστης)") : "(παλιά εγγραφή)",
            Table = o.RestaurantTable?.Name ?? "—",
            Action = o.ForceCloseAction ?? "Αναγκαστικό κλείσιμο",
            Description = "Παλιά αναγκαστική ενέργεια",
            Reason = o.ForceCloseResult ?? "Δεν υπάρχει αποθηκευμένο αποτέλεσμα",
            Quantity = 0,
            Amount = 0m
        }));
        return result.OrderByDescending(x => x.At).ToList();
    }

    public static void Add(AppDbContext db, Order order, int? userId, string action, string description,
        string reason, int quantity, decimal amount, int? orderItemId = null) =>
        db.CancellationAuditEntries.Add(new CancellationAuditEntry
        {
            OrderId = order.Id,
            OrderItemId = orderItemId,
            UserId = userId,
            At = DateTime.Now,
            TableName = order.RestaurantTable?.Name ?? "—",
            Action = action,
            Description = description,
            Reason = reason,
            Quantity = quantity,
            Amount = Math.Round(amount, 2)
        });
}
