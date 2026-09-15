using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Spatial.Tests;

[TestClass]
public sealed class CompactAdministrativeGeometryTests
{
    [TestMethod]
    [DataRow("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0), (2 2, 2 4, 4 4, 4 2, 2 2))", true)]
    [DataRow("MULTIPOLYGON (((0 0, 2 0, 2 2, 0 2, 0 0)), ((2 2, 4 2, 4 4, 2 4, 2 2)))", true)]
    [DataRow("MULTIPOLYGON (((0 0, 2 0, 2 2, 0 2, 0 0)), ((40 40, 60 40, 60 60, 40 60, 40 40)))", true)]
    [DataRow("POLYGON ((0 0, 9 10, 10 0, 0 0))", true)]
    [DataRow("MULTIPOLYGON (((0 0, 4 0, 4 4, 0 4, 0 0)), ((2 2, 6 2, 6 6, 2 6, 2 2)))", false)]
    [DataRow("POLYGON ((0 0, 4 4, 4 0, 0 4, 0 0))", false)]
    [DataRow("POLYGON EMPTY", false)]
    [DataRow("LINESTRING (0 0, 5 5)", false)]
    [DataRow("GEOMETRYCOLLECTION (POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0)), POINT (5 5))", false)]
    public void FullPredicateMatchesReferenceForBoundaryHoleAndExteriorPoints(string wkt, bool eligible)
    {
        var reference = new WKTReader().Read(wkt);
        var candidate = AdministrativeGeometry.ReadCompact(new WKBWriter().Write(reference), default);
        Assert.AreEqual(eligible, candidate.IsCompact);
        var points = new List<Point> { reference.Factory.CreatePoint() };
        for (var x = -1; x <= 12; x++)
        {
            for (var y = -1; y <= 12; y++)
            {
                points.Add(new Point(x, y));
            }
        }

        foreach (var distance in new[] { 0d, Math.BitDecrement(0.00015), 0.00015, Math.BitIncrement(0.00015), 0.00014, 0.00016 })
        {
            points.Add(new Point(-distance, 1));
            points.Add(new Point(2 + distance, 3));
            points.Add(new Point(10 + distance, 5));
        }

        foreach (var coordinate in reference.Coordinates)
        {
            points.Add(new Point(coordinate));
        }

        foreach (var point in points)
        {
            Assert.AreEqual(ReferenceContains(reference, point), candidate.Covers(point, default), $"{wkt}: {point}");
        }
    }

    [TestMethod]
    [DataRow("POLYGON Z ((0 0 4, 10 0 5, 10 10 6, 0 10 7, 0 0 4))")]
    [DataRow("POLYGON M ((0 0 4, 10 0 5, 10 10 6, 0 10 7, 0 0 4))")]
    [DataRow("POLYGON ZM ((0 0 4 8, 10 0 5 9, 10 10 6 10, 0 10 7 11, 0 0 4 8))")]
    public void DimensionalAndSridInputPreservesPlanarResults(string wkt)
    {
        var geometry = new WKTReader().Read(wkt);
        geometry.SRID = 4326;
        var bytes = new WKBWriter(ByteOrder.LittleEndian, true, true, true).Write(geometry);
        var reference = new WKBReader().Read(bytes);
        var candidate = AdministrativeGeometry.ReadCompact(bytes, default);
        Assert.IsTrue(candidate.IsCompact, "Valid Z/M/SRID polygons remain eligible for compact evaluation.");
        foreach (var point in new[] { new Point(0, 0), new Point(5, 5), new Point(11, 11), new Point(-0.00015, 5) })
        {
            Assert.AreEqual(ReferenceContains(reference, point), candidate.Covers(point, default), $"{wkt}: {point}");
        }
    }

    [TestMethod]
    public void MalformedInputAndCancellationPreserveTheirOutcomes()
    {
        var candidate = AdministrativeGeometry.ReadCompact([1], default);
        Assert.IsFalse(candidate.IsCompact, "Malformed WKB cannot publish compact state.");
        Assert.IsFalse(candidate.Covers(new Point(0, 0), default), "Malformed WKB retains the reference non-match.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => AdministrativeGeometry.ReadCompact([1], cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => candidate.Covers(new Point(0, 0), cancellation.Token));
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
