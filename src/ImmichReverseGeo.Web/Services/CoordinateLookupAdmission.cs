using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Web.Services;

internal enum WorkerJobLifecycle
{
    Admitted,
    Starting,
    Running,
    Stopping,
    Finalizing
}

internal sealed record WorkerJobBusyMetadata(
    WorkerJobKind JobKind,
    WorkerJobCapabilityFamily CapabilityFamily,
    WorkerJobRequestOrigin Origin,
    bool IsCancellable,
    WorkerJobLifecycle Lifecycle,
    DateTimeOffset AdmittedAtUtc,
    DateTimeOffset? StartedAtUtc)
{
    internal WorkerJobBusyMetadata(
        WorkerJobCapabilityFamily capabilityFamily,
        WorkerJobRequestOrigin origin,
        bool isCancellable)
        : this(
            capabilityFamily switch
            {
                WorkerJobCapabilityFamily.Processing => WorkerJobKind.ProcessAssets,
                WorkerJobCapabilityFamily.Lookup => WorkerJobKind.CoordinateLookup,
                WorkerJobCapabilityFamily.CacheMaintenance => WorkerJobKind.CacheMutation,
                _ => throw new ArgumentOutOfRangeException(nameof(capabilityFamily))
            },
            capabilityFamily,
            origin,
            isCancellable,
            WorkerJobLifecycle.Admitted,
            DateTimeOffset.UnixEpoch,
            null)
    {
    }
}

internal sealed record WorkerJobArbitrationDiagnosticSnapshot(
    bool IsAccepting,
    WorkerJobBusyMetadata? ActiveJob);

internal sealed record WorkerJobOwnerSnapshot(
    Guid JobId,
    WorkerJobKind JobKind,
    WorkerJobCapabilityFamily CapabilityFamily,
    WorkerJobRequestOrigin Origin,
    bool IsCancellable,
    WorkerJobLifecycle Lifecycle,
    DateTimeOffset AdmittedAtUtc,
    DateTimeOffset? StartedAtUtc,
    int? ChildProcessId);

internal interface IWorkerJobArbitrationDiagnostics
{
    WorkerJobArbitrationDiagnosticSnapshot Snapshot { get; }
    event Action? Changed;
}

internal interface IWorkerJobAdmissionLease : IAsyncDisposable
{
    WorkerJobContext Context { get; }
    WorkerJobDescriptor Descriptor { get; }

    bool IsStopRequested { get; }

    bool TryBindOwnerStop(
        WorkerJobContext context,
        Func<Task> requestStopAsync);

