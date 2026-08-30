using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Gadm.Services;

public class GadmDivisionCacheService
{
    private readonly ILogger<GadmDivisionCacheService> _logger;
    private readonly string _dataDir;
    private readonly Func<string, CancellationToken, Task> _sourceOperation;
    private readonly Func<string, string, CancellationToken, Task> _downloadOperation;
    private readonly Func<string, string, string, CancellationToken, long> _exportOperation;
    private readonly Func<CancellationToken, Task> _beforePublicationOperation;
    private readonly Func<CancellationToken, Task> _afterPublicationOperation;
    private readonly Func<string, GadmDivisionStatus> _statusOperation;
    private readonly Func<string, string, bool> _hasRowsOperation;
    private readonly Func<string, bool> _validationOperation;
    private readonly Action<string, string> _deleteFileOperation;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _inflightDownloads = new();
    private readonly ConcurrentDictionary<string, byte> _readyCaches = new();

    public GadmDivisionCacheService(ILogger<GadmDivisionCacheService> logger, StorageOptions dirs)
    {
        _logger = logger;
        _dataDir = dirs.DataDir;
        _sourceOperation = DownloadDataInternalAsync;
        _downloadOperation = DownloadFileAsync;
        _exportOperation = GadmCacheExporter.ExportGeoPackageToSqlite;
        _beforePublicationOperation = static _ => Task.CompletedTask;
        _afterPublicationOperation = static _ => Task.CompletedTask;
        _statusOperation = ReadStatus;
        _hasRowsOperation = HasRows;
        _validationOperation = IsValidDb;
        _deleteFileOperation = DeleteFileAndTemps;
    }

    public GadmDivisionCacheService(ILogger<GadmDivisionCacheService> logger, string dataDir)
    {
        _logger = logger;
        _dataDir = dataDir;
        _sourceOperation = DownloadDataInternalAsync;
        _downloadOperation = DownloadFileAsync;
        _exportOperation = GadmCacheExporter.ExportGeoPackageToSqlite;
        _beforePublicationOperation = static _ => Task.CompletedTask;
        _afterPublicationOperation = static _ => Task.CompletedTask;
        _statusOperation = ReadStatus;
        _hasRowsOperation = HasRows;
        _validationOperation = IsValidDb;
        _deleteFileOperation = DeleteFileAndTemps;
    }

    internal GadmDivisionCacheService(
        ILogger<GadmDivisionCacheService> logger,
        string dataDir,
        Func<string, CancellationToken, Task> sourceOperation)
        : this(logger, dataDir)
    {
        _sourceOperation = sourceOperation ?? throw new ArgumentNullException(nameof(sourceOperation));
    }

    internal GadmDivisionCacheService(
        ILogger<GadmDivisionCacheService> logger,
        string dataDir,
        Func<string, string, CancellationToken, Task> downloadOperation,
        Func<string, string, string, long> exportOperation)
        : this(logger, dataDir, downloadOperation,
            (geoPackagePath, outputPath, iso3, _) => exportOperation(geoPackagePath, outputPath, iso3))
    {
    }

    internal GadmDivisionCacheService(
        ILogger<GadmDivisionCacheService> logger,
        string dataDir,
        Func<string, string, CancellationToken, Task> downloadOperation,
        Func<string, string, string, CancellationToken, long> exportOperation)
        : this(logger, dataDir)
    {
        _downloadOperation = downloadOperation ?? throw new ArgumentNullException(nameof(downloadOperation));
        _exportOperation = exportOperation ?? throw new ArgumentNullException(nameof(exportOperation));
    }

    internal GadmDivisionCacheService(
        ILogger<GadmDivisionCacheService> logger,
        string dataDir,
        Func<string, string, CancellationToken, Task> downloadOperation,
        Func<string, string, string, long> exportOperation,
        Func<CancellationToken, Task> beforePublicationOperation)
        : this(logger, dataDir, downloadOperation, exportOperation)
    {
        _beforePublicationOperation = beforePublicationOperation ?? throw new ArgumentNullException(nameof(beforePublicationOperation));
    }

    internal GadmDivisionCacheService(
        ILogger<GadmDivisionCacheService> logger,
        string dataDir,
        Func<string, string, string, CancellationToken, long> exportOperation,
        Func<CancellationToken, Task> beforePublicationOperation,
        Func<CancellationToken, Task> afterPublicationOperation)
        : this(logger, dataDir, static (_, _, _) => Task.CompletedTask, exportOperation)
    {
        _beforePublicationOperation = beforePublicationOperation ?? throw new ArgumentNullException(nameof(beforePublicationOperation));
        _afterPublicationOperation = afterPublicationOperation ?? throw new ArgumentNullException(nameof(afterPublicationOperation));
    }

