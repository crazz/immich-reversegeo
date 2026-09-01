using System.Text;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol.Compatibility;

[TestClass]
public class SafeFailureTests
{
    [TestMethod]
    public void FailuresHaveOneSafeStructuredResultWithoutIgnoredFieldLeaks()
    {
        var ready = Encoding.UTF8.GetString(CompatibilityData.ReadFixture("canonical/ready.json"));
        const string sentinel = "SENTINEL-7f0c";
        const string credential = "Password=credential";
        const string connection = "Server=host;Database=db";
        const string sql = "SELECT secret FROM users";
        const string parser = "JsonException";
        const string stack = "at Worker.Method";
        var hostile = ready.Replace("\"payload\":{}", $"\"future\":\"{sentinel} {credential} {connection} {sql} {parser} {stack}\",\"payload\":{{}}", StringComparison.Ordinal).Replace("\"sequence\":1", "\"sequence\":0", StringComparison.Ordinal);
        AssertSafe(WorkerProtocolFailureCode.InvalidEnvelope, hostile, sentinel, credential, connection, sql, parser, stack);
    }

    [TestMethod]
    public void EveryStableCodecFailureFamilyHasSafeShape()
    {
        var ready = Encoding.UTF8.GetString(CompatibilityData.ReadFixture("canonical/ready.json"));
        foreach (var row in new (byte[] Frame, WorkerProtocolFailureCode Code)[]
        {
            (new byte[WorkerProtocolV1.MaxMessageBytes + 1], WorkerProtocolFailureCode.MessageTooLarge), ([0xc3], WorkerProtocolFailureCode.InvalidEncoding), ([] , WorkerProtocolFailureCode.InvalidFraming),
            ("{"u8.ToArray(), WorkerProtocolFailureCode.MalformedJson), (Encoding.UTF8.GetBytes(ready.Replace("\"sequence\":1", "\"sequence\":0", StringComparison.Ordinal)), WorkerProtocolFailureCode.InvalidEnvelope),
            (Encoding.UTF8.GetBytes(ready.Replace("immich-reversegeo.worker", "other", StringComparison.Ordinal)), WorkerProtocolFailureCode.UnsupportedProtocol), (Encoding.UTF8.GetBytes(ready.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal)), WorkerProtocolFailureCode.UnsupportedVersion),
            (Encoding.UTF8.GetBytes(ready.Replace("\"type\":\"ready\"", "\"type\":\"other\"", StringComparison.Ordinal)), WorkerProtocolFailureCode.UnsupportedType),
            (Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(CompatibilityData.ReadFixture("canonical/log-emitted.json")).Replace("\"level\":\"warning\"", "\"level\":\"invalid\"", StringComparison.Ordinal)), WorkerProtocolFailureCode.InvalidPayload)
        })
        {
            AssertSafe(row.Code, row.Frame);
        }
    }

    [TestMethod]
    public void HostileContentIsRedactedWhenTargetFailurePrecedenceIsAuthoritative()
    {
        const string sentinel = "SENTINEL-7f0c";
        const string credential = "Password=credential";
        const string connection = "Server=host;Database=db";
        const string sql = "SELECT secret FROM users";
        const string parser = "JsonException";
        const string stack = "at Worker.Method";
        var hostile = Encoding.UTF8.GetString(CompatibilityData.ReadFixture("canonical/ready.json")).Replace("\"payload\":{}", $"\"future\":\"{sentinel} {credential} {connection} {sql} {parser} {stack}\",\"payload\":{{}}", StringComparison.Ordinal);
        var forbidden = new[] { sentinel, credential, connection, sql, parser, stack };
        foreach (var row in new (string Candidate, WorkerProtocolFailureCode Code)[]
        {
            (hostile + "!", WorkerProtocolFailureCode.MalformedJson),
            (hostile.Replace("immich-reversegeo.worker", "unsupported", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedProtocol),
            (hostile.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedVersion),
            (hostile.Replace("worker-to-controller", "controller-to-worker", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            (hostile.Replace("\"category\":\"lifecycle\"", "\"category\":\"unknown\"", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            (hostile.Replace("\"type\":\"ready\"", "\"type\":\"unknown\"", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedType),
            (hostile.Replace("\"sequence\":1", "\"sequence\":0", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope)
        })
        {
            AssertSafe(row.Code, row.Candidate, forbidden);
        }
        var hostileLog = Encoding.UTF8.GetString(CompatibilityData.ReadFixture("canonical/log-emitted.json")).Replace("\"payload\":", $"\"future\":\"{sentinel} {credential} {connection} {sql} {parser} {stack}\",\"payload\":", StringComparison.Ordinal).Replace("\"level\":\"warning\"", "\"level\":\"invalid\"", StringComparison.Ordinal);
        AssertSafe(WorkerProtocolFailureCode.InvalidPayload, hostileLog, forbidden);
        AssertSafe(WorkerProtocolFailureCode.InvalidEncoding, [0xc3, .. Encoding.UTF8.GetBytes(hostile)], forbidden);
        AssertSafe(WorkerProtocolFailureCode.InvalidFraming, Encoding.UTF8.GetBytes(hostile + "\r"), forbidden);
        AssertSafe(WorkerProtocolFailureCode.MessageTooLarge, Encoding.UTF8.GetBytes(hostile).Concat(new byte[WorkerProtocolV1.MaxMessageBytes]).ToArray(), forbidden);
    }

    [TestMethod]
    public void RetainedInvalidSequenceFixtureHasExactSafeFailure()
    {
        AssertSafe(WorkerProtocolFailureCode.InvalidEnvelope, CompatibilityData.ReadFixture("invalid/ready-invalid-sequence.json"));
    }

    [TestMethod]
    public void PublicFailureConstructorRejectsBlankAndOverlongDiagnostics()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidEnvelope, " "));
        Assert.ThrowsExactly<ArgumentException>(() => new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidEnvelope, new string('x', 257)));
    }

    private static void AssertSafe(WorkerProtocolFailureCode expected, string json, params string[] forbidden) => AssertSafe(expected, Encoding.UTF8.GetBytes(json), forbidden);
    private static void AssertSafe(WorkerProtocolFailureCode expected, byte[] frame, params string[] forbidden)
    {
        var result = WorkerProtocolCodec.Parse(frame);
        Assert.IsFalse(result.IsSuccess); Assert.IsNull(result.Event); Assert.IsNotNull(result.Failure); Assert.AreEqual(expected, result.Failure.Code);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Diagnostic)); Assert.IsTrue(result.Failure.Diagnostic.Length <= 256);
        foreach (var text in forbidden) { Assert.IsFalse(result.Failure.Diagnostic.Contains(text, StringComparison.Ordinal)); }
        Assert.IsFalse(result.Failure.Diagnostic.Contains("Exception", StringComparison.Ordinal)); Assert.IsFalse(result.Failure.Diagnostic.Contains("StackTrace", StringComparison.Ordinal)); Assert.IsFalse(result.Failure.Diagnostic.Contains("\n at ", StringComparison.Ordinal));
    }
}
