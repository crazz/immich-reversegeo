using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol;

[TestClass]
public class ControllerInputSliceTests
{
    private static readonly Guid RunId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    private static readonly DateTimeOffset TimestampUtc = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void C17_ExecuteManual_CanonicalGoldenParsesSerializesAndReparses()
    {
        var expected = new WorkerProtocolControllerMessage(
            WorkerProtocolV1.RequestCategory,
            WorkerProtocolV1.ExecuteType,
            1,
            TimestampUtc,
            RunId,
            new ExecuteRequestPayload(new ProcessingRunRequest(RunId, ProcessingRunTrigger.Manual)));
        var golden = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\"}}"u8.ToArray();

        AssertRoundTrip("C17-EXECUTE-MANUAL-GOLDEN", golden, expected);
    }

    [TestMethod]
    public void C17_ExecuteScheduled_CanonicalGoldenParsesSerializesAndReparses()
    {
        var expected = new WorkerProtocolControllerMessage(WorkerProtocolV1.RequestCategory, WorkerProtocolV1.ExecuteType, 1, TimestampUtc, RunId, new ExecuteRequestPayload(new ProcessingRunRequest(RunId, ProcessingRunTrigger.Scheduled)));
        var golden = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"scheduled\"}}"u8.ToArray();

        AssertRoundTrip("C17-EXECUTE-SCHEDULED-GOLDEN", golden, expected);
    }

    [TestMethod]
    public void C17_ExecuteRunOnce_CanonicalGoldenParsesSerializesAndReparses()
    {
        var expected = new WorkerProtocolControllerMessage(WorkerProtocolV1.RequestCategory, WorkerProtocolV1.ExecuteType, 1, TimestampUtc, RunId, new ExecuteRequestPayload(new ProcessingRunRequest(RunId, ProcessingRunTrigger.RunOnce)));
        var golden = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"run-once\"}}"u8.ToArray();

        AssertRoundTrip("C17-EXECUTE-RUN-ONCE-GOLDEN", golden, expected);
    }

    [TestMethod]
    public void C17_Cancel_CanonicalGoldenParsesSerializesAndReparses()
    {
        var expected = new WorkerProtocolControllerMessage(
            WorkerProtocolV1.ControlCategory,
            WorkerProtocolV1.CancelType,
            2,
            TimestampUtc.AddSeconds(1),
            RunId,
            new CancelControlPayload());
        var golden = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"control\",\"type\":\"cancel\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{}}"u8.ToArray();

        AssertRoundTrip("C17-CANCEL-GOLDEN", golden, expected);
    }

    [TestMethod]
    public void C17_ReadyExecuteThenCorrelatedCancel_AcceptsIndependentInputSequence()
    {
        var validator = new WorkerProtocolControllerInputValidator();
        var execute = Parse("C17-READY-EXECUTE", ExecuteBytes(1));
        var cancel = Parse("C17-CORRELATED-CANCEL", CancelBytes(2, RunId));

        AssertAccepted("C17-READY-EXECUTE", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute);
        AssertAccepted("C17-CORRELATED-CANCEL", validator.Validate(cancel, true, WorkerProtocolExecutionPhase.BeforeInvocation), cancel, WorkerProtocolCancelDisposition.LatchedBeforeInvocation);
    }

    [TestMethod]
    public void C17_ExecuteBeforeReady_IsRejectedWithoutCreatingRequest()
    {
        var validator = new WorkerProtocolControllerInputValidator();
        var execute = Parse("C17-NOT-READY", ExecuteBytes(1));

        AssertRejected("C17-NOT-READY", validator.Validate(execute, false, WorkerProtocolExecutionPhase.BeforeInvocation), WorkerProtocolFailureCode.InvalidLifecycle);
        AssertAccepted("C17-READY-CORRECTION", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute);
    }

