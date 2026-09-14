using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol;

[TestClass]
public class ControllerInputEnvelopeMatrixTests
{
    private const string Execute = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\"}}";
    private const string Cancel = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"control\",\"type\":\"cancel\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{}}";

    [TestMethod]
    public void ControllerInput_EnvelopeKnownFieldMutationsFailClosed()
    {
        foreach (var row in new (string Label, string Json, WorkerProtocolFailureCode Code)[]
        {
            ("controller-input:protocol:missing", Execute.Replace("\"protocol\":\"immich-reversegeo.worker\",", "", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:protocol:null", Execute.Replace("\"protocol\":\"immich-reversegeo.worker\"", "\"protocol\":null", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:protocol:blank", Execute.Replace("immich-reversegeo.worker", "", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedProtocol),
            ("controller-input:version:missing", Execute.Replace("\"version\":1,", "", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:version:kind", Execute.Replace("\"version\":1", "\"version\":\"1\"", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:version:unsupported", Execute.Replace("\"version\":1", "\"version\":0", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedVersion),
            ("controller-input:direction:case", Execute.Replace("controller-to-worker", "Controller-to-worker", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:category:mismatch", Execute.Replace("\"category\":\"request\"", "\"category\":\"control\"", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:type:reserved", Execute.Replace("\"type\":\"execute\"", "\"type\":\"shutdown\"", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:sequence:zero", Execute.Replace("\"sequence\":1", "\"sequence\":0", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:timestampUtc:offset", Execute.Replace("2026-08-29T12:00:00.0000000Z", "2026-08-29T12:00:00.0000000+00:00", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:runId:compact", Execute.Replace("01234567-89ab-cdef-0123-456789abcdef", "0123456789abcdef0123456789abcdef", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:payload:kind", Execute.Replace("\"payload\":{\"trigger\":\"manual\"}", "\"payload\":[]", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:property:case", Execute.Replace("\"protocol\"", "\"Protocol\"", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope)
        })
        {
            AssertFailure(row.Label, row.Json, row.Code);
        }
    }

    [TestMethod]
    public void ControllerInput_PropertyAliasesAndCoreFieldKindsFailClosed()
    {
        foreach (var row in new (string Label, string Json, WorkerProtocolFailureCode Code)[]
        {
            ("controller-input:property:version-case", Execute.Replace("\"version\"", "\"Version\"", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:property:direction-case", Execute.Replace("\"direction\"", "\"Direction\"", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:property:category-case", Execute.Replace("\"category\"", "\"Category\"", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:property:type-case", Execute.Replace("\"type\"", "\"Type\"", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:property:sequence-case", Execute.Replace("\"sequence\"", "\"Sequence\"", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:property:timestamp-case", Execute.Replace("\"timestampUtc\"", "\"TimestampUtc\"", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:property:runid-case", Execute.Replace("\"runId\"", "\"RunId\"", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:property:payload-case", Execute.Replace("\"payload\"", "\"Payload\"", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:property:trigger-case", Execute.Replace("\"trigger\"", "\"Trigger\"", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("controller-input:version:null", Execute.Replace("\"version\":1", "\"version\":null", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:direction:null", Execute.Replace("\"direction\":\"controller-to-worker\"", "\"direction\":null", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:category:kind", Execute.Replace("\"category\":\"request\"", "\"category\":1", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:type:kind", Execute.Replace("\"type\":\"execute\"", "\"type\":1", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:direction:unsupported", Execute.Replace("controller-to-worker", "other", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:category:case-value", Execute.Replace("\"request\"", "\"Request\"", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:type:case-value", Execute.Replace("\"execute\"", "\"Execute\"", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:protocol:upper-value", Execute.Replace("immich-reversegeo.worker", "IMMICH-REVERSEGEO.WORKER", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedProtocol)
        }) { AssertFailure(row.Label, row.Json, row.Code); }
    }

    [TestMethod]
    public void ControllerInput_WorkerEventsAndReservedCommandsAreRejected()
    {
        foreach (var row in new (string Label, string Category, string Type)[]
        {
            ("controller-input:worker:ready", "lifecycle", "ready"), ("controller-input:worker:run-started", "lifecycle", "run-started"),
            ("controller-input:worker:eligibility", "lifecycle", "eligibility-determined"), ("controller-input:worker:progress", "progress", "progress-changed"),
            ("controller-input:worker:activity-start", "activity", "activity-started"), ("controller-input:worker:activity-end", "activity", "activity-ended"),
            ("controller-input:worker:log", "diagnostic", "log-emitted"), ("controller-input:worker:completed", "terminal", "completed"),
            ("controller-input:worker:cancelled", "terminal", "cancelled"), ("controller-input:worker:failed", "terminal", "failed"),
            ("controller-input:reserved:ping", "control", "ping"), ("controller-input:reserved:shutdown", "control", "shutdown"), ("controller-input:reserved:generic", "request", "command")
        })
        {
            var json = Execute.Replace("\"category\":\"request\",\"type\":\"execute\"", "\"category\":\"" + row.Category + "\",\"type\":\"" + row.Type + "\"", StringComparison.Ordinal);
            AssertFailure(row.Label, json, WorkerProtocolFailureCode.UnsupportedType);
        }
    }

    [TestMethod]
    public void ControllerInput_AdditionsStripAndDuplicatesRejectRecursively()
    {
        foreach (var row in new (string Label, string Json, bool Success)[]
        {
            ("controller-input:additive:envelope-scalar", Execute.Replace("\"payload\"", "\"future\":7,\"payload\"", StringComparison.Ordinal), true),
            ("controller-input:additive:envelope-null", Execute.Replace("\"payload\"", "\"future\":null,\"payload\"", StringComparison.Ordinal), true),
            ("controller-input:additive:envelope-array", Execute.Replace("\"payload\"", "\"future\":[7],\"payload\"", StringComparison.Ordinal), true),
            ("controller-input:additive:envelope-object", Execute.Replace("\"payload\"", "\"future\":{\"nested\":true},\"payload\"", StringComparison.Ordinal), true),
            ("controller-input:additive:execute-payload", Execute.Replace("\"trigger\":\"manual\"", "\"trigger\":\"manual\",\"jobId\":\"x\",\"mode\":null,\"settings\":[],\"workSet\":{}", StringComparison.Ordinal), true),
            ("controller-input:additive:cancel-payload", Cancel.Replace("\"payload\":{}", "\"payload\":{\"reason\":\"x\",\"token\":null,\"deadline\":[],\"commandId\":{},\"replacement\":true}", StringComparison.Ordinal), true),
            ("controller-input:duplicate:known-envelope", Execute.Replace("\"protocol\":\"immich-reversegeo.worker\",", "\"protocol\":\"immich-reversegeo.worker\",\"protocol\":\"immich-reversegeo.worker\",", StringComparison.Ordinal), false),
            ("controller-input:duplicate:unknown-payload", Execute.Replace("\"trigger\":\"manual\"", "\"trigger\":\"manual\",\"future\":1,\"future\":2", StringComparison.Ordinal), false),
            ("controller-input:duplicate:nested-object", Execute.Replace("\"payload\"", "\"future\":{\"x\":1,\"x\":2},\"payload\"", StringComparison.Ordinal), false),
            ("controller-input:duplicate:nested-array", Execute.Replace("\"payload\"", "\"future\":[{\"x\":1,\"x\":2}],\"payload\"", StringComparison.Ordinal), false)
        })
        {
            var result = WorkerProtocolCodec.ParseControllerInput(System.Text.Encoding.UTF8.GetBytes(row.Json));
            if (row.Success)
            {
                Assert.IsTrue(result.IsSuccess, row.Label); Assert.IsNotNull(result.Message, row.Label); Assert.IsNull(result.Failure, row.Label); Assert.IsNull(result.CancelDisposition, row.Label);
                CollectionAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes(row.Json.StartsWith("{\"protocol\"", StringComparison.Ordinal) && row.Json.Contains("\"type\":\"cancel\"", StringComparison.Ordinal) ? Cancel : Execute), WorkerProtocolCodec.SerializeControllerInput(result.Message), row.Label + ":stripped");
            }
            else { AssertFailure(row.Label, row.Json, WorkerProtocolFailureCode.InvalidEnvelope); }
        }
    }

    private static void AssertFailure(string label, string json, WorkerProtocolFailureCode code)
    {
        var result = WorkerProtocolCodec.ParseControllerInput(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.IsFalse(result.IsSuccess, label); Assert.IsNull(result.Message, label); Assert.IsNull(result.CancelDisposition, label); Assert.IsNotNull(result.Failure, label); Assert.AreEqual(code, result.Failure.Code, label); Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Diagnostic), label); Assert.IsTrue(result.Failure.Diagnostic.Length <= 256, label);
    }
}
