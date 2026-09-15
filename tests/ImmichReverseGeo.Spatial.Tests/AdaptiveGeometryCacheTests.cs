using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Spatial.Tests;

[TestClass]
public sealed class AdaptiveGeometryCacheTests
{
    private const long Budget = 8L * 1024 * 1024;
    private static byte[] Small => new WKBWriter().Write(new WKTReader().Read("POLYGON ((-1 -1, 1 -1, 1 1, -1 1, -1 -1))"));

    [TestMethod]
    public void SelectionUsesSizeAndBudgetIncludingThresholdsAndOverflow()
    {
        var limit = SpatialMemoryPolicy.MaximumPreparedWkbBytes;
        const long defaultBudget = 1024L * 1024 * 1024;
        Assert.IsTrue(SpatialMemoryPolicy.TrySelect(limit, defaultBudget, out var atLimit), "The exact size ceiling remains affordable.");
        Assert.IsFalse(atLimit.Compact, "The size ceiling is inclusive for prepared mode.");
        Assert.IsTrue(SpatialMemoryPolicy.TrySelect(limit + 1, defaultBudget, out var aboveLimit), "The first byte above the ceiling can still be cached.");
        Assert.IsTrue(aboveLimit.Compact, "Above the ceiling, selection switches to compact mode.");
        Assert.IsTrue(SpatialMemoryPolicy.TrySelect(limit, atLimit.ReservationBytes - 1, out var constrained), "Compact mode can fit when prepared mode exceeds the budget.");
        Assert.IsTrue(constrained.Compact, "An insufficient prepared budget selects the compact estimate.");
        Assert.IsFalse(SpatialMemoryPolicy.TrySelect(-1, defaultBudget, out _), "Negative metadata cannot be admitted.");
        Assert.IsFalse(SpatialMemoryPolicy.TrySelect(long.MaxValue, defaultBudget, out _), "Overflow cannot produce an affordable estimate.");
        Assert.IsFalse(SpatialMemoryPolicy.TrySelect(limit, 0, out _), "A zero budget retains no geometry.");
    }

