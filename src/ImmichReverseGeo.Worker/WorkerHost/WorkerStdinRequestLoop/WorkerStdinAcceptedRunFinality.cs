using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;

internal sealed class WorkerStdinAcceptedRunFinality(WorkerStdinRequestSource source) : IWorkerAcceptedRunFinality
{
    public Task CompleteAsync(
        ProcessingRunRequest request,
        ProcessingRunResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (!ReferenceEquals(request, result.Request))
        {
            throw new InvalidOperationException("The processing result does not belong to the accepted request.");
        }

        return source.NotifyTerminalAsync(request, cancellationToken);
    }

    public Task FailAsync(
        ProcessingRunRequest request,
        WorkerSafeFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(failure);
        return source.NotifyTerminalAsync(request, cancellationToken);
    }
}
