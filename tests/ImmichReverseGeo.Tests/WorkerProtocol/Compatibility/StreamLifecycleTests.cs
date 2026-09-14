using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol.Compatibility;

[TestClass]
public class StreamLifecycleTests
{
    [TestMethod]
    public void AcceptedShapes_FinalizeWithoutFabricatingTerminal()
    {
        var readyOnly = new WorkerProtocolEventStreamValidator();
        Accept(readyOnly, CompatibilityData.Ready());
        Assert.IsTrue(readyOnly.FinalizeStream().IsComplete);

        foreach (var terminal in new WorkerProtocolEvent[]
        {
            CompatibilityData.Terminal(3, WorkerProtocolV1.CancelledType, new CancelledPayload("manual", CompatibilityData.Start, CompatibilityData.Start.AddSeconds(1), 0, 0, 0, 0)),
            CompatibilityData.Terminal(3, WorkerProtocolV1.FailedType, new FailedPayload("manual", CompatibilityData.Start, CompatibilityData.Start.AddSeconds(1), 0, 0, 0, 0, "failure"))
        })
        {
            var validator = AtStarted();
            Accept(validator, terminal);
            Assert.IsTrue(validator.FinalizeStream().IsComplete);
        }

        var complete = AtEligibility();
        Accept(complete, new WorkerProtocolEvent(WorkerProtocolV1.ProgressCategory, WorkerProtocolV1.ProgressChangedType, 4, CompatibilityData.Start.AddSeconds(2), CompatibilityData.RunId, new ProgressChangedPayload(1, 1, 0, 0)));
        Accept(complete, new WorkerProtocolEvent(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityStartedType, 5, CompatibilityData.Start.AddSeconds(3), CompatibilityData.RunId, new ActivityStartedPayload(CompatibilityData.ActivityId, "download")));
        Accept(complete, new WorkerProtocolEvent(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityEndedType, 6, CompatibilityData.Start.AddSeconds(4), CompatibilityData.RunId, new ActivityEndedPayload(CompatibilityData.ActivityId)));
        Accept(complete, CompatibilityData.Terminal(7, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", CompatibilityData.Start, CompatibilityData.Start.AddSeconds(5), 1, 1, 0, 0)));
        Assert.IsTrue(complete.FinalizeStream().IsComplete);
        var missing = AtStarted();
        Assert.IsFalse(missing.FinalizeStream().IsComplete);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidLifecycle, missing.FinalizeStream().Failure!.Code);
    }

    [TestMethod]
    public void RejectionsAreAtomicForSequenceCorrelationLifecycleActivityAndTerminal()
    {
        AssertCorrected(AtStarted(), new WorkerProtocolEvent(WorkerProtocolV1.ProgressCategory, WorkerProtocolV1.ProgressChangedType, 3, CompatibilityData.Start.AddSeconds(1), CompatibilityData.RunId, new ProgressChangedPayload(0, 0, 0, 0)), WorkerProtocolFailureCode.InvalidLifecycle, CompatibilityData.Eligibility());
        AssertCorrected(AtStarted(), new WorkerProtocolEvent(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.EligibilityDeterminedType, 4, CompatibilityData.Start.AddSeconds(1), CompatibilityData.RunId, new EligibilityDeterminedPayload(0)), WorkerProtocolFailureCode.InvalidSequence, CompatibilityData.Eligibility());
        AssertCorrected(AtStarted(), new WorkerProtocolEvent(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.EligibilityDeterminedType, 3, CompatibilityData.Start.AddSeconds(1), Guid.Parse("11111111-1111-1111-1111-111111111111"), new EligibilityDeterminedPayload(0)), WorkerProtocolFailureCode.InvalidCorrelation, CompatibilityData.Eligibility());

        var activity = AtEligibility();
        AssertCorrected(activity, new WorkerProtocolEvent(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityEndedType, 4, CompatibilityData.Start.AddSeconds(2), CompatibilityData.RunId, new ActivityEndedPayload(CompatibilityData.ActivityId)), WorkerProtocolFailureCode.InvalidLifecycle, new WorkerProtocolEvent(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityStartedType, 4, CompatibilityData.Start.AddSeconds(2), CompatibilityData.RunId, new ActivityStartedPayload(CompatibilityData.ActivityId, "download")));
        AssertCorrected(activity, CompatibilityData.Terminal(5, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", CompatibilityData.Start, CompatibilityData.Start.AddSeconds(3), 0, 0, 0, 0)), WorkerProtocolFailureCode.InvalidLifecycle, new WorkerProtocolEvent(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityEndedType, 5, CompatibilityData.Start.AddSeconds(3), CompatibilityData.RunId, new ActivityEndedPayload(CompatibilityData.ActivityId)));
        Accept(activity, CompatibilityData.Terminal(6, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", CompatibilityData.Start, CompatibilityData.Start.AddSeconds(4), 0, 0, 0, 0)));
        AssertRejected(activity, new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 7, CompatibilityData.Start.AddSeconds(5), CompatibilityData.RunId, new LogEmittedPayload("information", "after")), WorkerProtocolFailureCode.InvalidLifecycle);
        Assert.IsTrue(activity.FinalizeStream().IsComplete);
    }

    [TestMethod]
    public void CardinalityAndTerminalMismatchesRejectWithoutAdvancingState()
    {
        var validator = new WorkerProtocolEventStreamValidator();
        AssertRejected(validator, CompatibilityData.Started(), WorkerProtocolFailureCode.InvalidLifecycle);
        Accept(validator, CompatibilityData.Ready());
        AssertCorrected(validator, CompatibilityData.Ready(2), WorkerProtocolFailureCode.InvalidLifecycle, CompatibilityData.Started());
        AssertCorrected(validator, CompatibilityData.Started(3), WorkerProtocolFailureCode.InvalidLifecycle, CompatibilityData.Eligibility());
        AssertCorrected(validator, CompatibilityData.Eligibility(4), WorkerProtocolFailureCode.InvalidLifecycle, new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 4, CompatibilityData.Start.AddSeconds(2), CompatibilityData.RunId, new LogEmittedPayload("information", "log")));
        AssertCorrected(validator, CompatibilityData.Terminal(5, WorkerProtocolV1.CompletedType, new CompletedPayload("scheduled", CompatibilityData.Start, CompatibilityData.Start.AddSeconds(3), 0, 0, 0, 0)), WorkerProtocolFailureCode.InvalidLifecycle, CompatibilityData.Terminal(5, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", CompatibilityData.Start, CompatibilityData.Start.AddSeconds(3), 0, 0, 0, 0)));
        AssertRejected(validator, CompatibilityData.Terminal(6, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", CompatibilityData.Start, CompatibilityData.Start.AddSeconds(4), 0, 0, 0, 0)), WorkerProtocolFailureCode.InvalidLifecycle);
        Assert.IsTrue(validator.FinalizeStream().IsComplete);
    }

    [TestMethod]
    public void SequenceLifecycleActivityAndTimestampRejectionsAreSafeAndCorrectable()
    {
        AssertCorrected(AtStarted(), CompatibilityData.Terminal(3, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", CompatibilityData.Start, CompatibilityData.Start.AddSeconds(1), 0, 0, 0, 0)), WorkerProtocolFailureCode.InvalidLifecycle, CompatibilityData.Eligibility());
        AssertCorrected(AtStarted(), CompatibilityData.Eligibility(2), WorkerProtocolFailureCode.InvalidSequence, CompatibilityData.Eligibility());
        AssertCorrected(AtStarted(), CompatibilityData.Eligibility(1), WorkerProtocolFailureCode.InvalidSequence, CompatibilityData.Eligibility());
        AssertCorrected(AtStarted(), new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 3, CompatibilityData.Start.AddSeconds(1), CompatibilityData.RunId, new LogEmittedPayload("information", "before")), WorkerProtocolFailureCode.InvalidLifecycle, CompatibilityData.Eligibility());
        AssertCorrected(AtStarted(), new WorkerProtocolEvent(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityStartedType, 3, CompatibilityData.Start.AddSeconds(1), CompatibilityData.RunId, new ActivityStartedPayload(CompatibilityData.ActivityId, "before")), WorkerProtocolFailureCode.InvalidLifecycle, CompatibilityData.Eligibility());

        var activity = AtEligibility();
        var start = new WorkerProtocolEvent(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityStartedType, 4, CompatibilityData.Start.AddSeconds(2), CompatibilityData.RunId, new ActivityStartedPayload(CompatibilityData.ActivityId, "work"));
        Accept(activity, start);
        AssertCorrected(activity, new WorkerProtocolEvent(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityStartedType, 5, CompatibilityData.Start.AddSeconds(3), CompatibilityData.RunId, new ActivityStartedPayload(CompatibilityData.ActivityId, "work")), WorkerProtocolFailureCode.InvalidLifecycle, new WorkerProtocolEvent(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityEndedType, 5, CompatibilityData.Start.AddSeconds(3), CompatibilityData.RunId, new ActivityEndedPayload(CompatibilityData.ActivityId)));

        var regression = AtEligibility();
        AssertCorrected(regression, new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 4, CompatibilityData.Start, CompatibilityData.RunId, new LogEmittedPayload("information", "old")), WorkerProtocolFailureCode.InvalidLifecycle, new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 4, CompatibilityData.Start.AddSeconds(2), CompatibilityData.RunId, new LogEmittedPayload("information", "new")));
    }

    [TestMethod]
    public void StreamFailuresRedactHostileValidPayloadTextAndRemainAtomic()
    {
        const string marker = "SENTINEL Password=one Server=host;Database=db SELECT secret JsonException Exception at Worker.Method";
        var sequence = AtEligibility();
        AssertRedactedCorrected(sequence, new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 5, CompatibilityData.Start.AddSeconds(2), CompatibilityData.RunId, new LogEmittedPayload("information", marker)), WorkerProtocolFailureCode.InvalidSequence, new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 4, CompatibilityData.Start.AddSeconds(2), CompatibilityData.RunId, new LogEmittedPayload("information", "correct")), marker);
        var correlation = AtEligibility();
        AssertRedactedCorrected(correlation, new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 4, CompatibilityData.Start.AddSeconds(2), Guid.Parse("11111111-1111-1111-1111-111111111111"), new LogEmittedPayload("information", marker)), WorkerProtocolFailureCode.InvalidCorrelation, new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 4, CompatibilityData.Start.AddSeconds(2), CompatibilityData.RunId, new LogEmittedPayload("information", "correct")), marker);
        var lifecycle = AtStarted();
        AssertRedactedCorrected(lifecycle, new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 3, CompatibilityData.Start.AddSeconds(1), CompatibilityData.RunId, new LogEmittedPayload("information", marker)), WorkerProtocolFailureCode.InvalidLifecycle, CompatibilityData.Eligibility(), marker);
    }

    private static WorkerProtocolEventStreamValidator AtStarted() { var validator = new WorkerProtocolEventStreamValidator(); Accept(validator, CompatibilityData.Ready()); Accept(validator, CompatibilityData.Started()); return validator; }
    private static WorkerProtocolEventStreamValidator AtEligibility() { var validator = AtStarted(); Accept(validator, CompatibilityData.Eligibility()); return validator; }
    private static void Accept(WorkerProtocolEventStreamValidator validator, WorkerProtocolEvent @event) => Assert.IsTrue(validator.Validate(@event).IsSuccess);
    private static void AssertRejected(WorkerProtocolEventStreamValidator validator, WorkerProtocolEvent rejected, WorkerProtocolFailureCode expectedCode) { var result = validator.Validate(rejected); Assert.IsFalse(result.IsSuccess); Assert.IsNull(result.Event); Assert.IsNotNull(result.Failure); Assert.AreEqual(expectedCode, result.Failure.Code); Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Diagnostic)); Assert.IsTrue(result.Failure.Diagnostic.Length <= 256); }
    private static void AssertRedactedCorrected(WorkerProtocolEventStreamValidator validator, WorkerProtocolEvent rejected, WorkerProtocolFailureCode expectedCode, WorkerProtocolEvent corrected, string marker) { var result = validator.Validate(rejected); Assert.IsFalse(result.IsSuccess); Assert.IsNull(result.Event); Assert.IsNotNull(result.Failure); Assert.AreEqual(expectedCode, result.Failure.Code); Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Diagnostic)); Assert.IsTrue(result.Failure.Diagnostic.Length <= 256); foreach (var fragment in new[] { "SENTINEL", "Password=", "Server=", "SELECT", "JsonException", "Exception", "at Worker" }) { Assert.IsFalse(result.Failure.Diagnostic.Contains(fragment, StringComparison.Ordinal)); } Assert.IsTrue(validator.Validate(corrected).IsSuccess); }
    private static void AssertCorrected(WorkerProtocolEventStreamValidator validator, WorkerProtocolEvent rejected, WorkerProtocolFailureCode expectedCode, WorkerProtocolEvent corrected) { var result = validator.Validate(rejected); Assert.IsFalse(result.IsSuccess); Assert.IsNull(result.Event); Assert.IsNotNull(result.Failure); Assert.AreEqual(expectedCode, result.Failure.Code); Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Diagnostic)); Assert.IsTrue(result.Failure.Diagnostic.Length <= 256); Assert.IsTrue(validator.Validate(corrected).IsSuccess); }
}
