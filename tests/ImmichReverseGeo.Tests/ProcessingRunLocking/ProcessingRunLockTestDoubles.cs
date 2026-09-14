using System.Collections.Concurrent;
using System.Data;
using ImmichReverseGeo.Web.ProcessingRunLocking;

namespace ImmichReverseGeo.Tests.ProcessingRunLocking;

internal sealed class RecordingRunLockSessionFactory(RecordingRunLockSession session)
    : IProcessingRunLockSessionFactory
{
    internal Exception? CreateFailure { get; set; }

    internal int CreateCalls { get; private set; }

    public IProcessingRunLockSession CreateSession()
    {
        CreateCalls++;
        if (CreateFailure is not null)
        {
            throw CreateFailure;
        }

        return session;
    }
}

internal sealed class RecordingRunLockSession : IProcessingRunLockSession
{
    private int _activeCommands;
    private ConnectionState _state = ConnectionState.Closed;

    internal ConcurrentQueue<string> Calls { get; } = new();

    internal ConcurrentQueue<ProcessingRunLockCommand> Commands { get; } = new();

    internal Action? ClearPoolBehavior { get; set; }

    internal Func<Task> DisposeBehavior { get; set; } = static () => Task.CompletedTask;

    internal Func<CancellationToken, Task> OpenBehavior { get; set; } = static _ => Task.CompletedTask;

    internal Func<ProcessingRunLockCommand, CancellationToken, Task<object?>> ExecuteBehavior { get; set; } =
        static (command, _) => Task.FromResult(DefaultScalar(command));

    internal int ClearPoolCalls { get; private set; }

    internal int DisposeCalls { get; private set; }

    internal int MaxConcurrentCommands { get; private set; }

    public ConnectionState State => _state;

    public event StateChangeEventHandler? StateChanged;

    public async ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        Calls.Enqueue("open");
        await OpenBehavior(cancellationToken).ConfigureAwait(false);
        SetState(ConnectionState.Open);
    }

    public async ValueTask<object?> ExecuteScalarAsync(
        ProcessingRunLockCommand command,
        CancellationToken cancellationToken)
    {
        Commands.Enqueue(command);
        Calls.Enqueue(CommandCall(command));
        int active = Interlocked.Increment(ref _activeCommands);
        MaxConcurrentCommands = Math.Max(MaxConcurrentCommands, active);
        try
        {
            return await ExecuteBehavior(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _activeCommands);
        }
    }

    public void ClearPool()
    {
        Calls.Enqueue("clear-pool");
        ClearPoolCalls++;
        ClearPoolBehavior?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Calls.Enqueue("dispose");
        DisposeCalls++;
        await DisposeBehavior().ConfigureAwait(false);
        SetState(ConnectionState.Closed);
    }

    internal void SetState(ConnectionState state)
    {
        ConnectionState original = _state;
        _state = state;
        StateChanged?.Invoke(this, new StateChangeEventArgs(original, state));
    }

    private static object? DefaultScalar(ProcessingRunLockCommand command)
    {
        if (command == ProcessingRunLockCommand.Acquire || command == ProcessingRunLockCommand.Release)
        {
            return true;
        }

        return 1;
    }

    private static string CommandCall(ProcessingRunLockCommand command)
    {
        if (command == ProcessingRunLockCommand.Acquire)
        {
            return "acquire";
        }

        if (command == ProcessingRunLockCommand.Release)
        {
            return "release";
        }

        return "probe";
    }
}

internal sealed class ManualRunLockTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ManualTimer timer;
        lock (_gate)
        {
            timer = new ManualTimer(this, callback, state, _now + dueTime, period);
            _timers.Add(timer);
        }

        return timer;
    }

    internal void Advance(TimeSpan amount)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _now += amount;
            due = _timers.Where(timer => timer.IsDue(_now)).ToArray();
            foreach (ManualTimer timer in due)
            {
                timer.MarkFired(_now);
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(
        ManualRunLockTimeProvider owner,
        TimerCallback callback,
        object? state,
        DateTimeOffset due,
        TimeSpan period) : ITimer
    {
        private bool _disposed;
        private DateTimeOffset _due = due;

        public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
        {
            if (_disposed)
            {
                return false;
            }

            _due = owner.GetUtcNow() + dueTime;
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        internal bool IsDue(DateTimeOffset now) => !_disposed && _due <= now;

        internal void MarkFired(DateTimeOffset now)
        {
            if (period == Timeout.InfiniteTimeSpan)
            {
                _disposed = true;
                owner.Remove(this);
            }
            else
            {
                _due = now + period;
            }
        }

        internal void Fire() => callback(state);
    }
}
