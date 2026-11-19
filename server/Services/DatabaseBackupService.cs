using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace RestaurantOrders.Services;

public sealed record DatabaseBackupInfo(string FileName, DateTime CreatedAt, long SizeBytes, string Kind);

/// <summary>Daily SQLite online backups plus admin initiated backup and restore staging.</summary>
public sealed class DatabaseBackupService : BackgroundService
{
    public const long MaxRestoreUploadBytes = 256L * 1024 * 1024;
    private readonly string _databasePath;
    private readonly string _backupDirectory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DatabaseBackupService(
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<DatabaseBackupService> logger)
    {
        var source = new SqliteConnectionStringBuilder(configuration.GetConnectionString("Default") ?? "Data Source=restaurant.db").DataSource;
        _databasePath = Path.GetFullPath(source);
        _backupDirectory = Path.Combine(Path.GetDirectoryName(_databasePath)!, "Backups");
        _lifetime = lifetime;
        _logger = logger;
    }

    public string BackupDirectory => _backupDirectory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_backupDirectory);
        try
        {
            if (!GetBackups().Any(x => x.Kind == "auto" && x.CreatedAt.Date == DateTime.Today))
                await CreateBackupAsync("auto", stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nextRun = DateTime.Today.AddDays(1).AddHours(3);
                if (DateTime.Now < DateTime.Today.AddHours(3)) nextRun = DateTime.Today.AddHours(3);
                await Task.Delay(nextRun - DateTime.Now, stoppingToken);
                await CreateBackupAsync("auto", stoppingToken);
                PruneAutomaticBackups();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Η αυτόματη δημιουργία αντιγράφου ασφαλείας απέτυχε.");
        }
    }

    public async Task<DatabaseBackupInfo> CreateBackupAsync(string kind = "manual", CancellationToken cancellationToken = default)
    {
        if (kind is not ("auto" or "manual" or "pre-restore" or "pre-edit"))
            throw new ArgumentOutOfRangeException(nameof(kind));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_databasePath))
                throw new FileNotFoundException("Δεν βρέθηκε η ενεργή βάση δεδομένων.", _databasePath);
            Directory.CreateDirectory(_backupDirectory);
            var fileName = $"backup-{kind}-{DateTime.Now:yyyyMMdd-HHmmssfff}.db";
            var path = Path.Combine(_backupDirectory, fileName);
            await BackupSqliteAsync(_databasePath, path, cancellationToken);
            var info = new FileInfo(path);
            return new DatabaseBackupInfo(fileName, info.LastWriteTime, info.Length, kind);
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<DatabaseBackupInfo> GetBackups() => Directory.Exists(_backupDirectory)
        ? Directory.EnumerateFiles(_backupDirectory, "backup-*.db", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                var file = new FileInfo(path);
                var kind = file.Name.StartsWith("backup-auto-", StringComparison.OrdinalIgnoreCase) ? "auto"
                    : file.Name.StartsWith("backup-manual-", StringComparison.OrdinalIgnoreCase) ? "manual"
                    : file.Name.StartsWith("backup-pre-edit-", StringComparison.OrdinalIgnoreCase) ? "pre-edit" : "pre-restore";
                return new DatabaseBackupInfo(file.Name, file.LastWriteTime, file.Length, kind);
            })
            .OrderByDescending(x => x.CreatedAt)
            .ToList()
        : Array.Empty<DatabaseBackupInfo>();

    public string? GetBackupPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName ||
            !fileName.StartsWith("backup-", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.Combine(_backupDirectory, fileName);
        return File.Exists(path) ? path : null;
    }

    public async Task StageRestoreAsync(Stream uploadedBackup, string uploadedFileName, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(uploadedFileName), ".db", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Επίλεξε αρχείο αντιγράφου ασφαλείας .db που δημιούργησε η εφαρμογή.");

        await CreateBackupAsync("pre-restore", cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        var uploadPath = Path.Combine(_backupDirectory, $"restore-upload-{Guid.NewGuid():N}.db");
        var stagedPath = _databasePath + ".restore-pending";
        try
        {
            Directory.CreateDirectory(_backupDirectory);
            await using (var output = new FileStream(uploadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await uploadedBackup.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxRestoreUploadBytes)
                        throw new InvalidDataException("Το αντίγραφο ασφαλείας ξεπερνά το όριο των 256 MB.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }

            ValidateRestaurantDatabase(uploadPath);
            if (File.Exists(stagedPath)) File.Delete(stagedPath);
            await BackupSqliteAsync(uploadPath, stagedPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(uploadPath)) File.Delete(uploadPath);
            _gate.Release();
        }
    }

    public void RequestRestartForRestore()
        => RequestRestartForConfigurationChange();

    public void RequestRestartForConfigurationChange()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (WindowsServiceHelpers.IsWindowsService())
                Environment.Exit(1); // The installed service is configured to restart after a non-zero exit.
            else
                _lifetime.StopApplication(); // Development host: restart it manually to apply the staged backup.
        });
    }

    public static void ApplyPendingRestore(string databasePath)
    {
        var pendingPath = databasePath + ".restore-pending";
        if (!File.Exists(pendingPath)) return;
        var backupDirectory = Path.Combine(Path.GetDirectoryName(databasePath)!, "Backups");
        Directory.CreateDirectory(backupDirectory);
        var currentBackup = Path.Combine(backupDirectory, $"backup-pre-restore-{DateTime.Now:yyyyMMdd-HHmmssfff}.db");
        var applyingPath = databasePath + ".restore-applying";
        try
        {
            ValidateRestaurantDatabase(pendingPath);
            if (File.Exists(databasePath)) BackupSqliteAsync(databasePath, currentBackup, CancellationToken.None).GetAwaiter().GetResult();
            BackupSqliteAsync(pendingPath, applyingPath, CancellationToken.None).GetAwaiter().GetResult();
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
            File.Move(applyingPath, databasePath, overwrite: true);
            File.Delete(pendingPath);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(applyingPath)) File.Delete(applyingPath); } catch { }
            try { File.Move(pendingPath, pendingPath + ".failed", overwrite: true); } catch { }
            Console.Error.WriteLine("Database restore was not applied; the existing database was preserved: " + ex.Message);
        }
    }

    private void PruneAutomaticBackups()
    {
        var files = GetBackups().Where(x => x.Kind == "auto").OrderByDescending(x => x.CreatedAt).ToList();
        foreach (var old in files.Skip(30))
        {
            try { File.Delete(Path.Combine(_backupDirectory, old.FileName)); }
            catch (Exception ex) { _logger.LogWarning(ex, "Δεν διαγράφηκε παλιό αυτόματο backup {FileName}.", old.FileName); }
        }
    }

    private static async Task BackupSqliteAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = destinationPath + ".tmp";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporaryPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                await source.OpenAsync(cancellationToken);
                await destination.OpenAsync(cancellationToken);
                source.BackupDatabase(destination);
                using var check = destination.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                var result = await check.ExecuteScalarAsync(cancellationToken);
                if (!string.Equals(result?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Το backup δεν πέρασε τον έλεγχο ακεραιότητας της SQLite.");
            }
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static void ValidateRestaurantDatabase(string path)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        source.Open();
        using var tables = source.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Users','Orders','MenuItems')";
        if (Convert.ToInt32(tables.ExecuteScalar()) != 3)
            throw new InvalidDataException("Το αρχείο δεν είναι βάση RestaurantOrders.");
        using var check = source.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Η βάση δεν πέρασε τον έλεγχο ακεραιότητας.");
    }
}
