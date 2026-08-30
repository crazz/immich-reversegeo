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

    [TestMethod]
    public async Task GetOrStartDownload_PreflightFailureIsRemovedAndRetryStartsNewTask()
    {
        var tempDir = CreateTempDir();
        var source = new ControlledSource(tempDir, "overture-divisions", "division_area", "release", "test-release");
        var svc = new OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, tempDir, _ => "CH", (_, _) => 0, source.RunAsync);
        try
        {
            source.Fault = true;
            var (failed, started) = svc.GetOrStartDownload("CHE");
            Assert.AreEqual(OvertureDivisionEnsureResult.StartedDownload, started);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await failed);
            Assert.IsFalse(File.Exists(Path.Combine(tempDir, "overture-divisions", "CHE.db")));
            source.Fault = false;
            var (retry, result) = svc.GetOrStartDownload("CHE");
            Assert.AreEqual(OvertureDivisionEnsureResult.StartedDownload, result);
            Assert.AreNotSame(failed, retry);
            source.Release();
            await retry;
            Assert.AreEqual(2, source.InvocationCount);
        }
        finally { DeleteTempDir(tempDir); }
    }

    [TestMethod]
    public async Task GetOrStartDownload_OwnerCancellationRetriesButWaiterCancellationKeepsTaskJoinable()
    {
        var tempDir = CreateTempDir();
        var source = new ControlledSource(tempDir, "overture-divisions", "division_area", "release", "test-release");
        var svc = new OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, tempDir, _ => "CH", (_, _) => 0, source.RunAsync);
        try
        {
            using var owner = new CancellationTokenSource();
            source.CancelWithOwnerToken = true;
            var (cancelled, _) = svc.GetOrStartDownload("CHE", owner.Token);
            Assert.AreEqual(owner.Token, source.OwnerToken);
            owner.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(async () => await cancelled);
            source.CancelWithOwnerToken = false;
            var (retry, _) = svc.GetOrStartDownload("CHE");
            source.Release();
            await retry;
            svc.DeleteFile("CHE");

            source.ResetGate();
            var (active, _) = svc.GetOrStartDownload("CHE");
            await source.Entered.Task;
            using var waiter = new CancellationTokenSource();
            var waiterEnsure = svc.EnsureDataAsync("CHE", waiter.Token);
            waiter.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiterEnsure);
            var (joined, joinedResult) = svc.GetOrStartDownload("CHE");
            Assert.AreSame(active, joined);
            Assert.AreEqual(OvertureDivisionEnsureResult.AwaitedExistingDownload, joinedResult);
            source.Release();
            await joined;
        }
        finally { DeleteTempDir(tempDir); }
    }

    [TestMethod]
    public async Task EnsureDataAsync_ForeignOwnerCancellationAfterRemovalUsesCapturedTaskAndDoesNotJoinRetry()
    {
        var tempDir = CreateTempDir();
        var source = new ControlledSource(tempDir, "overture-divisions", "division_area", "release", "test-release")
        {
            CancelWithOwnerToken = true
        };
        var service = new OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, tempDir, _ => "CH", (_, _) => 0, source.RunAsync);
        try
        {
            using var owner = new CancellationTokenSource();
            var (ownerTask, _) = service.GetOrStartDownload("CHE", owner.Token);
            await source.Entered.Task;
            var waiter = service.EnsureDataAsync("CHE");
            owner.Cancel();

            // Awaiting owner completion guarantees exact-value cleanup removed its map entry.
            await Assert.ThrowsAsync<TaskCanceledException>(async () => await ownerTask);
            source.CancelWithOwnerToken = false;
            source.ResetGate();
            var (retry, retryResult) = service.GetOrStartDownload("CHE");
            Assert.AreEqual(OvertureDivisionEnsureResult.StartedDownload, retryResult);

            // The live waiter must normalize the cancelled task it captured, not look up and join retry.
            var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(async () => await waiter);
            Assert.IsInstanceOfType<OperationCanceledException>(unavailable.InnerException);
            Assert.AreEqual(2, source.InvocationCount);

            source.Release();
            await retry;
            Assert.AreEqual(2, source.InvocationCount);
        }
        finally { DeleteTempDir(tempDir); }
    }

    [TestMethod]
    public async Task GetOrStartDownload_CancellationAfterExportDoesNotPublishCache()
    {
        var tempDir = CreateTempDir();
        var enteredPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exporter = new ControlledExporter { ThrowAfterOpen = false };
        var dbPath = Path.Combine(tempDir, "overture-divisions", "CHE.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        exporter.Export(dbPath, "CH");
        var publishedBytes = File.ReadAllBytes(dbPath);
        var publishedStatus = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            tempDir,
            _ => "CH").GetStatus()["CHE"];
        var service = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            tempDir,
            _ => "CH",
            new OvertureDivisionCacheTestHooks
            {
                HasRowsOperation = (_, _) => false,
                ExportOperation = (path, alpha2, _) => exporter.Export(path, alpha2),
                BeforePublication = async _ =>
                {
                    enteredPublication.TrySetResult();
                    await releasePublication.Task;
                }
            });
        try
        {
            using var cancellation = new CancellationTokenSource();
            var (task, _) = service.GetOrStartDownload("CHE", cancellation.Token);
            await enteredPublication.Task;
            cancellation.Cancel();
            releasePublication.TrySetResult();

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
            CollectionAssert.AreEqual(publishedBytes, File.ReadAllBytes(dbPath));
            var verifier = new OvertureDivisionCacheService(
                NullLogger<OvertureDivisionCacheService>.Instance,
                tempDir,
                _ => "CH");
            Assert.IsTrue(verifier.HasData("CHE"));
            Assert.AreEqual(publishedStatus, verifier.GetStatus()["CHE"]);
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(dbPath)!, "CHE.*.tmp").Length);
        }
        finally { DeleteTempDir(tempDir); }
    }

    [TestMethod]
    public async Task GetOrStartDownload_CancellationDuringTaskAcquisitionDoesNotReturnTuple()
    {
        var tempDir = CreateTempDir();
        using var cancellation = new CancellationTokenSource();
        var source = new ControlledSource(tempDir, "overture-divisions", "division_area", "release", "test-release");
        try
        {
            var service = new OvertureDivisionCacheService(
                NullLogger<OvertureDivisionCacheService>.Instance,
                tempDir,
                _ => "CH",
                new OvertureDivisionCacheTestHooks
                {
                    SourceOperation = source.RunAsync,
                    AfterInFlightTaskAcquired = cancellation.Cancel
                });

            Assert.Throws<OperationCanceledException>(() => service.GetOrStartDownload("CHE", cancellation.Token));
            await source.Entered.Task;
            Assert.AreEqual(1, source.InvocationCount);
            source.Release();
            await Task.Yield();
            Assert.AreEqual(1, source.InvocationCount);
        }
        finally { DeleteTempDir(tempDir); }
    }

    [TestMethod]
    public async Task GetOrStartDownload_ConcurrentCallersShareOneTaskAndReadyCacheSkipsSource()
    {
        var tempDir = CreateTempDir();
        var source = new ControlledSource(tempDir, "overture-divisions", "division_area", "release", "test-release");
        var svc = new OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, tempDir, _ => "CH", (_, _) => 0, source.RunAsync);
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
            var starter = results.Single(x => x.Result == OvertureDivisionEnsureResult.StartedDownload);
            Assert.AreEqual(4, results.Count(x => x.Result == OvertureDivisionEnsureResult.AwaitedExistingDownload));
            Assert.IsTrue(results.All(x => ReferenceEquals(starter.Task, x.Task)));
            Assert.AreEqual(1, source.InvocationCount);
            source.Release();
            await Task.WhenAll(results.Select(x => x.Task));
            Assert.IsTrue(svc.HasData("CHE"));
            var ready = svc.GetOrStartDownload("CHE");
            Assert.AreEqual(OvertureDivisionEnsureResult.AlreadyReady, ready.Result);
            Assert.AreEqual(1, source.InvocationCount);
            svc.DeleteFile("CHE");
            source.ResetGate();
            var (afterDeletion, afterDeletionResult) = svc.GetOrStartDownload("CHE");
            Assert.AreEqual(OvertureDivisionEnsureResult.StartedDownload, afterDeletionResult);
            Assert.AreNotSame(starter.Task, afterDeletion);
            source.Release();
            await afterDeletion;
            Assert.AreEqual(2, source.InvocationCount);
        }
        finally { DeleteTempDir(tempDir); }
    }

    [TestMethod]
    public void GetOrStartDownload_PreexistingValidCacheIsAlreadyReadyWithoutSourceWork()
    {
        var tempDir = CreateTempDir();
        var source = new ControlledSource(tempDir, "overture-divisions", "division_area", "release", "test-release");
        try
        {
            source.Publish("CHE");
            var svc = new OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, tempDir, _ => "CH", (_, _) => 0, source.RunAsync);
            var before = svc.GetStatus()["CHE"];
            var (task, result) = svc.GetOrStartDownload("CHE");
            var after = svc.GetStatus()["CHE"];
            Assert.AreEqual(OvertureDivisionEnsureResult.AlreadyReady, result);
            Assert.IsTrue(task.IsCompletedSuccessfully);
            Assert.AreEqual(0, source.InvocationCount);
            Assert.AreEqual(before, after);
        }
        finally { DeleteTempDir(tempDir); }
    }

    [TestMethod]
    public void RemoveExact_DoesNotRemoveReplacementLazy()
    {
        var map = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task>>();
        var oldValue = new Lazy<Task>(() => Task.CompletedTask);
        var replacement = new Lazy<Task>(() => Task.CompletedTask);
        map["CHE"] = oldValue;
        map["CHE"] = replacement;
        Assert.IsFalse(OvertureDivisionCacheService.RemoveExact(map, "CHE", oldValue));
        Assert.AreSame(replacement, map["CHE"]);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
    }

    private sealed class ControlledSource
    {
        private readonly string _tempDir;
        private readonly string _folder;
        private readonly string _table;
        private readonly string _metadataKey;
        private readonly string _metadataValue;
        private TaskCompletionSource _release = NewGate();

        public ControlledSource(string tempDir, string folder, string table, string metadataKey, string metadataValue)
        {
            _tempDir = tempDir; _folder = folder; _table = table; _metadataKey = metadataKey; _metadataValue = metadataValue;
        }

        public int InvocationCount { get; private set; }
        public bool Fault { get; set; }
        public bool CancelWithOwnerToken { get; set; }
        public CancellationToken OwnerToken { get; private set; }
        public TaskCompletionSource Entered { get; private set; } = NewGate();

        public async Task RunAsync(string iso3, CancellationToken ct)
        {
            InvocationCount++;
            OwnerToken = ct;
            Entered.TrySetResult();
            if (Fault) { throw new InvalidOperationException("controlled preflight failure"); }
            if (CancelWithOwnerToken) { await _release.Task.WaitAsync(ct); }
            await _release.Task;
            Publish(iso3);
        }

        public void Publish(string iso3)
        {
            var directory = Path.Combine(_tempDir, _folder);
            Directory.CreateDirectory(directory);
            var dbPath = Path.Combine(directory, $"{iso3}.db");
            using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE {_table} (id TEXT PRIMARY KEY); CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO {_table} VALUES ('row'); INSERT INTO _meta VALUES ('downloadedAt', '2026-01-01T00:00:00Z'); INSERT INTO _meta VALUES ('{_metadataKey}', '{_metadataValue}');";
            command.ExecuteNonQuery();
        }

        public void Release() => _release.TrySetResult();
        public void ResetGate() { _release = NewGate(); Entered = NewGate(); }
        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
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
