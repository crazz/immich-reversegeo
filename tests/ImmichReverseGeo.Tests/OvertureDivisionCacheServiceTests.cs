using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Core.WorkerJobs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class OvertureDivisionCacheServiceTests
{
    [TestMethod]
    [TestCategory("Change51")]
    public async Task WorkerMutation_EnsureObservesReadyAndRefreshPublishesWithBalancedProgress()
    {
        string tempDir = CreateTempDir();
        var exporter = new ControlledExporter { ThrowAfterOpen = false };
        string final = Path.Combine(tempDir, "overture-divisions", "CHE.db");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        exporter.Export(final, "CH");
        var reporter = new RecordingCacheMutationReporter();
        var service = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            tempDir,
            _ => "CH",
            new OvertureDivisionCacheTestHooks
            {
                ExportOperation = (path, alpha2, _) => exporter.Export(path, alpha2),
                ReleaseDiscovery = () => "test-release"
            });

        try
        {
            CacheMutationSourceResult ready = await service.ExecuteAsync(
                CacheMutationOperation.Ensure,
                "CHE",
                reporter,
                CancellationToken.None);
            CacheMutationSourceResult refreshed = await service.ExecuteAsync(
                CacheMutationOperation.Refresh,
                "CHE",
                reporter,
                CancellationToken.None);

            Assert.AreEqual(CacheMutationDisposition.AlreadyReady, ready.Disposition);
            Assert.AreEqual(CacheMutationDisposition.Published, refreshed.Disposition);
            Assert.AreEqual("test-release", refreshed.Version);
            Assert.AreEqual(1, reporter.ActivityStarts);
            Assert.AreEqual(1, reporter.ActivityEnds);
            CollectionAssert.Contains(reporter.Steps, CacheMutationProgressStep.CheckingExisting);
            CollectionAssert.Contains(reporter.Steps, CacheMutationProgressStep.Publishing);
            CollectionAssert.Contains(reporter.Steps, CacheMutationProgressStep.Completed);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task WorkerEnsureAndLegacyFacadeShareOneOwnedSourceFlight()
    {
        string tempDir = CreateTempDir();
        var exporter = new ControlledExporter { ThrowAfterOpen = false };
        var candidateValidated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            tempDir,
            _ => "CH",
            new OvertureDivisionCacheTestHooks
            {
                ExportOperation = (path, alpha2, _) => exporter.Export(path, alpha2),
                ReleaseDiscovery = () => "test-release",
                BeforePublication = async _ =>
                {
                    candidateValidated.TrySetResult();
                    await releasePublication.Task;
                }
            });
        Task<CacheMutationSourceResult>? worker = null;
        Task? legacy = null;

        try
        {
            worker = service.ExecuteAsync(
                CacheMutationOperation.Ensure,
                "CHE",
                CacheMutationReporters.None,
                CancellationToken.None).AsTask();
            await candidateValidated.Task.WaitAsync(TimeSpan.FromSeconds(5));

            (legacy, OvertureDivisionEnsureResult admission) =
                service.GetOrStartDownload("CHE");
            Assert.AreEqual(
                OvertureDivisionEnsureResult.AwaitedExistingDownload,
                admission);
            Assert.IsFalse(legacy.IsCompleted, "legacy-facade-waits-for-worker-owned-flight");
            Assert.AreEqual(1, exporter.OpenedOutputs.Count, "one-source-export-before-release");

            releasePublication.TrySetResult();
            await Task.WhenAll(worker, legacy).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(CacheMutationDisposition.Published, worker.Result.Disposition);
            Assert.AreEqual(1, exporter.OpenedOutputs.Count, "one-shared-source-export");
            Assert.IsTrue(service.HasData("CHE"));
        }
        finally
        {
            releasePublication.TrySetResult();
            if (worker is not null || legacy is not null)
            {
                await Task.WhenAll(
                    worker ?? Task.FromResult<CacheMutationSourceResult>(null!),
                    legacy ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            }

            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    [DataRow("wrong-country", OvertureDivisionEnsureResult.StartedDownload)]
    [DataRow("missing-release", OvertureDivisionEnsureResult.StartedDownload)]
    [DataRow("legacy-no-country", OvertureDivisionEnsureResult.AlreadyReady)]
    public async Task LegacyEnsureFacade_UsesSourceValidityWhilePreservingPreCountryMetadataCache(
        string row,
        OvertureDivisionEnsureResult expected)
    {
        string tempDir = CreateTempDir();
        string final = Path.Combine(tempDir, "overture-divisions", "CHE.db");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var exporter = new ControlledExporter { ThrowAfterOpen = false };
        exporter.Export(final, "CH");
        using (var connection = new SqliteConnection($"Data Source={final};Pooling=false"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = row switch
            {
                "wrong-country" => "UPDATE _meta SET value = 'US' WHERE key = 'country'",
                "missing-release" => "DELETE FROM _meta WHERE key = 'release'",
                "legacy-no-country" => "DELETE FROM _meta WHERE key = 'country'",
                _ => throw new AssertFailedException("unknown-cache-validity-row")
            };
            command.ExecuteNonQuery();
        }

        var sourceCalls = 0;
        var service = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            tempDir,
            _ => "CH",
            new OvertureDivisionCacheTestHooks
            {
                SourceOperation = (_, _) =>
                {
                    sourceCalls++;
                    return Task.CompletedTask;
                }
            });

        try
        {
            (Task task, OvertureDivisionEnsureResult result) =
                service.GetOrStartDownload("CHE");
            await task;

            Assert.AreEqual(expected, result, row);
            Assert.AreEqual(
                expected == OvertureDivisionEnsureResult.AlreadyReady ? 0 : 1,
                sourceCalls,
                row + "-source-calls");
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task WorkerMutation_PublicationFailureRetainsOldCacheAndCleansOnlyOwnedOrStaleTemps()
    {
        string tempDir = CreateTempDir();
        var exporter = new ControlledExporter { ThrowAfterOpen = false };
        string directory = Path.Combine(tempDir, "overture-divisions");
        string final = Path.Combine(directory, "CHE.db");
        Directory.CreateDirectory(directory);
        exporter.Export(final, "CH");
        byte[] oldBytes = File.ReadAllBytes(final);
        string liveSibling = Path.Combine(
            directory,
            $"CHE.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        string staleSibling = Path.Combine(
            directory,
            $"CHE.{int.MaxValue}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(liveSibling, "live");
        File.WriteAllText(staleSibling, "stale");
        var service = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            tempDir,
            _ => "CH",
            new OvertureDivisionCacheTestHooks
            {
                ExportOperation = (path, alpha2, _) => exporter.Export(path, alpha2),
                FilePublisher = new FailingPublisher()
            });

        try
        {
            await Assert.ThrowsAsync<CachePublicationException>(async () =>
                await service.ExecuteAsync(
                    CacheMutationOperation.Refresh,
                    "CHE",
                    CacheMutationReporters.None,
                    CancellationToken.None));

            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(final));
            Assert.IsTrue(File.Exists(liveSibling), "live foreign candidate retained");
            Assert.IsTrue(File.Exists(staleSibling), "unverifiable foreign candidate retained");
            Assert.AreEqual(2, Directory.GetFiles(directory, "CHE.*.tmp").Length);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task WorkerMutation_CleanupDiscoversOnlySafeOwnerOnlySidecars()
    {
        string tempDir = CreateTempDir();
        string directory = Path.Combine(tempDir, "overture-divisions");
        Directory.CreateDirectory(directory);
        string final = Path.Combine(directory, "CHE.db");
        ExportReaderCompatibleCache(final, "old-release");
        byte[] oldBytes = File.ReadAllBytes(final);
        var ownership = new CacheCandidateOwnership();
        string abandonedCandidate = Path.Combine(directory, "CHE.abandoned.tmp");
        string liveCandidate = Path.Combine(directory, "CHE.live.tmp");
        string unknownCandidate = Path.Combine(directory, "CHE.unknown.tmp");
        File.WriteAllBytes(
            CacheCandidateOwnership.GetOwnerPath(abandonedCandidate),
            CacheCandidateOwnership.GetMarkerBytes());
        File.WriteAllText(
            CacheCandidateOwnership.GetOwnerPath(unknownCandidate),
            "unrecognized-owner");
        using ICacheCandidateLease liveLease = ownership.Acquire(liveCandidate);
        var service = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            tempDir,
            static iso3 => iso3 == "CHE" ? "CH" : null,
            new OvertureDivisionCacheTestHooks
            {
                CandidateOwnership = ownership,
                ExportOperation = static (_, _, _) =>
                    throw new InvalidOperationException("stop after cleanup")
            });

        try
        {
            await AssertReadableOvertureCacheAsync(tempDir, "old-release");

            InvalidOperationException failure =
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await service.ExecuteAsync(
                        CacheMutationOperation.Refresh,
                        "CHE",
                        CacheMutationReporters.None,
                        CancellationToken.None));

            Assert.AreEqual("stop after cleanup", failure.Message);
            Assert.IsFalse(File.Exists(
                CacheCandidateOwnership.GetOwnerPath(abandonedCandidate)));
            Assert.IsTrue(File.Exists(
                CacheCandidateOwnership.GetOwnerPath(liveCandidate)));
            Assert.IsTrue(File.Exists(
                CacheCandidateOwnership.GetOwnerPath(unknownCandidate)));
            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(final));
            await AssertReadableOvertureCacheAsync(tempDir, "old-release");
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.tmp").Length);
        }
        finally
        {
            liveLease.Dispose();
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    [DataRow("zero-row")]
    [DataRow("missing-release")]
    public async Task WorkerMutation_InvalidCandidatePreservesOldCacheBeforePublication(
        string defect)
    {
        string tempDir = CreateTempDir();
        string directory = Path.Combine(tempDir, "overture-divisions");
        string final = Path.Combine(directory, "CHE.db");
        Directory.CreateDirectory(directory);
        var exporter = new ControlledExporter { ThrowAfterOpen = false };
        exporter.Export(final, "CH");
        byte[] oldBytes = File.ReadAllBytes(final);
        var publisher = new RejectingPublisher();
        var service = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            tempDir,
            _ => "CH",
            new OvertureDivisionCacheTestHooks
            {
                ExportOperation = (path, alpha2, _) =>
                {
                    long rows = exporter.Export(path, alpha2);
                    if (defect == "missing-release")
                    {
                        using var connection = new SqliteConnection(
                            $"Data Source={path};Pooling=false");
                        connection.Open();
                        using var command = connection.CreateCommand();
                        command.CommandText =
                            "DELETE FROM _meta WHERE key = 'release'";
                        command.ExecuteNonQuery();
                    }

                    return defect == "zero-row" ? 0 : rows;
                },
                FilePublisher = publisher
            });

        try
        {
            InvalidOperationException failure =
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await service.ExecuteAsync(
                        CacheMutationOperation.Refresh,
                        "CHE",
                        CacheMutationReporters.None,
                        CancellationToken.None));

            Assert.IsFalse(
                failure.Message.Contains(tempDir, StringComparison.Ordinal),
                "safe candidate failure omits the host path");
            Assert.AreEqual(0, publisher.Calls, defect + "-publisher-not-reached");
            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(final));
            Assert.IsTrue(service.HasData("CHE"));
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.tmp").Length);
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.owner").Length);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task WorkerMutation_CandidateMissingReaderSchemaPreservesReadableOldCacheBeforePublication()
    {
        string tempDir = CreateTempDir();
        string directory = Path.Combine(tempDir, "overture-divisions");
        string final = Path.Combine(directory, "CHE.db");
        Directory.CreateDirectory(directory);
        ExportReaderCompatibleCache(final, "old-release");
        byte[] oldBytes = File.ReadAllBytes(final);
        var exporter = new ControlledExporter { ThrowAfterOpen = false };
        var publisher = new RejectingPublisher();
        var service = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            tempDir,
            _ => "CH",
            new OvertureDivisionCacheTestHooks
            {
                ExportOperation = (path, alpha2, _) =>
                {
                    long rows = exporter.Export(path, alpha2);
                    using var connection = new SqliteConnection(
                        $"Data Source={path};Pooling=false");
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "ALTER TABLE division_area DROP COLUMN geom_wkb";
                    command.ExecuteNonQuery();
                    return rows;
                },
                FilePublisher = publisher
            });

        try
        {
            await AssertReadableOvertureCacheAsync(tempDir, "old-release");

            InvalidOperationException failure =
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await service.ExecuteAsync(
                        CacheMutationOperation.Refresh,
                        "CHE",
                        CacheMutationReporters.None,
                        CancellationToken.None));

            StringAssert.Contains(failure.Message, "invalid cache");
            Assert.AreEqual(0, publisher.Calls, "reader-incompatible-candidate-not-published");
            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(final));
            await AssertReadableOvertureCacheAsync(tempDir, "old-release");
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.tmp").Length);
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.owner").Length);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task WorkerMutation_InvalidSourceMappingFailsBeforeFilesystemOrExporter()
    {
        string tempDir = CreateTempDir();
        var exporter = new ControlledExporter { ThrowAfterOpen = false };
        var service = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            tempDir,
            _ => null,
            new OvertureDivisionCacheTestHooks
            {
                ExportOperation = (path, alpha2, _) => exporter.Export(path, alpha2)
            });

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.ExecuteAsync(
                    CacheMutationOperation.Ensure,
                    "CHE",
                    CacheMutationReporters.None,
                    CancellationToken.None));

            Assert.AreEqual(0, exporter.OpenedOutputs.Count);
            Assert.IsFalse(Directory.Exists(
                Path.Combine(tempDir, "overture-divisions")));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task WorkerMutation_UnixWriteDenialPreservesReadableOldCacheBeforeExporter()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("Unix directory permissions are unavailable on this platform.");
            return;
        }

        string tempDir = CreateTempDir();
        string directory = Path.Combine(tempDir, "overture-divisions");
        string final = Path.Combine(directory, "CHE.db");
        string unknownCandidate = Path.Combine(directory, "CHE.unknown.tmp");
        string liveCandidate = Path.Combine(
            directory,
            $"CHE.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        string probe = Path.Combine(directory, "permission-probe");
        ICacheCandidateLease? liveLease = null;
        UnixFileMode originalMode = default;
        bool permissionsChanged = false;
        try
        {
            Directory.CreateDirectory(directory);
            ExportReaderCompatibleCache(final, "old-release");
            byte[] oldBytes = File.ReadAllBytes(final);
            File.WriteAllText(unknownCandidate, "unknown");
            var ownership = new CacheCandidateOwnership();
            liveLease = ownership.Acquire(liveCandidate);
            File.WriteAllText(liveCandidate, "live");
            originalMode = File.GetUnixFileMode(directory);
            File.SetUnixFileMode(
                directory,
                originalMode
                    & ~UnixFileMode.UserWrite
                    & ~UnixFileMode.GroupWrite
                    & ~UnixFileMode.OtherWrite);
            permissionsChanged = true;

            bool writeDenied = false;
            try
            {
                File.WriteAllText(probe, "probe");
            }
            catch (Exception writeException) when (writeException is UnauthorizedAccessException or IOException)
            {
                writeDenied = true;
            }

            if (!writeDenied)
            {
                Assert.Inconclusive(
                    "The current filesystem identity bypasses Unix directory write permissions.");
            }

            var exporterCalls = 0;
            var reporter = new RecordingCacheMutationReporter();
            var service = new OvertureDivisionCacheService(
                NullLogger<OvertureDivisionCacheService>.Instance,
                tempDir,
                _ => "CH",
                new OvertureDivisionCacheTestHooks
                {
                    ExportOperation = (_, _, _) =>
                    {
                        exporterCalls++;
                        throw new AssertFailedException("Storage denial reached the exporter boundary.");
                    }
                });

            CacheCandidateOwnershipException exception =
                await Assert.ThrowsAsync<CacheCandidateOwnershipException>(async () =>
                    await service.ExecuteAsync(
                        CacheMutationOperation.Refresh,
                        "CHE",
                        reporter,
                        CancellationToken.None));

            Assert.AreEqual(
                "The cache candidate ownership lease could not be established.",
                exception.Message);
            Assert.AreEqual(0, exporterCalls);
            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(final));
            await AssertReadableOvertureCacheAsync(tempDir, "old-release");
            CollectionAssert.AreEquivalent(
                new[] { liveCandidate, unknownCandidate },
                Directory.GetFiles(directory, "CHE.*.tmp"));
            CollectionAssert.AreEquivalent(
                new[] { liveCandidate + ".owner" },
                Directory.GetFiles(directory, "CHE.*.owner"));
            Assert.AreEqual(0, reporter.ActivityStarts);
            Assert.AreEqual(0, reporter.ActivityEnds);
            CollectionAssert.AreEqual(
                new[] { CacheMutationProgressStep.CheckingExisting },
                reporter.Steps);
        }
        finally
        {
            try
            {
                if (permissionsChanged)
                {
                    File.SetUnixFileMode(directory, originalMode);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(probe))
                    {
                        File.Delete(probe);
                    }
                }
                finally
                {
                    try
                    {
                        liveLease?.Dispose();
                    }
                    finally
                    {
                        DeleteTempDir(tempDir);
                    }
                }
            }
        }
    }

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
            var (sharedTask, _) = service.GetOrStartDownload("CHE");
            source.Release();
            await sharedTask;
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

    private static void ExportReaderCompatibleCache(string path, string release)
    {
        using var connection = OvertureDivisionCacheService.OpenTemporaryOutputConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE division_area (
                id TEXT, name TEXT, subtype TEXT, class_name TEXT, admin_level INTEGER,
                country TEXT, is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO division_area
                (id, name, subtype, class_name, admin_level, country, is_land, is_territorial,
                 geom_wkb, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax)
            VALUES ('fixture', 'Region', 'region', 'land', 4, 'CH', 1, 0,
                    $geometry, 7, 46, 9, 48);
            INSERT INTO _meta VALUES ('downloadedAt', '2026-09-08T00:00:00Z');
            INSERT INTO _meta VALUES ('release', $release);
            """;
        command.Parameters.AddWithValue("$geometry", ValidPolygonWkb());
        command.Parameters.AddWithValue("$release", release);
        command.ExecuteNonQuery();
    }

    private static async Task AssertReadableOvertureCacheAsync(
        string tempDir,
        string expectedRelease)
    {
        var places = new OverturePlacesService(
            NullLogger<OverturePlacesService>.Instance,
            tempDir,
            tempDir);
        var divisions = new OvertureDivisionsService(
            NullLogger<OvertureDivisionsService>.Instance,
            places,
            tempDir,
            tempDir,
            static alpha2 => alpha2 == "CH" ? "CHE" : null);
        var diagnostics = await divisions.FindContainingDivisionAreasAsync(
            47,
            8,
            "CH",
            "CHE");

        Assert.IsNull(diagnostics.Error);
        Assert.AreEqual(expectedRelease, diagnostics.Release);
        Assert.AreEqual(1, diagnostics.Candidates.Count);
        Assert.AreEqual("fixture", diagnostics.BestMatch!.Id);
        Assert.IsTrue(diagnostics.BestMatch.GeometryContainsPoint);
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
            command.CommandText = $"""
                CREATE TABLE {_table} (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, subtype TEXT, class_name TEXT,
                    admin_level INTEGER, country TEXT, is_land INTEGER NOT NULL,
                    is_territorial INTEGER NOT NULL, geom_wkb BLOB, bbox_xmin REAL,
                    bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
                CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO {_table} VALUES (
                    'row', 'Ready', 'region', 'land', 4, 'CH', 1, 0,
                    NULL, 1000, 1000, 1001, 1001);
                INSERT INTO _meta VALUES ('downloadedAt', '2026-01-01T00:00:00Z');
                INSERT INTO _meta VALUES ('{_metadataKey}', '{_metadataValue}');
                """;
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
                CREATE TABLE division_area (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, subtype TEXT, class_name TEXT,
                    admin_level INTEGER, country TEXT, is_land INTEGER NOT NULL,
                    is_territorial INTEGER NOT NULL, geom_wkb BLOB, bbox_xmin REAL,
                    bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
                CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO division_area VALUES (
                    'test-id', 'Test division', 'region', 'land', 4, @country, 1, 0,
                    NULL, 1000, 1000, 1001, 1001);
                INSERT INTO _meta VALUES ('downloadedAt', '2026-03-27T12:00:00Z');
                INSERT INTO _meta VALUES ('release', 'test-release');
                INSERT INTO _meta VALUES ('country', 'CH');
                ";
            cmd.Parameters.AddWithValue("@country", alpha2);
            cmd.ExecuteNonQuery();
            return 1;
        }
    }

    private sealed class FailingPublisher : ICacheFilePublisher
    {
        public void Publish(string candidatePath, string finalPath)
        {
            throw new CachePublicationException(new IOException("controlled"));
        }
    }

    private sealed class RejectingPublisher : ICacheFilePublisher
    {
        public int Calls { get; private set; }

        public void Publish(string candidatePath, string finalPath)
        {
            Calls++;
            Assert.Fail("An invalid cache candidate reached publication.");
        }
    }

    private sealed class RecordingCacheMutationReporter : ICacheMutationReporter
    {
        public List<CacheMutationProgressStep> Steps { get; } = [];
        public int ActivityStarts { get; private set; }
        public int ActivityEnds { get; private set; }

        public ValueTask ReportProgressAsync(
            CacheMutationProgressPayload progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Steps.Add(progress.Step);
            return ValueTask.CompletedTask;
        }

        public ValueTask ReportLogAsync(
            string level,
            string message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<ICacheMutationActivity> BeginActivityAsync(
            string label,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActivityStarts++;
            return ValueTask.FromResult<ICacheMutationActivity>(new Activity(this));
        }

        private sealed class Activity(RecordingCacheMutationReporter owner) : ICacheMutationActivity
        {
            public ValueTask DisposeAsync()
            {
                owner.ActivityEnds++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
