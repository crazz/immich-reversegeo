using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class BundledCountryArtifactValidatorTests
{
    private static readonly CountryResolutionFixture HongKong = new("Hong Kong fixture", 0.5, 0.5, "Hong Kong", "HKG", "HK", "CN");

    [TestMethod]
    public void ValidArtifact_ReportsStatisticsAndExactTerritoryCoverage()
    {
        WithArtifact(null, (path, catalog) =>
        {
            var stats = BundledCountryArtifactValidator.Validate(path, catalog, [HongKong], []);
            Assert.AreEqual("fixture-release", stats.Release);
            Assert.AreEqual(2L, stats.RowCount);
            Assert.AreEqual(2, stats.StandardIdentityCount);
            Assert.IsTrue(stats.WkbBytes > 0);
        });
    }

    [TestMethod]
    [DataRow("UPDATE division_area SET alpha3 = 'CHN' WHERE country = 'HK'")]
    [DataRow("UPDATE division_area SET name = 'China' WHERE country = 'HK'")]
    [DataRow("UPDATE division_area SET source_country = 'ZZ' WHERE country = 'HK'")]
    [DataRow("UPDATE division_area SET source_country = 'CN' WHERE country = 'HK'")]
    [DataRow("UPDATE division_area SET source_country = 'XA' WHERE country = 'HK'")]
    [DataRow("UPDATE division_area SET subtype = 'locality' WHERE country = 'HK'")]
    [DataRow("UPDATE division_area SET geom_wkb = NULL WHERE country = 'HK'")]
    [DataRow("UPDATE division_area SET bbox_xmax = 0.1 WHERE country = 'HK'")]
    [DataRow("UPDATE division_area SET bbox_xmin = 1, bbox_xmax = 0 WHERE country = 'HK'")]
    [DataRow("DELETE FROM _meta WHERE key = 'release'")]
    [DataRow("DELETE FROM division_area")]
    [DataRow("ALTER TABLE division_area DROP COLUMN alpha3")]
    public void InvalidArtifact_IsRejected(string mutation)
    {
        WithArtifact(mutation, (path, catalog) =>
            Assert.ThrowsExactly<InvalidDataException>(() => BundledCountryArtifactValidator.Validate(path, catalog, [], [])));
    }

    [TestMethod]
    public void SovereignOnlyCoverage_DoesNotSatisfyTerritoryFixture()
    {
        WithArtifact("DELETE FROM division_area WHERE country = 'HK'", (path, catalog) =>
            Assert.ThrowsExactly<InvalidDataException>(() => BundledCountryArtifactValidator.Validate(path, catalog, [HongKong], [])));
    }

    [TestMethod]
    public void NearbyBoundary_DoesNotSatisfyExactCoverageFixture()
    {
        WithArtifact(null, (path, catalog) =>
            Assert.ThrowsExactly<InvalidDataException>(() => BundledCountryArtifactValidator.Validate(
                path, catalog, [HongKong with { Longitude = 1.0001 }], [])));
    }

    [TestMethod]
    [DataRow("point")]
    [DataRow("line")]
    public void NonPolygonBoundary_IsRejected(string shape)
    {
        WithArtifact(null, (path, catalog) =>
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                var factory = new GeometryFactory();
                Geometry geometry = shape == "point"
                    ? factory.CreatePoint(new Coordinate(0.5, 0.5))
                    : factory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)]);
                command.CommandText = "UPDATE division_area SET geom_wkb = $geometry WHERE country = 'HK'";
                command.Parameters.AddWithValue("$geometry", new WKBWriter().Write(geometry));
                command.ExecuteNonQuery();
            }

            Assert.ThrowsExactly<InvalidDataException>(() => BundledCountryArtifactValidator.Validate(path, catalog, [HongKong], []));
        });
    }
    private static void WithArtifact(string? mutation, Action<string, CountryIdentityCatalog> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"reversegeo-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "country.db");
        try
        {
            var catalogPath = Path.Combine(root, "catalog.json");
            File.WriteAllText(catalogPath, """
                {"iso3ToAlpha2":{"HKG":"HK","CHN":"CN"},"displayNamesByAlpha2":{"HK":"Hong Kong","CN":"China"},"explicitlyNonIsoAlpha2Codes":["XA"]}
                """);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE division_area (
                        id TEXT PRIMARY KEY, name TEXT, source_name TEXT, subtype TEXT, class_name TEXT,
                        admin_level INTEGER, country TEXT, source_country TEXT, alpha3 TEXT,
                        is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                        bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
                    CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT);
                    INSERT INTO _meta VALUES ('release', 'fixture-release');
                    INSERT INTO division_area VALUES ('hk','Hong Kong','Hong Kong','dependency',NULL,2,'HK','HK','HKG',1,0,$geometry,0,0,1,1);
                    INSERT INTO division_area VALUES ('cn','China','China','country',NULL,2,'CN','CN','CHN',1,0,$geometry,0,0,1,1);
                    """;
                var geometry = new GeometryFactory().CreatePolygon([
                    new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1),
                    new Coordinate(0, 1), new Coordinate(0, 0)]);
                command.Parameters.AddWithValue("$geometry", new WKBWriter().Write(geometry));
                command.ExecuteNonQuery();
                if (mutation is not null)
                {
                    command.Parameters.Clear();
                    command.CommandText = mutation;
                    command.ExecuteNonQuery();
                }
            }

            check(path, CountryIdentityCatalog.Load(catalogPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}