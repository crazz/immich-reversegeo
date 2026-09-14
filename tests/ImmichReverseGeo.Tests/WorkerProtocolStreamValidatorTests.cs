using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class WorkerProtocolStreamValidatorTests
{
    [TestMethod]
    public void Validate_RequiresReadyFirstAtSequenceOne_AndOnlyOnce()
    {
        var validator = new WorkerProtocolEventStreamValidator();

        AssertFailure(validator.Validate(WorkerProtocolV1TestData.Started()), WorkerProtocolFailureCode.InvalidLifecycle);
        AssertFailure(validator.Validate(WorkerProtocolV1TestData.Ready(2)), WorkerProtocolFailureCode.InvalidLifecycle);
        Assert.IsTrue(validator.Validate(WorkerProtocolV1TestData.Ready()).IsSuccess);
        AssertFailure(validator.Validate(WorkerProtocolV1TestData.Ready(2)), WorkerProtocolFailureCode.InvalidLifecycle);
        Assert.IsTrue(validator.Validate(WorkerProtocolV1TestData.Started()).IsSuccess);
        AssertFailure(validator.Validate(WorkerProtocolV1TestData.Started(3)), WorkerProtocolFailureCode.InvalidLifecycle);
        Assert.IsTrue(validator.Validate(Eligibility(3)).IsSuccess);
    }

    [TestMethod]
    public void Validate_RejectsDuplicateGapAndRegressionSequences_WithoutMutation()
    {
        var validator = NewAtStarted();

        AssertFailure(validator.Validate(Eligibility(2)), WorkerProtocolFailureCode.InvalidSequence);
        AssertFailure(validator.Validate(Eligibility(4)), WorkerProtocolFailureCode.InvalidSequence);
        Assert.IsTrue(validator.Validate(Eligibility(3)).IsSuccess);
    }

    [TestMethod]
    public void SequenceSuccessor_RejectsOverflowAfterLongMaxValueWithoutStateMutation()
    {
        var firstFailure = WorkerProtocolSequence.ValidateSuccessor(long.MaxValue, 1);
        var secondFailure = WorkerProtocolSequence.ValidateSuccessor(long.MaxValue, 1);

        Assert.IsNotNull(firstFailure);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidSequence, firstFailure.Code);
        Assert.IsNotNull(secondFailure);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidSequence, secondFailure.Code);
        Assert.IsNull(WorkerProtocolSequence.ValidateSuccessor(41, 42));
    }

    [TestMethod]
    public void Validate_RejectsMissingEmptyAndChangedCorrelation_WithoutMutation()
    {
        var validator = NewAtStarted();
        var changed = new WorkerProtocolEvent(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.EligibilityDeterminedType, 3, WorkerProtocolV1TestData.Midpoint, Guid.Parse("11111111-1111-1111-1111-111111111111"), new EligibilityDeterminedPayload(1));

        AssertFailure(validator.Validate(changed), WorkerProtocolFailureCode.InvalidCorrelation);
        Assert.IsTrue(validator.Validate(Eligibility(3)).IsSuccess);
    }

    [TestMethod]
    public void Validate_RequiresEligibilityBeforeProgressActivitiesAndLogs_AndAllowsPreCountCancellationOrFailure()
    {
        foreach (var nonterminal in new WorkerProtocolEvent[]
        {
            Progress(3, 0, 0, 0, 0),
            ActivityStarted(3),
            Log(3)
        })
        {
            var validator = NewAtStarted();
            AssertFailure(validator.Validate(nonterminal), WorkerProtocolFailureCode.InvalidLifecycle);
            Assert.IsTrue(validator.Validate(Eligibility(3)).IsSuccess);
        }

        AssertTerminalAccepted(WorkerProtocolV1.CancelledType, new CancelledPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0));
        AssertTerminalAccepted(WorkerProtocolV1.FailedType, new FailedPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0, "safe"));
    }

    [TestMethod]
    public void Validate_RequiresEligibilityForCompletionAndAllowsItOnlyOnce()
    {
        var validator = NewAtStarted();
        AssertFailure(validator.Validate(Terminal(3, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0))), WorkerProtocolFailureCode.InvalidLifecycle);
        Assert.IsTrue(validator.Validate(Eligibility(3)).IsSuccess);
        AssertFailure(validator.Validate(Eligibility(4)), WorkerProtocolFailureCode.InvalidLifecycle);
    }

    [TestMethod]
    public void Validate_AcceptsDiagnosticsAfterEligibility()
    {
        var validator = NewAtEligibility();

        Assert.IsTrue(validator.Validate(Log(4)).IsSuccess);
        Assert.IsTrue(validator.Validate(Terminal(5, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0))).IsSuccess);
    }

    [TestMethod]
    public void Validate_RejectsInvalidActivityPairingAndRequiresCleanupBeforeTerminal()
    {
        var validator = NewAtEligibility();
        var otherActivity = Guid.Parse("11111111-1111-1111-1111-111111111111");

        AssertFailure(validator.Validate(ActivityEnded(4, otherActivity)), WorkerProtocolFailureCode.InvalidLifecycle);
        Assert.IsTrue(validator.Validate(ActivityStarted(4)).IsSuccess);
        AssertFailure(validator.Validate(ActivityStarted(5)), WorkerProtocolFailureCode.InvalidLifecycle);
        AssertFailure(validator.Validate(Terminal(5, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0))), WorkerProtocolFailureCode.InvalidLifecycle);
        Assert.IsTrue(validator.Validate(ActivityEnded(5, WorkerProtocolV1TestData.ActivityId)).IsSuccess);
        Assert.IsTrue(validator.Validate(Terminal(6, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0))).IsSuccess);
    }

    [TestMethod]
    public void Validate_RequiresMonotonicProgressWithinEligibility()
    {
        var validator = NewAtEligibility(2);

        AssertFailure(validator.Validate(Progress(4, 2, 1, 0, 1)), WorkerProtocolFailureCode.InvalidLifecycle);
        AssertFailure(validator.Validate(Progress(4, 3, 1, 1, 1)), WorkerProtocolFailureCode.InvalidLifecycle);
        Assert.IsTrue(validator.Validate(Progress(4, 1, 1, 0, 0)).IsSuccess);
        AssertFailure(validator.Validate(Progress(5, 2, 0, 1, 1)), WorkerProtocolFailureCode.InvalidLifecycle);
        Assert.IsTrue(validator.Validate(Progress(5, 2, 1, 1, 0)).IsSuccess);
    }

    [TestMethod]
    public void Validate_RejectsTerminalTriggerStartAndCountMismatch_BeforeMutation()
    {
        var validator = NewAtEligibility();
        var wrongTrigger = new CompletedPayload("scheduled", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0);
        var wrongStart = new CompletedPayload("manual", WorkerProtocolV1TestData.Midpoint, WorkerProtocolV1TestData.End, 0, 0, 0, 0);
        var wrongCounts = new CompletedPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 1, 1, 0, 0);

        AssertFailure(validator.Validate(Terminal(4, WorkerProtocolV1.CompletedType, wrongTrigger)), WorkerProtocolFailureCode.InvalidLifecycle);
        AssertFailure(validator.Validate(Terminal(4, WorkerProtocolV1.CompletedType, wrongStart)), WorkerProtocolFailureCode.InvalidLifecycle);
        AssertFailure(validator.Validate(Terminal(4, WorkerProtocolV1.CompletedType, wrongCounts)), WorkerProtocolFailureCode.InvalidLifecycle);
        Assert.IsTrue(validator.Validate(Terminal(4, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0))).IsSuccess);
    }

    [TestMethod]
    public void Validate_RejectsTimestampRegressionAndPostTerminalMessages()
    {
        var validator = NewAtEligibility();
        var oldTimestamp = new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 4, WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.RunId, new LogEmittedPayload("information", "late"));
        AssertFailure(validator.Validate(oldTimestamp), WorkerProtocolFailureCode.InvalidLifecycle);
        Assert.IsTrue(validator.Validate(Terminal(4, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0))).IsSuccess);
        AssertFailure(validator.Validate(Log(5)), WorkerProtocolFailureCode.InvalidLifecycle);
    }

    [TestMethod]
    public void Finalize_DistinguishesEmptyReadyOnlyMissingAndTerminalStreams()
    {
        var empty = new WorkerProtocolEventStreamValidator().FinalizeStream();
        Assert.IsFalse(empty.IsComplete);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidLifecycle, empty.Failure!.Code);

        var readyOnly = new WorkerProtocolEventStreamValidator();
        Assert.IsTrue(readyOnly.Validate(WorkerProtocolV1TestData.Ready()).IsSuccess);
        Assert.IsTrue(readyOnly.FinalizeStream().IsComplete);

        var missingTerminal = NewAtStarted();
        Assert.IsFalse(missingTerminal.FinalizeStream().IsComplete);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidLifecycle, missingTerminal.FinalizeStream().Failure!.Code);

        var complete = NewAtEligibility();
        Assert.IsTrue(complete.Validate(Terminal(4, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0))).IsSuccess);
        Assert.IsTrue(complete.FinalizeStream().IsComplete);

        foreach (var terminal in new TerminalPayload[]
        {
            new CancelledPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0),
            new FailedPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0, "safe")
        })
        {
            var terminalStream = NewAtStarted();
            var type = terminal is CancelledPayload ? WorkerProtocolV1.CancelledType : WorkerProtocolV1.FailedType;
            Assert.IsTrue(terminalStream.Validate(Terminal(3, type, terminal)).IsSuccess);
            Assert.IsTrue(terminalStream.FinalizeStream().IsComplete);
        }
    }

    private static WorkerProtocolEventStreamValidator NewAtStarted()
    {
        var validator = new WorkerProtocolEventStreamValidator();
        Assert.IsTrue(validator.Validate(WorkerProtocolV1TestData.Ready()).IsSuccess);
        Assert.IsTrue(validator.Validate(WorkerProtocolV1TestData.Started()).IsSuccess);
        return validator;
    }

    private static WorkerProtocolEventStreamValidator NewAtEligibility(long count = 1)
    {
        var validator = NewAtStarted();
        Assert.IsTrue(validator.Validate(Eligibility(3, count)).IsSuccess);
        return validator;
    }

    private static WorkerProtocolEvent Eligibility(long sequence, long count = 1) =>
        new(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.EligibilityDeterminedType, sequence, WorkerProtocolV1TestData.Midpoint, WorkerProtocolV1TestData.RunId, new EligibilityDeterminedPayload(count));

    private static WorkerProtocolEvent Progress(long sequence, long processed, long updated, long skipped, long failed) =>
        new(WorkerProtocolV1.ProgressCategory, WorkerProtocolV1.ProgressChangedType, sequence, WorkerProtocolV1TestData.Midpoint, WorkerProtocolV1TestData.RunId, new ProgressChangedPayload(processed, updated, skipped, failed));

    private static WorkerProtocolEvent ActivityStarted(long sequence) =>
        new(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityStartedType, sequence, WorkerProtocolV1TestData.Midpoint, WorkerProtocolV1TestData.RunId, new ActivityStartedPayload(WorkerProtocolV1TestData.ActivityId, "download"));

    private static WorkerProtocolEvent ActivityEnded(long sequence, Guid activityId) =>
        new(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityEndedType, sequence, WorkerProtocolV1TestData.Midpoint, WorkerProtocolV1TestData.RunId, new ActivityEndedPayload(activityId));

    private static WorkerProtocolEvent Log(long sequence) =>
        new(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, sequence, WorkerProtocolV1TestData.Midpoint, WorkerProtocolV1TestData.RunId, new LogEmittedPayload("information", "log"));

    private static WorkerProtocolEvent Terminal(long sequence, string type, TerminalPayload payload) =>
        new(WorkerProtocolV1.TerminalCategory, type, sequence, payload.EndedAtUtc, WorkerProtocolV1TestData.RunId, payload);

    private static void AssertTerminalAccepted(string type, TerminalPayload payload)
    {
        var validator = NewAtStarted();
        Assert.IsTrue(validator.Validate(Terminal(3, type, payload)).IsSuccess);
    }

    private static void AssertFailure(WorkerProtocolParseResult result, WorkerProtocolFailureCode code)
    {
        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Event);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(code, result.Failure.Code);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Diagnostic));
        Assert.IsTrue(result.Failure.Diagnostic.Length <= 256);
        Assert.IsFalse(result.Failure.Diagnostic.Contains("JsonException", StringComparison.Ordinal));
        Assert.IsFalse(result.Failure.Diagnostic.Contains("StackTrace", StringComparison.Ordinal));
        Assert.IsFalse(result.Failure.Diagnostic.Contains("\n at ", StringComparison.Ordinal));
    }
}
