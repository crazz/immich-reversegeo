using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using WorkerStateBridge = ImmichReverseGeo.Web.WorkerEventStateBridge.WorkerEventStateBridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public enum ProcessingRunAdmissionResult
{
    Accepted,
    AlreadyRunning,
    Stopping
}

public interface IManualProcessingRunCoordinator
{
    Task<ProcessingRunAdmissionResult> TriggerManualAsync();
    Task? StopActiveRun();
    bool CancelActiveRun();
}

internal interface IProcessingRunCancellation : IDisposable
{
    CancellationToken Token { get; }
    void Cancel();
}

internal interface IProcessingRunCancellationFactory
{
    IProcessingRunCancellation Create(ProcessingRunRequest request, CancellationToken linkedToken);
}

internal enum ProcessingRunAdmissionAttempt
{
    Manual,
    Scheduled,
    Stop
}

internal interface IProcessingRunCoordinatorObserver
{
    ValueTask BeforeAdmissionGateAsync(ProcessingRunAdmissionAttempt attempt) => ValueTask.CompletedTask;
    ValueTask BeforeChildDispatchClaimAsync(ProcessingRunRequest request) => ValueTask.CompletedTask;
    void CoordinatorStarted() { }
    void CoordinatorStopping() { }
    void CoordinatorStopped() { }
    void BeforeRequestCancellation(ProcessingRunRequest request) { }
    void AfterRequestCancellation(ProcessingRunRequest request) { }
    ValueTask BeforeChildSettlementAsync(ProcessingRunRequest request, CancellationToken activeToken) => ValueTask.CompletedTask;
    ValueTask BeforeDetachAsync(ProcessingRunRequest request, CancellationToken activeToken) => ValueTask.CompletedTask;
    ValueTask BeforeDisposeAsync(ProcessingRunRequest request, CancellationToken activeToken) => ValueTask.CompletedTask;
}

public sealed class ProcessingRunCoordinator : IManualProcessingRunCoordinator, IScheduledRunTrigger, IHostedService, IDisposable, IAsyncDisposable
{
    private const string ScheduledWorkDetectionFailureMessage = "Scheduled work detection failed.";

    private readonly object _admissionGate = new();
    private readonly ProcessingState _state;
    private readonly ProcessingStateEventReporter _reporter;
    private readonly IProcessingWorkDetector? _scheduledRunWorkGate;
    private readonly IServiceScopeFactory _childBackendScopeFactory;
    private readonly ILogger<ProcessingRunCoordinator> _logger;
    private readonly Func<Guid> _createRunId;
    private readonly IProcessingRunCancellationFactory _cancellationFactory;
    private readonly IProcessingRunCoordinatorObserver? _observer;
    private readonly TimeProvider _timeProvider;
    private readonly WorkerJobCoordinator _workerCoordinator;
    private readonly TaskCompletionSource _applicationStoppingRegistrationReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _scheduledPreflightStopping = new();
    private readonly CancellationTokenRegistration _applicationStoppingRegistration;
    private TaskCompletionSource _scheduledPreflightDrained = CompletedSignal();
    private int _scheduledPreflightCount;
    private ActiveRun? _active;
    private Task? _shutdownTask;
    private bool _admissionOpen = true;

    internal ProcessingRunCoordinator(
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        IProcessingWorkDetector scheduledRunWorkGate,
        IServiceScopeFactory childBackendScopeFactory,
        ILogger<ProcessingRunCoordinator> logger,
        WorkerJobCoordinator workerCoordinator,
        IHostApplicationLifetime? applicationLifetime = null,
        TimeProvider? timeProvider = null)
        : this(
            state,
            reporter,
            scheduledRunWorkGate,
            childBackendScopeFactory,
            logger,
            Guid.NewGuid,
            new ProcessingRunCancellationFactory(),
            null,
            workerCoordinator,
            applicationLifetime,
            timeProvider)
    {
    }

    internal ProcessingRunCoordinator(
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        IProcessingWorkDetector scheduledRunWorkGate,
        IServiceScopeFactory childBackendScopeFactory,
        ILogger<ProcessingRunCoordinator> logger,
        Func<Guid> createRunId,
        WorkerJobCoordinator workerCoordinator,
        IHostApplicationLifetime? applicationLifetime = null,
        TimeProvider? timeProvider = null)
        : this(
            state,
            reporter,
            scheduledRunWorkGate,
            childBackendScopeFactory,
            logger,
            createRunId,
            new ProcessingRunCancellationFactory(),
            null,
            workerCoordinator,
            applicationLifetime,
            timeProvider)
    {
    }

    internal ProcessingRunCoordinator(
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        IProcessingWorkDetector scheduledRunWorkGate,
        IServiceScopeFactory childBackendScopeFactory,
        ILogger<ProcessingRunCoordinator> logger,
        Func<Guid> createRunId,
        IProcessingRunCoordinatorObserver? observer,
        WorkerJobCoordinator workerCoordinator,
        IHostApplicationLifetime? applicationLifetime = null,
        TimeProvider? timeProvider = null)
        : this(
            state,
            reporter,
            scheduledRunWorkGate,
            childBackendScopeFactory,
            logger,
            createRunId,
            new ProcessingRunCancellationFactory(),
            observer,
            workerCoordinator,
            applicationLifetime,
            timeProvider)
    {
    }

    internal ProcessingRunCoordinator(
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        IProcessingWorkDetector scheduledRunWorkGate,
        IServiceScopeFactory childBackendScopeFactory,
        ILogger<ProcessingRunCoordinator> logger,
        Func<Guid> createRunId,
        IProcessingRunCancellationFactory cancellationFactory,
        IProcessingRunCoordinatorObserver? observer,
        WorkerJobCoordinator workerCoordinator,
        IHostApplicationLifetime? applicationLifetime = null,
        TimeProvider? timeProvider = null)
        : this(
            state,
            reporter,
            scheduledRunWorkGate,
            childBackendScopeFactory,
            logger,
            createRunId,
            cancellationFactory,
            observer,
            workerCoordinator,
            applicationLifetime,
            timeProvider,
            scheduledRunsSupported: true)
    {
    }

    private ProcessingRunCoordinator(
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        IProcessingWorkDetector? scheduledRunWorkGate,
        IServiceScopeFactory childBackendScopeFactory,
        ILogger<ProcessingRunCoordinator> logger,
        Func<Guid> createRunId,
        IProcessingRunCancellationFactory cancellationFactory,
        IProcessingRunCoordinatorObserver? observer,
        WorkerJobCoordinator workerCoordinator,
        IHostApplicationLifetime? applicationLifetime,
        TimeProvider? timeProvider,
        bool scheduledRunsSupported)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(reporter);
        if (scheduledRunsSupported)
        {
            ArgumentNullException.ThrowIfNull(scheduledRunWorkGate);
        }
        else if (scheduledRunWorkGate is not null)
        {
            throw new ArgumentException(
                "A manual-only coordinator cannot use a scheduled work gate.",
                nameof(scheduledRunWorkGate));
        }
        ArgumentNullException.ThrowIfNull(childBackendScopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(createRunId);
        ArgumentNullException.ThrowIfNull(cancellationFactory);
        ArgumentNullException.ThrowIfNull(workerCoordinator);

        _state = state;
        _reporter = reporter;
        _scheduledRunWorkGate = scheduledRunWorkGate;
        _childBackendScopeFactory = childBackendScopeFactory;
        _logger = logger;
        _createRunId = createRunId;
        _cancellationFactory = cancellationFactory;
        _observer = observer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _workerCoordinator = workerCoordinator;
        try
        {
            _applicationStoppingRegistration = applicationLifetime?.ApplicationStopping.Register(BeginShutdownFromApplicationStopping)
                ?? default;
        }
        finally
        {
            _applicationStoppingRegistrationReady.TrySetResult();
        }
    }

