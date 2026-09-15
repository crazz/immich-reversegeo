using System.Globalization;
using NetTopologySuite.IO;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Spatial;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.CandidateTests;

// Compiled against each source's existing internal test seams. The same contract
// must hold for both source implementations, including their different facades.
[TestClass]
public sealed partial class LocalCandidatePreparationTests
{
    [TestMethod]
    public async Task Ensure_UpgradesOfflineOnceAndRetainsEverySourceValue()
    {
        using var fixture = new Fixture();
        var before = fixture.Snapshot();
        var reporter = new Reporter();
        var adapter = CreateAdapter(fixture);
        var first = await adapter.Service.ExecuteAsync(CacheMutationOperation.Ensure, "CHE", reporter, default);
        var after = File.ReadAllBytes(fixture.Path);
        var second = await adapter.Service.ExecuteAsync(CacheMutationOperation.Ensure, "CHE", reporter, default);

        Assert.AreEqual(CacheMutationDisposition.Published, first.Disposition, "Local publication is observable.");
        Assert.AreEqual(CacheMutationDisposition.AlreadyReady, second.Disposition, "Prepared generation is reused.");
        Assert.AreEqual("retained", first.Version);
        Assert.AreEqual(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), first.DownloadedAtUtc);
        Assert.AreEqual(new FileInfo(fixture.Path).Length, first.FileSizeBytes);
        CollectionAssert.AreEqual(before, fixture.Snapshot(), "Original rows, rowids, WKB, indexes and source metadata are unchanged.");
        CollectionAssert.AreEqual(after, File.ReadAllBytes(fixture.Path), "Second Ensure does not write.");
        Assert.IsFalse(fixture.NeedsBuild());
        Assert.AreEqual(1, reporter.Starts);
        Assert.AreEqual(1, reporter.Ends);
        Assert.IsFalse(reporter.Steps.Contains(CacheMutationProgressStep.Downloading));
        Assert.IsFalse(reporter.Steps.Contains(CacheMutationProgressStep.Exporting));
        fixture.AssertNoTemporaries();
    }

    [TestMethod]
    [DataRow("future")]
    [DataRow("invalid-bounds")]
    [DataRow("active-journal")]
    [DataRow("publication")]
    [DataRow("permission")]
    public async Task OptionalFailure_KeepsOriginalAndDoesNotRetryEveryAsset(string failure)
    {
        using var fixture = new Fixture();
        if (failure == "future")
        {
            fixture.Execute($"INSERT INTO _meta VALUES ('{AdministrativeCandidateIndex.VersionKey}', '9:future')");
        }
        if (failure == "invalid-bounds")
        {
            fixture.Execute($"UPDATE {fixture.Table} SET bbox_xmin=2, bbox_xmax=1");
        }
        if (failure == "active-journal")
        {
            File.WriteAllBytes(fixture.Path + "-wal", []);
        }
        var bytes = File.ReadAllBytes(fixture.Path);
        var publisher = new Publisher((_, _) => throw (failure == "permission"
            ? new UnauthorizedAccessException("private-path") : new CachePublicationException(new IOException("disk-full"))));
        var adapter = CreateAdapter(fixture, publisher: failure is "publication" or "permission" ? publisher : null);
        var reporter = new Reporter();
        for (var i = 0; i < 3; i++)
        {
            var result = await adapter.Service.ExecuteAsync(CacheMutationOperation.Ensure, "CHE", reporter, default);
            Assert.AreEqual(CacheMutationDisposition.AlreadyReady, result.Disposition, failure);
        }
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.Path), failure + ": original bytes survive");
        Assert.AreEqual(failure == "future" ? 0 : 1, reporter.Starts, "One attempt per unchanged generation.");
        Assert.AreEqual(reporter.Starts, reporter.Ends, failure + ": activity is balanced");
        if (failure is "publication" or "permission")
        {
            Assert.AreEqual(1, publisher.Calls);
            fixture.Execute("UPDATE _meta SET value='replacement' WHERE key IN ('version','release')");
            await adapter.Service.ExecuteAsync(CacheMutationOperation.Ensure, "CHE", reporter, default);
            Assert.AreEqual(2, publisher.Calls, "A changed generation can retry.");
        }
        fixture.AssertNoTemporaries();
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("foreign-cancel")]
    [DataRow("oom")]
    [DataRow("sqlite-oom")]
    [DataRow("sqlite-io-oom")]
    public async Task CriticalFailure_PropagatesAndKeepsOriginal(string failure)
    {
        using var fixture = new Fixture();
        var bytes = File.ReadAllBytes(fixture.Path);
        using var cancellation = new CancellationTokenSource();
        Exception expected = failure == "oom" ? new OutOfMemoryException("controlled")
            : new OperationCanceledException(failure == "cancel" ? cancellation.Token : new CancellationToken(true));
        var adapter = CreateAdapter(fixture, beforePublication: _ =>
        {
            if (failure == "cancel")
            {
                cancellation.Cancel();
            }
            return Task.FromException(failure switch
            {
                "sqlite-oom" => new SqliteException("controlled", 7),
                "sqlite-io-oom" => new SqliteException("controlled", 10, 3082),
                _ => expected
            });
        });
        var reporter = new Reporter();
        Exception? actual = null;
        try
        {
            await adapter.Service.ExecuteAsync(CacheMutationOperation.Ensure, "CHE", reporter, cancellation.Token);
        }
        catch (Exception exception)
        {
            actual = exception;
        }
        Assert.IsNotNull(actual, failure + ": failure must escape");
        if (failure is "cancel" or "foreign-cancel")
        {
            Assert.IsInstanceOfType<OperationCanceledException>(actual, failure);
        }
        else
        {
            Assert.IsInstanceOfType<OutOfMemoryException>(actual, failure);
        }
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.Path));
        Assert.AreEqual(reporter.Starts, reporter.Ends);
        fixture.AssertNoTemporaries();
    }

    [TestMethod]
    [DataRow("ValidatingCandidate", false)]
    [DataRow("Publishing", false)]
    [DataRow("Completed", true)]
    [DataRow("ActivityStart", false)]
    [DataRow("ActivityEnd", true)]
    public async Task ReporterIoFailure_IsNeverOptionalFallback(string stage, bool published)
    {
        using var fixture = new Fixture();
        var expected = new IOException("reporter-failure");
        var reporter = new Reporter { Failure = value => value == stage ? expected : null };
        var adapter = CreateAdapter(fixture);
        var actual = await Assert.ThrowsAsync<IOException>(async () =>
            await adapter.Service.ExecuteAsync(CacheMutationOperation.Ensure, "CHE", reporter, default));
        Assert.AreSame(expected, actual);
        Assert.AreEqual(!published, fixture.NeedsBuild(), "Publication before the reporter failure remains truthful.");
        fixture.AssertNoTemporaries();
    }

    [TestMethod]
    public async Task ConcurrentEnsureAndFacade_ShareOneLocalPublication()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new Publisher(new AtomicCacheFilePublisher().Publish);
        var adapter = CreateAdapter(fixture, async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        }, publisher);
        var first = adapter.Service.ExecuteAsync(CacheMutationOperation.Ensure, "CHE", CacheMutationReporters.None, default).AsTask();
        Task? facade = null;
        Task<CacheMutationSourceResult>? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var admission = adapter.Ensure();
            facade = admission.Task;
            Assert.AreEqual("AwaitedLocalPreparation", admission.State);
            second = adapter.Service.ExecuteAsync(CacheMutationOperation.Ensure, "CHE", CacheMutationReporters.None, default).AsTask();
            Assert.IsFalse(facade.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(first, facade, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, publisher.Calls);
            Assert.AreEqual(CacheMutationDisposition.Published, first.Result.Disposition);
            Assert.AreEqual(CacheMutationDisposition.Published, second.Result.Disposition);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, facade ?? Task.CompletedTask, second ?? Task.FromResult<CacheMutationSourceResult>(null!));
        }
        fixture.AssertNoTemporaries();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StaleCopy_CannotOverwriteReplacementOrResurrectDeletion(bool delete)
    {
        using var fixture = new Fixture();
        var adapter = CreateAdapter(fixture, _ =>
        {
            if (delete)
            {
                File.Delete(fixture.Path);
            }
            else
            {
                using var replacement = new Fixture();
                replacement.Execute("UPDATE _meta SET value='replacement' WHERE key IN ('version','release')");
                new AtomicCacheFilePublisher().Publish(CopySibling(replacement.Path, fixture.Path), fixture.Path);
            }
            return Task.CompletedTask;
        });
        if (delete)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await adapter.Service.ExecuteAsync(CacheMutationOperation.Ensure, "CHE", CacheMutationReporters.None, default));
            Assert.IsFalse(File.Exists(fixture.Path));
        }
        else
        {
            var result = await adapter.Service.ExecuteAsync(CacheMutationOperation.Ensure, "CHE", CacheMutationReporters.None, default);
            Assert.AreEqual(CacheMutationDisposition.AlreadyReady, result.Disposition);
            Assert.AreEqual("replacement", result.Version);
            Assert.IsTrue(fixture.NeedsBuild(), "Stale augmented copy was discarded.");
        }
        fixture.AssertNoTemporaries();
    }

    [TestMethod]
    public async Task IndexedLookup_PreservesHolesMultiPolygonBoundariesToleranceAndTies()
    {
        using var fixture = new Fixture();
        using (var connection = fixture.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"UPDATE {fixture.Table} SET geom_wkb=$wkb, bbox_xmax=10, bbox_ymax=10";
            command.Parameters.AddWithValue("$wkb", new WKBWriter().Write(new WKTReader().Read(
                "MULTIPOLYGON (((0 0,4 0,4 4,0 4,0 0),(1 1,1 2,2 2,2 1,1 1)),((6 6,10 6,10 10,6 10,6 6)))")));
            command.ExecuteNonQuery();
        }
        var adapter = CreateAdapter(fixture);
        var points = new[] { (0d,0d), (1.5d,1.5d), (4.0001d,3d), (4.001d,3d), (8d,8d), (5d,5d), (10d,10d) };
        var before = new List<string>();
        foreach (var point in points)
        {
            before.Add(await adapter.Query(point.Item1, point.Item2));
        }
        await adapter.Service.ExecuteAsync(CacheMutationOperation.Ensure, "CHE", CacheMutationReporters.None, default);
        for (var i=0; i<points.Length; i++)
        {
            Assert.AreEqual(before[i], await adapter.Query(points[i].Item1, points[i].Item2), $"{Source}: point {i}, including candidate order and selection flags");
        }
    }

    private static string CopySibling(string source, string target)
    {
        var sibling = target + ".replacement";
        File.Copy(source, sibling);
        return sibling;
    }

    private sealed record Adapter(ICacheMutationSourceOperation Service, Func<(Task Task, string State)> Ensure, Func<double, double, Task<string>> Query);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, Source == GeometrySource.Gadm ? "gadm-divisions" : "overture-divisions", "CHE.db");
        public string Table => Source == GeometrySource.Gadm ? "gadm_area" : "division_area";

        public Fixture()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var fields = Source == GeometrySource.Gadm ? "english_type TEXT,local_type TEXT,admin_level INTEGER"
                : "subtype TEXT,class_name TEXT,admin_level INTEGER,country TEXT,is_land INTEGER,is_territorial INTEGER";
            var values = Source == GeometrySource.Gadm ? "'Region',NULL,1" : "'region','land',4,'CH',1,0";
            Execute($"""
                CREATE TABLE {Table}(id TEXT PRIMARY KEY,name TEXT,{fields},geom_wkb BLOB,bbox_xmin REAL,bbox_ymin REAL,bbox_xmax REAL,bbox_ymax REAL);
                CREATE TABLE _meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                INSERT INTO {Table}(rowid,id,name,{(Source == GeometrySource.Gadm ? "english_type,local_type,admin_level" : "subtype,class_name,admin_level,country,is_land,is_territorial")},geom_wkb,bbox_xmin,bbox_ymin,bbox_xmax,bbox_ymax)
                    VALUES (17,'z','First',{values},X'010100000000000000000000000000000000000000',0,0,1,1);
                INSERT INTO {Table} SELECT 'a','Second',{values},zeroblob(256),0,0,1,1;
                CREATE INDEX fixture_bbox_y ON {Table}(bbox_ymin,bbox_ymax);
                INSERT INTO _meta VALUES('version','retained'),('release','retained'),('downloadedAt','2026-01-01T00:00:00Z');
                """);
        }

        public SqliteConnection Open()
        {
            var connection = new SqliteConnection($"Data Source={Path};Pooling=false;Mode=ReadWrite");
            connection.Open();
            return connection;
        }

        public void Execute(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public bool NeedsBuild()
        {
            using var connection = Open();
            return AdministrativeCandidateIndex.NeedsBuild(connection, Source, default);
        }

        public string[] Snapshot()
        {
            using var connection = Open();
            var values = new List<string>();
            foreach (var sql in new[] { $"SELECT rowid,* FROM {Table} ORDER BY rowid",
                $"SELECT key,value FROM _meta WHERE key <> '{AdministrativeCandidateIndex.VersionKey}' ORDER BY key",
                $"SELECT name,sql FROM sqlite_schema WHERE tbl_name='{Table}' ORDER BY name" })
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    values.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i =>
                        reader.GetValue(i) is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
                }
            }
            return values.ToArray();
        }

        public void AssertNoTemporaries()
        {
            Assert.AreEqual(0, Directory.GetFiles(Root, "*.tmp", SearchOption.AllDirectories).Length, "Owned candidates cleaned.");
            Assert.AreEqual(0, Directory.GetFiles(Root, "*.owner", SearchOption.AllDirectories).Length, "Ownership leases cleaned.");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class Publisher(Action<string, string> publish) : ICacheFilePublisher
    {
        public int Calls { get; private set; }
        public void Publish(string candidatePath, string finalPath)
        {
            Calls++;
            publish(candidatePath, finalPath);
        }
    }

    private sealed class Reporter : ICacheMutationReporter
    {
        public List<CacheMutationProgressStep> Steps { get; } = [];
        public Func<string, Exception?> Failure { get; init; } = _ => null;
        public int Starts { get; private set; }
        public int Ends { get; private set; }
        public ValueTask ReportProgressAsync(CacheMutationProgressPayload progress, CancellationToken ct)
        {
            if (Failure(progress.Step.ToString()) is { } error)
            {
                throw error;
            }
            Steps.Add(progress.Step);
            return ValueTask.CompletedTask;
        }
        public ValueTask ReportLogAsync(string level, string message, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<ICacheMutationActivity> BeginActivityAsync(string label, CancellationToken ct)
        {
            if (Failure("ActivityStart") is { } error)
            {
                throw error;
            }
            Starts++;
            return ValueTask.FromResult<ICacheMutationActivity>(new Activity(this));
        }
        private sealed class Activity(Reporter reporter) : ICacheMutationActivity
        {
            public ValueTask DisposeAsync()
            {
                reporter.Ends++;
                return reporter.Failure("ActivityEnd") is { } error ? ValueTask.FromException(error) : ValueTask.CompletedTask;
            }
        }
    }
}
