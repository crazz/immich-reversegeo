using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.WorkerFailureRecovery;

[TestClass]
[TestCategory("Change30")]
public sealed class ChildWorkerEscalationEvidenceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    [DoNotParallelize]
    public async Task SynchronousKillExit_DoesNotPublishEvidenceBeforeKillFacts()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        var input = new SessionInputStream();
        var process = new GatedKillProcess(input);
        ProcessingRunRequest request = SessionTestSupport.CreateRequest();
        ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
            process,
            request,
            new SessionRecordingSink(),
            new ChildWorkerLauncherOptions
            {
                TimeProvider = clock,
                ReadyTimeout = Timeout.InfiniteTimeSpan
            },
            new ChildWorkerObserverArmingAcknowledgements());

        try
        {
            process.StandardOutputSource.Enqueue(
                SessionTestSupport.Frame(
                    WorkerProtocolMapper.Ready(1, clock.GetUtcNow())));
            await session.ExecuteRequestAccepted.WaitAsync(TestTimeout);

            long timerGeneration = clock.TimerGeneration;
            Task<ChildWorkerCancellationResult> termination =
                session.RequestTermination(
                    new ChildWorkerTerminationRequest(
                        ChildWorkerStopRequest.Capture(clock),
                        ChildWorkerTerminationIntent.Shutdown));
            await clock
                .WaitForTimerCreatedAsync(timerGeneration)
                .WaitAsync(TestTimeout);
            await input.SecondFlush.WaitAsync(TestTimeout);

            clock.Advance(ChildWorkerCancellationPolicy.Grace);
            await process.KillEntered.Task.WaitAsync(TestTimeout);

            ChildWorkerCompletionObservation raw =
                await session.WaitForCompletionAsync().WaitAsync(TestTimeout);
            ChildWorkerCancellationFacts pending = session.CancellationFacts!;

            Assert.IsTrue(raw.ExitObserved);
            Assert.AreEqual(137, raw.ExitCode);
            Assert.IsTrue(pending.GraceExpired);
            Assert.IsFalse(pending.KillAttempted);
            Assert.IsNull(pending.KillOutcome);
            Assert.IsFalse(session.PhysicalExitConfirmed.IsCompleted);
            Assert.IsFalse(session.EvidenceFinality.IsCompleted);
            Assert.IsFalse(session.Settlement.IsCompleted);
            Assert.IsFalse(termination.IsCompleted);
            Assert.AreEqual(0, process.DisposeCalls);

            process.ReleaseKill();
            ChildWorkerCompletionObservation evidence =
                await session.EvidenceFinality.WaitAsync(TestTimeout);
            ChildWorkerCancellationResult result =
                await termination.WaitAsync(TestTimeout);

            var decision = WorkerRunEvidenceClassifier.Classify(new WorkerRunEvidence
            {
                Request = request,
                LastPhase = WorkerRunTransportPhase.Draining,
                Completion = evidence,
                Cancellation = result.Facts,
                ShutdownRequested = true
            });
            Assert.AreEqual(ProcessingRunOutcome.Cancelled, decision.Outcome);
            Assert.AreEqual(WorkerRunFailureCategory.ForcedTermination, decision.Category);
            Assert.IsTrue(decision.Anomalies.HasFlag(WorkerRunAnomaly.ForcedTermination));
            Assert.IsFalse(decision.Retry);

            Assert.AreSame(raw, evidence);
            Assert.AreEqual(
                ChildWorkerTerminationIntent.Shutdown,
                result.Facts.FirstIntent);
            Assert.IsTrue(result.Facts.GraceExpired);
            Assert.IsTrue(result.Facts.KillAttempted);
            Assert.AreEqual(
                ChildProcessKillOutcome.Requested,
                result.Facts.KillOutcome);
            Assert.AreEqual(1, process.KillCalls);
            Assert.AreEqual(1, input.DisposeCalls);
            Assert.AreEqual(1, process.StandardOutputSource.DisposeCalls);
            Assert.AreEqual(1, process.StandardErrorSource.DisposeCalls);
            Assert.AreEqual(1, process.DisposeCalls);
        }
        finally
        {
            process.ReleaseKill();
            process.Exit(137);
            await session.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
    }

    private sealed class GatedKillProcess : IChildProcess
    {
        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _killRelease = new(false);
        private int _exitState;
        private int _disposeCalls;
        private int _killCalls;

        internal GatedKillProcess(SessionInputStream input)
        {
            StandardInput = input;
        }

        public int ProcessId => 3002;
        public Stream StandardInput { get; }
        public Stream StandardOutput => StandardOutputSource;
        public Stream StandardError => StandardErrorSource;
        internal SessionOutputStream StandardOutputSource { get; } = new();
        internal SessionOutputStream StandardErrorSource { get; } = new();
        internal TaskCompletionSource KillEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal int KillCalls => Volatile.Read(ref _killCalls);

        public Task<int> WaitForExitAsync() => _exit.Task;

        public ChildProcessExitState GetExitState()
            => Volatile.Read(ref _exitState) == 0
                ? ChildProcessExitState.Alive
                : ChildProcessExitState.Exited;

        public ChildProcessKillOutcome KillProcessTree()
        {
            Interlocked.Increment(ref _killCalls);
            Exit(137);
            KillEntered.TrySetResult();
            _killRelease.Wait(CancellationToken.None);
            return ChildProcessKillOutcome.Requested;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ValueTask.CompletedTask;
        }

        internal void ReleaseKill() => _killRelease.Set();

        internal void Exit(int exitCode)
        {
            if (Interlocked.Exchange(ref _exitState, 1) != 0)
            {
                return;
            }

            _exit.TrySetResult(exitCode);
            StandardOutputSource.Complete();
            StandardErrorSource.Complete();
        }
    }
}
