using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class ProcessingBackgroundService : BackgroundService
{
    private readonly ILogger<ProcessingBackgroundService> _logger;
    private readonly ProcessingState _state;
    private readonly Func<Task> _initialiseSkippedAssetsAsync;
    private readonly ProcessingScheduleLoop _scheduleLoop;
    private readonly IScheduledRunTrigger _scheduledRunTrigger;

    internal ProcessingBackgroundService(
        ILogger<ProcessingBackgroundService> logger,
        ProcessingState state,
        IProcessingScheduleConfiguration configuration,
        SkippedAssetsRepository skipped,
        TimeProvider timeProvider,
        IScheduledRunTrigger scheduledRunTrigger)
        : this(logger, state, configuration, skipped.InitialiseAsync, timeProvider, scheduledRunTrigger)
    {
    }

    internal ProcessingBackgroundService(
        ILogger<ProcessingBackgroundService> logger,
        ProcessingState state,
        IProcessingScheduleConfiguration configuration,
        Func<Task> initialiseSkippedAssetsAsync,
        TimeProvider timeProvider,
        IScheduledRunTrigger scheduledRunTrigger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(initialiseSkippedAssetsAsync);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(scheduledRunTrigger);

        _logger = logger;
        _state = state;
        _initialiseSkippedAssetsAsync = initialiseSkippedAssetsAsync;
        _scheduledRunTrigger = scheduledRunTrigger;
        _scheduleLoop = new ProcessingScheduleLoop(configuration, timeProvider, state.AppendLog, scheduledRunTrigger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("ProcessingBackgroundService: initialising skipped-assets db");
        await _initialiseSkippedAssetsAsync().ConfigureAwait(false);
        _logger.LogInformation("ProcessingBackgroundService: skipped-assets db ready in {Elapsed}ms", sw.ElapsedMilliseconds);
        _state.AppendLog("Service started. Waiting for next scheduled run.");
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
        await _scheduledRunTrigger.TriggerScheduledAsync(stoppingToken).ConfigureAwait(false);
    }

    internal Task<ScheduledTriggerResult> TriggerScheduledAsync(CancellationToken stoppingToken)
    {
        return _scheduledRunTrigger.TriggerScheduledAsync(stoppingToken);
    }
}
