using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.ScheduledChildWorkerExecution;

[TestClass]
[TestCategory("Change35")]
[DoNotParallelize]
public sealed class ProcessScheduledGateTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task DetectorPositive_WorkerAuthoritativeNoWork_CompletesOneChildWithoutFallbackOrRetry()
    {
        var gate = CoordinatorScheduledGateTests.SignalGate.Decided(true);
        await using var fixture = CoordinatorScheduledGateTests.ScheduledCoordinatorFixture.Create(
            gate,
            CoordinatorScheduledGateTests.ProcessFixturePlan.NoWork);

        Task<ScheduledTriggerResult> scheduled = fixture.TriggerScheduledAsync();
        await gate.Entered.Task.WaitAsync(Bound);
        await fixture.Launcher.WaitForLaunchCountAsync(1).WaitAsync(Bound);
        var lease = fixture.Launcher.Leases.Single();
        ProcessingRunRequest request = lease.Request;

        Assert.AreEqual(ProcessingRunTrigger.Scheduled, request.Trigger, "the worker receives the admitted scheduled request");
        Assert.AreNotEqual(Guid.Empty, request.RunId, "the worker request retains only its real immutable run identity");
        Assert.IsFalse(scheduled.IsCompleted, "the scheduled caller awaits child terminal and cleanup finality");
        await lease.CompleteAsync().WaitAsync(Bound);
        lease.AssertExactCapture();

        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await scheduled.WaitAsync(Bound));
        ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(request)
            ?? throw new AssertFailedException("worker authoritative no-work did not create its worker finalization receipt");
        Assert.AreEqual(ProcessingRunOutcome.Completed, receipt.Result.Outcome, "worker-authoritative zero is an ordinary completed child result");
        Assert.AreEqual(1, fixture.Launcher.CallCount, "positive detector starts one child");
        Assert.AreEqual(0, fixture.ForbiddenResolutionCount, "worker-zero does not resolve the in-process or geodata graph");
        Assert.IsNull(fixture.Coordinator.ActiveRequest, "child cleanup releases the matching scheduled handle");
    }

    [TestMethod]
    public async Task DetectorPositive_WorkerBusyExitThree_RemainsOneFailedChildWithoutFallbackOrRetry()
    {
        var gate = CoordinatorScheduledGateTests.SignalGate.Decided(true);
        await using var fixture = CoordinatorScheduledGateTests.ScheduledCoordinatorFixture.Create(
            gate,
            new CoordinatorScheduledGateTests.ProcessFixturePlan(
                "terminal-mismatch",
                true,
                ["--terminal", "failed", "--exit-code", "3"]));

        Task<ScheduledTriggerResult> scheduled = fixture.TriggerScheduledAsync();
        await gate.Entered.Task.WaitAsync(Bound);
        await fixture.Launcher.WaitForLaunchCountAsync(1).WaitAsync(Bound);
        var lease = fixture.Launcher.Leases.Single();
        ProcessingRunRequest request = lease.Request;
        await lease.CompleteAsync().WaitAsync(Bound);
        lease.AssertExactCapture();

        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await scheduled.WaitAsync(Bound));
        ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(request)
            ?? throw new AssertFailedException("worker busy terminal did not create its worker finalization receipt");
        Assert.AreEqual(ProcessingRunOutcome.Failed, receipt.Result.Outcome, "exit 3 remains the worker failed/busy outcome");
        Assert.AreEqual(1, fixture.Launcher.CallCount, "worker busy has no replacement child or retry");
        Assert.AreEqual(0, fixture.ForbiddenResolutionCount, "worker busy has no in-process fallback");
        Assert.IsNull(fixture.Coordinator.ActiveRequest, "worker busy still releases the matching scheduled handle");
    }
}
