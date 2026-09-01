using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol;

[TestClass]
public class ControllerInputSafeFailureTests
{
    private const string Execute = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\"}}";

    [TestMethod]
    public void ControllerInput_CodecFailuresAreSafeAndBounded()
    {
        var tooLarge = new byte[WorkerProtocolV1.MaxMessageBytes + 1]; "TOO_LARGE_SENTINEL Password=too-large"u8.CopyTo(tooLarge);
        var invalidEncoding = "ENCODING_SENTINEL Password=encoding"u8.ToArray().Concat(new byte[] { 0xff }).ToArray();
        foreach (var row in new (string Label, byte[] Bytes, WorkerProtocolFailureCode Code, string[] Fragments)[]
        {
            ("controller-input:safe:too-large", tooLarge, WorkerProtocolFailureCode.MessageTooLarge, ["TOO_LARGE_SENTINEL", "Password=too-large"]),
            ("controller-input:safe:encoding", invalidEncoding, WorkerProtocolFailureCode.InvalidEncoding, ["ENCODING_SENTINEL", "Password=encoding"]),
            ("controller-input:safe:framing", System.Text.Encoding.UTF8.GetBytes(Carrier(Execute, "FRAMING_SENTINEL Password=framing") + "\r"), WorkerProtocolFailureCode.InvalidFraming, ["FRAMING_SENTINEL", "Password=framing"]),
            ("controller-input:safe:json", "{\"future\":\"JSON_SENTINEL Password=json\","u8.ToArray(), WorkerProtocolFailureCode.MalformedJson, ["JSON_SENTINEL", "Password=json"]),
            ("controller-input:safe:envelope", "{\"future\":\"ENVELOPE_SENTINEL Password=envelope\"}"u8.ToArray(), WorkerProtocolFailureCode.InvalidEnvelope, ["ENVELOPE_SENTINEL", "Password=envelope"]),
            ("controller-input:safe:protocol", System.Text.Encoding.UTF8.GetBytes(Carrier(Execute.Replace("immich-reversegeo.worker", "other", StringComparison.Ordinal), "PROTOCOL_SENTINEL Password=protocol")), WorkerProtocolFailureCode.UnsupportedProtocol, ["PROTOCOL_SENTINEL", "Password=protocol", "other"]),
            ("controller-input:safe:version", System.Text.Encoding.UTF8.GetBytes(Carrier(Execute.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal), "VERSION_SENTINEL Password=version")), WorkerProtocolFailureCode.UnsupportedVersion, ["VERSION_SENTINEL", "Password=version", "2"]),
            ("controller-input:safe:type", System.Text.Encoding.UTF8.GetBytes(Carrier(Execute.Replace("\"type\":\"execute\"", "\"type\":\"ping\"", StringComparison.Ordinal), "TYPE_SENTINEL Password=type")), WorkerProtocolFailureCode.UnsupportedType, ["TYPE_SENTINEL", "Password=type", "ping"]),
            ("controller-input:safe:payload", System.Text.Encoding.UTF8.GetBytes(Carrier(Execute.Replace("manual", "other", StringComparison.Ordinal), "PAYLOAD_SENTINEL Password=payload")), WorkerProtocolFailureCode.InvalidPayload, ["PAYLOAD_SENTINEL", "Password=payload", "other"])
        }) { AssertSafe(row.Label, WorkerProtocolCodec.ParseControllerInput(row.Bytes), row.Code, row.Fragments); }
    }

    [TestMethod]
    public void ControllerInput_ValidatorFailuresAreSafeAtomicAndCorrectable()
    {
        var accepted = Parse(Execute); var sequence = new WorkerProtocolControllerMessage(WorkerProtocolV1.ControlCategory, WorkerProtocolV1.CancelType, 3, new DateTimeOffset(2093, 1, 1, 0, 0, 0, TimeSpan.Zero), accepted.RunId, new CancelControlPayload()); var correlation = new WorkerProtocolControllerMessage(WorkerProtocolV1.ControlCategory, WorkerProtocolV1.CancelType, 2, new DateTimeOffset(2094, 1, 1, 0, 0, 0, TimeSpan.Zero), Guid.Parse("11111111-1111-1111-1111-111111111111"), new CancelControlPayload());
        foreach (var row in new (string Label, WorkerProtocolControllerMessage Candidate, WorkerProtocolFailureCode Code, string[] Fragments)[] { ("controller-input:safe:sequence", sequence, WorkerProtocolFailureCode.InvalidSequence, ["2093", "3"]), ("controller-input:safe:correlation", correlation, WorkerProtocolFailureCode.InvalidCorrelation, ["11111111-1111-1111-1111-111111111111", "2094"]) })
        {
            var validator = new WorkerProtocolControllerInputValidator(); Assert.IsTrue(validator.Validate(accepted, true, WorkerProtocolExecutionPhase.BeforeInvocation).IsSuccess, row.Label + ":pre"); var before = validator.Snapshot; AssertSafe(row.Label, validator.Validate(row.Candidate, true, WorkerProtocolExecutionPhase.BeforeInvocation), row.Code, row.Fragments); Assert.AreEqual(before, validator.Snapshot, row.Label + ":unchanged"); var correction = new WorkerProtocolControllerMessage(WorkerProtocolV1.ControlCategory, WorkerProtocolV1.CancelType, 2, new DateTimeOffset(2095, 1, 1, 0, 0, 0, TimeSpan.Zero), accepted.RunId, new CancelControlPayload()); Assert.IsTrue(validator.Validate(correction, true, WorkerProtocolExecutionPhase.BeforeInvocation).IsSuccess, row.Label + ":correction");
        }
        var id = Guid.Parse("22222222-2222-2222-2222-222222222222"); var lifecycle = new WorkerProtocolControllerMessage(WorkerProtocolV1.RequestCategory, WorkerProtocolV1.ExecuteType, 1, new DateTimeOffset(2096, 1, 1, 0, 0, 0, TimeSpan.Zero), id, new ExecuteRequestPayload(new ProcessingRunRequest(id, ProcessingRunTrigger.Manual))); var fresh = new WorkerProtocolControllerInputValidator(); var snapshot = fresh.Snapshot; AssertSafe("controller-input:safe:lifecycle", fresh.Validate(lifecycle, false, WorkerProtocolExecutionPhase.BeforeInvocation), WorkerProtocolFailureCode.InvalidLifecycle, ["22222222-2222-2222-2222-222222222222", "2096"]); Assert.AreEqual(snapshot, fresh.Snapshot, "controller-input:safe:lifecycle:unchanged"); Assert.IsTrue(fresh.Validate(lifecycle, true, WorkerProtocolExecutionPhase.BeforeInvocation).IsSuccess, "controller-input:safe:lifecycle:correction");
    }

    private static string Carrier(string json, string sentinel) => json.Replace("\"payload\"", "\"future\":\"" + sentinel + " Server=host;Database=db SELECT secret JsonException Exception at Worker StackTrace\",\"payload\"", StringComparison.Ordinal);
    private static WorkerProtocolControllerMessage Parse(string json) { var result = WorkerProtocolCodec.ParseControllerInput(System.Text.Encoding.UTF8.GetBytes(json)); Assert.IsTrue(result.IsSuccess); return result.Message!; }
    private static void AssertSafe(string label, WorkerProtocolControllerParseResult result, WorkerProtocolFailureCode code, string[] fragments)
    { Assert.IsFalse(result.IsSuccess, label); Assert.IsNull(result.Message, label); Assert.IsNull(result.CancelDisposition, label); Assert.IsNotNull(result.Failure, label); Assert.AreEqual(code, result.Failure.Code, label); Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Diagnostic), label); Assert.IsTrue(result.Failure.Diagnostic.Length <= 256, label); foreach (var marker in fragments.Concat(new[] { "Password=", "Server=", "Database=", "SELECT", "JsonException", "Exception", "StackTrace", "\r", "\n" })) { Assert.IsFalse(result.Failure.Diagnostic.Contains(marker, StringComparison.Ordinal), label + ":" + marker); } }
}
