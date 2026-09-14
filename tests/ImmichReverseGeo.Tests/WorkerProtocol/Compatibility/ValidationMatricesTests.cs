using System.Text;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol.Compatibility;

[TestClass]
public class ValidationMatricesTests
{
    [TestMethod]
    public void UnknownFieldsDuplicatesAndCanonicalRequiredNamesHaveExplicitOutcomes()
    {
        var ready = Text("canonical/ready.json");
        foreach (var addition in new[]
        {
            "\"futureScalar\":7", "\"futureNull\":null", "\"futureArray\":[7]", "\"futureObject\":{\"nested\":true}"
        })
        {
            AssertReady(ready.Replace("\"payload\":{}", "\"payload\":{" + addition + "}", StringComparison.Ordinal));
            AssertReady(ready.Replace("\"payload\":{}", addition + ",\"payload\":{}", StringComparison.Ordinal));
        }

        AssertReady(ready.Replace("\"protocol\":", "\"Protocol\":\"additive\",\"protocol\":", StringComparison.Ordinal));
        Reject(WorkerProtocolFailureCode.InvalidEnvelope, ready.Replace("\"protocol\":\"immich-reversegeo.worker\",", "", StringComparison.Ordinal), "ready:protocol:missing");
        Reject(WorkerProtocolFailureCode.InvalidEnvelope, ready.Replace("\"payload\":{}", "\"payload\":{\"future\":1,\"future\":2}", StringComparison.Ordinal), "ready:future:duplicate");
        Reject(WorkerProtocolFailureCode.InvalidEnvelope, ready.Replace("\"payload\":{}", "\"payload\":{\"trigger\":1,\"trigger\":2}", StringComparison.Ordinal), "ready:trigger:duplicate");
    }

