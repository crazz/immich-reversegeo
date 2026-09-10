using System.Collections.Immutable;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.CacheInventory;

[TestClass]
public sealed class CacheInventoryServiceTests
{
    [TestMethod]
    [TestCategory("Change53")]
    public async Task ConcurrentWaiterCancellation_DoesNotCancelSharedScan()
    {
        var scanStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScan = new TaskCompletionSource<CacheInventorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new DelegateScanner
        {
            Scan = (generation, cancellationToken) =>
            {
                scanStarted.TrySetResult();
                Assert.IsFalse(cancellationToken.IsCancellationRequested);
                return releaseScan.Task;
            }
        };
        await using var inventory = new CacheInventoryService(scanner);
        using var waiterCancellation = new CancellationTokenSource();

        Task<CacheInventorySnapshot>? cancelledWaiter = null;
        Task<CacheInventorySnapshot>? survivingWaiter = null;
        try
        {
            cancelledWaiter = inventory.GetSnapshotAsync(waiterCancellation.Token);
            survivingWaiter = inventory.GetSnapshotAsync();
            await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            waiterCancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(
                async () => await cancelledWaiter);
            Assert.AreEqual(1, scanner.ScanCalls);

            CacheInventorySnapshot expected = Snapshot(0);
            releaseScan.TrySetResult(expected);
            CacheInventorySnapshot actual = await survivingWaiter.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreSame(expected, actual);
            Assert.AreEqual(1, scanner.ScanCalls);
        }
        finally
        {
            releaseScan.TrySetResult(Snapshot(0));
            await DrainAsync(cancelledWaiter);
            await DrainAsync(survivingWaiter);
        }
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task RepeatedMutationDuringDirtyScan_FencesOlderObservation()
    {
        var dirtyScanStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDirtyScan = new TaskCompletionSource<CacheInventorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CacheInventorySnapshot initial = SnapshotWithEntry(0, "old-release");
        CacheInventorySnapshot obsolete = SnapshotWithEntry(1, "observed-before-second-mutation");
        CacheInventorySnapshot deleted = Snapshot(2);
        var scanner = new DelegateScanner
        {
            Scan = (generation, _) => generation switch
            {
                0 => Task.FromResult(initial),
                1 => StartDirtyScan(),
                2 => Task.FromResult(deleted),
                _ => throw new AssertFailedException($"unexpected-generation-{generation}")
            }
        };
        await using var inventory = new CacheInventoryService(scanner);
        Task<CacheInventorySnapshot>? afterFirstMutation = null;
        Task<CacheInventorySnapshot>? postInvalidationNewcomer = null;
        try
        {
            CacheInventorySnapshot before = await inventory.GetSnapshotAsync();
            Assert.AreEqual("old-release", OnlyEntry(before).DatasetVersion);
            inventory.InvalidateKey(CacheMutationSource.Overture, "CHE");
            afterFirstMutation = inventory.GetSnapshotAsync();
            await dirtyScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            inventory.InvalidateKey(CacheMutationSource.Overture, "CHE");
            postInvalidationNewcomer = inventory.GetSnapshotAsync();
            Assert.IsFalse(postInvalidationNewcomer.IsCompleted,
                "new-reader-waits-while-obsolete-scan-is-held");
            releaseDirtyScan.TrySetResult(obsolete);
            CacheInventorySnapshot[] readers = await Task.WhenAll(
                    afterFirstMutation,
                    postInvalidationNewcomer)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreSame(deleted, readers[0]);
            Assert.AreSame(deleted, readers[1]);
            Assert.AreEqual(0, readers[1].Sources.Length,
                "new-reader-observes-deleted-storage-not-obsolete-metadata");
            Assert.AreEqual(3, scanner.ScanCalls);
            CacheInventorySnapshot cached = await inventory.GetSnapshotAsync();
            Assert.AreSame(deleted, cached);
        }
        finally
        {
            releaseDirtyScan.TrySetResult(obsolete);
            await DrainAsync(afterFirstMutation);
            await DrainAsync(postInvalidationNewcomer);
        }

        Task<CacheInventorySnapshot> StartDirtyScan()
        {
            dirtyScanStarted.TrySetResult();
            return releaseDirtyScan.Task;
        }
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task ConcurrentRefreshes_CoalesceOneNewGeneration()
    {
        var refreshStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource<CacheInventorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new DelegateScanner
        {
            Scan = (generation, _) => generation switch
            {
                0 => Task.FromResult(Snapshot(0)),
                1 => StartRefresh(),
                _ => throw new AssertFailedException($"unexpected-generation-{generation}")
            }
        };
        await using var inventory = new CacheInventoryService(scanner);
        await inventory.GetSnapshotAsync();

        Task<CacheInventorySnapshot>? first = null;
        Task<CacheInventorySnapshot>? second = null;
        try
        {
            first = inventory.RefreshAsync();
            await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = inventory.RefreshAsync();
            releaseRefresh.TrySetResult(Snapshot(1));

            CacheInventorySnapshot[] results = await Task.WhenAll(first, second);
            Assert.AreSame(results[0], results[1]);
            Assert.AreEqual(2, scanner.ScanCalls);
        }
        finally
        {
            releaseRefresh.TrySetResult(Snapshot(1));
            await DrainAsync(first);
            await DrainAsync(second);
        }

        Task<CacheInventorySnapshot> StartRefresh()
        {
            refreshStarted.TrySetResult();
            return releaseRefresh.Task;
        }
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task IdleDuplicateKeySourceAndAllInvalidations_CoalescePerDirtyGeneration()
    {
        var scanner = new DelegateScanner
        {
            Scan = (generation, _) => Task.FromResult(Snapshot(generation))
        };
        await using var inventory = new CacheInventoryService(scanner);

        CacheInventorySnapshot initial = await inventory.GetSnapshotAsync();
        Assert.AreEqual(0L, initial.Generation);

        inventory.InvalidateKey(CacheMutationSource.Overture, "CHE");
        inventory.InvalidateKey(CacheMutationSource.Overture, "CHE");
        CacheInventorySnapshot afterKey = await inventory.GetSnapshotAsync();
        Assert.AreEqual(1L, afterKey.Generation);

        inventory.InvalidateSource(CacheMutationSource.Overture);
        inventory.InvalidateSource(CacheMutationSource.Overture);
        CacheInventorySnapshot afterSource = await inventory.GetSnapshotAsync();
        Assert.AreEqual(2L, afterSource.Generation);

        inventory.InvalidateAll();
        inventory.InvalidateAll();
        CacheInventorySnapshot afterAll = await inventory.GetSnapshotAsync();
        Assert.AreEqual(3L, afterAll.Generation);
        Assert.AreEqual(4, scanner.ScanCalls,
            "one-initial-scan-plus-one-scan-per-idle-dirty-generation");
        Assert.AreSame(afterAll, await inventory.GetSnapshotAsync(),
            "clean-generation-is-reused-after-duplicate-invalidations-coalesce");
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task ScannerStartsOutsideStateLock()
    {
        CacheInventoryService? inventory = null;
        var invalidateOnce = 0;
        var scanner = new DelegateScanner
        {
            Scan = async (generation, _) =>
            {
                if (Interlocked.Exchange(ref invalidateOnce, 1) == 0)
                {
                    Task reentrantInvalidation = Task.Run(() => inventory!.InvalidateAll());
                    await reentrantInvalidation.WaitAsync(TimeSpan.FromSeconds(2));
                }

                return Snapshot(generation);
            }
        };
        await using (inventory = new CacheInventoryService(scanner))
        {
            CacheInventorySnapshot snapshot = await inventory
                .GetSnapshotAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1L, snapshot.Generation);
            Assert.AreEqual(2, scanner.ScanCalls);
        }
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task PhysicalInspectionAdmission_SerializesExactAndFullWorkWhileCallerCancellationDetaches()
    {
        var scanner = new HeldPhysicalScanner();
        var inventory = new CacheInventoryService(scanner);
        using var waiterCancellation = new CancellationTokenSource();
        Task<CacheInventoryExactResult>? firstExact = null;
        Task<CacheInventoryExactResult>? cancelledDuplicate = null;
        Task<CacheInventoryExactResult>? secondExact = null;
        Task<CacheInventorySnapshot>? full = null;
        try
        {
            firstExact = inventory.GetExactAsync(CacheMutationSource.Overture, "CHE");
            await scanner.FirstStarted.WaitAsync(TimeSpan.FromSeconds(5));
            cancelledDuplicate = inventory.GetExactAsync(
                CacheMutationSource.Overture,
                "CHE",
                waiterCancellation.Token);
            secondExact = inventory.GetExactAsync(CacheMutationSource.Gadm, "JPN");
            full = inventory.RefreshAsync();

            Assert.AreEqual(1, scanner.StartedCount,
                "one-physical-callback-enters-before-the-held-operation-releases");
            Assert.AreEqual(1, scanner.MaxActiveCount,
                "full-and-exact-work-share-the-single-physical-admission");

            waiterCancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(
                async () => await cancelledDuplicate);
            Assert.AreEqual(0, scanner.LifetimeCancellationCount,
                "caller-cancellation-detaches-without-cancelling-physical-work");

            scanner.ReleaseAll();
            await Task.WhenAll(firstExact, secondExact, full)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await scanner.FourCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            await inventory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(3, scanner.ExactStartedCount,
                "duplicate-keys-remain-independent-bounded-operations");
            Assert.AreEqual(1, scanner.FullStartedCount);
            Assert.AreEqual(4, scanner.CompletedCount,
                "cancelled-waiter-work-and-surviving-work-fully-drain");
            Assert.AreEqual(1, scanner.MaxActiveCount,
                "fully-drained-results-prove-physical-work-never-overlapped");
        }
        finally
        {
            scanner.ReleaseAll();
            await DrainAsync(firstExact);
            await DrainAsync(cancelledDuplicate);
            await DrainAsync(secondExact);
            await DrainAsync(full);
            await DrainAsync(inventory.DisposeAsync().AsTask());
        }
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task Dispose_CancelsQueuedAdmissionAndDrainsHeldPhysicalWork()
    {
        var scanner = new HeldPhysicalScanner();
        var inventory = new CacheInventoryService(scanner);
        Task<CacheInventoryExactResult>? active = null;
        Task<CacheInventoryExactResult>? queuedExact = null;
        Task<CacheInventorySnapshot>? queuedFull = null;
        Task? dispose = null;
        try
        {
            active = inventory.GetExactAsync(CacheMutationSource.Overture, "CHE");
            await scanner.FirstStarted.WaitAsync(TimeSpan.FromSeconds(5));
            queuedExact = inventory.GetExactAsync(CacheMutationSource.Gadm, "JPN");
            queuedFull = inventory.RefreshAsync();

            dispose = inventory.DisposeAsync().AsTask();
            await scanner.FirstLifetimeCancellation.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(
                async () => await queuedExact);
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(
                async () => await queuedFull);
            Assert.AreEqual(1, scanner.StartedCount,
                "queued-admission-is-cancelled-before-reaching-the-scanner");
            Assert.AreEqual(0, scanner.CompletedCount,
                "dispose-does-not-finish-the-held-physical-callback");
            Assert.IsFalse(dispose.IsCompleted,
                "dispose-remains-pending-after-queued-cancellation-until-active-work-drains");

            scanner.ReleaseAll();
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            await active.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, scanner.CompletedCount,
                "dispose-drains-the-one-admitted-callback-after-release");
            Assert.AreEqual(1, scanner.MaxActiveCount);
        }
        finally
        {
            scanner.ReleaseAll();
            await DrainAsync(active);
            await DrainAsync(queuedExact);
            await DrainAsync(queuedFull);
            dispose ??= inventory.DisposeAsync().AsTask();
            await DrainAsync(dispose);
        }
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task Dispose_CancelsAndDrainsExactInspection()
    {
        var exactStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetimeCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExact = new TaskCompletionSource<CacheInventoryExactResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new DelegateScanner
        {
            Scan = (generation, _) => Task.FromResult(Snapshot(generation)),
            ScanExact = (_, _, cancellationToken) =>
            {
                cancellationToken.Register(() => lifetimeCancelled.TrySetResult());
                exactStarted.TrySetResult();
                return releaseExact.Task;
            }
        };
        var inventory = new CacheInventoryService(scanner);
        Task<CacheInventoryExactResult> exact = inventory.GetExactAsync(
            CacheMutationSource.Overture,
            "CHE");
        await exactStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task? dispose = null;
        try
        {
            dispose = inventory.DisposeAsync().AsTask();
            await lifetimeCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(dispose.IsCompleted, "dispose-must-drain-exact-inspection");

            releaseExact.TrySetResult(ExactAvailable());
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            CacheInventoryExactResult result = await exact.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(CacheInventoryEntryStatus.Available, result.Entry.Status);
        }
        finally
        {
            releaseExact.TrySetResult(ExactAvailable());
            await DrainAsync(exact);
            if (dispose is null)
            {
                dispose = inventory.DisposeAsync().AsTask();
            }

            await DrainAsync(dispose);
        }
    }

    private static CacheInventorySnapshot Snapshot(long generation) =>
        new(generation, DateTimeOffset.UnixEpoch, ImmutableArray<CacheInventorySourceSnapshot>.Empty);

    private static CacheInventorySnapshot SnapshotWithEntry(long generation, string version) =>
        new(
            generation,
            DateTimeOffset.UnixEpoch,
            [
                new CacheInventorySourceSnapshot(
                    CacheMutationSource.Overture,
                    CacheInventorySourceStatus.Ready,
                    null,
                    [
                        new CacheInventoryEntry(
                            CacheMutationSource.Overture,
                            "CHE",
                            CacheInventoryEntryStatus.Available,
                            1,
                            DateTimeOffset.UnixEpoch,
                            null,
                            version,
                            false,
                            null)
                    ])
            ]);

    private static CacheInventoryEntry OnlyEntry(CacheInventorySnapshot snapshot) =>
        snapshot.Sources.Single().Entries.Single();

    private static async Task DrainAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }
    }

    private static CacheInventoryExactResult ExactAvailable() =>
        new(
            new CacheInventoryEntry(
                CacheMutationSource.Overture,
                "CHE",
                CacheInventoryEntryStatus.Available,
                1,
                DateTimeOffset.UnixEpoch,
                null,
                null,
                false,
                null),
            true);

    private sealed class DelegateScanner : ICacheInventoryStorageScanner
    {
        internal required Func<long, CancellationToken, Task<CacheInventorySnapshot>> Scan { get; init; }
        internal Func<CacheMutationSource, string, CancellationToken,
            Task<CacheInventoryExactResult>>? ScanExact { get; init; }
        internal int ScanCalls { get; private set; }

        public Task<CacheInventorySnapshot> ScanAsync(
            long generation,
            CancellationToken cancellationToken)
        {
            ScanCalls++;
            return Scan(generation, cancellationToken);
        }

        public Task<CacheInventoryExactResult> ScanExactAsync(
            CacheMutationSource source,
            string iso3,
            CancellationToken cancellationToken) =>
            ScanExact?.Invoke(source, iso3, cancellationToken)
            ?? throw new NotSupportedException();
    }

    private sealed class HeldPhysicalScanner : ICacheInventoryStorageScanner
    {
        private readonly TaskCompletionSource _firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstLifetimeCancellation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fourCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseAll =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCount;
        private int _completedCount;
        private int _exactStartedCount;
        private int _fullStartedCount;
        private int _lifetimeCancellationCount;
        private int _maxActiveCount;
        private int _startedCount;

        internal Task FirstStarted => _firstStarted.Task;
        internal Task FirstLifetimeCancellation => _firstLifetimeCancellation.Task;
        internal Task FourCompleted => _fourCompleted.Task;
        internal int CompletedCount => Volatile.Read(ref _completedCount);
        internal int ExactStartedCount => Volatile.Read(ref _exactStartedCount);
        internal int FullStartedCount => Volatile.Read(ref _fullStartedCount);
        internal int LifetimeCancellationCount => Volatile.Read(ref _lifetimeCancellationCount);
        internal int MaxActiveCount => Volatile.Read(ref _maxActiveCount);
        internal int StartedCount => Volatile.Read(ref _startedCount);

        public async Task<CacheInventorySnapshot> ScanAsync(
            long generation,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _fullStartedCount);
            await HoldAsync(cancellationToken);
            return Snapshot(generation);
        }

        public async Task<CacheInventoryExactResult> ScanExactAsync(
            CacheMutationSource source,
            string iso3,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _exactStartedCount);
            await HoldAsync(cancellationToken);
            return ExactAvailable();
        }

        internal void ReleaseAll() => _releaseAll.TrySetResult();

        private async Task HoldAsync(CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _activeCount);
            UpdateMaximum(active);
            Interlocked.Increment(ref _startedCount);
            _firstStarted.TrySetResult();
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                Interlocked.Increment(ref _lifetimeCancellationCount);
                _firstLifetimeCancellation.TrySetResult();
            });
            try
            {
                await _releaseAll.Task;
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
                if (Interlocked.Increment(ref _completedCount) == 4)
                {
                    _fourCompleted.TrySetResult();
                }
            }
        }

        private void UpdateMaximum(int active)
        {
            int observed = Volatile.Read(ref _maxActiveCount);
            while (active > observed)
            {
                int previous = Interlocked.CompareExchange(
                    ref _maxActiveCount,
                    active,
                    observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }
}
