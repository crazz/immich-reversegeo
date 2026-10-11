using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Integration")]
public class LocationPreviewIntegrationTests
{
    [TestMethod]
    public async Task PreviewQueries_PageFilterAndPreserveDatabaseValues()
    {
        var connectionString = Environment.GetEnvironmentVariable("PREVIEW_TEST_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("Set PREVIEW_TEST_DB to an isolated PostgreSQL test database.");
        }

        var schema = $"preview_test_{Guid.NewGuid():N}";
        await using var setup = new NpgsqlConnection(connectionString);
        await setup.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", setup);
        await create.ExecuteNonQueryAsync();
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
            await using var seed = source.CreateCommand("""
                CREATE TABLE asset (id uuid PRIMARY KEY, "deletedAt" timestamptz);
                CREATE TABLE asset_exif ("assetId" uuid PRIMARY KEY, latitude double precision,
                    longitude double precision, country text, state text, city text);
                INSERT INTO asset SELECT ('00000000-0000-0000-0000-' || lpad(n::text, 12, '0'))::uuid, NULL
                    FROM generate_series(1, 207) AS n;
                UPDATE asset SET "deletedAt" = now() WHERE id = '00000000-0000-0000-0000-000000000206';
                INSERT INTO asset_exif
                    SELECT id, 48, 2, 'France', 'Region', 'Old city'
                    FROM asset WHERE id <> '00000000-0000-0000-0000-000000000207';
                UPDATE asset_exif SET latitude = NULL, state = NULL, city = ''' OR TRUE --'
                    WHERE "assetId" = '00000000-0000-0000-0000-000000000001';
                """);
            await seed.ExecuteNonQueryAsync();

            var repository = new ImmichDbRepository(source, NullLogger<ImmichDbRepository>.Instance);
            var all = LocationPreviewSelection.Create(LocationPreviewScope.All, null);
            var first = await repository.GetLocationPreviewBatchAsync(all, null);
            Assert.HasCount(101, first);
            Assert.IsNull(first[0].Latitude);
            Assert.IsNull(first[0].Existing.State);
            var second = await repository.GetLocationPreviewBatchAsync(all, first[99].Id);
            Assert.HasCount(101, second);
            var third = await repository.GetLocationPreviewBatchAsync(all, second[99].Id);
            Assert.HasCount(5, third);
            Assert.HasCount(205, first.Take(100).Concat(second.Take(100)).Concat(third).Select(a => a.Id).Distinct().ToArray());

            var exactCity = await repository.GetLocationPreviewBatchAsync(
                LocationPreviewSelection.Create(LocationPreviewScope.City, "' OR TRUE --"), null);
            Assert.HasCount(1, exactCity);
            var byIds = await repository.GetLocationPreviewBatchAsync(
                LocationPreviewSelection.Create(LocationPreviewScope.AssetIds,
                    "00000000-0000-0000-0000-000000000001,00000000-0000-0000-0000-000000000206,00000000-0000-0000-0000-000000000207"), null);
            Assert.HasCount(1, byIds);
            Assert.HasCount(101, await repository.GetLocationPreviewBatchAsync(
                LocationPreviewSelection.Create(LocationPreviewScope.Country, "France"), null));
            Assert.HasCount(101, await repository.GetLocationPreviewBatchAsync(
                LocationPreviewSelection.Create(LocationPreviewScope.State, "Region"), null));

            // Re-read after all preview scopes: neither values nor row counts were changed.
            await using var check = source.CreateCommand("SELECT count(*) FROM asset_exif WHERE country = 'France' AND (city = 'Old city' OR city = ''' OR TRUE --')");
            Assert.AreEqual(206L, await check.ExecuteScalarAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
