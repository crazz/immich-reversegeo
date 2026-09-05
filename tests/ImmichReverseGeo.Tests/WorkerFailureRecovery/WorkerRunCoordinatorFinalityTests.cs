using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventStateBridge;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerFailureRecovery;

[TestClass]
[TestCategory("Change30")]
public sealed class WorkerRunCoordinatorFinalityTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void NoProcessFinalizer_RegressedWallClockStillClaimsDurableReceipt()
    {
        DateTimeOffset admittedAt = SessionTestSupport.Start;
        var clock = new SequenceClock(
            admittedAt,
            admittedAt - TimeSpan.FromSeconds(1));
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = new ProcessingRunRequest(
            Guid.NewGuid(),
            ProcessingRunTrigger.Manual);
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        var finalizer = new WorkerRunFinalizer(request, reporter, clock);

        ProcessingRunResult result = finalizer.FinalizeNoProcess(
            WorkerRunFailureCategory.CommandResolution);

        Assert.AreEqual(admittedAt, result.StartedAtUtc);
        Assert.AreEqual(admittedAt, result.EndedAtUtc);
        Assert.IsTrue(finalizer.StateFinality.IsCompletedSuccessfully);
        ProcessingRunFinalizationReceipt receipt =
            reporter.GetFinalizationReceipt(request)!;
        Assert.AreSame(result, receipt.Result);
    }

    [TestMethod]
    public void NoProcessFinalizer_ClockReadFailureAfterAdmissionStillClaimsDurableReceipt()
    {
        DateTimeOffset admittedAt = SessionTestSupport.Start;
        var clock = new SecondReadFaultClock(admittedAt);
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = new ProcessingRunRequest(
            Guid.NewGuid(),
            ProcessingRunTrigger.Manual);
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        var finalizer = new WorkerRunFinalizer(request, reporter, clock);

        ProcessingRunResult result = finalizer.FinalizeNoProcess(
            WorkerRunFailureCategory.CommandResolution);

        Assert.AreEqual(admittedAt, result.StartedAtUtc);
        Assert.AreEqual(admittedAt, result.EndedAtUtc);
        Assert.IsTrue(finalizer.StateFinality.IsCompletedSuccessfully);
        ProcessingRunFinalizationReceipt receipt =
            reporter.GetFinalizationReceipt(request)!;
        Assert.AreSame(result, receipt.Result);
    }

    [TestMethod]
    public async Task StopBeforeAttachment_PreservesFirstDeadlineAndUsesExactReservedFinalizer()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        var invocation = new GatedInvocation();
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var coordinator = CreateCoordinator(state, reporter, clock, invocation);

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await coordinator.TriggerManualAsync());
        ProcessingRunRequest request =
            await invocation.Entered.Task.WaitAsync(TestTimeout);
        var evidenceGate = new ChildWorkerEvidenceFinalityGate();
        var equivalentRequest = new ProcessingRunRequest(
            request.RunId,
            request.Trigger);
        var equivalentFinalizer = new WorkerRunFinalizer(
            equivalentRequest,
            reporter,
            clock);
        Assert.IsFalse(coordinator.TryClaimChildExecution(
            equivalentRequest,
            equivalentFinalizer));
        var finalizer = new WorkerRunFinalizer(
            request,
            reporter,
            clock,
            evidenceGate);
        Assert.IsTrue(coordinator.TryClaimChildExecution(request, finalizer));
        Assert.IsFalse(coordinator.TryClaimChildExecution(request, finalizer));

        Task stop = coordinator.StopActiveRun()!;
        clock.Advance(TimeSpan.FromSeconds(4));
        long timerGeneration = clock.TimerGeneration;
        SessionFixture fixture = await CreateSessionAsync(
            request,
            reporter,
            clock,
            evidenceGate,
            exitOnKill: true);
        var unreservedFinalizer = new WorkerRunFinalizer(
            request,
            reporter,
            clock);
        Assert.IsFalse(coordinator.TryAttachChildSession(
            request,
            fixture.Session,
            fixture.Bridge,
            unreservedFinalizer));
        Assert.IsTrue(coordinator.TryAttachChildSession(
            request,
            fixture.Session,
            fixture.Bridge,
            finalizer));
        Task forwarding = ForwardFinalizerAsync(finalizer, invocation);

        await clock.WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);
        Assert.IsFalse(stop.IsCompleted);
        Assert.IsTrue(
            finalizer.State.Snapshot.Transport >= WorkerRunTransportPhase.Draining);

        clock.Advance(TimeSpan.FromSeconds(6));
        await stop.WaitAsync(TestTimeout);
        await forwarding.WaitAsync(TestTimeout);

        Assert.AreEqual(1, fixture.Process.KillCalls);
        Assert.AreEqual(
            ChildWorkerTerminationIntent.Stop,
            finalizer.Evidence!.Cancellation!.FirstIntent);
        Assert.AreEqual(
            WorkerRunTransportPhase.Released,
            finalizer.State.Snapshot.Transport);
        Assert.IsNull(coordinator.ActiveRequest);

        ProcessingRunFinalizationReceipt receipt =
            reporter.GetFinalizationReceipt(request)!;
        var competingResult = new ProcessingRunResult(
            request,
            receipt.Result.StartedAtUtc,
            receipt.Result.EndedAtUtc,
            0,
            0,
            0,
            0,
            ProcessingRunOutcome.Completed,
            null);
        ProcessingRunFinalizationAttempt retry = reporter.TryFinalize(
            request,
            competingResult,
            ProcessingRunFinalizationOrigin.ControlPlane);
        Assert.AreEqual(
            ProcessingRunFinalizationDisposition.ExistingWinner,
            retry.Disposition);
        Assert.AreSame(receipt, retry.Receipt);
        Assert.AreSame(receipt.Result, reporter.GetFinalizationReceipt(request)!.Result);
    }

    [TestMethod]
    public async Task FaultBeforeStop_OwnsFirstIntentAndLaterStopDoesNotRestartDeadline()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        var invocation = new GatedInvocation();
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var coordinator = CreateCoordinator(state, reporter, clock, invocation);

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await coordinator.TriggerManualAsync());
        ProcessingRunRequest request =
            await invocation.Entered.Task.WaitAsync(TestTimeout);
        var evidenceGate = new ChildWorkerEvidenceFinalityGate();
        var finalizer = new WorkerRunFinalizer(
            request,
            reporter,
            clock,
            evidenceGate);
        Assert.IsTrue(coordinator.TryClaimChildExecution(request, finalizer));
        long readyTimerGeneration = clock.TimerGeneration;
        SessionFixture fixture = await CreateSessionAsync(
            request,
            reporter,
            clock,
            evidenceGate,
            exitOnKill: true,
            readyTimeout: TimeSpan.FromSeconds(1));
        Assert.IsTrue(coordinator.TryAttachChildSession(
            request,
            fixture.Session,
            fixture.Bridge,
            finalizer));
        Task forwarding = ForwardFinalizerAsync(finalizer, invocation);

        await clock.WaitForTimerCreatedAsync(readyTimerGeneration)
            .WaitAsync(TestTimeout);
        long containmentTimerGeneration = clock.TimerGeneration;
        clock.Advance(TimeSpan.FromSeconds(1));
        ChildWorkerTerminalPreventingObservation observation =
            await fixture.Session.FirstTerminalPreventingObservation
                .WaitAsync(TestTimeout);
        await clock.WaitForTimerCreatedAsync(containmentTimerGeneration)
            .WaitAsync(TestTimeout);
        await fixture.Session.WaitForCancellationDeliveryAsync()
            .WaitAsync(TestTimeout);
        Assert.IsTrue(
            observation.Reason is ChildWorkerFaultContainmentReason.ReadyTimedOut);
        Assert.AreEqual(
            ChildWorkerTerminationIntent.FaultContainment,
            fixture.Session.CancellationFacts!.FirstIntent);
        long timerGeneration = clock.TimerGeneration;

        Task stop = coordinator.StopActiveRun()!;
        Assert.AreEqual(timerGeneration, clock.TimerGeneration);
        clock.Advance(ChildWorkerCancellationPolicy.Grace);

        await stop.WaitAsync(TestTimeout);
        await forwarding.WaitAsync(TestTimeout);
        Assert.AreEqual(1, fixture.Process.KillCalls);
        Assert.AreEqual(
            ChildWorkerTerminationIntent.FaultContainment,
            finalizer.Evidence!.Cancellation!.FirstIntent);
        Assert.AreSame(
            observation.Reason,
            finalizer.Evidence.Cancellation.FirstContainmentReason);
    }

    [TestMethod]
    public async Task ShutdownFirst_RecordsShutdownIntentAndJoinsFinalState()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        var invocation = new GatedInvocation();
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var coordinator = CreateCoordinator(state, reporter, clock, invocation);

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await coordinator.TriggerManualAsync());
        ProcessingRunRequest request =
            await invocation.Entered.Task.WaitAsync(TestTimeout);
        var evidenceGate = new ChildWorkerEvidenceFinalityGate();
        var finalizer = new WorkerRunFinalizer(
            request,
            reporter,
            clock,
            evidenceGate);
        Assert.IsTrue(coordinator.TryClaimChildExecution(request, finalizer));
        SessionFixture fixture = await CreateSessionAsync(
            request,
            reporter,
            clock,
            evidenceGate,
            exitOnKill: true);
        Assert.IsTrue(coordinator.TryAttachChildSession(
            request,
            fixture.Session,
            fixture.Bridge,
            finalizer));
        Task forwarding = ForwardFinalizerAsync(finalizer, invocation);

        long timerGeneration = clock.TimerGeneration;
        Task shutdown = coordinator.BeginShutdown();
        await clock.WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);
        clock.Advance(ChildWorkerCancellationPolicy.Grace);

        await shutdown.WaitAsync(TestTimeout);
        await forwarding.WaitAsync(TestTimeout);
        Assert.AreEqual(
            ChildWorkerTerminationIntent.Shutdown,
            finalizer.Evidence!.Cancellation!.FirstIntent);
        Assert.AreEqual(
            WorkerRunTransportPhase.Released,
            finalizer.State.Snapshot.Transport);
        Assert.IsNull(coordinator.ActiveRequest);
    }

    [TestMethod]
    public async Task DurableReceipt_PrecedesDetachAndOnlyThenAllowsReplacementAdmission()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        var first = new GatedInvocation();
        var second = new GatedInvocation();
        var observer = new BeforeDetachGate();
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var coordinator = CreateCoordinator(
            state,
            reporter,
            clock,
            observer,
            first,
            second);

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await coordinator.TriggerManualAsync());
        ProcessingRunRequest request =
            await first.Entered.Task.WaitAsync(TestTimeout);
        var finalizer = new WorkerRunFinalizer(request, reporter, clock);
        Assert.IsTrue(coordinator.TryClaimChildExecution(request, finalizer));
        ProcessingRunResult result = finalizer.FinalizeNoProcess(
            WorkerRunFailureCategory.CommandResolution);
        first.Completion.TrySetResult(result);

        await observer.Entered.Task.WaitAsync(TestTimeout);
        Assert.IsTrue(finalizer.StateFinality.IsCompletedSuccessfully);
        Assert.AreEqual(
            WorkerRunTransportPhase.EvidenceFinal,
            finalizer.State.Snapshot.Transport);
        Assert.AreSame(request, coordinator.ActiveRequest);
        Assert.AreEqual(
            ProcessingRunAdmissionResult.AlreadyRunning,
            await coordinator.TriggerManualAsync());

        observer.Release.TrySetResult();
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        Assert.AreEqual(
            WorkerRunTransportPhase.Released,
            finalizer.State.Snapshot.Transport);
        Assert.IsNull(coordinator.ActiveRequest);

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await coordinator.TriggerManualAsync());
        ProcessingRunRequest secondRequest =
            await second.Entered.Task.WaitAsync(TestTimeout);
        var secondFailure = new InvalidOperationException("test cleanup");
        reporter.Abandon(secondRequest, secondFailure);
        second.Completion.TrySetException(secondFailure);
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task MissingReceipt_FaultsFinalizerCompletionAndRetainsExactActiveOwnership()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        var invocation = new GatedInvocation();
        var observer = new BeforeChildSettlementSignal();
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var coordinator = CreateCoordinator(
            state,
            reporter,
            clock,
            observer,
            invocation);

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await coordinator.TriggerManualAsync());
        ProcessingRunRequest request =
            await invocation.Entered.Task.WaitAsync(TestTimeout);
        var evidenceGate = new ChildWorkerEvidenceFinalityGate();
        var finalizer = new WorkerRunFinalizer(
            request,
            reporter,
            clock,
            evidenceGate);
        Assert.IsTrue(coordinator.TryClaimChildExecution(request, finalizer));
        SessionFixture fixture = await CreateSessionAsync(
            request,
            reporter,
            clock,
            evidenceGate);
        Assert.IsTrue(coordinator.TryAttachChildSession(
            request,
            fixture.Session,
            fixture.Bridge,
            finalizer));
        Task forwarding = ForwardFinalizerAsync(finalizer, invocation);

        var staleFailure = new InvalidOperationException("release reporter arm");
        Assert.IsTrue(reporter.Abandon(request, staleFailure));
        fixture.Process.Exit(7);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalizer.Completion.WaitAsync(TestTimeout));
        await observer.Entered.Task.WaitAsync(TestTimeout);
        Assert.IsFalse(finalizer.StateFinality.IsCompleted);
        Assert.IsFalse(fixture.Session.Settlement.IsCompleted);
        Assert.AreSame(request, coordinator.ActiveRequest);
        Assert.IsFalse(coordinator.WaitForActiveRunAsync().IsCompleted);
        Assert.AreEqual(
            ProcessingRunAdmissionResult.AlreadyRunning,
            await coordinator.TriggerManualAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => forwarding.WaitAsync(TestTimeout));
    }

    private static ProcessingRunCoordinator CreateCoordinator(
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        TimeProvider clock,
        params GatedInvocation[] invocations)
        => CreateCoordinator(state, reporter, clock, null, invocations);

    private static ProcessingRunCoordinator CreateCoordinator(
        ProcessingState state,
        ProcessingStateEventReporter reporter,
        TimeProvider clock,
        IProcessingRunCoordinatorObserver? observer,
        params GatedInvocation[] invocations)
        => new(
            state,
            reporter,
            new GatedExecutor(invocations),
            NullLogger<ProcessingRunCoordinator>.Instance,
            Guid.NewGuid,
            observer,
            applicationLifetime: null,
            timeProvider: clock);

    private static async Task<SessionFixture> CreateSessionAsync(
        ProcessingRunRequest request,
        ProcessingStateEventReporter reporter,
        CancellationTestClock clock,
        ChildWorkerEvidenceFinalityGate evidenceGate,
        bool exitOnKill = false,
        TimeSpan? readyTimeout = null)
    {
        var input = new SessionInputStream();
        var process = new SessionTestProcess(
            input,
            ChildProcessKillOutcome.Requested,
            exitOnKill);
        WorkerEventStateBridge bridge =
            new WorkerEventStateBridgeFactory(reporter).Create(request);
        ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
            process,
            request,
            bridge,
            new ChildWorkerLauncherOptions
            {
                TimeProvider = clock,
                ReadyTimeout = readyTimeout ?? Timeout.InfiniteTimeSpan,
                EvidenceFinalityGate = evidenceGate
            },
            new ChildWorkerObserverArmingAcknowledgements());
        return new SessionFixture(process, bridge, session);
    }

    private static async Task ForwardFinalizerAsync(
        WorkerRunFinalizer finalizer,
        GatedInvocation invocation)
    {
        try
        {
            invocation.Completion.TrySetResult(
                await finalizer.Completion.ConfigureAwait(false));
        }
        catch (Exception failure)
        {
            invocation.Completion.TrySetException(failure);
            throw;
        }
    }

    private sealed class GatedExecutor(params GatedInvocation[] invocations)
        : IProcessingRunExecutor
    {
        private readonly Queue<GatedInvocation> _invocations = new(invocations);

        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            GatedInvocation invocation = _invocations.Dequeue();
            invocation.Token = cancellationToken;
            invocation.Entered.TrySetResult(request);
            return invocation.Completion.Task;
        }
    }

    private sealed class GatedInvocation
    {
        internal TaskCompletionSource<ProcessingRunRequest> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ProcessingRunResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; set; }
    }

    private sealed class BeforeDetachGate : IProcessingRunCoordinatorObserver
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask BeforeDetachAsync(
            ProcessingRunRequest request,
            CancellationToken activeToken)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }
    }

    private sealed class BeforeChildSettlementSignal
        : IProcessingRunCoordinatorObserver
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask BeforeChildSettlementAsync(
            ProcessingRunRequest request,
            CancellationToken activeToken)
        {
            Entered.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SessionFixture(
        SessionTestProcess Process,
        WorkerEventStateBridge Bridge,
        ChildWorkerSession Session);

    private sealed class SequenceClock(
        DateTimeOffset first,
        DateTimeOffset second) : TimeProvider
    {
        private int _calls;

        public override DateTimeOffset GetUtcNow()
        {
            return Interlocked.Increment(ref _calls) == 1
                ? first
                : second;
        }
    }

    private sealed class SecondReadFaultClock(DateTimeOffset admittedAt) : TimeProvider
    {
        private int _calls;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return admittedAt;
            }

            throw new InvalidOperationException("clock read failed");
        }
    }
}
