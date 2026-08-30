using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class ProcessingBackgroundService : BackgroundService
{
    private readonly ILogger<ProcessingBackgroundService> logger;
    private readonly ProcessingState state;
    private readonly ProcessingStateEventReporter _reporter;
    private readonly ProcessingOperations _operations;
    private readonly Func<Task> _initialiseSkippedAssetsAsync;
    private CancellationTokenSource? _runCts;
    private Task? _manualRunTask;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public ProcessingBackgroundService(
        ILogger<ProcessingBackgroundService> logger,
        ConfigService config,
        AdministrativeAreaResolverService administrativeResolver,
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        ImmichDbRepository db,
        OverturePlacesService overturePlaces,
        SkippedAssetsRepository skipped)
    {
        this.logger = logger;
        this.state = state;
        _reporter = reporter;
        _initialiseSkippedAssetsAsync = skipped.InitialiseAsync;
        _operations = new ProcessingOperations(
            db.GetUnprocessedCountAsync,
            config.GetConfigAsync,
            skipped.GetAllAsync,
            db.GetUnprocessedBatchAsync,
            administrativeResolver.ResolveAsync,
            overturePlaces.FindNearestInfrastructureWithDiagnosticsAsync,
            skipped.AddAsync,
            db.WriteLocationAsync);
    }

    internal ProcessingBackgroundService(
        ILogger<ProcessingBackgroundService> logger,
        ProcessingState state,
        ProcessingOperations operations)
        : this(logger, state, new ProcessingStateEventReporter(state), operations)
    {
    }

    internal ProcessingBackgroundService(
        ILogger<ProcessingBackgroundService> logger,
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        ProcessingOperations operations)
    {
        this.logger = logger;
        this.state = state;
        _reporter = reporter;
        _operations = operations;
        _initialiseSkippedAssetsAsync = () => Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("ProcessingBackgroundService: initialising skipped-assets db");
        await _initialiseSkippedAssetsAsync();
        logger.LogInformation("ProcessingBackgroundService: skipped-assets db ready in {Elapsed}ms", sw.ElapsedMilliseconds);
        state.AppendLog("Service started. Waiting for next scheduled run.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = await _operations.GetConfigAsync();
            if (!cfg.Schedule.Enabled)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                continue;
            }

            var next = GetNextOccurrence(cfg.Schedule.Cron);
            if (next is null)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            var delay = next.Value - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                state.AppendLog($"Next run scheduled at {next.Value:u}");
                await Task.Delay(delay, stoppingToken);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await TryRunScheduledAsync(stoppingToken);
            }
        }
    }

    internal async Task TryRunScheduledAsync(CancellationToken stoppingToken)
    {
        if (await _runLock.WaitAsync(0, stoppingToken))
        {
            try
            {
                state.MarkPending();
                var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Scheduled);
                if (!_reporter.Arm(request))
                {
                    state.ClearPending();
                    throw new InvalidOperationException("Processing event reporter is already armed.");
                }

                await RunOnceAsync(request, stoppingToken);
            }
            catch
            {
                TryClearPending(state);
                throw;
            }
            finally
            {
                _runLock.Release();
            }
        }
        else
        {
            state.AppendLog("Scheduled run skipped because a processing pass is already in progress.");
        }
    }

    /// <summary>Triggered by "Run Now" button in the Blazor UI.</summary>
    public Task TriggerRunAsync()
    {
        // Acquire the lock synchronously (non-blocking). If it's already held by a running
        // or recently-triggered run, bail out immediately — no silent race possible.
        if (!_runLock.Wait(0))
        {
            return Task.CompletedTask;
        }

        try
        {
            // Mark as pending right now so the UI disables the button on this render cycle,
            // before the background Task.Run has had a chance to call state.StartRun().
            state.MarkPending();
            var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
            if (!_reporter.Arm(request))
            {
                throw new InvalidOperationException("Processing event reporter is already armed.");
            }

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;

            var manualRunTask = Task.Run(async () =>
            {
                try { await RunOnceAsync(request, token); }
                finally { _runLock.Release(); }
            });
            _manualRunTask = manualRunTask;
            _ = manualRunTask.ContinueWith(t => logger.LogError(t.Exception, "TriggerRunAsync faulted"),
                                           TaskContinuationOptions.OnlyOnFaulted);

            return Task.CompletedTask;
        }
        catch
        {
            TryClearPending(state);
            _runLock.Release();
            throw;
        }
    }

    internal Task WaitForManualAdmissionAsync()
    {
        return _manualRunTask ?? Task.CompletedTask;
    }

    public void CancelRun() => _runCts?.Cancel();

    private Task RunOnceAsync(ProcessingRunRequest request, CancellationToken ct)
    {
        return RunOnceAsync(logger, state, _reporter, request, _operations, ct);
    }

    internal static Task RunOnceAsync(
        ILogger<ProcessingBackgroundService> logger,
        ProcessingState state,
        ProcessingOperations operations,
        CancellationToken ct)
    {
        var reporter = new ProcessingStateEventReporter(state);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        reporter.Arm(request);
        return RunOnceAsync(logger, state, reporter, request, operations, ct);
    }

    internal static async Task RunOnceAsync(
        ILogger<ProcessingBackgroundService> logger,
        ProcessingState state,
        IProcessingEventReporter reporter,
        ProcessingRunRequest request,
        ProcessingOperations operations,
        CancellationToken ct)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        long updated = 0;
        long skipped = 0;
        long failed = 0;
        var outcome = ProcessingRunOutcome.Completed;
        string? failureMessage = null;
        var reportingFailed = 0;
        void AbandonReporting(Exception failure)
        {
            Interlocked.Exchange(ref reportingFailed, 1);
            logger.LogError(failure, "Processing event reporting failed for run {RunId}", request.RunId);
            if (reporter is ProcessingStateEventReporter stateReporter)
            {
                stateReporter.Abandon(request, failure);
            }
        }

        // Publish the session even when the active execution token was already cancelled,
        // so the admitted pending run can project its terminal outcome.
        IProcessingRunEventSession session;
        try
        {
            session = await reporter.OpenRunAsync(request, startedAtUtc, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AbandonReporting(ex);
            throw;
        }

        try
        {
            var total = await operations.GetUnprocessedCountAsync(ct);
            await ReportAsync(() => session.DetermineEligibilityAsync(total, ct), AbandonReporting);
            if (total == 0)
            {
                return;
            }

            var skippedIds = await operations.GetSkippedAssetsAsync();
            if (skippedIds.Count > 0)
            {
                await ReportAsync(() => session.ReportLogAsync(ProcessingLogLevel.Information, $"Skipping {skippedIds.Count} previously unresolvable assets.", ct), AbandonReporting);
            }

            var cursor = AssetCursor.Initial;
            var cfg = await operations.GetConfigAsync();
            var batchNum = 0;
            while (!ct.IsCancellationRequested)
            {
                var batch = await operations.GetUnprocessedBatchAsync(cursor, cfg.Processing.BatchSize, ct);
                if (batch.Count == 0)
                {
                    break;
                }

                batchNum++;
                await ReportAsync(() => session.ReportLogAsync(ProcessingLogLevel.Information, $"Batch {batchNum}: fetched {batch.Count} assets (total processed so far: {Volatile.Read(ref updated)}).", ct), AbandonReporting);
                cursor = new AssetCursor(batch[^1].CreatedAt, batch[^1].Id);
                var maxParallelism = Math.Clamp(cfg.Processing.MaxDegreeOfParallelism, 1, 32);
                await Parallel.ForEachAsync(batch, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = maxParallelism }, async (asset, token) =>
                {
                    if (!skippedIds.Contains(asset.Id))
                    {
                        await ProcessAssetAsync(logger, state, session, operations, asset, cfg,
                            () => Interlocked.Increment(ref updated),
                            () => Interlocked.Increment(ref skipped),
                            () => Interlocked.Increment(ref failed), AbandonReporting, token);
                    }
                });

                if (cfg.Processing.BatchDelayMs > 0)
                {
                    await Task.Delay(cfg.Processing.BatchDelayMs, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = ProcessingRunOutcome.Cancelled;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception ex) when (Volatile.Read(ref reportingFailed) == 0)
        {
            logger.LogError(ex, "Fatal error during processing run");
            outcome = ProcessingRunOutcome.Failed;
            failureMessage = ex.Message;
        }
        finally
        {
            if (Volatile.Read(ref reportingFailed) == 0)
            {
                await ReportAsync(() => session.FinishAsync(new ProcessingRunResult(request, startedAtUtc, DateTimeOffset.UtcNow,
                    checked(updated + skipped + failed), updated, skipped, failed, outcome, failureMessage)), AbandonReporting);
            }
        }
    }

    internal static bool IsLoggerOnlyNoCitySkip(GeoResult geoResult)
    {
        return geoResult.HasMatch && geoResult.City is null;
    }

    private static async Task ProcessAssetAsync(
        ILogger<ProcessingBackgroundService> logger,
        ProcessingState state,
        IProcessingRunEventSession session,
        ProcessingOperations operations,
        AssetRecord asset,
        AppConfig cfg,
        Action updated,
        Action skipped,
        Action failed,
        Action<Exception> reportingFailure,
        CancellationToken ct)
    {
        var step = "FindCountry";
        var infrastructureFailed = false;
        void ReportFailure(Exception failure)
        {
            infrastructureFailed = true;
            reportingFailure(failure);
        }

        try
        {
            // 1. Country detection — bundled Overture country divisions only.
            var adminResolution = await operations.ResolveAdministrativeAreaAsync(
                asset.Latitude,
                asset.Longitude,
                cfg.Processing,
                session,
                ct);

            if (adminResolution is null)
            {
                await ReportAsync(() => session.ReportLogAsync(ProcessingLogLevel.Warning, $"Asset {asset.Id}: no country found at ({asset.Latitude:F4}, {asset.Longitude:F4}), skipping.", ct), ReportFailure);
                await operations.AddSkippedAssetAsync(asset.Id);
                await ReportAsync(session.ReportSkippedAsync, ReportFailure);
                skipped();
                return;
            }

            step = "FindAdminLevels";
            var iso3 = adminResolution.Iso3;
            var countryName = adminResolution.CountryName;
            var geoResult = adminResolution.GeoResult;

            if (cfg.Processing.UseAirportInfrastructure)
            {
                // 2. Transport lookup — bundled Overture airport infrastructure.
                step = "FindNearestInfrastructure";
                var infrastructure = await operations.FindNearestInfrastructureAsync(
                    asset.Latitude,
                    asset.Longitude,
                    iso3,
                    ct);

                if (infrastructure.BestMatch?.GeometryContainsPoint == true)
                {
                    geoResult = geoResult with { City = infrastructure.BestMatch.Name };
                }
                else if (geoResult.City is null && infrastructure.BestMatch is not null)
                {
                    geoResult = geoResult with { City = infrastructure.BestMatch.Name };
                }
            }

            // 3. City fallback — prefer city, then state, then country for country-only microstates.
            geoResult = geoResult.WithFallbackCity();

            // 4. Write back (only if we have country AND city)
            step = "WriteLocation";
            if (IsLoggerOnlyNoCitySkip(geoResult))
            {
                // Don't write partial data to Immich — a write with city=NULL would
                // satisfy the "country IS NULL" filter and prevent the asset from being
                // reprocessed, permanently losing the city. Log for investigation and
                // leave the asset unprocessed so it is retried on the next run.
                logger.LogWarning(
                    "Asset {AssetId}: country={Country} state={State} resolved but no city — skipping write (lat={Lat:F4}, lon={Lon:F4})",
                    asset.Id, geoResult.Country, geoResult.State, asset.Latitude, asset.Longitude);
                await ReportAsync(session.ReportSkippedAsync, ReportFailure);
                skipped();
                return;
            }

            if (geoResult.HasMatch)
            {
                if (cfg.Processing.VerboseLogging)
                {
                    await ReportAsync(() => session.ReportLogAsync(ProcessingLogLevel.Trace, $"Asset {asset.Id}: {geoResult.City}, {geoResult.State}, {geoResult.Country}", ct), ReportFailure);
                }
                else
                {
                    logger.LogDebug("Asset {AssetId}: {City}, {State}, {Country}",
                        asset.Id, geoResult.City, geoResult.State, geoResult.Country);
                }
                await operations.WriteLocationAsync(asset.Id, geoResult, ct);
                await ReportAsync(session.ReportUpdatedAsync, ReportFailure);
                updated();
            }
            else
            {
                await ReportAsync(() => session.ReportLogAsync(ProcessingLogLevel.Warning, $"Asset {asset.Id}: country={countryName} but no admin match, skipping.", ct), ReportFailure);
                await operations.AddSkippedAssetAsync(asset.Id);
                await ReportAsync(session.ReportSkippedAsync, ReportFailure);
                skipped();
            }
        }
        catch (ProcessingEventReportingException ex)
        {
            ReportFailure(ex.ReporterException);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.ReporterException).Throw();
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogDebug(
                "Processing cancelled at step={Step} for asset {AssetId}",
                step,
                asset.Id);
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception ex) when (!infrastructureFailed)
        {
            // Log full exception including type and stack trace so we can pinpoint the source
            logger.LogError(ex, "Error at step={Step} for asset {AssetId} [{ExType}]",
                step, asset.Id, ex.GetType().Name);
            await ReportAsync(() => session.ReportLogAsync(ProcessingLogLevel.Error, $"Asset {asset.Id} [{step}]: {ex.Message}"), ReportFailure);
            await ReportAsync(session.ReportFailedAsync, ReportFailure);
            failed();
        }
    }

    private static async ValueTask ReportAsync(Func<ValueTask> report, Action<Exception> reportingFailure)
    {
        try
        {
            await report();
        }
        catch (Exception ex)
        {
            reportingFailure(ex);
            throw;
        }
    }

    private static void TryClearPending(ProcessingState processingState)
    {
        try
        {
            processingState.ClearPending();
        }
        catch
        {
            // ClearPending commits before notifying; admission cleanup must still release its lock.
        }
    }

    private static DateTime? GetNextOccurrence(string cron)
    {
        try
        {
            var expr = CronExpression.Parse(cron, CronFormat.Standard);
            return expr.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);
        }
        catch { return null; }
    }

    internal sealed record ProcessingOperations(
        Func<CancellationToken, Task<long>> GetUnprocessedCountAsync,
        Func<Task<AppConfig>> GetConfigAsync,
        Func<Task<HashSet<Guid>>> GetSkippedAssetsAsync,
        Func<AssetCursor, int, CancellationToken, Task<List<AssetRecord>>> GetUnprocessedBatchAsync,
        Func<double, double, ProcessingConfig, IProcessingRunEventSession, CancellationToken, Task<AdministrativeAreaResolution?>> ResolveAdministrativeAreaAsync,
        Func<double, double, string?, CancellationToken, Task<OvertureInfrastructureLookupDiagnostics>> FindNearestInfrastructureAsync,
        Func<Guid, Task> AddSkippedAssetAsync,
        Func<Guid, GeoResult, CancellationToken, Task> WriteLocationAsync);

}
