using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RestaurantOrders.Data;
using RestaurantOrders.Models;
using RestaurantOrders.Services;
using RestaurantOrders.Services.Orders;
using RestaurantOrders.Services.Fiscal;
using RestaurantOrders.Services.Fiscal.Provider;

static void Check(bool ok, string description) { if (!ok) throw new Exception(description); Console.WriteLine("PASS: " + description); }
var staffCat = new MenuCategory { Name="Coffee", VatPercent=13m, IncomeClassificationCategory="category1_2" };
var staffItem = new MenuItem { Name="Coffee", Price=3m, StaffConsumptionPrice=0.10m, MenuCategory=staffCat };
var staffLine = new OrderItem { MenuItem=staffItem, Quantity=2, DiscountPercent=(1m - 0.10m / 3m) * 100m };
var staffOrder = new Order { StaffConsumptionInvoiceType="6.1", Items=new() { staffLine }, PaymentMethod=PaymentMethod.Cash };
var staffSettings = new CompanySettings { VatNumber="123456789", CompanyName="Example", ReceiptInvoiceType="11.1" };
var staffTotals = FiscalPayloadBuilder.CalculateTotals(staffOrder);
var staffPayload = FiscalPayloadBuilder.BuildInvoice(staffSettings, staffOrder, FiscalDocumentKind.Receipt, "ΑΥΤ", 1, staffTotals, FiscalConfigurationService.InvoiceType(staffOrder, FiscalDocumentKind.Receipt, staffSettings), null, false, new());
using (var staffJson = JsonDocument.Parse(JsonSerializer.Serialize(staffPayload)))
{
    var root=staffJson.RootElement; var line=root.GetProperty("invoiceDetails")[0];
    Check(root.GetProperty("invoiceHeader").GetProperty("invoiceType").GetString()=="6.1" && root.GetProperty("counterpart").GetProperty("vatNumber").GetString()=="123456789", "staff document uses 6.1 and issuer VAT as counterpart");
    Check(line.GetProperty("classificationCategory").GetString()=="category1_6" && line.GetProperty("classificationType").GetString()=="E3_595" && line.GetProperty("totalValue").GetDecimal()==0.20m && line.GetProperty("vatCategory").GetInt32()==2, "staff document uses cost, correct classification and item VAT");
    Check(FiscalConfigurationService.SeriesKind(staffOrder, FiscalDocumentKind.Receipt)==FiscalDocumentKind.SelfSupply, "staff goods use separate fiscal series");
    staffOrder.StaffConsumptionInvoiceType="6.2";
    Check(FiscalConfigurationService.SeriesKind(staffOrder, FiscalDocumentKind.Receipt)==FiscalDocumentKind.SelfUse && FiscalConfigurationService.InvoiceType(staffOrder,FiscalDocumentKind.Receipt,staffSettings)=="6.2", "staff services use 6.2 and separate series");
}
Check(FiscalDocumentIdentity.IsConfirmed(new ParochosSendResult { ExternalSystemId="sale-42", Mark=41, Success=true }, "sale-42", null, null), "matching external ID confirms receipt identity");
Check(!FiscalDocumentIdentity.IsConfirmed(new ParochosSendResult { ExternalSystemId="other", Mark=41, Success=true }, "sale-42", null, null), "different external ID cannot confirm receipt");
Check(!FiscalDocumentIdentity.IsConfirmed(new ParochosSendResult { Mark=41, Success=true }, "sale-42", null, null), "MARK without identifiers cannot confirm receipt");
Check(FiscalDocumentIdentity.IsConfirmed(new ParochosSendResult { ProcessId="process-42", Mark=41, Success=true }, "sale-42", "process-42", null), "matching stored process ID confirms receipt");
Check(!FiscalDocumentIdentity.IsConfirmed(new ParochosSendResult { ExternalSystemId="sale-42", ProcessId="other", Mark=41, Success=true }, "sale-42", "process-42", null), "conflicting process ID rejects receipt");
Check(FiscalStoredPayload.Total("{\"invoice\":{\"invoiceSummary\":{\"totalValue\":3.00}}}")==3m, "pending receipt amount comes from frozen fiscal payload");
Check(FiscalStoredPayload.Total("{\"source\":{\"invoice\":{\"invoiceSummary\":{\"totalValue\":9.00}}}}") == 9m, "wrapped fiscal payload amount is read");
Check(FiscalStoredPayload.Total("{invalid") is null, "invalid payload safely falls back");
using var sql = new SqliteConnection("Data Source=:memory:");
sql.Open();
var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(sql).Options;
using var db = new AppDbContext(options);
db.Database.EnsureCreated();
db.Database.ExecuteSqlRaw("CREATE TABLE OrderNumberSequence (Id INTEGER PRIMARY KEY, CurrentNumber INTEGER); INSERT INTO OrderNumberSequence VALUES (1,57)");
var device = new PrintDevice();
var printer = new PrinterService(NullLogger<PrinterService>.Instance, device);
var provider = DispatchProxy.Create<IParochosFiscalProvider, Simulated>();
var sim = (Simulated)(object)provider;
var config = new FiscalConfigurationService();
var service = new ParochosFiscalDocumentService(provider, config, null!,
    new FiscalCancellationService(provider, config, NullLogger<FiscalCancellationService>.Instance),
    printer, NullLogger<ParochosFiscalDocumentService>.Instance);
