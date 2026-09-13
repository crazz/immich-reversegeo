using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Tests.LifecycleTelemetry;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.LifecycleTelemetry;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WorkerStateBridge = ImmichReverseGeo.Web.WorkerEventStateBridge.WorkerEventStateBridge;

namespace ImmichReverseGeo.Tests.WorkerFailureRecovery;

[TestClass]
[TestCategory("Change30")]
public sealed class WorkerRunFinalizerOrderingTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(15);

    [TestMethod]
    [TestCategory("Change66")]
    [DataRow(false, "infrastructure-failed")]
    [DataRow(true, "protocol-failed")]
    public async Task CleanupFailure_LogsFinalClassifierFactsWithoutRewritingTheCommittedResult(
        bool protocolFailed, string expected)
    {
        await using var harness = await Harness.CreateAsync(failCleanup: true);
        harness.Emit(WorkerProtocolV1TestData.Ready());
        harness.Emit(WorkerProtocolV1TestData.Started());
        await harness.Sink.Started.Task.WaitAsync(Watchdog);
        if (protocolFailed)
        {
            harness.Process.Inner.StandardOutputSource.Enqueue(System.Text.Encoding.UTF8.GetBytes("secret-token-invalid-frame\n"));
        }

        harness.Process.Inner.Exit(0);
        harness.Process.ErrorGate.Release.TrySetResult();
        var result = await harness.Finalizer.Completion.WaitAsync(Watchdog);
        var final = harness.Logs.Entries.Single(entry => entry.Event.Id == 6641);
        Assert.AreEqual(expected, final["process_classification"]);
        Assert.IsNull(final["terminal_outcome"]);
        Assert.AreEqual(0, harness.Logs.Entries.Count(entry => entry.Event.Id == 6640));
        Assert.AreSame(harness.Reporter.GetFinalizationReceipt(harness.Request)!.Result, result);
        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.IsTrue(harness.Finalizer.Decision!.Anomalies.HasFlag(WorkerRunAnomaly.CleanupFailure));
        Assert.AreEqual(1, harness.Process.Inner.DisposeCalls);
        Assert.IsFalse(final.Rendered.Contains("secret-token", StringComparison.Ordinal));
        Assert.IsNull(final.Exception);
    }

    [TestMethod]
    public async Task MissingTerminal_WaitsForBothPumps_AndCommitsBeforeResourceDisposal()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Process.ErrorGate.Entered.Task.WaitAsync(Watchdog);
        harness.Emit(WorkerProtocolV1TestData.Ready());
        harness.Emit(WorkerProtocolV1TestData.Started());
        await harness.Sink.Started.Task.WaitAsync(Watchdog);
        harness.Process.Inner.Exit(0);

        Assert.IsTrue(harness.State.IsRunning);
        Assert.IsNull(harness.Reporter.GetFinalizationReceipt(harness.Request));
        Assert.IsFalse(harness.Finalizer.Completion.IsCompleted);
        Assert.AreEqual(0, harness.Process.Inner.DisposeCalls);

        var observedFinalStateBeforeDisposal = false;
        harness.State.OnChanged += () =>
        {
            if (!harness.State.IsRunning)
            {
                observedFinalStateBeforeDisposal |= harness.Process.Inner.DisposeCalls == 0;
            }
        };
        harness.Process.ErrorGate.Release.TrySetResult();
        var result = await harness.Finalizer.Completion.WaitAsync(Watchdog);

        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual(WorkerRunFailureCategory.MissingTerminal, harness.Finalizer.Decision!.Category);
        Assert.IsTrue(observedFinalStateBeforeDisposal);
        Assert.IsFalse(harness.State.IsRunning);
        Assert.AreEqual(1, harness.State.ErrorsThisRun);
        Assert.AreEqual(1, harness.State.GetRecentLog().Count(line => line.Contains("Run complete.", StringComparison.Ordinal)));
        Assert.AreEqual(1, harness.Process.Inner.DisposeCalls);
    }

