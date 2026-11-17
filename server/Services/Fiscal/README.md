# Application service boundaries

The application is organized around separate workflows. Order entry and kitchen dispatch do not call a fiscal provider implementation directly.

- `TableOrder.razor` coordinates the UI, but `KitchenOrderDispatchService` owns pending-item selection, kitchen/bar routing, dispatch batches, and sent state.
- After kitchen dispatch is committed, `FiscalOrderFormService` invokes the fiscal document boundary and prints the accepted fiscal Order Form.
- `IFiscalDocumentService` is the application contract. `ParochosFiscalDocumentService` is its current provider-specific implementation.
- `FiscalPayloadBuilder` owns myDATA invoice mapping and line/tax/total calculations; `FiscalConfigurationService`, `FiscalCardPaymentService`, and `FiscalCancellationService` own separate fiscal concerns.
- `IParochosFiscalProvider` is the provider transport boundary implemented by `ParochosService`. `ParochosResponseParser` converts provider response JSON into typed results.
- Card-terminal connectors (ePay, EDPS, Mellon, Viva) remain separate from fiscal-document submission. They handle terminal payment exchange, while fiscal services submit the resulting accounting data.

To replace the fiscal provider, implement `IFiscalDocumentService` and register that implementation in `Program.cs`. To replace only the Parochos transport while keeping the Parochos fiscal workflows, implement `IParochosFiscalProvider`. Kitchen dispatch remains independent in either case.

## Change ownership

- Kitchen routing, printer assignment, order batches, and “sent” state belong to `Services/Orders`.
- myDATA document rules and provider calls belong to `Services/Fiscal`.
- Provider HTTP/authentication and wire response parsing belong to `ParochosService` and `ParochosResponseParser`.
- Physical POS integrations belong to their provider-specific terminal services (`EpayWebEcrService`, `EdpsWebEcrService`, `MellonWebEcrService`, and `VivaCloudTerminalService`).

This gives future maintenance a smaller change surface: changing the fiscal provider should not require editing the kitchen dispatcher or order batching rules.