var fiscal = new FiscalOrderFormService(service, printer);
var dispatch = new KitchenOrderDispatchService(printer);
var cat = new MenuCategory { Name = "Coffee", PrinterStation = new PrinterStation { Name = "Disconnected", ConnectionType = "Sunmi" } };
var item = new OrderItem { MenuItem = new MenuItem { Name = "frape", MenuCategory = cat, Price = 3 } };
var order = new Order { RestaurantTable = new RestaurantTable { Name = "EX10" }, WaiterUser = new User(), Items = new() { item } };
db.Add(order); db.Add(new CompanySettings { UseParochosProvider = true, IssueOrderFormsToAade = true, VatNumber = "test", ParochosSubscriptionKey = "test" });
await db.SaveChangesAsync();
var accepted = await dispatch.DispatchPendingAsync(db, order.Id, "EX10", "-", "");
Check(accepted.DailyOrderNumber == 58 && accepted.FailedStations.Count == 1 && sim.Sends.Count == 0, "order commits before any fiscal call despite failed kitchen printer");
using var fresh = new AppDbContext(options); // discard original EF session to simulate restart between steps
var reopened = await fresh.OrderItems.SingleAsync();
Check(reopened.IsPrinted && reopened.KitchenPrintUnconfirmed, "reopened table keeps submitted item, with separate kitchen warning");
Check(!(await dispatch.DispatchPendingAsync(fresh, order.Id, "EX10", "-", "")).HasItems, "second submit does not dispatch or number another batch");
var eventRow = (await service.GetPendingTransmissionsAsync(fresh)).Single();
Check(eventRow.BatchId == accepted.BatchId, "interrupted handoff is visible after restart");
await fresh.MenuItems.ExecuteUpdateAsync(set => set.SetProperty(m=>m.Price, 5m)); // price change during interruption
var issued = await fiscal.CheckAsync(fresh, eventRow);
Check(sim.Sends.Single().Json.GetProperty("invoice").GetProperty("invoiceSummary").GetProperty("totalValue").GetDecimal()==3m,"8.6 uses accepted price, not the new catalog price");
Check(issued.OfflineIssued && issued.TransmissionFailure == 1, "offline 8.6 is issued with failure 1");
var original = await fresh.FiscalOrderDocuments.SingleAsync();
device.Online=true;
var print=await fiscal.PrintExistingAsync(fresh,original.Id);
var printCount=device.Count;
Check(print.Success && (await fiscal.PrintExistingAsync(fresh,original.Id)).WasAlreadyPrinted && device.Count==printCount && sim.Sends.Count==1,"fiscal print retry prints stored form without provider or second original");
device.Online=false;
Check(sim.Sends.Count == 1 && original.Items.Count == 1 && original.Mark == null && !await fresh.OrderPrintBatches.AnyAsync(b=>b.DispatchNotificationPending), "one fiscal ledger row consumes exactly one dispatch event");
await fiscal.CheckAsync(fresh,eventRow);
Check(sim.Sends.Count == 1, "stale dispatch request cannot reissue");
var currentOrder = await fresh.Orders.Include(o=>o.RestaurantTable).SingleAsync();
var cancel = await service.CancelSentOrderItemAsync(fresh,currentOrder,reopened.Id);
var reversal = await fresh.FiscalOrderDocuments.SingleAsync(f=>f.IsCancellation);
Check(cancel.OfflineIssued && reversal.CancellationSourceId == original.Id && await fresh.OrderItems.AnyAsync(i=>i.IsCancelled), "offline X records cancellation against exactly the original item and order form");
var deferred = await service.RetryPendingOrderFormTransmissionAsync(fresh,reversal.Id);
Check(deferred.RequiresRecovery && sim.Sends.Count == 1, "cancellation is not sent before original MARK");
sim.Send = new() { Success = true, Mark = 501 };
sim.Lookup = new() { ErrorCode = "00010005" };
var sent = await service.RetryPendingOrderFormTransmissionAsync(fresh,original.Id);
Check(sent.Mark == 501 && sim.Sends[1].Id == sim.Sends[0].Id && sim.Sends[1].Json.GetProperty("invoice").GetProperty("transmissionFailure").GetInt32()==1, "explicit miss retries original 8.6 identity and failure code");
sim.Send = new() { Success = true, Mark = 502 };
var neg = await service.RetryPendingOrderFormTransmissionAsync(fresh,reversal.Id);
var negative = sim.Sends.Last().Json.GetProperty("invoice");
Check(negative.GetProperty("invoiceSummary").GetProperty("totalValue").GetDecimal()==3m,"cancellation reverses the original price");
Check(neg.Mark==502 && negative.GetProperty("correlatedInvoices")[0].GetProperty("mark").GetInt64()==501 && negative.GetProperty("invoiceDetails")[0].GetProperty("recType").GetInt32()==7, "reversal gets its own MARK and references original MARK");
Check((await service.GetPendingTransmissionsAsync(fresh)).Count==0,"confirmed original and reversal disappear from pending");
fresh.Add(new OrderItem { OrderId=order.Id,MenuItemId=item.MenuItemId });
await fresh.SaveChangesAsync();
var second = await dispatch.DispatchPendingAsync(fresh,order.Id,"EX10","-","");
sim.Send=new() { ErrorCode="00010032" }; sim.Lookup=new() { ErrorCode="00010032" };
var tf2=await fiscal.ConsumeBatchAsync(fresh,second.BatchId);
Check(tf2.Issue.OfflineIssued && tf2.Issue.TransmissionFailure==2,"AADE outage yields failure 2");
var pending=(await service.GetPendingTransmissionsAsync(fresh)).Single();
var count=sim.Sends.Count;
sim.Lookup=new() { ErrorCode="00010005" };
await fiscal.CheckAsync(fresh,pending);
Check(sim.Sends.Count==count,"provider-owned failure 2 is checked, not resubmitted");
sim.Lookup=new() { Success=true,Mark=503,ExternalSystemId=(await fresh.FiscalOrderDocuments.SingleAsync(x=>x.Id==pending.FiscalOrderDocumentId)).ExternalSystemId };
await fiscal.CheckAsync(fresh,pending);
Check(sim.Sends.Count==count && (await service.GetPendingTransmissionsAsync(fresh)).Count==0,"same ID already transmitted: removed without resending");

// A stale POS number is not evidence. Only a lookup tied to this receipt is acceptable.
var receipt=new Order { RestaurantTableId=order.RestaurantTableId,WaiterUserId=order.WaiterUserId,
    FiscalStatus=FiscalStatus.Pending,ParochosExternalId="exact-receipt",ParochosProcessId="stored-process",
    InvoiceSeriesName="POS",InvoiceNumber=122,ParochosInvoicePayloadJson="""{"invoice":{"issuer":{"vatNumber":"123456789","branch":0},"invoiceHeader":{"series":"POS","aa":122,"invoiceType":"11.1","issueDate":"2026-11-09T10:00:00","PointOfService":"EX10"},"invoiceSummary":{"totalValue":3.0,"totalNetValue":2.42,"totalVatAmount":0.58},"invoiceDetails":[{"entityName":"coffee","quantity":1,"totalValue":3.0,"vatCategory":1}],"correlatedInvoices":[{"mark":123}],"paymentMethods":[{"type":3,"amount":3.0}]}}""",ParochosTransmissionState="OfflineIssued",ParochosTransmissionFailure=1 };