    internal GadmDivisionCacheService(
        ILogger<GadmDivisionCacheService> logger,
        string dataDir,
        Func<string, string, CancellationToken, Task> downloadOperation,
        Func<string, string, string, CancellationToken, long> exportOperation,
        Func<CancellationToken, Task> beforePublicationOperation,
        Func<CancellationToken, Task> afterPublicationOperation)
        : this(logger, dataDir, downloadOperation, exportOperation)
    {
        _beforePublicationOperation = beforePublicationOperation ?? throw new ArgumentNullException(nameof(beforePublicationOperation));
        _afterPublicationOperation = afterPublicationOperation ?? throw new ArgumentNullException(nameof(afterPublicationOperation));
    }

    internal GadmDivisionCacheService(
        ILogger<GadmDivisionCacheService> logger,
        string dataDir,
        Func<string, GadmDivisionStatus> statusOperation,
        Func<string, string, bool> hasRowsOperation,
        Func<string, bool> validationOperation,
        Action<string, string> deleteFileOperation)
        : this(logger, dataDir)
    {
        _statusOperation = statusOperation ?? throw new ArgumentNullException(nameof(statusOperation));
        _hasRowsOperation = hasRowsOperation ?? throw new ArgumentNullException(nameof(hasRowsOperation));
        _validationOperation = validationOperation ?? throw new ArgumentNullException(nameof(validationOperation));
        _deleteFileOperation = deleteFileOperation ?? throw new ArgumentNullException(nameof(deleteFileOperation));
    }

    internal GadmDivisionCacheService(
        ILogger<GadmDivisionCacheService> logger,
        string dataDir,
        Func<string, string, CancellationToken, Task> downloadOperation,
        Func<string, string, string, CancellationToken, long> exportOperation,
        Func<string, GadmDivisionStatus> statusOperation,
        Func<string, string, bool> hasRowsOperation,
        Func<string, bool> validationOperation,
        Action<string, string> deleteFileOperation)
        : this(logger, dataDir, downloadOperation, exportOperation)
    {
        _statusOperation = statusOperation ?? throw new ArgumentNullException(nameof(statusOperation));
        _hasRowsOperation = hasRowsOperation ?? throw new ArgumentNullException(nameof(hasRowsOperation));
        _validationOperation = validationOperation ?? throw new ArgumentNullException(nameof(validationOperation));
        _deleteFileOperation = deleteFileOperation ?? throw new ArgumentNullException(nameof(deleteFileOperation));
    }

    public Dictionary<string, GadmDivisionStatus> GetStatus()
    {
        var result = new Dictionary<string, GadmDivisionStatus>();
        var root = Path.Combine(_dataDir, "gadm-divisions");
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var file in Directory.GetFiles(root, "*.db"))
        {
            var iso3 = Path.GetFileNameWithoutExtension(file);
            try
            {
                var status = _statusOperation(file);
                result[iso3] = status;
                if (status.RowCount > 0)
                {
                    _readyCaches[iso3] = 0;
                }
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read GADM division database for {ISO3}", iso3);
                result[iso3] = new GadmDivisionStatus(0, null, null, null);
            }
        }

