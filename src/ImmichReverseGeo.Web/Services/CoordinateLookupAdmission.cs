using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Web.Services;

internal sealed record WorkerJobBusyMetadata(
    WorkerJobCapabilityFamily CapabilityFamily,
    WorkerJobRequestOrigin Origin,
    bool IsCancellable);

internal interface IWorkerJobAdmissionLease : IAsyncDisposable
{
    WorkerJobContext Context { get; }
    WorkerJobDescriptor Descriptor { get; }
}

internal abstract record WorkerJobAdmissionResult
{
    private WorkerJobAdmissionResult()
    {
    }

    internal sealed record Admitted(IWorkerJobAdmissionLease Lease) : WorkerJobAdmissionResult;
    internal sealed record Busy(WorkerJobBusyMetadata ActiveJob) : WorkerJobAdmissionResult;
    internal sealed record Unavailable(string Code, string Message) : WorkerJobAdmissionResult;
}

internal interface IWorkerJobAdmissionGate
{
    WorkerJobAdmissionResult TryAdmit(WorkerJobDispatch dispatch);
}

/// <summary>
/// Temporary Change 49 implementation. Change 50 replaces this class and its
/// registration while retaining the admission contract above.
/// </summary>
internal sealed class TemporaryCoordinateLookupAdmissionGate : IWorkerJobAdmissionGate, IAsyncDisposable
{
    private readonly object _gate = new();
    private Lease? _active;
    private bool _unavailable;

    public WorkerJobAdmissionResult TryAdmit(WorkerJobDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (dispatch.Context.JobKind != WorkerJobKind.CoordinateLookup
            || !ReferenceEquals(dispatch.Descriptor, WorkerJobDescriptors.CoordinateLookup))
        {
            return new WorkerJobAdmissionResult.Unavailable(
                "lookup-admission-kind",
                "The lookup worker request is not supported.");
        }

        lock (_gate)
        {
            if (_unavailable)
            {
                return new WorkerJobAdmissionResult.Unavailable(
                    "lookup-admission-stopped",
                    "Lookup workers are unavailable while the Web host is stopping.");
            }

            if (_active is not null)
            {
                return new WorkerJobAdmissionResult.Busy(new WorkerJobBusyMetadata(
                    _active.Descriptor.Arbitration.CapabilityFamily,
                    _active.Context.Origin,
                    _active.Descriptor.Arbitration.IsCancellable));
            }

            var lease = new Lease(this, dispatch.Context, dispatch.Descriptor);
            _active = lease;
            return new WorkerJobAdmissionResult.Admitted(lease);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _unavailable = true;
        }

        return ValueTask.CompletedTask;
    }

    private void Release(Lease lease)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_active, lease))
            {
                _active = null;
            }
        }
    }

    private sealed class Lease : IWorkerJobAdmissionLease
    {
        private readonly TemporaryCoordinateLookupAdmissionGate _owner;
        private int _released;

        internal Lease(
            TemporaryCoordinateLookupAdmissionGate owner,
            WorkerJobContext context,
            WorkerJobDescriptor descriptor)
        {
            _owner = owner;
            Context = context;
            Descriptor = descriptor;
        }

        public WorkerJobContext Context { get; }
        public WorkerJobDescriptor Descriptor { get; }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.Release(this);
            }

            return ValueTask.CompletedTask;
        }
    }
}
