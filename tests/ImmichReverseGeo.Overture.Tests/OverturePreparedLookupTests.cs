using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Spatial;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public sealed class OverturePreparedLookupTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PreparedLookupPreservesDiagnosticsWithNullableGeometryAndBothSchemaVersions(bool hasAdminLevel)
    {
        using var fixture = new Fixture(hasAdminLevel);
        using var cache = new AdministrativeGeometryCache(10_000_000);
        var reads = 0;
        var prepared = fixture.Service(new OvertureDivisionsTestHooks
        {
            SpatialCache = cache,
            AdministrativeCheckpoint = checkpoint =>
            {
                if (checkpoint == OvertureAdministrativeCheckpoint.AfterGeometryBlobRead)
                {
                    reads++;
                }
            }
        });
        var reference = fixture.Service();
        foreach (var point in new[] { (1d, 1d), (2d, 2d), (0d, 5d), (8d, 8d) })
        {
            var expected = await reference.FindContainingDivisionAreasAsync(point.Item1, point.Item2, "US", "USA");
            var actual = await prepared.FindContainingDivisionAreasAsync(point.Item1, point.Item2, "US", "USA");
            Assert.IsNull(actual.Error, "The prepared path must retain source availability.");
            Assert.AreEqual(expected.BestMatch, actual.BestMatch);
            Assert.AreEqual(expected.Release, actual.Release);
            CollectionAssert.AreEqual(expected.Candidates.ToArray(), actual.Candidates.ToArray(), "Candidate order, flags and decisions must match.");
        }

        Assert.AreEqual(2, reads, "Each non-null candidate blob is read once across distinct points.");
    }

    [TestMethod]
    public async Task ReplacementDuringReadKeepsOneSnapshotAndCannotPublishRetiredGeometry()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("SQLite on Windows does not share open database handles for deletion; this race exercises Unix replacement semantics.");
        }

        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        var replaced = false;
        var service = fixture.Service(new OvertureDivisionsTestHooks
        {
            SpatialCache = cache,
            AdministrativeCheckpoint = checkpoint =>
            {
                if (checkpoint == OvertureAdministrativeCheckpoint.AfterCandidateMetadata && !replaced)
                {
                    var next = fixture.Database + ".replacement";
                    fixture.Create(next, replacement: true);
                    File.Move(next, fixture.Database, overwrite: true);
                    replaced = true;
                }
            }
        });
        var oldSnapshot = await service.FindContainingDivisionAreasAsync(1, 1, "US", "USA");
        Assert.IsNull(oldSnapshot.Error);
        Assert.AreEqual("original", oldSnapshot.Release, "Metadata and blobs belong to the same open snapshot.");
        Assert.IsTrue(oldSnapshot.Candidates.Any(x => x.GeometryContainsPoint));
        Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes, "Retired preparation cannot be published.");
        var current = await service.FindContainingDivisionAreasAsync(1, 1, "US", "USA");
        Assert.IsNull(current.Error);
        Assert.AreEqual("replacement", current.Release);
        Assert.IsTrue(current.Candidates.All(x => !x.GeometryContainsPoint), "Same IDs in the new file must use new geometry.");
    }

    [TestMethod]
    public async Task ReplacementBetweenQueriesRetiresGeometryOnEveryPlatform()
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        var service = fixture.Service(new OvertureDivisionsTestHooks { SpatialCache = cache });
        var before = await service.FindContainingDivisionAreasAsync(1, 1, "US", "USA");
        Assert.IsTrue(before.Candidates.Any(x => x.GeometryContainsPoint));
        var next = fixture.Database + ".replacement";
        fixture.Create(next, replacement: true);
        File.Move(next, fixture.Database, overwrite: true);
        var after = await service.FindContainingDivisionAreasAsync(1, 1, "US", "USA");
        Assert.IsNull(after.Error);
        Assert.AreEqual("replacement", after.Release);
        Assert.IsTrue(after.Candidates.All(x => !x.GeometryContainsPoint), "Closed-file replacement must invalidate the old retained shapes.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationOrCriticalFailureAfterBlobReadReleasesReservationAndReadTransaction(bool outOfMemory)
    {
        using var fixture = new Fixture();
        using var cache = new AdministrativeGeometryCache(10_000_000);
        using var cancellation = new CancellationTokenSource();
        var service = fixture.Service(new OvertureDivisionsTestHooks
        {
            SpatialCache = cache,
            AdministrativeCheckpoint = checkpoint =>
            {
                if (checkpoint == OvertureAdministrativeCheckpoint.AfterGeometryBlobRead)
                {
                    if (outOfMemory)
                    {
                        throw new OutOfMemoryException("controlled source failure");
                    }

                    cancellation.Cancel();
                }
            }
        });
        Task Lookup() => service.FindContainingDivisionAreasAsync(1, 1, "US", "USA", cancellation.Token);
        if (outOfMemory)
        {
            await Assert.ThrowsAsync<OutOfMemoryException>(Lookup);
        }
        else
        {
            await Assert.ThrowsAsync<OperationCanceledException>(Lookup);
        }

        Assert.AreEqual(0L, cache.GetStatistics().AccountedBytes, "The interrupted loader releases admission.");
        using var connection = new SqliteConnection($"Data Source={fixture.Database};Pooling=false;Default Timeout=1");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE _meta SET value='released'";
        Assert.AreEqual(1, command.ExecuteNonQuery(), "No abandoned read transaction may block a writer.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly bool _hasAdminLevel;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        internal string Database => Path.Combine(Root, "overture-divisions", "USA.db");

        internal Fixture(bool hasAdminLevel = true)
        {
            _hasAdminLevel = hasAdminLevel;
            Directory.CreateDirectory(Path.GetDirectoryName(Database)!);
            Create(Database);
        }

        internal OvertureDivisionsService Service(OvertureDivisionsTestHooks? hooks = null)
        {
            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, Root, Root);
            return hooks is null
                ? new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, Root, Root, _ => "USA")
                : new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, Root, Root, _ => "USA", hooks);
        }

        internal void Create(string path, bool replacement = false)
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            var adminColumn = _hasAdminLevel ? "admin_level INTEGER," : "";
            command.CommandText = $"""
                CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO _meta VALUES ('release', '{(replacement ? "replacement" : "original")}');
                CREATE TABLE division_area (id TEXT PRIMARY KEY, name TEXT, subtype TEXT, class_name TEXT,
                    {adminColumn} country TEXT, is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                    bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
                """;
            command.ExecuteNonQuery();
            for (var i = 0; i < 3; i++)
            {
                command.Parameters.Clear();
                var level = _hasAdminLevel ? "2," : "";
                command.CommandText = $"INSERT INTO division_area VALUES ($id, $id, $subtype, NULL, {level} 'US', 1, 1, $geometry, 0, 0, 10, 10)";
                command.Parameters.AddWithValue("$id", i.ToString());
                command.Parameters.AddWithValue("$subtype", i == 0 ? "country" : "locality");
                var wkt = replacement ? "POLYGON ((20 20, 30 20, 30 30, 20 30, 20 20))"
                    : i == 0 ? "POLYGON ((0 0, 10 0, 9 10, 0 10, 0 0))" : "POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0))";
                command.Parameters.AddWithValue("$geometry", i == 2 ? DBNull.Value : new WKBWriter().Write(new WKTReader().Read(wkt)));
                command.ExecuteNonQuery();
            }
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
