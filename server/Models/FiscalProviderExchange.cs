namespace RestaurantOrders.Models;

/// <summary>Fiscal provider exchange; authorization headers are never stored.</summary>
public sealed class FiscalProviderExchange
{
    public long Id { get; set; }
    public DateTime AttemptedAtUtc { get; set; }
    public string Operation { get; set; } = "";
    public bool Production { get; set; }
    public string? ExternalSystemId { get; set; }
    public string? ProcessId { get; set; }
    public string RequestJson { get; set; } = "";
    public int? HttpStatus { get; set; }
    public string? ResponseBody { get; set; }
    public string? TransportError { get; set; }
}
