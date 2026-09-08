using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Core.WorkerJobs;

public static class WorkerJobProtocolV2
{
    public const string Protocol = WorkerProtocolV1.Protocol;
    public const int Version = 2;
    public const string WorkerToControllerDirection = WorkerProtocolV1.Direction;
    public const string ControllerToWorkerDirection = WorkerProtocolV1.ControllerToWorkerDirection;
    public const int MaxMessageBytes = WorkerProtocolV1.MaxMessageBytes;
    public const int MaxSafeTextLength = 256;

    public const string LifecycleCategory = "lifecycle";
    public const string ProgressCategory = "progress";
    public const string ActivityCategory = "activity";
    public const string DiagnosticCategory = "diagnostic";
    public const string TerminalCategory = "terminal";
    public const string RequestCategory = "request";
    public const string ControlCategory = "control";

    public const string ReadyType = "ready";
    public const string ExecuteType = "execute";
    public const string CancelType = "cancel";
    public const string JobStartedType = "job-started";
    public const string EligibilityDeterminedType = "eligibility-determined";
    public const string ProgressChangedType = "progress-changed";
    public const string ActivityStartedType = "activity-started";
    public const string ActivityEndedType = "activity-ended";
    public const string LogEmittedType = "log-emitted";
    public const string TerminalType = "terminal";

    internal static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    internal static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must have a zero UTC offset.", parameterName);
        }
    }

    internal static void RequireTrigger(string trigger, string parameterName)
    {
        if (!WorkerProtocolConversions.IsTrigger(trigger))
        {
            throw new ArgumentException("The trigger token is not defined.", parameterName);
        }
    }

    internal static void RequireCounts(long processed, long updated, long skipped, long failed)
    {
        if (processed < 0 || updated < 0 || skipped < 0 || failed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processed), "Counts must be non-negative.");
        }

        if (processed != checked(updated + skipped + failed))
        {
            throw new ArgumentException("Processed count must equal the classified counts.", nameof(processed));
        }
    }

    internal static void RequireSafeText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxSafeTextLength)
        {
            throw new ArgumentException("Safe protocol text must be non-blank and bounded.", parameterName);
        }
    }

    internal static void RequireDiagnosticText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Diagnostic protocol text must be non-blank.", parameterName);
        }
    }

    internal static string BoundSafeText(string value, string parameterName)
    {
        RequireDiagnosticText(value, parameterName);
        if (value.Length <= MaxSafeTextLength)
        {
            return value;
        }

        var prefixLength = MaxSafeTextLength - 1;
        if (char.IsHighSurrogate(value[prefixLength - 1])
            && char.IsLowSurrogate(value[prefixLength]))
        {
            prefixLength--;
        }

        return string.Concat(value.AsSpan(0, prefixLength), "…");
    }

    internal static bool IsOutputType(string category, string type) =>
        (category, type) is
            (LifecycleCategory, ReadyType) or
            (LifecycleCategory, JobStartedType) or
            (LifecycleCategory, EligibilityDeterminedType) or
            (ProgressCategory, ProgressChangedType) or
            (ActivityCategory, ActivityStartedType) or
            (ActivityCategory, ActivityEndedType) or
            (DiagnosticCategory, LogEmittedType) or
            (TerminalCategory, TerminalType);
}

public abstract record WorkerJobControllerPayload;

public sealed record ProcessAssetsExecutePayload : WorkerJobControllerPayload
{
    public ProcessAssetsRequest Request { get; }

    public ProcessAssetsExecutePayload(ProcessAssetsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }
}

public sealed record CoordinateLookupExecutePayload : WorkerJobControllerPayload
{
    public CoordinateLookupRequest Request { get; }

    public CoordinateLookupExecutePayload(CoordinateLookupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }
}

public sealed record WorkerJobCancelPayload : WorkerJobControllerPayload;

public sealed record WorkerJobControllerMessage
{
    public string Category { get; }
    public string Type { get; }
    public long Sequence { get; }
    public DateTimeOffset TimestampUtc { get; }
    public Guid JobId { get; }
    public WorkerJobKind JobKind { get; }
    public WorkerJobControllerPayload Payload { get; }

    public WorkerJobControllerMessage(
        string category,
        string type,
        long sequence,
        DateTimeOffset timestampUtc,
        Guid jobId,
        WorkerJobKind jobKind,
        WorkerJobControllerPayload payload)
    {
        if ((category, type) is not
            (WorkerJobProtocolV2.RequestCategory, WorkerJobProtocolV2.ExecuteType) and not
            (WorkerJobProtocolV2.ControlCategory, WorkerJobProtocolV2.CancelType))
        {
            throw new ArgumentException("The controller category and type are not defined.", nameof(type));
        }

        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        WorkerJobProtocolV2.RequireUtc(timestampUtc, nameof(timestampUtc));
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("A controller message requires a non-empty job ID.", nameof(jobId));
        }

