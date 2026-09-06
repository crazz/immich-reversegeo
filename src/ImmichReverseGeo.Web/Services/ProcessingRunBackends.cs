using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.WorkerFailureRecovery;

namespace ImmichReverseGeo.Web.Services;

internal enum ProcessingBackendKind
{
    InProcess,
    ChildWorker
}

internal sealed class TemporaryProcessingBackendSelection
{
    internal TemporaryProcessingBackendSelection(ProcessingBackendKind backend)
    {
        Backend = Validate(backend);
    }

    internal ProcessingBackendKind Backend { get; }

    internal static ProcessingBackendKind Validate(ProcessingBackendKind backend)
    {
        return backend switch
        {
            ProcessingBackendKind.InProcess => backend,
            ProcessingBackendKind.ChildWorker => backend,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unsupported processing backend.")
        };
    }
}

internal interface IProcessingRunBackend
{
    Task<ProcessingRunResult> ExecuteAsync(
        ProcessingRunRequest request,
        IProcessingEventReporter reporter,
        CancellationToken cancellationToken);
}

internal sealed class InProcessProcessingRunBackend(IProcessingRunExecutor executor) : IProcessingRunBackend
{
    public Task<ProcessingRunResult> ExecuteAsync(
        ProcessingRunRequest request,
        IProcessingEventReporter reporter,
        CancellationToken cancellationToken)
    {
        return executor.ExecuteAsync(request, reporter, cancellationToken);
    }
}

internal sealed class ChildWorkerProcessingRunBackend(
    WorkerRunControlPlane controlPlane,
    ProcessingRunCoordinator coordinator) : IProcessingRunBackend
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
