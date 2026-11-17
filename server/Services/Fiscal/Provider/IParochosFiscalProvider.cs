using RestaurantOrders.Models;

namespace RestaurantOrders.Services.Fiscal.Provider;

/// <summary>
/// Provider transport contract used by fiscal workflows. A future provider can
/// replace ParochosService without changing order intake or kitchen dispatch.
/// </summary>
public interface IParochosFiscalProvider
{
    Task<(bool Success, string? Error)> TestConnectionAsync(CompanySettings settings);
    Task<ParochosSendResult> SendAndWaitAsync(CompanySettings settings, object source, string externalSystemId, int? transmissionType = null);
    Task<ParochosSendResult> GetDocumentByProcessIdAsync(CompanySettings settings, string processId);
    Task<ParochosSendResult> GetDocumentByExternalSystemIdAsync(CompanySettings settings, string externalSystemId);
    Task<ParochosPaymentResult> RequestPaymentAsync(CompanySettings settings, string externalSystemId, string issuerVatNumber, DateTimeOffset invoiceIssueDate, int companyBranch, string invoiceType, string invoiceSeries, long invoiceNumber, decimal netValue, decimal vatAmount, decimal totalValue, decimal paymentAmount, string terminalId, string nspCode, long? mark = null);
    Task<ParochosSimpleResult> SubmitPaymentAsync(CompanySettings settings, string externalSystemId, long mark, decimal amount, string transactionId, string signature, string terminalId, string? paymentMethodInfo = null, int paymentType = 7);
    Task<ParochosSimpleResult> SetPointOfServiceStateAsync(CompanySettings settings, string pointOfService, int actionKind, string? targetPointOfService = null);
    Task<ParochosSimpleResult> OpenPointOfServiceAsync(CompanySettings settings, string pointOfService);
    Task<ParochosSimpleResult> ClosePointOfServiceAsync(CompanySettings settings, string pointOfService);
    Task<ParochosPointOfServiceResult> ListPointOfServiceDetailsAsync(CompanySettings settings, string pointOfService, int filter = 0);
    Task<ParochosSimpleResult> AssignDocumentAsync(CompanySettings settings, string pointOfService, string processId, string externalSystemId);
    Task<ParochosSimpleResult> ConnectDocumentsAsync(CompanySettings settings, string? orderFormProcessId, string? orderFormExternalSystemId, IEnumerable<long> documentMarks);
}
