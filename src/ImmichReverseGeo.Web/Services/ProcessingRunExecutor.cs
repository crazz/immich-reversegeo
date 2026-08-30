using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public sealed class ProcessingRunExecutor : IProcessingRunExecutor
{
    private readonly ILogger _logger;
    private readonly IProcessingRunConfiguration _configuration;
    private readonly IProcessingAssetRepository _assets;
    private readonly IProcessingSkippedStore _skippedStore;
    private readonly IProcessingAdministrativeResolver _administrativeResolver;
    private readonly IProcessingInfrastructureLookup _infrastructureLookup;
    private readonly IProcessingRunDelay _delay;
    private readonly TimeProvider _timeProvider;

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
        TimeProvider timeProvider)
    {
        _logger = logger;
        _configuration = configuration;
        _assets = assets;
        _skippedStore = skippedStore;
        _administrativeResolver = administrativeResolver;
        _infrastructureLookup = infrastructureLookup;
        _delay = delay;
        _timeProvider = timeProvider;
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
        long updated = 0;
        long skipped = 0;
        long failed = 0;
        var outcome = ProcessingRunOutcome.Completed;
        string? failureMessage = null;

        async ValueTask ReportAssetAsync(Func<ValueTask> operation, CancellationToken activeToken)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (activeToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ProcessingEventReportingException(ex);
            }
        }

        try
        {
            var total = await _assets.GetUnprocessedCountAsync(cancellationToken).ConfigureAwait(false);
            await session.DetermineEligibilityAsync(total, cancellationToken).ConfigureAwait(false);

            if (total > 0)
            {
                var skippedIds = await _skippedStore.GetAllAsync().ConfigureAwait(false);
                if (skippedIds.Count > 0)
                {
                    await session.ReportLogAsync(
                        ProcessingLogLevel.Information,
                        $"Skipping {skippedIds.Count} previously unresolvable assets.",
                        cancellationToken).ConfigureAwait(false);
                }

                var config = await _configuration.GetConfigAsync().ConfigureAwait(false);
                var cursor = AssetCursor.Initial;
                var batchNumber = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = await _assets.GetUnprocessedBatchAsync(
                        cursor,
                        config.Processing.BatchSize,
                        cancellationToken).ConfigureAwait(false);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    batchNumber++;
                    await session.ReportLogAsync(
                        ProcessingLogLevel.Information,
                        $"Batch {batchNumber}: fetched {batch.Count} assets (total processed so far: {Volatile.Read(ref updated)}).",
                        cancellationToken).ConfigureAwait(false);
                    cursor = new AssetCursor(batch[^1].CreatedAt, batch[^1].Id);

                    await Parallel.ForEachAsync(
                        batch,
                        new ParallelOptions
                        {
                            CancellationToken = cancellationToken,
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
                                    () => Interlocked.Increment(ref updated),
                                    () => Interlocked.Increment(ref skipped),
                                    () => Interlocked.Increment(ref failed),
                                    token).ConfigureAwait(false);
                            }
                        }).ConfigureAwait(false);

                    if (config.Processing.BatchDelayMs > 0)
                    {
                        await _delay.DelayAsync(
                            TimeSpan.FromMilliseconds(config.Processing.BatchDelayMs),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = ProcessingRunOutcome.Cancelled;
        }
        catch (Exception) when (reporting.HasFailure)
        {
            reporting.ThrowFirstFailure();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error during processing run");
            outcome = ProcessingRunOutcome.Failed;
            failureMessage = ex.Message;
        }

        var result = new ProcessingRunResult(
            request,
            startedAtUtc,
            UtcNow(),
            checked(updated + skipped + failed),
            updated,
            skipped,
            failed,
            outcome,
            failureMessage);
        await session.FinishAsync(result).ConfigureAwait(false);
        return result;
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
            if (resolution is null)
            {
                await reportAsync(
                    () => session.ReportLogAsync(
                        ProcessingLogLevel.Warning,
                        $"Asset {asset.Id}: no country found at ({asset.Latitude:F4}, {asset.Longitude:F4}), skipping.",
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
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