fresh.Add(receipt);await fresh.SaveChangesAsync();
Check(FiscalCollisionInspector.MatchesRecordedReceipt(receipt.ParochosInvoicePayloadJson, receipt.ParochosInvoicePayloadJson), "identical complete fiscal contents match");
var changedTable = System.Text.Json.Nodes.JsonNode.Parse(receipt.ParochosInvoicePayloadJson!)!;
changedTable["invoice"]!["invoiceHeader"]!["PointOfService"]="different-table";
Check(!FiscalCollisionInspector.MatchesRecordedReceipt(receipt.ParochosInvoicePayloadJson,changedTable.ToJsonString()), "same amounts and documents with different table cannot match");
var changedMark = System.Text.Json.Nodes.JsonNode.Parse(receipt.ParochosInvoicePayloadJson!)!;
changedMark["invoice"]!["correlatedInvoices"]![0]!["mark"]=124;
Check(!FiscalCollisionInspector.MatchesRecordedReceipt(receipt.ParochosInvoicePayloadJson,changedMark.ToJsonString()), "same table and amount with different order form cannot match");
var absentPoint = System.Text.Json.Nodes.JsonNode.Parse(receipt.ParochosInvoicePayloadJson!)!;
((System.Text.Json.Nodes.JsonObject)absentPoint["invoice"]!["invoiceHeader"]!).Remove("PointOfService");
Check(!FiscalCollisionInspector.MatchesRecordedReceipt(receipt.ParochosInvoicePayloadJson,absentPoint.ToJsonString()), "missing table cannot confirm receipt match");

sim.Lookup=new() { ErrorCode="00010022" };sim.ByProcess=new() { ErrorCode="00010022" };
await service.RetryPendingTransmissionAsync(fresh,receipt.Id);
Check(sim.Sends.Count==count && (await service.GetPendingTransmissionsAsync(fresh)).Count==1,"number collision alone cannot clear or resend receipt");
sim.ByProcess=new() { Success=true,Mark=999,ProcessId="stored-process" };
await service.RetryPendingTransmissionAsync(fresh,receipt.Id);
Check((await service.GetPendingTransmissionsAsync(fresh)).Any(x=>x.OrderId==receipt.Id) && receipt.ParochosMark==null,"matching process ID alone does not clear a disputed receipt without recorded details");
var matchingSend = new FiscalProviderExchange { AttemptedAtUtc=DateTime.UtcNow, Operation="/api/send", ExternalSystemId="exact-receipt", ProcessId="stored-process", HttpStatus=200, RequestJson="{\"source\":"+receipt.ParochosInvoicePayloadJson+"}" };
fresh.FiscalProviderExchanges.Add(matchingSend); await fresh.SaveChangesAsync();
await service.RetryPendingTransmissionAsync(fresh,receipt.Id);
Check(sim.Sends.Count==count && !(await service.GetPendingTransmissionsAsync(fresh)).Any(),"verified identity and matching recorded receipt details clear pending without resend");
fresh.FiscalProviderExchanges.Remove(matchingSend); await fresh.SaveChangesAsync();

foreach (var mode in new[] { 1, 2, 400 })
{
    fresh.ChangeTracker.Clear();
    var takeaway = new Order { PaymentMethod=PaymentMethod.Cash, RestaurantTableId=order.RestaurantTableId,
        WaiterUserId=order.WaiterUserId, Items=new(){new OrderItem { MenuItemId=item.MenuItemId }} };
    fresh.Add(takeaway); await fresh.SaveChangesAsync();
    await dispatch.DispatchPendingAsync(fresh,takeaway.Id,"TA","-","",tableService:false);
    Check(!await fresh.OrderPrintBatches.AnyAsync(b=>b.OrderId==takeaway.Id && b.DispatchNotificationPending),
        "takeaway submission does not issue a receipt or order form");
    sim.Lookup=new() { ErrorCode="00010005" };
    sim.Send=mode switch { 1 => new(){ErrorCategory="NETWORK",OutcomeUnknown=true},2=>new(){ErrorCode="00010032"},_=>new(){ErrorCategory="HTTP",ErrorCode="400"} };
    var paid=await service.IssueAsync(fresh,takeaway,FiscalDocumentKind.Receipt);
    if(mode==400) {Check(!paid.OfflineIssued && !paid.Success,"HTTP validation error never becomes a locally issued receipt");continue;}
    Check(paid.OfflineIssued && paid.TransmissionFailure==mode,"cash receipt issued offline at payment: TF"+mode);
    var pendingReceipt = (await service.GetPendingTransmissionsAsync(fresh)).Single(row=>row.OrderId==takeaway.Id);
    var id=takeaway.ParochosExternalId;
    if(mode==2) {
        var before=sim.Sends.Count; await fiscal.CheckAsync(fresh,pendingReceipt);
        Check(sim.Sends.Count==before,"TF2 receipt is not retransmitted by POS");
        sim.Lookup = new(){Success=true,Mark=702,ExternalSystemId=id};
    }
    sim.Send = new(){Success=true,Mark=701};
    var done = await fiscal.CheckAsync(fresh,pendingReceipt);
    Check(done.Mark>0 && !(await service.GetPendingTransmissionsAsync(fresh)).Any(row=>row.OrderId==takeaway.Id),
        "payment confirmed and removed from pending, TF"+mode);
    if(mode==1) Check(sim.Sends.Last().Id==id,"cash recovery never changes external ID");
}

// The two portal examples have the same series, number, type, coffee, VAT and total.
fresh.ChangeTracker.Clear();
var posSeries = new InvoiceSeries { SeriesName="POS", Kind=FiscalDocumentKind.Receipt, IsActive=true, NextNumber=125 };
fresh.Add(posSeries); await fresh.SaveChangesAsync();
async Task<Order> Coffee(){
    var o=new Order { RestaurantTableId=order.RestaurantTableId, WaiterUserId=order.WaiterUserId, PaymentMethod=PaymentMethod.Cash,
        Items=new(){new OrderItem { MenuItem=await fresh.MenuItems.Include(m=>m.MenuCategory).SingleAsync(m=>m.Id==item.MenuItemId) }} };
    o.Items[0].MenuItem!.Price=3; fresh.Add(o);await fresh.SaveChangesAsync();return o;
}
var offline125=await Coffee();
var ticket= new FiscalOrderDocument {OrderId=offline125.Id,ExternalSystemId="ticket-at-2001",SeriesName="TEST8",Number=1,
    Items=new(){new FiscalOrderDocumentItem { OrderItemId=offline125.Items[0].Id,Quantity=1 }}};
