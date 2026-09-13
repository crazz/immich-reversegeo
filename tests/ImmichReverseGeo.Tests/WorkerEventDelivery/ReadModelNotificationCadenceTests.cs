using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.WorkerEventDelivery;

namespace ImmichReverseGeo.Tests.WorkerEventDelivery;

[TestClass]
[TestCategory("Change65")]
public sealed class ReadModelNotificationCadenceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    [TestMethod]
    public async Task BurstAndContinuousUpdates_DispatchLatestAtMostTenTimesPerSecond()
    {
        var clock = new CancellationTestClock();
        var delivered = Channel.CreateUnbounded<long>();
        await using var cadence = new ReadModelNotificationCadence(clock, Interval, (_, revision) =>
        {
            delivered.Writer.TryWrite(revision);
            return ValueTask.CompletedTask;
        });
        var owner = new object();
        cadence.Bind(owner);
        for (int window = 1; window <= 10; window++)
        {
            for (int revision = (window - 1) * 1000 + 1; revision <= window * 1000; revision++)
            {
                cadence.Signal(owner, revision);
            }

            clock.Advance(TimeSpan.FromMilliseconds(99));
            Assert.AreEqual((long)window - 1, cadence.Observation.OrdinaryDispatched, "ordinary work cannot dispatch early");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.AreEqual((long)window * 1000, await delivered.Reader.ReadAsync().AsTask().WaitAsync(Bound));
        }

        Assert.AreEqual(10L, cadence.Observation.OrdinaryDispatched, "10,000 dirty revisions produce ten ordinary notifications in one virtual second");
    }

    [TestMethod]
    public async Task FinalBeforeTick_DispatchesImmediatelyAndCancelsSameRevisionTimer()
    {
        var clock = new CancellationTestClock();
        var delivered = Channel.CreateUnbounded<long>();
        await using var cadence = new ReadModelNotificationCadence(clock, Interval, (_, revision) =>
        {
            delivered.Writer.TryWrite(revision);
            return ValueTask.CompletedTask;
        });
        var owner = new object();
        cadence.Bind(owner);
        cadence.Signal(owner, 1);
        cadence.Signal(owner, 2, final: true);
        Assert.AreEqual(1L, cadence.Observation.FinalAccepted, "final dispatch is accepted before the terminal projection returns");
        Assert.AreEqual(2L, await delivered.Reader.ReadAsync().AsTask().WaitAsync(Bound));
        cadence.Signal(owner, 2, final: true);
        cadence.Signal(owner, 2);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0L, cadence.Observation.OrdinaryDispatched);
        Assert.AreEqual(1L, cadence.Observation.FinalAccepted);
        Assert.AreEqual(1L, cadence.Observation.FinalDispatched);
        Assert.AreEqual(0, cadence.Observation.PendingDispatches);
        Assert.IsFalse(delivered.Reader.TryRead(out _), "no duplicate final or delayed notification remains");
    }

    [TestMethod]
    public async Task BlockedObserver_HasOnePendingDispatchAndOldOwnerCannotNotifyNewOwner()
    {
        var clock = new CancellationTestClock();
        var entered = Signal();
        var release = Signal();
        var finished = Signal();
        var delivered = Channel.CreateUnbounded<(object Owner, long Revision)>();
        var oldOwner = new object();
        var currentOwner = oldOwner;
        await using var cadence = new ReadModelNotificationCadence(clock, Interval, async (owner, revision) =>
        {
            if (ReferenceEquals(owner, oldOwner))
            {
                entered.TrySetResult();
                await release.Task;
            }

            // This is the read model's required second ownership check.
            if (ReferenceEquals(owner, currentOwner))
            {
                delivered.Writer.TryWrite((owner, revision));
            }

            finished.TrySetResult();
        });
        cadence.Bind(oldOwner);
        try
        {
            cadence.Signal(oldOwner, 1);
            clock.Advance(Interval);
            await entered.Task.WaitAsync(Bound);
            for (int revision = 2; revision <= 100; revision++)
            {
                cadence.Signal(oldOwner, revision);
                clock.Advance(Interval);
            }

            Assert.AreEqual(1, cadence.Observation.PendingDispatches, "a stalled observer retains one pending notification");
            Assert.AreEqual(1L, cadence.Observation.OrdinaryDispatched);
            currentOwner = new object();
            cadence.Bind(currentOwner);
            cadence.Signal(oldOwner, 101, final: true);
            Assert.AreEqual(1L, cadence.Observation.StaleRejected);
            cadence.Signal(currentOwner, 1, final: true);
            release.TrySetResult();
            var next = await delivered.Reader.ReadAsync().AsTask().WaitAsync(Bound);
            Assert.AreSame(currentOwner, next.Owner);
            Assert.AreEqual(1L, next.Revision);
            Assert.IsFalse(delivered.Reader.TryRead(out _));
        }
        finally
        {
            release.TrySetResult();
            await finished.Task.WaitAsync(Bound);
        }
    }

    [TestMethod]
    public async Task Disposal_DoesNotWaitForRendererAndCancelsPendingWork()
    {
        var clock = new CancellationTestClock();
        var entered = Signal();
        var release = Signal();
        var finished = Signal();
        await using var cadence = new ReadModelNotificationCadence(clock, Interval, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            finished.TrySetResult();
        });
        var owner = new object();
        cadence.Bind(owner);
        try
        {
            cadence.Signal(owner, 1);
            clock.Advance(Interval);
            await entered.Task.WaitAsync(Bound);
            cadence.Signal(owner, 2);
            clock.Advance(Interval);
            Assert.AreEqual(1, cadence.Observation.PendingDispatches);
            await cadence.DisposeAsync().AsTask().WaitAsync(Bound);
            Assert.IsFalse(finished.Task.IsCompleted, "disposal returns while renderer is still held");
            Assert.AreEqual(0, cadence.Observation.PendingDispatches);
            cadence.Signal(owner, 3, final: true);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(0L, cadence.Observation.FinalAccepted);
            Assert.AreEqual(1L, cadence.Observation.OrdinaryDispatched);
            Assert.AreEqual(0, clock.ActiveTimerCount);
        }
        finally
        {
            release.TrySetResult();
            await finished.Task.WaitAsync(Bound);
        }
    }

    [TestMethod]
    public async Task PendingFinal_KeepsImmediatePriorityWhenLaterDirtyRevisionArrives()
    {
        var clock = new CancellationTestClock();
        var entered = Signal();
        var release = Signal();
        var finished = Signal();
        var delivered = Channel.CreateUnbounded<long>();
        await using var cadence = new ReadModelNotificationCadence(clock, Interval, async (_, revision) =>
        {
            if (revision == 1)
            {
                entered.TrySetResult();
                await release.Task;
                finished.TrySetResult();
            }
            else
            {
                delivered.Writer.TryWrite(revision);
            }
        });
        var owner = new object();
        cadence.Bind(owner);
        try
        {
            cadence.Signal(owner, 1);
            clock.Advance(Interval);
            await entered.Task.WaitAsync(Bound);
            cadence.Signal(owner, 2, final: true);
            cadence.Signal(owner, 3);
            clock.Advance(Interval);
            Assert.AreEqual(1, cadence.Observation.PendingDispatches);
            release.TrySetResult();
            Assert.AreEqual(3L, await delivered.Reader.ReadAsync().AsTask().WaitAsync(Bound), "pending notification observes the newest revision");
            Assert.AreEqual(1L, cadence.Observation.FinalAccepted);
            Assert.AreEqual(1L, cadence.Observation.FinalDispatched, "an accepted final cannot be downgraded by a later ordinary update");
            Assert.AreEqual(1L, cadence.Observation.OrdinaryDispatched);
        }
        finally
        {
            release.TrySetResult();
            await finished.Task.WaitAsync(Bound);
        }
    }

    [TestMethod]
    public async Task ObserverFailure_DoesNotBreakLaterFinalDispatch()
    {
        var clock = new CancellationTestClock();
        var failed = Signal();
        var final = Signal();
        await using var cadence = new ReadModelNotificationCadence(clock, Interval, (_, revision) =>
        {
            if (revision == 1)
            {
                failed.TrySetResult();
                throw new InvalidOperationException("controlled observer failure");
            }

            final.TrySetResult();
            return ValueTask.CompletedTask;
        });
        var owner = new object();
        cadence.Bind(owner);
        cadence.Signal(owner, 1);
        clock.Advance(Interval);
        await failed.Task.WaitAsync(Bound);
        cadence.Signal(owner, 2, final: true);
        await final.Task.WaitAsync(Bound);
        Assert.AreEqual(1L, cadence.Observation.ObserverFailures, "observer failure is bounded observational data");
        Assert.AreEqual(1L, cadence.Observation.FinalDispatched);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestMethod]
    public async Task EarlyOneShotCallback_RearmsRemainingIntervalWithoutLosingDirtyRevision()
    {
        var clock = new EarlyCallbackClock();
        var delivered = Channel.CreateUnbounded<long>();
        await using var cadence = new ReadModelNotificationCadence(clock, Interval, (_, revision) =>
        {
            delivered.Writer.TryWrite(revision);
            return ValueTask.CompletedTask;
        });
        var owner = new object();
        cadence.Bind(owner);
        cadence.Signal(owner, 1);
        clock.FireAfter(TimeSpan.FromMilliseconds(99));
        Assert.AreEqual(0L, cadence.Observation.OrdinaryDispatched, "an early callback must not violate the cadence");
        Assert.AreEqual(TimeSpan.FromMilliseconds(1), clock.Timer!.NextDue,
            "the remaining interval must be scheduled after a one-shot callback fires early");
        clock.FireAfter(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1L, await delivered.Reader.ReadAsync().AsTask().WaitAsync(Bound));
        Assert.AreEqual(1L, cadence.Observation.OrdinaryDispatched);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, clock.Timer.NextDue);
    }

    private sealed class EarlyCallbackClock : TimeProvider
    {
        private long _ticks;
        internal EarlyTimer? Timer { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Timer = new EarlyTimer(callback, state, dueTime);

        internal void FireAfter(TimeSpan elapsed)
        {
            _ticks += elapsed.Ticks;
            Timer!.Fire();
        }
    }

    private sealed class EarlyTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        internal TimeSpan NextDue { get; private set; } = dueTime;
        public bool Change(TimeSpan next, TimeSpan period)
        {
            NextDue = next;
            return true;
        }
        internal void Fire()
        {
            NextDue = Timeout.InfiniteTimeSpan;
            callback(state);
        }
        public void Dispose() => NextDue = Timeout.InfiniteTimeSpan;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
