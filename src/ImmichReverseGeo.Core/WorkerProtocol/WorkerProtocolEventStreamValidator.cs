using System;
using System.Collections.Generic;

namespace ImmichReverseGeo.Core.WorkerProtocol;

public sealed record WorkerProtocolStreamFinalizationResult
{
    public bool IsComplete { get; }
    public WorkerProtocolFailure? Failure { get; }

    private WorkerProtocolStreamFinalizationResult(bool isComplete, WorkerProtocolFailure? failure)
    {
        IsComplete = isComplete;
        Failure = failure;
    }

    public static WorkerProtocolStreamFinalizationResult Complete() => new(true, null);
    public static WorkerProtocolStreamFinalizationResult Incomplete(string diagnostic) => new(false, new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidLifecycle, diagnostic));
}

public sealed class WorkerProtocolEventStreamValidator
{
    private long _lastSequence;
    private DateTimeOffset? _lastTimestampUtc;
    private Guid? _runId;
    private DateTimeOffset? _startedAtUtc;
    private string? _trigger;
    private bool _started;
    private bool _eligibility;
    private long _eligibleCount;
    private long _processedCount;
    private long _updatedCount;
    private long _skippedCount;
    private long _failedCount;
    private bool _terminal;
    private readonly HashSet<Guid> _openActivities = [];

    // Lets typed consumers validate before their own projection without advancing this cursor.
    internal WorkerProtocolParseResult Preview(WorkerProtocolEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var failure = ValidateWithoutMutation(@event);
        return failure is null
            ? WorkerProtocolParseResult.Success(@event)
            : WorkerProtocolParseResult.Failed(failure.Code, failure.Diagnostic);
    }

    public WorkerProtocolParseResult Validate(WorkerProtocolEvent @event)
    {
        var result = Preview(@event);
        if (result.IsSuccess)
        {
            Commit(@event);
        }

        return result;
    }

    public WorkerProtocolStreamFinalizationResult FinalizeStream()
    {
        if (_lastSequence == 0)
        {
            return WorkerProtocolStreamFinalizationResult.Incomplete("The stream is missing its required ready event.");
        }

        if (!_started)
        {
            return WorkerProtocolStreamFinalizationResult.Complete();
        }

        if (!_terminal)
        {
            return WorkerProtocolStreamFinalizationResult.Incomplete("The accepted run is missing its terminal event.");
        }

        return WorkerProtocolStreamFinalizationResult.Complete();
    }

    private WorkerProtocolFailure? ValidateWithoutMutation(WorkerProtocolEvent @event)
    {
        if (_lastSequence == 0)
        {
            if (@event.Type != WorkerProtocolV1.ReadyType || @event.Sequence != 1)
            {
                return Failure(WorkerProtocolFailureCode.InvalidLifecycle, "The first event must be ready at sequence one.");
            }
        }
        else if (WorkerProtocolSequence.ValidateSuccessor(_lastSequence, @event.Sequence) is { } sequenceFailure)
        {
            return sequenceFailure;
        }

        if (_lastTimestampUtc is not null && @event.TimestampUtc < _lastTimestampUtc)
        {
            return Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Event timestamps must not regress.");
        }

        if (_terminal)
        {
            return Failure(WorkerProtocolFailureCode.InvalidLifecycle, "No event may follow a terminal event.");
        }

        if (_lastSequence == 0)
        {
            return null;
        }

        if (@event.Type == WorkerProtocolV1.ReadyType)
        {
            return Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Ready may occur only once and only first.");
        }

        if (!_started)
        {
            return @event.Type == WorkerProtocolV1.RunStartedType
                ? null
                : Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Run-started must follow ready.");
        }

        if (@event.RunId != _runId)
        {
            return Failure(WorkerProtocolFailureCode.InvalidCorrelation, "Run correlation must match the accepted run.");
        }

        if (@event.Type == WorkerProtocolV1.RunStartedType)
        {
            return Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Run-started may occur only once.");
        }

        if (@event.Type == WorkerProtocolV1.EligibilityDeterminedType)
        {
            return _eligibility
                ? Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Eligibility may occur only once.")
                : null;
        }

        if (WorkerProtocolV1.IsTerminal(@event.Type))
        {
            if (@event.Payload is not TerminalPayload terminal || terminal.StartedAtUtc != _startedAtUtc)
            {
                return Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Terminal start time must match run-started.");
            }

            if (terminal.Trigger != _trigger)
            {
                return Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Terminal trigger must match run-started.");
            }

            if (@event.Type == WorkerProtocolV1.CompletedType && !_eligibility)
            {
                return Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Completed requires eligibility.");
            }

            if (_openActivities.Count != 0)
            {
                return Failure(WorkerProtocolFailureCode.InvalidLifecycle, "All activities must end before terminal.");
            }

            if (_eligibility && (terminal.ProcessedCount != _processedCount || terminal.UpdatedCount != _updatedCount || terminal.SkippedCount != _skippedCount || terminal.FailedCount != _failedCount))
            {
                return Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Terminal counts must match accepted progress.");
            }

            if (!_eligibility && terminal.ProcessedCount != 0)
            {
                return Failure(WorkerProtocolFailureCode.InvalidLifecycle, "A pre-count terminal must have zero progress.");
            }

            return null;
        }

        if (!_eligibility)
        {
            return Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Progress, activity, and diagnostics require eligibility.");
        }

        return @event.Payload switch
        {
            ProgressChangedPayload progress when progress.ProcessedCount > _eligibleCount => Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Progress must not exceed eligibility."),
            ProgressChangedPayload progress when progress.ProcessedCount != _processedCount + 1 => Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Progress must advance by one disposition."),
            ProgressChangedPayload progress when progress.UpdatedCount < _updatedCount || progress.SkippedCount < _skippedCount || progress.FailedCount < _failedCount => Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Progress counts must not regress."),
            ActivityStartedPayload started when _openActivities.Contains(started.ActivityId) => Failure(WorkerProtocolFailureCode.InvalidLifecycle, "An activity may not start twice."),
            ActivityEndedPayload ended when !_openActivities.Contains(ended.ActivityId) => Failure(WorkerProtocolFailureCode.InvalidLifecycle, "Activity end requires a matching start."),
            _ => null
        };
    }

    private void Commit(WorkerProtocolEvent @event)
    {
        _lastSequence = @event.Sequence;
        _lastTimestampUtc = @event.TimestampUtc;
        if (@event.Type == WorkerProtocolV1.RunStartedType)
        {
            _started = true;
            _runId = @event.RunId;
            var started = (RunStartedPayload)@event.Payload;
            _startedAtUtc = started.StartedAtUtc;
            _trigger = started.Trigger;
        }
        else if (@event.Payload is EligibilityDeterminedPayload eligibility)
        {
            _eligibility = true;
            _eligibleCount = eligibility.EligibleCount;
        }
        else if (@event.Payload is ProgressChangedPayload progress)
        {
            _processedCount = progress.ProcessedCount;
            _updatedCount = progress.UpdatedCount;
            _skippedCount = progress.SkippedCount;
            _failedCount = progress.FailedCount;
        }
        else if (@event.Payload is ActivityStartedPayload activityStarted)
        {
            _openActivities.Add(activityStarted.ActivityId);
        }
        else if (@event.Payload is ActivityEndedPayload activityEnded)
        {
            _openActivities.Remove(activityEnded.ActivityId);
        }
        else if (WorkerProtocolV1.IsTerminal(@event.Type))
        {
            _terminal = true;
        }
    }

    private static WorkerProtocolFailure Failure(WorkerProtocolFailureCode code, string diagnostic) => new(code, diagnostic);
}
