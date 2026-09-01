using System.Text;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol.Compatibility;

[TestClass]
public class FramingAndEncodingTests
{
    [TestMethod]
    public void ExactAndOversizedFramesUseByteCountBeforeJsonClassification()
    {
        var exact = BoundaryReady(WorkerProtocolV1.MaxMessageBytes, "é");
        var oversizedValid = BoundaryReady(WorkerProtocolV1.MaxMessageBytes + 1, "é");
        var oversizedMalformed = new byte[WorkerProtocolV1.MaxMessageBytes + 1];

        Assert.AreEqual(WorkerProtocolV1.MaxMessageBytes, exact.Length);
        Assert.AreEqual(WorkerProtocolV1.MaxMessageBytes + 1, oversizedValid.Length);
        AssertAccepted(exact, CompatibilityData.Ready());
        AssertAccepted(exact.Concat("\n"u8.ToArray()).ToArray(), CompatibilityData.Ready());
        AssertAccepted(exact.Concat("\r\n"u8.ToArray()).ToArray(), CompatibilityData.Ready());
        AssertRejected(oversizedValid, WorkerProtocolFailureCode.MessageTooLarge, "ready:oversized");
        AssertRejected(oversizedValid.Concat("\n"u8.ToArray()).ToArray(), WorkerProtocolFailureCode.MessageTooLarge, "ready:oversized:lf");
        AssertRejected(oversizedValid.Concat("\r\n"u8.ToArray()).ToArray(), WorkerProtocolFailureCode.MessageTooLarge, "ready:oversized:crlf");
        AssertRejected(oversizedMalformed, WorkerProtocolFailureCode.MessageTooLarge, "malformed:oversized");
    }

    [TestMethod]
    public void DiagnosticFramesUseExactUtf8ByteLimitsAndDelimiterExclusion()
    {
        var asciiExact = BoundaryDiagnostic(WorkerProtocolV1.MaxMessageBytes, string.Empty);
        var asciiOver = BoundaryDiagnostic(WorkerProtocolV1.MaxMessageBytes + 1, string.Empty);
        var multibyteExact = BoundaryDiagnostic(WorkerProtocolV1.MaxMessageBytes, "é");
        var multibyteOver = BoundaryDiagnostic(WorkerProtocolV1.MaxMessageBytes + 1, "é");
        Assert.AreEqual(WorkerProtocolV1.MaxMessageBytes, asciiExact.Frame.Length); Assert.AreEqual(WorkerProtocolV1.MaxMessageBytes + 1, asciiOver.Frame.Length);
        Assert.AreEqual(WorkerProtocolV1.MaxMessageBytes, multibyteExact.Frame.Length); Assert.AreEqual(WorkerProtocolV1.MaxMessageBytes + 1, multibyteOver.Frame.Length);
        foreach (var exact in new[] { asciiExact, multibyteExact })
        {
            AssertAccepted(exact.Frame, exact.Event); AssertAccepted(exact.Frame.Concat("\n"u8.ToArray()).ToArray(), exact.Event); AssertAccepted(exact.Frame.Concat("\r\n"u8.ToArray()).ToArray(), exact.Event);
        }
        foreach (var over in new[] { asciiOver, multibyteOver })
        {
            AssertRejected(over.Frame, WorkerProtocolFailureCode.MessageTooLarge, $"diagnostic:{over.Label}:oversized"); AssertRejected(over.Frame.Concat("\n"u8.ToArray()).ToArray(), WorkerProtocolFailureCode.MessageTooLarge, $"diagnostic:{over.Label}:oversized:lf"); AssertRejected(over.Frame.Concat("\r\n"u8.ToArray()).ToArray(), WorkerProtocolFailureCode.MessageTooLarge, $"diagnostic:{over.Label}:oversized:crlf");
        }
        AssertRejected(new byte[WorkerProtocolV1.MaxMessageBytes + 1], WorkerProtocolFailureCode.MessageTooLarge, "malformed:oversized");
    }

    [TestMethod]
    public void AcceptedSingleFramesReturnTheCompleteExpectedEvent()
    {
        var ready = CompatibilityData.ReadFixture("canonical/ready.json");
        AssertAccepted(ready, CompatibilityData.Ready());
        AssertAccepted(ready.Concat("\n"u8.ToArray()).ToArray(), CompatibilityData.Ready());
        AssertAccepted(ready.Concat("\r\n"u8.ToArray()).ToArray(), CompatibilityData.Ready());
    }

