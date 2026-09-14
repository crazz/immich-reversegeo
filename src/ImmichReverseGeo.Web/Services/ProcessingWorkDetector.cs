using System;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.Services;

internal interface IScheduledRunWorkProbe
{
    Task<bool> HasUnprocessedAssetsAsync(CancellationToken cancellationToken);
}

internal sealed class RepositoryScheduledRunWorkProbe(Func<ImmichDbRepository> getRepository) : IScheduledRunWorkProbe
{
    public Task<bool> HasUnprocessedAssetsAsync(CancellationToken cancellationToken)
    {
        return getRepository().HasUnprocessedAssetsAsync(cancellationToken);
    }
}

internal sealed class ExistenceProcessingWorkDetector : IProcessingWorkDetector
{
    private readonly Func<CancellationToken, Task<bool>> _hasUnprocessedAssets;

    public ExistenceProcessingWorkDetector(Func<CancellationToken, Task<bool>> hasUnprocessedAssets)
    {
        _hasUnprocessedAssets = hasUnprocessedAssets ?? throw new ArgumentNullException(nameof(hasUnprocessedAssets));
    }

    public async Task<ProcessingWorkDetectionResult> DetectAsync(
        ProcessingWorkDetectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // This observation is advisory; the worker still counts eligible assets
        // under its own advisory lock before processing.
        bool hasWork = await _hasUnprocessedAssets(cancellationToken);
        return new ProcessingWorkDetectionResult(
            hasWork,
            new ProcessingWorkDetectionDiagnostics(
                ProcessingWorkDetectorKind.Existence,
                ProcessingWorkDetectionCoverage.FullEligibility,
                usedFallback: false));
    }
}