[TestMethod]
    public async Task ExecuteFlushedBeforeRunStarted_TracksDrainingWithoutInventingTerminationIntent()
    {
        await using var harness = await Harness.CreateAsync(splitExit: true);
        harness.Emit(WorkerProtocolV1TestData.Ready());
        await harness.Session.ExecuteRequestAccepted.WaitAsync(Watchdog);
        harness.Process.ExitSignal!.TrySetResult(0);
        await harness.Session.PhysicalExitConfirmed.WaitAsync(Watchdog);
        harness.Process.Inner.Exit(0);
        harness.Process.ErrorGate.Release.TrySetResult();

        var result = await harness.Finalizer.Completion.WaitAsync(Watchdog);
        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual(WorkerRunTransportPhase.Draining, harness.Finalizer.Evidence!.LastPhase);
        Assert.IsNull(harness.Session.CancellationFacts);
        Assert.AreEqual(0, harness.Process.Inner.KillCalls);
    }

    [TestMethod]
    public async Task TerminalObserverThrowsAfterReceipt_DoesNotContainOrRewriteCompletedRun()
    {
        await using var harness = await Harness.CreateAsync();
        harness.State.OnChanged += () =>
        {
            if (!harness.State.IsRunning)
            {
                throw new InvalidOperationException("secret observer detail must not become a fatal result");
            }
        };
        harness.Emit(WorkerProtocolV1TestData.Ready());
        harness.Emit(WorkerProtocolV1TestData.Started());
        harness.Emit(WorkerProtocolV1TestData.Eligible());
        harness.Emit(WorkerProtocolV1TestData.Completed());
        await harness.Sink.TerminalAttempted.Task.WaitAsync(Watchdog);

        var receipt = harness.Reporter.GetFinalizationReceipt(harness.Request);
        Assert.IsNotNull(receipt);
        Assert.AreEqual(ProcessingRunOutcome.Completed, receipt.Result.Outcome);
        Assert.IsFalse(harness.State.IsRunning);
        Assert.IsNull(harness.State.LastError);
        Assert.IsFalse(harness.Finalizer.Completion.IsCompleted);
        Assert.AreEqual(0, harness.Process.Inner.KillCalls);
        Assert.AreEqual(0, harness.Process.Inner.DisposeCalls);

        harness.Process.Inner.Exit(0);
        harness.Process.ErrorGate.Release.TrySetResult();
        var result = await harness.Finalizer.Completion.WaitAsync(Watchdog);

        Assert.AreSame(receipt.Result, result);
        Assert.AreEqual(0, harness.Process.Inner.KillCalls);
        Assert.IsNull(harness.Session.CancellationFacts);
        Assert.IsNull(harness.State.LastError);
        Assert.AreEqual(0, harness.State.ErrorsThisRun);
        Assert.AreEqual(1, harness.State.GetRecentLog().Count(line => line.Contains("Run complete.", StringComparison.Ordinal)));
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal ProcessingRunRequest Request { get; } = new(WorkerProtocolV1TestData.RunId, ProcessingRunTrigger.Manual);
        internal ProcessingState State { get; } = new();
        internal ProcessingStateEventReporter Reporter { get; private set; } = null!;
        internal WorkerRunFinalizer Finalizer { get; private set; } = null!;
        internal ChildWorkerSession Session { get; private set; } = null!;
        internal ObservedSink Sink { get; private set; } = null!;
        internal GatedProcess Process { get; } = new();
        internal RecordingLifecycleLogs Logs { get; } = new();
        private ChildWorkerEvidenceFinalityGate EvidenceGate { get; } = new();

        internal static async Task<Harness> CreateAsync(bool splitExit = false, bool failCleanup = false)
        {
            var harness = new Harness();
            harness.Process.FailCleanup = failCleanup;
            if (splitExit)
            {
                harness.Process.ExitSignal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            var clock = new CancellationTestClock(WorkerProtocolV1TestData.End.AddDays(1));
            harness.State.MarkPending();
            harness.Reporter = new ProcessingStateEventReporter(harness.State);
            Assert.IsTrue(harness.Reporter.Arm(harness.Request));
            var bridge = new WorkerStateBridge(harness.Request, harness.Reporter);
            harness.Sink = new ObservedSink(bridge);
            harness.Finalizer = new WorkerRunFinalizer(harness.Request, harness.Reporter, clock, harness.EvidenceGate);
            var dispatch = new ProcessAssetsWorkerJobDispatch(harness.Request);
            var telemetry = new WorkerJobTelemetry(harness.Logs.CreateLogger(LifecycleEventCatalog.Category), clock, dispatch.Context);
            harness.Session = await ChildWorkerSession.CreateAsync(harness.Process, dispatch,
                new ProcessAssetsWorkerJobEventSink(harness.Request, harness.Sink),
                new ChildWorkerLauncherOptions { TimeProvider = clock, ReadyTimeout = Timeout.InfiniteTimeSpan, EvidenceFinalityGate = harness.EvidenceGate },
                new ChildWorkerObserverArmingAcknowledgements(), InternalWorkerProtocolVersion.V1, telemetry, clock.GetTimestamp());
            _ = harness.Finalizer.Start(harness.Session, bridge,
                observation => harness.Session.RequestTermination(new ChildWorkerTerminationRequest(
                    observation.ObservedAt, ChildWorkerTerminationIntent.FaultContainment, observation.Reason)), () => false);
            return harness;
        }

        internal void Emit(WorkerProtocolEvent @event) => Process.Inner.StandardOutputSource.Enqueue(SessionTestSupport.Frame(@event));

        public async ValueTask DisposeAsync()
        {
            Process.ExitSignal?.TrySetResult(0);
            Process.Inner.Exit(0);
            Process.ErrorGate.Release.TrySetResult();
            EvidenceGate.Release();
            try
            {
                await Session.DisposeAsync().AsTask().WaitAsync(Watchdog);
            }
            catch (IOException) when (Process.FailCleanup)
            {
                // The finalizer already observed the deliberately failing disposal.
            }
            Logs.Dispose();
        }
    }

    private sealed class ObservedSink(WorkerStateBridge bridge) : IWorkerProtocolEventSink
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource TerminalAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask AcceptAsync(WorkerProtocolEvent @event, CancellationToken cancellationToken)
        {
            try
            {
                await bridge.AcceptAsync(@event, cancellationToken);
            }
            finally
            {
                if (@event.Type == WorkerProtocolV1.RunStartedType)
                {
                    Started.TrySetResult();
                }
                if (WorkerProtocolV1.IsTerminal(@event.Type))
                {
                    TerminalAttempted.TrySetResult();
                }
            }
        }
    }

    private sealed class GatedProcess : IChildProcess
    {
        internal SessionTestProcess Inner { get; } = new(new SessionInputStream(), ChildProcessKillOutcome.Requested, true);
        internal GatedReadStream ErrorGate { get; }
        internal TaskCompletionSource<int>? ExitSignal { get; set; }
        internal bool FailCleanup { get; set; }
        internal GatedProcess() => ErrorGate = new GatedReadStream(Inner.StandardError);
        public int ProcessId => Inner.ProcessId;
        public Stream StandardInput => Inner.StandardInput;
        public Stream StandardOutput => Inner.StandardOutput;
        public Stream StandardError => ErrorGate;
        public Task<int> WaitForExitAsync() => ExitSignal?.Task ?? Inner.WaitForExitAsync();
        public ChildWorkingSetObservation ReadWorkingSet() => Inner.ReadWorkingSet();
        public ChildProcessExitState GetExitState() => ExitSignal?.Task.IsCompleted == true ? ChildProcessExitState.Exited : Inner.GetExitState();
        public ChildProcessKillOutcome KillProcessTree() => Inner.KillProcessTree();
        public async ValueTask DisposeAsync()
        {
            await Inner.DisposeAsync();
            if (FailCleanup)
            {
                throw new IOException("secret-token-cleanup");
            }
        }
    }

    private sealed class GatedReadStream(Stream inner) : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
