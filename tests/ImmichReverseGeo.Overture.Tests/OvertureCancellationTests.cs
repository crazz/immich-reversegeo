using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class OvertureCancellationTests
{
    [TestMethod]
    public async Task PreCancelledToken_RejectsWarmReleaseAndAvailableBundledOrCachedResults()
    {
        var root = CreateTempDir();
        try
        {
            CreateInfrastructureDb(root, ValidPolygonWkb());
            CreateCountryDb(root, ValidPolygonWkb());
            CreateDivisionDb(root, ValidPolygonWkb());
            var releaseCalls = 0;
            var places = new OverturePlacesService(
                NullLogger<OverturePlacesService>.Instance,
                root,
                root,
                new OverturePlacesTestHooks
                {
                    ReleaseDiscovery = () =>
                    {
                        releaseCalls++;
                        return "test-release";
                    }
                });
            Assert.AreEqual("test-release", await places.GetLatestReleaseForOvertureAsync());
            Assert.AreEqual(1, releaseCalls);
            var divisions = new OvertureDivisionsService(
                NullLogger<OvertureDivisionsService>.Instance,
                places,
                root,
                root,
                _ => "CHE");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                places.GetLatestReleaseForOvertureAsync(cancellation.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                places.FindNearestInfrastructureWithDiagnosticsAsync(47, 8, "CHE", cancellation.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                divisions.FindBundledCountryAsync(47, 8, cancellation.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                divisions.FindContainingDivisionAreasAsync(47, 8, "CH", "CHE", cancellation.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                divisions.ResolveAdministrativeGeoAsync(47, 8, "CH", "CHE", ct: cancellation.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                places.FindNearestPlaceWithDiagnosticsAsync(47, 8, "CH", cancellation.Token));

            var cache = new OvertureDivisionCacheService(
                NullLogger<OvertureDivisionCacheService>.Instance,
                root,
                _ => "CH");
            Assert.Throws<OperationCanceledException>(() => cache.GetOrStartDownload("CHE", cancellation.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() => cache.EnsureDataAsync("CHE", cancellation.Token));
            Assert.AreEqual(1, releaseCalls);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [TestMethod]
    public async Task PlaceLookup_ClassifiesActiveCancellationMemoryAndOrdinaryFailures()
    {
        var root = CreateTempDir();
        try
        {
            using var active = new CancellationTokenSource();
            var activeService = CreatePlaces(root, new OverturePlacesTestHooks
            {
                ReleaseDiscovery = () => "release",
                PlaceQuery = (_, _, _, _, token) =>
                {
                    active.Cancel();
                    throw new OperationCanceledException(token);
                }
            });
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                activeService.FindNearestPlaceWithDiagnosticsAsync(47, 8, "CH", active.Token));

            var oomService = CreatePlaces(root, new OverturePlacesTestHooks
            {
                ReleaseDiscovery = () => "release",
                PlaceQuery = (_, _, _, _, _) => throw new OutOfMemoryException("controlled")
            });
            await Assert.ThrowsAsync<OutOfMemoryException>(() =>
                oomService.FindNearestPlaceWithDiagnosticsAsync(47, 8, "CH"));

            foreach (var failure in new Exception[]
                     {
                         new OperationCanceledException("foreign"),
                         new InvalidOperationException("ordinary")
                     })
            {
                var ordinaryService = CreatePlaces(root, new OverturePlacesTestHooks
                {
                    ReleaseDiscovery = () => "release",
                    PlaceQuery = (_, _, _, _, _) => throw failure
                });
                var result = await ordinaryService.FindNearestPlaceWithDiagnosticsAsync(47, 8, "CH");
                Assert.AreEqual(failure.Message, result.Error);
            }
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [TestMethod]
    public async Task InfrastructureLookup_ClassifiesActiveCancellationMemoryAndOrdinaryFailures()
    {
        var root = CreateTempDir();
        try
        {
            using var active = new CancellationTokenSource();
            var activeService = CreatePlaces(root, new OverturePlacesTestHooks
            {
                BundledInfrastructureQuery = (_, _, token) =>
                {
                    active.Cancel();
                    throw new OperationCanceledException(token);
                }
            });
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                activeService.FindNearestInfrastructureWithDiagnosticsAsync(47, 8, "CHE", active.Token));

            var oomService = CreatePlaces(root, new OverturePlacesTestHooks
            {
                BundledInfrastructureQuery = (_, _, _) => throw new OutOfMemoryException("controlled")
            });
            await Assert.ThrowsAsync<OutOfMemoryException>(() =>
                oomService.FindNearestInfrastructureWithDiagnosticsAsync(47, 8, "CHE"));

            foreach (var failure in new Exception[]
                     {
                         new OperationCanceledException("foreign"),
                         new InvalidOperationException("ordinary")
                     })
            {
                var ordinaryService = CreatePlaces(root, new OverturePlacesTestHooks
                {
                    BundledInfrastructureQuery = (_, _, _) => throw failure
                });
                var result = await ordinaryService.FindNearestInfrastructureWithDiagnosticsAsync(47, 8, "CHE");
                Assert.AreEqual(failure.Message, result.Error);
            }
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [TestMethod]
    public async Task DivisionLookup_ClassifiesActiveCancellationMemoryAndOrdinaryFailures()
    {
        var root = CreateTempDir();
        try
        {
            var places = CreatePlaces(root, new OverturePlacesTestHooks { ReleaseDiscovery = () => "release" });
            using var active = new CancellationTokenSource();
            var activeService = CreateDivisions(root, places, new OvertureDivisionsTestHooks
            {
                CachedDivisionQuery = (_, _, _, token) =>
                {
                    active.Cancel();
                    throw new OperationCanceledException(token);
                }
            });
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                activeService.FindContainingDivisionAreasAsync(47, 8, "CH", "CHE", active.Token));

            var oomService = CreateDivisions(root, places, new OvertureDivisionsTestHooks
            {
                CachedDivisionQuery = (_, _, _, _) => throw new OutOfMemoryException("controlled")
            });
            await Assert.ThrowsAsync<OutOfMemoryException>(() =>
                oomService.FindContainingDivisionAreasAsync(47, 8, "CH", "CHE"));

            foreach (var failure in new Exception[]
                     {
                         new OperationCanceledException("foreign"),
                         new InvalidOperationException("ordinary")
                     })
            {
                var ordinaryService = CreateDivisions(root, places, new OvertureDivisionsTestHooks
                {
                    CachedDivisionQuery = (_, _, _, _) => throw failure
                });
                var result = await ordinaryService.FindContainingDivisionAreasAsync(47, 8, "CH", "CHE");
                Assert.AreEqual(failure.Message, result.Error);
            }
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [TestMethod]
    public async Task ReleaseAndSourceMetadata_PropagateMemoryAndKeepOrdinaryFallbacks()
    {
        var root = CreateTempDir();
        try
        {
            var ordinaryPlaces = CreatePlaces(root, new OverturePlacesTestHooks
            {
                ReleaseDiscovery = () => throw new InvalidOperationException("ordinary")
            });
            Assert.AreEqual(
                OverturePlacesLogic.DocumentedFallbackRelease,
                await ordinaryPlaces.GetLatestReleaseForOvertureAsync());

            var oomPlaces = CreatePlaces(root, new OverturePlacesTestHooks
            {
                ReleaseDiscovery = () => throw new OutOfMemoryException("controlled")
            });
            await Assert.ThrowsAsync<OutOfMemoryException>(() => oomPlaces.GetLatestReleaseForOvertureAsync());

            Assert.AreEqual(
                OverturePlacesLogic.DocumentedFallbackRelease,
                OvertureDivisionCacheService.ResolveLatestRelease(() => throw new InvalidOperationException("ordinary")));
            Assert.Throws<OutOfMemoryException>(() =>
                OvertureDivisionCacheService.ResolveLatestRelease(() => throw new OutOfMemoryException("controlled")));

            Assert.AreEqual(0, OverturePlacesLogic.ParseSources("not json").Count);
            Assert.AreEqual(
                0,
                OverturePlacesLogic.ParseSources("[]", _ => throw new InvalidOperationException("ordinary")).Count);
            Assert.Throws<OutOfMemoryException>(() =>
                OverturePlacesLogic.ParseSources("[]", _ => throw new OutOfMemoryException("controlled")));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [TestMethod]
    public async Task CacheStatusReadinessValidationAndDeletion_PropagateMemoryButKeepOrdinaryFallbacks()
    {
        var root = CreateTempDir();
        var cacheDir = Path.Combine(root, "overture-divisions");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "CHE.db"), "placeholder");
        try
        {
            var ordinary = CreateCache(root, new OvertureDivisionCacheTestHooks
            {
                StatusReader = _ => throw new InvalidOperationException("ordinary"),
                HasRowsOperation = (_, _) => throw new InvalidOperationException("ordinary"),
                DeletionOperation = (_, _) => throw new InvalidOperationException("ordinary")
            });
            Assert.AreEqual(0L, ordinary.GetStatus()["CHE"].RowCount);
            Assert.IsFalse(ordinary.HasData("CHE"));
            ordinary.DeleteFile("CHE");

            var statusOom = CreateCache(root, new OvertureDivisionCacheTestHooks
            {
                StatusReader = _ => throw new OutOfMemoryException("controlled")
            });
            Assert.Throws<OutOfMemoryException>(() => statusOom.GetStatus());

            var readinessOom = CreateCache(root, new OvertureDivisionCacheTestHooks
            {
                HasRowsOperation = (_, _) => throw new OutOfMemoryException("controlled")
            });
            Assert.Throws<OutOfMemoryException>(() => readinessOom.HasData("CHE"));

            var deletionOom = CreateCache(root, new OvertureDivisionCacheTestHooks
            {
                DeletionOperation = (_, _) => throw new OutOfMemoryException("controlled")
            });
            Assert.Throws<OutOfMemoryException>(() => deletionOom.DeleteFile("CHE"));

            File.Delete(Path.Combine(cacheDir, "CHE.db"));
            var validationOrdinary = CreateCache(root, new OvertureDivisionCacheTestHooks
            {
                HasRowsOperation = (_, _) => false,
                ExportOperation = (path, _, _) =>
                {
                    File.WriteAllText(path, "temporary");
                    return 1;
                },
                ValidationOperation = _ => throw new InvalidOperationException("ordinary")
            });
            var (ordinaryTask, _) = validationOrdinary.GetOrStartDownload("CHE");
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await ordinaryTask);
            Assert.AreEqual(0, Directory.GetFiles(cacheDir, "CHE.*.tmp").Length);

            var validationOom = CreateCache(root, new OvertureDivisionCacheTestHooks
            {
                HasRowsOperation = (_, _) => false,
                ExportOperation = (path, _, _) =>
                {
                    File.WriteAllText(path, "temporary");
                    return 1;
                },
                ValidationOperation = _ => throw new OutOfMemoryException("controlled")
            });
            var (oomTask, _) = validationOom.GetOrStartDownload("CHE");
            await Assert.ThrowsAsync<OutOfMemoryException>(async () => await oomTask);
            Assert.AreEqual(0, Directory.GetFiles(cacheDir, "CHE.*.tmp").Length);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [TestMethod]
    public async Task MalformedCachedDivisionAndBundledInfrastructure_AreCandidateMisses()
    {
        var root = CreateTempDir();
        try
        {
            CreateInfrastructureDb(root, [1, 2, 3]);
            CreateDivisionDb(root, [1, 2, 3]);
            var places = CreatePlaces(root, new OverturePlacesTestHooks { ReleaseDiscovery = () => "release" });
            var infrastructure = await places.FindNearestInfrastructureWithDiagnosticsAsync(47, 8, "CHE");
            Assert.HasCount(1, infrastructure.Candidates);
            Assert.IsFalse(infrastructure.Candidates[0].GeometryContainsPoint);

            var divisions = CreateDivisions(root, places);
            var division = await divisions.FindContainingDivisionAreasAsync(47, 8, "CH", "CHE");
            Assert.HasCount(1, division.Candidates);
            Assert.IsFalse(division.Candidates[0].GeometryContainsPoint);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }


    [TestMethod]
    public async Task CachedDivisionTopologyException_IsCandidateLocalAndNextCandidateWins()
    {
        var root = CreateTempDir();
        try
        {
            CreateDivisionDb(root, ValidPolygonWkb());
            InsertDivisionCandidate(root, "second", ValidPolygonWkb());
            var calls = 0;
            var places = CreatePlaces(root, new OverturePlacesTestHooks { ReleaseDiscovery = () => "release" });
            var divisions = CreateDivisions(root, places, new OvertureDivisionsTestHooks
            {
                GeometryContains = (_, _) => ++calls == 1
                    ? throw new TopologyException("controlled topology")
                    : true
            });

            var result = await divisions.FindContainingDivisionAreasAsync(47, 8, "CH", "CHE");

            Assert.HasCount(2, result.Candidates);
            Assert.IsFalse(result.Candidates[0].GeometryContainsPoint);
            Assert.IsTrue(result.Candidates[1].GeometryContainsPoint);
            Assert.IsNotNull(result.BestMatch);
            Assert.AreEqual("second", result.BestMatch.Id);
            Assert.AreEqual(2, calls);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }
    [TestMethod]
    public async Task CachedDivision_TopologyFallbackStillObservesCancellationBeforeCandidateContinuation()
    {
        var root = CreateTempDir();
        using var cancellation = new CancellationTokenSource();
        try
        {
            CreateDivisionDb(root, ValidPolygonWkb());
            InsertDivisionCandidate(root, "second", ValidPolygonWkb());
            var calls = 0;
            var places = CreatePlaces(root, new OverturePlacesTestHooks { ReleaseDiscovery = () => "release" });
            var divisions = CreateDivisions(root, places, new OvertureDivisionsTestHooks
            {
                GeometryContains = (_, _) =>
                {
                    calls++;
                    cancellation.Cancel();
                    throw new TopologyException("controlled topology");
                }
            });

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                divisions.FindContainingDivisionAreasAsync(47, 8, "CH", "CHE", cancellation.Token));
            Assert.AreEqual(1, calls);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [TestMethod]
    public async Task CachedDivision_RealHasColumnProbesObserveCancellationBeforeAndAfterSchemaRowRead()
    {
        var root = CreateTempDir();
        try
        {
            CreateDivisionDb(root, ValidPolygonWkb());
            var places = CreatePlaces(root, new OverturePlacesTestHooks { ReleaseDiscovery = () => "release" });

            using (var beforeReadCancellation = new CancellationTokenSource())
            {
                var afterReads = 0;
                var beforeRead = CreateDivisions(root, places, new OvertureDivisionsTestHooks
                {
                    HasColumnCheckpoint = checkpoint =>
                    {
                        if (checkpoint == OvertureHasColumnCheckpoint.BeforeRowRead)
                        {
                            beforeReadCancellation.Cancel();
                        }

                        if (checkpoint == OvertureHasColumnCheckpoint.AfterRowRead)
                        {
                            afterReads++;
                        }
                    }
                });

                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    beforeRead.FindContainingDivisionAreasAsync(47, 8, "CH", "CHE", beforeReadCancellation.Token));
                Assert.AreEqual(0, afterReads);
            }

            using (var afterReadCancellation = new CancellationTokenSource())
            {
                var beforeReads = 0;
                var afterReads = 0;
                var afterRead = CreateDivisions(root, places, new OvertureDivisionsTestHooks
                {
                    HasColumnCheckpoint = checkpoint =>
                    {
                        if (checkpoint == OvertureHasColumnCheckpoint.BeforeRowRead)
                        {
                            beforeReads++;
                        }

                        if (checkpoint == OvertureHasColumnCheckpoint.AfterRowRead)
                        {
                            afterReads++;
                            afterReadCancellation.Cancel();
                        }
                    }
                });

                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    afterRead.FindContainingDivisionAreasAsync(47, 8, "CH", "CHE", afterReadCancellation.Token));
                Assert.AreEqual(1, beforeReads);
                Assert.AreEqual(1, afterReads);
            }
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [TestMethod]
    public void CandidateGeometry_PropagatesMemoryAndContainsOnlyRecognizedMalformedData()
    {
        var point = NtsGeometryServices.Instance
            .CreateGeometryFactory(srid: 4326)
            .CreatePoint(new Coordinate(8, 47));

        Assert.IsFalse(OvertureDataAccess.TryGeometryContains([1, 2, 3], point));
        Assert.Throws<OutOfMemoryException>(() =>
            OvertureDataAccess.TryGeometryContains(
                ValidPolygonWkb(),
                point,
                (_, _) => throw new OutOfMemoryException("controlled")));
        Assert.Throws<InvalidOperationException>(() =>
            OvertureDataAccess.TryGeometryContains(
                ValidPolygonWkb(),
                point,
                (_, _) => throw new InvalidOperationException("not malformed data")));

        var candidates = 0;
        Assert.IsFalse(OvertureDataAccess.TryGeometryContains(
            ValidPolygonWkb(),
            point,
            (_, _) => ++candidates == 1
                ? throw new TopologyException("controlled first candidate")
                : true));
        Assert.IsTrue(OvertureDataAccess.TryGeometryContains(
            ValidPolygonWkb(),
            point,
            (_, _) => ++candidates == 2));
        Assert.AreEqual(2, candidates);
    }

    [TestMethod]
    public async Task MalformedBundledCountryGeometry_FailsArtifactLoading()
    {
        var root = CreateTempDir();
        try
        {
            CreateCountryDb(root, [1, 2, 3]);
            var places = CreatePlaces(root);
            var divisions = CreateDivisions(root, places);

            await Assert.ThrowsAsync<ParseException>(() => divisions.FindBundledCountryAsync(47, 8));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    private static OverturePlacesService CreatePlaces(string root, OverturePlacesTestHooks? hooks = null) =>
        hooks is null
            ? new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root)
            : new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root, hooks);

    private static OvertureDivisionsService CreateDivisions(
        string root,
        OverturePlacesService places,
        OvertureDivisionsTestHooks? hooks = null) =>
        hooks is null
            ? new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, _ => "CHE")
            : new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, _ => "CHE", hooks);

    private static OvertureDivisionCacheService CreateCache(string root, OvertureDivisionCacheTestHooks hooks) =>
        new(NullLogger<OvertureDivisionCacheService>.Instance, root, _ => "CH", hooks);

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempDir(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] ValidPolygonWkb()
    {
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var polygon = factory.CreatePolygon(
        [
            new Coordinate(7, 46),
            new Coordinate(9, 46),
            new Coordinate(9, 48),
            new Coordinate(7, 48),
            new Coordinate(7, 46)
        ]);
        return new WKBWriter().Write(polygon);
    }

    private static void CreateInfrastructureDb(string root, byte[] geometry)
    {
        var path = Path.Combine(root, "defaults", "overture-airports.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE infrastructure (
                id TEXT, name TEXT, feature_type TEXT, subtype TEXT, class_name TEXT,
                latitude REAL, longitude REAL, geom_wkb BLOB,
                bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO _meta VALUES ('release', 'test-release');
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO infrastructure VALUES (
                'airport', 'Test Airport', 'infrastructure', 'airport', 'airport',
                47, 8, $geometry, 7, 46, 9, 48);
            """;
        command.Parameters.AddWithValue("$geometry", geometry);
        command.ExecuteNonQuery();
    }

    private static void CreateDivisionDb(string root, byte[] geometry)
    {
        var path = Path.Combine(root, "overture-divisions", "CHE.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE division_area (
                id TEXT, name TEXT, subtype TEXT, class_name TEXT, admin_level INTEGER,
                country TEXT, is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO _meta VALUES ('release', 'test-release');
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO division_area VALUES (
                'division', 'Test Division', 'region', 'land', 4, 'CH', 1, 0,
                $geometry, 7, 46, 9, 48);
            """;
        command.Parameters.AddWithValue("$geometry", geometry);
        command.ExecuteNonQuery();
    }

    private static void InsertDivisionCandidate(string root, string id, byte[] geometry)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "overture-divisions", "CHE.db")};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO division_area VALUES ($id, 'Second Division', 'region', 'land', 3, 'CH', 1, 0, $geometry, 7, 46, 9, 48);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$geometry", geometry);
        command.ExecuteNonQuery();
    }

    private static void CreateCountryDb(string root, byte[] geometry)
    {
        var path = Path.Combine(root, "defaults", "overture-country-divisions.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE division_area (
                id TEXT, name TEXT, subtype TEXT, class_name TEXT, admin_level INTEGER,
                country TEXT, alpha3 TEXT, is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO division_area VALUES (
                'country', 'Switzerland', 'country', 'land', 2, 'CH', 'CHE', 1, 0,
                $geometry, 7, 46, 9, 48);
            """;
        command.Parameters.AddWithValue("$geometry", geometry);
        command.ExecuteNonQuery();
    }
}
