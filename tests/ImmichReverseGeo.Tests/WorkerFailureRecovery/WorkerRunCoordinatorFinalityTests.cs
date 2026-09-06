using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;
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
    [TestCategory("Change32")]
    public async Task AbruptExitWithoutTerminal_CommitsControlPlaneCrashAndReachesIdleWithoutWarning()
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
        var finalizer = new WorkerRunFinalizer(request, reporter, clock, evidenceGate);
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

        fixture.Process.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolV1TestData.Ready()));
        await fixture.Session.WaitForStartupAsync().WaitAsync(TestTimeout);
        fixture.Process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(
            new WorkerProtocolEvent(
                WorkerProtocolV1.LifecycleCategory,
                WorkerProtocolV1.RunStartedType,
                2,
                WorkerProtocolV1TestData.Start,
                request.RunId,
                new RunStartedPayload("manual", WorkerProtocolV1TestData.Start))));
        fixture.Process.Exit(137);

        ProcessingRunResult result = await finalizer.Completion.WaitAsync(TestTimeout);
        await forwarding.WaitAsync(TestTimeout);
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        ProcessingRunFinalizationReceipt receipt =
            reporter.GetFinalizationReceipt(request)!;

        Assert.AreSame(receipt.Result, result);
        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual(ProcessingRunFinalizationOrigin.ControlPlane, receipt.Origin);
        Assert.AreEqual(WorkerRunAuthority.ControlPlane, finalizer.Decision!.Authority);
        Assert.AreEqual(WorkerRunFailureCategory.UnmappedExit, finalizer.Decision.Category);
        Assert.AreEqual(WorkerRunAnomaly.None, finalizer.Decision.Anomalies);
        Assert.IsNull(finalizer.Evidence!.Completion!.Terminal);
        var protocol = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(
            finalizer.Evidence.Completion.FirstProtocolObservation);
        Assert.AreEqual(WorkerProtocolFailureDetail.MissingTerminal, protocol.Failure.Detail);
        Assert.IsNull(coordinator.ActiveRequest);
        Assert.IsFalse(state.IsRunning);
        Assert.AreEqual(
            0,
            state.GetRecentLog().Count(line => line.Contains("[WARN]", StringComparison.Ordinal)),
            "expected-crash-finality-does-not-create-a-post-terminal-warning");
        Assert.AreEqual(
            1,
            state.GetRecentLog().Count(line => line.Contains("Run complete.", StringComparison.Ordinal)),
            "control-plane-crash-projects-one-summary");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [TestCategory("Change32")]
    public async Task TerminalInputCloseFailure_ContainsAttachedChildAndPreservesCommittedTerminal(
        bool publishBeforeReceipt)
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        var invocation = new GatedInvocation();
        var observer = new LifecycleObserver();
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var coordinator = new ProcessingRunCoordinator(
            state,
            reporter,
            new TemporaryProcessingBackendSelection(ProcessingBackendKind.InProcess),
            ProcessingRunBackendTestScopeFactory.Create(new GatedExecutor(invocation)),
            NullLogger<ProcessingRunCoordinator>.Instance,
            () => WorkerProtocolV1TestData.RunId,
            observer,
            applicationLifetime: null,
            timeProvider: clock);

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
        TerminalCloseFixture fixture = await CreateTerminalCloseSessionAsync(
            request,
            reporter,
            clock,
            evidenceGate,
            holdTerminalProjection: publishBeforeReceipt,
            holdInputClose: !publishBeforeReceipt);
        Assert.IsTrue(coordinator.TryAttachChildSession(
            request,
            fixture.Session,
            fixture.Bridge,
            finalizer));
        Task forwarding = ForwardFinalizerAsync(finalizer, invocation);
        long timerGeneration = clock.TimerGeneration;

        fixture.Process.Inner.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolV1TestData.Ready()));
        fixture.Process.Inner.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolV1TestData.Started()));
        fixture.Process.Inner.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolV1TestData.Eligible()));
        fixture.Process.Inner.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolV1TestData.Completed()));

        await fixture.Sink.TerminalEntered.Task.WaitAsync(TestTimeout);
        await fixture.Input.DisposeEntered.Task.WaitAsync(TestTimeout);
        ChildWorkerTerminalPreventingObservation observation;
        if (publishBeforeReceipt)
        {
            observation = await fixture.Session.FirstTerminalPreventingObservation
                .WaitAsync(TestTimeout);
            Assert.IsNull(reporter.GetFinalizationReceipt(request));
            fixture.Sink.ReleaseTerminal();
        }
        else
        {
            await fixture.Sink.TerminalForwarded.Task.WaitAsync(TestTimeout);
            Assert.IsNotNull(reporter.GetFinalizationReceipt(request));
            fixture.Input.ReleaseClose();
            observation = await fixture.Session.FirstTerminalPreventingObservation
                .WaitAsync(TestTimeout);
        }

        await fixture.Sink.TerminalForwarded.Task.WaitAsync(TestTimeout);
        ProcessingRunFinalizationReceipt receipt =
            reporter.GetFinalizationReceipt(request)!;
        Assert.AreEqual(ProcessingRunOutcome.Completed, receipt.Result.Outcome);
        Assert.IsInstanceOfType<ChildWorkerFaultContainmentReason.TerminalInputCloseFailed>(
            observation.Reason);
        await clock.WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);
        Assert.AreEqual(timerGeneration + 1, clock.TimerGeneration);
        clock.Advance(ChildWorkerCancellationPolicy.Grace);
        await fixture.Process.Inner.KillObserved.Task.WaitAsync(TestTimeout);

        ProcessingRunResult result = await finalizer.Completion.WaitAsync(TestTimeout);
        await forwarding.WaitAsync(TestTimeout);
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        ChildWorkerTerminationRequest firstTerminationRequest =
            await fixture.Session.FirstTerminationRequest.WaitAsync(TestTimeout);

        Assert.AreSame(receipt.Result, result);
        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        Assert.AreEqual(1, fixture.Process.Inner.KillCalls);
        Assert.AreEqual(1, fixture.Input.DisposeCalls);
        Assert.AreEqual(1, fixture.Process.Inner.DisposeCalls);
        Assert.AreEqual(1, fixture.Process.Inner.StandardOutputSource.DisposeCalls);
        Assert.AreEqual(1, fixture.Process.Inner.StandardErrorSource.DisposeCalls);
        Assert.IsNull(coordinator.ActiveRequest);
        Assert.AreEqual(1, observer.BeforeChildSettlementCalls);
        Assert.AreEqual(1, observer.BeforeDetachCalls);
        Assert.AreEqual(1, observer.BeforeDisposeCalls);
        Assert.AreEqual(
            ChildWorkerTerminationIntent.FaultContainment,
            finalizer.Evidence!.Cancellation!.FirstIntent);
        Assert.AreEqual(
            observation.ObservedAt.FirstStopAtUtc,
            finalizer.Evidence.Cancellation.FirstStopAtUtc);
        Assert.AreEqual(
            observation.ObservedAt.FirstStopAtUtc
                + ChildWorkerCancellationPolicy.Grace,
            finalizer.Evidence.Cancellation.DeadlineUtc);
        Assert.AreSame(
            observation.Reason,
            finalizer.Evidence.Cancellation.FirstContainmentReason);
        Assert.AreEqual(
            ChildWorkerTerminationIntent.FaultContainment,
            firstTerminationRequest.Intent);
        Assert.AreSame(
            observation.Reason,
            firstTerminationRequest.Reason);
        Assert.AreEqual(timerGeneration + 1, clock.TimerGeneration);
        Assert.IsTrue(finalizer.Decision!.Anomalies.HasFlag(
            WorkerRunAnomaly.InputTransport));
        Assert.IsTrue(finalizer.Decision.Anomalies.HasFlag(
            WorkerRunAnomaly.ForcedTermination));
        Assert.AreEqual(
            1,
            state.GetRecentLog().Count(line => line.Contains(
                "Run complete.",
                StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("Change32")]
    public async Task ReceiptBackedSinkFailureThenTerminalInputCloseFailure_ContainsOnceAndRetainsBothFacts()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        var invocation = new GatedInvocation();
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state, processingEvent =>
        {
            if (processingEvent is RunFinished)
            {
                throw new InvalidOperationException("Synthetic terminal projection failure.");
            }
        });
        var coordinator = CreateCoordinator(state, reporter, clock, invocation);

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await coordinator.TriggerManualAsync());
        ProcessingRunRequest request =
            await invocation.Entered.Task.WaitAsync(TestTimeout);
        var evidenceGate = new ChildWorkerEvidenceFinalityGate();
        var finalizer = new WorkerRunFinalizer(request, reporter, clock, evidenceGate);
        Assert.IsTrue(coordinator.TryClaimChildExecution(request, finalizer));
        TerminalCloseFixture fixture = await CreateTerminalCloseSessionAsync(
            request,
            reporter,
            clock,
            evidenceGate,
            holdTerminalProjection: false,
            holdInputClose: true);
        Assert.IsTrue(coordinator.TryAttachChildSession(
            request,
            fixture.Session,
            fixture.Bridge,
            finalizer));
        Task forwarding = ForwardFinalizerAsync(finalizer, invocation);
        long timerGeneration = clock.TimerGeneration;

        EnqueueTerminalSequence(fixture.Process.Inner, request);
        await fixture.Sink.TerminalEntered.Task.WaitAsync(TestTimeout);
        await fixture.Input.DisposeEntered.Task.WaitAsync(TestTimeout);
        ChildWorkerTerminalPreventingObservation firstObservation =
            await fixture.Session.FirstTerminalPreventingObservation.WaitAsync(TestTimeout);
        ProcessingRunFinalizationReceipt receipt = reporter.GetFinalizationReceipt(request)!;

        Assert.IsNotNull(receipt);
        Assert.AreEqual(ProcessingRunFinalizationOrigin.WorkerTerminal, receipt.Origin);
        Assert.IsInstanceOfType<ChildWorkerFaultContainmentReason.SinkFailure>(
            firstObservation.Reason);
        Assert.IsInstanceOfType<WorkerEventStateBridgeObservation.ProjectionResponseIndeterminate>(
            fixture.Bridge.FirstObservation);
        Assert.AreEqual(timerGeneration, clock.TimerGeneration);

        fixture.Input.ReleaseClose();
        ChildWorkerTerminalPreventingObservation closeFailure =
            await fixture.Session.TerminalInputCloseFailure.WaitAsync(TestTimeout);
        await clock.WaitForTimerCreatedAsync(timerGeneration).WaitAsync(TestTimeout);
        ChildWorkerTerminationRequest firstTermination =
            await fixture.Session.FirstTerminationRequest.WaitAsync(TestTimeout);

        Assert.AreSame(firstObservation, await fixture.Session.FirstTerminalPreventingObservation);
        Assert.IsInstanceOfType<ChildWorkerFaultContainmentReason.TerminalInputCloseFailed>(
            closeFailure.Reason);
        Assert.AreEqual(timerGeneration + 1, clock.TimerGeneration);
        Assert.AreEqual(ChildWorkerTerminationIntent.FaultContainment, firstTermination.Intent);
        Assert.AreSame(closeFailure.Reason, firstTermination.Reason);

        clock.Advance(ChildWorkerCancellationPolicy.Grace);
        await fixture.Process.Inner.KillObserved.Task.WaitAsync(TestTimeout);
        ProcessingRunResult result = await finalizer.Completion.WaitAsync(TestTimeout);
        await forwarding.WaitAsync(TestTimeout);
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

        Assert.AreSame(receipt.Result, result);
        Assert.AreEqual(1, fixture.Input.DisposeCalls);
        Assert.AreEqual(1, fixture.Process.Inner.KillCalls);
        Assert.AreEqual(timerGeneration + 1, clock.TimerGeneration);
        Assert.IsNull(coordinator.ActiveRequest);
        Assert.AreSame(closeFailure.Reason, finalizer.Evidence!.Cancellation!.FirstContainmentReason);
        Assert.AreSame(closeFailure, finalizer.Evidence.TerminalInputCloseFailure);
        Assert.IsTrue(finalizer.Decision!.Anomalies.HasFlag(WorkerRunAnomaly.InputTransport));
        Assert.IsTrue(finalizer.Decision.Anomalies.HasFlag(WorkerRunAnomaly.ProjectionAfterTerminal));
    }

    [TestMethod]
    [TestCategory("Change32")]
    public async Task UncommittedSinkFailureThenTerminalInputCloseFailure_JoinsTheExistingContainmentOwner()
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
        var finalizer = new WorkerRunFinalizer(request, reporter, clock, evidenceGate);
        Assert.IsTrue(coordinator.TryClaimChildExecution(request, finalizer));
        var input = new FaultingInputCloseStream(holdClose: true);
        var process = new TerminalCloseTestProcess(input);
        WorkerEventStateBridge bridge = new WorkerEventStateBridgeFactory(reporter).Create(request);
        var sink = new TerminalSinkFailureGate(bridge);
        ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
            process,
            request,
            sink,
            new ChildWorkerLauncherOptions
            {
                TimeProvider = clock,
                ReadyTimeout = Timeout.InfiniteTimeSpan,
                EvidenceFinalityGate = evidenceGate
            },
            new ChildWorkerObserverArmingAcknowledgements());
        Assert.IsTrue(coordinator.TryAttachChildSession(request, session, bridge, finalizer));
        Task forwarding = ForwardFinalizerAsync(finalizer, invocation);
        long timerGeneration = clock.TimerGeneration;

        EnqueueTerminalSequence(process.Inner, request);
        await sink.TerminalEntered.Task.WaitAsync(TestTimeout);
        await input.DisposeEntered.Task.WaitAsync(TestTimeout);
        ChildWorkerTerminalPreventingObservation firstObservation =
            await session.FirstTerminalPreventingObservation.WaitAsync(TestTimeout);
        await clock.WaitForTimerCreatedAsync(timerGeneration).WaitAsync(TestTimeout);
        ChildWorkerTerminationRequest firstTermination =
            await session.FirstTerminationRequest.WaitAsync(TestTimeout);

        Assert.IsNull(reporter.GetFinalizationReceipt(request));
        Assert.IsInstanceOfType<ChildWorkerFaultContainmentReason.SinkFailure>(
            firstObservation.Reason);
        Assert.AreSame(firstObservation.Reason, firstTermination.Reason);
        Assert.AreEqual(timerGeneration + 1, clock.TimerGeneration);

        input.ReleaseClose();
        ChildWorkerTerminalPreventingObservation closeFailure =
            await session.TerminalInputCloseFailure.WaitAsync(TestTimeout);

        Assert.IsInstanceOfType<ChildWorkerFaultContainmentReason.TerminalInputCloseFailed>(
            closeFailure.Reason);
        Assert.AreSame(firstObservation, await session.FirstTerminalPreventingObservation);
        Assert.AreEqual(timerGeneration + 1, clock.TimerGeneration);

        clock.Advance(ChildWorkerCancellationPolicy.Grace);
        await process.Inner.KillObserved.Task.WaitAsync(TestTimeout);
        await finalizer.Completion.WaitAsync(TestTimeout);
        await forwarding.WaitAsync(TestTimeout);
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

        Assert.AreEqual(1, input.DisposeCalls);
        Assert.AreEqual(1, process.Inner.KillCalls);
        Assert.AreEqual(timerGeneration + 1, clock.TimerGeneration);
        Assert.AreSame(firstObservation.Reason, finalizer.Evidence!.Cancellation!.FirstContainmentReason);
        Assert.AreSame(closeFailure, finalizer.Evidence.TerminalInputCloseFailure);
        Assert.IsTrue(finalizer.Decision!.Anomalies.HasFlag(WorkerRunAnomaly.InputTransport));
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
            new TemporaryProcessingBackendSelection(ProcessingBackendKind.InProcess),
            ProcessingRunBackendTestScopeFactory.Create(new GatedExecutor(invocations)),
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

    private static async Task<TerminalCloseFixture> CreateTerminalCloseSessionAsync(
        ProcessingRunRequest request,
        ProcessingStateEventReporter reporter,
        CancellationTestClock clock,
        ChildWorkerEvidenceFinalityGate evidenceGate,
        bool holdTerminalProjection,
        bool holdInputClose)
    {
        var input = new FaultingInputCloseStream(holdInputClose);
        var process = new TerminalCloseTestProcess(input);
        WorkerEventStateBridge bridge =
            new WorkerEventStateBridgeFactory(reporter).Create(request);
        var sink = new TerminalProjectionGate(bridge, holdTerminalProjection);
        ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
            process,
            request,
            sink,
            new ChildWorkerLauncherOptions
            {
                TimeProvider = clock,
                ReadyTimeout = Timeout.InfiniteTimeSpan,
                EvidenceFinalityGate = evidenceGate
            },
            new ChildWorkerObserverArmingAcknowledgements());
        return new TerminalCloseFixture(process, input, bridge, sink, session);
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

    private static void EnqueueTerminalSequence(
        SessionTestProcess process,
        ProcessingRunRequest request)
    {
        process.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolV1TestData.Ready()));
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(
            new WorkerProtocolEvent(
                WorkerProtocolV1.LifecycleCategory,
                WorkerProtocolV1.RunStartedType,
                2,
                WorkerProtocolV1TestData.Start,
                request.RunId,
                new RunStartedPayload("manual", WorkerProtocolV1TestData.Start))));
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(
            new WorkerProtocolEvent(
                WorkerProtocolV1.LifecycleCategory,
                WorkerProtocolV1.EligibilityDeterminedType,
                3,
                WorkerProtocolV1TestData.Midpoint,
                request.RunId,
                new EligibilityDeterminedPayload(1))));
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(
            new WorkerProtocolEvent(
                WorkerProtocolV1.TerminalCategory,
                WorkerProtocolV1.CompletedType,
                4,
                WorkerProtocolV1TestData.End,
                request.RunId,
                new CompletedPayload(
                    "manual",
                    WorkerProtocolV1TestData.Start,
                    WorkerProtocolV1TestData.End,
                    0,
                    0,
                    0,
                    0))));
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

    private sealed class LifecycleObserver : IProcessingRunCoordinatorObserver
    {
        private int _beforeChildSettlementCalls;
        private int _beforeDetachCalls;
        private int _beforeDisposeCalls;

        internal int BeforeChildSettlementCalls =>
            Volatile.Read(ref _beforeChildSettlementCalls);
        internal int BeforeDetachCalls => Volatile.Read(ref _beforeDetachCalls);
        internal int BeforeDisposeCalls => Volatile.Read(ref _beforeDisposeCalls);

        public ValueTask BeforeChildSettlementAsync(
            ProcessingRunRequest request,
            CancellationToken activeToken)
        {
            Interlocked.Increment(ref _beforeChildSettlementCalls);
            return ValueTask.CompletedTask;
        }

        public ValueTask BeforeDetachAsync(
            ProcessingRunRequest request,
            CancellationToken activeToken)
        {
            Interlocked.Increment(ref _beforeDetachCalls);
            return ValueTask.CompletedTask;
        }

        public ValueTask BeforeDisposeAsync(
            ProcessingRunRequest request,
            CancellationToken activeToken)
        {
            Interlocked.Increment(ref _beforeDisposeCalls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TerminalProjectionGate(
        WorkerEventStateBridge bridge,
        bool holdTerminal) : IWorkerProtocolEventSink
    {
        private readonly TaskCompletionSource _terminalRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource TerminalEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource TerminalForwarded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask AcceptAsync(
            WorkerProtocolEvent @event,
            CancellationToken cancellationToken)
        {
            if (WorkerProtocolV1.IsTerminal(@event.Type))
            {
                TerminalEntered.TrySetResult();
                if (holdTerminal)
                {
                    await _terminalRelease.Task.ConfigureAwait(false);
                }
            }

            await bridge.AcceptAsync(@event, cancellationToken);
            if (WorkerProtocolV1.IsTerminal(@event.Type))
            {
                TerminalForwarded.TrySetResult();
            }
        }

        internal void ReleaseTerminal() => _terminalRelease.TrySetResult();
    }

    private sealed class TerminalSinkFailureGate(
        WorkerEventStateBridge bridge) : IWorkerProtocolEventSink
    {
        internal TaskCompletionSource TerminalEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask AcceptAsync(
            WorkerProtocolEvent @event,
            CancellationToken cancellationToken)
        {
            if (WorkerProtocolV1.IsTerminal(@event.Type))
            {
                TerminalEntered.TrySetResult();
                throw new InvalidOperationException("Synthetic terminal sink failure.");
            }

            return bridge.AcceptAsync(@event, cancellationToken);
        }
    }

    private sealed class FaultingInputCloseStream(bool holdClose) : Stream
    {
        private readonly TaskCompletionSource _closeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCalls;

        internal TaskCompletionSource DisposeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

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
            => Task.CompletedTask;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public override ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref _disposeCalls) != 1)
            {
                return ValueTask.CompletedTask;
            }

            DisposeEntered.TrySetResult();
            return new ValueTask(FailCloseAsync());
        }

        private async Task FailCloseAsync()
        {
            if (holdClose)
            {
                await _closeRelease.Task.ConfigureAwait(false);
            }

            throw new IOException("Synthetic terminal input close failure.");
        }

        internal void ReleaseClose() => _closeRelease.TrySetResult();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class TerminalCloseTestProcess(Stream standardInput) : IChildProcess
    {
        internal SessionTestProcess Inner { get; } = new(
            new SessionInputStream(),
            ChildProcessKillOutcome.Requested,
            exitOnKill: true);

        public int ProcessId => Inner.ProcessId;
        public Stream StandardInput { get; } = standardInput;
        public Stream StandardOutput => Inner.StandardOutput;
        public Stream StandardError => Inner.StandardError;
        public Task<int> WaitForExitAsync() => Inner.WaitForExitAsync();
        public ChildProcessExitState GetExitState() => Inner.GetExitState();
        public ChildProcessKillOutcome KillProcessTree() => Inner.KillProcessTree();
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    private sealed record SessionFixture(
        SessionTestProcess Process,
        WorkerEventStateBridge Bridge,
        ChildWorkerSession Session);

    private sealed record TerminalCloseFixture(
        TerminalCloseTestProcess Process,
        FaultingInputCloseStream Input,
        WorkerEventStateBridge Bridge,
        TerminalProjectionGate Sink,
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
