using ImmichReverseGeo.Tests.CrossProcessRunExclusion;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ImmichReverseGeo.Tests.DatabaseMaintenance;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Change54")]
[DoNotParallelize]
public sealed class ImmichResetRepositoryIntegrationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task ProductionRepository_PreservesExactAllSelectedAndMatchingScopes()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid activeMatch = Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Guid deletedMatch = Guid.Parse("22222222-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        Guid activeOther = Guid.Parse("33333333-cccc-cccc-cccc-cccccccccccc");
        Guid unchanged = Guid.Parse("44444444-dddd-dddd-dddd-dddddddddddd");
        const string exact = "  O'Brien; DROP TABLE asset_exif; --  ";
        const string exactState = "  State 'quoted'  ";
        const string exactCountry = "  Country; --  ";
        await fixture.SeedAsync([
            new Row(activeMatch, false, exact, exactState, exactCountry, 1, 2),
            new Row(deletedMatch, true, exact, exactState, exactCountry, 3, 4),
            new Row(activeOther, false, "Other", "State C", "Country C", 5, 6),
            new Row(unchanged, false, null, null, null, 7, 8)
        ]);
        IReadOnlyList<string> schemaBefore = await fixture.SchemaFingerprintAsync();
        AssetRow activeAssetBefore = await fixture.ReadAssetAsync(activeMatch);
        AssetRow deletedAssetBefore = await fixture.ReadAssetAsync(deletedMatch);

        await using (var readCoordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered))
        {
            DatabaseMaintenanceAdmissionResult.Admitted readOverlap =
                Assert.IsInstanceOfType<DatabaseMaintenanceAdmissionResult.Admitted>(
                    readCoordinator.TryReserveDatabaseMaintenance(
                        DatabaseMaintenanceRequestOrigin.ResetGeoDataPage));
            try
            {
                IReadOnlyList<LocationValueOption> options =
                    await fixture.Repository.GetLocationValueOptionsAsync(LocationResetScope.City);
                Assert.IsTrue(await fixture.Repository.TestConnectionAsync());
                Assert.IsTrue(options.Any(option => string.Equals(
                    option.Value,
                    exact,
                    StringComparison.Ordinal)));
                Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance>(
                    readCoordinator.Snapshot.ActiveOwner);
            }
            finally
            {
                await readOverlap.Reservation.DisposeAsync();
            }
        }

        IReadOnlyList<Guid> matching = await fixture.Repository.ClearLocationDataByValueAsync(
            LocationResetScope.City,
            exact);

        CollectionAssert.AreEqual(new[] { activeMatch }, matching.ToArray());
        Assert.IsTrue((await fixture.ReadAsync(activeMatch)).LocationIsNull);
        Assert.AreEqual(exact, (await fixture.ReadAsync(deletedMatch)).City);
        Assert.AreEqual("Other", (await fixture.ReadAsync(activeOther)).City);

        await fixture.RestoreLocationsAsync([
            new Row(activeMatch, false, exact, exactState, exactCountry, 1, 2)
        ]);
        IReadOnlyList<Guid> matchingState = await fixture.Repository.ClearLocationDataByValueAsync(
            LocationResetScope.State,
            exactState);
        CollectionAssert.AreEqual(new[] { activeMatch }, matchingState.ToArray());
        Assert.AreEqual(exactState, (await fixture.ReadAsync(deletedMatch)).State);

        await fixture.RestoreLocationsAsync([
            new Row(activeMatch, false, exact, exactState, exactCountry, 1, 2)
        ]);
        IReadOnlyList<Guid> matchingCountry = await fixture.Repository.ClearLocationDataByValueAsync(
            LocationResetScope.Country,
            exactCountry);
        CollectionAssert.AreEqual(new[] { activeMatch }, matchingCountry.ToArray());
        Assert.AreEqual(exactCountry, (await fixture.ReadAsync(deletedMatch)).Country);
        Assert.HasCount(0, await fixture.Repository.ClearLocationDataByValueAsync(
            LocationResetScope.Country,
            "value-that-is-not-present"));

        IReadOnlyList<Guid> selected = await fixture.Repository.ClearLocationDataForAssetsAsync(
            [deletedMatch, unchanged, deletedMatch]);

        CollectionAssert.AreEqual(new[] { deletedMatch }, selected.ToArray());
        Assert.IsTrue((await fixture.ReadAsync(deletedMatch)).LocationIsNull);
        Assert.IsTrue((await fixture.ReadAsync(unchanged)).LocationIsNull);

        await fixture.RestoreLocationsAsync([
            new Row(activeMatch, false, "One", "State A", "Country A", 1, 2),
            new Row(deletedMatch, true, "Two", "State B", "Country B", 3, 4),
            new Row(activeOther, false, "Three", "State C", "Country C", 5, 6)
        ]);
        long all = await fixture.Repository.ClearAllLocationDataAsync();

        Assert.AreEqual(3L, all);
        foreach (Guid id in new[] { activeMatch, deletedMatch, activeOther, unchanged })
        {
            StoredRow stored = await fixture.ReadAsync(id);
            Assert.IsTrue(stored.LocationIsNull);
        }

        StoredRow sentinel = await fixture.ReadAsync(activeMatch);
        Assert.AreEqual(1d, sentinel.Latitude);
        Assert.AreEqual(2d, sentinel.Longitude);
        Assert.AreEqual(4L, await fixture.RowCountAsync("asset"));
        Assert.AreEqual(4L, await fixture.RowCountAsync("asset_exif"));
        CollectionAssert.AreEqual(
            schemaBefore.ToArray(),
            (await fixture.SchemaFingerprintAsync()).ToArray());
        Assert.AreEqual(activeAssetBefore, await fixture.ReadAssetAsync(activeMatch));
        Assert.AreEqual(deletedAssetBefore, await fixture.ReadAssetAsync(deletedMatch));
    }

    [TestMethod]
    public async Task StatementTriggerFailure_RollsBackTheWholeResetStatement()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid first = Guid.Parse("55555555-eeee-eeee-eeee-eeeeeeeeeeee");
        Guid second = Guid.Parse("66666666-ffff-ffff-ffff-ffffffffffff");
        await fixture.SeedAsync([
            new Row(first, false, "First", "State", "Country", 1, 2),
            new Row(second, false, "Second", "State", "Country", 3, 4)
        ]);
        await fixture.ExecuteAsync(
            """
            CREATE FUNCTION change54_reject_reset() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF EXISTS (SELECT 1 FROM asset_exif WHERE city IS NULL) THEN
                    RAISE EXCEPTION 'controlled change54 statement rollback' USING ERRCODE = 'P5401';
                END IF;
                RETURN NULL;
            END;
            $$;
            CREATE TRIGGER change54_reject_reset_trigger
            AFTER UPDATE ON asset_exif
            FOR EACH STATEMENT EXECUTE FUNCTION change54_reject_reset();
            """);
        try
        {
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => fixture.Repository.ClearAllLocationDataAsync());

            Assert.AreEqual("P5401", exception.SqlState);
            Assert.AreEqual("controlled change54 statement rollback", exception.MessageText);
            Assert.AreEqual("First", (await fixture.ReadAsync(first)).City);
            Assert.AreEqual("Second", (await fixture.ReadAsync(second)).City);

            var skipped = new RecordingSkippedStore();
            await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
            var controller = new DatabaseMaintenanceController(
                coordinator,
                fixture.Repository,
                skipped,
                NullLogger<DatabaseMaintenanceController>.Instance);
            DatabaseMaintenanceResult result = await controller.ExecuteAsync(
                new DatabaseMaintenanceRequest.ResetAll(Confirmed: true));
            Assert.AreEqual(DatabaseMaintenanceDisposition.Failed, result.Disposition);
            Assert.AreEqual("immich-statement-failed", result.Postgres.Code);
            Assert.IsTrue(result.Postgres.OutcomeConfirmed);
            Assert.IsNull(result.Postgres.Count);
            Assert.AreEqual(DatabaseMaintenanceStageStatus.NotStarted, result.Skipped.Status);
            Assert.AreEqual(0, skipped.CallCount);
            Assert.AreEqual("First", (await fixture.ReadAsync(first)).City);
            Assert.AreEqual("Second", (await fixture.ReadAsync(second)).City);
        }
        finally
        {
            await fixture.ExecuteAsync(
                "DROP TRIGGER IF EXISTS change54_reject_reset_trigger ON asset_exif; DROP FUNCTION IF EXISTS change54_reject_reset();");
        }
    }

    [TestMethod]
    public async Task RestrictedRole_ReceivesRealUpdatePermissionDenial()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync([
            new Row(Guid.NewGuid(), false, "City", "State", "Country", 1, 2)
        ]);
        string role = $"change54_{Guid.NewGuid():N}";
        string quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(role);
        string quotedDatabase = new NpgsqlCommandBuilder().QuoteIdentifier(fixture.DatabaseName);
        StoredRow before = await fixture.ReadAnyAsync();
        try
        {
            await fixture.ExecuteAsync($"CREATE ROLE {quotedRole} LOGIN PASSWORD 'change54_test_only'");
            await fixture.ExecuteAsync(
                $"GRANT CONNECT ON DATABASE {quotedDatabase} TO {quotedRole}; GRANT USAGE ON SCHEMA public TO {quotedRole}; GRANT SELECT ON asset, asset_exif TO {quotedRole};");
            NpgsqlConnectionStringBuilder restricted = new(fixture.ConnectionString)
            {
                Username = role,
                Password = "change54_test_only",
                Pooling = false
            };
            await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(restricted.ConnectionString);
            var repository = new ImmichDbRepository(
                dataSource,
                NullLogger<ImmichDbRepository>.Instance);

            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => repository.ClearAllLocationDataAsync());

            Assert.AreEqual("42501", exception.SqlState);
            Assert.AreEqual(before, await fixture.ReadAnyAsync());
        }
        finally
        {
            if (await fixture.RoleExistsAsync(role))
            {
                await fixture.ExecuteAsync($"DROP OWNED BY {quotedRole}; DROP ROLE IF EXISTS {quotedRole};");
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PostgresIntegrationDatabase _database;
        private readonly NpgsqlDataSource _dataSource;

        private Fixture(
            PostgresIntegrationDatabase database,
            NpgsqlDataSource dataSource)
        {
            _database = database;
            _dataSource = dataSource;
            ConnectionString = database.CreateConnectionString($"change54_{Guid.NewGuid():N}");
            Repository = new ImmichDbRepository(
                dataSource,
                NullLogger<ImmichDbRepository>.Instance);
        }

        internal string DatabaseName => _database.DatabaseName;
        internal string ConnectionString { get; }
        internal ImmichDbRepository Repository { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            PostgresIntegrationSettings settings = PostgresIntegrationSettings.ReadEnvironment() switch
            {
                PostgresIntegrationSettingsAvailable available => available.Settings,
                PostgresIntegrationSettingsFailure failure => throw new AssertFailedException(failure.Reason),
                _ => throw new AssertFailedException("PostgreSQL integration settings are unavailable.")
            };
            using var timeout = new CancellationTokenSource(Bound);
            PostgresIntegrationDatabase database = await settings.CreateCaseAsync(
                Guid.NewGuid(),
                timeout.Token);
            try
            {
                string connectionString = database.CreateConnectionString($"change54_{Guid.NewGuid():N}");
                NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
                return new Fixture(database, dataSource);
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        internal async Task SeedAsync(IReadOnlyList<Row> rows)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
            foreach (Row row in rows)
            {
                await using NpgsqlCommand command = new(
                    """
                    INSERT INTO asset (id, "createdAt", "deletedAt") VALUES (@id, now(), @deletedAt);
                    INSERT INTO asset_exif ("assetId", city, state, country, latitude, longitude)
                    VALUES (@id, @city, @state, @country, @latitude, @longitude);
                    """,
                    connection);
                command.Parameters.AddWithValue("id", row.Id);
                command.Parameters.AddWithValue("deletedAt", row.Deleted ? DateTime.UtcNow : DBNull.Value);
                command.Parameters.AddWithValue("city", (object?)row.City ?? DBNull.Value);
                command.Parameters.AddWithValue("state", (object?)row.State ?? DBNull.Value);
                command.Parameters.AddWithValue("country", (object?)row.Country ?? DBNull.Value);
                command.Parameters.AddWithValue("latitude", row.Latitude);
                command.Parameters.AddWithValue("longitude", row.Longitude);
                await command.ExecuteNonQueryAsync();
            }
        }

        internal async Task RestoreLocationsAsync(IReadOnlyList<Row> rows)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
            foreach (Row row in rows)
            {
                await using NpgsqlCommand command = new(
                    "UPDATE asset_exif SET city=@city, state=@state, country=@country WHERE \"assetId\"=@id",
                    connection);
                command.Parameters.AddWithValue("id", row.Id);
                command.Parameters.AddWithValue("city", row.City!);
                command.Parameters.AddWithValue("state", row.State!);
                command.Parameters.AddWithValue("country", row.Country!);
                await command.ExecuteNonQueryAsync();
            }
        }

        internal async Task<StoredRow> ReadAsync(Guid id)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new(
                "SELECT city, state, country, latitude, longitude FROM asset_exif WHERE \"assetId\"=@id",
                connection);
            command.Parameters.AddWithValue("id", id);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            return new StoredRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetDouble(3),
                reader.GetDouble(4));
        }

        internal async Task<StoredRow> ReadAnyAsync()
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new(
                "SELECT city, state, country, latitude, longitude FROM asset_exif ORDER BY \"assetId\" LIMIT 1",
                connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            return new StoredRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetDouble(3),
                reader.GetDouble(4));
        }

        internal async Task<AssetRow> ReadAssetAsync(Guid id)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new(
                "SELECT \"createdAt\", \"deletedAt\" FROM asset WHERE id=@id",
                connection);
            command.Parameters.AddWithValue("id", id);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            return new AssetRow(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1));
        }

        internal async Task<IReadOnlyList<string>> SchemaFingerprintAsync()
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new(
                """
                SELECT table_name, column_name, data_type, is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name IN ('asset', 'asset_exif')
                ORDER BY table_name, ordinal_position
                """,
                connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            var fingerprint = new List<string>();
            while (await reader.ReadAsync())
            {
                fingerprint.Add(string.Join(
                    '|',
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }

            return fingerprint;
        }

        internal async Task<long> RowCountAsync(string table)
        {
            string sql = table switch
            {
                "asset" => "SELECT COUNT(*) FROM asset",
                "asset_exif" => "SELECT COUNT(*) FROM asset_exif",
                _ => throw new ArgumentOutOfRangeException(nameof(table))
            };
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<bool> RoleExistsAsync(string role)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new(
                "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role)",
                connection);
            command.Parameters.AddWithValue("role", role);
            return (bool)(await command.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            await _dataSource.DisposeAsync();
            await _database.DisposeAsync();
        }
    }

    private sealed record Row(
        Guid Id,
        bool Deleted,
        string? City,
        string? State,
        string? Country,
        double Latitude,
        double Longitude);

    private sealed record StoredRow(
        string? City,
        string? State,
        string? Country,
        double Latitude,
        double Longitude)
    {
        internal bool LocationIsNull => City is null && State is null && Country is null;
    }

    private sealed record AssetRow(DateTime CreatedAt, DateTime? DeletedAt);

    private sealed class RecordingSkippedStore : ISkippedAssetsMaintenanceStore
    {
        internal int CallCount { get; private set; }

        public Task<long> ClearAllAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(0L);
        }

        public Task<long> RemoveAsync(
            IEnumerable<Guid> assetIds,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(0L);
        }
    }
}