    bool TryAdvance(
        WorkerJobContext context,
        WorkerJobLifecycle lifecycle,
        int? childProcessId = null);
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

internal sealed class WorkerJobCoordinator :
    IWorkerJobAdmissionGate,
    IWorkerJobArbitrationDiagnostics,
    IHostedService,
    IAsyncDisposable,
    IDisposable
{
    private readonly object _gate = new();
    private readonly IReadOnlyDictionary<WorkerJobKind, WorkerJobDescriptor> _descriptors;
    private readonly TimeProvider _timeProvider;
    private readonly TaskCompletionSource _applicationStoppingRegistrationReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _applicationStoppingRegistration;
    private Lease? _active;
    private Task? _shutdownTask;
    private bool _admissionOpen = true;

    internal WorkerJobCoordinator(
        IEnumerable<WorkerJobDescriptor> descriptors,
        IHostApplicationLifetime? applicationLifetime = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        _descriptors = ValidateDescriptors(descriptors);
        _timeProvider = timeProvider ?? TimeProvider.System;
        try
        {
            _applicationStoppingRegistration = applicationLifetime?.ApplicationStopping.Register(
                BeginShutdownFromApplicationStopping) ?? default;
        }
        finally
        {
            _applicationStoppingRegistrationReady.TrySetResult();
        }
    }

    public event Action? Changed;

    public WorkerJobArbitrationDiagnosticSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return SnapshotUnderGate();
            }
        }
    }

    internal WorkerJobOwnerSnapshot? ActiveOwner
    {
        get
        {
            lock (_gate)
            {
                return _active?.OwnerSnapshotUnderGate;
            }
        }
    }

    public WorkerJobAdmissionResult TryAdmit(WorkerJobDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (!_descriptors.TryGetValue(dispatch.Context.JobKind, out WorkerJobDescriptor? descriptor)
            || !ReferenceEquals(descriptor, dispatch.Descriptor))
        {
            return new WorkerJobAdmissionResult.Unavailable(
                "worker-admission-kind",
                "The requested worker job is not available in this Web host.");
        }

        WorkerJobAdmissionResult result;
        lock (_gate)
        {
            if (!_admissionOpen)
            {
                result = new WorkerJobAdmissionResult.Unavailable(
                    "worker-admission-stopped",
                    "Worker jobs are unavailable while the Web host is stopping.");
            }
            else if (_active is not null)
            {
                result = new WorkerJobAdmissionResult.Busy(_active.SafeSnapshotUnderGate);
            }
            else
            {
                var lease = new Lease(
                    this,
                    dispatch.Context,
                    descriptor,
                    _timeProvider.GetUtcNow().ToUniversalTime());
                _active = lease;
                result = new WorkerJobAdmissionResult.Admitted(lease);
            }
        }

        if (result is WorkerJobAdmissionResult.Admitted)
        {
            NotifyChanged();
        }

        return result;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return BeginShutdown();
    }

    public ValueTask DisposeAsync() => new(BeginShutdown());

    public void Dispose()
    {
        BeginShutdown().GetAwaiter().GetResult();
    }

    internal Task BeginShutdown()
    {
        TaskCompletionSource start;
        Task shutdown;
        Lease? active;
        lock (_gate)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            _admissionOpen = false;
            active = _active;
            active?.MarkStopRequested();
            start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shutdown = CompleteShutdownAsync(active, start.Task);
            _shutdownTask = shutdown;
        }

        NotifyChanged();
        start.TrySetResult();
        return shutdown;
    }

    private async Task CompleteShutdownAsync(Lease? active, Task start)
    {
        await start.ConfigureAwait(false);
        try
        {
            if (active is not null)
            {
                await active.RequestShutdownStopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await _applicationStoppingRegistrationReady.Task.ConfigureAwait(false);
            await _applicationStoppingRegistration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void BeginShutdownFromApplicationStopping()
    {
        _ = BeginShutdown();
    }

    private bool TryBindOwnerStop(
        Lease lease,
        WorkerJobContext context,
        Func<Task> requestStopAsync)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requestStopAsync);
        lock (_gate)
        {
            if (!ReferenceEquals(_active, lease)
                || !Matches(lease.Context, context))
            {
                return false;
            }
        }

        return lease.TryBindOwnerStopCore(requestStopAsync);
    }

    private bool TryAdvance(
        Lease lease,
        WorkerJobContext context,
        WorkerJobLifecycle lifecycle,
        int? childProcessId)
    {
        bool changed;
        lock (_gate)
        {
            if (!ReferenceEquals(_active, lease)
                || !Matches(lease.Context, context))
            {
                return false;
            }

            changed = lease.TryAdvanceUnderGate(
                lifecycle,
                childProcessId,
                _timeProvider.GetUtcNow().ToUniversalTime());
        }

        if (changed)
        {
            NotifyChanged();
        }

        return changed;
    }

    private void Release(Lease lease)
    {
        bool changed = false;
        lock (_gate)
        {
            if (ReferenceEquals(_active, lease)
                && Matches(_active.Context, lease.Context))
            {
                _active = null;
                changed = true;
            }
        }

        lease.CompleteRelease();
        if (changed)
        {
            NotifyChanged();
        }
    }

    private WorkerJobArbitrationDiagnosticSnapshot SnapshotUnderGate()
    {
        return new WorkerJobArbitrationDiagnosticSnapshot(
            _admissionOpen,
            _active?.SafeSnapshotUnderGate);
    }

    private void NotifyChanged()
    {
        Action? handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch
            {
                // Diagnostics are observational and cannot own admission or finality.
            }
        }
    }

    private static IReadOnlyDictionary<WorkerJobKind, WorkerJobDescriptor> ValidateDescriptors(
        IEnumerable<WorkerJobDescriptor> descriptors)
    {
        var registered = new Dictionary<WorkerJobKind, WorkerJobDescriptor>();
        foreach (WorkerJobDescriptor descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            WorkerJobArbitrationMetadata metadata = descriptor.Arbitration;
            if (!Enum.IsDefined(descriptor.Kind)
                || !Enum.IsDefined(metadata.CapabilityFamily)
                || !Enum.IsDefined(metadata.ResourceClass)
                || metadata.ResourceClass != WorkerJobResourceClass.ExclusiveHeavyWorker
                || !metadata.IsHeavy
                || !metadata.IsGeodataBearing)
            {
                throw new InvalidOperationException(
                    $"The {descriptor.Kind} worker descriptor has inconsistent arbitration metadata.");
            }

            WorkerJobCapabilityFamily expectedFamily = descriptor.Kind switch
            {
                WorkerJobKind.ProcessAssets => WorkerJobCapabilityFamily.Processing,
                WorkerJobKind.CoordinateLookup => WorkerJobCapabilityFamily.Lookup,
                WorkerJobKind.CacheMutation => WorkerJobCapabilityFamily.CacheMaintenance,
                _ => throw new InvalidOperationException("A worker coordinator descriptor has an unknown job kind.")
            };
            if (metadata.CapabilityFamily != expectedFamily)
            {
                throw new InvalidOperationException(
                    $"The {descriptor.Kind} worker descriptor has an inconsistent capability family.");
            }

            if (!registered.TryAdd(descriptor.Kind, descriptor))
            {
                throw new InvalidOperationException(
                    $"The {descriptor.Kind} worker descriptor is registered more than once.");
            }
        }

        if (registered.Count == 0)
        {
            throw new InvalidOperationException("At least one exclusive heavy worker descriptor is required.");
        }

        return registered;
    }

    private static bool Matches(WorkerJobContext expected, WorkerJobContext actual)
    {
        return expected.JobId == actual.JobId
            && expected.JobKind == actual.JobKind
            && expected.Origin == actual.Origin;
    }

    private sealed class Lease : IWorkerJobAdmissionLease
    {
        private readonly WorkerJobCoordinator _owner;
        private readonly object _bindingGate = new();
        private readonly TaskCompletionSource<Func<Task>?> _stopBinding =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _shutdownStopTask;
        private int _releasedState;
        private int _stopRequested;
        private WorkerJobLifecycle _lifecycle = WorkerJobLifecycle.Admitted;
        private DateTimeOffset? _startedAtUtc;
        private int? _childProcessId;

        internal Lease(
            WorkerJobCoordinator owner,
            WorkerJobContext context,
            WorkerJobDescriptor descriptor,
            DateTimeOffset admittedAtUtc)
        {
            _owner = owner;
            Context = context;
            Descriptor = descriptor;
            AdmittedAtUtc = admittedAtUtc;
        }

        public WorkerJobContext Context { get; }
        public WorkerJobDescriptor Descriptor { get; }
        public bool IsStopRequested => Volatile.Read(ref _stopRequested) != 0;
        private DateTimeOffset AdmittedAtUtc { get; }

        internal WorkerJobBusyMetadata SafeSnapshotUnderGate => new(
            Context.JobKind,
            Descriptor.Arbitration.CapabilityFamily,
            Context.Origin,
            Descriptor.Arbitration.IsCancellable,
            _lifecycle,
            AdmittedAtUtc,
            _startedAtUtc);

        internal WorkerJobOwnerSnapshot OwnerSnapshotUnderGate => new(
            Context.JobId,
            Context.JobKind,
            Descriptor.Arbitration.CapabilityFamily,
            Context.Origin,
            Descriptor.Arbitration.IsCancellable,
            _lifecycle,
            AdmittedAtUtc,
            _startedAtUtc,
            _childProcessId);

        public bool TryBindOwnerStop(
            WorkerJobContext context,
            Func<Task> requestStopAsync)
        {
            return _owner.TryBindOwnerStop(this, context, requestStopAsync);
        }

        public bool TryAdvance(
            WorkerJobContext context,
            WorkerJobLifecycle lifecycle,
            int? childProcessId = null)
        {
            return _owner.TryAdvance(this, context, lifecycle, childProcessId);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _releasedState, 1) == 0)
            {
                _owner.Release(this);
            }

            return ValueTask.CompletedTask;
        }

        internal void MarkStopRequested()
        {
            Volatile.Write(ref _stopRequested, 1);
        }

        internal Task RequestShutdownStopAsync()
        {
            MarkStopRequested();
            TaskCompletionSource? start = null;
            Task shutdownStopTask;
            lock (_bindingGate)
            {
                if (_shutdownStopTask is null)
                {
                    start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _shutdownStopTask = StopAndAwaitReleaseAsync(start.Task);
                }

                shutdownStopTask = _shutdownStopTask;
            }

            start?.TrySetResult();
            return shutdownStopTask;
        }

        internal bool TryBindOwnerStopCore(Func<Task> requestStopAsync)
        {
            lock (_bindingGate)
            {
                if (Volatile.Read(ref _releasedState) != 0
                    || _stopBinding.Task.IsCompleted)
                {
                    return false;
                }

                return _stopBinding.TrySetResult(requestStopAsync);
            }
        }

        internal bool TryAdvanceUnderGate(
            WorkerJobLifecycle lifecycle,
            int? childProcessId,
            DateTimeOffset observedAtUtc)
        {
            if (!Enum.IsDefined(lifecycle)
                || Volatile.Read(ref _releasedState) != 0)
            {
                return false;
            }

            if (childProcessId is <= 0
                || (childProcessId is not null && lifecycle != WorkerJobLifecycle.Running))
            {
                return false;
            }

            if (lifecycle < _lifecycle)
            {
                if (lifecycle == WorkerJobLifecycle.Running
                    && childProcessId is not null
                    && _childProcessId is null)
                {
                    _childProcessId = childProcessId;
                    _startedAtUtc ??= observedAtUtc;
                    return true;
                }

                return false;
            }

            if (lifecycle == _lifecycle)
            {
                return false;
            }

            if (lifecycle == WorkerJobLifecycle.Running)
            {
                if (childProcessId is null || _childProcessId is not null)
                {
                    return false;
                }

                _childProcessId = childProcessId;
            }

            if (lifecycle >= WorkerJobLifecycle.Starting)
            {
                _startedAtUtc ??= observedAtUtc;
            }

            _lifecycle = lifecycle;
            return true;
        }

        internal void CompleteRelease()
        {
            _stopBinding.TrySetResult(null);
            _released.TrySetResult();
        }

        private async Task StopAndAwaitReleaseAsync(Task start)
        {
            await start.ConfigureAwait(false);
            Func<Task>? stop = await _stopBinding.Task.ConfigureAwait(false);
            ExceptionDispatchInfo? stopFailure = null;
            if (stop is not null)
            {
                try
                {
                    await stop().ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    stopFailure = ExceptionDispatchInfo.Capture(failure);
                }
            }

            await _released.Task.ConfigureAwait(false);
            stopFailure?.Throw();
        }
    }
}
