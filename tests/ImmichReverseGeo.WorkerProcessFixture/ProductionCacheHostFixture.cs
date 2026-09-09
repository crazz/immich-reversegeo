using System.Text.Json;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal static class ProductionCacheHostFixture
{
    internal static async Task<int> RunAsync(
        FixtureOptions options,
        InternalWorkerProtocolVersion protocolVersion)
    {
        if (protocolVersion != InternalWorkerProtocolVersion.V2)
        {
            throw new FixtureInputException("Production CacheMutation fixtures require protocol v2.");
        }

        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        ApplicationCompositionContext context = ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            options.ResourceRoot,
            options.ResourceRoot,
            options.ResourceRoot);
        var builder = InternalWorkerHost.CreateBuilder(context, outcomes, protocolVersion);

        if (options.Scenario == FixtureScenario.RealCacheOutputFailure)
        {
            ProductionCoordinateHostFixture.ConfigureManagedOutputFailure(
                builder.Services,
                options.ResourceRoot,
                WorkerJobKind.CacheMutation,
                "cache-mutation-job-started");
        }

        if (options.Scenario == FixtureScenario.RealCacheOvertureSuccess)
        {
            builder.Services.RemoveAll<OvertureDivisionCacheService>();
            builder.Services.AddSingleton(sp => new OvertureDivisionCacheService(
                sp.GetRequiredService<ILogger<OvertureDivisionCacheService>>(),
                options.ResourceRoot,
                static iso3 => iso3 == "CHE" ? "CH" : null,
                new OvertureDivisionCacheTestHooks
                {
                    ExportOperation = (destination, alpha2, cancellationToken) =>
                        CreateOvertureCache(
                            options.ResourceRoot,
                            destination,
                            alpha2,
                            cancellationToken)
                }));
        }

        builder.Services.RemoveAll<GadmDivisionCacheService>();
        builder.Services.AddSingleton(sp => new GadmDivisionCacheService(
            sp.GetRequiredService<ILogger<GadmDivisionCacheService>>(),
            options.ResourceRoot,
            new GadmDivisionCacheTestHooks
            {
                DownloadOperation = options.Scenario == FixtureScenario.RealCacheFailure
                    ? (_, _, _) => FailSourceAsync(options.ResourceRoot)
                    : options.Scenario == FixtureScenario.RealCacheInfrastructureFailure
                        ? (_, _, _) => FailInfrastructureAsync(options.ResourceRoot)
                    : (_, destination, cancellationToken) =>
                        CreateSourceAsync(options.ResourceRoot, destination, cancellationToken),
                BeforePublication = options.Scenario == FixtureScenario.RealCacheUnresponsive
                    ? _ => HoldBeforePublicationAsync(options.ResourceRoot)
                    : options.Scenario == FixtureScenario.RealCacheCancellation
                        ? cancellationToken =>
                            WaitForCancellationBeforePublicationAsync(
                                options.ResourceRoot,
                                cancellationToken)
                    : null
            }));

        return await InternalWorkerHost.RunHostAsync(
            builder.Build(),
            outcomes).ConfigureAwait(false);
    }

    private static long CreateOvertureCache(
        string resourceRoot,
        string destination,
        string alpha2,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.WriteAllText(
            Path.Combine(resourceRoot, "remote-export-substituted.marker"),
            "checked-in-local-source");
        string fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "overture-che-tiny.json");
        TinyOvertureSource source = JsonSerializer.Deserialize<TinyOvertureSource>(
            File.ReadAllText(fixturePath))
            ?? throw new FixtureInputException("The checked-in Overture source fixture is empty.");
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = new SqliteConnection($"Data Source={destination};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE division_area (
                id TEXT, name TEXT, subtype TEXT, class_name TEXT, admin_level INTEGER,
                country TEXT, is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO division_area
                (id, name, subtype, class_name, admin_level, country, is_land, is_territorial,
                 geom_wkb, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax)
            VALUES ($id, $name, 'region', 'land', 4, $country, 1, 0,
                    $geometry, $xmin, $ymin, $xmax, $ymax);
            INSERT INTO _meta VALUES ('downloadedAt', '2026-09-09T00:00:00Z');
            INSERT INTO _meta VALUES ('release', $release);
            INSERT INTO _meta VALUES ('country', $country);
            """;
        command.Parameters.AddWithValue("$id", source.Id);
        command.Parameters.AddWithValue("$name", source.Name);
        command.Parameters.AddWithValue("$country", alpha2);
        command.Parameters.AddWithValue("$geometry", CreatePolygonWkb(source));
        command.Parameters.AddWithValue("$xmin", source.MinLongitude);
        command.Parameters.AddWithValue("$ymin", source.MinLatitude);
        command.Parameters.AddWithValue("$xmax", source.MaxLongitude);
        command.Parameters.AddWithValue("$ymax", source.MaxLatitude);
        command.Parameters.AddWithValue("$release", source.Release);
        command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        return 1;
    }

    private static byte[] CreatePolygonWkb(TinyOvertureSource source)
    {
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var polygon = factory.CreatePolygon(
        [
            new Coordinate(source.MinLongitude, source.MinLatitude),
            new Coordinate(source.MaxLongitude, source.MinLatitude),
            new Coordinate(source.MaxLongitude, source.MaxLatitude),
            new Coordinate(source.MinLongitude, source.MaxLatitude),
            new Coordinate(source.MinLongitude, source.MinLatitude)
        ]);
        return new WKBWriter().Write(polygon);
    }

    private static async Task CreateSourceAsync(
        string resourceRoot,
        string destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(
            Path.Combine(resourceRoot, "network-substituted.marker"),
            "checked-in-local-source",
            cancellationToken).ConfigureAwait(false);

        string fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "gadm-che-tiny.json");
        await using FileStream stream = File.OpenRead(fixturePath);
        TinyGadmSource source = (await JsonSerializer.DeserializeAsync<TinyGadmSource>(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false))
            ?? throw new FixtureInputException("The checked-in GADM source fixture is empty.");
        CreateGeoPackage(destination, source, cancellationToken);
    }

    private static void CreateGeoPackage(
        string path,
        TinyGadmSource source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();
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
        cancellationToken.ThrowIfCancellationRequested();

        byte[] wkb = new WKBWriter().Write(new Point(source.Longitude, source.Latitude));
        var geometry = new byte[8 + wkb.Length];
        geometry[0] = (byte)'G';
        geometry[1] = (byte)'P';
        geometry[3] = 1;
        Buffer.BlockCopy(wkb, 0, geometry, 8, wkb.Length);

        command.CommandText = """
            INSERT INTO gadm41_CHE_0 VALUES ($countryId, $countryName, $geometry);
            INSERT INTO gadm41_CHE_1 VALUES ($regionId, $regionName, $geometry);
            """;
        command.Parameters.AddWithValue("$countryId", source.CountryId);
        command.Parameters.AddWithValue("$countryName", source.CountryName);
        command.Parameters.AddWithValue("$regionId", source.RegionId);
        command.Parameters.AddWithValue("$regionName", source.RegionName);
        command.Parameters.AddWithValue("$geometry", geometry);
        command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task HoldBeforePublicationAsync(string resourceRoot)
    {
        await File.WriteAllTextAsync(
            Path.Combine(resourceRoot, "cache-before-publication.marker"),
            "candidate-validated").ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task WaitForCancellationBeforePublicationAsync(
        string resourceRoot,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            Path.Combine(resourceRoot, "cache-cancellation-ready.marker"),
            "candidate-validated",
            CancellationToken.None).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    private static async Task FailSourceAsync(string resourceRoot)
    {
        await File.WriteAllTextAsync(
            Path.Combine(resourceRoot, "cache-source-failure.marker"),
            "controlled-source-failure").ConfigureAwait(false);
        throw new IOException("Controlled cache source failure.");
    }

    private static Task FailInfrastructureAsync(string resourceRoot)
    {
        File.WriteAllText(
            Path.Combine(resourceRoot, "cache-infrastructure-fault.marker"),
            "controlled-out-of-memory");
        throw new OutOfMemoryException("Controlled cache infrastructure failure.");
    }

    private sealed record TinyGadmSource(
        string CountryId,
        string CountryName,
        string RegionId,
        string RegionName,
        double Longitude,
        double Latitude);

    private sealed record TinyOvertureSource(
        string Id,
        string Name,
        string Release,
        double MinLongitude,
        double MinLatitude,
        double MaxLongitude,
        double MaxLatitude);
}
