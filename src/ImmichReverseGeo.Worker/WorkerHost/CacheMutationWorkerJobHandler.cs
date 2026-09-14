using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Web.WorkerHost;

internal sealed class CacheMutationWorkerJobHandler :
    IWorkerJobHandler<CacheMutationRequest, CacheMutationResult>
{
    private readonly IWorkerCacheMutationOperation _operation;
    private readonly TimeProvider _timeProvider;

    internal CacheMutationWorkerJobHandler(
        IWorkerCacheMutationOperation operation,
        TimeProvider timeProvider)
    {
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<CacheMutationResult> ExecuteAsync(
        WorkerJobContext context,
        CacheMutationRequest request,
        IWorkerJobEventReporter eventReporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventReporter);
        if (context.JobKind != WorkerJobKind.CacheMutation
            || context.Origin != WorkerJobRequestOrigin.Manual)
        {
            throw new ArgumentException(
                "The cache request must use a manual CacheMutation worker job.",
                nameof(context));
        }

        if (eventReporter is not IWorkerJobHostReporter hostReporter)
        {
            throw new ArgumentException(
                "The cache handler requires the host lifecycle reporter.",
                nameof(eventReporter));
        }

        DateTimeOffset startedAtUtc = _timeProvider.GetUtcNow();
        await hostReporter.ReportStartedAsync(
            "manual",
            startedAtUtc,
            cancellationToken).ConfigureAwait(false);
        var reporter = new Reporter(request, eventReporter, _timeProvider);
        await reporter.ReportLogAsync(
            "information",
            $"Starting {request.Source} cache {request.Operation} for {request.Iso3}.",
            cancellationToken).ConfigureAwait(false);
        try
        {
            CacheMutationSourceResult sourceResult = await _operation.ExecuteAsync(
                request,
                reporter,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await reporter.ReportLogAsync(
                "information",
                $"Completed {request.Source} cache {request.Operation} for {request.Iso3}.",
                cancellationToken).ConfigureAwait(false);
            return new CacheMutationResult(
                startedAtUtc,
                _timeProvider.GetUtcNow(),
                sourceResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await reporter.ReportLogAsync(
                "information",
                $"Cancelled {request.Source} cache {request.Operation} for {request.Iso3}.",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await reporter.ReportLogAsync(
                "error",
                $"Failed {request.Source} cache {request.Operation} for {request.Iso3}.",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Reporter(
        CacheMutationRequest request,
        IWorkerJobEventReporter reporter,
        TimeProvider timeProvider) : ICacheMutationReporter
    {
        public ValueTask ReportProgressAsync(
            CacheMutationProgressPayload progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);
            if (progress.Source != request.Source
                || progress.Operation != request.Operation
                || !string.Equals(progress.Iso3, request.Iso3, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Cache progress does not match the accepted immutable request.",
                    nameof(progress));
            }

            return ReportAsync(progress, cancellationToken);
        }

        public ValueTask ReportLogAsync(
            string level,
            string message,
            CancellationToken cancellationToken) =>
            ReportAsync(
                new WorkerJobLogPayload(level, message),
                cancellationToken);

        public async ValueTask<ICacheMutationActivity> BeginActivityAsync(
            string label,
            CancellationToken cancellationToken)
        {
            var activityId = Guid.NewGuid();
            await ReportAsync(
                new WorkerJobActivityStartedPayload(activityId, label),
                cancellationToken).ConfigureAwait(false);
            return new Activity(activityId, reporter, timeProvider);
        }

        private ValueTask ReportAsync(
            WorkerJobOutputPayload payload,
            CancellationToken cancellationToken) =>
            reporter.ReportAsync(
                new WorkerJobHandlerEvent(timeProvider.GetUtcNow(), payload),
                cancellationToken);
    }

    private sealed class Activity(
        Guid activityId,
        IWorkerJobEventReporter reporter,
        TimeProvider timeProvider) : ICacheMutationActivity
    {
        private int _ended;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            return reporter.ReportAsync(
                new WorkerJobHandlerEvent(
                    timeProvider.GetUtcNow(),
                    new WorkerJobActivityEndedPayload(activityId)),
                CancellationToken.None);
        }
    }
}
