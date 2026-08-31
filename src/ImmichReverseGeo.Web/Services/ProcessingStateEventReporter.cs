using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Web.Services;

/// <summary>Projects the one admitted in-process event run into the singleton Web state.</summary>
public sealed class ProcessingStateEventReporter : ProcessingEventReporter
{
    private readonly ProcessingState _state;
    private readonly object _projectionGate = new();
    private readonly Dictionary<Guid, IDisposable> _activities = [];
    private ProcessingRunRequest? _armedRequest;
    private ProcessingRunRequest? _lastReleasedRequest;
    private bool _terminal;
    private ProcessingProgress? _lastProgress;
    private ProcessingState.ProgressSnapshot? _armedProgress;
    private bool _eligibilityProjected;
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
            _lastProgress = null;
            _armedProgress = _state.ReadProgressSnapshot();
            _eligibilityProjected = false;
            _controlObserver?.Invoke("arm", request);
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
        cancellationToken.ThrowIfCancellationRequested();

        lock (_projectionGate)
        {
            if (!ReferenceEquals(_armedRequest, processingEvent.Request) || _terminal)
            {
                return ValueTask.CompletedTask;
            }

            if (processingEvent is ProgressChanged attemptedProgress)
            {
                // The disposition can follow an irreversible write/skip/failure decision.
                _lastProgress = attemptedProgress.Progress;
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
                case RunFinished finished:
                    ProjectTerminal(finished.Result);
                    break;
            }
        }

        return ValueTask.CompletedTask;
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

    private void ProjectTerminal(ProcessingRunResult result)
    {
        if (_terminal)
        {
            return;
        }

        ExceptionDispatchInfo? firstFailure = null;

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
            Attempt(_state.CompleteRun);
            Attempt(() => _state.AppendLog($"Run complete. Processed={_state.ProcessedThisRun} Skipped={_state.SkippedThisRun} Errors={_state.ErrorsThisRun}"));

            if (firstFailure is not null)
            {
                RestoreFailureSnapshot(firstFailure.SourceException);
            }
        }
        finally
        {
            ReleaseArm();
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
