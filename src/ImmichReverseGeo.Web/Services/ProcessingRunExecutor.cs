using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public sealed class ProcessingRunExecutor : IProcessingRunExecutor
{
    private const string RunLockBusyFailure = "Another Immich ReverseGeo worker is already processing this database.";
    private const string RunLockInfrastructureFailure = "The database run lock could not be acquired.";
    private const string RunLockOwnershipLostFailure = "The database run lock was lost during processing.";

    private readonly ILogger _logger;
    private readonly IProcessingRunConfiguration _configuration;
    private readonly IProcessingAssetRepository _assets;
    private readonly IProcessingSkippedStore _skippedStore;
    private readonly IProcessingAdministrativeResolver _administrativeResolver;
    private readonly IProcessingInfrastructureLookup _infrastructureLookup;
    private readonly IProcessingRunDelay _delay;
    private readonly TimeProvider _timeProvider;
    private readonly IProcessingRunLock? _runLock;
    private readonly WorkerProcessExitOutcomeAccumulator? _workerOutcomes;
    private readonly IProcessingRunDomainOperation _domainOperation;

    public ProcessingRunExecutor(
        ILogger<ProcessingRunExecutor> logger,
        IProcessingRunConfiguration configuration,
        IProcessingAssetRepository assets,
        IProcessingSkippedStore skippedStore,
        IProcessingAdministrativeResolver administrativeResolver,
        IProcessingInfrastructureLookup infrastructureLookup,
        IProcessingRunDelay delay,
        TimeProvider timeProvider)
        : this((ILogger)logger, configuration, assets, skippedStore, administrativeResolver, infrastructureLookup, delay, timeProvider)
    {
    }

    internal ProcessingRunExecutor(
        ILogger logger,
        IProcessingRunConfiguration configuration,
        IProcessingAssetRepository assets,
        IProcessingSkippedStore skippedStore,
        IProcessingAdministrativeResolver administrativeResolver,
        IProcessingInfrastructureLookup infrastructureLookup,
        IProcessingRunDelay delay,
        TimeProvider timeProvider,
        IProcessingRunLock? runLock = null,
        WorkerProcessExitOutcomeAccumulator? workerOutcomes = null,
        IProcessingRunDomainOperation? domainOperation = null)
    {
        if (runLock is not null && workerOutcomes is null)
        {
            throw new ArgumentException("A worker run lock requires the worker outcome accumulator.", nameof(workerOutcomes));
        }

        _logger = logger;
        _configuration = configuration;
        _assets = assets;
        _skippedStore = skippedStore;
        _administrativeResolver = administrativeResolver;
        _infrastructureLookup = infrastructureLookup;
        _delay = delay;
        _timeProvider = timeProvider;
        _runLock = runLock;
        _workerOutcomes = workerOutcomes;
        _domainOperation = domainOperation ?? DefaultProcessingRunDomainOperation.Instance;
    }

    public async Task<ProcessingRunResult> ExecuteAsync(
        ProcessingRunRequest request,
        IProcessingEventReporter reporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reporter);

        var startedAtUtc = UtcNow();
        var rawSession = await reporter.OpenRunAsync(request, startedAtUtc, CancellationToken.None).ConfigureAwait(false);
        var reporting = new ReporterAdmissionBoundary();
        var session = new GuardedProcessingRunEventSession(rawSession, reporting);
        ProcessingRunCounts counts = new();
        var outcome = ProcessingRunOutcome.Completed;
        string? failureMessage = null;
        IProcessingRunLockLease? runLockLease = null;
        CancellationTokenSource? protectedWorkCancellation = null;
        CancellationToken activeToken = cancellationToken;
        bool domainWorkStarted = false;

        try
        {
            try
            {
                ProcessingRunLockAcquisition? acquisition = await AcquireRunLockAsync(cancellationToken).ConfigureAwait(false);
                bool runDomainWork = true;
                switch (acquisition)
                {
                    case ProcessingRunLockAcquisition.Acquired acquired:
                        runLockLease = acquired.Lease;
                        protectedWorkCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken,
                            runLockLease.OwnershipLost);
                        activeToken = protectedWorkCancellation.Token;
                        break;
                    case ProcessingRunLockAcquisition.Busy:
                        outcome = ProcessingRunOutcome.Failed;
                        failureMessage = RunLockBusyFailure;
                        AddWorkerOutcome(WorkerProcessExitFact.Busy());
                        runDomainWork = false;
                        break;
                    case ProcessingRunLockAcquisition.Cancelled:
                        outcome = ProcessingRunOutcome.Cancelled;
                        AddWorkerOutcome(WorkerProcessExitFact.ShutdownCancelled());
                        runDomainWork = false;
                        break;
                    case ProcessingRunLockAcquisition.InfrastructureFailure:
                        outcome = ProcessingRunOutcome.Failed;
                        failureMessage = RunLockInfrastructureFailure;
                        AddWorkerOutcome(WorkerProcessExitFact.ExecutionInfrastructure());
                        runDomainWork = false;
                        break;
                }

                if (runDomainWork)
                {
                    domainWorkStarted = true;
                    await _domainOperation.ExecuteAsync(
                        session,
                        () => ExecuteDomainAsync(session, runLockLease, counts, activeToken),
                        activeToken).ConfigureAwait(false);
                }
            }
            catch (ProcessingEventReportingException ex)
            {
                if (reporting.HasFailure)
                {
                    reporting.ThrowFirstFailure();
                }

                ExceptionDispatchInfo.Capture(ex.ReporterException).Throw();
                throw;
            }
            catch (OperationCanceledException) when (runLockLease?.IsOwnershipLost == true)
            {
                outcome = ProcessingRunOutcome.Failed;
                failureMessage = RunLockOwnershipLostFailure;
                AddWorkerOutcome(WorkerProcessExitFact.ExecutionInfrastructure());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = ProcessingRunOutcome.Cancelled;
                AddWorkerOutcome(WorkerProcessExitFact.ShutdownCancelled());
            }
            catch (Exception) when (reporting.HasFailure)
            {
                reporting.ThrowFirstFailure();
                throw;
            }
            catch (OutOfMemoryException) when (!domainWorkStarted)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error during processing run");
                outcome = ProcessingRunOutcome.Failed;
                failureMessage = ex.Message;
            }

            if (runLockLease?.IsOwnershipLost == true)
            {
                outcome = ProcessingRunOutcome.Failed;
                failureMessage = RunLockOwnershipLostFailure;
                AddWorkerOutcome(WorkerProcessExitFact.ExecutionInfrastructure());
            }

            var result = new ProcessingRunResult(
                request,
                startedAtUtc,
                UtcNow(),
                counts.Processed,
                counts.Updated,
                counts.Skipped,
                counts.Failed,
                outcome,
                failureMessage);
            await session.FinishAsync(result).ConfigureAwait(false);
            return result;
        }
        finally
        {
            await CompleteRunLockAsync(runLockLease, protectedWorkCancellation).ConfigureAwait(false);
        }
    }

    private async ValueTask<ProcessingRunLockAcquisition?> AcquireRunLockAsync(CancellationToken cancellationToken)
    {
        if (_runLock is null)
        {
            return null;
        }

        try
        {
            return await _runLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ProcessingRunLockAcquisition.Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new ProcessingRunLockAcquisition.InfrastructureFailure();
        }
    }

    private async Task CompleteRunLockAsync(
        IProcessingRunLockLease? lease,
        CancellationTokenSource? protectedWorkCancellation)
    {
        try
        {
            if (lease is null)
            {
                return;
            }

            try
            {
                ProcessingRunLockRelease release = await lease.ReleaseAsync().ConfigureAwait(false);
                if (lease.IsOwnershipLost)
                {
                    AddWorkerOutcome(WorkerProcessExitFact.ExecutionInfrastructure());
                }

                if (release.InfrastructureFailure)
                {
                    AddWorkerOutcome(WorkerProcessExitFact.CleanupInfrastructure());
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AddWorkerOutcome(WorkerProcessExitFact.CleanupInfrastructure());
            }
        }
        finally
        {
            protectedWorkCancellation?.Dispose();
        }
    }

    private void AddWorkerOutcome(WorkerProcessExitFact fact) => _workerOutcomes?.Add(fact);

    private static void ThrowIfOwnershipLost(
        IProcessingRunLockLease? lease,
        CancellationToken activeToken)
    {
        if (lease is not null)
        {
            if (lease.IsOwnershipLost)
            {
                throw new OperationCanceledException(lease.OwnershipLost);
            }

            activeToken.ThrowIfCancellationRequested();
        }
    }

    private async Task ExecuteDomainAsync(
        IProcessingRunEventSession session,
        IProcessingRunLockLease? runLockLease,
        ProcessingRunCounts counts,
        CancellationToken activeToken)
    {
        static async ValueTask ReportAssetAsync(
            Func<ValueTask> operation,
            CancellationToken reportToken)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (reportToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ProcessingEventReportingException(ex);
            }
        }

        ThrowIfOwnershipLost(runLockLease, activeToken);
        var total = await _assets.GetUnprocessedCountAsync(activeToken).ConfigureAwait(false);
        ThrowIfOwnershipLost(runLockLease, activeToken);
        await session.DetermineEligibilityAsync(total, activeToken).ConfigureAwait(false);

        if (total > 0)
        {
            ThrowIfOwnershipLost(runLockLease, activeToken);
            var skippedIds = await _skippedStore.GetAllAsync().ConfigureAwait(false);
            ThrowIfOwnershipLost(runLockLease, activeToken);
            if (skippedIds.Count > 0)
            {
                await session.ReportLogAsync(
                    ProcessingLogLevel.Information,
                    $"Skipping {skippedIds.Count} previously unresolvable assets.",
                    activeToken).ConfigureAwait(false);
            }

            ThrowIfOwnershipLost(runLockLease, activeToken);
            var config = await _configuration.GetConfigAsync().ConfigureAwait(false);
            ThrowIfOwnershipLost(runLockLease, activeToken);
            var cursor = AssetCursor.Initial;
            var batchNumber = 0;
            while (true)
            {
                activeToken.ThrowIfCancellationRequested();
                var batch = await _assets.GetUnprocessedBatchAsync(
                    cursor,
                    config.Processing.BatchSize,
                    activeToken).ConfigureAwait(false);
                ThrowIfOwnershipLost(runLockLease, activeToken);
                if (batch.Count == 0)
                {
                    break;
                }

                batchNumber++;
                await session.ReportLogAsync(
                    ProcessingLogLevel.Information,
                    $"Batch {batchNumber}: fetched {batch.Count} assets (total processed so far: {counts.Updated}).",
                    activeToken).ConfigureAwait(false);
                cursor = new AssetCursor(batch[^1].CreatedAt, batch[^1].Id);

                await Parallel.ForEachAsync(
                    batch,
                    new ParallelOptions
                    {
                        CancellationToken = activeToken,
                        MaxDegreeOfParallelism = Math.Clamp(config.Processing.MaxDegreeOfParallelism, 1, 32)
                    },
                    async (asset, token) =>
                    {
                        if (!skippedIds.Contains(asset.Id))
                        {
                            await ProcessAssetAsync(
                                session,
                                asset,
                                config,
                                ReportAssetAsync,
                                counts.IncrementUpdated,
                                counts.IncrementSkipped,
                                counts.IncrementFailed,
                                token).ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);

                if (config.Processing.BatchDelayMs > 0)
                {
                    await _delay.DelayAsync(
                        TimeSpan.FromMilliseconds(config.Processing.BatchDelayMs),
                        activeToken).ConfigureAwait(false);
                }
            }
        }

        ThrowIfOwnershipLost(runLockLease, activeToken);
    }

    private sealed class ProcessingRunCounts
    {
        private long _updated;
        private long _skipped;
        private long _failed;

        internal long Updated => Volatile.Read(ref _updated);
        internal long Skipped => Volatile.Read(ref _skipped);
        internal long Failed => Volatile.Read(ref _failed);
        internal long Processed => checked(Updated + Skipped + Failed);

        internal void IncrementUpdated() => Interlocked.Increment(ref _updated);
        internal void IncrementSkipped() => Interlocked.Increment(ref _skipped);
        internal void IncrementFailed() => Interlocked.Increment(ref _failed);
    }

    private async Task ProcessAssetAsync(
        IProcessingRunEventSession session,
        AssetRecord asset,
        AppConfig config,
        Func<Func<ValueTask>, CancellationToken, ValueTask> reportAsync,
        Action updated,
        Action skipped,
        Action failed,
        CancellationToken cancellationToken)
    {
        var step = "FindCountry";
        try
        {
            var resolution = await _administrativeResolver.ResolveAsync(
                asset.Latitude,
                asset.Longitude,
                config.Processing,
                session,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (resolution is null)
            {
                await reportAsync(
                    () => session.ReportLogAsync(
                        ProcessingLogLevel.Warning,
                        $"Asset {asset.Id}: no country found at ({asset.Latitude:F4}, {asset.Longitude:F4}), skipping.",
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await _skippedStore.AddAsync(asset.Id).ConfigureAwait(false);
                await reportAsync(session.ReportSkippedAsync, CancellationToken.None).ConfigureAwait(false);
                skipped();
                return;
            }

            step = "FindAdminLevels";
            var geoResult = resolution.GeoResult;
            if (config.Processing.UseAirportInfrastructure)
            {
                step = "FindNearestInfrastructure";
                var infrastructure = await _infrastructureLookup.FindNearestInfrastructureAsync(
                    asset.Latitude,
                    asset.Longitude,
                    resolution.Iso3,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (infrastructure.BestMatch?.GeometryContainsPoint == true
                    || (geoResult.City is null && infrastructure.BestMatch is not null))
                {
                    geoResult = geoResult with { City = infrastructure.BestMatch.Name };
                }
            }

            geoResult = geoResult.WithFallbackCity();
            step = "WriteLocation";
            if (IsLoggerOnlyNoCitySkip(geoResult))
            {
                _logger.LogWarning(
                    "Asset {AssetId}: country={Country} state={State} resolved but no city — skipping write (lat={Lat:F4}, lon={Lon:F4})",
                    asset.Id,
                    geoResult.Country,
                    geoResult.State,
                    asset.Latitude,
                    asset.Longitude);
                cancellationToken.ThrowIfCancellationRequested();
                await reportAsync(session.ReportSkippedAsync, CancellationToken.None).ConfigureAwait(false);
                skipped();
                return;
            }

            if (geoResult.HasMatch)
            {
                if (config.Processing.VerboseLogging)
                {
                    await reportAsync(
                        () => session.ReportLogAsync(
                            ProcessingLogLevel.Trace,
                            $"Asset {asset.Id}: {geoResult.City}, {geoResult.State}, {geoResult.Country}",
                            cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogDebug(
                        "Asset {AssetId}: {City}, {State}, {Country}",
                        asset.Id,
                        geoResult.City,
                        geoResult.State,
                        geoResult.Country);
                }

                cancellationToken.ThrowIfCancellationRequested();
                await _assets.WriteLocationAsync(asset.Id, geoResult, cancellationToken).ConfigureAwait(false);
                await reportAsync(session.ReportUpdatedAsync, CancellationToken.None).ConfigureAwait(false);
                updated();
            }
            else
            {
                await reportAsync(
                    () => session.ReportLogAsync(
                        ProcessingLogLevel.Warning,
                        $"Asset {asset.Id}: country={resolution.CountryName} but no admin match, skipping.",
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await _skippedStore.AddAsync(asset.Id).ConfigureAwait(false);
                await reportAsync(session.ReportSkippedAsync, CancellationToken.None).ConfigureAwait(false);
                skipped();
            }
        }
        catch (ProcessingEventReportingException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Processing cancelled at step={Step} for asset {AssetId}",
                step,
                asset.Id);
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error at step={Step} for asset {AssetId} [{ExType}]",
                step,
                asset.Id,
                ex.GetType().Name);
            await reportAsync(
                () => session.ReportLogAsync(
                    ProcessingLogLevel.Error,
                    $"Asset {asset.Id} [{step}]: {ex.Message}",
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
            await reportAsync(session.ReportFailedAsync, CancellationToken.None).ConfigureAwait(false);
            failed();
        }
    }

    internal static bool IsLoggerOnlyNoCitySkip(GeoResult geoResult)
    {
        return geoResult.HasMatch && geoResult.City is null;
    }

    private DateTimeOffset UtcNow()
    {
        return _timeProvider.GetUtcNow().ToUniversalTime();
    }

    private sealed class GuardedProcessingRunEventSession(
        IProcessingRunEventSession inner,
        ReporterAdmissionBoundary boundary) : IProcessingRunEventSession
    {
        public ProcessingRunRequest Request => inner.Request;

        public ValueTask DetermineEligibilityAsync(
            long eligibleCount,
            CancellationToken cancellationToken = default)
        {
            return boundary.InvokeAsync(
                () => inner.DetermineEligibilityAsync(eligibleCount, cancellationToken),
                cancellationToken);
        }

        public ValueTask ReportUpdatedAsync()
        {
            return boundary.InvokeAsync(inner.ReportUpdatedAsync, CancellationToken.None);
        }

        public ValueTask ReportSkippedAsync()
        {
            return boundary.InvokeAsync(inner.ReportSkippedAsync, CancellationToken.None);
        }

        public ValueTask ReportFailedAsync()
        {
            return boundary.InvokeAsync(inner.ReportFailedAsync, CancellationToken.None);
        }

        public ValueTask ReportLogAsync(
            ProcessingLogLevel level,
            string message,
            CancellationToken cancellationToken = default)
        {
            return boundary.InvokeAsync(
                () => inner.ReportLogAsync(level, message, cancellationToken),
                cancellationToken);
        }

        public async ValueTask<IAsyncDisposable> BeginActivityAsync(
            string label,
            CancellationToken cancellationToken = default)
        {
            var activity = await boundary.InvokeAsync(
                () => inner.BeginActivityAsync(label, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return new GuardedActivity(activity, boundary);
        }

        public ValueTask FinishAsync(ProcessingRunResult result)
        {
            return boundary.InvokeAsync(
                () => inner.FinishAsync(result),
                CancellationToken.None);
        }
    }

    private sealed class GuardedActivity(
        IAsyncDisposable inner,
        ReporterAdmissionBoundary boundary) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return boundary.InvokeAsync(inner.DisposeAsync, CancellationToken.None);
        }
    }

    private sealed class ReporterAdmissionBoundary
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private ExceptionDispatchInfo? _firstFailure;

        public bool HasFailure => Volatile.Read(ref _firstFailure) is not null;

        public async ValueTask InvokeAsync(Func<ValueTask> operation, CancellationToken activeToken)
        {
            await InvokeAsync(
                async () =>
                {
                    await operation().ConfigureAwait(false);
                    return true;
                },
                activeToken).ConfigureAwait(false);
        }

        public async ValueTask<T> InvokeAsync<T>(
            Func<ValueTask<T>> operation,
            CancellationToken activeToken)
        {
            ThrowFirstFailureIfPresent();
            await _gate.WaitAsync(activeToken).ConfigureAwait(false);
            try
            {
                ThrowFirstFailureIfPresent();
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (activeToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var captured = ExceptionDispatchInfo.Capture(ex);
                    Interlocked.CompareExchange(ref _firstFailure, captured, null);
                    ThrowFirstFailure();
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public void ThrowFirstFailure()
        {
            var failure = Volatile.Read(ref _firstFailure);
            if (failure is null)
            {
                throw new InvalidOperationException("No reporter failure has been captured.");
            }

            failure.Throw();
        }

        private void ThrowFirstFailureIfPresent()
        {
            Volatile.Read(ref _firstFailure)?.Throw();
        }
    }
}