    internal static ProcessingRunCoordinator CreateManualOnly(
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        IServiceScopeFactory childBackendScopeFactory,
        ILogger<ProcessingRunCoordinator> logger,
        Func<Guid> createRunId,
        IProcessingRunCoordinatorObserver? observer,
        WorkerJobCoordinator workerCoordinator,
        IHostApplicationLifetime? applicationLifetime = null,
        TimeProvider? timeProvider = null)
    {
        return new ProcessingRunCoordinator(
            state,
            reporter,
            scheduledRunWorkGate: null,
            childBackendScopeFactory,
            logger,
            createRunId,
            new ProcessingRunCancellationFactory(),
            observer,
            workerCoordinator,
            applicationLifetime,
            timeProvider,
            scheduledRunsSupported: false);
    }

    public async Task<ProcessingRunAdmissionResult> TriggerManualAsync()
    {
        await BeforeAdmissionGateAsync(ProcessingRunAdmissionAttempt.Manual).ConfigureAwait(false);
        var reservation = await ReserveAsync(
            ProcessingRunTrigger.Manual,
            CancellationToken.None).ConfigureAwait(false);
        if (reservation.Result != ProcessingRunAdmissionResult.Accepted)
        {
            return reservation.Result;
        }

        await PrepareAndDispatchAsync(reservation.Handle!).ConfigureAwait(false);
        return ProcessingRunAdmissionResult.Accepted;
    }

    async Task<ScheduledTriggerResult> IScheduledRunTrigger.TriggerScheduledAsync(CancellationToken stoppingToken)
    {
        if (_scheduledRunWorkGate is null)
        {
            throw new InvalidOperationException("Scheduled processing is not available in this deployment mode.");
        }

        CancellationTokenSource? preflight = BeginScheduledPreflight(stoppingToken);
        if (preflight is null)
        {
            return ScheduledTriggerResult.RejectedAlreadyRunning;
        }

        bool hasWork;
        try
        {
            var detectionRequest = new ProcessingWorkDetectionRequest(
                ProcessingRunTrigger.Scheduled,
                ProcessingWorkDetectionSnapshot.Current);
            ProcessingWorkDetectionResult detection = await _scheduledRunWorkGate
                .DetectAsync(detectionRequest, preflight.Token).ConfigureAwait(false);
            hasWork = detection.HasWork;
            preflight.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException cancellation)
            when (preflight.Token.IsCancellationRequested
                && (cancellation.CancellationToken == preflight.Token
                    || cancellation.CancellationToken == stoppingToken))
        {
            EndScheduledPreflight(preflight);
            if (stoppingToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellation.Message, cancellation, stoppingToken);
            }

            throw;
        }
        catch (Exception failure)
        {
            EndScheduledPreflight(preflight);
            _logger.LogError(failure, ScheduledWorkDetectionFailureMessage);
            return ScheduledTriggerResult.AcceptedAfterTerminal;
        }

        EndScheduledPreflight(preflight);
        if (!hasWork)
        {
            _logger.LogInformation("Scheduled run skipped because no eligible work was detected.");
            return ScheduledTriggerResult.AcceptedAfterTerminal;
        }

        await BeforeAdmissionGateAsync(ProcessingRunAdmissionAttempt.Scheduled).ConfigureAwait(false);
        var reservation = await ReserveAsync(
            ProcessingRunTrigger.Scheduled,
            stoppingToken).ConfigureAwait(false);
        if (reservation.Result != ProcessingRunAdmissionResult.Accepted)
        {
            if (reservation.Result == ProcessingRunAdmissionResult.AlreadyRunning)
            {
                string message = reservation.Busy switch
                {
                    ExclusiveHeavyOwnerBusyMetadata.CacheMaintenance =>
                        "Scheduled run skipped because cache maintenance is in progress.",
                    ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance =>
                        "Scheduled run skipped because database maintenance is in progress.",
                    null or ExclusiveHeavyOwnerBusyMetadata.Worker
                    {
                        Job.CapabilityFamily: WorkerJobCapabilityFamily.Processing
                    } => "Scheduled run skipped because a processing pass is already in progress.",
                    _ => "Scheduled run skipped because another background job is already using the heavy worker."
                };
                _state.AppendLog(message);
            }

            return ScheduledTriggerResult.RejectedAlreadyRunning;
        }

        var handle = reservation.Handle!;
        await PrepareAndDispatchAsync(handle).ConfigureAwait(false);

        try
        {
            await handle.CleanupCompleted.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested && ex.CancellationToken == stoppingToken)
        {
            await handle.CleanupCompleted.Task.ConfigureAwait(false);
            handle.ExecutionFailure?.Throw();
            throw;
        }

