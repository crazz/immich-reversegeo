using System.Collections.Concurrent;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

// Delegate time to the existing clock, adding observations of exact timer kinds.
// Cadence and memory timers cannot satisfy a readiness or grace checkpoint.
internal sealed class MatrixClock : TimeProvider
{
    private readonly CancellationTestClock _clock = new();
    private readonly ConcurrentDictionary<(TimeSpan Due, TimeSpan Period), TaskCompletionSource> _created = new();
    public override long TimestampFrequency => _clock.TimestampFrequency;
    public override long GetTimestamp() => _clock.GetTimestamp();
    public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    internal int ActiveTimerCount => _clock.ActiveTimerCount;
    internal void Advance(TimeSpan amount) => _clock.Advance(amount);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = _clock.CreateTimer(callback, state, dueTime, period);
        _created.GetOrAdd((dueTime, period), static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        return timer;
    }

    internal Task WaitForOneShotAsync(TimeSpan due, string phase) => MatrixWait.ForAsync(
        _created.GetOrAdd((due, Timeout.InfiniteTimeSpan), static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task,
        phase);
}
