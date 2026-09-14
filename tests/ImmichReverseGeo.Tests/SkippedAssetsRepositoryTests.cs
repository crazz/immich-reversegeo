using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class SkippedAssetsRepositoryTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup() => _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task AddAndGet_RoundTrips()
    {
        var repo = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repo.InitialiseAsync();

        var id = Guid.NewGuid();
        await repo.AddAsync(id);
        var all = await repo.GetAllAsync();

        Assert.IsTrue(all.Contains(id));
    }

    [TestMethod]
    public async Task Add_Duplicate_DoesNotThrow()
    {
        var repo = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repo.InitialiseAsync();
        var id = Guid.NewGuid();
        await repo.AddAsync(id);
        await repo.AddAsync(id); // INSERT OR IGNORE
        var all = await repo.GetAllAsync();
        Assert.AreEqual(1, all.Count);
    }

    /// <summary>
    /// Test 6: Persistence across instances — data written by instance A is readable by instance B
    /// pointing to the same directory, after instance B calls InitialiseAsync.
    /// </summary>
    [TestMethod]
    public async Task Persistence_AcrossInstances_DataIsReadableByNewInstance()
    {
        // Instance A: initialise and add a GUID
        var repoA = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repoA.InitialiseAsync();

        var id = Guid.NewGuid();
        await repoA.AddAsync(id);

        // Instance B: points to same directory, initialise (table already exists — no-op for CREATE IF NOT EXISTS)
        var repoB = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repoB.InitialiseAsync();

        var all = await repoB.GetAllAsync();

        Assert.IsTrue(all.Contains(id), "GUID added by instance A should be returned by instance B");
    }

    [TestMethod]
    public async Task RemoveAsync_RemovesOnlyMatchingIds()
    {
        var repo = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repo.InitialiseAsync();

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await repo.AddAsync(third);

        var removed = await repo.RemoveAsync([first, third]);
        var remaining = await repo.GetAllAsync();

        Assert.AreEqual(2L, removed);
        Assert.IsFalse(remaining.Contains(first));
        Assert.IsTrue(remaining.Contains(second));
        Assert.IsFalse(remaining.Contains(third));
    }

    [TestMethod]
    [TestCategory("Change54")]
    public async Task ClearAllAsync_ReturnsActualCountAndClosesFileHandles()
    {
        var repo = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repo.InitialiseAsync();
        await repo.AddAsync(Guid.NewGuid());
        await repo.AddAsync(Guid.NewGuid());

        long removed = await repo.ClearAllAsync();

        Assert.AreEqual(2L, removed);
        Assert.AreEqual(0L, await repo.GetCountAsync());
        string databasePath = Path.Combine(_tempDir, "skipped.db");
        File.Delete(databasePath);
        Assert.IsFalse(File.Exists(databasePath));
    }

    [TestMethod]
    [TestCategory("Change54")]
    public async Task MissingDatabase_ReturnsConfirmedZeroWithoutCreatingFile()
    {
        var repo = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        string databasePath = Path.Combine(_tempDir, "skipped.db");

        Assert.AreEqual(0L, await repo.GetCountAsync());
        Assert.AreEqual(0L, await repo.ClearAllAsync());
        Assert.AreEqual(0L, await repo.RemoveAsync([Guid.NewGuid()]));
        Assert.IsFalse(File.Exists(databasePath));
        Assert.IsFalse(Directory.Exists(_tempDir));
    }

    [TestMethod]
    [TestCategory("Change54")]
    public async Task ReadOnlyDatabase_DoesNotReportSuccessfulClear()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file permissions are exercised on Linux and macOS.");
            return;
        }

        var repo = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repo.InitialiseAsync();
        await repo.AddAsync(Guid.NewGuid());
        string databasePath = Path.Combine(_tempDir, "skipped.db");
        UnixFileMode original = File.GetUnixFileMode(databasePath);
        try
        {
            File.SetUnixFileMode(databasePath, UnixFileMode.UserRead);

            await Assert.ThrowsAsync<SqliteException>(() => repo.ClearAllAsync());
            Assert.AreEqual(1L, await repo.GetCountAsync());
        }
        finally
        {
            File.SetUnixFileMode(databasePath, original);
        }
    }

    [TestMethod]
    [TestCategory("Change54")]
    public async Task ControllerRelease_PrecedesActualFreshCountConnectionAndHandleDeletion()
    {
        var repo = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repo.InitialiseAsync();
        await repo.AddAsync(Guid.NewGuid());
        await repo.AddAsync(Guid.NewGuid());
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        DatabaseMaintenanceAdmissionResult.Admitted readOverlap =
            Assert.IsInstanceOfType<DatabaseMaintenanceAdmissionResult.Admitted>(
                coordinator.TryReserveDatabaseMaintenance(
                    DatabaseMaintenanceRequestOrigin.ResetGeoDataPage));
        try
        {
            Assert.AreEqual(2L, await repo.GetCountAsync());
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance>(
                coordinator.Snapshot.ActiveOwner);
        }
        finally
        {
            await readOverlap.Reservation.DisposeAsync();
        }

        var controller = new DatabaseMaintenanceController(
            coordinator,
            new UnusedImmichStore(),
            repo,
            NullLogger<DatabaseMaintenanceController>.Instance);

        DatabaseMaintenanceResult result = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ClearSkipList());

        Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, result.Disposition);
        Assert.AreEqual(2L, result.Skipped.Count);
        Assert.IsNull(coordinator.Snapshot.ActiveOwner);
        Assert.AreEqual(0L, await repo.GetCountAsync());
        string databasePath = Path.Combine(_tempDir, "skipped.db");
        File.Delete(databasePath);
        Assert.IsFalse(File.Exists(databasePath));
    }

    [TestMethod]
    [TestCategory("Change54")]
    public async Task WindowsExclusiveFileHandle_IsReportedAsActualStorageFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows file sharing is exercised by the Windows Change54 CI matrix.");
            return;
        }

        var repo = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repo.InitialiseAsync();
        await repo.AddAsync(Guid.NewGuid());
        string databasePath = Path.Combine(_tempDir, "skipped.db");
        using (new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Exception? failure = null;
            try
            {
                await repo.ClearAllAsync();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure);
            Assert.IsTrue(failure is SqliteException or IOException, failure.GetType().FullName);
        }

        Assert.AreEqual(1L, await repo.GetCountAsync());
    }

    private sealed class UnusedImmichStore : IImmichLocationResetStore
    {
        public Task<long> ClearAllLocationDataAsync(CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Clear Skip List must not use the Immich store.");

        public Task<IReadOnlyList<Guid>> ClearLocationDataForAssetsAsync(
            IReadOnlyCollection<Guid> assetIds,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Clear Skip List must not use the Immich store.");

        public Task<IReadOnlyList<Guid>> ClearLocationDataByValueAsync(
            LocationResetScope scope,
            string value,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Clear Skip List must not use the Immich store.");
    }
}
