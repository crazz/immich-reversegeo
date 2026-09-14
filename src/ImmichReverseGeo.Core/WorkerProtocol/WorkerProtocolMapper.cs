using System;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Core.WorkerProtocol;

public static class WorkerProtocolMapper
{
    public static WorkerProtocolEvent Ready(long sequence, DateTimeOffset timestampUtc) =>
        new(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.ReadyType, sequence, timestampUtc, null, new ReadyPayload());

    public static WorkerProtocolEvent Map(ProcessingEvent source, long sequence, DateTimeOffset timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source switch
        {
            RunStarted started => Map(started, sequence),
            EligibilityDetermined eligibility => new WorkerProtocolEvent(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.EligibilityDeterminedType, sequence, timestampUtc, eligibility.Request.RunId, new EligibilityDeterminedPayload(eligibility.EligibleCount)),
            ProgressChanged progress => new WorkerProtocolEvent(WorkerProtocolV1.ProgressCategory, WorkerProtocolV1.ProgressChangedType, sequence, timestampUtc, progress.Request.RunId, new ProgressChangedPayload(progress.Progress.ProcessedCount, progress.Progress.UpdatedCount, progress.Progress.SkippedCount, progress.Progress.FailedCount)),
            ActivityStarted activityStarted => new WorkerProtocolEvent(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityStartedType, sequence, timestampUtc, activityStarted.Request.RunId, new ActivityStartedPayload(activityStarted.ActivityId, activityStarted.Label)),
            ActivityEnded activityEnded => new WorkerProtocolEvent(WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityEndedType, sequence, timestampUtc, activityEnded.Request.RunId, new ActivityEndedPayload(activityEnded.ActivityId)),
            LogEmitted log => new WorkerProtocolEvent(WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, sequence, timestampUtc, log.Request.RunId, new LogEmittedPayload(WorkerProtocolConversions.LogLevel(log.Level), log.Message)),
            RunFinished finished => Map(finished, sequence),
            _ => throw new ArgumentOutOfRangeException(nameof(source), "The processing event is not supported by protocol v1.")
        };
    }

    public static WorkerProtocolEvent Map(RunStarted source, long sequence)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new WorkerProtocolEvent(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.RunStartedType, sequence, source.StartedAtUtc, source.Request.RunId, new RunStartedPayload(WorkerProtocolConversions.Trigger(source.Request.Trigger), source.StartedAtUtc));
    }

    public static WorkerProtocolEvent Map(RunFinished source, long sequence)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = source.Result;
        var terminal = result.Outcome switch
        {
            ProcessingRunOutcome.Completed => (WorkerProtocolV1.CompletedType, (TerminalPayload)new CompletedPayload(WorkerProtocolConversions.Trigger(result.Request.Trigger), result.StartedAtUtc, result.EndedAtUtc, result.ProcessedCount, result.UpdatedCount, result.SkippedCount, result.FailedCount)),
            ProcessingRunOutcome.Cancelled => (WorkerProtocolV1.CancelledType, (TerminalPayload)new CancelledPayload(WorkerProtocolConversions.Trigger(result.Request.Trigger), result.StartedAtUtc, result.EndedAtUtc, result.ProcessedCount, result.UpdatedCount, result.SkippedCount, result.FailedCount)),
            ProcessingRunOutcome.Failed => (WorkerProtocolV1.FailedType, (TerminalPayload)new FailedPayload(WorkerProtocolConversions.Trigger(result.Request.Trigger), result.StartedAtUtc, result.EndedAtUtc, result.ProcessedCount, result.UpdatedCount, result.SkippedCount, result.FailedCount, result.FailureMessage!)),
            _ => throw new ArgumentOutOfRangeException(nameof(source), "The processing result outcome is not supported by protocol v1.")
        };

        return new WorkerProtocolEvent(WorkerProtocolV1.TerminalCategory, terminal.Item1, sequence, result.EndedAtUtc, result.Request.RunId, terminal.Item2);
    }
}

internal static class WorkerProtocolConversions
{
    public static string Trigger(ProcessingRunTrigger trigger) => trigger switch
    {
        ProcessingRunTrigger.Manual => "manual",
        ProcessingRunTrigger.Scheduled => "scheduled",
        ProcessingRunTrigger.RunOnce => "run-once",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger))
    };

    public static string LogLevel(ProcessingLogLevel level) => level switch
    {
        ProcessingLogLevel.Trace => "trace",
        ProcessingLogLevel.Information => "information",
        ProcessingLogLevel.Warning => "warning",
        ProcessingLogLevel.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    public static bool IsTrigger(string value) => TryTrigger(value, out _);

    public static bool TryTrigger(string value, out ProcessingRunTrigger trigger)
    {
        switch (value)
        {
            case "manual":
                trigger = ProcessingRunTrigger.Manual;
                return true;
            case "scheduled":
                trigger = ProcessingRunTrigger.Scheduled;
                return true;
            case "run-once":
                trigger = ProcessingRunTrigger.RunOnce;
                return true;
            default:
                trigger = default;
                return false;
        }
    }
    public static bool IsLogLevel(string value) => value is "trace" or "information" or "warning" or "error";
}