    [TestMethod]
    [DataRow(GeometrySource.Gadm, "GBR")]
    [DataRow(GeometrySource.Gadm, "USA")]
    [DataRow(GeometrySource.Overture, "JPN")]
    [DataRow(GeometrySource.Overture, "GBR")]
    public void SameBytesChooseSameModeAndDifferentSizesWithinCountryChooseIndependently(GeometrySource source, string country)
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(Budget);
        var generation = cache.ObserveGeneration(source, country, fixture.Path);
        var large = Large();
        Assert.IsTrue(cache.Covers(generation, "large", large.Length, () => large, new Point(0, 0)));
        Assert.IsTrue(cache.Covers(generation, "large", large.Length, () => throw new AssertFailedException("Hit loaded WKB"), new Point(0.2, 0.3)));
        Assert.AreEqual(1L, cache.GetStatistics().CompactConstructions);
        Assert.AreEqual(1L, cache.GetStatistics().CompactHits);
        Assert.IsTrue(cache.Covers(generation, "small", Small.Length, () => Small, new Point(0, 0)));
        var state = cache.GetStatistics();
        Assert.AreEqual(2L, state.Preparations);
        Assert.AreEqual(1L, state.CompactConstructions, "Small polygon remains prepared under the same country identity.");
        Assert.IsTrue(state.CompactRetainedBytes > 0 && state.CompactWorkspaceBytes > 0);
        Assert.IsTrue(state.AccountedBytes <= Budget);
        cache.Dispose();
        Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes);
        Assert.AreEqual(0L, cache.GetStatistics().CompactRetainedBytes);
        Assert.AreEqual(0L, cache.GetStatistics().CompactWorkspaceBytes);
    }

    [TestMethod]
    public void ImpossibleAdmissionsDoNotEvictUsefulEntriesAcrossSources()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(Budget);
        var a = cache.ObserveGeneration(GeometrySource.Gadm, "AAA", fixture.Path);
        var b = cache.ObserveGeneration(GeometrySource.Overture, "BBB", fixture.Path);
        foreach (var generation in new[] { a, b })
        {
            Assert.IsTrue(cache.Covers(generation, "kept", Small.Length, () => Small, new Point(0, 0)));
        }

        var before = cache.GetStatistics();
        foreach (var bytes in new[] { long.MaxValue, Budget, -1L })
        {
            Assert.IsTrue(cache.Covers(a, "rejected", bytes, () => Small, new Point(0, 0)));
        }

        foreach (var generation in new[] { a, b })
        {
            Assert.IsTrue(cache.Covers(generation, "kept", Small.Length, () => throw new AssertFailedException("Rejected admission evicted useful entry"), new Point(0, 0)));
        }

        var after = cache.GetStatistics();
        Assert.AreEqual(before.Evictions, after.Evictions);
        Assert.AreEqual(before.AccountedBytes, after.AccountedBytes);
        Assert.AreEqual(3L, after.IntrinsicRejections);
        Assert.AreEqual(3L, after.UnretainedEvaluations);
    }

    [TestMethod]
    public void CompactFailureReleasesOwnerLeaseAndGenerationAccounting()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(Budget);
        var generation = cache.ObserveGeneration(GeometrySource.Gadm, "AAA", fixture.Path);
        var bytes = Large();
        Assert.ThrowsExactly<OutOfMemoryException>(() => cache.Covers(generation, "large", bytes.Length, () => bytes, new FailingPoint()));
        File.AppendAllText(fixture.Path, "new generation");
        cache.ObserveGeneration(GeometrySource.Gadm, "AAA", fixture.Path);
        Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes, "The failed owner's lease must be released before retirement.");
        Assert.AreEqual(0L, cache.GetStatistics().CompactRetainedBytes, "Retirement releases compact retained accounting.");
        Assert.AreEqual(0L, cache.GetStatistics().CompactWorkspaceBytes, "Retirement releases compact workspace accounting.");
    }

    [TestMethod]
    public async Task CompactWaiterCanCancelWhileRetirementPreservesActiveWorkspace()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(Budget);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var generation = cache.ObserveGeneration(GeometrySource.Gadm, "AAA", fixture.Path);
        var bytes = Large();
        Assert.IsTrue(cache.Covers(generation, "large", bytes.Length, () => bytes, new Point(0, 0)));
        var active = Task.Run(() => cache.Covers(generation, "large", bytes.Length, () => bytes, new BlockingPoint(entered, release)));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            var waiter = Task.Run(() => cache.Covers(generation, "large", bytes.Length, () => bytes, new Point(0, 0), cancellation.Token));
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (cache.GetStatistics().CompactHits < 2)
            {
                await Task.Delay(10, bound.Token);
            }

            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiter.WaitAsync(TimeSpan.FromSeconds(5)));
            var held = cache.GetStatistics().AccountedBytes;
            File.AppendAllText(fixture.Path, "replacement");
            cache.ObserveGeneration(GeometrySource.Gadm, "AAA", fixture.Path);
            Assert.AreEqual(0, cache.GetStatistics().RetainedEntries);
            Assert.AreEqual(held, cache.GetStatistics().AccountedBytes, "Retired active geometry still owns its workspace.");
            Assert.IsTrue(cache.GetStatistics().CompactWorkspaceBytes > 0);
        }
        finally
        {
            release.Set();
        }

        Assert.IsTrue(await active);
        Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes);
        Assert.AreEqual(0L, cache.GetStatistics().CompactWorkspaceBytes);
    }

    private static byte[] Large()
    {
        const int count = 15000;
        var coordinates = new Coordinate[count + 1];
        for (var i = 0; i < count; i++)
        {
            var angle = i * Math.PI * 2 / count;
            coordinates[i] = new Coordinate(Math.Cos(angle), Math.Sin(angle));
        }

        coordinates[count] = coordinates[0].Copy();
        return new WKBWriter().Write(new Polygon(new LinearRing(coordinates)));
    }

    private sealed class FailingPoint() : Point(0, 0)
    {
        public override Coordinate Coordinate => throw new OutOfMemoryException("controlled compact failure");
    }

    private sealed class BlockingPoint(ManualResetEventSlim entered, ManualResetEventSlim release) : Point(0, 0)
    {
        public override Coordinate Coordinate
        {
            get
            {
                entered.Set();
                Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)));
                return base.Coordinate;
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Path { get; } = System.IO.Path.GetTempFileName();
        public void Dispose() => File.Delete(Path);
    }
}
