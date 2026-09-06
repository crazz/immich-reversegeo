using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.WorkerFailureRecovery;

namespace ImmichReverseGeo.Web.Services;

internal interface IChildProcessingRunBackend
{
    Task<ProcessingRunResult> ExecuteAsync(
        ProcessingRunRequest request,
        IProcessingEventReporter reporter,
        CancellationToken cancellationToken);
}

internal sealed class ChildWorkerProcessingRunBackend(
    WorkerRunControlPlane controlPlane,
    ProcessingRunCoordinator coordinator) : IChildProcessingRunBackend
{
    public Task<ProcessingRunResult> ExecuteAsync(
        ProcessingRunRequest request,
        IProcessingEventReporter reporter,
        CancellationToken cancellationToken)
    {
        // The control plane uses the singleton state reporter, and the exact active
        // coordinator handle owns cancellation intent and child control. This adapter
        // deliberately adds no duplicate reporting or cancellation registration.
        return controlPlane.ExecuteAsync(coordinator, request);
    }
}
