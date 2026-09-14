using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol;

[TestClass]
public class ControllerInputLifecycleCompletionTests
{
    private static readonly Guid RunId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    private static readonly DateTimeOffset Time = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ControllerInput_SequenceAndSecondExecuteRejectionsAreAtomic()
    {
        foreach (var row in new (string Label, long Sequence, WorkerProtocolExecutionPhase Phase, WorkerProtocolFailureCode Code, WorkerProtocolCancelDisposition CorrectionDisposition)[]
        {
            ("controller-input:sequence:gap", 3, WorkerProtocolExecutionPhase.BeforeInvocation, WorkerProtocolFailureCode.InvalidSequence, WorkerProtocolCancelDisposition.LatchedBeforeInvocation),
            ("controller-input:sequence:duplicate", 1, WorkerProtocolExecutionPhase.BeforeInvocation, WorkerProtocolFailureCode.InvalidSequence, WorkerProtocolCancelDisposition.LatchedBeforeInvocation),
            ("controller-input:sequence:regression", 1, WorkerProtocolExecutionPhase.BeforeInvocation, WorkerProtocolFailureCode.InvalidSequence, WorkerProtocolCancelDisposition.LatchedBeforeInvocation),
            ("controller-input:second-execute:before", 2, WorkerProtocolExecutionPhase.BeforeInvocation, WorkerProtocolFailureCode.InvalidLifecycle, WorkerProtocolCancelDisposition.LatchedBeforeInvocation),
            ("controller-input:second-execute:executing", 2, WorkerProtocolExecutionPhase.Executing, WorkerProtocolFailureCode.InvalidLifecycle, WorkerProtocolCancelDisposition.CooperativeCancellationRequested),
            ("controller-input:second-execute:cancellation-requested", 2, WorkerProtocolExecutionPhase.BeforeInvocation, WorkerProtocolFailureCode.InvalidLifecycle, WorkerProtocolCancelDisposition.AlreadyCancelledNoOp),
            ("controller-input:second-execute:terminal-all-outcomes", 2, WorkerProtocolExecutionPhase.Terminal, WorkerProtocolFailureCode.InvalidLifecycle, WorkerProtocolCancelDisposition.TerminalNoOp) // Terminal is the shared public representation for completed, cancelled, and failed outcomes.
        })
        {
            var validator = new WorkerProtocolControllerInputValidator(); var execute = Execute(1); Accept(row.Label + ":pre", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute, null);
            if (row.Label.Contains("cancellation-requested", StringComparison.Ordinal)) { var latch = Cancel(2); Accept(row.Label + ":latch", validator.Validate(latch, true, WorkerProtocolExecutionPhase.BeforeInvocation), latch, WorkerProtocolCancelDisposition.LatchedBeforeInvocation); }
            var before = validator.Snapshot; var candidate = row.Label.Contains("second-execute", StringComparison.Ordinal) ? Execute(row.Label.Contains("cancellation-requested", StringComparison.Ordinal) ? 3 : row.Sequence) : Cancel(row.Sequence);
            Reject(row.Label, validator.Validate(candidate, true, row.Phase), row.Code); Assert.AreEqual(before, validator.Snapshot, row.Label + ":unchanged");
            var correction = Cancel(before.LastSequence + 1); Accept(row.Label + ":correction", validator.Validate(correction, true, row.Phase), correction, row.CorrectionDisposition);
        }
    }

    [TestMethod]
    public void ControllerInput_ReadyParserCorrelationAndStdoutIndependenceAreExplicit()
    {
        var validator = new WorkerProtocolControllerInputValidator(); var execute = Execute(1); Reject("controller-input:ready:before", validator.Validate(execute, false, WorkerProtocolExecutionPhase.BeforeInvocation), WorkerProtocolFailureCode.InvalidLifecycle); Accept("controller-input:ready:correction", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute, null);
        var stdout = new WorkerProtocolEventStreamValidator(); Assert.IsTrue(stdout.Validate(WorkerProtocolMapper.Ready(1, Time)).IsSuccess, "controller-input:stdout:ready"); Assert.IsTrue(stdout.Validate(new WorkerProtocolEvent(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.RunStartedType, 2, Time, RunId, new RunStartedPayload("manual", Time))).IsSuccess, "controller-input:stdout:run-started"); Assert.IsTrue(stdout.Validate(new WorkerProtocolEvent(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.EligibilityDeterminedType, 3, Time.AddSeconds(1), RunId, new EligibilityDeterminedPayload(0))).IsSuccess, "controller-input:stdout:eligibility");
        var before = validator.Snapshot; var bad = WorkerProtocolCodec.ParseControllerInput("{bad"u8); RejectParse("controller-input:after-execute:malformed", bad, WorkerProtocolFailureCode.MalformedJson); Assert.AreEqual(before, validator.Snapshot, "controller-input:after-execute:malformed:unchanged");
        var unsupported = WorkerProtocolCodec.ParseControllerInput(System.Text.Encoding.UTF8.GetBytes("{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"control\",\"type\":\"ping\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:02.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{}}")); RejectParse("controller-input:after-execute:unsupported", unsupported, WorkerProtocolFailureCode.UnsupportedType); Assert.AreEqual(before, validator.Snapshot, "controller-input:after-execute:unsupported:unchanged");
        var correction = Cancel(2); Accept("controller-input:stdout-independent:cancel", validator.Validate(correction, true, WorkerProtocolExecutionPhase.BeforeInvocation), correction, WorkerProtocolCancelDisposition.LatchedBeforeInvocation);
    }

