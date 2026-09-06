using System;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.ProcessingRunLocking;

internal interface IProcessingRunLock
{
    ValueTask<ProcessingRunLockAcquisition> AcquireAsync(CancellationToken cancellationToken);
}

internal abstract record ProcessingRunLockAcquisition
{
    private ProcessingRunLockAcquisition()
    {
    }

    internal sealed record Acquired(IProcessingRunLockLease Lease) : ProcessingRunLockAcquisition;

    internal sealed record Busy : ProcessingRunLockAcquisition;

    internal sealed record Cancelled : ProcessingRunLockAcquisition;

    internal sealed record InfrastructureFailure : ProcessingRunLockAcquisition;
}

internal interface IProcessingRunLockLease : IAsyncDisposable
{
    CancellationToken OwnershipLost { get; }

    bool IsOwnershipLost { get; }

    Task<ProcessingRunLockRelease> ReleaseAsync();
}

internal sealed record ProcessingRunLockRelease(bool InfrastructureFailure);