        ArgumentNullException.ThrowIfNull(payload);
        var payloadMatches = type switch
        {
            WorkerJobProtocolV2.ExecuteType =>
                (jobKind == WorkerJobKind.ProcessAssets
                    && payload is ProcessAssetsExecutePayload execute
                    && execute.Request.ProcessingRequest.RunId == jobId)
                || (jobKind == WorkerJobKind.CoordinateLookup
                    && payload is CoordinateLookupExecutePayload),
            WorkerJobProtocolV2.CancelType => payload is WorkerJobCancelPayload,
            _ => false
        };
        if (!payloadMatches)
        {
            throw new ArgumentException("The payload does not match the job kind and controller message type.", nameof(payload));
        }

        Category = category;
        Type = type;
        Sequence = sequence;
        TimestampUtc = timestampUtc;
        JobId = jobId;
        JobKind = jobKind;
        Payload = payload;
    }
}

public abstract record WorkerJobOutputPayload;

public sealed record WorkerJobReadyPayload : WorkerJobOutputPayload
{
    public IReadOnlyList<WorkerJobKind> SupportedJobKinds { get; }

    public WorkerJobReadyPayload(IEnumerable<WorkerJobKind> supportedJobKinds)
    {
        ArgumentNullException.ThrowIfNull(supportedJobKinds);
        var copy = new List<WorkerJobKind>();
        foreach (var kind in supportedJobKinds)
        {
            if (copy.Contains(kind))
            {
                throw new ArgumentException("Supported job kinds must be unique.", nameof(supportedJobKinds));
            }

            copy.Add(kind);
        }

        copy.Sort();
        SupportedJobKinds = new ReadOnlyCollection<WorkerJobKind>(copy);
    }
}

public sealed record WorkerJobStartedPayload : WorkerJobOutputPayload
{
    public string Trigger { get; }
    public DateTimeOffset StartedAtUtc { get; }

    public WorkerJobStartedPayload(string trigger, DateTimeOffset startedAtUtc)
    {
        WorkerJobProtocolV2.RequireTrigger(trigger, nameof(trigger));
        WorkerJobProtocolV2.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        Trigger = trigger;
        StartedAtUtc = startedAtUtc;
    }
}

public sealed record ProcessAssetsEligibilityPayload : WorkerJobOutputPayload
{
    public long EligibleCount { get; }

    public ProcessAssetsEligibilityPayload(long eligibleCount)
    {
        if (eligibleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eligibleCount));
        }

        EligibleCount = eligibleCount;
    }
}

public sealed record ProcessAssetsProgressPayload : WorkerJobOutputPayload
{
    public long ProcessedCount { get; }
    public long UpdatedCount { get; }
    public long SkippedCount { get; }
    public long FailedCount { get; }

    public ProcessAssetsProgressPayload(long processedCount, long updatedCount, long skippedCount, long failedCount)
    {
        WorkerJobProtocolV2.RequireCounts(processedCount, updatedCount, skippedCount, failedCount);
        ProcessedCount = processedCount;
        UpdatedCount = updatedCount;
        SkippedCount = skippedCount;
        FailedCount = failedCount;
    }
}

public sealed record WorkerJobActivityStartedPayload : WorkerJobOutputPayload
{
    public Guid ActivityId { get; }
    public string Label { get; }

    public WorkerJobActivityStartedPayload(Guid activityId, string label)
    {
        if (activityId == Guid.Empty)
        {
            throw new ArgumentException("An activity ID must not be empty.", nameof(activityId));
        }

        WorkerJobProtocolV2.RequireDiagnosticText(label, nameof(label));
        ActivityId = activityId;
        Label = label;
    }
}

public sealed record WorkerJobActivityEndedPayload : WorkerJobOutputPayload
{
    public Guid ActivityId { get; }

    public WorkerJobActivityEndedPayload(Guid activityId)
    {
        if (activityId == Guid.Empty)
        {
            throw new ArgumentException("An activity ID must not be empty.", nameof(activityId));
        }

        ActivityId = activityId;
    }
}

public sealed record WorkerJobLogPayload : WorkerJobOutputPayload
{
    public string Level { get; }
    public string Message { get; }

    public WorkerJobLogPayload(string level, string message)
    {
        if (!WorkerProtocolConversions.IsLogLevel(level))
        {
            throw new ArgumentException("The log-level token is not defined.", nameof(level));
        }

        WorkerJobProtocolV2.RequireDiagnosticText(message, nameof(message));
        Level = level;
        Message = message;
    }
}