        handle.ExecutionFailure?.Throw();
        stoppingToken.ThrowIfCancellationRequested();
        return ScheduledTriggerResult.AcceptedAfterTerminal;
    }

    public Task? StopActiveRun()
    {
        return StopActiveRunCore();
    }

    public bool CancelActiveRun()
    {
        ActiveRun? handle;
        ActiveRun.StopClaim claim;
        lock (_admissionGate)
        {
            handle = _active;
            if (handle is null)
            {
                return false;
            }

            claim = handle.ClaimStop(
                _timeProvider,
                ChildWorkerTerminationIntent.Stop,
                trackCancellationDispatch: false);
        }

        PublishFirstStopClaim(handle, claim);
        if (claim.IsFirst)
        {
            StartAttachedTermination(handle);
        }

        RequestCancellation(handle);
        return true;
    }

    internal bool TryClaimChildExecution(
        ProcessingRunRequest request,
        WorkerRunFinalizer finalizer)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(finalizer);

        lock (_admissionGate)
        {
            if (_active is null
                || !ReferenceEquals(_active.Request, request)
                || !ReferenceEquals(finalizer.Request, request)
                || !ReferenceEquals(finalizer.Reporter, _reporter)
                || !ReferenceEquals(finalizer.Clock, _timeProvider))
            {
                return false;
            }

            var claimed = _active.TryClaimChildExecution(finalizer);
            if (claimed)
            {
                finalizer.ObserveAdmission(_active.IsStopRequested);
            }
            return claimed;
        }
    }

    internal bool TryAttachChildSession(
        ProcessingRunRequest request,
        ChildWorkerSession session,
        WorkerStateBridge? bridge = null,
        WorkerRunFinalizer? finalizer = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);

        var claimDefaultFinalizer = bridge is not null && finalizer is null;
        finalizer ??= bridge is null
            ? null
            : new WorkerRunFinalizer(request, _reporter, _timeProvider);
        if ((bridge is null) != (finalizer is null)
            || (bridge is not null
                && (!ReferenceEquals(bridge.Request, request)
                    || !ReferenceEquals(bridge.Reporter, _reporter)))
            || (finalizer is not null
                && (!ReferenceEquals(finalizer.Request, request)
                    || !ReferenceEquals(finalizer.Reporter, _reporter)
                    || !ReferenceEquals(finalizer.Clock, _timeProvider))))
        {
            return false;
        }

        ActiveRun handle;
        ActiveRun.ChildAttachment? attachment;
        lock (_admissionGate)
        {
            if (_active is null
                || !ReferenceEquals(_active.Request, request)
                || session.RunId != request.RunId
                || !ReferenceEquals(session.Request, request)
                || !ReferenceEquals(session.Clock, _timeProvider))
            {
                return false;
            }

            handle = _active;
            if (!handle.TryAttachChildSession(
                    session,
                    bridge,
                    finalizer,
                    claimDefaultFinalizer,
                    out attachment))
            {
                return false;
            }
        }

        handle.Lease.TryAdvance(
            handle.Lease.Context,
            WorkerJobLifecycle.Running,
            session.ProcessId);

        if (attachment!.Finalizer is not null)
        {
            try
            {
                var completion = attachment.Finalizer.Start(
                    session,
                    bridge!,
                    observation => RequestFaultContainment(handle, observation),
                    () => handle.IsShutdownRequested);
                if (!ReferenceEquals(completion, attachment.Finalizer.Completion))
                {
                    throw new InvalidOperationException(
                        "The child finalizer did not return its owned completion task.");
                }

                attachment.FinalizerStarted.TrySetResult();
            }
            catch (Exception failure)
            {
                attachment.FinalizerStarted.TrySetException(failure);
                throw;
            }
        }

        StartAttachedTermination(handle);

        return true;
    }

    private void StartAttachedTermination(ActiveRun handle)
    {
        if (!handle.OwnsFinalization)
        {
            handle.StartAttachedTermination(allowFaultContainment: false);
            return;
        }

        var allowFaultContainment =
            _reporter.GetFinalizationReceipt(handle.Request) is null;
        handle.StartAttachedTermination(allowFaultContainment);
    }

    private void PublishFirstStopClaim(
        ActiveRun handle,
        ActiveRun.StopClaim claim)
    {
        if (!claim.IsFirst)
        {
            return;
        }

        handle.ObserveWorkerCancellation();
        try
        {
            if (!handle.Lease.TryAdvance(
                    handle.Lease.Context,
                    WorkerJobLifecycle.Stopping))
            {
                _logger.LogError(
                    "Processing run {RunId} could not publish its shared Stopping lifecycle",
                    handle.Request.RunId);
            }
        }
        catch (Exception failure)
        {
            _logger.LogError(
                failure,
                "Processing run {RunId} shared Stopping lifecycle publication faulted",
                handle.Request.RunId);
        }
    }

    private void RequestFaultContainment(
        ActiveRun handle,
        ChildWorkerTerminalPreventingObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!ReferenceEquals(observation.ObservedAt.Clock, _timeProvider))
        {
            throw new InvalidOperationException(
                "The child fault observation clock does not match the active run clock.");
        }

        if (_reporter.GetFinalizationReceipt(handle.Request) is not null
            && observation.Reason is not ChildWorkerFaultContainmentReason.TerminalInputCloseFailed)
        {
            return;
        }

        handle.StartFaultContainment(observation);
    }

    private Task? StopActiveRunCore()
    {
        ActiveRun? handle;
        ActiveRun.StopClaim claim;
        lock (_admissionGate)
        {
            handle = _active;
            if (handle is null)
            {
                return null;
            }

            claim = handle.ClaimStop(
                _timeProvider,
                ChildWorkerTerminationIntent.Stop,
                trackCancellationDispatch: true);
        }

        PublishFirstStopClaim(handle, claim);
        if (claim.IsFirst)
        {
            try
            {
                DispatchCancellation(handle, claim.CancellationDispatch!);
            }
            finally
            {
                StartAttachedTermination(handle);
            }
        }

        return claim.Settlement;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _observer?.CoordinatorStarted();
            return Task.CompletedTask;
        }
        catch (Exception failure)
        {
            return CompleteFailedStartAsync(failure);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return BeginShutdown();
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(BeginShutdown());
    }

    public void Dispose()
    {
        BeginShutdown().GetAwaiter().GetResult();
    }

    internal Task BeginShutdown()
    {
        TaskCompletionSource start;
        TaskCompletionSource<Task> workerShutdownReady;
        TaskCompletionSource<Task> preflightStopReady;
        Task scheduledPreflightDrained;
        Task shutdown;
        ActiveRun? handle;
        ActiveRun.StopClaim? stopClaim;
        lock (_admissionGate)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            _admissionOpen = false;
            scheduledPreflightDrained = _scheduledPreflightDrained.Task;
            handle = _active;
            handle?.MarkShutdownRequested();
            stopClaim = handle?.ClaimStop(
                _timeProvider,
                ChildWorkerTerminationIntent.Shutdown,
                trackCancellationDispatch: true);
            start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            workerShutdownReady = new TaskCompletionSource<Task>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            preflightStopReady = new TaskCompletionSource<Task>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            shutdown = CompleteShutdownAsync(
                handle,
                stopClaim,
                workerShutdownReady.Task,
                preflightStopReady.Task,
                scheduledPreflightDrained,
                start.Task);
            _shutdownTask = shutdown;
        }

        try
        {
            workerShutdownReady.TrySetResult(_workerCoordinator.BeginShutdown());
        }
        catch (Exception failure)
        {
            workerShutdownReady.TrySetResult(Task.FromException(failure));
        }

        if (handle is not null && stopClaim is not null)
        {
            PublishFirstStopClaim(handle, stopClaim.Value);
        }

        try
        {
            _scheduledPreflightStopping.Cancel();
            preflightStopReady.TrySetResult(Task.CompletedTask);
        }
        catch (Exception failure)
        {
            preflightStopReady.TrySetResult(Task.FromException(failure));
        }
        finally
        {
            start.TrySetResult();
        }

        return shutdown;
    }

    private ValueTask BeforeAdmissionGateAsync(ProcessingRunAdmissionAttempt attempt)
    {
        return _observer?.BeforeAdmissionGateAsync(attempt) ?? ValueTask.CompletedTask;
    }

    private CancellationTokenSource? BeginScheduledPreflight(CancellationToken callerToken)
    {
        lock (_admissionGate)
        {
            if (!_admissionOpen)
            {
                return null;
            }

            if (_scheduledPreflightCount++ == 0)
            {
                _scheduledPreflightDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return CancellationTokenSource.CreateLinkedTokenSource(
                callerToken,
                _scheduledPreflightStopping.Token);
        }
    }

    private void EndScheduledPreflight(CancellationTokenSource preflight)
    {
        preflight.Dispose();
        TaskCompletionSource? drained = null;
        lock (_admissionGate)
        {
            if (--_scheduledPreflightCount == 0)
            {
                drained = _scheduledPreflightDrained;
            }
        }

        drained?.TrySetResult();
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult();
        return signal;
    }

    internal Task WaitForActiveRunAsync()
    {
        lock (_admissionGate)
        {
            return _active?.CleanupCompleted.Task ?? Task.CompletedTask;
        }
    }

    internal ProcessingRunRequest? ActiveRequest
    {
        get
        {
            lock (_admissionGate)
            {
                return _active?.Request;
            }
        }
    }

    private async ValueTask<(
        ProcessingRunAdmissionResult Result,
        ActiveRun? Handle,
        ExclusiveHeavyOwnerBusyMetadata? Busy)> ReserveAsync(
        ProcessingRunTrigger trigger,
        CancellationToken linkedCancellationToken)
    {
        lock (_admissionGate)
        {
            if (!_admissionOpen)
            {
                return (ProcessingRunAdmissionResult.Stopping, null, null);
            }

            if (_active is not null)
            {
                return (ProcessingRunAdmissionResult.AlreadyRunning, null, null);
            }
        }

        var request = new ProcessingRunRequest(_createRunId(), trigger);
        var dispatch = new ProcessAssetsWorkerJobDispatch(request);
        WorkerJobAdmissionResult admission = _workerCoordinator.TryAdmit(dispatch);
        if (admission is WorkerJobAdmissionResult.Busy busy)
        {
            return (ProcessingRunAdmissionResult.AlreadyRunning, null, busy.ActiveOwner);
        }

        if (admission is WorkerJobAdmissionResult.Unavailable)
        {
            return (ProcessingRunAdmissionResult.Stopping, null, null);
        }

        IWorkerJobAdmissionLease lease =
            ((WorkerJobAdmissionResult.Admitted)admission).Lease;
        var transaction = new ProcessingAdmissionTransaction(
            this,
            request,
            lease,
            trigger,
            linkedCancellationToken);
        return await transaction.TryCommitAsync().ConfigureAwait(false);
    }

    private sealed class ProcessingAdmissionTransaction
    {
        private readonly ProcessingRunCoordinator _owner;
        private readonly ProcessingRunRequest _request;
        private readonly IWorkerJobAdmissionLease _lease;
        private readonly ProcessingRunTrigger _trigger;
        private readonly CancellationToken _linkedCancellationToken;
        private IProcessingRunCancellation? _cancellation;
        private ActiveRun? _handle;
        private int _published;
        private int _rollbackStarted;

        internal ProcessingAdmissionTransaction(
            ProcessingRunCoordinator owner,
            ProcessingRunRequest request,
            IWorkerJobAdmissionLease lease,
            ProcessingRunTrigger trigger,
            CancellationToken linkedCancellationToken)
        {
            _owner = owner;
            _request = request;
            _lease = lease;
            _trigger = trigger;
            _linkedCancellationToken = linkedCancellationToken;
        }

        internal async ValueTask<(
            ProcessingRunAdmissionResult Result,
            ActiveRun? Handle,
            ExclusiveHeavyOwnerBusyMetadata? Busy)> TryCommitAsync()
        {
            try
            {
                _cancellation = _owner._cancellationFactory.Create(
                    _request,
                    _linkedCancellationToken);
                var createdHandle = new ActiveRun(_request, _cancellation, _lease);
                _handle = createdHandle;
                if (_trigger == ProcessingRunTrigger.Scheduled)
                {
                    createdHandle.RegisterScheduledCancellation(
                        () => _owner.RequestScheduledChildTermination(createdHandle));
                }

                ProcessingRunAdmissionResult? rejected = TryPublish();
                if (rejected is not null)
                {
                    await RollbackAsync().ConfigureAwait(false);
                    return (rejected.Value, null, null);
                }

                if (!_lease.TryBindOwnerStop(
                        _lease.Context,
                        () => _owner.RequestWorkerCoordinatorStopAsync(createdHandle)))
                {
                    await RollbackAsync().ConfigureAwait(false);
                    return (ProcessingRunAdmissionResult.Stopping, null, null);
                }

                return (ProcessingRunAdmissionResult.Accepted, createdHandle, null);
            }
            catch (Exception startupFailure)
            {
                ExceptionDispatchInfo captured = ExceptionDispatchInfo.Capture(startupFailure);
                try
                {
                    await RollbackAsync().ConfigureAwait(false);
                }
                catch (Exception rollbackFailure)
                {
                    _owner._logger.LogError(
                        rollbackFailure,
                        "Processing run {RunId} admission rollback faulted",
                        _request.RunId);
                }

                captured.Throw();
                throw;
            }
        }

        private ProcessingRunAdmissionResult? TryPublish()
        {
            lock (_owner._admissionGate)
            {
                if (!_owner._admissionOpen)
                {
                    return ProcessingRunAdmissionResult.Stopping;
                }

                if (_owner._active is not null)
                {
                    return ProcessingRunAdmissionResult.AlreadyRunning;
                }

                _owner._active = _handle!;
                Volatile.Write(ref _published, 1);
                return null;
            }
        }

        private async ValueTask RollbackAsync()
        {
            if (Interlocked.Exchange(ref _rollbackStarted, 1) != 0)
            {
                return;
            }

            if (Interlocked.Exchange(ref _published, 0) != 0)
            {
                lock (_owner._admissionGate)
                {
                    if (ReferenceEquals(_owner._active, _handle))
                    {
                        _owner._active = null;
                    }
                }
            }

            ExceptionDispatchInfo? firstFailure = null;
            async ValueTask AttemptAsync(Func<ValueTask> operation)
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    firstFailure ??= ExceptionDispatchInfo.Capture(failure);
                }
            }

            ActiveRun? handle = _handle;
            if (handle is not null)
            {
                await AttemptAsync(
                    handle.DisposeScheduledCancellationRegistrationAsync).ConfigureAwait(false);
                await AttemptAsync(
                    () => new ValueTask(handle.DisposeCancellationAsync())).ConfigureAwait(false);
            }
            else if (_cancellation is not null)
            {
                await AttemptAsync(() =>
                {
                    _cancellation.Dispose();
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            }

            await AttemptAsync(_lease.DisposeAsync).ConfigureAwait(false);
            firstFailure?.Throw();
        }
    }

    private Task RequestWorkerCoordinatorStopAsync(ActiveRun handle)
    {
        ActiveRun.StopClaim claim;
        lock (_admissionGate)
        {
            if (!ReferenceEquals(_active, handle)
                || !ReferenceEquals(_active.Request, handle.Request))
            {
                return Task.CompletedTask;
            }

            handle.MarkShutdownRequested();
            claim = handle.ClaimStop(
                _timeProvider,
                ChildWorkerTerminationIntent.Shutdown,
                trackCancellationDispatch: true);
        }

        PublishFirstStopClaim(handle, claim);
        if (claim.IsFirst)
        {
            try
            {
                DispatchCancellation(handle, claim.CancellationDispatch!);
            }
            finally
            {
                StartAttachedTermination(handle);
            }
        }

        return Task.CompletedTask;
    }

    private void RequestScheduledChildTermination(ActiveRun handle)
    {
        var claim = handle.ClaimStop(
            _timeProvider,
            ChildWorkerTerminationIntent.Stop,
            trackCancellationDispatch: false);
        PublishFirstStopClaim(handle, claim);
        if (claim.IsFirst)
        {
            StartAttachedTermination(handle);
        }
    }

    private async Task PrepareAndDispatchAsync(ActiveRun handle)
    {
        try
        {
            ThrowIfShutdownCancellationRequested(handle);
            if (!handle.Lease.TryAdvance(
                    handle.Lease.Context,
                    WorkerJobLifecycle.Starting))
            {
                throw new OperationCanceledException(handle.Cancellation.Token);
            }

            _state.MarkPending();
            ThrowIfShutdownCancellationRequested(handle);
            if (!_reporter.Arm(handle.Request))
            {
                throw new InvalidOperationException("Processing event reporter is already armed.");
            }

            ThrowIfShutdownCancellationRequested(handle);

            if (_observer is not null)
            {
                await _observer.BeforeChildDispatchClaimAsync(handle.Request).ConfigureAwait(false);
            }

            if (!TryClaimChildDispatch(handle))
            {
                if (!handle.IsShutdownRequested && handle.Cancellation.Token.IsCancellationRequested)
                {
                    await FinalizePredispatchAsync(
                        handle,
                        ProcessingRunOutcome.Cancelled,
                        safeFailureMessage: null).ConfigureAwait(false);
                    return;
                }

                throw new OperationCanceledException(handle.Cancellation.Token);
            }

            Task<ProcessingRunResult> execution;
            try
            {
                var scope = _childBackendScopeFactory.CreateAsyncScope();
                handle.SetChildBackendScope(scope);
                var backend = scope.ServiceProvider.GetRequiredService<IChildProcessingRunBackend>();
                execution = backend.ExecuteAsync(handle.Request, _reporter, handle.Cancellation.Token)
                    ?? throw new InvalidOperationException("The child processing backend returned no execution task.");
            }
            catch (Exception failure)
            {
                await FailPreparationAsync(handle, failure).ConfigureAwait(false);
                throw;
            }

            var observation = ObserveExecutionAsync(handle, execution);
            handle.SetOwnedExecution(observation);
        }
        catch (Exception failure) when (!handle.HasOwnedExecution)
        {
            await FailPreparationAsync(handle, failure).ConfigureAwait(false);
            throw;
        }
        finally
        {
            handle.PreparationCompleted.TrySetResult();
        }
    }

    private bool TryClaimChildDispatch(ActiveRun handle)
    {
        lock (_admissionGate)
        {
            if (!_admissionOpen
                || !ReferenceEquals(_active, handle)
                || handle.IsShutdownRequested)
            {
                return false;
            }

            return handle.TryClaimChildDispatch();
        }
    }

    private async Task FinalizePredispatchAsync(
        ActiveRun handle,
        ProcessingRunOutcome outcome,
        string? safeFailureMessage)
    {
        ExceptionDispatchInfo? projectionFailure = null;
        try
        {
            if (!_reporter.TryFinalizePredispatch(handle.Request, outcome, safeFailureMessage))
            {
                throw new InvalidOperationException(
                    "The processing event reporter rejected predispatch finalization for the active request.");
            }
        }
        catch (Exception failure)
        {
            projectionFailure = ExceptionDispatchInfo.Capture(failure);
            ObserveRunFailure(handle, failure);
            if (!handle.IsShutdownRequested)
            {
                CleanupProjectionAfterFailure(handle, failure);
            }
        }
        finally
        {
            if (handle.TryBeginCleanup())
            {
                await CompleteCleanupAsync(handle, projectionFailure).ConfigureAwait(false);
            }
        }

        projectionFailure?.Throw();
    }

    private static void ThrowIfShutdownCancellationRequested(ActiveRun handle)
    {
        if (handle.IsShutdownRequested)
        {
            throw new OperationCanceledException(handle.Cancellation.Token);
        }
    }

    private async Task FailPreparationAsync(ActiveRun handle, Exception failure)
    {
        if (!handle.TryBeginCleanup())
        {
            await handle.CleanupCompleted.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            ObserveRunFailure(handle, failure);
            if (!handle.IsShutdownRequested)
            {
                CleanupProjectionAfterFailure(handle, failure);
            }
        }
        finally
        {
            await CompleteCleanupAsync(handle, ExceptionDispatchInfo.Capture(failure)).ConfigureAwait(false);
        }
    }

    private async Task ObserveExecutionAsync(ActiveRun handle, Task<ProcessingRunResult> execution)
    {
        ExceptionDispatchInfo? failure = null;
        try
        {
            var result = await execution.ConfigureAwait(false);
            if (!ReferenceEquals(result.Request, handle.Request))
            {
                throw new InvalidOperationException("The processing executor returned a result for a different request.");
            }
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
            ObserveRunFailure(handle, ex);
            if (!handle.IsShutdownRequested)
            {
                CleanupProjectionAfterFailure(handle, ex);
            }
        }
        finally
        {
            if (handle.TryBeginCleanup())
            {
                await CompleteCleanupAsync(handle, failure).ConfigureAwait(false);
            }
        }
    }

    private void ObserveInfrastructureFailure(ActiveRun handle, Exception failure)
    {
        if (failure is OperationCanceledException cancellation
            && handle.Cancellation.Token.IsCancellationRequested
            && cancellation.CancellationToken == handle.Cancellation.Token)
        {
            _logger.LogDebug(failure, "Processing run {RunId} ended through the active cancellation boundary", handle.Request.RunId);
            return;
        }

        if (failure is OutOfMemoryException)
        {
            _logger.LogCritical(failure, "Processing run {RunId} exhausted memory outside a domain terminal", handle.Request.RunId);
            return;
        }

        _logger.LogError(failure, "Processing run {RunId} faulted outside a domain terminal", handle.Request.RunId);
    }

    private void ObserveRunFailure(ActiveRun handle, Exception failure)
    {
        if (!handle.IsShutdownRequested)
        {
            ObserveInfrastructureFailure(handle, failure);
            return;
        }

        if (failure is OperationCanceledException cancellation
            && cancellation.CancellationToken == handle.Cancellation.Token)
        {
            _logger.LogDebug(
                "Processing run {RunId} ended through the host shutdown cancellation boundary",
                handle.Request.RunId);
        }
        else if (failure is OutOfMemoryException)
        {
            _logger.LogCritical(
                "Processing run {RunId} exhausted memory during host shutdown",
                handle.Request.RunId);
        }
        else
        {
            _logger.LogError(
                "Processing run {RunId} faulted during host shutdown",
                handle.Request.RunId);
        }
    }

    private void TryAbandon(ProcessingRunRequest request, Exception failure)
    {
        try
        {
            if (!_reporter.Abandon(request, failure)
                && !_reporter.AbandonPending(request, failure))
            {
                _reporter.RollbackPendingAfterArmRejection(request);
            }
        }
        catch (Exception abandonmentFailure)
        {
            _logger.LogError(abandonmentFailure, "Processing run {RunId} projection abandonment faulted", request.RunId);
        }
    }

    private void CleanupProjectionAfterFailure(
        ActiveRun handle,
        Exception failure)
    {
        if (handle.OwnsFinalization)
        {
            return;
        }

        if (!handle.IsShutdownRequested)
        {
            TryAbandon(handle.Request, failure);
            return;
        }

        try
        {
            var abandoned = handle.HasClaimedChildDispatch
                ? _reporter.AbandonProjectedActivities(handle.Request)
                : _reporter.AbandonForShutdown(handle.Request);
            if (!abandoned)
            {
                _reporter.RollbackPendingAfterArmRejection(handle.Request);
            }
        }
        catch (Exception abandonmentFailure)
        {
            if (abandonmentFailure is OutOfMemoryException)
            {
                _logger.LogCritical(
                    "Processing run {RunId} shutdown activity cleanup exhausted memory",
                    handle.Request.RunId);
            }
            else
            {
                _logger.LogError(
                    "Processing run {RunId} shutdown activity cleanup faulted",
                    handle.Request.RunId);
            }
        }
    }

    private async Task CompleteCleanupAsync(ActiveRun handle, ExceptionDispatchInfo? primaryFailure)
    {
        ExceptionDispatchInfo? cleanupFailure = null;
        handle.CloseControlPlaneForCleanup();
        handle.Lease.TryAdvance(handle.Lease.Context, WorkerJobLifecycle.Finalizing);
        try
        {
            await handle.WaitForCancellationDispatchAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            cleanupFailure = ExceptionDispatchInfo.Capture(failure);
            ObserveCleanupFailure(handle, failure);
        }

        try
        {
            if (_observer is not null)
            {
                await _observer.BeforeChildSettlementAsync(handle.Request, handle.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            cleanupFailure = ExceptionDispatchInfo.Capture(failure);
            ObserveCleanupFailure(handle, failure);
        }

        try
        {
            await handle.SettleAttachedChildAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            cleanupFailure ??= ExceptionDispatchInfo.Capture(failure);
            ObserveCleanupFailure(handle, failure);
        }

        try
        {
            await handle.DisposeScheduledCancellationRegistrationAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            cleanupFailure ??= ExceptionDispatchInfo.Capture(failure);
            ObserveCleanupFailure(handle, failure);
        }

        if (handle.IsShutdownRequested && primaryFailure is not null)
        {
            CleanupProjectionAfterFailure(handle, primaryFailure.SourceException);
        }

        try
        {
            if (_observer is not null)
            {
                await _observer.BeforeDetachAsync(handle.Request, handle.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            cleanupFailure = ExceptionDispatchInfo.Capture(failure);
            ObserveCleanupFailure(handle, failure);
        }

        try
        {
            if (_observer is not null)
            {
                await _observer.BeforeDisposeAsync(handle.Request, handle.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            cleanupFailure ??= ExceptionDispatchInfo.Capture(failure);
            ObserveCleanupFailure(handle, failure);
        }

        try
        {
            await handle.DisposeCancellationAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            cleanupFailure ??= ExceptionDispatchInfo.Capture(failure);
            ObserveCleanupFailure(handle, failure);
        }

        try
        {
            await handle.DisposeChildBackendScopeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            cleanupFailure ??= ExceptionDispatchInfo.Capture(failure);
            ObserveCleanupFailure(handle, failure);
        }
        finally
        {
            bool releaseAdmission = false;
            lock (_admissionGate)
            {
                if (ReferenceEquals(_active, handle)
                    && ReferenceEquals(_active.Request, handle.Request))
                {
                    _active = null;
                    handle.MarkFinalizerReleased();
                    releaseAdmission = true;
                }
            }

            if (releaseAdmission)
            {
                try
                {
                    await handle.Lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    cleanupFailure ??= ExceptionDispatchInfo.Capture(failure);
                    ObserveCleanupFailure(handle, failure);
                }
            }

            handle.ExecutionFailure = primaryFailure ?? cleanupFailure;
            handle.CleanupCompleted.TrySetResult();
        }
    }

    private void ObserveCleanupFailure(ActiveRun handle, Exception failure)
    {
        if (handle.IsShutdownRequested)
        {
            if (failure is OutOfMemoryException)
            {
                _logger.LogCritical(
                    "Processing run {RunId} host shutdown cleanup exhausted memory",
                    handle.Request.RunId);
            }
            else
            {
                _logger.LogError(
                    "Processing run {RunId} host shutdown cleanup faulted",
                    handle.Request.RunId);
            }

            return;
        }

        if (failure is OutOfMemoryException)
        {
            _logger.LogCritical(failure, "Processing run {RunId} cleanup exhausted memory", handle.Request.RunId);
        }
        else
        {
            _logger.LogError(failure, "Processing run {RunId} cleanup faulted", handle.Request.RunId);
        }
    }

    private void BeginShutdownFromApplicationStopping()
    {
        _ = BeginShutdown();
    }

    private async Task CompleteFailedStartAsync(Exception startupFailure)
    {
        var capturedFailure = ExceptionDispatchInfo.Capture(startupFailure);
        try
        {
            await BeginShutdown().ConfigureAwait(false);
        }
        catch
        {
            // CompleteShutdownAsync records every shutdown phase failure before preserving startup failure.
        }

        capturedFailure.Throw();
    }

    private async Task CompleteShutdownAsync(
        ActiveRun? handle,
        ActiveRun.StopClaim? stopClaim,
        Task<Task> workerShutdownReady,
        Task<Task> preflightStopReady,
        Task scheduledPreflightDrained,
        Task start)
    {
        await start.ConfigureAwait(false);
        ExceptionDispatchInfo? firstFailure = null;

        async Task AttemptAsync(Func<Task> operation)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(failure);
                ObserveShutdownFailure(failure);
            }
        }

        await AttemptAsync(async () =>
            await BeforeAdmissionGateAsync(ProcessingRunAdmissionAttempt.Stop).ConfigureAwait(false)).ConfigureAwait(false);
        await AttemptAsync(() =>
        {
            _observer?.CoordinatorStopping();
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        if (handle is not null && stopClaim is not null)
        {
            await AttemptAsync(() => StartOrJoinShutdownStop(handle, stopClaim.Value)).ConfigureAwait(false);
        }

        await AttemptAsync(async () =>
            await await workerShutdownReady.ConfigureAwait(false)).ConfigureAwait(false);
        await AttemptAsync(async () =>
            await scheduledPreflightDrained.ConfigureAwait(false)).ConfigureAwait(false);
        await AttemptAsync(async () =>
            await await preflightStopReady.ConfigureAwait(false)).ConfigureAwait(false);

        await AttemptAsync(() =>
        {
            _observer?.CoordinatorStopped();
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await AttemptAsync(async () =>
        {
            await _applicationStoppingRegistrationReady.Task.ConfigureAwait(false);
            await _applicationStoppingRegistration.DisposeAsync().ConfigureAwait(false);
            _scheduledPreflightStopping.Dispose();
        }).ConfigureAwait(false);

        firstFailure?.Throw();
    }

    private Task StartOrJoinShutdownStop(
        ActiveRun handle,
        ActiveRun.StopClaim claim)
    {
        try
        {
            if (claim.IsFirst)
            {
                DispatchCancellation(handle, claim.CancellationDispatch!);
            }
        }
        finally
        {
            StartAttachedTermination(handle);
        }

        return claim.Settlement;
    }

    private void ObserveShutdownFailure(Exception failure)
    {
        if (failure is OutOfMemoryException)
        {
            _logger.LogCritical("Processing coordinator shutdown exhausted memory");
        }
        else
        {
            _logger.LogError("Processing coordinator shutdown faulted");
        }
    }

    private void RequestCancellation(ActiveRun handle)
    {
        _observer?.BeforeRequestCancellation(handle.Request);
        var failure = handle.RequestCancellation();
        _observer?.AfterRequestCancellation(handle.Request);
        if (failure is null || !handle.TryMarkCancellationFailureObserved())
        {
            return;
        }

        if (handle.IsShutdownRequested)
        {
            if (failure.SourceException is OutOfMemoryException)
            {
                _logger.LogCritical(
                    "Processing run {RunId} host shutdown cancellation exhausted memory",
                    handle.Request.RunId);
            }
            else
            {
                _logger.LogError(
                    "Processing run {RunId} host shutdown cancellation faulted",
                    handle.Request.RunId);
            }

            return;
        }

        if (failure.SourceException is OutOfMemoryException)
        {
            _logger.LogCritical(failure.SourceException, "Processing run {RunId} cancellation callback exhausted memory", handle.Request.RunId);
        }
        else
        {
            _logger.LogError(failure.SourceException, "Processing run {RunId} cancellation callback faulted", handle.Request.RunId);
        }
    }

    private void DispatchCancellation(ActiveRun handle, TaskCompletionSource completion)
    {
        try
        {
            _ = Task.Run(() => CompleteCancellationDispatch(handle, completion, propagateFailure: false));
        }
        catch (Exception failure)
        {
            completion.TrySetException(failure);
        }
    }

    private void CompleteCancellationDispatch(
        ActiveRun handle,
        TaskCompletionSource completion,
        bool propagateFailure)
    {
        try
        {
            RequestCancellation(handle);
            completion.TrySetResult();
        }
        catch (Exception failure)
        {
            completion.TrySetException(failure);
            if (propagateFailure)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }

    private sealed class ProcessingRunCancellationFactory : IProcessingRunCancellationFactory
    {
        public IProcessingRunCancellation Create(ProcessingRunRequest request, CancellationToken linkedToken)
        {
            var source = request.Trigger == ProcessingRunTrigger.Scheduled
                ? CancellationTokenSource.CreateLinkedTokenSource(linkedToken)
                : new CancellationTokenSource();
            return new ProcessingRunCancellation(source);
        }
    }

    private sealed class ProcessingRunCancellation(CancellationTokenSource source) : IProcessingRunCancellation
    {
        public CancellationToken Token => source.Token;
        public void Cancel() => source.Cancel();
        public void Dispose() => source.Dispose();
    }

    private sealed class ActiveRun
    {
        private int _cleanupStarted;
        private bool _childDispatchClaimed;
        private int _cancellationFailureObserved;
        private int _shutdownRequested;
        private Task? _ownedExecution;
        private AsyncServiceScope? _childBackendScope;
        private CancellationTokenRegistration? _scheduledCancellationRegistration;
        private readonly object _cancellationGate = new();
        private readonly object _childGate = new();
        private readonly TaskCompletionSource _disposalCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _cancellationRequested;
        private bool _cancellationInProgress;
        private bool _cancellationCompleted;
        private bool _disposalRequested;
        private bool _disposalAttempted;
        private bool _disposalCompletedState;
        private bool _stopClaimsClosed;
        private bool _childAttachmentClosed;
        private ExceptionDispatchInfo? _cancellationFailure;
        private ChildAttachment? _child;
        private WorkerRunFinalizer? _finalizer;
        private ChildWorkerStopRequest? _stopRequest;
        private ChildWorkerTerminationIntent _stopIntent;
        private Lazy<Task<ChildWorkerCancellationResult>>? _childTermination;
        private ChildWorkerTerminationIntent? _childTerminationIntent;
        private Task? _cancellationDispatch;

        public ActiveRun(
            ProcessingRunRequest request,
            IProcessingRunCancellation cancellation,
            IWorkerJobAdmissionLease lease)
        {
            Request = request;
            Cancellation = cancellation;
            Lease = lease;
        }

        public ProcessingRunRequest Request { get; }
        public IProcessingRunCancellation Cancellation { get; }
        public IWorkerJobAdmissionLease Lease { get; }
        public TaskCompletionSource PreparationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExceptionDispatchInfo? ExecutionFailure { get; set; }
        public bool HasOwnedExecution => Volatile.Read(ref _ownedExecution) is not null;
        public bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;
        public bool IsStopRequested
        {
            get
            {
                lock (_childGate)
                {
                    return _stopRequest is not null;
                }
            }
        }
        public bool HasClaimedChildDispatch
        {
            get
            {
                lock (_childGate)
                {
                    return _childDispatchClaimed;
                }
            }
        }

        public bool OwnsFinalization
        {
            get
            {
                lock (_childGate)
                {
                    return _finalizer is not null;
                }
            }
        }

        public void MarkShutdownRequested()
        {
            Volatile.Write(ref _shutdownRequested, 1);
        }

        public bool TryClaimChildDispatch()
        {
            lock (_childGate)
            {
                if (_childDispatchClaimed
                    || (Request.Trigger == ProcessingRunTrigger.Scheduled
                        && (_stopRequest is not null
                            || _stopClaimsClosed
                            || Cancellation.Token.IsCancellationRequested)))
                {
                    return false;
                }

                _childDispatchClaimed = true;
                return true;
            }
        }

        public void SetOwnedExecution(Task execution)
        {
            if (Interlocked.CompareExchange(ref _ownedExecution, execution, null) is not null)
            {
                throw new InvalidOperationException("Execution ownership has already been established.");
            }
        }

        public void SetChildBackendScope(AsyncServiceScope scope)
        {
            if (_childBackendScope is not null)
            {
                throw new InvalidOperationException("Backend scope ownership has already been established.");
            }

            _childBackendScope = scope;
        }

        public async ValueTask DisposeChildBackendScopeAsync()
        {
            if (_childBackendScope is AsyncServiceScope scope)
            {
                _childBackendScope = null;
                await scope.DisposeAsync().ConfigureAwait(false);
            }
        }

        public void RegisterScheduledCancellation(Action callback)
        {
            if (_scheduledCancellationRegistration is not null)
            {
                throw new InvalidOperationException("Scheduled cancellation has already been registered.");
            }

            _scheduledCancellationRegistration = Cancellation.Token.Register(callback);
        }

        public ValueTask DisposeScheduledCancellationRegistrationAsync()
        {
            if (_scheduledCancellationRegistration is not CancellationTokenRegistration registration)
            {
                return ValueTask.CompletedTask;
            }

            _scheduledCancellationRegistration = null;
            return registration.DisposeAsync();
        }

        public bool TryBeginCleanup()
        {
            return Interlocked.Exchange(ref _cleanupStarted, 1) == 0;
        }

        public StopClaim ClaimStop(
            TimeProvider timeProvider,
            ChildWorkerTerminationIntent intent,
            bool trackCancellationDispatch)
        {
            if (intent is not ChildWorkerTerminationIntent.Stop
                and not ChildWorkerTerminationIntent.Shutdown)
            {
                throw new ArgumentOutOfRangeException(nameof(intent));
            }

            lock (_childGate)
            {
                if (_stopRequest is not null)
                {
                    return new StopClaim(false, CleanupCompleted.Task, null);
                }

                if (_stopClaimsClosed)
                {
                    return new StopClaim(false, CleanupCompleted.Task, null);
                }

                _stopRequest = ChildWorkerStopRequest.Capture(timeProvider);
                _stopIntent = intent;
                TaskCompletionSource? cancellationDispatch = null;
                if (trackCancellationDispatch)
                {
                    cancellationDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _cancellationDispatch = cancellationDispatch.Task;
                }
                return new StopClaim(true, CleanupCompleted.Task, cancellationDispatch);
            }
        }

        public void CloseControlPlaneForCleanup()
        {
            lock (_childGate)
            {
                _stopClaimsClosed = true;
                _childAttachmentClosed = true;
            }
        }

        public bool TryClaimChildExecution(WorkerRunFinalizer finalizer)
        {
            lock (_childGate)
            {
                if (_childAttachmentClosed || _finalizer is not null || _child is not null)
                {
                    return false;
                }

                _finalizer = finalizer;
                return true;
            }
        }

        public bool TryAttachChildSession(
            ChildWorkerSession session,
            WorkerStateBridge? bridge,
            WorkerRunFinalizer? finalizer,
            bool claimDefaultFinalizer,
            out ChildAttachment? attachment)
        {
            lock (_childGate)
            {
                if (_childAttachmentClosed || _child is not null)
                {
                    attachment = null;
                    return false;
                }

                if (finalizer is not null)
                {
                    if (_finalizer is null)
                    {
                        if (!claimDefaultFinalizer)
                        {
                            attachment = null;
                            return false;
                        }

                        _finalizer = finalizer;
                    }
                    else if (!ReferenceEquals(_finalizer, finalizer))
                    {
                        attachment = null;
                        return false;
                    }
                }
                else if (_finalizer is not null)
                {
                    attachment = null;
                    return false;
                }

                attachment = new ChildAttachment(session, bridge, finalizer);
                _child = attachment;
                return true;
            }
        }

        public void StartAttachedTermination(bool allowFaultContainment)
        {
            ChildTerminationDispatch? dispatch;
            lock (_childGate)
            {
                dispatch = EnsureChildTerminationUnderGate(
                    allowFaultContainment
                        ? GetCompletedTerminalPreventingObservationUnderGate()
                        : null);
            }

            if (dispatch is not null)
            {
                _finalizer?.State.AdvanceTransport(WorkerRunTransportPhase.Draining);
                dispatch.Start();
            }
        }

        public void StartFaultContainment(
            ChildWorkerTerminalPreventingObservation observation)
        {
            ChildTerminationDispatch? dispatch;
            lock (_childGate)
            {
                if (_child is null || _child.Session.EvidenceFinality.IsCompleted)
                {
                    return;
                }

                dispatch = EnsureChildTerminationUnderGate(observation);
            }

            if (dispatch is not null)
            {
                _finalizer?.State.AdvanceTransport(WorkerRunTransportPhase.Draining);
                dispatch.Start();
            }
        }

        public Task WaitForCancellationDispatchAsync()
        {
            lock (_childGate)
            {
                return _cancellationDispatch ?? Task.CompletedTask;
            }
        }

        public async Task SettleAttachedChildAsync()
        {
            ChildAttachment? child;
            WorkerRunFinalizer? finalizer;
            Lazy<Task<ChildWorkerCancellationResult>>? childTermination;
            lock (_childGate)
            {
                child = _child;
                finalizer = _finalizer;
                childTermination = _childTermination;
            }

            if (child is null && finalizer is null)
            {
                return;
            }

            ExceptionDispatchInfo? firstFailure = null;

            async Task AttemptAsync(Func<Task> operation)
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    firstFailure ??= ExceptionDispatchInfo.Capture(failure);
                }
            }

            if (childTermination is not null && finalizer is null)
            {
                await AttemptAsync(async () => await childTermination.Value.ConfigureAwait(false)).ConfigureAwait(false);
            }
            else if (childTermination is not null)
            {
                _ = childTermination.Value;
            }

            if (finalizer is not null)
            {
                if (child is not null)
                {
                    await AttemptAsync(async () => await child.FinalizerStarted.Task.ConfigureAwait(false)).ConfigureAwait(false);
                }

                await finalizer.StateFinality.ConfigureAwait(false);
                await AttemptAsync(async () => await finalizer.Completion.ConfigureAwait(false)).ConfigureAwait(false);

                lock (_childGate)
                {
                    childTermination = _childTermination;
                }

                if (childTermination is not null)
                {
                    await AttemptAsync(async () => await childTermination.Value.ConfigureAwait(false)).ConfigureAwait(false);
                }
            }

            if (child is not null)
            {
                await AttemptAsync(async () => await child.Session.Settlement.ConfigureAwait(false)).ConfigureAwait(false);
                await AttemptAsync(async () => await child.Session.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);

                if (child.Bridge is not null)
                {
                    await AttemptAsync(async () => await child.Bridge.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
                }
            }

            firstFailure?.Throw();
        }

        public void MarkFinalizerReleased()
        {
            WorkerRunFinalizer? finalizer;
            lock (_childGate)
            {
                finalizer = _finalizer;
            }

            finalizer?.ObserveRelease();
        }

        public void ObserveWorkerCancellation()
        {
            WorkerRunFinalizer? finalizer;
            lock (_childGate)
            {
                finalizer = _finalizer;
            }

            finalizer?.ObserveCancellation();
        }

        public ExceptionDispatchInfo? RequestCancellation()
        {
            lock (_cancellationGate)
            {
                if (_cancellationRequested || _cancellationCompleted || _disposalRequested)
                {
                    return _cancellationFailure;
                }
                _cancellationRequested = true;
                _cancellationInProgress = true;
            }

            ExceptionDispatchInfo? failure = null;
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                var performDeferredDisposal = false;
                lock (_cancellationGate)
                {
                    _cancellationFailure = failure;
                    _cancellationInProgress = false;
                    _cancellationCompleted = true;
                    if (_disposalRequested && !_disposalAttempted)
                    {
                        _disposalAttempted = true;
                        performDeferredDisposal = true;
                    }
                }
                if (performDeferredDisposal)
                {
                    PerformDisposal();
                }
            }
            return failure;
        }

        public bool TryMarkCancellationFailureObserved()
        {
            return Interlocked.Exchange(ref _cancellationFailureObserved, 1) == 0;
        }

        public Task DisposeCancellationAsync()
        {
            var performDisposal = false;
            lock (_cancellationGate)
            {
                _disposalRequested = true;
                if (_disposalCompletedState)
                {
                    return _disposalCompleted.Task;
                }
                if (!_disposalAttempted && !_cancellationInProgress)
                {
                    _disposalAttempted = true;
                    performDisposal = true;
                }
            }
            if (performDisposal)
            {
                PerformDisposal();
            }
            return _disposalCompleted.Task;
        }

        private ChildWorkerTerminalPreventingObservation?
            GetCompletedTerminalPreventingObservationUnderGate()
        {
            if (_child?.Finalizer is null
                || _child.Session.EvidenceFinality.IsCompleted
                || !_child.Session.FirstTerminalPreventingObservation.IsCompletedSuccessfully)
            {
                return null;
            }

            return _child.Session.FirstTerminalPreventingObservation.Result;
        }

        private ChildTerminationDispatch? EnsureChildTerminationUnderGate(
            ChildWorkerTerminalPreventingObservation? observation)
        {
            if (_child is null)
            {
                return null;
            }

            if (observation is not null
                && !ReferenceEquals(observation.ObservedAt.Clock, _child.Session.Clock))
            {
                throw new InvalidOperationException(
                    "The child fault observation clock does not match the attached session clock.");
            }

            var faultWins = observation is not null
                && (_stopRequest is null
                    || observation.ObservedAt.FirstStopTimestamp
                        < _stopRequest.FirstStopTimestamp);

            if (_childTermination is null)
            {
                ChildWorkerTerminationRequest request;
                if (faultWins)
                {
                    request = new ChildWorkerTerminationRequest(
                        observation!.ObservedAt,
                        ChildWorkerTerminationIntent.FaultContainment,
                        observation.Reason);
                }
                else if (_stopRequest is not null)
                {
                    request = new ChildWorkerTerminationRequest(
                        _stopRequest,
                        _stopIntent);
                }
                else
                {
                    return null;
                }

                var child = _child;
                _childTerminationIntent = request.Intent;
                _childTermination = new Lazy<Task<ChildWorkerCancellationResult>>(
                    () =>
                    {
                        try
                        {
                            return child.Session.RequestTermination(request);
                        }
                        catch (Exception failure)
                        {
                            return Task.FromException<ChildWorkerCancellationResult>(failure);
                        }
                    },
                    LazyThreadSafetyMode.ExecutionAndPublication);
            }

            var containment = observation is not null
                && _childTerminationIntent != ChildWorkerTerminationIntent.FaultContainment
                    ? new ChildWorkerTerminationRequest(
                        observation.ObservedAt,
                        ChildWorkerTerminationIntent.FaultContainment,
                        observation.Reason)
                    : null;
            return new ChildTerminationDispatch(
                _child.Session,
                _childTermination,
                containment);
        }

        private void PerformDisposal()
        {
            ExceptionDispatchInfo? failure = null;
            try
            {
                Cancellation.Dispose();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }

            lock (_cancellationGate)
            {
                _disposalCompletedState = true;
            }
            if (failure is null)
            {
                _disposalCompleted.TrySetResult();
            }
            else
            {
                _disposalCompleted.TrySetException(failure.SourceException);
            }
        }

        public readonly record struct StopClaim(
            bool IsFirst,
            Task Settlement,
            TaskCompletionSource? CancellationDispatch);

        public sealed record ChildAttachment(
            ChildWorkerSession Session,
            WorkerStateBridge? Bridge,
            WorkerRunFinalizer? Finalizer)
        {
            internal TaskCompletionSource FinalizerStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed record ChildTerminationDispatch(
            ChildWorkerSession Session,
            Lazy<Task<ChildWorkerCancellationResult>> Primary,
            ChildWorkerTerminationRequest? Containment)
        {
            internal void Start()
            {
                _ = Primary.Value;
                if (Containment is not null)
                {
                    _ = Session.RequestTermination(Containment);
                }
            }
        }
    }
}
