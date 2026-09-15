using ImmichReverseGeo.Core.Countries;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Tests;

public partial class AdministrativeAreaResolverTerritoryTests
{
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 1)]
    [DataRow(false, 2)]
    [DataRow(true, 0)]
    [DataRow(true, 1)]
    [DataRow(true, 2)]
    public async Task CompletePrimary_DoesNotReadBrokenSecondary_WithAnyReporter(bool preferGadm, int reporting)
    {
        var root = CreateDemandFixture();
        try
        {
            var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            var unusedSource = preferGadm ? "overture" : "gadm";
            var unusedPath = Path.Combine(root, unusedSource + "-divisions", territory.Alpha3 + ".db");
            await File.WriteAllTextAsync(unusedPath, "Intentionally unreadable secondary cache");
            var catalog = CountryIdentityCatalog.Load(GetIdentityCatalogPath());
            var resolver = CreateResolver(root, catalog);
            var config = new ProcessingConfig
            {
                UseGadmAdministrativeAreas = true,
                PreferGadmAdministrativeAreas = preferGadm,
                UseGadmTerritoryFallbacks = false
            };
            var reporter = new RecordingProcessingEventReporter();
            var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
            var session = reporting == 1
                ? await NoOpProcessingEventReporter.Instance.OpenRunAsync(request, DateTimeOffset.UtcNow)
                : await reporter.OpenRunAsync(request, DateTimeOffset.UtcNow);
            await session.DetermineEligibilityAsync(1);
            var result = reporting == 0
                ? await resolver.ResolveAsync(territory.Latitude, territory.Longitude, config)
                : await resolver.ResolveAsync(territory.Latitude, territory.Longitude, config, session);

            Assert.IsNotNull(result);
            Assert.AreEqual(preferGadm ? "GADM City" : "Overture City", result.GeoResult.City);
            Assert.AreEqual(preferGadm ? "GADM State" : "Overture State", result.GeoResult.State);
            Assert.AreEqual(territory.DisplayName, result.GeoResult.Country);
            if (preferGadm)
            {
                Assert.IsNull(result.OvertureResult);
            }
            else
            {
                Assert.IsNull(result.GadmResult);
            }

            var messages = reporter.EventsFor(request).OfType<LogEmitted>().ToArray();
            var unusedLabel = preferGadm ? "Overture administrative" : "GADM";
            Assert.IsFalse(messages.Any(message => message.Message.Contains(unusedLabel, StringComparison.Ordinal)));
            Assert.AreEqual("Intentionally unreadable secondary cache", await File.ReadAllTextAsync(unusedPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, "city")]
    [DataRow(false, "state")]
    [DataRow(false, "both")]
    [DataRow(true, "city")]
    [DataRow(true, "both")]
    public async Task IncompletePrimary_FillsOnlyMissingFields(bool preferGadm, string missing)
    {
        var root = CreateDemandFixture();
        try
        {
            var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            var source = preferGadm ? "gadm" : "overture";
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, source + "-divisions", territory.Alpha3 + ".db")};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                // Keep source-valid rows, but exclude the missing field's polygons from this point.
                command.CommandText = $"UPDATE {(preferGadm ? "gadm_area" : "division_area")} SET bbox_xmin=1000, bbox_xmax=1001 WHERE $missing='both' OR id=$id";
                command.Parameters.AddWithValue("$missing", missing);
                command.Parameters.AddWithValue("$id", source + "-" + missing);
                command.ExecuteNonQuery();
            }

            var result = await CreateResolver(root, CountryIdentityCatalog.Load(GetIdentityCatalogPath()))
                .ResolveAsync(territory.Latitude, territory.Longitude, new ProcessingConfig
                {
                    UseGadmAdministrativeAreas = true,
                    PreferGadmAdministrativeAreas = preferGadm,
                    UseGadmTerritoryFallbacks = false
                });

            Assert.IsNotNull(result);
            if (preferGadm)
            {
                Assert.IsNotNull(result.OvertureResult);
            }
            else
            {
                Assert.IsNotNull(result.GadmResult);
            }
            var primary = preferGadm ? "GADM" : "Overture";
            var secondary = preferGadm ? "Overture" : "GADM";
            Assert.AreEqual((missing is "city" or "both" ? secondary : primary) + " City", result.GeoResult.City);
            Assert.AreEqual((missing is "state" or "both" ? secondary : primary) + " State", result.GeoResult.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DisabledGadm_IgnoresStoredPreferenceAndBrokenGadmCache()
    {
        var root = CreateDemandFixture();
        try
        {
            var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            var path = Path.Combine(root, "gadm-divisions", territory.Alpha3 + ".db");
            await File.WriteAllTextAsync(path, "broken");
            var result = await CreateResolver(root, CountryIdentityCatalog.Load(GetIdentityCatalogPath()))
                .ResolveAsync(territory.Latitude, territory.Longitude, new ProcessingConfig
                {
                    UseGadmAdministrativeAreas = false,
                    PreferGadmAdministrativeAreas = true
                });
            Assert.IsNotNull(result);
            Assert.IsNull(result.GadmResult);
            Assert.AreEqual("Overture City", result.GeoResult.City);
            Assert.AreEqual("Overture State", result.GeoResult.State);
            Assert.AreEqual("broken", await File.ReadAllTextAsync(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateDemandFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        File.Copy(GetBundledArtifactPath(), Path.Combine(root, "defaults", "overture-country-divisions.db"));
        var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
        CreateDistinctResultCaches(root, territory.Alpha3, territory.Latitude, territory.Longitude);
        return root;
    }
}
