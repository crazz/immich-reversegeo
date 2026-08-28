using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class CountryCodeServiceTests
{
    [TestMethod]
    public void Iso3ToAlpha2_CHE_ReturnsCH()
    {
        var service = CountryCodeService.CreateForTest();
        Assert.AreEqual("CH", service.Iso3ToAlpha2("CHE"));
    }

    [TestMethod]
    public void Iso3ToAlpha2_Unknown_ReturnsNull()
    {
        var service = CountryCodeService.CreateForTest();
        Assert.IsNull(service.Iso3ToAlpha2("XYZ"));
    }

    [TestMethod]
    public void Alpha2ToIso3_VA_ReturnsVAT()
    {
        var service = CountryCodeService.CreateForTest();
        Assert.AreEqual("VAT", service.Alpha2ToIso3("VA"));
    }

    [TestMethod]
    public void MandatoryCountryIdentities_MapBothDirectionsAndCanonicalNames()
    {
        var service = CountryCodeService.CreateForTest();

        foreach (var fixture in CountryResolutionFixtureCatalog.MandatoryTerritories
                     .DistinctBy(fixture => fixture.Alpha2))
        {
            Assert.AreEqual(fixture.Alpha2, service.Iso3ToAlpha2(fixture.Alpha3), fixture.Label);
            Assert.AreEqual(fixture.Alpha3, service.Alpha2ToIso3(fixture.Alpha2), fixture.Label);

            var identity = service.FindByAlpha2(fixture.Alpha2);
            Assert.IsNotNull(identity, fixture.Label);
            Assert.AreEqual(fixture.DisplayName, identity.DisplayName, fixture.Label);
        }
    }

    [TestMethod]
    public void BundledCountryCodes_AreMappedOrExplicitlyNonIso()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dbPath = Path.Combine(repoRoot, "src", "ImmichReverseGeo.Web", "bundled-data", "defaults", "overture-country-divisions.db");
        var isoPath = Path.Combine(repoRoot, "src", "ImmichReverseGeo.Web", "bundled-data", "iso3166.json");

        Assert.IsTrue(File.Exists(dbPath), $"Bundled country divisions DB not found at {dbPath}");
        Assert.IsTrue(File.Exists(isoPath), $"ISO identity catalog not found at {isoPath}");

        var catalog = CountryIdentityCatalog.Load(isoPath);
        var missing = new List<string>();

        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT country FROM division_area WHERE country IS NOT NULL AND TRIM(country) <> ''";
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            var alpha2 = reader.GetString(0).ToUpperInvariant();
            if (catalog.FindByAlpha2(alpha2) is null && !catalog.IsExplicitlyNonIso(alpha2))
            {
                missing.Add(alpha2);
            }
        }

        CollectionAssert.AreEquivalent(Array.Empty<string>(), missing);
    }
}
