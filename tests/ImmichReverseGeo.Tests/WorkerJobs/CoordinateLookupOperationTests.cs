using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Gadm.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Tests.WorkerJobs;

[TestClass]
[TestCategory("Change48")]
public sealed class CoordinateLookupOperationTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 9, 8, 18, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ExecuteAsync_MatchedCountryUsesActualOvertureSelectionAndDisablesOptionalSources()
    {
        var sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("USA", "United States", "US", "country-us"),
            Overture = OvertureDiagnostics()
        };
        var reporter = new RecordingReporter();
        var operation = new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1)));

        CoordinateLookupResult result = await operation.ExecuteAsync(
            Request(),
            Started,
            reporter,
            CancellationToken.None);

        Assert.AreEqual(CoordinateLookupCountryStatus.Matched, result.Country.Status);
        Assert.AreEqual("Washington", result.OvertureAdministrative.State);
        Assert.AreEqual("Seattle", result.OvertureAdministrative.City);
        Assert.AreEqual("Seattle", result.FinalLocation.City!.Value);
        Assert.AreEqual(
            CoordinateLookupFinalSource.CachedOvertureDivisions,
            result.FinalLocation.City.Source);
        Assert.AreEqual(CoordinateLookupSourceState.Disabled, result.GadmDivisions.State);
        Assert.AreEqual(CoordinateLookupSourceState.Disabled, result.AirportInfrastructure.State);
        Assert.AreEqual(CoordinateLookupSourceState.Disabled, result.LiveOverturePlaces.State);
        Assert.AreEqual(1, sources.CountryCalls);
        Assert.AreEqual(1, sources.OvertureCacheCalls);
        Assert.AreEqual(1, sources.OvertureQueryCalls);
        Assert.AreEqual(0, sources.GadmCacheCalls);
        Assert.AreEqual(0, sources.AirportCalls);
        Assert.AreEqual(0, sources.PlacesCalls);
        CollectionAssert.Contains(
            reporter.Payloads.Select(static payload => payload.GetType()).ToArray(),
            typeof(CoordinateLookupProgressPayload));
    }

    [TestMethod]
    public async Task ExecuteAsync_NoCountryStartsNoCacheOrOptionalSourceWork()
    {
        var sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.SpatialNoMatch("fixture no match")
        };
        var operation = new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1)));

        CoordinateLookupResult result = await operation.ExecuteAsync(
            Request(includeAirport: true, includePlaces: true, preferGadm: true),
            Started,
            new RecordingReporter(),
            CancellationToken.None);

        Assert.AreEqual(CoordinateLookupCountryStatus.NoMatch, result.Country.Status);
        Assert.IsNull(result.FinalLocation.Country);
        Assert.IsNull(result.FinalLocation.State);
        Assert.IsNull(result.FinalLocation.City);
        Assert.AreEqual(CoordinateLookupSourceState.Skipped, result.OvertureDivisions.State);
        Assert.AreEqual(CoordinateLookupSourceState.Skipped, result.GadmDivisions.State);
        Assert.AreEqual(CoordinateLookupSourceState.Skipped, result.AirportInfrastructure.State);
        Assert.AreEqual(CoordinateLookupSourceState.Skipped, result.LiveOverturePlaces.State);
        Assert.AreEqual(0, sources.OvertureCacheCalls);
        Assert.AreEqual(0, sources.GadmCacheCalls);
        Assert.AreEqual(0, sources.AirportCalls);
        Assert.AreEqual(0, sources.PlacesCalls);
    }

    [TestMethod]
    public async Task ExecuteAsync_CountryIdentityMappingFailureStartsNoDownstreamWork()
    {
        var sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.IdentityMappingFailure(
                "Fixture territory",
                "FT",
                "country-fixture",
                "fixture identity mapping failure")
        };
        CoordinateLookupResult result = await new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1))).ExecuteAsync(
                Request(includeAirport: true, includePlaces: true, preferGadm: true),
                Started,
                new RecordingReporter(),
                CancellationToken.None);

        Assert.AreEqual(CoordinateLookupCountryStatus.MappingFailed, result.Country.Status);
        Assert.AreEqual("country-identity-failed", result.Country.Error!.Code);
        Assert.IsNull(result.FinalLocation.Country);
        Assert.IsNull(result.FinalLocation.State);
        Assert.IsNull(result.FinalLocation.City);
        Assert.AreEqual(0, sources.OvertureCacheCalls);
        Assert.AreEqual(0, sources.GadmCacheCalls);
        Assert.AreEqual(0, sources.AirportCalls);
        Assert.AreEqual(0, sources.PlacesCalls);
    }

    [TestMethod]
    public async Task ExecuteAsync_CountryFailureEscapesInsteadOfBecomingNoMatch()
    {
        var expected = new InvalidOperationException("fixture country failure");
        var sources = new RecordingSources { CountryFailure = expected };
        var operation = new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1)));

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => operation.ExecuteAsync(
                Request(includeAirport: true, includePlaces: true, preferGadm: true),
                Started,
                new RecordingReporter(),
                CancellationToken.None));

        Assert.AreSame(expected, actual);
        Assert.AreEqual(0, sources.OvertureCacheCalls);
        Assert.AreEqual(0, sources.GadmCacheCalls);
        Assert.AreEqual(0, sources.AirportCalls);
        Assert.AreEqual(0, sources.PlacesCalls);
    }

    [TestMethod]
    public async Task ExecuteAsync_CancellationDuringFirstCacheUnwindsActivityAndStartsNoOptionalWork()
    {
        using var cancellation = new CancellationTokenSource();
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingSources? sources = null;
        Task? rawCacheTask = null;
        sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("USA", "United States", "US", "country-us"),
            StartOvertureCache = token =>
            {
                rawCacheTask = ObserveCancellationAsync(
                    token,
                    cancellationObserved,
                    releaseCleanup,
                    () => sources!.CacheTaskUnwound = true);
                cancellation.Cancel();
                return (rawCacheTask, OvertureDivisionEnsureResult.StartedDownload);
            }
        };
        var reporter = new RecordingReporter(respectCancellation: false);
        var operation = new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1)),
            () => Guid.Parse("11111111-2222-3333-4444-555555555555"));

        Task<CoordinateLookupResult> lookup = operation.ExecuteAsync(
            Request(includeAirport: true, includePlaces: true, preferGadm: true),
            Started,
            reporter,
            cancellation.Token);
        Exception? completionFailure = null;
        try
        {
            await cancellationObserved.Task;
            Assert.IsFalse(lookup.IsCompleted, "The owner must await cache cleanup before unwinding.");
        }
        finally
        {
            releaseCleanup.TrySetResult();
            try
            {
                await lookup;
            }
            catch (Exception ex)
            {
                completionFailure = ex;
            }

            if (rawCacheTask is not null)
            {
                try
                {
                    await rawCacheTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        Assert.IsInstanceOfType<OperationCanceledException>(completionFailure);

        Assert.IsTrue(sources.CacheTaskUnwound);
        Assert.AreEqual(0, sources.GadmCacheCalls);
        Assert.AreEqual(0, sources.AirportCalls);
        Assert.AreEqual(0, sources.PlacesCalls);
        Assert.AreEqual(1, reporter.Payloads.OfType<WorkerJobActivityStartedPayload>().Count());
        Assert.AreEqual(1, reporter.Payloads.OfType<WorkerJobActivityEndedPayload>().Count());
    }

    [TestMethod]
    public async Task ExecuteAsync_CombinedSourcesPreserveTerritoryFallbackFieldSelectionAndPlacesDiagnostics()
    {
        var sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("DNK", "Denmark", "DK", "country-dk"),
            Overture = OvertureDiagnostics("Capital Region", "Copenhagen"),
            Gadm = GadmDiagnostics("Hovedstaden"),
            Airport = AirportDiagnostics("Copenhagen Airport", geometryContains: true),
            Places = PlacesDiagnostics("Diagnostic cafe")
        };
        var operation = new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1)));

        CoordinateLookupResult result = await operation.ExecuteAsync(
            Request(includeAirport: true, includePlaces: true, preferGadm: true),
            Started,
            new RecordingReporter(),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "DNK", "GRL", "FRO" },
            sources.GadmCacheCodes.ToArray());
        Assert.AreEqual("Hovedstaden", result.FinalLocation.State!.Value);
        Assert.AreEqual(
            CoordinateLookupFinalSource.CachedGadmDivisions,
            result.FinalLocation.State.Source);
        Assert.AreEqual("Copenhagen Airport", result.FinalLocation.City!.Value);
        Assert.AreEqual(
            CoordinateLookupFinalSource.BundledAirportGeometryMatch,
            result.FinalLocation.City.Source);
        Assert.AreEqual("Diagnostic cafe", result.LiveOverturePlaces.BestMatch!.Name);
        Assert.AreNotEqual(
            result.LiveOverturePlaces.BestMatch.Name,
            result.FinalLocation.City.Value);
        Assert.AreEqual(CoordinateLookupGadmAttribution.LicenseUrl, result.GadmDivisions.LicenseUrl);
        Assert.AreEqual(CoordinateLookupGadmAttribution.UsageNotice, result.GadmDivisions.UsageNotice);
    }

    [TestMethod]
    public async Task ExecuteAsync_NonContainingAirportLeavesFieldByFieldAdministrativeFallback()
    {
        var sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("DNK", "Denmark", "DK", "country-dk"),
            Overture = OvertureDiagnostics("Capital Region", "Copenhagen"),
            Gadm = GadmDiagnostics("Hovedstaden"),
            Airport = AirportDiagnostics("Copenhagen Airport", geometryContains: false)
        };
        var operation = new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1)));

        CoordinateLookupResult result = await operation.ExecuteAsync(
            Request(includeAirport: true, preferGadm: true),
            Started,
            new RecordingReporter(),
            CancellationToken.None);

        Assert.AreEqual("Hovedstaden", result.FinalLocation.State!.Value);
        Assert.AreEqual("Copenhagen", result.FinalLocation.City!.Value);
        Assert.AreEqual(
            CoordinateLookupFinalSource.CachedOvertureDivisions,
            result.FinalLocation.City.Source);
    }

    [TestMethod]
    public async Task ExecuteAsync_FinalCityUsesAirportFallbackThenStateThenCountry()
    {
        var airportFallbackSources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("DNK", "Denmark", "DK", "country-dk"),
            Overture = EmptyOvertureDiagnostics(),
            Airport = AirportDiagnostics("Copenhagen Airport", geometryContains: false)
        };
        CoordinateLookupResult airportFallback = await new CoordinateLookupOperation(
            airportFallbackSources,
            new FixedTimeProvider(Started.AddSeconds(1))).ExecuteAsync(
                Request(includeAirport: true),
                Started,
                new RecordingReporter(),
                CancellationToken.None);
        Assert.AreEqual("Copenhagen Airport", airportFallback.FinalLocation.City!.Value);
        Assert.AreEqual(
            CoordinateLookupFinalSource.BundledAirportFallback,
            airportFallback.FinalLocation.City.Source);

        var stateFallbackSources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("DNK", "Denmark", "DK", "country-dk"),
            Overture = OvertureStateOnlyDiagnostics("Capital Region")
        };
        CoordinateLookupResult stateFallback = await new CoordinateLookupOperation(
            stateFallbackSources,
            new FixedTimeProvider(Started.AddSeconds(1))).ExecuteAsync(
                Request(),
                Started,
                new RecordingReporter(),
                CancellationToken.None);
        Assert.AreEqual("Capital Region", stateFallback.FinalLocation.City!.Value);
        Assert.AreEqual(
            CoordinateLookupFinalSource.CachedOvertureDivisions,
            stateFallback.FinalLocation.City.Source);

        var countryFallbackSources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("DNK", "Denmark", "DK", "country-dk"),
            Overture = EmptyOvertureDiagnostics()
        };
        CoordinateLookupResult countryFallback = await new CoordinateLookupOperation(
            countryFallbackSources,
            new FixedTimeProvider(Started.AddSeconds(1))).ExecuteAsync(
                Request(),
                Started,
                new RecordingReporter(),
                CancellationToken.None);
        Assert.AreEqual("Denmark", countryFallback.FinalLocation.City!.Value);
        Assert.AreEqual(
            CoordinateLookupFinalSource.BundledCountryDivisions,
            countryFallback.FinalLocation.City.Source);
    }

    [TestMethod]
    public async Task ExecuteAsync_GadmUnavailableRetainsLicenseAndFallsBackToOverture()
    {
        var sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("DNK", "Denmark", "DK", "country-dk"),
            Overture = OvertureDiagnostics("Capital Region", "Copenhagen"),
            GadmCachesReady = false
        };
        CoordinateLookupResult result = await new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1))).ExecuteAsync(
                Request(preferGadm: true),
                Started,
                new RecordingReporter(),
                CancellationToken.None);

        Assert.AreEqual(CoordinateLookupSourceState.Unavailable, result.GadmDivisions.State);
        Assert.AreEqual(GadmDivisionsLogic.DatasetVersion, result.GadmDivisions.Version);
        Assert.AreEqual(CoordinateLookupGadmAttribution.LicenseUrl, result.GadmDivisions.LicenseUrl);
        Assert.AreEqual(CoordinateLookupGadmAttribution.UsageNotice, result.GadmDivisions.UsageNotice);
        Assert.AreEqual(3, result.GadmDivisions.Caches.Count);
        Assert.IsTrue(result.GadmDivisions.Caches.All(
            static cache => cache.State == CoordinateLookupSourceState.Unavailable
                && cache.Error?.Code == "gadm-cache-unavailable"));
        Assert.AreEqual(0, sources.GadmQueryCalls);
        Assert.AreEqual("Capital Region", result.FinalLocation.State!.Value);
        Assert.AreEqual("Copenhagen", result.FinalLocation.City!.Value);
        Assert.AreEqual(
            CoordinateLookupFinalSource.CachedOvertureDivisions,
            result.FinalLocation.State.Source);
    }

    [TestMethod]
    public async Task ExecuteAsync_GadmNoMatchRetainsLicenseAndFallsBackToOverture()
    {
        var sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("DNK", "Denmark", "DK", "country-dk"),
            Overture = OvertureDiagnostics("Capital Region", "Copenhagen")
        };
        CoordinateLookupResult result = await new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1))).ExecuteAsync(
                Request(preferGadm: true),
                Started,
                new RecordingReporter(),
                CancellationToken.None);

        Assert.AreEqual(CoordinateLookupSourceState.NoMatch, result.GadmDivisions.State);
        Assert.AreEqual(GadmDivisionsLogic.DatasetVersion, result.GadmDivisions.Version);
        Assert.AreEqual(CoordinateLookupGadmAttribution.LicenseUrl, result.GadmDivisions.LicenseUrl);
        Assert.AreEqual(CoordinateLookupGadmAttribution.UsageNotice, result.GadmDivisions.UsageNotice);
        Assert.AreEqual(1, sources.GadmQueryCalls);
        Assert.AreEqual("Capital Region", result.FinalLocation.State!.Value);
        Assert.AreEqual("Copenhagen", result.FinalLocation.City!.Value);
    }

    [TestMethod]
    public async Task ExecuteAsync_AttemptedQueryFailuresAreFailedAndRetainCountryFallback()
    {
        var sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("DNK", "Denmark", "DK", "country-dk"),
            OvertureFailure = new InvalidOperationException("overture query failure"),
            GadmFailure = new InvalidOperationException("gadm query failure"),
            AirportFailure = new InvalidOperationException("airport query failure"),
            PlacesFailure = new InvalidOperationException("places query failure")
        };
        CoordinateLookupResult result = await new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1))).ExecuteAsync(
                Request(includeAirport: true, includePlaces: true, preferGadm: true),
                Started,
                new RecordingReporter(),
                CancellationToken.None);

        Assert.AreEqual(CoordinateLookupSourceState.Failed, result.OvertureDivisions.State);
        Assert.AreEqual(CoordinateLookupSourceState.Failed, result.GadmDivisions.State);
        Assert.AreEqual(CoordinateLookupSourceState.Failed, result.AirportInfrastructure.State);
        Assert.AreEqual(CoordinateLookupSourceState.Failed, result.LiveOverturePlaces.State);
        Assert.AreEqual("Denmark", result.FinalLocation.Country!.Value);
        Assert.IsNull(result.FinalLocation.State);
        Assert.AreEqual("Denmark", result.FinalLocation.City!.Value);
        Assert.AreEqual(
            CoordinateLookupFinalSource.BundledCountryDivisions,
            result.FinalLocation.City.Source);
    }

    [TestMethod]
    public async Task ExecuteAsync_OverlongSourceDiagnosticsAreTrimmedIntoAValidTerminal()
    {
        string longText = new('"', CoordinateLookupProtocolBounds.MaxDisplayTextLength + 257);
        var sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("DNK", longText, "DK", "country-dk"),
            Overture = OvertureDiagnostics(longText, longText),
            Gadm = GadmDiagnostics(longText),
            Airport = AirportDiagnostics(longText, geometryContains: true, source: longText),
            Places = PlacesDiagnostics(longText, source: longText)
        };
        CoordinateLookupResult result = await new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1))).ExecuteAsync(
                Request(includeAirport: true, includePlaces: true, preferGadm: true),
                Started,
                new RecordingReporter(),
                CancellationToken.None);
        var terminal = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            1,
            Started.AddSeconds(1),
            Guid.Parse("cccccccc-bbbb-aaaa-dddd-eeeeeeeeeeee"),
            WorkerJobKind.CoordinateLookup,
            new WorkerJobTerminalPayload(
                            WorkerJobTerminalOutcome.Completed,
                Started,
                Started.AddSeconds(1),
                null,
                result,
                null));

        byte[] frame = WorkerJobProtocolCodec.Serialize(terminal);
        Assert.IsTrue(result.TruncatedTextCount > 0);
        Assert.IsTrue(result.OvertureDivisions.TruncatedTextCount > 0);
        Assert.IsTrue(result.GadmDivisions.TruncatedTextCount > 0);
        Assert.IsTrue(result.AirportInfrastructure.TruncatedTextCount > 0);
        Assert.IsTrue(result.LiveOverturePlaces.TruncatedTextCount > 0);
        Assert.IsTrue(frame.Length <= WorkerProtocolV1.MaxMessageBytes);
        Assert.IsTrue(WorkerJobProtocolCodec.Parse(frame).IsSuccess);
    }

    [TestMethod]
    public async Task Handler_OwnsStartUsesOperationAndHasNoAssetPersistenceDependency()
    {
        var sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("USA", "United States", "US", "country-us"),
            Overture = OvertureDiagnostics()
        };
        var time = new FixedTimeProvider(Started);
        var reporter = new RecordingReporter();
        var handler = new CoordinateLookupWorkerJobHandler(
            new CoordinateLookupOperation(sources, time),
            time);
        var context = new WorkerJobContext(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            WorkerJobKind.CoordinateLookup,
            WorkerJobRequestOrigin.Manual);

        CoordinateLookupResult result = await handler.ExecuteAsync(
            context,
            Request(),
            reporter,
            CancellationToken.None);

        Assert.AreEqual(1, reporter.StartCalls);
        Assert.AreEqual("manual", reporter.Trigger);
        Assert.AreEqual(Started, result.StartedAtUtc);
        Type[] dependencies = typeof(CoordinateLookupWorkerJobHandler)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single()
            .GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { typeof(CoordinateLookupOperation), typeof(TimeProvider) },
            dependencies,
            "handler-has-no-asset-exif-skipped-or-advisory-lock-dependency");
    }

    [TestMethod]
    public async Task ExecuteAsync_ActualOvertureCancellationBeforePublicationCleansTempAndPublishesNoCache()
    {
        string tempDir = CreateTempDir();
        var enteredPublication = NewGate();
        var releasePublication = NewGate();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var cache = CreateActualCache(
                tempDir,
                beforePublication: async _ =>
                {
                    enteredPublication.TrySetResult();
                    await releasePublication.Task;
                });
            var sources = new RecordingSources
            {
                Country = BundledCountryLookupResult.Matched("CHE", "Switzerland", "CH", "country-ch"),
                ActualOvertureCache = cache,
                Overture = OvertureDiagnostics("Zurich", "Zurich")
            };
            var operation = new CoordinateLookupOperation(
                sources,
                new FixedTimeProvider(Started.AddSeconds(1)));
            Task<CoordinateLookupResult> lookup = operation.ExecuteAsync(
                Request(),
                Started,
                new RecordingReporter(respectCancellation: false),
                cancellation.Token);
            Exception? completionFailure = null;
            try
            {
                await enteredPublication.Task;
                cancellation.Cancel();
            }
            finally
            {
                cancellation.Cancel();
                releasePublication.TrySetResult();
                try
                {
                    await lookup;
                }
                catch (Exception ex)
                {
                    completionFailure = ex;
                }
            }

            Assert.IsInstanceOfType<OperationCanceledException>(completionFailure);
            string directory = Path.Combine(tempDir, "overture-divisions");
            Assert.IsFalse(File.Exists(Path.Combine(directory, "CHE.db")));
            Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.tmp").Length);
            Assert.AreEqual(0, sources.OvertureQueryCalls);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_ActualPublishedOvertureCacheSurvivesLaterOptionalCancellationAndAllWorkUnwinds()
    {
        string tempDir = CreateTempDir();
        var cachePublished = NewGate();
        var releaseCache = NewGate();
        var airportStarted = NewGate();
        var airportCancelled = NewGate();
        var releaseAirportCleanup = NewGate();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var cache = CreateActualCache(
                tempDir,
                afterPublication: async _ =>
                {
                    cachePublished.TrySetResult();
                    await releaseCache.Task;
                });
            var sources = new RecordingSources
            {
                Country = BundledCountryLookupResult.Matched("CHE", "Switzerland", "CH", "country-ch"),
                ActualOvertureCache = cache,
                Overture = OvertureDiagnostics("Zurich", "Zurich"),
                AirportOperation = token => GatedAirportAsync(
                    token,
                    airportStarted,
                    airportCancelled,
                    releaseAirportCleanup)
            };
            var reporter = new RecordingReporter(respectCancellation: false);
            var operation = new CoordinateLookupOperation(
                sources,
                new FixedTimeProvider(Started.AddSeconds(1)),
                () => Guid.Parse("99999999-2222-3333-4444-555555555555"));
            Task<CoordinateLookupResult> lookup = operation.ExecuteAsync(
                Request(includeAirport: true),
                Started,
                reporter,
                cancellation.Token);
            Exception? completionFailure = null;
            try
            {
                await Task.WhenAll(cachePublished.Task, airportStarted.Task);
                string dbPath = Path.Combine(tempDir, "overture-divisions", "CHE.db");
                Assert.IsTrue(File.Exists(dbPath), "The cache must be atomically visible before cancellation.");
                byte[] publishedBytes = await File.ReadAllBytesAsync(dbPath);
                cancellation.Cancel();
                releaseCache.TrySetResult();
                await airportCancelled.Task;
                Assert.IsFalse(lookup.IsCompleted, "The operation must await optional-source cleanup.");
                releaseAirportCleanup.TrySetResult();
                try
                {
                    await lookup;
                }
                catch (Exception ex)
                {
                    completionFailure = ex;
                }

                CollectionAssert.AreEqual(publishedBytes, await File.ReadAllBytesAsync(dbPath));
                var verifier = new OvertureDivisionCacheService(
                    NullLogger<OvertureDivisionCacheService>.Instance,
                    tempDir,
                    _ => "CH");
                Assert.IsTrue(verifier.HasData("CHE"), "The published cache remains byte-valid.");
                var places = new OverturePlacesService(
                    NullLogger<OverturePlacesService>.Instance,
                    tempDir,
                    tempDir);
                var divisions = new OvertureDivisionsService(
                    NullLogger<OvertureDivisionsService>.Instance,
                    places,
                    tempDir,
                    tempDir,
                    _ => "CHE");
                OvertureDivisionLookupDiagnostics readable =
                    await divisions.FindContainingDivisionAreasAsync(47, 8, "CH", "CHE");
                Assert.AreEqual("fixture", readable.BestMatch?.Id);
                Assert.AreEqual("Zurich", readable.BestMatch?.Name);
            }
            finally
            {
                cancellation.Cancel();
                releaseCache.TrySetResult();
                releaseAirportCleanup.TrySetResult();
                try
                {
                    await lookup;
                }
                catch (Exception ex)
                {
                    completionFailure ??= ex;
                }
            }

            Assert.IsInstanceOfType<OperationCanceledException>(completionFailure);
            Assert.AreEqual(0, sources.OvertureQueryCalls, "Cancellation starts no later query work.");
            Assert.AreEqual(0, sources.PlacesCalls, "Cancellation starts no later optional work.");
            Assert.AreEqual(1, reporter.Payloads.OfType<WorkerJobActivityStartedPayload>().Count());
            Assert.AreEqual(1, reporter.Payloads.OfType<WorkerJobActivityEndedPayload>().Count());
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_OrdinaryOwnedCacheFailureCompletesAsUnavailableDegradation()
    {
        var sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("USA", "United States", "US", "country-us"),
            StartOvertureCache = _ => (
                Task.FromException(new InvalidOperationException("source fixture failure")),
                OvertureDivisionEnsureResult.StartedDownload)
        };
        var operation = new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1)));

        CoordinateLookupResult result = await operation.ExecuteAsync(
            Request(),
            Started,
            new RecordingReporter(),
            CancellationToken.None);

        Assert.AreEqual(CoordinateLookupSourceState.Unavailable, result.OvertureDivisions.State);
        Assert.AreEqual("overture-cache-unavailable", result.OvertureDivisions.Error!.Code);
        Assert.AreEqual(0, sources.OvertureQueryCalls);
    }

    [TestMethod]
    public async Task ExecuteAsync_ReporterFailureAfterOwnedCacheAcquisitionWaitsForTaskCleanupAndEscapes()
    {
        var release = NewGate();
        var taskStarted = NewGate();
        Task? rawTask = null;
        var expected = new IOException("reporter fixture failure");
        var sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("USA", "United States", "US", "country-us"),
            StartOvertureCache = _ =>
            {
                rawTask = WaitForReleaseAsync(taskStarted, release);
                return (rawTask, OvertureDivisionEnsureResult.StartedDownload);
            }
        };
        var reporter = new RecordingReporter(
            failure: payload => payload is WorkerJobActivityStartedPayload ? expected : null);
        var operation = new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1)));
        Task<CoordinateLookupResult> lookup = operation.ExecuteAsync(
            Request(),
            Started,
            reporter,
            CancellationToken.None);
        Exception? completionFailure = null;
        try
        {
            await taskStarted.Task;
            Assert.IsFalse(lookup.IsCompleted, "Reporter failure must not abandon an owned cache task.");
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await lookup;
            }
            catch (Exception ex)
            {
                completionFailure = ex;
            }
            if (rawTask is not null)
            {
                await rawTask;
            }
        }

        Assert.IsNotNull(completionFailure);
        Assert.AreSame(expected, completionFailure.InnerException);
    }

    [TestMethod]
    public async Task ExecuteAsync_OwnedCacheOutOfMemoryOverridesEarlierReporterFailure()
    {
        var expected = new OutOfMemoryException("cache fixture fatal");
        var sources = new RecordingSources
        {
            Country = BundledCountryLookupResult.Matched("USA", "United States", "US", "country-us"),
            StartOvertureCache = _ => (
                Task.FromException(expected),
                OvertureDivisionEnsureResult.StartedDownload)
        };
        var reporter = new RecordingReporter(
            failure: payload => payload is WorkerJobActivityStartedPayload
                ? new IOException("reporter fixture failure")
                : null);
        var operation = new CoordinateLookupOperation(
            sources,
            new FixedTimeProvider(Started.AddSeconds(1)));

        OutOfMemoryException actual = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            operation.ExecuteAsync(
                Request(),
                Started,
                reporter,
                CancellationToken.None));

        Assert.AreSame(expected, actual);
    }

    private static async Task WaitForReleaseAsync(
        TaskCompletionSource started,
        TaskCompletionSource release)
    {
        started.TrySetResult();
        await release.Task;
    }

    private static async Task<OvertureInfrastructureLookupDiagnostics> GatedAirportAsync(
        CancellationToken token,
        TaskCompletionSource started,
        TaskCompletionSource cancelled,
        TaskCompletionSource releaseCleanup)
    {
        started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new AssertFailedException("The gated airport source was not cancelled.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            cancelled.TrySetResult();
            await releaseCleanup.Task;
            throw;
        }
    }

    private static OvertureDivisionCacheService CreateActualCache(
        string tempDir,
        Func<CancellationToken, Task>? beforePublication = null,
        Func<CancellationToken, Task>? afterPublication = null) =>
        new(
            NullLogger<OvertureDivisionCacheService>.Instance,
            tempDir,
            _ => "CH",
            new OvertureDivisionCacheTestHooks
            {
                ExportOperation = (path, _, _) => ExportValidOvertureCache(path),
                BeforePublication = beforePublication,
                AfterPublication = afterPublication
            });

    private static long ExportValidOvertureCache(string path)
    {
        using var connection = OvertureDivisionCacheService.OpenTemporaryOutputConnection(path);
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
            VALUES ('fixture', 'Zurich', 'locality', 'land', 8, 'CH', 1, 0,
                    $geometry, 7, 46, 9, 48);
            INSERT INTO _meta VALUES ('downloadedAt', '2026-09-08T00:00:00Z');
            INSERT INTO _meta VALUES ('release', 'fixture-release');
            """;
        command.Parameters.AddWithValue("$geometry", ValidPolygonWkb());
        command.ExecuteNonQuery();
        return 1;
    }

    private static byte[] ValidPolygonWkb()
    {
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var polygon = factory.CreatePolygon(
        [
            new Coordinate(7, 46),
            new Coordinate(9, 46),
            new Coordinate(9, 48),
            new Coordinate(7, 48),
            new Coordinate(7, 46)
        ]);
        return new WKBWriter().Write(polygon);
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ObserveCancellationAsync(
        CancellationToken token,
        TaskCompletionSource cancellationObserved,
        TaskCompletionSource releaseCleanup,
        Action unwound)
    {
        try
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                await releaseCleanup.Task;
                throw;
            }
        }
        finally
        {
            unwound();
        }
    }

    private static CoordinateLookupRequest Request(
        bool includeAirport = false,
        bool includePlaces = false,
        bool preferGadm = false) =>
        new(
            47.6062,
            -122.3321,
            includeAirport,
            includePlaces,
            preferGadm,
            new CoordinateLookupCityResolverOverrides(null, []));

    private static OvertureDivisionLookupDiagnostics OvertureDiagnostics(
        string stateName = "Washington",
        string cityName = "Seattle")
    {
        var state = new OvertureDivisionCandidateDiagnostic(
            "state-wa",
            stateName,
            "region",
            null,
            4,
            "US",
            true,
            false,
            true,
            true,
            100,
            false,
            "state candidate");
        var city = new OvertureDivisionCandidateDiagnostic(
            "city-seattle",
            cityName,
            "locality",
            null,
            8,
            "US",
            true,
            false,
            true,
            true,
            5,
            true,
            "city candidate");
        return new OvertureDivisionLookupDiagnostics(
            new OvertureDivisionResult(
                city.Id,
                city.Name,
                city.SubType,
                city.ClassName,
                city.AdminLevel,
                city.Country,
                "USA",
                city.IsLand,
                city.IsTerritorial,
                city.BoundingBoxContainsPoint,
                city.GeometryContainsPoint,
                city.GeometryContainsPoint,
                city.BoundingBoxArea),
            [state, city],
            "2026-08-20.0");
    }

    private static OvertureDivisionLookupDiagnostics OvertureStateOnlyDiagnostics(string stateName)
    {
        var state = new OvertureDivisionCandidateDiagnostic(
            "state-only",
            stateName,
            "region",
            null,
            4,
            "DK",
            true,
            false,
            true,
            true,
            100,
            true,
            "selected state candidate");
        return new OvertureDivisionLookupDiagnostics(
            new OvertureDivisionResult(
                state.Id,
                state.Name,
                state.SubType,
                state.ClassName,
                state.AdminLevel,
                state.Country,
                "DNK",
                state.IsLand,
                state.IsTerritorial,
                state.BoundingBoxContainsPoint,
                state.GeometryContainsPoint,
                state.GeometryContainsPoint,
                state.BoundingBoxArea),
            [state],
            "2026-08-20.0");
    }

    private static OvertureDivisionLookupDiagnostics EmptyOvertureDiagnostics() =>
        new(null, [], "2026-08-20.0");

    private static GadmDivisionLookupDiagnostics GadmDiagnostics(string stateName)
    {
        var state = new GadmDivisionCandidateDiagnostic(
            "gadm-state",
            stateName,
            "Region",
            "Region",
            1,
            true,
            true,
            50,
            true,
            "selected state");
        return new GadmDivisionLookupDiagnostics(
            new GadmDivisionResult(
                state.Id,
                state.Name,
                state.EnglishType,
                state.LocalType,
                state.AdminLevel,
                state.BoundingBoxContainsPoint,
                state.GeometryContainsPoint,
                state.BoundingBoxArea),
            [state],
            GadmDivisionsLogic.DatasetVersion);
    }

    private static OvertureInfrastructureLookupDiagnostics AirportDiagnostics(
        string name,
        bool geometryContains,
        string source = "Overture")
    {
        var candidate = new OvertureInfrastructureCandidateDiagnostic(
            "airport-cph",
            name,
            "infrastructure",
            "airport",
            "international",
            800,
            true,
            geometryContains,
            [source],
            true,
            "selected airport");
        return new OvertureInfrastructureLookupDiagnostics(
            new OvertureInfrastructureResult(
                candidate.Id,
                candidate.Name,
                candidate.FeatureType,
                candidate.SubType,
                candidate.ClassName,
                candidate.DistanceMetres,
                candidate.BoundingBoxContainsPoint,
                candidate.GeometryContainsPoint,
                candidate.Sources),
            [candidate],
            "2026-08-20.0");
    }

    private static OvertureLookupDiagnostics PlacesDiagnostics(
        string name,
        string source = "Overture")
    {
        var candidate = new OvertureCandidateDiagnostic(
            "place-cafe",
            name,
            "cafe",
            "food_and_drink",
            0.9,
            "open",
            12,
            true,
            [source],
            true,
            "selected place");
        return new OvertureLookupDiagnostics(
            new OverturePlaceResult(
                candidate.Id,
                candidate.Name,
                candidate.Category,
                candidate.BasicCategory,
                candidate.Confidence,
                candidate.OperatingStatus,
                candidate.DistanceMetres,
                candidate.BoundingBoxContainsPoint,
                candidate.Sources),
            [candidate],
            "2026-08-20.0",
            "DK");
    }

    private sealed class RecordingReporter(
        bool respectCancellation = true,
        Func<WorkerJobOutputPayload, Exception?>? failure = null) :
        ICoordinateLookupEventReporter,
        IWorkerJobHostReporter
    {
        internal List<WorkerJobOutputPayload> Payloads { get; } = [];
        internal int StartCalls { get; private set; }
        internal string? Trigger { get; private set; }
        public DateTimeOffset? StartedAtUtc { get; private set; }

        public ValueTask ReportAsync(
            WorkerJobOutputPayload payload,
            CancellationToken cancellationToken)
        {
            if (respectCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            Exception? exception = failure?.Invoke(payload);
            if (exception is not null)
            {
                throw exception;
            }
            Payloads.Add(payload);
            return ValueTask.CompletedTask;
        }

        public ValueTask ReportAsync(
            WorkerJobHandlerEvent @event,
            CancellationToken cancellationToken) =>
            ReportAsync(@event.Payload, cancellationToken);

        public ValueTask ReportStartedAsync(
            string trigger,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (respectCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            StartCalls++;
            Trigger = trigger;
            StartedAtUtc = startedAtUtc;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingSources : ICoordinateLookupSources
    {
        internal BundledCountryLookupResult Country { get; init; } =
            BundledCountryLookupResult.SpatialNoMatch();
        internal OvertureDivisionLookupDiagnostics Overture { get; init; } =
            new(null, [], null);
        internal GadmDivisionLookupDiagnostics Gadm { get; init; } =
            new(null, [], GadmDivisionsLogic.DatasetVersion);
        internal OvertureInfrastructureLookupDiagnostics Airport { get; init; } =
            new(null, [], null);
        internal OvertureLookupDiagnostics Places { get; init; } =
            new(null, [], null, null);
        internal Exception? CountryFailure { get; init; }
        internal Exception? OvertureFailure { get; init; }
        internal Exception? GadmFailure { get; init; }
        internal Exception? AirportFailure { get; init; }
        internal Exception? PlacesFailure { get; init; }
        internal bool GadmCachesReady { get; init; } = true;
        internal Func<CancellationToken, (Task Task, OvertureDivisionEnsureResult Result)>?
            StartOvertureCache { get; init; }
        internal OvertureDivisionCacheService? ActualOvertureCache { get; init; }
        internal Func<CancellationToken, Task<OvertureInfrastructureLookupDiagnostics>>?
            AirportOperation { get; init; }

        internal int CountryCalls { get; private set; }
        internal int OvertureCacheCalls { get; private set; }
        internal int OvertureQueryCalls { get; private set; }
        internal int GadmCacheCalls { get; private set; }
        internal int GadmQueryCalls { get; private set; }
        internal int AirportCalls { get; private set; }
        internal int PlacesCalls { get; private set; }
        internal bool CacheTaskUnwound { get; set; }
        internal List<string> GadmCacheCodes { get; } = [];

        public Task<BundledCountryLookupResult> FindCountryAsync(
            double latitude,
            double longitude,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CountryCalls++;
            if (CountryFailure is not null)
            {
                return Task.FromException<BundledCountryLookupResult>(CountryFailure);
            }
            return Task.FromResult(Country);
        }

        public CityResolverProfile ResolveCityProfile(
            CoordinateLookupCityResolverOverrides overrides,
            string? iso3) =>
            new CityResolverProfileCatalog().GetProfile(
                CoordinateLookupCityProfileConversions.ToConfig(overrides),
                iso3);

        public IReadOnlyList<string> ExpandGadmCandidateCodes(string iso3) =>
            GadmCountryFallbackCatalog.ExpandCandidateCodes(iso3);

        public (Task Task, OvertureDivisionEnsureResult Result) GetOrStartOvertureCache(
            string iso3,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OvertureCacheCalls++;
            if (ActualOvertureCache is not null)
            {
                return ActualOvertureCache.GetOrStartDownload(iso3, cancellationToken);
            }
            if (StartOvertureCache is not null)
            {
                return StartOvertureCache(cancellationToken);
            }
            return (Task.CompletedTask, OvertureDivisionEnsureResult.AlreadyReady);
        }

        public bool HasOvertureCache(string iso3) =>
            ActualOvertureCache?.HasData(iso3) ?? true;

        public Task<OvertureDivisionLookupDiagnostics> FindOvertureDivisionsAsync(
            double latitude,
            double longitude,
            string alpha2,
            string iso3,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OvertureQueryCalls++;
            if (OvertureFailure is not null)
            {
                return Task.FromException<OvertureDivisionLookupDiagnostics>(OvertureFailure);
            }
            return Task.FromResult(Overture);
        }

        public (Task Task, GadmDivisionEnsureResult Result) GetOrStartGadmCache(
            string iso3,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GadmCacheCalls++;
            GadmCacheCodes.Add(iso3);
            return (Task.CompletedTask, GadmDivisionEnsureResult.AlreadyReady);
        }

        public bool HasGadmCache(string iso3) => GadmCachesReady;

        public Task<GadmDivisionLookupDiagnostics> FindGadmDivisionsAsync(
            double latitude,
            double longitude,
            IReadOnlyList<string> iso3Codes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GadmQueryCalls++;
            if (GadmFailure is not null)
            {
                return Task.FromException<GadmDivisionLookupDiagnostics>(GadmFailure);
            }
            return Task.FromResult(Gadm);
        }

        public Task<OvertureInfrastructureLookupDiagnostics> FindAirportAsync(
            double latitude,
            double longitude,
            string iso3,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AirportCalls++;
            if (AirportFailure is not null)
            {
                return Task.FromException<OvertureInfrastructureLookupDiagnostics>(AirportFailure);
            }
            if (AirportOperation is not null)
            {
                return AirportOperation(cancellationToken);
            }
            return Task.FromResult(Airport);
        }

        public Task<OvertureLookupDiagnostics> FindPlacesAsync(
            double latitude,
            double longitude,
            string alpha2,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlacesCalls++;
            if (PlacesFailure is not null)
            {
                return Task.FromException<OvertureLookupDiagnostics>(PlacesFailure);
            }
            return Task.FromResult(Places);
        }
    }
}
