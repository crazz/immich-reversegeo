using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Web.WorkerHost;

internal sealed class SkippedAssetsWorkerStartupInitializer(SkippedAssetsRepository skippedAssetsRepository) : IWorkerStartupInitializer
{
    public async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        await skippedAssetsRepository.InitialiseAsync(cancellationToken);
    }
}

internal sealed class WorkerStdinTransportConfigured : IWorkerTransportAvailability
{
    public bool IsConfigured => true;
}

internal sealed class TransitionalWorkerPreRequestFinality : IWorkerPreRequestFinality
{
    internal WorkerPreRequestOutcome? Outcome { get; private set; }

    public Task CompleteAsync(WorkerPreRequestOutcome outcome, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Outcome = outcome;
        return Task.CompletedTask;
    }
}
