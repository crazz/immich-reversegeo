using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.ChildWorkerLaunching;

public sealed partial class ChildWorkerLaunchingTests
{
    [TestMethod]
    [TestCategory("Change53")]
    public async Task InventoryInvalidation_WaitsForRealMutationSessionResourceFinality()
    {
        var process = new ByteProcess(953);
        var factory = new RecordingFactory { Process = process };
        var inner = new CacheMutationWorkerClient(
            new CacheInvocationBuilder(),
            new ChildWorkerLauncher(factory),
            TimeProvider.System);
        var invalidator = new FinalityInvalidator(process);
        var client = new CacheInventoryMutationWorkerClient(inner, invalidator);
        var dispatch = new CacheMutationWorkerJobDispatch(
            Guid.Parse("53535353-5353-5353-5353-535353535353"),
            new CacheMutationRequest(
                CacheMutationSource.Overture,
                CacheMutationOperation.Refresh,
                "CHE"));
        var lease = new CacheAdmissionLease(dispatch);
        var sink = new CacheEventSink();
        CacheMutationWorkerStartResult start = await client.StartAsync(
            lease,
            dispatch.Request,
            sink,
            CancellationToken.None);
        ICacheMutationWorkerSession session =
            Assert.IsInstanceOfType<CacheMutationWorkerStartResult.Started>(start).Session;
        Task<CacheMutationWorkerOutcome> completion = session.Completion;
        DateTimeOffset startedAt = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        var result = new CacheMutationResult(
            startedAt,
            startedAt.AddSeconds(1),
            new CacheMutationSourceResult(
                CacheMutationSource.Overture,
                CacheMutationOperation.Refresh,
                "CHE",
                CacheMutationDisposition.Published,
                2,
                startedAt,
                4096,
                "2026-09-10.0",
                null));
        byte[] ready = Frame(WorkerJobProtocolMapper.Ready(
            1,
            startedAt.AddSeconds(-1),
            new WorkerJobReadyPayload([WorkerJobKind.CacheMutation])));
        byte[] jobStarted = Frame(WorkerJobProtocolMapper.JobStarted(
            dispatch.Context,
            "manual",
            startedAt,
            2));
        byte[] terminal = Frame(WorkerJobProtocolMapper.Terminal(
            dispatch.Context,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                result.StartedAtUtc,
                result.EndedAtUtc,
                null,
                null,
                result,
                null),
            3));
        try
        {
            process.StandardOutput.Write(ready);
            process.StandardOutput.Write(jobStarted);
            process.StandardOutput.Write(terminal);
            await process.StandardOutput.WaitForConsumedAsync(
                ready.LongLength + jobStarted.LongLength + terminal.LongLength);
            await sink.TerminalObserved.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, invalidator.Calls);
            Assert.IsFalse(completion.IsCompleted, "terminal-is-not-resource-finality");

            process.Exit(0);
            process.StandardOutput.Complete();
            Assert.AreEqual(0, invalidator.Calls);
            process.StandardError.Complete();

            await invalidator.Invalidated.WaitAsync(TimeSpan.FromSeconds(5));
            CacheMutationWorkerOutcome outcome = await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsInstanceOfType<CacheMutationWorkerOutcome.Completed>(outcome);
            Assert.AreEqual(1, invalidator.Calls);
            Assert.AreEqual(1, invalidator.ProcessDisposeCallsAtInvalidation);
            Assert.AreEqual(CacheMutationSource.Overture, invalidator.Source);
            Assert.AreEqual("CHE", invalidator.Iso3);
        }
        finally
        {
            process.StandardOutput.Complete();
            process.StandardError.Complete();
            process.Exit(0);
            await session.DisposeAsync();
        }
    }

    private sealed class FinalityInvalidator(ByteProcess process) : ICacheInventoryInvalidator
    {
        private readonly TaskCompletionSource _invalidated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Calls { get; private set; }
        internal int ProcessDisposeCallsAtInvalidation { get; private set; }
        internal CacheMutationSource? Source { get; private set; }
        internal string? Iso3 { get; private set; }
        internal Task Invalidated => _invalidated.Task;

        public void InvalidateKey(CacheMutationSource source, string iso3)
        {
            Calls++;
            Source = source;
            Iso3 = iso3;
            ProcessDisposeCallsAtInvalidation = process.DisposeCalls;
            _invalidated.TrySetResult();
        }

        public void InvalidateSource(CacheMutationSource source) =>
            throw new AssertFailedException();

        public void InvalidateAll() => throw new AssertFailedException();
    }
}
