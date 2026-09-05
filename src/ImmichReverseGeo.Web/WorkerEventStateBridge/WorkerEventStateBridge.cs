using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Web.WorkerEventStateBridge;

internal sealed class WorkerEventStateBridge : IWorkerProtocolEventSink, IAsyncDisposable
{
    private const string ProjectionFailureDiagnostic = "Processing-state projection failed.";
    private const string ActivityCleanupFailureDiagnostic = "Projected activity cleanup failed.";
    private const string RunIdMismatchDiagnostic = "Event run ID does not match the bridge request.";
    private const string TriggerMismatchDiagnostic = "Event trigger does not match the bridge request.";
    private const string StateRejectionDiagnostic = "Processing-state projection no longer accepts the bridge request.";
    private const string ClosedProjectionDiagnostic = "The event has no supported state projection.";

    private readonly ProcessingStateEventReporter _reporter;
    private readonly WorkerProtocolEventStreamValidator _validator = new();
    private readonly SemaphoreSlim _projectionGate = new(1, 1);
    private readonly CancellationTokenSource _disposalCancellation = new();
    private readonly object _disposeGate = new();

    private WorkerEventStateBridgeObservation? _firstObservation;
    private WorkerEventStateBridgeObservation.ProjectionFailed? _poisonObservation;
    private Task? _disposeTask;
    private int _disposeStarted;
    private int _isReady;
    private int _isTerminal;

