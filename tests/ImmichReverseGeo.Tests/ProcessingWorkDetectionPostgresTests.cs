using ImmichReverseGeo.Tests.CrossProcessRunExclusion;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Tests.ProcessingRunLocking;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change57")]
[TestCategory("Change58")]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ProcessingWorkDetectionPostgresTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealExistenceObservationLeavesSkippedSnapshotAndFreshCountToTheAdmittedWorker(bool workDisappears)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await ScheduledExistencePostgresFixture.CreateAsync(bound.Token);
        await using var connection = await fixture.DataSource.OpenConnectionAsync(bound.Token);
        Guid id = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO asset (id, "createdAt") VALUES (@id, now());
            INSERT INTO asset_exif ("assetId", latitude, longitude) VALUES (@id, 1, 2);
            """, connection))
        {
            seed.Parameters.AddWithValue("id", id);
            await seed.ExecuteNonQueryAsync(bound.Token);
        }
        var domain = new ExecutorFixture().EnableSnapshots();
        domain.Config.Processing.BatchDelayMs = 0;
        domain.SkippedIds.Add(id);
        string? workerRows = null;
        var ledger = new System.Collections.Concurrent.ConcurrentQueue<string>();
        domain.CountBehavior = async token =>
        {
            ledger.Enqueue("worker-count");
            if (workDisappears)
            {
                // The observation already completed and the worker acquired its lock.
                await using var removeEligibility = new NpgsqlCommand("UPDATE asset SET \"deletedAt\" = now()", connection);
                await removeEligibility.ExecuteNonQueryAsync(token);
            }
            workerRows = await ScheduledExistencePostgresFixture.ReadRowsAsync(connection, token);
            return await fixture.Repository.GetUnprocessedCountAsync(token);
        };
        domain.BatchBehavior = (cursor, size, _, token) => fixture.Repository.GetUnprocessedBatchAsync(cursor, size, token);
        var lockSession = new RecordingRunLockSession
        {
            ExecuteBehavior = (command, _) =>
            {
                ledger.Enqueue(command == ProcessingRunLockCommand.Acquire ? "Acquire"
                    : command == ProcessingRunLockCommand.Release ? "Release" : "Probe");
                return Task.FromResult<object?>(command == ProcessingRunLockCommand.Probe ? 1 : true);
            }
        };
        var runLock = new PostgresqlProcessingRunLock(new RecordingRunLockSessionFactory(lockSession),
            new ManualRunLockTimeProvider(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var executor = new ProcessingRunExecutor(domain.Logger, domain, domain, domain, domain, domain,
            domain, domain.TimeProvider, runLock, new WorkerProcessExitOutcomeAccumulator());
        int probes = 0;
        var probe = new RepositoryScheduledRunWorkProbe(() => fixture.Repository);
        var detector = new ExistenceProcessingWorkDetector(async token =>
        {
            Assert.AreEqual(0, domain.CountCalls);
            Assert.AreEqual(0, domain.SkippedCalls);
            Assert.AreEqual(0, domain.ConfigCalls);
            Interlocked.Increment(ref probes);
            bool hasWork = await probe.HasUnprocessedAssetsAsync(token);
            Assert.IsTrue(hasWork, "Skipped IDs are not part of the Web query.");
            ledger.Enqueue("existence-positive");
            return hasWork;
        });
        var state = new ProcessingState();
        var admission = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        // Only transport and advisory-lock I/O are substituted; SQL, detector, coordinator and executor are real.
        await using var coordinator = new ProcessingRunCoordinator(state, new ProcessingStateEventReporter(state), detector,
            ProcessingRunBackendTestScopeFactory.Create(executor), NullLogger<ProcessingRunCoordinator>.Instance, admission);
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal,
            await ((IScheduledRunTrigger)coordinator).TriggerScheduledAsync(bound.Token));
        CollectionAssert.AreEqual(new[] { "existence-positive", "Acquire", "worker-count", "Release" }, ledger.ToArray());
        Assert.AreEqual(1, probes);
        Assert.AreEqual(1, domain.CountCalls);
        Assert.AreEqual(workDisappears ? 0 : 1, domain.SkippedCalls);
        Assert.AreEqual(workDisappears ? 0 : 1, domain.ConfigCalls);
        Assert.AreEqual(workDisappears ? 0 : 2, domain.BatchCalls);
        Assert.IsEmpty(domain.Resolutions);
        Assert.IsEmpty(domain.AirportCalls);
        Assert.IsEmpty(domain.Writes);
        Assert.IsEmpty(domain.SkippedWrites);
        Assert.AreEqual(workerRows, await ScheduledExistencePostgresFixture.ReadRowsAsync(connection, bound.Token));
        Assert.AreEqual(workDisappears ? 0L : 1L, state.TotalUnprocessed);
        Assert.AreEqual(0L, state.ProcessedThisRun);
        Assert.IsFalse(state.IsRunning);
        Assert.IsNotNull(state.LastRunCompleted);
        Assert.IsNull(coordinator.ActiveRequest);
        Assert.IsNull(admission.Snapshot.ActiveOwner);
        Assert.AreEqual(1, lockSession.DisposeCalls);
    }

    [TestMethod]
    public async Task UnchangedRepositoryPredicateAndAdapterAgreeOnEveryEligibilityNearMissWithoutMutatingRows()
    {
        PostgresIntegrationSettings settings = PostgresIntegrationSettings.ReadEnvironment() switch
        {
            PostgresIntegrationSettingsAvailable available => available.Settings,
            PostgresIntegrationSettingsFailure failure => throw new AssertFailedException(failure.Reason),
            _ => throw new AssertFailedException("PostgreSQL integration settings are unavailable.")
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using PostgresIntegrationDatabase database = await settings.CreateCaseAsync(Guid.NewGuid(), timeout.Token);
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(database.CreateConnectionString($"change58_{Guid.NewGuid():N}"));
        var repository = new ImmichDbRepository(dataSource, NullLogger<ImmichDbRepository>.Instance);
        var counter = new RepositoryScheduledRunWorkProbe(() => repository);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(timeout.Token);

        Row[] cases =
        [
            new("empty-set", null, null, 1, 2, false, false, Assets: 0),
            new("missing-exif", null, null, 1, 2, false, false, IncludeExif: false),
            new("eligible", null, null, 1, 2, false, true),
            new("multiple-eligible", null, null, 1, 2, false, true, Assets: 3),
            new("null-state", null, null, 1, 2, false, true, State: null),
            new("city-present", "City", null, 1, 2, false, false),
            new("country-present", null, "Country", 1, 2, false, false),
            new("latitude-missing", null, null, null, 2, false, false),
            new("longitude-missing", null, null, 1, null, false, false),
            new("deleted", null, null, 1, 2, true, false),
            new("empty-city-is-not-null", "", null, 1, 2, false, false),
            new("empty-country-is-not-null", null, "", 1, 2, false, false),
            new("zero-coordinates-remain-present", null, null, 0, 0, false, true)
        ];
        foreach (Row row in cases)
        {
            await using (var clear = new NpgsqlCommand("TRUNCATE asset_exif, asset", connection))
            {
                await clear.ExecuteNonQueryAsync(timeout.Token);
            }
            for (int index = 0; index < row.Assets; index++)
            {
                Guid id = Guid.NewGuid();
                await using var asset = new NpgsqlCommand(
                    "INSERT INTO asset (id, \"createdAt\", \"deletedAt\") VALUES (@id, now(), @deleted)", connection);
                asset.Parameters.AddWithValue("id", id);
                asset.Parameters.AddWithValue("deleted", NpgsqlDbType.TimestampTz, row.Deleted ? DateTime.UtcNow : DBNull.Value);
                await asset.ExecuteNonQueryAsync(timeout.Token);
                if (!row.IncludeExif)
                {
                    continue;
                }
                await using var exif = new NpgsqlCommand(
                    """
                    INSERT INTO asset_exif ("assetId", city, state, country, latitude, longitude)
                    VALUES (@id, @city, @state, @country, @latitude, @longitude)
                    """, connection);
                exif.Parameters.AddWithValue("id", id);
                exif.Parameters.AddWithValue("city", NpgsqlDbType.Text, (object?)row.City ?? DBNull.Value);
                exif.Parameters.AddWithValue("state", NpgsqlDbType.Text, (object?)row.State ?? DBNull.Value);
                exif.Parameters.AddWithValue("country", NpgsqlDbType.Text, (object?)row.Country ?? DBNull.Value);
                exif.Parameters.AddWithValue("latitude", NpgsqlDbType.Double, (object?)row.Latitude ?? DBNull.Value);
                exif.Parameters.AddWithValue("longitude", NpgsqlDbType.Double, (object?)row.Longitude ?? DBNull.Value);
                await exif.ExecuteNonQueryAsync(timeout.Token);
            }
            string before = await ReadRowsAsync(connection, timeout.Token);
            int reads = 0;
            var detector = new ExistenceProcessingWorkDetector(token =>
            {
                Assert.AreEqual(timeout.Token, token);
                Interlocked.Increment(ref reads);
                return counter.HasUnprocessedAssetsAsync(token);
            });
            ProcessingWorkDetectionResult result = await detector.DetectAsync(ProcessingWorkDetectorStub.Request(), timeout.Token);
            Assert.AreEqual(row.Eligible, result.HasWork, row.Name);
            Assert.AreEqual(1, reads, row.Name + " uses exactly one advisory repository read");
            Assert.AreEqual(row.Eligible ? (long)row.Assets : 0L, await repository.GetUnprocessedCountAsync(timeout.Token),
                row.Name + " leaves the exact count independently available to Dashboard and execution");
            Assert.AreEqual(before, await ReadRowsAsync(connection, timeout.Token), row.Name + " preserves both tables' row values");
        }
    }

    private static async Task<string> ReadRowsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        return await ScheduledExistencePostgresFixture.ReadRowsAsync(connection, cancellationToken);
    }

    private sealed record Row(string Name, string? City, string? Country, double? Latitude, double? Longitude, bool Deleted, bool Eligible,
        int Assets = 1, bool IncludeExif = true, string? State = "state-is-not-an-eligibility-filter");
}
