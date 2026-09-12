using System.Text.Json;
using Npgsql;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change58")]
[TestCategory("Integration")]
[TestCategory("Performance")]
[DoNotParallelize]
public sealed class ScheduledExistencePostgresPlanTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("early-match", 1)]
    [DataRow("late-match", 10000)]
    [DataRow("no-match", 0)]
    public async Task ExactProductionExistsQueryProducesReadOnlyPlanEvidence(string scenario, int matchPosition)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await ScheduledExistencePostgresFixture.CreateAsync(bound.Token);
        await using var connection = await fixture.DataSource.OpenConnectionAsync(bound.Token);
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO asset (id, "createdAt")
            SELECT md5(g::text)::uuid, timestamptz '2026-01-01 00:00:00+00'
            FROM generate_series(1, 10000) AS g ORDER BY g;
            INSERT INTO asset_exif ("assetId", city, latitude, longitude)
            SELECT md5(g::text)::uuid, CASE WHEN g = @match THEN NULL ELSE 'already populated' END, 1, 2
            FROM generate_series(1, 10000) AS g ORDER BY g;
            ANALYZE asset;
            ANALYZE asset_exif;
            """, connection))
        {
            seed.Parameters.AddWithValue("match", matchPosition);
            await seed.ExecuteNonQueryAsync(bound.Token);
        }
        string before = await ScheduledExistencePostgresFixture.ReadRowsAsync(connection, bound.Token);
        bool hasWork = await fixture.Repository.HasUnprocessedAssetsAsync(bound.Token);
        long count = await fixture.Repository.GetUnprocessedCountAsync(bound.Token);
        Assert.AreEqual(matchPosition != 0, hasWork);
        Assert.AreEqual(count > 0, hasWork);

        string query = ScheduledExistencePostgresFixture.ExistenceSql;
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + query, connection);
        string rawPlan = (string)(await explain.ExecuteScalarAsync(bound.Token))!;
        using JsonDocument plan = JsonDocument.Parse(rawPlan);
        Assert.AreEqual(JsonValueKind.Array, plan.RootElement.ValueKind);
        Assert.AreEqual(JsonValueKind.Object, plan.RootElement[0].GetProperty("Plan").ValueKind);
        Assert.AreEqual(before, await ScheduledExistencePostgresFixture.ReadRowsAsync(connection, bound.Token));

        await using var indexes = new NpgsqlCommand("""
            SELECT COALESCE(jsonb_agg(indexdef ORDER BY tablename, indexname), '[]'::jsonb)::text
            FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('asset', 'asset_exif')
            """, connection);
        using JsonDocument indexDefinitions = JsonDocument.Parse((string)(await indexes.ExecuteScalarAsync(bound.Token))!);
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "immich-reversegeo.slnx")))
        {
            root = root.Parent;
        }
        Assert.IsNotNull(root, "The opt-in evidence output belongs to this repository's _out directory.");
        string folder = Path.Combine(root.FullName, "_out", "postgresql-plans", "change58");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{scenario}-{Guid.NewGuid():N}.json");
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            await JsonSerializer.SerializeAsync(output, new
            {
                Scenario = scenario,
                Rows = 10000,
                MatchPosition = matchPosition,
                PlacementMeaning = "Insertion position only; PostgreSQL chooses its own access and join order.",
                PostgreSqlVersion = connection.PostgreSqlVersion.ToString(),
                Schema = "Isolated minimal asset/asset_exif: UUID primary keys, EXIF foreign key, nullable location/GPS/deletion fields.",
                UpstreamWitness = "Immich v3.2.0 commit 1b6098c9dbfffe978bec2d414606ed7a4c8e019a; unrelated columns/indexes omitted.",
                ExistingIndexes = indexDefinitions.RootElement,
                Query = query,
                HasWork = hasWork,
                IndependentExactCount = count,
                RowsUnchanged = true,
                Explain = plan.RootElement
            }, new JsonSerializerOptions { WriteIndented = true }, bound.Token);
        }
        TestContext.AddResultFile(path);
        TestContext.WriteLine($"PostgreSQL plan evidence: {path}");
    }
}
