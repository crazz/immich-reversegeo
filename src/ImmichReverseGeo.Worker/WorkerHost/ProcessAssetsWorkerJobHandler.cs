using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;

namespace ImmichReverseGeo.Web.WorkerHost;

internal sealed class ProcessAssetsWorkerJobHandler :
    IWorkerJobHandler<ProcessAssetsRequest, ProcessAssetsResult>
{
    private readonly IWorkerStartupInitializer _initializer;
    private readonly IProcessingRunExecutor _executor;
    private readonly TimeProvider _timeProvider;

    internal ProcessAssetsWorkerJobHandler(
        IWorkerStartupInitializer initializer,
        IProcessingRunExecutor executor,
        TimeProvider timeProvider)
    {
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<ProcessAssetsResult> ExecuteAsync(
        WorkerJobContext context,
        ProcessAssetsRequest request,
        IWorkerJobEventReporter eventReporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventReporter);
        if (context.JobKind != WorkerJobKind.ProcessAssets
            || context.JobId != request.ProcessingRequest.RunId)
        {
            throw new ArgumentException(
                "The processing request must use the accepted worker-job identity.",
                nameof(request));
        }

        if (eventReporter is not IProcessAssetsWorkerJobHostReporter hostReporter)
        {
            throw new ArgumentException(
                "The processing handler requires the host lifecycle reporter.",
                nameof(eventReporter));
        }

        await _initializer.InitialiseAsync(cancellationToken).ConfigureAwait(false);
        var adapter = new ProcessAssetsProcessingEventAdapter(
            request.ProcessingRequest,
            hostReporter,
            _timeProvider);
        ProcessingRunResult result = await _executor.ExecuteAsync(
            request.ProcessingRequest,
            adapter,
            cancellationToken).ConfigureAwait(false);
        adapter.RequireMatchingTerminal(result);
        var typedResult = new ProcessAssetsResult(
            WorkerProtocolConversions.Trigger(result.Request.Trigger),
            result.StartedAtUtc,
            result.EndedAtUtc,
            result.ProcessedCount,
            result.UpdatedCount,
            result.SkippedCount,
            result.FailedCount);
        return result.Outcome switch
        {
            ProcessingRunOutcome.Completed => typedResult,
            ProcessingRunOutcome.Cancelled => throw new ProcessAssetsWorkerJobCancelledException(
                result,
                typedResult),
            ProcessingRunOutcome.Failed => throw new ProcessAssetsWorkerJobFailedException(
                result,
                typedResult),
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
    }
}

internal abstract class ProcessAssetsWorkerJobOutcomeException : Exception
{
    protected ProcessAssetsWorkerJobOutcomeException(
        ProcessingRunResult processingResult,
        ProcessAssetsResult typedResult)
        : base("The processing job returned a non-success outcome.")
    {
        ProcessingResult = processingResult;
        TypedResult = typedResult;
    }

    internal ProcessingRunResult ProcessingResult { get; }

    internal ProcessAssetsResult TypedResult { get; }
}

internal sealed class ProcessAssetsWorkerJobCancelledException(
    ProcessingRunResult processingResult,
    ProcessAssetsResult typedResult) :
    ProcessAssetsWorkerJobOutcomeException(processingResult, typedResult);

internal sealed class ProcessAssetsWorkerJobFailedException(
    ProcessingRunResult processingResult,
    ProcessAssetsResult typedResult) :
    ProcessAssetsWorkerJobOutcomeException(processingResult, typedResult);

internal interface IWorkerJobHostReporter : IWorkerJobEventReporter
{
    DateTimeOffset? StartedAtUtc { get; }

    ValueTask ReportStartedAsync(
        string trigger,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken);
}

internal interface IProcessAssetsWorkerJobHostReporter : IWorkerJobHostReporter
{

    ValueTask ReportStartedAsync(
        ProcessingRunRequest request,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken);
}

internal sealed class WorkerJobNdjsonEventReporter : IProcessAssetsWorkerJobHostReporter
{
    private readonly WorkerNdjsonEmitter _emitter;
    private readonly WorkerJobContext _context;

    internal WorkerJobNdjsonEventReporter(
        WorkerNdjsonEmitter emitter,
        WorkerJobContext context)
    {
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public ValueTask ReportAsync(
        WorkerJobHandlerEvent @event,
        CancellationToken cancellationToken) =>
        _emitter.SubmitJobEventAsync(_context, @event, cancellationToken);

    public ValueTask ReportStartedAsync(
        ProcessingRunRequest request,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        if (request.RunId != _context.JobId)
        {
            throw new ArgumentException(
                "The processing start event does not match the accepted worker job.",
                nameof(request));
        }

        return ReportStartedAsync(
            WorkerProtocolConversions.Trigger(request.Trigger),
            startedAtUtc,
            cancellationToken);
    }

    public ValueTask ReportStartedAsync(
        string trigger,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        if (StartedAtUtc is not null)
        {
            throw new InvalidOperationException("The worker job started more than once.");
        }

        StartedAtUtc = startedAtUtc;
        return _emitter.SubmitJobStartedAsync(
            _context,
            trigger,
            startedAtUtc,
            cancellationToken);
    }
}

internal sealed class ProcessAssetsProcessingEventAdapter : ProcessingEventReporter
{
    private readonly ProcessingRunRequest _request;
    private readonly IProcessAssetsWorkerJobHostReporter _reporter;
    private readonly TimeProvider _timeProvider;
    private ProcessingRunResult? _terminal;
    private bool _started;

    internal ProcessAssetsProcessingEventAdapter(
        ProcessingRunRequest request,
        IProcessAssetsWorkerJobHostReporter reporter,
        TimeProvider timeProvider)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    protected override ValueTask AcceptAsync(
        ProcessingEvent processingEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processingEvent);
        if (!ReferenceEquals(processingEvent.Request, _request))
        {
            throw new ArgumentException(
                "The processing event does not belong to the accepted worker job.",
                nameof(processingEvent));
        }

        if (processingEvent is RunStarted started)
        {
            if (_started)
            {
                throw new InvalidOperationException("The processing executor started more than once.");
            }

            _started = true;
            return _reporter.ReportStartedAsync(
                _request,
                started.StartedAtUtc,
                cancellationToken);
        }

        if (!_started)
        {
            throw new InvalidOperationException("Processing output requires the executor start event.");
        }

        if (processingEvent is RunFinished finished)
        {
            if (_terminal is not null)
            {
                throw new InvalidOperationException("The processing executor finished more than once.");
            }

            _terminal = finished.Result;
            return ValueTask.CompletedTask;
        }

        WorkerJobOutputPayload payload = processingEvent switch
        {
            EligibilityDetermined eligibility =>
                new ProcessAssetsEligibilityPayload(eligibility.EligibleCount),
            ProgressChanged progress => new ProcessAssetsProgressPayload(
                progress.Progress.ProcessedCount,
                progress.Progress.UpdatedCount,
                progress.Progress.SkippedCount,
                progress.Progress.FailedCount),
            ActivityStarted activityStarted => new WorkerJobActivityStartedPayload(
                activityStarted.ActivityId,
                activityStarted.Label),
            ActivityEnded activityEnded =>
                new WorkerJobActivityEndedPayload(activityEnded.ActivityId),
            LogEmitted log => new WorkerJobLogPayload(
                WorkerProtocolConversions.LogLevel(log.Level),
                log.Message),
            _ => throw new ArgumentOutOfRangeException(nameof(processingEvent))
        };
        return _reporter.ReportAsync(
            new WorkerJobHandlerEvent(_timeProvider.GetUtcNow(), payload),
            cancellationToken);
    }

    internal void RequireMatchingTerminal(ProcessingRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!ReferenceEquals(_terminal, result))
        {
            throw new InvalidOperationException(
                "The processing executor result must match its terminal event.");
        }
    }
}
