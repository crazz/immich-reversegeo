using ImmichReverseGeo.Gadm.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

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
    public void GetOrStartDownload_PreCancelledTokenDoesNotReturnReadyCache()
    {
        var tempDir = CreateTempDir();
        var source = new ControlledSource(tempDir);
        try
        {
            source.Publish("CHE");
            var service = new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, tempDir, source.RunAsync);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => service.GetOrStartDownload("CHE", cancellation.Token));
            Assert.AreEqual(0, source.InvocationCount);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task GetOrStartDownload_ControlledOomEscapesWithoutLeavingInflightOwnership()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir,
                (_, _) => throw new OutOfMemoryException("controlled"));

            var (failed, started) = service.GetOrStartDownload("CHE");
            Assert.AreEqual(GadmDivisionEnsureResult.StartedDownload, started);
            await Assert.ThrowsAsync<OutOfMemoryException>(async () => await failed);

            var (retry, retryResult) = service.GetOrStartDownload("CHE");
            Assert.AreEqual(GadmDivisionEnsureResult.StartedDownload, retryResult);
            Assert.AreNotSame(failed, retry);
            await Assert.ThrowsAsync<OutOfMemoryException>(async () => await retry);
        }
        finally
        {
            DeleteTempDir(tempDir);
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
            var (cancelled, _) = svc.GetOrStartDownload("CHE", owner.Token);
            Assert.AreEqual(owner.Token, source.OwnerToken);
            var liveWaiter = svc.EnsureDataAsync("CHE", CancellationToken.None);
            owner.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(async () => await cancelled);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await liveWaiter);
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
    public async Task DownloadDataInternal_MalformedSourceArtifactsFailAndCleanTemporaryFiles()
    {
        var tempDir = CreateTempDir();
        var directory = Path.Combine(tempDir, "gadm-divisions");
        try
        {
            foreach (var artifact in new[] { "header", "schema", "wkb" })
            {
                var publishedPath = Path.Combine(directory, "CHE.db");
                Directory.CreateDirectory(directory);
                File.Delete(publishedPath);
                CreateValidCache(publishedPath, "published-" + artifact);
                var publishedContent = File.ReadAllBytes(publishedPath);
                var service = new GadmDivisionCacheService(
                    NullLogger<GadmDivisionCacheService>.Instance,
                    tempDir,
                    async (_, downloadPath, ct) =>
                    {
                        Directory.CreateDirectory(directory);
                        await WriteMalformedGeoPackageAsync(downloadPath, artifact, ct);
                    },
                    (geoPackagePath, outputPath, iso3, ct) =>
                        GadmCacheExporter.ExportGeoPackageToSqlite(geoPackagePath, outputPath, iso3, ct),
                    static _ => new GadmDivisionStatus(0, null, null, null),
                    static (_, _) => false,
                    File.Exists,
                    static (_, _) => { });

                var (task, result) = service.GetOrStartDownload("CHE");
                Assert.AreEqual(GadmDivisionEnsureResult.StartedDownload, result);
                var exception = await Assert.ThrowsAsync<Exception>(async () => await task);
                Assert.IsTrue(
                    exception is InvalidDataException or SqliteException or ParseException,
                    $"Expected malformed {artifact} source failure, got {exception.GetType().Name}.");
                Assert.IsTrue(File.Exists(publishedPath));
                CollectionAssert.AreEqual(publishedContent, File.ReadAllBytes(publishedPath));
                Assert.AreEqual("published-" + artifact, ReadVersion(publishedPath));
                Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.tmp").Length);
                Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.gpkg.download").Length);
            }
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task DownloadDataInternal_CancellationBeforePublicationCleansTempsAndPreservesExistingCache()
    {
        var tempDir = CreateTempDir();
        var directory = Path.Combine(tempDir, "gadm-divisions");
        var enteredPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new CancellationTokenSource();
        try
        {
            var service = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir,
                async (_, downloadPath, ct) =>
                {
                    Directory.CreateDirectory(directory);
                    await File.WriteAllTextAsync(downloadPath, "package", ct);
                },
                (_, temporaryDbPath, _) =>
                {
                    CreateValidCache(temporaryDbPath, "temporary");
                    return 1;
                },
                async _ =>
                {
                    enteredPublication.TrySetResult();
                    await releasePublication.Task;
                });

            var (task, result) = service.GetOrStartDownload("CHE", owner.Token);
            Assert.AreEqual(GadmDivisionEnsureResult.StartedDownload, result);
            await enteredPublication.Task;
            CreateValidCache(Path.Combine(directory, "CHE.db"), "published");
            owner.Cancel();
            releasePublication.TrySetResult();

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
            Assert.AreEqual("published", ReadVersion(Path.Combine(directory, "CHE.db")));
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.tmp").Length);
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.gpkg.download").Length);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task DownloadDataInternal_ExporterObservesCancellationAndPreservesPublishedCache()
    {
        var tempDir = CreateTempDir();
        var directory = Path.Combine(tempDir, "gadm-divisions");
        var exporterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExporter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new CancellationTokenSource();
        try
        {
            var service = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir,
                async (_, downloadPath, ct) =>
                {
                    Directory.CreateDirectory(directory);
                    await File.WriteAllTextAsync(downloadPath, "package", ct);
                },
                (_, temporaryDbPath, _, ct) =>
                {
                    File.WriteAllText(temporaryDbPath, "partial sqlite");
                    exporterEntered.TrySetResult();
                    releaseExporter.Task.GetAwaiter().GetResult();
                    ct.ThrowIfCancellationRequested();
                    return 1;
                });

            var (task, result) = service.GetOrStartDownload("CHE", owner.Token);
            Assert.AreEqual(GadmDivisionEnsureResult.StartedDownload, result);
            await exporterEntered.Task;
            CreateValidCache(Path.Combine(directory, "CHE.db"), "published");
            owner.Cancel();
            releaseExporter.TrySetResult();

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
            Assert.AreEqual("published", ReadVersion(Path.Combine(directory, "CHE.db")));
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.tmp").Length);
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.gpkg.download").Length);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public void RemoveExact_DoesNotRemoveReplacementLazy()
    {
        var map = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task>>();
        var oldValue = new Lazy<Task>(() => Task.CompletedTask); var replacement = new Lazy<Task>(() => Task.CompletedTask);
        map["CHE"] = oldValue; map["CHE"] = replacement;
        Assert.IsFalse(GadmDivisionCacheService.RemoveExact(map, "CHE", oldValue)); Assert.AreSame(replacement, map["CHE"]);
    }


    [TestMethod]
    public void ExportGeoPackageToSqlite_PhaseDistinctOccurrenceAwareCheckpointsStopBeforeNativeReads()
    {
        var tempDir = CreateTempDir();
        var source = Path.Combine(tempDir, "source.gpkg");
        try
        {
            CreateTwoLayerGeoPackage(source);

            using (var rowCancellation = new CancellationTokenSource())
            {
                var beforeDataRowReads = 0;
                var afterDataRowReads = 0;
                Assert.Throws<OperationCanceledException>(() => GadmCacheExporter.ExportGeoPackageToSqlite(
                    source,
                    Path.Combine(tempDir, "rows.db"),
                    "CHE",
                    rowCancellation.Token,
                    checkpoint =>
                    {
                        if (checkpoint == GadmCacheExporter.GadmExportCheckpoint.BeforeDataRowRead
                            && ++beforeDataRowReads == 2)
                        {
                            rowCancellation.Cancel();
                        }

                        if (checkpoint == GadmCacheExporter.GadmExportCheckpoint.AfterDataRowRead)
                        {
                            afterDataRowReads++;
                        }
                    }));
                Assert.AreEqual(2, beforeDataRowReads);
                Assert.AreEqual(1, afterDataRowReads);
            }

            using (var layerDefinitionCancellation = new CancellationTokenSource())
            {
                var beforeLayerDefinitionReads = 0;
                var afterLayerDefinitionReads = 0;
                Assert.Throws<OperationCanceledException>(() => GadmCacheExporter.ExportGeoPackageToSqlite(
                    source,
                    Path.Combine(tempDir, "layer-definitions.db"),
                    "CHE",
                    layerDefinitionCancellation.Token,
                    checkpoint =>
                    {
                        if (checkpoint == GadmCacheExporter.GadmExportCheckpoint.BeforeLayerDefinitionRowRead
                            && ++beforeLayerDefinitionReads == 2)
                        {
                            layerDefinitionCancellation.Cancel();
                        }

                        if (checkpoint == GadmCacheExporter.GadmExportCheckpoint.AfterLayerDefinitionRowRead)
                        {
                            afterLayerDefinitionReads++;
                        }
                    }));
                Assert.AreEqual(2, beforeLayerDefinitionReads);
                Assert.AreEqual(1, afterLayerDefinitionReads);
            }

            using (var layerCancellation = new CancellationTokenSource())
            {
                var layers = 0;
                Assert.Throws<OperationCanceledException>(() => GadmCacheExporter.ExportGeoPackageToSqlite(
                    source,
                    Path.Combine(tempDir, "layers.db"),
                    "CHE",
                    layerCancellation.Token,
                    checkpoint =>
                    {
                        if (checkpoint == GadmCacheExporter.GadmExportCheckpoint.Layer && ++layers == 2)
                        {
                            layerCancellation.Cancel();
                        }
                    }));
            }
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public void ExportGeoPackageToSqlite_GeometryAndInsertCheckpointsStopBeforeContinuationAndRollback()
    {
        var tempDir = CreateTempDir();
        var source = Path.Combine(tempDir, "source.gpkg");
        try
        {
            CreateTwoLayerGeoPackage(source);

            RunCase(
                GadmCacheExporter.GadmExportCheckpoint.BeforeGeometry,
                checkpoints =>
                {
                    Assert.IsFalse(checkpoints.Contains(GadmCacheExporter.GadmExportCheckpoint.AfterGeometry));
                    Assert.IsFalse(checkpoints.Contains(GadmCacheExporter.GadmExportCheckpoint.BeforeInsert));
                });
            RunCase(
                GadmCacheExporter.GadmExportCheckpoint.AfterGeometry,
                checkpoints => Assert.IsFalse(checkpoints.Contains(GadmCacheExporter.GadmExportCheckpoint.BeforeInsert)));
            RunCase(
                GadmCacheExporter.GadmExportCheckpoint.BeforeInsert,
                checkpoints => Assert.IsFalse(checkpoints.Contains(GadmCacheExporter.GadmExportCheckpoint.AfterInsert)));
            RunCase(
                GadmCacheExporter.GadmExportCheckpoint.AfterInsert,
                checkpoints => Assert.AreEqual(
                    1,
                    checkpoints.Count(checkpoint => checkpoint == GadmCacheExporter.GadmExportCheckpoint.BeforeDataRowRead)));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }

        void RunCase(
            GadmCacheExporter.GadmExportCheckpoint cancellationCheckpoint,
            Action<List<GadmCacheExporter.GadmExportCheckpoint>> assertNoContinuation)
        {
            using var cancellation = new CancellationTokenSource();
            var checkpoints = new List<GadmCacheExporter.GadmExportCheckpoint>();
            var output = Path.Combine(tempDir, $"{cancellationCheckpoint}.db");

            Assert.Throws<OperationCanceledException>(() => GadmCacheExporter.ExportGeoPackageToSqlite(
                source,
                output,
                "CHE",
                cancellation.Token,
                checkpoint =>
                {
                    checkpoints.Add(checkpoint);
                    if (checkpoint == cancellationCheckpoint)
                    {
                        cancellation.Cancel();
                    }
                }));

            Assert.IsTrue(checkpoints.Contains(cancellationCheckpoint));
            assertNoContinuation(checkpoints);
            Assert.AreEqual(0L, ReadGadmAreaCount(output));
        }
    }

    [TestMethod]
    public async Task DownloadDataInternal_CancellationAfterMoveDoesNotReturnSuccess()
    {
        var tempDir = CreateTempDir();
        var directory = Path.Combine(tempDir, "gadm-divisions");
        var afterMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAfterMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new CancellationTokenSource();
        try
        {
            var service = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir,
                async (_, downloadPath, ct) =>
                {
                    Directory.CreateDirectory(directory);
                    await File.WriteAllTextAsync(downloadPath, "package", ct);
                },
                (_, temporaryDbPath, _, _) =>
                {
                    CreateValidCache(temporaryDbPath, "temporary");
                    return 1;
                },
                static _ => Task.CompletedTask,
                async _ =>
                {
                    afterMove.TrySetResult();
                    await releaseAfterMove.Task;
                });

            var (task, result) = service.GetOrStartDownload("CHE", owner.Token);
            Assert.AreEqual(GadmDivisionEnsureResult.StartedDownload, result);
            await afterMove.Task;
            owner.Cancel();
            releaseAfterMove.TrySetResult();

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "CHE.db")));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task LifecycleBoundaries_ControlledOomEscapesUnchanged()
    {
        var tempDir = CreateTempDir();
        var directory = Path.Combine(tempDir, "gadm-divisions");
        var cachePath = Path.Combine(directory, "CHE.db");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(cachePath, "cache");

            var status = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance, tempDir,
                _ => throw new OutOfMemoryException("status"),
                static (_, _) => false, static _ => false, static (_, _) => { });
            Assert.Throws<OutOfMemoryException>(() => status.GetStatus());

            var readiness = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance, tempDir,
                static _ => new GadmDivisionStatus(0, null, null, null),
                (_, _) => throw new OutOfMemoryException("readiness"), static _ => false, static (_, _) => { });
            Assert.Throws<OutOfMemoryException>(() => readiness.HasData("CHE"));

            var deletion = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance, tempDir,
                static _ => new GadmDivisionStatus(0, null, null, null), static (_, _) => false, static _ => false,
                (_, _) => throw new OutOfMemoryException("delete"));
            Assert.Throws<OutOfMemoryException>(() => deletion.DeleteFile("CHE"));

            var validation = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance, tempDir,
                async (_, downloadPath, ct) =>
                {
                    Directory.CreateDirectory(directory);
                    await File.WriteAllTextAsync(downloadPath, "package", ct);
                },
                (_, temporaryDbPath, _, _) =>
                {
                    CreateValidCache(temporaryDbPath, "temporary");
                    return 1;
                },
                static _ => new GadmDivisionStatus(0, null, null, null), static (_, _) => false,
                _ => throw new OutOfMemoryException("validation"), static (_, _) => { });
            var (task, _) = validation.GetOrStartDownload("CHE");
            await Assert.ThrowsAsync<OutOfMemoryException>(async () => await task);
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.tmp").Length);
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.gpkg.download").Length);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    private static async Task WriteMalformedGeoPackageAsync(string path, string artifact, CancellationToken ct)
    {
        if (artifact == "header")
        {
            await File.WriteAllTextAsync(path, "not a sqlite database", ct);
            return;
        }

        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        if (artifact == "schema")
        {
            command.CommandText = "CREATE TABLE gpkg_contents (table_name TEXT, data_type TEXT);";
            await command.ExecuteNonQueryAsync(ct);
            return;
        }

        command.CommandText = """
            CREATE TABLE gpkg_contents (table_name TEXT, data_type TEXT);
            CREATE TABLE gpkg_geometry_columns (table_name TEXT, column_name TEXT);
            CREATE TABLE gadm41_CHE_0 (GID_0 TEXT, NAME_0 TEXT, geom BLOB);
            INSERT INTO gpkg_contents VALUES ('gadm41_CHE_0', 'features');
            INSERT INTO gpkg_geometry_columns VALUES ('gadm41_CHE_0', 'geom');
            INSERT INTO gadm41_CHE_0 VALUES ('CHE.0_1', 'Switzerland', $geometry);
            """;
        command.Parameters.AddWithValue("$geometry", new byte[] { (byte)'G', (byte)'P', 0, 0, 0, 0, 0, 0, 1 });
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void CreateTwoLayerGeoPackage(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE gpkg_contents (table_name TEXT, data_type TEXT);
            CREATE TABLE gpkg_geometry_columns (table_name TEXT, column_name TEXT);
            CREATE TABLE gadm41_CHE_0 (GID_0 TEXT, NAME_0 TEXT, geom BLOB);
            CREATE TABLE gadm41_CHE_1 (GID_1 TEXT, NAME_1 TEXT, geom BLOB);
            INSERT INTO gpkg_contents VALUES ('gadm41_CHE_0', 'features');
            INSERT INTO gpkg_contents VALUES ('gadm41_CHE_1', 'features');
            INSERT INTO gpkg_geometry_columns VALUES ('gadm41_CHE_0', 'geom');
            INSERT INTO gpkg_geometry_columns VALUES ('gadm41_CHE_1', 'geom');
            """;
        command.ExecuteNonQuery();

        var wkb = new WKBWriter().Write(new Point(8, 47));
        var geometry = new byte[8 + wkb.Length];
        geometry[0] = (byte)'G';
        geometry[1] = (byte)'P';
        geometry[3] = 1;
        Buffer.BlockCopy(wkb, 0, geometry, 8, wkb.Length);

        command.CommandText = """
            INSERT INTO gadm41_CHE_0 VALUES ('CHE.0_1', 'Switzerland', $geometry);
            INSERT INTO gadm41_CHE_0 VALUES ('CHE.0_2', 'Switzerland', $geometry);
            INSERT INTO gadm41_CHE_1 VALUES ('CHE.1_1', 'Region', $geometry);
            """;
        command.Parameters.AddWithValue("$geometry", geometry);
        command.ExecuteNonQuery();
    }

    private static void CreateValidCache(string path, string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE gadm_area (id TEXT PRIMARY KEY); CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO gadm_area VALUES ('row'); INSERT INTO _meta VALUES ('downloadedAt', '2026-01-01T00:00:00Z'); INSERT INTO _meta VALUES ('version', $version);";
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
    }

    private static long ReadGadmAreaCount(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM gadm_area";
        return (long)command.ExecuteScalar()!;
    }

    private static string ReadVersion(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM _meta WHERE key = 'version'";
        return (string)command.ExecuteScalar()!;
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
