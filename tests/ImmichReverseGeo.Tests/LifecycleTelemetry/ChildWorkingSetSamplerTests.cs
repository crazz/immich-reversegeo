using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.LifecycleTelemetry;

[TestClass]
[TestCategory("Change66")]
public sealed class ChildWorkingSetSamplerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Sampling_AttemptsImmediatelyEverySecondAndOnceAtFinality()
    {
        var clock = new SamplingClock();
        var samples = new Queue<ChildWorkingSetObservation>([
            ChildWorkingSetObservation.Available(20),
            ChildWorkingSetObservation.Unavailable(ChildWorkingSetUnavailable.AccessDenied),
            ChildWorkingSetObservation.Available(70),
            ChildWorkingSetObservation.Available(0)]);
        await using var process = new SampleProcess(() => samples.Dequeue());
        var sampler = new ChildWorkingSetSampler(process, clock);
        Assert.AreEqual(1, process.Reads);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1000), clock.Timer!.Period);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1000), clock.Timer.DueTime);
        clock.Advance(999);
        Assert.AreEqual(1, process.Reads);
        clock.Advance(1);
        Assert.AreEqual(2, process.Reads);
        clock.Advance(1000);
        Assert.AreEqual(3, process.Reads);
        var summary = await sampler.CompleteAsync().WaitAsync(Bound);
        Assert.AreEqual(4, process.Reads);
        Assert.AreEqual(70L, summary.PeakBytes);
        Assert.AreEqual(3L, summary.SuccessfulSamples);
        Assert.AreSame(sampler.CompleteAsync(), sampler.CompleteAsync());
        clock.Advance(5000);
        Assert.AreEqual(4, process.Reads);
        Assert.IsTrue(clock.Timer.Disposed);
        Assert.AreEqual(1, clock.Timer.DisposeCalls);
    }

    [TestMethod]
    public async Task UnavailableReason_IsOrderIndependentAndNeverInventsZeroMemory()
    {
        ChildWorkingSetUnavailable[] reasons = [ChildWorkingSetUnavailable.ProcessExited,
            ChildWorkingSetUnavailable.SampleFailed, ChildWorkingSetUnavailable.AccessDenied, ChildWorkingSetUnavailable.NotSupported];
        for (int maximum = 0; maximum < reasons.Length; maximum++)
        {
            foreach (bool reverse in new[] { false, true })
            {
                var selected = reasons.Take(maximum + 1).ToArray();
                if (reverse)
                {
                    Array.Reverse(selected);
                }

                var clock = new SamplingClock();
                var sequence = new Queue<ChildWorkingSetUnavailable>(selected);
                await using var process = new SampleProcess(() => ChildWorkingSetObservation.Unavailable(
                    sequence.Count > 0 ? sequence.Dequeue() : ChildWorkingSetUnavailable.ProcessExited));
                var sampler = new ChildWorkingSetSampler(process, clock);
                for (int index = 1; index < selected.Length; index++)
                {
                    clock.Advance(1000);
                }

                var summary = await sampler.CompleteAsync().WaitAsync(Bound);
                Assert.IsNull(summary.PeakBytes);
                Assert.AreEqual(0L, summary.SuccessfulSamples);
                Assert.AreEqual(reasons[maximum], summary.UnavailableReason);
            }
        }
    }

    [TestMethod]
    public async Task ThrowingOrInvalidObservation_BecomesSampleFailure()
    {
        foreach (Func<ChildWorkingSetObservation> read in new Func<ChildWorkingSetObservation>[]
        {
            () => throw new UnauthorizedAccessException("secret-path-and-credentials"),
            () => ChildWorkingSetObservation.Available(-1),
            () => default
        })
        {
            await using var process = new SampleProcess(read);
            var sampler = new ChildWorkingSetSampler(process, new SamplingClock());
            var result = await sampler.CompleteAsync().WaitAsync(Bound);
            Assert.AreEqual(ChildWorkingSetUnavailable.SampleFailed, result.UnavailableReason);
            Assert.IsNull(result.PeakBytes);
            Assert.AreEqual(0L, result.SuccessfulSamples);
        }
    }

    [TestMethod]
    public async Task Finality_JoinsTheAlreadyEnteredTimerCallbackThenSamplesExactlyOnce()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new SamplingClock();
        int reads = 0;
        await using var process = new SampleProcess(() =>
        {
            if (Interlocked.Increment(ref reads) == 2)
            {
                entered.TrySetResult();
                if (!release.Wait(Bound))
                {
                    throw new TimeoutException("The test did not release its sample callback.");
                }
            }

            return ChildWorkingSetObservation.Available(reads * 10);
        });
        var sampler = new ChildWorkingSetSampler(process, clock);
        Task tick = Task.Run(() => clock.Advance(1000));
        Task<ChildWorkingSetSummary>? finishing = null;
        try
        {
            await entered.Task.WaitAsync(Bound);
            finishing = sampler.CompleteAsync();
            await clock.Timer!.DisposalRequested.Task.WaitAsync(Bound);
            Assert.IsFalse(finishing.IsCompleted, "Disposal has reached the active callback join; the callback is still held.");
            Assert.AreEqual(2, Volatile.Read(ref reads));
        }
        finally
        {
            release.Set();
            await tick.WaitAsync(Bound);
            finishing ??= sampler.CompleteAsync();
            await finishing.WaitAsync(Bound);
        }

        var summary = await finishing;
        Assert.AreEqual(3L, summary.SuccessfulSamples);
        Assert.AreEqual(30L, summary.PeakBytes);
        Assert.AreEqual(3, process.Reads);
        clock.Advance(1000);
        Assert.AreEqual(3, process.Reads);
    }

    [TestMethod]
    public async Task TimerCreationFailure_DoesNotLoseSuccessfulImmediateAndFinalSamples()
    {
        await using var process = new SampleProcess(() => ChildWorkingSetObservation.Available(35));
        var sampler = new ChildWorkingSetSampler(process, new ThrowingTimerClock());
        var summary = await sampler.CompleteAsync().WaitAsync(Bound);
        Assert.AreEqual(35L, summary.PeakBytes);
        Assert.AreEqual(2L, summary.SuccessfulSamples);
    }

    private sealed class SampleProcess(Func<ChildWorkingSetObservation> read) : IChildProcess
    {
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        public int ProcessId => 42;
        public Stream StandardInput => Stream.Null;
        public Stream StandardOutput => Stream.Null;
        public Stream StandardError => Stream.Null;
        public Task<int> WaitForExitAsync() => throw new InvalidOperationException("Sampling cannot wait for exit.");
        public ChildProcessExitState GetExitState() => throw new InvalidOperationException("Sampling cannot control exit.");
        public ChildProcessKillOutcome KillProcessTree() => throw new InvalidOperationException("Sampling cannot kill.");
        public ChildWorkingSetObservation ReadWorkingSet()
        {
            Interlocked.Increment(ref _reads);
            return read();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingTimerClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("secret-timer-provider");
    }

    private sealed class SamplingClock : TimeProvider
    {
        private long _milliseconds;
        internal SamplingTimer? Timer { get; private set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.IsNull(Timer, "One timer per sampler.");
            Timer = new(callback, state, dueTime, period);
            return Timer;
        }

        internal void Advance(long milliseconds)
        {
            _milliseconds += milliseconds;
            Timer?.Advance(_milliseconds);
        }
    }

    private sealed class SamplingTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private readonly object _gate = new();
        private long _next = (long)dueTime.TotalMilliseconds;
        private int _active;
        private readonly TaskCompletionSource _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposalRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TimeSpan DueTime => dueTime;
        internal TimeSpan Period => period;
        internal bool Disposed { get; private set; }
        internal int DisposeCalls { get; private set; }
        public bool Change(TimeSpan due, TimeSpan repeat) => throw new InvalidOperationException("Sampler must use its single periodic timer.");

        internal void Advance(long milliseconds)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (Disposed || milliseconds < _next)
                    {
                        return;
                    }
                    _active++;
                    _next += (long)period.TotalMilliseconds;
                }

                try
                {
                    callback(state);
                }
                finally
                {
                    lock (_gate)
                    {
                        _active--;
                        if (Disposed && _active == 0)
                        {
                            _joined.TrySetResult();
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                DisposeCalls++;
                Disposed = true;
                if (_active == 0)
                {
                    _joined.TrySetResult();
                }
                DisposalRequested.TrySetResult();
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return new(_joined.Task);
        }
    }
}
