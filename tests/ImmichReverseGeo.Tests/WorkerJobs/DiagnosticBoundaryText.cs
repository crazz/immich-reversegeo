using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerJobs;

internal sealed record DiagnosticBoundaryText(string Value, int V1FrameBytes);

internal static class DiagnosticBoundaryTextFactory
{
    internal static DiagnosticBoundaryText CreateLargestV1Log(
        ProcessingRunRequest request,
        long sequence,
        DateTimeOffset timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var candidate = string.Concat(Enumerable.Repeat("\"\\é", 400_000));
        var lower = 1;
        var upper = candidate.Length;
        var bestLength = 0;
        var bestFrameBytes = 0;
        while (lower <= upper)
        {
            var length = lower + ((upper - lower) / 2);
            try
            {
                byte[] frame = WorkerProtocolCodec.Serialize(
                    WorkerProtocolMapper.Map(
                        new LogEmitted(
                            request,
                            ProcessingLogLevel.Information,
                            candidate[..length]),
                        sequence,
                        timestampUtc));
                bestLength = length;
                bestFrameBytes = frame.Length;
                lower = length + 1;
            }
            catch (ArgumentException)
            {
                upper = length - 1;
            }
        }

        if (bestLength == 0)
        {
            throw new InvalidOperationException("No valid near-limit v1 diagnostic was found.");
        }

        return new DiagnosticBoundaryText(candidate[..bestLength], bestFrameBytes);
    }
}
