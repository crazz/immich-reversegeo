using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class OvertureDivisionCacheServiceTests
{
    [TestMethod]
    public void GetStatus_WithValidDb_ReturnsRowCountAndRelease()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var dbDir = Path.Combine(tempDir, "overture-divisions");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "CHE.db");

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE division_area (id TEXT PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO division_area VALUES ('1', 'Zurich');
                INSERT INTO _meta VALUES ('downloadedAt', '2026-03-27T12:00:00Z');
                INSERT INTO _meta VALUES ('release', '2026-03-18.0');
                ";
            cmd.ExecuteNonQuery();
        }

        try
        {
            var svc = new OvertureDivisionCacheService(
                NullLogger<OvertureDivisionCacheService>.Instance,
                tempDir,
                _ => "CH");
            var status = svc.GetStatus();

            Assert.IsTrue(status.ContainsKey("CHE"));
            Assert.AreEqual(1L, status["CHE"].RowCount);
            Assert.AreEqual("2026-03-18.0", status["CHE"].Release);
            Assert.IsNotNull(status["CHE"].DownloadedAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetOrStartDownload_PostOpenFailuresCleanupAndRetryPublishesCache()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var dbDir = Path.Combine(tempDir, "overture-divisions");
        var dbPath = Path.Combine(dbDir, "CHE.db");
        var exporter = new ControlledExporter();
        var svc = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            tempDir,
            _ => "CH",
            exporter.Export);

        try
        {
            for (var i = 0; i < 2; i++)
            {
                var (downloadTask, result) = svc.GetOrStartDownload("CHE");

                Assert.AreEqual(OvertureDivisionEnsureResult.StartedDownload, result);
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await downloadTask);
                Assert.IsTrue(downloadTask.IsFaulted);
                Assert.IsFalse(File.Exists(dbPath));
                Assert.AreEqual(0, Directory.GetFiles(dbDir, "CHE.*.tmp").Length);
            }

            Assert.AreEqual(2, exporter.OpenedOutputs.Count);
            Assert.AreEqual(2, exporter.OpenedOutputs.Select(output => output.Path).Distinct().Count());
            Assert.IsTrue(exporter.OpenedOutputs.All(output => !output.Pooling));

            exporter.ThrowAfterOpen = false;
            var (retryTask, retryResult) = svc.GetOrStartDownload("CHE");

            Assert.AreEqual(OvertureDivisionEnsureResult.StartedDownload, retryResult);
            await retryTask;

            Assert.AreEqual(3, exporter.OpenedOutputs.Count);
            Assert.IsFalse(exporter.OpenedOutputs[2].Pooling);
            Assert.IsFalse(exporter.OpenedOutputs.Take(2).Any(output => output.Path == exporter.OpenedOutputs[2].Path));
            Assert.IsTrue(File.Exists(dbPath));
            Assert.IsTrue(svc.HasData("CHE"));
            Assert.AreEqual(0, Directory.GetFiles(dbDir, "CHE.*.tmp").Length);

            var status = svc.GetStatus();
            Assert.AreEqual(1L, status["CHE"].RowCount);
            Assert.AreEqual("test-release", status["CHE"].Release);
            Assert.IsNotNull(status["CHE"].DownloadedAt);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void DeleteFile_RemovesDbAndTempFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var dbDir = Path.Combine(tempDir, "overture-divisions");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "CHE.db");
        var tmpPath = Path.Combine(dbDir, "CHE.abc.tmp");
        File.WriteAllText(dbPath, "db");
        File.WriteAllText(tmpPath, "tmp");

        try
        {
            var svc = new OvertureDivisionCacheService(
                NullLogger<OvertureDivisionCacheService>.Instance,
                tempDir,
                _ => "CH");
            svc.DeleteFile("CHE");

            Assert.IsFalse(File.Exists(dbPath));
            Assert.IsFalse(File.Exists(tmpPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class ControlledExporter
    {
        public List<(string Path, bool Pooling)> OpenedOutputs { get; } = [];

        public bool ThrowAfterOpen { get; set; } = true;

        public long Export(string tmpPath, string alpha2)
        {
            using var sqlite = OvertureDivisionCacheService.OpenTemporaryOutputConnection(tmpPath);
            OpenedOutputs.Add((
                tmpPath,
                new SqliteConnectionStringBuilder(sqlite.ConnectionString).Pooling));

            if (ThrowAfterOpen)
            {
                throw new InvalidOperationException("Controlled post-open export failure.");
            }

            using var cmd = sqlite.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE division_area (id TEXT PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO division_area VALUES ('test-id', 'Test division');
                INSERT INTO _meta VALUES ('downloadedAt', '2026-03-27T12:00:00Z');
                INSERT INTO _meta VALUES ('release', 'test-release');
                ";
            cmd.ExecuteNonQuery();
            return 1;
        }
    }
}
