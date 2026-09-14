using System;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Core.Processing;

public abstract record ProcessingEvent
{
    public ProcessingRunRequest Request { get; }

    private protected ProcessingEvent(ProcessingRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }
}

public sealed record RunStarted : ProcessingEvent
{
    public DateTimeOffset StartedAtUtc { get; }

    public RunStarted(ProcessingRunRequest request, DateTimeOffset startedAtUtc)
        : base(request)
    {
        if (startedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The start timestamp must have a zero UTC offset.", nameof(startedAtUtc));
        }

        StartedAtUtc = startedAtUtc;
    }
}

public sealed record EligibilityDetermined : ProcessingEvent
{
    public long EligibleCount { get; }

    public EligibilityDetermined(ProcessingRunRequest request, long eligibleCount)
        : base(request)
    {
        if (eligibleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eligibleCount));
        }

        EligibleCount = eligibleCount;
    }
}

public sealed record ProcessingProgress
{
    public long ProcessedCount { get; }
    public long UpdatedCount { get; }
    public long SkippedCount { get; }
    public long FailedCount { get; }

    public ProcessingProgress(long processedCount, long updatedCount, long skippedCount, long failedCount)
    {
        if (processedCount < 0 || updatedCount < 0 || skippedCount < 0 || failedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processedCount));
        }

        if (processedCount != checked(updatedCount + skippedCount + failedCount))
        {
            throw new ArgumentException("Processed count must equal its dispositions.", nameof(processedCount));
        }

        ProcessedCount = processedCount;
        UpdatedCount = updatedCount;
        SkippedCount = skippedCount;
        FailedCount = failedCount;
    }
}

public sealed record ProgressChanged : ProcessingEvent
{
    public ProcessingProgress Progress { get; }

    public ProgressChanged(ProcessingRunRequest request, ProcessingProgress progress)
        : base(request)
    {
        ArgumentNullException.ThrowIfNull(progress);
        Progress = progress;
    }
}

public sealed record ActivityStarted : ProcessingEvent
{
    public Guid ActivityId { get; }
    public string Label { get; }

    public ActivityStarted(ProcessingRunRequest request, Guid activityId, string label)
        : base(request)
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

public sealed record ActivityEnded : ProcessingEvent
{
    public Guid ActivityId { get; }

    public ActivityEnded(ProcessingRunRequest request, Guid activityId)
        : base(request)
    {
        if (activityId == Guid.Empty)
        {
            throw new ArgumentException("An activity ID must not be empty.", nameof(activityId));
        }

        ActivityId = activityId;
    }
}

public enum ProcessingLogLevel
{
    Trace,
    Information,
    Warning,
    Error
}

public sealed record LogEmitted : ProcessingEvent
{
    public ProcessingLogLevel Level { get; }
    public string Message { get; }

    public LogEmitted(ProcessingRunRequest request, ProcessingLogLevel level, string message)
        : base(request)
    {
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A log message must not be blank.", nameof(message));
        }

        Level = level;
        Message = message;
    }
}

public sealed record RunFinished : ProcessingEvent
{
    public ProcessingRunResult Result { get; }

    public RunFinished(ProcessingRunRequest request, ProcessingRunResult result)
        : base(request)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!ReferenceEquals(result.Request, request))
        {
            throw new ArgumentException("The terminal result must belong to the event request.", nameof(result));
        }

        Result = result;
    }
}
