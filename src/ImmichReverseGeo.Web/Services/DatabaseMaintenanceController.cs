using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ImmichReverseGeo.Web.Services;

internal enum DatabaseMaintenanceOperation
{
    ResetAll,
    ResetSelected,
    ResetMatching,
    ClearSkipList,
    RetrySkippedCleanup
}

internal enum DatabaseMaintenanceDisposition
{
    Validation,
    Busy,
    Unavailable,
    Complete,
    Partial,
    Failed
}

internal enum DatabaseMaintenanceStageStatus
{
    NotStarted,
    Succeeded,
    Failed
}

internal sealed record DatabaseMaintenanceStageResult(
    DatabaseMaintenanceStageStatus Status,
    long? Count,
    bool OutcomeConfirmed,
    string? Code,
    string? Message)
{
    internal static DatabaseMaintenanceStageResult NotStarted { get; } = new(
        DatabaseMaintenanceStageStatus.NotStarted,
        null,
        true,
        null,
        null);

    internal static DatabaseMaintenanceStageResult Succeeded(long count) => new(
        DatabaseMaintenanceStageStatus.Succeeded,
        count,
        true,
        null,
        null);

    internal static DatabaseMaintenanceStageResult Failed(
        bool outcomeConfirmed,
        string code,
        string message) => new(
        DatabaseMaintenanceStageStatus.Failed,
        null,
        outcomeConfirmed,
        code,
        message);
}

internal sealed class DatabaseMaintenanceResult
{
    internal DatabaseMaintenanceResult(
        DatabaseMaintenanceOperation operation,
        DatabaseMaintenanceDisposition disposition,
        DatabaseMaintenanceStageResult postgres,
        DatabaseMaintenanceStageResult skipped,
        string message,
        int invalidTokenCount = 0,
        ExclusiveHeavyOwnerBusyMetadata? busyOwner = null,
        SkippedCleanupRetryCapability? retry = null)
    {
        Operation = operation;
        Disposition = disposition;
        Postgres = postgres;
        Skipped = skipped;
        Message = message;
        InvalidTokenCount = invalidTokenCount;
        BusyOwner = busyOwner;
        Retry = retry;
    }

    internal DatabaseMaintenanceOperation Operation { get; }
    internal DatabaseMaintenanceDisposition Disposition { get; }
    internal DatabaseMaintenanceStageResult Postgres { get; }
    internal DatabaseMaintenanceStageResult Skipped { get; }
    internal string Message { get; }
    internal int InvalidTokenCount { get; }
    internal ExclusiveHeavyOwnerBusyMetadata? BusyOwner { get; }
    internal SkippedCleanupRetryCapability? Retry { get; }
    internal bool WasAdmitted => Disposition is
        DatabaseMaintenanceDisposition.Complete or
        DatabaseMaintenanceDisposition.Partial or
        DatabaseMaintenanceDisposition.Failed;
}

internal abstract record DatabaseMaintenanceRequest(DatabaseMaintenanceRequestOrigin Origin)
{
    internal sealed record ResetAll(bool Confirmed)
        : DatabaseMaintenanceRequest(DatabaseMaintenanceRequestOrigin.ResetGeoDataPage);

    internal sealed record ResetSelected(string? Input)
        : DatabaseMaintenanceRequest(DatabaseMaintenanceRequestOrigin.ResetGeoDataPage);

    internal sealed record ResetMatching(LocationResetScope Scope, string? Value)
        : DatabaseMaintenanceRequest(DatabaseMaintenanceRequestOrigin.ResetGeoDataPage);

    internal sealed record ClearSkipList()
        : DatabaseMaintenanceRequest(DatabaseMaintenanceRequestOrigin.DataPage);
}

internal interface IDatabaseMaintenanceController
{
    Task<DatabaseMaintenanceResult> ExecuteAsync(DatabaseMaintenanceRequest request);
    Task<DatabaseMaintenanceResult> RetrySkippedCleanupAsync(SkippedCleanupRetryCapability retry);
}

