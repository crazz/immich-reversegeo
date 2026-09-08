using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Web.WorkerHost;

internal sealed class CoordinateLookupWorkerJobHandler :
    IWorkerJobHandler<CoordinateLookupRequest, CoordinateLookupResult>
{
    private readonly CoordinateLookupOperation _operation;
    private readonly TimeProvider _timeProvider;

    internal CoordinateLookupWorkerJobHandler(
        CoordinateLookupOperation operation,
        TimeProvider timeProvider)
    {
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<CoordinateLookupResult> ExecuteAsync(
        WorkerJobContext context,
        CoordinateLookupRequest request,
        IWorkerJobEventReporter eventReporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventReporter);
        if (context.JobKind != WorkerJobKind.CoordinateLookup)
        {
            throw new ArgumentException(
                "The coordinate request must use the CoordinateLookup worker-job kind.",
                nameof(context));
        }

        if (eventReporter is not IWorkerJobHostReporter hostReporter)
        {
            throw new ArgumentException(
                "The coordinate handler requires the host lifecycle reporter.",
                nameof(eventReporter));
        }

        DateTimeOffset startedAtUtc = _timeProvider.GetUtcNow();
        await hostReporter.ReportStartedAsync(
            "manual",
            startedAtUtc,
            cancellationToken).ConfigureAwait(false);
        return await _operation.ExecuteAsync(
            request,
            startedAtUtc,
            new CoordinateLookupHandlerEventReporter(eventReporter, _timeProvider),
            cancellationToken).ConfigureAwait(false);
    }

    private sealed class CoordinateLookupHandlerEventReporter : ICoordinateLookupEventReporter
    {
        private readonly IWorkerJobEventReporter _reporter;
        private readonly TimeProvider _timeProvider;

        internal CoordinateLookupHandlerEventReporter(
            IWorkerJobEventReporter reporter,
            TimeProvider timeProvider)
        {
            _reporter = reporter;
            _timeProvider = timeProvider;
        }

        public ValueTask ReportAsync(
            WorkerJobOutputPayload payload,
            CancellationToken cancellationToken) =>
            _reporter.ReportAsync(
                new WorkerJobHandlerEvent(_timeProvider.GetUtcNow(), payload),
                cancellationToken);
    }
}
