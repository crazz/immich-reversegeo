using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Gadm.Services;

public class GadmDivisionCacheService : ICacheMutationSourceOperation
{
    private static readonly HttpClient DownloadClient = new();
    private static readonly string[] RequiredAreaColumns =
    [
        "id",
        "name",
        "english_type",
        "local_type",
        "admin_level",
        "geom_wkb",
        "bbox_xmin",
        "bbox_ymin",
        "bbox_xmax",
        "bbox_ymax"
    ];
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
    private readonly ICacheFilePublisher _filePublisher;
    private readonly ICacheCandidateOwnership _candidateOwnership;
    private readonly Action _afterInFlightTaskAcquired;
    private readonly Action _afterSharedMutationObserved;
    private readonly ConcurrentDictionary<string, InflightMutation> _inflightMutations = new();
    private readonly ConcurrentDictionary<string, byte> _readyCaches = new();

    public CacheMutationSource Source => CacheMutationSource.Gadm;

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
        _filePublisher = new AtomicCacheFilePublisher();
        _candidateOwnership = new CacheCandidateOwnership();
        _afterInFlightTaskAcquired = static () => { };
        _afterSharedMutationObserved = static () => { };
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
        _filePublisher = new AtomicCacheFilePublisher();
        _candidateOwnership = new CacheCandidateOwnership();
        _afterInFlightTaskAcquired = static () => { };
        _afterSharedMutationObserved = static () => { };
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
        GadmDivisionCacheTestHooks hooks)
        : this(logger, dataDir)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        _sourceOperation = hooks.SourceOperation ?? _sourceOperation;
        _downloadOperation = hooks.DownloadOperation ?? _downloadOperation;
        _exportOperation = hooks.ExportOperation ?? _exportOperation;
        _beforePublicationOperation = hooks.BeforePublication ?? _beforePublicationOperation;
        _afterPublicationOperation = hooks.AfterPublication ?? _afterPublicationOperation;
        _statusOperation = hooks.StatusOperation ?? _statusOperation;
        _hasRowsOperation = hooks.HasRowsOperation ?? _hasRowsOperation;
        _validationOperation = hooks.ValidationOperation ?? _validationOperation;
        _deleteFileOperation = hooks.DeleteFileOperation ?? _deleteFileOperation;
        _filePublisher = hooks.FilePublisher ?? _filePublisher;
        _candidateOwnership = hooks.CandidateOwnership ?? _candidateOwnership;
        _afterInFlightTaskAcquired = hooks.AfterInFlightTaskAcquired ?? _afterInFlightTaskAcquired;
        _afterSharedMutationObserved = hooks.AfterSharedMutationObserved ?? _afterSharedMutationObserved;
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

        string path = GetDbPath(iso3);
        var hasData = RunHasRowsOperation(path, "gadm_area")
            && RunValidationOperation(path)
            && EncodedCountryMatches(path, iso3);
        if (hasData)
        {
            _readyCaches[iso3] = 0;
        }
        else
        {
            _readyCaches.TryRemove(iso3, out _);
        }

        return hasData;
    }

    public void DeleteFile(string iso3)
    {
        _readyCaches.TryRemove(iso3, out _);
        _deleteFileOperation(GetDbPath(iso3), iso3);
    }

    public async ValueTask<CacheMutationSourceResult> ExecuteAsync(
        CacheMutationOperation operation,
        string iso3,
        ICacheMutationReporter reporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        ValidateSourceIdentity(iso3);
        cancellationToken.ThrowIfCancellationRequested();
        await ReportAsync(
            reporter,
            CacheMutationProgressStep.CheckingExisting,
            operation,
            iso3,
            "Checking the existing GADM cache.",
            cancellationToken).ConfigureAwait(false);

        while (true)
        {
            if (_inflightMutations.TryGetValue(iso3, out InflightMutation? current))
            {
                _afterSharedMutationObserved();
                CacheMutationSourceResult sharedResult = await WaitForSharedMutationAsync(
                    current.Task.Value,
                    iso3,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (operation == CacheMutationOperation.Refresh
                    && current.Operation == CacheMutationOperation.Ensure)
                {
                    continue;
                }

                if (current.Operation == operation)
                {
                    await ReportAsync(
                        reporter,
                        CacheMutationProgressStep.Completed,
                        operation,
                        iso3,
                        "The GADM cache is ready.",
                        cancellationToken,
                        sharedResult.Version).ConfigureAwait(false);
                    return sharedResult;
                }

                CacheMutationDisposition joinedDisposition =
                    CacheMutationDisposition.AlreadyReady;
                if (!TryReadWorkerResult(
                        operation,
                        iso3,
                        joinedDisposition,
                        out CacheMutationSourceResult? joined))
                {
                    throw new InvalidOperationException(
                        "The completed GADM cache operation could not be verified.");
                }

                await ReportAsync(
                    reporter,
                    CacheMutationProgressStep.Completed,
                    operation,
                    iso3,
                    "The GADM cache is ready.",
                    cancellationToken,
                    joined.Version).ConfigureAwait(false);
                return joined;
            }

            if (operation == CacheMutationOperation.Ensure
                && TryReadWorkerResult(
                    operation,
                    iso3,
                    CacheMutationDisposition.AlreadyReady,
                    out CacheMutationSourceResult? ready))
            {
                await ReportAsync(
                    reporter,
                    CacheMutationProgressStep.Completed,
                    operation,
                    iso3,
                    "The GADM cache is already ready.",
                    cancellationToken,
                    ready.Version).ConfigureAwait(false);
                return ready;
            }

            InflightMutation? candidate = null;
            candidate = new InflightMutation(
                operation,
                new Lazy<Task<CacheMutationSourceResult>>(
                    () => RunOwnedMutationAsync(
                        operation,
                        iso3,
                        reporter,
                        cancellationToken,
                        candidate!),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            InflightMutation winner = _inflightMutations.GetOrAdd(iso3, candidate);
            if (!ReferenceEquals(candidate, winner))
            {
                continue;
            }

            return await winner.Task.Value.ConfigureAwait(false);
        }
    }

    public (Task Task, GadmDivisionEnsureResult Result) GetOrStartDownload(string iso3, CancellationToken ct = default)
    {
        ValidateSourceIdentity(iso3);
        ct.ThrowIfCancellationRequested();
        if (_inflightMutations.TryGetValue(iso3, out InflightMutation? active))
        {
            _afterInFlightTaskAcquired();
            Task activeTask = active.Task.Value;
            ct.ThrowIfCancellationRequested();
            return (activeTask, GadmDivisionEnsureResult.AwaitedExistingDownload);
        }

        if (HasData(iso3))
        {
            if (_inflightMutations.TryGetValue(iso3, out active))
            {
                _afterInFlightTaskAcquired();
                Task activeTask = active.Task.Value;
                ct.ThrowIfCancellationRequested();
                return (activeTask, GadmDivisionEnsureResult.AwaitedExistingDownload);
            }

            ct.ThrowIfCancellationRequested();
            return (Task.CompletedTask, GadmDivisionEnsureResult.AlreadyReady);
        }

        InflightMutation? candidate = null;
        candidate = new InflightMutation(
            CacheMutationOperation.Ensure,
            new Lazy<Task<CacheMutationSourceResult>>(
                () => RunLegacySourceOperationAsync(iso3, ct, candidate!),
                LazyThreadSafetyMode.ExecutionAndPublication));
        InflightMutation winner = _inflightMutations.GetOrAdd(iso3, candidate);

        var result = ReferenceEquals(candidate, winner)
            ? GadmDivisionEnsureResult.StartedDownload
            : GadmDivisionEnsureResult.AwaitedExistingDownload;

        _afterInFlightTaskAcquired();
        Task sharedTask = winner.Task.Value;
        ct.ThrowIfCancellationRequested();
        return (sharedTask, result);
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

    private static async Task<CacheMutationSourceResult> WaitForSharedMutationAsync(
        Task<CacheMutationSourceResult> sharedTask,
        string iso3,
        CancellationToken cancellationToken)
    {
        try
        {
            return await sharedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"GADM cache source operation was cancelled for {iso3}.",
                ex);
        }
    }

    private async Task<CacheMutationSourceResult> RunLegacySourceOperationAsync(
        string iso3,
        CancellationToken ct,
        InflightMutation owner)
    {
        try
        {
            await _sourceOperation(iso3, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!TryReadWorkerResult(
                    CacheMutationOperation.Ensure,
                    iso3,
                    CacheMutationDisposition.Published,
                    out CacheMutationSourceResult? result))
            {
                throw new InvalidOperationException(
                    "The completed GADM cache operation could not be verified.");
            }

            return result;
        }
        finally
        {
            RemoveExact(_inflightMutations, iso3, owner);
        }
    }

    private async Task<CacheMutationSourceResult> RunOwnedMutationAsync(
        CacheMutationOperation operation,
        string iso3,
        ICacheMutationReporter reporter,
        CancellationToken ct,
        InflightMutation owner)
    {
        try
        {
            return await DownloadDataInternalAsync(
                iso3,
                operation,
                reporter,
                ct).ConfigureAwait(false);
        }
        finally
        {
            RemoveExact(_inflightMutations, iso3, owner);
        }
    }

    internal static bool RemoveExact(ConcurrentDictionary<string, Lazy<Task>> downloads, string iso3, Lazy<Task> lazy)
    {
        return ((ICollection<KeyValuePair<string, Lazy<Task>>>)downloads).Remove(
            new KeyValuePair<string, Lazy<Task>>(iso3, lazy));
    }

    private static bool RemoveExact(
        ConcurrentDictionary<string, InflightMutation> operations,
        string iso3,
        InflightMutation operation)
    {
        return ((ICollection<KeyValuePair<string, InflightMutation>>)operations).Remove(
            new KeyValuePair<string, InflightMutation>(iso3, operation));
    }

    private async Task DownloadDataInternalAsync(string iso3, CancellationToken ct)
    {
        _ = await DownloadDataInternalAsync(
            iso3,
            CacheMutationOperation.Ensure,
            CacheMutationReporters.None,
            ct).ConfigureAwait(false);
    }

    private async Task<CacheMutationSourceResult> DownloadDataInternalAsync(
        string iso3,
        CacheMutationOperation operation,
        ICacheMutationReporter reporter,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dbPath = GetDbPath(iso3);
        if (operation == CacheMutationOperation.Ensure
            && TryReadWorkerResult(
                operation,
                iso3,
                CacheMutationDisposition.AlreadyReady,
                out CacheMutationSourceResult? ready))
        {
            ct.ThrowIfCancellationRequested();
            return ready;
        }

        var gadmCode = ValidateSourceIdentity(iso3);

        var dir = Path.GetDirectoryName(dbPath)!;
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The GADM cache storage is unavailable.", ex);
        }

        var tmpDbPath = Path.Combine(
            dir,
            $"{iso3}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        var tmpDownloadPath = Path.Combine(
            dir,
            $"{iso3}.{Environment.ProcessId}.{Guid.NewGuid():N}.gpkg.download");

        TryCleanupAbandonedCandidates(dir, iso3, ".tmp");
        TryCleanupAbandonedCandidates(dir, iso3, ".gpkg.download");
        ICacheCandidateLease? tmpDbLease = null;
        ICacheCandidateLease? tmpDownloadLease = null;
        try
        {
            tmpDbLease = _candidateOwnership.Acquire(tmpDbPath);
            tmpDownloadLease = _candidateOwnership.Acquire(tmpDownloadPath);
            await ReportAsync(
                reporter,
                CacheMutationProgressStep.PreparingSource,
                operation,
                iso3,
                "Preparing the GADM source.",
                ct).ConfigureAwait(false);
            await using ICacheMutationActivity activity = await reporter.BeginActivityAsync(
                $"GADM cache {iso3}",
                ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await ReportAsync(
                reporter,
                CacheMutationProgressStep.Downloading,
                operation,
                iso3,
                "Downloading GADM administrative areas.",
                ct).ConfigureAwait(false);
            await _downloadOperation(GadmDivisionsLogic.BuildCountryGeoPackageUrl(gadmCode), tmpDownloadPath, ct);
            ct.ThrowIfCancellationRequested();
            await ReportAsync(
                reporter,
                CacheMutationProgressStep.Exporting,
                operation,
                iso3,
                "Exporting GADM administrative areas.",
                ct).ConfigureAwait(false);
            var rowCount = await Task.Run(() => _exportOperation(tmpDownloadPath, tmpDbPath, iso3, ct), ct);
            ct.ThrowIfCancellationRequested();
            if (rowCount == 0)
            {
                throw new InvalidOperationException($"No GADM rows were downloaded for {iso3}.");
            }

            if (!RunValidationOperation(tmpDbPath)
                || !EncodedCountryMatches(tmpDbPath, iso3))
            {
                throw new InvalidOperationException(
                    $"GADM division download for {iso3} produced an invalid cache.");
            }

            await ReportAsync(
                reporter,
                CacheMutationProgressStep.ValidatingCandidate,
                operation,
                iso3,
                "Validating the GADM cache candidate.",
                ct).ConfigureAwait(false);
            await _beforePublicationOperation(ct);
            ct.ThrowIfCancellationRequested();
            await ReportAsync(
                reporter,
                CacheMutationProgressStep.Publishing,
                operation,
                iso3,
                "Publishing the GADM cache.",
                ct).ConfigureAwait(false);
            _filePublisher.Publish(tmpDbPath, dbPath);
            await _afterPublicationOperation(ct);
            ct.ThrowIfCancellationRequested();
            _readyCaches[iso3] = 0;
            _logger.LogInformation("GADM division download complete for {ISO3} via {GadmCode}: {Rows} areas", iso3, gadmCode, rowCount);

            if (!TryReadWorkerResult(
                    operation,
                    iso3,
                    CacheMutationDisposition.Published,
                    out CacheMutationSourceResult? published))
            {
                throw new InvalidOperationException("The published GADM cache could not be verified.");
            }

            await ReportAsync(
                reporter,
                CacheMutationProgressStep.Completed,
                operation,
                iso3,
                "The GADM cache is ready.",
                ct,
                published.Version).ConfigureAwait(false);
            return published;
        }
        finally
        {
            try
            {
                tmpDownloadLease?.Dispose();
            }
            finally
            {
                tmpDbLease?.Dispose();
            }
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
            cmd.CommandText = "PRAGMA table_info(gadm_area)";
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    columns.Add(reader.GetString(1));
                }
            }

            foreach (string requiredColumn in RequiredAreaColumns)
            {
                if (!columns.Contains(requiredColumn))
                {
                    return false;
                }
            }

            cmd.CommandText = "SELECT COUNT(*) FROM gadm_area";
            if ((long)cmd.ExecuteScalar()! <= 0)
            {
                return false;
            }

            var downloadedAt = ReadMetaTimestamp(conn, "downloadedAt");
            var version = ReadMetaValue(conn, "version");
            return downloadedAt is not null
                && !string.IsNullOrWhiteSpace(version)
                && new FileInfo(path).Length > 0;
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

    private bool TryReadWorkerResult(
        CacheMutationOperation operation,
        string iso3,
        CacheMutationDisposition disposition,
        [NotNullWhen(true)] out CacheMutationSourceResult? result)
    {
        result = null;
        string path = GetDbPath(iso3);
        try
        {
            if (!RunValidationOperation(path)
                || !EncodedCountryMatches(path, iso3))
            {
                return false;
            }

            GadmDivisionStatus status = _statusOperation(path);
            if (status.RowCount <= 0
                || status.DownloadedAt is null
                || status.FileSizeBytes is null or <= 0
                || string.IsNullOrWhiteSpace(status.Version))
            {
                return false;
            }

            string version = status.Version;
            result = new CacheMutationSourceResult(
                CacheMutationSource.Gadm,
                operation,
                iso3,
                disposition,
                status.RowCount,
                new DateTimeOffset(status.DownloadedAt.Value.ToUniversalTime()),
                status.FileSizeBytes.Value,
                version,
                CreateAttribution(version));
            return true;
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

    private static string ValidateSourceIdentity(string iso3)
    {
        CacheMutationRequest.RequireCanonicalIso3(iso3, nameof(iso3));
        string gadmCode = GadmCountryCodeMapper.ToGadmCode(iso3);
        CacheMutationRequest.RequireCanonicalIso3(gadmCode, nameof(iso3));
        return gadmCode;
    }

    private static CacheMutationGadmAttribution CreateAttribution(string version) =>
        new(
            CacheMutationGadmAttribution.OfficialDatasetName,
            version,
            CacheMutationGadmAttribution.OfficialLicenseUrl,
            CacheMutationGadmAttribution.NonCommercialUseNotice);

    private static ValueTask ReportAsync(
        ICacheMutationReporter reporter,
        CacheMutationProgressStep step,
        CacheMutationOperation operation,
        string iso3,
        string message,
        CancellationToken cancellationToken,
        string? datasetVersion = null) =>
        reporter.ReportProgressAsync(
            new CacheMutationProgressPayload(
                step,
                CacheMutationSource.Gadm,
                operation,
                iso3,
                message,
                CreateAttribution(datasetVersion ?? GadmDivisionsLogic.DatasetVersion)),
            cancellationToken);

    private void TryCleanupAbandonedCandidates(
        string directory,
        string iso3,
        string suffix)
    {
        try
        {
            var candidates = new HashSet<string>(
                Directory.GetFiles(directory, $"{iso3}.*{suffix}"),
                StringComparer.Ordinal);
            foreach (string ownerPath in Directory.GetFiles(
                         directory,
                         $"{iso3}.*{suffix}.owner"))
            {
                candidates.Add(ownerPath[..^".owner".Length]);
            }

            foreach (string candidate in candidates)
            {
                _candidateOwnership.TryCleanupAbandoned(candidate);
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

    private static bool EncodedCountryMatches(string path, string iso3)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM _meta WHERE key = 'iso3'";
            string? encodedIso3 = command.ExecuteScalar()?.ToString();
            return encodedIso3 is null
                || string.Equals(encodedIso3, iso3, StringComparison.Ordinal);
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
        using var response = await DownloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, ct);
    }

    private sealed record InflightMutation(
        CacheMutationOperation Operation,
        Lazy<Task<CacheMutationSourceResult>> Task);
}

internal sealed class GadmDivisionCacheTestHooks
{
    public Func<string, CancellationToken, Task>? SourceOperation { get; init; }
    public Func<string, string, CancellationToken, Task>? DownloadOperation { get; init; }
    public Func<string, string, string, CancellationToken, long>? ExportOperation { get; init; }
    public Func<CancellationToken, Task>? BeforePublication { get; init; }
    public Func<CancellationToken, Task>? AfterPublication { get; init; }
    public Func<string, GadmDivisionStatus>? StatusOperation { get; init; }
    public Func<string, string, bool>? HasRowsOperation { get; init; }
    public Func<string, bool>? ValidationOperation { get; init; }
    public Action<string, string>? DeleteFileOperation { get; init; }
    public ICacheFilePublisher? FilePublisher { get; init; }
    public ICacheCandidateOwnership? CandidateOwnership { get; init; }
    public Action? AfterInFlightTaskAcquired { get; init; }
    public Action? AfterSharedMutationObserved { get; init; }
}

public enum GadmDivisionEnsureResult
{
    AlreadyReady,
    AwaitedExistingDownload,
    StartedDownload
}

public record GadmDivisionStatus(long RowCount, DateTime? DownloadedAt, string? Version, long? FileSizeBytes);
