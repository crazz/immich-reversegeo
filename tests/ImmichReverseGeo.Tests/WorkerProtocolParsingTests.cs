using System.Text;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class WorkerProtocolParsingTests
{
    private const int IndependentMaxMessageBytes = 1_048_576;
    private const string ReadyJson = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":null,\"payload\":{}}";
    private const string StartedJson = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"run-started\",\"sequence\":9223372036854775807,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\",\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\"}}";
    private const string TerminalJson = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"completed\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:05.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\",\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\",\"endedAtUtc\":\"2026-08-29T12:00:05.0000000Z\",\"processedCount\":3,\"updatedCount\":1,\"skippedCount\":1,\"failedCount\":1,\"failureMessage\":null}}";
    private const string LogJson = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"diagnostic\",\"type\":\"log-emitted\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"level\":\"error\",\"message\":\"message\"}}";
    private const string ActivityJson = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"activity\",\"type\":\"activity-started\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"activityId\":\"fedcba98-7654-3210-fedc-ba9876543210\",\"label\":\"download\"}}";
    private const string ProgressJson = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"progress\",\"type\":\"progress-changed\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"processedCount\":3,\"updatedCount\":1,\"skippedCount\":1,\"failedCount\":1}}";
    private const string EligibilityJson = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"eligibility-determined\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"eligibleCount\":1}}";
    private const string ActivityEndedJson = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"activity\",\"type\":\"activity-ended\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"activityId\":\"fedcba98-7654-3210-fedc-ba9876543210\"}}";

    [TestMethod]
    public void Parse_HandAuthoredFrames_ReturnExactTypedFields()
    {
        var ready = Parse(ReadyJson);
        Assert.AreEqual("lifecycle", ready.Category);
        Assert.AreEqual("ready", ready.Type);
        Assert.AreEqual(1L, ready.Sequence);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero), ready.TimestampUtc);
        Assert.IsNull(ready.RunId);
        Assert.IsInstanceOfType<ReadyPayload>(ready.Payload);

        var started = Parse(StartedJson);
        Assert.AreEqual("lifecycle", started.Category);
        Assert.AreEqual("run-started", started.Type);
        Assert.AreEqual(long.MaxValue, started.Sequence);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero), started.TimestampUtc);
        Assert.AreEqual(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), started.RunId);
        var startedPayload = (RunStartedPayload)started.Payload;
        Assert.AreEqual("manual", startedPayload.Trigger);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero), startedPayload.StartedAtUtc);

        var terminal = Parse(TerminalJson);
        Assert.AreEqual("terminal", terminal.Category);
        Assert.AreEqual("completed", terminal.Type);
        Assert.AreEqual(2L, terminal.Sequence);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 29, 12, 0, 5, TimeSpan.Zero), terminal.TimestampUtc);
        Assert.AreEqual(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), terminal.RunId);
        var payload = (CompletedPayload)terminal.Payload;
        Assert.AreEqual("manual", payload.Trigger);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero), payload.StartedAtUtc);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 29, 12, 0, 5, TimeSpan.Zero), payload.EndedAtUtc);
        Assert.AreEqual(3L, payload.ProcessedCount);
        Assert.AreEqual(1L, payload.UpdatedCount);
        Assert.AreEqual(1L, payload.SkippedCount);
        Assert.AreEqual(1L, payload.FailedCount);
        Assert.IsNull(payload.FailureMessage);
    }

    [TestMethod]
    public void Parse_AllRequiredEnvelopeProperties_RejectMissingWrongOrDuplicateMutation()
    {
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"protocol\":\"immich-reversegeo.worker\",", ""));
        AssertRejected(WorkerProtocolFailureCode.UnsupportedProtocol, ReadyJson.Replace("immich-reversegeo.worker", "wrong-protocol"));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"protocol\":\"immich-reversegeo.worker\",", "\"protocol\":\"immich-reversegeo.worker\",\"protocol\":\"immich-reversegeo.worker\","));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"version\":1,", ""));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"version\":1", "\"version\":\"1\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"version\":1,", "\"version\":1,\"version\":1,"));
        AssertRejected(WorkerProtocolFailureCode.UnsupportedType, ReadyJson.Replace("\"direction\":\"worker-to-controller\",", ""));
        AssertRejected(WorkerProtocolFailureCode.UnsupportedType, ReadyJson.Replace("worker-to-controller", "controller-to-worker"));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"direction\":\"worker-to-controller\",", "\"direction\":\"worker-to-controller\",\"direction\":\"worker-to-controller\","));
        AssertRejected(WorkerProtocolFailureCode.UnsupportedType, ReadyJson.Replace("\"category\":\"lifecycle\",", ""));
        AssertRejected(WorkerProtocolFailureCode.UnsupportedType, ReadyJson.Replace("\"category\":\"lifecycle\"", "\"category\":\"wrong\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"category\":\"lifecycle\",", "\"category\":\"lifecycle\",\"category\":\"lifecycle\","));
        AssertRejected(WorkerProtocolFailureCode.UnsupportedType, ReadyJson.Replace("\"type\":\"ready\",", ""));
        AssertRejected(WorkerProtocolFailureCode.UnsupportedType, ReadyJson.Replace("\"type\":\"ready\"", "\"type\":\"wrong\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"type\":\"ready\",", "\"type\":\"ready\",\"type\":\"ready\","));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"sequence\":1,", ""));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"sequence\":1", "\"sequence\":\"1\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"sequence\":1,", "\"sequence\":1,\"sequence\":1,"));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",", ""));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"timestampUtc\":1"));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",", "\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\","));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"runId\":null,", ""));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"runId\":null", "\"runId\":1"));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"runId\":null,", "\"runId\":null,\"runId\":null,"));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace(",\"payload\":{}", ""));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"payload\":{}", "\"payload\":null"));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"payload\":{}", "\"payload\":{},\"payload\":{}"));
    }

    [TestMethod]
    public void Parse_EveryKnownPayloadProperty_RejectsMissingWrongOrDuplicateMutation()
    {
        AssertPayloadPropertyRejected(StartedJson, "\"trigger\":\"manual\"", "\"trigger\":1", false);
        AssertPayloadPropertyRejected(StartedJson, "\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"startedAtUtc\":1", true);
        AssertPayloadPropertyRejected(ProgressJson, "\"processedCount\":3", "\"processedCount\":\"3\"", false);
        AssertPayloadPropertyRejected(ProgressJson, "\"updatedCount\":1", "\"updatedCount\":\"1\"", false);
        AssertPayloadPropertyRejected(ProgressJson, "\"skippedCount\":1", "\"skippedCount\":\"1\"", false);
        AssertPayloadPropertyRejected(ProgressJson, "\"failedCount\":1", "\"failedCount\":\"1\"", true);
        AssertPayloadPropertyRejected(EligibilityJson, "\"eligibleCount\":1", "\"eligibleCount\":\"1\"", true);
        AssertPayloadPropertyRejected(ActivityJson, "\"activityId\":\"fedcba98-7654-3210-fedc-ba9876543210\"", "\"activityId\":1", false);
        AssertPayloadPropertyRejected(ActivityJson, "\"label\":\"download\"", "\"label\":1", true);
        AssertPayloadPropertyRejected(ActivityEndedJson, "\"activityId\":\"fedcba98-7654-3210-fedc-ba9876543210\"", "\"activityId\":1", true);
        AssertPayloadPropertyRejected(LogJson, "\"level\":\"error\"", "\"level\":1", false);
        AssertPayloadPropertyRejected(LogJson, "\"message\":\"message\"", "\"message\":1", true);
        AssertPayloadPropertyRejected(TerminalJson, "\"trigger\":\"manual\"", "\"trigger\":1", false);
        AssertPayloadPropertyRejected(TerminalJson, "\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"startedAtUtc\":1", false);
        AssertPayloadPropertyRejected(TerminalJson, "\"endedAtUtc\":\"2026-08-29T12:00:05.0000000Z\"", "\"endedAtUtc\":1", false);
        AssertPayloadPropertyRejected(TerminalJson, "\"processedCount\":3", "\"processedCount\":\"3\"", false);
        AssertPayloadPropertyRejected(TerminalJson, "\"updatedCount\":1", "\"updatedCount\":\"1\"", false);
        AssertPayloadPropertyRejected(TerminalJson, "\"skippedCount\":1", "\"skippedCount\":\"1\"", false);
        AssertPayloadPropertyRejected(TerminalJson, "\"failedCount\":1", "\"failedCount\":\"1\"", false);
        AssertPayloadPropertyRejected(TerminalJson, "\"failureMessage\":null", "\"failureMessage\":1", true);
    }

    [TestMethod]
    public void Parse_CanonicalIntegerGuidAndTimestampForms_AreEnforced()
    {
        foreach (var mutation in new[]
        {
            ReadyJson.Replace("\"sequence\":1", "\"sequence\":0"),
            ReadyJson.Replace("\"sequence\":1", "\"sequence\":-1"),
            ReadyJson.Replace("\"sequence\":1", "\"sequence\":1.0"),
            ReadyJson.Replace("\"sequence\":1", "\"sequence\":1e0"),
            StartedJson.Replace("9223372036854775807", "9223372036854775808"),
            StartedJson.Replace("01234567-89ab-cdef-0123-456789abcdef", "01234567-89AB-cdef-0123-456789abcdef"),
            StartedJson.Replace("01234567-89ab-cdef-0123-456789abcdef", "{01234567-89ab-cdef-0123-456789abcdef}"),
            StartedJson.Replace("\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"timestampUtc\":\"2026-08-29T12:00:00Z\""),
            StartedJson.Replace("\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"timestampUtc\":\"2026-08-29T12:00:00.0000000+00:00\"")
        })
        {
            AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, mutation);
        }

        AssertRejected(WorkerProtocolFailureCode.MalformedJson, ReadyJson.Replace("\"sequence\":1", "\"sequence\":01"));
    }

    [TestMethod]
    public void Parse_InvalidPayloadDomainAndTerminalConsistency_AreRejected()
    {
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, StartedJson.Replace("\"manual\"", "\"automatic\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, LogJson.Replace("\"level\":\"error\"", "\"level\":\"verbose\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, LogJson.Replace("\"message\":\"message\"", "\"message\":\" \""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, ActivityJson.Replace("\"activityId\":\"fedcba98-7654-3210-fedc-ba9876543210\"", "\"activityId\":\"00000000-0000-0000-0000-000000000000\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, ActivityJson.Replace("\"label\":\"download\"", "\"label\":\" \""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, TerminalJson.Replace("\"processedCount\":3", "\"processedCount\":-1"));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, TerminalJson.Replace("\"processedCount\":3", "\"processedCount\":3.0"));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, TerminalJson.Replace("\"processedCount\":3", "\"processedCount\":9223372036854775808"));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, TerminalJson.Replace("\"processedCount\":3", "\"processedCount\":4"));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, TerminalJson.Replace("\"endedAtUtc\":\"2026-08-29T12:00:05.0000000Z\"", "\"endedAtUtc\":\"2026-08-29T11:59:59.0000000Z\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, TerminalJson.Replace("\"timestampUtc\":\"2026-08-29T12:00:05.0000000Z\"", "\"timestampUtc\":\"2026-08-29T12:00:04.0000000Z\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, TerminalJson.Replace("\"failureMessage\":null", "\"failureMessage\":\"unexpected\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, TerminalJson.Replace("\"type\":\"completed\"", "\"type\":\"failed\""));
    }

    [TestMethod]
    public void Parse_UnknownAndDuplicatePropertiesAtEveryNestedObject_AreHandledSafely()
    {
        Assert.IsTrue(WorkerProtocolCodec.Parse(Encoding.UTF8.GetBytes(ReadyJson.Replace("\"payload\":{}", "\"payload\":{\"future\":{\"nested\":true},\"items\":[{\"item\":1}]},\"futureEnvelope\":{\"nested\":true}"))).IsSuccess);
        foreach (var mutation in new[]
        {
            ReadyJson.Replace("\"payload\":{}", "\"payload\":{\"future\":{\"nested\":true,\"nested\":false}}"),
            ReadyJson.Replace("\"payload\":{}", "\"payload\":{\"items\":[{\"item\":1,\"item\":2}]}"),
            ReadyJson.Replace("\"payload\":{}", "\"payload\":{\"future\":{\"nested\":true},\"future\":{\"nested\":false}}")
        })
        {
            AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, mutation);
        }
    }

    [TestMethod]
    public void Parse_FramingUsesExactUtf8ByteLimitAndSafeDiagnostics()
    {
        var exact = CreateExactLimitReadyFrame();
        var oversized = new byte[exact.Length + 1];
        exact.CopyTo(oversized, 0);
        oversized[^1] = (byte)'x';
        Assert.AreEqual(IndependentMaxMessageBytes, exact.Length);
        Assert.AreEqual(IndependentMaxMessageBytes + 1, oversized.Length);
        Assert.IsTrue(WorkerProtocolCodec.Parse(exact).IsSuccess);
        AssertRejected(WorkerProtocolFailureCode.MessageTooLarge, oversized);
        Assert.IsTrue(WorkerProtocolCodec.Parse(Encoding.UTF8.GetBytes(ReadyJson + "\n")).IsSuccess);
        Assert.IsTrue(WorkerProtocolCodec.Parse(Encoding.UTF8.GetBytes(ReadyJson + "\r\n")).IsSuccess);
        AssertRejected(WorkerProtocolFailureCode.InvalidFraming, Encoding.UTF8.GetBytes(ReadyJson + "\r"));
        AssertRejected(WorkerProtocolFailureCode.InvalidFraming, Encoding.UTF8.GetBytes(ReadyJson + "\n" + ReadyJson));
        AssertRejected(WorkerProtocolFailureCode.InvalidEncoding, [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(ReadyJson)]);
        AssertRejected(WorkerProtocolFailureCode.InvalidEncoding, [0xc3, 0x28]);
        var failure = WorkerProtocolCodec.Parse(Encoding.UTF8.GetBytes(ReadyJson.Replace("immich-reversegeo.worker", "secret-input-token"))).Failure!;
        Assert.IsFalse(failure.Diagnostic.Contains("secret-input-token", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Parse_FramingRejectsEmptyAndLiteralLineBreaksButAcceptsEscapedLineBreaks()
    {
        AssertRejected(WorkerProtocolFailureCode.InvalidFraming, []);
        var escaped = Parse(LogJson.Replace("\"message\":\"message\"", "\"message\":\"first\\nsecond\\rthird\""));
        Assert.AreEqual("first\nsecond\rthird", ((LogEmittedPayload)escaped.Payload).Message);
        AssertRejected(WorkerProtocolFailureCode.InvalidFraming, LogJson.Replace("\"message\":\"message\"", "\"message\":\"first\nsecond\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidFraming, LogJson.Replace("\"message\":\"message\"", "\"message\":\"first\rsecond\""));
    }

    [TestMethod]
    public void Parse_CaseSensitiveKnownNamesAndTokensAreRejected()
    {
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"protocol\"", "\"Protocol\""));
        AssertRejected(WorkerProtocolFailureCode.UnsupportedProtocol, ReadyJson.Replace("immich-reversegeo.worker", "IMMICH-REVERSEGEO.WORKER"));
        AssertRejected(WorkerProtocolFailureCode.UnsupportedType, ReadyJson.Replace("\"direction\":\"worker-to-controller\"", "\"direction\":\"Worker-to-controller\""));
        AssertRejected(WorkerProtocolFailureCode.UnsupportedType, ReadyJson.Replace("\"category\":\"lifecycle\"", "\"category\":\"Lifecycle\""));
        AssertRejected(WorkerProtocolFailureCode.UnsupportedType, ReadyJson.Replace("\"type\":\"ready\"", "\"type\":\"Ready\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, StartedJson.Replace("\"trigger\":\"manual\"", "\"trigger\":\"Manual\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, LogJson.Replace("\"level\":\"error\"", "\"level\":\"Error\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, EligibilityJson.Replace("\"eligibleCount\"", "\"EligibleCount\""));
    }

    [TestMethod]
    public void Parse_RejectsEnvelopeAndPayloadInvariants()
    {
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, ReadyJson.Replace("\"runId\":null", "\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, StartedJson.Replace("\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\"", "\"runId\":null"));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, StartedJson.Replace("\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\"}}", "\"startedAtUtc\":\"2026-08-29T12:00:01.0000000Z\"}}"));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, TerminalJson.Replace("\"timestampUtc\":\"2026-08-29T12:00:05.0000000Z\"", "\"timestampUtc\":\"2026-08-29T12:00:04.0000000Z\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, ProgressJson.Replace("\"processedCount\":3", "\"processedCount\":2"));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, ProgressJson.Replace("\"processedCount\":3,\"updatedCount\":1,\"skippedCount\":1,\"failedCount\":1", "\"processedCount\":9223372036854775807,\"updatedCount\":9223372036854775807,\"skippedCount\":1,\"failedCount\":0"));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, TerminalJson.Replace("\"failureMessage\":null", "\"failureMessage\":\"unexpected\""));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, EligibilityJson.Replace("\"eligibleCount\":1", "\"eligibleCount\":-1"));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, ActivityEndedJson.Replace("fedcba98-7654-3210-fedc-ba9876543210", "00000000-0000-0000-0000-000000000000"));
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, TerminalJson.Replace("\"type\":\"completed\"", "\"type\":\"cancelled\"").Replace("\"failureMessage\":null", "\"failureMessage\":\"unexpected\""));
    }

    [TestMethod]
    public void Parse_FailuresHaveSafeShapeAndRedactInputSecrets()
    {
        const string malformedSecret = "password=malformed";
        const string protocolSecret = "Server=secret-password";
        const string envelopeSecret = "connection-string-secret";
        const string payloadSecret = "select * from secret_table";
        AssertSafeFailure(WorkerProtocolFailureCode.MalformedJson, ReadyJson + malformedSecret, malformedSecret);
        AssertSafeFailure(WorkerProtocolFailureCode.UnsupportedProtocol, ReadyJson.Replace("immich-reversegeo.worker", protocolSecret), protocolSecret);
        AssertSafeFailure(WorkerProtocolFailureCode.InvalidEnvelope, ReadyJson.Replace("\"sequence\":1", "\"sequence\":\"connection-string-secret\""), envelopeSecret);
        AssertSafeFailure(WorkerProtocolFailureCode.InvalidPayload, LogJson.Replace("\"level\":\"error\"", "\"level\":\"select * from secret_table\""), payloadSecret);
    }

    [TestMethod]
    public void Parse_MalformedAndUnsupportedCombinations_AreRejected()
    {
        AssertRejected(WorkerProtocolFailureCode.MalformedJson, ReadyJson[..^1]);
        AssertRejected(WorkerProtocolFailureCode.UnsupportedVersion, ReadyJson.Replace("\"version\":1", "\"version\":2"));
        AssertRejected(WorkerProtocolFailureCode.UnsupportedType, ReadyJson.Replace("\"category\":\"lifecycle\",\"type\":\"ready\"", "\"category\":\"progress\",\"type\":\"ready\""));
        AssertRejected(WorkerProtocolFailureCode.UnsupportedType, ReadyJson.Replace("\"direction\":\"worker-to-controller\"", "\"direction\":\"controller-to-worker\""));
    }

    private static WorkerProtocolEvent Parse(string json)
    {
        var result = WorkerProtocolCodec.Parse(Encoding.UTF8.GetBytes(json));
        Assert.IsTrue(result.IsSuccess, result.Failure?.Diagnostic);
        return result.Event!;
    }

    private static void AssertRejected(WorkerProtocolFailureCode expectedCode, string json) => AssertRejected(expectedCode, Encoding.UTF8.GetBytes(json));

    private static void AssertRejected(WorkerProtocolFailureCode expectedCode, byte[] frame)
    {
        var result = WorkerProtocolCodec.Parse(frame);
        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Event);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(expectedCode, result.Failure.Code);
    }

    private static void AssertSafeFailure(WorkerProtocolFailureCode expectedCode, string rawFrame, string secret)
    {
        var result = WorkerProtocolCodec.Parse(Encoding.UTF8.GetBytes(rawFrame));

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Event);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(expectedCode, result.Failure.Code);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Diagnostic));
        Assert.IsTrue(result.Failure.Diagnostic.Length <= 256);
        Assert.IsFalse(result.Failure.Diagnostic.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(result.Failure.Diagnostic.Contains(rawFrame, StringComparison.Ordinal));
        Assert.IsFalse(result.Failure.Diagnostic.Contains("JsonException", StringComparison.Ordinal));
        Assert.IsFalse(result.Failure.Diagnostic.Contains("StackTrace", StringComparison.Ordinal));
        Assert.IsFalse(result.Failure.Diagnostic.Contains(" at ", StringComparison.Ordinal));
    }

    private static void AssertPayloadPropertyRejected(string validFrame, string property, string wrongProperty, bool isLast)
    {
        var missing = isLast
            ? validFrame.Replace("," + property, "").Replace(property, "")
            : validFrame.Replace(property + ",", "");
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, missing);
        AssertRejected(WorkerProtocolFailureCode.InvalidPayload, validFrame.Replace(property, wrongProperty));
        AssertRejected(WorkerProtocolFailureCode.InvalidEnvelope, validFrame.Replace(property, property + "," + property));
    }

    private static byte[] CreateExactLimitReadyFrame()
    {
        const string prefix = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":null,\"payload\":{\"padding\":\"";
        const string suffix = "\"}}";
        const string multibyte = "é";
        var paddingLength = IndependentMaxMessageBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(multibyte) - Encoding.UTF8.GetByteCount(suffix);
        return Encoding.UTF8.GetBytes(prefix + multibyte + new string('x', paddingLength) + suffix);
    }
}
