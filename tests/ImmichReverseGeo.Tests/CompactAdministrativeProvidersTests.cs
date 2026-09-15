using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Spatial;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Tests;

[TestClass]
public sealed class CompactAdministrativeProvidersTests
{
    [TestMethod]
    [DataRow("GB", "GBR")]
    [DataRow("US", "USA")]
    [DataRow("JP", "JPN")]
    public async Task BothProvidersPreserveReferenceResultsWhenBudgetSelectsCompactGeometry(string iso2, string iso3)
    {
        using var fixture = new Fixture(iso3);
        using var cached = new AdministrativeGeometryCache(8 * 1024 * 1024);
        using var uncached = new AdministrativeGeometryCache(0);
        var gadm = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, fixture.Root, cached);
        var gadmReference = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, fixture.Root, uncached);
        var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, fixture.Root, fixture.Root);
        var overture = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, fixture.Root, fixture.Root, _ => iso3, cached);
        var overtureReference = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, fixture.Root, fixture.Root, _ => iso3, uncached);

        foreach (var point in new[] { 0d, 0.2, 1d, 1.00016, 3d })
        {
            var expected = await gadmReference.FindContainingDivisionAreasAsync(0, point, iso3);
            var actual = await gadm.FindContainingDivisionAreasAsync(0, point, iso3);
            Assert.AreEqual(expected.BestMatch, actual.BestMatch, $"GADM {iso3}, longitude={point}");
        }

        Assert.AreEqual(1L, cached.GetStatistics().CompactConstructions, "GADM queries share one compact entry.");
        Assert.IsTrue(cached.GetStatistics().CompactHits > 0, "Different GADM points reuse the entry.");
        foreach (var point in new[] { 0d, 0.2, 1d, 1.00016, 3d })
        {
            var expected = await overtureReference.FindContainingDivisionAreasAsync(0, point, iso2, iso3);
            var actual = await overture.FindContainingDivisionAreasAsync(0, point, iso2, iso3);
            Assert.AreEqual(expected.BestMatch, actual.BestMatch, $"Overture {iso3}, longitude={point}");
            Assert.AreEqual(expected.Error, actual.Error, "Representation cannot alter source diagnostics.");
        }

        Assert.AreEqual(2L, cached.GetStatistics().CompactConstructions, "Source identity keeps each provider's entry separate.");
        Assert.IsTrue(cached.GetStatistics().AccountedBytes <= 8 * 1024 * 1024, "Both sources use one budget.");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        internal Fixture(string country)
        {
            const int count = 15000;
            var coordinates = new Coordinate[count + 1];
            for (var i = 0; i < count; i++)
            {
                var angle = i * Math.PI * 2 / count;
                coordinates[i] = new Coordinate(Math.Cos(angle), Math.Sin(angle));
            }

            coordinates[count] = coordinates[0].Copy();
            var bytes = new WKBWriter().Write(new Polygon(new LinearRing(coordinates)));
            Create("gadm-divisions", country, bytes);
            Create("overture-divisions", country, bytes);
        }

        private void Create(string source, string country, byte[] bytes)
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
                    INSERT INTO gadm_area VALUES ('area','fixture',NULL,NULL,2,$geometry,-1,-1,1,1);
                    """
                : """
                    CREATE TABLE division_area (id TEXT PRIMARY KEY, name TEXT, subtype TEXT, class_name TEXT,
                        admin_level INTEGER, country TEXT, is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                        bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
                    INSERT INTO division_area VALUES ('area','fixture','locality',NULL,2,NULL,1,1,$geometry,-1,-1,1,1);
                    """;
            command.Parameters.AddWithValue("$geometry", bytes);
            command.ExecuteNonQuery();
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