    [TestMethod]
    public void ControllerInput_GenuineSequenceRegressionIsAtomicAndCorrectable()
    {
        var validator = new WorkerProtocolControllerInputValidator(); var execute = Execute(1); Accept("controller-input:sequence:regression:execute", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute, null); var second = Cancel(2); Accept("controller-input:sequence:regression:second", validator.Validate(second, true, WorkerProtocolExecutionPhase.BeforeInvocation), second, WorkerProtocolCancelDisposition.LatchedBeforeInvocation); var before = validator.Snapshot;
        var lower = new WorkerProtocolControllerMessage(WorkerProtocolV1.ControlCategory, WorkerProtocolV1.CancelType, 1, Time.AddSeconds(3), RunId, new CancelControlPayload()); Reject("controller-input:sequence:regression:lower", validator.Validate(lower, true, WorkerProtocolExecutionPhase.BeforeInvocation), WorkerProtocolFailureCode.InvalidSequence); Assert.AreEqual(before, validator.Snapshot, "controller-input:sequence:regression:unchanged");
        var correction = Cancel(3); Accept("controller-input:sequence:regression:correction", validator.Validate(correction, true, WorkerProtocolExecutionPhase.BeforeInvocation), correction, WorkerProtocolCancelDisposition.AlreadyCancelledNoOp);
    }

    [TestMethod]
    public void ControllerInput_CorrelationCodecFailuresAndRecoveryAreAtomic()
    {
        foreach (var row in new (string Label, string RunId, WorkerProtocolFailureCode Code)[]
        {
            ("controller-input:cancel-runid:empty", "00000000-0000-0000-0000-000000000000", WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:cancel-runid:upper", "01234567-89AB-cdef-0123-456789abcdef", WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:cancel-runid:braces", "{01234567-89ab-cdef-0123-456789abcdef}", WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:cancel-runid:compact", "0123456789abcdef0123456789abcdef", WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:cancel-runid:malformed", "not-a-guid", WorkerProtocolFailureCode.InvalidEnvelope)
        })
        {
            var validator = new WorkerProtocolControllerInputValidator(); var execute = Execute(1); Accept(row.Label + ":pre", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute, null); var before = validator.Snapshot;
            var parsed = WorkerProtocolCodec.ParseControllerInput(System.Text.Encoding.UTF8.GetBytes("{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"control\",\"type\":\"cancel\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:02.0000000Z\",\"runId\":\"" + row.RunId + "\",\"payload\":{}}"));
            RejectParse(row.Label, parsed, row.Code); Assert.AreEqual(before, validator.Snapshot, row.Label + ":unchanged"); var correction = Cancel(2); Accept(row.Label + ":correction", validator.Validate(correction, true, WorkerProtocolExecutionPhase.BeforeInvocation), correction, WorkerProtocolCancelDisposition.LatchedBeforeInvocation);
        }
    }

