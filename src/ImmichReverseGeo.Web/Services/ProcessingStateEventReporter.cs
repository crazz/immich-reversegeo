using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Web.Services;

/// <summary>Projects the one admitted in-process event run into the singleton Web state.</summary>
public sealed class ProcessingStateEventReporter : ProcessingEventReporter
{
    private const int MaxPostTerminalDiagnosticLength = 256;

    private readonly ProcessingState _state;
    private readonly object _projectionGate = new();
    private readonly Dictionary<Guid, IDisposable> _activities = [];
    private ProcessingRunRequest? _armedRequest;
    private ProcessingRunRequest? _lastReleasedRequest;
    private bool _terminal;
    private DateTimeOffset? _startedAtUtc;
    private ProcessingProgress? _lastProgress;
    private ProcessingState.ProgressSnapshot? _armedProgress;
    private bool _eligibilityProjected;
    private ProcessingRunFinalizationReceipt? _finalizationReceipt;
    private bool _postTerminalDiagnosticAppended;
    private readonly Action<ProcessingEvent>? _beforeProjection;
    private readonly Action<string, ProcessingRunRequest>? _controlObserver;

    public ProcessingStateEventReporter(ProcessingState state)
        : this(state, null)
    {
    }

    internal ProcessingStateEventReporter(ProcessingState state, Action<ProcessingEvent>? beforeProjection)
        : this(state, beforeProjection, null)
    {
    }

    internal ProcessingStateEventReporter(
        ProcessingState state,
        Action<ProcessingEvent>? beforeProjection,
        Action<string, ProcessingRunRequest>? controlObserver)
    {
        _state = state;
        _beforeProjection = beforeProjection;
        _controlObserver = controlObserver;
    }