fresh.Add(ticket); await fresh.SaveChangesAsync();
sim.Send=new(){ErrorCategory="NETWORK",OutcomeUnknown=true};sim.Lookup=new(){ErrorCode="00010005"};
var p125=await service.IssueAsync(fresh,offline125,FiscalDocumentKind.Receipt);
Check(p125.InvoiceNumber==125 && p125.OfflineIssued && offline125.ReceiptScopeJson!.Contains(ticket.Id.ToString()),"offline POS 125 reserves its number and remembers its unsigned 8.6");
var n=sim.Sends.Count;
sim.Lookup=new(){ErrorCode="00010022"};
await service.RetryPendingTransmissionAsync(fresh,offline125.Id);
Check(sim.Sends.Count==n && offline125.InvoiceNumber==125 && offline125.ParochosMark==null,"duplicate POS 125 never changes number or adopts foreign MARK");
var inspection = new FiscalCollisionInspector(provider, config);
var nothingFound = await inspection.InspectAsync(fresh, offline125.Id);
Check(nothingFound.Contains("Δεν βρέθηκε"), "collision inspection reports no local signed candidate");
var sameExchange = new FiscalProviderExchange { AttemptedAtUtc=DateTime.UtcNow, Operation="/api/send", ExternalSystemId=offline125.ParochosExternalId,
    ProcessId="own-process", HttpStatus=200, RequestJson="{\"source\":"+offline125.ParochosInvoicePayloadJson+"}" };
fresh.FiscalProviderExchanges.Add(sameExchange); await fresh.SaveChangesAsync();
sim.Lookup = new() { Success=true, ExternalSystemId=offline125.ParochosExternalId, ProcessId="own-process", Mark=787125 };
var ownCheck=await inspection.InspectAsync(fresh,offline125.Id);
Check(ownCheck.Contains("ίδιο εξωτερικό αναγνωριστικό") && ownCheck.Contains("Σύγκριση με το αποθηκευμένο αίτημα"), "inspection checks pending receipt's own identity and original saved send");
fresh.FiscalProviderExchanges.Remove(sameExchange); await fresh.SaveChangesAsync();
sim.Lookup = new() { ErrorCode="00010022" };
var changedPayload = System.Text.Json.Nodes.JsonNode.Parse(offline125.ParochosInvoicePayloadJson!)!;
changedPayload["invoice"]!["invoiceSummary"]!["totalValue"] = 8m;
changedPayload["invoice"]!["invoiceHeader"]!["PointOfService"] = "OTHER-TABLE";
changedPayload["invoice"]!["correlatedInvoices"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"mark\":123456}]");
var otherReceipt = new Order { RestaurantTableId=order.RestaurantTableId, WaiterUserId=order.WaiterUserId,
    InvoiceSeriesName="POS", InvoiceNumber=125, FiscalStatus=FiscalStatus.Issued,
    ParochosMark=777125, ParochosExternalId="other-pos-125", ParochosInvoicePayloadJson=changedPayload.ToJsonString() };
fresh.Add(otherReceipt); await fresh.SaveChangesAsync();
sim.Lookup = new() { Success=true, ExternalSystemId="other-pos-125", Mark=777125 };
var sendsBeforeInspect=sim.Sends.Count;
var foundDifference = await inspection.InspectAsync(fresh,offline125.Id);
Check(foundDifference.Contains("Ο πάροχος επιβεβαίωσε") && foundDifference.Contains("σύνολο") && foundDifference.Contains("συσχετισμένα δελτία") && foundDifference.Contains("τραπέζι") && sim.Sends.Count==sendsBeforeInspect, "collision inspection confirms local candidate at provider and identifies payload differences without send");
var equivalent = System.Text.Json.Nodes.JsonNode.Parse(offline125.ParochosInvoicePayloadJson!)!;
equivalent["invoice"]!["invoiceSummary"]!["totalValue"] = 3.000m;
equivalent["invoice"]!["invoiceDetails"]![0]!["quantity"] = 1.000m;
equivalent["invoice"]!["invoiceDetails"]![0]!["totalValue"] = 3.000m;
otherReceipt.ParochosInvoicePayloadJson=equivalent.ToJsonString(); await fresh.SaveChangesAsync();
var sameValues = await inspection.InspectAsync(fresh,offline125.Id);
Check(!sameValues.Contains("Διαφέρουν:") && sameValues.Contains("δεν αποδεικνύει"), "equal amounts with differing decimal precision are not reported as different, and matching content is not proof");
var exactSend = new FiscalProviderExchange { AttemptedAtUtc=DateTime.UtcNow, Operation="/api/send", ExternalSystemId="other-pos-125",
    ProcessId="verified-process", HttpStatus=200, RequestJson="{\"source\":"+changedPayload.ToJsonString()+"}", ResponseBody="{\"status\":0}" };
fresh.FiscalProviderExchanges.Add(exactSend); await fresh.SaveChangesAsync();
sim.Lookup = new() { Success=true, ExternalSystemId="other-pos-125", ProcessId="verified-process", Mark=777125 };
var fromHistory=await inspection.InspectAsync(fresh,offline125.Id);
Check(fromHistory.Contains("το αποθηκευμένο /api/send") && fromHistory.Contains("σύνολο"), "comparison uses recorded send when provider confirms the same process ID");
fresh.FiscalProviderExchanges.Remove(exactSend); await fresh.SaveChangesAsync();
sim.Lookup = new() { Success=true, ExternalSystemId="wrong-id", Mark=777125 };
var unconfirmedCandidate = await inspection.InspectAsync(fresh,offline125.Id);
Check(unconfirmedCandidate.Contains("δεν επιβεβαίωσε") && !unconfirmedCandidate.Contains("Ο πάροχος επιβεβαίωσε"), "collision inspection rejects a mismatched provider identity");
fresh.Remove(otherReceipt); await fresh.SaveChangesAsync();
sim.Lookup = new() { ErrorCode="00010022" };
var preservedPayload = offline125.ParochosInvoicePayloadJson;
var preservedExternal = offline125.ParochosExternalId;
var reservedBefore = await fresh.ReceiptNumberHolds.CountAsync();
Check(await FiscalOutboxArchive.SetArchivedAsync(fresh, offline125.Id, null, true)==1, "problem receipt can be cleaned from active list");
await fresh.Entry(offline125).ReloadAsync();
Check(offline125.ParochosOutboxArchived && offline125.InvoiceNumber==125 && offline125.ParochosInvoicePayloadJson==preservedPayload && offline125.ParochosExternalId==preservedExternal && await fresh.ReceiptNumberHolds.CountAsync()==reservedBefore, "cleaning preserves identity, payload and number reservations");
Check(!(await service.GetPendingTransmissionsAsync(fresh)).Any(x=>x.OrderId==offline125.Id) && (await FiscalOutboxArchive.ListArchivedAsync(fresh)).Any(x=>x.OrderId==offline125.Id), "cleaned receipt leaves active outbox and can be recovered");
var sendCountBeforeArchive = sim.Sends.Count;
await service.IssueAsync(fresh,offline125,FiscalDocumentKind.Receipt);
Check(sim.Sends.Count==sendCountBeforeArchive && offline125.InvoiceNumber==125, "archived receipt cannot be recreated or resent");
Check(await FiscalOutboxArchive.SetArchivedAsync(fresh, offline125.Id, null, false)==1, "cleaned receipt can be restored");
await fresh.Entry(offline125).ReloadAsync();
Check((await service.GetPendingTransmissionsAsync(fresh)).Any(x=>x.OrderId==offline125.Id), "restored receipt returns to active outbox");

