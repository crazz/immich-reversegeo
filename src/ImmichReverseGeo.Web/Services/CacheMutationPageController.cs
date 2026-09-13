using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Web.Services;

internal enum CacheMutationPagePhase
{
    Idle,
    Admitting,
    Starting,
    Running,
    CancelRequested,
    Completed,
    Cancelled,
    Busy,
    Unavailable,
    Failed
}

internal sealed record CacheMutationPageState(
    CacheMutationPagePhase Phase,
    string Status,
    string? Error,
    CacheMutationSource? Source,
    string? Iso3,
    string? JobId,
    CacheMutationProgressStep? CurrentStep,
    string? CurrentActivity,
    CacheMutationResult? Result,
    ExclusiveHeavyOwnerBusyMetadata? BusyOwner,
    bool HasAdmittedOperation,
    bool TerminalObserved)
{
    internal static CacheMutationPageState Idle { get; } = new(
        CacheMutationPagePhase.Idle,
        "Ready to refresh a cache.",
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        false,
        false);

    internal bool IsActive => Phase is
        CacheMutationPagePhase.Admitting or
        CacheMutationPagePhase.Starting or
        CacheMutationPagePhase.Running or
        CacheMutationPagePhase.CancelRequested;

    internal bool CanCancel => HasAdmittedOperation
        && !TerminalObserved
        && Phase is CacheMutationPagePhase.Starting or CacheMutationPagePhase.Running;
}

internal interface ICacheMutationCompletionSink
{
    ValueTask CompletedAsync(CacheMutationResult result);
}

internal sealed class CacheMutationPageControllerFactory(
    IWorkerJobAdmissionGate admission,
    ICacheMutationWorkerClient workerClient,
    CacheMutationPageControllerHostLifetime hostLifetime,
    ICacheMutationCompletionSink? completionSink = null,
    TimeProvider? time = null,
    WorkerEventDeliveryPolicy? policy = null)
{
    internal CacheMutationPageController Create(
        Func<Task> stateChanged,
        Func<Task> reloadStatus)
    {
        ArgumentNullException.ThrowIfNull(stateChanged);
        ArgumentNullException.ThrowIfNull(reloadStatus);
        var controller = new CacheMutationPageController(
            admission,
            workerClient,
            static () => Guid.NewGuid(),
            stateChanged,
            reloadStatus,
            completionSink,
            hostLifetime.Unregister,
            time ?? TimeProvider.System,
            policy ?? (WorkerEventDeliveryPolicy.ProductionEnabled ? new WorkerEventDeliveryPolicy() : null));
        if (!hostLifetime.Register(controller))
        {
            _ = controller.DisposeAsync();
        }

        return controller;
    }
}

internal sealed class CacheMutationPageControllerHostLifetime : IHostedService
{
    private readonly object _gate = new();
    private readonly HashSet<CacheMutationPageController> _controllers = [];
    private bool _stopping;

    internal bool Register(CacheMutationPageController controller)
    {
        lock (_gate)
        {
            return !_stopping && _controllers.Add(controller);
        }
    }

