using System.Text;
using System.Text.Json.Nodes;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerJobs;

[TestClass]
[TestCategory("Change48")]
public sealed class CoordinateLookupWorkerJobProtocolTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly Guid JobId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly DateTimeOffset Started =
        new DateTimeOffset(2026, 9, 8, 12, 13, 14, TimeSpan.Zero).AddTicks(1234567);
    private static readonly DateTimeOffset Ended = Started.AddSeconds(2);

    [TestMethod]
    public void Request_RoundTripsCanonicalOverridesAndPreservesPartialCatalogMerge()
    {
        var config = new CityResolverConfig
        {
            DefaultProfile = new CityResolverProfile
            {
                PreferredSubtypes = [],
                TieBreakMode = CityResolverTieBreakModes.LargestArea
            },
            CountryOverrides =
            {
                ["USA"] = new CityResolverProfile
                {
                    PreferredSubtypes = ["borough", "locality"],
                    TieBreakMode = string.Empty
                }
            }
        };
        CoordinateLookupCityResolverOverrides snapshot =
            CoordinateLookupCityProfileConversions.Snapshot(config);
        var request = new CoordinateLookupRequest(
            90,
            -180,
            includeAirportInfrastructure: true,
            includeLiveOverturePlaces: false,
            preferGadmAdministrativeAreas: true,
            snapshot);
        var message = Execute(request);

        byte[] bytes = WorkerJobProtocolCodec.SerializeControllerInput(message);
        WorkerJobControllerParseResult parsed = WorkerJobProtocolCodec.ParseControllerInput(bytes);

        Assert.IsTrue(parsed.IsSuccess);
        var payload = Assert.IsInstanceOfType<CoordinateLookupExecutePayload>(parsed.Message!.Payload);
        Assert.AreEqual(90, payload.Request.Latitude);
        Assert.AreEqual(-180, payload.Request.Longitude);
        Assert.AreEqual(CoordinateLookupTieBreak.LargestArea, payload.Request.CityResolverOverrides.DefaultProfile!.TieBreak);
        Assert.IsNull(payload.Request.CityResolverOverrides.CountryProfiles[0].Profile.TieBreak, "country-tie-break-inherits");

        CityResolverConfig decodedConfig =
            CoordinateLookupCityProfileConversions.ToConfig(payload.Request.CityResolverOverrides);
        var catalog = new CityResolverProfileCatalog
        {
            DefaultProfile = new CityResolverProfile
            {
                PreferredSubtypes = ["locality"],
                TieBreakMode = CityResolverTieBreakModes.SmallestArea
            }
        };
        CityResolverProfile resolved = catalog.GetProfile(decodedConfig, "USA");
        Assert.AreEqual(CityResolverTieBreakModes.LargestArea, resolved.TieBreakMode);
        CollectionAssert.AreEqual(new[] { "borough", "locality" }, resolved.PreferredSubtypes);
    }

    [TestMethod]
    public void Request_HasCanonicalNdjsonGoldenWithOrderedPartialProfiles()
    {
        var request = new CoordinateLookupRequest(
            90,
            -180,
            includeAirportInfrastructure: true,
            includeLiveOverturePlaces: false,
            preferGadmAdministrativeAreas: true,
            new CoordinateLookupCityResolverOverrides(
                new CoordinateLookupCityProfile([], CoordinateLookupTieBreak.LargestArea),
                [
                    new CoordinateLookupCountryProfile(
                        "USA",
                        new CoordinateLookupCityProfile(["borough", "locality"], null))
                ]));

        string actual = Encoding.UTF8.GetString(
            WorkerJobProtocolCodec.SerializeControllerInput(Execute(request)));

        const string expected =
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-09-08T12:13:14.1234567Z\",\"jobId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"jobKind\":\"CoordinateLookup\",\"payload\":{\"latitude\":90,\"longitude\":-180,\"includeAirportInfrastructure\":true,\"includeLiveOverturePlaces\":false,\"preferGadmAdministrativeAreas\":true,\"cityResolverOverrides\":{\"defaultProfile\":{\"preferredSubtypes\":[],\"tieBreak\":\"largest-area\"},\"countryProfiles\":[{\"countryCode\":\"USA\",\"profile\":{\"preferredSubtypes\":[\"borough\",\"locality\"],\"tieBreak\":null}}]}}}";
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void Request_RejectsInvalidCoordinatesProfilesAndUnregisteredKindBeforeAcceptance()
    {
        CoordinateLookupCityResolverOverrides empty = EmptyOverrides();
        var oppositeExtrema = new CoordinateLookupRequest(-90, 180, false, false, false, empty);
        Assert.AreEqual(-90, oppositeExtrema.Latitude);
        Assert.AreEqual(180, oppositeExtrema.Longitude);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CoordinateLookupRequest(double.NaN, 0, false, false, false, empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CoordinateLookupRequest(double.PositiveInfinity, 0, false, false, false, empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CoordinateLookupRequest(double.NegativeInfinity, 0, false, false, false, empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CoordinateLookupRequest(0, double.PositiveInfinity, false, false, false, empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CoordinateLookupRequest(0, double.NegativeInfinity, false, false, false, empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CoordinateLookupRequest(0, -180.000001, false, false, false, empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CoordinateLookupRequest(0, 180.000001, false, false, false, empty));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new CoordinateLookupCityProfile(["Locality"], null));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new CoordinateLookupCityResolverOverrides(
                null,
                [
                    new CoordinateLookupCountryProfile("USA", new CoordinateLookupCityProfile([], null)),
                    new CoordinateLookupCountryProfile("USA", new CoordinateLookupCityProfile([], null))
                ]));

        var validator = new WorkerJobControllerInputValidator([WorkerJobDescriptors.ProcessAssets]);
        WorkerJobControllerValidationResult rejected = validator.Validate(
            Execute(new CoordinateLookupRequest(0, 0, false, false, false, empty)),
            isReady: true,
            WorkerJobExecutionPhase.BeforeInvocation);
        Assert.IsFalse(rejected.IsSuccess);
        Assert.AreEqual(WorkerProtocolFailureCode.UnsupportedType, rejected.Failure!.Code);
        Assert.IsNull(validator.Snapshot.JobId);
        Assert.IsNull(validator.Snapshot.Request);
    }

    [TestMethod]
    public void DescriptorAndRegistry_AreExplicitHeavyGeodataFactsAndOptInOnly()
    {
        WorkerJobDescriptor descriptor = WorkerJobDescriptors.CoordinateLookup;
        Assert.AreEqual(WorkerJobCapabilityFamily.Lookup, descriptor.Arbitration.CapabilityFamily);
        Assert.AreEqual(WorkerJobResourceClass.ExclusiveHeavyWorker, descriptor.Arbitration.ResourceClass);
        Assert.IsTrue(descriptor.Arbitration.IsHeavy);
        Assert.IsTrue(descriptor.Arbitration.IsCancellable);
        Assert.IsTrue(descriptor.Arbitration.IsGeodataBearing);
        Assert.IsTrue(WorkerJobDescriptors.ProcessAssets.Arbitration.IsGeodataBearing);

        var productionShape = new WorkerJobHandlerRegistry(
        [
            new WorkerJobHandlerRegistration<ProcessAssetsRequest, ProcessAssetsResult>(
                WorkerJobDescriptors.ProcessAssets,
                static _ => throw new InvalidOperationException("resolution-not-expected"))
        ]);
        CollectionAssert.AreEqual(
            new[] { WorkerJobKind.ProcessAssets },
            productionShape.SupportedJobKinds.ToArray());

        var optIn = new WorkerJobHandlerRegistry(
        [
            new WorkerJobHandlerRegistration<CoordinateLookupRequest, CoordinateLookupResult>(
                descriptor,
                static _ => throw new InvalidOperationException("resolution-not-expected"))
        ]);
        CollectionAssert.AreEqual(
            new[] { WorkerJobKind.CoordinateLookup },
            optIn.SupportedJobKinds.ToArray());

        var validator = new WorkerJobControllerInputValidator(optIn.SupportedJobDescriptors);
        WorkerJobControllerValidationResult accepted = validator.Validate(
            Execute(new CoordinateLookupRequest(0, 0, false, false, false, EmptyOverrides())),
            isReady: true,
            WorkerJobExecutionPhase.BeforeInvocation);
        Assert.IsTrue(accepted.IsSuccess);
        Assert.AreSame(descriptor, validator.Snapshot.Descriptor);
        Assert.IsInstanceOfType<CoordinateLookupRequest>(validator.Snapshot.Request);
    }

    [TestMethod]
    public void ProgressAndCompletedResult_RoundTripWithClosedCoordinateVariants()
    {
        var request = new CoordinateLookupRequest(1.5, -2.5, true, true, true, EmptyOverrides());
        var progress = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.ProgressCategory,
            WorkerJobProtocolV2.ProgressChangedType,
            3,
            Started,
            JobId,
            WorkerJobKind.CoordinateLookup,
            new CoordinateLookupProgressPayload(
                CoordinateLookupProgressStep.OvertureCache,
                CoordinateLookupSourceState.Ready,
                "USA",
                "Overture cache ready."));
        WorkerJobProtocolParseResult parsedProgress =
            WorkerJobProtocolCodec.Parse(WorkerJobProtocolCodec.Serialize(progress));
        Assert.IsTrue(parsedProgress.IsSuccess);
        var progressPayload =
            Assert.IsInstanceOfType<CoordinateLookupProgressPayload>(parsedProgress.Message!.Payload);
        Assert.AreEqual(CoordinateLookupProgressStep.OvertureCache, progressPayload.Step);

        CoordinateLookupResult result = Result(request, []);
        var terminal = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            4,
            Ended,
            JobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                Started,
                Ended,
                null,
                result,
                null));
        byte[] bytes = WorkerJobProtocolCodec.Serialize(terminal);
        WorkerJobProtocolParseResult parsed = WorkerJobProtocolCodec.Parse(bytes);
        Assert.IsTrue(parsed.IsSuccess);
        CoordinateLookupResult? roundTrip =
            Assert.IsInstanceOfType<WorkerJobTerminalPayload>(parsed.Message!.Payload).CoordinateLookupResult;
        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(request.Latitude, roundTrip.Request.Latitude);
        Assert.AreEqual(CoordinateLookupCountryStatus.NoMatch, roundTrip.Country.Status);
    }

    [TestMethod]
    public void Progress_AllClosedStepAndSourceStateTokensHaveCanonicalNdjsonGoldens()
    {
        string[] stepTokens =
        [
            "country",
            "overture-cache",
            "overture-administrative",
            "gadm-cache",
            "gadm-administrative",
            "airport",
            "live-places",
            "final-selection"
        ];
        string[] stateTokens =
        [
            "disabled",
            "skipped",
            "ready",
            "no-match",
            "unavailable",
            "failed"
        ];

        foreach (CoordinateLookupProgressStep step in Enum.GetValues<CoordinateLookupProgressStep>())
        {
            foreach (CoordinateLookupSourceState state in Enum.GetValues<CoordinateLookupSourceState>())
            {
                var message = new WorkerJobOutputMessage(
                    WorkerJobProtocolV2.ProgressCategory,
                    WorkerJobProtocolV2.ProgressChangedType,
                    3,
                    Started,
                    JobId,
                    WorkerJobKind.CoordinateLookup,
                    new CoordinateLookupProgressPayload(step, state, "USA", "Step complete."));
                string actual = Encoding.UTF8.GetString(WorkerJobProtocolCodec.Serialize(message));
                string expected =
                    "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"progress\",\"type\":\"progress-changed\",\"sequence\":3,\"timestampUtc\":\"2026-09-08T12:13:14.1234567Z\",\"jobId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"jobKind\":\"CoordinateLookup\",\"payload\":{\"step\":\""
                    + stepTokens[(int)step]
                    + "\",\"state\":\""
                    + stateTokens[(int)state]
                    + "\",\"countryCode\":\"USA\",\"message\":\"Step complete.\"}}";

                Assert.AreEqual(expected, actual, $"{step}/{state}");
                WorkerJobProtocolParseResult parsed = WorkerJobProtocolCodec.Parse(
                    Encoding.UTF8.GetBytes(expected));
                Assert.IsTrue(parsed.IsSuccess, $"{step}/{state}: {parsed.Failure?.Diagnostic}");
                var progress = Assert.IsInstanceOfType<CoordinateLookupProgressPayload>(
                    parsed.Message!.Payload);
                Assert.AreEqual(step, progress.Step);
                Assert.AreEqual(state, progress.State);
            }
        }
    }

    [TestMethod]
    public void CancelledAndFailedTerminals_HaveCanonicalCoordinateNdjsonGoldens()
    {
        var cancelled = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            4,
            Ended,
            JobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Cancelled,
                Started,
                Ended,
                null,
                null));
        var failed = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            4,
            Ended,
            JobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Failed,
                Started,
                Ended,
                null,
                null,
                new WorkerJobSafeError(
                    "coordinate-lookup-failed",
                    WorkerJobFailureCategory.Domain,
                    "The coordinate lookup failed.")));

        const string cancelledGolden =
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"terminal\",\"sequence\":4,\"timestampUtc\":\"2026-09-08T12:13:16.1234567Z\",\"jobId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"jobKind\":\"CoordinateLookup\",\"payload\":{\"outcome\":\"cancelled\",\"startedAtUtc\":\"2026-09-08T12:13:14.1234567Z\",\"endedAtUtc\":\"2026-09-08T12:13:16.1234567Z\",\"result\":null,\"error\":null}}";
        const string failedGolden =
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"terminal\",\"sequence\":4,\"timestampUtc\":\"2026-09-08T12:13:16.1234567Z\",\"jobId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"jobKind\":\"CoordinateLookup\",\"payload\":{\"outcome\":\"failed\",\"startedAtUtc\":\"2026-09-08T12:13:14.1234567Z\",\"endedAtUtc\":\"2026-09-08T12:13:16.1234567Z\",\"result\":null,\"error\":{\"code\":\"coordinate-lookup-failed\",\"category\":\"domain\",\"message\":\"The coordinate lookup failed.\"}}}";

        Assert.AreEqual(cancelledGolden, Encoding.UTF8.GetString(WorkerJobProtocolCodec.Serialize(cancelled)));
        Assert.AreEqual(failedGolden, Encoding.UTF8.GetString(WorkerJobProtocolCodec.Serialize(failed)));
        Assert.IsTrue(WorkerJobProtocolCodec.Parse(Encoding.UTF8.GetBytes(cancelledGolden)).IsSuccess);
        Assert.IsTrue(WorkerJobProtocolCodec.Parse(Encoding.UTF8.GetBytes(failedGolden)).IsSuccess);
    }

    [TestMethod]
    public void Parser_RejectsMalformedNonCanonicalOverLimitDuplicateAndKindMismatchedRequests()
    {
        var baseline = new CoordinateLookupRequest(
            1,
            2,
            false,
            false,
            false,
            EmptyOverrides());
        string json = Encoding.UTF8.GetString(
            WorkerJobProtocolCodec.SerializeControllerInput(Execute(baseline)));
        string payloadPrefix = "\"payload\":{";
        var rows = new (string Label, string Json, WorkerProtocolFailureCode Code)[]
        {
            ("noncanonical-latitude", json.Replace("\"latitude\":1", "\"latitude\":1.0", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("malformed-latitude", json.Replace("\"latitude\":1", "\"latitude\":\"1\"", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("out-of-range-latitude", json.Replace("\"latitude\":1", "\"latitude\":91", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("duplicate-latitude", json.Replace("\"latitude\":1", "\"latitude\":1,\"latitude\":1", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("noncanonical-country", json.Replace("\"countryProfiles\":[]", "\"countryProfiles\":[{\"countryCode\":\"usa\",\"profile\":{\"preferredSubtypes\":[],\"tieBreak\":null}}]", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("duplicate-subtype", json.Replace("\"defaultProfile\":null", "\"defaultProfile\":{\"preferredSubtypes\":[\"locality\",\"locality\"],\"tieBreak\":null}", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("unsorted-profiles", json.Replace("\"countryProfiles\":[]", "\"countryProfiles\":[{\"countryCode\":\"USA\",\"profile\":{\"preferredSubtypes\":[],\"tieBreak\":null}},{\"countryCode\":\"CAN\",\"profile\":{\"preferredSubtypes\":[],\"tieBreak\":null}}]", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("too-many-country-profiles", json.Replace("\"countryProfiles\":[]", "\"countryProfiles\":[" + string.Join(',', Enumerable.Range(0, CoordinateLookupProtocolBounds.MaxCountryProfiles + 1).Select(index => $"{{\"countryCode\":\"{(char)('A' + index / 26)}{(char)('A' + index % 26)}A\",\"profile\":{{\"preferredSubtypes\":[],\"tieBreak\":null}}}}")) + "]", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("too-many-subtypes", json.Replace("\"defaultProfile\":null", "\"defaultProfile\":{\"preferredSubtypes\":[" + string.Join(',', Enumerable.Range(0, CoordinateLookupProtocolBounds.MaxPreferredSubtypes + 1).Select(index => $"\"s{index}\"")) + "],\"tieBreak\":null}", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("kind-mismatch", json.Replace("\"jobKind\":\"CoordinateLookup\"", "\"jobKind\":\"ProcessAssets\"", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("payload-shape-mismatch", json.Replace(payloadPrefix, payloadPrefix + "\"trigger\":\"manual\",", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload)
        };

        foreach ((string label, string row, WorkerProtocolFailureCode code) in rows)
        {
            WorkerJobControllerParseResult parsed = WorkerJobProtocolCodec.ParseControllerInput(
                Encoding.UTF8.GetBytes(row));
            Assert.IsFalse(parsed.IsSuccess, label);
            Assert.AreEqual(code, parsed.Failure!.Code, label);
        }
    }

    [TestMethod]
    public void CompletedResult_HasCanonicalNdjsonGoldenWithGadmLicenseAndFinalAttribution()
    {
        CoordinateLookupRequest request = new(
            1.5,
            -2.5,
            true,
            true,
            true,
            EmptyOverrides());
        CoordinateLookupSourceResult skipped = Source(
            CoordinateLookupSourceState.Skipped,
            [],
            null);
        CoordinateLookupSourceResult gadm = new(
            CoordinateLookupSourceState.NoMatch,
            null,
            "4.1",
            null,
            [],
            [new CoordinateLookupCacheStatus("USA", CoordinateLookupSourceState.Ready, null)],
            null,
            CoordinateLookupGadmAttribution.DatasetName,
            CoordinateLookupGadmAttribution.LicenseUrl,
            CoordinateLookupGadmAttribution.UsageNotice);
        CoordinateLookupResult result = new(
            request,
            Started,
            Ended,
            new CoordinateLookupCountryResult(
                CoordinateLookupCountryStatus.Matched,
                "USA",
                "US",
                "United States",
                "country-1",
                null),
            skipped,
            gadm,
            skipped,
            skipped,
            new CoordinateLookupAdministrativeResult("Washington", "Seattle"),
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupProfileSummary(
                "USA",
                ["locality"],
                CoordinateLookupTieBreak.SmallestArea),
            ["done"],
            new CoordinateLookupFinalLocation(
                new CoordinateLookupAttributedValue(
                    "United States",
                    CoordinateLookupFinalSource.BundledCountryDivisions),
                new CoordinateLookupAttributedValue(
                    "Washington",
                    CoordinateLookupFinalSource.CachedOvertureDivisions),
                new CoordinateLookupAttributedValue(
                    "Seattle",
                    CoordinateLookupFinalSource.CachedOvertureDivisions)));
        var terminal = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            4,
            Ended,
            JobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                Started,
                Ended,
                null,
                result,
                null));

        const string emptySource =
            "{\"state\":\"skipped\",\"release\":null,\"version\":null,\"bestMatch\":null,\"candidates\":[],\"caches\":[],\"error\":null,\"attribution\":null,\"licenseUrl\":null,\"usageNotice\":null,\"omittedCandidateCount\":0,\"omittedCacheCount\":0,\"truncatedTextCount\":0}";
        const string gadmSource =
            "{\"state\":\"no-match\",\"release\":null,\"version\":\"4.1\",\"bestMatch\":null,\"candidates\":[],\"caches\":[{\"countryCode\":\"USA\",\"state\":\"ready\",\"error\":null}],\"error\":null,\"attribution\":\"GADM\",\"licenseUrl\":\"https://gadm.org/license.html\",\"usageNotice\":\"GADM data is limited to academic and other non-commercial use.\",\"omittedCandidateCount\":0,\"omittedCacheCount\":0,\"truncatedTextCount\":0}";
        const string resultGolden =
            "{\"request\":{\"latitude\":1.5,\"longitude\":-2.5,\"includeAirportInfrastructure\":true,\"includeLiveOverturePlaces\":true,\"preferGadmAdministrativeAreas\":true,\"cityResolverOverrides\":{\"defaultProfile\":null,\"countryProfiles\":[]}},\"startedAtUtc\":\"2026-09-08T12:13:14.1234567+00:00\",\"endedAtUtc\":\"2026-09-08T12:13:16.1234567+00:00\",\"country\":{\"status\":\"matched\",\"iso3\":\"USA\",\"alpha2\":\"US\",\"name\":\"United States\",\"sourceId\":\"country-1\",\"error\":null},\"overtureDivisions\":"
            + emptySource
            + ",\"gadmDivisions\":"
            + gadmSource
            + ",\"airportInfrastructure\":"
            + emptySource
            + ",\"liveOverturePlaces\":"
            + emptySource
            + ",\"overtureAdministrative\":{\"state\":\"Washington\",\"city\":\"Seattle\"},\"gadmAdministrative\":{\"state\":null,\"city\":null},\"profile\":{\"countryCode\":\"USA\",\"preferredSubtypes\":[\"locality\"],\"tieBreak\":\"smallest-area\"},\"trace\":[\"done\"],\"omittedTraceCount\":0,\"truncatedTextCount\":0,\"finalLocation\":{\"country\":{\"value\":\"United States\",\"source\":\"bundled-country-divisions\"},\"state\":{\"value\":\"Washington\",\"source\":\"cached-overture-divisions\"},\"city\":{\"value\":\"Seattle\",\"source\":\"cached-overture-divisions\"}}}";
        const string expected =
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"terminal\",\"sequence\":4,\"timestampUtc\":\"2026-09-08T12:13:16.1234567Z\",\"jobId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"jobKind\":\"CoordinateLookup\",\"payload\":{\"outcome\":\"completed\",\"startedAtUtc\":\"2026-09-08T12:13:14.1234567Z\",\"endedAtUtc\":\"2026-09-08T12:13:16.1234567Z\",\"result\":"
            + resultGolden
            + ",\"error\":null}}";

        byte[] bytes = WorkerJobProtocolCodec.Serialize(terminal);
        Assert.AreEqual(expected, Encoding.UTF8.GetString(bytes));
        Assert.IsTrue(WorkerJobProtocolCodec.Parse(bytes).IsSuccess);

        string wrongKind = expected.Replace(
            "\"jobKind\":\"CoordinateLookup\"",
            "\"jobKind\":\"ProcessAssets\"",
            StringComparison.Ordinal);
        WorkerJobProtocolParseResult rejected = WorkerJobProtocolCodec.Parse(
            Encoding.UTF8.GetBytes(wrongKind));
        Assert.IsFalse(rejected.IsSuccess);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidPayload, rejected.Failure!.Code);
    }

    [TestMethod]
    [DataRow("country-status-integer", "\"status\":\"matched\"", "\"status\":0")]
    [DataRow("country-status-numeric-string", "\"status\":\"matched\"", "\"status\":\"0\"")]
    [DataRow("country-status-wrong-case", "\"status\":\"matched\"", "\"status\":\"Matched\"")]
    [DataRow("source-state-integer", "\"overtureDivisions\":{\"state\":\"ready\"", "\"overtureDivisions\":{\"state\":2")]
    [DataRow("source-state-numeric-string", "\"overtureDivisions\":{\"state\":\"ready\"", "\"overtureDivisions\":{\"state\":\"2\"")]
    [DataRow("source-state-wrong-case", "\"overtureDivisions\":{\"state\":\"ready\"", "\"overtureDivisions\":{\"state\":\"Ready\"")]
    [DataRow("cache-state-integer", "\"countryCode\":\"USA\",\"state\":\"ready\"", "\"countryCode\":\"USA\",\"state\":2")]
    [DataRow("cache-state-numeric-string", "\"countryCode\":\"USA\",\"state\":\"ready\"", "\"countryCode\":\"USA\",\"state\":\"2\"")]
    [DataRow("cache-state-wrong-case", "\"countryCode\":\"USA\",\"state\":\"ready\"", "\"countryCode\":\"USA\",\"state\":\"Ready\"")]
    [DataRow("tie-break-integer", "\"tieBreak\":\"smallest-area\"", "\"tieBreak\":0")]
    [DataRow("tie-break-numeric-string", "\"tieBreak\":\"smallest-area\"", "\"tieBreak\":\"0\"")]
    [DataRow("tie-break-wrong-case", "\"tieBreak\":\"smallest-area\"", "\"tieBreak\":\"Smallest-Area\"")]
    [DataRow("final-source-integer", "\"source\":\"bundled-country-divisions\"", "\"source\":0")]
    [DataRow("final-source-numeric-string", "\"source\":\"bundled-country-divisions\"", "\"source\":\"0\"")]
    [DataRow("final-source-wrong-case", "\"source\":\"bundled-country-divisions\"", "\"source\":\"Bundled-Country-Divisions\"")]
    [DataRow("cache-country-null", "\"countryCode\":\"USA\",\"state\":\"ready\"", "\"countryCode\":null,\"state\":\"ready\"")]
    public void CompletedResultParser_RejectsNonCanonicalEnumTokensAndNullCacheCountryCode(
        string label,
        string search,
        string replacement)
    {
        CoordinateLookupRequest request = new(
            1.5,
            -2.5,
            true,
            true,
            true,
            EmptyOverrides());
        CoordinateLookupSourceResult source = new(
            CoordinateLookupSourceState.Ready,
            null,
            null,
            null,
            [],
            [new CoordinateLookupCacheStatus("USA", CoordinateLookupSourceState.Ready, null)],
            null,
            null,
            null,
            null);
        CoordinateLookupResult result = new(
            request,
            Started,
            Ended,
            new CoordinateLookupCountryResult(
                CoordinateLookupCountryStatus.Matched,
                "USA",
                "US",
                "United States",
                "country-1",
                null),
            source,
            Source(CoordinateLookupSourceState.Skipped, [], null),
            Source(CoordinateLookupSourceState.Skipped, [], null),
            Source(CoordinateLookupSourceState.Skipped, [], null),
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupProfileSummary(
                "USA",
                ["locality"],
                CoordinateLookupTieBreak.SmallestArea),
            [],
            new CoordinateLookupFinalLocation(
                new CoordinateLookupAttributedValue(
                    "United States",
                    CoordinateLookupFinalSource.BundledCountryDivisions),
                null,
                null));
        var terminal = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            4,
            Ended,
            JobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                Started,
                Ended,
                null,
                result,
                null));
        string valid = Encoding.UTF8.GetString(WorkerJobProtocolCodec.Serialize(terminal));
        string malformed = valid.Replace(search, replacement, StringComparison.Ordinal);
        Assert.AreNotEqual(valid, malformed, $"{label}-fixture-mutation");

        WorkerJobProtocolParseResult parsed = WorkerJobProtocolCodec.Parse(
            Encoding.UTF8.GetBytes(malformed));

        Assert.IsFalse(parsed.IsSuccess, label);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidPayload, parsed.Failure!.Code, label);
    }

    [TestMethod]
    [DataRow("request")]
    [DataRow("country")]
    [DataRow("overtureDivisions")]
    [DataRow("gadmDivisions")]
    [DataRow("airportInfrastructure")]
    [DataRow("liveOverturePlaces")]
    [DataRow("overtureAdministrative")]
    [DataRow("gadmAdministrative")]
    [DataRow("profile")]
    [DataRow("trace")]
    [DataRow("finalLocation")]
    public void CompletedResultParser_RejectsNullRequiredAggregateMembers(string member)
    {
        CoordinateLookupRequest request = new(0, 0, false, false, false, EmptyOverrides());
        CoordinateLookupResult result = Result(request, []);
        var terminal = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            2,
            Ended,
            JobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                Started,
                Ended,
                null,
                result,
                null));
        JsonNode document = JsonNode.Parse(WorkerJobProtocolCodec.Serialize(terminal))!;
        document["payload"]!["result"]![member] = null;

        WorkerJobProtocolParseResult parsed = WorkerJobProtocolCodec.Parse(
            Encoding.UTF8.GetBytes(document.ToJsonString()));

        Assert.IsFalse(parsed.IsSuccess, member);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidPayload, parsed.Failure!.Code, member);
    }

    [TestMethod]
    [DataRow("request-latitude", "request/latitude")]
    [DataRow("request-airport-flag", "request/includeAirportInfrastructure")]
    [DataRow("country-status", "country/status")]
    [DataRow("source-state", "overtureDivisions/state")]
    [DataRow("cache-state", "overtureDivisions/caches/0/state")]
    [DataRow("profile-tie-break", "profile/tieBreak")]
    [DataRow("final-source", "finalLocation/country/source")]
    [DataRow("nullable-country-error", "country/error")]
    [DataRow("started-at", "startedAtUtc")]
    [DataRow("result-omitted-trace-count", "omittedTraceCount")]
    [DataRow("source-omitted-candidate-count", "overtureDivisions/omittedCandidateCount")]
    [DataRow("candidate-omitted-source-count", "overtureDivisions/candidates/0/omittedRecordSourceCount")]
    [DataRow("best-match-truncated-text-count", "overtureDivisions/bestMatch/truncatedTextCount")]
    public void CompletedResultParser_RejectsMissingRequiredConstructorAndCountFields(
        string label,
        string path)
    {
        CoordinateLookupCandidate candidate = Candidate();
        CoordinateLookupSourceResult source = new(
            CoordinateLookupSourceState.Ready,
            null,
            null,
            candidate,
            [candidate],
            [new CoordinateLookupCacheStatus("USA", CoordinateLookupSourceState.Ready, null)],
            null,
            null,
            null,
            null);
        CoordinateLookupRequest request = new(1, 2, true, false, true, EmptyOverrides());
        CoordinateLookupResult result = new(
            request,
            Started,
            Ended,
            new CoordinateLookupCountryResult(
                CoordinateLookupCountryStatus.Matched,
                "USA",
                "US",
                "United States",
                "country-1",
                null),
            source,
            Source(CoordinateLookupSourceState.Skipped, [], null),
            Source(CoordinateLookupSourceState.Skipped, [], null),
            Source(CoordinateLookupSourceState.Skipped, [], null),
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupProfileSummary(
                "USA",
                ["locality"],
                CoordinateLookupTieBreak.SmallestArea),
            [],
            new CoordinateLookupFinalLocation(
                new CoordinateLookupAttributedValue(
                    "United States",
                    CoordinateLookupFinalSource.BundledCountryDivisions),
                null,
                null));
        var terminal = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            2,
            Ended,
            JobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                Started,
                Ended,
                null,
                result,
                null));
        JsonNode document = JsonNode.Parse(WorkerJobProtocolCodec.Serialize(terminal))!;
        JsonNode resultNode = document["payload"]!["result"]!;
        RemovePath(resultNode, path);

        WorkerJobProtocolParseResult parsed = WorkerJobProtocolCodec.Parse(
            Encoding.UTF8.GetBytes(document.ToJsonString()));

        Assert.IsFalse(parsed.IsSuccess, label);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidPayload, parsed.Failure!.Code, label);
    }

    [TestMethod]
    public void FailedTerminalParser_RejectsMissingSafeErrorCategory()
    {
        var terminal = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            2,
            Ended,
            JobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Failed,
                Started,
                Ended,
                null,
                null,
                new WorkerJobSafeError(
                    "coordinate-lookup-failed",
                    WorkerJobFailureCategory.Domain,
                    "The coordinate lookup failed.")));
        JsonNode document = JsonNode.Parse(WorkerJobProtocolCodec.Serialize(terminal))!;
        document["payload"]!["error"]!.AsObject().Remove("category");

        WorkerJobProtocolParseResult parsed = WorkerJobProtocolCodec.Parse(
            Encoding.UTF8.GetBytes(document.ToJsonString()));

        Assert.IsFalse(parsed.IsSuccess);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidPayload, parsed.Failure!.Code);
    }

    [TestMethod]
    public void WorstCaseEscaping_ProducerBoundsResultBelowTransportLimitAndReportsTruncation()
    {
        string escaped = new('\u0001', CoordinateLookupProtocolBounds.MaxDisplayTextLength);
        string escapedId = new('\u0001', CoordinateLookupProtocolBounds.MaxIdentifierLength);
        var candidate = new CoordinateLookupCandidate(
            escapedId,
            escaped,
            true,
            escaped,
            true,
            true,
            escaped,
            escaped,
            escaped,
            escaped,
            escaped,
            99,
            true,
            true,
            escaped,
            escaped,
            escaped,
            escaped,
            1,
            1,
            1,
            Enumerable.Repeat(escapedId, CoordinateLookupProtocolBounds.MaxRecordSourcesPerCandidate + 3)
                .Select(static (value, index) => value[..^1] + (char)('a' + index))
                .ToArray());
        var candidates = Enumerable.Repeat(candidate, CoordinateLookupProtocolBounds.MaxCandidatesPerSource + 5).ToArray();
        CoordinateLookupSourceResult source = Source(CoordinateLookupSourceState.Ready, candidates, escaped);
        var request = new CoordinateLookupRequest(0, 0, true, true, true, EmptyOverrides());
        CoordinateLookupResult result = Result(
            request,
            Enumerable.Repeat(escaped, CoordinateLookupProtocolBounds.MaxTraceEntries + 5).ToArray(),
            source,
            useReadySourceForEverySection: true);
        var terminal = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            2,
            Ended,
            JobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                Started,
                Ended,
                null,
                result,
                null));

        byte[] bytes = WorkerJobProtocolCodec.Serialize(terminal);

        Assert.IsLessThanOrEqualTo(WorkerJobProtocolV2.MaxMessageBytes, bytes.Length);
        WorkerJobProtocolParseResult parsed = WorkerJobProtocolCodec.Parse(bytes);
        Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
        CoordinateLookupResult parsedResult = Assert.IsInstanceOfType<CoordinateLookupResult>(
            Assert.IsInstanceOfType<WorkerJobTerminalPayload>(parsed.Message!.Payload)
                .CoordinateLookupResult);
        Assert.AreEqual(5, result.OmittedTraceCount);
        Assert.AreEqual(5, parsedResult.OmittedTraceCount);
        Assert.AreEqual(5, parsedResult.OvertureDivisions.OmittedCandidateCount);
        Assert.AreEqual(5, parsedResult.GadmDivisions.OmittedCandidateCount);
        Assert.AreEqual(5, parsedResult.AirportInfrastructure.OmittedCandidateCount);
        Assert.AreEqual(5, parsedResult.LiveOverturePlaces.OmittedCandidateCount);
        Assert.IsGreaterThan(0, candidate.OmittedRecordSourceCount);
    }

    [TestMethod]
    public async Task OptInStdinSource_AcceptsTypedCoordinateLeaseWithoutChangingProductionRegistry()
    {
        var request = new CoordinateLookupRequest(12.5, 45.25, false, true, false, EmptyOverrides());
        byte[] content = WorkerJobProtocolCodec.SerializeControllerInput(Execute(request));
        byte[] frame = [.. content, (byte)'\n'];
        var input = new PendingAfterFrameInputStream(frame);
        await using var source = new WorkerStdinRequestSource(
            new MemoryInputFactory(input),
            NullLogger<WorkerStdinRequestSource>.Instance,
            InternalWorkerProtocolVersion.V2,
            [WorkerJobDescriptors.CoordinateLookup]);

        InitialProcessingRunAcquisition acquisition =
            await source.AcquireAsync(CancellationToken.None);
        var accepted = Assert.IsInstanceOfType<InitialProcessingRunAcquisition.Accepted>(acquisition);
        try
        {
            Assert.AreEqual(JobId, accepted.Lease.Context.JobId);
            Assert.AreEqual(WorkerJobKind.CoordinateLookup, accepted.Lease.Context.JobKind);
            var acceptedRequest =
                Assert.IsInstanceOfType<CoordinateLookupRequest>(accepted.Lease.JobRequest);
            Assert.AreEqual(request.Latitude, acceptedRequest.Latitude);
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = accepted.Lease.Request);

            await input.PendingRead.WaitAsync(Bound);
            ValueTask<WorkerInputPumpFinality> settlement =
                accepted.Lease.SettleAsync(CancellationToken.None);
            await input.CancellationObserved.WaitAsync(Bound);
            Assert.IsInstanceOfType<WorkerInputPumpFinality.ExpectedShutdownFinality>(
                await settlement.AsTask().WaitAsync(Bound));
        }
        finally
        {
            await accepted.Lease.DisposeAsync();
        }

        Assert.AreEqual(1, input.DisposeCount);
    }

    private static WorkerJobControllerMessage Execute(CoordinateLookupRequest request) =>
        new(
            WorkerJobProtocolV2.RequestCategory,
            WorkerJobProtocolV2.ExecuteType,
            1,
            Started,
            JobId,
            WorkerJobKind.CoordinateLookup,
            new CoordinateLookupExecutePayload(request));

    private static CoordinateLookupCityResolverOverrides EmptyOverrides() =>
        new(null, []);

    private static CoordinateLookupResult Result(
        CoordinateLookupRequest request,
        IReadOnlyList<string> trace,
        CoordinateLookupSourceResult? readySource = null,
        bool useReadySourceForEverySection = false)
    {
        CoordinateLookupSourceResult skipped = Source(CoordinateLookupSourceState.Skipped, [], null);
        CoordinateLookupSourceResult optional = useReadySourceForEverySection
            ? readySource ?? skipped
            : skipped;
        return new CoordinateLookupResult(
            request,
            Started,
            Ended,
            new CoordinateLookupCountryResult(
                CoordinateLookupCountryStatus.NoMatch,
                null,
                null,
                null,
                null,
                null),
            readySource ?? skipped,
            optional,
            optional,
            optional,
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupProfileSummary(null, [], CoordinateLookupTieBreak.SmallestArea),
            trace,
            new CoordinateLookupFinalLocation(null, null, null));
    }

    private static CoordinateLookupSourceResult Source(
        CoordinateLookupSourceState state,
        IReadOnlyList<CoordinateLookupCandidate> candidates,
        string? text) =>
        new(
            state,
            text,
            text,
            null,
            candidates,
            [],
            null,
            text,
            text,
            text);

    private static CoordinateLookupCandidate Candidate() =>
        new(
            "candidate-1",
            "Candidate One",
            true,
            "selected",
            true,
            true,
            "division",
            "locality",
            null,
            null,
            "land",
            8,
            true,
            false,
            "US",
            null,
            null,
            null,
            null,
            null,
            1,
            ["fixture-source"]);

    private static void RemovePath(JsonNode root, string path)
    {
        string[] segments = path.Split('/');
        JsonNode current = root;
        for (int index = 0; index < segments.Length - 1; index++)
        {
            current = int.TryParse(segments[index], out int arrayIndex)
                ? current[arrayIndex]!
                : current[segments[index]]!;
        }

        if (int.TryParse(segments[^1], out int finalArrayIndex))
        {
            current.AsArray().RemoveAt(finalArrayIndex);
        }
        else
        {
            current.AsObject().Remove(segments[^1]);
        }
    }

    private sealed class MemoryInputFactory(Stream input) : IWorkerStandardInputStreamFactory
    {
        public Stream OpenStandardInput() => input;
    }

    private sealed class PendingAfterFrameInputStream(byte[] frame) : Stream
    {
        private int _position;
        private int _disposeCount;

        internal Task PendingRead => _pendingRead.Task;
        internal Task CancellationObserved => _cancellationObserved.Task;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        private readonly TaskCompletionSource _pendingRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int remaining = frame.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int copied = Math.Min(remaining, count);
            frame.AsSpan(_position, copied).CopyTo(buffer.AsSpan(offset, copied));
            _position += copied;
            return copied;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int remaining = frame.Length - _position;
            if (remaining > 0)
            {
                int copied = Math.Min(remaining, buffer.Length);
                frame.AsMemory(_position, copied).CopyTo(buffer);
                _position += copied;
                return copied;
            }

            _pendingRead.TrySetResult();
            var cancelled = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                _cancellationObserved.TrySetResult();
                cancelled.TrySetCanceled(cancellationToken);
            });
            try
            {
                return await cancelled.Task;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref _disposeCount);
            }

            base.Dispose(disposing);
        }
    }
}
