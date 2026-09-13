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

internal enum CoordinateLookupPagePhase
{
    Idle,
    Validating,
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

internal sealed record CoordinateLookupSubmission(
    double Latitude,
    double Longitude,
    bool IncludeAirportInfrastructure,
    bool IncludeLiveOverturePlaces,
    bool PreferGadmAdministrativeAreas);

internal sealed record CoordinateLookupPageState(
    CoordinateLookupPagePhase Phase,
    string Status,
    string? Error,
    string? JobId,
    CoordinateLookupProgressStep? CurrentStep,
    string? CurrentActivity,
    CoordinateLookupResult? Result,
    bool ResultIsFromLastCompletedLookup,
    bool HasAdmittedOperation,
    bool TerminalObserved)
{
    internal static CoordinateLookupPageState Idle { get; } = new(
        CoordinateLookupPagePhase.Idle,
        "Ready to look up coordinates.",
        null,
        null,
        null,
        null,
        null,
        false,
        false,
        false);

    internal bool IsActive => Phase is
        CoordinateLookupPagePhase.Validating or
        CoordinateLookupPagePhase.Admitting or
        CoordinateLookupPagePhase.Starting or
        CoordinateLookupPagePhase.Running or
        CoordinateLookupPagePhase.CancelRequested;
    internal bool FormControlsEnabled => !IsActive;
    internal bool CanCancel => HasAdmittedOperation
        && !TerminalObserved
        && Phase is CoordinateLookupPagePhase.Starting or CoordinateLookupPagePhase.Running;
}

internal interface ICoordinateLookupSettingsSnapshotProvider
{
    ValueTask<CoordinateLookupCityResolverOverrides> GetAsync(
        CancellationToken cancellationToken);
}

internal sealed class ConfigCoordinateLookupSettingsSnapshotProvider(
    ConfigService config) : ICoordinateLookupSettingsSnapshotProvider
{
    public async ValueTask<CoordinateLookupCityResolverOverrides> GetAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = await config.GetConfigAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return CoordinateLookupCityProfileConversions.Snapshot(
            current.Processing.CityResolver);
    }
}

