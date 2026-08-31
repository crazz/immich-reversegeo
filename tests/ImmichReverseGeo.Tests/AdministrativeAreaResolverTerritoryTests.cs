using System.Collections.Concurrent;
using System.Reflection;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
namespace ImmichReverseGeo.Tests;

#pragma warning disable BL0006 // Test-only renderer attaches the real Razor component without changing production seams.

[TestClass]
public class AdministrativeAreaResolverTerritoryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    [TestMethod]
    public async Task ResolveAsync_SelectsDistinctOvertureOrGadmResultsByPreferenceAndRetainsTerritory()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        try
        {
            File.Copy(GetBundledArtifactPath(), Path.Combine(root, "defaults", "overture-country-divisions.db"));
            var catalog = CountryIdentityCatalog.Load(GetIdentityCatalogPath());
            var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            CreateDistinctResultCaches(root, territory.Alpha3, territory.Latitude, territory.Longitude);
            var resolver = CreateResolver(root, catalog);

            foreach (var preferGadm in new[] { false, true })
            {
                var result = await resolver.ResolveAsync(territory.Latitude, territory.Longitude, new ProcessingConfig
                {
                    UseGadmAdministrativeAreas = true,
                    PreferGadmAdministrativeAreas = preferGadm,
                    UseGadmTerritoryFallbacks = false
                });

                Assert.IsNotNull(result, territory.Label);
                Assert.AreEqual(territory.DisplayName, result.CountryName, territory.Label);
                Assert.AreEqual(territory.Alpha3, result.Iso3, territory.Label);
                Assert.AreEqual(territory.Alpha2, result.Alpha2, territory.Label);
                Assert.IsNotNull(result.OvertureResult, territory.Label);
                Assert.AreEqual("Overture State", result.OvertureResult.State, territory.Label);
                Assert.AreEqual("Overture City", result.OvertureResult.City, territory.Label);
                Assert.IsNotNull(result.GadmResult, territory.Label);
                Assert.AreEqual("GADM State", result.GadmResult.State, territory.Label);
                Assert.AreEqual("GADM City", result.GadmResult.City, territory.Label);
                Assert.AreEqual(preferGadm ? "GADM State" : "Overture State", result.GeoResult.State, territory.Label);
                Assert.AreEqual(preferGadm ? "GADM City" : "Overture City", result.GeoResult.City, territory.Label);
                Assert.AreEqual(territory.DisplayName, result.GeoResult.Country, territory.Label);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

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
                var reporter = new RecordingProcessingEventReporter();
                var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
                var session = await reporter.OpenRunAsync(request, DateTimeOffset.UtcNow);
                await session.DetermineEligibilityAsync(1);
                var result = await resolver.ResolveAsync(fixture.Latitude, fixture.Longitude, config, session);
                Assert.IsNotNull(result, fixture.Label);
                Assert.AreEqual(fixture.DisplayName, result.CountryName, fixture.Label);
                Assert.AreEqual(fixture.Alpha3, result.Iso3, fixture.Label);
                Assert.AreEqual(fixture.Alpha2, result.Alpha2, fixture.Label);
                var events = reporter.EventsFor(request);
                AssertInformationLogs(events, ExpectedReadyCacheLogs(fixture.Alpha3, fixture.DisplayName), fixture.Label);
                AssertActivityIdsPaired(events, 0);
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
            await WaitAsync(sourceEntered.Task);
            var cacheWaited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reporter = new RecordingProcessingEventReporter
            {
                AfterAcceptAsync = (processingEvent, _) =>
                {
                    if (processingEvent is ActivityStarted { Label: var label } && label == $"Waiting for Overture administrative cache for {fixture.Alpha3}...")
                    {
                        cacheWaited.TrySetResult();
                    }

                    return ValueTask.CompletedTask;
                }
            };
            var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
            var session = await reporter.OpenRunAsync(request, DateTimeOffset.UtcNow);
            await session.DetermineEligibilityAsync(1);
            var resolution = resolver.ResolveAsync(fixture.Latitude, fixture.Longitude, new ProcessingConfig(), session);
            await WaitAsync(cacheWaited.Task);
            owner.Cancel();
            releaseSource.TrySetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(() => WaitAsync(ownerTask));
            var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(() => WaitAsync(resolution));
            Assert.IsInstanceOfType<OperationCanceledException>(unavailable.InnerException);
            var events = reporter.EventsFor(request);
            AssertInformationLogs(events,
                [
                    "Checking bundled Overture country coverage...",
                    $"Country resolved as {fixture.DisplayName} ({fixture.Alpha3}).",
                    $"Waiting for in-flight Overture administrative cache download for {fixture.Alpha3}."
                ],
                fixture.Label);
            var waiter = events.OfType<ActivityStarted>().Single(activity => activity.Label == $"Waiting for Overture administrative cache for {fixture.Alpha3}...");
            AssertActivityIdsPaired(events, 1, waiter.ActivityId);
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

    [TestMethod]
    public async Task ConcurrentEqualLabelActivities_CorrelateStartsToTheirSpecificRequests()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var secondRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(firstRoot, "defaults"));
        Directory.CreateDirectory(Path.Combine(secondRoot, "defaults"));
        try
        {
            File.Copy(GetBundledArtifactPath(), Path.Combine(firstRoot, "defaults", "overture-country-divisions.db"));
            File.Copy(GetBundledArtifactPath(), Path.Combine(secondRoot, "defaults", "overture-country-divisions.db"));
            var catalog = CountryIdentityCatalog.Load(GetIdentityCatalogPath());
            var fixture = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            var first = CreateGatedOvertureResolver(firstRoot, catalog);
            var second = CreateGatedOvertureResolver(secondRoot, catalog);
            var reporter = new RecordingProcessingEventReporter();
            var firstRequest = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
            var secondRequest = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
            var firstSession = await reporter.OpenRunAsync(firstRequest, DateTimeOffset.UtcNow);
            var secondSession = await reporter.OpenRunAsync(secondRequest, DateTimeOffset.UtcNow);
            await firstSession.DetermineEligibilityAsync(1);
            await secondSession.DetermineEligibilityAsync(1);

            var firstResolution = first.Resolver.ResolveAsync(fixture.Latitude, fixture.Longitude, new ProcessingConfig(), firstSession);
            var secondResolution = second.Resolver.ResolveAsync(fixture.Latitude, fixture.Longitude, new ProcessingConfig(), secondSession);
            await Task.WhenAll(WaitAsync(first.Entered.Task), WaitAsync(second.Entered.Task));

            var firstStart = reporter.EventsFor(firstRequest).OfType<ActivityStarted>().Single(activity => activity.Label == $"Downloading Overture administrative cache for {fixture.Alpha3}...");
            var secondStart = reporter.EventsFor(secondRequest).OfType<ActivityStarted>().Single(activity => activity.Label == $"Downloading Overture administrative cache for {fixture.Alpha3}...");
            Assert.AreNotEqual(firstStart.ActivityId, secondStart.ActivityId);

            second.Release.TrySetResult();
            await WaitAsync(secondResolution);
            Assert.AreEqual(0, reporter.EventsFor(firstRequest).OfType<ActivityEnded>().Count(activity => activity.ActivityId == firstStart.ActivityId));
            Assert.AreEqual(1, reporter.EventsFor(secondRequest).OfType<ActivityEnded>().Count(activity => activity.ActivityId == secondStart.ActivityId));

            first.Release.TrySetResult();
            await WaitAsync(firstResolution);
            Assert.AreEqual(1, reporter.EventsFor(firstRequest).OfType<ActivityEnded>().Count(activity => activity.ActivityId == firstStart.ActivityId));
            AssertInformationLogs(reporter.EventsFor(firstRequest), ExpectedFreshOvertureLogs(fixture.Alpha3, fixture.DisplayName), fixture.Label);
            AssertInformationLogs(reporter.EventsFor(secondRequest), ExpectedFreshOvertureLogs(fixture.Alpha3, fixture.DisplayName), fixture.Label);
            AssertActivityIdsPaired(reporter.EventsFor(firstRequest), 1, firstStart.ActivityId);
            AssertActivityIdsPaired(reporter.EventsFor(secondRequest), 1, secondStart.ActivityId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task TerritoryProcessing_WriteBoundaryPreservesCountryAndPreferredAdministrativeValues()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        try
        {
            File.Copy(GetBundledArtifactPath(), Path.Combine(root, "defaults", "overture-country-divisions.db"));
            var catalog = CountryIdentityCatalog.Load(GetIdentityCatalogPath());
            var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            CreateDistinctResultCaches(root, territory.Alpha3, territory.Latitude, territory.Longitude);
            var resolver = CreateResolver(root, catalog);

            foreach (var preferGadm in new[] { false, true })
            {
                GeoResult? writtenGeo = null;
                var asset = new AssetRecord(Guid.NewGuid(), territory.Latitude, territory.Longitude, DateTime.UtcNow);
                var batches = new Queue<List<AssetRecord>>([[asset], []]);
                var operations = new ProcessingRunExecution.ProcessingOperations(
                    _ => Task.FromResult(1L),
                    () => Task.FromResult(new AppConfig { Processing = new ProcessingConfig { BatchDelayMs = 0, MaxDegreeOfParallelism = 1, UseAirportInfrastructure = false, UseGadmAdministrativeAreas = true, PreferGadmAdministrativeAreas = preferGadm, UseGadmTerritoryFallbacks = false } }),
                    () => Task.FromResult(new HashSet<Guid>()),
                    (_, _, _) => Task.FromResult(batches.Dequeue()),
                    (lat, lon, config, session, ct) => resolver.ResolveAsync(lat, lon, config, session, ct),
                    (_, _, _, _) => throw new AssertFailedException("Airport lookup must remain disabled."),
                    _ => Task.CompletedTask,
                    (_, geo, _) => { writtenGeo = geo; return Task.CompletedTask; });
                var reporter = new RecordingProcessingEventReporter();
                var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);

                await ProcessingRunExecution.RunOnceAsync(NullLogger<ProcessingBackgroundService>.Instance, new ProcessingState(), reporter, request, operations, CancellationToken.None);

                Assert.IsNotNull(writtenGeo, territory.Label);
                Assert.AreEqual(territory.DisplayName, writtenGeo.Country, territory.Label);
                Assert.AreEqual(preferGadm ? "GADM State" : "Overture State", writtenGeo.State, territory.Label);
                Assert.AreEqual(preferGadm ? "GADM City" : "Overture City", writtenGeo.City, territory.Label);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LookupPageCore_OverlapsAdmittedProcessingRunWithoutAttributingLookupEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        try
        {
            File.Copy(GetBundledArtifactPath(), Path.Combine(root, "defaults", "overture-country-divisions.db"));
            var catalog = CountryIdentityCatalog.Load(GetIdentityCatalogPath());
            var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            var resolver = CreateGatedOvertureResolver(root, catalog);
            var attempts = new ConcurrentQueue<ProcessingEvent>();
            var state = new ProcessingState();
            var stateReporter = new ProcessingStateEventReporter(state, attempts.Enqueue);
            var asset = new AssetRecord(Guid.NewGuid(), territory.Latitude, territory.Longitude, DateTime.UtcNow);
            var batches = new Queue<List<AssetRecord>>([[asset], []]);
            var processing = new ProcessingRunCoordinatorTestHost(
                NullLogger<ProcessingBackgroundService>.Instance,
                state,
                stateReporter,
                new ProcessingRunExecution.ProcessingOperations(
                    _ => Task.FromResult(1L),
                    () => Task.FromResult(new AppConfig { Processing = new ProcessingConfig { BatchDelayMs = 0, MaxDegreeOfParallelism = 1, UseAirportInfrastructure = false } }),
                    () => Task.FromResult(new HashSet<Guid>()),
                    (_, _, _) => Task.FromResult(batches.Dequeue()),
                    (lat, lon, config, session, ct) => resolver.Resolver.ResolveAsync(lat, lon, config, session, ct),
                    (_, _, _, _) => throw new AssertFailedException("Airport lookup must remain disabled."),
                    _ => Task.CompletedTask,
                    (_, _, _) => Task.CompletedTask));

            await processing.TriggerRunAsync();
            await WaitAsync(resolver.Entered.Task);
            Assert.IsTrue(state.IsRunning, "The overlap must use the run admitted by ProcessingBackgroundService.");
            Assert.AreEqual(0, resolver.Cache.GetStatus().Count);

            var lookup = new ImmichReverseGeo.Web.Components.Pages.Lookup();
            SetProperty(lookup, "GadmDivisions", resolver.GadmDivisions);
            SetProperty(lookup, "GadmCache", resolver.GadmCache);
            SetProperty(lookup, "Overture", resolver.Places);
            SetProperty(lookup, "OvertureCache", resolver.Cache);
            SetProperty(lookup, "OvertureDivisions", resolver.Divisions);
            SetProperty(lookup, "Config", new ConfigService(NullLogger<ConfigService>.Instance, root));
            SetProperty(lookup, "CityResolverCatalog", resolver.CityCatalog);
            SetField(lookup, "_lat", territory.Latitude);
            SetField(lookup, "_lon", territory.Longitude);
            SetField(lookup, "_includeAirportInfrastructure", false);
            SetField(lookup, "_includeLivePlaces", false);
            SetField(lookup, "_preferGadmAdministrativeAreas", false);

            using var services = new ServiceCollection().BuildServiceProvider();
            await using var renderer = new LookupTestRenderer(services, lookup, $"Waiting for Overture administrative cache for {territory.Alpha3}...");
            await renderer.AttachAsync(lookup);
            var lookupRun = renderer.RunLookupAsync(lookup);
            await WaitAsync(renderer.WaitingForOvertureCache.Task);
            CollectionAssert.AreEqual(
                new[]
                {
                    "Checking bundled Overture country coverage...",
                    $"Country resolved as {territory.DisplayName} ({territory.Alpha3}).",
                    $"Waiting for Overture administrative cache for {territory.Alpha3}..."
                },
                renderer.Statuses.ToArray());

            resolver.Release.TrySetResult();
            await Task.WhenAll(WaitAsync(lookupRun), WaitAsync(processing.WaitForManualAdmissionAsync()));

            CollectionAssert.AreEqual(
                new[]
                {
                    "Checking bundled Overture country coverage...",
                    $"Country resolved as {territory.DisplayName} ({territory.Alpha3}).",
                    $"Waiting for Overture administrative cache for {territory.Alpha3}...",
                    "Querying cached Overture administrative areas...",
                    "Lookup finished."
                },
                renderer.Statuses.ToArray());
            Assert.AreEqual(1, resolver.Cache.GetStatus().Count);
            Assert.AreEqual(1L, resolver.Cache.GetStatus()[territory.Alpha3].RowCount);
            var lookupResult = GetField<object>(lookup, "_result");
            Assert.IsNotNull(lookupResult);
            Assert.IsTrue(GetProperty<bool>(lookupResult, "OvertureDivisionCacheReady"));
            Assert.IsNotNull(GetProperty<object>(lookupResult, "OvertureDivisionDiagnostics"));
            Assert.IsNull(GetField<object?>(lookup, "_error"));
            Assert.IsFalse(GetField<bool>(lookup, "_running"));

            var events = attempts.ToArray();
            var request = events.OfType<RunStarted>().Single().Request;
            Assert.AreEqual(events.Length, events.Count(processingEvent => ReferenceEquals(processingEvent.Request, request)));
            AssertExactEventSequence(events,
                "RunStarted", "EligibilityDetermined:1",
                "LogEmitted:Information:Batch 1: fetched 1 assets (total processed so far: 0).",
                "LogEmitted:Information:Checking bundled Overture country coverage...",
                $"LogEmitted:Information:Country resolved as {territory.DisplayName} ({territory.Alpha3}).",
                $"ActivityStarted:Downloading Overture administrative cache for {territory.Alpha3}...",
                $"LogEmitted:Information:Starting Overture administrative cache download for {territory.Alpha3}.",
                $"ActivityEnded:Downloading Overture administrative cache for {territory.Alpha3}...",
                $"LogEmitted:Information:Overture administrative cache ready for {territory.Alpha3}.",
                "LogEmitted:Information:Querying cached Overture administrative areas...",
                "ProgressChanged:1:0:0",
                "RunFinished:Completed:1:0:0");
            var processingStart = events.OfType<ActivityStarted>().Single();
            AssertActivityIdsPaired(events, 1, processingStart.ActivityId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertExactEventSequence(IEnumerable<ProcessingEvent> events, params string[] expected)
    {
        var labelsById = new Dictionary<Guid, string>();
        var actual = events.Select(processingEvent => processingEvent switch
        {
            RunStarted => "RunStarted",
            EligibilityDetermined eligibility => $"EligibilityDetermined:{eligibility.EligibleCount}",
            LogEmitted log => $"LogEmitted:{log.Level}:{log.Message}",
            ActivityStarted started => DescribeStart(started, labelsById),
            ActivityEnded ended => $"ActivityEnded:{labelsById[ended.ActivityId]}",
            ProgressChanged progress => $"ProgressChanged:{progress.Progress.UpdatedCount}:{progress.Progress.SkippedCount}:{progress.Progress.FailedCount}",
            RunFinished finished => $"RunFinished:{finished.Result.Outcome}:{finished.Result.UpdatedCount}:{finished.Result.SkippedCount}:{finished.Result.FailedCount}",
            _ => processingEvent.GetType().Name
        }).ToArray();
        CollectionAssert.AreEqual(expected, actual);
    }

    private static string DescribeStart(ActivityStarted started, IDictionary<Guid, string> labelsById)
    {
        labelsById.Add(started.ActivityId, started.Label);
        return $"ActivityStarted:{started.Label}";
    }

    private static void SetProperty(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new AssertFailedException($"Lookup property {propertyName} was not found.");
        property.SetValue(target, value);
    }

    private static void SetField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException($"Lookup field {fieldName} was not found.");
        field.SetValue(target, value);
    }

    private static T GetField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException($"Lookup field {fieldName} was not found.");
        return (T)field.GetValue(target)!;
    }

    private static T GetProperty<T>(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new AssertFailedException($"Lookup result property {propertyName} was not found.");
        return (T)property.GetValue(target)!;
    }

    private static void AssertInformationLogs(IEnumerable<ProcessingEvent> events, IReadOnlyList<string> expected, string context)
    {
        var actual = events.OfType<LogEmitted>().ToArray();
        Assert.AreEqual(expected.Count, actual.Length, $"{context}: expected complete Information log array: {string.Join(" | ", expected)}. Actual: {string.Join(" | ", actual.Select(x => $"[{x.Level}] {x.Message}"))}");
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.AreEqual(ProcessingLogLevel.Information, actual[index].Level, $"{context}: log {index} must be Information.");
            Assert.AreEqual(expected[index], actual[index].Message, $"{context}: unexpected Information log at index {index}.");
        }
    }

    private static void AssertActivityIdsPaired(IEnumerable<ProcessingEvent> events, int expectedAcceptedCount, params Guid[] expectedAcceptedIds)
    {
        Assert.AreEqual(expectedAcceptedCount, expectedAcceptedIds.Length, "The proof must name every expected accepted activity ID.");
        Assert.AreEqual(expectedAcceptedIds.Length, expectedAcceptedIds.Distinct().Count(), "Expected accepted activity IDs must be unique.");
        var starts = events.OfType<ActivityStarted>().ToArray();
        var ends = events.OfType<ActivityEnded>().ToArray();
        foreach (var start in starts)
        {
            Assert.AreNotEqual(Guid.Empty, start.ActivityId, "Every accepted activity-start ID must be non-empty.");
        }
        Assert.AreEqual(expectedAcceptedCount, starts.Length, "Unexpected accepted activity-start count.");
        Assert.AreEqual(expectedAcceptedCount, ends.Length, "Unexpected accepted activity-end count.");
        CollectionAssert.AreEquivalent(expectedAcceptedIds, starts.Select(activity => activity.ActivityId).ToArray(), "Accepted activity IDs differed.");
        foreach (var expectedId in expectedAcceptedIds)
        {
            Assert.AreEqual(1, starts.Count(activity => activity.ActivityId == expectedId), $"Activity {expectedId} must start exactly once.");
            Assert.AreEqual(1, ends.Count(activity => activity.ActivityId == expectedId), $"Activity {expectedId} must have exactly one paired end.");
        }
    }

    private static string[] ExpectedFreshOvertureLogs(string iso3, string countryName) =>
    [
        "Checking bundled Overture country coverage...",
        $"Country resolved as {countryName} ({iso3}).",
        $"Starting Overture administrative cache download for {iso3}.",
        $"Overture administrative cache ready for {iso3}.",
        "Querying cached Overture administrative areas..."
    ];

    private static string[] ExpectedReadyCacheLogs(string iso3, string countryName) =>
    [
        "Checking bundled Overture country coverage...",
        $"Country resolved as {countryName} ({iso3}).",
        $"Overture administrative cache already ready for {iso3}.",
        $"Overture administrative cache ready for {iso3}.",
        "Querying cached Overture administrative areas...",
        $"Preparing GADM administrative caches for {iso3}...",
        $"GADM administrative cache already ready for {iso3}.",
        $"GADM administrative cache ready for {iso3}.",
        $"Querying cached GADM administrative areas across {iso3}..."
    ];

    private static (AdministrativeAreaResolverService Resolver, OverturePlacesService Places, OvertureDivisionsService Divisions, OvertureDivisionCacheService Cache, GadmDivisionsService GadmDivisions, GadmDivisionCacheService GadmCache, CityResolverProfileCatalogService CityCatalog, TaskCompletionSource Entered, TaskCompletionSource Release) CreateGatedOvertureResolver(string root, CountryIdentityCatalog catalog)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new StorageOptions(root, root);
        var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
        var divisions = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, alpha2 => catalog.FindByAlpha2(alpha2)?.Alpha3);
        var cache = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            root,
            iso3 => catalog.FindByAlpha3(iso3)?.Alpha2,
            new OvertureDivisionCacheTestHooks
            {
                SourceOperation = async (iso3, ct) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(TestTimeout, ct);
                    CreateReadyOvertureCache(root, iso3);
                }
            });
        var cityCatalog = new CityResolverProfileCatalogService(NullLogger<CityResolverProfileCatalogService>.Instance, storage);
        var gadmDivisions = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, root);
        var gadmCache = new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, root);
        var resolver = new AdministrativeAreaResolverService(
            NullLogger<AdministrativeAreaResolverService>.Instance,
            cityCatalog, divisions, cache, gadmDivisions, gadmCache);
        return (resolver, places, divisions, cache, gadmDivisions, gadmCache, cityCatalog, entered, release);
    }

    private static async Task WaitForOwnerCancellationAsync(CancellationToken ct, TaskCompletionSource entered, TaskCompletionSource release)
    {
        entered.TrySetResult();
        await release.Task.WaitAsync(TestTimeout, ct);
        ct.ThrowIfCancellationRequested();
    }

    private static Task WaitAsync(Task task) => task.WaitAsync(TestTimeout);
    private static Task<T> WaitAsync<T>(Task<T> task) => task.WaitAsync(TestTimeout);

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

    private static void CreateDistinctResultCaches(string root, string iso3, double lat, double lon)
    {
        var geometry = new WKBWriter().Write(new GeometryFactory().CreatePolygon(
        [
            new Coordinate(lon - 1, lat - 1),
            new Coordinate(lon + 1, lat - 1),
            new Coordinate(lon + 1, lat + 1),
            new Coordinate(lon - 1, lat + 1),
            new Coordinate(lon - 1, lat - 1)
        ]));
        var overtureDirectory = Path.Combine(root, "overture-divisions");
        Directory.CreateDirectory(overtureDirectory);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(overtureDirectory, iso3 + ".db")};Pooling=false"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE division_area (id TEXT PRIMARY KEY, name TEXT NOT NULL, subtype TEXT NULL, class_name TEXT NULL, admin_level INTEGER NULL, country TEXT NULL, is_land INTEGER NOT NULL, is_territorial INTEGER NOT NULL, geom_wkb BLOB NOT NULL, bbox_xmin REAL NULL, bbox_ymin REAL NULL, bbox_xmax REAL NULL, bbox_ymax REAL NULL);
                CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO _meta VALUES ('release', 'test');
                INSERT INTO _meta VALUES ('downloadedAt', '2026-08-19T00:00:00Z');
                INSERT INTO division_area VALUES ('overture-state', 'Overture State', 'region', 'land', 1, 'US', 1, 0, $geometry, $xmin, $ymin, $xmax, $ymax);
                INSERT INTO division_area VALUES ('overture-city', 'Overture City', 'locality', 'land', 2, 'US', 1, 0, $geometry, $xmin, $ymin, $xmax, $ymax);
                """;
            command.Parameters.AddWithValue("$geometry", geometry);
            command.Parameters.AddWithValue("$xmin", lon - 1);
            command.Parameters.AddWithValue("$ymin", lat - 1);
            command.Parameters.AddWithValue("$xmax", lon + 1);
            command.Parameters.AddWithValue("$ymax", lat + 1);
            command.ExecuteNonQuery();
        }

        var gadmDirectory = Path.Combine(root, "gadm-divisions");
        Directory.CreateDirectory(gadmDirectory);
        using var gadmConnection = new SqliteConnection($"Data Source={Path.Combine(gadmDirectory, iso3 + ".db")};Pooling=false");
        gadmConnection.Open();
        using var gadmCommand = gadmConnection.CreateCommand();
        gadmCommand.CommandText = """
            CREATE TABLE gadm_area (id TEXT PRIMARY KEY, name TEXT NOT NULL, english_type TEXT NULL, local_type TEXT NULL, admin_level INTEGER NOT NULL, geom_wkb BLOB NOT NULL, bbox_xmin REAL NOT NULL, bbox_ymin REAL NOT NULL, bbox_xmax REAL NOT NULL, bbox_ymax REAL NOT NULL);
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO _meta VALUES ('version', 'test');
            INSERT INTO _meta VALUES ('downloadedAt', '2026-08-19T00:00:00Z');
            INSERT INTO gadm_area VALUES ('gadm-state', 'GADM State', 'State', 'State', 1, $geometry, $xmin, $ymin, $xmax, $ymax);
            INSERT INTO gadm_area VALUES ('gadm-city', 'GADM City', 'City', 'City', 2, $geometry, $xmin, $ymin, $xmax, $ymax);
            """;
        gadmCommand.Parameters.AddWithValue("$geometry", geometry);
        gadmCommand.Parameters.AddWithValue("$xmin", lon - 1);
        gadmCommand.Parameters.AddWithValue("$ymin", lat - 1);
        gadmCommand.Parameters.AddWithValue("$xmax", lon + 1);
        gadmCommand.Parameters.AddWithValue("$ymax", lat + 1);
        gadmCommand.ExecuteNonQuery();
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

    private sealed class LookupTestRenderer : Renderer
    {
        private static readonly MethodInfo RunLookupCore = typeof(ImmichReverseGeo.Web.Components.Pages.Lookup).GetMethod("RunLookupCore", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Lookup.RunLookupCore was not found.");
        private static readonly MethodInfo StateHasChanged = typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ComponentBase.StateHasChanged was not found.");
        private readonly ImmichReverseGeo.Web.Components.Pages.Lookup _lookup;
        private readonly string _waitingStatus;

        public LookupTestRenderer(IServiceProvider services, ImmichReverseGeo.Web.Components.Pages.Lookup lookup, string waitingStatus)
            : base(services, NullLoggerFactory.Instance)
        {
            _lookup = lookup;
            _waitingStatus = waitingStatus;
        }

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public List<string> Statuses { get; } = [];
        public TaskCompletionSource WaitingForOvertureCache { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AttachAsync(IComponent component)
        {
            return Dispatcher.InvokeAsync(() => AssignRootComponentId(component));
        }

        public Task RunLookupAsync(ImmichReverseGeo.Web.Components.Pages.Lookup lookup)
        {
            return Dispatcher.InvokeAsync(() => lookup.RunLookupAsync(
                () => (Task)RunLookupCore.Invoke(lookup, null)!,
                () => StateHasChanged.Invoke(lookup, null)));
        }

        protected override void HandleException(Exception exception)
        {
            throw exception;
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            var status = GetField<string?>(_lookup, "_lookupStatus");
            if (status is not null && status != "Preparing lookup..." && (Statuses.Count == 0 || Statuses[^1] != status))
            {
                Statuses.Add(status);
                if (status == _waitingStatus)
                {
                    WaitingForOvertureCache.TrySetResult();
                }
            }

            return Task.CompletedTask;
        }
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

}

#pragma warning restore BL0006
