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
using ImmichReverseGeo.Web.WorkerEventDelivery;
using AcceptedDelivery = ImmichReverseGeo.Web.WorkerEventDelivery.WorkerEventDelivery;

namespace ImmichReverseGeo.Tests.LifecycleTelemetry;

[TestClass]
[TestCategory("Change66")]
public sealed class LaunchTelemetryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private const string Canary = "secret-token-51.501,-0.142-connection-password";

    [TestMethod]
    public async Task ThrowingCadenceObservation_DoesNotLoseTheStartedProcessOrSamplerOwner()
    {
        using var logs = new RecordingLifecycleLogs();
        var clock = new CancellationTestClock();
        var inner = new SessionTestProcess(new SessionInputStream(), ChildProcessKillOutcome.Requested, false);
        var launcher = new ChildWorkerLauncher(new Factory(() => inner), logs.CreateLogger(LifecycleEventCatalog.Category));
        ChildWorkerSession? session = null;
        try
        {
            var result = await launcher.LaunchDescriptorAsync(Descriptor(), SessionTestSupport.CreateRequest(),
                new ThrowingCadenceSink(), new ChildWorkerLauncherOptions
                {
                    TimeProvider = clock,
                    ReadyTimeout = Timeout.InfiniteTimeSpan
                }, CancellationToken.None);
            session = ((ChildWorkerLaunchResult.Started)result).Session;
            inner.StandardOutputSource.Enqueue(SessionTestSupport.Frame(
                WorkerProtocolV1TestData.Ready()));
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup.WaitAsync(Bound));
            inner.Exit(0);
            await session.Settlement.WaitAsync(Bound);
            Assert.AreEqual(1, inner.DisposeCalls);
            Assert.AreEqual(0, clock.ActiveTimerCount);
            Assert.IsFalse(logs.Entries.Any(entry => entry.Rendered.Contains(Canary, StringComparison.Ordinal)));
        }
        finally
        {
            inner.Exit(0);
            if (session is not null)
            {
                await session.DisposeAsync();
            }
            else
            {
                await inner.DisposeAsync();
            }
        }
    }

    [TestMethod]
    [DataRow(false, "dashboard-manual")]
    [DataRow(true, "scheduler")]
    public async Task RealLauncher_PreservesIdentityAndTimesAcceptedReadyAndJoinsMemory(bool scheduled, string origin)
    {
        using var logs = new RecordingLifecycleLogs();
        var clock = new CancellationTestClock();
        var input = new SessionInputStream();
        var inner = new SessionTestProcess(input, ChildProcessKillOutcome.Requested, false);
        var process = new MemoryProbeProcess(inner);
        var request = new ProcessingRunRequest(Guid.NewGuid(),
            scheduled ? ProcessingRunTrigger.Scheduled : ProcessingRunTrigger.Manual);
        var sink = new SessionRecordingSink();
        var launcher = new ChildWorkerLauncher(new Factory(() =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(7));
            return process;
        }), logs.CreateLogger(LifecycleEventCatalog.Category));
        var result = await launcher.LaunchDescriptorAsync(Descriptor(), request, sink,
            new ChildWorkerLauncherOptions { TimeProvider = clock, ReadyTimeout = Timeout.InfiniteTimeSpan },
            CancellationToken.None);
        var session = ((ChildWorkerLaunchResult.Started)result).Session;
        var fixture = new SessionTestSupport.SessionFixture(clock, input, inner, request, sink, session);
        try
        {
            CollectionAssert.AreEqual(new[] { 6610, 6611 }, logs.Entries.Select(entry => entry.Event.Id).ToArray());
            Assert.IsNull(logs.Entries[0]["worker_process_id"]);
            Assert.AreEqual(7L, logs.Entries[1]["process_start_duration_ms"]);
            Assert.AreEqual(1, process.Reads, "One sampler attempts immediately at ownership.");

            clock.Advance(TimeSpan.FromMilliseconds(11));
            SessionTestSupport.EmitReady(fixture);
            await session.Startup.WaitAsync(Bound);
            var ready = logs.Entries.Single(entry => entry.Event.Id == 6612);
            Assert.AreEqual(11L, ready["readiness_duration_ms"]);
            Assert.AreEqual(18L, ready["startup_duration_ms"]);
            SessionTestSupport.EmitCompletedTerminal(fixture);
            await sink.TerminalAccepted.WaitAsync(Bound);
            inner.Exit(0);
            await session.Settlement.WaitAsync(Bound);
            var terminal = logs.Entries.Single(entry => entry.Event.Id == 6640);
            Assert.AreEqual("completed", terminal["terminal_outcome"]);
            Assert.AreEqual(4L, terminal["terminal_sequence"]);
            foreach (var entry in logs.Entries)
            {
                Assert.AreEqual(request.RunId, entry["job_id"]);
                Assert.AreEqual("ProcessAssets", entry["job_kind"]);
                Assert.AreEqual(origin, entry["job_origin"]);
                Assert.AreEqual(Environment.ProcessId, entry["controller_process_id"]);
                if (entry.Event.Id != 6610)
                {
                    Assert.AreEqual(inner.ProcessId, entry["worker_process_id"]);
                }

                Assert.IsFalse(entry.Rendered.Contains(Canary, StringComparison.Ordinal));
                Assert.IsNull(entry.Exception);
                Assert.AreEqual(0, entry.Scopes.Length);
            }

            Assert.AreEqual(2, process.Reads, "Final sampling completes before process disposal.");
            Assert.AreEqual(22L, session.WorkingSetObservation.PeakBytes);
            Assert.AreEqual(2L, session.WorkingSetObservation.SuccessfulSamples);
            Assert.IsFalse(process.ReadAfterDispose);
            Assert.AreEqual(1, inner.DisposeCalls);
            Assert.AreEqual(0, clock.ActiveTimerCount);
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, process.Reads);
        }
        finally
        {
            inner.Exit(0);
            await session.DisposeAsync().AsTask().WaitAsync(Bound);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NoProcessFailure_HasOneFinalEventWithNullPidAndNoSample(bool throws)
    {
        using var logs = new RecordingLifecycleLogs();
        var launcher = new ChildWorkerLauncher(new Factory(() =>
            throws ? throw new IOException(Canary) : null), logs.CreateLogger(LifecycleEventCatalog.Category));
        var result = await launcher.LaunchDescriptorAsync(Descriptor(), SessionTestSupport.CreateRequest(),
            new SessionRecordingSink(), new ChildWorkerLauncherOptions(), CancellationToken.None);
        Assert.IsInstanceOfType<ChildWorkerLaunchResult.StartFailed>(result);
        CollectionAssert.AreEqual(new[] { 6610, 6641 }, logs.Entries.Select(entry => entry.Event.Id).ToArray());
        var final = logs.Entries[1];
        Assert.AreEqual("startup-failed", final["process_classification"]);
        Assert.AreEqual(false, final["ready_observed"]);
        Assert.IsNull(final["worker_process_id"]);
        Assert.IsNull(final["exit_code"]);
        Assert.IsNull(final["terminal_outcome"]);
        Assert.IsNull(final["peak_working_set_bytes"]);
        Assert.AreEqual(0L, final["memory_sample_count"]);
        Assert.AreEqual("no-sample", final["memory_unavailable_reason"]);
        Assert.IsFalse(final.Rendered.Contains(Canary, StringComparison.Ordinal));
        Assert.IsNull(final.Exception);
    }

    [TestMethod]
    public async Task HostileProtocolBeforeReady_ReportsOnlyFirstBoundedCode()
    {
        using var logs = new RecordingLifecycleLogs();
        var inner = new SessionTestProcess(new SessionInputStream(), ChildProcessKillOutcome.Requested, false);
        var launcher = new ChildWorkerLauncher(new Factory(() => inner), logs.CreateLogger(LifecycleEventCatalog.Category));
        var result = await launcher.LaunchDescriptorAsync(Descriptor(), SessionTestSupport.CreateRequest(),
            new SessionRecordingSink(), new ChildWorkerLauncherOptions { ReadyTimeout = Timeout.InfiniteTimeSpan },
            CancellationToken.None);
        var session = ((ChildWorkerLaunchResult.Started)result).Session;
        try
        {
            inner.StandardOutputSource.Enqueue(System.Text.Encoding.UTF8.GetBytes(Canary + "\n" + Canary + "\n"));
            await session.Startup.WaitAsync(Bound);
            inner.Exit(6);
            await session.Settlement.WaitAsync(Bound);
            Assert.AreEqual(0, logs.Entries.Count(entry => entry.Event.Id is 6612 or 6640));
            var violation = logs.Entries.Single(entry => entry.Event.Id == 6630);
            Assert.AreEqual("ready", violation["protocol_phase"]);
            Assert.AreEqual("worker-output", violation["protocol_direction"]);
            Assert.AreEqual("malformed-json", violation["violation_code"]);
            Assert.IsNull(violation["sequence"]);
            Assert.IsFalse(logs.Entries.Any(entry => entry.Rendered.Contains(Canary, StringComparison.Ordinal)));
        }
        finally
        {
            inner.Exit(6);
            await session.DisposeAsync().AsTask().WaitAsync(Bound);
        }
    }

    private static ChildProcessStartDescriptor Descriptor() => new(Canary, [Canary], Canary,
        ChildProcessEnvironmentPolicy.InheritCurrentAndRemoveReservedProtocolVersion);

    private sealed class Factory(Func<IChildProcess?> start) : IChildProcessFactory
    {
        public ValueTask<IChildProcess?> StartAsync(ChildProcessStartDescriptor descriptor, CancellationToken cancellationToken) =>
            ValueTask.FromResult(start());
    }

    private sealed class ThrowingCadenceSink : IWorkerProtocolEventSink, IAcceptedWorkerEventSink
    {
        public ReadModelNotificationCadence.OwnerObservation? NotificationOwnerObservation => throw new IOException(Canary);
        public void BindDeliveryScope(WorkerEventDeliveryScope scope) { }
        public ValueTask AcceptDeliveryAsync(AcceptedDelivery delivery, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask AcceptAsync(WorkerProtocolEvent message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class MemoryProbeProcess(SessionTestProcess inner) : IChildProcess
    {
        internal int Reads { get; private set; }
        internal bool ReadAfterDispose { get; private set; }
        public int ProcessId => inner.ProcessId;
        public Stream StandardInput => inner.StandardInput;
        public Stream StandardOutput => inner.StandardOutput;
        public Stream StandardError => inner.StandardError;
        public Task<int> WaitForExitAsync() => inner.WaitForExitAsync();
        public ChildWorkingSetObservation ReadWorkingSet()
        {
            ReadAfterDispose |= inner.DisposeCalls != 0;
            return ChildWorkingSetObservation.Available(++Reads * 11);
        }
        public ChildProcessExitState GetExitState() => inner.GetExitState();
        public ChildProcessKillOutcome KillProcessTree() => inner.KillProcessTree();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
