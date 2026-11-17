namespace RestaurantOrders.Models;

public class Order
{
    public int Id { get; set; }

    public int RestaurantTableId { get; set; }
    public RestaurantTable? RestaurantTable { get; set; }

    public int WaiterUserId { get; set; }
    public User? WaiterUser { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? ClosedAt { get; set; }
    public string? ForceCloseAction { get; set; }
    public string? StaffConsumptionInvoiceType { get; set; }
    public string? ForceCloseResult { get; set; }
    public int? ForceClosedByUserId { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Open;
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.None;

    // Null = ακόμα δεν έχει μετρηθεί σε κάποιο "Κλείσιμο Ημέρας". Μόλις γίνει κλείσιμο,
    // παίρνει το Id εκείνου του κλεισίματος και δεν ξαναμετριέται σε επόμενο.
    public int? DayCloseId { get; set; }
    public DayClose? DayClose { get; set; }

    // Φορολογικό παραστατικό (myDATA/Parochos)
    public FiscalDocumentKind? DocumentKind { get; set; }
    public string? InvoiceSeriesName { get; set; }
    public long? InvoiceNumber { get; set; }
    public FiscalStatus FiscalStatus { get; set; } = FiscalStatus.NotIssued;
    public string? FiscalErrorMessage { get; set; }

    public string? ParochosExternalId { get; set; }
    public string? ParochosProcessId { get; set; }
    public long? ParochosMark { get; set; }
    public string? ParochosUid { get; set; }
    public string? ParochosAuthenticationCode { get; set; }
    public string? ParochosQrCode { get; set; }
    public string? ParochosPdfUrl { get; set; }

    // Durable outbox for invoice submissions whose result is ambiguous or still
    // being processed. The exact request and externalSystemId must survive a
    // restart so recovery can query Parochos before any retry.
    // Immutable local 8.6 and order-line identities for this full payment, including unsigned 8.6s.
    public string? ReceiptScopeJson { get; set; }
    public string? ParochosInvoicePayloadJson { get; set; }
    public string? ParochosTransmissionState { get; set; }
    public bool ParochosOutboxArchived { get; set; }
    public DateTime? ParochosTransmissionAttemptedAtUtc { get; set; }
    public DateTime? ParochosOfflineReceiptPrintedAtUtc { get; set; }
    // 1 = entity/point of issue cannot reach provider, 2 = provider cannot
    // reach AADE/myDATA. Retained so every retry uses the original reason.
    public int? ParochosTransmissionFailure { get; set; }

    // EFT-POS / Parochos requestPayment στοιχεία.
    public string? ParochosPaymentSignature { get; set; }
    public string? ParochosPaymentUid { get; set; }
    public string? CardTransactionId { get; set; }
    public string? CardTerminalId { get; set; }
    public string? CardNspCode { get; set; }

    // Deferred AADE-preloaded EFT-POS transaction reconciliation.
    public int? AadePreloadedTerminalSettingsId { get; set; }
    public string? AadePreloadedIntentId { get; set; }
    public string? AadePreloadedStatus { get; set; }
    public DateTime? AadePreloadedAt { get; set; }
    public string? AadePreloadedStatusMessage { get; set; }

    // Στοιχεία πελάτη - μόνο όταν εκδίδεται Τιμολόγιο (όχι απλή απόδειξη)
    public string? CounterpartVatNumber { get; set; }
    public string? CounterpartName { get; set; }
    public string? CounterpartStreet { get; set; }
    public string? CounterpartCity { get; set; }
    public string? CounterpartPostalCode { get; set; }
    public string? CounterpartStreetNumber { get; set; }
    public string? CounterpartTaxOffice { get; set; }
    public string? CounterpartMainActivity { get; set; }

    public List<OrderItem> Items { get; set; } = new();

    public List<OrderPayment> Payments { get; set; } = new();
}
