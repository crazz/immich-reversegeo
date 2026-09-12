using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

// The only override is the disposable session boundary; production admission/controllers stay intact.
internal sealed class BoundaryWorkerSessions(BoundaryRuntimeSentinel events) : ICoordinateLookupWorkerClient, ICacheMutationWorkerClient
{
    internal bool Unavailable { get; set; }
    internal bool HoldLookup { get; set; }
    internal LookupSession? Lookup { get; private set; }
    internal int Sessions { get; private set; }
    internal int Disposals { get; private set; }

    public ValueTask<CoordinateLookupWorkerStartResult> StartAsync(IWorkerJobAdmissionLease admission,
        CoordinateLookupRequest request, IWorkerJobEventSink eventSink, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            return ValueTask.FromResult<CoordinateLookupWorkerStartResult>(new CoordinateLookupWorkerStartResult.Unavailable("test-unavailable", "Worker is unavailable."));
        }
        Sessions++;
        events.Record("Lookup admission", "fake worker session");
        Lookup = new LookupSession(admission.Context.JobId, () => Disposals++);
        if (!HoldLookup)
        {
            Lookup.Finish();
        }
        return ValueTask.FromResult<CoordinateLookupWorkerStartResult>(new CoordinateLookupWorkerStartResult.Started(Lookup));
    }

    public ValueTask<CacheMutationWorkerStartResult> StartAsync(IWorkerJobAdmissionLease admission,
        CacheMutationRequest request, IWorkerJobEventSink eventSink, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            return ValueTask.FromResult<CacheMutationWorkerStartResult>(new CacheMutationWorkerStartResult.Unavailable("test-unavailable", "Worker is unavailable."));
        }
        Sessions++;
        events.Record("cache admission", "fake worker session");
        return ValueTask.FromResult<CacheMutationWorkerStartResult>(new CacheMutationWorkerStartResult.Started(new CacheSession(admission.Context.JobId, () => Disposals++)));
    }

    internal sealed class LookupSession(Guid jobId, Action disposed) : ICoordinateLookupWorkerSession
    {
        private readonly TaskCompletionSource<CoordinateLookupWorkerOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid JobId => jobId;
        public WorkerJobKind JobKind => WorkerJobKind.CoordinateLookup;
        public InternalWorkerProtocolVersion ProtocolVersion => InternalWorkerProtocolVersion.V2;
        public bool IsCancellable => true;
        public Task<CoordinateLookupWorkerOutcome> Completion => _completion.Task;
        internal void Finish() => _completion.TrySetResult(new CoordinateLookupWorkerOutcome.Cancelled());
        public Task RequestStopAsync()
        {
            Finish();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            disposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CacheSession(Guid jobId, Action disposed) : ICacheMutationWorkerSession
    {
        public Guid JobId => jobId;
        public WorkerJobKind JobKind => WorkerJobKind.CacheMutation;
        public InternalWorkerProtocolVersion ProtocolVersion => InternalWorkerProtocolVersion.V2;
        public bool IsCancellable => true;
        public Task<CacheMutationWorkerOutcome> Completion => Task.FromResult<CacheMutationWorkerOutcome>(new CacheMutationWorkerOutcome.Cancelled());
        public Task RequestStopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            disposed();
            return ValueTask.CompletedTask;
        }
    }
}
