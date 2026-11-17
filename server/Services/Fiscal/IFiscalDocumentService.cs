using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services.Fiscal;

/// <summary>
/// Application boundary for issuing and managing fiscal documents.
/// Order taking and kitchen dispatch should depend on this contract, not on a
/// particular fiscal provider or its transport implementation.
/// </summary>
public interface IFiscalDocumentService
{
    Task<CardPaymentPreparation> PrepareCardPaymentAsync(AppDbContext db, Order order, FiscalDocumentKind kind, BankPosTerminalSettings? selectedPos = null, long? mark = null);
    Task<ParochosSimpleResult> SubmitPreloadedPaymentAsync(AppDbContext db, Order order, decimal amount, string transactionId, string signature, string terminalId, string? paymentMethodInfo = null, int paymentType = 7);
    Task<FiscalIssueResult> IssueAsync(AppDbContext db, Order order, FiscalDocumentKind kind, string? cardTransactionId = null);
    Task<IReadOnlyList<PendingFiscalTransmission>> GetPendingTransmissionsAsync(AppDbContext db);
    Task<FiscalIssueResult> RetryPendingTransmissionAsync(AppDbContext db, int orderId);
    Task<FiscalIssueResult> RetryPendingOrderFormTransmissionAsync(AppDbContext db, int fiscalOrderDocumentId);
    Task<OfflineReceiptPrintResult> PrintPendingOfflineReceiptAsync(AppDbContext db, int orderId);
    Task<FiscalIssueResult> CancelSentOrderItemAsync(AppDbContext db, Order order, int orderItemId, string reason = "Ακύρωση είδους από τον σερβιτόρο", int? quantityToCancel = null);
    Task<FiscalIssueResult> ApplyDiscountWithReturnOrderFormsAsync(AppDbContext db, Order order, IReadOnlyCollection<int> orderItemIds);
    Task<FiscalIssueResult> CancelWholeOrderAsync(AppDbContext db, Order order, string reason = "Ακύρωση ολόκληρης παραγγελίας από τον σερβιτόρο", bool closePointOfService = true);
    Task<FiscalIssueResult> CloseEmptyOrderAsync(AppDbContext db, Order order);
    Task<CardPaymentPreparation> PrepareSelectedCardPaymentAsync(AppDbContext db, Order order, IReadOnlyCollection<OrderItem> selectedItems, FiscalDocumentKind kind, BankPosTerminalSettings? selectedPos = null);
    Task<FiscalIssueResult> IssueSelectedItemsPaymentAsync(AppDbContext db, Order order, IReadOnlyCollection<OrderItem> selectedItems, PaymentMethod paymentMethod, FiscalDocumentKind kind, string? cardTransactionId = null, string? cardSignature = null, string? cardTerminalId = null, string? cardNspCode = null, string? paymentExternalSystemId = null, string? paymentSeriesName = null, long? paymentInvoiceNumber = null);
    Task<FiscalIssueResult> IssueOrderFormAsync(AppDbContext db, Order order, List<OrderItem> newlyAddedItems);
    Task<FiscalIssueResult> CreditIssuedDocumentAsync(AppDbContext db, int orderId, int? orderPaymentId, string reason);
}
