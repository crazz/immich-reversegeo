using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Spatial.Tests;

[TestClass]
public sealed class AdministrativeGeometryCacheTests
{
    private static byte[] Polygon => new WKBWriter().Write(new WKTReader().Read("POLYGON ((0 0, 10 0, 9 10, 0 10, 0 0))"));

    [TestMethod]
    public void RetainedAreaAvoidsBlobLoadsForDifferentPointsAndSourcesStaySeparate()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        var generation = cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
        var loads = 0;
        byte[] Load() { loads++; return Polygon; }
        Assert.IsTrue(cache.Covers(generation, "same-id", Polygon.Length, Load, new Point(1, 1)));
        Assert.IsTrue(cache.Covers(generation, "same-id", Polygon.Length, Load, new Point(2, 2)));
        var other = cache.ObserveGeneration(GeometrySource.Overture, "USA", fixture.Path);
        Assert.IsTrue(cache.Covers(other, "same-id", Polygon.Length, Load, new Point(3, 3)));
        Assert.AreEqual(2, loads, "Only one load per source generation.");
        Assert.AreEqual(1L, cache.GetStatistics().Hits, "Different points reuse the same area.");
    }

    [TestMethod]
    public async Task ConcurrentQueriesHaveOneOwnerAndCancelledWaiterDoesNotRemoveIt()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var generation = cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
        var loads = 0;
        byte[] Load()
        {
            Interlocked.Increment(ref loads);
            entered.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)), "Owner loader must be released.");
            return Polygon;
        }

        var owner = Task.Run(() => cache.Covers(generation, "area", Polygon.Length, Load, new Point(1, 1)));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            var waiter = Task.Run(() => cache.Covers(generation, "area", Polygon.Length, Load, new Point(2, 2), cancellation.Token));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiter);
            Assert.AreEqual(1, cache.GetStatistics().PendingPreparations, "Waiter cannot remove owner state.");
            Assert.AreEqual(1, loads, "Waiter must not start a duplicate loader.");
        }
        finally
        {
            release.Set();
        }

        Assert.IsTrue(await owner);
        Assert.IsTrue(cache.Covers(generation, "area", Polygon.Length, Load, new Point(3, 3)));
        Assert.AreEqual(1, loads);
    }

    [TestMethod]
    public void FailedOrCancelledOwnerReleasesReservationAndCanBeRetried()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        using var cancellation = new CancellationTokenSource();
        var generation = cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
        Assert.ThrowsExactly<OutOfMemoryException>(() => cache.Covers(generation, "area", 100,
            () => throw new OutOfMemoryException("controlled"), new Point(1, 1)));
        Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes, "Failed reservation must be released.");
        Assert.ThrowsExactly<OperationCanceledException>(() => cache.Covers(generation, "area", Polygon.Length,
            () => { cancellation.Cancel(); return Polygon; }, new Point(1, 1), cancellation.Token));
        Assert.AreEqual(0, cache.GetStatistics().RetainedEntries, "Cancelled owner cannot publish.");
        Assert.IsTrue(cache.Covers(generation, "area", Polygon.Length, () => Polygon, new Point(1, 1)));
    }

    [TestMethod]
    public void SmallBudgetUsesUnretainedEvaluationWithoutChangingResults()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(1);
        var generation = cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
        for (var i = 0; i < 3; i++)
        {
            Assert.IsTrue(cache.Covers(generation, "area", Polygon.Length, () => Polygon, new Point(i, 1)));
        }

        var statistics = cache.GetStatistics();
        Assert.AreEqual(0L, statistics.AccountedBytes, "Oversized work must not become retained state.");
        Assert.AreEqual(3L, statistics.UnretainedEvaluations);
    }

    [TestMethod]
    public void ManyAreasEvictWithinOneBudgetAndDisposeReleasesOwnership()
    {
        using var fixture = new Fixture();
        var cache = new AdministrativeGeometryCache(7_000_000);
        var generation = cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
        for (var i = 0; i < 100; i++)
        {
            Assert.IsTrue(cache.Covers(generation, i.ToString(), Polygon.Length, () => Polygon, new Point(1, 1)));
            Assert.IsTrue(cache.GetStatistics().AccountedBytes <= 7_000_000, "All retained entries share the budget.");
        }

        Assert.IsTrue(cache.GetStatistics().Evictions > 0, "History cannot grow indefinitely.");
        cache.Dispose();
        Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes, "Finality releases retained entries.");
        Assert.ThrowsExactly<ObjectDisposedException>(() => cache.Covers(generation, "area", 0, () => [], new Point(1, 1)));
    }

    [TestMethod]
    public async Task ReplacementRetiresPinnedEntryWithoutReleasingItsChargeEarly()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var generation = cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
        var task = Task.Run(() => cache.Covers(generation, "area", Polygon.Length, () => Polygon, new BlockingPoint(entered, release)));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        var before = cache.GetStatistics().AccountedBytes;
        try
        {
            File.AppendAllText(fixture.Path, "replacement");
            var next = cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
            Assert.AreNotSame(generation, next);
            Assert.AreEqual(0, cache.GetStatistics().RetainedEntries, "Old entry is no longer reusable.");
            Assert.AreEqual(before, cache.GetStatistics().AccountedBytes, "An active lease still owns the memory.");
        }
        finally
        {
            release.Set();
        }

        Assert.IsTrue(await task);
        Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes, "Last lease releases the retired geometry.");
    }

    [TestMethod]
    public async Task OwnerCancellationReachesLiveWaiterWithoutCancellingItsTokenAndNextAcquisitionRetries()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        using var ownerCancellation = new CancellationTokenSource();
        using var waiterCancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var generation = cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
        byte[] Load()
        {
            entered.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)));
            return Polygon;
        }

        var owner = Task.Run(() => cache.Covers(generation, "area", Polygon.Length, Load, new Point(1, 1), ownerCancellation.Token));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        var waiter = Task.Run(() => cache.Covers(generation, "area", Polygon.Length, () => Polygon, new Point(2, 2), waiterCancellation.Token));
        try
        {
            await WaitForAsync(() => cache.GetStatistics().WaitingQueries == 1);
            ownerCancellation.Cancel();
        }
        finally
        {
            release.Set();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await owner);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiter);
        Assert.IsFalse(waiterCancellation.IsCancellationRequested, "Provider classification must see a live waiter token.");
        Assert.AreEqual(0, cache.GetStatistics().PendingPreparations);
        Assert.IsTrue(cache.Covers(generation, "area", Polygon.Length, () => Polygon, new Point(2, 2)));
    }

    [TestMethod]
    public async Task RetiredGenerationCannotPublishAfterItsLoaderCompletes()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var old = cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
        var owner = Task.Run(() => cache.Covers(old, "area", Polygon.Length,
            () => { entered.Set(); Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10))); return Polygon; }, new Point(1, 1)));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            File.AppendAllText(fixture.Path, "next-generation");
            cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
        }
        finally
        {
            release.Set();
        }

        Assert.IsTrue(await owner, "The acquired old snapshot still completes coherently.");
        Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes, "Retired work cannot become retained state.");
        Assert.AreEqual(0, cache.GetStatistics().RetainedEntries);
    }

    [TestMethod]
    public async Task OversizedEvaluationsAcrossProvidersHaveOnlyOneMaterializationOwner()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
        var second = cache.ObserveGeneration(GeometrySource.Overture, "USA", fixture.Path);
        var loads = 0;
        byte[] Load()
        {
            Interlocked.Increment(ref loads);
            entered.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)));
            return Polygon;
        }

        var owner = Task.Run(() => cache.Covers(first, "area", Polygon.Length, Load, new Point(1, 1)));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        var other = Task.Run(() => cache.Covers(second, "area", Polygon.Length, Load, new Point(2, 2)));
        try
        {
            await WaitForAsync(() => cache.GetStatistics().PendingPreparations == 2);
            Assert.AreEqual(1, Volatile.Read(ref loads), "Queued oversized work must not materialize concurrently.");
        }
        finally
        {
            release.Set();
        }

        Assert.IsTrue(await owner);
        Assert.IsTrue(await other);
        Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisposeWaitsForActivePredicateAndLoaderWhileCancellingJoinedAndQueuedQueries(bool loaderFails)
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        using var predicateEntered = new ManualResetEventSlim();
        using var loaderEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var generation = cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
        var lease = Task.Run(() => cache.Covers(generation, "retained", Polygon.Length, () => Polygon,
            new BlockingPoint(predicateEntered, release)));
        Assert.IsTrue(predicateEntered.Wait(TimeSpan.FromSeconds(10)));
        var owner = Task.Run(() => cache.Covers(generation, "preparing", Polygon.Length, () =>
        {
            loaderEntered.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)));
            if (loaderFails)
            {
                throw new OutOfMemoryException("controlled loader failure during shutdown");
            }

            return Polygon;
        }, new Point(1, 1)));
        Assert.IsTrue(loaderEntered.Wait(TimeSpan.FromSeconds(10)));
        var joined = Task.Run(() => cache.Covers(generation, "preparing", Polygon.Length, () => Polygon, new Point(2, 2)));
        var queued = Task.Run(() => cache.Covers(generation, "queued", Polygon.Length, () => Polygon, new Point(3, 3)));
        Task disposal = Task.CompletedTask;
        try
        {
            await WaitForAsync(() => cache.GetStatistics().WaitingQueries == 1 && cache.GetStatistics().PendingPreparations == 2);
            disposal = Task.Run(cache.Dispose);
            await WaitForAsync(() => joined.IsCompleted && queued.IsCompleted);
            Assert.IsFalse(disposal.IsCompleted, "Finality must wait for the non-preemptible predicate and loader.");
        }
        finally
        {
            release.Set();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await lease);
        if (loaderFails)
        {
            await Assert.ThrowsAsync<OutOfMemoryException>(async () => await owner);
        }
        else
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await owner);
        }

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await joined);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await queued);
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes, "All reservations and leases unwind before disposal returns.");
        Assert.AreEqual(0, cache.GetStatistics().PendingPreparations, "No owned preparation survives finality.");
    }

    [TestMethod]
    public void CriticalPredicateFailureAfterPublicationDoesNotLeaveAPinnedEntry()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        var generation = cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
        Assert.ThrowsExactly<OutOfMemoryException>(() => cache.Covers(generation, "area", Polygon.Length,
            () => Polygon, new FailingPoint()));
        File.AppendAllText(fixture.Path, "replacement");
        cache.ObserveGeneration(GeometrySource.Gadm, "USA", fixture.Path);
        Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes, "The failed owner's lease must be released before retirement.");
    }

    private sealed class FailingPoint() : Point(1, 1)
    {
        protected override Envelope ComputeEnvelopeInternal() => throw new OutOfMemoryException("controlled predicate failure");
    }

    [TestMethod]
    public void RepeatedInvocationsAndManyCountriesDoNotAccumulateRetainedHistory()
    {
        using var fixture = new Fixture();
        for (var invocation = 0; invocation < 3; invocation++)
        {
            using var cache = new AdministrativeGeometryCache(7_000_000);
            for (var country = 0; country < 520; country++)
            {
                var generation = cache.ObserveGeneration(country % 2 == 0 ? GeometrySource.Gadm : GeometrySource.Overture,
                    country.ToString(), fixture.Path);
                Assert.IsTrue(cache.Covers(generation, "same-id", Polygon.Length, () => Polygon, new Point(1, 1)));
                Assert.IsTrue(cache.GetStatistics().AccountedBytes <= 7_000_000, "Countries and both providers share one bound.");
            }

            cache.Dispose();
            Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes, "Each invocation releases its history.");
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, bound.Token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Path { get; } = System.IO.Path.GetTempFileName();
        public void Dispose() => File.Delete(Path);
    }

    private sealed class BlockingPoint(ManualResetEventSlim entered, ManualResetEventSlim release) : Point(1, 1)
    {
        protected override Envelope ComputeEnvelopeInternal()
        {
            entered.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)), "Pinned query must be released.");
            return base.ComputeEnvelopeInternal();
        }
    }
}
