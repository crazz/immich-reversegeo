using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Spatial.Tests;

[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
public sealed class CompactConcurrentMemoryTests
{
    [TestMethod]
    public async Task SeparateCompactEntriesEvaluateConcurrentlyWithinTheirOwnWorkspaceAllowances()
    {
        const int count = 2200000;
        var coordinates = new Coordinate[count + 1];
        for (var i = 0; i < count; i++)
        {
            var angle = i * 2 * Math.PI / count;
            coordinates[i] = new Coordinate(Math.Cos(angle), Math.Sin(angle));
        }

        coordinates[count] = coordinates[0].Copy();
        var bytes = new WKBWriter().Write(new Polygon(new LinearRing(coordinates)));
        var path = Path.GetTempFileName();
        try
        {
            using var cache = new AdministrativeGeometryCache(2L * 1024 * 1024 * 1024);
            var generations = new[]
            {
                cache.ObserveGeneration(GeometrySource.Gadm, "AAA", path),
                cache.ObserveGeneration(GeometrySource.Overture, "BBB", path)
            };
            var before = GC.GetTotalMemory(true);
            foreach (var generation in generations)
            {
                Assert.IsTrue(cache.Covers(generation, "large", bytes.Length, () => bytes, new Point(0, 0)));
            }

            var retained = GC.GetTotalMemory(true) - before;
            using var ready = new Barrier(2);
            var queries = generations.Select(generation => Task.Run(() =>
                cache.Covers(generation, "large", bytes.Length,
                    () => throw new AssertFailedException("Concurrent hit reloaded geometry."), new ConcurrentPoint(ready)))).ToArray();
            var results = await Task.WhenAll(queries).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.IsTrue(results.All(result => !result), "Both exterior queries retain the reference non-match.");
            var afterDistanceRetained = GC.GetTotalMemory(true) - before;
            var statistics = cache.GetStatistics();
            Assert.AreEqual(2L, statistics.CompactConstructions);
            Assert.AreEqual(2L, statistics.CompactHits);
            Assert.AreEqual(0L, statistics.Evictions);
            Assert.IsTrue(statistics.AccountedBytes <= statistics.BudgetBytes);
            cache.Dispose();
            Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes);
            Assert.AreEqual(0L, cache.GetStatistics().CompactWorkspaceBytes);
            var output = Path.Combine(Environment.CurrentDirectory, "_out", "performance", "compact-geometry");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, $"concurrent-{Guid.NewGuid():N}.json"), JsonSerializer.Serialize(new
            {
                wkbBytes = bytes.Length, retained, afterDistanceRetained, statistics,
                scope = "Two compact entries with overlapping distance queries; heap delta excludes preexisting fixture buffers; no RSS gate."
            }));
            GC.KeepAlive(bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class ConcurrentPoint(Barrier ready) : Point(50, 50)
    {
        private bool _signalled;

        public override Coordinate Coordinate
        {
            get
            {
                if (!_signalled)
                {
                    _signalled = true;
                    Assert.IsTrue(ready.SignalAndWait(TimeSpan.FromSeconds(10)), "Separate compact gates must allow both entries to enter evaluation.");
                }

                return base.Coordinate;
            }
        }
    }
}
