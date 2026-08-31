using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

internal sealed class ProcessingRunCoordinatorTestHost
{
    private readonly ProcessingRunCoordinator _coordinator;

    public ProcessingRunCoordinatorTestHost(
        ILogger<ProcessingBackgroundService> logger,
        ProcessingState state,
        ProcessingRunExecution.ProcessingOperations operations)
        : this(logger, state, new ProcessingStateEventReporter(state), operations)
    {
    }

    public ProcessingRunCoordinatorTestHost(
        ILogger<ProcessingBackgroundService> logger,
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        ProcessingRunExecution.ProcessingOperations operations)
        : this(
            state,
            reporter,
            new ProcessingRunExecutor(logger, operations, operations, operations, operations, operations, operations, TimeProvider.System))
    {
    }

    public ProcessingRunCoordinatorTestHost(
        ILogger<ProcessingBackgroundService> logger,
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        IProcessingRunExecutor executor,
        IProcessingScheduleConfiguration configuration)
        : this(state, reporter, executor)
    {
    }

    private ProcessingRunCoordinatorTestHost(
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        IProcessingRunExecutor executor)
    {
        _coordinator = new ProcessingRunCoordinator(
            state,
            reporter,
            executor,
            NullLogger<ProcessingRunCoordinator>.Instance,
            Guid.NewGuid);
    }

    public async Task TriggerRunAsync()
    {
        await _coordinator.TriggerManualAsync().ConfigureAwait(false);
    }

    public void CancelRun()
    {
        _coordinator.CancelActiveRun();
    }

    public Task WaitForManualAdmissionAsync()
    {
        return _coordinator.WaitForActiveRunAsync();
    }

    public async Task TryRunScheduledAsync(CancellationToken stoppingToken)
    {
        await ((IScheduledRunTrigger)_coordinator).TriggerScheduledAsync(stoppingToken).ConfigureAwait(false);
    }
}
