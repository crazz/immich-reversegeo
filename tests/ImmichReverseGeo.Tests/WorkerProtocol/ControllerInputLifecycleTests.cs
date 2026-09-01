using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol;

[TestClass]
public class ControllerInputLifecycleTests
{
    private static readonly Guid RunId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ControllerInput_ReadyThenExecuteAndCancel_PreservesRequestAcrossAllTriggers()
    {
        foreach (var row in new (string Label, ProcessingRunTrigger Trigger)[]
        {
            ("controller-input:lifecycle:manual", ProcessingRunTrigger.Manual),
            ("controller-input:lifecycle:scheduled", ProcessingRunTrigger.Scheduled),
            ("controller-input:lifecycle:run-once", ProcessingRunTrigger.RunOnce)
        })
        {
            var validator = new WorkerProtocolControllerInputValidator();
            var execute = Execute(1, row.Trigger);
            var cancel = Cancel(2);
            AssertSuccess(row.Label + ":execute", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute, null);
            AssertSuccess(row.Label + ":cancel", validator.Validate(cancel, true, WorkerProtocolExecutionPhase.BeforeInvocation), cancel, WorkerProtocolCancelDisposition.LatchedBeforeInvocation);
            Assert.AreEqual(new ProcessingRunRequest(RunId, row.Trigger), validator.Snapshot.Request, row.Label + ":request");
            Assert.IsTrue(validator.Snapshot.CancellationRequested, row.Label + ":cancelled");
        }
    }

    [TestMethod]
    public void ControllerInput_CooperativeRepeatedAndTerminalCancelsHaveDefinedEffects()
    {
        foreach (var row in new (string Label, WorkerProtocolExecutionPhase Phase, WorkerProtocolCancelDisposition Disposition, WorkerProtocolCancelDisposition RepeatedDisposition)[]
        {
            ("controller-input:cancel:during", WorkerProtocolExecutionPhase.Executing, WorkerProtocolCancelDisposition.CooperativeCancellationRequested, WorkerProtocolCancelDisposition.AlreadyCancelledNoOp),
            ("controller-input:cancel:terminal-all-outcomes", WorkerProtocolExecutionPhase.Terminal, WorkerProtocolCancelDisposition.TerminalNoOp, WorkerProtocolCancelDisposition.TerminalNoOp)
        })
        {
            var validator = new WorkerProtocolControllerInputValidator();
            AssertSuccess(row.Label + ":execute", validator.Validate(Execute(1, ProcessingRunTrigger.Manual), true, WorkerProtocolExecutionPhase.BeforeInvocation), Execute(1, ProcessingRunTrigger.Manual), null);
            var first = Cancel(2);
            AssertSuccess(row.Label + ":first", validator.Validate(first, true, row.Phase), first, row.Disposition);
            var repeated = Cancel(3);
            AssertSuccess(row.Label + ":repeat", validator.Validate(repeated, true, row.Phase), repeated, row.RepeatedDisposition);
        }
    }

    [TestMethod]
    public void ControllerInput_RejectionsAreAtomicAndCorrectable()
    {
        var validator = new WorkerProtocolControllerInputValidator();
        var execute = Execute(1, ProcessingRunTrigger.Manual);
        AssertSuccess("controller-input:atomic:execute", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute, null);
        var before = validator.Snapshot;
        var wrong = new WorkerProtocolControllerMessage(WorkerProtocolV1.ControlCategory, WorkerProtocolV1.CancelType, 2, Timestamp.AddSeconds(1), Guid.Parse("11111111-1111-1111-1111-111111111111"), new CancelControlPayload());
        AssertFailure("controller-input:atomic:wrong-run", validator.Validate(wrong, true, WorkerProtocolExecutionPhase.BeforeInvocation), WorkerProtocolFailureCode.InvalidCorrelation);
        Assert.AreEqual(before, validator.Snapshot, "controller-input:atomic:unchanged");
        var correction = Cancel(2);
        AssertSuccess("controller-input:atomic:correction", validator.Validate(correction, true, WorkerProtocolExecutionPhase.BeforeInvocation), correction, WorkerProtocolCancelDisposition.LatchedBeforeInvocation);
    }

    [TestMethod]
    public void ControllerInput_EofIsPureAndDoesNotCancel()
    {
        var empty = new WorkerProtocolControllerInputValidator();
        AssertFinal("controller-input:eof:before", empty.FinalizeInput(false), WorkerProtocolControllerInputFinalization.NoRequest);
        AssertFinalFailure("controller-input:eof:partial", empty.FinalizeInput(true));

        var validator = new WorkerProtocolControllerInputValidator();
        var execute = Execute(1, ProcessingRunTrigger.Manual);
        AssertSuccess("controller-input:eof:execute", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute, null);
        var before = validator.Snapshot;
        AssertFinal("controller-input:eof:after-execute", validator.FinalizeInput(false), WorkerProtocolControllerInputFinalization.ControlsClosed);
        Assert.AreEqual(before with { ControlsClosed = true }, validator.Snapshot, "controller-input:eof:no-cancel");
        AssertFailure("controller-input:eof:closed-input", validator.Validate(Cancel(2), true, WorkerProtocolExecutionPhase.BeforeInvocation), WorkerProtocolFailureCode.InvalidLifecycle);
    }