internal sealed class CoordinateLookupPageControllerFactory(
    IWorkerJobAdmissionGate admission,
    ICoordinateLookupWorkerClient workerClient,
    ICoordinateLookupSettingsSnapshotProvider settings,
    CoordinateLookupPageControllerHostLifetime hostLifetime,
    TimeProvider? time = null,
    WorkerEventDeliveryPolicy? policy = null)
{
    internal CoordinateLookupPageController Create(Action stateChanged)
    {
        ArgumentNullException.ThrowIfNull(stateChanged);
        CoordinateLookupPageController controller = new(
            admission,
            workerClient,
            settings,
            static () => Guid.NewGuid(),
            stateChanged,
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

internal sealed class CoordinateLookupPageControllerHostLifetime : IHostedService
{
    private readonly object _gate = new();
    private readonly HashSet<CoordinateLookupPageController> _controllers = [];
    private bool _stopping;

    internal bool Register(CoordinateLookupPageController controller)
    {
        lock (_gate)
        {
            return !_stopping && _controllers.Add(controller);
        }
    }

    internal void Unregister(CoordinateLookupPageController controller)
    {
        lock (_gate)
        {
            _controllers.Remove(controller);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CoordinateLookupPageController[] controllers;
        lock (_gate)
        {
            _stopping = true;
            controllers = [.. _controllers];
        }

        await Task.WhenAll(controllers.Select(static controller =>
            controller.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}

internal sealed class CoordinateLookupPageController : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IWorkerJobAdmissionGate _admission;
    private readonly ICoordinateLookupWorkerClient _workerClient;
    private readonly ICoordinateLookupSettingsSnapshotProvider _settings;
    private readonly Func<Guid> _createJobId;
    private readonly Action _stateChanged;
    private readonly Action<CoordinateLookupPageController> _onDisposed;
    private readonly TaskCompletionSource _disposedCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Activity> _activities = [];
    private CoordinateLookupPageState _state = CoordinateLookupPageState.Idle;
    private ActiveOperation? _active;
    private CancellationTokenSource? _attemptCancellation;
    private Task _currentAttempt = Task.CompletedTask;
    private long _generation;
    private bool _attemptInProgress;
    private bool _disposeStarted;
    private bool _disposed;
    private readonly ReadModelNotificationCadence? _cadence;
    private object _notificationOwner = new();
    private long _notificationRevision;
    private CoordinateLookupPageState? _lastNotifiedState;

    internal CoordinateLookupPageController(
        IWorkerJobAdmissionGate admission,
        ICoordinateLookupWorkerClient workerClient,
        ICoordinateLookupSettingsSnapshotProvider settings,
        Func<Guid> createJobId,
        Action stateChanged,
        Action<CoordinateLookupPageController>? onDisposed = null,
        TimeProvider? time = null,
        WorkerEventDeliveryPolicy? policy = null)
    {
        _admission = admission;
        _workerClient = workerClient;
        _settings = settings;
        _createJobId = createJobId;
        _stateChanged = stateChanged;
        _onDisposed = onDisposed ?? (static _ => { });
        if (policy is not null)
        {
            policy.Validate();
            _cadence = new(time ?? TimeProvider.System, policy.NotificationCadence, DispatchNotificationAsync);
            _cadence.Bind(_notificationOwner);
        }
    }

    internal CoordinateLookupPageState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    internal Task SubmitAsync(CoordinateLookupSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        Task attempt;
        long generation;
        lock (_gate)
        {
            if (_disposed || _attemptInProgress)
            {
                return Task.CompletedTask;
            }

            _attemptInProgress = true;
            generation = ++_generation;
            _notificationOwner = new object();
            _notificationRevision = 0;
            _cadence?.Bind(_notificationOwner);
            _attemptCancellation = new CancellationTokenSource();
            _state = RetainResult(
                CoordinateLookupPagePhase.Validating,
                "Validating coordinates…",
                null);
            attempt = RunAttemptAsync(
                generation,
                submission,
                _attemptCancellation.Token);
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
                || _state.Phase == CoordinateLookupPagePhase.CancelRequested)
            {
                return Task.CompletedTask;
            }

            _state = _state with
            {
                Phase = CoordinateLookupPagePhase.CancelRequested,
                Status = "Cancelling lookup…"
            };
        }

        Notify(generation);
        return active.RequestStopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        ActiveOperation? active;
        Task attempt;
        CancellationTokenSource? cancellation;
        bool disposeOwner;
        lock (_gate)
        {
            disposeOwner = !_disposeStarted;
            if (!disposeOwner)
            {
                active = null;
                attempt = Task.CompletedTask;
                cancellation = null;
            }
            else
            {
                _disposeStarted = true;
                _disposed = true;
                _cadence?.Dispose();
                _generation++;
                active = _active;
                attempt = _currentAttempt;
                cancellation = _attemptCancellation;
            }
        }

        if (disposeOwner)
        {
            try
            {
                cancellation?.Cancel();
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
        CoordinateLookupSubmission submission,
        CancellationToken cancellationToken)
    {
        IWorkerJobAdmissionLease? lease = null;
        ICoordinateLookupWorkerSession? session = null;
        ActiveOperation? active = null;
        CoordinateLookupWorkerOutcome? outcome = null;
        try
        {
            if (!TryValidate(submission, out string? validationError))
            {
                SetRetainedFailure(generation, validationError!);
                return;
            }

            CoordinateLookupCityResolverOverrides overrides;
            try
            {
                overrides = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                SetUnavailable(
                    generation,
                    "Lookup settings could not be read. Check the application logs and try again.",
                    retainResult: true);
                return;
            }

            var request = new CoordinateLookupRequest(
                submission.Latitude,
                submission.Longitude,
                submission.IncludeAirportInfrastructure,
                submission.IncludeLiveOverturePlaces,
                submission.PreferGadmAdministrativeAreas,
                overrides);
            Guid jobId = _createJobId();
            if (jobId == Guid.Empty)
            {
                throw new InvalidOperationException("The lookup job identity source returned an empty value.");
            }

            var dispatch = new CoordinateLookupWorkerJobDispatch(jobId, request);
            if (!SetAdmitting(generation, dispatch))
            {
                return;
            }

            WorkerJobAdmissionResult admission = _admission.TryAdmit(dispatch);
            if (admission is WorkerJobAdmissionResult.Busy busy)
            {
                SetBusy(generation, busy.ActiveOwner);
                return;
            }

            if (admission is WorkerJobAdmissionResult.Unavailable unavailable)
            {
                SetUnavailable(generation, unavailable.Message, retainResult: true);
                return;
            }

            lease = ((WorkerJobAdmissionResult.Admitted)admission).Lease;
            active = new ActiveOperation(generation, lease);
            if (!lease.TryBindOwnerStop(lease.Context, active.RequestStopAsync)
                || !lease.TryAdvance(lease.Context, WorkerJobLifecycle.Starting))
            {
                active.CompleteWithoutSession();
                SetUnavailable(
                    generation,
                    "The lookup worker became unavailable while the application was stopping.",
                    retainResult: true);
                return;
            }

            if (!SetStarting(generation, active))
            {
                return;
            }

            var sink = new AcceptedCapabilityEventSink(lease.Context, message => ApplyEvent(generation, lease.Context, message));
            CoordinateLookupWorkerStartResult start = await _workerClient.StartAsync(
                lease,
                request,
                sink,
                cancellationToken).ConfigureAwait(false);
            if (start is CoordinateLookupWorkerStartResult.Unavailable startUnavailable)
            {
                active.CompleteWithoutSession();
                SetUnavailable(generation, startUnavailable.Message, retainResult: false);
                return;
            }

            CoordinateLookupWorkerStartResult.Started started =
                (CoordinateLookupWorkerStartResult.Started)start;
            session = started.Session;
            if (started.ChildProcessId is not null)
            {
                lease.TryAdvance(
                    lease.Context,
                    WorkerJobLifecycle.Running,
                    started.ChildProcessId);
            }

            if (session.JobId != lease.Context.JobId
                || session.JobKind != WorkerJobKind.CoordinateLookup
                || session.ProtocolVersion != InternalWorkerProtocolVersion.V2)
            {
                active.AttachSession(session);
                await active.RequestStopAsync().ConfigureAwait(false);
                outcome = new CoordinateLookupWorkerOutcome.Failed(
                    "lookup-correlation",
                    "The lookup worker session did not match the admitted job.");
            }
            else
            {
                active.AttachSession(session);
                outcome = await session.Completion.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (active is not null)
            {
                active.CompleteWithoutSession();
            }
        }
        catch
        {
            if (active is not null)
            {
                active.CompleteWithoutSession();
            }

            outcome = new CoordinateLookupWorkerOutcome.Failed(
                "lookup-controller",
                "The lookup could not be completed. Check the application logs and try again.");
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
            CompleteAttempt(generation, active, outcome);
        }
    }

    private bool SetAdmitting(long generation, CoordinateLookupWorkerJobDispatch dispatch)
    {
        lock (_gate)
        {
            if (!CanMutate(generation))
            {
                return false;
            }

            _state = _state with
            {
                Phase = CoordinateLookupPagePhase.Admitting,
                Status = "Checking worker availability…",
                Error = null,
                JobId = dispatch.Context.JobId.ToString("D"),
                CurrentStep = null,
                CurrentActivity = null,
                HasAdmittedOperation = false,
                TerminalObserved = false
            };
        }

        Notify(generation);
        return true;
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
            _activities.Clear();
            _state = new CoordinateLookupPageState(
                CoordinateLookupPagePhase.Starting,
                "Starting isolated lookup…",
                null,
                active.Lease.Context.JobId.ToString("D"),
                null,
                null,
                null,
                false,
                true,
                false);
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
                    && (message.JobId != context.JobId
                        || message.JobKind != context.JobKind)))
            {
                return false;
            }

            switch (message.Payload)
            {
                case WorkerJobReadyPayload ready
                    when ready.SupportedJobKinds.Contains(WorkerJobKind.CoordinateLookup)
                        && _state.Phase != CoordinateLookupPagePhase.CancelRequested:
                    _state = _state with { Status = "Lookup worker is ready." };
                    break;
                case WorkerJobStartedPayload
                    when _state.Phase != CoordinateLookupPagePhase.CancelRequested:
                    _state = _state with
                    {
                        Phase = CoordinateLookupPagePhase.Running,
                        Status = "Lookup worker started."
                    };
                    break;
                case CoordinateLookupProgressPayload progress:
                    _state = _state with
                    {
                        Phase = _state.Phase == CoordinateLookupPagePhase.CancelRequested
                            ? _state.Phase
                            : CoordinateLookupPagePhase.Running,
                        Status = _state.Phase == CoordinateLookupPagePhase.CancelRequested
                            ? _state.Status
                            : progress.Message,
                        CurrentStep = progress.Step
                    };
                    break;
                case WorkerJobActivityStartedPayload started:
                    _activities.RemoveAll(item => item.Id == started.ActivityId);
                    _activities.Add(new Activity(started.ActivityId, Bound(started.Label)));
                    _state = _state with
                    {
                        CurrentActivity = _activities[^1].Label
                    };
                    break;
                case WorkerJobActivityEndedPayload ended:
                    _activities.RemoveAll(item => item.Id == ended.ActivityId);
                    _state = _state with
                    {
                        CurrentActivity = _activities.Count == 0
                            ? null
                            : _activities[^1].Label
                    };
                    break;
                case WorkerJobLogPayload log
                    when (log.Level is "warning" or "error")
                        && _state.Phase != CoordinateLookupPagePhase.CancelRequested:
                    _state = _state with { Status = Bound(log.Message) };
                    break;
                case WorkerJobTerminalPayload:
                    _state = _state with
                    {
                        Status = "Finishing lookup…",
                        CurrentActivity = null,
                        TerminalObserved = true
                    };
                    break;
            }
        }

        Notify(generation);
        return true;
    }

    private void CompleteAttempt(
        long generation,
        ActiveOperation? active,
        CoordinateLookupWorkerOutcome? outcome)
    {
        lock (_gate)
        {
            if (active is not null && ReferenceEquals(_active, active))
            {
                _active = null;
            }

            _attemptInProgress = false;
            _attemptCancellation?.Dispose();
            _attemptCancellation = null;
            if (!CanMutate(generation) || outcome is null)
            {
                return;
            }

            _activities.Clear();
            _state = outcome switch
            {
                CoordinateLookupWorkerOutcome.Completed completed => _state with
                {
                    Phase = CoordinateLookupPagePhase.Completed,
                    Status = "Lookup completed.",
                    Error = null,
                    CurrentActivity = null,
                    Result = completed.Result,
                    ResultIsFromLastCompletedLookup = false,
                    HasAdmittedOperation = false,
                    TerminalObserved = true
                },
                CoordinateLookupWorkerOutcome.Cancelled => _state with
                {
                    Phase = CoordinateLookupPagePhase.Cancelled,
                    Status = "Lookup cancelled.",
                    Error = null,
                    CurrentActivity = null,
                    HasAdmittedOperation = false,
                    TerminalObserved = true
                },
                CoordinateLookupWorkerOutcome.Failed failed => _state with
                {
                    Phase = CoordinateLookupPagePhase.Failed,
                    Status = "Lookup failed.",
                    Error = $"{failed.Message} ({failed.Code})",
                    CurrentActivity = null,
                    HasAdmittedOperation = false,
                    TerminalObserved = true
                },
                _ => _state
            };
        }

        Notify(generation);
    }

    private void SetRetainedFailure(long generation, string error)
    {
        lock (_gate)
        {
            if (!CanMutate(generation))
            {
                return;
            }

            _state = RetainResult(
                CoordinateLookupPagePhase.Failed,
                "Check the coordinates and try again.",
                error);
        }

        Notify(generation);
    }

    private void SetBusy(long generation, ExclusiveHeavyOwnerBusyMetadata busy)
    {
        string owner = busy is ExclusiveHeavyOwnerBusyMetadata.Worker
            {
                Job.CapabilityFamily: WorkerJobCapabilityFamily.Lookup
            }
            ? "another coordinate lookup"
            : busy is ExclusiveHeavyOwnerBusyMetadata.CacheMaintenance
                ? "cache maintenance"
                : busy is ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance
                    ? "database maintenance"
                    : "another background job";
        lock (_gate)
        {
            if (!CanMutate(generation))
            {
                return;
            }

            _state = RetainResult(
                CoordinateLookupPagePhase.Busy,
                $"Lookup could not start because {owner} is running. Try again after it finishes.",
                null);
        }

        Notify(generation);
    }

    private void SetUnavailable(long generation, string message, bool retainResult)
    {
        lock (_gate)
        {
            if (!CanMutate(generation))
            {
                return;
            }

            _state = _state with
            {
                Phase = CoordinateLookupPagePhase.Unavailable,
                Status = "Lookup worker unavailable.",
                Error = Bound(message),
                CurrentStep = null,
                CurrentActivity = null,
                Result = retainResult ? _state.Result : null,
                ResultIsFromLastCompletedLookup = retainResult && _state.Result is not null,
                HasAdmittedOperation = false,
                TerminalObserved = false
            };
        }

        Notify(generation);
    }

    private CoordinateLookupPageState RetainResult(
        CoordinateLookupPagePhase phase,
        string status,
        string? error)
    {
        return _state with
        {
            Phase = phase,
            Status = status,
            Error = error,
            JobId = null,
            CurrentStep = null,
            CurrentActivity = null,
            ResultIsFromLastCompletedLookup = _state.Result is not null,
            HasAdmittedOperation = false,
            TerminalObserved = false
        };
    }

    private bool CanMutate(long generation)
    {
        return !_disposed && generation == _generation;
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

        try
        {
            _stateChanged();
        }
        catch
        {
            // Rendering is observational and cannot own worker finality.
        }
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

        _stateChanged();
        return ValueTask.CompletedTask;
    }

    private static bool TryValidate(
        CoordinateLookupSubmission submission,
        out string? error)
    {
        if (!double.IsFinite(submission.Latitude)
            || submission.Latitude is < -90 or > 90)
        {
            error = "Latitude must be a finite number from -90 through 90.";
            return false;
        }

        if (!double.IsFinite(submission.Longitude)
            || submission.Longitude is < -180 or > 180)
        {
            error = "Longitude must be a finite number from -180 through 180.";
            return false;
        }

        error = null;
        return true;
    }

    private static string Bound(string value)
    {
        const int maximumLength = WorkerJobProtocolV2.MaxSafeTextLength;
        if (value.Length <= maximumLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maximumLength - 1), "…");
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
        private readonly TaskCompletionSource<ICoordinateLookupWorkerSession?> _session =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _stopTask;

        internal ActiveOperation(long generation, IWorkerJobAdmissionLease lease)
        {
            Generation = generation;
            Lease = lease;
        }

        internal long Generation { get; }
        internal IWorkerJobAdmissionLease Lease { get; }

        internal void AttachSession(ICoordinateLookupWorkerSession session)
        {
            _session.TrySetResult(session);
        }

        internal void CompleteWithoutSession()
        {
            _session.TrySetResult(null);
        }

        internal Task RequestStopAsync()
        {
            Lease.TryAdvance(Lease.Context, WorkerJobLifecycle.Stopping);
            lock (_gate)
            {
                _stopTask ??= StopAsync();
                return _stopTask;
            }
        }

        internal void Complete()
        {
            CompleteWithoutSession();
        }

        private async Task StopAsync()
        {
            ICoordinateLookupWorkerSession? session = await _session.Task.ConfigureAwait(false);
            if (session is not null && session.IsCancellable)
            {
                await session.RequestStopAsync().ConfigureAwait(false);
            }
        }
    }

}