Check(await FiscalOutboxArchive.SetArchivedAsync(fresh, original.OrderId, original.Id, true)==0, "issued order form with MARK cannot be cleaned");
var problemForm = new FiscalOrderDocument { OrderId=offline125.Id, ExternalSystemId="clean-form", SeriesName="FORM", Number=231,
    ParochosTransmissionState="NeedsReview", ParochosInvoicePayloadJson="{\"invoice\":{}}" };
fresh.Add(problemForm); await fresh.SaveChangesAsync();
Check(await FiscalOutboxArchive.SetArchivedAsync(fresh, offline125.Id, problemForm.Id, true)==1, "problem order form can be cleaned");
await fresh.Entry(problemForm).ReloadAsync();
Check(problemForm.ParochosOutboxArchived && problemForm.Number==231 && (await FiscalOutboxArchive.ListArchivedAsync(fresh)).Any(x=>x.FiscalOrderDocumentId==problemForm.Id), "archived form retains its number and appears in archive");
Check(await FiscalOutboxArchive.SetArchivedAsync(fresh, offline125.Id, problemForm.Id, false)==1, "problem order form can be restored");
await fresh.Entry(problemForm).ReloadAsync(); fresh.Remove(problemForm); await fresh.SaveChangesAsync();

Check(await ReceiptNumberAllocator.ConfirmPortalLastAsync(fresh,posSeries.Id,126)==127,"portal 126 + local holds => next 127");
var sale127=await Coffee();
sim.Send=new(){Success=true,Mark=700127};
var p127=await service.IssueAsync(fresh,sale127,FiscalDocumentKind.Receipt);
Check(p127.InvoiceNumber==127 && offline125.InvoiceNumber==125,"new identical coffee uses POS 127, offline POS 125 unchanged");
Check(await ReceiptNumberAllocator.ConfirmPortalLastAsync(fresh,posSeries.Id,125)==128,"admin cannot rewind the series");
sim.Lookup=new(){Success=true,Mark=700125,ExternalSystemId="other-receipt"};
await service.RetryPendingTransmissionAsync(fresh,offline125.Id);
Check(offline125.ParochosMark==null && offline125.InvoiceNumber==125,"conflicting echoed external identity cannot clear POS 125");

// Newly learned 8.6 MARK belongs to one sale, not every document on this table.
var waiting=await Coffee();
var linked=new FiscalOrderDocument {OrderId=waiting.Id,ExternalSystemId="only-this-8.6",SeriesName="TEST8",Number=2};
fresh.Add(linked);await fresh.SaveChangesAsync();
sim.Send=new(){ErrorCategory="NETWORK",OutcomeUnknown=true};sim.Lookup=new(){ErrorCode="00010005"};
await service.IssueAsync(fresh,waiting,FiscalDocumentKind.Receipt);
var expectedId=waiting.ParochosExternalId; var expectedNumber=waiting.InvoiceNumber;
var beforeMissing=sim.Sends.Count;
var blocked=await service.RetryPendingTransmissionAsync(fresh,waiting.Id);
Check(blocked.RequiresRecovery && sim.Sends.Count==beforeMissing,"missing MARK for a captured 8.6 defers resend of the receipt");
linked.Mark=8202;
fresh.Add(new FiscalOrderDocument {OrderId=waiting.Id,ExternalSystemId="late-unrelated",SeriesName="TEST8",Number=3,Mark=8203});
await fresh.SaveChangesAsync();
sim.Send=new(){Success=true,Mark=8001};
await service.RetryPendingTransmissionAsync(fresh,waiting.Id);
var lastWire=sim.Sends.Last();
Check(lastWire.Id==expectedId && lastWire.Json.GetProperty("invoice").GetProperty("invoiceHeader").GetProperty("aa").GetInt64()==expectedNumber &&
    lastWire.Json.GetProperty("invoice").GetProperty("correlatedInvoices").EnumerateArray().Select(v=>v.GetProperty("mark").GetInt64()).SequenceEqual(new[]{8202L}),
    "explicit absence retries identical receipt identity with only the captured 8.6 MARK");

var shared=$"Data Source=seq-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
using(var keeper=new SqliteConnection(shared)){
 keeper.Open();var opts=new DbContextOptionsBuilder<AppDbContext>().UseSqlite(shared).Options;
 using(var setup=new AppDbContext(opts)){setup.Database.EnsureCreated();setup.InvoiceSeries.Add(new(){SeriesName="K",NextNumber=125});setup.SaveChanges();}
 var allocations=await Task.WhenAll(Enumerable.Range(0,4).Select(idx=>Task.Run(async()=>{
    using var parallel=new AppDbContext(opts);
    return await ReceiptNumberAllocator.ReserveAsync(parallel,"K","concurrent-"+idx,idx+1);
 })));
 Check(allocations.Order().SequenceEqual(new[]{125L,126L,127L,128L}),"four concurrent SQLite connections cannot allocate the same number");
 using var after=new AppDbContext(opts);
 Check(await ReceiptNumberAllocator.ReserveAsync(after,"K","concurrent-0",1)==allocations[0],"original reservation survives reopening");
 await using(var tx=await after.Database.BeginTransactionAsync()){
    await ReceiptNumberAllocator.ReserveAsync(after,"K","rollback",91);
    await tx.RollbackAsync();
 }
 using var verify=new AppDbContext(opts);
 Check(!await verify.ReceiptNumberHolds.AnyAsync(h=>h.ExternalId=="rollback") && await ReceiptNumberAllocator.NextAsync(verify,"K")==129,
    "rollback never leaves half a receipt reservation");
}