    [TestMethod]
    public void EnvelopeMutationsAreOtherwiseValidAndHaveExactCategories()
    {
        var json = Text("canonical/ready.json");
        foreach (var row in new (string Label, string Candidate, WorkerProtocolFailureCode Code)[]
        {
            ("ready:protocol:missing", Remove(json, "protocol"), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:protocol:duplicate", Duplicate(json, "\"protocol\":\"immich-reversegeo.worker\""), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:protocol:wrong-kind", Replace(json, "\"protocol\":\"immich-reversegeo.worker\"", "\"protocol\":1"), WorkerProtocolFailureCode.InvalidEnvelope),
            ("ready:version:missing", Remove(json, "version"), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:version:duplicate", Duplicate(json, "\"version\":1"), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:version:wrong-kind", Replace(json, "\"version\":1", "\"version\":\"1\""), WorkerProtocolFailureCode.InvalidEnvelope),
            ("ready:direction:missing", Remove(json, "direction"), WorkerProtocolFailureCode.UnsupportedType), ("ready:direction:duplicate", Duplicate(json, "\"direction\":\"worker-to-controller\""), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:direction:wrong-kind", Replace(json, "\"direction\":\"worker-to-controller\"", "\"direction\":1"), WorkerProtocolFailureCode.UnsupportedType),
            ("ready:category:missing", Remove(json, "category"), WorkerProtocolFailureCode.UnsupportedType), ("ready:category:duplicate", Duplicate(json, "\"category\":\"lifecycle\""), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:category:wrong-kind", Replace(json, "\"category\":\"lifecycle\"", "\"category\":null"), WorkerProtocolFailureCode.UnsupportedType),
            ("ready:type:missing", Remove(json, "type"), WorkerProtocolFailureCode.UnsupportedType), ("ready:type:duplicate", Duplicate(json, "\"type\":\"ready\""), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:type:wrong-kind", Replace(json, "\"type\":\"ready\"", "\"type\":[]"), WorkerProtocolFailureCode.UnsupportedType),
            ("ready:sequence:missing", Remove(json, "sequence"), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:sequence:duplicate", Duplicate(json, "\"sequence\":1"), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:sequence:wrong-kind", Replace(json, "\"sequence\":1", "\"sequence\":null"), WorkerProtocolFailureCode.InvalidEnvelope),
            ("ready:timestampUtc:missing", Remove(json, "timestampUtc"), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:timestampUtc:duplicate", Duplicate(json, "\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\""), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:timestampUtc:wrong-kind", Replace(json, "\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"timestampUtc\":false"), WorkerProtocolFailureCode.InvalidEnvelope),
            ("ready:runId:missing", Remove(json, "runId"), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:runId:duplicate", Duplicate(json, "\"runId\":null"), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:runId:wrong-value", Replace(json, "\"runId\":null", "\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\""), WorkerProtocolFailureCode.InvalidPayload),
            ("ready:payload:missing", Remove(json, "payload"), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:payload:duplicate", Duplicate(json, "\"payload\":{}"), WorkerProtocolFailureCode.InvalidEnvelope), ("ready:payload:wrong-kind", Replace(json, "\"payload\":{}", "\"payload\":null"), WorkerProtocolFailureCode.InvalidEnvelope)
        })
        {
            Reject(row.Code, row.Candidate, row.Label);
        }
    }

    [TestMethod]
    public void EveryPayloadRequiredFieldHasMissingDuplicateWrongKindAndInvariantRows()
    {
        foreach (var row in PayloadRows())
        {
            var json = Text("canonical/" + row.Fixture + ".json");
            foreach (var field in row.Fields)
            {
                Reject(WorkerProtocolFailureCode.InvalidPayload, Remove(json, FieldName(field.Token)), $"{row.Fixture}:{FieldName(field.Token)}:missing");
                Reject(WorkerProtocolFailureCode.InvalidEnvelope, Duplicate(json, field.Token), $"{row.Fixture}:{FieldName(field.Token)}:duplicate");
                Reject(WorkerProtocolFailureCode.InvalidPayload, Replace(json, field.Token, field.WrongKind), $"{row.Fixture}:{FieldName(field.Token)}:wrong-kind");
            }
            foreach (var invariant in row.InvariantCandidates)
            {
                Reject(WorkerProtocolFailureCode.InvalidPayload, Replace(json, invariant.From, invariant.To), $"{row.Fixture}:{invariant.From}:invariant");
            }
        }
    }

    [TestMethod]
    public void DiscriminatorGuidTimestampAndIntegerFormsFailClosed()
    {
        var ready = Text("canonical/ready.json");
        foreach (var row in new (string Label, string From, string To, WorkerProtocolFailureCode Code)[]
        {
            ("ready:protocol:case", "immich-reversegeo.worker", "IMMICH-REVERSEGEO.WORKER", WorkerProtocolFailureCode.UnsupportedProtocol), ("ready:version:unsupported", "\"version\":1", "\"version\":2", WorkerProtocolFailureCode.UnsupportedVersion),
            ("ready:direction:case", "worker-to-controller", "Worker-to-controller", WorkerProtocolFailureCode.UnsupportedType), ("ready:category:unknown", "\"category\":\"lifecycle\"", "\"category\":\"unknown\"", WorkerProtocolFailureCode.UnsupportedType),
            ("ready:type:unknown", "\"type\":\"ready\"", "\"type\":\"unknown\"", WorkerProtocolFailureCode.UnsupportedType), ("ready:category:case", "\"category\":\"lifecycle\"", "\"category\":\"Lifecycle\"", WorkerProtocolFailureCode.UnsupportedType), ("ready:type:case", "\"type\":\"ready\"", "\"type\":\"Ready\"", WorkerProtocolFailureCode.UnsupportedType), ("ready:category:type-mismatch", "\"category\":\"lifecycle\",\"type\":\"ready\"", "\"category\":\"terminal\",\"type\":\"ready\"", WorkerProtocolFailureCode.UnsupportedType)
        })
        {
            Reject(row.Code, Replace(ready, row.From, row.To), row.Label);
        }
        foreach (var token in new[] { "\"version\":-1", "\"version\":1.0", "\"version\":1e0", "\"version\":9223372036854775808" })
        {
            Reject(WorkerProtocolFailureCode.InvalidEnvelope, Replace(ready, "\"version\":1", token), $"ready:version:{token}");
        }
        var started = Text("canonical/run-started.json");
        foreach (var id in new[] { "00000000-0000-0000-0000-000000000000", "01234567-89AB-cdef-0123-456789abcdef", "{01234567-89ab-cdef-0123-456789abcdef}", "0123456789abcdef0123456789abcdef", "bad-guid" })
        {
            Reject(WorkerProtocolFailureCode.InvalidEnvelope, Replace(started, CompatibilityData.RunId.ToString("D"), id), $"run-started:runId:{id}");
        }
        foreach (var stamp in new[] { "2026-08-29T12:00:00Z", "2026-08-29T12:00:00.0000000+00:00", "2026-08-29T12:00:00.0000000z", "2026-02-30T12:00:00.0000000Z", "2026-08-29T12:00:00.00000000Z" })
        {
            Reject(WorkerProtocolFailureCode.InvalidEnvelope, ReplaceExactlyOnce(started, "\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"timestampUtc\":\"" + stamp + "\""), $"run-started:timestampUtc:{stamp}");
            Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(started, "\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"startedAtUtc\":\"" + stamp + "\""), $"run-started:startedAtUtc:{stamp}");
        }
        foreach (var number in new[] { "-1", "\"1\"", "1.0", "1e0", "9223372036854775808", "-9223372036854775809" })
        {
            Reject(WorkerProtocolFailureCode.InvalidEnvelope, Replace(started, "\"sequence\":2", "\"sequence\":" + number), $"run-started:sequence:{number}");
        }
    }

    [TestMethod]
    public void EventPayloadIdentityAndTerminalChronologyInvariantsHaveSingleDefectRows()
    {
        var started = Text("canonical/run-started.json");
        Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(started, "\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\""), "run-started:timestampUtc:mismatch");
        Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(started, "\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\"", "\"runId\":null"), "run-started:runId:null");

        var completed = Text("canonical/completed.json");
        var chronologyOnly = ReplaceExactlyOnce(completed, "\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"startedAtUtc\":\"2026-08-29T12:00:07.0000000Z\"");
        Reject(WorkerProtocolFailureCode.InvalidPayload, chronologyOnly, "completed:chronology:started-after-ended");
        foreach (var terminal in new[] { "completed", "cancelled", "failed" })
        {
            var terminalJson = Text("canonical/" + terminal + ".json");
            var ended = terminal == "completed" ? "2026-08-29T12:00:06.0000000Z" : "2026-08-29T12:00:01.0000000Z";
            Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(terminalJson, "\"timestampUtc\":\"" + ended + "\"", "\"timestampUtc\":\"2026-08-29T12:00:02.0000000Z\""), $"{terminal}:timestampUtc:chronology");
        }

        var log = Text("canonical/log-emitted.json");
        Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(log, "\"message\":\"cache \\u0022quoted\\u0022\"", "\"message\":\" \""), "log-emitted:message:blank");
    }

    [TestMethod]
    public void ActivityIdentifiersRejectEveryNoncanonicalFormIndependentlyOfRunId()
    {
        foreach (var fixture in new[] { "activity-started", "activity-ended" })
        {
            var json = Text("canonical/" + fixture + ".json");
            foreach (var id in new[] { "FEDCBA98-7654-3210-FEDC-BA9876543210", "{fedcba98-7654-3210-fedc-ba9876543210}", "fedcba9876543210fedcba9876543210", "invalid", "00000000-0000-0000-0000-000000000000" })
            {
                Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(json, "\"activityId\":\"fedcba98-7654-3210-fedc-ba9876543210\"", "\"activityId\":\"" + id + "\""), $"{fixture}:activityId:{id}");
            }
            Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(json, "\"activityId\":\"fedcba98-7654-3210-fedc-ba9876543210\"", "\"activityId\":null"), $"{fixture}:activityId:null");
            Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(json, "\"activityId\":\"fedcba98-7654-3210-fedc-ba9876543210\"", "\"activityId\":1"), $"{fixture}:activityId:number");
        }
    }

    [TestMethod]
    public void CaseOnlyRequiredEnvelopeAndPayloadNamesFailClosed()
    {
        var ready = Text("canonical/ready.json");
        foreach (var name in new[] { "protocol", "version", "direction", "category", "type", "sequence", "timestampUtc", "runId", "payload" })
        {
            var expected = name is "direction" or "category" or "type" ? WorkerProtocolFailureCode.UnsupportedType : WorkerProtocolFailureCode.InvalidEnvelope;
            Reject(expected, ReplaceExactlyOnce(ready, "\"" + name + "\":", "\"" + char.ToUpperInvariant(name[0]) + name[1..] + "\":"), $"ready:{name}:case");
        }
        var started = Text("canonical/run-started.json");
        Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(started, "\"trigger\":", "\"Trigger\":"), "run-started:trigger:case");
        Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(started, "\"startedAtUtc\":", "\"StartedAtUtc\":"), "run-started:startedAtUtc:case");
        foreach (var row in PayloadRows())
        {
            var payload = Text("canonical/" + row.Fixture + ".json");
            foreach (var field in row.Fields)
            {
                var name = FieldName(field.Token);
                Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(payload, "\"" + name + "\":", "\"" + char.ToUpperInvariant(name[0]) + name[1..] + "\":"), $"{row.Fixture}:{name}:case");
            }
        }
    }

    [TestMethod]
    public void CountAndInt64PayloadFormsAreDirectlyAcceptedOrRejected()
    {
        var progress = Text("canonical/progress-changed.json");
        var zeroProgress = ReplaceExactlyOnce(ReplaceExactlyOnce(ReplaceExactlyOnce(ReplaceExactlyOnce(progress, "\"processedCount\":3", "\"processedCount\":0"), "\"updatedCount\":1", "\"updatedCount\":0"), "\"skippedCount\":1", "\"skippedCount\":0"), "\"failedCount\":1", "\"failedCount\":0");
        AssertAccepted(new WorkerProtocolEvent(WorkerProtocolV1.ProgressCategory, WorkerProtocolV1.ProgressChangedType, 4, CompatibilityData.Start.AddSeconds(2), CompatibilityData.RunId, new ProgressChangedPayload(0, 0, 0, 0)), zeroProgress);
        var negativeProgress = ReplaceExactlyOnce(ReplaceExactlyOnce(ReplaceExactlyOnce(ReplaceExactlyOnce(progress, "\"processedCount\":3", "\"processedCount\":-1"), "\"updatedCount\":1", "\"updatedCount\":-1"), "\"skippedCount\":1", "\"skippedCount\":0"), "\"failedCount\":1", "\"failedCount\":0");
        Reject(WorkerProtocolFailureCode.InvalidPayload, negativeProgress, "progress-changed:counts:negative");
        var maxProgress = ReplaceExactlyOnce(ReplaceExactlyOnce(ReplaceExactlyOnce(ReplaceExactlyOnce(progress, "\"processedCount\":3", "\"processedCount\":9223372036854775807"), "\"updatedCount\":1", "\"updatedCount\":9223372036854775807"), "\"skippedCount\":1", "\"skippedCount\":0"), "\"failedCount\":1", "\"failedCount\":0");
        AssertAccepted(new WorkerProtocolEvent(WorkerProtocolV1.ProgressCategory, WorkerProtocolV1.ProgressChangedType, 4, CompatibilityData.Start.AddSeconds(2), CompatibilityData.RunId, new ProgressChangedPayload(long.MaxValue, long.MaxValue, 0, 0)), maxProgress);
        AssertCountFailures(progress, "processedCount", "3"); AssertCountFailures(progress, "updatedCount", "1"); AssertCountFailures(progress, "skippedCount", "1"); AssertCountFailures(progress, "failedCount", "1");
        Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(progress, "\"processedCount\":3", "\"processedCount\":2"), "progress-changed:processedCount:sum-mismatch");

        var terminal = Text("canonical/completed.json");
        var zeroTerminal = ReplaceExactlyOnce(ReplaceExactlyOnce(ReplaceExactlyOnce(ReplaceExactlyOnce(terminal, "\"processedCount\":3", "\"processedCount\":0"), "\"updatedCount\":1", "\"updatedCount\":0"), "\"skippedCount\":1", "\"skippedCount\":0"), "\"failedCount\":1", "\"failedCount\":0");
        AssertAccepted(new WorkerProtocolEvent(WorkerProtocolV1.TerminalCategory, WorkerProtocolV1.CompletedType, 8, CompatibilityData.Start.AddSeconds(6), CompatibilityData.RunId, new CompletedPayload("manual", CompatibilityData.Start, CompatibilityData.Start.AddSeconds(6), 0, 0, 0, 0)), zeroTerminal);
        var negativeTerminal = ReplaceExactlyOnce(ReplaceExactlyOnce(ReplaceExactlyOnce(ReplaceExactlyOnce(terminal, "\"processedCount\":3", "\"processedCount\":-1"), "\"updatedCount\":1", "\"updatedCount\":-1"), "\"skippedCount\":1", "\"skippedCount\":0"), "\"failedCount\":1", "\"failedCount\":0");
        Reject(WorkerProtocolFailureCode.InvalidPayload, negativeTerminal, "completed:counts:negative");
        var maxTerminal = ReplaceExactlyOnce(ReplaceExactlyOnce(ReplaceExactlyOnce(ReplaceExactlyOnce(terminal, "\"processedCount\":3", "\"processedCount\":9223372036854775807"), "\"updatedCount\":1", "\"updatedCount\":9223372036854775807"), "\"skippedCount\":1", "\"skippedCount\":0"), "\"failedCount\":1", "\"failedCount\":0");
        AssertAccepted(new WorkerProtocolEvent(WorkerProtocolV1.TerminalCategory, WorkerProtocolV1.CompletedType, 8, CompatibilityData.Start.AddSeconds(6), CompatibilityData.RunId, new CompletedPayload("manual", CompatibilityData.Start, CompatibilityData.Start.AddSeconds(6), long.MaxValue, long.MaxValue, 0, 0)), maxTerminal);
        AssertCountFailures(terminal, "processedCount", "3"); AssertCountFailures(terminal, "updatedCount", "1"); AssertCountFailures(terminal, "skippedCount", "1"); AssertCountFailures(terminal, "failedCount", "1");
        Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(terminal, "\"processedCount\":3", "\"processedCount\":2"), "completed:processedCount:sum-mismatch");
    }

    [TestMethod]
    public void NullBlankAndInvalidTokenValuesHaveSingleFieldAuthority()
    {
        var ready = Text("canonical/ready.json");
        foreach (var token in new[] { "\"protocol\":\"immich-reversegeo.worker\"", "\"version\":1", "\"direction\":\"worker-to-controller\"", "\"category\":\"lifecycle\"", "\"type\":\"ready\"", "\"sequence\":1", "\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\"" })
        {
            var expected = token.Contains("direction", StringComparison.Ordinal) || token.Contains("category", StringComparison.Ordinal) || token.Contains("type", StringComparison.Ordinal) ? WorkerProtocolFailureCode.UnsupportedType : WorkerProtocolFailureCode.InvalidEnvelope;
            Reject(expected, ReplaceExactlyOnce(ready, token, token[..(token.IndexOf(':') + 1)] + "null"), $"ready:{FieldName(token)}:null");
        }
        foreach (var row in PayloadRows())
        {
            var json = Text("canonical/" + row.Fixture + ".json");
            foreach (var field in row.Fields)
            {
                if (FieldName(field.Token) == "failureMessage" && row.Fixture is "completed" or "cancelled")
                {
                    continue;
                }
                Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(json, field.Token, "\"" + FieldName(field.Token) + "\":null"), $"{row.Fixture}:{FieldName(field.Token)}:null");
            }
        }
        foreach (var fixture in new[] { "run-started", "completed", "cancelled", "failed" })
        {
            var json = Text("canonical/" + fixture + ".json");
            var trigger = fixture == "cancelled" ? "scheduled" : fixture == "failed" ? "run-once" : "manual";
            Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(json, "\"trigger\":\"" + trigger + "\"", "\"trigger\":\" \""), $"{fixture}:trigger:blank");
            Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(json, "\"trigger\":\"" + trigger + "\"", "\"trigger\":\"invalid\""), $"{fixture}:trigger:invalid");
        }
        var log = Text("canonical/log-emitted.json");
        Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(log, "\"level\":\"warning\"", "\"level\":\" \""), "log-emitted:level:blank");
        Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(log, "\"level\":\"warning\"", "\"level\":\"invalid\""), "log-emitted:level:invalid");
        Reject(WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(log, "\"message\":\"cache \\u0022quoted\\u0022\"", "\"message\":\" \""), "log-emitted:message:blank");
    }

    private static IEnumerable<(string Fixture, (string Token, string WrongKind)[] Fields, (string From, string To)[] InvariantCandidates)> PayloadRows()
    {
        yield return ("run-started", [("\"trigger\":\"manual\"", "\"trigger\":1"), ("\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"startedAtUtc\":1")], [("\"trigger\":\"manual\"", "\"trigger\":\"Manual\"")]);
        yield return ("eligibility-determined", [("\"eligibleCount\":3", "\"eligibleCount\":\"3\"")], [("\"eligibleCount\":3", "\"eligibleCount\":-1")]);
        yield return ("progress-changed", [("\"processedCount\":3", "\"processedCount\":\"3\""), ("\"updatedCount\":1", "\"updatedCount\":\"1\""), ("\"skippedCount\":1", "\"skippedCount\":\"1\""), ("\"failedCount\":1", "\"failedCount\":\"1\"")], [("\"processedCount\":3", "\"processedCount\":4")]);
        yield return ("activity-started", [("\"activityId\":\"fedcba98-7654-3210-fedc-ba9876543210\"", "\"activityId\":1"), ("\"label\":\"cache download\"", "\"label\":1")], [("\"label\":\"cache download\"", "\"label\":\" \"")]);
        yield return ("activity-ended", [("\"activityId\":\"fedcba98-7654-3210-fedc-ba9876543210\"", "\"activityId\":1")], [("fedcba98-7654-3210-fedc-ba9876543210", "00000000-0000-0000-0000-000000000000")]);
        yield return ("log-emitted", [("\"level\":\"warning\"", "\"level\":1"), ("\"message\":\"cache \\u0022quoted\\u0022\"", "\"message\":1")], [("\"level\":\"warning\"", "\"level\":\"Warning\"")]);
        foreach (var terminal in new[] { "completed", "cancelled", "failed" })
        {
            yield return (terminal, [("\"trigger\":\"" + (terminal == "cancelled" ? "scheduled" : terminal == "failed" ? "run-once" : "manual") + "\"", "\"trigger\":1"), ("\"startedAtUtc\":\"2026-08-29T12:00:00.0000000Z\"", "\"startedAtUtc\":1"), ("\"endedAtUtc\":\"2026-08-29T12:00:0" + (terminal == "completed" ? "6" : "1") + ".0000000Z\"", "\"endedAtUtc\":1"), ("\"processedCount\":" + (terminal == "completed" ? "3" : "0"), "\"processedCount\":\"0\""), ("\"updatedCount\":" + (terminal == "completed" ? "1" : "0"), "\"updatedCount\":\"0\""), ("\"skippedCount\":" + (terminal == "completed" ? "1" : "0"), "\"skippedCount\":\"0\""), ("\"failedCount\":" + (terminal == "completed" ? "1" : "0"), "\"failedCount\":\"0\""), (terminal == "failed" ? "\"failureMessage\":\"controlled failure\"" : "\"failureMessage\":null", "\"failureMessage\":1")], [(terminal == "failed" ? "\"failureMessage\":\"controlled failure\"" : "\"failureMessage\":null", terminal == "failed" ? "\"failureMessage\":\" \"" : "\"failureMessage\":\"unexpected\"")]);
        }
    }

    private static void AssertCountFailures(string json, string name, string value)
    {
        foreach (var invalid in new[] { "-1", "9223372036854775808", "-9223372036854775809", "\"1\"", "1.0", "1e0", "01" })
        {
            Reject(invalid == "01" ? WorkerProtocolFailureCode.MalformedJson : WorkerProtocolFailureCode.InvalidPayload, ReplaceExactlyOnce(json, "\"" + name + "\":" + value, "\"" + name + "\":" + invalid), $"count:{name}:{invalid}");
        }
    }
    private static void AssertAccepted(WorkerProtocolEvent expected, string json)
    {
        var result = WorkerProtocolCodec.Parse(Encoding.UTF8.GetBytes(json));
        Assert.IsTrue(result.IsSuccess, result.Failure?.Diagnostic);
        Assert.IsNull(result.Failure);
        Assert.AreEqual(expected, result.Event);
    }
    private static string FieldName(string token) => token[1..token.IndexOf('"', 1)];
    private static string Text(string fixture) => Encoding.UTF8.GetString(CompatibilityData.ReadFixture(fixture));
    private static string Replace(string value, string from, string to) => value.Replace(from, to, StringComparison.Ordinal);
    private static string ReplaceExactlyOnce(string value, string from, string to)
    {
        var first = value.IndexOf(from, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0, "The qualified source property must exist.");
        Assert.AreEqual(first, value.LastIndexOf(from, StringComparison.Ordinal), "The qualified source property must occur exactly once.");
        return value[..first] + to + value[(first + from.Length)..];
    }
    private static string Duplicate(string value, string token) => value.Replace(token, token + "," + token, StringComparison.Ordinal);
    // Raw lexical rename leaves the candidate otherwise byte-for-byte valid while making the required canonical name absent.
    private static string Remove(string value, string name) => value.Replace("\"" + name + "\":", "\"" + name + "Missing\":", StringComparison.Ordinal);
    private static void AssertReady(string json) { var result = WorkerProtocolCodec.Parse(Encoding.UTF8.GetBytes(json)); Assert.IsTrue(result.IsSuccess, result.Failure?.Diagnostic); Assert.AreEqual(CompatibilityData.Ready(), result.Event); }
    private static void Reject(WorkerProtocolFailureCode expected, string json, string caseLabel) { Assert.IsFalse(string.IsNullOrWhiteSpace(caseLabel), "A case identity is required."); var result = WorkerProtocolCodec.Parse(Encoding.UTF8.GetBytes(json)); Assert.IsFalse(result.IsSuccess, caseLabel); Assert.IsNull(result.Event, caseLabel); Assert.IsNotNull(result.Failure, caseLabel); Assert.AreEqual(expected, result.Failure.Code, caseLabel); Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Diagnostic), caseLabel); Assert.IsTrue(result.Failure.Diagnostic.Length <= 256, caseLabel); }
}
