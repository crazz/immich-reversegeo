using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol.Compatibility;

[TestClass]
public class CanonicalFrameTests
{
    [TestMethod]
    public void CanonicalFixtures_ParseSerializeRepeatedlyAndRoundTrip()
    {
        foreach (var row in CompatibilityData.CanonicalRows)
        {
            var fixture = CompatibilityData.ReadFixture($"canonical/{row.Name}.json");
            var parsed = WorkerProtocolCodec.Parse(fixture);
            Assert.IsTrue(parsed.IsSuccess, row.Name + ": " + parsed.Failure?.Diagnostic);
            Assert.AreEqual(row.Expected, parsed.Event, row.Name);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var serialized = WorkerProtocolCodec.Serialize(row.Create());
                CollectionAssert.AreEqual(fixture, serialized, row.Name);
                var reparsed = WorkerProtocolCodec.Parse(serialized);
                Assert.IsTrue(reparsed.IsSuccess, reparsed.Failure?.Diagnostic);
                Assert.AreEqual(row.Expected, reparsed.Event, row.Name);
            }
        }
    }

    [TestMethod]
    public void MinimumAndMaximumUtcYearsUseManualSevenFractionFrames()
    {
        foreach (var row in new (WorkerProtocolEvent Event, byte[] Bytes)[]
        {
            (new WorkerProtocolEvent(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.ReadyType, 1, new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero), null, new ReadyPayload()), "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"0001-01-01T00:00:00.0000000Z\",\"runId\":null,\"payload\":{}}"u8.ToArray()),
            (new WorkerProtocolEvent(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.ReadyType, 1, new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9999999), null, new ReadyPayload()), "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"9999-12-31T23:59:59.9999999Z\",\"runId\":null,\"payload\":{}}"u8.ToArray())
        })
        {
            CollectionAssert.AreEqual(row.Bytes, WorkerProtocolCodec.Serialize(row.Event));
            var parsed = WorkerProtocolCodec.Parse(row.Bytes); Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic); Assert.AreEqual(row.Event, parsed.Event);
            var reparsed = WorkerProtocolCodec.Parse(WorkerProtocolCodec.Serialize(row.Event)); Assert.IsTrue(reparsed.IsSuccess, reparsed.Failure?.Diagnostic); Assert.AreEqual(row.Event, reparsed.Event);
        }
    }

    [TestMethod]
    public void DefinedTokensEscapingAndCounts_RoundTripWithExactMappings()
    {
        foreach (var @event in CompatibilityData.TokenVariants())
        {
            var parsed = WorkerProtocolCodec.Parse(WorkerProtocolCodec.Serialize(@event));
            Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
            Assert.AreEqual(@event, parsed.Event);
        }
    }

    [TestMethod]
    public void AdditiveEnvelopeAndPayloadShapesCanonicalizeAndDuplicateEnvelopeFails()
    {
        var canonical = CompatibilityData.ReadFixture("canonical/ready.json");
        var ready = System.Text.Encoding.UTF8.GetString(canonical);
        foreach (var addition in new[] { "\"futureScalar\":7", "\"futureNull\":null", "\"futureArray\":[7]", "\"futureObject\":{\"nested\":true}" })
        {
            foreach (var candidate in new[] { ready.Replace("\"payload\":{}", addition + ",\"payload\":{}", StringComparison.Ordinal), ready.Replace("\"payload\":{}", "\"payload\":{" + addition + "}", StringComparison.Ordinal) })
            {
                var parsed = WorkerProtocolCodec.Parse(System.Text.Encoding.UTF8.GetBytes(candidate));
                Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
                Assert.AreEqual(CompatibilityData.Ready(), parsed.Event);
                CollectionAssert.AreEqual(canonical, WorkerProtocolCodec.Serialize(parsed.Event!));
            }
        }
        var duplicate = WorkerProtocolCodec.Parse(System.Text.Encoding.UTF8.GetBytes(ready.Replace("\"payload\":{}", "\"future\":1,\"future\":2,\"payload\":{}", StringComparison.Ordinal)));
        Assert.IsFalse(duplicate.IsSuccess); Assert.IsNull(duplicate.Event); Assert.AreEqual(WorkerProtocolFailureCode.InvalidEnvelope, duplicate.Failure!.Code); Assert.IsFalse(string.IsNullOrWhiteSpace(duplicate.Failure.Diagnostic)); Assert.IsTrue(duplicate.Failure.Diagnostic.Length <= 256);
    }

    [TestMethod]
    public void ManualDefinedVariantBytesSerializeAndReparseExactly()
    {
        foreach (var row in ManualVariants())
        {
            var manual = row.Bytes();
            CollectionAssert.AreEqual(manual, WorkerProtocolCodec.Serialize(row.Event));
            var parsedManual = WorkerProtocolCodec.Parse(manual);
            Assert.IsTrue(parsedManual.IsSuccess, parsedManual.Failure?.Diagnostic);
            Assert.AreEqual(row.Event, parsedManual.Event);
            var reparsed = WorkerProtocolCodec.Parse(WorkerProtocolCodec.Serialize(row.Event));
            Assert.IsTrue(reparsed.IsSuccess, reparsed.Failure?.Diagnostic);
            Assert.AreEqual(row.Event, reparsed.Event);
        }
    }

    private static IEnumerable<(WorkerProtocolEvent Event, Func<byte[]> Bytes)> ManualVariants()
    {
        yield return (CompatibilityData.Started(2, "manual"), () => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"run-started\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\",\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\"}}"u8.ToArray());
        yield return (CompatibilityData.Started(2, "scheduled"), () => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"run-started\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"scheduled\",\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\"}}"u8.ToArray());
        yield return (CompatibilityData.Started(2, "run-once"), () => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"run-started\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"run-once\",\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\"}}"u8.ToArray());
        yield return (new WorkerProtocolEvent(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.EligibilityDeterminedType, long.MaxValue, CompatibilityData.Start.AddSeconds(1), CompatibilityData.RunId, new EligibilityDeterminedPayload(long.MaxValue)), () => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"eligibility-determined\",\"sequence\":9223372036854775807,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"eligibleCount\":9223372036854775807}}"u8.ToArray());
        yield return (new WorkerProtocolEvent(WorkerProtocolV1.ProgressCategory, WorkerProtocolV1.ProgressChangedType, 4, CompatibilityData.Start.AddSeconds(1), CompatibilityData.RunId, new ProgressChangedPayload(long.MaxValue, long.MaxValue, 0, 0)), () => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"progress\",\"type\":\"progress-changed\",\"sequence\":4,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"processedCount\":9223372036854775807,\"updatedCount\":9223372036854775807,\"skippedCount\":0,\"failedCount\":0}}"u8.ToArray());
        yield return (CompatibilityData.Terminal(3, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", CompatibilityData.Start, CompatibilityData.Start, long.MaxValue, long.MaxValue, 0, 0)), () => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"completed\",\"sequence\":3,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\",\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\",\"endedAtUtc\":\"2026-08-29T12:00:00.0000000Z\",\"processedCount\":9223372036854775807,\"updatedCount\":9223372036854775807,\"skippedCount\":0,\"failedCount\":0,\"failureMessage\":null}}"u8.ToArray());
        foreach (var level in new[] { "trace", "information", "warning", "error" })
        {
            yield return (new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 4, CompatibilityData.Start.AddSeconds(1), CompatibilityData.RunId, new LogEmittedPayload(level, "escape \"x\" \\ line\nnext")), () => System.Text.Encoding.UTF8.GetBytes("{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"diagnostic\",\"type\":\"log-emitted\",\"sequence\":4,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"level\":\"" + level + "\",\"message\":\"escape \\u0022x\\u0022 \\\\ line\\nnext\"}}"));
        }
        yield return (CompatibilityData.Terminal(3, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", CompatibilityData.Start, CompatibilityData.Start, 0, 0, 0, 0)), () => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"completed\",\"sequence\":3,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\",\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\",\"endedAtUtc\":\"2026-08-29T12:00:00.0000000Z\",\"processedCount\":0,\"updatedCount\":0,\"skippedCount\":0,\"failedCount\":0,\"failureMessage\":null}}"u8.ToArray());
        yield return (CompatibilityData.Terminal(3, WorkerProtocolV1.CancelledType, new CancelledPayload("scheduled", CompatibilityData.Start, CompatibilityData.Start, 0, 0, 0, 0)), () => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"cancelled\",\"sequence\":3,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"scheduled\",\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\",\"endedAtUtc\":\"2026-08-29T12:00:00.0000000Z\",\"processedCount\":0,\"updatedCount\":0,\"skippedCount\":0,\"failedCount\":0,\"failureMessage\":null}}"u8.ToArray());
        yield return (CompatibilityData.Terminal(3, WorkerProtocolV1.FailedType, new FailedPayload("run-once", CompatibilityData.Start, CompatibilityData.Start, 0, 0, 0, 0, "failure")), () => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"failed\",\"sequence\":3,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"run-once\",\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\",\"endedAtUtc\":\"2026-08-29T12:00:00.0000000Z\",\"processedCount\":0,\"updatedCount\":0,\"skippedCount\":0,\"failedCount\":0,\"failureMessage\":\"failure\"}}"u8.ToArray());
    }

    [TestMethod]
    public void OriginalAndAdditiveFixtures_RetainAndCanonicalize()
    {
        var canonical = CompatibilityData.ReadFixture("canonical/ready.json");
        foreach (var path in new[] { "compatibility/original-ready.json", "compatibility/additive-ready-envelope.json", "compatibility/additive-ready-payload.json" })
        {
            var parsed = WorkerProtocolCodec.Parse(CompatibilityData.ReadFixture(path));
            Assert.IsTrue(parsed.IsSuccess, path + ": " + parsed.Failure?.Diagnostic);
            Assert.AreEqual(CompatibilityData.Ready(), parsed.Event);
            CollectionAssert.AreEqual(canonical, WorkerProtocolCodec.Serialize(parsed.Event!));
        }
    }
}

