using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Spatial;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Tests;

[TestClass]
public sealed class PreparedAdministrativeProvidersTests
{
    [TestMethod]
    public async Task BothProvidersShareAdmissionAndDoNotConfuseSameAreaIdsOrCoordinatesAcrossCountries()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(3_000_000);
        var gadm = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, fixture.Root, cache);
        var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, fixture.Root, fixture.Root);
        var overture = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance,
            places, fixture.Root, fixture.Root, _ => "USA", cache);

        var first = await gadm.FindContainingDivisionAreasAsync(1, 1, "USA");
        var repeated = await gadm.FindContainingDivisionAreasAsync(2, 2, "USA");
        Assert.IsTrue(first.BestMatch!.GeometryContainsPoint);
        Assert.IsTrue(repeated.BestMatch!.GeometryContainsPoint);
        Assert.AreEqual(1L, cache.GetStatistics().BlobLoads, "Different coordinates in one area reuse its blob.");
        var otherSource = await overture.FindContainingDivisionAreasAsync(1, 1, "US", "USA");
        Assert.IsNull(otherSource.Error);
        Assert.IsFalse(otherSource.BestMatch!.GeometryContainsPoint, "The Overture area with the same ID has different geometry.");
        Assert.AreEqual(2L, cache.GetStatistics().BlobLoads);
        Assert.AreEqual(1, cache.GetStatistics().RetainedEntries, "The two providers share a budget that fits one fixture entry.");
        var anotherCountry = await overture.FindContainingDivisionAreasAsync(1, 1, "CA", "CAN");
        Assert.IsTrue(anotherCountry.BestMatch!.GeometryContainsPoint, "The same coordinates and ID in another country must load that country's shape.");
        var again = await gadm.FindContainingDivisionAreasAsync(1, 1, "USA");
        Assert.AreEqual(first.BestMatch, again.BestMatch, "Eviction changes reuse, never the selected result.");
        Assert.AreEqual(4L, cache.GetStatistics().BlobLoads, "Cross-source pressure evicts through one shared owner.");
        Assert.IsTrue(cache.GetStatistics().AccountedBytes <= 3_000_000);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        internal Fixture()
        {
            Create("gadm-divisions", "USA", true);
            Create("overture-divisions", "USA", false);
            Create("overture-divisions", "CAN", true);
        }

        private void Create(string source, string country, bool contains)
        {
            var directory = Path.Combine(Root, source);
            Directory.CreateDirectory(directory);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, country + ".db")};Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT); INSERT INTO _meta VALUES ('version','fixture'),('release','fixture');";
            command.ExecuteNonQuery();
            command.CommandText = source == "gadm-divisions"
                ? """
                    CREATE TABLE gadm_area (id TEXT PRIMARY KEY, name TEXT, english_type TEXT, local_type TEXT,
                        admin_level INTEGER, geom_wkb BLOB, bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
                    INSERT INTO gadm_area VALUES ('same-id','fixture',NULL,NULL,2,$geometry,0,0,10,10);
                    """
                : """
                    CREATE TABLE division_area (id TEXT PRIMARY KEY, name TEXT, subtype TEXT, class_name TEXT,
                        admin_level INTEGER, country TEXT, is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                        bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
                    INSERT INTO division_area VALUES ('same-id','fixture','locality',NULL,2,NULL,1,1,$geometry,0,0,10,10);
                    """;
            var wkt = contains ? "POLYGON ((0 0, 10 0, 9 10, 0 10, 0 0))"
                : "POLYGON ((20 20, 30 20, 30 30, 20 30, 20 20))";
            command.Parameters.AddWithValue("$geometry", new WKBWriter().Write(new WKTReader().Read(wkt)));
            command.ExecuteNonQuery();
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