// Exercise the real raw TCP/ESC-POS route without contacting a shop printer.
var tcp=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);
tcp.Start();
try
{
    var station=await fresh.PrinterStations.SingleAsync();
    station.ConnectionType="Tcp"; station.IpAddress="127.0.0.1"; station.Port=((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
    await fresh.SaveChangesAsync();
    async Task<byte[]> ReceiveTicket()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client=await tcp.AcceptTcpClientAsync(timeout.Token);
        using var bytes=new MemoryStream();
        await client.GetStream().CopyToAsync(bytes,timeout.Token);
        return bytes.ToArray();
    }
    fresh.Add(new OrderItem { OrderId=order.Id,MenuItemId=item.MenuItemId }); await fresh.SaveChangesAsync();
    var kitchenBytes=ReceiveTicket();
    var batch=await dispatch.DispatchPendingAsync(fresh,order.Id,"EX10","-","");
    Check((await kitchenBytes).Length>20 && batch.FailedStations.Count==0,"real TCP kitchen packet reaches loopback printer");
    sim.Send=new(){ErrorCategory="NETWORK",OutcomeUnknown=true};sim.Lookup=new(){ErrorCode="00010005"};
    var formBytes=ReceiveTicket(); var off=await fiscal.ConsumeBatchAsync(fresh,batch.BatchId);
    var paper=System.Text.Encoding.ASCII.GetString(await formBytes);
    Check(off.Issue.OfflineIssued && paper.Contains("frape") && paper.Contains("FAILURE_1"),"network printer receives actual ESC/POS offline 8.6 with product and failure code");
} finally { tcp.Stop(); }

// Exercise real HTTP serialization and persistent history using an isolated in-memory provider.
var historyFactory = new MemoryContextFactory(options);
var historyStore = new FiscalProviderExchangeStore(historyFactory, NullLogger<FiscalProviderExchangeStore>.Instance);
var httpHandler = new FiscalTestHttpHandler();
var httpFactory = new FiscalTestHttpFactory(new HttpClient(httpHandler));
var historyProvider = new ParochosService(httpFactory, NullLogger<ParochosService>.Instance, historyStore);
var providerSettings = new CompanySettings { ParochosSubscriptionKey="secret-subscription", ParochosUsername="secret-user",
    ParochosPassword="secret-password", ParochosPollingAttempts=2, ParochosPollingDelayMs=1 };
var sendResult = await historyProvider.SendAndWaitAsync(providerSettings, new { invoice = new { invoiceHeader = new { series="POS", aa=321 } } }, "history-sale");
Check(sendResult.Success && sendResult.Mark==777, "simulated provider returns a signed result after polling");
fresh.ChangeTracker.Clear();
var exchanges = await fresh.FiscalProviderExchanges.AsNoTracking().OrderBy(e=>e.Id).ToListAsync();
Check(exchanges.Count==2 && exchanges[0].Operation=="/api/send" && exchanges[1].Operation=="/api/get" &&
    exchanges[0].RequestJson==httpHandler.SendRequest && exchanges[0].ResponseBody==httpHandler.SendResponse &&
    exchanges[1].ResponseBody==httpHandler.GetResponse && exchanges.All(e=>e.ExternalSystemId=="history-sale"),
    "send and polling JSON requests and responses are stored exactly");
Check(exchanges.All(e=>!e.RequestJson.Contains("secret-password") && !(e.ResponseBody ?? "").Contains("secret-jwt")),
    "login credentials and authorization tokens are not stored in exchange history");
httpHandler.FailGet=true;
await historyProvider.GetDocumentByExternalSystemIdAsync(providerSettings,"history-failure");
var failedExchange=await fresh.FiscalProviderExchanges.AsNoTracking().OrderByDescending(e=>e.Id).FirstAsync();
Check(failedExchange.ExternalSystemId=="history-failure" && failedExchange.HttpStatus==null && failedExchange.ResponseBody==null && failedExchange.TransportError=="HttpRequestException", "failed HTTP exchange is stored without inventing a provider response");

var offlineHandler = new FiscalTestHttpHandler { FailLogin=true };
var offlineProvider = new ParochosService(new FiscalTestHttpFactory(new HttpClient(offlineHandler)), NullLogger<ParochosService>.Instance, historyStore);
var offlineAttempt = await offlineProvider.SendAndWaitAsync(providerSettings, new { invoice = new { invoiceHeader = new { series="POS", aa=322 } } }, "history-offline");
var offlineExchange=await fresh.FiscalProviderExchanges.AsNoTracking().OrderByDescending(e=>e.Id).FirstAsync();
Check(!offlineAttempt.Success && offlineExchange.ExternalSystemId=="history-offline" && offlineExchange.TransportError=="NotSentAuthentication" && offlineExchange.ResponseBody==null && !offlineExchange.RequestJson.Contains("secret-password"), "offline authentication failure records intended fiscal request as not sent without credentials");

// Simulate upgrading a previous database with a real existing order and no handoff columns.
using var legacySql = new SqliteConnection("Data Source=:memory:");
legacySql.Open();
using var legacy = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(legacySql).Options);
DataSeeder.Seed(legacy);
legacy.Add(new OrderPrintBatch { Number=1, DailyNumber=1, Order=new Order { WaiterUserId=legacy.Users.First().Id,
    RestaurantTable=new RestaurantTable{Name="Legacy"} }});
legacy.SaveChanges();
legacy.ChangeTracker.Clear();
legacy.Database.ExecuteSqlRaw("ALTER TABLE OrderPrintBatches DROP COLUMN DispatchNotificationPending; ALTER TABLE OrderItems DROP COLUMN KitchenPrintUnconfirmed; ALTER TABLE Orders DROP COLUMN ReceiptScopeJson; ALTER TABLE InvoiceSeries DROP COLUMN PortalLastNumber; DROP TABLE ReceiptNumberHolds; DROP TABLE FiscalProviderExchanges;");
DataSeeder.Seed(legacy);
DataSeeder.Seed(legacy);
Check(!legacy.OrderPrintBatches.Any(b=>b.DispatchNotificationPending) && !legacy.OrderItems.Any(i=>i.KitchenPrintUnconfirmed) && !legacy.FiscalProviderExchanges.Any(),"in-place column upgrade is idempotent and does not replay historic orders");

