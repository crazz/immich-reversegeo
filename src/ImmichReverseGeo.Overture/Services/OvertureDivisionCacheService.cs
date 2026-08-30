using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using ImmichReverseGeo.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Overture.Services;

public class OvertureDivisionCacheService
{
    private readonly ILogger<OvertureDivisionCacheService> _logger;
    private readonly string _dataDir;
    private readonly Func<string, string?> _iso3ToAlpha2;
    private readonly Func<string, string, CancellationToken, long> _exportOperation;
    private readonly Func<string, CancellationToken, Task> _sourceOperation;
    private readonly Func<CancellationToken, Task> _beforePublication;
    private readonly Func<string, OvertureDivisionStatus> _statusReader;
    private readonly Func<string, string, bool> _hasRowsOperation;
    private readonly Func<string, bool> _validationOperation;
    private readonly Action<string, string> _deletionOperation;
    private readonly Func<string?> _releaseDiscovery;
    private readonly Action _afterInFlightTaskAcquired;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _inflightDownloads = new();
    private readonly ConcurrentDictionary<string, byte> _readyCaches = new();

    public OvertureDivisionCacheService(
        ILogger<OvertureDivisionCacheService> logger,
        StorageOptions dirs,
        Func<string, string?> iso3ToAlpha2)
    {
        _logger = logger;
        _dataDir = dirs.DataDir;
        _iso3ToAlpha2 = iso3ToAlpha2;
        _exportOperation = ExportOvertureDivisions;
        _sourceOperation = DownloadDataInternalAsync;
        _beforePublication = _ => Task.CompletedTask;
        _statusReader = ReadStatus;
        _hasRowsOperation = HasRows;
        _validationOperation = IsValidDb;
        _deletionOperation = DeleteFileAndTemps;
        _releaseDiscovery = DiscoverLatestOvertureReleaseForCache;
        _afterInFlightTaskAcquired = static () => { };
    }

    public OvertureDivisionCacheService(ILogger<OvertureDivisionCacheService> logger, string dataDir, Func<string, string?> iso3ToAlpha2)
    {
        _logger = logger;
        _dataDir = dataDir;
        _iso3ToAlpha2 = iso3ToAlpha2;
        _exportOperation = ExportOvertureDivisions;
        _sourceOperation = DownloadDataInternalAsync;
        _beforePublication = _ => Task.CompletedTask;
        _statusReader = ReadStatus;
        _hasRowsOperation = HasRows;
        _validationOperation = IsValidDb;
        _deletionOperation = DeleteFileAndTemps;
        _releaseDiscovery = DiscoverLatestOvertureReleaseForCache;
        _afterInFlightTaskAcquired = static () => { };
    }

    internal OvertureDivisionCacheService(
        ILogger<OvertureDivisionCacheService> logger,
        string dataDir,
        Func<string, string?> iso3ToAlpha2,
        Func<string, string, long> exportOperation)
        : this(logger, dataDir, iso3ToAlpha2)
    {
        ArgumentNullException.ThrowIfNull(exportOperation);
        _exportOperation = (path, alpha2, _) => exportOperation(path, alpha2);
    }

    internal OvertureDivisionCacheService(
        ILogger<OvertureDivisionCacheService> logger,
        string dataDir,
        Func<string, string?> iso3ToAlpha2,
        Func<string, string, long> exportOperation,
        Func<string, CancellationToken, Task> sourceOperation)
        : this(logger, dataDir, iso3ToAlpha2, exportOperation)
    {
        _sourceOperation = sourceOperation ?? throw new ArgumentNullException(nameof(sourceOperation));
    }

    internal OvertureDivisionCacheService(
        ILogger<OvertureDivisionCacheService> logger,
        string dataDir,
        Func<string, string?> iso3ToAlpha2,
        Func<string, string, long> exportOperation,
        Func<CancellationToken, Task> beforePublication)
        : this(logger, dataDir, iso3ToAlpha2, exportOperation)
    {
        _beforePublication = beforePublication ?? throw new ArgumentNullException(nameof(beforePublication));
    }

    internal OvertureDivisionCacheService(
        ILogger<OvertureDivisionCacheService> logger,
        string dataDir,
        Func<string, string?> iso3ToAlpha2,
        OvertureDivisionCacheTestHooks hooks)
        : this(logger, dataDir, iso3ToAlpha2)
    {
        if (hooks.ExportOperation is not null)
        {
            _exportOperation = hooks.ExportOperation;
        }

        _sourceOperation = hooks.SourceOperation ?? _sourceOperation;
        _beforePublication = hooks.BeforePublication ?? _beforePublication;
        _statusReader = hooks.StatusReader ?? _statusReader;
        _hasRowsOperation = hooks.HasRowsOperation ?? _hasRowsOperation;
        _validationOperation = hooks.ValidationOperation ?? _validationOperation;
        _deletionOperation = hooks.DeletionOperation ?? _deletionOperation;
        _releaseDiscovery = hooks.ReleaseDiscovery ?? _releaseDiscovery;
        _afterInFlightTaskAcquired = hooks.AfterInFlightTaskAcquired ?? _afterInFlightTaskAcquired;
    }

