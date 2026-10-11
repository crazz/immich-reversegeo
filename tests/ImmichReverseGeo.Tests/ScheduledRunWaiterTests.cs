using System.Threading.Channels;
using Cronos;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ScheduledRunWaiterTests
{
    [TestMethod]
    public async Task AnnualCron_WaitsUntilNextOccurrenceWithoutOversizedTimers()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var next = CronExpression.Parse("0 0 1 1 *")
            .GetNextOccurrence(clock.GetUtcNow().UtcDateTime, TimeZoneInfo.Utc)
            ?? throw new InvalidOperationException("Annual cron must have a next occurrence.");
        var deadline = new DateTimeOffset(next);
        var wait = ScheduledRunWaiter.WaitUntilAsync(deadline, clock, CancellationToken.None);

        while (clock.GetUtcNow() < deadline)
        {
            Assert.IsFalse(wait.IsCompleted, "Processing must not start before the annual deadline.");
            var timer = await clock.NextTimerAsync();
            Assert.IsTrue(timer.DueTime > TimeSpan.Zero && timer.DueTime <= TimeSpan.FromDays(1));
            clock.Advance(timer.DueTime);
            timer.Fire();
        }

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(deadline, clock.GetUtcNow());
    }

    [TestMethod]
    public async Task FinalInterval_WaitsOnlyUntilTheDeadline()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var deadline = clock.GetUtcNow().AddDays(1).AddMinutes(10);
        var wait = ScheduledRunWaiter.WaitUntilAsync(deadline, clock, CancellationToken.None);
        var first = await clock.NextTimerAsync();
        clock.Advance(first.DueTime);
        first.Fire();

        var final = await clock.NextTimerAsync();
        Assert.AreEqual(TimeSpan.FromMinutes(10), final.DueTime);
        Assert.IsFalse(wait.IsCompleted);
        clock.Advance(final.DueTime);
        final.Fire();

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(deadline, clock.GetUtcNow());
    }

    [TestMethod]
    public async Task CancellationDuringLongWait_DisposesTimerAndStopsPromptly()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        using var cancellation = new CancellationTokenSource();
        var wait = ScheduledRunWaiter.WaitUntilAsync(clock.GetUtcNow().AddYears(1), clock, cancellation.Token);
        var timer = await clock.NextTimerAsync();

        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(timer.IsDisposed);
    }

    [TestMethod]
    public async Task DeadlineAlreadyPassed_DoesNotCreateTimer()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

        await ScheduledRunWaiter.WaitUntilAsync(clock.GetUtcNow().AddMinutes(-1), clock, CancellationToken.None);

        Assert.IsFalse(clock.HasPendingTimer);
    }

    [TestMethod]
    public async Task ClockMovesForwardPastDeadline_DoesNotWaitForOriginalDuration()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var wait = ScheduledRunWaiter.WaitUntilAsync(clock.GetUtcNow().AddDays(10), clock, CancellationToken.None);
        var timer = await clock.NextTimerAsync();

        clock.Advance(TimeSpan.FromDays(20));
        timer.Fire();

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(clock.HasPendingTimer);
    }

    [TestMethod]
    public async Task ClockMovesBackward_DoesNotStartBeforeUtcDeadline()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var deadline = clock.GetUtcNow().AddHours(1);
        var wait = ScheduledRunWaiter.WaitUntilAsync(deadline, clock, CancellationToken.None);
        var first = await clock.NextTimerAsync();

        clock.Advance(TimeSpan.FromHours(-1));
        first.Fire();

        var second = await clock.NextTimerAsync();
        Assert.IsFalse(wait.IsCompleted);
        Assert.AreEqual(TimeSpan.FromHours(2), second.DueTime);
        clock.Advance(second.DueTime);
        second.Fire();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private long _utcTicks = utcNow.UtcTicks;
        private readonly Channel<ManualTimer> _timers = Channel.CreateUnbounded<ManualTimer>();

        public bool HasPendingTimer => _timers.Reader.TryPeek(out _);

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan interval)
        {
            Interlocked.Add(ref _utcTicks, interval.Ticks);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            _timers.Writer.TryWrite(timer);
            return timer;
        }

        public Task<ManualTimer> NextTimerAsync() =>
            _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private int _disposed;

        public TimeSpan DueTime { get; private set; } = dueTime;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public bool Change(TimeSpan newDueTime, TimeSpan period)
        {
            if (IsDisposed)
            {
                return false;
            }

            DueTime = newDueTime;
            return true;
        }

        public void Fire()
        {
            if (!IsDisposed)
            {
                callback(state);
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
