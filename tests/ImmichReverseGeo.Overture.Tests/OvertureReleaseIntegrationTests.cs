using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
[TestCategory("Integration")]
public class OvertureReleaseIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("CZ", 50.0875, 14.4213)]
    [DataRow("SK", 48.1486, 17.1077)]
    [DataRow("VN", 21.0285, 105.8542)]
    public async Task CurrentRelease_LiveDivisionHierarchyResolvesAdministrativeAreas(string country, double lat, double lon)
    {
        var root = Path.GetTempPath();
        var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
        var divisions = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, _ => null);
        var diagnostics = await divisions.FindContainingDivisionAreasAsync(lat, lon, country);
        Assert.IsNull(diagnostics.Error, diagnostics.Error);
        Assert.IsTrue(diagnostics.Candidates.Any(c => c.GeometryContainsPoint && OvertureDivisionsLogic.ParseHierarchy(c).Count > 0));
        var result = OvertureDivisionsLogic.SelectAdministrativeAreas(diagnostics.Candidates);
        Assert.IsNotNull(result.City);
        Assert.IsNotNull(result.State);
        TestContext.WriteLine($"{country}: {diagnostics.Release}; city={result.City}; state={result.State}");
    }

    [TestMethod]
    public async Task CurrentRelease_CountryCacheExportsAndReadsHierarchy()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, root, _ => "CH");
            await cache.EnsureDataAsync("CHE");
            Assert.IsTrue(cache.HasData("CHE"));
            using (var conn = new SqliteConnection($"Data Source={Path.Combine(root, "overture-divisions", "CHE.db")};Pooling=false"))
            {
                conn.Open();
                using var query = conn.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM division_area WHERE division_id IS NOT NULL AND hierarchy_json IS NOT NULL";
                Assert.IsTrue(Convert.ToInt64(query.ExecuteScalar()) > 0);
            }

            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
            var divisions = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, _ => "CHE");
            var diagnostics = await divisions.FindContainingDivisionAreasAsync(47.3769, 8.5417, "CH", "CHE");
            Assert.IsNull(diagnostics.Error, diagnostics.Error);
            Assert.IsTrue(diagnostics.Candidates.Any(c => OvertureDivisionsLogic.ParseHierarchy(c).Count > 0));
            var result = await divisions.ResolveAdministrativeGeoAsync(47.3769, 8.5417, "CH", "CHE");
            Assert.IsNotNull(result);
            Assert.IsNotNull(result.City);
            Assert.IsNotNull(result.State);
            TestContext.WriteLine($"CHE cache: {diagnostics.Release}; city={result.City}; state={result.State}; rows={cache.GetStatus()["CHE"].RowCount}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
