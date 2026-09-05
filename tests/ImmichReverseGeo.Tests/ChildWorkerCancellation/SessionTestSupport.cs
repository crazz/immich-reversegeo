using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.ChildWorkerCancellation;

internal static class SessionTestSupport
{
    internal static readonly DateTimeOffset Start =
        new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    internal static ProcessingRunRequest CreateRequest()
        => new(Guid.NewGuid(), ProcessingRunTrigger.Manual);

    internal static async Task<SessionFixture> CreateAsync(
        CancellationTestClock? clock = null,
        SessionInputStream? input = null,
        ChildProcessKillOutcome killOutcome = ChildProcessKillOutcome.Requested,
        bool exitOnKill = false)
    {
        clock ??= new CancellationTestClock(Start);
        input ??= new SessionInputStream();
        var process = new SessionTestProcess(input, killOutcome, exitOnKill);
        var request = CreateRequest();
        var sink = new SessionRecordingSink();
        var session = await ChildWorkerSession.CreateAsync(
            process,
            request,
            sink,
            new ChildWorkerLauncherOptions
            {
                TimeProvider = clock,
                ReadyTimeout = Timeout.InfiniteTimeSpan
            },
            new ChildWorkerObserverArmingAcknowledgements());

        return new SessionFixture(clock, input, process, request, sink, session);
    }

    internal static void EmitReady(SessionFixture fixture)
    {
        var ready = WorkerProtocolMapper.Ready(1, fixture.Clock.GetUtcNow());
        fixture.Process.StandardOutputSource.Enqueue(Frame(ready));
    }

    internal static byte[] Frame(WorkerProtocolEvent @event)
    {
        var serialized = WorkerProtocolCodec.Serialize(@event);
        var frame = new byte[serialized.Length + 1];
        serialized.CopyTo(frame, 0);
        frame[^1] = (byte)10;
        return frame;
    }

    internal sealed record SessionFixture(
        CancellationTestClock Clock,
        SessionInputStream Input,
        SessionTestProcess Process,
        ProcessingRunRequest Request,
        SessionRecordingSink Sink,
        ChildWorkerSession Session);
}

internal sealed class CancellationTestClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<CancellationTestTimer> _timers = [];
    private TaskCompletionSource _nextTimerCreated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _now;
    private long _timerGeneration;
    private int _timerDisposeCalls;
    private int _blockTimerCallbackAfterInvocation;
    private readonly ManualResetEventSlim _timerCallbackRelease = new(false);
    private readonly TaskCompletionSource _timerCallbackInvoked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal CancellationTestClock(DateTimeOffset? start = null)
    {
        _now = (start ?? DateTimeOffset.UnixEpoch).ToUniversalTime();
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    internal long TimerGeneration
    {
        get
        {
            lock (_gate)
            {
                return _timerGeneration;
            }
        }
    }

    internal int TimerDisposeCalls => Volatile.Read(ref _timerDisposeCalls);
    internal Task TimerCallbackInvoked => _timerCallbackInvoked.Task;

    internal bool BlockTimerCallbackAfterInvocation
    {
        get => Volatile.Read(ref _blockTimerCallbackAfterInvocation) != 0;
        set => Volatile.Write(ref _blockTimerCallbackAfterInvocation, value ? 1 : 0);
    }

    internal int ActiveTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(timer => !timer.IsDisposed);
            }
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(dueTime));
        }

        TaskCompletionSource created;
        CancellationTestTimer timer;
        lock (_gate)
        {
            timer = new CancellationTestTimer(
                this,
                callback,
                state,
                dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime);
            _timers.Add(timer);
            _timerGeneration++;
            created = _nextTimerCreated;
            _nextTimerCreated = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        created.TrySetResult();
        return timer;
    }

    internal Task WaitForTimerCreatedAsync(
        long afterGeneration,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _timerGeneration > afterGeneration
                ? Task.CompletedTask
                : _nextTimerCreated.Task.WaitAsync(cancellationToken);
        }
    }

    internal void ReleaseTimerCallback() => _timerCallbackRelease.Set();

    internal void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        CancellationTestTimer[] due;
        lock (_gate)
        {
            _now += amount;
            due = _timers
                .Where(timer => timer.IsDue(_now))
                .ToArray();
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class CancellationTestTimer(
        CancellationTestClock owner,
        TimerCallback callback,
        object? state,
        DateTimeOffset? dueAt) : ITimer
    {
        private readonly TaskCompletionSource _callbackSettled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        private int _fired;
        private DateTimeOffset? _dueAt = dueAt;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        internal bool IsDue(DateTimeOffset now)
            => !IsDisposed
                && Volatile.Read(ref _fired) == 0
                && _dueAt is not null
                && _dueAt <= now;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (IsDisposed)
                {
                    return false;
                }

                _dueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner._now + dueTime;
                Volatile.Write(ref _fired, 0);
                return true;
            }
        }

        public void Dispose()
        {
            Interlocked.Increment(ref owner._timerDisposeCalls);
            if (Interlocked.Exchange(ref _disposed, 1) == 0
                && Volatile.Read(ref _fired) == 0)
            {
                _callbackSettled.TrySetResult();
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return new ValueTask(_callbackSettled.Task);
        }

        internal void Fire()
        {
            if (IsDisposed || Interlocked.Exchange(ref _fired, 1) != 0)
            {
                return;
            }

            try
            {
                callback(state);
                if (owner.BlockTimerCallbackAfterInvocation)
                {
                    owner._timerCallbackInvoked.TrySetResult();
                    owner._timerCallbackRelease.Wait();
                }
            }
            finally
            {
                _callbackSettled.TrySetResult();
            }
        }
    }
}

