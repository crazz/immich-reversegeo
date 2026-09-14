using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Gadm.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ImmichReverseGeo.WorkerProcessFixture;

// Only the fixture's external data boundaries differ from production. The actual
// InternalWorkerHost owns dispatch, execution, protocol, cancellation and finality.
internal static class MemorySoakHostFixture
{
    internal static async Task<int> RunAsync(FixtureOptions options, InternalWorkerProtocolVersion version)
    {
        string root = options.ResourceRoot;
        await PrepareInputsAsync(root).ConfigureAwait(false);
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var context = ApplicationCompositionContext.Create(CompositionEnvironment.Development, root, root, root);
        var builder = InternalWorkerHost.CreateBuilder(context, outcomes, version);
        var data = new LocalData(root, options.Scenario == FixtureScenario.RealMemorySoakNetworkProbe);
        builder.Services.RemoveAll<IProcessingAssetRepository>();
        builder.Services.AddSingleton<IProcessingAssetRepository>(data);
        builder.Services.RemoveAll<IProcessingRunConfiguration>();
        builder.Services.AddSingleton<IProcessingRunConfiguration>(data);
        builder.Services.RemoveAll<IProcessingAdministrativeResolver>();
        builder.Services.AddSingleton<IProcessingAdministrativeResolver>(data);
        builder.Services.RemoveAll<IProcessingInfrastructureLookup>();
        builder.Services.AddSingleton<IProcessingInfrastructureLookup>(data);
        builder.Services.RemoveAll<ICoordinateLookupSources>();
        builder.Services.AddSingleton<ICoordinateLookupSources>(data);
        builder.Services.RemoveAll<IProcessingRunLock>();
        builder.Services.AddSingleton<IProcessingRunLock>(new ProductionProcessingHostFixture.HermeticRunLock(root));
        builder.Services.RemoveAll<NpgsqlDataSource>();
        builder.Services.AddSingleton<NpgsqlDataSource>(_ => data.Forbid<NpgsqlDataSource>());
        builder.Services.RemoveAll<OvertureDivisionCacheService>();
        builder.Services.AddSingleton<OvertureDivisionCacheService>(_ => data.Forbid<OvertureDivisionCacheService>());
        builder.Services.RemoveAll<GadmDivisionCacheService>();
        builder.Services.AddSingleton<GadmDivisionCacheService>(_ => data.Forbid<GadmDivisionCacheService>());
        return await InternalWorkerHost.RunHostAsync(builder.Build(), outcomes).ConfigureAwait(false);
    }