    [TestMethod]
    public void FramingEncodingAndPurityRejectionsHaveNoEventAndSafeFailure()
    {
        var ready = CompatibilityData.ReadFixture("canonical/ready.json");
        foreach (var row in new (string Label, byte[] Frame, WorkerProtocolFailureCode Code)[]
        {
            ("empty-frame", [], WorkerProtocolFailureCode.InvalidFraming),
            ("whitespace-only", "   "u8.ToArray(), WorkerProtocolFailureCode.MalformedJson),
            ("bare-cr", ready.Concat("\r"u8.ToArray()).ToArray(), WorkerProtocolFailureCode.InvalidFraming),
            ("repeated-delimiter", ready.Concat("\n\n"u8.ToArray()).ToArray(), WorkerProtocolFailureCode.InvalidFraming),
            ("utf8-bom", [0xef, 0xbb, 0xbf, .. ready], WorkerProtocolFailureCode.InvalidEncoding),
            ("truncated-utf8", [0xc3], WorkerProtocolFailureCode.InvalidEncoding),
            ("trailing-byte", ready.Concat("x"u8.ToArray()).ToArray(), WorkerProtocolFailureCode.MalformedJson),
            ("multiple-frames", ready.Concat(ready).ToArray(), WorkerProtocolFailureCode.MalformedJson),
            ("ordinary-log", "ordinary log line"u8.ToArray(), WorkerProtocolFailureCode.MalformedJson),
            ("prefix", Encoding.UTF8.GetBytes("prefix ").Concat(ready).ToArray(), WorkerProtocolFailureCode.MalformedJson),
            ("unsupported-protocol", Encoding.UTF8.GetBytes("{\"protocol\":\"unsupported\"}"), WorkerProtocolFailureCode.UnsupportedProtocol),
            ("truncated-string", "{\"protocol\":\"unterminated"u8.ToArray(), WorkerProtocolFailureCode.MalformedJson)
        })
        {
            AssertRejected(row.Frame, row.Code, row.Label);
        }

        var escapedNewline = WorkerProtocolCodec.Serialize(new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 2, CompatibilityData.Start.AddSeconds(1), CompatibilityData.RunId, new LogEmittedPayload("information", "first\nsecond")));
        AssertAccepted(escapedNewline, new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 2, CompatibilityData.Start.AddSeconds(1), CompatibilityData.RunId, new LogEmittedPayload("information", "first\nsecond")));
        AssertRejected(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(escapedNewline).Replace("\\n", "\n", StringComparison.Ordinal)), WorkerProtocolFailureCode.InvalidFraming, "diagnostic:message:literal-lf");
        var escapedCarriageReturn = WorkerProtocolCodec.Serialize(new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 2, CompatibilityData.Start.AddSeconds(1), CompatibilityData.RunId, new LogEmittedPayload("information", "first\rsecond")));
        AssertAccepted(escapedCarriageReturn, new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 2, CompatibilityData.Start.AddSeconds(1), CompatibilityData.RunId, new LogEmittedPayload("information", "first\rsecond")));
        AssertRejected(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(escapedCarriageReturn).Replace("\\r", "\r", StringComparison.Ordinal)), WorkerProtocolFailureCode.InvalidFraming, "diagnostic:message:literal-cr");
    }

    private static (byte[] Frame, WorkerProtocolEvent Event, string Label) BoundaryDiagnostic(int length, string multibyte)
    {
        const string prefix = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"diagnostic\",\"type\":\"log-emitted\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"level\":\"information\",\"message\":\"";
        const string suffix = "\"}}";
        var padding = length - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(multibyte) - Encoding.UTF8.GetByteCount(suffix);
        Assert.IsTrue(padding >= 0);
        var message = multibyte + new string('x', padding);
        var frame = Encoding.UTF8.GetBytes(prefix + message + suffix);
        Assert.AreEqual(length, frame.Length);
        return (frame, new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 2, CompatibilityData.Start.AddSeconds(1), CompatibilityData.RunId, new LogEmittedPayload("information", message)), string.IsNullOrEmpty(multibyte) ? "ascii" : "multibyte");
    }

    private static byte[] BoundaryReady(int length, string multibyte)
    {
        const string prefix = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":null,\"payload\":{\"padding\":\"";
        const string suffix = "\"}}";
        var paddingLength = length - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(multibyte) - Encoding.UTF8.GetByteCount(suffix);
        Assert.IsTrue(paddingLength >= 0);
        return Encoding.UTF8.GetBytes(prefix + multibyte + new string('x', paddingLength) + suffix);
    }

    private static void AssertAccepted(byte[] frame, WorkerProtocolEvent expected)
    {
        var result = WorkerProtocolCodec.Parse(frame);
        Assert.IsTrue(result.IsSuccess, result.Failure?.Diagnostic);
        Assert.IsNull(result.Failure);
        Assert.AreEqual(expected, result.Event);
    }

    private static void AssertRejected(byte[] frame, WorkerProtocolFailureCode expected, string caseLabel)
    {
        var result = WorkerProtocolCodec.Parse(frame);
        Assert.IsFalse(result.IsSuccess, caseLabel);
        Assert.IsNull(result.Event, caseLabel);
        Assert.IsNotNull(result.Failure, caseLabel);
        Assert.AreEqual(expected, result.Failure.Code, caseLabel);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Diagnostic), caseLabel);
        Assert.IsTrue(result.Failure.Diagnostic.Length <= 256, caseLabel);
    }
}
