using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Spatial;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Gadm.Tests;

[TestClass]
public sealed class GadmPreparedLookupTests
{
    [TestMethod]
    public async Task PreparedLookupMatchesReferenceDiagnosticsAndDoesNotReadBlobsOnHit()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        var reads = 0;
        var prepared = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, fixture.Root, cache,
            checkpoint => { if (checkpoint == GadmDivisionsService.GadmLookupCheckpoint.AfterGeometryBlobRead) { reads++; } });
        var reference = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, fixture.Root);
        var points = new[] { (1d, 1d), (2d, 2d), (0d, 5d), (8d, 8d), (4.0001d, 3d) };
        var expectedResults = await Task.WhenAll(points.Select(point =>
            reference.FindContainingDivisionAreasAsync(point.Item1, point.Item2, "USA")));
        using (var connection = new SqliteConnection($"Data Source={fixture.Database};Pooling=false"))
        {
            connection.Open();
            Assert.IsTrue(AdministrativeCandidateIndex.Build(connection, GeometrySource.Gadm, default));
        }
        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index];
            var expected = expectedResults[index];
            var actual = await prepared.FindContainingDivisionAreasAsync(point.Item1, point.Item2, "USA");
            Assert.IsNull(actual.Error);
            Assert.AreEqual(expected.BestMatch, actual.BestMatch);
            Assert.AreEqual(expected.Version, actual.Version);
            CollectionAssert.AreEqual(expected.Candidates.ToArray(), actual.Candidates.ToArray());
        }

        Assert.AreEqual(2, reads, "The two candidate blobs are each materialized once across distinct points.");
    }

    [TestMethod]
    public async Task CountryOnlyGeometryMatchStillSuppressesBoundingBoxCityFallback()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        var service = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, fixture.Root, cache);
        var result = await service.ResolveAdministrativeGeoAsync(8, 8, "USA");
        Assert.IsNotNull(result);
        Assert.IsNull(result.City, "Country containment must not be filtered out before city selection.");
    }

    [TestMethod]
    public async Task SamePathReplacementDoesNotReusePreviousPreparedGeometry()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        var service = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, fixture.Root, cache);
        var before = await service.FindContainingDivisionAreasAsync(1, 1, "USA");
        Assert.IsTrue(before.Candidates.Any(x => x.GeometryContainsPoint));
        File.Delete(fixture.Database);
        fixture.Create("POLYGON ((20 20, 30 20, 30 30, 20 30, 20 20))");
        var after = await service.FindContainingDivisionAreasAsync(1, 1, "USA");
        Assert.IsNull(after.Error);
        Assert.IsTrue(after.Candidates.All(x => !x.GeometryContainsPoint), "New generation must load its own geometry.");
    }

    [TestMethod]
    public async Task CancellationAfterBlobReadCannotPublishAndSourceHandleIsReleased()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        using var cancellation = new CancellationTokenSource();
        var service = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, fixture.Root, cache,
            checkpoint => { if (checkpoint == GadmDivisionsService.GadmLookupCheckpoint.AfterGeometryBlobRead) { cancellation.Cancel(); } });
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.FindContainingDivisionAreasAsync(1, 1, "USA", cancellation.Token));
        Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes, "Cancellation releases its build reservation.");
        using var connection = new SqliteConnection($"Data Source={fixture.Database};Pooling=false;Default Timeout=1");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE _meta SET value='after-cancellation'";
        Assert.AreEqual(1, command.ExecuteNonQuery(), "The cancelled read must not keep its source transaction locked.");
    }

    [TestMethod]
    public async Task AcceleratedReadFailure_DiscardsPartialRowsBeforeOneLegacyRetry()
    {
        using var fixture = new Fixture();
        var reference = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, fixture.Root);
        var expected = await reference.FindContainingDivisionAreasAsync(1, 1, "USA");
        using (var connection = new SqliteConnection($"Data Source={fixture.Database};Pooling=false"))
        {
            connection.Open();
            Assert.IsTrue(AdministrativeCandidateIndex.Build(connection, GeometrySource.Gadm, default));
        }
        using var cache = new AdministrativeGeometryCache(10_000_000);
        var reads = 0;
        var service = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, fixture.Root, cache,
            checkpoint =>
            {
                if (checkpoint == GadmDivisionsService.GadmLookupCheckpoint.AfterCandidateRowRead && ++reads == 2)
                {
                    throw new SqliteException("controlled optional reader failure", 1);
                }
            });
        var actual = await service.FindContainingDivisionAreasAsync(1, 1, "USA");
        Assert.IsNull(actual.Error);
        Assert.AreEqual(5, reads, "Two partial reads plus one full two-row legacy scan and EOF.");
        Assert.AreEqual(expected.BestMatch, actual.BestMatch);
        CollectionAssert.AreEqual(expected.Candidates.ToArray(), actual.Candidates.ToArray(), "No partial or duplicate candidates survive the retry.");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        internal string Database => Path.Combine(Root, "gadm-divisions", "USA.db");

        internal Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Database)!);
            Create();
        }

        internal void Create(string? replacement = null)
        {
            using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO _meta VALUES ('version', 'fixture');
                CREATE TABLE gadm_area (id TEXT PRIMARY KEY, name TEXT, english_type TEXT, local_type TEXT,
                    admin_level INTEGER, geom_wkb BLOB, bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
                """;
            command.ExecuteNonQuery();
            for (var i = 0; i < 2; i++)
            {
                command.CommandText = "INSERT INTO gadm_area VALUES ($id, $id, NULL, NULL, $level, $geometry, 0, 0, 10, 10)";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", i == 0 ? "country" : "city");
                command.Parameters.AddWithValue("$level", i * 2);
                var wkt = replacement ?? (i == 0
                    ? "POLYGON ((0 0, 10 0, 9 10, 0 10, 0 0))"
                    : "POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0))");
                command.Parameters.AddWithValue("$geometry", new WKBWriter().Write(new WKTReader().Read(wkt)));
                command.ExecuteNonQuery();
            }
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