    private static async Task PrepareInputsAsync(string root)
    {
        string input = Path.Combine(root, "soak-input");
        Directory.CreateDirectory(input);
        string assets = Path.Combine(input, "assets.db");
        string geodata = Path.Combine(input, "gadm.db");
        if (!File.Exists(assets))
        {
            using var connection = Open(assets);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE assets (id TEXT PRIMARY KEY, latitude REAL, longitude REAL, country TEXT, state TEXT, city TEXT)";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO assets VALUES ($id, 47, 8, NULL, NULL, NULL)";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            for (int index = 1; index <= 16; index++)
            {
                id.Value = new Guid(index, 0, 0, new byte[8]).ToString("D");
                command.ExecuteNonQuery();
            }
        }
        if (!File.Exists(geodata))
        {
            string source = Path.Combine(input, "source.gpkg");
            await ProductionCacheHostFixture.CreateSourceAsync(root, source, CancellationToken.None).ConfigureAwait(false);
            GadmCacheExporter.ExportGeoPackageToSqlite(source, geodata, "CHE", CancellationToken.None);
        }
        Directory.CreateDirectory(Path.Combine(root, "gadm-divisions"));
        File.Copy(geodata, Path.Combine(root, "gadm-divisions", "CHE.db"));
        File.Copy(assets, Path.Combine(root, "assets-result.db"));
        File.WriteAllText(Path.Combine(root, "soak-input-ready.marker"), "local-immutable-inputs");
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private sealed class LocalData(string root, bool networkProbe) : IProcessingAssetRepository, IProcessingRunConfiguration,
        IProcessingAdministrativeResolver, IProcessingInfrastructureLookup, ICoordinateLookupSources
    {
        private readonly GadmDivisionsService _gadm = new(NullLogger<GadmDivisionsService>.Instance, root);

        internal T Forbid<T>()
        {
            File.WriteAllText(Path.Combine(root, "soak-network-forbidden.marker"), "forbidden-external-boundary");
            throw new InvalidOperationException("soak/network-forbidden");
        }

        public Task<AppConfig> GetConfigAsync() => Task.FromResult(new AppConfig
        {
            Processing = new ProcessingConfig
            {
                BatchDelayMs = 0, BatchSize = 8, MaxDegreeOfParallelism = 1,
                UseAirportInfrastructure = false, UseGadmAdministrativeAreas = true
            }
        });

        public Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Open(Path.Combine(root, "assets-result.db"));
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM assets WHERE country IS NULL";
            return Task.FromResult((long)command.ExecuteScalar()!);
        }

        public Task<List<AssetRecord>> GetUnprocessedBatchAsync(AssetCursor cursor, int batchSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Open(Path.Combine(root, "assets-result.db"));
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, latitude, longitude FROM assets WHERE country IS NULL AND id > $cursor ORDER BY id LIMIT $limit";
            command.Parameters.AddWithValue("$cursor", cursor.Id.ToString("D"));
            command.Parameters.AddWithValue("$limit", batchSize);
            using var reader = command.ExecuteReader();
            var batch = new List<AssetRecord>();
            while (reader.Read())
            {
                batch.Add(new(Guid.Parse(reader.GetString(0)), reader.GetDouble(1), reader.GetDouble(2), DateTime.UnixEpoch));
            }
            return Task.FromResult(batch);
        }

        public Task WriteLocationAsync(Guid assetId, GeoResult result, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Open(Path.Combine(root, "assets-result.db"));
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE assets SET country=$country, state=$state, city=$city WHERE id=$id";
            command.Parameters.AddWithValue("$country", (object?)result.Country ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", (object?)result.State ?? DBNull.Value);
            command.Parameters.AddWithValue("$city", (object?)result.City ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", assetId.ToString("D"));
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("soak/asset-write-mismatch");
            }
            return Task.CompletedTask;
        }

        public async Task<AdministrativeAreaResolution?> ResolveAsync(double latitude, double longitude,
            ProcessingConfig config, IProcessingRunEventSession session, CancellationToken cancellationToken = default)
        {
            var result = await _gadm.FindContainingDivisionAreasAsync(latitude, longitude, "CHE", cancellationToken).ConfigureAwait(false);
            if (result.BestMatch is null || result.Error is not null)
            {
                throw new InvalidOperationException("soak/local-geodata-no-match");
            }
            File.WriteAllText(Path.Combine(root, "soak-geodata-queried.marker"), "actual-gadm-query");
            return new("CHE", "CH", "Switzerland", new("Switzerland", result.BestMatch.Name, result.BestMatch.Name), null, null);
        }

        public Task<OvertureInfrastructureLookupDiagnostics> FindNearestInfrastructureAsync(double latitude,
            double longitude, string? iso3, CancellationToken cancellationToken = default) => Forbid<Task<OvertureInfrastructureLookupDiagnostics>>();

        public Task<BundledCountryLookupResult> FindCountryAsync(double latitude, double longitude, CancellationToken cancellationToken) =>
            networkProbe ? Forbid<Task<BundledCountryLookupResult>>()
                : Task.FromResult(BundledCountryLookupResult.Matched("CHE", "Switzerland", "CH", "local-fixture-country"));
        public CityResolverProfile ResolveCityProfile(CoordinateLookupCityResolverOverrides overrides, string? iso3) =>
            new CityResolverProfileCatalog().GetProfile(CoordinateLookupCityProfileConversions.ToConfig(overrides), iso3);
        public IReadOnlyList<string> ExpandGadmCandidateCodes(string iso3) => ["CHE"];
        public (Task Task, OvertureDivisionEnsureResult Result) GetOrStartOvertureCache(string iso3, CancellationToken cancellationToken) =>
            (Task.CompletedTask, OvertureDivisionEnsureResult.AlreadyReady);
        public bool HasOvertureCache(string iso3) => false;
        public Task<OvertureDivisionLookupDiagnostics> FindOvertureDivisionsAsync(double latitude, double longitude,
            string alpha2, string iso3, CancellationToken cancellationToken) => Forbid<Task<OvertureDivisionLookupDiagnostics>>();
        public (Task Task, GadmDivisionEnsureResult Result) GetOrStartGadmCache(string iso3, CancellationToken cancellationToken) =>
            (Task.CompletedTask, GadmDivisionEnsureResult.AlreadyReady);
        public bool HasGadmCache(string iso3) => File.Exists(Path.Combine(root, "gadm-divisions", "CHE.db"));
        public async Task<GadmDivisionLookupDiagnostics> FindGadmDivisionsAsync(double latitude, double longitude,
            IReadOnlyList<string> iso3Codes, CancellationToken cancellationToken)
        {
            var result = await _gadm.FindContainingDivisionAreasAsync(latitude, longitude, "CHE", cancellationToken).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(root, "soak-geodata-queried.marker"), "actual-gadm-query");
            return result;
        }
        public Task<OvertureInfrastructureLookupDiagnostics> FindAirportAsync(double latitude, double longitude,
            string iso3, CancellationToken cancellationToken) => Forbid<Task<OvertureInfrastructureLookupDiagnostics>>();
        public Task<OvertureLookupDiagnostics> FindPlacesAsync(double latitude, double longitude,
            string alpha2, CancellationToken cancellationToken) => Forbid<Task<OvertureLookupDiagnostics>>();
    }
}