    [TestMethod]
    public void C17_WrongRunCancelAtSequenceTwo_IsRejectedAndCorrectedSequenceRemainsEligible()
    {
        var validator = new WorkerProtocolControllerInputValidator();
        var execute = Parse("C17-ATOMIC-PRE", ExecuteBytes(1));
        var wrongCancel = Parse("C17-ATOMIC-DEFECT", CancelBytes(2, Guid.Parse("11111111-1111-1111-1111-111111111111")));
        var correctedCancel = Parse("C17-ATOMIC-CORRECTION", CancelBytes(2, RunId));

        AssertAccepted("C17-ATOMIC-PRE", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute);
        AssertRejected("C17-ATOMIC-DEFECT", validator.Validate(wrongCancel, true, WorkerProtocolExecutionPhase.BeforeInvocation), WorkerProtocolFailureCode.InvalidCorrelation);
        AssertAccepted("C17-ATOMIC-CORRECTION", validator.Validate(correctedCancel, true, WorkerProtocolExecutionPhase.BeforeInvocation), correctedCancel, WorkerProtocolCancelDisposition.LatchedBeforeInvocation);
    }

    [TestMethod]
    public void C17_TimestampRegression_IsRejectedAndCorrectedSameSequenceRemainsEligible()
    {
        var validator = new WorkerProtocolControllerInputValidator();
        var execute = Parse("controller-input:timestampUtc:pre", ExecuteBytes(1));
        var regression = new WorkerProtocolControllerMessage(WorkerProtocolV1.ControlCategory, WorkerProtocolV1.CancelType, 2, TimestampUtc.AddTicks(-1), RunId, new CancelControlPayload());
        var correction = Parse("controller-input:timestampUtc:correction", CancelBytes(2, RunId));

        AssertAccepted("controller-input:timestampUtc:pre", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute);
        AssertRejected("controller-input:timestampUtc:regression", validator.Validate(regression, true, WorkerProtocolExecutionPhase.BeforeInvocation), WorkerProtocolFailureCode.InvalidLifecycle);
        AssertAccepted("controller-input:timestampUtc:correction", validator.Validate(correction, true, WorkerProtocolExecutionPhase.BeforeInvocation), correction, WorkerProtocolCancelDisposition.LatchedBeforeInvocation);
    }

    [TestMethod]
    public void C17_CancelDispositions_ArePureAndDoNotMutateRequest()
    {
        var validator = new WorkerProtocolControllerInputValidator();
        var execute = Parse("controller-input:phase:execute", ExecuteBytes(1));
        var firstCancel = Parse("controller-input:phase:latch", CancelBytes(2, RunId));
        var repeatedCancel = Parse("controller-input:phase:repeat", CancelBytes(3, RunId));
        var terminalCancel = Parse("controller-input:phase:terminal", CancelBytes(4, RunId));

        AssertAccepted("controller-input:phase:execute", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute, null);
        AssertAccepted("controller-input:phase:latch", validator.Validate(firstCancel, true, WorkerProtocolExecutionPhase.BeforeInvocation), firstCancel, WorkerProtocolCancelDisposition.LatchedBeforeInvocation);
        AssertAccepted("controller-input:phase:repeat", validator.Validate(repeatedCancel, true, WorkerProtocolExecutionPhase.Executing), repeatedCancel, WorkerProtocolCancelDisposition.AlreadyCancelledNoOp);
        AssertAccepted("controller-input:phase:terminal", validator.Validate(terminalCancel, true, WorkerProtocolExecutionPhase.Terminal), terminalCancel, WorkerProtocolCancelDisposition.TerminalNoOp);
        Assert.AreEqual(new ProcessingRunRequest(RunId, ProcessingRunTrigger.Manual), ((ExecuteRequestPayload)execute.Payload).Request, "controller-input:phase:request");
    }

