using System.Collections.Immutable;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.CacheInventory;

[TestClass]
public sealed class CacheInventoryAdapterTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    [TestCategory("Change53")]
    public async Task MutationCompletion_IsLazyAndPreservesExactSuccessfulOutcome()
    {
        var invalidator = new RecordingInvalidator();
        var innerSession = new FakeMutationSession(Guid.NewGuid());
        var client = new CacheInventoryMutationWorkerClient(
            new FakeMutationClient(innerSession),
            invalidator);
        var lease = new FakeLease(innerSession.JobId);
        CacheMutationWorkerStartResult started = await client.StartAsync(
            lease,
            new CacheMutationRequest(
                CacheMutationSource.Overture,
                CacheMutationOperation.Refresh,
                "CHE"),
            new NoOpEventSink(),
            CancellationToken.None);
        ICacheMutationWorkerSession decorated =
            ((CacheMutationWorkerStartResult.Started)started).Session;

        Assert.AreEqual(0, innerSession.CompletionReads, "constructor-must-not-observe-completion");
        Task<CacheMutationWorkerOutcome> completion = decorated.Completion;
        Assert.AreEqual(1, innerSession.CompletionReads);
        Assert.AreEqual(0, invalidator.Keys.Count);
        var expected = new CacheMutationWorkerOutcome.Completed(Result());
        innerSession.Complete(expected);

        CacheMutationWorkerOutcome actual = await completion.WaitAsync(Bound);
        Assert.AreSame(expected, actual);
        CollectionAssert.AreEqual(
            new[] { (CacheMutationSource.Overture, "CHE") },
            invalidator.Keys);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task MutationAlreadyReadyCompletion_IsLazyAndPreservesExactOutcome()
    {
        var invalidator = new RecordingInvalidator();
        var innerSession = new FakeMutationSession(Guid.NewGuid());
        var client = new CacheInventoryMutationWorkerClient(
            new FakeMutationClient(innerSession),
            invalidator);
        var lease = new FakeLease(innerSession.JobId);
        CacheMutationWorkerStartResult started = await client.StartAsync(
            lease,
            new CacheMutationRequest(
                CacheMutationSource.Overture,
                CacheMutationOperation.Ensure,
                "CHE"),
            new NoOpEventSink(),
            CancellationToken.None);
        ICacheMutationWorkerSession decorated =
            ((CacheMutationWorkerStartResult.Started)started).Session;

        Assert.AreEqual(0, innerSession.CompletionReads);
        Task<CacheMutationWorkerOutcome> completion = decorated.Completion;
        Assert.AreEqual(1, innerSession.CompletionReads);
        var expected = new CacheMutationWorkerOutcome.Completed(Result(
            CacheMutationSource.Overture,
            CacheMutationOperation.Ensure,
            "CHE",
            CacheMutationDisposition.AlreadyReady));
        innerSession.Complete(expected);

        CacheMutationWorkerOutcome actual = await completion.WaitAsync(Bound);

        Assert.AreSame(expected, actual);
        CollectionAssert.AreEqual(
            new[] { (CacheMutationSource.Overture, "CHE") },
            invalidator.Keys);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task CorrelationRejectedSession_DisposesWithoutObservingOrInvalidatingCompletion()
    {
        var invalidator = new RecordingInvalidator();
        Guid admittedJobId = Guid.NewGuid();
        var mismatched = new FakeMutationSession(Guid.NewGuid());
        mismatched.Complete(new CacheMutationWorkerOutcome.Completed(Result()));
        var client = new CacheInventoryMutationWorkerClient(
            new FakeMutationClient(mismatched),
            invalidator);
        var gate = new FakeAdmissionGate(admittedJobId);
        await using var controller = new CacheMutationPageController(
            gate,
            client,
            () => admittedJobId,
            static () => Task.CompletedTask,
            static () => Task.CompletedTask);

        await controller.RefreshAsync(CacheMutationSource.Overture, "CHE").WaitAsync(Bound);

        Assert.AreEqual(CacheMutationPagePhase.Failed, controller.State.Phase);
        Assert.AreEqual(0, mismatched.CompletionReads);
        Assert.AreEqual(1, mismatched.DisposeCalls);
        Assert.AreEqual(0, invalidator.Keys.Count);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task MutationNonSuccessOutcomes_PreserveIdentityWithoutInvalidation()
    {
        CacheMutationWorkerOutcome[] outcomes =
        [
            new CacheMutationWorkerOutcome.Cancelled(),
            new CacheMutationWorkerOutcome.Unavailable("unavailable", "safe"),
            new CacheMutationWorkerOutcome.Failed("failed", "safe")
        ];

        foreach (CacheMutationWorkerOutcome expected in outcomes)
        {
            var invalidator = new RecordingInvalidator();
            var innerSession = new FakeMutationSession(Guid.NewGuid());
            var client = new CacheInventoryMutationWorkerClient(
                new FakeMutationClient(innerSession),
                invalidator);
            var lease = new FakeLease(innerSession.JobId);
            CacheMutationWorkerStartResult started = await client.StartAsync(
                lease,
                new CacheMutationRequest(
                    CacheMutationSource.Overture,
                    CacheMutationOperation.Refresh,
                    "CHE"),
                new NoOpEventSink(),
                CancellationToken.None);
            ICacheMutationWorkerSession decorated =
                ((CacheMutationWorkerStartResult.Started)started).Session;
            Task<CacheMutationWorkerOutcome> completion = decorated.Completion;

            innerSession.Complete(expected);
            CacheMutationWorkerOutcome actual = await completion.WaitAsync(Bound);

            Assert.AreSame(expected, actual);
            Assert.AreEqual(0, invalidator.Keys.Count);
        }
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task DeletionAfterDisposedPage_InvalidatesBeforeOrdinaryInventoryReuse()
    {
        var fileSystem = new BlockingDeletionFileSystem();
        var scanner = new CountingScanner(() => fileSystem.Exists);
        await using var inventory = new CacheInventoryService(scanner);
        CacheInventorySnapshot primed = await inventory.GetSnapshotAsync();
        Assert.AreEqual(CacheInventoryEntryStatus.Available,
            primed.Sources.Single().Entries.Single().Status);
        var reservation = new Reservation();
        CacheDeletionCommand command = CreateDeletionCommand(
            fileSystem,
            new DeletionAdmissionGate(reservation));
        var invalidationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidator = new ObservingInvalidator(
            inventory,
            () =>
            {
                Assert.IsFalse(fileSystem.Exists, "storage-is-deleted-before-invalidation");
                Assert.AreEqual(1, reservation.DisposeCalls,
                    "maintenance-reservation-is-released-before-invalidation");
                invalidationObserved.TrySetResult();
            });
        var operations = new CacheInventoryDeletionOperations(command, invalidator);
        var reloads = 0;
        var renders = 0;
        CacheDeletionOperationResult? authoritativeResult = null;
        await using var controller = new CacheDeletionPageController(
            async target =>
            {
                authoritativeResult = await operations.DeleteAsync(target);
                return authoritativeResult;
            },
            operations.DeleteAllAsync,
            () =>
            {
                renders++;
                return Task.CompletedTask;
            },
            () =>
            {
                reloads++;
                return Task.CompletedTask;
            });
        controller.RequestDelete(new CacheDeletionTarget(CacheMutationSource.Overture, "CHE"));
        long confirmation = controller.State.Confirmation!.Generation;
        Task deletion = controller.ConfirmAsync(confirmation);
        Task? disposal = null;
        try
        {
            await fileSystem.DeleteStarted.Task.WaitAsync(Bound);
            int rendersBeforeDispose = renders;
            disposal = controller.DisposeAsync().AsTask();
            fileSystem.AllowDelete.TrySetResult();
            await invalidationObserved.Task.WaitAsync(Bound);
            await Task.WhenAll(deletion, disposal).WaitAsync(Bound);
            CacheInventorySnapshot afterDelete = await inventory.GetSnapshotAsync().WaitAsync(Bound);

            Assert.IsNotNull(authoritativeResult, "authoritative-result-crosses-adapter-before-ui-gate");
            Assert.AreEqual(CacheDeletionOperationDisposition.Completed,
                authoritativeResult.Disposition);
            Assert.AreEqual(CacheDeletionTargetDisposition.Deleted,
                authoritativeResult.Targets.Single().Disposition);
            Assert.IsFalse(fileSystem.Exists, "actual-storage-state-deleted");
            Assert.AreEqual(1, invalidator.Keys.Count);
            Assert.AreEqual(0, reloads, "disposed-page-must-not-reload");
            Assert.AreEqual(rendersBeforeDispose, renders,
                "disposed-page-must-not-render-authoritative-result");
            Assert.AreEqual(2, scanner.ScanCalls,
                "ordinary-get-rescans-after-authoritative-delete");
            Assert.AreEqual(0, afterDelete.Sources.Single().Entries.Length,
                "ordinary-get-observes-deleted-storage");
        }
        finally
        {
            fileSystem.AllowDelete.TrySetResult();
            await DrainAsync(deletion);
            if (disposal is null)
            {
                disposal = controller.DisposeAsync().AsTask();
            }

            await DrainAsync(disposal);
        }
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task PartialDeleteAll_InvalidatesOnlyActualDeletedKeysAndPreservesFailures()
    {
        var invalidator = new RecordingInvalidator();
        var fileSystem = new BlockingDeletionFileSystem
        {
            FailedBasename = "JPN.db"
        };
        fileSystem.AllowDelete.TrySetResult();
        var operations = new CacheInventoryDeletionOperations(
            CreateDeletionCommand(fileSystem),
            invalidator);

        CacheDeletionOperationResult result = await operations.DeleteAllAsync(
            CacheMutationSource.Overture,
            [
                new CacheDeletionTarget(CacheMutationSource.Overture, "CHE"),
                new CacheDeletionTarget(CacheMutationSource.Overture, "JPN")
            ]);

        Assert.AreEqual(1, result.DeletedCount);
        Assert.AreEqual(1, result.FailedCount);
        CollectionAssert.AreEqual(
            new[] { (CacheMutationSource.Overture, "CHE") },
            invalidator.Keys);
        Assert.AreEqual(
            CacheDeletionTargetDisposition.Failed,
            result.Targets.Single(target => target.Iso3 == "JPN").Disposition);
    }

    private static CacheDeletionCommand CreateDeletionCommand(
        ICacheDeletionFileSystem fileSystem,
        IWorkerJobAdmissionGate? admissionGate = null) =>
        new(
            admissionGate ?? new DeletionAdmissionGate(new Reservation()),
            fileSystem,
            new StorageOptions(
                Path.Combine(Path.GetTempPath(), "cache-inventory-adapter"),
                Path.Combine(AppContext.BaseDirectory, "data")),
            CountryCodeService.CreateForTest(Path.Combine(AppContext.BaseDirectory, "data")),
            NullLogger<CacheDeletionCommand>.Instance);

    private static CacheMutationResult Result(
        CacheMutationSource source = CacheMutationSource.Overture,
        CacheMutationOperation operation = CacheMutationOperation.Refresh,
        string iso3 = "CHE",
        CacheMutationDisposition disposition = CacheMutationDisposition.Published)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new CacheMutationResult(
            now,
            now,
            new CacheMutationSourceResult(
                source,
                operation,
                iso3,
                disposition,
                1,
                now,
                1,
                "test-release",
                null));
    }

    private sealed class RecordingInvalidator : ICacheInventoryInvalidator
    {
        internal List<(CacheMutationSource Source, string Iso3)> Keys { get; } = [];

        public void InvalidateKey(CacheMutationSource source, string iso3) => Keys.Add((source, iso3));
        public void InvalidateSource(CacheMutationSource source) => throw new AssertFailedException();
        public void InvalidateAll() => throw new AssertFailedException();
    }

    private sealed class ObservingInvalidator(
        ICacheInventoryInvalidator inner,
        Action beforeInvalidation) : ICacheInventoryInvalidator
    {
        internal List<(CacheMutationSource Source, string Iso3)> Keys { get; } = [];

        public void InvalidateKey(CacheMutationSource source, string iso3)
        {
            beforeInvalidation();
            Keys.Add((source, iso3));
            inner.InvalidateKey(source, iso3);
        }

        public void InvalidateSource(CacheMutationSource source) => inner.InvalidateSource(source);
        public void InvalidateAll() => inner.InvalidateAll();
    }

    private sealed class FakeMutationClient(FakeMutationSession session) : ICacheMutationWorkerClient
    {
        public ValueTask<CacheMutationWorkerStartResult> StartAsync(
            IWorkerJobAdmissionLease admission,
            CacheMutationRequest request,
            IWorkerJobEventSink eventSink,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<CacheMutationWorkerStartResult>(
                new CacheMutationWorkerStartResult.Started(session));
    }

    private sealed class FakeMutationSession(Guid jobId) : ICacheMutationWorkerSession
    {
        private readonly TaskCompletionSource<CacheMutationWorkerOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid JobId { get; } = jobId;
        public WorkerJobKind JobKind => WorkerJobKind.CacheMutation;
        public InternalWorkerProtocolVersion ProtocolVersion => InternalWorkerProtocolVersion.V2;
        public bool IsCancellable => true;
        public Task<CacheMutationWorkerOutcome> Completion
        {
            get
            {
                CompletionReads++;
                return _completion.Task;
            }
        }

        internal int CompletionReads { get; private set; }
        internal int DisposeCalls { get; private set; }

        internal void Complete(CacheMutationWorkerOutcome outcome) => _completion.TrySetResult(outcome);
        public Task RequestStopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAdmissionGate(Guid jobId) : IWorkerJobAdmissionGate
    {
        public WorkerJobAdmissionResult TryAdmit(WorkerJobDispatch dispatch) =>
            new WorkerJobAdmissionResult.Admitted(new FakeLease(
                new WorkerJobContext(jobId, WorkerJobKind.CacheMutation, WorkerJobRequestOrigin.Manual)));

        public CacheMaintenanceAdmissionResult TryReserveCacheMaintenance(
            CacheMaintenanceRequestOrigin origin) => throw new AssertFailedException();
    }

    private sealed class FakeLease : IWorkerJobAdmissionLease
    {
        internal FakeLease(Guid jobId)
            : this(new WorkerJobContext(
                jobId,
                WorkerJobKind.CacheMutation,
                WorkerJobRequestOrigin.Manual))
        {
        }

        internal FakeLease(WorkerJobContext context)
        {
            Context = context;
        }

        public WorkerJobContext Context { get; }
        public WorkerJobDescriptor Descriptor => WorkerJobDescriptors.CacheMutation;
        public bool IsStopRequested => false;
        public bool TryBindOwnerStop(WorkerJobContext context, Func<Task> requestStopAsync) => true;
        public bool TryAdvance(WorkerJobContext context, WorkerJobLifecycle lifecycle, int? childProcessId = null) => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpEventSink : IWorkerJobEventSink
    {
        public ValueTask AcceptAsync(
            WorkerJobOutputMessage message,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class DeletionAdmissionGate(Reservation reservation) : IWorkerJobAdmissionGate
    {
        public WorkerJobAdmissionResult TryAdmit(WorkerJobDispatch dispatch) =>
            throw new AssertFailedException();

        public CacheMaintenanceAdmissionResult TryReserveCacheMaintenance(
            CacheMaintenanceRequestOrigin origin) =>
            new CacheMaintenanceAdmissionResult.Reserved(reservation);
    }

    private sealed class Reservation : ICacheMaintenanceReservation
    {
        internal int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingDeletionFileSystem : ICacheDeletionFileSystem
    {
        internal TaskCompletionSource DeleteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowDelete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? FailedBasename { get; init; }
        internal bool Exists { get; private set; } = true;

        public ValueTask<CacheDeletionFileInspection> InspectAsync(
            string sourceRoot,
            string finalPath) => ValueTask.FromResult(CacheDeletionFileInspection.Ready);

        public async ValueTask DeleteAsync(string finalPath)
        {
            DeleteStarted.TrySetResult();
            await AllowDelete.Task;
            if (string.Equals(Path.GetFileName(finalPath), FailedBasename, StringComparison.Ordinal))
            {
                throw new IOException("achieved-delete-failure");
            }

            Exists = false;
        }
    }

    private sealed class CountingScanner(Func<bool>? exists = null) : ICacheInventoryStorageScanner
    {
        internal int ScanCalls { get; private set; }

        public Task<CacheInventorySnapshot> ScanAsync(
            long generation,
            CancellationToken cancellationToken)
        {
            ScanCalls++;
            ImmutableArray<CacheInventoryEntry> entries = exists?.Invoke() == false
                ? []
                :
                [
                    new CacheInventoryEntry(
                        CacheMutationSource.Overture,
                        "CHE",
                        CacheInventoryEntryStatus.Available,
                        1,
                        DateTimeOffset.UnixEpoch,
                        null,
                        "before-delete",
                        false,
                        null)
                ];
            return Task.FromResult(new CacheInventorySnapshot(
                generation,
                DateTimeOffset.UtcNow,
                [
                    new CacheInventorySourceSnapshot(
                        CacheMutationSource.Overture,
                        CacheInventorySourceStatus.Ready,
                        null,
                        entries)
                ]));
        }

        public Task<CacheInventoryExactResult> ScanExactAsync(
            CacheMutationSource source,
            string iso3,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static async Task DrainAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Bound);
        }
        catch
        {
        }
    }
}