internal sealed class SessionTestProcess : IChildProcess
{
    private readonly TaskCompletionSource<int> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ChildProcessKillOutcome _killOutcome;
    private readonly bool _exitOnKill;
    private int _exitState;
    private int _disposeCalls;
    private int _killCalls;

    internal SessionTestProcess(
        SessionInputStream input,
        ChildProcessKillOutcome killOutcome,
        bool exitOnKill)
    {
        StandardInput = input;
        StandardOutputSource = new SessionOutputStream();
        StandardErrorSource = new SessionOutputStream();
        StandardOutput = StandardOutputSource;
        StandardError = StandardErrorSource;
        _killOutcome = killOutcome;
        _exitOnKill = exitOnKill;
    }

    public int ProcessId => 2801;
    public Stream StandardInput { get; }
    public Stream StandardOutput { get; }
    public Stream StandardError { get; }
    internal SessionOutputStream StandardOutputSource { get; }
    internal SessionOutputStream StandardErrorSource { get; }
    internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
    internal int KillCalls => Volatile.Read(ref _killCalls);
    internal TaskCompletionSource KillObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<int> WaitForExitAsync() => _exit.Task;

    public ChildProcessExitState GetExitState()
        => Volatile.Read(ref _exitState) == 0
            ? ChildProcessExitState.Alive
            : ChildProcessExitState.Exited;

    public ChildProcessKillOutcome KillProcessTree()
    {
        Interlocked.Increment(ref _killCalls);
        KillObserved.TrySetResult();

        if (GetExitState() == ChildProcessExitState.Exited)
        {
            return ChildProcessKillOutcome.AlreadyExited;
        }

        if (_killOutcome == ChildProcessKillOutcome.Requested && _exitOnKill)
        {
            Exit(137);
        }

        return _killOutcome;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCalls);
        return ValueTask.CompletedTask;
    }

    internal void Exit(int code)
    {
        if (Interlocked.Exchange(ref _exitState, 1) == 0)
        {
            _exit.TrySetResult(code);
            CompleteStreams();
        }
    }

    internal void FailExitObservation()
        => _exit.TrySetException(new InvalidOperationException("Synthetic exit observation failure."));

    internal void ConfirmPhysicalExitWithoutCode()
    {
        Interlocked.Exchange(ref _exitState, 1);
        CompleteStreams();
    }

    internal void CompleteStreams()
    {
        StandardOutputSource.Complete();
        StandardErrorSource.Complete();
    }
}

internal sealed class SessionRecordingSink : IWorkerProtocolEventSink
{
    private readonly object _gate = new();
    private readonly List<WorkerProtocolEvent> _events = [];