    [TestMethod]
    public void C17_UndefinedPhase_RejectionIsAtomicForExecuteAndCancel()
    {
        var undefined = (WorkerProtocolExecutionPhase)99;
        var execute = Parse("controller-input:phase:execute-candidate", ExecuteBytes(1));
        var cancel = Parse("controller-input:phase:cancel-candidate", CancelBytes(2, RunId));
        var validator = new WorkerProtocolControllerInputValidator();

        AssertRejected("controller-input:phase:execute-undefined", validator.Validate(execute, true, undefined), WorkerProtocolFailureCode.InvalidLifecycle);
        AssertAccepted("controller-input:phase:execute-correction", validator.Validate(execute, true, WorkerProtocolExecutionPhase.BeforeInvocation), execute, null);
        AssertRejected("controller-input:phase:cancel-undefined", validator.Validate(cancel, true, undefined), WorkerProtocolFailureCode.InvalidLifecycle);
        AssertAccepted("controller-input:phase:cancel-correction", validator.Validate(cancel, true, WorkerProtocolExecutionPhase.BeforeInvocation), cancel, WorkerProtocolCancelDisposition.LatchedBeforeInvocation);
    }

    [TestMethod]
    public void C17_OutOfInt64Sequence_IsRejectedByCodec()
    {
        var bytes = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":9223372036854775808,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\"}}"u8.ToArray();
        var result = WorkerProtocolCodec.ParseControllerInput(bytes);

        AssertRejected("C17-OUT-OF-INT64", result, WorkerProtocolFailureCode.InvalidEnvelope);
    }

    private static void AssertRoundTrip(string label, byte[] golden, WorkerProtocolControllerMessage expected)
    {
        var parsed = Parse(label + "-PARSE", golden);
        Assert.AreEqual(expected, parsed, label);
        CollectionAssert.AreEqual(golden, WorkerProtocolCodec.SerializeControllerInput(expected), label + "-SERIALIZE");
        var reparsed = Parse(label + "-REPARSE", WorkerProtocolCodec.SerializeControllerInput(expected));
        Assert.AreEqual(expected, reparsed, label);
    }

    private static WorkerProtocolControllerMessage Parse(string label, byte[] bytes)
    {
        var result = WorkerProtocolCodec.ParseControllerInput(bytes);
        Assert.IsTrue(result.IsSuccess, label + ": " + result.Failure?.Diagnostic);
        Assert.IsNull(result.Failure, label);
        Assert.IsNull(result.CancelDisposition, label);
        Assert.IsNotNull(result.Message, label);
        return result.Message;
    }

    private static byte[] ExecuteBytes(long sequence) => System.Text.Encoding.UTF8.GetBytes($"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":{sequence},\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{{\"trigger\":\"manual\"}}}}");
    private static byte[] CancelBytes(long sequence, Guid runId) => System.Text.Encoding.UTF8.GetBytes($"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"control\",\"type\":\"cancel\",\"sequence\":{sequence},\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"{runId:D}\",\"payload\":{{}}}}");

    private static void AssertAccepted(string label, WorkerProtocolControllerParseResult result, WorkerProtocolControllerMessage expected, WorkerProtocolCancelDisposition? expectedDisposition = null)
    {
        Assert.IsTrue(result.IsSuccess, label + ": " + result.Failure?.Diagnostic);
        Assert.AreEqual(expected, result.Message, label);
        Assert.IsNull(result.Failure, label);
        Assert.AreEqual(expectedDisposition, result.CancelDisposition, label);
    }

    private static void AssertRejected(string label, WorkerProtocolControllerParseResult result, WorkerProtocolFailureCode expectedCode)
    {
        Assert.IsFalse(result.IsSuccess, label);
        Assert.IsNull(result.Message, label);
        Assert.IsNotNull(result.Failure, label);
        Assert.IsNull(result.CancelDisposition, label);
        Assert.AreEqual(expectedCode, result.Failure.Code, label);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Diagnostic), label);
        Assert.IsTrue(result.Failure.Diagnostic.Length <= 256, label);
    }
}
