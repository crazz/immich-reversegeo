using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerFailureRecovery;

namespace ImmichReverseGeo.Web.Services;

internal sealed class CacheInventoryMutationWorkerClient(
    ICacheMutationWorkerClient inner,
    ICacheInventoryInvalidator invalidator) : ICacheMutationWorkerClient
{
    public async ValueTask<CacheMutationWorkerStartResult> StartAsync(
        IWorkerJobAdmissionLease admission,
        CacheMutationRequest request,
        IWorkerJobEventSink eventSink,
        CancellationToken cancellationToken)
    {
        CacheMutationWorkerStartResult result = await inner
            .StartAsync(admission, request, eventSink, cancellationToken)
            .ConfigureAwait(false);
        return result is CacheMutationWorkerStartResult.Started started
            ? new CacheMutationWorkerStartResult.Started(
                new Session(started.Session, invalidator),
                started.ChildProcessId)
            : result;
    }

    private sealed class Session(
        ICacheMutationWorkerSession inner,
        ICacheInventoryInvalidator invalidator) : ICacheMutationWorkerSession
    {
        private readonly object _gate = new();
        private Task<CacheMutationWorkerOutcome>? _completion;

        public Guid JobId => inner.JobId;
        public WorkerJobKind JobKind => inner.JobKind;
        public InternalWorkerProtocolVersion ProtocolVersion => inner.ProtocolVersion;
        public bool IsCancellable => inner.IsCancellable;

        public Task<CacheMutationWorkerOutcome> Completion
        {
            get
            {
                lock (_gate)
                {
                    return _completion ??= CompleteAsync();
                }
            }
        }

        public Task RequestStopAsync() => inner.RequestStopAsync();

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private async Task<CacheMutationWorkerOutcome> CompleteAsync()
        {
            CacheMutationWorkerOutcome outcome = await inner.Completion.ConfigureAwait(false);
            if (outcome is CacheMutationWorkerOutcome.Completed completed)
            {
                invalidator.InvalidateKey(
                    completed.Result.Cache.Source,
                    completed.Result.Cache.Iso3);
            }

            return outcome;
        }
    }
}
