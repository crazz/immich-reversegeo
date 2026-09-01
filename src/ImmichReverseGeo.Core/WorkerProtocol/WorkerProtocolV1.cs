using System;
using System.Globalization;

namespace ImmichReverseGeo.Core.WorkerProtocol;

public static class WorkerProtocolV1
{
    public const string Protocol = "immich-reversegeo.worker";
    public const int Version = 1;
    public const string Direction = "worker-to-controller";
    public const int MaxMessageBytes = 1_048_576;

    public const string LifecycleCategory = "lifecycle";
    public const string ProgressCategory = "progress";
    public const string ActivityCategory = "activity";
    public const string DiagnosticCategory = "diagnostic";
    public const string TerminalCategory = "terminal";

    public const string ReadyType = "ready";
    public const string RunStartedType = "run-started";
    public const string EligibilityDeterminedType = "eligibility-determined";
    public const string ProgressChangedType = "progress-changed";
    public const string ActivityStartedType = "activity-started";
    public const string ActivityEndedType = "activity-ended";
    public const string LogEmittedType = "log-emitted";
    public const string CompletedType = "completed";
    public const string CancelledType = "cancelled";
    public const string FailedType = "failed";

    internal static bool IsKnown(string category, string type) =>
        (category, type) is
            (LifecycleCategory, ReadyType) or
            (LifecycleCategory, RunStartedType) or
            (LifecycleCategory, EligibilityDeterminedType) or
            (ProgressCategory, ProgressChangedType) or
            (ActivityCategory, ActivityStartedType) or
            (ActivityCategory, ActivityEndedType) or
            (DiagnosticCategory, LogEmittedType) or
            (TerminalCategory, CompletedType) or
            (TerminalCategory, CancelledType) or
            (TerminalCategory, FailedType);

    internal static bool IsTerminal(string type) => type is CompletedType or CancelledType or FailedType;
    internal static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    internal static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must have a zero UTC offset.", parameterName);
        }
    }

    internal static void RequireCounts(long processedCount, long updatedCount, long skippedCount, long failedCount)
    {
        if (processedCount < 0 || updatedCount < 0 || skippedCount < 0 || failedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processedCount), "Counts must be non-negative.");
        }

        if (processedCount != checked(updatedCount + skippedCount + failedCount))
        {
            throw new ArgumentException("Processed count must equal the classified counts.", nameof(processedCount));
        }
    }
}

public enum WorkerProtocolFailureCode
{
    MessageTooLarge,
    InvalidEncoding,
    InvalidFraming,
    MalformedJson,
    InvalidEnvelope,
    UnsupportedProtocol,
    UnsupportedVersion,
    UnsupportedType,
    InvalidPayload,
    InvalidSequence,
    InvalidCorrelation,
    InvalidLifecycle
}

public sealed record WorkerProtocolFailure
{
    public WorkerProtocolFailureCode Code { get; }
    public string Diagnostic { get; }

    public WorkerProtocolFailure(WorkerProtocolFailureCode code, string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic) || diagnostic.Length > 256)
        {
            throw new ArgumentException("A protocol diagnostic must be non-blank and bounded.", nameof(diagnostic));
        }

        Code = code;
        Diagnostic = diagnostic;
    }
}

public sealed record WorkerProtocolParseResult
{
    public WorkerProtocolEvent? Event { get; }
    public WorkerProtocolFailure? Failure { get; }
    public bool IsSuccess => Event is not null;

    private WorkerProtocolParseResult(WorkerProtocolEvent? @event, WorkerProtocolFailure? failure)
    {
        Event = @event;
        Failure = failure;
    }

    public static WorkerProtocolParseResult Success(WorkerProtocolEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return new WorkerProtocolParseResult(@event, null);
    }

    public static WorkerProtocolParseResult Failed(WorkerProtocolFailureCode code, string diagnostic) => new(null, new WorkerProtocolFailure(code, diagnostic));
}

