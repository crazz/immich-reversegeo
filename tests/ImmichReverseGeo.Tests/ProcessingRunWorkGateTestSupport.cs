using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

internal sealed class AlwaysHasWorkScheduledRunGate : IProcessingWorkDetector
{
    internal static AlwaysHasWorkScheduledRunGate Instance { get; } = new();

    public Task<ProcessingWorkDetectionResult> DetectAsync(ProcessingWorkDetectionRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(ProcessingWorkDetectorStub.Result(true));
    }
}
