using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.LifecycleTelemetry;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Tests.LifecycleTelemetry;

[TestClass]
[TestCategory("Change66")]
public sealed class CancellationTelemetryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task CooperativeStop_JoinsRepeatedRequestsAndTimesExitBeforeDelayedStderrDrain()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ReadyAsync();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(10));
        var stop = harness.Session.RequestStop();
        var repeated = harness.Session.RequestTermination(new ChildWorkerTerminationRequest(
            ChildWorkerStopRequest.Capture(harness.Clock), ChildWorkerTerminationIntent.Shutdown));
        Assert.AreSame(stop, repeated);
        await harness.Input.SecondFlush.WaitAsync(Bound);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(5));
        harness.Process.Exit(130);
        await harness.Session.PhysicalExitConfirmed.WaitAsync(Bound);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.IsFalse(stop.IsCompleted, "The owned stderr pump has not reached finality.");
        Assert.IsFalse(harness.Logs.Entries.Any(entry => entry.Event.Id == 6621));
        harness.Error.Complete();
        await stop.WaitAsync(Bound);
        await harness.Session.Telemetry!.CancellationObservation.WaitAsync(Bound);
        var events = harness.Logs.Entries.Where(entry => entry.Event.Id is >= 6620 and <= 6623).ToArray();
        CollectionAssert.AreEqual(new[] { 6620, 6621 }, events.Select(entry => entry.Event.Id).ToArray());
        Assert.AreEqual("user", events[0]["cancellation_reason"]);
        Assert.AreEqual("ready", events[0]["cancellation_phase"]);
        Assert.AreEqual(10000L, events[0]["grace_period_ms"]);
        Assert.AreEqual(5L, events[1]["cancellation_duration_ms"], "The 500 ms drain delay must be excluded.");
        Assert.AreEqual(false, events[1]["terminal_observed"]);
        Assert.AreEqual(true, events[1]["exit_observed"]);
        Assert.AreEqual(0, harness.Process.KillCalls);
        Assert.AreEqual(0, harness.Clock.ActiveTimerCount);
    }

    [TestMethod]
    [DataRow((int)ChildProcessKillOutcome.Requested, "succeeded")]
    [DataRow((int)ChildProcessKillOutcome.PermissionDenied, "failed")]
    [DataRow((int)ChildProcessKillOutcome.Unsupported, "not-supported")]
    public async Task GraceExpiry_ReportsOneAttemptAndWaitsForExitAndDrain(int kill, string expected)
    {
        await using var harness = await Harness.CreateAsync((ChildProcessKillOutcome)kill);
        await harness.ReadyAsync();
        var stop = harness.Session.RequestStop();
        await harness.Input.SecondFlush.WaitAsync(Bound);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(9999));
        Assert.AreEqual(0, harness.Process.KillCalls);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await harness.Process.KillObserved.Task.WaitAsync(Bound);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(7));
        harness.Process.Exit(137);
        await harness.Session.PhysicalExitConfirmed.WaitAsync(Bound);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.IsFalse(stop.IsCompleted);
        Assert.IsFalse(harness.Logs.Entries.Any(entry => entry.Event.Id == 6623));
        harness.Error.Complete();
        await stop.WaitAsync(Bound);
        await harness.Session.Telemetry!.CancellationObservation.WaitAsync(Bound);
        var events = harness.Logs.Entries.Where(entry => entry.Event.Id is >= 6620 and <= 6623).ToArray();
        CollectionAssert.AreEqual(new[] { 6620, 6622, 6623 }, events.Select(entry => entry.Event.Id).ToArray());
        Assert.AreEqual(10000L, events[1]["grace_elapsed_ms"]);
        Assert.AreEqual("kill-process-tree", events[1]["escalation_action"]);
        Assert.AreEqual(expected, events[2]["kill_result"]);
        Assert.AreEqual(7L, events[2]["escalation_duration_ms"]);
        Assert.AreEqual(1, harness.Process.KillCalls);
    }

    [TestMethod]
    public async Task BlockedCancellationLogger_DoesNotDelayTheExistingKillDeadline()
    {
        using var logger = new BlockingCancellationLogger();
        await using var harness = await Harness.CreateAsync(logger: logger);
        await harness.ReadyAsync();
        var stop = harness.Session.RequestStop();
        try
        {
            await logger.Entered.Task.WaitAsync(Bound);
            harness.Clock.Advance(ChildWorkerCancellationPolicy.Grace);
            await harness.Process.KillObserved.Task.WaitAsync(Bound);
            Assert.AreEqual(1, harness.Process.KillCalls, "Control proceeds while the log provider is held.");
            harness.Process.Exit(137);
            harness.Error.Complete();
            await stop.WaitAsync(Bound);
        }
        finally
        {
            logger.Release.Set();
        }

        await harness.Session.Telemetry!.CancellationObservation.WaitAsync(Bound);
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal readonly CancellationTestClock Clock = new();
        internal readonly SessionInputStream Input = new();
        internal readonly SessionOutputStream Error = new();
        internal readonly RecordingLifecycleLogs Logs = new();
        internal readonly SessionTestProcess Process;
        internal ChildWorkerSession Session = null!;

        private Harness(ChildProcessKillOutcome kill)
        {
            Process = new(Input, kill, false);
        }

        internal static async Task<Harness> CreateAsync(ChildProcessKillOutcome kill = ChildProcessKillOutcome.Requested,
            ILogger? logger = null)
        {
            var harness = new Harness(kill);
            var launcher = new ChildWorkerLauncher(new Factory(new HeldErrorProcess(harness.Process, harness.Error)),
                logger ?? harness.Logs.CreateLogger(LifecycleEventCatalog.Category));
            var result = await launcher.LaunchDescriptorAsync(new ChildProcessStartDescriptor("safe", [], "safe",
                    ChildProcessEnvironmentPolicy.InheritCurrent),
                SessionTestSupport.CreateRequest(), new SessionRecordingSink(),
                new ChildWorkerLauncherOptions { TimeProvider = harness.Clock, ReadyTimeout = Timeout.InfiniteTimeSpan },
                CancellationToken.None);
            harness.Session = ((ChildWorkerLaunchResult.Started)result).Session;
            return harness;
        }

        internal async Task ReadyAsync()
        {
            Process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, Clock.GetUtcNow())));
            await Session.Startup.WaitAsync(Bound);
        }

        public async ValueTask DisposeAsync()
        {
            Process.Exit(137);
            Error.Complete();
            await Session.DisposeAsync().AsTask().WaitAsync(Bound);
            await Session.Telemetry!.CancellationObservation.WaitAsync(Bound);
            Logs.Dispose();
        }
    }

    private sealed class Factory(IChildProcess process) : IChildProcessFactory
    {
        public ValueTask<IChildProcess?> StartAsync(ChildProcessStartDescriptor descriptor, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChildProcess?>(process);
    }

    private sealed class HeldErrorProcess(SessionTestProcess inner, Stream error) : IChildProcess
    {
        public int ProcessId => inner.ProcessId;
        public Stream StandardInput => inner.StandardInput;
        public Stream StandardOutput => inner.StandardOutput;
        public Stream StandardError => error;
        public Task<int> WaitForExitAsync() => inner.WaitForExitAsync();
        public ChildWorkingSetObservation ReadWorkingSet() => inner.ReadWorkingSet();
        public ChildProcessExitState GetExitState() => inner.GetExitState();
        public ChildProcessKillOutcome KillProcessTree() => inner.KillProcessTree();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class BlockingCancellationLogger : ILogger, IDisposable
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new(false);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 6620)
            {
                Entered.TrySetResult();
                Release.Wait();
            }
        }
        public void Dispose() => Release.Dispose();
    }
}