public enum WorkerJobFailureCategory
{
    Domain,
    Dependency,
    Configuration,
    Internal
}

public sealed record WorkerJobSafeError
{
    public string Code { get; }
    public WorkerJobFailureCategory Category { get; }
    public string Message { get; }

    public WorkerJobSafeError(string code, WorkerJobFailureCategory category, string message)
    {
        WorkerJobProtocolV2.RequireSafeText(code, nameof(code));
        WorkerJobProtocolV2.RequireSafeText(message, nameof(message));
        Code = code;
        Category = category;
        Message = message;
    }
}

public enum WorkerJobTerminalOutcome
{
    Completed,
    Cancelled,
    Failed
}

public sealed record WorkerJobTerminalPayload : WorkerJobOutputPayload
{
    public WorkerJobTerminalOutcome Outcome { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset EndedAtUtc { get; }
    public ProcessAssetsResult? ProcessAssetsResult { get; }
    public CoordinateLookupResult? CoordinateLookupResult { get; }
    public WorkerJobSafeError? Error { get; }

    public WorkerJobTerminalPayload(
        WorkerJobTerminalOutcome outcome,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        ProcessAssetsResult? processAssetsResult,
        WorkerJobSafeError? error)
        : this(outcome, startedAtUtc, endedAtUtc, processAssetsResult, null, error)
    {
    }

    public WorkerJobTerminalPayload(
        WorkerJobTerminalOutcome outcome,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        ProcessAssetsResult? processAssetsResult,
        CoordinateLookupResult? coordinateLookupResult,
        WorkerJobSafeError? error)
    {
        WorkerJobProtocolV2.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        WorkerJobProtocolV2.RequireUtc(endedAtUtc, nameof(endedAtUtc));
        if (endedAtUtc < startedAtUtc)
        {
            throw new ArgumentException("The terminal timestamp must not precede the start timestamp.", nameof(endedAtUtc));
        }

        var validShape = outcome switch
        {
            WorkerJobTerminalOutcome.Completed =>
                (processAssetsResult is not null) != (coordinateLookupResult is not null)
                && error is null,
            WorkerJobTerminalOutcome.Cancelled =>
                processAssetsResult is null && coordinateLookupResult is null && error is null,
            WorkerJobTerminalOutcome.Failed =>
                processAssetsResult is null && coordinateLookupResult is null && error is not null,
            _ => false
        };
        if (!validShape)
        {
            throw new ArgumentException("The terminal result and error do not match the outcome.");
        }

        if (processAssetsResult is not null
            && (processAssetsResult.StartedAtUtc != startedAtUtc || processAssetsResult.EndedAtUtc != endedAtUtc))
        {
            throw new ArgumentException("The terminal and typed-result timestamps must match.", nameof(processAssetsResult));
        }

        if (coordinateLookupResult is not null
            && (coordinateLookupResult.StartedAtUtc != startedAtUtc || coordinateLookupResult.EndedAtUtc != endedAtUtc))
        {
            throw new ArgumentException("The terminal and typed-result timestamps must match.", nameof(coordinateLookupResult));
        }

        Outcome = outcome;
        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
        ProcessAssetsResult = processAssetsResult;
        CoordinateLookupResult = coordinateLookupResult;
        Error = error;
    }
}

public sealed record WorkerJobHandlerEvent
{
    public DateTimeOffset TimestampUtc { get; }
    public WorkerJobOutputPayload Payload { get; }

    public WorkerJobHandlerEvent(DateTimeOffset timestampUtc, WorkerJobOutputPayload payload)
    {
        WorkerJobProtocolV2.RequireUtc(timestampUtc, nameof(timestampUtc));
        ArgumentNullException.ThrowIfNull(payload);
        if (payload is not ProcessAssetsEligibilityPayload
            and not ProcessAssetsProgressPayload
            and not CoordinateLookupProgressPayload
            and not WorkerJobActivityStartedPayload
            and not WorkerJobActivityEndedPayload
            and not WorkerJobLogPayload)
        {
            throw new ArgumentException(
                "Handlers may report only non-terminal job events.",
                nameof(payload));
        }

        TimestampUtc = timestampUtc;
        Payload = payload;
    }
}

public sealed record WorkerJobOutputMessage
{
    public string Category { get; }
    public string Type { get; }
    public long Sequence { get; }
    public DateTimeOffset TimestampUtc { get; }
    public Guid? JobId { get; }
    public WorkerJobKind? JobKind { get; }
    public WorkerJobOutputPayload Payload { get; }

