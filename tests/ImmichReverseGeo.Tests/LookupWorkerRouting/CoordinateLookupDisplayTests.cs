using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.LookupWorkerRouting;

[TestClass]
[TestCategory("Change49")]
public sealed class CoordinateLookupDisplayTests
{
    [TestMethod]
    public void Presenter_PreservesClosedStatesCacheDetailsOmissionsAndUnknownCandidateFacts()
    {
        var sourceError = new WorkerJobSafeError(
            "source-unavailable",
            WorkerJobFailureCategory.Dependency,
            "The source is unavailable.");
        var cacheError = new WorkerJobSafeError(
            "cache-unavailable",
            WorkerJobFailureCategory.Dependency,
            "The cache is unavailable.");
        var candidate = new CoordinateLookupCandidate(
            "candidate",
            "Candidate",
            true,
            "selected",
            null,
            null,
            "division_area",
            "locality",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            omittedRecordSourceCount: 2,
            truncatedTextCount: 3);
        var overture = new CoordinateLookupSourceResult(
            CoordinateLookupSourceState.Unavailable,
            "2026-09",
            null,
            candidate,
            [candidate],
            [new CoordinateLookupCacheStatus("USA", CoordinateLookupSourceState.Failed, cacheError)],
            sourceError,
            "Overture Maps",
            null,
            null,
            omittedCandidateCount: 4,
            omittedCacheCount: 5,
            truncatedTextCount: 6);
        var gadm = new CoordinateLookupSourceResult(
            CoordinateLookupSourceState.Failed,
            null,
            "4.1",
            null,
            [],
            [],
            sourceError,
            CoordinateLookupGadmAttribution.DatasetName,
            CoordinateLookupGadmAttribution.LicenseUrl,
            CoordinateLookupGadmAttribution.UsageNotice);
        CoordinateLookupResult result = Result(overture, gadm);

        var display = new CoordinateLookupDisplayResult(result);

        Assert.AreEqual(CoordinateLookupSourceState.Unavailable,
            display.OvertureDivisionDiagnostics.State);
        Assert.AreEqual("The source is unavailable.",
            display.OvertureDivisionDiagnostics.Error);
        Assert.AreEqual(4, display.OvertureDivisionDiagnostics.OmittedCandidateCount);
        Assert.AreEqual(5, display.OvertureDivisionDiagnostics.OmittedCacheCount);
        Assert.AreEqual("USA", display.OvertureDivisionDiagnostics.Caches.Single().CountryCode);
        Assert.AreEqual(CoordinateLookupSourceState.Failed,
            display.OvertureDivisionDiagnostics.Caches.Single().State);
        Assert.AreEqual("The cache is unavailable.",
            display.OvertureDivisionDiagnostics.Caches.Single().Error!.Message);
        Assert.IsNull(display.OvertureDivision!.DistanceMetres);
        Assert.IsNull(display.OvertureDivision.Confidence);
        Assert.IsNull(display.OvertureDivision.BoundingBoxArea);
        Assert.IsNull(display.OvertureDivision.BoundingBoxContainsPoint);
        Assert.IsNull(display.OvertureDivision.GeometryContainsPoint);
        Assert.IsNull(display.OvertureDivision.IsTerritorial);
        Assert.AreEqual(2, display.OvertureDivision.OmittedRecordSourceCount);
        Assert.AreEqual(3, display.OvertureDivision.TruncatedTextCount);
        Assert.AreEqual(6, display.OvertureDivisionDiagnostics.TruncatedTextCount);
        Assert.AreEqual(CoordinateLookupSourceState.Failed,
            display.GadmDivisionDiagnostics.State);
        Assert.AreEqual(CoordinateLookupGadmAttribution.DatasetName,
            display.GadmDivisionDiagnostics.Attribution);
        Assert.AreEqual(CoordinateLookupGadmAttribution.LicenseUrl,
            display.GadmDivisionDiagnostics.LicenseUrl);
        Assert.AreEqual("Final country", display.FinalCountry);
        Assert.AreNotEqual(display.CountryName, display.FinalCountry);
        Assert.AreEqual(7, display.OmittedTraceCount);
        Assert.AreEqual(8, display.TruncatedTextCount);
    }

    private static CoordinateLookupResult Result(
        CoordinateLookupSourceResult overture,
        CoordinateLookupSourceResult gadm)
    {
        DateTimeOffset started = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var request = new CoordinateLookupRequest(
            47.4,
            8.5,
            false,
            false,
            true,
            new CoordinateLookupCityResolverOverrides(null, []));
        var disabled = new CoordinateLookupSourceResult(
            CoordinateLookupSourceState.Disabled,
            null,
            null,
            null,
            [],
            [],
            null,
            null,
            null,
            null);
        return new CoordinateLookupResult(
            request,
            started,
            started.AddSeconds(1),
            new CoordinateLookupCountryResult(
                CoordinateLookupCountryStatus.Matched,
                "USA",
                "US",
                "Country source value",
                "country",
                null),
            overture,
            gadm,
            disabled,
            disabled,
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupProfileSummary(
                "USA",
                ["locality"],
                CoordinateLookupTieBreak.SmallestArea),
            ["trace"],
            new CoordinateLookupFinalLocation(
                new CoordinateLookupAttributedValue(
                    "Final country",
                    CoordinateLookupFinalSource.BundledCountryDivisions),
                null,
                null),
            omittedTraceCount: 7,
            truncatedTextCount: 8);
    }
}
