using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol;

[TestClass]
public class ControllerInputPrimitiveMatrixTests
{
    private const string Execute = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\"}}";
    private const string Cancel = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"control\",\"type\":\"cancel\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{}}";

    [TestMethod]
    public void ControllerInput_ExecuteAndCancelPayloadMutationsAreClassified()
    {
        foreach (var row in new (string Label, string Json, WorkerProtocolFailureCode Code)[]
        {
            ("controller-input:trigger:missing", Execute.Replace("\"trigger\":\"manual\"", "", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("controller-input:trigger:null", Execute.Replace("\"trigger\":\"manual\"", "\"trigger\":null", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("controller-input:trigger:blank", Execute.Replace("manual", "", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("controller-input:trigger:kind", Execute.Replace("\"manual\"", "1", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("controller-input:trigger:case", Execute.Replace("manual", "Manual", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("controller-input:trigger:whitespace", Execute.Replace("manual", " manual", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("controller-input:trigger:unknown", Execute.Replace("manual", "other", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("controller-input:cancel:missing", Cancel.Replace(",\"payload\":{}", "", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:cancel:null", Cancel.Replace("\"payload\":{}", "\"payload\":null", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:cancel:kind", Cancel.Replace("\"payload\":{}", "\"payload\":[]", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope)
        }) { AssertFailure(row.Label, row.Json, row.Code); }
    }

    [TestMethod]
    public void ControllerInput_GuidAndTimestampNoncanonicalFormsFailForBothKinds()
    {
        foreach (var row in new (string Label, string Json, WorkerProtocolFailureCode Code)[]
        {
            ("controller-input:execute:guid-empty", Execute.Replace("01234567-89ab-cdef-0123-456789abcdef", "00000000-0000-0000-0000-000000000000", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:cancel:guid-upper", Cancel.Replace("89ab", "89AB", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:execute:guid-braces", Execute.Replace("01234567-89ab-cdef-0123-456789abcdef", "{01234567-89ab-cdef-0123-456789abcdef}", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:cancel:guid-compact", Cancel.Replace("01234567-89ab-cdef-0123-456789abcdef", "0123456789abcdef0123456789abcdef", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:execute:guid-null", Execute.Replace("\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\"", "\"runId\":null", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:cancel:guid-kind", Cancel.Replace("\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\"", "\"runId\":1", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:timestamp:lower-z", Execute.Replace("Z\"", "z\"", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:timestamp:offset", Execute.Replace("Z\"", "+00:00\"", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:timestamp:fraction", Execute.Replace(".0000000Z", ".00000000Z", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:timestamp:impossible", Execute.Replace("2026-08-29", "2026-02-30", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:timestamp:missing", Execute.Replace("\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",", "", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:timestamp:null", Execute.Replace("\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"timestampUtc\":null", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:timestamp:kind", Execute.Replace("\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"timestampUtc\":1", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:execute:guid-malformed", Execute.Replace("01234567-89ab-cdef-0123-456789abcdef", "not-a-guid", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:cancel:guid-empty", Cancel.Replace("01234567-89ab-cdef-0123-456789abcdef", "00000000-0000-0000-0000-000000000000", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:cancel:guid-braces", Cancel.Replace("01234567-89ab-cdef-0123-456789abcdef", "{01234567-89ab-cdef-0123-456789abcdef}", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:execute:guid-kind", Execute.Replace("\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\"", "\"runId\":[]", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:cancel:guid-null", Cancel.Replace("\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\"", "\"runId\":null", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope)
        }) { AssertFailure(row.Label, row.Json, row.Code); }
    }

    [TestMethod]
    public void ControllerInput_SequenceLexemesAndManualBoundaryBytesAreExact()
    {
        foreach (var row in new (string Label, string Json, bool Success, WorkerProtocolFailureCode Code)[]
        {
            ("controller-input:sequence:one", Execute, true, default),
            ("controller-input:sequence:ordinary", Execute.Replace("\"sequence\":1", "\"sequence\":42", StringComparison.Ordinal), true, default),
            ("controller-input:sequence:max", Execute.Replace("\"sequence\":1", "\"sequence\":9223372036854775807", StringComparison.Ordinal), true, default),
            ("controller-input:sequence:zero", Execute.Replace("\"sequence\":1", "\"sequence\":0", StringComparison.Ordinal), false, WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:sequence:negative", Execute.Replace("\"sequence\":1", "\"sequence\":-1", StringComparison.Ordinal), false, WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:sequence:overflow", Execute.Replace("\"sequence\":1", "\"sequence\":9223372036854775808", StringComparison.Ordinal), false, WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:sequence:quoted", Execute.Replace("\"sequence\":1", "\"sequence\":\"1\"", StringComparison.Ordinal), false, WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:sequence:fraction", Execute.Replace("\"sequence\":1", "\"sequence\":1.0", StringComparison.Ordinal), false, WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:sequence:exponent", Execute.Replace("\"sequence\":1", "\"sequence\":1e0", StringComparison.Ordinal), false, WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:sequence:leading-zero", Execute.Replace("\"sequence\":1", "\"sequence\":01", StringComparison.Ordinal), false, WorkerProtocolFailureCode.MalformedJson)
        })
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(row.Json); var result = WorkerProtocolCodec.ParseControllerInput(bytes);
            if (row.Success) { Assert.IsTrue(result.IsSuccess, row.Label); Assert.IsNotNull(result.Message, row.Label); Assert.IsNull(result.Failure, row.Label); Assert.IsNull(result.CancelDisposition, row.Label); CollectionAssert.AreEqual(bytes, WorkerProtocolCodec.SerializeControllerInput(result.Message), row.Label + ":bytes"); }
            else { AssertFailure(row.Label, row.Json, row.Code); }
        }
    }

    [TestMethod]
    public void ControllerInput_TimestampBoundaryBytesAreManualAndExact()
    {
        foreach (var row in new (string Label, string Json)[]
        {
            ("controller-input:timestamp:min", "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"0001-01-01T00:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\"}}"),
            ("controller-input:timestamp:max", "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"9999-12-31T23:59:59.9999999Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\"}}")
        })
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(row.Json); var result = WorkerProtocolCodec.ParseControllerInput(bytes);
            Assert.IsTrue(result.IsSuccess, row.Label); Assert.IsNotNull(result.Message, row.Label); Assert.IsNull(result.Failure, row.Label); Assert.IsNull(result.CancelDisposition, row.Label);
            CollectionAssert.AreEqual(bytes, WorkerProtocolCodec.SerializeControllerInput(result.Message), row.Label + ":serialize");
            var reparsed = WorkerProtocolCodec.ParseControllerInput(WorkerProtocolCodec.SerializeControllerInput(result.Message)); Assert.IsTrue(reparsed.IsSuccess, row.Label + ":reparse"); Assert.AreEqual(result.Message, reparsed.Message, row.Label + ":typed"); Assert.IsNull(reparsed.Failure, row.Label + ":reparse"); Assert.IsNull(reparsed.CancelDisposition, row.Label + ":reparse");
        }
    }

    [TestMethod]
    public void ControllerInput_KnownLookingPayloadAdditionsAreStripped()
    {
        foreach (var row in new (string Label, string Json, string Expected)[]
        {
            ("controller-input:execute:additions", Execute.Replace("\"trigger\":\"manual\"", "\"trigger\":\"manual\",\"jobId\":7,\"mode\":null,\"settings\":[],\"schedule\":{},\"assetIds\":[1],\"credentials\":\"x\",\"connectionString\":\"x\",\"workSet\":true", StringComparison.Ordinal), Execute),
            ("controller-input:cancel:additions", Cancel.Replace("\"payload\":{}", "\"payload\":{\"reason\":7,\"token\":null,\"deadline\":[],\"commandId\":{},\"replacement\":true}", StringComparison.Ordinal), Cancel)
        })
        {
            var result = WorkerProtocolCodec.ParseControllerInput(System.Text.Encoding.UTF8.GetBytes(row.Json));
            Assert.IsTrue(result.IsSuccess, row.Label); Assert.IsNotNull(result.Message, row.Label); Assert.IsNull(result.Failure, row.Label); Assert.IsNull(result.CancelDisposition, row.Label);
            CollectionAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes(row.Expected), WorkerProtocolCodec.SerializeControllerInput(result.Message), row.Label + ":stripped");
        }
    }

    private static void AssertFailure(string label, string json, WorkerProtocolFailureCode code)
    { var r = WorkerProtocolCodec.ParseControllerInput(System.Text.Encoding.UTF8.GetBytes(json)); Assert.IsFalse(r.IsSuccess, label); Assert.IsNull(r.Message, label); Assert.IsNull(r.CancelDisposition, label); Assert.IsNotNull(r.Failure, label); Assert.AreEqual(code, r.Failure.Code, label); Assert.IsFalse(string.IsNullOrWhiteSpace(r.Failure.Diagnostic), label); Assert.IsTrue(r.Failure.Diagnostic.Length <= 256, label); }
}
