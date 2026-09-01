using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol;

[TestClass]
public class ControllerInputCompatibilityTests
{
    private const string Execute = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\"}}";

    [TestMethod]
    public void ControllerInput_AdditiveEnvelopeAndPayloadPropertiesAreStripped()
    {
        var candidate = Execute.Replace("\"payload\":{\"trigger\":\"manual\"}", "\"future\":[{\"nested\":true}],\"payload\":{\"trigger\":\"manual\",\"jobId\":\"ignored\",\"credentials\":null,\"workSet\":{\"assetIds\":[1]}}", StringComparison.Ordinal);
        var parsed = WorkerProtocolCodec.ParseControllerInput(System.Text.Encoding.UTF8.GetBytes(candidate));

        Assert.IsTrue(parsed.IsSuccess, "controller-input:additive:accepted");
        Assert.IsNull(parsed.Failure, "controller-input:additive:accepted");
        Assert.IsNull(parsed.CancelDisposition, "controller-input:additive:accepted");
        Assert.IsNotNull(parsed.Message, "controller-input:additive:accepted");
        var execute = (ExecuteRequestPayload)parsed.Message.Payload;
        Assert.AreEqual(new ProcessingRunRequest(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), ProcessingRunTrigger.Manual), execute.Request, "controller-input:additive:request");
        CollectionAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes(Execute), WorkerProtocolCodec.SerializeControllerInput(parsed.Message), "controller-input:additive:canonical");
    }

    [TestMethod]
    public void ControllerInput_KnownFieldAndDiscriminatorMutationsFailClosed()
    {
        foreach (var row in new (string Label, string Json, WorkerProtocolFailureCode Code)[]
        {
            ("controller-input:protocol:unsupported", Execute.Replace("immich-reversegeo.worker", "other", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedProtocol),
            ("controller-input:version:unsupported", Execute.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedVersion),
            ("controller-input:direction:worker-event", Execute.Replace("controller-to-worker", "worker-to-controller", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:category:ping", Execute.Replace("\"category\":\"request\"", "\"category\":\"control\"", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            ("controller-input:trigger:case", Execute.Replace("manual", "Manual", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("controller-input:runId:upper", Execute.Replace("01234567-89ab-cdef-0123-456789abcdef", "01234567-89AB-cdef-0123-456789abcdef", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:sequence:exponent", Execute.Replace("\"sequence\":1", "\"sequence\":1e0", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("controller-input:payload:null", Execute.Replace("\"payload\":{\"trigger\":\"manual\"}", "\"payload\":null", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope)
        })
        {
            var result = WorkerProtocolCodec.ParseControllerInput(System.Text.Encoding.UTF8.GetBytes(row.Json));
            Assert.IsFalse(result.IsSuccess, row.Label);
            Assert.IsNull(result.Message, row.Label);
            Assert.IsNotNull(result.Failure, row.Label);
            Assert.IsNull(result.CancelDisposition, row.Label);
            Assert.AreEqual(row.Code, result.Failure.Code, row.Label);        }
    }

    [TestMethod]
    public void ControllerInput_HostileSingleDefectDoesNotLeakRawValue()
    {
        const string sentinel = "SENTINEL Password=one;Server=host;Database=db JsonException at Worker.Method";
        var hostile = Execute.Replace("\"trigger\":\"manual\"", "\"trigger\":\"" + sentinel + "\"", StringComparison.Ordinal);
        var result = WorkerProtocolCodec.ParseControllerInput(System.Text.Encoding.UTF8.GetBytes(hostile));

        Assert.IsFalse(result.IsSuccess, "controller-input:trigger:hostile");
        Assert.IsNull(result.Message, "controller-input:trigger:hostile");
        Assert.IsNotNull(result.Failure, "controller-input:trigger:hostile");
        Assert.IsNull(result.CancelDisposition, "controller-input:trigger:hostile");
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidPayload, result.Failure.Code, "controller-input:trigger:hostile");
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Diagnostic), "controller-input:trigger:hostile");
        Assert.IsTrue(result.Failure.Diagnostic.Length <= 256, "controller-input:trigger:hostile");
        foreach (var marker in new (string Label, string Value)[] { ("raw-frame", hostile), ("sentinel", "SENTINEL"), ("credential", "Password="), ("connection", "Server="), ("connection-string", "Database="), ("parser", "JsonException"), ("exception", "Exception"), ("stack", "at Worker"), ("stacktrace", "StackTrace"), ("cr", "\r"), ("lf", "\n") })
        {
            Assert.IsFalse(result.Failure.Diagnostic.Contains(marker.Value, StringComparison.Ordinal), "controller-input:trigger:hostile:" + marker.Label);
        }
    }
}