    [TestMethod]
    public void ControllerInput_PartialEofIsAtomicAndDoesNotConsumeSequence()
    {
        var validator = new WorkerProtocolControllerInputValidator(); var before = validator.Snapshot; var partial = validator.FinalizeInput(true);
        Assert.IsFalse(partial.IsSuccess, "controller-input:eof:partial"); Assert.IsNull(partial.State, "controller-input:eof:partial"); Assert.IsNotNull(partial.Failure, "controller-input:eof:partial"); Assert.AreEqual(WorkerProtocolFailureCode.InvalidFraming, partial.Failure.Code, "controller-input:eof:partial"); Assert.AreEqual(before, validator.Snapshot, "controller-input:eof:partial:unchanged");
        var execute = Execute(1); Accept("controller-input:eof:partial:execute", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute, null); var accepted = validator.Snapshot; var afterExecute = validator.FinalizeInput(true);
        Assert.IsFalse(afterExecute.IsSuccess, "controller-input:eof:partial:after-execute"); Assert.IsNull(afterExecute.State, "controller-input:eof:partial:after-execute"); Assert.IsNotNull(afterExecute.Failure, "controller-input:eof:partial:after-execute"); Assert.AreEqual(WorkerProtocolFailureCode.InvalidFraming, afterExecute.Failure.Code, "controller-input:eof:partial:after-execute"); Assert.IsFalse(string.IsNullOrWhiteSpace(afterExecute.Failure.Diagnostic), "controller-input:eof:partial:after-execute:diagnostic"); Assert.IsTrue(afterExecute.Failure.Diagnostic.Length <= 256, "controller-input:eof:partial:after-execute:bounded");
        foreach (var marker in new[] { "{bad", "SENTINEL", "Password=", "JsonException", "Exception", "StackTrace", "\r", "\n" })
        {
            Assert.IsFalse(afterExecute.Failure.Diagnostic.Contains(marker, StringComparison.Ordinal), "controller-input:eof:partial:after-execute:diagnostic:" + marker);
        }
        Assert.AreEqual(accepted, validator.Snapshot, "controller-input:eof:partial:after-execute:unchanged"); var cancel = Cancel(2); Accept("controller-input:eof:partial:after-execute:cancel", validator.Validate(cancel, true, WorkerProtocolExecutionPhase.BeforeInvocation), cancel, WorkerProtocolCancelDisposition.LatchedBeforeInvocation);
    }

    [TestMethod]
    public void ControllerInput_AcknowledgementsAreAbsentAndRunStartedRemainsWorkerEvent()
    {
        var workerTypes = new[] { WorkerProtocolV1.ReadyType, WorkerProtocolV1.RunStartedType, WorkerProtocolV1.EligibilityDeterminedType, WorkerProtocolV1.ProgressChangedType, WorkerProtocolV1.ActivityStartedType, WorkerProtocolV1.ActivityEndedType, WorkerProtocolV1.LogEmittedType, WorkerProtocolV1.CompletedType, WorkerProtocolV1.CancelledType, WorkerProtocolV1.FailedType };
        Assert.IsFalse(workerTypes.Contains("execute-accepted", StringComparer.Ordinal), "controller-input:ack:execute-absent"); Assert.IsFalse(workerTypes.Contains("cancel-accepted", StringComparer.Ordinal), "controller-input:ack:cancel-absent"); Assert.IsFalse(workerTypes.Contains("ack", StringComparer.Ordinal), "controller-input:ack:generic-absent");
        var started = new WorkerProtocolEvent(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.RunStartedType, 2, Time, RunId, new RunStartedPayload("manual", Time)); Assert.AreEqual(WorkerProtocolV1.RunStartedType, started.Type, "controller-input:ack:run-started-worker-event");
        foreach (var type in new[] { "execute-accepted", "cancel-accepted", "ack" })
        {
            var failure = WorkerProtocolCodec.ParseControllerInput(System.Text.Encoding.UTF8.GetBytes("{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"control\",\"type\":\"" + type + "\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{}}")); RejectParse("controller-input:ack:" + type, failure, WorkerProtocolFailureCode.UnsupportedType);
        }
    }

    private static WorkerProtocolControllerMessage Execute(long sequence) => new(WorkerProtocolV1.RequestCategory, WorkerProtocolV1.ExecuteType, sequence, Time, RunId, new ExecuteRequestPayload(new ProcessingRunRequest(RunId, ProcessingRunTrigger.Manual)));
    private static WorkerProtocolControllerMessage Cancel(long sequence) => new(WorkerProtocolV1.ControlCategory, WorkerProtocolV1.CancelType, sequence, Time.AddSeconds(sequence), RunId, new CancelControlPayload());
    private static void Accept(string label, WorkerProtocolControllerParseResult r, WorkerProtocolControllerMessage expected, WorkerProtocolCancelDisposition? d) { Assert.IsTrue(r.IsSuccess, label); Assert.AreEqual(expected, r.Message, label); Assert.AreEqual(d, r.CancelDisposition, label); Assert.IsNull(r.Failure, label); }
    private static void Reject(string label, WorkerProtocolControllerParseResult r, WorkerProtocolFailureCode code) { Assert.IsFalse(r.IsSuccess, label); Assert.IsNull(r.Message, label); Assert.IsNull(r.CancelDisposition, label); Assert.IsNotNull(r.Failure, label); Assert.AreEqual(code, r.Failure.Code, label); Assert.IsTrue(r.Failure.Diagnostic.Length <= 256, label); }
    private static void RejectParse(string label, WorkerProtocolControllerParseResult r, WorkerProtocolFailureCode code) => Reject(label, r, code);
}
