using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Spatial.Tests;

[TestClass]
public sealed class CompactCacheFallbackTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LargeUnsupportedOrMalformedInputDoesNotRetainRawStateUnderCompactCharge(bool malformed)
    {
        var bytes = UnsupportedLine();
        if (malformed)
        {
            Array.Fill(bytes, (byte)255);
        }

        var path = Path.GetTempFileName();
        try
        {
            using var cache = new AdministrativeGeometryCache(8 * 1024 * 1024);
            var generation = cache.ObserveGeneration(GeometrySource.Gadm, "AAA", path);
            for (var i = 0; i < 3; i++)
            {
                Assert.IsFalse(cache.Covers(generation, "area", bytes.Length, () => bytes, new Point(0, 0)), "Ineligible geometry retains its reference non-match.");
                Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes, "No compact reservation or raw entry survives evaluation.");
            }

            Assert.AreEqual(3L, cache.GetStatistics().UnretainedEvaluations, "Each unsupported candidate follows the reference path.");
            Assert.AreEqual(0L, cache.GetStatistics().CompactConstructions, "Ineligible input cannot publish compact state.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void MetadataMismatchCannotPublishUnderAnIncorrectCompactEstimate()
    {
        var bytes = UnsupportedLine();
        var path = Path.GetTempFileName();
        try
        {
            using var cache = new AdministrativeGeometryCache(8 * 1024 * 1024);
            var generation = cache.ObserveGeneration(GeometrySource.Overture, "AAA", path);
            Assert.IsFalse(cache.Covers(generation, "area", bytes.Length + 1, () => bytes, new Point(0, 0)));
            Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes, "Mismatched byte length must release the reservation.");
            Assert.AreEqual(0, cache.GetStatistics().RetainedEntries, "Mismatched input cannot become a cache entry.");
            Assert.AreEqual(1L, cache.GetStatistics().UnretainedEvaluations, "The compatible unretained path remains available.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] UnsupportedLine()
    {
        var coordinates = new Coordinate[15000];
        for (var i = 0; i < coordinates.Length; i++)
        {
            coordinates[i] = new Coordinate(10 + i, 10);
        }

        return new WKBWriter().Write(new LineString(coordinates));
    }
}