        return result;
    }

    public bool HasData(string iso3)
    {
        if (string.IsNullOrWhiteSpace(iso3))
        {
            return false;
        }

        if (_readyCaches.ContainsKey(iso3))
        {
            return true;
        }

        var hasRows = _hasRowsOperation(GetDbPath(iso3), "gadm_area");
        if (hasRows)
        {
            _readyCaches[iso3] = 0;
        }

        return hasRows;
    }

    public void DeleteFile(string iso3)
    {
        _readyCaches.TryRemove(iso3, out _);
        _deleteFileOperation(GetDbPath(iso3), iso3);
    }

    public (Task Task, GadmDivisionEnsureResult Result) GetOrStartDownload(string iso3, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var hasData = HasData(iso3);
        ct.ThrowIfCancellationRequested();
        if (hasData)
        {
            ct.ThrowIfCancellationRequested();
            return (Task.CompletedTask, GadmDivisionEnsureResult.AlreadyReady);
        }

        Lazy<Task>? candidate = null;
        candidate = new Lazy<Task>(
            () => RunSourceOperationAsync(iso3, ct, candidate!),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var winningLazy = _inflightDownloads.GetOrAdd(iso3, candidate);

        var result = ReferenceEquals(candidate, winningLazy)
            ? GadmDivisionEnsureResult.StartedDownload
            : GadmDivisionEnsureResult.AwaitedExistingDownload;

        ct.ThrowIfCancellationRequested();
        return (winningLazy.Value, result);
    }

    public async Task<GadmDivisionEnsureResult> EnsureDataAsync(string iso3, CancellationToken ct = default)
    {
        var (downloadTask, result) = GetOrStartDownload(iso3, ct);

        try
        {
            await downloadTask.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"GADM cache source operation was cancelled for {iso3}.");
        }
    }

    private async Task RunSourceOperationAsync(string iso3, CancellationToken ct, Lazy<Task> lazy)
    {
        try
        {
            await _sourceOperation(iso3, ct);
        }
        finally
        {
            RemoveExact(_inflightDownloads, iso3, lazy);
        }
    }

    internal static bool RemoveExact(ConcurrentDictionary<string, Lazy<Task>> downloads, string iso3, Lazy<Task> lazy)
    {
        return ((ICollection<KeyValuePair<string, Lazy<Task>>>)downloads).Remove(
            new KeyValuePair<string, Lazy<Task>>(iso3, lazy));
    }

    private async Task DownloadDataInternalAsync(string iso3, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dbPath = GetDbPath(iso3);
        var hasData = HasData(iso3);
        ct.ThrowIfCancellationRequested();
        if (hasData)
        {
            ct.ThrowIfCancellationRequested();
            return;
        }

        ct.ThrowIfCancellationRequested();
        var gadmCode = GadmCountryCodeMapper.ToGadmCode(iso3);

        var dir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dir);
        var tmpDbPath = Path.Combine(dir, $"{iso3}.{Guid.NewGuid():N}.tmp");
        var tmpDownloadPath = Path.Combine(dir, $"{iso3}.{Guid.NewGuid():N}.gpkg.download");

        foreach (var stale in Directory.GetFiles(dir, $"{iso3}.*.tmp"))
        {
            TryDelete(stale);
        }

        foreach (var stale in Directory.GetFiles(dir, $"{iso3}.*.gpkg.download"))
        {
            TryDelete(stale);
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            await _downloadOperation(GadmDivisionsLogic.BuildCountryGeoPackageUrl(gadmCode), tmpDownloadPath, ct);
            ct.ThrowIfCancellationRequested();
            var rowCount = await Task.Run(() => _exportOperation(tmpDownloadPath, tmpDbPath, iso3, ct), ct);
            ct.ThrowIfCancellationRequested();
            if (rowCount == 0)
            {
                throw new InvalidOperationException($"No GADM rows were downloaded for {iso3}.");
            }

            if (!_validationOperation(tmpDbPath))
            {
                throw new InvalidOperationException(
                    $"GADM division download for {iso3} produced an invalid SQLite file at {tmpDbPath}");
            }

            SqliteConnection.ClearAllPools();
            await _beforePublicationOperation(ct);
            ct.ThrowIfCancellationRequested();
            File.Move(tmpDbPath, dbPath, overwrite: true);
            await _afterPublicationOperation(ct);
            ct.ThrowIfCancellationRequested();
            _readyCaches[iso3] = 0;
            _logger.LogInformation("GADM division download complete for {ISO3} via {GadmCode}: {Rows} areas", iso3, gadmCode, rowCount);
        }
        catch
        {
            TryDeleteAfterFailure(tmpDbPath);
            throw;
        }
        finally
        {
            TryDeleteAfterFailure(tmpDownloadPath);
        }
    }

    private string GetDbPath(string iso3)
    {
        return Path.Combine(_dataDir, "gadm-divisions", $"{iso3}.db");
    }

    private static GadmDivisionStatus ReadStatus(string file)
    {
        using var conn = new SqliteConnection($"Data Source={file};Pooling=false");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM gadm_area";
        var count = (long)cmd.ExecuteScalar()!;
        var downloadedAt = ReadMetaTimestamp(conn, "downloadedAt");
        var version = ReadMetaValue(conn, "version");
        var fileSizeBytes = new FileInfo(file).Length;
        return new GadmDivisionStatus(count, downloadedAt, version, fileSizeBytes);
    }

    private static bool HasRows(string path, string tableName)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Pooling=false");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {tableName}";
            return (long)cmd.ExecuteScalar()! > 0;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidDb(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Pooling=false");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM _meta WHERE key = 'downloadedAt'";
            return cmd.ExecuteScalar() is not null;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadMetaValue(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM _meta WHERE key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private static DateTime? ReadMetaTimestamp(SqliteConnection conn, string key)
    {
        var text = ReadMetaValue(conn, key);
        if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static void DeleteFileAndTemps(string path, string iso3)
    {
        TryDelete(path);
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir))
        {
            return;
        }

        foreach (var stale in Directory.GetFiles(dir, $"{iso3}.*.tmp"))
        {
            TryDelete(stale);
        }
    }

    private static void TryDeleteAfterFailure(string path)
    {
        try
        {
            TryDelete(path);
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
        }
    }

    private static async Task DownloadFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, ct);
    }
}

public enum GadmDivisionEnsureResult
{
    AlreadyReady,
    AwaitedExistingDownload,
    StartedDownload
}

public record GadmDivisionStatus(long RowCount, DateTime? DownloadedAt, string? Version, long? FileSizeBytes);