    internal WorkerEventStateBridge(ProcessingRunRequest request, ProcessingStateEventReporter reporter)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reporter);
        Request = request;
        _reporter = reporter;
    }

    internal ProcessingRunRequest Request { get; }
    internal bool IsReady => Volatile.Read(ref _isReady) != 0;
    internal bool IsTerminal => Volatile.Read(ref _isTerminal) != 0;
    internal WorkerEventStateBridgeObservation? FirstObservation => Volatile.Read(ref _firstObservation);

    public async ValueTask AcceptAsync(WorkerProtocolEvent @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (!await TryEnterProjectionAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await AcceptInsideGateAsync(@event).ConfigureAwait(false);
        }
        finally
        {
            _projectionGate.Release();
        }
    }

    private async ValueTask<bool> TryEnterProjectionAsync(CancellationToken cancellationToken)
    {
        if (IsDisposing)
        {
            return false;
        }

        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposalCancellation.Token);

        try
        {
            await _projectionGate.WaitAsync(waitCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsDisposing)
        {
            return false;
        }

        if (IsDisposing)
        {
            _projectionGate.Release();
            return false;
        }

        return true;
    }

    private async ValueTask AcceptInsideGateAsync(WorkerProtocolEvent @event)
    {
        if (_poisonObservation is not null)
        {
            throw new WorkerEventStateBridgeException(_poisonObservation);
        }

        var correlationFailure = ValidateExpectedRequest(@event);
        if (correlationFailure is not null)
        {
            throw Reject(correlationFailure);
        }

        var processingEvent = Map(@event);
        var preview = _validator.Preview(@event);
        if (!preview.IsSuccess)
        {
            throw Reject(preview.Failure!);
        }

        await ProjectAsync(processingEvent).ConfigureAwait(false);
        Commit(@event, processingEvent is not null);

        if (@event.Type == WorkerProtocolV1.ReadyType)
        {
            Volatile.Write(ref _isReady, 1);
        }
        else if (processingEvent is RunFinished)
        {
            Volatile.Write(ref _isTerminal, 1);
        }
    }

    private async ValueTask ProjectAsync(ProcessingEvent? processingEvent)
    {
        if (processingEvent is null)
        {
            if (!_reporter.IsArmed(Request))
            {
                throw Reject(new WorkerProtocolFailure(
                    WorkerProtocolFailureCode.InvalidCorrelation,
                    StateRejectionDiagnostic));
            }

            return;
        }

        bool projected;
        try
        {
            projected = await _reporter
                .TryProjectAsync(processingEvent, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            throw Poison(ProjectionFailureDiagnostic);
        }

        if (!projected)
        {
            throw Reject(new WorkerProtocolFailure(
                WorkerProtocolFailureCode.InvalidCorrelation,
                StateRejectionDiagnostic));
        }
    }

    private void Commit(WorkerProtocolEvent @event, bool stateWasProjected)
    {
        WorkerProtocolParseResult committed;
        try
        {
            committed = _validator.Validate(@event);
        }
        catch
        {
            throw Poison(ProjectionFailureDiagnostic);
        }

        if (committed.IsSuccess)
        {
            return;
        }

        if (stateWasProjected)
        {
            throw Poison(ProjectionFailureDiagnostic);
        }

        throw Reject(committed.Failure!);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposeStarted, 1);
                _disposalCancellation.Cancel();
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private bool IsDisposing => Volatile.Read(ref _disposeStarted) != 0;

    private async Task DisposeCoreAsync()
    {
        await _projectionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (IsTerminal)
            {
                return;
            }

            RecordObservation(WorkerEventStateBridgeObservation.NonterminalDisposed.Instance);

            try
            {
                _reporter.AbandonProjectedActivities(Request);
            }
            catch
            {
                var failure = new WorkerEventStateBridgeObservation.ProjectionFailed(
                    ActivityCleanupFailureDiagnostic);
                RecordObservation(failure);
                throw new WorkerEventStateBridgeException(failure);
            }
        }
        finally
        {
            _projectionGate.Release();
        }
    }

    private WorkerProtocolFailure? ValidateExpectedRequest(WorkerProtocolEvent @event)
    {
        if (@event.Type == WorkerProtocolV1.ReadyType)
        {
            return null;
        }

        if (@event.RunId != Request.RunId)
        {
            return new WorkerProtocolFailure(
                WorkerProtocolFailureCode.InvalidCorrelation,
                RunIdMismatchDiagnostic);
        }

        var trigger = @event.Payload switch
        {
            RunStartedPayload started => started.Trigger,
            TerminalPayload terminal => terminal.Trigger,
            _ => null
        };

        if (trigger is not null && trigger != WorkerProtocolConversions.Trigger(Request.Trigger))
        {
            return new WorkerProtocolFailure(
                WorkerProtocolFailureCode.InvalidCorrelation,
                TriggerMismatchDiagnostic);
        }

        return null;
    }

    private ProcessingEvent? Map(WorkerProtocolEvent @event)
    {
        return @event.Payload switch
        {
            ReadyPayload => null,
            RunStartedPayload started => new RunStarted(Request, started.StartedAtUtc),
            EligibilityDeterminedPayload eligibility => new EligibilityDetermined(
                Request,
                eligibility.EligibleCount),
            ProgressChangedPayload progress => new ProgressChanged(
                Request,
                new ProcessingProgress(
                    progress.ProcessedCount,
                    progress.UpdatedCount,
                    progress.SkippedCount,
                    progress.FailedCount)),
            ActivityStartedPayload activityStarted => new ActivityStarted(
                Request,
                activityStarted.ActivityId,
                activityStarted.Label),
            ActivityEndedPayload activityEnded => new ActivityEnded(
                Request,
                activityEnded.ActivityId),
            LogEmittedPayload log => new LogEmitted(
                Request,
                MapLogLevel(log.Level),
                log.Message),
            CompletedPayload completed => MapTerminal(completed, ProcessingRunOutcome.Completed),
            CancelledPayload cancelled => MapTerminal(cancelled, ProcessingRunOutcome.Cancelled),
            FailedPayload failed => MapTerminal(failed, ProcessingRunOutcome.Failed),
            _ => throw Reject(new WorkerProtocolFailure(
                WorkerProtocolFailureCode.InvalidPayload,
                ClosedProjectionDiagnostic))
        };
    }

    private RunFinished MapTerminal(TerminalPayload terminal, ProcessingRunOutcome outcome)
    {
        var result = new ProcessingRunResult(
            Request,
            terminal.StartedAtUtc,
            terminal.EndedAtUtc,
            terminal.ProcessedCount,
            terminal.UpdatedCount,
            terminal.SkippedCount,
            terminal.FailedCount,
            outcome,
            terminal.FailureMessage);
        return new RunFinished(Request, result);
    }

    private static ProcessingLogLevel MapLogLevel(string level)
    {
        return level switch
        {
            "trace" => ProcessingLogLevel.Trace,
            "information" => ProcessingLogLevel.Information,
            "warning" => ProcessingLogLevel.Warning,
            "error" => ProcessingLogLevel.Error,
            _ => throw new InvalidOperationException(ClosedProjectionDiagnostic)
        };
    }

    private WorkerEventStateBridgeException Reject(WorkerProtocolFailure failure)
    {
        var observation = new WorkerEventStateBridgeObservation.EventRejected(failure);
        RecordObservation(observation);
        return new WorkerEventStateBridgeException(observation);
    }

    private WorkerEventStateBridgeException Poison(string diagnostic)
    {
        _poisonObservation = new WorkerEventStateBridgeObservation.ProjectionFailed(diagnostic);
        RecordObservation(_poisonObservation);
        return new WorkerEventStateBridgeException(_poisonObservation);
    }

    private void RecordObservation(WorkerEventStateBridgeObservation observation)
    {
        Interlocked.CompareExchange(ref _firstObservation, observation, null);
    }
}