    internal IReadOnlyList<WorkerProtocolEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public ValueTask AcceptAsync(
        WorkerProtocolEvent @event,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _events.Add(@event);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class SessionInputStream : Stream
{
    private readonly object _gate = new();
    private readonly MemoryStream _written = new();
    private readonly TaskCompletionSource _secondFlush =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstWrite =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondWrite =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _blockedWriteRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _synchronousWriteRelease = new(false);
    private readonly TaskCompletionSource _synchronousWriteEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _writeCalls;
    private int _flushCalls;
    private int _disposeCalls;
    private int _blockWriteCall;

    internal int WriteCalls => Volatile.Read(ref _writeCalls);
    internal int FlushCalls => Volatile.Read(ref _flushCalls);
    internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
    internal Task FirstWrite => _firstWrite.Task;
    internal Task SecondWrite => _secondWrite.Task;
    internal Task SecondFlush => _secondFlush.Task;
    internal Task SynchronousWriteEntered => _synchronousWriteEntered.Task;

    internal int BlockWriteCall
    {
        get => Volatile.Read(ref _blockWriteCall);
        set => Volatile.Write(ref _blockWriteCall, value);
    }

    internal int SynchronousBlockWriteCall { get; set; }
    internal int FaultWriteCall { get; set; }
    internal int FaultFlushCall { get; set; }
    internal Action<int>? WriteBoundary { get; set; }
    internal Action<int>? FlushBoundary { get; set; }

    internal IReadOnlyList<byte[]> Frames
    {
        get
        {
            byte[] bytes;
            lock (_gate)
            {
                bytes = _written.ToArray();
            }

            var frames = new List<byte[]>();
            var start = 0;
            for (var index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] != 10)
                {
                    continue;
                }

                frames.Add(bytes[start..index]);
                start = index + 1;
            }

            return frames;
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _flushCalls);
        if (call == 2)
        {
            _secondFlush.TrySetResult();
        }

        FlushBoundary?.Invoke(call);
        return call == FaultFlushCall
            ? Task.FromException(new IOException("Synthetic flush failure."))
            : Task.CompletedTask;
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _writeCalls);
        if (call == 1)
        {
            _firstWrite.TrySetResult();
        }
        else if (call == 2)
        {
            _secondWrite.TrySetResult();
        }

        WriteBoundary?.Invoke(call);
        if (call == FaultWriteCall)
        {
            throw new IOException("Synthetic write failure.");
        }

        if (call == SynchronousBlockWriteCall)
        {
            _synchronousWriteEntered.TrySetResult();
            _synchronousWriteRelease.Wait(CancellationToken.None);
        }

        if (call == BlockWriteCall)
        {
            await _blockedWriteRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (DisposeCalls != 0)
        {
            throw new ObjectDisposedException(nameof(SessionInputStream));
        }

        lock (_gate)
        {
            _written.Write(buffer.Span);
        }
    }

    public override ValueTask DisposeAsync()
    {
        if (Interlocked.Increment(ref _disposeCalls) == 1)
        {
            _blockedWriteRelease.TrySetResult();
        }

        return ValueTask.CompletedTask;
    }

    internal void ReleaseBlockedWrite() => _blockedWriteRelease.TrySetResult();
    internal void ReleaseSynchronousWrite() => _synchronousWriteRelease.Set();

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();
}

internal sealed class SessionOutputStream : Stream
{
    private readonly object _gate = new();
    private readonly Queue<byte> _bytes = [];
    private TaskCompletionSource _changed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;
    private int _disposeCalls;

    internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

    internal void Enqueue(ReadOnlySpan<byte> bytes)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (_completed)
            {
                throw new InvalidOperationException("The output stream is complete.");
            }

            foreach (var value in bytes)
            {
                _bytes.Enqueue(value);
            }

            changed = _changed;
            _changed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }

    internal void Complete()
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            _completed = true;
            changed = _changed;
            _changed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_bytes.Count != 0)
                {
                    var count = Math.Min(buffer.Length, _bytes.Count);
                    for (var index = 0; index < count; index++)
                    {
                        buffer.Span[index] = _bytes.Dequeue();
                    }

                    return count;
                }

                if (_completed)
                {
                    return 0;
                }

                wait = _changed.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override ValueTask DisposeAsync()
    {
        if (Interlocked.Increment(ref _disposeCalls) == 1)
        {
            Complete();
        }

        return ValueTask.CompletedTask;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();
}
