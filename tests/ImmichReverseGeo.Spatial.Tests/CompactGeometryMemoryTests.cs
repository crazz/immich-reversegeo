using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Spatial.Tests;

[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
public sealed class CompactGeometryMemoryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(15000, 0)]
    [DataRow(1000000, 0)]
    [DataRow(2200000, 0)]
    [DataRow(4, 1000)]
    [DataRow(4, 100000)]
    public void SizeAndRingMatrixPreservesResultsAndReportsScopedMemory(int vertices, int holes)
    {
        var bytes = Fixture(vertices, holes);
        var points = new[] { new Point(0, 0), new Point(1, 0), new Point(1.00015, 0), new Point(3, 3), new Point(3.125, 3.125), new Point(50, 50) };
        var expected = Reference(bytes, points);
        var before = GC.GetTotalMemory(true);
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        var geometry = AdministrativeGeometry.ReadCompact(bytes, default);
        var constructionMs = timer.Elapsed.TotalMilliseconds;
        var constructionAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
        var constructedRetained = GC.GetTotalMemory(true) - before;
        Assert.IsTrue(geometry.IsCompact, "All matrix shapes must be valid nonempty polygons.");
        var timings = new List<double>();
        var allocations = new List<long>();
        for (var repeat = 0; repeat < 3; repeat++)
        {
            for (var i = 0; i < points.Length; i++)
            {
                allocated = GC.GetTotalAllocatedBytes(true);
                timer.Restart();
                var actual = geometry.Covers(points[i], default);
                timings.Add(timer.Elapsed.TotalMilliseconds);
                allocations.Add(GC.GetTotalAllocatedBytes(true) - allocated);
                Assert.AreEqual(expected[i], actual, $"Vertices={vertices}, holes={holes}, repeat={repeat}, point={i}");
            }
        }

        var afterDistanceRetained = GC.GetTotalMemory(true) - before;
        var report = JsonSerializer.Serialize(new
        {
            vertices, holes, wkbBytes = bytes.Length, constructionMs, constructionAllocated,
            constructedRetained, afterDistanceRetained, predicateMilliseconds = timings, predicateAllocations = allocations,
            scope = "Whole test-host managed heap delta; fixture bytes/points preexist baseline; no RSS gate."
        });
        var output = Path.Combine(Environment.CurrentDirectory, "_out", "performance", "compact-geometry");
        Directory.CreateDirectory(output);
        var reportPath = Path.Combine(output, $"{vertices}-{holes}-{Guid.NewGuid():N}.json");
        File.WriteAllText(reportPath, report + Environment.NewLine);
        TestContext.WriteLine($"Evidence: {reportPath}");
        GC.KeepAlive(geometry);
        GC.KeepAlive(bytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool[] Reference(byte[] bytes, Point[] points)
    {
        var geometry = new WKBReader().Read(bytes);
        return points.Select(p => geometry.Covers(p) || geometry.Distance(p) <= 0.00015).ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] Fixture(int vertices, int holes)
    {
        if (holes == 0)
        {
            var coordinates = new Coordinate[vertices + 1];
            for (var i = 0; i < vertices; i++)
            {
                var angle = i * 2 * Math.PI / vertices;
                coordinates[i] = new Coordinate(Math.Cos(angle), Math.Sin(angle));
            }

            coordinates[vertices] = coordinates[0].Copy();
            return new WKBWriter().Write(new Polygon(new LinearRing(coordinates)));
        }

        var side = (int)Math.Ceiling(Math.Sqrt(holes));
        var shell = Rectangle(-1, -1, side + 2, side + 2);
        var rings = new LinearRing[holes];
        for (var i = 0; i < holes; i++)
        {
            var x = 1 + i % side;
            var y = 1 + i / side;
            rings[i] = Rectangle(x, y, x + 0.25, y + 0.25);
        }

        return new WKBWriter().Write(new Polygon(shell, rings));
    }

    private static LinearRing Rectangle(double minX, double minY, double maxX, double maxY)
        => new([new Coordinate(minX, minY), new Coordinate(maxX, minY), new Coordinate(maxX, maxY), new Coordinate(minX, maxY), new Coordinate(minX, minY)]);
}