    public WorkerJobOutputMessage(
        string category,
        string type,
        long sequence,
        DateTimeOffset timestampUtc,
        Guid? jobId,
        WorkerJobKind? jobKind,
        WorkerJobOutputPayload payload)
    {
        if (!WorkerJobProtocolV2.IsOutputType(category, type))
        {
            throw new ArgumentException("The output category and type are not defined.", nameof(type));
        }

        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        WorkerJobProtocolV2.RequireUtc(timestampUtc, nameof(timestampUtc));
        ArgumentNullException.ThrowIfNull(payload);
        if (type == WorkerJobProtocolV2.ReadyType)
        {
            if (jobId is not null || jobKind is not null || payload is not WorkerJobReadyPayload)
            {
                throw new ArgumentException("Ready must be process-scoped with a supported-kind payload.", nameof(payload));
            }
        }
        else
        {
            if (jobId is null || jobId == Guid.Empty || jobKind is null)
            {
                throw new ArgumentException("Job output requires identity and kind.", nameof(jobId));
            }

            ValidatePayload(type, timestampUtc, jobKind.Value, payload);
        }

        Category = category;
        Type = type;
        Sequence = sequence;
        TimestampUtc = timestampUtc;
        JobId = jobId;
        JobKind = jobKind;
        Payload = payload;
    }

    private static void ValidatePayload(
        string type,
        DateTimeOffset timestampUtc,
        WorkerJobKind jobKind,
        WorkerJobOutputPayload payload)
    {
        var commonMatches = type switch
        {
            WorkerJobProtocolV2.JobStartedType => payload is WorkerJobStartedPayload started && started.StartedAtUtc == timestampUtc,
            WorkerJobProtocolV2.ActivityStartedType => payload is WorkerJobActivityStartedPayload,
            WorkerJobProtocolV2.ActivityEndedType => payload is WorkerJobActivityEndedPayload,
            WorkerJobProtocolV2.LogEmittedType => payload is WorkerJobLogPayload,
            WorkerJobProtocolV2.TerminalType => payload is WorkerJobTerminalPayload terminal && terminal.EndedAtUtc == timestampUtc,
            _ => false
        };
        var processAssetsMatches = jobKind == WorkerJobKind.ProcessAssets && type switch
        {
            WorkerJobProtocolV2.EligibilityDeterminedType => payload is ProcessAssetsEligibilityPayload,
            WorkerJobProtocolV2.ProgressChangedType => payload is ProcessAssetsProgressPayload,
            _ => false
        };
        var coordinateLookupMatches = jobKind == WorkerJobKind.CoordinateLookup
            && type == WorkerJobProtocolV2.ProgressChangedType
            && payload is CoordinateLookupProgressPayload;
        if (!commonMatches && !processAssetsMatches && !coordinateLookupMatches)
        {
            throw new ArgumentException("The output payload is not valid for the job kind and type.", nameof(payload));
        }

        if (payload is WorkerJobTerminalPayload terminalPayload
            && terminalPayload.ProcessAssetsResult is not null
            && jobKind != WorkerJobKind.ProcessAssets)
        {
            throw new ArgumentException("The typed terminal result does not match the job kind.", nameof(payload));
        }


        if (payload is WorkerJobTerminalPayload coordinateTerminal
            && coordinateTerminal.CoordinateLookupResult is not null
            && jobKind != WorkerJobKind.CoordinateLookup)
        {
            throw new ArgumentException("The typed terminal result does not match the job kind.", nameof(payload));
        }
    }
}

public sealed record WorkerJobProtocolParseResult
{
    public WorkerJobOutputMessage? Message { get; }
    public WorkerProtocolFailure? Failure { get; }
    public bool IsSuccess => Message is not null;

    private WorkerJobProtocolParseResult(WorkerJobOutputMessage? message, WorkerProtocolFailure? failure)
    {
        Message = message;
        Failure = failure;
    }

    public static WorkerJobProtocolParseResult Success(WorkerJobOutputMessage message) => new(message, null);

    public static WorkerJobProtocolParseResult Failed(WorkerProtocolFailureCode code, string diagnostic) =>
        new(null, new WorkerProtocolFailure(code, diagnostic));
}

public sealed record WorkerJobControllerParseResult
{
    public WorkerJobControllerMessage? Message { get; }
    public WorkerProtocolFailure? Failure { get; }
    public bool IsSuccess => Message is not null;

    private WorkerJobControllerParseResult(WorkerJobControllerMessage? message, WorkerProtocolFailure? failure)
    {
        Message = message;
        Failure = failure;
    }

    public static WorkerJobControllerParseResult Success(WorkerJobControllerMessage message) => new(message, null);

    public static WorkerJobControllerParseResult Failed(WorkerProtocolFailureCode code, string diagnostic) =>
        new(null, new WorkerProtocolFailure(code, diagnostic));
}