    internal void Unregister(CacheMutationPageController controller)
    {
        lock (_gate)
        {
            _controllers.Remove(controller);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CacheMutationPageController[] controllers;
        lock (_gate)
        {
            _stopping = true;
            controllers = [.. _controllers];
        }

        await Task.WhenAll(controllers.Select(static controller =>
            controller.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}

internal sealed class CacheMutationPageController : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IWorkerJobAdmissionGate _admission;
    private readonly ICacheMutationWorkerClient _workerClient;
    private readonly Func<Guid> _createJobId;
    private readonly Func<Task> _stateChanged;
    private readonly Func<Task> _reloadStatus;
    private readonly ICacheMutationCompletionSink? _completionSink;
    private readonly Action<CacheMutationPageController> _onDisposed;
    private readonly List<Activity> _activities = [];
    private readonly TaskCompletionSource _disposedCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CacheMutationPageState _state = CacheMutationPageState.Idle;
    private ActiveOperation? _active;
    private Task _currentAttempt = Task.CompletedTask;
    private long _generation;
    private bool _attemptInProgress;
    private bool _disposeStarted;
    private bool _disposed;
    private readonly ReadModelNotificationCadence? _cadence;
    private object _notificationOwner = new();
    private long _notificationRevision;
    private CacheMutationPageState? _lastNotifiedState;

    internal CacheMutationPageController(
        IWorkerJobAdmissionGate admission,
        ICacheMutationWorkerClient workerClient,
        Func<Guid> createJobId,
        Func<Task> stateChanged,
        Func<Task> reloadStatus,
        ICacheMutationCompletionSink? completionSink = null,
        Action<CacheMutationPageController>? onDisposed = null,
        TimeProvider? time = null,
        WorkerEventDeliveryPolicy? policy = null)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _workerClient = workerClient ?? throw new ArgumentNullException(nameof(workerClient));
        _createJobId = createJobId ?? throw new ArgumentNullException(nameof(createJobId));
        _stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
        _reloadStatus = reloadStatus ?? throw new ArgumentNullException(nameof(reloadStatus));
        _completionSink = completionSink;
        _onDisposed = onDisposed ?? (static _ => { });
        if (policy is not null)
        {
            policy.Validate();
            _cadence = new(time ?? TimeProvider.System, policy.NotificationCadence, DispatchNotificationAsync);
            _cadence.Bind(_notificationOwner);
        }
    }

    internal CacheMutationPageState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    internal Task RefreshAsync(CacheMutationSource source, string iso3)
    {
        Task attempt;
        long generation;
        lock (_gate)
        {
            if (_disposed || _attemptInProgress)
            {
                return Task.CompletedTask;
            }

            var request = new CacheMutationRequest(
                source,
                CacheMutationOperation.Refresh,
                iso3);
            var dispatch = new CacheMutationWorkerJobDispatch(_createJobId(), request);
            generation = ++_generation;
            _notificationOwner = new object();
            _notificationRevision = 0;
            _cadence?.Bind(_notificationOwner);
            _attemptInProgress = true;
            _state = new CacheMutationPageState(
                CacheMutationPagePhase.Admitting,
                "Checking worker availability…",
                null,
                source,
                iso3,
                dispatch.Context.JobId.ToString("D"),
                null,
                null,
                null,
                null,
                false,
                false);
            attempt = RunAttemptAsync(generation, dispatch);
            _currentAttempt = attempt;
        }

        Notify(generation);
        return attempt;
    }

    internal Task CancelAsync()
    {
        ActiveOperation? active;
        long generation;
        lock (_gate)
        {
            active = _active;
            generation = _generation;
            if (_disposed
                || active is null
                || _state.TerminalObserved
                || _state.Phase == CacheMutationPagePhase.CancelRequested)
            {
                return Task.CompletedTask;
            }

            _state = _state with
            {
                Phase = CacheMutationPagePhase.CancelRequested,
                Status = "Cancelling cache refresh…"
            };
        }

        Notify(generation);
        return active.RequestStopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        ActiveOperation? active;
        Task attempt;
        bool owner;
        lock (_gate)
        {
            owner = !_disposeStarted;
            if (!owner)
            {
                active = null;
                attempt = Task.CompletedTask;
            }
            else
            {
                _disposeStarted = true;
                _disposed = true;
                _cadence?.Dispose();
                _generation++;
                active = _active;
                attempt = _currentAttempt;
            }
        }

        if (owner)
        {
            try
            {
                if (active is not null)
                {
                    await active.RequestStopAsync().ConfigureAwait(false);
                }

                await attempt.ConfigureAwait(false);
            }
            finally
            {
                if (_cadence is not null)
                {
                    await _cadence.DisposeAsync().ConfigureAwait(false);
                }

                _onDisposed(this);
                _disposedCompletion.TrySetResult();
            }
        }

        await _disposedCompletion.Task.ConfigureAwait(false);
    }

    private async Task RunAttemptAsync(
        long generation,
        CacheMutationWorkerJobDispatch dispatch)
    {
        IWorkerJobAdmissionLease? lease = null;
        ICacheMutationWorkerSession? session = null;
        ActiveOperation? active = null;
        CacheMutationWorkerOutcome? outcome = null;
        bool admitted = false;
        try
        {
            WorkerJobAdmissionResult admission = _admission.TryAdmit(dispatch);
            if (admission is WorkerJobAdmissionResult.Busy busy)
            {
                string message = busy.ActiveOwner switch
                {
                    ExclusiveHeavyOwnerBusyMetadata.CacheMaintenance =>
                        "Cache maintenance is in progress. Try again after it finishes.",
                    ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance =>
                        "Database maintenance is in progress. Try again after it finishes.",
                    _ => "Another worker job is active. Try again after it finishes."
                };
                SetRejected(generation, CacheMutationPagePhase.Busy,
                    message, busy.ActiveOwner);
                return;
            }

            if (admission is WorkerJobAdmissionResult.Unavailable unavailable)
            {
                SetRejected(generation, CacheMutationPagePhase.Unavailable, unavailable.Message, null);
                return;
            }

            admitted = true;
            lease = ((WorkerJobAdmissionResult.Admitted)admission).Lease;
            active = new ActiveOperation(lease);
            if (!lease.TryBindOwnerStop(lease.Context, active.RequestStopAsync)
                || !lease.TryAdvance(lease.Context, WorkerJobLifecycle.Starting))
            {
                active.CompleteWithoutSession();
                outcome = new CacheMutationWorkerOutcome.Failed(
                    "cache-worker-shutdown",
                    "The cache worker became unavailable while the application was stopping.");
                return;
            }

            if (!SetStarting(generation, active))
            {
                return;
            }

            CacheMutationWorkerStartResult start = await _workerClient.StartAsync(
                lease,
                dispatch.Request,
                new AcceptedCapabilityEventSink(lease.Context, message => ApplyEvent(generation, lease.Context, message)),
                CancellationToken.None).ConfigureAwait(false);
            if (start is CacheMutationWorkerStartResult.Unavailable unavailableStart)
            {
                active.CompleteWithoutSession();
                outcome = new CacheMutationWorkerOutcome.Unavailable(
                    unavailableStart.Code,
                    unavailableStart.Message);
                return;
            }

            CacheMutationWorkerStartResult.Started started =
                (CacheMutationWorkerStartResult.Started)start;
            session = started.Session;
            active.AttachSession(session);
            if (started.ChildProcessId is not null)
            {
                lease.TryAdvance(
                    lease.Context,
                    WorkerJobLifecycle.Running,
                    started.ChildProcessId);
            }

            if (session.JobId != lease.Context.JobId
                || session.JobKind != WorkerJobKind.CacheMutation
                || session.ProtocolVersion != InternalWorkerProtocolVersion.V2)
            {
                await active.RequestStopAsync().ConfigureAwait(false);
                outcome = new CacheMutationWorkerOutcome.Failed(
                    "cache-correlation",
                    "The cache worker session did not match the admitted job.");
            }
            else
            {
                outcome = await session.Completion.ConfigureAwait(false);
            }
        }
        catch
        {
            active?.CompleteWithoutSession();
            outcome = new CacheMutationWorkerOutcome.Failed(
                "cache-controller",
                "The cache refresh could not be completed. Check the application logs and try again.");
        }
        finally
        {
            if (lease is not null)
            {
                lease.TryAdvance(lease.Context, WorkerJobLifecycle.Finalizing);
            }

            if (session is not null)
            {
                await DisposeSafelyAsync(session).ConfigureAwait(false);
            }

            if (lease is not null)
            {
                await DisposeSafelyAsync(lease).ConfigureAwait(false);
            }

            active?.Complete();
            if (admitted && CanInvokeCallback(generation))
            {
                await InvokeSafelyAsync(_reloadStatus).ConfigureAwait(false);
            }

            bool acceptedOutcome = CompleteAttempt(generation, active, outcome);
            if (acceptedOutcome
                && outcome is CacheMutationWorkerOutcome.Completed completed
                && _completionSink is not null)
            {
                await InvokeCompletionSafelyAsync(completed.Result).ConfigureAwait(false);
            }
        }
    }

    private bool SetStarting(long generation, ActiveOperation active)
    {
        lock (_gate)
        {
            if (!CanMutate(generation))
            {
                return false;
            }

            _active = active;
            _state = _state with
            {
                Phase = CacheMutationPagePhase.Starting,
                Status = "Starting isolated cache worker…",
                HasAdmittedOperation = true
            };
        }

        Notify(generation);
        return true;
    }

    private bool ApplyEvent(
        long generation,
        WorkerJobContext context,
        WorkerJobOutputMessage message)
    {
        lock (_gate)
        {
            if (!CanMutate(generation)
                || _active is null
                || !ReferenceEquals(_active.Lease.Context, context)
                || (message.Type != WorkerJobProtocolV2.ReadyType
                    && (message.JobId != context.JobId || message.JobKind != context.JobKind)))
            {
                return false;
            }

            switch (message.Payload)
            {
                case WorkerJobStartedPayload when _state.Phase != CacheMutationPagePhase.CancelRequested:
                    _state = _state with
                    {
                        Phase = CacheMutationPagePhase.Running,
                        Status = "Cache worker started."
                    };
                    break;
                case CacheMutationProgressPayload progress
                    when progress.Source == _state.Source
                        && string.Equals(progress.Iso3, _state.Iso3, StringComparison.Ordinal):
                    _state = _state with
                    {
                        Phase = _state.Phase == CacheMutationPagePhase.CancelRequested
                            ? _state.Phase
                            : CacheMutationPagePhase.Running,
                        Status = _state.Phase == CacheMutationPagePhase.CancelRequested
                            ? _state.Status
                            : progress.Message,
                        CurrentStep = progress.Step
                    };
                    break;
                case WorkerJobActivityStartedPayload started:
                    _activities.RemoveAll(activity => activity.Id == started.ActivityId);
                    _activities.Add(new Activity(started.ActivityId, started.Label));
                    _state = _state with { CurrentActivity = _activities[^1].Label };
                    break;
                case WorkerJobActivityEndedPayload ended:
                    _activities.RemoveAll(activity => activity.Id == ended.ActivityId);
                    _state = _state with
                    {
                        CurrentActivity = _activities.Count == 0 ? null : _activities[^1].Label
                    };
                    break;
                case WorkerJobTerminalPayload:
                    _state = _state with
                    {
                        Status = "Finishing cache refresh…",
                        CurrentActivity = null,
                        TerminalObserved = true
                    };
                    break;
            }
        }

        Notify(generation);
        return true;
    }

    private bool CompleteAttempt(
        long generation,
        ActiveOperation? active,
        CacheMutationWorkerOutcome? outcome)
    {
        lock (_gate)
        {
            if (active is not null && ReferenceEquals(_active, active))
            {
                _active = null;
            }

            _attemptInProgress = false;
            if (!CanMutate(generation) || outcome is null)
            {
                return false;
            }

            _activities.Clear();
            _state = outcome switch
            {
                CacheMutationWorkerOutcome.Completed completed => _state with
                {
                    Phase = CacheMutationPagePhase.Completed,
                    Status = completed.Result.Cache.Disposition == CacheMutationDisposition.AlreadyReady
                        ? "The cache was already ready."
                        : "Cache refresh completed.",
                    Error = null,
                    Result = completed.Result,
                    CurrentActivity = null,
                    HasAdmittedOperation = false,
                    TerminalObserved = true
                },
                CacheMutationWorkerOutcome.Cancelled => _state with
                {
                    Phase = CacheMutationPagePhase.Cancelled,
                    Status = "Cache refresh cancelled.",
                    Error = null,
                    CurrentActivity = null,
                    HasAdmittedOperation = false,
                    TerminalObserved = true
                },
                CacheMutationWorkerOutcome.Unavailable unavailable => _state with
                {
                    Phase = CacheMutationPagePhase.Unavailable,
                    Status = "Cache worker unavailable.",
                    Error = $"{unavailable.Message} ({unavailable.Code})",
                    CurrentActivity = null,
                    HasAdmittedOperation = false,
                    TerminalObserved = false
                },
                CacheMutationWorkerOutcome.Failed failed => _state with
                {
                    Phase = CacheMutationPagePhase.Failed,
                    Status = "Cache refresh failed.",
                    Error = $"{failed.Message} ({failed.Code})",
                    CurrentActivity = null,
                    HasAdmittedOperation = false,
                    TerminalObserved = true
                },
                _ => _state
            };
        }

        Notify(generation);
        return true;
    }

    private void SetRejected(
        long generation,
        CacheMutationPagePhase phase,
        string message,
        ExclusiveHeavyOwnerBusyMetadata? busy)
    {
        lock (_gate)
        {
            if (!CanMutate(generation))
            {
                return;
            }

            _attemptInProgress = false;
            _state = _state with
            {
                Phase = phase,
                Status = message,
                Error = null,
                JobId = null,
                BusyOwner = busy,
                HasAdmittedOperation = false,
                TerminalObserved = false
            };
        }

        Notify(generation);
    }

    private bool CanMutate(long generation) => !_disposed && generation == _generation;

    private bool CanInvokeCallback(long generation)
    {
        lock (_gate)
        {
            return CanMutate(generation);
        }
    }

    internal NotificationCadenceObservation? NotificationObservation => _cadence?.Observation;

    private void Notify(long generation)
    {
        lock (_gate)
        {
            if (!CanMutate(generation))
            {
                return;
            }

            if (_cadence is not null)
            {
                if (!ReferenceEquals(_lastNotifiedState, _state))
                {
                    _lastNotifiedState = _state;
                    _cadence.Signal(_notificationOwner, ++_notificationRevision,
                        final: !_state.IsActive || _state.TerminalObserved);
                }

                return;
            }
        }

        _ = InvokeSafelyAsync(_stateChanged);
    }

    private ValueTask DispatchNotificationAsync(object owner, long revision)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(owner, _notificationOwner))
            {
                return ValueTask.CompletedTask;
            }
        }