public abstract record WorkerProtocolPayload;
public sealed record ReadyPayload : WorkerProtocolPayload;

public sealed record RunStartedPayload : WorkerProtocolPayload
{
    public string Trigger { get; }
    public DateTimeOffset StartedAtUtc { get; }

    public RunStartedPayload(string trigger, DateTimeOffset startedAtUtc)
    {
        if (!WorkerProtocolConversions.IsTrigger(trigger))
        {
            throw new ArgumentException("The trigger token is not defined.", nameof(trigger));
        }

        WorkerProtocolV1.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        Trigger = trigger;
        StartedAtUtc = startedAtUtc;
    }
}

public sealed record EligibilityDeterminedPayload : WorkerProtocolPayload
{
    public long EligibleCount { get; }

    public EligibilityDeterminedPayload(long eligibleCount)
    {
        if (eligibleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eligibleCount));
        }

        EligibleCount = eligibleCount;
    }
}

public sealed record ProgressChangedPayload : WorkerProtocolPayload
{
    public long ProcessedCount { get; }
    public long UpdatedCount { get; }
    public long SkippedCount { get; }
    public long FailedCount { get; }

    public ProgressChangedPayload(long processedCount, long updatedCount, long skippedCount, long failedCount)
    {
        WorkerProtocolV1.RequireCounts(processedCount, updatedCount, skippedCount, failedCount);
        ProcessedCount = processedCount;
        UpdatedCount = updatedCount;
        SkippedCount = skippedCount;
        FailedCount = failedCount;
    }
}

public sealed record ActivityStartedPayload : WorkerProtocolPayload
{
    public Guid ActivityId { get; }
    public string Label { get; }

    public ActivityStartedPayload(Guid activityId, string label)
    {
        if (activityId == Guid.Empty)
        {
            throw new ArgumentException("An activity ID must not be empty.", nameof(activityId));
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("An activity label must not be blank.", nameof(label));
        }

        ActivityId = activityId;
        Label = label;
    }
}

public sealed record ActivityEndedPayload : WorkerProtocolPayload
{
    public Guid ActivityId { get; }

    public ActivityEndedPayload(Guid activityId)
    {
        if (activityId == Guid.Empty)
        {
            throw new ArgumentException("An activity ID must not be empty.", nameof(activityId));
        }

        ActivityId = activityId;
    }
}

public sealed record LogEmittedPayload : WorkerProtocolPayload
{
    public string Level { get; }
    public string Message { get; }

    public LogEmittedPayload(string level, string message)
    {
        if (!WorkerProtocolConversions.IsLogLevel(level))
        {
            throw new ArgumentException("The log-level token is not defined.", nameof(level));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A log message must not be blank.", nameof(message));
        }

        Level = level;
        Message = message;
    }
}

