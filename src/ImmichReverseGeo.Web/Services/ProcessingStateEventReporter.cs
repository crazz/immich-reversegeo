using System;
using System.Collections.Generic;
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
    private bool _terminal;
    private ProcessingProgress? _lastProgress;
    private ProcessingState.ProgressSnapshot? _armedProgress;
    private bool _eligibilityProjected;
    private readonly Action<ProcessingEvent>? _beforeProjection;

    public ProcessingStateEventReporter(ProcessingState state)
        : this(state, null)
    {
    }

    internal ProcessingStateEventReporter(ProcessingState state, Action<ProcessingEvent>? beforeProjection)
    {
        _state = state;
        _beforeProjection = beforeProjection;
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
            _terminal = false;
            _lastProgress = null;
            _armedProgress = _state.ReadProgressSnapshot();
            _eligibilityProjected = false;
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

            _terminal = true;
            _armedRequest = null;
            _lastProgress = null;
            _armedProgress = null;
            _eligibilityProjected = false;
            return true;
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

        if (result.Outcome == ProcessingRunOutcome.Cancelled)
        {
            _state.AppendLog("Run cancelled.");
        }
        else if (result.Outcome == ProcessingRunOutcome.Failed)
        {
            _state.IncrementError($"Fatal: {result.FailureMessage}");
        }

        foreach (var activity in _activities.Values)
        {
            activity.Dispose();
        }

        _activities.Clear();
        _state.CompleteRun();
        _state.AppendLog($"Run complete. Processed={_state.ProcessedThisRun} Skipped={_state.SkippedThisRun} Errors={_state.ErrorsThisRun}");
        _terminal = true;
        _armedRequest = null;
        _lastProgress = null;
        _armedProgress = null;
        _eligibilityProjected = false;
    }

    private void EndActivity(Guid activityId)
    {
        if (_activities.Remove(activityId, out var scope))
        {
            scope.Dispose();
        }
    }
}
