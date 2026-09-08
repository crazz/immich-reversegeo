using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.WorkerFailureRecovery;

namespace ImmichReverseGeo.Web.Services;

internal enum WebDeploymentMode
{
    Standard,
    WebOnly
}

internal enum InternalSchedulingPolicy
{
    Available,
    DisabledByDeploymentMode
}

internal enum ProcessAssetsWorkerState
{
    Idle,
    Starting,
    Running,
    Cancelling,
    Failed
}

internal sealed record ProcessAssetsWebStatusSnapshot(
    WebDeploymentMode Mode,
    InternalSchedulingPolicy SchedulePolicy,
    ProcessAssetsWorkerState Worker,
    string? FailureSummary,
    long Revision)
{
    internal string ModeLabel => Mode switch
    {
        WebDeploymentMode.Standard => "Standard",
        WebDeploymentMode.WebOnly => "Web-only",
        _ => throw new ArgumentOutOfRangeException(nameof(Mode), Mode, null)
    };

    internal string SchedulePolicyLabel => SchedulePolicy switch
    {
        InternalSchedulingPolicy.Available => "Available",
        InternalSchedulingPolicy.DisabledByDeploymentMode => "Disabled by Web-only",
        _ => throw new ArgumentOutOfRangeException(nameof(SchedulePolicy), SchedulePolicy, null)
    };

    internal string SchedulePolicyDescription => SchedulePolicy switch
    {
        InternalSchedulingPolicy.Available =>
            "Internal scheduling is available in Standard mode. Your saved schedule settings control whether and when it runs.",
        InternalSchedulingPolicy.DisabledByDeploymentMode =>
            "Web-only disables internal scheduling. Saved schedule values are retained, and manual runs remain available from the Dashboard.",
        _ => throw new ArgumentOutOfRangeException(nameof(SchedulePolicy), SchedulePolicy, null)
    };

    internal string WorkerLabel => Worker switch
    {
        ProcessAssetsWorkerState.Idle => "Idle",
        ProcessAssetsWorkerState.Starting => "Starting",
        ProcessAssetsWorkerState.Running => "Running",
        ProcessAssetsWorkerState.Cancelling => "Cancelling",
        ProcessAssetsWorkerState.Failed => "Failed",
        _ => throw new ArgumentOutOfRangeException(nameof(Worker), Worker, null)
    };

    internal string WorkerCssClass => Worker switch
    {
        ProcessAssetsWorkerState.Idle => "idle",
        ProcessAssetsWorkerState.Starting => "starting",
        ProcessAssetsWorkerState.Running => "running",
        ProcessAssetsWorkerState.Cancelling => "cancelling",
        ProcessAssetsWorkerState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(Worker), Worker, null)
    };
}

internal interface IProcessAssetsWebStatus
{
    ProcessAssetsWebStatusSnapshot Current { get; }
    IDisposable Subscribe(Action<ProcessAssetsWebStatusSnapshot> changed);
}

internal interface IProcessAssetsWorkerStatusSink
{
    void Admit(ProcessingRunRequest exactRequest, bool cancellationAlreadyWon);
    void ObserveTransport(ProcessingRunRequest exactRequest, WorkerRunTransportPhase phase);
    void ObserveCancellation(ProcessingRunRequest exactRequest);
    void ObserveFinality(
        ProcessingRunRequest exactRequest,
        ProcessingRunOutcome outcome,
        WorkerRunFailureCategory category);
    void Release(ProcessingRunRequest exactRequest);
}

internal sealed class ProcessAssetsWebStatus : IProcessAssetsWebStatus, IProcessAssetsWorkerStatusSink
{
    private readonly object _gate = new();
    private readonly HashSet<Subscription> _subscriptions = [];
    private readonly Queue<Notification> _notifications = [];
    private ProcessAssetsWebStatusSnapshot _current;
    private ProcessingRunRequest? _currentRequest;
    private ProcessingRunOutcome? _currentFinality;
    private WorkerRunTransportPhase _highestTransport;
    private bool _cancellationWon;
    private bool _notificationDrainScheduled;

