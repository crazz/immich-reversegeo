using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Core.Processing;

public interface IProcessingEventReporter
{
    ValueTask<IProcessingRunEventSession> OpenRunAsync(ProcessingRunRequest request, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default);
}

public interface IProcessingRunEventSession
{
    ProcessingRunRequest Request { get; }
    ValueTask DetermineEligibilityAsync(long eligibleCount, CancellationToken cancellationToken = default);
    ValueTask ReportUpdatedAsync();
    ValueTask ReportSkippedAsync();
    ValueTask ReportFailedAsync();
    ValueTask ReportLogAsync(ProcessingLogLevel level, string message, CancellationToken cancellationToken = default);
    ValueTask<IAsyncDisposable> BeginActivityAsync(string label, CancellationToken cancellationToken = default);
    ValueTask FinishAsync(ProcessingRunResult result);
}