public abstract record TerminalPayload : WorkerProtocolPayload
{
    public string Trigger { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset EndedAtUtc { get; }
    public long ProcessedCount { get; }
    public long UpdatedCount { get; }
    public long SkippedCount { get; }
    public long FailedCount { get; }
    public string? FailureMessage { get; }

    protected TerminalPayload(string trigger, DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc, long processedCount, long updatedCount, long skippedCount, long failedCount, string? failureMessage, bool requiresFailureMessage)
    {
        if (!WorkerProtocolConversions.IsTrigger(trigger))
        {
            throw new ArgumentException("The trigger token is not defined.", nameof(trigger));
        }

        WorkerProtocolV1.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        WorkerProtocolV1.RequireUtc(endedAtUtc, nameof(endedAtUtc));
        if (endedAtUtc < startedAtUtc)
        {
            throw new ArgumentException("The terminal timestamp must not precede the start timestamp.", nameof(endedAtUtc));
        }

        WorkerProtocolV1.RequireCounts(processedCount, updatedCount, skippedCount, failedCount);
        if (requiresFailureMessage != !string.IsNullOrWhiteSpace(failureMessage))
        {
            throw new ArgumentException("Failure detail does not match the terminal outcome.", nameof(failureMessage));
        }

        Trigger = trigger;
        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
        ProcessedCount = processedCount;
        UpdatedCount = updatedCount;
        SkippedCount = skippedCount;
        FailedCount = failedCount;
        FailureMessage = failureMessage;
    }
}

public sealed record CompletedPayload : TerminalPayload
{
    public CompletedPayload(string trigger, DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc, long processedCount, long updatedCount, long skippedCount, long failedCount, string? failureMessage = null)
        : base(trigger, startedAtUtc, endedAtUtc, processedCount, updatedCount, skippedCount, failedCount, failureMessage, false) { }
}

public sealed record CancelledPayload : TerminalPayload
{
    public CancelledPayload(string trigger, DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc, long processedCount, long updatedCount, long skippedCount, long failedCount, string? failureMessage = null)
        : base(trigger, startedAtUtc, endedAtUtc, processedCount, updatedCount, skippedCount, failedCount, failureMessage, false) { }
}

public sealed record FailedPayload : TerminalPayload
{
    public FailedPayload(string trigger, DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc, long processedCount, long updatedCount, long skippedCount, long failedCount, string failureMessage)
        : base(trigger, startedAtUtc, endedAtUtc, processedCount, updatedCount, skippedCount, failedCount, failureMessage, true) { }
}

public sealed record WorkerProtocolEvent
{
    public string Category { get; }
    public string Type { get; }
    public long Sequence { get; }
    public DateTimeOffset TimestampUtc { get; }
    public Guid? RunId { get; }
    public WorkerProtocolPayload Payload { get; }

    public WorkerProtocolEvent(string category, string type, long sequence, DateTimeOffset timestampUtc, Guid? runId, WorkerProtocolPayload payload)
    {
        if (!WorkerProtocolV1.IsKnown(category, type))
        {
            throw new ArgumentException("The category and type combination is not defined.", nameof(type));
        }

        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        WorkerProtocolV1.RequireUtc(timestampUtc, nameof(timestampUtc));
        ArgumentNullException.ThrowIfNull(payload);
        if (type == WorkerProtocolV1.ReadyType)
        {
            if (runId is not null || payload is not ReadyPayload)
            {
                throw new ArgumentException("Ready must be uncorrelated with an empty payload.", nameof(payload));
            }
        }
        else
        {
            if (runId is null || runId == Guid.Empty)
            {
                throw new ArgumentException("Run-scoped events require a non-empty run ID.", nameof(runId));
            }

            ValidatePayload(type, payload, timestampUtc);
        }

        Category = category;
        Type = type;
        Sequence = sequence;
        TimestampUtc = timestampUtc;
        RunId = runId;
        Payload = payload;
    }

    private static void ValidatePayload(string type, WorkerProtocolPayload payload, DateTimeOffset timestampUtc)
    {
        var matches = type switch
        {
            WorkerProtocolV1.RunStartedType => payload is RunStartedPayload started && timestampUtc == started.StartedAtUtc,
            WorkerProtocolV1.EligibilityDeterminedType => payload is EligibilityDeterminedPayload,
            WorkerProtocolV1.ProgressChangedType => payload is ProgressChangedPayload,
            WorkerProtocolV1.ActivityStartedType => payload is ActivityStartedPayload,
            WorkerProtocolV1.ActivityEndedType => payload is ActivityEndedPayload,
            WorkerProtocolV1.LogEmittedType => payload is LogEmittedPayload,
            WorkerProtocolV1.CompletedType => payload is CompletedPayload completed && timestampUtc == completed.EndedAtUtc,
            WorkerProtocolV1.CancelledType => payload is CancelledPayload cancelled && timestampUtc == cancelled.EndedAtUtc,
            WorkerProtocolV1.FailedType => payload is FailedPayload failed && timestampUtc == failed.EndedAtUtc,
            _ => false
        };

        if (!matches)
        {
            throw new ArgumentException("The payload does not match the event type or timestamp.", nameof(payload));
        }
    }
}
