using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change58")]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ScheduledExistencePostgresFailureTests
{
    [TestMethod]
    public async Task CancelledConnectionOpeningPropagatesTheCallerToken()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await ScheduledExistencePostgresFixture.CreateAsync(bound.Token);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        OperationCanceledException failure = await Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.Repository.HasUnprocessedAssetsAsync(caller.Token));
        Assert.AreEqual(caller.Token, failure.CancellationToken);
        Assert.IsFalse(await fixture.Repository.HasUnprocessedAssetsAsync(bound.Token), "A later independent read still works.");
    }

    [TestMethod]
    [DataRow(false, "42P01")]
    [DataRow(true, "3D000")]
    public async Task MissingSchemaOrDatabaseIsAFailureAndNeverNoWork(bool missingDatabase, string sqlState)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await ScheduledExistencePostgresFixture.CreateAsync(bound.Token);
        var settings = new NpgsqlConnectionStringBuilder(fixture.Database.CreateConnectionString("change58_failure"));
        if (missingDatabase)
        {
            settings.Database = $"change58_absent_{Guid.NewGuid():N}";
        }
        else
        {
            settings.SearchPath = "pg_catalog";
        }
        await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
        var repository = new ImmichDbRepository(source, NullLogger<ImmichDbRepository>.Instance);
        PostgresException failure = await Assert.ThrowsExactlyAsync<PostgresException>(
            () => repository.HasUnprocessedAssetsAsync(bound.Token));
        Assert.AreEqual(sqlState, failure.SqlState);
        Assert.IsFalse(await fixture.Repository.HasUnprocessedAssetsAsync(bound.Token));
    }

    [TestMethod]
    public async Task ExistingDataSourceTimeoutPropagatesAndReleasesTheQueryConnection()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await ScheduledExistencePostgresFixture.CreateAsync(bound.Token);
        var settings = new NpgsqlConnectionStringBuilder(fixture.Database.CreateConnectionString("change58_timeout"))
        {
            CommandTimeout = 1,
            MaxPoolSize = 1
        };
        await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
        var repository = new ImmichDbRepository(source, NullLogger<ImmichDbRepository>.Instance);
        await using var owner = await fixture.DataSource.OpenConnectionAsync(bound.Token);
        await using (var transaction = await owner.BeginTransactionAsync(bound.Token))
        {
            try
            {
                await using var acquire = new NpgsqlCommand("LOCK TABLE asset IN ACCESS EXCLUSIVE MODE", owner, transaction);
                await acquire.ExecuteNonQueryAsync(bound.Token); // The server has granted the conflicting lock.
                NpgsqlException failure = await Assert.ThrowsAsync<NpgsqlException>(
                    () => repository.HasUnprocessedAssetsAsync(bound.Token));
                Assert.IsInstanceOfType<TimeoutException>(failure.InnerException);
                Assert.IsFalse(bound.IsCancellationRequested, "This was the data-source command timeout, not the outer test deadline.");
            }
            finally
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }
        // A one-slot pool makes a leaked query connection observable on this fresh read.
        Assert.IsFalse(await repository.HasUnprocessedAssetsAsync(bound.Token));
    }
}
