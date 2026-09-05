using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using WorkerStateBridge = ImmichReverseGeo.Web.WorkerEventStateBridge.WorkerEventStateBridge;
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
    private readonly object _admissionGate = new();
    private readonly ProcessingState _state;
    private readonly ProcessingStateEventReporter _reporter;
    private readonly IProcessingRunExecutor _executor;
    private readonly ILogger<ProcessingRunCoordinator> _logger;
    private readonly Func<Guid> _createRunId;
    private readonly IProcessingRunCancellationFactory _cancellationFactory;
    private readonly IProcessingRunCoordinatorObserver? _observer;
    private readonly TimeProvider _timeProvider;
    private readonly TaskCompletionSource _applicationStoppingRegistrationReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _applicationStoppingRegistration;
    private ActiveRun? _active;
    private Task? _shutdownTask;
    private bool _admissionOpen = true;

    public ProcessingRunCoordinator(
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        IProcessingRunExecutor executor,
        ILogger<ProcessingRunCoordinator> logger,
        IHostApplicationLifetime? applicationLifetime = null,
        TimeProvider? timeProvider = null)
        : this(
            state,
            reporter,
            executor,
            logger,
            Guid.NewGuid,
            new ProcessingRunCancellationFactory(),
            null,
            applicationLifetime,
            timeProvider)
    {
    }

    internal ProcessingRunCoordinator(
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        IProcessingRunExecutor executor,
        ILogger<ProcessingRunCoordinator> logger,
        Func<Guid> createRunId,
        IHostApplicationLifetime? applicationLifetime = null,
        TimeProvider? timeProvider = null)
        : this(
            state,
            reporter,
            executor,
            logger,
            createRunId,
            new ProcessingRunCancellationFactory(),
            null,
            applicationLifetime,
            timeProvider)
    {
    }

    internal ProcessingRunCoordinator(
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        IProcessingRunExecutor executor,
        ILogger<ProcessingRunCoordinator> logger,
        Func<Guid> createRunId,
        IProcessingRunCoordinatorObserver? observer,
        IHostApplicationLifetime? applicationLifetime = null,
        TimeProvider? timeProvider = null)
        : this(
            state,
            reporter,
            executor,
            logger,
            createRunId,
            new ProcessingRunCancellationFactory(),
            observer,
            applicationLifetime,
            timeProvider)
    {
    }

    internal ProcessingRunCoordinator(
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        IProcessingRunExecutor executor,
        ILogger<ProcessingRunCoordinator> logger,
        Func<Guid> createRunId,
        IProcessingRunCancellationFactory cancellationFactory,
        IProcessingRunCoordinatorObserver? observer,
        IHostApplicationLifetime? applicationLifetime = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(createRunId);
        ArgumentNullException.ThrowIfNull(cancellationFactory);

        _state = state;
        _reporter = reporter;
        _executor = executor;
        _logger = logger;
        _createRunId = createRunId;
        _cancellationFactory = cancellationFactory;
        _observer = observer;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

    public async Task<ProcessingRunAdmissionResult> TriggerManualAsync()
    {
        await BeforeAdmissionGateAsync(ProcessingRunAdmissionAttempt.Manual).ConfigureAwait(false);
        var reservation = Reserve(ProcessingRunTrigger.Manual, CancellationToken.None);
        if (reservation.Result != ProcessingRunAdmissionResult.Accepted)
        {
            return reservation.Result;
        }

        await PrepareAndDispatchAsync(reservation.Handle!).ConfigureAwait(false);
        return ProcessingRunAdmissionResult.Accepted;
    }

    async Task<ScheduledTriggerResult> IScheduledRunTrigger.TriggerScheduledAsync(CancellationToken stoppingToken)
    {
        await BeforeAdmissionGateAsync(ProcessingRunAdmissionAttempt.Scheduled).ConfigureAwait(false);
        var reservation = Reserve(ProcessingRunTrigger.Scheduled, stoppingToken);
        if (reservation.Result != ProcessingRunAdmissionResult.Accepted)
        {
            if (reservation.Result == ProcessingRunAdmissionResult.AlreadyRunning)
            {
                _state.AppendLog("Scheduled run skipped because a processing pass is already in progress.");
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

            claim = handle.ClaimStop(_timeProvider, trackCancellationDispatch: false);
        }

        if (claim.IsFirst)
        {
            handle.StartAttachedStop();
        }

        RequestCancellation(handle);
        return true;
    }

    internal bool TryAttachChildSession(
        ProcessingRunRequest request,
        ChildWorkerSession session,
        WorkerStateBridge? bridge = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);

        ActiveRun handle;
        var startStop = false;
        lock (_admissionGate)
        {
            if (_active is null
                || !ReferenceEquals(_active.Request, request)
                || session.RunId != request.RunId
                || !ReferenceEquals(session.Request, request)
                || !ReferenceEquals(session.Clock, _timeProvider)
                || (bridge is not null && !ReferenceEquals(bridge.Request, request)))
            {
                return false;
            }

            handle = _active;
            if (!handle.TryAttachChildSession(session, bridge, out startStop))
            {
                return false;
            }
        }

        if (startStop)
        {
            handle.StartAttachedStop();
        }

        return true;
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

            claim = handle.ClaimStop(_timeProvider, trackCancellationDispatch: true);
        }

        if (claim.IsFirst)
        {
            try
            {
                DispatchCancellation(handle, claim.CancellationDispatch!);
            }
            finally
            {
                handle.StartAttachedStop();
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
        Task shutdown;
        lock (_admissionGate)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            _admissionOpen = false;
            var handle = _active;
            handle?.MarkShutdownRequested();
            ActiveRun.StopClaim? stopClaim = handle?.ClaimStop(
                _timeProvider,
                trackCancellationDispatch: true);
            start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shutdown = CompleteShutdownAsync(handle, stopClaim, start.Task);
            _shutdownTask = shutdown;
        }

        start.TrySetResult();
        return shutdown;
    }

    private ValueTask BeforeAdmissionGateAsync(ProcessingRunAdmissionAttempt attempt)
    {
        return _observer?.BeforeAdmissionGateAsync(attempt) ?? ValueTask.CompletedTask;
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

    private (ProcessingRunAdmissionResult Result, ActiveRun? Handle) Reserve(
        ProcessingRunTrigger trigger,
        CancellationToken linkedCancellationToken)
    {
        lock (_admissionGate)
        {
            if (!_admissionOpen)
            {
                return (ProcessingRunAdmissionResult.Stopping, null);
            }

            if (_active is not null)
            {
                return (ProcessingRunAdmissionResult.AlreadyRunning, null);
            }

            var request = new ProcessingRunRequest(_createRunId(), trigger);
            var cancellation = _cancellationFactory.Create(request, linkedCancellationToken);
            var handle = new ActiveRun(request, cancellation);
            _active = handle;
            return (ProcessingRunAdmissionResult.Accepted, handle);
        }
    }

    private async Task PrepareAndDispatchAsync(ActiveRun handle)
    {
        try
        {
            ThrowIfShutdownCancellationRequested(handle);
            _state.MarkPending();
            ThrowIfShutdownCancellationRequested(handle);
            if (!_reporter.Arm(handle.Request))
            {
                throw new InvalidOperationException("Processing event reporter is already armed.");
            }
            ThrowIfShutdownCancellationRequested(handle);

            Task<ProcessingRunResult> execution;
            try
            {
                execution = _executor.ExecuteAsync(handle.Request, _reporter, handle.Cancellation.Token)
                    ?? throw new InvalidOperationException("The processing run executor returned no execution task.");
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
        if (!handle.IsShutdownRequested)
        {
            TryAbandon(handle.Request, failure);
            return;
        }

        try
        {
            if (!_reporter.AbandonProjectedActivities(handle.Request))
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
        finally
        {
            lock (_admissionGate)
            {
                if (ReferenceEquals(_active, handle)
                    && ReferenceEquals(_active.Request, handle.Request))
                {
                    _active = null;
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

        await AttemptAsync(() =>
        {
            _observer?.CoordinatorStopped();
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await AttemptAsync(async () =>
        {
            await _applicationStoppingRegistrationReady.Task.ConfigureAwait(false);
            await _applicationStoppingRegistration.DisposeAsync().ConfigureAwait(false);
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
            handle.StartAttachedStop();
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
        private int _cancellationFailureObserved;
        private int _shutdownRequested;
        private Task? _ownedExecution;
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
        private ChildWorkerStopRequest? _stopRequest;
        private Lazy<Task<ChildWorkerCancellationResult>>? _childStop;
        private Task? _cancellationDispatch;

        public ActiveRun(ProcessingRunRequest request, IProcessingRunCancellation cancellation)
        {
            Request = request;
            Cancellation = cancellation;
        }

        public ProcessingRunRequest Request { get; }
        public IProcessingRunCancellation Cancellation { get; }
        public TaskCompletionSource PreparationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExceptionDispatchInfo? ExecutionFailure { get; set; }
        public bool HasOwnedExecution => Volatile.Read(ref _ownedExecution) is not null;
        public bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

        public void MarkShutdownRequested()
        {
            Volatile.Write(ref _shutdownRequested, 1);
        }

        public void SetOwnedExecution(Task execution)
        {
            if (Interlocked.CompareExchange(ref _ownedExecution, execution, null) is not null)
            {
                throw new InvalidOperationException("Execution ownership has already been established.");
            }
        }

        public bool TryBeginCleanup()
        {
            return Interlocked.Exchange(ref _cleanupStarted, 1) == 0;
        }

        public StopClaim ClaimStop(
            TimeProvider timeProvider,
            bool trackCancellationDispatch)
        {
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
                TaskCompletionSource? cancellationDispatch = null;
                if (trackCancellationDispatch)
                {
                    cancellationDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _cancellationDispatch = cancellationDispatch.Task;
                }
                EnsureChildStopUnderGate();
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

        public bool TryAttachChildSession(
            ChildWorkerSession session,
            WorkerStateBridge? bridge,
            out bool startStop)
        {
            lock (_childGate)
            {
                if (_childAttachmentClosed || _child is not null)
                {
                    startStop = false;
                    return false;
                }

                _child = new ChildAttachment(session, bridge);
                EnsureChildStopUnderGate();
                startStop = _childStop is not null;
                return true;
            }
        }

        public void StartAttachedStop()
        {
            Lazy<Task<ChildWorkerCancellationResult>>? childStop;
            lock (_childGate)
            {
                childStop = _childStop;
            }

            if (childStop is not null)
            {
                _ = childStop.Value;
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
            Lazy<Task<ChildWorkerCancellationResult>>? childStop;
            lock (_childGate)
            {
                child = _child;
                childStop = _childStop;
            }

            if (child is null)
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

            if (childStop is not null)
            {
                await AttemptAsync(async () => await childStop.Value.ConfigureAwait(false)).ConfigureAwait(false);
            }

            await AttemptAsync(async () => await child.Session.Settlement.ConfigureAwait(false)).ConfigureAwait(false);
            await AttemptAsync(async () => await child.Session.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);

            if (child.Bridge is not null)
            {
                await AttemptAsync(async () => await child.Bridge.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
            }

            firstFailure?.Throw();
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

        private void EnsureChildStopUnderGate()
        {
            if (_childStop is not null || _child is null || _stopRequest is null)
            {
                return;
            }

            var child = _child;
            var stopRequest = _stopRequest;
            _childStop = new Lazy<Task<ChildWorkerCancellationResult>>(
                () =>
                {
                    try
                    {
                        return child.Session.RequestStop(stopRequest);
                    }
                    catch (Exception failure)
                    {
                        return Task.FromException<ChildWorkerCancellationResult>(failure);
                    }
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
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

        private sealed record ChildAttachment(
            ChildWorkerSession Session,
            WorkerStateBridge? Bridge);
    }
}