        return new ValueTask(_stateChanged());
    }

    private async Task InvokeCompletionSafelyAsync(CacheMutationResult result)
    {
        try
        {
            await _completionSink!.CompletedAsync(result).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task InvokeSafelyAsync(Func<Task> callback)
    {
        try
        {
            await callback().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async ValueTask DisposeSafelyAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private sealed record Activity(Guid Id, string Label);

    private sealed class ActiveOperation
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<ICacheMutationWorkerSession?> _session =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _stopTask;

        internal ActiveOperation(IWorkerJobAdmissionLease lease)
        {
            Lease = lease;
        }

        internal IWorkerJobAdmissionLease Lease { get; }

        internal void AttachSession(ICacheMutationWorkerSession session) =>
            _session.TrySetResult(session);

        internal void CompleteWithoutSession() => _session.TrySetResult(null);

        internal Task RequestStopAsync()
        {
            Lease.TryAdvance(Lease.Context, WorkerJobLifecycle.Stopping);
            lock (_gate)
            {
                _stopTask ??= StopAsync();
                return _stopTask;
            }
        }

        internal void Complete() => CompleteWithoutSession();

        private async Task StopAsync()
        {
            ICacheMutationWorkerSession? session = await _session.Task.ConfigureAwait(false);
            if (session is not null && session.IsCancellable)
            {
                await session.RequestStopAsync().ConfigureAwait(false);
            }
        }
    }

}