internal interface IImmichLocationResetStore
{
    Task<long> ClearAllLocationDataAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> ClearLocationDataForAssetsAsync(
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> ClearLocationDataByValueAsync(
        LocationResetScope scope,
        string value,
        CancellationToken cancellationToken = default);
}

internal interface ILocationValueOptionsReader
{
    Task<IReadOnlyList<LocationValueOption>> GetLocationValueOptionsAsync(
        LocationResetScope scope,
        CancellationToken cancellationToken = default);
}

internal interface ISkippedAssetsMaintenanceStore
{
    Task<long> ClearAllAsync(CancellationToken cancellationToken = default);
    Task<long> RemoveAsync(IEnumerable<Guid> assetIds, CancellationToken cancellationToken = default);
}

internal interface ISkippedAssetsCountReader
{
    Task<long> GetCountAsync();
}

internal abstract record SkippedCleanupTarget
{
    private SkippedCleanupTarget()
    {
    }

    internal sealed record All : SkippedCleanupTarget;
    internal sealed record Ids(IReadOnlyList<Guid> AssetIds) : SkippedCleanupTarget;
}

internal sealed class SkippedCleanupRetryCapability
{
    private readonly object _gate = new();
    private readonly DatabaseMaintenanceController _owner;
    private SkippedCleanupTarget? _target;
    private bool _inProgress;

    internal SkippedCleanupRetryCapability(
        DatabaseMaintenanceController owner,
        SkippedCleanupTarget target)
    {
        _owner = owner;
        _target = target;
    }

    internal bool TryBegin(
        DatabaseMaintenanceController owner,
        out SkippedCleanupTarget? target)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner)
                || _inProgress
                || _target is null)
            {
                target = null;
                return false;
            }

            _inProgress = true;
            target = _target;
            return true;
        }
    }

    internal void Finish(bool succeeded)
    {
        lock (_gate)
        {
            if (!_inProgress)
            {
                return;
            }

            if (succeeded)
            {
                _target = null;
            }

            _inProgress = false;
        }
    }
}

internal sealed class DatabaseMaintenanceController : IDatabaseMaintenanceController
{
    private const int MaxSelectedInputLength = 1_048_576;
    private const int MaxSelectedIdentifiers = 10_000;
    private const int MaxMatchingValueLength = 4_096;

    private readonly object _trackingGate = new();
    private readonly HashSet<Task> _trackedOperations = [];
    private readonly WorkerJobCoordinator _coordinator;
    private readonly IImmichLocationResetStore _immich;
    private readonly ISkippedAssetsMaintenanceStore _skipped;
    private readonly ILogger<DatabaseMaintenanceController> _logger;
    private readonly Func<DatabaseMaintenanceResult, Task>? _resultFrozen;

    public DatabaseMaintenanceController(
        WorkerJobCoordinator coordinator,
        IImmichLocationResetStore immich,
        ISkippedAssetsMaintenanceStore skipped,
        ILogger<DatabaseMaintenanceController> logger)
        : this(coordinator, immich, skipped, logger, null)
    {
    }

