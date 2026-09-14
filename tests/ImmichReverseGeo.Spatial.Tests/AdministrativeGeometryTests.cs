using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Spatial.Tests;

[TestClass]
public sealed class AdministrativeGeometryTests
{
    // Synthetic, original fixtures: no private coordinates or third-party polygons.
    [TestMethod]
    [DataRow("POLYGON ((0 0, 10 0, 9 10, 0 10, 0 0), (2 2, 2 4, 4 4, 4 2, 2 2))")]
    [DataRow("MULTIPOLYGON (((0 0, 2 0, 2 2, 0 2, 0 0)), ((4 4, 6 4, 6 6, 4 6, 4 4)))")]
    [DataRow("MULTIPOLYGON (((0 0, 4 0, 4 4, 0 4, 0 0)), ((2 2, 6 2, 6 6, 2 6, 2 2)))")]
    [DataRow("POLYGON ((0 0, 4 4, 4 0, 0 4, 0 0))")]
    [DataRow("POLYGON EMPTY")]
    [DataRow("LINESTRING (0 0, 5 5)")]
    public void PreparedAndReferenceEvaluationAgree(string wkt)
    {
        var reference = new WKTReader().Read(wkt);
        var candidate = AdministrativeGeometry.Read(new WKBWriter().Write(reference), true, default);
        var coordinates = new (double X, double Y)[]
        {
            (0, 0), (0, 5), (1, 1), (3, 3), (2, 3), (4, 3), (5, 5), (8, 8),
            (12, 12), (-0.00014, 1), (-0.00016, 1), (2.00014, 3), (2.00016, 3)
        };
        foreach (var (x, y) in coordinates)
        {
            var point = reference.Factory.CreatePoint(new Coordinate(x, y));
            Assert.AreEqual(ReferenceContains(reference, point), candidate.Covers(point, default), $"{wkt}: {x}, {y}");
        }
    }

    [TestMethod]
    public void MalformedInputRemainsCandidateLocal()
    {
        var candidate = AdministrativeGeometry.Read([1], true, default);
        Assert.IsFalse(candidate.Covers(new Point(0, 0), default));
    }

    [TestMethod]
    public void CancellationIsCheckedBeforeReadingAndReturning()
    {
        using var cancellation = new CancellationTokenSource();
        var candidate = AdministrativeGeometry.Read([1], true, default);
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => AdministrativeGeometry.Read([1], true, cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => candidate.Covers(new Point(0, 0), cancellation.Token));
    }

    [TestMethod]
    public void MemoryPolicyBoundsAndRejectsOverflow()
    {
        Assert.AreEqual(1024L * 1024 * 1024, SpatialMemoryPolicy.BudgetFor(4L * 1024 * 1024 * 1024));
        Assert.AreEqual(512L * 1024 * 1024, SpatialMemoryPolicy.BudgetFor(2L * 1024 * 1024 * 1024));
        Assert.AreEqual(128L * 1024 * 1024, SpatialMemoryPolicy.BudgetFor(0));
        Assert.IsFalse(SpatialMemoryPolicy.TryEstimate(long.MaxValue, out _, out _));
        Assert.IsFalse(SpatialMemoryPolicy.TryEstimate(-1, out _, out _));
    }

    private static bool ReferenceContains(Geometry geometry, Point point)
    {
        try
        {
            return geometry.Covers(point) || geometry.Distance(point) <= 0.00015;
        }
        catch (ParseException)
        {
            return false;
        }
        catch (TopologyException)
        {
            return false;
        }
    }
}