fresh.ChangeTracker.Clear();
var staffSale = new Order { StaffConsumptionInvoiceType="6.1", PaymentMethod=PaymentMethod.Cash, RestaurantTableId=order.RestaurantTableId, WaiterUserId=order.WaiterUserId,
    Items = new() { new OrderItem { Quantity=2, IsPrinted=true, MenuItem=new MenuItem { Name="Staff coffee", Price=3m, StaffConsumptionPrice=0.10m,
        MenuCategory=new MenuCategory { Name="Staff goods", VatPercent=13m, IncomeClassificationCategory="category1_2" } }, DiscountPercent=(1m-0.10m/3m)*100m } } };
fresh.Add(staffSale); await fresh.SaveChangesAsync();
sim.Send=new() { Success=true, Mark=990001 };
var staffIssued=await service.IssueAsync(fresh,staffSale,FiscalDocumentKind.Receipt);
Check(staffIssued.Success && staffIssued.Mark==990001, "staff consumption is issued through normal provider workflow");
var issuedStaffJson=sim.Sends.Last().Json.GetProperty("invoice");
Check(issuedStaffJson.GetProperty("invoiceHeader").GetProperty("invoiceType").GetString()=="6.1" && issuedStaffJson.GetProperty("invoiceSummary").GetProperty("totalValue").GetDecimal()==0.20m && issuedStaffJson.GetProperty("invoiceDetails")[0].GetProperty("classificationCategory").GetString()=="category1_6" && issuedStaffJson.GetProperty("invoiceDetails")[0].GetProperty("classificationType").GetString()=="E3_595", "actual provider request contains 6.1, correct classification and cost-price totals");
var withForm = new Order { StaffConsumptionInvoiceType="6.1", PaymentMethod=PaymentMethod.Cash, RestaurantTableId=order.RestaurantTableId, WaiterUserId=order.WaiterUserId,
    Items=new() { new OrderItem { Quantity=2, IsPrinted=true, MenuItem=new MenuItem { Name="Staff tea", Price=3m, StaffConsumptionPrice=0.10m,
        MenuCategory=new MenuCategory { Name="Staff items", VatPercent=13m, IncomeClassificationCategory="category1_2" } }, DiscountPercent=(1m-0.10m/3m)*100m } } };
fresh.Add(withForm); await fresh.SaveChangesAsync();
fresh.Add(new FiscalOrderDocument { OrderId=withForm.Id, ExternalSystemId="staff-original", SeriesName="PAR", Number=1, Mark=990002,
    Items=new() { new FiscalOrderDocumentItem { OrderItemId=withForm.Items[0].Id, Quantity=2, DiscountPercentAtIssue=0m } } });
await fresh.SaveChangesAsync();
sim.Send=new() { Success=true, Mark=990003, ProcessId="staff-return" };
var staffReturnService=new FiscalCancellationService(provider,config,NullLogger<FiscalCancellationService>.Instance);
var staffReturn=await staffReturnService.ApplyDiscountWithReturnOrderFormsAsync(fresh,withForm,new[]{withForm.Items[0].Id});
var staffReturnJson=sim.Sends.Last().Json.GetProperty("invoice");
Check(staffReturn.Success && staffReturnJson.GetProperty("invoiceHeader").GetProperty("invoiceType").GetString()=="8.6" && staffReturnJson.GetProperty("invoiceSummary").GetProperty("totalValue").GetDecimal()==5.80m && staffReturnJson.GetProperty("correlatedInvoices")[0].GetProperty("mark").GetInt64()==990002, "staff cost adjustment returns 5.80 through correlated 8.6");
withForm.FiscalStatus=FiscalStatus.Issued; withForm.ParochosMark=990099; withForm.ParochosExternalId="staff-issued"; withForm.ParochosTransmissionState="Issued";
await fresh.SaveChangesAsync();
sim.Connect=new ParochosSimpleResult { Success=false, ErrorCode="TEST-REJECT", ErrorMessage="σφάλμα σύνδεσης δοκιμής" };
var sendCountBeforeConnectRecovery=sim.Sends.Count;
var connectRecovery=await service.IssueAsync(fresh,withForm,FiscalDocumentKind.Receipt);
Check(!connectRecovery.Success && connectRecovery.ErrorMessage!.Contains("TEST-REJECT") && connectRecovery.ErrorMessage.Contains("σφάλμα σύνδεσης δοκιμής") && sim.Sends.Count==sendCountBeforeConnectRecovery, "issued receipt recovery reports connect error without sending a second receipt");
sim.Connect=new ParochosSimpleResult { Success=true };
fresh.ChangeTracker.Clear();
var previouslyRejected = new Order { StaffConsumptionInvoiceType="6.1", PaymentMethod=PaymentMethod.Cash, RestaurantTableId=order.RestaurantTableId, WaiterUserId=order.WaiterUserId,
    InvoiceSeriesName="TEST-SELF", InvoiceNumber=1, ParochosExternalId="staff-rejected-313", ParochosTransmissionState="Rejected", ParochosInvoicePayloadJson="{}", FiscalStatus=FiscalStatus.Failed,
    Items=new() { new OrderItem { Quantity=1, IsPrinted=true, MenuItem=new MenuItem { Name="Rejected coffee", Price=3m, StaffConsumptionPrice=0.10m,
        MenuCategory=new MenuCategory { Name="Rejected goods", VatPercent=24m, IncomeClassificationCategory="category1_2" } }, DiscountPercent=(1m-0.10m/3m)*100m } } };
fresh.Add(previouslyRejected); await fresh.SaveChangesAsync();
fresh.Add(new ReceiptNumberHold { SeriesName="TEST-SELF", Number=1, OrderId=previouslyRejected.Id, ExternalId="staff-rejected-313" });
await fresh.SaveChangesAsync();
sim.Send=new() { Success=true, Mark=990004 };
var retryCount=sim.Sends.Count;
var fixedIssue=await service.IssueAsync(fresh,previouslyRejected,FiscalDocumentKind.Receipt);
Check(fixedIssue.Success && fixedIssue.Mark==990004 && sim.Sends.Count==retryCount+1 && sim.Sends.Last().Id!="staff-rejected-313" &&
    sim.Sends.Last().Json.GetProperty("invoice").GetProperty("invoiceDetails")[0].GetProperty("classificationType").GetString()=="E3_595" &&
    await fresh.ReceiptNumberHolds.AnyAsync(h=>h.ExternalId=="staff-rejected-313" && h.Number==1), "explicitly rejected 313 can be retried with corrected code while preserving original reservation");

