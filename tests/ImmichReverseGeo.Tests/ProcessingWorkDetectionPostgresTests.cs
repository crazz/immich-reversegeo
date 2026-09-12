using ImmichReverseGeo.Tests.CrossProcessRunExclusion;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change57")]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ProcessingWorkDetectionPostgresTests
{
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
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(database.CreateConnectionString($"change57_{Guid.NewGuid():N}"));
        var repository = new ImmichDbRepository(dataSource, NullLogger<ImmichDbRepository>.Instance);
        var counter = new RepositoryScheduledRunWorkCounter(() => repository);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(timeout.Token);

        Row[] cases =
        [
            new("eligible", null, null, 1, 2, false, true),
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
            await using (var seed = new NpgsqlCommand(
                """
                TRUNCATE asset_exif, asset;
                INSERT INTO asset (id, "createdAt", "deletedAt") VALUES (@id, now(), @deleted);
                INSERT INTO asset_exif ("assetId", city, state, country, latitude, longitude)
                VALUES (@id, @city, 'state-is-not-an-eligibility-filter', @country, @latitude, @longitude);
                """, connection))
            {
                seed.Parameters.AddWithValue("id", Guid.NewGuid());
                seed.Parameters.AddWithValue("deleted", NpgsqlDbType.TimestampTz, row.Deleted ? DateTime.UtcNow : DBNull.Value);
                seed.Parameters.AddWithValue("city", NpgsqlDbType.Text, (object?)row.City ?? DBNull.Value);
                seed.Parameters.AddWithValue("country", NpgsqlDbType.Text, (object?)row.Country ?? DBNull.Value);
                seed.Parameters.AddWithValue("latitude", NpgsqlDbType.Double, (object?)row.Latitude ?? DBNull.Value);
                seed.Parameters.AddWithValue("longitude", NpgsqlDbType.Double, (object?)row.Longitude ?? DBNull.Value);
                await seed.ExecuteNonQueryAsync(timeout.Token);
            }
            string before = await ReadRowsAsync(connection, timeout.Token);
            int reads = 0;
            var detector = new CountBackedProcessingWorkDetector(token =>
            {
                Assert.AreEqual(timeout.Token, token);
                Interlocked.Increment(ref reads);
                return counter.GetUnprocessedCountAsync(token);
            });
            ProcessingWorkDetectionResult result = await detector.DetectAsync(ProcessingWorkDetectorStub.Request(), timeout.Token);
            Assert.AreEqual(row.Eligible, result.HasWork, row.Name);
            Assert.AreEqual(1, reads, row.Name + " uses exactly one advisory repository read");
            Assert.AreEqual(row.Eligible ? 1L : 0L, await repository.GetUnprocessedCountAsync(timeout.Token),
                row.Name + " leaves the exact count independently available to Dashboard and execution");
            Assert.AreEqual(before, await ReadRowsAsync(connection, timeout.Token), row.Name + " preserves both tables' row values");
        }
    }

    private static async Task<string> ReadRowsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT row_to_json(a)::text || row_to_json(e)::text FROM asset a JOIN asset_exif e ON e.\"assetId\"=a.id", connection);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private sealed record Row(string Name, string? City, string? Country, double? Latitude, double? Longitude, bool Deleted, bool Eligible);
}
