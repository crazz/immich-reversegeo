using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Composition;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
public sealed class HeavyCompositionPathTests
{
    [TestMethod]
    public async Task OverturePlaces_UsesBundledAirportRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "literal-data-root");
        try
        {
            var bundledDefaults = Path.Combine(root, "bundled-data", "defaults");
            Directory.CreateDirectory(bundledDefaults);
            Directory.CreateDirectory(Path.Combine(data, "defaults"));
            File.Copy(BundledAirportFixturePath(), Path.Combine(bundledDefaults, "overture-airports.db"));
            File.WriteAllText(Path.Combine(data, "defaults", "overture-airports.db"), "bundled-root-decoy");

            using var provider = BuildWorkerProvider(root, data);
            var diagnostics = await provider.GetRequiredService<OverturePlacesService>()
                .FindNearestInfrastructureWithDiagnosticsAsync(47.460972, 8.553525, "CHE");

            Assert.IsNull(diagnostics.Error, "bundled-airport-error");
            Assert.IsNotNull(diagnostics.BestMatch, "bundled-airport-match");
            Assert.AreEqual("Zürich Airport", diagnostics.BestMatch.Name, "bundled-airport-name");
            Assert.AreEqual("airport", diagnostics.BestMatch.SubType, "bundled-airport-subtype");
            Assert.AreEqual("international_airport", diagnostics.BestMatch.ClassName, "bundled-airport-class");
            Assert.IsTrue(diagnostics.BestMatch.BoundingBoxContainsPoint, "bundled-airport-bbox");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [TestMethod]
    public async Task OvertureDivisions_UsesDataRootForCachedDivisionAreas()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "literal-data-root");
        try
        {
            CreateDivisionDatabase(Path.Combine(data, "overture-divisions", "CHE.db"));
            var bundledDivisionsDirectory = Path.Combine(root, "bundled-data", "overture-divisions");
            Directory.CreateDirectory(bundledDivisionsDirectory);
            File.WriteAllText(Path.Combine(bundledDivisionsDirectory, "CHE.db"), "bundled-data-root-decoy");

            using var provider = BuildWorkerProvider(root, data);
            var diagnostics = await provider.GetRequiredService<OvertureDivisionsService>()
                .FindContainingDivisionAreasAsync(47, 8, "CH", "CHE");

            Assert.IsNull(diagnostics.Error, "cached-division-error");
            Assert.AreEqual("test-release", diagnostics.Release, "cached-division-release");
            Assert.IsNotNull(diagnostics.BestMatch, "cached-division-match");
            Assert.AreEqual("division", diagnostics.BestMatch.Id, "cached-division-id");
            Assert.AreEqual("Test Division", diagnostics.BestMatch.Name, "cached-division-name");
            Assert.AreEqual("CH", diagnostics.BestMatch.Country, "cached-division-country");
            Assert.IsTrue(diagnostics.BestMatch.GeometryContainsPoint, "cached-division-geometry");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [TestMethod]
    public void OvertureDivisionCache_DeletesOnlyConfiguredDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "literal-data-root");
        var path = Path.Combine(data, "overture-divisions", "CHE.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "overture-cache-defect");
            using var provider = BuildWorkerProvider(root, data);
            provider.GetRequiredService<OvertureDivisionCacheService>().DeleteFile("CHE");
            Assert.IsFalse(File.Exists(path), "overture-cache-configured-data-root");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [TestMethod]
    public void GadmDivisionCache_DeletesOnlyConfiguredDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "literal-data-root");
        var path = Path.Combine(data, "gadm-divisions", "CHE.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "gadm-cache-defect");
            using var provider = BuildWorkerProvider(root, data);
            provider.GetRequiredService<GadmDivisionCacheService>().DeleteFile("CHE");
            Assert.IsFalse(File.Exists(path), "gadm-cache-configured-data-root");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [TestMethod]
    public async Task GadmDivisions_UsesConfiguredDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "literal-data-root");
        var path = Path.Combine(data, "gadm-divisions", "CHE.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not a sqlite database");
            using var provider = BuildWorkerProvider(root, data);
            var diagnostics = await provider.GetRequiredService<GadmDivisionsService>()
                .FindContainingDivisionAreasAsync(47, 8, "CHE");
            Assert.IsNotNull(diagnostics.Error, "gadm-service-configured-data-root");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    private static ServiceProvider BuildWorkerProvider(string root, string data)
    {
        var services = new ServiceCollection();
        services.AddInternalWorkerComposition(ApplicationCompositionContext.Create(
            CompositionEnvironment.Development, root, data, Path.Combine(root, "config")));
        return services.BuildServiceProvider();
    }

    private static void CreateDivisionDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE division_area (
                id TEXT, name TEXT, subtype TEXT, class_name TEXT, admin_level INTEGER,
                country TEXT, is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO _meta VALUES ('release', 'test-release');
            INSERT INTO division_area VALUES (
                'division', 'Test Division', 'region', 'land', 4, 'CH', 1, 0,
                $geometry, 7, 46, 9, 48);
            """;
        command.Parameters.AddWithValue("$geometry", ValidPolygonWkb());
        command.ExecuteNonQuery();
    }

    private static byte[] ValidPolygonWkb() =>
    [
        1, 3, 0, 0, 0, 1, 0, 0, 0, 5, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 28, 64, 0, 0, 0, 0, 0, 0, 71, 64,
        0, 0, 0, 0, 0, 0, 34, 64, 0, 0, 0, 0, 0, 0, 71, 64,
        0, 0, 0, 0, 0, 0, 34, 64, 0, 0, 0, 0, 0, 0, 72, 64,
        0, 0, 0, 0, 0, 0, 28, 64, 0, 0, 0, 0, 0, 0, 72, 64,
        0, 0, 0, 0, 0, 0, 28, 64, 0, 0, 0, 0, 0, 0, 71, 64
    ];

    private static string BundledAirportFixturePath()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/ImmichReverseGeo.Web/bundled-data/defaults/overture-airports.db"));
        Assert.IsTrue(File.Exists(path), "bundled-airport-fixture-source");
        return path;
    }

    private static void DeleteFixture(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