    internal DatabaseMaintenanceController(
        WorkerJobCoordinator coordinator,
        IImmichLocationResetStore immich,
        ISkippedAssetsMaintenanceStore skipped,
        ILogger<DatabaseMaintenanceController> logger,
        Func<DatabaseMaintenanceResult, Task>? resultFrozen)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _immich = immich ?? throw new ArgumentNullException(nameof(immich));
        _skipped = skipped ?? throw new ArgumentNullException(nameof(skipped));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resultFrozen = resultFrozen;
    }

    internal int TrackedOperationCount
    {
        get
        {
            lock (_trackingGate)
            {
                return _trackedOperations.Count;
            }
        }
    }

    public Task<DatabaseMaintenanceResult> ExecuteAsync(DatabaseMaintenanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        PreparedRequest? prepared = Prepare(request, out DatabaseMaintenanceResult? validation);
        return prepared is null
            ? Task.FromResult(validation!)
            : AdmitAndTrack(prepared, null);
    }

    public Task<DatabaseMaintenanceResult> RetrySkippedCleanupAsync(
        SkippedCleanupRetryCapability retry)
    {
        if (retry is null
            || !retry.TryBegin(this, out SkippedCleanupTarget? target))
        {
            return Task.FromResult(Validation(
                DatabaseMaintenanceOperation.RetrySkippedCleanup,
                "This skipped-list retry is no longer available."));
        }

        var prepared = new PreparedRequest.Retry(target!);
        return AdmitAndTrack(prepared, retry);
    }

    private Task<DatabaseMaintenanceResult> AdmitAndTrack(
        PreparedRequest request,
        SkippedCleanupRetryCapability? retry)
    {
        DatabaseMaintenanceAdmissionResult admission =
            _coordinator.TryReserveDatabaseMaintenance(request.Origin);
        if (admission is DatabaseMaintenanceAdmissionResult.Busy busy)
        {
            retry?.Finish(succeeded: false);
            return Task.FromResult(new DatabaseMaintenanceResult(
                request.Operation,
                DatabaseMaintenanceDisposition.Busy,
                DatabaseMaintenanceStageResult.NotStarted,
                DatabaseMaintenanceStageResult.NotStarted,
                "Database maintenance could not start because another operation is active.",
                request.InvalidTokenCount,
                busy.ActiveOwner));
        }

        if (admission is DatabaseMaintenanceAdmissionResult.Unavailable unavailable)
        {
            retry?.Finish(succeeded: false);
            return Task.FromResult(new DatabaseMaintenanceResult(
                request.Operation,
                DatabaseMaintenanceDisposition.Unavailable,
                DatabaseMaintenanceStageResult.NotStarted,
                DatabaseMaintenanceStageResult.NotStarted,
                unavailable.Message,
                request.InvalidTokenCount));
        }

        IDatabaseMaintenanceReservation reservation =
            ((DatabaseMaintenanceAdmissionResult.Admitted)admission).Reservation;
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DatabaseMaintenanceResult> operation = RunAdmittedAsync(
            request,
            reservation,
            retry,
            start.Task);
        lock (_trackingGate)
        {
            _trackedOperations.Add(operation);
        }

        Task<DatabaseMaintenanceResult> trackedOperation = ObserveAndUntrackAsync(operation);
        start.TrySetResult();
        return trackedOperation;
    }

    private async Task<DatabaseMaintenanceResult> RunAdmittedAsync(
        PreparedRequest request,
        IDatabaseMaintenanceReservation reservation,
        SkippedCleanupRetryCapability? retry,
        Task start)
    {
        await start.ConfigureAwait(false);
        DatabaseMaintenanceResult result;
        bool retrySucceeded = false;
        try
        {
            result = await ExecutePreparedAsync(request).ConfigureAwait(false);
            retrySucceeded = retry is not null
                && result.Disposition == DatabaseMaintenanceDisposition.Complete;
            if (_resultFrozen is not null)
            {
                try
                {
                    await _resultFrozen(result).ConfigureAwait(false);
                }
                catch
                {
                    // This hook is observational and cannot alter result or finality.
                }
            }
        }
        catch (Exception exception)
        {
            Failure failure = SafeFailure(exception, StoreKind.Controller);
            LogFailure(request.Operation, StoreKind.Controller, failure, exception);
            result = new DatabaseMaintenanceResult(
                request.Operation,
                DatabaseMaintenanceDisposition.Failed,
                DatabaseMaintenanceStageResult.Failed(
                    outcomeConfirmed: false,
                    failure.Code,
                    failure.Message),
                DatabaseMaintenanceStageResult.NotStarted,
                failure.Message,
                request.InvalidTokenCount);
        }
        finally
        {
            retry?.Finish(retrySucceeded);
            await reservation.DisposeAsync().ConfigureAwait(false);
        }

        return result;
    }

    private async Task<DatabaseMaintenanceResult> ObserveAndUntrackAsync(
        Task<DatabaseMaintenanceResult> operation)
    {
        try
        {
            return await operation.ConfigureAwait(false);
        }
        finally
        {
            lock (_trackingGate)
            {
                _trackedOperations.Remove(operation);
            }
        }
    }

    private async Task<DatabaseMaintenanceResult> ExecutePreparedAsync(PreparedRequest request)
    {
        if (request is PreparedRequest.ClearSkipList)
        {
            return await ExecuteClearSkipListAsync(request).ConfigureAwait(false);
        }

        if (request is PreparedRequest.Retry retry)
        {
            return await ExecuteRetryAsync(retry).ConfigureAwait(false);
        }

        DatabaseMaintenanceStageResult postgres;
        SkippedCleanupTarget target;
        try
        {
            switch (request)
            {
                case PreparedRequest.ResetAll:
                    long allCount = await _immich.ClearAllLocationDataAsync(
                        CancellationToken.None).ConfigureAwait(false);
                    postgres = DatabaseMaintenanceStageResult.Succeeded(allCount);
                    target = new SkippedCleanupTarget.All();
                    break;
                case PreparedRequest.ResetSelected selected:
                    IReadOnlyList<Guid> selectedIds = await _immich.ClearLocationDataForAssetsAsync(
                        selected.AssetIds,
                        CancellationToken.None).ConfigureAwait(false);
                    postgres = DatabaseMaintenanceStageResult.Succeeded(selectedIds.Count);
                    target = new SkippedCleanupTarget.Ids(selected.AssetIds);
                    break;
                case PreparedRequest.ResetMatching matching:
                    IReadOnlyList<Guid> matchingIds = await _immich.ClearLocationDataByValueAsync(
                        matching.Scope,
                        matching.Value,
                        CancellationToken.None).ConfigureAwait(false);
                    Guid[] frozenIds = matchingIds.Distinct().ToArray();
                    postgres = DatabaseMaintenanceStageResult.Succeeded(frozenIds.Length);
                    target = new SkippedCleanupTarget.Ids(Array.AsReadOnly(frozenIds));
                    break;
                default:
                    throw new InvalidOperationException("Unknown database maintenance request.");
            }
        }
        catch (Exception exception)
        {
            Failure failure = SafeFailure(exception, StoreKind.Postgres);
            LogFailure(request.Operation, StoreKind.Postgres, failure, exception);
            DatabaseMaintenanceStageResult failed = DatabaseMaintenanceStageResult.Failed(
                failure.OutcomeConfirmed,
                failure.Code,
                failure.Message);
            return new DatabaseMaintenanceResult(
                request.Operation,
                DatabaseMaintenanceDisposition.Failed,
                failed,
                DatabaseMaintenanceStageResult.NotStarted,
                failure.Message,
                request.InvalidTokenCount);
        }

        try
        {
            long removed = await CleanupSkippedAsync(target).ConfigureAwait(false);
            DatabaseMaintenanceStageResult skipped = DatabaseMaintenanceStageResult.Succeeded(removed);
            return new DatabaseMaintenanceResult(
                request.Operation,
                DatabaseMaintenanceDisposition.Complete,
                postgres,
                skipped,
                CompleteMessage(request.Operation, postgres.Count ?? 0, removed, request.InvalidTokenCount),
                request.InvalidTokenCount);
        }
        catch (Exception exception)
        {
            Failure failure = SafeFailure(exception, StoreKind.Skipped);
            LogFailure(request.Operation, StoreKind.Skipped, failure, exception);
            var capability = new SkippedCleanupRetryCapability(this, target);
            return new DatabaseMaintenanceResult(
                request.Operation,
                DatabaseMaintenanceDisposition.Partial,
                postgres,
                DatabaseMaintenanceStageResult.Failed(
                    failure.OutcomeConfirmed,
                    failure.Code,
                    failure.Message),
                $"Immich location data was reset for {postgres.Count ?? 0:N0} asset(s), but skipped-list cleanup could not be confirmed. Retry only the skipped-list cleanup.",
                request.InvalidTokenCount,
                retry: capability);
        }
    }

    private async Task<DatabaseMaintenanceResult> ExecuteClearSkipListAsync(PreparedRequest request)
    {
        try
        {
            long removed = await _skipped.ClearAllAsync(CancellationToken.None).ConfigureAwait(false);
            return new DatabaseMaintenanceResult(
                request.Operation,
                DatabaseMaintenanceDisposition.Complete,
                DatabaseMaintenanceStageResult.NotStarted,
                DatabaseMaintenanceStageResult.Succeeded(removed),
                $"Cleared {removed:N0} skipped asset(s). They will be retried on the next run.");
        }
        catch (Exception exception)
        {
            Failure failure = SafeFailure(exception, StoreKind.Skipped);
            LogFailure(request.Operation, StoreKind.Skipped, failure, exception);
            return new DatabaseMaintenanceResult(
                request.Operation,
                DatabaseMaintenanceDisposition.Failed,
                DatabaseMaintenanceStageResult.NotStarted,
                DatabaseMaintenanceStageResult.Failed(
                    failure.OutcomeConfirmed,
                    failure.Code,
                    failure.Message),
                failure.Message);
        }
    }

    private async Task<DatabaseMaintenanceResult> ExecuteRetryAsync(PreparedRequest.Retry request)
    {
        try
        {
            long removed = await CleanupSkippedAsync(request.Target).ConfigureAwait(false);
            return new DatabaseMaintenanceResult(
                request.Operation,
                DatabaseMaintenanceDisposition.Complete,
                DatabaseMaintenanceStageResult.NotStarted,
                DatabaseMaintenanceStageResult.Succeeded(removed),
                $"Skipped-list cleanup completed and removed {removed:N0} tracked asset(s).");
        }
        catch (Exception exception)
        {
            Failure failure = SafeFailure(exception, StoreKind.Skipped);
            LogFailure(request.Operation, StoreKind.Skipped, failure, exception);
            return new DatabaseMaintenanceResult(
                request.Operation,
                DatabaseMaintenanceDisposition.Failed,
                DatabaseMaintenanceStageResult.NotStarted,
                DatabaseMaintenanceStageResult.Failed(
                    failure.OutcomeConfirmed,
                    failure.Code,
                    failure.Message),
                $"Skipped-list cleanup could not be confirmed. {failure.Message}");
        }
    }

    private Task<long> CleanupSkippedAsync(SkippedCleanupTarget target) => target switch
    {
        SkippedCleanupTarget.All => _skipped.ClearAllAsync(CancellationToken.None),
        SkippedCleanupTarget.Ids ids => _skipped.RemoveAsync(ids.AssetIds, CancellationToken.None),
        _ => throw new InvalidOperationException("Unknown skipped cleanup target.")
    };

    private static PreparedRequest? Prepare(
        DatabaseMaintenanceRequest request,
        out DatabaseMaintenanceResult? validation)
    {
        validation = null;
        switch (request)
        {
            case DatabaseMaintenanceRequest.ResetAll { Confirmed: true }:
                return new PreparedRequest.ResetAll();
            case DatabaseMaintenanceRequest.ResetAll:
                validation = Validation(
                    DatabaseMaintenanceOperation.ResetAll,
                    "Confirm Reset All Data before starting database maintenance.");
                return null;
            case DatabaseMaintenanceRequest.ResetSelected selected:
                return PrepareSelected(selected.Input, out validation);
            case DatabaseMaintenanceRequest.ResetMatching matching:
                if (!Enum.IsDefined(matching.Scope))
                {
                    validation = Validation(
                        DatabaseMaintenanceOperation.ResetMatching,
                        "Select City, State, or Country before starting database maintenance.");
                    return null;
                }

                if (string.IsNullOrWhiteSpace(matching.Value))
                {
                    validation = Validation(
                        DatabaseMaintenanceOperation.ResetMatching,
                        "Select a location value before starting database maintenance.");
                    return null;
                }

                if (matching.Value.Length > MaxMatchingValueLength)
                {
                    validation = Validation(
                        DatabaseMaintenanceOperation.ResetMatching,
                        "The selected location value is too long.");
                    return null;
                }

                return new PreparedRequest.ResetMatching(matching.Scope, matching.Value);
            case DatabaseMaintenanceRequest.ClearSkipList:
                return new PreparedRequest.ClearSkipList();
            default:
                validation = Validation(
                    DatabaseMaintenanceOperation.ClearSkipList,
                    "The database maintenance request is not valid.");
                return null;
        }
    }

    private static PreparedRequest? PrepareSelected(
        string? input,
        out DatabaseMaintenanceResult? validation)
    {
        validation = null;
        if (input is null || input.Length > MaxSelectedInputLength)
        {
            validation = Validation(
                DatabaseMaintenanceOperation.ResetSelected,
                input is null
                    ? "Paste at least one asset GUID to reset."
                    : "The selected asset input is too long.");
            return null;
        }

        string[] tokens = input.Split(
            ['\r', '\n', ',', ';', ' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new HashSet<Guid>();
        int invalid = 0;
        foreach (string token in tokens)
        {
            if (Guid.TryParse(token, out Guid id))
            {
                ids.Add(id);
                if (ids.Count > MaxSelectedIdentifiers)
                {
                    validation = Validation(
                        DatabaseMaintenanceOperation.ResetSelected,
                        "Too many asset GUIDs were submitted.",
                        invalid);
                    return null;
                }
            }
            else
            {
                invalid++;
            }
        }

        if (ids.Count == 0)
        {
            validation = Validation(
                DatabaseMaintenanceOperation.ResetSelected,
                invalid > 0
                    ? $"No valid asset GUIDs were found. Ignored {invalid:N0} invalid token(s)."
                    : "Paste at least one asset GUID to reset.",
                invalid);
            return null;
        }

        Guid[] ordered = ids.OrderBy(id => id).ToArray();
        return new PreparedRequest.ResetSelected(Array.AsReadOnly(ordered), invalid);
    }

    private static DatabaseMaintenanceResult Validation(
        DatabaseMaintenanceOperation operation,
        string message,
        int invalidTokenCount = 0) => new(
        operation,
        DatabaseMaintenanceDisposition.Validation,
        DatabaseMaintenanceStageResult.NotStarted,
        DatabaseMaintenanceStageResult.NotStarted,
        message,
        invalidTokenCount);

    private static string CompleteMessage(
        DatabaseMaintenanceOperation operation,
        long postgresCount,
        long skippedCount,
        int invalidTokenCount)
    {
        string ignored = invalidTokenCount > 0
            ? $" Ignored {invalidTokenCount:N0} invalid token(s)."
            : string.Empty;
        return operation switch
        {
            DatabaseMaintenanceOperation.ResetAll =>
                $"Cleared reverse geo data from {postgresCount:N0} asset(s) and removed {skippedCount:N0} skipped asset(s). Ready to reprocess.",
            DatabaseMaintenanceOperation.ResetSelected =>
                $"Cleared reverse geo data for {postgresCount:N0} asset(s) and removed {skippedCount:N0} matching asset(s) from the skip list.{ignored}",
            DatabaseMaintenanceOperation.ResetMatching =>
                $"Cleared reverse geo data for {postgresCount:N0} matching asset(s) and removed {skippedCount:N0} matching asset(s) from the skip list.",
            _ => "Database maintenance completed."
        };
    }

    private Failure SafeFailure(Exception exception, StoreKind store)
    {
        if (store == StoreKind.Postgres)
        {
            return exception switch
            {
                PostgresException { SqlState: "42501" } => new Failure(
                    "immich-permission",
                    "Immich rejected the location reset because the configured database user lacks update permission. The skipped list was not changed.",
                    true),
                PostgresException { SqlState: "28P01" } => new Failure(
                    "immich-authentication",
                    "Immich rejected the configured database credentials. The skipped list was not changed.",
                    true),
                PostgresException { SqlState: "25006" } => new Failure(
                    "immich-read-only",
                    "The Immich database is read-only, so the location reset did not run. The skipped list was not changed.",
                    true),
                PostgresException { SqlState: "57014" } => new Failure(
                    "immich-statement-cancelled",
                    "PostgreSQL cancelled and rolled back the location reset statement. The skipped list was not changed.",
                    true),
                PostgresException { InvariantSeverity: "ERROR" } => new Failure(
                    "immich-statement-failed",
                    "PostgreSQL rejected and rolled back the location reset statement. The skipped list was not changed.",
                    true),
                PostgresException => new Failure(
                    "immich-outcome-unconfirmed",
                    "The Immich database result could not be confirmed after a PostgreSQL server error. The skipped list was not changed.",
                    false),
                TimeoutException or OperationCanceledException => new Failure(
                    "immich-outcome-unconfirmed",
                    "The Immich database result could not be confirmed after a timeout. The skipped list was not changed.",
                    false),
                NpgsqlException => new Failure(
                    "immich-outcome-unconfirmed",
                    "The Immich database result could not be confirmed because the connection failed. The skipped list was not changed.",
                    false),
                UnauthorizedAccessException => new Failure(
                    "immich-outcome-unconfirmed",
                    "The Immich database result could not be confirmed. The skipped list was not changed.",
                    false),
                _ => new Failure(
                    "immich-outcome-unconfirmed",
                    "The Immich database result could not be confirmed. The skipped list was not changed.",
                    false)
            };
        }

        if (store == StoreKind.Skipped)
        {
            return exception switch
            {
                SqliteException { SqliteErrorCode: 8 } => new Failure(
                    "skipped-read-only",
                    "The skipped-assets database is read-only.",
                    true),
                SqliteException { SqliteErrorCode: 5 or 6 } => new Failure(
                    "skipped-in-use",
                    "The skipped-assets database is in use by another operation.",
                    false),
                UnauthorizedAccessException or System.Security.SecurityException => new Failure(
                    "skipped-permission",
                    "The skipped-assets database could not be updated because storage permission was denied.",
                    true),
                IOException or SqliteException => new Failure(
                    "skipped-io",
                    "The skipped-assets database update could not be confirmed.",
                    false),
                TimeoutException or OperationCanceledException => new Failure(
                    "skipped-timeout",
                    "The skipped-assets database update could not be confirmed after a timeout.",
                    false),
                _ => new Failure(
                    "skipped-failed",
                    "The skipped-assets database update could not be confirmed.",
                    false)
            };
        }

        return new Failure(
            "database-maintenance-failed",
            "Database maintenance could not be completed.",
            false);
    }

    private void LogFailure(
        DatabaseMaintenanceOperation operation,
        StoreKind store,
        Failure failure,
        Exception exception)
    {
        _logger.LogWarning(
            "Database maintenance {Operation} failed in {Store} with {FailureCode} ({FailureType}, {HResult}).",
            operation,
            store,
            failure.Code,
            exception.GetType().Name,
            exception.HResult);
    }

    private enum StoreKind
    {
        Postgres,
        Skipped,
        Controller
    }

    private sealed record Failure(string Code, string Message, bool OutcomeConfirmed);

    private abstract record PreparedRequest(
        DatabaseMaintenanceOperation Operation,
        DatabaseMaintenanceRequestOrigin Origin,
        int InvalidTokenCount)
    {
        internal sealed record ResetAll()
            : PreparedRequest(
                DatabaseMaintenanceOperation.ResetAll,
                DatabaseMaintenanceRequestOrigin.ResetGeoDataPage,
                0);

        internal sealed record ResetSelected(IReadOnlyList<Guid> AssetIds, int InvalidCount)
            : PreparedRequest(
                DatabaseMaintenanceOperation.ResetSelected,
                DatabaseMaintenanceRequestOrigin.ResetGeoDataPage,
                InvalidCount);

        internal sealed record ResetMatching(LocationResetScope Scope, string Value)
            : PreparedRequest(
                DatabaseMaintenanceOperation.ResetMatching,
                DatabaseMaintenanceRequestOrigin.ResetGeoDataPage,
                0);

        internal sealed record ClearSkipList()
            : PreparedRequest(
                DatabaseMaintenanceOperation.ClearSkipList,
                DatabaseMaintenanceRequestOrigin.DataPage,
                0);

        internal sealed record Retry(SkippedCleanupTarget Target)
            : PreparedRequest(
                DatabaseMaintenanceOperation.RetrySkippedCleanup,
                DatabaseMaintenanceRequestOrigin.ResetGeoDataPage,
                0);
    }
}