    internal bool Arm(ProcessingRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_projectionGate)
        {
            if (_armedRequest is not null)
            {
                return false;
            }

            _armedRequest = request;
            _lastReleasedRequest = null;
            _terminal = false;
            _startedAtUtc = null;
            _lastProgress = null;
            _armedProgress = _state.ReadProgressSnapshot();
            _eligibilityProjected = false;
            _finalizationReceipt = null;
            _postTerminalDiagnosticAppended = false;
            _controlObserver?.Invoke("arm", request);
            return true;
        }
    }

    internal bool IsArmed(ProcessingRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_projectionGate)
        {
            return ReferenceEquals(_armedRequest, request) && !_terminal;
        }
    }

    internal ProcessingRunFinalizationReceipt? GetFinalizationReceipt(ProcessingRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_projectionGate)
        {
            return ReferenceEquals(_finalizationReceipt?.Request, request)
                ? _finalizationReceipt
                : null;
        }
    }

    internal ProcessingRunFinalizationAttempt TryFinalize(
        ProcessingRunRequest request,
        ProcessingRunResult result,
        ProcessingRunFinalizationOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        if (!ReferenceEquals(result.Request, request))
        {
            throw new ArgumentException("The finalization result must retain the exact request instance.", nameof(result));
        }

        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "The finalization origin must be defined.");
        }

        lock (_projectionGate)
        {
            return FinalizeUnderLock(request, result, origin, null);
        }
    }

    internal ProcessingRunResult CreateAbnormalResult(
        ProcessingRunRequest request,
        ProcessingRunOutcome outcome,
        DateTimeOffset fallbackStartedAtUtc,
        DateTimeOffset endedAtUtc,
        string? safeFailureMessage)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (outcome is not ProcessingRunOutcome.Failed and not ProcessingRunOutcome.Cancelled)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Abnormal finalization may only synthesize a failed or cancelled result.");
        }

        if (fallbackStartedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The fallback start timestamp must have a zero UTC offset.", nameof(fallbackStartedAtUtc));
        }

        if (endedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The end timestamp must have a zero UTC offset.", nameof(endedAtUtc));
        }

        lock (_projectionGate)
        {
            if (!ReferenceEquals(_armedRequest, request) || _terminal || _finalizationReceipt is not null)
            {
                throw new InvalidOperationException("The reporter does not own uncommitted finalization for this request.");
            }

            var progress = _lastProgress;
            var updated = progress?.UpdatedCount ?? 0;
            var skipped = progress?.SkippedCount ?? 0;
            var failed = progress?.FailedCount ?? 0;
            var processed = checked(updated + skipped + failed);
            var startedAtUtc = _startedAtUtc ?? fallbackStartedAtUtc;
            // A wall-clock correction must not strand a settled run before its
            // durable receipt can be claimed.
            var effectiveEndedAtUtc = endedAtUtc < startedAtUtc
                ? startedAtUtc
                : endedAtUtc;
            return new ProcessingRunResult(
                request,
                startedAtUtc,
                effectiveEndedAtUtc,
                processed,
                updated,
                skipped,
                failed,
                outcome,
                safeFailureMessage);
        }
    }

    internal bool TryAppendPostTerminalDiagnostic(
        ProcessingRunFinalizationReceipt receipt,
        string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (string.IsNullOrWhiteSpace(diagnostic) || diagnostic.Length > MaxPostTerminalDiagnosticLength)
        {
            throw new ArgumentException("A post-terminal diagnostic must be non-blank and bounded.", nameof(diagnostic));
        }

        lock (_projectionGate)
        {
            if (!ReferenceEquals(_finalizationReceipt, receipt) || _postTerminalDiagnosticAppended)
            {
                return false;
            }

            _postTerminalDiagnosticAppended = true;
            try
            {
                _state.AppendLog($"[WARN] {diagnostic}");
            }
            catch
            {
                // AppendLog commits before notification; the diagnostic remains exactly once.
            }

            return true;
        }
    }

    internal bool AbandonProjectedActivities(ProcessingRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_projectionGate)
        {
            if (!ReferenceEquals(_armedRequest, request) || _terminal)
            {
                return false;
            }

            var activities = _activities.Values.ToArray();
            _activities.Clear();
            ExceptionDispatchInfo? firstFailure = null;
            foreach (var activity in activities)
            {
                try
                {
                    activity.Dispose();
                }
                catch (Exception failure)
                {
                    firstFailure ??= ExceptionDispatchInfo.Capture(failure);
                }
            }

            firstFailure?.Throw();
            return true;
        }
    }

    internal bool Abandon(ProcessingRunRequest request, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(failure);

        lock (_projectionGate)
        {
            if (!ReferenceEquals(_armedRequest, request))
            {
                return false;
            }

            try
            {
                _controlObserver?.Invoke("abandon", request);
                foreach (var activity in _activities.Values)
                {
                    try
                    {
                        activity.Dispose();
                    }
                    catch
                    {
                        // Recovery must finish even when a synchronous state observer is the fault source.
                    }
                }

                _activities.Clear();
                RestoreFailureSnapshot(failure);
                return true;
            }
            finally
            {
                ReleaseArm();
            }
        }
    }

    internal bool RollbackPendingAfterArmRejection(ProcessingRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_projectionGate)
        {
            if (ReferenceEquals(_armedRequest, request) || ReferenceEquals(_lastReleasedRequest, request))
            {
                return false;
            }

            _state.ClearPending();
            return true;
        }
    }

    internal bool AbandonPending(ProcessingRunRequest request, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(failure);

        lock (_projectionGate)
        {
            if (_armedRequest is not null || ReferenceEquals(_lastReleasedRequest, request))
            {
                return false;
            }

            try
            {
                RestoreFailureSnapshot(failure);
                return true;
            }
            finally
            {
                ReleaseArm();
            }
        }
    }

    protected override ValueTask AcceptAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken)
    {
        Project(processingEvent, cancellationToken);
        return ValueTask.CompletedTask;
    }

    internal ValueTask<bool> TryProjectAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken)
        => ValueTask.FromResult(Project(processingEvent, cancellationToken));

    private bool Project(ProcessingEvent processingEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_projectionGate)
        {
            if (!ReferenceEquals(_armedRequest, processingEvent.Request) || _terminal)
            {
                return false;
            }

            if (processingEvent is RunFinished finished)
            {
                var finalization = FinalizeUnderLock(
                    processingEvent.Request,
                    finished.Result,
                    ProcessingRunFinalizationOrigin.WorkerTerminal,
                    processingEvent);
                return finalization.Disposition == ProcessingRunFinalizationDisposition.Committed;
            }

            if (processingEvent is ProgressChanged attemptedProgress)
            {
                // The disposition can follow an irreversible write/skip/failure decision.
                _lastProgress = attemptedProgress.Progress;
            }
            else if (processingEvent is RunStarted attemptedStart)
            {
                _startedAtUtc = attemptedStart.StartedAtUtc;
            }

            _beforeProjection?.Invoke(processingEvent);

            switch (processingEvent)
            {
                case RunStarted:
                    break;
                case EligibilityDetermined eligibility:
                    _eligibilityProjected = true;
                    _state.StartRun(eligibility.EligibleCount);
                    _state.AppendLog(eligibility.EligibleCount == 0
                        ? "Run started — nothing to process, all assets already have location data."
                        : $"Run started. {eligibility.EligibleCount} assets to process.");
                    break;
                case ProgressChanged progress:
                    _state.ApplyProgress(progress.Progress.UpdatedCount, progress.Progress.SkippedCount, progress.Progress.FailedCount);
                    break;
                case LogEmitted log:
                    ProjectLog(log);
                    break;
                case ActivityStarted activityStarted:
                    if (!_activities.ContainsKey(activityStarted.ActivityId))
                    {
                        _activities.Add(activityStarted.ActivityId, _state.BeginActivity(activityStarted.Label));
                    }
                    break;
                case ActivityEnded activityEnded:
                    EndActivity(activityEnded.ActivityId);
                    break;
            }
        }

        return true;
    }

    private void ProjectLog(LogEmitted log)
    {
        switch (log.Level)
        {
            case ProcessingLogLevel.Warning:
                _state.AppendLog($"[WARN] {log.Message}");
                break;
            case ProcessingLogLevel.Error:
                _state.ReportErrorDiagnostic(log.Message);
                break;
            case ProcessingLogLevel.Trace:
            case ProcessingLogLevel.Information:
                _state.AppendLog(log.Message);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(log));
        }
    }

    private ProcessingRunFinalizationAttempt FinalizeUnderLock(
        ProcessingRunRequest request,
        ProcessingRunResult result,
        ProcessingRunFinalizationOrigin origin,
        ProcessingEvent? terminalEvent)
    {
        if (_finalizationReceipt is not null)
        {
            return ReferenceEquals(_finalizationReceipt.Request, request)
                ? new ProcessingRunFinalizationAttempt(
                    ProcessingRunFinalizationDisposition.ExistingWinner,
                    _finalizationReceipt)
                : new ProcessingRunFinalizationAttempt(
                    ProcessingRunFinalizationDisposition.RejectedBeforeCommit,
                    null);
        }

        if (!ReferenceEquals(_armedRequest, request) || _terminal)
        {
            return new ProcessingRunFinalizationAttempt(
                ProcessingRunFinalizationDisposition.RejectedBeforeCommit,
                null);
        }

        var receipt = new ProcessingRunFinalizationReceipt(request, result, origin);
        _finalizationReceipt = receipt;
        ProjectTerminal(result, terminalEvent);
        return new ProcessingRunFinalizationAttempt(
            ProcessingRunFinalizationDisposition.Committed,
            receipt);
    }

    private void ProjectTerminal(ProcessingRunResult result, ProcessingEvent? terminalEvent)
    {
        ExceptionDispatchInfo? firstFailure = null;
        var progress = _lastProgress;
        var fallback = _eligibilityProjected
            ? ProcessingState.ProgressSnapshot.Empty
            : _armedProgress ?? _state.ReadProgressSnapshot();
        var updated = progress?.UpdatedCount ?? fallback.Processed;
        var skipped = progress?.SkippedCount ?? fallback.Skipped;
        var failed = progress?.FailedCount ?? fallback.Errors;
        var priorLastError = _state.LastError;
        var priorLog = _state.GetRecentLog();
        var completedAt = DateTime.UtcNow;

        void Attempt(Action mutation)
        {
            try
            {
                mutation();
            }
            catch (Exception failure)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(failure);
            }
        }

        try
        {
            if (terminalEvent is not null && _beforeProjection is not null)
            {
                Attempt(() => _beforeProjection(terminalEvent));
            }

            if (result.Outcome == ProcessingRunOutcome.Cancelled)
            {
                Attempt(() => _state.AppendLog("Run cancelled."));
            }
            else if (result.Outcome == ProcessingRunOutcome.Failed)
            {
                Attempt(() => _state.IncrementError($"Fatal: {result.FailureMessage}"));
            }

            foreach (var activity in _activities.Values)
            {
                Attempt(activity.Dispose);
            }

            _activities.Clear();
            Attempt(() => _state.CompleteRun(completedAt));
            Attempt(() => _state.AppendLog($"Run complete. Processed={_state.ProcessedThisRun} Skipped={_state.SkippedThisRun} Errors={_state.ErrorsThisRun}"));

            if (firstFailure is not null)
            {
                try
                {
                    _state.RestoreTerminalSnapshot(
                        updated,
                        skipped,
                        failed,
                        result.Outcome,
                        result.FailureMessage,
                        priorLastError,
                        completedAt,
                        priorLog);
                }
                catch
                {
                    // The canonical snapshot is committed before notification; preserve the first failure.
                }
            }
        }
        finally
        {
            try
            {
                ReleaseArm();
            }
            catch (Exception failure)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(failure);
            }
        }

        firstFailure?.Throw();
    }

    private void RestoreFailureSnapshot(Exception failure)
    {
        var progress = _lastProgress;
        var fallback = _eligibilityProjected
            ? ProcessingState.ProgressSnapshot.Empty
            : _armedProgress ?? _state.ReadProgressSnapshot();
        var updated = progress?.UpdatedCount ?? fallback.Processed;
        var skipped = progress?.SkippedCount ?? fallback.Skipped;
        var failed = progress?.FailedCount ?? fallback.Errors;
        try
        {
            _state.RestoreFatalFailureSnapshot(updated, skipped, failed, $"Fatal: {failure.Message}");
        }
        catch
        {
            // The snapshot is committed before notification; never recurse through the broken session.
        }
    }

    private void ReleaseArm()
    {
        var releasedRequest = _armedRequest;
        _terminal = true;
        _lastReleasedRequest = releasedRequest;
        _armedRequest = null;
        _startedAtUtc = null;
        _lastProgress = null;
        _armedProgress = null;
        _eligibilityProjected = false;
        if (releasedRequest is not null)
        {
            _controlObserver?.Invoke("release", releasedRequest);
        }
    }

    private void EndActivity(Guid activityId)
    {
        if (_activities.Remove(activityId, out var scope))
        {
            scope.Dispose();
        }
    }
}
