using System;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Web.WorkerEventStateBridge;

internal sealed class WorkerEventStateBridgeFactory
{
    private readonly ProcessingStateEventReporter _reporter;

    internal WorkerEventStateBridgeFactory(ProcessingStateEventReporter reporter)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        _reporter = reporter;
    }

    internal WorkerEventStateBridge Create(ProcessingRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_reporter.IsArmed(request))
        {
            throw new InvalidOperationException("The processing-state reporter is not armed for the admitted request.");
        }

        return new WorkerEventStateBridge(request, _reporter);
    }
}
