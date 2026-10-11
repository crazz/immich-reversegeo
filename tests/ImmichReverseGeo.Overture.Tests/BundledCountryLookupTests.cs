using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Tests;

/// <summary>
/// Regression tests for the bundled country lookup, built against a synthetic two-country
/// database so they run without the 155 MB LFS-backed bundled dataset.
///
/// The scenario reproduces the shape that caused repeated OOM kills in production: a country
/// whose bounding box spans most of the globe (antimeridian crossings and distant overseas
/// territories do this) is returned by the spatial index for practically every point on earth.
/// The lookup must reject it using the prepared-geometry index alone — never by measuring
/// distance to its full boundary, which copies every ring into a fresh Coordinate[].
/// </summary>
[TestClass]
public class BundledCountryLookupTests
{
    private const double PointLon = 10.0;
    private const double PointLat = 50.0;

    [TestMethod]
    public async Task PointInsideCompactCountry_IsNotConfusedBySprawlingNeighbour()
    {
        var root = CreateFixture();
        try
        {
            var service = CreateService(root);

            var result = await service.FindBundledCountryAsync(PointLat, PointLon);

            Assert.AreEqual("AAA", result.Iso3, "The point lies inside AA, not the globe-spanning SP.");
            Assert.AreEqual("Aaland", result.CountryName);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task PointInsideCompactCountry_DoesNotScanTheSprawlingCountrysBoundary()
    {
        var root = CreateFixture();
        try
        {
            var service = CreateService(root);

            // Warm up: the first call parses the WKB and builds the prepared-geometry indexes.
            var warmup = await service.FindBundledCountryAsync(PointLat, PointLon);
            Assert.AreEqual("AAA", warmup.Iso3);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10; i++)
            {
                _ = await service.FindBundledCountryAsync(PointLat, PointLon);
            }

            var allocatedPerLookup = (GC.GetAllocatedBytesForCurrentThread() - before) / 10;

            // SP's boundary carries ~50k vertices across two rings. Measuring distance to it
            // copies each ring into a new Coordinate[] — roughly 2.4 MB per lookup, much of it
            // straight onto the large object heap. Containment via the prepared index allocates
            // a few KB and never materialises a coordinate array.
            Assert.IsTrue(
                allocatedPerLookup < 512 * 1024,
                $"Expected a warm lookup to allocate well under 512 KB, but it allocated {allocatedPerLookup:N0} bytes. "
                + "This usually means the lookup fell back to a full-boundary distance scan.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task PointJustOutsideBoundary_StillResolvesViaTolerance()
    {
        var root = CreateFixture();
        try
        {
            var service = CreateService(root);

            // Step ~5m beyond the middle of AA's south-west edge, along that edge's normal:
            // outside the boundary, inside the bounding box, within the ~16m tolerance.
            const double offset = 0.00005 / 1.4142135623730951;

            var result = await service.FindBundledCountryAsync(49.5 - offset, 9.5 - offset);

            Assert.AreEqual("AAA", result.Iso3, "Points within the boundary tolerance should still resolve.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task PointOutsideBoundingBox_IsNotRescuedByTolerance()
    {
        var root = CreateFixture();
        try
        {
            var service = CreateService(root);

            // Documents a long-standing limit rather than asserting desirable behaviour: the
            // spatial index is queried with the bare point envelope, so a country whose bounding
            // box excludes the point is never a candidate and the tolerance cannot apply. Sitting
            // ~5m west of AA's bounding box therefore resolves to nothing, even though the
            // tolerance is ~16m. Widening the query envelope would change this, at the cost of
            // pulling more countries into every lookup.
            var result = await service.FindBundledCountryAsync(PointLat, 9.0 - 0.00005);

            Assert.IsNull(result.Iso3);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task PointOutsideEveryBoundary_ReturnsNoMatch()
    {
        var root = CreateFixture();
        try
        {
            var service = CreateService(root);

            // Open water: inside SP's globe-spanning bounding box, inside no actual boundary.
            var result = await service.FindBundledCountryAsync(0.0, 0.0);

            Assert.IsNull(result.Iso3);
            Assert.IsNull(result.CountryName);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static OvertureDivisionsService CreateService(string root)
    {
        var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
        return new OvertureDivisionsService(
            NullLogger<OvertureDivisionsService>.Instance,
            places,
            root,
            root,
            static alpha2 => alpha2.ToUpperInvariant() switch
            {
                "AA" => "AAA",
                "SP" => "SPP",
                _ => null
            });
    }

    /// <summary>
    /// Builds a bundled database holding two countries: a compact square containing the test
    /// point, and a sprawling country made of two dense far-apart blobs whose combined envelope
    /// covers nearly the whole globe while its actual area is nowhere near the test point.
    /// </summary>
    private static string CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-bundled", Guid.NewGuid().ToString("N"));
        var defaultsDir = Path.Combine(root, "defaults");
        Directory.CreateDirectory(defaultsDir);

        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var writer = new WKBWriter();

        // A diamond, so that points exist which fall inside the bounding box but outside the
        // boundary — the only place the tolerance fallback can ever apply (see
        // PointOutsideBoundingBox_IsNotRescuedByTolerance).
        var compact = factory.CreatePolygon([
            new Coordinate(9.0, 50.0),
            new Coordinate(10.0, 49.0),
            new Coordinate(11.0, 50.0),
            new Coordinate(10.0, 51.0),
            new Coordinate(9.0, 50.0)
        ]);

        var sprawling = factory.CreateMultiPolygon([
            CreateDenseBlob(factory, -179.4, -84.4, 0.5, 25_000),
            CreateDenseBlob(factory, 179.4, 84.4, 0.5, 25_000)
        ]);

        using var conn = new SqliteConnection($"Data Source={Path.Combine(defaultsDir, "overture-country-divisions.db")}");
        conn.Open();

        using (var schema = conn.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE division_area (
                    id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    subtype TEXT,
                    class_name TEXT,
                    admin_level INTEGER,
                    country TEXT,
                    is_land INTEGER,
                    is_territorial INTEGER,
                    geom_wkb BLOB,
                    bbox_xmin REAL,
                    bbox_ymin REAL,
                    bbox_xmax REAL,
                    bbox_ymax REAL
                );
                CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO _meta (key, value) VALUES ('release', 'test');
                """;
            schema.ExecuteNonQuery();
        }

        InsertCountry(conn, writer, "aa", "Aaland", "AA", compact);
        InsertCountry(conn, writer, "sp", "Sprawlia", "SP", sprawling);

        return root;
    }

    private static Polygon CreateDenseBlob(GeometryFactory factory, double centreX, double centreY, double radius, int vertices)
    {
        var ring = new Coordinate[vertices + 1];
        for (var i = 0; i < vertices; i++)
        {
            var angle = 2 * Math.PI * i / vertices;
            ring[i] = new Coordinate(
                centreX + (radius * Math.Cos(angle)),
                centreY + (radius * Math.Sin(angle)));
        }

        ring[vertices] = ring[0].Copy();
        return factory.CreatePolygon(ring);
    }

    private static void InsertCountry(
        SqliteConnection conn,
        WKBWriter writer,
        string id,
        string name,
        string alpha2,
        Geometry geometry)
    {
        var envelope = geometry.EnvelopeInternal;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO division_area
                (id, name, subtype, class_name, admin_level, country, is_land, is_territorial,
                 geom_wkb, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax)
            VALUES
                ($id, $name, 'country', NULL, 2, $country, 1, 0,
                 $wkb, $xmin, $ymin, $xmax, $ymax)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$country", alpha2);
        cmd.Parameters.AddWithValue("$wkb", writer.Write(geometry));
        cmd.Parameters.AddWithValue("$xmin", envelope.MinX);
        cmd.Parameters.AddWithValue("$ymin", envelope.MinY);
        cmd.Parameters.AddWithValue("$xmax", envelope.MaxX);
        cmd.Parameters.AddWithValue("$ymax", envelope.MaxY);
        cmd.ExecuteNonQuery();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
