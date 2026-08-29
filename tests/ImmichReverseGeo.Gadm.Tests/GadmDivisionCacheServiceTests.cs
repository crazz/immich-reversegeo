using ImmichReverseGeo.Gadm.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Gadm.Tests;

[TestClass]
public class GadmDivisionCacheServiceTests
{
    [TestMethod]
    public void GetStatus_ReadsCachedDbMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var dbDir = Path.Combine(tempDir, "gadm-divisions");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "CHE.db");

        try
        {
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE gadm_area (id TEXT PRIMARY KEY);
                    CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO gadm_area (id) VALUES ('row-1');
                    INSERT INTO gadm_area (id) VALUES ('row-2');
                    INSERT INTO _meta (key, value) VALUES ('downloadedAt', '2026-04-05T12:34:56Z');
                    INSERT INTO _meta (key, value) VALUES ('version', '4.1');
                    """;
                cmd.ExecuteNonQuery();
            }

            var svc = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir);

            var status = svc.GetStatus();

            Assert.AreEqual(1, status.Count);
            Assert.IsTrue(status.ContainsKey("CHE"));
            Assert.AreEqual(2, status["CHE"].RowCount);
            Assert.AreEqual("4.1", status["CHE"].Version);
            Assert.IsTrue(status["CHE"].FileSizeBytes > 0);
            Assert.AreEqual(DateTime.Parse("2026-04-05T12:34:56Z", null, System.Globalization.DateTimeStyles.RoundtripKind), status["CHE"].DownloadedAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
        var dbDir = Path.Combine(tempDir, "gadm-divisions");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "CHE.db");
        var tmpPath = Path.Combine(dbDir, "CHE.abc.tmp");
        File.WriteAllText(dbPath, "db");
        File.WriteAllText(tmpPath, "tmp");

        try
        {
            var svc = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir);
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
    [TestMethod]
    public async Task GetOrStartDownload_PreflightFailureIsRemovedAndRetryStartsNewTask()
    {
        var tempDir = CreateTempDir(); var source = new ControlledSource(tempDir); var svc = new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, tempDir, source.RunAsync);
        try
        {
            source.Fault = true;
            var (failed, started) = svc.GetOrStartDownload("CHE");
            Assert.AreEqual(GadmDivisionEnsureResult.StartedDownload, started);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await failed);
            Assert.IsFalse(File.Exists(Path.Combine(tempDir, "gadm-divisions", "CHE.db")));
            source.Fault = false;
            var (retry, result) = svc.GetOrStartDownload("CHE");
            Assert.AreEqual(GadmDivisionEnsureResult.StartedDownload, result);
            Assert.AreNotSame(failed, retry);
            source.Release(); await retry;
            Assert.AreEqual(2, source.InvocationCount);
        }
        finally { DeleteTempDir(tempDir); }
    }

    [TestMethod]
    public async Task GetOrStartDownload_OwnerCancellationRetriesButWaiterCancellationKeepsTaskJoinable()
    {
        var tempDir = CreateTempDir(); var source = new ControlledSource(tempDir); var svc = new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, tempDir, source.RunAsync);
        try
        {
            using var owner = new CancellationTokenSource(); source.CancelWithOwnerToken = true;
            var (cancelled, _) = svc.GetOrStartDownload("CHE", owner.Token); Assert.AreEqual(owner.Token, source.OwnerToken); owner.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(async () => await cancelled);
            source.CancelWithOwnerToken = false;
            var (retry, _) = svc.GetOrStartDownload("CHE"); source.Release(); await retry; svc.DeleteFile("CHE");
            source.ResetGate();
            var (active, _) = svc.GetOrStartDownload("CHE"); await source.Entered.Task;
            using var waiter = new CancellationTokenSource();
            var waiterEnsure = svc.EnsureDataAsync("CHE", waiter.Token);
            waiter.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiterEnsure);
            var (joined, result) = svc.GetOrStartDownload("CHE");
            Assert.AreSame(active, joined); Assert.AreEqual(GadmDivisionEnsureResult.AwaitedExistingDownload, result);
            source.Release(); await joined;
        }
        finally { DeleteTempDir(tempDir); }
    }

    [TestMethod]
    public async Task GetOrStartDownload_ConcurrentCallersShareOneTaskAndReadyCacheSkipsSource()
    {
        var tempDir = CreateTempDir(); var source = new ControlledSource(tempDir); var svc = new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, tempDir, source.RunAsync);
        try
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callers = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return svc.GetOrStartDownload("CHE");
            })).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(callers);
            await source.Entered.Task;
            var starter = results.Single(x => x.Result == GadmDivisionEnsureResult.StartedDownload);
            Assert.AreEqual(4, results.Count(x => x.Result == GadmDivisionEnsureResult.AwaitedExistingDownload));
            Assert.IsTrue(results.All(x => ReferenceEquals(starter.Task, x.Task)));
            Assert.AreEqual(1, source.InvocationCount); source.Release();
            await Task.WhenAll(results.Select(x => x.Task));
            Assert.IsTrue(svc.HasData("CHE"));
            var ready = svc.GetOrStartDownload("CHE"); Assert.AreEqual(GadmDivisionEnsureResult.AlreadyReady, ready.Result); Assert.AreEqual(1, source.InvocationCount);
            svc.DeleteFile("CHE"); source.ResetGate();
            var (afterDeletion, afterDeletionResult) = svc.GetOrStartDownload("CHE");
            Assert.AreEqual(GadmDivisionEnsureResult.StartedDownload, afterDeletionResult); Assert.AreNotSame(starter.Task, afterDeletion);
            source.Release(); await afterDeletion; Assert.AreEqual(2, source.InvocationCount);
        }
        finally { DeleteTempDir(tempDir); }
    }


    [TestMethod]
    public void GetOrStartDownload_PreexistingValidCacheIsAlreadyReadyWithoutSourceWork()
    {
        var tempDir = CreateTempDir(); var source = new ControlledSource(tempDir);
        try
        {
            source.Publish("CHE");
            var svc = new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, tempDir, source.RunAsync);
            var before = svc.GetStatus()["CHE"];
            var (task, result) = svc.GetOrStartDownload("CHE");
            var after = svc.GetStatus()["CHE"];
            Assert.AreEqual(GadmDivisionEnsureResult.AlreadyReady, result); Assert.IsTrue(task.IsCompletedSuccessfully);
            Assert.AreEqual(0, source.InvocationCount); Assert.AreEqual(before, after);
        }
        finally { DeleteTempDir(tempDir); }
    }

    [TestMethod]
    public async Task DownloadDataInternal_PostArtifactFaultAndCancellationCleanTemporaryArtifacts()
    {
        var tempDir = CreateTempDir(); var directory = Path.Combine(tempDir, "gadm-divisions");
        try
        {
            foreach (var cancelled in new[] { false, true })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var svc = new GadmDivisionCacheService(
                    NullLogger<GadmDivisionCacheService>.Instance,
                    tempDir,
                    async (_, downloadPath, ct) =>
                    {
                        Directory.CreateDirectory(directory);
                        await File.WriteAllTextAsync(downloadPath, "package", ct);
                        entered.TrySetResult();
                        if (cancelled) { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    },
                    (_, tmpDbPath, _) =>
                    {
                        File.WriteAllText(tmpDbPath, "partial sqlite");
                        throw new InvalidOperationException("controlled export failure");
                    });
                using var owner = new CancellationTokenSource();
                var (task, result) = svc.GetOrStartDownload("CHE", owner.Token);
                Assert.AreEqual(GadmDivisionEnsureResult.StartedDownload, result);
                await entered.Task;
                if (cancelled)
                {
                    owner.Cancel();
                    await Assert.ThrowsAsync<TaskCanceledException>(async () => await task);
                }
                else
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
                }
                Assert.IsFalse(File.Exists(Path.Combine(directory, "CHE.db")));
                Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.tmp").Length);
                Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.gpkg.download").Length);
            }
        }
        finally { DeleteTempDir(tempDir); }
    }

    [TestMethod]
    public void RemoveExact_DoesNotRemoveReplacementLazy()
    {
        var map = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task>>();
        var oldValue = new Lazy<Task>(() => Task.CompletedTask); var replacement = new Lazy<Task>(() => Task.CompletedTask);
        map["CHE"] = oldValue; map["CHE"] = replacement;
        Assert.IsFalse(GadmDivisionCacheService.RemoveExact(map, "CHE", oldValue)); Assert.AreSame(replacement, map["CHE"]);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }

    private static void DeleteTempDir(string path)
    {
        SqliteConnection.ClearAllPools(); if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
    }

    private sealed class ControlledSource
    {
        private readonly string _tempDir; private TaskCompletionSource _release = NewGate();
        public ControlledSource(string tempDir) { _tempDir = tempDir; }
        public int InvocationCount { get; private set; }
        public bool Fault { get; set; }
        public bool CancelWithOwnerToken { get; set; }
        public CancellationToken OwnerToken { get; private set; }
        public TaskCompletionSource Entered { get; private set; } = NewGate();
        public async Task RunAsync(string iso3, CancellationToken ct)
        {
            InvocationCount++; OwnerToken = ct; Entered.TrySetResult();
            if (Fault) { throw new InvalidOperationException("controlled preflight failure"); }
            if (CancelWithOwnerToken) { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            await _release.Task;
            Publish(iso3);
        }
        public void Publish(string iso3)
        {
            var directory = Path.Combine(_tempDir, "gadm-divisions"); Directory.CreateDirectory(directory);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, iso3 + ".db")};Pooling=false"); connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE gadm_area (id TEXT PRIMARY KEY); CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO gadm_area VALUES ('row'); INSERT INTO _meta VALUES ('downloadedAt', '2026-01-01T00:00:00Z'); INSERT INTO _meta VALUES ('version', 'test-version');"; command.ExecuteNonQuery();
        }
        public void Release() => _release.TrySetResult();
        public void ResetGate() { _release = NewGate(); Entered = NewGate(); }
        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

}
