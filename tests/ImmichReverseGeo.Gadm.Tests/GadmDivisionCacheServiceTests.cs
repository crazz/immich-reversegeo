using ImmichReverseGeo.Core.WorkerJobs;
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
            using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false"))
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
            var (retry, _) = svc.GetOrStartDownload("CHE"); source.Release(); await retry;
            File.Delete(Path.Combine(tempDir, "gadm-divisions", "CHE.db"));
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
            File.Delete(Path.Combine(tempDir, "gadm-divisions", "CHE.db")); source.ResetGate();
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
    [TestCategory("Change51")]
    public async Task ExecuteAsync_EnsureAcceptsLegacyValidCacheWithoutSourceWorkOrTimestampRewrite()
    {
        var tempDir = CreateTempDir();
        var directory = Path.Combine(tempDir, "gadm-divisions");
        var cachePath = Path.Combine(directory, "CHE.db");
        try
        {
            CreateValidCache(cachePath, "4.0");
            DateTime beforeWriteUtc = File.GetLastWriteTimeUtc(cachePath);
            byte[] beforeBytes = File.ReadAllBytes(cachePath);
            var reporter = new RecordingCacheMutationReporter();
            var service = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir,
                new GadmDivisionCacheTestHooks
                {
                    DownloadOperation = static (_, _, _) =>
                        throw new AssertFailedException("Ensure reached the GADM download boundary."),
                    ExportOperation = static (_, _, _, _) =>
                        throw new AssertFailedException("Ensure reached the GADM export boundary."),
                    CandidateOwnership = new RejectingCandidateOwnership()
                });

            CacheMutationSourceResult result = await service.ExecuteAsync(
                CacheMutationOperation.Ensure,
                "CHE",
                reporter,
                CancellationToken.None);

            Assert.AreEqual(CacheMutationSource.Gadm, service.Source);
            Assert.AreEqual(CacheMutationDisposition.AlreadyReady, result.Disposition);
            Assert.AreEqual(CacheMutationOperation.Ensure, result.Operation);
            Assert.AreEqual("CHE", result.Iso3);
            Assert.AreEqual("4.0", result.Version);
            Assert.AreEqual(result.Version, result.GadmAttribution!.DatasetVersion);
            Assert.AreEqual(CacheMutationGadmAttribution.OfficialDatasetName, result.GadmAttribution.DatasetName);
            Assert.AreEqual(CacheMutationGadmAttribution.OfficialLicenseUrl, result.GadmAttribution.LicenseUrl);
            Assert.AreEqual(CacheMutationGadmAttribution.NonCommercialUseNotice, result.GadmAttribution.UsageNotice);
            CollectionAssert.AreEqual(beforeBytes, File.ReadAllBytes(cachePath));
            Assert.AreEqual(beforeWriteUtc, File.GetLastWriteTimeUtc(cachePath));
            CollectionAssert.AreEqual(
                new[] { CacheMutationProgressStep.CheckingExisting, CacheMutationProgressStep.Completed },
                reporter.Progress.Select(item => item.Step).ToArray());
            Assert.AreEqual("4.0", reporter.Progress[^1].GadmAttribution!.DatasetVersion);
            Assert.AreEqual(0, reporter.StartedActivities);
            Assert.AreEqual(0, reporter.EndedActivities);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task ExecuteAsync_RefreshPublishesValidatedReplacementAndReportsCanonicalMetadata()
    {
        var tempDir = CreateTempDir();
        var directory = Path.Combine(tempDir, "gadm-divisions");
        var cachePath = Path.Combine(directory, "CHE.db");
        try
        {
            CreateValidCache(cachePath, "old", "CHE");
            var reporter = new RecordingCacheMutationReporter();
            string? observedOldVersion = null;
            var publisher = new DelegatingPublisher((candidatePath, finalPath) =>
            {
                Assert.AreEqual(cachePath, finalPath);
                Assert.IsTrue(File.Exists(candidatePath));
                observedOldVersion = ReadVersion(finalPath);
                new AtomicCacheFilePublisher().Publish(candidatePath, finalPath);
            });
            var service = CreateMutationService(tempDir, publisher);

            CacheMutationSourceResult result = await service.ExecuteAsync(
                CacheMutationOperation.Refresh,
                "CHE",
                reporter,
                CancellationToken.None);

            Assert.AreEqual("old", observedOldVersion);
            Assert.AreEqual("4.1", ReadVersion(cachePath));
            Assert.AreEqual(CacheMutationDisposition.Published, result.Disposition);
            Assert.AreEqual(CacheMutationOperation.Refresh, result.Operation);
            Assert.AreEqual(GadmDivisionsLogic.DatasetVersion, result.Version);
            Assert.AreEqual(result.Version, result.GadmAttribution!.DatasetVersion);
            Assert.IsTrue(result.RowCount > 0);
            Assert.IsTrue(result.FileSizeBytes > 0);
            Assert.AreEqual(1, reporter.StartedActivities);
            Assert.AreEqual(1, reporter.EndedActivities);
            CollectionAssert.AreEqual(
                new[]
                {
                    CacheMutationProgressStep.CheckingExisting,
                    CacheMutationProgressStep.PreparingSource,
                    CacheMutationProgressStep.Downloading,
                    CacheMutationProgressStep.Exporting,
                    CacheMutationProgressStep.ValidatingCandidate,
                    CacheMutationProgressStep.Publishing,
                    CacheMutationProgressStep.Completed
                },
                reporter.Progress.Select(item => item.Step).ToArray());
            Assert.IsTrue(reporter.Progress.All(item =>
                item.GadmAttribution is not null
                && item.GadmAttribution.DatasetName == CacheMutationGadmAttribution.OfficialDatasetName
                && item.GadmAttribution.DatasetVersion == GadmDivisionsLogic.DatasetVersion
                && item.GadmAttribution.LicenseUrl == CacheMutationGadmAttribution.OfficialLicenseUrl
                && item.GadmAttribution.UsageNotice == CacheMutationGadmAttribution.NonCommercialUseNotice));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task ExecuteAsync_RefreshRealExporterPublishesCacheReadableByGadmDivisionsService()
    {
        var tempDir = CreateTempDir();
        var sourcePath = Path.Combine(tempDir, "source.gpkg");
        var directory = Path.Combine(tempDir, "gadm-divisions");
        var publisher = new DelegatingPublisher(
            (candidatePath, finalPath) =>
                new AtomicCacheFilePublisher().Publish(candidatePath, finalPath));
        try
        {
            CreateTwoLayerGeoPackage(sourcePath);
            var service = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir,
                new GadmDivisionCacheTestHooks
                {
                    DownloadOperation = async (_, path, ct) =>
                    {
                        await using var source = File.OpenRead(sourcePath);
                        await using var destination = new FileStream(
                            path,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 81920,
                            useAsync: true);
                        await source.CopyToAsync(destination, ct);
                    },
                    ExportOperation = GadmCacheExporter.ExportGeoPackageToSqlite,
                    FilePublisher = publisher
                });

            CacheMutationSourceResult result = await service.ExecuteAsync(
                CacheMutationOperation.Refresh,
                "CHE",
                CacheMutationReporters.None,
                CancellationToken.None);

            var reader = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir);
            var diagnostics = await reader.FindContainingDivisionAreasAsync(
                47,
                8,
                "CHE",
                CancellationToken.None);

            Assert.AreEqual(CacheMutationDisposition.Published, result.Disposition);
            Assert.AreEqual(GadmDivisionsLogic.DatasetVersion, result.Version);
            Assert.AreEqual(3, result.RowCount);
            Assert.AreEqual(1, publisher.InvocationCount);
            Assert.IsNull(diagnostics.Error);
            Assert.AreEqual(GadmDivisionsLogic.DatasetVersion, diagnostics.Version);
            Assert.AreEqual(3, diagnostics.Candidates.Count);
            Assert.IsNotNull(diagnostics.BestMatch);
            Assert.AreEqual("CHE.1_1", diagnostics.BestMatch.Id);
            Assert.AreEqual("Region", diagnostics.BestMatch.Name);
            Assert.IsTrue(diagnostics.BestMatch.GeometryContainsPoint);
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.tmp").Length);
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.gpkg.download").Length);
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.owner").Length);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task ExecuteAsync_ZeroRowProductionExportPreservesOldSpatialCacheAndCleansOwnedArtifacts()
    {
        var tempDir = CreateTempDir();
        var validSourcePath = Path.Combine(tempDir, "old-source.gpkg");
        var emptySourcePath = Path.Combine(tempDir, "empty-source.gpkg");
        var directory = Path.Combine(tempDir, "gadm-divisions");
        var cachePath = Path.Combine(directory, "CHE.db");
        var unknownCandidate = Path.Combine(directory, "CHE.unknown.tmp");
        var liveCandidate = Path.Combine(
            directory,
            $"CHE.{Environment.ProcessId}.{Guid.NewGuid():N}.gpkg.download");
        string? ownedDb = null;
        string? ownedDownload = null;
        long? observedExportRows = null;
        try
        {
            CreateTwoLayerGeoPackage(validSourcePath);
            CreateEmptyGeoPackage(emptySourcePath);
            Directory.CreateDirectory(directory);
            Assert.AreEqual(3, GadmCacheExporter.ExportGeoPackageToSqlite(
                validSourcePath,
                cachePath,
                "CHE",
                CancellationToken.None));
            byte[] oldBytes = File.ReadAllBytes(cachePath);
            File.WriteAllText(unknownCandidate, "unknown-candidate");
            var ownership = new CacheCandidateOwnership();
            using ICacheCandidateLease liveLease = ownership.Acquire(liveCandidate);
            File.WriteAllText(liveCandidate, "live-candidate");
            var publisher = new DelegatingPublisher(static (_, _) =>
                throw new AssertFailedException("The zero-row candidate reached publication."));
            var reporter = new RecordingCacheMutationReporter();
            var service = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir,
                new GadmDivisionCacheTestHooks
                {
                    DownloadOperation = async (_, path, ct) =>
                    {
                        ownedDownload = path;
                        await CopyFileAsync(emptySourcePath, path, ct);
                    },
                    ExportOperation = (sourcePath, outputPath, iso3, ct) =>
                    {
                        ownedDb = outputPath;
                        long rows = GadmCacheExporter.ExportGeoPackageToSqlite(
                            sourcePath,
                            outputPath,
                            iso3,
                            ct);
                        observedExportRows = rows;
                        return rows;
                    },
                    FilePublisher = publisher,
                    CandidateOwnership = ownership
                });

            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await service.ExecuteAsync(
                        CacheMutationOperation.Refresh,
                        "CHE",
                        reporter,
                        CancellationToken.None));

            Assert.AreEqual("No GADM rows were downloaded for CHE.", exception.Message);
            Assert.IsNotNull(observedExportRows);
            Assert.AreEqual(0L, observedExportRows!.Value);
            Assert.AreEqual(0, publisher.InvocationCount);
            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(cachePath));
            await AssertSpatialCacheRemainsReadableAsync(tempDir);
            Assert.IsNotNull(ownedDb);
            Assert.IsNotNull(ownedDownload);
            Assert.IsFalse(File.Exists(ownedDb!));
            Assert.IsFalse(File.Exists(ownedDownload!));
            Assert.IsFalse(File.Exists(ownedDb! + ".owner"));
            Assert.IsFalse(File.Exists(ownedDownload! + ".owner"));
            Assert.IsTrue(File.Exists(unknownCandidate));
            Assert.IsTrue(File.Exists(liveCandidate));
            CollectionAssert.AreEquivalent(
                new[] { unknownCandidate },
                Directory.GetFiles(directory, "CHE.*.tmp"));
            CollectionAssert.AreEquivalent(
                new[] { liveCandidate },
                Directory.GetFiles(directory, "CHE.*.gpkg.download"));
            CollectionAssert.AreEquivalent(
                new[] { liveCandidate + ".owner" },
                Directory.GetFiles(directory, "CHE.*.owner"));
            Assert.AreEqual(1, reporter.StartedActivities);
            Assert.AreEqual(1, reporter.EndedActivities);
            Assert.IsFalse(reporter.Progress.Any(
                item => item.Step == CacheMutationProgressStep.Completed));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task ExecuteAsync_UnixWriteDenialPreservesOldSpatialCacheBeforeSourceWork()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("Unix directory permissions are unavailable on this platform.");
            return;
        }

        var tempDir = CreateTempDir();
        var validSourcePath = Path.Combine(tempDir, "old-source.gpkg");
        var directory = Path.Combine(tempDir, "gadm-divisions");
        var cachePath = Path.Combine(directory, "CHE.db");
        var unknownCandidate = Path.Combine(directory, "CHE.unknown.gpkg.download");
        var liveCandidate = Path.Combine(
            directory,
            $"CHE.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        ICacheCandidateLease? liveLease = null;
        UnixFileMode originalMode = default;
        var permissionsChanged = false;
        var probePath = Path.Combine(directory, "permission-probe");
        try
        {
            CreateTwoLayerGeoPackage(validSourcePath);
            Directory.CreateDirectory(directory);
            Assert.AreEqual(3, GadmCacheExporter.ExportGeoPackageToSqlite(
                validSourcePath,
                cachePath,
                "CHE",
                CancellationToken.None));
            byte[] oldBytes = File.ReadAllBytes(cachePath);
            File.WriteAllText(unknownCandidate, "unknown-candidate");
            var ownership = new RecordingCandidateOwnership(new CacheCandidateOwnership());
            liveLease = ownership.Acquire(liveCandidate);
            File.WriteAllText(liveCandidate, "live-candidate");
            originalMode = File.GetUnixFileMode(directory);
            File.SetUnixFileMode(
                directory,
                originalMode
                    & ~UnixFileMode.UserWrite
                    & ~UnixFileMode.GroupWrite
                    & ~UnixFileMode.OtherWrite);
            permissionsChanged = true;

            var writeDenied = false;
            try
            {
                File.WriteAllText(probePath, "probe");
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

            var reporter = new RecordingCacheMutationReporter();
            var service = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir,
                new GadmDivisionCacheTestHooks
                {
                    DownloadOperation = static (_, _, _) =>
                        throw new AssertFailedException("Storage denial reached the download boundary."),
                    ExportOperation = static (_, _, _, _) =>
                        throw new AssertFailedException("Storage denial reached the export boundary."),
                    FilePublisher = new DelegatingPublisher(static (_, _) =>
                        throw new AssertFailedException("Storage denial reached publication.")),
                    CandidateOwnership = ownership
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
            Assert.AreEqual(2, ownership.AcquiredPaths.Count);
            string ownedCandidate = ownership.AcquiredPaths[1];
            Assert.IsTrue(ownedCandidate.EndsWith(".tmp", StringComparison.Ordinal));
            Assert.IsFalse(File.Exists(ownedCandidate));
            Assert.IsFalse(File.Exists(ownedCandidate + ".owner"));
            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(cachePath));
            await AssertSpatialCacheRemainsReadableAsync(tempDir);
            Assert.IsTrue(File.Exists(unknownCandidate));
            Assert.IsTrue(File.Exists(liveCandidate));
            CollectionAssert.AreEquivalent(
                new[] { liveCandidate },
                Directory.GetFiles(directory, "CHE.*.tmp"));
            CollectionAssert.AreEquivalent(
                new[] { unknownCandidate },
                Directory.GetFiles(directory, "CHE.*.gpkg.download"));
            CollectionAssert.AreEquivalent(
                new[] { liveCandidate + ".owner" },
                Directory.GetFiles(directory, "CHE.*.owner"));
            Assert.AreEqual(0, reporter.StartedActivities);
            Assert.AreEqual(0, reporter.EndedActivities);
            CollectionAssert.AreEqual(
                new[] { CacheMutationProgressStep.CheckingExisting },
                reporter.Progress.Select(item => item.Step).ToArray());
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
                    if (File.Exists(probePath))
                    {
                        File.Delete(probePath);
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
    [TestCategory("Change51")]
    public async Task ExecuteAsync_PublicationFailurePreservesOldCacheAndForeignCandidates()
    {
        var tempDir = CreateTempDir();
        var directory = Path.Combine(tempDir, "gadm-divisions");
        var cachePath = Path.Combine(directory, "CHE.db");
        var foreignDb = Path.Combine(directory, "CHE.foreign.tmp");
        var foreignDownload = Path.Combine(directory, "CHE.foreign.gpkg.download");
        var unrecognizedOwner = Path.Combine(directory, "CHE.unknown.tmp.owner");
        var liveDb = Path.Combine(
            directory,
            $"CHE.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        string? ownedDb = null;
        string? ownedDownload = null;
        try
        {
            CreateValidCache(cachePath, "old", "CHE");
            byte[] oldBytes = File.ReadAllBytes(cachePath);
            File.WriteAllText(foreignDb, "foreign-db");
            File.WriteAllText(foreignDownload, "foreign-download");
            File.WriteAllText(unrecognizedOwner, "unrecognized-owner");
            var ownership = new CacheCandidateOwnership();
            using ICacheCandidateLease liveLease = ownership.Acquire(liveDb);
            File.WriteAllText(liveDb, "live-db");
            var reporter = new RecordingCacheMutationReporter();
            var publisher = new DelegatingPublisher(static (_, _) =>
                throw new IOException("controlled publication failure"));
            var service = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir,
                new GadmDivisionCacheTestHooks
                {
                    DownloadOperation = async (_, path, ct) =>
                    {
                        ownedDownload = path;
                        await File.WriteAllTextAsync(path, "package", ct);
                    },
                    ExportOperation = (_, path, iso3, _) =>
                    {
                        ownedDb = path;
                        CreateValidCache(path, GadmDivisionsLogic.DatasetVersion, iso3);
                        return 1;
                    },
                    FilePublisher = publisher,
                    CandidateOwnership = ownership
                });

            IOException exception = await Assert.ThrowsAsync<IOException>(async () => await service.ExecuteAsync(
                CacheMutationOperation.Refresh,
                "CHE",
                reporter,
                CancellationToken.None));

            Assert.AreEqual("controlled publication failure", exception.Message);
            Assert.AreEqual(1, publisher.InvocationCount);
            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(cachePath));
            Assert.IsTrue(File.Exists(foreignDb));
            Assert.IsTrue(File.Exists(foreignDownload));
            Assert.IsTrue(File.Exists(unrecognizedOwner));
            Assert.IsTrue(File.Exists(liveDb));
            Assert.IsNotNull(ownedDb);
            Assert.IsNotNull(ownedDownload);
            Assert.IsFalse(File.Exists(ownedDb!));
            Assert.IsFalse(File.Exists(ownedDownload!));
            Assert.IsFalse(File.Exists(ownedDb! + ".owner"));
            Assert.IsFalse(File.Exists(ownedDownload! + ".owner"));
            Assert.AreEqual(1, reporter.StartedActivities);
            Assert.AreEqual(1, reporter.EndedActivities);
            Assert.IsFalse(reporter.Progress.Any(item => item.Step == CacheMutationProgressStep.Completed));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task ExecuteAsync_CancelledWaiterDoesNotCancelLegacyEnsureOwner()
    {
        var tempDir = CreateTempDir();
        var source = new ControlledSource(tempDir);
        var waiterObservedSharedMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? ownerTask = null;
        Task<CacheMutationSourceResult>? waiterTask = null;
        try
        {
            var service = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir,
                new GadmDivisionCacheTestHooks
                {
                    SourceOperation = source.RunAsync,
                    AfterSharedMutationObserved = () =>
                    {
                        waiterObservedSharedMutation.TrySetResult();
                    }
                });
            var owner = service.GetOrStartDownload("CHE");
            ownerTask = owner.Task;
            var ownerResult = owner.Result;
            Assert.AreEqual(GadmDivisionEnsureResult.StartedDownload, ownerResult);
            await source.Entered.Task;

            using var waiterCancellation = new CancellationTokenSource();
            waiterTask = service.ExecuteAsync(
                CacheMutationOperation.Ensure,
                "CHE",
                CacheMutationReporters.None,
                waiterCancellation.Token).AsTask();
            await waiterObservedSharedMutation.Task;
            waiterCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiterTask);
            Assert.IsFalse(ownerTask.IsCompleted);

            var (joinedTask, joinedResult) = service.GetOrStartDownload("CHE");
            Assert.AreSame(ownerTask, joinedTask);
            Assert.AreEqual(GadmDivisionEnsureResult.AwaitedExistingDownload, joinedResult);
            source.Release();
            await ownerTask;
            await joinedTask;
            Assert.AreEqual(1, source.InvocationCount);
        }
        finally
        {
            source.Release();
            if (ownerTask is not null)
            {
                try
                {
                    await ownerTask;
                }
                catch
                {
                }
            }

            if (waiterTask is not null)
            {
                try
                {
                    await waiterTask;
                }
                catch
                {
                }
            }

            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task ExecuteAsync_RefreshWaitsForEnsureThenBuildsReplacement()
    {
        var tempDir = CreateTempDir();
        var source = new ControlledSource(tempDir);
        var refreshDownloads = 0;
        var refreshObservedSharedMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new DelegatingPublisher(
            (candidatePath, finalPath) =>
                new AtomicCacheFilePublisher().Publish(candidatePath, finalPath));
        Task? ensureTask = null;
        Task<CacheMutationSourceResult>? refreshTask = null;
        try
        {
            var service = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir,
                new GadmDivisionCacheTestHooks
                {
                    SourceOperation = source.RunAsync,
                    DownloadOperation = async (_, path, ct) =>
                    {
                        Interlocked.Increment(ref refreshDownloads);
                        await File.WriteAllTextAsync(path, "package", ct);
                    },
                    ExportOperation = (_, path, iso3, _) =>
                    {
                        CreateValidCache(path, GadmDivisionsLogic.DatasetVersion, iso3);
                        return 1;
                    },
                    FilePublisher = publisher,
                    AfterSharedMutationObserved = () =>
                    {
                        refreshObservedSharedMutation.TrySetResult();
                    }
                });
            var ensure = service.GetOrStartDownload("CHE");
            ensureTask = ensure.Task;
            var ensureResult = ensure.Result;
            Assert.AreEqual(GadmDivisionEnsureResult.StartedDownload, ensureResult);
            await source.Entered.Task;

            refreshTask = service.ExecuteAsync(
                CacheMutationOperation.Refresh,
                "CHE",
                CacheMutationReporters.None,
                CancellationToken.None).AsTask();
            await refreshObservedSharedMutation.Task;
            Assert.IsFalse(refreshTask.IsCompleted);
            Assert.AreEqual(0, refreshDownloads);
            Assert.AreEqual(0, publisher.InvocationCount);
            source.Release();
            await ensureTask;
            CacheMutationSourceResult refresh = await refreshTask;

            Assert.AreEqual(1, refreshDownloads);
            Assert.AreEqual(1, publisher.InvocationCount);
            Assert.AreEqual(CacheMutationDisposition.Published, refresh.Disposition);
            Assert.AreEqual(CacheMutationOperation.Refresh, refresh.Operation);
            Assert.AreEqual(GadmDivisionsLogic.DatasetVersion, ReadVersion(
                Path.Combine(tempDir, "gadm-divisions", "CHE.db")));
        }
        finally
        {
            source.Release();
            if (ensureTask is not null)
            {
                try
                {
                    await ensureTask;
                }
                catch
                {
                }
            }

            if (refreshTask is not null)
            {
                try
                {
                    await refreshTask;
                }
                catch
                {
                }
            }

            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task ExecuteAsync_EncodedCountryMismatchFailsBeforePublication()
    {
        var tempDir = CreateTempDir();
        var directory = Path.Combine(tempDir, "gadm-divisions");
        var cachePath = Path.Combine(directory, "CHE.db");
        try
        {
            CreateValidCache(cachePath, "old", "CHE");
            byte[] oldBytes = File.ReadAllBytes(cachePath);
            var publisher = new DelegatingPublisher(static (_, _) =>
                throw new AssertFailedException("Country-mismatched candidate reached publication."));
            var service = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir,
                new GadmDivisionCacheTestHooks
                {
                    DownloadOperation = static async (_, path, ct) =>
                        await File.WriteAllTextAsync(path, "package", ct),
                    ExportOperation = static (_, path, _, _) =>
                    {
                        CreateValidCache(path, GadmDivisionsLogic.DatasetVersion, "DEU");
                        return 1;
                    },
                    FilePublisher = publisher
                });

            await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.ExecuteAsync(
                CacheMutationOperation.Refresh,
                "CHE",
                CacheMutationReporters.None,
                CancellationToken.None));

            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(cachePath));
            Assert.AreEqual(0, publisher.InvocationCount);
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.tmp").Length);
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.gpkg.download").Length);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task ExecuteAsync_MalformedIso3FailsBeforeStorageOrSourceWork()
    {
        var tempDir = CreateTempDir();
        var sourceCalls = 0;
        try
        {
            var reporter = new RecordingCacheMutationReporter();
            var service = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir,
                new GadmDivisionCacheTestHooks
                {
                    DownloadOperation = (_, _, _) =>
                    {
                        Interlocked.Increment(ref sourceCalls);
                        return Task.CompletedTask;
                    },
                    CandidateOwnership = new RejectingCandidateOwnership()
                });

            await Assert.ThrowsAsync<ArgumentException>(async () => await service.ExecuteAsync(
                CacheMutationOperation.Ensure,
                "che",
                reporter,
                CancellationToken.None));

            Assert.AreEqual(0, sourceCalls);
            Assert.AreEqual(0, reporter.Progress.Count);
            Assert.IsFalse(Directory.Exists(Path.Combine(tempDir, "gadm-divisions")));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
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
                    File.Exists);

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
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.owner").Length);
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
                static (_, _) => false, static _ => false);
            Assert.Throws<OutOfMemoryException>(() => status.GetStatus());

            var readiness = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance, tempDir,
                static _ => new GadmDivisionStatus(0, null, null, null),
                (_, _) => throw new OutOfMemoryException("readiness"), static _ => false);
            Assert.Throws<OutOfMemoryException>(() => readiness.HasData("CHE"));

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
                _ => throw new OutOfMemoryException("validation"));
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

    private static void CreateEmptyGeoPackage(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE gpkg_contents (table_name TEXT, data_type TEXT);
            CREATE TABLE gpkg_geometry_columns (table_name TEXT, column_name TEXT);
            CREATE TABLE gadm41_CHE_0 (GID_0 TEXT, NAME_0 TEXT, geom BLOB);
            INSERT INTO gpkg_contents VALUES ('gadm41_CHE_0', 'features');
            INSERT INTO gpkg_geometry_columns VALUES ('gadm41_CHE_0', 'geom');
            """;
        command.ExecuteNonQuery();
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(sourcePath);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task AssertSpatialCacheRemainsReadableAsync(string tempDir)
    {
        var reader = new GadmDivisionsService(
            NullLogger<GadmDivisionsService>.Instance,
            tempDir);
        var diagnostics = await reader.FindContainingDivisionAreasAsync(
            47,
            8,
            "CHE",
            CancellationToken.None);

        Assert.IsNull(diagnostics.Error);
        Assert.AreEqual(GadmDivisionsLogic.DatasetVersion, diagnostics.Version);
        Assert.AreEqual(3, diagnostics.Candidates.Count);
        Assert.IsNotNull(diagnostics.BestMatch);
        Assert.AreEqual("CHE.1_1", diagnostics.BestMatch.Id);
        Assert.AreEqual("Region", diagnostics.BestMatch.Name);
        Assert.IsTrue(diagnostics.BestMatch.GeometryContainsPoint);
    }

    private static GadmDivisionCacheService CreateMutationService(
        string tempDir,
        ICacheFilePublisher filePublisher)
    {
        return new GadmDivisionCacheService(
            NullLogger<GadmDivisionCacheService>.Instance,
            tempDir,
            new GadmDivisionCacheTestHooks
            {
                DownloadOperation = static async (_, path, ct) =>
                    await File.WriteAllTextAsync(path, "package", ct),
                ExportOperation = static (_, path, iso3, _) =>
                {
                    CreateValidCache(path, GadmDivisionsLogic.DatasetVersion, iso3);
                    return 1;
                },
                FilePublisher = filePublisher
            });
    }

    private static void CreateValidCache(string path, string version, string? iso3 = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE gadm_area (id TEXT PRIMARY KEY, name TEXT NOT NULL, english_type TEXT NULL, local_type TEXT NULL, admin_level INTEGER NOT NULL, geom_wkb BLOB NOT NULL, bbox_xmin REAL NOT NULL, bbox_ymin REAL NOT NULL, bbox_xmax REAL NOT NULL, bbox_ymax REAL NOT NULL); CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO gadm_area VALUES ('row', 'Area', NULL, NULL, 1, X'0101', 0, 0, 1, 1); INSERT INTO _meta VALUES ('downloadedAt', '2026-01-01T00:00:00Z'); INSERT INTO _meta VALUES ('version', $version);";
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
        if (iso3 is not null)
        {
            command.CommandText = "INSERT INTO _meta VALUES ('iso3', $iso3);";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$iso3", iso3);
            command.ExecuteNonQuery();
        }
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
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class DelegatingPublisher(Action<string, string> publish) : ICacheFilePublisher
    {
        public int InvocationCount { get; private set; }

        public void Publish(string candidatePath, string finalPath)
        {
            InvocationCount++;
            publish(candidatePath, finalPath);
        }
    }

    private sealed class RejectingCandidateOwnership : ICacheCandidateOwnership
    {
        public ICacheCandidateLease Acquire(string candidatePath) =>
            throw new AssertFailedException("The mutation reached candidate ownership.");

        public bool TryCleanupAbandoned(string candidatePath) =>
            throw new AssertFailedException("The mutation reached candidate cleanup.");
    }

    private sealed class RecordingCandidateOwnership(ICacheCandidateOwnership inner) : ICacheCandidateOwnership
    {
        public List<string> AcquiredPaths { get; } = [];

        public ICacheCandidateLease Acquire(string candidatePath)
        {
            AcquiredPaths.Add(candidatePath);
            return inner.Acquire(candidatePath);
        }

        public bool TryCleanupAbandoned(string candidatePath) =>
            inner.TryCleanupAbandoned(candidatePath);
    }

    private sealed class RecordingCacheMutationReporter : ICacheMutationReporter
    {
        public List<CacheMutationProgressPayload> Progress { get; } = [];
        public int StartedActivities { get; private set; }
        public int EndedActivities { get; private set; }

        public ValueTask ReportProgressAsync(
            CacheMutationProgressPayload progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Progress.Add(progress);
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
            StartedActivities++;
            return ValueTask.FromResult<ICacheMutationActivity>(new RecordingActivity(this));
        }

        private sealed class RecordingActivity(RecordingCacheMutationReporter owner) : ICacheMutationActivity
        {
            private int _disposed;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.EndedActivities++;
                }

                return ValueTask.CompletedTask;
            }
        }
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
            var directory = Path.Combine(_tempDir, "gadm-divisions");
            Directory.CreateDirectory(directory);
            CreateValidCache(Path.Combine(directory, iso3 + ".db"), "test-version");
        }
        public void Release() => _release.TrySetResult();
        public void ResetGate() { _release = NewGate(); Entered = NewGate(); }
        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

}
