using System;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.Services;

internal interface IScheduledRunWorkCounter
{
    Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken);
}

internal sealed class RepositoryScheduledRunWorkCounter(Func<ImmichDbRepository> getRepository) : IScheduledRunWorkCounter
{
    public Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken)
    {
        return getRepository().GetUnprocessedCountAsync(cancellationToken);
    }
}

internal sealed class CountBackedProcessingWorkDetector : IProcessingWorkDetector
{
    private readonly Func<CancellationToken, Task<long>> _getUnprocessedCount;

    public CountBackedProcessingWorkDetector(Func<CancellationToken, Task<long>> getUnprocessedCount)
    {
        _getUnprocessedCount = getUnprocessedCount ?? throw new ArgumentNullException(nameof(getUnprocessedCount));
    }

    public async Task<ProcessingWorkDetectionResult> DetectAsync(
        ProcessingWorkDetectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // This Web-side count is advisory; an eligible worker repeats the authoritative
        // count under its own advisory lock before processing.
        long unprocessedCount = await _getUnprocessedCount(cancellationToken);
        return new ProcessingWorkDetectionResult(
            unprocessedCount > 0,
            new ProcessingWorkDetectionDiagnostics(
                ProcessingWorkDetectorKind.CountBacked,
                ProcessingWorkDetectionCoverage.FullEligibility,
                usedFallback: false));
    }
}
