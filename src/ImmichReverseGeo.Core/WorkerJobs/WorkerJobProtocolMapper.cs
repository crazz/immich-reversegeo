using System;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Core.WorkerJobs;

public static class WorkerJobProtocolMapper
{
    public static WorkerJobOutputMessage Ready(
        long sequence,
        DateTimeOffset timestampUtc,
        WorkerJobReadyPayload payload) =>
        new(
            WorkerJobProtocolV2.LifecycleCategory,
            WorkerJobProtocolV2.ReadyType,
            sequence,
            timestampUtc,
            null,
            null,
            payload);

    public static WorkerJobOutputMessage JobStarted(
        WorkerJobContext context,
        string trigger,
        DateTimeOffset startedAtUtc,
        long sequence) =>
        new(
            WorkerJobProtocolV2.LifecycleCategory,
            WorkerJobProtocolV2.JobStartedType,
            sequence,
            startedAtUtc,
            context.JobId,
            context.JobKind,
            new WorkerJobStartedPayload(trigger, startedAtUtc));

    public static WorkerJobOutputMessage Map(
        WorkerJobContext context,
        WorkerJobHandlerEvent source,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        (string Category, string Type) = source.Payload switch
        {
            ProcessAssetsEligibilityPayload =>
                (WorkerJobProtocolV2.LifecycleCategory, WorkerJobProtocolV2.EligibilityDeterminedType),
            ProcessAssetsProgressPayload =>
                (WorkerJobProtocolV2.ProgressCategory, WorkerJobProtocolV2.ProgressChangedType),
            CoordinateLookupProgressPayload =>
                (WorkerJobProtocolV2.ProgressCategory, WorkerJobProtocolV2.ProgressChangedType),
            WorkerJobActivityStartedPayload =>
                (WorkerJobProtocolV2.ActivityCategory, WorkerJobProtocolV2.ActivityStartedType),
            WorkerJobActivityEndedPayload =>
                (WorkerJobProtocolV2.ActivityCategory, WorkerJobProtocolV2.ActivityEndedType),
            WorkerJobLogPayload =>
                (WorkerJobProtocolV2.DiagnosticCategory, WorkerJobProtocolV2.LogEmittedType),
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };

        return new WorkerJobOutputMessage(
            Category,
            Type,
            sequence,
            source.TimestampUtc,
            context.JobId,
            context.JobKind,
            source.Payload);
    }

    public static WorkerJobOutputMessage Terminal(
        WorkerJobContext context,
        WorkerJobTerminalPayload payload,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payload);
        return new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            sequence,
            payload.EndedAtUtc,
            context.JobId,
            context.JobKind,
            payload);
    }
}

public sealed class ProcessAssetsWorkerJobProjection
{
    private readonly ProcessingRunRequest _request;
    private string? _trigger;
    private DateTimeOffset? _startedAtUtc;
    private long _processed;
    private long _updated;
    private long _skipped;
    private long _failed;

    public ProcessAssetsWorkerJobProjection(ProcessingRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
    }

    public WorkerProtocolEvent Map(WorkerJobOutputMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Payload switch
        {
            WorkerJobReadyPayload => WorkerProtocolMapper.Ready(message.Sequence, message.TimestampUtc),
            WorkerJobStartedPayload started => MapStarted(message, started),
            ProcessAssetsEligibilityPayload eligibility => new WorkerProtocolEvent(
                WorkerProtocolV1.LifecycleCategory,
                WorkerProtocolV1.EligibilityDeterminedType,
                message.Sequence,
                message.TimestampUtc,
                RequireIdentity(message),
                new EligibilityDeterminedPayload(eligibility.EligibleCount)),
            ProcessAssetsProgressPayload progress => MapProgress(message, progress),
            WorkerJobActivityStartedPayload started => new WorkerProtocolEvent(
                WorkerProtocolV1.ActivityCategory,
                WorkerProtocolV1.ActivityStartedType,
                message.Sequence,
                message.TimestampUtc,
                RequireIdentity(message),
                new ActivityStartedPayload(started.ActivityId, started.Label)),
            WorkerJobActivityEndedPayload ended => new WorkerProtocolEvent(
                WorkerProtocolV1.ActivityCategory,
                WorkerProtocolV1.ActivityEndedType,
                message.Sequence,
                message.TimestampUtc,
                RequireIdentity(message),
                new ActivityEndedPayload(ended.ActivityId)),
            WorkerJobLogPayload log => new WorkerProtocolEvent(
                WorkerProtocolV1.DiagnosticCategory,
                WorkerProtocolV1.LogEmittedType,
                message.Sequence,
                message.TimestampUtc,
                RequireIdentity(message),
                new LogEmittedPayload(log.Level, log.Message)),
            WorkerJobTerminalPayload terminal => MapTerminal(message, terminal),
            _ => throw new ArgumentOutOfRangeException(nameof(message))
        };
    }

