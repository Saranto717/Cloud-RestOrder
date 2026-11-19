using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace RestaurantOrders.Services;

public sealed class LocalDatabaseLocationService(IConfiguration configuration)
{
    private readonly string _settingsPath = Path.Combine(
        Path.GetFullPath(configuration["Installation:DataDirectory"] ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RestaurantOrders")),
        "appsettings.Installed.json");

    public string CurrentPath => Path.GetFullPath(
        new SqliteConnectionStringBuilder(configuration.GetConnectionString("Default") ?? "Data Source=restaurant.db").DataSource);

    public async Task<string> MoveDatabaseAsync(string directory, string fileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Συμπλήρωσε τον φάκελο της βάσης.");
        if (string.IsNullOrWhiteSpace(fileName)) throw new InvalidOperationException("Συμπλήρωσε το όνομα του αρχείου βάσης.");
        fileName = fileName.Trim();
        if (Path.GetFileName(fileName) != fileName ||
            !string.Equals(Path.GetExtension(fileName), ".db", StringComparison.OrdinalIgnoreCase) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Χρησιμοποίησε έγκυρο όνομα αρχείου που τελειώνει σε .db.");

        var sourcePath = CurrentPath;
        var targetDirectory = Path.GetFullPath(directory.Trim());
        var targetPath = Path.Combine(targetDirectory, fileName);
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Αυτή είναι ήδη η ενεργή διαδρομή της βάσης.");
        if (File.Exists(targetPath))
            throw new IOException("Υπάρχει ήδη αρχείο με αυτό το όνομα. Διάλεξε διαφορετικό όνομα ώστε να μη γραφτούν πάνω του δεδομένα.");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Δεν βρέθηκε η ενεργή βάση δεδομένων.", sourcePath);

        Directory.CreateDirectory(targetDirectory);
        var writeProbe = Path.Combine(targetDirectory, $".restaurantorders-write-test-{Guid.NewGuid():N}");
        await using (var probe = new FileStream(writeProbe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true))
            await probe.WriteAsync(new byte[] { 1 }, cancellationToken);
        File.Delete(writeProbe);

        var oldBackupDirectory = Path.Combine(Path.GetDirectoryName(sourcePath)!, "Backups");
        Directory.CreateDirectory(oldBackupDirectory);
        var safetyCopy = Path.Combine(oldBackupDirectory, $"backup-before-location-change-{DateTime.Now:yyyyMMdd-HHmmssfff}.db");
        var temporaryTarget = targetPath + $".move-{Guid.NewGuid():N}";
        var destinationCreated = false;
        try
        {
            await BackupSqliteAsync(sourcePath, safetyCopy, cancellationToken);
            await BackupSqliteAsync(sourcePath, temporaryTarget, cancellationToken);
            ValidateRestaurantDatabase(temporaryTarget);
            File.Move(temporaryTarget, targetPath, overwrite: false);
            destinationCreated = true;
            await WriteLocationSettingsAsync(targetDirectory, fileName, cancellationToken);
            return targetPath;
        }
        catch
        {
            if (File.Exists(temporaryTarget)) File.Delete(temporaryTarget);
            if (destinationCreated && File.Exists(targetPath)) File.Delete(targetPath);
            throw;
        }
    }

    private async Task WriteLocationSettingsAsync(string directory, string fileName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        JsonObject root;
        if (File.Exists(_settingsPath))
        {
            var parsed = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath, cancellationToken));
            root = parsed as JsonObject ?? throw new InvalidDataException("Το αρχείο ρυθμίσεων εγκατάστασης δεν είναι έγκυρο JSON.");
        }
        else root = new JsonObject();

        var database = root["Database"] as JsonObject ?? new JsonObject();
        database["Directory"] = directory;
        database["FileName"] = fileName;
        root["Database"] = database;

        var temporarySettings = _settingsPath + ".tmp";
        await File.WriteAllTextAsync(temporarySettings, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        File.Move(temporarySettings, _settingsPath, overwrite: true);
    }

    private static async Task BackupSqliteAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + ".tmp";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        try
        {
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
            }.ToString());
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
            using var check = destination.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            if (!string.Equals((await check.ExecuteScalarAsync(cancellationToken))?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Το αντίγραφο της βάσης απέτυχε στον έλεγχο ακεραιότητας.");
            await destination.CloseAsync();
            await source.CloseAsync();
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static void ValidateRestaurantDatabase(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Users','Orders','MenuItems')";
        if (Convert.ToInt32(command.ExecuteScalar()) != 3)
            throw new InvalidDataException("Το αρχείο δεν είναι βάση RestaurantOrders.");
    }
}
