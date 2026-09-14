using System.Text;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol.Compatibility;

[TestClass]
public class DecodeCompatibilityTests
{
    [TestMethod]
    public void AdditionsDuplicatesAndCaseVariantsHaveExplicitCompatibilityOutcomes()
    {
        var ready = Json("canonical/ready.json");
        foreach (var addition in new[] { "\"futureScalar\":1", "\"futureNull\":null", "\"futureArray\":[1]", "\"futureObject\":{\"value\":true}" })
        {
            AssertReady(ready.Replace("\"payload\":{}", "\"payload\":{" + addition + "}", StringComparison.Ordinal));
            AssertReady(ready.Replace("\"payload\":{}", addition + ",\"payload\":{}", StringComparison.Ordinal));
        }
        AssertReady(ready.Replace("\"protocol\":", "\"Protocol\":\"additive\",\"protocol\":", StringComparison.Ordinal));
        Reject(WorkerProtocolFailureCode.InvalidEnvelope, ready.Replace("\"protocol\":\"immich-reversegeo.worker\",", "", StringComparison.Ordinal), "ready:protocol:missing");
        Reject(WorkerProtocolFailureCode.InvalidEnvelope, ready.Replace("\"payload\":{}", "\"payload\":{\"future\":1,\"future\":2}", StringComparison.Ordinal), "ready:future:duplicate");
        Reject(WorkerProtocolFailureCode.InvalidEnvelope, ready.Replace("\"sequence\":1", "\"sequence\":1,\"sequence\":1", StringComparison.Ordinal), "ready:sequence:duplicate");
    }

    [TestMethod]
    public void DiscriminatorPrimitiveAndPayloadMutationsFailClosed()
    {
        var ready = Json("canonical/ready.json");
        foreach (var row in new[]
        {
            ("protocol-unsupported", "\"protocol\":\"immich-reversegeo.worker\"", "\"protocol\":\"IMMICH-REVERSEGEO.WORKER\"", WorkerProtocolFailureCode.UnsupportedProtocol),
            ("version-future", "\"version\":1", "\"version\":2", WorkerProtocolFailureCode.UnsupportedVersion),
            ("direction-wrong", "\"direction\":\"worker-to-controller\"", "\"direction\":\"controller-to-worker\"", WorkerProtocolFailureCode.UnsupportedType),
            ("category-wrong", "\"category\":\"lifecycle\"", "\"category\":\"terminal\"", WorkerProtocolFailureCode.UnsupportedType),
            ("type-unknown", "\"type\":\"ready\"", "\"type\":\"unknown\"", WorkerProtocolFailureCode.UnsupportedType),
            ("sequence-zero", "\"sequence\":1", "\"sequence\":0", WorkerProtocolFailureCode.InvalidEnvelope),
            ("timestamp-short", "\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"timestampUtc\":\"2026-08-29T12:00:00Z\"", WorkerProtocolFailureCode.InvalidEnvelope),
            ("run-id-number", "\"runId\":null", "\"runId\":1", WorkerProtocolFailureCode.InvalidEnvelope)
        })
        {
            Reject(row.Item4, ready.Replace(row.Item2, row.Item3, StringComparison.Ordinal), row.Item1);
        }
        foreach (var version in new[] { "\"version\":\"1\"", "\"version\":1.0", "\"version\":1e0", "\"version\":-1" })
        {
            Reject(WorkerProtocolFailureCode.InvalidEnvelope, ready.Replace("\"version\":1", version, StringComparison.Ordinal), "ready:version:" + version);
        }
        var started = Json("canonical/run-started.json");
        foreach (var id in new[] { "00000000-0000-0000-0000-000000000000", "01234567-89AB-cdef-0123-456789abcdef", "{01234567-89ab-cdef-0123-456789abcdef}", "0123456789abcdef0123456789abcdef", "invalid" })
        {
            Reject(WorkerProtocolFailureCode.InvalidEnvelope, started.Replace(CompatibilityData.RunId.ToString("D"), id, StringComparison.Ordinal), "run-started:runId:" + id);
        }
        foreach (var name in new[] { "trigger", "startedAtUtc" })
        {
            Reject(WorkerProtocolFailureCode.InvalidPayload, started.Replace($"\"{name}\":", $"\"{name}X\":", StringComparison.Ordinal), "run-started:" + name + ":case-name");
        }
        foreach (var row in CompatibilityData.CanonicalRows.Where(row => row.Name != "ready"))
        {
            var json = Json($"canonical/{row.Name}.json");
            Reject(WorkerProtocolFailureCode.InvalidEnvelope, json.Replace("\"payload\":", "\"payload\":null,\"payloadX\":", StringComparison.Ordinal), row.Name + ":payload:null");
        }
    }

    private static string Json(string path) => Encoding.UTF8.GetString(CompatibilityData.ReadFixture(path));
    private static void AssertReady(string json)
    {
        var result = WorkerProtocolCodec.Parse(Encoding.UTF8.GetBytes(json));
        Assert.IsTrue(result.IsSuccess, result.Failure?.Diagnostic);
        Assert.AreEqual(CompatibilityData.Ready(), result.Event);
        CollectionAssert.AreEqual(CompatibilityData.ReadFixture("canonical/ready.json"), WorkerProtocolCodec.Serialize(result.Event!));
    }
    internal static void Reject(WorkerProtocolFailureCode expected, string json, string caseLabel)
    {
        var result = WorkerProtocolCodec.Parse(Encoding.UTF8.GetBytes(json));
        Assert.IsFalse(result.IsSuccess, caseLabel);
        Assert.IsNull(result.Event, caseLabel);
        Assert.IsNotNull(result.Failure, caseLabel);
        Assert.AreEqual(expected, result.Failure.Code, caseLabel);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Diagnostic), caseLabel);
        Assert.IsTrue(result.Failure.Diagnostic.Length <= 256, caseLabel);
    }
}