    public static WorkerJobOutputMessage MapV1(
        ProcessingRunRequest request,
        WorkerProtocolEvent message)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(message);
        if ((message.Payload is ReadyPayload && message.RunId is not null)
            || (message.Payload is not ReadyPayload && message.RunId != request.RunId))
        {
            throw new ArgumentException(
                "The v1 message does not match the processing request.",
                nameof(message));
        }

        if (message.Payload is ReadyPayload)
        {
            return WorkerJobProtocolMapper.Ready(
                message.Sequence,
                message.TimestampUtc,
                new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets]));
        }

        var context = new WorkerJobContext(
            request.RunId,
            WorkerJobKind.ProcessAssets,
            request.Trigger switch
            {
                ProcessingRunTrigger.Manual => WorkerJobRequestOrigin.Manual,
                ProcessingRunTrigger.Scheduled => WorkerJobRequestOrigin.Scheduled,
                ProcessingRunTrigger.RunOnce => WorkerJobRequestOrigin.RunOnce,
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            });
        return message.Payload switch
        {
            RunStartedPayload started => WorkerJobProtocolMapper.JobStarted(
                context,
                started.Trigger,
                started.StartedAtUtc,
                message.Sequence),
            EligibilityDeterminedPayload eligibility => WorkerJobProtocolMapper.Map(
                context,
                Handler(message, new ProcessAssetsEligibilityPayload(eligibility.EligibleCount)),
                message.Sequence),
            ProgressChangedPayload progress => WorkerJobProtocolMapper.Map(
                context,
                Handler(message, new ProcessAssetsProgressPayload(
                    progress.ProcessedCount,
                    progress.UpdatedCount,
                    progress.SkippedCount,
                    progress.FailedCount)),
                message.Sequence),
            ActivityStartedPayload started => WorkerJobProtocolMapper.Map(
                context,
                Handler(message, new WorkerJobActivityStartedPayload(started.ActivityId, started.Label)),
                message.Sequence),
            ActivityEndedPayload ended => WorkerJobProtocolMapper.Map(
                context,
                Handler(message, new WorkerJobActivityEndedPayload(ended.ActivityId)),
                message.Sequence),
            LogEmittedPayload log => WorkerJobProtocolMapper.Map(
                context,
                Handler(message, new WorkerJobLogPayload(log.Level, log.Message)),
                message.Sequence),
            TerminalPayload terminal => WorkerJobProtocolMapper.Terminal(
                context,
                Terminal(terminal),
                message.Sequence),
            _ => throw new ArgumentOutOfRangeException(nameof(message))
        };
    }

    private static WorkerJobHandlerEvent Handler(
        WorkerProtocolEvent message,
        WorkerJobOutputPayload payload) =>
        new(message.TimestampUtc, payload);

    private static WorkerJobTerminalPayload Terminal(TerminalPayload terminal)
    {
        WorkerJobTerminalOutcome outcome = terminal switch
        {
            CompletedPayload => WorkerJobTerminalOutcome.Completed,
            CancelledPayload => WorkerJobTerminalOutcome.Cancelled,
            FailedPayload => WorkerJobTerminalOutcome.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(terminal))
        };
        ProcessAssetsResult? result = outcome == WorkerJobTerminalOutcome.Completed
            ? new ProcessAssetsResult(
                terminal.Trigger,
                terminal.StartedAtUtc,
                terminal.EndedAtUtc,
                terminal.ProcessedCount,
                terminal.UpdatedCount,
                terminal.SkippedCount,
                terminal.FailedCount)
            : null;
        WorkerJobSafeError? error = outcome == WorkerJobTerminalOutcome.Failed
            ? new WorkerJobSafeError(
                "processing-failed",
                WorkerJobFailureCategory.Domain,
                WorkerJobProtocolV2.BoundSafeText(
                    terminal.FailureMessage!,
                    nameof(terminal.FailureMessage)))
            : null;
        return new WorkerJobTerminalPayload(
            outcome,
            terminal.StartedAtUtc,
            terminal.EndedAtUtc,
            result,
            error);
    }

    private WorkerProtocolEvent MapStarted(
        WorkerJobOutputMessage message,
        WorkerJobStartedPayload started)
    {
        RequireIdentity(message);
        _trigger = started.Trigger;
        _startedAtUtc = started.StartedAtUtc;
        return new WorkerProtocolEvent(
            WorkerProtocolV1.LifecycleCategory,
            WorkerProtocolV1.RunStartedType,
            message.Sequence,
            message.TimestampUtc,
            _request.RunId,
            new RunStartedPayload(started.Trigger, started.StartedAtUtc));
    }

    private WorkerProtocolEvent MapProgress(
        WorkerJobOutputMessage message,
        ProcessAssetsProgressPayload progress)
    {
        RequireIdentity(message);
        _processed = progress.ProcessedCount;
        _updated = progress.UpdatedCount;
        _skipped = progress.SkippedCount;
        _failed = progress.FailedCount;
        return new WorkerProtocolEvent(
            WorkerProtocolV1.ProgressCategory,
            WorkerProtocolV1.ProgressChangedType,
            message.Sequence,
            message.TimestampUtc,
            _request.RunId,
            new ProgressChangedPayload(_processed, _updated, _skipped, _failed));
    }

    private WorkerProtocolEvent MapTerminal(
        WorkerJobOutputMessage message,
        WorkerJobTerminalPayload terminal)
    {
        RequireIdentity(message);
        string trigger = _trigger
            ?? throw new InvalidOperationException("A terminal requires a preceding job-started event.");
        DateTimeOffset startedAtUtc = _startedAtUtc
            ?? throw new InvalidOperationException("A terminal requires a preceding job-started event.");

        if (terminal.ProcessAssetsResult is { } completed)
        {
            trigger = completed.Trigger;
            startedAtUtc = completed.StartedAtUtc;
            _processed = completed.ProcessedCount;
            _updated = completed.UpdatedCount;
            _skipped = completed.SkippedCount;
            _failed = completed.FailedCount;
        }

        (string Type, TerminalPayload Payload) mapped = terminal.Outcome switch
        {
            WorkerJobTerminalOutcome.Completed => (
                WorkerProtocolV1.CompletedType,
                new CompletedPayload(
                    trigger,
                    startedAtUtc,
                    terminal.EndedAtUtc,
                    _processed,
                    _updated,
                    _skipped,
                    _failed)),
            WorkerJobTerminalOutcome.Cancelled => (
                WorkerProtocolV1.CancelledType,
                new CancelledPayload(
                    trigger,
                    startedAtUtc,
                    terminal.EndedAtUtc,
                    _processed,
                    _updated,
                    _skipped,
                    _failed)),
            WorkerJobTerminalOutcome.Failed => (
                WorkerProtocolV1.FailedType,
                new FailedPayload(
                    trigger,
                    startedAtUtc,
                    terminal.EndedAtUtc,
                    _processed,
                    _updated,
                    _skipped,
                    _failed,
                    terminal.Error!.Message)),
            _ => throw new ArgumentOutOfRangeException(nameof(terminal))
        };
        return new WorkerProtocolEvent(
            WorkerProtocolV1.TerminalCategory,
            mapped.Type,
            message.Sequence,
            message.TimestampUtc,
            _request.RunId,
            mapped.Payload);
    }

    private Guid RequireIdentity(WorkerJobOutputMessage message)
    {
        if (message.JobId != _request.RunId || message.JobKind != WorkerJobKind.ProcessAssets)
        {
            throw new ArgumentException("The v2 message does not match the processing request.", nameof(message));
        }

        return _request.RunId;
    }
}
