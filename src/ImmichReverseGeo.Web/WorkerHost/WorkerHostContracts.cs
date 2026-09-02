using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Web.WorkerHost;

internal interface IWorkerStartupInitializer
{
    Task InitialiseAsync(CancellationToken cancellationToken);
}

internal interface IWorkerReadinessPublisher
{
    Task PublishAsync(CancellationToken cancellationToken);
}

internal interface IInitialProcessingRunAcquirer
{
    Task<InitialProcessingRunAcquisition> AcquireAsync(CancellationToken cancellationToken);
}

internal interface IProcessingRunLease : IAsyncDisposable
{
    ProcessingRunRequest Request { get; }

    CancellationToken CancellationToken { get; }

    ValueTask SettleAsync(CancellationToken cancellationToken);
}

internal interface IWorkerPreRequestFinality
{
    Task CompleteAsync(WorkerPreRequestOutcome outcome, CancellationToken cancellationToken);
}

internal interface IWorkerAcceptedRunFinality
{
    Task CompleteAsync(ProcessingRunRequest request, ProcessingRunResult result, CancellationToken cancellationToken);

    Task FailAsync(ProcessingRunRequest request, WorkerSafeFailure failure, CancellationToken cancellationToken);
}

internal interface IWorkerTransportAvailability
{
    bool IsConfigured { get; }
}

internal abstract class InitialProcessingRunAcquisition
{
    private InitialProcessingRunAcquisition()
    {
    }

    internal static Accepted Accept(IProcessingRunLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new Accepted(lease);
    }

    internal static PreRequestEof EndOfInput()
    {
        return new PreRequestEof();
    }

    internal static PreRequestFailure Fail(WorkerSafeFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new PreRequestFailure(failure);
    }

    internal sealed class Accepted : InitialProcessingRunAcquisition
    {
        internal Accepted(IProcessingRunLease lease)
        {
            Lease = lease;
        }

        internal IProcessingRunLease Lease { get; }
    }

    internal sealed class PreRequestEof : InitialProcessingRunAcquisition
    {
    }

    internal sealed class PreRequestFailure : InitialProcessingRunAcquisition
    {
        internal PreRequestFailure(WorkerSafeFailure failure)
        {
            Failure = failure;
        }

        internal WorkerSafeFailure Failure { get; }
    }
}

internal sealed class WorkerPreRequestOutcome
{
    private WorkerPreRequestOutcome(string category, WorkerSafeFailure? failure)
    {
        Category = category;
        SafeFailure = failure;
    }

    internal string Category { get; }

    internal WorkerSafeFailure? SafeFailure { get; }

    internal static WorkerPreRequestOutcome CleanEndOfInput()
    {
        return new WorkerPreRequestOutcome("worker-input-closed", null);
    }

    internal static WorkerPreRequestOutcome TransportNotConfigured()
    {
        return new WorkerPreRequestOutcome("worker-transport-not-configured", null);
    }

    internal static WorkerPreRequestOutcome Failure(WorkerSafeFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new WorkerPreRequestOutcome(failure.Category, failure);
    }
}

internal sealed class WorkerSafeFailure
{
    private WorkerSafeFailure(string category)
    {
        Category = category;
    }

    internal string Category { get; }

    internal static WorkerSafeFailure Startup()
    {
        return new WorkerSafeFailure("worker-startup-failed");
    }

    internal static WorkerSafeFailure Readiness()
    {
        return new WorkerSafeFailure("worker-readiness-failed");
    }

    internal static WorkerSafeFailure Acquisition()
    {
        return new WorkerSafeFailure("worker-request-acquisition-failed");
    }

    internal static WorkerSafeFailure AcceptedInfrastructure()
    {
        return new WorkerSafeFailure("worker-accepted-infrastructure-failed");
    }

    internal static WorkerSafeFailure Cleanup()
    {
        return new WorkerSafeFailure("worker-cleanup-failed");
    }
}
