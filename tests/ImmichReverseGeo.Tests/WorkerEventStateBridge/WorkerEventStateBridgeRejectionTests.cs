using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.WorkerEventStateBridge;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change27")]
public sealed class WorkerEventStateBridgeRejectionTests
{
    [TestMethod]
    [DataRow(-1L)]
    [DataRow(1L)]
    [DataRow(50L)]
    public async Task WrongSequence_IsRejectedWithoutAdvancingTheSharedCursor(long offset)
    {
        await using var test = new BridgeTestCase();
        await test.BeginAsync(0);
        var log = new LogEmitted(test.Request, ProcessingLogLevel.Information, "accepted after rejection");
        await test.RejectWithoutMutationAsync(test.Frame(log, test.NextSequence + offset));
        var first = test.Bridge.FirstObservation;
        await test.RejectWithoutMutationAsync(test.Frame(log, test.NextSequence + offset));
        Assert.AreSame(first, test.Bridge.FirstObservation, "Only the first bounded observation is retained.");
        await test.SendAsync(log);
        StringAssert.EndsWith(test.Logs.Last(), "accepted after rejection");
        await test.FinishAsync(ProcessingRunOutcome.Completed);
    }

    [TestMethod]
    [DataRow("run-id")]
    [DataRow("run-start-trigger")]
    public async Task AdmittedRequestMismatch_IsRejectedBeforeClaimingRun(string mismatch)
    {
        await using var test = new BridgeTestCase();
        await test.ReadyAsync();
        var request = mismatch == "run-id"
            ? new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual)
            : new ProcessingRunRequest(test.Request.RunId, ProcessingRunTrigger.Scheduled);
        await test.RejectWithoutMutationAsync(test.Frame(new RunStarted(request, BridgeTestCase.StartedAt)));
        await test.SendAsync(new RunStarted(test.Request, BridgeTestCase.StartedAt));
        await test.SendAsync(new EligibilityDetermined(test.Request, 0));
        await test.FinishAsync(ProcessingRunOutcome.Completed);
    }

    [TestMethod]
    [DataRow("start-before-ready")]
    [DataRow("eligibility-before-start")]
    [DataRow("log-before-eligibility")]
    [DataRow("completed-before-eligibility")]
    public async Task PrematureLifecycle_IsRejectedAndCorrectNextEventRemainsAcceptable(string kind)
    {
        await using var test = new BridgeTestCase(previousRun: true);
        if (kind != "start-before-ready")
        {
            await test.ReadyAsync();
        }
        if (kind is "log-before-eligibility" or "completed-before-eligibility")
        {
            await test.SendAsync(new RunStarted(test.Request, BridgeTestCase.StartedAt));
        }

        ProcessingEvent invalid = kind switch
        {
            "start-before-ready" => new RunStarted(test.Request, BridgeTestCase.StartedAt),
            "eligibility-before-start" => new EligibilityDetermined(test.Request, 0),
            "log-before-eligibility" => new LogEmitted(test.Request, ProcessingLogLevel.Information, "premature"),
            _ => new RunFinished(test.Request, test.Result(ProcessingRunOutcome.Completed))
        };
        await test.RejectWithoutMutationAsync(test.Frame(invalid));
        if (kind == "start-before-ready")
        {
            await test.ReadyAsync();
        }
        if (kind is "start-before-ready" or "eligibility-before-start")
        {
            await test.SendAsync(new RunStarted(test.Request, BridgeTestCase.StartedAt));
        }
        await test.SendAsync(new EligibilityDetermined(test.Request, 0));
        await test.FinishAsync(ProcessingRunOutcome.Completed);
    }

    [TestMethod]
    [DataRow("duplicate-ready")]
    [DataRow("duplicate-start")]
    [DataRow("duplicate-eligibility")]
    [DataRow("unknown-activity-end")]
    [DataRow("duplicate-activity-start")]
    [DataRow("terminal-open-activity")]
    [DataRow("progress-no-disposition")]
    [DataRow("progress-skipped-disposition")]
    [DataRow("progress-regression")]
    [DataRow("progress-exceeds-eligibility")]
    [DataRow("terminal-without-progress")]
    [DataRow("terminal-progress-mismatch")]
    [DataRow("terminal-start-mismatch")]
    [DataRow("terminal-trigger-mismatch")]
    [DataRow("timestamp-regression")]
    public async Task TypedLifecycleContradiction_PreservesAllStateAndNotifications(string kind)
    {
        await using var test = new BridgeTestCase();
        await test.BeginAsync(kind == "progress-exceeds-eligibility" ? 0 : 3);
        var activityId = Guid.NewGuid();
        if (kind is "duplicate-activity-start" or "terminal-open-activity")
        {
            await test.SendAsync(new ActivityStarted(test.Request, activityId, "active"));
        }
        if (kind is "progress-regression" or "terminal-progress-mismatch")
        {
            await test.ProgressAsync(1, 0, 0);
        }

        ProcessingEvent attempted = kind switch
        {
            "duplicate-start" => new RunStarted(test.Request, BridgeTestCase.StartedAt),
            "duplicate-eligibility" => new EligibilityDetermined(test.Request, 3),
            "unknown-activity-end" => new ActivityEnded(test.Request, activityId),
            "duplicate-activity-start" => new ActivityStarted(test.Request, activityId, "active"),
            "progress-no-disposition" => new ProgressChanged(test.Request, new ProcessingProgress(0, 0, 0, 0)),
            "progress-skipped-disposition" => new ProgressChanged(test.Request, new ProcessingProgress(2, 2, 0, 0)),
            "progress-regression" => new ProgressChanged(test.Request, new ProcessingProgress(2, 0, 2, 0)),
            "progress-exceeds-eligibility" => new ProgressChanged(test.Request, new ProcessingProgress(1, 1, 0, 0)),
            "terminal-without-progress" => new RunFinished(test.Request, test.Result(ProcessingRunOutcome.Completed, 1)),
            "terminal-start-mismatch" => new RunFinished(test.Request, new ProcessingRunResult(test.Request,
                BridgeTestCase.StartedAt.AddTicks(1), BridgeTestCase.EndedAt, 0, 0, 0, 0, ProcessingRunOutcome.Completed, null)),
            "timestamp-regression" => new LogEmitted(test.Request, ProcessingLogLevel.Information, "old timestamp"),
            _ => new RunFinished(test.Request, test.Result(ProcessingRunOutcome.Completed))
        };
        var frame = test.Frame(attempted);
        if (kind == "duplicate-ready")
        {
            frame = WorkerProtocolMapper.Ready(test.NextSequence, BridgeTestCase.StartedAt.AddTicks(test.NextSequence));
        }
        else if (kind == "timestamp-regression")
        {
            frame = WorkerProtocolMapper.Map(attempted, test.NextSequence, BridgeTestCase.ReadyAt);
        }
        else if (kind == "terminal-trigger-mismatch")
        {
            var otherTrigger = new ProcessingRunRequest(test.Request.RunId, ProcessingRunTrigger.Scheduled);
            var result = new ProcessingRunResult(otherTrigger, BridgeTestCase.StartedAt, BridgeTestCase.EndedAt,
                0, 0, 0, 0, ProcessingRunOutcome.Completed, null);
            frame = WorkerProtocolMapper.Map(new RunFinished(otherTrigger, result), test.NextSequence);
        }

        await test.RejectWithoutMutationAsync(frame);
        Assert.IsInstanceOfType<WorkerEventStateBridgeObservation.EventRejected>(test.Bridge.FirstObservation);
        await test.SendAsync(new LogEmitted(test.Request, ProcessingLogLevel.Information, "cursor retained"));
        StringAssert.EndsWith(test.Logs.Last(), "cursor retained");
    }

    [TestMethod]
    [DataRow(ProcessingRunOutcome.Cancelled)]
    [DataRow(ProcessingRunOutcome.Failed)]
    public async Task EligibleWithoutProgress_RequiresZeroTerminalCounts(ProcessingRunOutcome outcome)
    {
        await using var test = new BridgeTestCase();
        await test.BeginAsync(3);
        await test.RejectWithoutMutationAsync(test.Frame(new RunFinished(test.Request, test.Result(outcome, 1))));
        await test.FinishAsync(outcome);
        Assert.IsTrue(test.Bridge.IsTerminal);
        Assert.AreEqual(0L, test.State.ProcessedThisRun);
    }

    [TestMethod]
    public async Task DuplicateTerminalAndLaterEvents_NeverReplayCompletionOrSummary()
    {
        await using var test = new BridgeTestCase();
        await test.BeginAsync(0);
        var terminal = test.Frame(new RunFinished(test.Request, test.Result(ProcessingRunOutcome.Completed)));
        await test.FinishAsync(ProcessingRunOutcome.Completed);
        await test.RejectWithoutMutationAsync(terminal);
        await test.RejectWithoutMutationAsync(test.Frame(new LogEmitted(test.Request, ProcessingLogLevel.Error, "late")));
        Assert.AreEqual(1, test.Logs.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task StaleAdapterArm_IsRejectedWithoutConsumingTheProjectionCursor()
    {
        await using var test = new BridgeTestCase();
        await test.BeginAsync(0);
        Assert.IsTrue(await test.Adapter.TryProjectAsync(new RunFinished(test.Request, test.Result(ProcessingRunOutcome.Completed)), CancellationToken.None));
        var next = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        test.State.MarkPending();
        Assert.IsTrue(test.Adapter.Arm(next));
        await test.RejectWithoutMutationAsync(test.Frame(new LogEmitted(test.Request, ProcessingLogLevel.Error, "stale")));
        Assert.IsTrue(test.Adapter.IsArmed(next));
    }

    [TestMethod]
    public async Task ReadyAfterArmReplacement_IsRejectedBeforeReadinessCanAuthorizeExecute()
    {
        await using var test = new BridgeTestCase();
        Assert.IsTrue(await test.Adapter.TryProjectAsync(
            new RunFinished(test.Request, test.Result(ProcessingRunOutcome.Cancelled)), CancellationToken.None));
        var next = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        test.State.MarkPending();
        Assert.IsTrue(test.Adapter.Arm(next));
        var ready = WorkerProtocolMapper.Ready(1, BridgeTestCase.ReadyAt);
        await test.RejectWithoutMutationAsync(ready);
        Assert.IsFalse(test.Bridge.IsReady, "A stale bridge must not acknowledge readiness.");
        var rejected = (WorkerEventStateBridgeObservation.EventRejected)test.Bridge.FirstObservation!;
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidCorrelation, rejected.Failure.Code);
        await test.RejectWithoutMutationAsync(ready);
        Assert.AreSame(rejected, test.Bridge.FirstObservation);
        var current = test.Snapshot();
        await test.Bridge.DisposeAsync();
        Assert.AreEqual(current, test.Snapshot());
        Assert.IsTrue(test.Adapter.IsArmed(next));
    }

    [TestMethod]
    public async Task Factory_RequiresTheExactAlreadyArmedRequestWithoutMutatingState()
    {
        await using var test = new BridgeTestCase();
        var factory = new WorkerEventStateBridgeFactory(test.Adapter);
        var equivalent = new ProcessingRunRequest(test.Request.RunId, test.Request.Trigger);
        var before = test.Snapshot();
        Assert.ThrowsExactly<InvalidOperationException>(() => factory.Create(equivalent));
        Assert.AreEqual(before, test.Snapshot());
        Assert.IsTrue(test.Adapter.IsArmed(test.Request));
    }

    [TestMethod]
    public void SharedPreview_DoesNotAdvanceUntilValidateCommits()
    {
        var validator = new WorkerProtocolEventStreamValidator();
        var ready = WorkerProtocolMapper.Ready(1, BridgeTestCase.ReadyAt);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var started = WorkerProtocolMapper.Map(new RunStarted(request, BridgeTestCase.StartedAt), 2);
        Assert.IsTrue(validator.Preview(ready).IsSuccess);
        Assert.IsTrue(validator.Preview(ready).IsSuccess);
        Assert.IsFalse(validator.Preview(started).IsSuccess, "Previewing ready must not claim readiness.");
        Assert.IsTrue(validator.Validate(ready).IsSuccess);
        Assert.IsFalse(validator.Preview(ready).IsSuccess);
        Assert.IsTrue(validator.Preview(started).IsSuccess);
        Assert.IsTrue(validator.Preview(started).IsSuccess);
        Assert.IsTrue(validator.Validate(started).IsSuccess);
        Assert.IsFalse(validator.Preview(started).IsSuccess);
    }

    [TestMethod]
    public void ImmutableTypedBoundary_RejectsImpossibleShapesBeforeTheyCanReachTheBridge()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new WorkerProtocolEvent("unknown", "unknown", 1, BridgeTestCase.ReadyAt, null, new ReadyPayload()));
        Assert.Throws<ArgumentException>(() => new WorkerProtocolEvent(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.RunStartedType,
            2, BridgeTestCase.StartedAt, id, new ReadyPayload()));
        Assert.Throws<ArgumentException>(() => new WorkerProtocolEvent(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.ReadyType,
            1, BridgeTestCase.ReadyAt.ToOffset(TimeSpan.FromHours(1)), null, new ReadyPayload()));
        Assert.Throws<ArgumentException>(() => new ProgressChangedPayload(2, 1, 0, 0));
        Assert.Throws<ArgumentException>(() => new ActivityStartedPayload(Guid.Empty, "invalid"));
        var request = new ProcessingRunRequest(id, ProcessingRunTrigger.Manual);
        Assert.Throws<ArgumentException>(() => new ProcessingRunResult(request, BridgeTestCase.StartedAt, BridgeTestCase.EndedAt,
            0, 0, 0, 0, ProcessingRunOutcome.Failed, null));
        Assert.Throws<ArgumentException>(() => new ProcessingRunResult(request, BridgeTestCase.StartedAt, BridgeTestCase.EndedAt,
            0, 0, 0, 0, ProcessingRunOutcome.Completed, "invalid detail"));
    }
}
