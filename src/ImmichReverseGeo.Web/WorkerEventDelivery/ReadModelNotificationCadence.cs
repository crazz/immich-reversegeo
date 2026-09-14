using System;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.WorkerEventDelivery;

// Mutations remain synchronous. This boundary retains one dirty revision and
// one pending dispatch while an observer is busy, never one task per mutation.
internal sealed class ReadModelNotificationCadence : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly Func<object, long, ValueTask> _dispatch;
    private ITimer? _timer;
    private object? _owner;
    private OwnerObservation? _ownerObservation;
    private long _revision;
    private long _finalRevision = -1;
    private long? _lastOrdinaryDispatch;
    private long? _timerStarted;
    private TimeSpan _timerDelay;
    private Dispatch? _dirty;
    private Dispatch? _pending;
    private bool _draining;
    private bool _disposed;
    private long _ordinary;
    private long _finalAccepted;
    private long _finalDispatched;
    private long _superseded;
    private long _stale;
    private long _failures;

    internal ReadModelNotificationCadence(
        TimeProvider time, TimeSpan interval, Func<object, long, ValueTask> dispatch)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(dispatch);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _time = time;
        _interval = interval;
        _dispatch = dispatch;
    }

    internal NotificationCadenceObservation Observation
    {
        get
        {
            lock (_gate)
            {
                return new(_ordinary, _finalAccepted, _finalDispatched, _superseded,
                    _stale, _failures, _pending is null ? 0 : 1, _draining);
            }
        }
    }

    internal void Bind(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(_owner, owner))
            {
                return;
            }

            _ownerObservation?.Freeze();
            _ownerObservation = new OwnerObservation();
            _owner = owner;
            _revision = 0;
            _finalRevision = -1;
            _dirty = null;
            _pending = null;
            CancelTimerUnderGate();
        }
    }

    internal OwnerObservation? CaptureOwner(object owner)
    {
        lock (_gate)
        {
            return ReferenceEquals(_owner, owner) ? _ownerObservation : null;
        }
    }

    internal void CompleteOwner(OwnerObservation? observation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(observation, _ownerObservation))
            {
                observation?.Freeze();
            }
        }
    }

    internal void Signal(object owner, long revision, bool final = false)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(owner, _owner) || revision < _revision)
            {
                _stale++;
                return;
            }

            if (revision <= _finalRevision)
            {
                return;
            }

            _revision = revision;
            var next = new Dispatch(owner, revision, final);
            if (final)
            {
                _finalRevision = revision;
                _finalAccepted++;
                _dirty = null;
                CancelTimerUnderGate();
                QueueUnderGate(next);
            }
            else
            {
                _dirty = next;
                if (_timerStarted is null)
                {
                    ScheduleUnderGate(_interval);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ownerObservation?.Freeze();
            _owner = null;
            _dirty = null;
            _pending = null;
            _timerStarted = null;
            _timer?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        // Timer callbacks only hand off bounded work. Never join a renderer here.
        if (_timer is { } timer)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnTimer(object? state)
    {
        lock (_gate)
        {
            if (_disposed || _timerStarted is not { } started)
            {
                return;
            }

            TimeSpan remaining = _timerDelay - _time.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero)
            {
                // A one-shot callback can be early or queued from an older
                // schedule. Retain the current deadline and rearm it; simply
                // returning would strand the dirty revision with no timer.
                _timer!.Change(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)),
                    Timeout.InfiniteTimeSpan);
                return;
            }

            _timerStarted = null;
            if (_dirty is { } dirty)
            {
                _dirty = null;
                QueueUnderGate(dirty);
            }
        }
    }

    private void QueueUnderGate(Dispatch next)
    {
        if (_pending is { } pending)
        {
            _superseded++;
            // A later snapshot may update the pending revision, but cannot
            // downgrade an already accepted final notification to cadence work.
            if (pending.Final && ReferenceEquals(pending.Owner, next.Owner))
            {
                next = next with { Final = true };
            }
        }

        _pending = next;
        if (!_draining)
        {
            _draining = true;
            _ = Task.Run(DrainAsync);
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            Dispatch next;
            lock (_gate)
            {
                if (_disposed || _pending is null)
                {
                    _draining = false;
                    return;
                }

                next = _pending;
                _pending = null;
                if (!ReferenceEquals(next.Owner, _owner))
                {
                    _stale++;
                    continue;
                }

                if (!next.Final && _lastOrdinaryDispatch is { } previous)
                {
                    TimeSpan remaining = _interval - _time.GetElapsedTime(previous);
                    if (remaining > TimeSpan.Zero)
                    {
                        _dirty = _dirty is { } dirty && dirty.Revision > next.Revision ? dirty : next;
                        ScheduleUnderGate(remaining);
                        _draining = false;
                        return;
                    }
                }

                if (next.Final)
                {
                    _finalDispatched++;
                }
                else
                {
                    _lastOrdinaryDispatch = _time.GetTimestamp();
                    _ordinary++;
                    _ownerObservation?.CountOrdinaryDispatch();
                }
            }

            try
            {
                // The read model also checks this exact owner at its dispatch seam.
                await _dispatch(next.Owner, next.Revision).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate)
                {
                    _failures++;
                }
            }
        }
    }

    private void ScheduleUnderGate(TimeSpan delay)
    {
        _timerStarted = _time.GetTimestamp();
        _timerDelay = delay;
        if (_timer is null)
        {
            _timer = _time.CreateTimer(OnTimer, null, delay, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void CancelTimerUnderGate()
    {
        _timerStarted = null;
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private sealed record Dispatch(object Owner, long Revision, bool Final);

    // Updated and frozen only by this cadence owner under its existing gate.
    // A retained handle never redirects to a subsequent job's observation.
    internal sealed class OwnerObservation
    {
        private readonly TaskCompletionSource<long> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _ordinary;
        internal long OrdinaryDispatched => Interlocked.Read(ref _ordinary);
        internal Task<long> FinalOrdinaryDispatched => _final.Task;

        internal void CountOrdinaryDispatch()
        {
            if (!_final.Task.IsCompleted && _ordinary < long.MaxValue)
            {
                _ordinary++;
            }
        }

        internal void Freeze() => _final.TrySetResult(_ordinary);
    }
}

internal sealed record NotificationCadenceObservation(
    long OrdinaryDispatched, long FinalAccepted, long FinalDispatched,
    long SupersededDispatches, long StaleRejected, long ObserverFailures,
    int PendingDispatches, bool DispatchActive);
