using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;

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

internal interface IWorkerRunLease : IAsyncDisposable
{
    WorkerJobContext Context { get; }

    IWorkerJobRequest JobRequest { get; }

    ProcessingRunRequest Request =>
        JobRequest is ProcessAssetsRequest processAssets
            ? processAssets.ProcessingRequest
            : throw new InvalidOperationException(
                "The accepted worker job is not a processing request.");

    CancellationToken CancellationToken { get; }

    void NotifyExecutionStarting();

    ValueTask<WorkerInputPumpFinality> SettleAsync(CancellationToken cancellationToken);
}

internal interface IProcessingRunLease : IWorkerRunLease
{
    new ProcessingRunRequest Request { get; }

    WorkerJobContext IWorkerRunLease.Context => new(
        Request.RunId,
        WorkerJobKind.ProcessAssets,
        Request.Trigger switch
        {
            ProcessingRunTrigger.Manual => WorkerJobRequestOrigin.Manual,
            ProcessingRunTrigger.Scheduled => WorkerJobRequestOrigin.Scheduled,
            ProcessingRunTrigger.RunOnce => WorkerJobRequestOrigin.RunOnce,
            _ => throw new ArgumentOutOfRangeException(nameof(Request))
        });

    IWorkerJobRequest IWorkerRunLease.JobRequest => new ProcessAssetsRequest(Request);
}

internal abstract class WorkerInputPumpFinality
{
    private WorkerInputPumpFinality()
    {
    }

    internal static WorkerInputPumpFinality ControlsClosed()
    {
        return new ControlsClosedFinality();
    }

    internal static WorkerInputPumpFinality InputFailure(WorkerSafeFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new InputFailureFinality(failure);
    }

    internal static WorkerInputPumpFinality ReaderFailure()
    {
        return new ReaderFailureFinality();
    }

    internal static WorkerInputPumpFinality ExpectedShutdown()
    {
        return new ExpectedShutdownFinality();
    }

    internal sealed class ControlsClosedFinality : WorkerInputPumpFinality
    {
    }

    internal sealed class InputFailureFinality : WorkerInputPumpFinality
    {
        internal InputFailureFinality(WorkerSafeFailure failure)
        {
            Failure = failure;
        }

        internal WorkerSafeFailure Failure { get; }
    }

    internal sealed class ReaderFailureFinality : WorkerInputPumpFinality
    {
    }

    internal sealed class ExpectedShutdownFinality : WorkerInputPumpFinality
    {
    }
}

internal interface IWorkerPreRequestFinality
{
    Task CompleteAsync(WorkerPreRequestOutcome outcome, CancellationToken cancellationToken);
}

internal interface IWorkerAcceptedRunFinality
{
    Task CompleteAsync(
        ProcessingRunRequest request,
        ProcessingRunResult result,
        CancellationToken cancellationToken);

    Task FailAsync(
        ProcessingRunRequest request,
        WorkerSafeFailure failure,
        CancellationToken cancellationToken);
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

    internal static Accepted Accept(IWorkerRunLease lease)
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
        internal Accepted(IWorkerRunLease lease)
        {
            Lease = lease;
        }

        internal IWorkerRunLease Lease { get; }
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
    private WorkerSafeFailure(string category, WorkerSafeFailureKind kind)
    {
        Category = category;
        Kind = kind;
    }

    internal string Category { get; }

    internal WorkerSafeFailureKind Kind { get; }

    internal static WorkerSafeFailure Startup()
    {
        return new WorkerSafeFailure("worker-startup-failed", WorkerSafeFailureKind.Infrastructure);
    }

    internal static WorkerSafeFailure Readiness()
    {
        return new WorkerSafeFailure("worker-readiness-failed", WorkerSafeFailureKind.Infrastructure);
    }

    internal static WorkerSafeFailure Acquisition()
    {
        return new WorkerSafeFailure("worker-request-acquisition-failed", WorkerSafeFailureKind.Infrastructure);
    }

    internal static WorkerSafeFailure AcceptedInfrastructure()
    {
        return new WorkerSafeFailure("worker-accepted-infrastructure-failed", WorkerSafeFailureKind.Infrastructure);
    }

    internal static WorkerSafeFailure Cleanup()
    {
        return new WorkerSafeFailure("worker-cleanup-failed", WorkerSafeFailureKind.Infrastructure);
    }

    internal static WorkerSafeFailure Input(WorkerProtocolFailureCode code)
    {
        return new WorkerSafeFailure($"worker-input-{code.ToString().ToLowerInvariant()}", WorkerSafeFailureKind.InputProtocol);
    }

    internal static WorkerSafeFailure Reader()
    {
        return new WorkerSafeFailure("worker-input-reader-failure", WorkerSafeFailureKind.Reader);
    }
}

internal enum WorkerSafeFailureKind
{
    Infrastructure,
    InputProtocol,
    Reader
}
