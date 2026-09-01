using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ImmichReverseGeo.Core.WorkerProtocol;

public static class WorkerProtocolCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Serialize(WorkerProtocolEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("protocol", WorkerProtocolV1.Protocol);
            writer.WriteNumber("version", WorkerProtocolV1.Version);
            writer.WriteString("direction", WorkerProtocolV1.Direction);
            writer.WriteString("category", @event.Category);
            writer.WriteString("type", @event.Type);
            writer.WriteNumber("sequence", @event.Sequence);
            writer.WriteString("timestampUtc", WorkerProtocolV1.FormatTimestamp(@event.TimestampUtc));
            if (@event.RunId is null)
            {
                writer.WriteNull("runId");
            }
            else
            {
                writer.WriteString("runId", @event.RunId.Value.ToString("D"));
            }

            writer.WritePropertyName("payload");
            WritePayload(writer, @event.Payload);
            writer.WriteEndObject();
        }

        var message = buffer.WrittenSpan.ToArray();
        if (message.Length > WorkerProtocolV1.MaxMessageBytes)
        {
            throw new ArgumentException("The serialized protocol message exceeds the configured byte limit.", nameof(@event));
        }

        return message;
    }

    public static WorkerProtocolParseResult Parse(ReadOnlySpan<byte> frame)
    {
        if (!TryGetContent(frame, out var content, out var framingFailure))
        {
            return framingFailure!;
        }

        if (content.Length > WorkerProtocolV1.MaxMessageBytes)
        {
            return Fail(WorkerProtocolFailureCode.MessageTooLarge, "Message exceeds the configured byte limit.");
        }

        if (content.Length == 0)
        {
            return Fail(WorkerProtocolFailureCode.InvalidFraming, "Message content must not be empty.");
        }

        if (content.Length >= 3 && content[0] == 0xef && content[1] == 0xbb && content[2] == 0xbf)
        {
            return Fail(WorkerProtocolFailureCode.InvalidEncoding, "UTF-8 byte-order marks are not permitted.");
        }

        foreach (var value in content)
        {
            if (value is (byte)'\r' or (byte)'\n')
            {
                return Fail(WorkerProtocolFailureCode.InvalidFraming, "Message content must be one line.");
            }
        }

        try
        {
            _ = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Fail(WorkerProtocolFailureCode.InvalidEncoding, "Message is not valid UTF-8.");
        }

        try
        {
            using var document = JsonDocument.Parse(content.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Fail(WorkerProtocolFailureCode.InvalidEnvelope, "The envelope must be a JSON object.");
            }

            if (HasDuplicateProperties(document.RootElement))
            {
                return Fail(WorkerProtocolFailureCode.InvalidEnvelope, "Duplicate JSON properties are not permitted.");
            }

            return ParseEnvelope(document.RootElement);
        }
        catch (JsonException)
        {
            return Fail(WorkerProtocolFailureCode.MalformedJson, "Message is not valid JSON.");
        }
    }

    private static WorkerProtocolParseResult ParseEnvelope(JsonElement envelope)
    {
        if (!TryString(envelope, "protocol", out var protocol))
        {
            return Fail(WorkerProtocolFailureCode.InvalidEnvelope, "Envelope protocol is required.");
        }

        if (protocol != WorkerProtocolV1.Protocol)
        {
            return Fail(WorkerProtocolFailureCode.UnsupportedProtocol, "Protocol identifier is not supported.");
        }

        if (!TryInteger(envelope, "version", out var version))
        {
            return Fail(WorkerProtocolFailureCode.InvalidEnvelope, "Envelope version is required.");
        }

        if (version != WorkerProtocolV1.Version)
        {
            return Fail(WorkerProtocolFailureCode.UnsupportedVersion, "Protocol version is not supported.");
        }

        if (!TryString(envelope, "direction", out var direction) || direction != WorkerProtocolV1.Direction)
        {
            return Fail(WorkerProtocolFailureCode.UnsupportedType, "Direction is not supported.");
        }

        if (!TryString(envelope, "category", out var category) || !TryString(envelope, "type", out var type) || !WorkerProtocolV1.IsKnown(category, type))
        {
            return Fail(WorkerProtocolFailureCode.UnsupportedType, "Category and type are not supported.");
        }

        if (!TryInteger(envelope, "sequence", out var sequence) || sequence < 1)
        {
            return Fail(WorkerProtocolFailureCode.InvalidEnvelope, "Sequence must be a positive canonical integer.");
        }

        if (!TryTimestamp(envelope, "timestampUtc", out var timestampUtc))
        {
            return Fail(WorkerProtocolFailureCode.InvalidEnvelope, "Timestamp must be canonical UTC text.");
        }

        if (!envelope.TryGetProperty("runId", out var runIdElement) || !TryRunId(runIdElement, out var runId))
        {
            return Fail(WorkerProtocolFailureCode.InvalidEnvelope, "Run ID must be null or a canonical GUID.");
        }

        if (!envelope.TryGetProperty("payload", out var payloadElement) || payloadElement.ValueKind != JsonValueKind.Object)
        {
            return Fail(WorkerProtocolFailureCode.InvalidEnvelope, "Payload must be an object.");
        }

        try
        {
            var payload = ParsePayload(type, payloadElement);
            return WorkerProtocolParseResult.Success(new WorkerProtocolEvent(category, type, sequence, timestampUtc, runId, payload));
        }
        catch (ArgumentException)
        {
            return Fail(WorkerProtocolFailureCode.InvalidPayload, "Payload values violate a protocol invariant.");
        }
        catch (OverflowException)
        {
            return Fail(WorkerProtocolFailureCode.InvalidPayload, "Payload values violate a protocol invariant.");
        }
    }

    private static WorkerProtocolPayload ParsePayload(string type, JsonElement payload)
    {
        return type switch
        {
            WorkerProtocolV1.ReadyType => new ReadyPayload(),
            WorkerProtocolV1.RunStartedType when TryString(payload, "trigger", out var trigger) && TryTimestamp(payload, "startedAtUtc", out var startedAtUtc) => new RunStartedPayload(trigger, startedAtUtc),
            WorkerProtocolV1.EligibilityDeterminedType when TryInteger(payload, "eligibleCount", out var eligibleCount) => new EligibilityDeterminedPayload(eligibleCount),
            WorkerProtocolV1.ProgressChangedType when TryCounts(payload, out var progressCounts) => new ProgressChangedPayload(progressCounts.Processed, progressCounts.Updated, progressCounts.Skipped, progressCounts.Failed),
            WorkerProtocolV1.ActivityStartedType when TryGuid(payload, "activityId", out var startedId) && TryString(payload, "label", out var label) => new ActivityStartedPayload(startedId, label),
            WorkerProtocolV1.ActivityEndedType when TryGuid(payload, "activityId", out var endedId) => new ActivityEndedPayload(endedId),
            WorkerProtocolV1.LogEmittedType when TryString(payload, "level", out var level) && TryString(payload, "message", out var message) => new LogEmittedPayload(level, message),
            WorkerProtocolV1.CompletedType when TryTerminalValues(payload, out var completed) => new CompletedPayload(completed.Trigger, completed.Started, completed.Ended, completed.Processed, completed.Updated, completed.Skipped, completed.Failed, completed.FailureMessage),
            WorkerProtocolV1.CancelledType when TryTerminalValues(payload, out var cancelled) => new CancelledPayload(cancelled.Trigger, cancelled.Started, cancelled.Ended, cancelled.Processed, cancelled.Updated, cancelled.Skipped, cancelled.Failed, cancelled.FailureMessage),
            WorkerProtocolV1.FailedType when TryTerminalValues(payload, out var failed) && failed.FailureMessage is not null => new FailedPayload(failed.Trigger, failed.Started, failed.Ended, failed.Processed, failed.Updated, failed.Skipped, failed.Failed, failed.FailureMessage),
            _ => throw new ArgumentException("The payload is missing a required property.")
        };
    }

    private static void WritePayload(Utf8JsonWriter writer, WorkerProtocolPayload payload)
    {
        writer.WriteStartObject();
        switch (payload)
        {
            case ReadyPayload:
                break;
            case RunStartedPayload started:
                writer.WriteString("trigger", started.Trigger);
                writer.WriteString("startedAtUtc", WorkerProtocolV1.FormatTimestamp(started.StartedAtUtc));
                break;
            case EligibilityDeterminedPayload eligibility:
                writer.WriteNumber("eligibleCount", eligibility.EligibleCount);
                break;
            case ProgressChangedPayload progress:
                WriteCounts(writer, progress.ProcessedCount, progress.UpdatedCount, progress.SkippedCount, progress.FailedCount);
                break;
            case ActivityStartedPayload activityStarted:
                writer.WriteString("activityId", activityStarted.ActivityId.ToString("D"));
                writer.WriteString("label", activityStarted.Label);
                break;
            case ActivityEndedPayload activityEnded:
                writer.WriteString("activityId", activityEnded.ActivityId.ToString("D"));
                break;
            case LogEmittedPayload log:
                writer.WriteString("level", log.Level);
                writer.WriteString("message", log.Message);
                break;
            case TerminalPayload terminal:
                writer.WriteString("trigger", terminal.Trigger);
                writer.WriteString("startedAtUtc", WorkerProtocolV1.FormatTimestamp(terminal.StartedAtUtc));
                writer.WriteString("endedAtUtc", WorkerProtocolV1.FormatTimestamp(terminal.EndedAtUtc));
                WriteCounts(writer, terminal.ProcessedCount, terminal.UpdatedCount, terminal.SkippedCount, terminal.FailedCount);
                if (terminal.FailureMessage is null)
                {
                    writer.WriteNull("failureMessage");
                }
                else
                {
                    writer.WriteString("failureMessage", terminal.FailureMessage);
                }
                break;
            default:
                throw new ArgumentException("The payload is not supported by protocol v1.", nameof(payload));
        }

        writer.WriteEndObject();
    }

    private static void WriteCounts(Utf8JsonWriter writer, long processed, long updated, long skipped, long failed)
    {
        writer.WriteNumber("processedCount", processed);
        writer.WriteNumber("updatedCount", updated);
        writer.WriteNumber("skippedCount", skipped);
        writer.WriteNumber("failedCount", failed);
    }

    private static bool TryGetContent(ReadOnlySpan<byte> frame, out ReadOnlySpan<byte> content, out WorkerProtocolParseResult? failure)
    {
        content = frame;
        failure = null;
        if (content.Length > 0 && content[^1] == (byte)'\n')
        {
            content = content[..^1];
            if (content.Length > 0 && content[^1] == (byte)'\r')
            {
                content = content[..^1];
            }
        }
        else if (content.Length > 0 && content[^1] == (byte)'\r')
        {
            failure = Fail(WorkerProtocolFailureCode.InvalidFraming, "A bare carriage return is not a valid delimiter.");
            return false;
        }

        return true;
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && (value = property.GetString()!) is not null;
    }

    private static bool TryNullableString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    private static bool TryInteger(JsonElement element, string name, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var raw = property.GetRawText();
        if (raw.Length == 0 || raw[0] == '-' || (raw.Length > 1 && raw[0] == '0'))
        {
            return false;
        }

        foreach (var character in raw)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryTimestamp(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        return TryString(element, name, out var text)
            && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value)
            && WorkerProtocolV1.FormatTimestamp(value) == text;
    }

    private static bool TryRunId(JsonElement element, out Guid? runId)
    {
        runId = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String || !TryCanonicalGuid(element.GetString(), out var parsed))
        {
            return false;
        }

        runId = parsed;
        return true;
    }

    private static bool TryGuid(JsonElement element, string name, out Guid value)
    {
        value = Guid.Empty;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && TryCanonicalGuid(property.GetString(), out value);
    }

    private static bool TryCanonicalGuid(string? text, out Guid value)
    {
        value = Guid.Empty;
        return text is not null && Guid.TryParseExact(text, "D", out value) && value != Guid.Empty && value.ToString("D") == text;
    }

    private static bool TryCounts(JsonElement payload, out (long Processed, long Updated, long Skipped, long Failed) counts)
    {
        counts = default;
        return TryInteger(payload, "processedCount", out counts.Processed)
            && TryInteger(payload, "updatedCount", out counts.Updated)
            && TryInteger(payload, "skippedCount", out counts.Skipped)
            && TryInteger(payload, "failedCount", out counts.Failed);
    }

    private static bool TryTerminalValues(JsonElement payload, out (string Trigger, DateTimeOffset Started, DateTimeOffset Ended, long Processed, long Updated, long Skipped, long Failed, string? FailureMessage) values)
    {
        values = default;
        return TryString(payload, "trigger", out values.Trigger)
            && TryTimestamp(payload, "startedAtUtc", out values.Started)
            && TryTimestamp(payload, "endedAtUtc", out values.Ended)
            && TryCounts(payload, out var counts)
            && TryNullableString(payload, "failureMessage", out values.FailureMessage)
            && AssignCounts(ref values, counts);
    }

    private static bool AssignCounts(ref (string Trigger, DateTimeOffset Started, DateTimeOffset Ended, long Processed, long Updated, long Skipped, long Failed, string? FailureMessage) values, (long Processed, long Updated, long Skipped, long Failed) counts)
    {
        values.Processed = counts.Processed;
        values.Updated = counts.Updated;
        values.Skipped = counts.Skipped;
        values.Failed = counts.Failed;
        return true;
    }

    private static WorkerProtocolParseResult Fail(WorkerProtocolFailureCode code, string diagnostic) => WorkerProtocolParseResult.Failed(code, diagnostic);
}
