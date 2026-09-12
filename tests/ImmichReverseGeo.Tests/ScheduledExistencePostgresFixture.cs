using System.Reflection;
using ImmichReverseGeo.Tests.CrossProcessRunExclusion;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ImmichReverseGeo.Tests;

internal sealed class ScheduledExistencePostgresFixture(
    PostgresIntegrationDatabase database, NpgsqlDataSource dataSource) : IAsyncDisposable
{
    internal PostgresIntegrationDatabase Database { get; } = database;
    internal NpgsqlDataSource DataSource { get; } = dataSource;
    internal ImmichDbRepository Repository { get; } = new(dataSource, NullLogger<ImmichDbRepository>.Instance);

    // Read the exact production statement without exporting SQL through the repository API.
    internal static string ExistenceSql => (string)typeof(ImmichDbRepository)
        .GetField("UnprocessedAssetsExistenceSql", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;

    internal static async Task<ScheduledExistencePostgresFixture> CreateAsync(CancellationToken token)
    {
        PostgresIntegrationSettings settings = PostgresIntegrationSettings.ReadEnvironment() switch
        {
            PostgresIntegrationSettingsAvailable available => available.Settings,
            PostgresIntegrationSettingsFailure failure => throw new AssertFailedException(failure.Reason),
            _ => throw new AssertFailedException("PostgreSQL integration settings are unavailable.")
        };
        PostgresIntegrationDatabase database = await settings.CreateCaseAsync(Guid.NewGuid(), token);
        try
        {
            return new(database, NpgsqlDataSource.Create(database.CreateConnectionString($"change58_{Guid.NewGuid():N}")));
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    internal static async Task<string> ReadRowsAsync(NpgsqlConnection connection, CancellationToken token)
    {
        // Separate sorted tables include missing EXIF rows; an inner-join snapshot would hide them.
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object(
                'asset', (SELECT COALESCE(jsonb_agg(to_jsonb(a) ORDER BY a.id), '[]'::jsonb) FROM asset a),
                'asset_exif', (SELECT COALESCE(jsonb_agg(to_jsonb(e) ORDER BY e."assetId"), '[]'::jsonb) FROM asset_exif e)
            )::text
            """, connection);
        return (string)(await command.ExecuteScalarAsync(token))!;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DataSource.DisposeAsync();
        }
        finally
        {
            await Database.DisposeAsync();
        }
    }
}
