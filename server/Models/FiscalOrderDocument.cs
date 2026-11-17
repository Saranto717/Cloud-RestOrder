namespace RestaurantOrders.Models;

/// <summary>
/// Ένα φορολογικό "Δελτίο Παραγγελίας" (Order Form) που εκδόθηκε στην ΑΑΔΕ μέσω Parochos
/// τη στιγμή που ο σερβιτόρος έστειλε είδη στην κουζίνα/μπαρ. Η τελική Απόδειξη/Τιμολόγιο
/// στο κλείσιμο του τραπεζιού τα συσχετίζει (correlatedInvoices) όλα μαζί.
/// </summary>
public class FiscalOrderDocument
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public string ExternalSystemId { get; set; } = "";
    public string SeriesName { get; set; } = "";
    public long Number { get; set; }
    public long? Mark { get; set; }
    public string? QrCode { get; set; }
    public string? ProcessId { get; set; }
    public string? Uid { get; set; }
    public DateTime IssuedAt { get; set; } = DateTime.Now;

    // The exact fiscal payload and idempotency state are retained for Order
    // Forms too.  A provider outage must not make already-dispatched kitchen
    // items appear unsent or cause a second 8.6 document to be created.
    public string? ParochosInvoicePayloadJson { get; set; }
    public string? ParochosTransmissionState { get; set; }
    public bool ParochosOutboxArchived { get; set; }
    public DateTime? ParochosTransmissionAttemptedAtUtc { get; set; }
    public int? ParochosTransmissionFailure { get; set; }
    public string? FiscalErrorMessage { get; set; }

    // When Parochos cancels the open Order Tickets, keep the original
    // Order Form rows as historical evidence instead of deleting them.
    public bool IsCancelled { get; set; } = false;
    public bool IsCancellation { get; set; } = false;
    // A non-cancelled 8.6 recType=7 document that returns the discount portion
    // of an already-issued order form. It remains open for final POS settlement.
    public DateTime? PrintConfirmedAtUtc { get; set; }
    public string? ConfirmedPrinterIds { get; set; }
    public int? CancellationSourceId { get; set; }
    public bool IsDiscountReturn { get; set; } = false;
    public DateTime? CancelledAt { get; set; }
    public long? CancellationMark { get; set; }

    public List<FiscalOrderDocumentItem> Items { get; set; } = new();
}
