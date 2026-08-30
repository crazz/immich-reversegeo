using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Overture.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class ProcessingBackgroundService : BackgroundService, IScheduledRunTrigger
{
    private readonly ILogger<ProcessingBackgroundService> logger;
    private readonly ProcessingState state;
    private readonly ProcessingStateEventReporter _reporter;
    private readonly IProcessingRunExecutor _executor;
    private readonly Func<Task> _initialiseSkippedAssetsAsync;
    private readonly ProcessingScheduleLoop _scheduleLoop;
    private CancellationTokenSource? _runCts;
    private Task? _manualRunTask;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public ProcessingBackgroundService(ILogger<ProcessingBackgroundService> logger, ProcessingState state, ProcessingStateEventReporter reporter, IProcessingRunExecutor executor, IProcessingScheduleConfiguration configuration, SkippedAssetsRepository skipped, TimeProvider timeProvider)
    {
        this.logger = logger; this.state = state; _reporter = reporter; _executor = executor; _initialiseSkippedAssetsAsync = skipped.InitialiseAsync; _scheduleLoop = new ProcessingScheduleLoop(configuration, timeProvider, state.AppendLog, this);
    }

    internal ProcessingBackgroundService(ILogger<ProcessingBackgroundService> logger, ProcessingState state, ProcessingOperations operations)
        : this(logger, state, new ProcessingStateEventReporter(state), operations) { }

    internal ProcessingBackgroundService(ILogger<ProcessingBackgroundService> logger, ProcessingState state, ProcessingStateEventReporter reporter, ProcessingOperations operations)
    {
        this.logger = logger; this.state = state; _reporter = reporter; _executor = new ProcessingRunExecutor(logger, operations, operations, operations, operations, operations, operations, TimeProvider.System); _initialiseSkippedAssetsAsync = () => Task.CompletedTask; _scheduleLoop = new ProcessingScheduleLoop(operations, TimeProvider.System, state.AppendLog, this);
    }

    internal ProcessingBackgroundService(ILogger<ProcessingBackgroundService> logger, ProcessingState state, ProcessingStateEventReporter reporter, IProcessingRunExecutor executor, IProcessingScheduleConfiguration configuration)
        : this(logger, state, reporter, executor, configuration, () => Task.CompletedTask, TimeProvider.System)
    {
    }

    internal ProcessingBackgroundService(ILogger<ProcessingBackgroundService> logger, ProcessingState state, ProcessingStateEventReporter reporter, IProcessingRunExecutor executor, IProcessingScheduleConfiguration configuration, Func<Task> initialiseSkippedAssetsAsync, TimeProvider timeProvider)
    {
        this.logger = logger; this.state = state; _reporter = reporter; _executor = executor; _initialiseSkippedAssetsAsync = initialiseSkippedAssetsAsync; _scheduleLoop = new ProcessingScheduleLoop(configuration, timeProvider, state.AppendLog, this);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation("ProcessingBackgroundService: initialising skipped-assets db");
        await _initialiseSkippedAssetsAsync().ConfigureAwait(false);
        logger.LogInformation("ProcessingBackgroundService: skipped-assets db ready in {Elapsed}ms", sw.ElapsedMilliseconds);
        state.AppendLog("Service started. Waiting for next scheduled run.");
        try
        {
            await _scheduleLoop.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested && ex.CancellationToken == stoppingToken)
        {
        }
    }

    internal async Task TryRunScheduledAsync(CancellationToken stoppingToken)
    {
        await TriggerScheduledAsync(stoppingToken).ConfigureAwait(false);
    }

    async Task<ScheduledTriggerResult> IScheduledRunTrigger.TriggerScheduledAsync(CancellationToken stoppingToken)
    {
        return await TriggerScheduledAsync(stoppingToken).ConfigureAwait(false);
    }

    internal async Task<ScheduledTriggerResult> TriggerScheduledAsync(CancellationToken stoppingToken)
    {
        if (await _runLock.WaitAsync(0, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                state.MarkPending();
                var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Scheduled);
                if (!_reporter.Arm(request)) { state.ClearPending(); throw new InvalidOperationException("Processing event reporter is already armed."); }
                await RunOnceAsync(request, stoppingToken).ConfigureAwait(false);
                return ScheduledTriggerResult.AcceptedAfterTerminal;
            }
            catch { TryClearPending(state); throw; }
            finally { _runLock.Release(); }
        }

        state.AppendLog("Scheduled run skipped because a processing pass is already in progress.");
        return ScheduledTriggerResult.RejectedAlreadyRunning;
    }

    public Task TriggerRunAsync()
    {
        if (!_runLock.Wait(0)) { return Task.CompletedTask; }
        try
        {
            state.MarkPending();
            var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
            if (!_reporter.Arm(request)) { throw new InvalidOperationException("Processing event reporter is already armed."); }
            _runCts?.Dispose(); _runCts = new CancellationTokenSource(); var token = _runCts.Token;
            var manualRunTask = Task.Run(async () => { try { await RunOnceAsync(request, token).ConfigureAwait(false); } finally { _runLock.Release(); } });
            _manualRunTask = manualRunTask;
            _ = manualRunTask.ContinueWith(t => logger.LogError(t.Exception, "TriggerRunAsync faulted"), TaskContinuationOptions.OnlyOnFaulted);
            return Task.CompletedTask;
        }
        catch { TryClearPending(state); _runLock.Release(); throw; }
    }

    internal Task WaitForManualAdmissionAsync() => _manualRunTask ?? Task.CompletedTask;
    public void CancelRun() => _runCts?.Cancel();

    private async Task RunOnceAsync(ProcessingRunRequest request, CancellationToken cancellationToken)
    {
        try { await _executor.ExecuteAsync(request, _reporter, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _reporter.Abandon(request, ex); throw; }
    }

    internal static async Task RunOnceAsync(ILogger<ProcessingBackgroundService> logger, ProcessingState state, ProcessingOperations operations, CancellationToken cancellationToken)
    {
        var reporter = new ProcessingStateEventReporter(state); var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce); reporter.Arm(request);
        await RunOnceAsync(logger, state, reporter, request, operations, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RunOnceAsync(ILogger<ProcessingBackgroundService> logger, ProcessingState state, IProcessingEventReporter reporter, ProcessingRunRequest request, ProcessingOperations operations, CancellationToken cancellationToken)
    {
        var executor = new ProcessingRunExecutor(logger, operations, operations, operations, operations, operations, operations, TimeProvider.System);
        try
        {
            await executor.ExecuteAsync(request, reporter, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (reporter is ProcessingStateEventReporter stateReporter)
            {
                stateReporter.Abandon(request, ex);
            }

            throw;
        }
    }

    internal static bool IsLoggerOnlyNoCitySkip(GeoResult geoResult) => ProcessingRunExecutor.IsLoggerOnlyNoCitySkip(geoResult);

    private static void TryClearPending(ProcessingState processingState)
    {
        try { processingState.ClearPending(); } catch { }
    }


    internal sealed class ProcessingOperations(
        Func<CancellationToken, Task<long>> count, Func<Task<AppConfig>> config, Func<Task<HashSet<Guid>>> getSkipped,
        Func<AssetCursor, int, CancellationToken, Task<List<AssetRecord>>> getBatch,
        Func<double, double, ProcessingConfig, IProcessingRunEventSession, CancellationToken, Task<AdministrativeAreaResolution?>> resolve,
        Func<double, double, string?, CancellationToken, Task<OvertureInfrastructureLookupDiagnostics>> infrastructure,
        Func<Guid, Task> addSkipped, Func<Guid, GeoResult, CancellationToken, Task> write)
        : IProcessingRunConfiguration, IProcessingScheduleConfiguration, IProcessingAssetRepository, IProcessingSkippedStore, IProcessingAdministrativeResolver, IProcessingInfrastructureLookup, IProcessingRunDelay
    {
        public Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default) => count(cancellationToken);
        public Task<AppConfig> GetConfigAsync() => config();
        async Task<ProcessingScheduleSnapshot> IProcessingScheduleConfiguration.GetSnapshotAsync()
        {
            var appConfig = await config().ConfigureAwait(false);
            return new ProcessingScheduleSnapshot(appConfig.Schedule.Enabled, appConfig.Schedule.Cron);
        }
        public Task<HashSet<Guid>> GetAllAsync() => getSkipped();
        public Task<List<AssetRecord>> GetUnprocessedBatchAsync(AssetCursor cursor, int batchSize, CancellationToken cancellationToken = default) => getBatch(cursor, batchSize, cancellationToken);
        public Task<AdministrativeAreaResolution?> ResolveAsync(double latitude, double longitude, ProcessingConfig processingConfig, IProcessingRunEventSession session, CancellationToken cancellationToken = default) => resolve(latitude, longitude, processingConfig, session, cancellationToken);
        public Task<OvertureInfrastructureLookupDiagnostics> FindNearestInfrastructureAsync(double latitude, double longitude, string? iso3, CancellationToken cancellationToken = default) => infrastructure(latitude, longitude, iso3, cancellationToken);
        public Task AddAsync(Guid assetId) => addSkipped(assetId);
        public Task WriteLocationAsync(Guid assetId, GeoResult geoResult, CancellationToken cancellationToken = default) => write(assetId, geoResult, cancellationToken);
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }
}
