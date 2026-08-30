using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class AdministrativeAreaResolverTerritoryTests
{
    [TestMethod]
    public async Task MandatoryTerritories_ReachOwnOvertureAndGadmCacheStages()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        try
        {
            File.Copy(GetBundledArtifactPath(), Path.Combine(root, "defaults", "overture-country-divisions.db"));
            var catalog = CountryIdentityCatalog.Load(GetIdentityCatalogPath());
            foreach (var fixture in CountryResolutionFixtureCatalog.MandatoryTerritories.DistinctBy(fixture => fixture.Alpha2))
            {
                CreateReadyOvertureCache(root, fixture.Alpha3);
                CreateReadyGadmCache(root, fixture.Alpha3);
            }

            var resolver = CreateResolver(root, catalog);
            var config = new ProcessingConfig { UseGadmAdministrativeAreas = true, PreferGadmAdministrativeAreas = false, UseGadmTerritoryFallbacks = false };
            foreach (var fixture in CountryResolutionFixtureCatalog.MandatoryTerritories)
            {
                var progress = new RecordingProgress();
                var result = await resolver.ResolveAsync(fixture.Latitude, fixture.Longitude, config, progress);
                Assert.IsNotNull(result, fixture.Label);
                Assert.AreEqual(fixture.DisplayName, result.CountryName, fixture.Label);
                Assert.AreEqual(fixture.Alpha3, result.Iso3, fixture.Label);
                Assert.AreEqual(fixture.Alpha2, result.Alpha2, fixture.Label);
                Assert.IsTrue(progress.Messages.Any(message => message.Contains($"Overture administrative cache ready for {fixture.Alpha3}", StringComparison.Ordinal)), fixture.Label);
                Assert.IsTrue(progress.Messages.Any(message => message.Contains($"GADM administrative cache ready for {fixture.Alpha3}", StringComparison.Ordinal)), fixture.Label);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ResolveAsync_ForeignOvertureCacheOwnerCancellationIsSourceUnavailabilityForLiveWaiter()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        using var owner = new CancellationTokenSource();
        var sourceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            File.Copy(GetBundledArtifactPath(), Path.Combine(root, "defaults", "overture-country-divisions.db"));
            var catalog = CountryIdentityCatalog.Load(GetIdentityCatalogPath());
            var fixture = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            var storage = new StorageOptions(root, root);
            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
            var divisions = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, alpha2 => catalog.FindByAlpha2(alpha2)?.Alpha3);
            var cache = new OvertureDivisionCacheService(
                NullLogger<OvertureDivisionCacheService>.Instance,
                root,
                iso3 => catalog.FindByAlpha3(iso3)?.Alpha2,
                new OvertureDivisionCacheTestHooks
                {
                    SourceOperation = (_, ct) => WaitForOwnerCancellationAsync(ct, sourceEntered, releaseSource)
                });
            var resolver = new AdministrativeAreaResolverService(NullLogger<AdministrativeAreaResolverService>.Instance, new CityResolverProfileCatalogService(NullLogger<CityResolverProfileCatalogService>.Instance, storage), divisions, cache, new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, root), new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, root));
            var ownerTask = cache.GetOrStartDownload(fixture.Alpha3, owner.Token).Task;
            await sourceEntered.Task;
            var progress = new RecordingProgress();
            var resolution = resolver.ResolveAsync(fixture.Latitude, fixture.Longitude, new ProcessingConfig(), progress);
            await progress.CacheWaited.Task;
            owner.Cancel();
            releaseSource.TrySetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await ownerTask);
            var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolution);
            Assert.IsInstanceOfType<OperationCanceledException>(unavailable.InnerException);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ResolveAsync_CompletedSharedTaskFailureDoesNotStartReplacementTask()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        try
        {
            File.Copy(GetBundledArtifactPath(), Path.Combine(root, "defaults", "overture-country-divisions.db"));
            var catalog = CountryIdentityCatalog.Load(GetIdentityCatalogPath());
            var fixture = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            var storage = new StorageOptions(root, root);
            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
            var divisions = new OvertureDivisionsService(
                NullLogger<OvertureDivisionsService>.Instance,
                places,
                root,
                root,
                alpha2 => catalog.FindByAlpha2(alpha2)?.Alpha3);
            var sourceCalls = 0;
            var cache = new OvertureDivisionCacheService(
                NullLogger<OvertureDivisionCacheService>.Instance,
                root,
                iso3 => catalog.FindByAlpha3(iso3)?.Alpha2,
                new OvertureDivisionCacheTestHooks
                {
                    SourceOperation = (_, _) =>
                    {
                        sourceCalls++;
                        return Task.FromException(new OperationCanceledException("foreign owner cancellation"));
                    }
                });
            var resolver = new AdministrativeAreaResolverService(
                NullLogger<AdministrativeAreaResolverService>.Instance,
                new CityResolverProfileCatalogService(NullLogger<CityResolverProfileCatalogService>.Instance, storage),
                divisions,
                cache,
                new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, root),
                new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, root));

            var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                resolver.ResolveAsync(fixture.Latitude, fixture.Longitude, new ProcessingConfig()));

            Assert.IsInstanceOfType<OperationCanceledException>(unavailable.InnerException);
            Assert.AreEqual(1, sourceCalls);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForOwnerCancellationAsync(CancellationToken ct, TaskCompletionSource entered, TaskCompletionSource release)
    {
        entered.TrySetResult();
        await release.Task;
        ct.ThrowIfCancellationRequested();
    }

    private static AdministrativeAreaResolverService CreateResolver(string root, CountryIdentityCatalog catalog)
    {
        var storage = new StorageOptions(root, root);
        var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
        var divisions = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, alpha2 => catalog.FindByAlpha2(alpha2)?.Alpha3);
        var overtureCache = new OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, root, iso3 => catalog.FindByAlpha3(iso3)?.Alpha2);
        var gadmDivisions = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, root);
        var gadmCache = new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, root);
        var cityCatalog = new CityResolverProfileCatalogService(NullLogger<CityResolverProfileCatalogService>.Instance, storage);
        return new AdministrativeAreaResolverService(NullLogger<AdministrativeAreaResolverService>.Instance, cityCatalog, divisions, overtureCache, gadmDivisions, gadmCache);
    }

    private static void CreateReadyOvertureCache(string root, string iso3)
    {
        var directory = Path.Combine(root, "overture-divisions");
        Directory.CreateDirectory(directory);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, $"{iso3}.db")};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE division_area (id TEXT PRIMARY KEY, name TEXT NOT NULL, subtype TEXT NULL, class_name TEXT NULL, admin_level INTEGER NULL, country TEXT NULL, is_land INTEGER NOT NULL, is_territorial INTEGER NOT NULL, geom_wkb BLOB NULL, bbox_xmin REAL NULL, bbox_ymin REAL NULL, bbox_xmax REAL NULL, bbox_ymax REAL NULL);
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO division_area VALUES ('ready', 'Ready', 'region', 'land', 1, NULL, 1, 0, NULL, 1000, 1000, 1001, 1001);
            INSERT INTO _meta VALUES ('release', 'test');
            INSERT INTO _meta VALUES ('downloadedAt', '2026-08-19T00:00:00Z');
            """;
        command.ExecuteNonQuery();
    }

    private static void CreateReadyGadmCache(string root, string iso3)
    {
        var directory = Path.Combine(root, "gadm-divisions");
        Directory.CreateDirectory(directory);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, $"{iso3}.db")};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE gadm_area (id TEXT PRIMARY KEY, name TEXT NOT NULL, english_type TEXT NULL, local_type TEXT NULL, admin_level INTEGER NOT NULL, geom_wkb BLOB NULL, bbox_xmin REAL NOT NULL, bbox_ymin REAL NOT NULL, bbox_xmax REAL NOT NULL, bbox_ymax REAL NOT NULL);
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO gadm_area VALUES ('ready', 'Ready', 'State', 'State', 1, NULL, 1000, 1000, 1001, 1001);
            INSERT INTO _meta VALUES ('version', 'test');
            INSERT INTO _meta VALUES ('downloadedAt', '2026-08-19T00:00:00Z');
            """;
        command.ExecuteNonQuery();
    }

    private static string GetBundledArtifactPath()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "src", "ImmichReverseGeo.Web", "bundled-data", "defaults", "overture-country-divisions.db");
    }

    private static string GetIdentityCatalogPath()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "src", "ImmichReverseGeo.Web", "bundled-data", "iso3166.json");
    }

    private sealed class RecordingProgress : IAdministrativeAreaResolutionProgress
    {
        public List<string> Messages { get; } = [];
        public TaskCompletionSource CacheWaited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable BeginActivity(string activity)
        {
            Messages.Add(activity);
            if (activity.StartsWith("Waiting for Overture administrative cache", StringComparison.Ordinal)) { CacheWaited.TrySetResult(); }
            return new NoopDisposable();
        }
        public void Report(string message) { Messages.Add(message); }
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