    public Dictionary<string, OvertureDivisionStatus> GetStatus()
    {
        var result = new Dictionary<string, OvertureDivisionStatus>();
        var root = Path.Combine(_dataDir, "overture-divisions");
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var file in Directory.GetFiles(root, "*.db"))
        {
            var iso3 = Path.GetFileNameWithoutExtension(file);
            try
            {
                var status = _statusReader(file);
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
                _logger.LogWarning(ex, "Failed to read Overture division database for {ISO3}", iso3);
                result[iso3] = new OvertureDivisionStatus(0, null, null, null);
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

        var hasRows = RunHasRowsOperation(GetDbPath(iso3), "division_area");
        if (hasRows)
        {
            _readyCaches[iso3] = 0;
        }

        return hasRows;
    }

    public void DeleteFile(string iso3)
    {
        _readyCaches.TryRemove(iso3, out _);
        RunDeletionOperation(GetDbPath(iso3), iso3);
    }

    public (Task Task, OvertureDivisionEnsureResult Result) GetOrStartDownload(string iso3, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (HasData(iso3))
        {
            ct.ThrowIfCancellationRequested();
            return (Task.CompletedTask, OvertureDivisionEnsureResult.AlreadyReady);
        }

        Lazy<Task>? candidate = null;
        candidate = new Lazy<Task>(
            () => RunSourceOperationAsync(iso3, ct, candidate!),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var winningLazy = _inflightDownloads.GetOrAdd(iso3, candidate);

        var result = ReferenceEquals(candidate, winningLazy)
            ? OvertureDivisionEnsureResult.StartedDownload
            : OvertureDivisionEnsureResult.AwaitedExistingDownload;

        _afterInFlightTaskAcquired();
        var sharedTask = winningLazy.Value;
        ct.ThrowIfCancellationRequested();
        return (sharedTask, result);
    }

    public Task<OvertureDivisionEnsureResult> EnsureDataAsync(string iso3, CancellationToken ct = default)
    {
        var sharedDownload = GetOrStartDownload(iso3, ct);
        return AwaitSharedDownloadAsync(sharedDownload.Task, sharedDownload.Result, iso3, ct);
    }

    private static async Task<OvertureDivisionEnsureResult> AwaitSharedDownloadAsync(
        Task sharedDownload,
        OvertureDivisionEnsureResult result,
        string iso3,
        CancellationToken ct)
    {
        try
        {
            await sharedDownload.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Overture division source became unavailable for {iso3}.", ex);
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
        if (HasData(iso3))
        {
            ct.ThrowIfCancellationRequested();
            return;
        }

        var alpha2 = _iso3ToAlpha2(iso3);
        if (alpha2 is null)
        {
            throw new InvalidOperationException($"Could not determine alpha-2 code for {iso3}.");
        }

        var dir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dir);
        var tmpPath = Path.Combine(dir, $"{iso3}.{Guid.NewGuid():N}.tmp");

        foreach (var stale in Directory.GetFiles(dir, $"{iso3}.*.tmp"))
        {
            TryDelete(stale);
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var rowCount = await Task.Run(() => _exportOperation(tmpPath, alpha2, ct), ct);
            ct.ThrowIfCancellationRequested();
            if (rowCount == 0)
            {
                throw new InvalidOperationException($"No Overture division rows were downloaded for {iso3}.");
            }

            if (!RunValidationOperation(tmpPath))
            {
                throw new InvalidOperationException(
                    $"Overture division download for {iso3} produced an invalid SQLite file at {tmpPath}");
            }

            ct.ThrowIfCancellationRequested();
            await _beforePublication(ct);
            ct.ThrowIfCancellationRequested();
            File.Move(tmpPath, dbPath, overwrite: true);
            ct.ThrowIfCancellationRequested();
            _readyCaches[iso3] = 0;
            _logger.LogInformation("Overture division download complete for {ISO3}: {Rows} areas", iso3, rowCount);
        }
        catch
        {
            TryDeleteAfterFailure(tmpPath);
            throw;
        }
    }

    private long ExportOvertureDivisions(string tmpPath, string alpha2, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var release = GetLatestOvertureReleaseForCache();
        ct.ThrowIfCancellationRequested();
        var releaseUrl = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            OvertureDivisionsLogic.DivisionAreaReleaseUrlTemplate,
            release);

        using var duck = new DuckDBConnection("Data Source=:memory:");
        duck.Open();
        ct.ThrowIfCancellationRequested();
        OvertureDataAccess.LoadAzureAndSpatial(duck);
        ct.ThrowIfCancellationRequested();

        using var selectCmd = duck.CreateCommand();
        selectCmd.CommandText = $@"
            SELECT
                id,
                COALESCE(names.common['en'], names.primary, id) AS name,
                subtype,
                class,
                admin_level,
                country,
                COALESCE(is_land, true) AS is_land,
                COALESCE(is_territorial, false) AS is_territorial,
                ST_AsWKB(geometry) AS geom_wkb,
                bbox.xmin AS bbox_xmin,
                bbox.ymin AS bbox_ymin,
                bbox.xmax AS bbox_xmax,
                bbox.ymax AS bbox_ymax
            FROM read_parquet('{releaseUrl}', filename = true, hive_partitioning = 1)
            WHERE lower(country) = '{alpha2.ToLowerInvariant()}'
            ";
        ct.ThrowIfCancellationRequested();
        using var reader = selectCmd.ExecuteReader();
        ct.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();

        using var sqlite = OpenTemporaryOutputConnection(tmpPath);

        using (var ddl = sqlite.CreateCommand())
        {
            ddl.CommandText = @"
                CREATE TABLE division_area (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    subtype TEXT NULL,
                    class_name TEXT NULL,
                    admin_level INTEGER NULL,
                    country TEXT NULL,
                    is_land INTEGER NOT NULL,
                    is_territorial INTEGER NOT NULL,
                    geom_wkb BLOB NULL,
                    bbox_xmin REAL NULL,
                    bbox_ymin REAL NULL,
                    bbox_xmax REAL NULL,
                    bbox_ymax REAL NULL
                );
                CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE INDEX idx_division_area_bbox_x ON division_area (bbox_xmin, bbox_xmax);
                CREATE INDEX idx_division_area_bbox_y ON division_area (bbox_ymin, bbox_ymax);
                CREATE INDEX idx_division_area_subtype ON division_area (subtype);
                CREATE INDEX idx_division_area_admin_level ON division_area (admin_level);
                CREATE INDEX idx_division_area_country ON division_area (country);
                ";
            ddl.ExecuteNonQuery();
        }

        using (var meta = sqlite.CreateCommand())
        {
            meta.CommandText = @"
                INSERT INTO _meta VALUES ('downloadedAt', $at);
                INSERT INTO _meta VALUES ('release', $release);
                ";
            meta.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            meta.Parameters.AddWithValue("$release", release);
            meta.ExecuteNonQuery();
        }

        using var tx = sqlite.BeginTransaction();
        using var insert = sqlite.CreateCommand();
        insert.CommandText = @"
            INSERT OR IGNORE INTO division_area (
                id, name, subtype, class_name, admin_level, country, is_land, is_territorial,
                geom_wkb, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax
            ) VALUES (
                $id, $name, $subtype, $class, $adminLevel, $country, $isLand, $isTerritorial,
                $geom, $xmin, $ymin, $xmax, $ymax
            )
            ";
        var pId = insert.Parameters.Add("$id", SqliteType.Text);
        var pName = insert.Parameters.Add("$name", SqliteType.Text);
        var pSubType = insert.Parameters.Add("$subtype", SqliteType.Text);
        var pClass = insert.Parameters.Add("$class", SqliteType.Text);
        var pAdminLevel = insert.Parameters.Add("$adminLevel", SqliteType.Integer);
        var pCountry = insert.Parameters.Add("$country", SqliteType.Text);
        var pIsLand = insert.Parameters.Add("$isLand", SqliteType.Integer);
        var pIsTerritorial = insert.Parameters.Add("$isTerritorial", SqliteType.Integer);
        var pGeom = insert.Parameters.Add("$geom", SqliteType.Blob);
        var pXMin = insert.Parameters.Add("$xmin", SqliteType.Real);
        var pYMin = insert.Parameters.Add("$ymin", SqliteType.Real);
        var pXMax = insert.Parameters.Add("$xmax", SqliteType.Real);
        var pYMax = insert.Parameters.Add("$ymax", SqliteType.Real);

        long rows = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var hasRow = reader.Read();
            ct.ThrowIfCancellationRequested();
            if (!hasRow)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();
            pId.Value = reader.GetString(0);
            pName.Value = reader.GetString(1);
            pSubType.Value = reader.IsDBNull(2) ? DBNull.Value : reader.GetString(2);
            pClass.Value = reader.IsDBNull(3) ? DBNull.Value : reader.GetString(3);
            pAdminLevel.Value = reader.IsDBNull(4) ? DBNull.Value : reader.GetInt32(4);
            pCountry.Value = reader.IsDBNull(5) ? DBNull.Value : reader.GetString(5);
            pIsLand.Value = reader.GetBoolean(6) ? 1 : 0;
            pIsTerritorial.Value = reader.GetBoolean(7) ? 1 : 0;
            pGeom.Value = reader.IsDBNull(8) ? DBNull.Value : ReadBlobValue(reader, 8);
            pXMin.Value = reader.IsDBNull(9) ? DBNull.Value : reader.GetDouble(9);
            pYMin.Value = reader.IsDBNull(10) ? DBNull.Value : reader.GetDouble(10);
            pXMax.Value = reader.IsDBNull(11) ? DBNull.Value : reader.GetDouble(11);
            pYMax.Value = reader.IsDBNull(12) ? DBNull.Value : reader.GetDouble(12);
            insert.ExecuteNonQuery();
            rows++;
        }

        ct.ThrowIfCancellationRequested();
        tx.Commit();
        ct.ThrowIfCancellationRequested();
        sqlite.Close();
        SqliteConnection.ClearPool(sqlite);
        ct.ThrowIfCancellationRequested();
        return rows;
    }