    internal ProcessAssetsWebStatus(DeploymentMode deploymentMode)
    {
        ArgumentNullException.ThrowIfNull(deploymentMode);
        var (mode, policy) = deploymentMode switch
        {
            _ when ReferenceEquals(deploymentMode, DeploymentMode.Standard) =>
                (WebDeploymentMode.Standard, InternalSchedulingPolicy.Available),
            _ when ReferenceEquals(deploymentMode, DeploymentMode.WebOnly) =>
                (WebDeploymentMode.WebOnly, InternalSchedulingPolicy.DisabledByDeploymentMode),
            _ => throw new ArgumentException(
                "ProcessAssets Web status requires a resolved Web-hosted deployment mode.",
                nameof(deploymentMode))
        };

        _current = new ProcessAssetsWebStatusSnapshot(
            mode,
            policy,
            ProcessAssetsWorkerState.Idle,
            FailureSummary: null,
            Revision: 0);
    }

    public ProcessAssetsWebStatusSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public IDisposable Subscribe(Action<ProcessAssetsWebStatusSnapshot> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var subscription = new Subscription(this, changed);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }
        return subscription;
    }

    void IProcessAssetsWorkerStatusSink.Admit(
        ProcessingRunRequest exactRequest,
        bool cancellationAlreadyWon)
    {
        ArgumentNullException.ThrowIfNull(exactRequest);
        bool scheduleDrain;
        lock (_gate)
        {
            _currentRequest = exactRequest;
            _currentFinality = null;
            _highestTransport = WorkerRunTransportPhase.Admitted;
            _cancellationWon = cancellationAlreadyWon;
            scheduleDrain = PublishUnderGate(
                cancellationAlreadyWon
                    ? ProcessAssetsWorkerState.Cancelling
                    : ProcessAssetsWorkerState.Starting,
                failureSummary: null);
        }
        ScheduleDrainIfNeeded(scheduleDrain);
    }

    void IProcessAssetsWorkerStatusSink.ObserveTransport(
        ProcessingRunRequest exactRequest,
        WorkerRunTransportPhase phase)
    {
        ArgumentNullException.ThrowIfNull(exactRequest);
        bool scheduleDrain;
        lock (_gate)
        {
            if (!ReferenceEquals(_currentRequest, exactRequest))
            {
                return;
            }

            if (_currentFinality is not null)
            {
                return;
            }

            if (phase <= _highestTransport)
            {
                return;
            }

            var mapped = MapTransportPhase(phase, _current.Worker, _cancellationWon);
            _highestTransport = phase;
            scheduleDrain = PublishUnderGate(mapped, _current.FailureSummary);
        }
        ScheduleDrainIfNeeded(scheduleDrain);
    }

    void IProcessAssetsWorkerStatusSink.ObserveCancellation(ProcessingRunRequest exactRequest)
    {
        ArgumentNullException.ThrowIfNull(exactRequest);
        bool scheduleDrain;
        lock (_gate)
        {
            if (!ReferenceEquals(_currentRequest, exactRequest))
            {
                return;
            }

            if (_currentFinality is not null)
            {
                return;
            }

            _cancellationWon = true;
            scheduleDrain = _current.Worker == ProcessAssetsWorkerState.Failed
                ? false
                : PublishUnderGate(ProcessAssetsWorkerState.Cancelling, failureSummary: null);
        }
        ScheduleDrainIfNeeded(scheduleDrain);
    }

    void IProcessAssetsWorkerStatusSink.ObserveFinality(
        ProcessingRunRequest exactRequest,
        ProcessingRunOutcome outcome,
        WorkerRunFailureCategory category)
    {
        ArgumentNullException.ThrowIfNull(exactRequest);
        bool scheduleDrain;
        lock (_gate)
        {
            if (!ReferenceEquals(_currentRequest, exactRequest))
            {
                return;
            }

            _currentFinality = outcome;
            scheduleDrain = outcome == ProcessingRunOutcome.Failed
                ? PublishUnderGate(
                    ProcessAssetsWorkerState.Failed,
                    WorkerRunDiagnostics.Describe(category))
                : false;
        }
        ScheduleDrainIfNeeded(scheduleDrain);
    }

    void IProcessAssetsWorkerStatusSink.Release(ProcessingRunRequest exactRequest)
    {
        ArgumentNullException.ThrowIfNull(exactRequest);
        bool scheduleDrain;
        lock (_gate)
        {
            if (!ReferenceEquals(_currentRequest, exactRequest))
            {
                return;
            }

            var retainFailure = _currentFinality == ProcessingRunOutcome.Failed
                && _current.Worker == ProcessAssetsWorkerState.Failed;
            _currentRequest = null;
            _currentFinality = null;
            _cancellationWon = false;
            scheduleDrain = retainFailure
                ? false
                : PublishUnderGate(ProcessAssetsWorkerState.Idle, failureSummary: null);
        }
        ScheduleDrainIfNeeded(scheduleDrain);
    }

    internal static ProcessAssetsWorkerState MapTransportPhase(
        WorkerRunTransportPhase phase,
        ProcessAssetsWorkerState current,
        bool cancellationWon)
    {
        if (current == ProcessAssetsWorkerState.Failed)
        {
            return current;
        }

        return phase switch
        {
            WorkerRunTransportPhase.Admitted or
            WorkerRunTransportPhase.Resolving or
            WorkerRunTransportPhase.Starting or
            WorkerRunTransportPhase.PreReady or
            WorkerRunTransportPhase.Ready => cancellationWon
                ? ProcessAssetsWorkerState.Cancelling
                : ProcessAssetsWorkerState.Starting,
            WorkerRunTransportPhase.Accepted => cancellationWon
                ? ProcessAssetsWorkerState.Cancelling
                : ProcessAssetsWorkerState.Running,
            WorkerRunTransportPhase.Draining => cancellationWon
                ? ProcessAssetsWorkerState.Cancelling
                : current,
            WorkerRunTransportPhase.EvidenceFinal or WorkerRunTransportPhase.Released => current,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
        };
    }

    private bool PublishUnderGate(
        ProcessAssetsWorkerState worker,
        string? failureSummary)
    {
        if (_current.Worker == worker
            && string.Equals(_current.FailureSummary, failureSummary, StringComparison.Ordinal))
        {
            return false;
        }

        _current = _current with
        {
            Worker = worker,
            FailureSummary = failureSummary,
            Revision = checked(_current.Revision + 1)
        };
        _notifications.Enqueue(new Notification(_current, [.. _subscriptions]));
        if (_notificationDrainScheduled)
        {
            return false;
        }

        _notificationDrainScheduled = true;
        return true;
    }

    private void ScheduleDrainIfNeeded(bool scheduleDrain)
    {
        if (!scheduleDrain)
        {
            return;
        }

        try
        {
            _ = Task.Run(DrainNotifications);
        }
        catch
        {
            lock (_gate)
            {
                _notificationDrainScheduled = false;
            }
        }
    }

    private void DrainNotifications()
    {
        while (true)
        {
            Notification notification;
            lock (_gate)
            {
                if (_notifications.Count == 0)
                {
                    _notificationDrainScheduled = false;
                    return;
                }

                notification = _notifications.Dequeue();
            }

            foreach (var subscription in notification.Subscriptions)
            {
                subscription.Deliver(notification.Snapshot);
            }
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed record Notification(
        ProcessAssetsWebStatusSnapshot Snapshot,
        Subscription[] Subscriptions);

    private sealed class Subscription(
        ProcessAssetsWebStatus owner,
        Action<ProcessAssetsWebStatusSnapshot> changed) : IDisposable
    {
        private int _active = 1;

        internal void Deliver(ProcessAssetsWebStatusSnapshot snapshot)
        {
            if (Volatile.Read(ref _active) == 0)
            {
                return;
            }

            try
            {
                changed(snapshot);
            }
            catch
            {
                // UI observers cannot affect coordinator ownership or lifecycle finality.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0)
            {
                return;
            }

            owner.Unsubscribe(this);
        }
    }
}
