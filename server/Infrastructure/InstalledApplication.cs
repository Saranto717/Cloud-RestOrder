using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RestaurantOrders.Services;

namespace RestaurantOrders.Infrastructure;

/// <summary>Installed-only paths; ordinary development keeps its existing configuration.</summary>
public static class InstalledApplication
{
    public static string? Configure(WebApplicationBuilder builder)
    {
        var installationEnabled = builder.Configuration.GetValue<bool>("Installation:Enabled");
        if (!installationEnabled)
        {
            var connection = new SqliteConnectionStringBuilder(builder.Configuration.GetConnectionString("Default") ?? "Data Source=restaurant.db");
            if (!string.IsNullOrWhiteSpace(connection.DataSource) && connection.DataSource != ":memory:")
            {
                var developerDatabasePath = Path.GetFullPath(connection.DataSource);
                DatabaseBackupService.ApplyPendingRestore(developerDatabasePath);
            }
            return null;
        }

        var dataDirectory = Path.GetFullPath(builder.Configuration["Installation:DataDirectory"] ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RestaurantOrders"));
        Directory.CreateDirectory(dataDirectory);
        builder.Configuration.AddJsonFile(Path.Combine(dataDirectory, "appsettings.Installed.json"), optional: true);
        var configuredDatabaseDirectory = builder.Configuration["Database:Directory"];
        var databaseDirectory = string.IsNullOrWhiteSpace(configuredDatabaseDirectory)
            ? dataDirectory
            : Path.IsPathRooted(configuredDatabaseDirectory)
                ? Path.GetFullPath(configuredDatabaseDirectory)
                : Path.GetFullPath(Path.Combine(dataDirectory, configuredDatabaseDirectory));
        var databaseFileName = builder.Configuration["Database:FileName"] ?? "restaurant.db";
        if (Path.GetFileName(databaseFileName) != databaseFileName ||
            !string.Equals(Path.GetExtension(databaseFileName), ".db", StringComparison.OrdinalIgnoreCase) ||
            databaseFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Η ρύθμιση του ονόματος SQLite βάσης δεν είναι έγκυρη.");
        Directory.CreateDirectory(databaseDirectory);
        var databasePath = Path.Combine(databaseDirectory, databaseFileName);
        builder.Configuration["ConnectionStrings:Default"] = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();
        DatabaseBackupService.ApplyPendingRestore(databasePath);
        // No developer HTTPS certificate or separate runtime installation is needed.
        if (string.IsNullOrWhiteSpace(builder.Configuration["Urls"]))
            builder.WebHost.UseUrls("http://0.0.0.0:5080");

        var keys = Directory.CreateDirectory(Path.Combine(dataDirectory, "DataProtection-Keys"));
        var protection = builder.Services.AddDataProtection()
            .SetApplicationName("RestaurantOrders").PersistKeysToFileSystem(keys);
        if (OperatingSystem.IsWindows())
        {
            protection.ProtectKeysWithDpapi(protectToLocalMachine: true);
            builder.Logging.AddEventLog(options =>
            {
                if (OperatingSystem.IsWindows()) options.SourceName = "RestaurantOrders";
            });
        }
        return dataDirectory;
    }

    /// <summary>The installer uses SQLite's online backup API, including committed WAL data.</summary>
    public static bool HandleImport(string[] args)
    {
        var config = new ConfigurationBuilder().AddCommandLine(args).Build();
        var source = config["import-database"];
        if (source is null) return false;
        try
        {
            var destination = config["data-directory"] ?? throw new ArgumentException("Missing data directory.");
            ImportDatabase(source, destination, config["database-directory"], config["database-file-name"]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Database import failed: " + ex.Message);
            Environment.ExitCode = 1;
        }
        return true;
    }

    public static void ImportDatabase(string sourcePath, string dataDirectory, string? databaseDirectory = null, string? databaseFileName = null)
    {
        databaseDirectory = string.IsNullOrWhiteSpace(databaseDirectory) ? dataDirectory : databaseDirectory;
        databaseFileName = string.IsNullOrWhiteSpace(databaseFileName) ? "restaurant.db" : databaseFileName;
        if (Path.GetFileName(databaseFileName) != databaseFileName ||
            !string.Equals(Path.GetExtension(databaseFileName), ".db", StringComparison.OrdinalIgnoreCase) ||
            databaseFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Το όνομα SQLite βάσης δεν είναι έγκυρο.", nameof(databaseFileName));
        var targetDirectory = Path.GetFullPath(databaseDirectory);
        var targetPath = Path.Combine(targetDirectory, databaseFileName);
        if (File.Exists(targetPath)) throw new IOException("An installed database already exists; it will not be overwritten.");
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(sourcePath), Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        source.Open();
        using (var command = source.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Users','Orders','MenuItems')";
            if (Convert.ToInt32(command.ExecuteScalar()) != 3)
                throw new InvalidDataException("The selected file is not a RestaurantOrders database.");
        }
        Directory.CreateDirectory(targetDirectory);
        var temporaryPath = targetPath + ".import-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath, Pooling = false
            }.ToString()))
            {
                target.Open();
                source.BackupDatabase(target);
                using var check = target.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The imported database failed its integrity check.");
            }
            File.Move(temporaryPath, targetPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
