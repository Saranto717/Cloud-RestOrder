using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public static class DataSeeder
{
    public static void Seed(AppDbContext db)
    {
        db.Database.EnsureCreated();
        EnsureCloudSyncOutboxSchema(db);
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS RestaurantAppearances (
            Id INTEGER NOT NULL PRIMARY KEY, DisplayName TEXT NOT NULL,
            BackgroundImageDataUri TEXT NULL, ShowOccupiedTableTimer INTEGER NOT NULL DEFAULT 0
        );");
        EnsureTableTransferSchema(db);
        EnsureExtraOptionCategories(db);
        EnsureOrderNumberSequence(db);
        EnsureParochosSchemaCompatibility(db);
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS FiscalProviderExchanges (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, AttemptedAtUtc TEXT NOT NULL, Operation TEXT NOT NULL,
            Production INTEGER NOT NULL, ExternalSystemId TEXT NULL, ProcessId TEXT NULL,
            RequestJson TEXT NOT NULL, HttpStatus INTEGER NULL, ResponseBody TEXT NULL, TransportError TEXT NULL
        );");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_FiscalProviderExchanges_ExternalSystemId ON FiscalProviderExchanges(ExternalSystemId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_FiscalProviderExchanges_ProcessId ON FiscalProviderExchanges(ProcessId)");
        EnsureBankPosSchema(db);
        EnsurePrinterStationSchema(db);
        EnsureBackofficeSchema(db);
        AddColumnIfMissing(db, "DayCloses", "PendingPreloadCount", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "DayCloses", "PendingPreloadAmount", "TEXT NOT NULL DEFAULT '0'");
        AddColumnIfMissing(db, "DayCloses", "EmployeeSummaryJson", "TEXT NULL");
        AddColumnIfMissing(db, "OrderItems", "DiscountPercent", "TEXT NOT NULL DEFAULT '0'");

        if (!db.Users.Any())
        {
            db.Users.Add(new User
            {
                FirstName = "Διαχειριστής",
                CodeHash = PasswordHasher.Hash("1234"),
                Role = Role.Admin,
                IsActive = true
            });
            db.SaveChanges();
        }
    }

    private static void EnsureCloudSyncOutboxSchema(AppDbContext db)
    {
        // Additive upgrade for installed SQLite databases. Existing POS data is not rewritten.
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS CloudSyncOutboxEvents (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            EventId TEXT NOT NULL,
            EntityType TEXT NOT NULL,
            EntityId TEXT NOT NULL,
            EventType TEXT NOT NULL,
            SchemaVersion INTEGER NOT NULL,
            OccurredAtUtc TEXT NOT NULL,
            PayloadJson TEXT NOT NULL,
            AttemptCount INTEGER NOT NULL DEFAULT 0,
            LastAttemptAtUtc TEXT NULL,
            AcknowledgedAtUtc TEXT NULL,
            LastError TEXT NULL
        );");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_CloudSyncOutboxEvents_EventId ON CloudSyncOutboxEvents(EventId);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_CloudSyncOutboxEvents_AcknowledgedAtUtc_Id ON CloudSyncOutboxEvents(AcknowledgedAtUtc, Id);");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS CloudSyncSnapshotStates (
            Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
            CompletedAtUtc TEXT NULL
        );");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS CloudSyncSettings (
            Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
            Enabled INTEGER NOT NULL DEFAULT 0,
            ApiBaseUrl TEXT NOT NULL DEFAULT 'https://texn-support.ddns.net/',
            ProtectedStoreToken TEXT NOT NULL DEFAULT '',
            ProtectedDeviceSecret TEXT NOT NULL DEFAULT '',
            DeviceRegistered INTEGER NOT NULL DEFAULT 0,
            LastCloudCursor INTEGER NOT NULL DEFAULT 0,
            LastSuccessfulSyncAtUtc TEXT NULL,
            LastSyncError TEXT NULL
        );");
        db.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO CloudSyncSettings (Id) VALUES (1);");
    }

    private static void EnsurePrinterStationSchema(AppDbContext db)
    {
        AddColumnIfMissing(db, "PrinterStations", "ConnectionType", "TEXT NOT NULL DEFAULT 'Tcp'");
        AddColumnIfMissing(db, "PrinterStations", "PrintOrders", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "PrinterStations", "PrintFiscalDocuments", "INTEGER NOT NULL DEFAULT 1");
    }

    private static void EnsureBackofficeSchema(AppDbContext db)
    {
        // EnsureCreated doesn't add new tables to an existing installation; these
        // idempotent statements upgrade its database without replacing customer data.
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS InventoryItems (
            Id INTEGER NOT NULL CONSTRAINT PK_InventoryItems PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL, MeasurementUnitCode INTEGER NOT NULL DEFAULT 1,
            QuantityOnHand TEXT NOT NULL DEFAULT '0', MinimumQuantity TEXT NOT NULL DEFAULT '0',
            UnitCost TEXT NOT NULL DEFAULT '0', IsActive INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL
        );");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS InventoryRecipeLines (
            Id INTEGER NOT NULL CONSTRAINT PK_InventoryRecipeLines PRIMARY KEY AUTOINCREMENT,
            MenuItemId INTEGER NOT NULL, InventoryItemId INTEGER NOT NULL, QuantityPerProduct TEXT NOT NULL,
            CONSTRAINT FK_InventoryRecipeLines_MenuItems_MenuItemId FOREIGN KEY (MenuItemId) REFERENCES MenuItems (Id) ON DELETE CASCADE,
            CONSTRAINT FK_InventoryRecipeLines_InventoryItems_InventoryItemId FOREIGN KEY (InventoryItemId) REFERENCES InventoryItems (Id) ON DELETE RESTRICT
        );");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_InventoryRecipeLines_MenuItemId_InventoryItemId ON InventoryRecipeLines (MenuItemId, InventoryItemId);");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS InventorySuppliers (
            Id INTEGER NOT NULL CONSTRAINT PK_InventorySuppliers PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL, VatNumber TEXT NULL, Phone TEXT NULL, Email TEXT NULL, Notes TEXT NULL, IsActive INTEGER NOT NULL DEFAULT 1
        );");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS InventoryPurchases (
            Id INTEGER NOT NULL CONSTRAINT PK_InventoryPurchases PRIMARY KEY AUTOINCREMENT,
            SupplierId INTEGER NULL, InvoiceNumber TEXT NULL, PurchasedAt TEXT NOT NULL, TotalCost TEXT NOT NULL DEFAULT '0',
            Notes TEXT NULL, CreatedByUserId INTEGER NOT NULL,
            CONSTRAINT FK_InventoryPurchases_InventorySuppliers_SupplierId FOREIGN KEY (SupplierId) REFERENCES InventorySuppliers (Id) ON DELETE SET NULL
        );");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS InventoryPurchaseLines (
            Id INTEGER NOT NULL CONSTRAINT PK_InventoryPurchaseLines PRIMARY KEY AUTOINCREMENT,
            InventoryPurchaseId INTEGER NOT NULL, InventoryItemId INTEGER NOT NULL, Quantity TEXT NOT NULL,
            UnitCost TEXT NOT NULL, TotalCost TEXT NOT NULL,
            CONSTRAINT FK_InventoryPurchaseLines_InventoryPurchases_InventoryPurchaseId FOREIGN KEY (InventoryPurchaseId) REFERENCES InventoryPurchases (Id) ON DELETE CASCADE,
            CONSTRAINT FK_InventoryPurchaseLines_InventoryItems_InventoryItemId FOREIGN KEY (InventoryItemId) REFERENCES InventoryItems (Id) ON DELETE RESTRICT
        );");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS InventoryMovements (
            Id INTEGER NOT NULL CONSTRAINT PK_InventoryMovements PRIMARY KEY AUTOINCREMENT,
            InventoryItemId INTEGER NOT NULL, Kind INTEGER NOT NULL, QuantityDelta TEXT NOT NULL, UnitCost TEXT NOT NULL DEFAULT '0',
            OrderItemId INTEGER NULL, RelatedMovementId INTEGER NULL, InventoryPurchaseId INTEGER NULL,
            CreatedAt TEXT NOT NULL, CreatedByUserId INTEGER NOT NULL, Note TEXT NULL,
            CONSTRAINT FK_InventoryMovements_InventoryItems_InventoryItemId FOREIGN KEY (InventoryItemId) REFERENCES InventoryItems (Id) ON DELETE RESTRICT
        );");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_InventoryMovements_OrderItem_Ingredient_Consumption ON InventoryMovements (OrderItemId, InventoryItemId, Kind) WHERE OrderItemId IS NOT NULL AND Kind = 1;");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_InventoryMovements_RelatedMovement_Return ON InventoryMovements (RelatedMovementId) WHERE RelatedMovementId IS NOT NULL AND Kind = 2;");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_InventoryMovements_CreatedAt_Kind ON InventoryMovements (CreatedAt, Kind);");
    }

    private static void EnsureOrderNumberSequence(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS OrderNumberSequence (
            Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
            CurrentNumber INTEGER NOT NULL DEFAULT 0
        );");
        db.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO OrderNumberSequence (Id, CurrentNumber) VALUES (1, 0);");
    }

    private static void EnsureExtraOptionCategories(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ExtraOptionMenuCategories (
            ExtraOptionId INTEGER NOT NULL,
            MenuCategoryId INTEGER NOT NULL,
            CONSTRAINT PK_ExtraOptionMenuCategories PRIMARY KEY (ExtraOptionId, MenuCategoryId),
            CONSTRAINT FK_ExtraOptionMenuCategories_ExtraOptions_ExtraOptionId
                FOREIGN KEY (ExtraOptionId) REFERENCES ExtraOptions (Id) ON DELETE CASCADE,
            CONSTRAINT FK_ExtraOptionMenuCategories_MenuCategories_MenuCategoryId
                FOREIGN KEY (MenuCategoryId) REFERENCES MenuCategories (Id) ON DELETE CASCADE
        );");
        db.Database.ExecuteSqlRaw(@"INSERT OR IGNORE INTO ExtraOptionMenuCategories (ExtraOptionId, MenuCategoryId)
            SELECT Id, MenuCategoryId FROM ExtraOptions WHERE MenuCategoryId IS NOT NULL;");
    }

    private static void EnsureTableTransferSchema(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS TableTransfers (
            Id TEXT NOT NULL PRIMARY KEY, SourceOrderId INTEGER NOT NULL, TargetOrderId INTEGER NULL,
            SourceTableId INTEGER NOT NULL, TargetTableId INTEGER NOT NULL, RequestedByUserId INTEGER NOT NULL,
            SourceName TEXT NOT NULL, TargetName TEXT NOT NULL, AccountFingerprint TEXT NOT NULL,
            ProviderRequired INTEGER NOT NULL, SourceMarksJson TEXT NOT NULL DEFAULT '[]', SourceItemIdsJson TEXT NOT NULL DEFAULT '[]', TargetMarksJson TEXT NOT NULL DEFAULT '[]',
            SourceBalance TEXT NOT NULL DEFAULT '0', TargetBalance TEXT NOT NULL DEFAULT '0', Stage INTEGER NOT NULL,
            LastError TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL
        );");
        AddColumnIfMissing(db, "TableTransfers", "SourceItemIdsJson", "TEXT NOT NULL DEFAULT '[]'");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_TableTransfers_Stage_SourceTableId_TargetTableId ON TableTransfers(Stage, SourceTableId, TargetTableId)");
    }

    /// <summary>
    /// Το project χρησιμοποιεί EnsureCreated() αντί για migrations. Αυτές οι ALTER TABLE
    /// εντολές επιτρέπουν σε υπάρχον restaurant.db να πάρει τα νέα Parochos fields χωρίς
    /// να χρειάζεται διαγραφή της βάσης.
    /// </summary>
    private static void EnsureParochosSchemaCompatibility(AppDbContext db)
    {
        // Νέα δυνατότητα: απενεργοποίηση κατηγορίας χωρίς διαγραφή της από την υπάρχουσα βάση.
        AddColumnIfMissing(db, "MenuCategories", "IsActive", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "MenuCategories", "OrderPriority", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "MenuCategories", "AutoOpenExtras", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "MenuItems", "MeasurementUnitCode", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "MenuItems", "StaffConsumptionPrice", "TEXT NOT NULL DEFAULT '0.10'");
        AddColumnIfMissing(db, "MenuItems", "DisplayOrder", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "MenuItems", "TileSize", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "MenuItems", "TileScale", "REAL NOT NULL DEFAULT 1.0");
        AddColumnIfMissing(db, "ExtraOptions", "DisplayOrder", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "ExtraOptions", "TileSize", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "ExtraOptions", "TileScale", "REAL NOT NULL DEFAULT 1.0");
        AddColumnIfMissing(db, "Tables", "TileBackgroundColor", "TEXT NOT NULL DEFAULT '#ffffff'");
        AddColumnIfMissing(db, "Tables", "TileTextColor", "TEXT NOT NULL DEFAULT '#1f2a44'");
        AddColumnIfMissing(db, "Tables", "TileBorderColor", "TEXT NOT NULL DEFAULT '#d8dde6'");
        AddColumnIfMissing(db, "Tables", "TileFontSize", "INTEGER NOT NULL DEFAULT 16");
        AddColumnIfMissing(db, "Tables", "TileBold", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "Tables", "IsTakeAway", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "RestaurantAppearances", "TableTileWidth", "INTEGER NOT NULL DEFAULT 140");
        AddColumnIfMissing(db, "RestaurantAppearances", "TableTileHeight", "INTEGER NOT NULL DEFAULT 110");
        AddColumnIfMissing(db, "RestaurantAppearances", "TableRows", "INTEGER NOT NULL DEFAULT 3");
        AddColumnIfMissing(db, "TableAreas", "TileWidth", "INTEGER NULL");
        AddColumnIfMissing(db, "TableAreas", "TileHeight", "INTEGER NULL");
        AddColumnIfMissing(db, "TableAreas", "Rows", "INTEGER NULL");
        // Η ΠΡΩΤΑ ήταν η αυτόματη προεπιλογή της 17.3.12. Καθάρισέ την μία φορά,
        // ώστε οι παλιές κατηγορίες να ξεκινούν χωρίς σειρά, εκτός αν επιλεγεί ρητά.
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS AppDataMigrations (Name TEXT NOT NULL PRIMARY KEY);");
        var priorityDefaultMigration = "order-priority-default-none-v1";
        if (!db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM AppDataMigrations WHERE Name = 'order-priority-default-none-v1'").AsEnumerable().Any(x => x > 0))
        {
            db.Database.ExecuteSqlRaw("UPDATE MenuCategories SET OrderPriority = '' WHERE OrderPriority = 'ΠΡΩΤΑ';");
            db.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO AppDataMigrations (Name) VALUES ({0});", priorityDefaultMigration);
        }

        AddColumnIfMissing(db, "CompanySettings", "UseParochosProvider", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "CompanySettings", "TakeAwayReceiptOnly", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "CompanySettings", "TaxOffice", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "CompanySettings", "MainActivity", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "CompanySettings", "AadeRegistryUsername", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "CompanySettings", "AadeRegistryPassword", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "CompanySettings", "AadeRegistryCalledByVat", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "CompanySettings", "ParochosTransmissionType", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "CompanySettings", "FiscalProviderName", "TEXT NOT NULL DEFAULT 'Parochos'");
        AddColumnIfMissing(db, "CompanySettings", "FiscalProviderLicense", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "CompanySettings", "FiscalSoftwareInfo", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "CompanySettings", "ParochosCardTerminalId", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "CompanySettings", "ParochosNspCode", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "CompanySettings", "ParochosSpecialInvoiceCategory", "INTEGER NOT NULL DEFAULT 12");
        AddColumnIfMissing(db, "CompanySettings", "ParochosPollingAttempts", "INTEGER NOT NULL DEFAULT 40");
        AddColumnIfMissing(db, "CompanySettings", "ParochosPollingDelayMs", "INTEGER NOT NULL DEFAULT 750");
        AddColumnIfMissing(db, "InvoiceSeries", "IsActive", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "InvoiceSeries", "PortalLastNumber", "INTEGER NOT NULL DEFAULT 0");
        EnsureActiveInvoiceSeries(db);

        AddColumnIfMissing(db, "Orders", "ParochosPaymentSignature", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "ForceCloseAction", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "StaffConsumptionInvoiceType", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "ForceCloseResult", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "ForceClosedByUserId", "INTEGER NULL");
        AddColumnIfMissing(db, "Orders", "ParochosPaymentUid", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "CardTransactionId", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "CardTerminalId", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "CardNspCode", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "AadePreloadedTerminalSettingsId", "INTEGER NULL");
        AddColumnIfMissing(db, "Orders", "AadePreloadedIntentId", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "AadePreloadedStatus", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "AadePreloadedAt", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "AadePreloadedStatusMessage", "TEXT NULL");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS ReceiptNumberHolds (ExternalId TEXT PRIMARY KEY NOT NULL, OrderId INTEGER NOT NULL, SeriesName TEXT NOT NULL, Number INTEGER NOT NULL);");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_ReceiptNumberHolds_SeriesName_Number ON ReceiptNumberHolds(SeriesName,Number);");
        AddColumnIfMissing(db, "Orders", "ReceiptScopeJson", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "ParochosInvoicePayloadJson", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "ParochosTransmissionState", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "ParochosOutboxArchived", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "Orders", "ParochosTransmissionAttemptedAtUtc", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "ParochosOfflineReceiptPrintedAtUtc", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "ParochosTransmissionFailure", "INTEGER NULL");
        AddColumnIfMissing(db, "Orders", "CounterpartStreetNumber", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "CounterpartTaxOffice", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "CounterpartMainActivity", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "ProcessId", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "QrCode", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "IsCancellation", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "IsDiscountReturn", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "IsCancelled", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "CancelledAt", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "CancellationMark", "INTEGER NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "ParochosInvoicePayloadJson", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "ParochosTransmissionState", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "ParochosOutboxArchived", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "ParochosTransmissionAttemptedAtUtc", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "ParochosTransmissionFailure", "INTEGER NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "PrintConfirmedAtUtc", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "ConfirmedPrinterIds", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "CancellationSourceId", "INTEGER NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "FiscalErrorMessage", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderDocumentItems", "DiscountPercentAtIssue", "TEXT NOT NULL DEFAULT '0'");
        AddColumnIfMissing(db, "FiscalOrderDocumentItems", "DiscountReturnGrossAmount", "TEXT NOT NULL DEFAULT '0'");
        AddColumnIfMissing(db, "OrderItems", "IsCancelled", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "OrderItems", "CancelledAt", "TEXT NULL");
        AddColumnIfMissing(db, "OrderItems", "CancelReason", "TEXT NULL");
        AddColumnIfMissing(db, "OrderItems", "OrderPrintBatchId", "INTEGER NULL");
        AddColumnIfMissing(db, "OrderItems", "CreatedAt", "TEXT NOT NULL DEFAULT ''");
        db.Database.ExecuteSqlRaw("UPDATE OrderItems SET CreatedAt = COALESCE((SELECT CreatedAt FROM Orders WHERE Orders.Id = OrderItems.OrderId), CURRENT_TIMESTAMP) WHERE CreatedAt = '';");

        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS OrderPrintBatches (
            Id INTEGER NOT NULL CONSTRAINT PK_OrderPrintBatches PRIMARY KEY AUTOINCREMENT,
            OrderId INTEGER NOT NULL,
            Number INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            CONSTRAINT FK_OrderPrintBatches_Orders_OrderId FOREIGN KEY (OrderId) REFERENCES Orders (Id) ON DELETE CASCADE
        );");
        AddColumnIfMissing(db, "OrderItems", "KitchenPrintUnconfirmed", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "OrderPrintBatches", "AcceptedLinesJson", "TEXT NULL");
        AddColumnIfMissing(db, "OrderPrintBatches", "AcceptedTableName", "TEXT NULL");
        AddColumnIfMissing(db, "OrderPrintBatches", "DispatchNotificationPending", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "OrderPrintBatches", "DailyNumber", "INTEGER NOT NULL DEFAULT 0");

        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS IX_OrderPrintBatches_OrderId_Number
            ON OrderPrintBatches (OrderId, Number);");

        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS FiscalOrderCancellations (
            Id INTEGER NOT NULL CONSTRAINT PK_FiscalOrderCancellations PRIMARY KEY AUTOINCREMENT,
            OrderId INTEGER NOT NULL,
            ExternalSystemId TEXT NOT NULL,
            ProcessId TEXT NULL,
            Mark INTEGER NULL,
            CreatedAt TEXT NOT NULL,
            Reason TEXT NULL,
            CONSTRAINT FK_FiscalOrderCancellations_Orders_OrderId FOREIGN KEY (OrderId) REFERENCES Orders (Id) ON DELETE CASCADE
        );");
        AddColumnIfMissing(db, "FiscalOrderCancellations", "DocumentKind", "INTEGER NULL");
        AddColumnIfMissing(db, "FiscalOrderCancellations", "SeriesName", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderCancellations", "Number", "INTEGER NULL");
        AddColumnIfMissing(db, "FiscalOrderCancellations", "QrCode", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderCancellations", "PaymentId", "INTEGER NULL");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS CancellationAuditEntries (
            Id INTEGER NOT NULL CONSTRAINT PK_CancellationAuditEntries PRIMARY KEY AUTOINCREMENT,
            OrderId INTEGER NOT NULL, OrderItemId INTEGER NULL, UserId INTEGER NULL,
            At TEXT NOT NULL, TableName TEXT NOT NULL, Action TEXT NOT NULL,
            Description TEXT NOT NULL, Reason TEXT NOT NULL, Quantity INTEGER NOT NULL, Amount TEXT NOT NULL
        );");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_CancellationAuditEntries_At ON CancellationAuditEntries (At);");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS DatabaseEditAudit (
            Id INTEGER NOT NULL CONSTRAINT PK_DatabaseEditAudit PRIMARY KEY AUTOINCREMENT,
            At TEXT NOT NULL, UserId INTEGER NOT NULL, TableName TEXT NOT NULL,
            PrimaryKeyJson TEXT NOT NULL, BeforeJson TEXT NOT NULL, AfterJson TEXT NOT NULL
        );");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_DatabaseEditAudit_At ON DatabaseEditAudit (At);");

        // Πολλοί εκτυπωτές ανά κατηγορία. Δημιουργείται και σε υπάρχουσες βάσεις
        // (το EnsureCreated δεν προσθέτει νέους πίνακες σε ήδη υπάρχον restaurant.db).
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS MenuCategoryPrinterStations (
            MenuCategoryId INTEGER NOT NULL,
            PrinterStationId INTEGER NOT NULL,
            CONSTRAINT PK_MenuCategoryPrinterStations PRIMARY KEY (MenuCategoryId, PrinterStationId),
            CONSTRAINT FK_MenuCategoryPrinterStations_MenuCategories_MenuCategoryId
                FOREIGN KEY (MenuCategoryId) REFERENCES MenuCategories (Id) ON DELETE CASCADE,
            CONSTRAINT FK_MenuCategoryPrinterStations_PrinterStations_PrinterStationId
                FOREIGN KEY (PrinterStationId) REFERENCES PrinterStations (Id) ON DELETE CASCADE
        );");

        // Μεταφέρει χωρίς απώλεια τις ήδη υπάρχουσες αντιστοιχίσεις από την παλιά στήλη.
        db.Database.ExecuteSqlRaw(@"INSERT OR IGNORE INTO MenuCategoryPrinterStations (MenuCategoryId, PrinterStationId)
            SELECT Id, PrinterStationId FROM MenuCategories WHERE PrinterStationId IS NOT NULL;");
    }


    private static void EnsureBankPosSchema(AppDbContext db)
    {
        // EnsureCreated does not add tables/columns to an existing SQLite database.
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS BankPosTerminalSettings (
            Id INTEGER NOT NULL CONSTRAINT PK_BankPosTerminalSettings PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL DEFAULT 'Κύριο POS', Provider TEXT NOT NULL DEFAULT 'Viva',
            Protocol TEXT NOT NULL DEFAULT 'Viva Cloud', UseProduction INTEGER NOT NULL DEFAULT 0,
            IsEnabled INTEGER NOT NULL DEFAULT 0, TerminalId TEXT NOT NULL DEFAULT '',
            IdUrl TEXT NOT NULL DEFAULT '', RedeemCode TEXT NOT NULL DEFAULT '', ProviderCode TEXT NOT NULL DEFAULT '',
            ParochosNspCode TEXT NOT NULL DEFAULT '', ProviderId INTEGER NOT NULL DEFAULT 19,
            IsAppToApp INTEGER NOT NULL DEFAULT 0, MerchantId TEXT NOT NULL DEFAULT '', ApiKey TEXT NOT NULL DEFAULT '', ApiSecret TEXT NOT NULL DEFAULT '',
            AadePreloaded INTEGER NOT NULL DEFAULT 0, AadePreloadedDuration INTEGER NOT NULL DEFAULT 24,
            Username TEXT NOT NULL DEFAULT '', Password TEXT NOT NULL DEFAULT '', Endpoint TEXT NOT NULL DEFAULT 'https://webecr-test.epayworldwide.com/',
            AuthEndpoint TEXT NOT NULL DEFAULT '', RedeemEndpoint TEXT NOT NULL DEFAULT '',
            IpAddress TEXT NOT NULL DEFAULT '', Port INTEGER NOT NULL DEFAULT 0,
            TimeoutSeconds INTEGER NOT NULL DEFAULT 60, Notes TEXT NOT NULL DEFAULT '', UpdatedAt TEXT NOT NULL
        );");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "IdUrl", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "RedeemCode", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "ProviderCode", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "ParochosNspCode", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "ProviderId", "INTEGER NOT NULL DEFAULT 19");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "IsAppToApp", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "AuthEndpoint", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "RedeemEndpoint", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "AadePreloaded", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "AadePreloadedDuration", "INTEGER NOT NULL DEFAULT 24");
    }

    private static void EnsureActiveInvoiceSeries(AppDbContext db)
    {
        foreach (var kind in Enum.GetValues<FiscalDocumentKind>())
        {
            if (db.InvoiceSeries.Any(x => x.Kind == kind && x.IsActive))
                continue;

            var latest = db.InvoiceSeries
                .Where(x => x.Kind == kind)
                .OrderByDescending(x => x.Id)
                .FirstOrDefault();

            if (latest is not null)
            {
                latest.IsActive = true;
                db.SaveChanges();
            }
        }
    }

    private static void AddColumnIfMissing(AppDbContext db, string table, string column, string definition)
    {
        var exists = db.Database.SqlQueryRaw<long>(
            $"SELECT COUNT(*) AS Value FROM pragma_table_info('{table}') WHERE name = '{column}'")
            .Single() > 0;

        if (!exists)
        {
            db.Database.ExecuteSqlRaw($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};");
        }
    }
}
