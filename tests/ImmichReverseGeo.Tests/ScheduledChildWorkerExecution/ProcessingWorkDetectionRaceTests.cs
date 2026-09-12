using System.Collections.Concurrent;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Tests.ProcessingRunLocking;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.ScheduledChildWorkerExecution;

[TestClass]
[TestCategory("Change57")]
[DoNotParallelize]
public sealed class ProcessingWorkDetectionRaceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task PositiveDetectionStillRunsTheIndependentWorkerLockAndZeroEligibilityGate()
    {
        var ledger = new ConcurrentQueue<string>();
        var detector = new ProcessingWorkDetectorStub((_, _) =>
        {
            ledger.Enqueue("detection-positive");
            return Task.FromResult(ProcessingWorkDetectorStub.Result(true));
        });
        var lockSession = new RecordingRunLockSession
        {
            ExecuteBehavior = (command, _) =>
            {
                ledger.Enqueue(command == ProcessingRunLockCommand.Acquire ? "Acquire"
                    : command == ProcessingRunLockCommand.Release ? "Release" : "Probe");
                return Task.FromResult<object?>(command == ProcessingRunLockCommand.Probe ? 1 : true);
            }
        };
        var runLock = new PostgresqlProcessingRunLock(
            new RecordingRunLockSessionFactory(lockSession), new ManualRunLockTimeProvider(),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var domain = new ExecutorFixture();
        domain.CountBehavior = _ =>
        {
            ledger.Enqueue("authoritative-zero");
            return Task.FromResult(0L);
        };
        var executor = new ProcessingRunExecutor(domain.Logger, domain, domain, domain, domain, domain,
            domain, domain.TimeProvider, runLock, new WorkerProcessExitOutcomeAccumulator());
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var admission = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        // Transport is substituted here; companion process tests exercise actual child transport.
        await using var coordinator = new ProcessingRunCoordinator(state, reporter, detector,
            ProcessingRunBackendTestScopeFactory.Create(executor), NullLogger<ProcessingRunCoordinator>.Instance, admission);

        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal,
            await ((IScheduledRunTrigger)coordinator).TriggerScheduledAsync(CancellationToken.None).WaitAsync(Bound));

        CollectionAssert.AreEqual(new[] { "detection-positive", "Acquire", "authoritative-zero", "Release" }, ledger.ToArray(),
            "The advisory observation cannot bypass or replace the worker's lock and fresh count.");
        Assert.AreEqual(1, detector.Calls.Length);
        Assert.AreEqual(1, domain.CountCalls);
        Assert.AreEqual(0, domain.ConfigCalls);
        Assert.AreEqual(0, domain.SkippedCalls);
        Assert.AreEqual(0, domain.BatchCalls);
        Assert.AreEqual(1, lockSession.DisposeCalls);
        Assert.AreEqual(0L, state.TotalUnprocessed);
        Assert.AreEqual(0L, state.ProcessedThisRun);
        Assert.IsFalse(state.IsRunning);
        Assert.IsNotNull(state.LastRunCompleted);
        Assert.IsNull(coordinator.ActiveRequest);
        Assert.IsNull(admission.Snapshot.ActiveOwner);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task NegativeObservationRemainsClosedUntilAnOrdinaryLaterTrigger(int implementationKind)
    {
        var kind = (ProcessingWorkDetectorKind)implementationKind;
        var detector = ProcessingWorkDetectorStub.Scripted(
            ProcessingWorkDetectorStub.Result(false, kind), ProcessingWorkDetectorStub.Result(true, kind));
        await using var fixture = CoordinatorScheduledGateTests.ScheduledCoordinatorFixture.Create(
            detector, CoordinatorScheduledGateTests.ProcessFixturePlan.NoWork);
        var initial = CoordinatorScheduledGateTests.Snapshot(fixture.State);

        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await fixture.TriggerScheduledAsync().WaitAsync(Bound));
        Assert.AreEqual(1, detector.Calls.Length, "New work is scripted but cannot reopen the completed occurrence.");
        Assert.AreEqual(0, fixture.Launcher.CallCount);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.AreEqual(initial, CoordinatorScheduledGateTests.Snapshot(fixture.State));

        Task<ScheduledTriggerResult> later = fixture.TriggerScheduledAsync();
        await fixture.Launcher.WaitForLaunchCountAsync(1).WaitAsync(Bound);
        var child = fixture.Launcher.Leases.Single();
        await child.CompleteAsync().WaitAsync(Bound);
        child.AssertExactCapture();
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await later.WaitAsync(Bound));
        Assert.AreEqual(2, detector.Calls.Length);
        Assert.AreEqual(1, fixture.Launcher.CallCount);
        Assert.AreEqual(0, fixture.ForbiddenResolutionCount);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.IsNotNull(fixture.Reporter.GetFinalizationReceipt(child.Request));
    }

    [TestMethod]
    public async Task ManualProcessingUsesItsChildWithoutEvaluatingTheDetector()
    {
        var detector = ProcessingWorkDetectorStub.FailOnUse();
        await using var fixture = CoordinatorScheduledGateTests.ScheduledCoordinatorFixture.Create(
            detector, CoordinatorScheduledGateTests.ProcessFixturePlan.NoWork);
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound));
        await fixture.Launcher.WaitForLaunchCountAsync(1).WaitAsync(Bound);
        await fixture.Launcher.Leases.Single().CompleteAsync().WaitAsync(Bound);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        Assert.AreEqual(0, detector.Calls.Length);
        Assert.AreEqual(1, fixture.Launcher.CallCount);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
    }
}
