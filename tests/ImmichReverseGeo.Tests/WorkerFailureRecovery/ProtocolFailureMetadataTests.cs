using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerFailureRecovery;

[TestClass]
[TestCategory("Change30")]
public class ProtocolFailureMetadataTests
{
    [TestMethod]
    public void Preview_ReadinessFailureHasTypedDetailAndDoesNotAdvanceState()
    {
        var validator = new WorkerProtocolEventStreamValidator();

        AssertFailure(validator.Preview(WorkerProtocolV1TestData.Started()),
            WorkerProtocolFailureDetail.Readiness,
            "The first event must be ready at sequence one.");
        Assert.IsTrue(validator.Validate(WorkerProtocolV1TestData.Ready()).IsSuccess, "preview-leaves-ready-slot-available");
    }

    [TestMethod]
    public void Preview_ProgressFailureHasTypedDetailAndDoesNotAdvanceState()
    {
        var validator = NewAtEligibility(2);

        AssertFailure(validator.Preview(Progress(4, 3, 1, 1, 1)),
            WorkerProtocolFailureDetail.ProgressConsistency,
            "Progress must not exceed eligibility.");
        Assert.IsTrue(validator.Validate(Progress(4, 1, 1, 0, 0)).IsSuccess, "preview-leaves-progress-cursor-unchanged");
    }

    [TestMethod]
    public void Preview_TerminalFailureHasTypedDetailAndDoesNotAdvanceState()
    {
        var validator = NewAtEligibility();
        var wrongTrigger = new CompletedPayload("scheduled", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0);

        AssertFailure(validator.Preview(Terminal(4, wrongTrigger)),
            WorkerProtocolFailureDetail.TerminalConsistency,
            "Terminal trigger must match run-started.");
        Assert.IsTrue(validator.Validate(Terminal(4, Completed())).IsSuccess, "preview-leaves-terminal-slot-available");
    }

    [TestMethod]
    public void Preview_ActivityFailureHasTypedDetailAndDoesNotAdvanceState()
    {
        var validator = NewAtEligibility();
        var unknown = Guid.Parse("33333333-3333-3333-3333-333333333333");

        AssertFailure(validator.Preview(ActivityEnded(4, unknown)),
            WorkerProtocolFailureDetail.ActivityCardinality,
            "Activity end requires a matching start.");
        Assert.IsTrue(validator.Validate(ActivityStarted(4)).IsSuccess, "preview-leaves-activity-slot-available");
    }

    [TestMethod]
    public void Finalize_MissingTerminalHasTypedDetailWithoutChangingTheOriginalFailureContract()
    {
        var validator = NewAtStarted();

        var finalization = validator.FinalizeStream();

        Assert.IsFalse(finalization.IsComplete);
        Assert.IsNotNull(finalization.Failure);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidLifecycle, finalization.Failure.Code);
        Assert.AreEqual(WorkerProtocolFailureDetail.MissingTerminal, finalization.Failure.Detail);
        Assert.AreEqual("The accepted run is missing its terminal event.", finalization.Failure.Diagnostic);
    }

    [TestMethod]
    public void WorkerProtocolFailure_PreservesExistingDefaultAndValidatesTypedMetadata()
    {
        var untyped = new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidLifecycle, "existing diagnostic");
        var typed = new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidLifecycle, "typed diagnostic", WorkerProtocolFailureDetail.Readiness);

        Assert.AreEqual(WorkerProtocolFailureDetail.None, untyped.Detail);
        Assert.AreEqual(WorkerProtocolFailureDetail.Readiness, typed.Detail);
        Assert.ThrowsExactly<ArgumentException>(() => new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidLifecycle, " ", WorkerProtocolFailureDetail.Readiness));
        Assert.ThrowsExactly<ArgumentException>(() => new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidLifecycle, new string('x', 257), WorkerProtocolFailureDetail.Readiness));
    }

    private static WorkerProtocolEventStreamValidator NewAtStarted()
    {
        var validator = new WorkerProtocolEventStreamValidator();
        Assert.IsTrue(validator.Validate(WorkerProtocolV1TestData.Ready()).IsSuccess);
        Assert.IsTrue(validator.Validate(WorkerProtocolV1TestData.Started()).IsSuccess);
        return validator;
    }

    private static WorkerProtocolEventStreamValidator NewAtEligibility(long eligibleCount = 1)
    {
        var validator = NewAtStarted();
        Assert.IsTrue(validator.Validate(new WorkerProtocolEvent(
            WorkerProtocolV1.LifecycleCategory,
            WorkerProtocolV1.EligibilityDeterminedType,
            3,
            WorkerProtocolV1TestData.Midpoint,
            WorkerProtocolV1TestData.RunId,
            new EligibilityDeterminedPayload(eligibleCount))).IsSuccess);
        return validator;
    }

    private static WorkerProtocolEvent Progress(long sequence, long processed, long updated, long skipped, long failed) => new(
        WorkerProtocolV1.ProgressCategory,
        WorkerProtocolV1.ProgressChangedType,
        sequence,
        WorkerProtocolV1TestData.Midpoint,
        WorkerProtocolV1TestData.RunId,
        new ProgressChangedPayload(processed, updated, skipped, failed));

    private static WorkerProtocolEvent ActivityStarted(long sequence) => new(
        WorkerProtocolV1.ActivityCategory,
        WorkerProtocolV1.ActivityStartedType,
        sequence,
        WorkerProtocolV1TestData.Midpoint,
        WorkerProtocolV1TestData.RunId,
        new ActivityStartedPayload(WorkerProtocolV1TestData.ActivityId, "download"));

    private static WorkerProtocolEvent ActivityEnded(long sequence, Guid activityId) => new(
        WorkerProtocolV1.ActivityCategory,
        WorkerProtocolV1.ActivityEndedType,
        sequence,
        WorkerProtocolV1TestData.Midpoint,
        WorkerProtocolV1TestData.RunId,
        new ActivityEndedPayload(activityId));

    private static WorkerProtocolEvent Terminal(long sequence, CompletedPayload payload) => new(
        WorkerProtocolV1.TerminalCategory,
        WorkerProtocolV1.CompletedType,
        sequence,
        payload.EndedAtUtc,
        WorkerProtocolV1TestData.RunId,
        payload);

    private static CompletedPayload Completed() => new("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0);

    private static void AssertFailure(WorkerProtocolParseResult result, WorkerProtocolFailureDetail detail, string diagnostic)
    {
        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Event);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidLifecycle, result.Failure.Code, "failure-code-is-preserved");
        Assert.AreEqual(detail, result.Failure.Detail, "failure-detail-is-typed");
        Assert.AreEqual(diagnostic, result.Failure.Diagnostic, "failure-diagnostic-is-preserved");
    }
}
