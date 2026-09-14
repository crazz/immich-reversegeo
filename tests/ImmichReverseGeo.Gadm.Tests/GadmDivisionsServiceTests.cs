using ImmichReverseGeo.Gadm.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Gadm.Tests;

[TestClass]
public class GadmDivisionsServiceTests
{
    [TestMethod]
    public async Task FindContainingDivisionAreasAsync_PreCancelledTokenDoesNotReturnMissingCacheDiagnostic()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var service = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, CreateTempDir());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.FindContainingDivisionAreasAsync(47, 8, "CHE", cancelled.Token));
    }

    [TestMethod]
    public async Task FindContainingDivisionAreasAsync_ActiveCancellationAndOomEscapeButOrdinaryFaultIsDiagnostic()
    {
        var tempDir = CreateTempDir();
        try
        {
            CreateLookupDb(tempDir, "CHE", [1]);
            using var cancellation = new CancellationTokenSource();
            var cancelled = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir,
                (_, _, _, ct) => throw new OperationCanceledException(ct));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                cancelled.FindContainingDivisionAreasAsync(47, 8, "CHE", cancellation.Token));

            var oom = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir,
                (_, _, _, _) => throw new OutOfMemoryException("controlled"));
            await Assert.ThrowsAsync<OutOfMemoryException>(() => oom.FindContainingDivisionAreasAsync(47, 8, "CHE"));

            var ordinary = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir,
                (_, _, _, _) => throw new InvalidOperationException("controlled"));
            var diagnostic = await ordinary.FindContainingDivisionAreasAsync(47, 8, "CHE");
            Assert.AreEqual("controlled", diagnostic.Error);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task FindContainingDivisionAreasAsync_MultiCachePreservesOrdinaryFaultButPropagatesOom()
    {
        var tempDir = CreateTempDir();
        try
        {
            CreateLookupDb(tempDir, "CHE", [1]);
            var ordinary = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir,
                (_, _, _, _) => throw new InvalidOperationException("controlled multi"));
            var diagnostic = await ordinary.FindContainingDivisionAreasAsync(47, 8, new[] { "CHE" });
            Assert.AreEqual("controlled multi", diagnostic.Error);

            var oom = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir,
                (_, _, _, _) => throw new OutOfMemoryException("controlled"));
            await Assert.ThrowsAsync<OutOfMemoryException>(() =>
                oom.FindContainingDivisionAreasAsync(47, 8, new[] { "CHE" }));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task FindContainingDivisionAreasAsync_CancellationBeforeNativeReaderReadIsObserved()
    {
        var tempDir = CreateTempDir();
        using var cancellation = new CancellationTokenSource();
        try
        {
            CreateLookupDb(tempDir, "CHE", [1]);
            var afterReads = 0;
            var service = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir,
                checkpoint =>
                {
                    if (checkpoint == GadmDivisionsService.GadmLookupCheckpoint.BeforeCandidateRowRead)
                    {
                        cancellation.Cancel();
                    }

                    if (checkpoint == GadmDivisionsService.GadmLookupCheckpoint.AfterCandidateRowRead)
                    {
                        afterReads++;
                    }
                });

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.FindContainingDivisionAreasAsync(47, 8, "CHE", cancellation.Token));
            Assert.AreEqual(0, afterReads);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task FindContainingDivisionAreasAsync_CancellationAfterMetadataScalarIsObserved()
    {
        var tempDir = CreateTempDir();
        using var cancellation = new CancellationTokenSource();
        try
        {
            CreateLookupDb(tempDir, "CHE", ValidPolygonWkb());
            var service = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir,
                checkpoint =>
                {
                    if (checkpoint == GadmDivisionsService.GadmLookupCheckpoint.AfterMetadataScalar)
                    {
                        cancellation.Cancel();
                    }
                });

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.FindContainingDivisionAreasAsync(47, 8, "CHE", cancellation.Token));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task FindContainingDivisionAreasAsync_MalformedWkbRetainsBoundingBoxFallbackAndRanking()
    {
        var tempDir = CreateTempDir();
        try
        {
            CreateLookupDb(tempDir, "CHE", [1]);
            var service = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, tempDir);

            var result = await service.FindContainingDivisionAreasAsync(47, 8, "CHE");

            Assert.AreEqual(1, result.Candidates.Count);
            Assert.IsTrue(result.Candidates[0].BoundingBoxContainsPoint);
            Assert.IsFalse(result.Candidates[0].GeometryContainsPoint);
            Assert.IsNotNull(result.BestMatch);
            Assert.AreEqual("area", result.BestMatch.Id);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task FindContainingDivisionAreasAsync_TopologyExceptionIsCandidateLocalAndRetainsBoundingBoxFallback()
    {
        var tempDir = CreateTempDir();
        try
        {
            CreateLookupDb(tempDir, "CHE", [1]);
            InsertLookupCandidate(tempDir, "CHE", "valid-area", "Valid Area", [2]);
            var geometryCalls = 0;
            var service = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir,
                (_, _) => ++geometryCalls == 1
                    ? throw new TopologyException("controlled topology")
                    : true);

            var result = await service.FindContainingDivisionAreasAsync(47, 8, "CHE");

            Assert.AreEqual(2, result.Candidates.Count);
            Assert.IsTrue(result.Candidates[0].BoundingBoxContainsPoint);
            Assert.IsFalse(result.Candidates[0].GeometryContainsPoint);
            Assert.IsTrue(result.Candidates[1].GeometryContainsPoint);
            Assert.IsNotNull(result.BestMatch);
            Assert.AreEqual("valid-area", result.BestMatch.Id);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task FindContainingDivisionAreasAsync_GeometryOomEscapesAndPostGeometryCancellationIsObserved()
    {
        var tempDir = CreateTempDir();
        try
        {
            CreateLookupDb(tempDir, "CHE", [1]);
            var oom = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir,
                (_, _) => throw new OutOfMemoryException("controlled geometry"));
            await Assert.ThrowsAsync<OutOfMemoryException>(() => oom.FindContainingDivisionAreasAsync(47, 8, "CHE"));

            using var cancellation = new CancellationTokenSource();
            var cancellationService = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir,
                (_, _) =>
                {
                    cancellation.Cancel();
                    return false;
                });
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                cancellationService.FindContainingDivisionAreasAsync(47, 8, "CHE", cancellation.Token));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }


    [TestMethod]
    public async Task ResolveAdministrativeGeoAsync_CancellationAfterStateSelectionIsObserved()
    {
        var tempDir = CreateTempDir();
        using var cancellation = new CancellationTokenSource();
        try
        {
            CreateLookupDb(tempDir, "CHE", ValidPolygonWkb());
            var service = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir,
                checkpoint =>
                {
                    if (checkpoint == GadmDivisionsService.GadmLookupCheckpoint.StateSelected)
                    {
                        cancellation.Cancel();
                    }
                });

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.ResolveAdministrativeGeoAsync(47, 8, "CHE", cancellation.Token));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task ResolveAdministrativeGeoAsync_CancellationAfterCitySelectionIsObserved()
    {
        var tempDir = CreateTempDir();
        using var cancellation = new CancellationTokenSource();
        try
        {
            CreateLookupDb(tempDir, "CHE", ValidPolygonWkb());
            var service = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir,
                checkpoint =>
                {
                    if (checkpoint == GadmDivisionsService.GadmLookupCheckpoint.CitySelected)
                    {
                        cancellation.Cancel();
                    }
                });

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.ResolveAdministrativeGeoAsync(47, 8, "CHE", cancellation.Token));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    private static byte[] ValidPolygonWkb()
    {
        var polygon = new GeometryFactory().CreatePolygon(
        [
            new Coordinate(7, 46),
            new Coordinate(9, 46),
            new Coordinate(9, 48),
            new Coordinate(7, 48),
            new Coordinate(7, 46)
        ]);
        return new WKBWriter().Write(polygon);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateLookupDb(string tempDir, string iso3, byte[] wkb)
    {
        var dir = Path.Combine(tempDir, "gadm-divisions");
        Directory.CreateDirectory(dir);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(dir, iso3 + ".db")};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE gadm_area (
                id TEXT NOT NULL, name TEXT NOT NULL, english_type TEXT NULL, local_type TEXT NULL,
                admin_level INTEGER NOT NULL, geom_wkb BLOB NOT NULL,
                bbox_xmin REAL NOT NULL, bbox_ymin REAL NOT NULL, bbox_xmax REAL NOT NULL, bbox_ymax REAL NOT NULL);
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO _meta VALUES ('version', 'test');
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO gadm_area VALUES ('area', 'Area', NULL, NULL, 2, $wkb, 7, 46, 9, 48);
            """;
        command.Parameters.AddWithValue("$wkb", wkb);
        command.ExecuteNonQuery();
    }

    private static void InsertLookupCandidate(string tempDir, string iso3, string id, string name, byte[] wkb)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(tempDir, "gadm-divisions", iso3 + ".db")};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO gadm_area VALUES ($id, $name, NULL, NULL, 2, $wkb, 7, 46, 9, 48);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$wkb", wkb);
        command.ExecuteNonQuery();
    }

    private static void DeleteTempDir(string path)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