using var cashierSql = new SqliteConnection("Data Source=:memory:"); cashierSql.Open();
using var cashierDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(cashierSql).Options);
cashierDb.Database.EnsureCreated();
cashierDb.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS OrderNumberSequence (Id INTEGER PRIMARY KEY, CurrentNumber INTEGER NOT NULL); INSERT INTO OrderNumberSequence (Id, CurrentNumber) VALUES (1, 0);");
var cashierUser = new User { FirstName="Cashier" };
var cashierTable = new RestaurantTable { Name="A1" };
var cashierCategory = new MenuCategory { Name="Drinks" };
var cashierItem = new MenuItem { Name="Coffee", Price=3m, MenuCategory=cashierCategory };
var ordinaryOrder = new Order { WaiterUser=cashierUser, RestaurantTable=cashierTable, Status=OrderStatus.Closed, ClosedAt=DateTime.Now, PaymentMethod=PaymentMethod.Cash,
    Items=new(){ new OrderItem { MenuItem=cashierItem, Quantity=1 } }, Payments=new(){new OrderPayment { Amount=3m, PaymentMethod=PaymentMethod.Cash, FiscalStatus=FiscalStatus.Issued }} };
var consumptionOrder = new Order { WaiterUser=cashierUser, RestaurantTable=cashierTable, Status=OrderStatus.Closed, ClosedAt=DateTime.Now, PaymentMethod=PaymentMethod.Cash,
    StaffConsumptionInvoiceType="6.1", InvoiceSeriesName="AYT", InvoiceNumber=2,
    Items=new(){ new OrderItem { MenuItem=cashierItem, Quantity=1, DiscountPercent=(1m-0.10m/3m)*100m } }, Payments=new(){new OrderPayment { Amount=0.10m, PaymentMethod=PaymentMethod.Cash, FiscalStatus=FiscalStatus.Issued }} };
cashierDb.AddRange(ordinaryOrder,consumptionOrder); await cashierDb.SaveChangesAsync();
var cashierCurrent=(await CashierService.GetCurrentSummary(cashierDb)).Single();
Check(cashierCurrent.CashTotal==3m && cashierCurrent.GrandTotal==3m && cashierCurrent.StaffConsumptionTotal==0.10m && cashierCurrent.StaffConsumptionMovements.Single().Document=="AYT 2", "staff consumption is excluded from cash totals and remains an identifiable movement");
var cashierDaily=await CashierActivityService.GetForDate(cashierDb,DateTime.Today);
Check(cashierDaily.PaidTotal==3m && cashierDaily.HitTotal==3m && cashierDaily.Entries.Any(e=>e.Kind=="Ιδιοκατανάλωση" && e.Amount==0.10m), "daily activity separates staff consumption from paid and sales totals");
var cashierClose=await CashierService.PerformDayClose(cashierDb,cashierUser.Id);
cashierDb.ChangeTracker.Clear();
var reloadedClose=await CashierService.GetClosingById(cashierDb,cashierClose.Id);
Check(reloadedClose!.GrandTotal==3m && reloadedClose.Employees.Single().StaffConsumptionMovements.Single().Amount==0.10m, "day-close snapshot retains separate staff consumption after reload");

public class PrintDevice : Microsoft.JSInterop.IJSRuntime {
    public bool Online; public int Count;
    public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, default, args);
    public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args) {
        if(id!="sunmiPrinter.printRaw") throw new Exception("unexpected device request");
        if(Online) Count++;
        return ValueTask.FromResult((T)(object)Online);
    }
}
public class Simulated : DispatchProxy {
    public List<(string Id,JsonElement Json)> Sends { get; } = new();
    public ParochosSimpleResult Connect { get; set; } = new() { Success=true };
    public ParochosSendResult Send { get; set; } = new() {ErrorCategory="NETWORK",OutcomeUnknown=true};
    public ParochosSendResult Lookup { get; set; } = new() {ErrorCategory="NETWORK",OutcomeUnknown=true};
    public ParochosSendResult ByProcess { get; set; } = new();
    protected override object Invoke(MethodInfo? method, object?[]? args) {
        switch(method!.Name){
            case "SendAndWaitAsync": Sends.Add(((string)args![2]!, JsonSerializer.SerializeToElement(args[1])));return Task.FromResult(Send);
            case "GetDocumentByExternalSystemIdAsync":return Task.FromResult(Lookup);
            case "GetDocumentByProcessIdAsync":return Task.FromResult(ByProcess);
            case "ConnectDocumentsAsync":return Task.FromResult(Connect);
            case "AssignDocumentAsync": case "ClosePointOfServiceAsync":return Task.FromResult(new ParochosSimpleResult{Success=true});
            default:throw new Exception("Unexpected provider call: "+method.Name);
        }
    }
}

public sealed class MemoryContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(options);
}
public sealed class FiscalTestHttpFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
public sealed class FiscalTestHttpHandler : HttpMessageHandler
{
    public string? SendRequest;
    public string SendResponse = "{\"status\":0,\"externalSystemId\":\"history-sale\",\"processId\":\"p-123\"}";
    public string GetResponse = "{\"status\":1,\"externalSystemId\":\"history-sale\",\"processId\":\"p-123\",\"signing\":{\"mark\":777}}";
    public bool FailGet;
    public bool FailLogin;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path=request.RequestUri!.AbsolutePath;
        if(path.Contains("loginToSubscription", StringComparison.OrdinalIgnoreCase) && FailLogin) throw new HttpRequestException("offline");
        if(path.Contains("loginToSubscription", StringComparison.OrdinalIgnoreCase))
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content=new StringContent("{\"jwt\":\"secret-jwt\",\"jwtExpiration\":\"2099-01-01T00:00:00Z\",\"jwtRefreshToken\":\"secret-refresh\",\"jwtRefreshTokenExpiration\":\"2099-01-01T00:00:00Z\",\"url1\":\"https://provider.invalid\"}") };
        if(path.Equals("/api/send",StringComparison.OrdinalIgnoreCase))
        {
            SendRequest=await request.Content!.ReadAsStringAsync(token);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content=new StringContent(SendResponse) };
        }
        if(path.Equals("/api/get",StringComparison.OrdinalIgnoreCase))
        {
            if(FailGet) throw new HttpRequestException("disconnected");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content=new StringContent(GetResponse) };
        }
        throw new InvalidOperationException("Unexpected provider request");
    }
}
