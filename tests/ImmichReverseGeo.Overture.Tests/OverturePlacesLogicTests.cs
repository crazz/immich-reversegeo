using DuckDB.NET.Data;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class OverturePlacesLogicTests
{
    [TestMethod]
    public void BuildQuery_WithCountryFilter_EmbedsAlpha2AndReleaseUrl()
    {
        var sql = OverturePlacesLogic.BuildQuery(
            lat: 47.45,
            lon: 8.55,
            minLat: 47.40,
            maxLat: 47.50,
            minLon: 8.50,
            maxLon: 8.60,
            alpha2: "CH",
            releaseUrl: "https://example.test/release/theme=places/type=place/*");

        StringAssert.Contains(sql, "read_parquet('https://example.test/release/theme=places/type=place/*'");
        StringAssert.Contains(sql, "lower(addresses[1].country) = 'ch'");
        StringAssert.Contains(sql, "bbox.xmin BETWEEN 8.5 AND 8.6");
        StringAssert.Contains(sql, "bbox.ymin BETWEEN 47.4 AND 47.5");
    }

    [TestMethod]
    public void BuildQuery_WithoutCountryFilter_DoesNotEmbedCountryClause()
    {
        var sql = OverturePlacesLogic.BuildQuery(
            lat: 47.45,
            lon: 8.55,
            minLat: 47.40,
            maxLat: 47.50,
            minLon: 8.50,
            maxLon: 8.60,
            alpha2: null,
            releaseUrl: "https://example.test/release/theme=places/type=place/*");

        Assert.IsFalse(sql.Contains("addresses[1].country", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BuildQuery_UsesTaxonomyForCurrentAndTransitionalParquetSchemas(bool includeDeprecatedCategories)
    {
        var root = Path.Combine(Path.GetTempPath(), $"overture-places-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var parquetPath = Path.Combine(root, "places.parquet").Replace("'", "''", StringComparison.Ordinal);
            using var connection = new DuckDBConnection("Data Source=:memory:");
            connection.Open();
            using (var fixture = connection.CreateCommand())
            {
                // Coordinate macros keep this schema-binding regression independent
                // of the spatial extension and its external installation endpoint.
                var deprecatedColumn = includeDeprecatedCategories
                    ? ", {'primary': 'restaurant'} AS categories"
                    : string.Empty;
                fixture.CommandText = $$"""
                    CREATE MACRO ST_X(point) AS point.x;
                    CREATE MACRO ST_Y(point) AS point.y;
                    CREATE TABLE places AS
                    SELECT
                        id,
                        {'primary': name, 'common': map(['en'], [name])} AS names,
                        CASE WHEN primary_category IS NULL THEN NULL
                            ELSE {'primary': primary_category, 'hierarchy': [primary_category], 'alternates': []::VARCHAR[]}
                            END AS taxonomy,
                        'museum' AS basic_category,
                        0.98::DOUBLE AS confidence,
                        {'x': 8.55::DOUBLE, 'y': 47.45::DOUBLE} AS geometry,
                        'open' AS operating_status,
                        {'xmin': 8.54, 'xmax': 8.56, 'ymin': 47.44, 'ymax': 47.46} AS bbox,
                        [{'dataset': 'overture'}] AS sources,
                        [{'country': country}] AS addresses
                        {{deprecatedColumn}}
                    FROM (VALUES
                        ('museum', 'Art Museum', 'art_museum', 'CH'),
                        ('basic-only', 'Local Museum', NULL, 'CH'),
                        ('other-country', 'Other Museum', 'art_museum', 'FR')
                    ) AS input(id, name, primary_category, country);
                    COPY places TO '{{parquetPath}}' (FORMAT PARQUET);
                    """;
                fixture.ExecuteNonQuery();
            }

            using var query = connection.CreateCommand();
            query.CommandText = OverturePlacesLogic.BuildQuery(
                lat: 47.45, lon: 8.55,
                minLat: 47.40, maxLat: 47.50,
                minLon: 8.50, maxLon: 8.60,
                alpha2: "CH", releaseUrl: parquetPath);
            using var reader = query.ExecuteReader();
            var places = new Dictionary<string, OverturePlaceResult>();
            while (reader.Read())
            {
                var place = new OverturePlaceResult(
                    Id: reader.GetString(0),
                    Name: reader.GetString(1),
                    Category: reader.IsDBNull(2) ? null : reader.GetString(2),
                    BasicCategory: reader.GetString(3),
                    Confidence: reader.GetDouble(4),
                    OperatingStatus: reader.GetString(7),
                    DistanceMetres: 0,
                    BoundingBoxContainsPoint: reader.GetBoolean(8),
                    Sources: []);
                places.Add(place.Id, place);
            }

            CollectionAssert.AreEquivalent(new[] { "museum", "basic-only" }, places.Keys.ToArray());
            Assert.AreEqual("Art Museum", places["museum"].Name);
            Assert.AreEqual("art_museum", places["museum"].Category);
            Assert.IsNull(places["basic-only"].Category);
            Assert.AreEqual("museum", places["basic-only"].BasicCategory);
            Assert.IsTrue(places.Values.All(OverturePlacesLogic.IsInterestingPhotoPlace),
                "Taxonomy categories and basic-category-only places must retain the existing eligibility rules.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BuildQuery_DenseAreaRetainsNearestLandmarkRegardlessOfParquetRowOrder(bool reverseRows)
    {
        var root = Path.Combine(Path.GetTempPath(), $"overture-places-dense-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var parquetPath = Path.Combine(root, "places.parquet").Replace("'", "''", StringComparison.Ordinal);
            using var connection = new DuckDBConnection("Data Source=:memory:");
            connection.Open();
            using (var fixture = connection.CreateCommand())
            {
                var order = reverseRows ? "DESC" : "ASC";
                fixture.CommandText = $$"""
                    CREATE MACRO ST_X(point) AS point.x;
                    CREATE MACRO ST_Y(point) AS point.y;
                    CREATE TABLE places AS
                    SELECT
                        id,
                        {'primary': id, 'common': map(['en'], [id])} AS names,
                        {'primary': category} AS taxonomy,
                        category AS basic_category,
                        0.98::DOUBLE AS confidence,
                        {'x': longitude, 'y': 47.45::DOUBLE} AS geometry,
                        'open' AS operating_status,
                        {'xmin': longitude, 'xmax': longitude, 'ymin': 47.45, 'ymax': 47.45} AS bbox,
                        [{'dataset': 'overture'}] AS sources,
                        [{'country': 'CH'}] AS addresses
                    FROM (
                        SELECT range AS position, 'retail-' || range AS id,
                            'retail' AS category, 8.55::DOUBLE AS longitude FROM range(100)
                        UNION ALL SELECT 100, 'far-museum', 'museum', 8.56::DOUBLE
                        UNION ALL SELECT 101, 'near-museum', 'museum', 8.5501::DOUBLE
                    ) AS input
                    ORDER BY position {{order}};
                    COPY places TO '{{parquetPath}}' (FORMAT PARQUET);
                    """;
                fixture.ExecuteNonQuery();
            }

            using var query = connection.CreateCommand();
            query.CommandText = OverturePlacesLogic.BuildQuery(
                lat: 47.45, lon: 8.55,
                minLat: 47.40, maxLat: 47.50,
                minLon: 8.50, maxLon: 8.60,
                alpha2: "CH", releaseUrl: parquetPath);
            using var reader = query.ExecuteReader();
            OverturePlaceResult? best = null;
            var eligibleIds = new List<string>();
            while (reader.Read())
            {
                var candidate = new OverturePlaceResult(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetDouble(4), reader.GetString(7),
                    OverturePlacesLogic.HaversineMetres(47.45, 8.55, reader.GetDouble(5), reader.GetDouble(6)),
                    reader.GetBoolean(8), []);
                if (candidate.DistanceMetres > OverturePlacesLogic.SearchRadiusMetres
                    || !OverturePlacesLogic.IsInterestingPhotoPlace(candidate))
                {
                    continue;
                }

                eligibleIds.Add(candidate.Id);
                if (best is null || OverturePlacesLogic.ShouldPreferCandidate(candidate, best))
                {
                    best = candidate;
                }
            }

            CollectionAssert.AreEquivalent(new[] { "far-museum", "near-museum" }, eligibleIds);
            Assert.IsNotNull(best);
            Assert.AreEqual("near-museum", best.Id,
                "Unrelated places must not prevent the nearest eligible landmark from reaching selection.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ParseSources_DeduplicatesAndReturnsDatasets()
    {
        var sources = OverturePlacesLogic.ParseSources("""
            [
              { "dataset": "meta" },
              { "dataset": "foursquare" },
              { "dataset": "meta" }
            ]
            """);

        CollectionAssert.AreEqual(new[] { "meta", "foursquare" }, sources.ToArray());
    }

    [TestMethod]
    public void ParseSources_InvalidJson_ReturnsEmpty()
    {
        var sources = OverturePlacesLogic.ParseSources("not json");

        Assert.AreEqual(0, sources.Count);
    }

    [TestMethod]
    public void ShouldPreferCandidate_PrefersActiveOverClosed()
    {
        var active = CreateCandidate("active", confidence: 0.30, distanceMetres: 600);
        var closed = CreateCandidate("closed", confidence: 0.95, distanceMetres: 10);

        Assert.IsTrue(OverturePlacesLogic.ShouldPreferCandidate(active, closed));
        Assert.IsFalse(OverturePlacesLogic.ShouldPreferCandidate(closed, active));
    }

    [TestMethod]
    public void ShouldPreferCandidate_PrefersBoundingBoxContainmentBeforeDistance()
    {
        var bboxWinner = CreateCandidate("active", confidence: 0.92, distanceMetres: 800, bboxContainsPoint: true);
        var closer = CreateCandidate("active", confidence: 0.99, distanceMetres: 50, bboxContainsPoint: false);

        Assert.IsTrue(OverturePlacesLogic.ShouldPreferCandidate(bboxWinner, closer));
        Assert.IsFalse(OverturePlacesLogic.ShouldPreferCandidate(closer, bboxWinner));
    }

    [TestMethod]
    public void ShouldPreferCandidate_UsesDistanceWhenStatusAndContainmentMatch()
    {
        var closer = CreateCandidate("active", confidence: 0.82, distanceMetres: 120);
        var farther = CreateCandidate("active", confidence: 0.80, distanceMetres: 400);

        Assert.IsTrue(OverturePlacesLogic.ShouldPreferCandidate(closer, farther));
        Assert.IsFalse(OverturePlacesLogic.ShouldPreferCandidate(farther, closer));
    }

    [TestMethod]
    public void InfrastructureSelection_PrefersAirportClassDespiteNearerContainingCandidate()
    {
        var international = CreateInfrastructureCandidate(
            "international", "international_airport", distanceMetres: 800,
            bboxContainsPoint: false, geometryContainsPoint: false);
        var regional = CreateInfrastructureCandidate(
            "regional", "regional_airport", distanceMetres: 20,
            bboxContainsPoint: true, geometryContainsPoint: true);

        AssertInfrastructureWinnerInEitherOrder(international, regional);
    }

    [TestMethod]
    public void InfrastructureSelection_PrefersMatchingGeometryOverNearerBoundingBoxOfSameClass()
    {
        var containing = CreateInfrastructureCandidate(
            "containing", "international_airport", distanceMetres: 800,
            bboxContainsPoint: true, geometryContainsPoint: true);
        var bboxOnly = CreateInfrastructureCandidate(
            "bbox-only", "international_airport", distanceMetres: 20,
            bboxContainsPoint: true, geometryContainsPoint: false);

        AssertInfrastructureWinnerInEitherOrder(containing, bboxOnly);
    }

    [TestMethod]
    public void InfrastructureSelection_PrefersNearestOfOverlappingAirportsOfSameClass()
    {
        var closer = CreateInfrastructureCandidate(
            "closer", "regional_airport", distanceMetres: 20,
            bboxContainsPoint: true, geometryContainsPoint: true);
        var farther = CreateInfrastructureCandidate(
            "farther", "regional_airport", distanceMetres: 800,
            bboxContainsPoint: true, geometryContainsPoint: true);

        AssertInfrastructureWinnerInEitherOrder(closer, farther);
    }

    [TestMethod]
    public void IsInterestingPhotoPlace_AcceptsHighConfidenceMuseum()
    {
        var candidate = new OverturePlaceResult(
            Id: Guid.NewGuid().ToString("N"),
            Name: "Kunsthaus Zurich",
            Category: "museum",
            BasicCategory: "arts_and_entertainment",
            Confidence: 0.96,
            OperatingStatus: "active",
            DistanceMetres: 150,
            BoundingBoxContainsPoint: true,
            Sources: ["overture"]);

        Assert.IsTrue(OverturePlacesLogic.IsInterestingPhotoPlace(candidate));
    }

    [TestMethod]
    public void IsInterestingPhotoPlace_RejectsLowConfidenceCandidate()
    {
        var candidate = new OverturePlaceResult(
            Id: Guid.NewGuid().ToString("N"),
            Name: "Scenic Lookout",
            Category: "tourist_attraction",
            BasicCategory: "attractions",
            Confidence: 0.72,
            OperatingStatus: "active",
            DistanceMetres: 150,
            BoundingBoxContainsPoint: true,
            Sources: ["overture"]);

        Assert.IsFalse(OverturePlacesLogic.IsInterestingPhotoPlace(candidate));
    }

    [TestMethod]
    public void IsInterestingPhotoPlace_RejectsRoutineRetail()
    {
        var candidate = new OverturePlaceResult(
            Id: Guid.NewGuid().ToString("N"),
            Name: "Corner Shop",
            Category: "convenience_store",
            BasicCategory: "retail",
            Confidence: 0.98,
            OperatingStatus: "active",
            DistanceMetres: 25,
            BoundingBoxContainsPoint: true,
            Sources: ["overture"]);

        Assert.IsFalse(OverturePlacesLogic.IsInterestingPhotoPlace(candidate));
    }

    [TestMethod]
    public void IsInterestingPhotoPlace_RejectsTransportNoise()
    {
        var candidate = new OverturePlaceResult(
            Id: Guid.NewGuid().ToString("N"),
            Name: "Gate A12",
            Category: "airport_gate",
            BasicCategory: "transportation",
            Confidence: 0.99,
            OperatingStatus: "active",
            DistanceMetres: 40,
            BoundingBoxContainsPoint: true,
            Sources: ["overture"]);

        Assert.IsFalse(OverturePlacesLogic.IsInterestingPhotoPlace(candidate));
    }

    [TestMethod]
    [DataRow("anglican_church", "anglican_or_episcopal_place_of_worship", "christian_place_of_worship")]
    [DataRow("episcopal_church", "anglican_or_episcopal_place_of_worship", "christian_place_of_worship")]
    [DataRow("baptist_church", "baptist_place_of_worship", "christian_place_of_worship")]
    [DataRow("catholic_church", "roman_catholic_place_of_worship", "christian_place_of_worship")]
    [DataRow("church_cathedral", "christian_place_of_worship", "christian_place_of_worship")]
    [DataRow("evangelical_church", "protestant_place_of_worship", "christian_place_of_worship")]
    [DataRow("pentecostal_church", "pentecostal_place_of_worship", "christian_place_of_worship")]
    [DataRow("buddhist_temple", "buddhist_place_of_worship", "buddhist_place_of_worship")]
    [DataRow("hindu_temple", "hindu_place_of_worship", "hindu_place_of_worship")]
    [DataRow("mosque", "muslim_place_of_worship", "muslim_place_of_worship")]
    [DataRow("sikh_temple", "sikh_place_of_worship", "place_of_worship")]
    [DataRow("synagogue", "jewish_place_of_worship", "jewish_place_of_worship")]
    [DataRow("temple", "place_of_worship", "place_of_worship")]
    [DataRow("theaters_and_performance_venues", "performing_arts_venue", "performing_arts_venue")]
    [DataRow("attractions_and_activities", "arts_and_entertainment", "arts_and_entertainment")]
    public void IsInterestingPhotoPlace_PreservesRenamedLandmarkCategories(
        string legacyCategory, string taxonomyCategory, string basicCategory)
    {
        var legacy = CreateCandidate("open", 0.96, 100) with
        {
            Category = legacyCategory,
            BasicCategory = basicCategory
        };

        Assert.IsTrue(OverturePlacesLogic.IsInterestingPhotoPlace(legacy),
            "The fixture must represent a previously accepted landmark category.");
        Assert.IsTrue(OverturePlacesLogic.IsInterestingPhotoPlace(legacy with { Category = taxonomyCategory }),
            "The documented primary category rename must preserve landmark eligibility.");
    }

    [TestMethod]
    public void IsInterestingPhotoPlace_PreservesRenamedWarehouseExclusion()
    {
        var warehouse = CreateCandidate("open", 0.98, 100) with
        {
            Category = "b2b_storage_and_warehouses",
            BasicCategory = "b2b_transportation_and_storage_service"
        };

        Assert.IsFalse(OverturePlacesLogic.IsInterestingPhotoPlace(warehouse));
        Assert.IsFalse(OverturePlacesLogic.IsInterestingPhotoPlace(warehouse with { Category = "b2b_storage" }));
    }

    [TestMethod]
    public void IsInterestingPhotoPlace_DoesNotExpandGenericEntertainmentBasicCategory()
    {
        var candidate = CreateCandidate("open", 0.98, 100) with
        {
            Category = "art_studio",
            BasicCategory = "arts_and_entertainment"
        };

        Assert.IsFalse(OverturePlacesLogic.IsInterestingPhotoPlace(candidate),
            "The attraction primary-category alias must not broaden eligibility for unrelated entertainment categories.");
    }

    private static OverturePlaceResult CreateCandidate(string? status, double confidence, double distanceMetres, bool bboxContainsPoint = false) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            Name: "Candidate",
            Category: "airport",
            BasicCategory: "transportation",
            Confidence: confidence,
            OperatingStatus: status,
            DistanceMetres: distanceMetres,
            BoundingBoxContainsPoint: bboxContainsPoint,
            Sources: ["overture"]);

    private static OvertureInfrastructureResult CreateInfrastructureCandidate(
        string id, string className, double distanceMetres, bool bboxContainsPoint, bool geometryContainsPoint) =>
        new(
            Id: id,
            Name: id,
            FeatureType: "infrastructure",
            SubType: "airport",
            ClassName: className,
            DistanceMetres: distanceMetres,
            BoundingBoxContainsPoint: bboxContainsPoint,
            GeometryContainsPoint: geometryContainsPoint,
            Sources: ["overture"]);

    private static void AssertInfrastructureWinnerInEitherOrder(
        OvertureInfrastructureResult expected, OvertureInfrastructureResult other)
    {
        foreach (var candidates in new[] { new[] { expected, other }, new[] { other, expected } })
        {
            var winner = candidates[0];
            if (OverturePlacesLogic.ShouldPreferInfrastructureCandidate(candidates[1], winner))
            {
                winner = candidates[1];
            }

            Assert.AreEqual(expected.Id, winner.Id);
        }
    }
}
