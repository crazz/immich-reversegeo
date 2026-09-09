using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Core.WorkerJobs;

public static class WorkerJobProtocolCodec
{
    private const string DiagnosticTruncationMarker = "…";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions CoordinateJson = CreateCoordinateJsonOptions();

    private static JsonSerializerOptions CreateCoordinateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new CanonicalKebabCaseEnumConverter<CoordinateLookupTieBreak>());
        options.Converters.Add(new CanonicalKebabCaseEnumConverter<CoordinateLookupCountryStatus>());
        options.Converters.Add(new CanonicalKebabCaseEnumConverter<CoordinateLookupSourceState>());
        options.Converters.Add(new CanonicalKebabCaseEnumConverter<CoordinateLookupFinalSource>());
        options.Converters.Add(new CanonicalKebabCaseEnumConverter<WorkerJobFailureCategory>());
        return options;
    }

    public static byte[] SerializeControllerInput(WorkerJobControllerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteEnvelopeStart(
                writer,
                WorkerJobProtocolV2.ControllerToWorkerDirection,
                message.Category,
                message.Type,
                message.Sequence,
                message.TimestampUtc,
                message.JobId,
                message.JobKind);
            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            switch (message.Payload)
            {
                case ProcessAssetsExecutePayload execute:
                    writer.WriteString(
                        "trigger",
                        WorkerProtocolConversions.Trigger(
                            execute.Request.ProcessingRequest.Trigger));
                    break;
                case CoordinateLookupExecutePayload execute:
                    WriteCoordinateLookupRequest(writer, execute.Request);
                    break;
                case CacheMutationExecutePayload execute:
                    writer.WriteString("source", FormatCacheMutationSource(execute.Request.Source));
                    writer.WriteString("operation", FormatCacheMutationOperation(execute.Request.Operation));
                    writer.WriteString("iso3", execute.Request.Iso3);
                    break;
                case WorkerJobCancelPayload:
                    break;
                default:
                    throw new ArgumentException("The controller payload is not supported by protocol v2.", nameof(message));
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return EnforceSize(buffer.WrittenSpan, nameof(message));
    }

    public static byte[] Serialize(WorkerJobOutputMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        bool hasDiagnostic = TryGetDiagnosticText(message.Payload, out string? diagnostic);
        if (hasDiagnostic
            && diagnostic!.Length > WorkerJobProtocolV2.MaxMessageBytes)
        {
            return SerializeBoundedDiagnostic(message, diagnostic);
        }

        byte[] serialized = SerializeOutput(message);
        if (serialized.Length <= WorkerJobProtocolV2.MaxMessageBytes)
        {
            return serialized;
        }

        return !hasDiagnostic
            ? EnforceSize(serialized, nameof(message))
            : SerializeBoundedDiagnostic(message, diagnostic!);
    }

    private static byte[] SerializeOutput(WorkerJobOutputMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteEnvelopeStart(
                writer,
                WorkerJobProtocolV2.WorkerToControllerDirection,
                message.Category,
                message.Type,
                message.Sequence,
                message.TimestampUtc,
                message.JobId,
                message.JobKind);
            writer.WritePropertyName("payload");
            WriteOutputPayload(writer, message.Payload);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] SerializeBoundedDiagnostic(
        WorkerJobOutputMessage message,
        string diagnostic)
    {
        byte[]? best = null;
        var lower = 0;
        var upper = Math.Min(diagnostic.Length, WorkerJobProtocolV2.MaxMessageBytes);
        while (lower <= upper)
        {
            int requestedLength = lower + ((upper - lower) / 2);
            int prefixLength = UnicodePrefixLength(diagnostic, requestedLength);
            string bounded = string.Concat(
                diagnostic.AsSpan(0, prefixLength),
                DiagnosticTruncationMarker);
            byte[] candidate = SerializeOutput(WithDiagnosticText(message, bounded));
            if (candidate.Length <= WorkerJobProtocolV2.MaxMessageBytes)
            {
                best = candidate;
                lower = requestedLength + 1;
            }
            else
            {
                upper = requestedLength - 1;
            }
        }

        return best
            ?? throw new ArgumentException(
                "The serialized protocol message exceeds the configured byte limit.",
                nameof(message));
    }

    private static int UnicodePrefixLength(string value, int requestedLength)
    {
        if (requestedLength > 0
            && requestedLength < value.Length
            && char.IsHighSurrogate(value[requestedLength - 1])
            && char.IsLowSurrogate(value[requestedLength]))
        {
            return requestedLength - 1;
        }

        return requestedLength;
    }

    private static bool TryGetDiagnosticText(
        WorkerJobOutputPayload payload,
        out string? diagnostic)
    {
        diagnostic = payload switch
        {
            WorkerJobActivityStartedPayload activity => activity.Label,
            WorkerJobLogPayload log => log.Message,
            _ => null
        };
        return diagnostic is not null;
    }

    private static WorkerJobOutputMessage WithDiagnosticText(
        WorkerJobOutputMessage message,
        string diagnostic)
    {
        WorkerJobOutputPayload payload = message.Payload switch
        {
            WorkerJobActivityStartedPayload activity =>
                new WorkerJobActivityStartedPayload(activity.ActivityId, diagnostic),
            WorkerJobLogPayload log => new WorkerJobLogPayload(log.Level, diagnostic),
            _ => throw new ArgumentException(
                "Only diagnostic payloads can be transport-normalized.",
                nameof(message))
        };
        return new WorkerJobOutputMessage(
            message.Category,
            message.Type,
            message.Sequence,
            message.TimestampUtc,
            message.JobId,
            message.JobKind,
            payload);
    }

    public static WorkerJobControllerParseResult ParseControllerInput(ReadOnlySpan<byte> frame) =>
        ParseFrame(
            frame,
            ParseControllerEnvelope,
            WorkerJobControllerParseResult.Failed);

    public static WorkerJobProtocolParseResult Parse(ReadOnlySpan<byte> frame) =>
        ParseFrame(
            frame,
            ParseOutputEnvelope,
            WorkerJobProtocolParseResult.Failed);

    private static T ParseFrame<T>(
        ReadOnlySpan<byte> frame,
        Func<JsonElement, T> parseEnvelope,
        Func<WorkerProtocolFailureCode, string, T> fail)
    {
        if (!TryGetContent(frame, out var content, out var framingFailure))
        {
            return fail(framingFailure!.Code, framingFailure.Diagnostic);
        }

        if (content.Length > WorkerJobProtocolV2.MaxMessageBytes)
        {
            return fail(WorkerProtocolFailureCode.MessageTooLarge, "Message exceeds the configured byte limit.");
        }

        if (content.Length == 0)
        {
            return fail(WorkerProtocolFailureCode.InvalidFraming, "Message content must not be empty.");
        }

        if (content.Length >= 3 && content[0] == 0xef && content[1] == 0xbb && content[2] == 0xbf)
        {
            return fail(WorkerProtocolFailureCode.InvalidEncoding, "UTF-8 byte-order marks are not permitted.");
        }

        foreach (var value in content)
        {
            if (value is (byte)'\r' or (byte)'\n')
            {
                return fail(WorkerProtocolFailureCode.InvalidFraming, "Message content must be one line.");
            }
        }

        try
        {
            _ = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return fail(WorkerProtocolFailureCode.InvalidEncoding, "Message is not valid UTF-8.");
        }

        try
        {
            using var document = JsonDocument.Parse(content.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return fail(WorkerProtocolFailureCode.InvalidEnvelope, "The envelope must be a JSON object.");
            }

            if (HasDuplicateProperties(document.RootElement))
            {
                return fail(WorkerProtocolFailureCode.InvalidEnvelope, "Duplicate JSON properties are not permitted.");
            }

            return parseEnvelope(document.RootElement);
        }
        catch (JsonException)
        {
            return fail(WorkerProtocolFailureCode.MalformedJson, "Message is not valid JSON.");
        }
    }

    private static WorkerJobControllerParseResult ParseControllerEnvelope(JsonElement envelope)
    {
        if (!TryEnvelope(
                envelope,
                WorkerJobProtocolV2.ControllerToWorkerDirection,
                out var category,
                out var type,
                out var sequence,
                out var timestamp,
                out var jobId,
                out var jobKind,
                out var payload,
                out var failure))
        {
            return WorkerJobControllerParseResult.Failed(failure!.Code, failure.Diagnostic);
        }

        if ((category, type) is not
            (WorkerJobProtocolV2.RequestCategory, WorkerJobProtocolV2.ExecuteType) and not
            (WorkerJobProtocolV2.ControlCategory, WorkerJobProtocolV2.CancelType))
        {
            return WorkerJobControllerParseResult.Failed(
                WorkerProtocolFailureCode.UnsupportedType,
                "Category and type are not supported.");
        }

        if (jobId is null || jobKind is null)
        {
            return WorkerJobControllerParseResult.Failed(
                WorkerProtocolFailureCode.InvalidCorrelation,
                "Controller messages require a job identity and kind.");
        }

        try
        {
            WorkerJobControllerPayload typedPayload;
            if (type == WorkerJobProtocolV2.ExecuteType)
            {
                if (jobKind == WorkerJobKind.ProcessAssets
                    && HasExactProperties(payload, "trigger")
                    && TryString(payload, "trigger", out var trigger)
                    && WorkerProtocolConversions.TryTrigger(trigger, out var processingTrigger))
                {
                    typedPayload = new ProcessAssetsExecutePayload(
                        new ProcessAssetsRequest(
                            new ProcessingRunRequest(jobId.Value, processingTrigger)));
                }
                else if (jobKind == WorkerJobKind.CoordinateLookup
                    && TryCoordinateLookupRequest(payload, out CoordinateLookupRequest? request))
                {
                    typedPayload = new CoordinateLookupExecutePayload(request!);
                }
                else if (jobKind == WorkerJobKind.CacheMutation
                    && HasExactProperties(payload, "source", "operation", "iso3")
                    && TryString(payload, "source", out var sourceText)
                    && TryCacheMutationSource(sourceText, out var source)
                    && TryString(payload, "operation", out var operationText)
                    && TryCacheMutationOperation(operationText, out var operation)
                    && TryString(payload, "iso3", out var iso3))
                {
                    typedPayload = new CacheMutationExecutePayload(
                        new CacheMutationRequest(source, operation, iso3));
                }
                else
                {
                    return WorkerJobControllerParseResult.Failed(
                        WorkerProtocolFailureCode.InvalidPayload,
                        "The execute payload does not match the job kind.");
                }
            }
            else
            {
                if (!HasExactProperties(payload))
                {
                    return WorkerJobControllerParseResult.Failed(
                        WorkerProtocolFailureCode.InvalidPayload,
                        "The cancel payload must be empty.");
                }

                typedPayload = new WorkerJobCancelPayload();
            }

            return WorkerJobControllerParseResult.Success(
                new WorkerJobControllerMessage(
                    category,
                    type,
                    sequence,
                    timestamp,
                    jobId.Value,
                    jobKind.Value,
                    typedPayload));
        }
        catch (ArgumentException)
        {
            return WorkerJobControllerParseResult.Failed(
                WorkerProtocolFailureCode.InvalidPayload,
                "Payload values violate a protocol invariant.");
        }
    }

    private static WorkerJobProtocolParseResult ParseOutputEnvelope(JsonElement envelope)
    {
        if (!TryEnvelope(
                envelope,
                WorkerJobProtocolV2.WorkerToControllerDirection,
                out var category,
                out var type,
                out var sequence,
                out var timestamp,
                out var jobId,
                out var jobKind,
                out var payload,
                out var failure))
        {
            return WorkerJobProtocolParseResult.Failed(failure!.Code, failure.Diagnostic);
        }

        if (!WorkerJobProtocolV2.IsOutputType(category, type))
        {
            return WorkerJobProtocolParseResult.Failed(
                WorkerProtocolFailureCode.UnsupportedType,
                "Category and type are not supported.");
        }

        try
        {
            WorkerJobOutputPayload typedPayload = ParseOutputPayload(
                type,
                timestamp,
                jobKind,
                payload);
            return WorkerJobProtocolParseResult.Success(
                new WorkerJobOutputMessage(
                    category,
                    type,
                    sequence,
                    timestamp,
                    jobId,
                    jobKind,
                    typedPayload));
        }
        catch (ArgumentException)
        {
            return WorkerJobProtocolParseResult.Failed(
                WorkerProtocolFailureCode.InvalidPayload,
                "Payload values violate a protocol invariant.");
        }
        catch (OverflowException)
        {
            return WorkerJobProtocolParseResult.Failed(
                WorkerProtocolFailureCode.InvalidPayload,
                "Payload values violate a protocol invariant.");
        }
    }

    private static WorkerJobOutputPayload ParseOutputPayload(
        string type,
        DateTimeOffset timestamp,
        WorkerJobKind? jobKind,
        JsonElement payload)
    {
        return type switch
        {
            WorkerJobProtocolV2.ReadyType when
                jobKind is null
                && HasExactProperties(payload, "supportedJobKinds")
                && TryKinds(payload, out var kinds) =>
                    new WorkerJobReadyPayload(kinds),
            WorkerJobProtocolV2.JobStartedType when
                HasExactProperties(payload, "trigger", "startedAtUtc")
                && TryString(payload, "trigger", out var trigger)
                && TryTimestamp(payload, "startedAtUtc", out var started) =>
                    new WorkerJobStartedPayload(trigger, started),
            WorkerJobProtocolV2.EligibilityDeterminedType when
                jobKind == WorkerJobKind.ProcessAssets
                && HasExactProperties(payload, "eligibleCount")
                && TryInteger(payload, "eligibleCount", out var eligible) =>
                    new ProcessAssetsEligibilityPayload(eligible),
            WorkerJobProtocolV2.ProgressChangedType when
                jobKind == WorkerJobKind.ProcessAssets
                && HasExactProperties(payload, "processedCount", "updatedCount", "skippedCount", "failedCount")
                && TryCounts(payload, out var counts) =>
                    new ProcessAssetsProgressPayload(
                        counts.Processed,
                        counts.Updated,
                        counts.Skipped,
                        counts.Failed),
            WorkerJobProtocolV2.ProgressChangedType when
                jobKind == WorkerJobKind.CoordinateLookup
                && HasExactProperties(payload, "step", "state", "countryCode", "message")
                && TryString(payload, "step", out var stepText)
                && TryCoordinateLookupProgressStep(stepText, out var step)
                && TryString(payload, "state", out var progressStateText)
                && TryCoordinateLookupSourceState(progressStateText, out var progressState)
                && TryOptionalString(payload, "countryCode", out var progressCountryCode)
                && TryString(payload, "message", out var progressMessage) =>
                    new CoordinateLookupProgressPayload(
                        step,
                        progressState,
                        progressCountryCode,
                        progressMessage),
            WorkerJobProtocolV2.ProgressChangedType when
                jobKind == WorkerJobKind.CacheMutation
                && HasExactProperties(
                    payload,
                    "step",
                    "source",
                    "operation",
                    "iso3",
                    "message",
                    "gadmAttribution")
                && TryString(payload, "step", out var cacheStepText)
                && TryCacheMutationProgressStep(cacheStepText, out var cacheStep)
                && TryString(payload, "source", out var cacheSourceText)
                && TryCacheMutationSource(cacheSourceText, out var cacheSource)
                && TryString(payload, "operation", out var cacheOperationText)
                && TryCacheMutationOperation(cacheOperationText, out var cacheOperation)
                && TryString(payload, "iso3", out var cacheIso3)
                && TryString(payload, "message", out var cacheMessage)
                && payload.TryGetProperty("gadmAttribution", out JsonElement cacheAttributionElement)
                && TryCacheMutationAttribution(cacheAttributionElement, out var cacheAttribution) =>
                    new CacheMutationProgressPayload(
                        cacheStep,
                        cacheSource,
                        cacheOperation,
                        cacheIso3,
                        cacheMessage,
                        cacheAttribution),
            WorkerJobProtocolV2.ActivityStartedType when
                HasExactProperties(payload, "activityId", "label")
                && TryGuid(payload, "activityId", out var activityId)
                && TryString(payload, "label", out var label) =>
                    new WorkerJobActivityStartedPayload(activityId, label),
            WorkerJobProtocolV2.ActivityEndedType when
                HasExactProperties(payload, "activityId")
                && TryGuid(payload, "activityId", out var endedActivityId) =>
                    new WorkerJobActivityEndedPayload(endedActivityId),
            WorkerJobProtocolV2.LogEmittedType when
                HasExactProperties(payload, "level", "message")
                && TryString(payload, "level", out var level)
                && TryString(payload, "message", out var message) =>
                    new WorkerJobLogPayload(level, message),
            WorkerJobProtocolV2.TerminalType when jobKind is not null =>
                ParseTerminal(jobKind.Value, payload),
            _ => throw new ArgumentException("The payload does not match the output type and job kind.")
        };
    }

    private static WorkerJobTerminalPayload ParseTerminal(WorkerJobKind jobKind, JsonElement payload)
    {
        if (!HasExactProperties(payload, "outcome", "startedAtUtc", "endedAtUtc", "result", "error")
            || !TryString(payload, "outcome", out var outcomeText)
            || !TryTerminalOutcome(outcomeText, out var outcome)
            || !TryTimestamp(payload, "startedAtUtc", out var started)
            || !TryTimestamp(payload, "endedAtUtc", out var ended)
            || !payload.TryGetProperty("result", out var resultElement)
            || !payload.TryGetProperty("error", out var errorElement))
        {
            throw new ArgumentException("The terminal payload is incomplete.");
        }

        ProcessAssetsResult? result = null;
        CoordinateLookupResult? coordinateLookupResult = null;
        CacheMutationResult? cacheMutationResult = null;
        WorkerJobSafeError? error = null;
        if (resultElement.ValueKind != JsonValueKind.Null)
        {
            if (jobKind == WorkerJobKind.ProcessAssets
                && resultElement.ValueKind == JsonValueKind.Object
                && HasExactProperties(
                    resultElement,
                    "trigger",
                    "startedAtUtc",
                    "endedAtUtc",
                    "processedCount",
                    "updatedCount",
                    "skippedCount",
                    "failedCount")
                && TryString(resultElement, "trigger", out var trigger)
                && TryTimestamp(resultElement, "startedAtUtc", out var resultStarted)
                && TryTimestamp(resultElement, "endedAtUtc", out var resultEnded)
                && TryCounts(resultElement, out var counts))
            {
                result = new ProcessAssetsResult(
                    trigger,
                    resultStarted,
                    resultEnded,
                    counts.Processed,
                    counts.Updated,
                    counts.Skipped,
                    counts.Failed);
            }
            else if (jobKind == WorkerJobKind.CoordinateLookup
                && TryCoordinateLookupResult(resultElement, out coordinateLookupResult))
            {
            }
            else if (jobKind == WorkerJobKind.CacheMutation
                && TryCacheMutationResult(resultElement, out cacheMutationResult))
            {
            }
            else
            {
                throw new ArgumentException("The terminal result does not match the job kind.");
            }
        }

        if (errorElement.ValueKind != JsonValueKind.Null)
        {
            if (errorElement.ValueKind != JsonValueKind.Object
                || !HasExactProperties(errorElement, "code", "category", "message")
                || !TryString(errorElement, "code", out var code)
                || !TryString(errorElement, "category", out var categoryText)
                || !TryFailureCategory(categoryText, out var failureCategory)
                || !TryString(errorElement, "message", out var safeMessage))
            {
                throw new ArgumentException("The terminal error is invalid.");
            }

            error = new WorkerJobSafeError(code, failureCategory, safeMessage);
        }

        return new WorkerJobTerminalPayload(
            outcome,
            started,
            ended,
            result,
            coordinateLookupResult,
            cacheMutationResult,
            error);
    }

    private static void WriteCoordinateLookupRequest(
        Utf8JsonWriter writer,
        CoordinateLookupRequest request)
    {
        writer.WriteNumber("latitude", request.Latitude);
        writer.WriteNumber("longitude", request.Longitude);
        writer.WriteBoolean("includeAirportInfrastructure", request.IncludeAirportInfrastructure);
        writer.WriteBoolean("includeLiveOverturePlaces", request.IncludeLiveOverturePlaces);
        writer.WriteBoolean("preferGadmAdministrativeAreas", request.PreferGadmAdministrativeAreas);
        writer.WritePropertyName("cityResolverOverrides");
        writer.WriteStartObject();
        writer.WritePropertyName("defaultProfile");
        if (request.CityResolverOverrides.DefaultProfile is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteCoordinateLookupCityProfile(writer, request.CityResolverOverrides.DefaultProfile);
        }

        writer.WritePropertyName("countryProfiles");
        writer.WriteStartArray();
        foreach (CoordinateLookupCountryProfile countryProfile in request.CityResolverOverrides.CountryProfiles)
        {
            writer.WriteStartObject();
            writer.WriteString("countryCode", countryProfile.CountryCode);
            writer.WritePropertyName("profile");
            WriteCoordinateLookupCityProfile(writer, countryProfile.Profile);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteCacheMutationResult(
        Utf8JsonWriter writer,
        CacheMutationResult result)
    {
        CacheMutationSourceResult cache = result.Cache;
        writer.WriteStartObject();
        writer.WriteString("startedAtUtc", WorkerJobProtocolV2.FormatTimestamp(result.StartedAtUtc));
        writer.WriteString("endedAtUtc", WorkerJobProtocolV2.FormatTimestamp(result.EndedAtUtc));
        writer.WriteString("source", FormatCacheMutationSource(cache.Source));
        writer.WriteString("operation", FormatCacheMutationOperation(cache.Operation));
        writer.WriteString("iso3", cache.Iso3);
        writer.WriteString("disposition", FormatCacheMutationDisposition(cache.Disposition));
        writer.WriteNumber("rowCount", cache.RowCount);
        writer.WriteString("downloadedAtUtc", WorkerJobProtocolV2.FormatTimestamp(cache.DownloadedAtUtc));
        writer.WriteNumber("fileSizeBytes", cache.FileSizeBytes);
        writer.WriteString("version", cache.Version);
        writer.WritePropertyName("gadmAttribution");
        WriteCacheMutationAttribution(writer, cache.GadmAttribution);
        writer.WriteEndObject();
    }

    private static void WriteCacheMutationAttribution(
        Utf8JsonWriter writer,
        CacheMutationGadmAttribution? attribution)
    {
        if (attribution is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("datasetName", attribution.DatasetName);
            writer.WriteString("datasetVersion", attribution.DatasetVersion);
            writer.WriteString("licenseUrl", attribution.LicenseUrl);
            writer.WriteString("usageNotice", attribution.UsageNotice);
            writer.WriteEndObject();
        }
    }

    private static bool TryCacheMutationResult(
        JsonElement element,
        out CacheMutationResult? result)
    {
        result = null;
        if (element.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                element,
                "startedAtUtc",
                "endedAtUtc",
                "source",
                "operation",
                "iso3",
                "disposition",
                "rowCount",
                "downloadedAtUtc",
                "fileSizeBytes",
                "version",
                "gadmAttribution")
            || !TryTimestamp(element, "startedAtUtc", out var started)
            || !TryTimestamp(element, "endedAtUtc", out var ended)
            || !TryString(element, "source", out var sourceText)
            || !TryCacheMutationSource(sourceText, out var source)
            || !TryString(element, "operation", out var operationText)
            || !TryCacheMutationOperation(operationText, out var operation)
            || !TryString(element, "iso3", out var iso3)
            || !TryString(element, "disposition", out var dispositionText)
            || !TryCacheMutationDisposition(dispositionText, out var disposition)
            || !TryInteger(element, "rowCount", out var rowCount)
            || !TryTimestamp(element, "downloadedAtUtc", out var downloadedAt)
            || !TryInteger(element, "fileSizeBytes", out var fileSizeBytes)
            || !TryString(element, "version", out var version)
            || !element.TryGetProperty("gadmAttribution", out JsonElement attributionElement)
            || !TryCacheMutationAttribution(attributionElement, out var attribution))
        {
            return false;
        }

        try
        {
            result = new CacheMutationResult(
                started,
                ended,
                new CacheMutationSourceResult(
                    source,
                    operation,
                    iso3,
                    disposition,
                    rowCount,
                    downloadedAt,
                    fileSizeBytes,
                    version,
                    attribution));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryCacheMutationAttribution(
        JsonElement element,
        out CacheMutationGadmAttribution? attribution)
    {
        attribution = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                element,
                "datasetName",
                "datasetVersion",
                "licenseUrl",
                "usageNotice")
            || !TryString(element, "datasetName", out var datasetName)
            || !TryString(element, "datasetVersion", out var datasetVersion)
            || !TryString(element, "licenseUrl", out var licenseUrl)
            || !TryString(element, "usageNotice", out var usageNotice))
        {
            return false;
        }

        try
        {
            attribution = new CacheMutationGadmAttribution(
                datasetName,
                datasetVersion,
                licenseUrl,
                usageNotice);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string FormatCacheMutationSource(CacheMutationSource source) => source switch
    {
        CacheMutationSource.Overture => "overture",
        CacheMutationSource.Gadm => "gadm",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    private static bool TryCacheMutationSource(string value, out CacheMutationSource source)
    {
        source = value switch
        {
            "overture" => CacheMutationSource.Overture,
            "gadm" => CacheMutationSource.Gadm,
            _ => default
        };
        return value is "overture" or "gadm";
    }

    private static string FormatCacheMutationOperation(CacheMutationOperation operation) => operation switch
    {
        CacheMutationOperation.Ensure => "ensure",
        CacheMutationOperation.Refresh => "refresh",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static bool TryCacheMutationOperation(string value, out CacheMutationOperation operation)
    {
        operation = value switch
        {
            "ensure" => CacheMutationOperation.Ensure,
            "refresh" => CacheMutationOperation.Refresh,
            _ => default
        };
        return value is "ensure" or "refresh";
    }

    private static string FormatCacheMutationDisposition(CacheMutationDisposition disposition) => disposition switch
    {
        CacheMutationDisposition.AlreadyReady => "already-ready",
        CacheMutationDisposition.Published => "published",
        _ => throw new ArgumentOutOfRangeException(nameof(disposition))
    };

    private static bool TryCacheMutationDisposition(
        string value,
        out CacheMutationDisposition disposition)
    {
        disposition = value switch
        {
            "already-ready" => CacheMutationDisposition.AlreadyReady,
            "published" => CacheMutationDisposition.Published,
            _ => default
        };
        return value is "already-ready" or "published";
    }

    private static string FormatCacheMutationProgressStep(CacheMutationProgressStep step) => step switch
    {
        CacheMutationProgressStep.CheckingExisting => "checking-existing",
        CacheMutationProgressStep.PreparingSource => "preparing-source",
        CacheMutationProgressStep.Downloading => "downloading",
        CacheMutationProgressStep.Exporting => "exporting",
        CacheMutationProgressStep.ValidatingCandidate => "validating-candidate",
        CacheMutationProgressStep.Publishing => "publishing",
        CacheMutationProgressStep.Completed => "completed",
        _ => throw new ArgumentOutOfRangeException(nameof(step))
    };

    private static bool TryCacheMutationProgressStep(
        string value,
        out CacheMutationProgressStep step)
    {
        step = value switch
        {
            "checking-existing" => CacheMutationProgressStep.CheckingExisting,
            "preparing-source" => CacheMutationProgressStep.PreparingSource,
            "downloading" => CacheMutationProgressStep.Downloading,
            "exporting" => CacheMutationProgressStep.Exporting,
            "validating-candidate" => CacheMutationProgressStep.ValidatingCandidate,
            "publishing" => CacheMutationProgressStep.Publishing,
            "completed" => CacheMutationProgressStep.Completed,
            _ => default
        };
        return value is
            "checking-existing" or
            "preparing-source" or
            "downloading" or
            "exporting" or
            "validating-candidate" or
            "publishing" or
            "completed";
    }

    private static void WriteCoordinateLookupCityProfile(
        Utf8JsonWriter writer,
        CoordinateLookupCityProfile profile)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("preferredSubtypes");
        writer.WriteStartArray();
        foreach (string subtype in profile.PreferredSubtypes)
        {
            writer.WriteStringValue(subtype);
        }

        writer.WriteEndArray();
        if (profile.TieBreak is null)
        {
            writer.WriteNull("tieBreak");
        }
        else
        {
            writer.WriteString("tieBreak", FormatCoordinateLookupTieBreak(profile.TieBreak.Value));
        }

        writer.WriteEndObject();
    }

    private static bool TryCoordinateLookupRequest(
        JsonElement payload,
        out CoordinateLookupRequest? request)
    {
        request = null;
        if (!HasExactProperties(
                payload,
                "latitude",
                "longitude",
                "includeAirportInfrastructure",
                "includeLiveOverturePlaces",
                "preferGadmAdministrativeAreas",
                "cityResolverOverrides")
            || !TryCanonicalDouble(payload, "latitude", out double latitude)
            || !TryCanonicalDouble(payload, "longitude", out double longitude)
            || !TryBoolean(payload, "includeAirportInfrastructure", out bool includeAirport)
            || !TryBoolean(payload, "includeLiveOverturePlaces", out bool includePlaces)
            || !TryBoolean(payload, "preferGadmAdministrativeAreas", out bool preferGadm)
            || !payload.TryGetProperty("cityResolverOverrides", out JsonElement overridesElement)
            || !TryCoordinateLookupOverrides(overridesElement, out CoordinateLookupCityResolverOverrides? overrides))
        {
            return false;
        }

        request = new CoordinateLookupRequest(
            latitude,
            longitude,
            includeAirport,
            includePlaces,
            preferGadm,
            overrides!);
        return true;
    }

    private static bool TryCoordinateLookupOverrides(
        JsonElement element,
        out CoordinateLookupCityResolverOverrides? overrides)
    {
        overrides = null;
        if (!HasExactProperties(element, "defaultProfile", "countryProfiles")
            || !element.TryGetProperty("defaultProfile", out JsonElement defaultElement)
            || !element.TryGetProperty("countryProfiles", out JsonElement countriesElement)
            || countriesElement.ValueKind != JsonValueKind.Array
            || countriesElement.GetArrayLength() > CoordinateLookupProtocolBounds.MaxCountryProfiles)
        {
            return false;
        }

        CoordinateLookupCityProfile? defaultProfile = null;
        if (defaultElement.ValueKind != JsonValueKind.Null
            && !TryCoordinateLookupCityProfile(defaultElement, allowInheritedTieBreak: true, out defaultProfile))
        {
            return false;
        }

        var countries = new List<CoordinateLookupCountryProfile>();
        foreach (JsonElement countryElement in countriesElement.EnumerateArray())
        {
            if (!HasExactProperties(countryElement, "countryCode", "profile")
                || !TryString(countryElement, "countryCode", out string countryCode)
                || !countryElement.TryGetProperty("profile", out JsonElement profileElement)
                || !TryCoordinateLookupCityProfile(profileElement, allowInheritedTieBreak: true, out CoordinateLookupCityProfile? profile))
            {
                return false;
            }

            countries.Add(new CoordinateLookupCountryProfile(countryCode, profile!));
        }

        overrides = new CoordinateLookupCityResolverOverrides(defaultProfile, countries);
        return true;
    }

    private static bool TryCoordinateLookupCityProfile(
        JsonElement element,
        bool allowInheritedTieBreak,
        out CoordinateLookupCityProfile? profile)
    {
        profile = null;
        if (!HasExactProperties(element, "preferredSubtypes", "tieBreak")
            || !element.TryGetProperty("preferredSubtypes", out JsonElement subtypesElement)
            || subtypesElement.ValueKind != JsonValueKind.Array
            || subtypesElement.GetArrayLength() > CoordinateLookupProtocolBounds.MaxPreferredSubtypes
            || !element.TryGetProperty("tieBreak", out JsonElement tieBreakElement))
        {
            return false;
        }

        var subtypes = new List<string>();
        foreach (JsonElement subtypeElement in subtypesElement.EnumerateArray())
        {
            if (subtypeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            subtypes.Add(subtypeElement.GetString()!);
        }

        CoordinateLookupTieBreak? tieBreak = null;
        if (tieBreakElement.ValueKind == JsonValueKind.String)
        {
            if (!TryCoordinateLookupTieBreak(tieBreakElement.GetString()!, out CoordinateLookupTieBreak parsedTieBreak))
            {
                return false;
            }

            tieBreak = parsedTieBreak;
        }
        else if (!allowInheritedTieBreak || tieBreakElement.ValueKind != JsonValueKind.Null)
        {
            return false;
        }

        profile = new CoordinateLookupCityProfile(subtypes, tieBreak);
        return true;
    }

    private static void WriteCoordinateLookupResult(
        Utf8JsonWriter writer,
        CoordinateLookupResult result)
    {
        JsonSerializer.Serialize(writer, result, CoordinateJson);
    }

    private static bool TryCoordinateLookupResult(
        JsonElement element,
        out CoordinateLookupResult? result)
    {
        result = null;
        if (element.ValueKind != JsonValueKind.Object
            || !CoordinateLookupResultCollectionsWithinBounds(element))
        {
            return false;
        }

        try
        {
            result = element.Deserialize<CoordinateLookupResult>(CoordinateJson);
            return result is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool CoordinateLookupResultCollectionsWithinBounds(JsonElement result)
    {
        if (!result.TryGetProperty("trace", out JsonElement trace)
            || trace.ValueKind != JsonValueKind.Array
            || trace.GetArrayLength() > CoordinateLookupProtocolBounds.MaxTraceEntries
            || !result.TryGetProperty("omittedTraceCount", out _)
            || !result.TryGetProperty("truncatedTextCount", out _))
        {
            return false;
        }

        string[] sourceNames =
        [
            "overtureDivisions",
            "gadmDivisions",
            "airportInfrastructure",
            "liveOverturePlaces"
        ];
        foreach (string sourceName in sourceNames)
        {
            if (!result.TryGetProperty(sourceName, out JsonElement source)
                || source.ValueKind != JsonValueKind.Object
                || !source.TryGetProperty("candidates", out JsonElement candidates)
                || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() > CoordinateLookupProtocolBounds.MaxCandidatesPerSource
                || !source.TryGetProperty("caches", out JsonElement caches)
                || caches.ValueKind != JsonValueKind.Array
                || caches.GetArrayLength() > CoordinateLookupProtocolBounds.MaxCacheStatuses
                || !source.TryGetProperty("omittedCandidateCount", out _)
                || !source.TryGetProperty("omittedCacheCount", out _)
                || !source.TryGetProperty("truncatedTextCount", out _))
            {
                return false;
            }

            foreach (JsonElement candidate in candidates.EnumerateArray())
            {
                if (!CoordinateLookupCandidateCollectionsWithinBounds(candidate))
                {
                    return false;
                }
            }

            if (source.TryGetProperty("bestMatch", out JsonElement bestMatch)
                && bestMatch.ValueKind != JsonValueKind.Null
                && !CoordinateLookupCandidateCollectionsWithinBounds(bestMatch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CoordinateLookupCandidateCollectionsWithinBounds(JsonElement candidate) =>
        candidate.ValueKind == JsonValueKind.Object
        && candidate.TryGetProperty("recordSources", out JsonElement sources)
        && sources.ValueKind == JsonValueKind.Array
        && sources.GetArrayLength() <= CoordinateLookupProtocolBounds.MaxRecordSourcesPerCandidate
        && candidate.TryGetProperty("omittedRecordSourceCount", out _)
        && candidate.TryGetProperty("truncatedTextCount", out _);

    private static string FormatCoordinateLookupTieBreak(CoordinateLookupTieBreak tieBreak) =>
        tieBreak switch
        {
            CoordinateLookupTieBreak.SmallestArea => "smallest-area",
            CoordinateLookupTieBreak.LargestArea => "largest-area",
            _ => throw new ArgumentOutOfRangeException(nameof(tieBreak))
        };

    private static bool TryCoordinateLookupTieBreak(
        string value,
        out CoordinateLookupTieBreak tieBreak)
    {
        tieBreak = value switch
        {
            "smallest-area" => CoordinateLookupTieBreak.SmallestArea,
            "largest-area" => CoordinateLookupTieBreak.LargestArea,
            _ => default
        };
        return value is "smallest-area" or "largest-area";
    }

    private static string FormatCoordinateLookupProgressStep(CoordinateLookupProgressStep step) =>
        step switch
        {
            CoordinateLookupProgressStep.Country => "country",
            CoordinateLookupProgressStep.OvertureCache => "overture-cache",
            CoordinateLookupProgressStep.OvertureAdministrative => "overture-administrative",
            CoordinateLookupProgressStep.GadmCache => "gadm-cache",
            CoordinateLookupProgressStep.GadmAdministrative => "gadm-administrative",
            CoordinateLookupProgressStep.Airport => "airport",
            CoordinateLookupProgressStep.LivePlaces => "live-places",
            CoordinateLookupProgressStep.FinalSelection => "final-selection",
            _ => throw new ArgumentOutOfRangeException(nameof(step))
        };

    private static bool TryCoordinateLookupProgressStep(
        string value,
        out CoordinateLookupProgressStep step)
    {
        step = value switch
        {
            "country" => CoordinateLookupProgressStep.Country,
            "overture-cache" => CoordinateLookupProgressStep.OvertureCache,
            "overture-administrative" => CoordinateLookupProgressStep.OvertureAdministrative,
            "gadm-cache" => CoordinateLookupProgressStep.GadmCache,
            "gadm-administrative" => CoordinateLookupProgressStep.GadmAdministrative,
            "airport" => CoordinateLookupProgressStep.Airport,
            "live-places" => CoordinateLookupProgressStep.LivePlaces,
            "final-selection" => CoordinateLookupProgressStep.FinalSelection,
            _ => default
        };
        return value is
            "country" or
            "overture-cache" or
            "overture-administrative" or
            "gadm-cache" or
            "gadm-administrative" or
            "airport" or
            "live-places" or
            "final-selection";
    }

    private static string FormatCoordinateLookupSourceState(CoordinateLookupSourceState state) =>
        state switch
        {
            CoordinateLookupSourceState.Disabled => "disabled",
            CoordinateLookupSourceState.Skipped => "skipped",
            CoordinateLookupSourceState.Ready => "ready",
            CoordinateLookupSourceState.NoMatch => "no-match",
            CoordinateLookupSourceState.Unavailable => "unavailable",
            CoordinateLookupSourceState.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };

    private static bool TryCoordinateLookupSourceState(
        string value,
        out CoordinateLookupSourceState state)
    {
        state = value switch
        {
            "disabled" => CoordinateLookupSourceState.Disabled,
            "skipped" => CoordinateLookupSourceState.Skipped,
            "ready" => CoordinateLookupSourceState.Ready,
            "no-match" => CoordinateLookupSourceState.NoMatch,
            "unavailable" => CoordinateLookupSourceState.Unavailable,
            "failed" => CoordinateLookupSourceState.Failed,
            _ => default
        };
        return value is "disabled" or "skipped" or "ready" or "no-match" or "unavailable" or "failed";
    }

    private static bool TryCanonicalDouble(JsonElement element, string name, out double value)
    {
        value = default;
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetDouble(out value)
            || !double.IsFinite(value))
        {
            return false;
        }

        string canonical = JsonSerializer.Serialize(value);
        return property.GetRawText() == canonical;
    }

    private static bool TryBoolean(JsonElement element, string name, out bool value)
    {
        value = default;
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryOptionalString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out JsonElement property))
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

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteEnvelopeStart(
        Utf8JsonWriter writer,
        string direction,
        string category,
        string type,
        long sequence,
        DateTimeOffset timestamp,
        Guid? jobId,
        WorkerJobKind? jobKind)
    {
        writer.WriteStartObject();
        writer.WriteString("protocol", WorkerJobProtocolV2.Protocol);
        writer.WriteNumber("version", WorkerJobProtocolV2.Version);
        writer.WriteString("direction", direction);
        writer.WriteString("category", category);
        writer.WriteString("type", type);
        writer.WriteNumber("sequence", sequence);
        writer.WriteString("timestampUtc", WorkerJobProtocolV2.FormatTimestamp(timestamp));
        if (jobId is null)
        {
            writer.WriteNull("jobId");
        }
        else
        {
            writer.WriteString("jobId", jobId.Value.ToString("D"));
        }

        if (jobKind is null)
        {
            writer.WriteNull("jobKind");
        }
        else
        {
            writer.WriteString("jobKind", WorkerJobKindNames.Format(jobKind.Value));
        }
    }

    private static void WriteOutputPayload(Utf8JsonWriter writer, WorkerJobOutputPayload payload)
    {
        writer.WriteStartObject();
        switch (payload)
        {
            case WorkerJobReadyPayload ready:
                writer.WritePropertyName("supportedJobKinds");
                writer.WriteStartArray();
                foreach (var kind in ready.SupportedJobKinds)
                {
                    writer.WriteStringValue(WorkerJobKindNames.Format(kind));
                }

                writer.WriteEndArray();
                break;
            case WorkerJobStartedPayload started:
                writer.WriteString("trigger", started.Trigger);
                writer.WriteString("startedAtUtc", WorkerJobProtocolV2.FormatTimestamp(started.StartedAtUtc));
                break;
            case ProcessAssetsEligibilityPayload eligibility:
                writer.WriteNumber("eligibleCount", eligibility.EligibleCount);
                break;
            case ProcessAssetsProgressPayload progress:
                WriteCounts(writer, progress.ProcessedCount, progress.UpdatedCount, progress.SkippedCount, progress.FailedCount);
                break;
            case CoordinateLookupProgressPayload progress:
                writer.WriteString("step", FormatCoordinateLookupProgressStep(progress.Step));
                writer.WriteString("state", FormatCoordinateLookupSourceState(progress.State));
                WriteOptionalString(writer, "countryCode", progress.CountryCode);
                writer.WriteString("message", progress.Message);
                break;
            case CacheMutationProgressPayload progress:
                writer.WriteString("step", FormatCacheMutationProgressStep(progress.Step));
                writer.WriteString("source", FormatCacheMutationSource(progress.Source));
                writer.WriteString("operation", FormatCacheMutationOperation(progress.Operation));
                writer.WriteString("iso3", progress.Iso3);
                writer.WriteString("message", progress.Message);
                writer.WritePropertyName("gadmAttribution");
                WriteCacheMutationAttribution(writer, progress.GadmAttribution);
                break;
            case WorkerJobActivityStartedPayload activityStarted:
                writer.WriteString("activityId", activityStarted.ActivityId.ToString("D"));
                writer.WriteString("label", activityStarted.Label);
                break;
            case WorkerJobActivityEndedPayload activityEnded:
                writer.WriteString("activityId", activityEnded.ActivityId.ToString("D"));
                break;
            case WorkerJobLogPayload log:
                writer.WriteString("level", log.Level);
                writer.WriteString("message", log.Message);
                break;
            case WorkerJobTerminalPayload terminal:
                WriteTerminal(writer, terminal);
                break;
            default:
                throw new ArgumentException("The output payload is not supported by protocol v2.", nameof(payload));
        }

        writer.WriteEndObject();
    }

    private static void WriteTerminal(Utf8JsonWriter writer, WorkerJobTerminalPayload terminal)
    {
        writer.WriteString("outcome", FormatTerminalOutcome(terminal.Outcome));
        writer.WriteString("startedAtUtc", WorkerJobProtocolV2.FormatTimestamp(terminal.StartedAtUtc));
        writer.WriteString("endedAtUtc", WorkerJobProtocolV2.FormatTimestamp(terminal.EndedAtUtc));
        writer.WritePropertyName("result");
        if (terminal.ProcessAssetsResult is null
            && terminal.CoordinateLookupResult is null
            && terminal.CacheMutationResult is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            if (terminal.ProcessAssetsResult is { } result)
            {
                writer.WriteStartObject();
                writer.WriteString("trigger", result.Trigger);
                writer.WriteString("startedAtUtc", WorkerJobProtocolV2.FormatTimestamp(result.StartedAtUtc));
                writer.WriteString("endedAtUtc", WorkerJobProtocolV2.FormatTimestamp(result.EndedAtUtc));
                WriteCounts(writer, result.ProcessedCount, result.UpdatedCount, result.SkippedCount, result.FailedCount);
                writer.WriteEndObject();
            }
            else if (terminal.CoordinateLookupResult is not null)
            {
                WriteCoordinateLookupResult(writer, terminal.CoordinateLookupResult!);
            }
            else
            {
                WriteCacheMutationResult(writer, terminal.CacheMutationResult!);
            }
        }

        writer.WritePropertyName("error");
        if (terminal.Error is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("code", terminal.Error.Code);
            writer.WriteString("category", FormatFailureCategory(terminal.Error.Category));
            writer.WriteString("message", terminal.Error.Message);
            writer.WriteEndObject();
        }
    }

    private static bool TryEnvelope(
        JsonElement envelope,
        string expectedDirection,
        out string category,
        out string type,
        out long sequence,
        out DateTimeOffset timestamp,
        out Guid? jobId,
        out WorkerJobKind? jobKind,
        out JsonElement payload,
        out WorkerProtocolFailure? failure)
    {
        category = type = string.Empty;
        sequence = 0;
        timestamp = default;
        jobId = null;
        jobKind = null;
        payload = default;
        failure = null;
        if (!HasExactProperties(
                envelope,
                "protocol",
                "version",
                "direction",
                "category",
                "type",
                "sequence",
                "timestampUtc",
                "jobId",
                "jobKind",
                "payload"))
        {
            failure = new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidEnvelope, "Envelope properties are invalid.");
            return false;
        }

        if (!TryString(envelope, "protocol", out var protocol) || protocol != WorkerJobProtocolV2.Protocol)
        {
            failure = new WorkerProtocolFailure(WorkerProtocolFailureCode.UnsupportedProtocol, "Protocol identifier is not supported.");
            return false;
        }

        if (!TryInteger(envelope, "version", out var version) || version != WorkerJobProtocolV2.Version)
        {
            failure = new WorkerProtocolFailure(WorkerProtocolFailureCode.UnsupportedVersion, "Protocol version is not supported.");
            return false;
        }

        if (!TryString(envelope, "direction", out var direction) || direction != expectedDirection)
        {
            failure = new WorkerProtocolFailure(WorkerProtocolFailureCode.UnsupportedType, "Direction is not supported.");
            return false;
        }

        if (!TryString(envelope, "category", out category)
            || !TryString(envelope, "type", out type)
            || !TryInteger(envelope, "sequence", out sequence)
            || sequence < 1
            || !TryTimestamp(envelope, "timestampUtc", out timestamp)
            || !TryNullableGuid(envelope, "jobId", out jobId)
            || !TryNullableKind(envelope, "jobKind", out jobKind)
            || !envelope.TryGetProperty("payload", out payload)
            || payload.ValueKind != JsonValueKind.Object)
        {
            failure = new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidEnvelope, "Envelope values are invalid.");
            return false;
        }

        return true;
    }

    private static byte[] EnforceSize(ReadOnlySpan<byte> bytes, string parameterName)
    {
        if (bytes.Length > WorkerJobProtocolV2.MaxMessageBytes)
        {
            throw new ArgumentException("The serialized protocol message exceeds the configured byte limit.", parameterName);
        }

        return bytes.ToArray();
    }

    private static void WriteCounts(Utf8JsonWriter writer, long processed, long updated, long skipped, long failed)
    {
        writer.WriteNumber("processedCount", processed);
        writer.WriteNumber("updatedCount", updated);
        writer.WriteNumber("skippedCount", skipped);
        writer.WriteNumber("failedCount", failed);
    }

    private static string FormatTerminalOutcome(WorkerJobTerminalOutcome outcome) => outcome switch
    {
        WorkerJobTerminalOutcome.Completed => "completed",
        WorkerJobTerminalOutcome.Cancelled => "cancelled",
        WorkerJobTerminalOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private static bool TryTerminalOutcome(string value, out WorkerJobTerminalOutcome outcome)
    {
        outcome = value switch
        {
            "completed" => WorkerJobTerminalOutcome.Completed,
            "cancelled" => WorkerJobTerminalOutcome.Cancelled,
            "failed" => WorkerJobTerminalOutcome.Failed,
            _ => default
        };
        return value is "completed" or "cancelled" or "failed";
    }

    private static string FormatFailureCategory(WorkerJobFailureCategory category) => category switch
    {
        WorkerJobFailureCategory.Domain => "domain",
        WorkerJobFailureCategory.Dependency => "dependency",
        WorkerJobFailureCategory.Configuration => "configuration",
        WorkerJobFailureCategory.Internal => "internal",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private static bool TryFailureCategory(string value, out WorkerJobFailureCategory category)
    {
        category = value switch
        {
            "domain" => WorkerJobFailureCategory.Domain,
            "dependency" => WorkerJobFailureCategory.Dependency,
            "configuration" => WorkerJobFailureCategory.Configuration,
            "internal" => WorkerJobFailureCategory.Internal,
            _ => default
        };
        return value is "domain" or "dependency" or "configuration" or "internal";
    }

    private static bool TryKinds(JsonElement payload, out IReadOnlyList<WorkerJobKind> kinds)
    {
        var values = new List<WorkerJobKind>();
        kinds = values;
        if (!payload.TryGetProperty("supportedJobKinds", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !WorkerJobKindNames.TryParse(item.GetString()!, out var kind)
                || values.Contains(kind))
            {
                return false;
            }

            values.Add(kind);
        }

        var sorted = new List<WorkerJobKind>(values);
        sorted.Sort();
        return values.SequenceEqual(sorted);
    }

    private static bool HasExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            actual.Add(property.Name);
        }

        return actual.SetEquals(names);
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

    private static bool TryGetContent(
        ReadOnlySpan<byte> frame,
        out ReadOnlySpan<byte> content,
        out WorkerProtocolFailure? failure)
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
            failure = new WorkerProtocolFailure(
                WorkerProtocolFailureCode.InvalidFraming,
                "A bare carriage return is not a valid delimiter.");
            return false;
        }

        return true;
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString()!) is not null;
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
            && DateTimeOffset.TryParseExact(
                text,
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value)
            && WorkerJobProtocolV2.FormatTimestamp(value) == text;
    }

    private static bool TryNullableGuid(JsonElement element, string name, out Guid? value)
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

        if (property.ValueKind != JsonValueKind.String
            || !TryCanonicalGuid(property.GetString(), out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryNullableKind(JsonElement element, string name, out WorkerJobKind? value)
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

        if (property.ValueKind != JsonValueKind.String
            || !WorkerJobKindNames.TryParse(property.GetString()!, out var parsed))
        {
            return false;
        }

        value = parsed;
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
        return text is not null
            && Guid.TryParseExact(text, "D", out value)
            && value != Guid.Empty
            && value.ToString("D") == text;
    }

    private static bool TryCounts(
        JsonElement payload,
        out (long Processed, long Updated, long Skipped, long Failed) counts)
    {
        counts = default;
        return TryInteger(payload, "processedCount", out counts.Processed)
            && TryInteger(payload, "updatedCount", out counts.Updated)
            && TryInteger(payload, "skippedCount", out counts.Skipped)
            && TryInteger(payload, "failedCount", out counts.Failed);
    }

    private sealed class CanonicalKebabCaseEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private static readonly IReadOnlyDictionary<string, TEnum> ValuesByToken =
            Enum.GetValues<TEnum>().ToDictionary(
                static value => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString()),
                static value => value,
                StringComparer.Ordinal);
        private static readonly IReadOnlyDictionary<TEnum, string> TokensByValue =
            ValuesByToken.ToDictionary(
                static pair => pair.Value,
                static pair => pair.Key);

        public override TEnum Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || reader.GetString() is not { } token
                || !ValuesByToken.TryGetValue(token, out TEnum value))
            {
                throw new JsonException("The enum token is not canonical.");
            }

            return value;
        }

        public override void Write(
            Utf8JsonWriter writer,
            TEnum value,
            JsonSerializerOptions options)
        {
            if (!TokensByValue.TryGetValue(value, out string? token))
            {
                throw new JsonException("The enum value is not defined.");
            }

            writer.WriteStringValue(token);
        }
    }
}
