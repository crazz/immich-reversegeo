using DuckDB.NET.Data;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class NearbyPlacesTests
{
    [TestMethod]
    [DataRow("sports_and_recreation", false)]
    [DataRow("pipeline_transportation", false)]
    [DataRow("travel_and_transportation", false)]
    [DataRow("support_service", false)]
    [DataRow("port", true)]
    [DataRow("commercial_port", true)]
    [DataRow("art_museum", true)]
    [DataRow("train_station", true)]
    [DataRow("national_park", true)]
    [DataRow("baroque_church", true)]
    [DataRow("gas_station", false)]
    [DataRow("airport_gate", false)]
    [DataRow("museum_shop", false)]
    public void CategoryFilter_MatchesCompleteTermsAndPreservesCompoundCategories(string category, bool expected)
    {
        Assert.AreEqual(expected, OverturePlacesLogic.IsInterestingPhotoPlace(Place("test", category, 0.99, 10)));
    }

    [TestMethod]
    public void CategoryFilter_AlsoMatchesBasicCategoryAsASeparateIdentifier()
    {
        var place = Place("test", "unknown_type", 0.99, 10) with { BasicCategory = "art_museum" };
        Assert.IsTrue(OverturePlacesLogic.IsInterestingPhotoPlace(place));
        Assert.IsFalse(OverturePlacesLogic.IsInterestingPhotoPlace(place with { Category = "gas_station" }));
    }
    [TestMethod]
    public void Sources_HandleDatasetAndProviderResourceWithoutUsingPropertyNamesAsAttribution()
    {
        var sources = OverturePlacesLogic.ParseSources("""
            [null, {"dataset":null,"provider":"example","resource":"places"},
             {"dataset":"legacy"}, {"property":"names"}, {"dataset":42,"provider":"fallback"}]
            """);
        CollectionAssert.AreEqual(new[] { "example/places", "legacy", "fallback" }, sources.ToArray());
    }
    [TestMethod]
    public void Diagnostics_ShowIncludedAndExcludedReasonsAndOneNearestSuggestion()
    {
        var result = OverturePlacesLogic.BuildPlaceDiagnostics(
        [
            Place("far", "museum", 0.95, 500),
            Place("shop", "convenience_store", 0.99, 10),
            Place("near", "museum", 0.95, 100),
            Place("unknown", "museum", 0, 50) with { ConfidenceKnown = false },
            Place("closed", "museum", 0.95, 60) with { OperatingStatus = "permanently_closed" },
            Place("outside", "museum", 0.99, 2501)
        ], "release", "CH");

        Assert.AreEqual("near", result.BestMatch?.Id);
        Assert.AreEqual(5, result.Candidates.Count);
        Assert.AreEqual(2, result.Candidates.Count(c => c.Eligible));
        Assert.AreEqual(1, result.Candidates.Count(c => c.Selected));
        StringAssert.Contains(result.Candidates.Single(c => c.Id == "unknown").Decision, "unknown");
        StringAssert.Contains(result.Candidates.Single(c => c.Id == "closed").Decision, "not operating");
        Assert.IsFalse(result.Candidates.Single(c => c.Id == "unknown").ConfidenceKnown);
        Assert.IsTrue(result.Candidates.Where(c => !c.Eligible).All(c => c.Decision.StartsWith("Excluded:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Diagnostics_DistanceTiesAreStableAndEmptySuggestionsStillExplainExclusions()
    {
        var tie = OverturePlacesLogic.BuildPlaceDiagnostics([Place("b", "museum", 0.95, 100), Place("a", "museum", 0.95, 100)], null, null);
        Assert.AreEqual("a", tie.BestMatch?.Id);
        var empty = OverturePlacesLogic.BuildPlaceDiagnostics([Place("shop", "store", 0.95, 10)], null, null);
        Assert.IsNull(empty.BestMatch);
        Assert.IsFalse(empty.Candidates.Single().Eligible);
        Assert.IsFalse(empty.Candidates.Single().Selected);
    }

    [TestMethod]
    public void Query_FiltersAndOrdersBeforeLimitAndRetainsBoundedExcludedSample()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var duck = new DuckDBConnection("Data Source=:memory:");
            duck.Open();
            using var cmd = duck.CreateCommand();
            var path = Path.Combine(root, "places.parquet").Replace('\\', '/');
            // Struct coordinates let the full production query execute without downloading spatial extensions.
            cmd.CommandText = $$"""
                CREATE MACRO ST_X(p) AS p.x;
                CREATE MACRO ST_Y(p) AS p.y;
                CREATE TABLE fixture(id VARCHAR, category VARCHAR, confidence DOUBLE, lon DOUBLE, status VARCHAR, country VARCHAR);
                INSERT INTO fixture SELECT 'shop-' || i, 'convenience_store', 0.99, (i+1)*0.00001, 'open', 'CH' FROM range(200) r(i);
                INSERT INTO fixture SELECT 'museum-' || i, 'museum', 0.95, 0.005+i*0.00001, 'open', 'CH' FROM range(90) r(i);
                INSERT INTO fixture VALUES ('nearest', 'museum', 0.90, 0.003, ' OPEN ', 'CH');
                INSERT INTO fixture VALUES ('far', 'museum', 0.99, 0.1, 'open', 'CH');
                INSERT INTO fixture VALUES ('wrong-country', 'museum', 0.99, 0.000001, 'open', 'DE');
                INSERT INTO fixture VALUES ('unknown', 'museum', NULL, 0.000002, 'open', 'CH');
                INSERT INTO fixture VALUES ('closed', 'museum', 0, 0.000003, 'permanently_closed', 'CH');
                INSERT INTO fixture VALUES ('low', 'museum', 0.89, 0.000004, 'open', 'CH');
                INSERT INTO fixture VALUES ('pipeline', 'pipeline_transportation', 0.99, 0.000005, 'open', 'CH');
                INSERT INTO fixture VALUES ('sports', 'sports_and_recreation', 0.99, 0.000006, 'open', 'CH');
                INSERT INTO fixture VALUES ('airport-gate', 'airport_gate', 0.99, 0.000007, 'open', 'CH');
                INSERT INTO fixture VALUES ('real-port', 'commercial_port', 0.99, 0.001, 'open', 'CH');
                INSERT INTO fixture VALUES ('art-museum', 'art_museum', 0.99, 0.002, 'open', 'CH');
                INSERT INTO fixture VALUES ('baroque-church', 'baroque_church', 0.99, 0.0025, 'open', 'CH');
                COPY (SELECT id,
                    {'primary': id, 'common': map(['en'], [id])} AS names,
                    {'primary': category} AS taxonomy, category AS basic_category, confidence,
                    {'x': lon, 'y': 0.0} AS geometry,
                    {'xmin': lon, 'xmax': lon, 'ymin': 0.0, 'ymax': 0.0} AS bbox,
                    [{'country': country}] AS addresses, status AS operating_status,
                    [{'dataset': 'fixture-source'}] AS sources
                    FROM fixture) TO '{{path}}' (FORMAT PARQUET);
                """;
            cmd.ExecuteNonQuery();
            cmd.CommandText = OverturePlacesLogic.BuildQuery(0, 0, -1, 1, -1, 1, "CH", path);
            var places = new List<OverturePlaceResult>();
            var sqlReasons = new Dictionary<string, string?>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var place = Place(reader.GetString(0), reader.GetString(2), reader.IsDBNull(4) ? 0 : reader.GetDouble(4), reader.GetDouble(10)) with
                    {
                        OperatingStatus = reader.GetString(7),
                        Sources = OverturePlacesLogic.ParseSources(reader.GetString(9)),
                        ConfidenceKnown = !reader.IsDBNull(4)
                    };
                    places.Add(place);
                    sqlReasons[place.Id] = reader.IsDBNull(12) ? null : reader.GetString(12);
                }
            }

            var result = OverturePlacesLogic.BuildPlaceDiagnostics(places, null, "CH");
            Assert.AreEqual("real-port", result.BestMatch?.Id, "Actual ports must match without admitting unrelated transportation categories.");
            Assert.AreEqual(80, result.Candidates.Count(c => c.Eligible));
            Assert.AreEqual(20, result.Candidates.Count(c => !c.Eligible));
            Assert.AreEqual("museum-75", result.Candidates.Last(c => c.Eligible).Id, "The closest 80 useful places must survive the limit.");
            Assert.IsTrue(result.Candidates.Single(c => c.Id == "art-museum").Eligible);
            Assert.IsTrue(result.Candidates.Single(c => c.Id == "baroque-church").Eligible, "An exclusion such as bar must not match part of another category term.");
            Assert.IsFalse(result.Candidates.Single(c => c.Id == "pipeline").Eligible);
            Assert.IsFalse(result.Candidates.Single(c => c.Id == "sports").Eligible);
            Assert.IsFalse(result.Candidates.Single(c => c.Id == "airport-gate").Eligible);
            Assert.IsFalse(result.Candidates.Any(c => c.Id is "far" or "wrong-country"));
            CollectionAssert.AreEqual(new[] { "fixture-source" }, result.BestMatch!.Sources.ToArray());
            foreach (var place in places)
            {
                Assert.AreEqual(OverturePlacesLogic.GetExclusionReason(place), sqlReasons[place.Id], "SQL and diagnostic filters must agree.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static OverturePlaceResult Place(string id, string category, double confidence, double distance) =>
        new(id, id, category, category, confidence, "open", distance, false, ["source"]);
}