internal static class CompatibilityData
{
    internal static readonly DateTimeOffset Start = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    internal static readonly Guid RunId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    internal static readonly Guid ActivityId = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
    internal static byte[] ReadFixture(string relativePath) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "WorkerProtocol", "Compatibility", "Fixtures", "v1", relativePath));
    internal static WorkerProtocolEvent Ready(long sequence = 1) => new(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.ReadyType, sequence, Start, null, new ReadyPayload());
    internal static WorkerProtocolEvent Started(long sequence = 2, string trigger = "manual") => new(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.RunStartedType, sequence, Start, RunId, new RunStartedPayload(trigger, Start));
    internal static WorkerProtocolEvent Eligibility(long sequence = 3, long count = 3) => new(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.EligibilityDeterminedType, sequence, Start.AddSeconds(1), RunId, new EligibilityDeterminedPayload(count));
    internal static WorkerProtocolEvent Terminal(long sequence, string type, TerminalPayload payload) => new(WorkerProtocolV1.TerminalCategory, type, sequence, payload.EndedAtUtc, RunId, payload);
    internal static readonly (string Name, WorkerProtocolEvent Expected, Func<WorkerProtocolEvent> Create)[] CanonicalRows =
    [
        ("ready", Ready(), () => Ready()),
        ("run-started", Started(), () => Started()),
        ("eligibility-determined", Eligibility(), () => Eligibility()),
        ("progress-changed", new(WorkerProtocolV1.ProgressCategory, WorkerProtocolV1.ProgressChangedType, 4, Start.AddSeconds(2), RunId, new ProgressChangedPayload(3, 1, 1, 1)), () => new(WorkerProtocolV1.ProgressCategory, WorkerProtocolV1.ProgressChangedType, 4, Start.AddSeconds(2), RunId, new ProgressChangedPayload(3, 1, 1, 1))),
        ("activity-started", new(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityStartedType, 5, Start.AddSeconds(3), RunId, new ActivityStartedPayload(ActivityId, "cache download")), () => new(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityStartedType, 5, Start.AddSeconds(3), RunId, new ActivityStartedPayload(ActivityId, "cache download"))),
        ("activity-ended", new(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityEndedType, 6, Start.AddSeconds(4), RunId, new ActivityEndedPayload(ActivityId)), () => new(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityEndedType, 6, Start.AddSeconds(4), RunId, new ActivityEndedPayload(ActivityId))),
        ("log-emitted", new(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 7, Start.AddSeconds(5), RunId, new LogEmittedPayload("warning", "cache \"quoted\"")), () => new(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 7, Start.AddSeconds(5), RunId, new LogEmittedPayload("warning", "cache \"quoted\""))),
        ("completed", Terminal(8, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", Start, Start.AddSeconds(6), 3, 1, 1, 1)), () => Terminal(8, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", Start, Start.AddSeconds(6), 3, 1, 1, 1))),
        ("cancelled", Terminal(3, WorkerProtocolV1.CancelledType, new CancelledPayload("scheduled", Start, Start.AddSeconds(1), 0, 0, 0, 0)), () => Terminal(3, WorkerProtocolV1.CancelledType, new CancelledPayload("scheduled", Start, Start.AddSeconds(1), 0, 0, 0, 0))),
        ("failed", Terminal(3, WorkerProtocolV1.FailedType, new FailedPayload("run-once", Start, Start.AddSeconds(1), 0, 0, 0, 0, "controlled failure")), () => Terminal(3, WorkerProtocolV1.FailedType, new FailedPayload("run-once", Start, Start.AddSeconds(1), 0, 0, 0, 0, "controlled failure")))
    ];
    internal static IEnumerable<WorkerProtocolEvent> TokenVariants()
    {
        yield return Started(2, "manual"); yield return Started(2, "scheduled"); yield return Started(2, "run-once");
        foreach (var level in new[] { "trace", "information", "warning", "error" })
        {
            yield return new(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, 4, Start.AddSeconds(1), RunId, new LogEmittedPayload(level, "escaped \"quote\" \\ slash\nline"));
        }
        yield return Terminal(3, WorkerProtocolV1.CompletedType, new CompletedPayload("manual", Start, Start, 0, 0, 0, 0));
        yield return Terminal(3, WorkerProtocolV1.CancelledType, new CancelledPayload("scheduled", Start, Start, 0, 0, 0, 0));
        yield return Terminal(3, WorkerProtocolV1.FailedType, new FailedPayload("run-once", Start, Start, 0, 0, 0, 0, "failure"));
        yield return new(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.EligibilityDeterminedType, long.MaxValue, DateTimeOffset.MaxValue, RunId, new EligibilityDeterminedPayload(long.MaxValue));
    }
}