    internal static SqliteConnection OpenTemporaryOutputConnection(string tmpPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = tmpPath,
            Pooling = false
        }.ConnectionString;
        var sqlite = new SqliteConnection(connectionString);
        try
        {
            sqlite.Open();
            return sqlite;
        }
        catch
        {
            sqlite.Dispose();
            throw;
        }
    }

    private string GetLatestOvertureReleaseForCache() => ResolveLatestRelease(_releaseDiscovery);

    internal static string ResolveLatestRelease(Func<string?> releaseDiscovery)
    {
        try
        {
            return releaseDiscovery() ?? OverturePlacesLogic.DocumentedFallbackRelease;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            return OverturePlacesLogic.DocumentedFallbackRelease;
        }
    }

    private static string? DiscoverLatestOvertureReleaseForCache()
    {
        using var conn = new DuckDBConnection("Data Source=:memory:");
        conn.Open();
        OvertureDataAccess.LoadHttpfs(conn);
        using var query = conn.CreateCommand();
        query.CommandText = $"SELECT latest FROM '{OverturePlacesLogic.LatestCatalogUrl}'";
        return query.ExecuteScalar()?.ToString();
    }

    private static OvertureDivisionStatus ReadStatus(string file)
    {
        using var conn = new SqliteConnection($"Data Source={file};Pooling=false");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM division_area";
        var count = (long)cmd.ExecuteScalar()!;
        var downloadedAt = ReadMetaTimestamp(conn, "downloadedAt");
        var release = ReadMetaValue(conn, "release");
        return new OvertureDivisionStatus(count, downloadedAt, release, new FileInfo(file).Length);
    }

    private bool RunHasRowsOperation(string path, string tableName)
    {
        try
        {
            return _hasRowsOperation(path, tableName);
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

    private bool RunValidationOperation(string path)
    {
        try
        {
            return _validationOperation(path);
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

    private void RunDeletionOperation(string path, string iso3)
    {
        try
        {
            _deletionOperation(path, iso3);
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
        }
    }

    private string GetDbPath(string iso3)
    {
        return Path.Combine(_dataDir, "overture-divisions", $"{iso3}.db");
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

    private static byte[] ReadBlobValue(System.Data.Common.DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        if (value is byte[] bytes)
        {
            return bytes;
        }

        if (value is Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        throw new InvalidCastException($"Unsupported blob value type '{value.GetType().FullName}' at ordinal {ordinal}.");
    }
}

internal sealed class OvertureDivisionCacheTestHooks
{
    public Func<string, string, CancellationToken, long>? ExportOperation { get; init; }
    public Func<string, CancellationToken, Task>? SourceOperation { get; init; }
    public Func<CancellationToken, Task>? BeforePublication { get; init; }
    public Func<string, OvertureDivisionStatus>? StatusReader { get; init; }
    public Func<string, string, bool>? HasRowsOperation { get; init; }
    public Func<string, bool>? ValidationOperation { get; init; }
    public Action<string, string>? DeletionOperation { get; init; }
    public Func<string?>? ReleaseDiscovery { get; init; }
    public Action? AfterInFlightTaskAcquired { get; init; }
}

public record OvertureDivisionStatus(long RowCount, DateTime? DownloadedAt, string? Release, long? FileSizeBytes);

public enum OvertureDivisionEnsureResult
{
    AlreadyReady,
    AwaitedExistingDownload,
    StartedDownload
}