    [TestMethod]
    public void ControllerInput_FirstMessageAndSequenceRejectionsAreAtomic()
    {
        foreach (var row in new (string Label, WorkerProtocolControllerMessage Message)[]
        {
            ("controller-input:first:cancel", Cancel(1)),
            ("controller-input:first:execute-sequence-two", Execute(2, ProcessingRunTrigger.Manual))
        })
        {
            var validator = new WorkerProtocolControllerInputValidator();
            var before = validator.Snapshot;
            AssertFailure(row.Label, validator.Validate(row.Message, true, WorkerProtocolExecutionPhase.BeforeInvocation), WorkerProtocolFailureCode.InvalidLifecycle);
            Assert.AreEqual(before, validator.Snapshot, row.Label + ":unchanged");
            var correction = Execute(1, ProcessingRunTrigger.Manual);
            AssertSuccess(row.Label + ":correction", validator.Validate(correction, true, WorkerProtocolExecutionPhase.BeforeInvocation), correction, null);
        }
    }

    [TestMethod]
    public void ControllerInput_SecondExecuteAndReplayAreRejectedWithoutReplacingRequest()
    {
        foreach (var row in new (string Label, WorkerProtocolExecutionPhase Phase, WorkerProtocolCancelDisposition CorrectionDisposition)[]
        {
            ("controller-input:second-execute:before", WorkerProtocolExecutionPhase.BeforeInvocation, WorkerProtocolCancelDisposition.LatchedBeforeInvocation),
            ("controller-input:second-execute:executing", WorkerProtocolExecutionPhase.Executing, WorkerProtocolCancelDisposition.CooperativeCancellationRequested),
            ("controller-input:second-execute:terminal-all-outcomes", WorkerProtocolExecutionPhase.Terminal, WorkerProtocolCancelDisposition.TerminalNoOp)
        })
        {
            var validator = new WorkerProtocolControllerInputValidator();
            var execute = Execute(1, ProcessingRunTrigger.Manual);
            AssertSuccess(row.Label + ":pre", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute, null);
            var before = validator.Snapshot;
            AssertFailure(row.Label, validator.Validate(Execute(2, ProcessingRunTrigger.RunOnce), true, row.Phase), WorkerProtocolFailureCode.InvalidLifecycle);
            Assert.AreEqual(before, validator.Snapshot, row.Label + ":unchanged");
            var cancel = Cancel(2);
            AssertSuccess(row.Label + ":correction", validator.Validate(cancel, true, row.Phase), cancel, row.CorrectionDisposition);
            AssertFailure(row.Label + ":replay", validator.Validate(cancel, true, row.Phase), WorkerProtocolFailureCode.InvalidSequence);
        }
    }

    [TestMethod]
    public void ControllerInput_EofAfterCancelAndTerminalOutcomesIsEffectFree()
    {
        foreach (var row in new (string Label, WorkerProtocolExecutionPhase Phase, WorkerProtocolCancelDisposition Disposition)[]
        {
            ("controller-input:eof:after-cancellation-requested", WorkerProtocolExecutionPhase.BeforeInvocation, WorkerProtocolCancelDisposition.LatchedBeforeInvocation),
            ("controller-input:eof:terminal-all-outcomes", WorkerProtocolExecutionPhase.Terminal, WorkerProtocolCancelDisposition.TerminalNoOp) // Terminal is the shared public representation for completed, cancelled, and failed outcomes.
        })
        {
            var validator = new WorkerProtocolControllerInputValidator();
            var execute = Execute(1, ProcessingRunTrigger.Manual);
            AssertSuccess(row.Label + ":execute", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute, null);
            var cancel = Cancel(2);
            AssertSuccess(row.Label + ":cancel", validator.Validate(cancel, true, row.Phase), cancel, row.Disposition);
            var before = validator.Snapshot;
            AssertFinal(row.Label + ":finalize", validator.FinalizeInput(false), WorkerProtocolControllerInputFinalization.ControlsClosed);
            Assert.AreEqual(before with { ControlsClosed = true }, validator.Snapshot, row.Label + ":unchanged");
            AssertFailure(row.Label + ":later-control", validator.Validate(Cancel(3), true, row.Phase), WorkerProtocolFailureCode.InvalidLifecycle);
        }
    }

    private static WorkerProtocolControllerMessage Execute(long sequence, ProcessingRunTrigger trigger) => new(WorkerProtocolV1.RequestCategory, WorkerProtocolV1.ExecuteType, sequence, Timestamp, RunId, new ExecuteRequestPayload(new ProcessingRunRequest(RunId, trigger)));
    private static WorkerProtocolControllerMessage Cancel(long sequence) => new(WorkerProtocolV1.ControlCategory, WorkerProtocolV1.CancelType, sequence, Timestamp.AddSeconds(sequence), RunId, new CancelControlPayload());

    private static void AssertSuccess(string label, WorkerProtocolControllerParseResult result, WorkerProtocolControllerMessage expected, WorkerProtocolCancelDisposition? disposition)
    {
        Assert.IsTrue(result.IsSuccess, label);
        Assert.AreEqual(expected, result.Message, label);
        Assert.AreEqual(disposition, result.CancelDisposition, label);
        Assert.IsNull(result.Failure, label);
    }

    private static void AssertFailure(string label, WorkerProtocolControllerParseResult result, WorkerProtocolFailureCode code)
    {
        Assert.IsFalse(result.IsSuccess, label);
        Assert.IsNull(result.Message, label);
        Assert.IsNull(result.CancelDisposition, label);
        Assert.IsNotNull(result.Failure, label);
        Assert.AreEqual(code, result.Failure.Code, label);
    }

    private static void AssertFinal(string label, WorkerProtocolControllerInputFinalizationResult result, WorkerProtocolControllerInputFinalization state)
    {
        Assert.IsTrue(result.IsSuccess, label);
        Assert.AreEqual(state, result.State, label);
        Assert.IsNull(result.Failure, label);
    }

    private static void AssertFinalFailure(string label, WorkerProtocolControllerInputFinalizationResult result)
    {
        Assert.IsFalse(result.IsSuccess, label);
        Assert.IsNull(result.State, label);
        Assert.IsNotNull(result.Failure, label);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidFraming, result.Failure.Code, label);
    }
}
