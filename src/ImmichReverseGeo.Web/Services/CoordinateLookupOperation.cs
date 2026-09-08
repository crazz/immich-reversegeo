using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Gadm.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;

namespace ImmichReverseGeo.Web.Services;

internal interface ICoordinateLookupEventReporter
{
    ValueTask ReportAsync(WorkerJobOutputPayload payload, CancellationToken cancellationToken);
}

internal interface ICoordinateLookupSources
{
    Task<BundledCountryLookupResult> FindCountryAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken);

    CityResolverProfile ResolveCityProfile(
        CoordinateLookupCityResolverOverrides overrides,
        string? iso3);

    IReadOnlyList<string> ExpandGadmCandidateCodes(string iso3);

    (Task Task, OvertureDivisionEnsureResult Result) GetOrStartOvertureCache(
        string iso3,
        CancellationToken cancellationToken);

    bool HasOvertureCache(string iso3);

    Task<OvertureDivisionLookupDiagnostics> FindOvertureDivisionsAsync(
        double latitude,
        double longitude,
        string alpha2,
        string iso3,
        CancellationToken cancellationToken);

    (Task Task, GadmDivisionEnsureResult Result) GetOrStartGadmCache(
        string iso3,
        CancellationToken cancellationToken);

    bool HasGadmCache(string iso3);

    Task<GadmDivisionLookupDiagnostics> FindGadmDivisionsAsync(
        double latitude,
        double longitude,
        IReadOnlyList<string> iso3Codes,
        CancellationToken cancellationToken);

    Task<OvertureInfrastructureLookupDiagnostics> FindAirportAsync(
        double latitude,
        double longitude,
        string iso3,
        CancellationToken cancellationToken);

    Task<OvertureLookupDiagnostics> FindPlacesAsync(
        double latitude,
        double longitude,
        string alpha2,
        CancellationToken cancellationToken);
}

internal sealed class CoordinateLookupSources : ICoordinateLookupSources
{
    private readonly OvertureDivisionsService _overtureDivisions;
    private readonly OvertureDivisionCacheService _overtureCache;
    private readonly OverturePlacesService _overturePlaces;
    private readonly GadmDivisionCacheService _gadmCache;
    private readonly GadmDivisionsService _gadmDivisions;
    private readonly CityResolverProfileCatalogService _cityProfiles;

    internal CoordinateLookupSources(
        OvertureDivisionsService overtureDivisions,
        OvertureDivisionCacheService overtureCache,
        OverturePlacesService overturePlaces,
        GadmDivisionCacheService gadmCache,
        GadmDivisionsService gadmDivisions,
        CityResolverProfileCatalogService cityProfiles)
    {
        _overtureDivisions = overtureDivisions ?? throw new ArgumentNullException(nameof(overtureDivisions));
        _overtureCache = overtureCache ?? throw new ArgumentNullException(nameof(overtureCache));
        _overturePlaces = overturePlaces ?? throw new ArgumentNullException(nameof(overturePlaces));
        _gadmCache = gadmCache ?? throw new ArgumentNullException(nameof(gadmCache));
        _gadmDivisions = gadmDivisions ?? throw new ArgumentNullException(nameof(gadmDivisions));
        _cityProfiles = cityProfiles ?? throw new ArgumentNullException(nameof(cityProfiles));
    }

    public Task<BundledCountryLookupResult> FindCountryAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken) =>
        _overtureDivisions.FindBundledCountryAsync(latitude, longitude, cancellationToken);

    public CityResolverProfile ResolveCityProfile(
        CoordinateLookupCityResolverOverrides overrides,
        string? iso3) =>
        _cityProfiles.GetProfile(CoordinateLookupCityProfileConversions.ToConfig(overrides), iso3);

    public IReadOnlyList<string> ExpandGadmCandidateCodes(string iso3) =>
        GadmCountryFallbackCatalog.ExpandCandidateCodes(iso3);

    public (Task Task, OvertureDivisionEnsureResult Result) GetOrStartOvertureCache(
        string iso3,
        CancellationToken cancellationToken) =>
        _overtureCache.GetOrStartDownload(iso3, cancellationToken);

    public bool HasOvertureCache(string iso3) => _overtureCache.HasData(iso3);

    public Task<OvertureDivisionLookupDiagnostics> FindOvertureDivisionsAsync(
        double latitude,
        double longitude,
        string alpha2,
        string iso3,
        CancellationToken cancellationToken) =>
        _overtureDivisions.FindContainingDivisionAreasAsync(
            latitude,
            longitude,
            alpha2,
            iso3,
            cancellationToken);

    public (Task Task, GadmDivisionEnsureResult Result) GetOrStartGadmCache(
        string iso3,
        CancellationToken cancellationToken) =>
        _gadmCache.GetOrStartDownload(iso3, cancellationToken);

    public bool HasGadmCache(string iso3) => _gadmCache.HasData(iso3);

    public Task<GadmDivisionLookupDiagnostics> FindGadmDivisionsAsync(
        double latitude,
        double longitude,
        IReadOnlyList<string> iso3Codes,
        CancellationToken cancellationToken) =>
        _gadmDivisions.FindContainingDivisionAreasAsync(
            latitude,
            longitude,
            iso3Codes,
            cancellationToken);

    public Task<OvertureInfrastructureLookupDiagnostics> FindAirportAsync(
        double latitude,
        double longitude,
        string iso3,
        CancellationToken cancellationToken) =>
        _overturePlaces.FindNearestInfrastructureWithDiagnosticsAsync(
            latitude,
            longitude,
            iso3,
            cancellationToken);

    public Task<OvertureLookupDiagnostics> FindPlacesAsync(
        double latitude,
        double longitude,
        string alpha2,
        CancellationToken cancellationToken) =>
        _overturePlaces.FindNearestPlaceWithDiagnosticsAsync(
            latitude,
            longitude,
            alpha2,
            cancellationToken);
}

internal sealed class CoordinateLookupOperation
{
    private readonly ICoordinateLookupSources _sources;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _activityIdFactory;

    internal CoordinateLookupOperation(
        ICoordinateLookupSources sources,
        TimeProvider timeProvider,
        Func<Guid>? activityIdFactory = null)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _activityIdFactory = activityIdFactory ?? Guid.NewGuid;
    }

    internal async Task<CoordinateLookupResult> ExecuteAsync(
        CoordinateLookupRequest request,
        DateTimeOffset startedAtUtc,
        ICoordinateLookupEventReporter reporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reporter);
        cancellationToken.ThrowIfCancellationRequested();

        var trace = new List<string>();
        await ProgressAsync(
            reporter,
            CoordinateLookupProgressStep.Country,
            CoordinateLookupSourceState.Ready,
            null,
            "Checking bundled Overture country coverage.",
            cancellationToken).ConfigureAwait(false);

        BundledCountryLookupResult countryLookup = await _sources.FindCountryAsync(
            request.Latitude,
            request.Longitude,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (!countryLookup.IsMatched)
        {
            CoordinateLookupCountryStatus status = countryLookup.Status switch
            {
                BundledCountryLookupStatus.SpatialNoMatch => CoordinateLookupCountryStatus.NoMatch,
                BundledCountryLookupStatus.IdentityMappingFailure => CoordinateLookupCountryStatus.MappingFailed,
                _ => CoordinateLookupCountryStatus.Failed
            };
            string code = status == CoordinateLookupCountryStatus.NoMatch
                ? "country-no-match"
                : "country-identity-failed";
            string message = status == CoordinateLookupCountryStatus.NoMatch
                ? "Bundled country coverage found no match."
                : "Bundled country identity mapping failed.";
            trace.Add(message);
            CityResolverProfile unmatchedProfile = _sources.ResolveCityProfile(
                request.CityResolverOverrides,
                null);
            await ProgressAsync(
                reporter,
                CoordinateLookupProgressStep.Country,
                CoordinateLookupSourceState.NoMatch,
                null,
                message,
                cancellationToken).ConfigureAwait(false);
            return BuildEarlyResult(
                request,
                startedAtUtc,
                status,
                code,
                message,
                unmatchedProfile,
                trace);
        }

        string iso3 = countryLookup.Iso3!;
        string alpha2 = countryLookup.Alpha2!;
        CityResolverProfile cityProfile = _sources.ResolveCityProfile(
            request.CityResolverOverrides,
            iso3);
        IReadOnlyList<string> gadmCodes = request.PreferGadmAdministrativeAreas
            ? _sources.ExpandGadmCandidateCodes(iso3)
            : [];
        trace.Add($"Country matched from bundled Overture divisions: {iso3}.");
        await ProgressAsync(
            reporter,
            CoordinateLookupProgressStep.Country,
            CoordinateLookupSourceState.Ready,
            iso3,
            $"Country resolved as {iso3}.",
            cancellationToken).ConfigureAwait(false);

        var startedTasks = new List<Task>();
        Exception? primaryFailure = null;
        try
        {
            Task<CacheOutcome> overtureCacheTask = EnsureOvertureCacheAsync(
                iso3,
                reporter,
                cancellationToken);
            startedTasks.Add(overtureCacheTask);

            Task<GadmCacheOutcome>? gadmCacheTask = null;
            if (request.PreferGadmAdministrativeAreas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                gadmCacheTask = EnsureGadmCachesAsync(gadmCodes, reporter, cancellationToken);
                startedTasks.Add(gadmCacheTask);
            }

            Task<SourceOutcome<OvertureInfrastructureLookupDiagnostics>>? airportTask = null;
            if (request.IncludeAirportInfrastructure)
            {
                cancellationToken.ThrowIfCancellationRequested();
                airportTask = RunSourceAsync(
                    () => _sources.FindAirportAsync(
                        request.Latitude,
                        request.Longitude,
                        iso3,
                        cancellationToken),
                    "airport-source-failed",
                    "Bundled airport lookup failed.",
                    cancellationToken);
                startedTasks.Add(airportTask);
            }

            Task<SourceOutcome<OvertureLookupDiagnostics>>? placesTask = null;
            if (request.IncludeLiveOverturePlaces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                placesTask = RunSourceAsync(
                    () => _sources.FindPlacesAsync(
                        request.Latitude,
                        request.Longitude,
                        alpha2,
                        cancellationToken),
                    "places-source-failed",
                    "Live Overture Places lookup failed.",
                    cancellationToken);
                startedTasks.Add(placesTask);
            }

            CacheOutcome overtureCache = await overtureCacheTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SourceOutcome<OvertureDivisionLookupDiagnostics> overtureDiagnostics =
                overtureCache.Ready
                    ? await RunSourceAsync(
                        () => _sources.FindOvertureDivisionsAsync(
                            request.Latitude,
                            request.Longitude,
                            alpha2,
                            iso3,
                            cancellationToken),
                        "overture-admin-failed",
                        "Cached Overture administrative lookup failed.",
                        cancellationToken).ConfigureAwait(false)
                    : SourceOutcome<OvertureDivisionLookupDiagnostics>.Failure(
                        overtureCache.Error ?? DependencyError(
                            "overture-cache-unavailable",
                            "Overture administrative cache is unavailable."));

            await ProgressAsync(
                reporter,
                CoordinateLookupProgressStep.OvertureAdministrative,
                SourceState(overtureDiagnostics.Value?.BestMatch, overtureDiagnostics.Error),
                iso3,
                "Overture administrative lookup finished.",
                cancellationToken).ConfigureAwait(false);

            GadmCacheOutcome gadmCache = gadmCacheTask is null
                ? GadmCacheOutcome.Disabled()
                : await gadmCacheTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SourceOutcome<GadmDivisionLookupDiagnostics>? gadmDiagnostics = null;
            if (request.PreferGadmAdministrativeAreas && gadmCache.ReadyCodes.Count > 0)
            {
                gadmDiagnostics = await RunSourceAsync(
                    () => _sources.FindGadmDivisionsAsync(
                        request.Latitude,
                        request.Longitude,
                        gadmCache.ReadyCodes,
                        cancellationToken),
                    "gadm-admin-failed",
                    "Cached GADM administrative lookup failed.",
                    cancellationToken).ConfigureAwait(false);
                await ProgressAsync(
                    reporter,
                    CoordinateLookupProgressStep.GadmAdministrative,
                    SourceState(gadmDiagnostics.Value?.BestMatch, gadmDiagnostics.Error),
                    iso3,
                    "GADM administrative lookup finished.",
                    cancellationToken).ConfigureAwait(false);
            }

            SourceOutcome<OvertureInfrastructureLookupDiagnostics>? airport = airportTask is null
                ? null
                : await airportTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SourceOutcome<OvertureLookupDiagnostics>? places = placesTask is null
                ? null
                : await placesTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            OvertureDivisionLookupDiagnostics? overtureValue = overtureDiagnostics.Value;
            GadmDivisionLookupDiagnostics? gadmValue = gadmDiagnostics?.Value;
            string? overtureState = overtureValue is null
                ? null
                : OvertureDivisionsLogic.SelectStateName(overtureValue.Candidates);
            string? overtureCity = overtureValue is null
                ? null
                : OvertureDivisionsLogic.SelectCityName(overtureValue.Candidates, cityProfile);
            string? gadmState = gadmValue is null
                ? null
                : GadmDivisionsLogic.SelectStateName(gadmValue.Candidates);
            string? gadmCity = gadmValue is null
                ? null
                : GadmDivisionsLogic.SelectCityName(gadmValue.Candidates);

            var overtureAdmin = new CoordinateLookupAdministrativeResult(
                NormalizeOptional(overtureState, out int overtureAdminTrim),
                NormalizeOptional(overtureCity, out int overtureCityTrim));
            var gadmAdmin = new CoordinateLookupAdministrativeResult(
                NormalizeOptional(gadmState, out int gadmAdminTrim),
                NormalizeOptional(gadmCity, out int gadmCityTrim));
            int resultTruncatedText = overtureAdminTrim + overtureCityTrim + gadmAdminTrim + gadmCityTrim;

            CoordinateLookupSourceResult overtureSection = MapOverture(
                overtureCache,
                overtureDiagnostics);
            CoordinateLookupSourceResult gadmSection = MapGadm(
                request.PreferGadmAdministrativeAreas,
                gadmCache,
                gadmDiagnostics);
            CoordinateLookupSourceResult airportSection = MapAirport(
                request.IncludeAirportInfrastructure,
                airport);
            CoordinateLookupSourceResult placesSection = MapPlaces(
                request.IncludeLiveOverturePlaces,
                places);

            string? adminState = request.PreferGadmAdministrativeAreas && gadmAdmin.State is not null
                ? gadmAdmin.State
                : overtureAdmin.State;
            CoordinateLookupFinalSource? adminStateSource = request.PreferGadmAdministrativeAreas
                && gadmAdmin.State is not null
                    ? CoordinateLookupFinalSource.CachedGadmDivisions
                    : overtureAdmin.State is not null
                        ? CoordinateLookupFinalSource.CachedOvertureDivisions
                        : null;
            string? adminCity = request.PreferGadmAdministrativeAreas && gadmAdmin.City is not null
                ? gadmAdmin.City
                : overtureAdmin.City;
            CoordinateLookupFinalSource? adminCitySource = request.PreferGadmAdministrativeAreas
                && gadmAdmin.City is not null
                    ? CoordinateLookupFinalSource.CachedGadmDivisions
                    : overtureAdmin.City is not null
                        ? CoordinateLookupFinalSource.CachedOvertureDivisions
                        : null;
            string countryName = NormalizeRequired(countryLookup.CountryName, "Unknown country", out int countryTrim);
            resultTruncatedText += countryTrim;
            CoordinateLookupAttributedValue countryFinal = new(
                countryName,
                CoordinateLookupFinalSource.BundledCountryDivisions);
            CoordinateLookupAttributedValue? stateFinal = adminState is null || adminStateSource is null
                ? null
                : new CoordinateLookupAttributedValue(adminState, adminStateSource.Value);

            string? airportName = airportSection.BestMatch?.Name;
            bool airportContains = airportSection.BestMatch?.GeometryContainsCoordinate == true;
            string? finalCity = airportContains ? airportName : adminCity ?? airportName;
            CoordinateLookupFinalSource? finalCitySource = airportContains
                ? CoordinateLookupFinalSource.BundledAirportGeometryMatch
                : adminCitySource ?? (airportName is null
                    ? null
                    : CoordinateLookupFinalSource.BundledAirportFallback);
            if (finalCity is null && stateFinal is not null)
            {
                finalCity = stateFinal.Value;
                finalCitySource = stateFinal.Source;
            }
            else if (finalCity is null)
            {
                finalCity = countryFinal.Value;
                finalCitySource = countryFinal.Source;
            }

            string finalTrace = NormalizeRequired(
                $"Final city decision: {finalCity}.",
                "Final city decision completed.",
                out int finalTraceTrim);
            resultTruncatedText += finalTraceTrim;
            trace.Add(finalTrace);
            await ProgressAsync(
                reporter,
                CoordinateLookupProgressStep.FinalSelection,
                CoordinateLookupSourceState.Ready,
                iso3,
                "Coordinate lookup finished.",
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var countryResult = new CoordinateLookupCountryResult(
                CoordinateLookupCountryStatus.Matched,
                iso3,
                alpha2,
                countryName,
                NormalizeRequired(countryLookup.SourceId, "unknown", out int sourceTrim),
                null);
            resultTruncatedText += sourceTrim;
            return new CoordinateLookupResult(
                request,
                startedAtUtc,
                _timeProvider.GetUtcNow(),
                countryResult,
                overtureSection,
                gadmSection,
                airportSection,
                placesSection,
                overtureAdmin,
                gadmAdmin,
                ProfileSummary(iso3, cityProfile),
                trace,
                new CoordinateLookupFinalLocation(
                    countryFinal,
                    stateFinal,
                    finalCity is null || finalCitySource is null
                        ? null
                        : new CoordinateLookupAttributedValue(finalCity, finalCitySource.Value)),
                truncatedTextCount: resultTruncatedText);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
        }

        Exception? unwindFailure = await ObserveStartedTasksAsync(startedTasks).ConfigureAwait(false);
        if (unwindFailure is OutOfMemoryException)
        {
            ExceptionDispatchInfo.Capture(unwindFailure).Throw();
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        if (unwindFailure is not null)
        {
            ExceptionDispatchInfo.Capture(unwindFailure).Throw();
        }

        throw new InvalidOperationException("The lookup operation ended without a result.");
    }

    private CoordinateLookupResult BuildEarlyResult(
        CoordinateLookupRequest request,
        DateTimeOffset startedAtUtc,
        CoordinateLookupCountryStatus countryStatus,
        string errorCode,
        string errorMessage,
        CityResolverProfile profile,
        IReadOnlyList<string> trace)
    {
        var skipped = EmptySource(CoordinateLookupSourceState.Skipped);
        return new CoordinateLookupResult(
            request,
            startedAtUtc,
            _timeProvider.GetUtcNow(),
            new CoordinateLookupCountryResult(
                countryStatus,
                null,
                null,
                null,
                null,
                DependencyError(errorCode, errorMessage)),
            skipped,
            request.PreferGadmAdministrativeAreas
                ? GadmSource(CoordinateLookupSourceState.Skipped)
                : GadmSource(CoordinateLookupSourceState.Disabled),
            EmptySource(request.IncludeAirportInfrastructure
                ? CoordinateLookupSourceState.Skipped
                : CoordinateLookupSourceState.Disabled),
            EmptySource(request.IncludeLiveOverturePlaces
                ? CoordinateLookupSourceState.Skipped
                : CoordinateLookupSourceState.Disabled),
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupAdministrativeResult(null, null),
            ProfileSummary(null, profile),
            trace,
            new CoordinateLookupFinalLocation(null, null, null));
    }

    private async Task<CacheOutcome> EnsureOvertureCacheAsync(
        string iso3,
        ICoordinateLookupEventReporter reporter,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (Task task, OvertureDivisionEnsureResult result) =
                _sources.GetOrStartOvertureCache(iso3, cancellationToken);
            if (result == OvertureDivisionEnsureResult.AlreadyReady)
            {
                await ProgressAsync(
                    reporter,
                    CoordinateLookupProgressStep.OvertureCache,
                    CoordinateLookupSourceState.Ready,
                    iso3,
                    $"Overture administrative cache is ready for {iso3}.",
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                string action = result == OvertureDivisionEnsureResult.StartedDownload
                    ? "Downloading"
                    : "Waiting for";
                await AwaitCacheActivityAsync(
                    reporter,
                    task,
                    $"{action} Overture administrative cache for {iso3}",
                    ownsTask: result == OvertureDivisionEnsureResult.StartedDownload,
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new CacheOutcome(
                _sources.HasOvertureCache(iso3),
                [new CoordinateLookupCacheStatus(
                    iso3,
                    _sources.HasOvertureCache(iso3)
                        ? CoordinateLookupSourceState.Ready
                        : CoordinateLookupSourceState.Unavailable,
                    null)],
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (CoordinateLookupReporterException)
        {
            throw;
        }
        catch
        {
            WorkerJobSafeError error = DependencyError(
                "overture-cache-unavailable",
                "Overture administrative cache is unavailable.");
            return new CacheOutcome(
                false,
                [new CoordinateLookupCacheStatus(
                    iso3,
                    CoordinateLookupSourceState.Unavailable,
                    error)],
                error);
        }
    }

    private async Task<GadmCacheOutcome> EnsureGadmCachesAsync(
        IReadOnlyList<string> codes,
        ICoordinateLookupEventReporter reporter,
        CancellationToken cancellationToken)
    {
        var ready = new List<string>();
        var statuses = new List<CoordinateLookupCacheStatus>();
        foreach (string code in codes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                (Task task, GadmDivisionEnsureResult result) =
                    _sources.GetOrStartGadmCache(code, cancellationToken);
                if (result != GadmDivisionEnsureResult.AlreadyReady)
                {
                    string action = result == GadmDivisionEnsureResult.StartedDownload
                        ? "Downloading"
                        : "Waiting for";
                    await AwaitCacheActivityAsync(
                        reporter,
                        task,
                        $"{action} GADM administrative cache for {code}",
                        ownsTask: result == GadmDivisionEnsureResult.StartedDownload,
                        cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                bool hasData = _sources.HasGadmCache(code);
                if (hasData)
                {
                    ready.Add(code);
                }
                WorkerJobSafeError? error = hasData
                    ? null
                    : DependencyError(
                        "gadm-cache-unavailable",
                        "A GADM administrative cache is unavailable.");
                statuses.Add(new CoordinateLookupCacheStatus(
                    code,
                    hasData ? CoordinateLookupSourceState.Ready : CoordinateLookupSourceState.Unavailable,
                    error));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (CoordinateLookupReporterException)
            {
                throw;
            }
            catch
            {
                WorkerJobSafeError error = DependencyError(
                    "gadm-cache-unavailable",
                    "A GADM administrative cache is unavailable.");
                statuses.Add(new CoordinateLookupCacheStatus(
                    code,
                    CoordinateLookupSourceState.Unavailable,
                    error));
            }
        }

        await ProgressAsync(
            reporter,
            CoordinateLookupProgressStep.GadmCache,
            ready.Count > 0 ? CoordinateLookupSourceState.Ready : CoordinateLookupSourceState.Unavailable,
            codes.FirstOrDefault(),
            "GADM administrative cache preparation finished.",
            cancellationToken).ConfigureAwait(false);
        return new GadmCacheOutcome(ready, statuses);
    }

    private async Task AwaitCacheActivityAsync(
        ICoordinateLookupEventReporter reporter,
        Task task,
        string label,
        bool ownsTask,
        CancellationToken cancellationToken)
    {
        Guid activityId = _activityIdFactory();
        bool activityStarted = false;
        Exception? primaryFailure = null;
        try
        {
            await ReportEventAsync(
                reporter,
                new WorkerJobActivityStartedPayload(activityId, label),
                cancellationToken).ConfigureAwait(false);
            activityStarted = true;
            if (ownsTask)
            {
                await task.ConfigureAwait(false);
            }
            else
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
        }

        Exception? unwindFailure = null;
        if (ownsTask && primaryFailure is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                unwindFailure = ex;
            }
        }

        Exception? activityFailure = null;
        if (activityStarted)
        {
            try
            {
                await ReportEventAsync(
                    reporter,
                    new WorkerJobActivityEndedPayload(activityId),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                activityFailure = ex;
            }
        }

        OutOfMemoryException? fatal = primaryFailure as OutOfMemoryException
            ?? unwindFailure as OutOfMemoryException
            ?? activityFailure as OutOfMemoryException;
        if (fatal is not null)
        {
            ExceptionDispatchInfo.Capture(fatal).Throw();
        }

        if (activityFailure is not null)
        {
            ExceptionDispatchInfo.Capture(activityFailure).Throw();
        }

        if (primaryFailure is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The shared cache operation was cancelled.", primaryFailure);
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        if (unwindFailure is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The owned cache operation was cancelled.", unwindFailure);
        }

        if (unwindFailure is not null)
        {
            ExceptionDispatchInfo.Capture(unwindFailure).Throw();
        }
    }

    private static async Task<SourceOutcome<T>> RunSourceAsync<T>(
        Func<Task<T>> operation,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            T value = await operation().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return SourceOutcome<T>.Success(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            return SourceOutcome<T>.Failure(DependencyError(errorCode, errorMessage));
        }
    }

    private static async Task<Exception?> ObserveStartedTasksAsync(IEnumerable<Task> tasks)
    {
        Exception? first = null;
        OutOfMemoryException? fatal = null;
        foreach (Task task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OutOfMemoryException ex)
            {
                fatal ??= ex;
            }
            catch (Exception ex)
            {
                first ??= ex;
            }
        }

        return fatal ?? first;
    }

    private static ValueTask ProgressAsync(
        ICoordinateLookupEventReporter reporter,
        CoordinateLookupProgressStep step,
        CoordinateLookupSourceState state,
        string? countryCode,
        string message,
        CancellationToken cancellationToken) =>
        ReportEventAsync(
            reporter,
            new CoordinateLookupProgressPayload(step, state, countryCode, message),
            cancellationToken);

    private static async ValueTask ReportEventAsync(
        ICoordinateLookupEventReporter reporter,
        WorkerJobOutputPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await reporter.ReportAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (CoordinateLookupReporterException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CoordinateLookupReporterException(ex);
        }
    }

    private static CoordinateLookupSourceState SourceState<T>(T? bestMatch, WorkerJobSafeError? error)
        where T : class =>
        error is not null
            ? CoordinateLookupSourceState.Failed
            : bestMatch is null
                ? CoordinateLookupSourceState.NoMatch
                : CoordinateLookupSourceState.Ready;

    private static CoordinateLookupSourceResult MapOverture(
        CacheOutcome cache,
        SourceOutcome<OvertureDivisionLookupDiagnostics> outcome)
    {
        if (outcome.Value is null)
        {
            return new CoordinateLookupSourceResult(
                cache.Ready
                    ? CoordinateLookupSourceState.Failed
                    : CoordinateLookupSourceState.Unavailable,
                null,
                null,
                null,
                [],
                cache.Statuses,
                outcome.Error,
                null,
                null,
                null);
        }

        OvertureDivisionLookupDiagnostics value = outcome.Value;
        WorkerJobSafeError? error = value.Error is null
            ? outcome.Error
            : DependencyError("overture-admin-failed", "Cached Overture administrative lookup failed.");
        CoordinateLookupCandidate[] candidates = value.Candidates
            .OrderByDescending(static candidate => candidate.Selected)
            .ThenByDescending(static candidate => candidate.GeometryContainsPoint)
            .ThenByDescending(static candidate => candidate.BoundingBoxContainsPoint)
            .ThenBy(static candidate => OvertureDivisionsLogic.GetSubtypeRank(candidate.SubType))
            .ThenBy(static candidate => candidate.AdminLevel ?? int.MaxValue)
            .ThenByDescending(static candidate => candidate.IsTerritorial)
            .ThenBy(static candidate => candidate.BoundingBoxArea)
            .ThenBy(static candidate => candidate.Id, StringComparer.Ordinal)
            .Select(MapDivisionCandidate)
            .ToArray();
        CoordinateLookupCandidate? best = value.BestMatch is null
            ? null
            : MapDivisionBest(value.BestMatch, candidates);
        return new CoordinateLookupSourceResult(
            SourceState(best, error),
            NormalizeOptional(value.Release, out int releaseTrim),
            null,
            best,
            candidates,
            cache.Statuses,
            error,
            null,
            null,
            null,
            truncatedTextCount: releaseTrim + candidates.Sum(static candidate => candidate.TruncatedTextCount));
    }

    private static CoordinateLookupSourceResult MapGadm(
        bool enabled,
        GadmCacheOutcome cache,
        SourceOutcome<GadmDivisionLookupDiagnostics>? outcome)
    {
        if (!enabled)
        {
            return GadmSource(CoordinateLookupSourceState.Disabled);
        }

        if (outcome?.Value is null)
        {
            bool queryFailed = cache.ReadyCodes.Count > 0 && outcome?.Error is not null;
            return new CoordinateLookupSourceResult(
                queryFailed
                    ? CoordinateLookupSourceState.Failed
                    : CoordinateLookupSourceState.Unavailable,
                null,
                GadmDivisionsLogic.DatasetVersion,
                null,
                [],
                cache.Statuses,
                outcome?.Error ?? (cache.ReadyCodes.Count == 0
                    ? DependencyError(
                        "gadm-cache-unavailable",
                        "GADM administrative caches are unavailable.")
                    : null),
                CoordinateLookupGadmAttribution.DatasetName,
                CoordinateLookupGadmAttribution.LicenseUrl,
                CoordinateLookupGadmAttribution.UsageNotice);
        }

        GadmDivisionLookupDiagnostics value = outcome.Value;
        WorkerJobSafeError? error = value.Error is null
            ? outcome.Error
            : DependencyError("gadm-admin-failed", "Cached GADM administrative lookup failed.");
        CoordinateLookupCandidate[] candidates = value.Candidates
            .OrderByDescending(static candidate => candidate.Selected)
            .ThenByDescending(static candidate => candidate.GeometryContainsPoint)
            .ThenByDescending(static candidate => candidate.BoundingBoxContainsPoint)
            .ThenByDescending(static candidate => candidate.AdminLevel)
            .ThenBy(static candidate => candidate.BoundingBoxArea)
            .ThenBy(static candidate => candidate.Id, StringComparer.Ordinal)
            .Select(MapGadmCandidate)
            .ToArray();
        CoordinateLookupCandidate? best = value.BestMatch is null
            ? null
            : MapGadmBest(value.BestMatch, candidates);
        return new CoordinateLookupSourceResult(
            SourceState(best, error),
            null,
            NormalizeOptional(value.Version, out int versionTrim),
            best,
            candidates,
            cache.Statuses,
            error,
            CoordinateLookupGadmAttribution.DatasetName,
            CoordinateLookupGadmAttribution.LicenseUrl,
            CoordinateLookupGadmAttribution.UsageNotice,
            truncatedTextCount: versionTrim + candidates.Sum(static candidate => candidate.TruncatedTextCount));
    }

    private static CoordinateLookupSourceResult MapAirport(
        bool enabled,
        SourceOutcome<OvertureInfrastructureLookupDiagnostics>? outcome)
    {
        if (!enabled)
        {
            return EmptySource(CoordinateLookupSourceState.Disabled);
        }

        if (outcome?.Value is not OvertureInfrastructureLookupDiagnostics value)
        {
            return FailedSource(outcome?.Error);
        }

        WorkerJobSafeError? error = value.Error is null
            ? outcome.Error
            : DependencyError("airport-source-failed", "Bundled airport lookup failed.");
        CoordinateLookupCandidate[] candidates = value.Candidates
            .OrderByDescending(static candidate => candidate.Selected)
            .ThenBy(static candidate => OverturePlacesLogic.GetInfrastructureClassRank(candidate.SubType, candidate.ClassName))
            .ThenByDescending(static candidate => candidate.GeometryContainsPoint)
            .ThenByDescending(static candidate => candidate.BoundingBoxContainsPoint)
            .ThenBy(static candidate => candidate.DistanceMetres)
            .ThenBy(static candidate => candidate.Id, StringComparer.Ordinal)
            .Select(MapAirportCandidate)
            .ToArray();
        CoordinateLookupCandidate? best = value.BestMatch is null
            ? null
            : MapAirportBest(value.BestMatch, candidates);
        return new CoordinateLookupSourceResult(
            SourceState(best, error),
            NormalizeOptional(value.Release, out int releaseTrim),
            null,
            best,
            candidates,
            [],
            error,
            null,
            null,
            null,
            truncatedTextCount: releaseTrim + candidates.Sum(static candidate => candidate.TruncatedTextCount));
    }

    private static CoordinateLookupSourceResult MapPlaces(
        bool enabled,
        SourceOutcome<OvertureLookupDiagnostics>? outcome)
    {
        if (!enabled)
        {
            return EmptySource(CoordinateLookupSourceState.Disabled);
        }

        if (outcome?.Value is not OvertureLookupDiagnostics value)
        {
            return FailedSource(outcome?.Error);
        }

        WorkerJobSafeError? error = value.Error is null
            ? outcome.Error
            : DependencyError("places-source-failed", "Live Overture Places lookup failed.");
        CoordinateLookupCandidate[] candidates = value.Candidates
            .OrderByDescending(static candidate => candidate.Selected)
            .ThenByDescending(static candidate => OverturePlacesLogic.IsPreferredStatus(candidate.OperatingStatus))
            .ThenByDescending(static candidate => candidate.BoundingBoxContainsPoint)
            .ThenBy(static candidate => candidate.DistanceMetres)
            .ThenBy(static candidate => candidate.Id, StringComparer.Ordinal)
            .Select(MapPlaceCandidate)
            .ToArray();
        CoordinateLookupCandidate? best = value.BestMatch is null
            ? null
            : MapPlaceBest(value.BestMatch, candidates);
        return new CoordinateLookupSourceResult(
            SourceState(best, error),
            NormalizeOptional(value.Release, out int releaseTrim),
            null,
            best,
            candidates,
            [],
            error,
            null,
            null,
            null,
            truncatedTextCount: releaseTrim + candidates.Sum(static candidate => candidate.TruncatedTextCount));
    }

    private static CoordinateLookupCandidate MapDivisionCandidate(OvertureDivisionCandidateDiagnostic value)
    {
        var text = new TextNormalizer();
        return new CoordinateLookupCandidate(
            text.Required(value.Id, "unknown", CoordinateLookupProtocolBounds.MaxIdentifierLength),
            text.Required(value.Name, "Unnamed division"),
            value.Selected,
            text.Required(value.Decision, "No diagnostic decision."),
            value.BoundingBoxContainsPoint,
            value.GeometryContainsPoint,
            null,
            text.Optional(value.SubType),
            null,
            null,
            text.Optional(value.ClassName),
            value.AdminLevel,
            value.IsLand,
            value.IsTerritorial,
            text.Optional(value.Country),
            null,
            null,
            null,
            null,
            null,
            value.BoundingBoxArea,
            [],
            truncatedTextCount: text.TruncatedCount);
    }

    private static CoordinateLookupCandidate MapDivisionBest(
        OvertureDivisionResult value,
        IReadOnlyList<CoordinateLookupCandidate> candidates)
    {
        CoordinateLookupCandidate? matching = candidates.FirstOrDefault(
            candidate => string.Equals(candidate.Id, value.Id, StringComparison.Ordinal));
        return matching ?? MapDivisionCandidate(new OvertureDivisionCandidateDiagnostic(
            value.Id,
            value.Name,
            value.SubType,
            value.ClassName,
            value.AdminLevel,
            value.Country,
            value.IsLand,
            value.IsTerritorial,
            value.BoundingBoxContainsPoint,
            value.GeometryContainsPoint,
            value.BoundingBoxArea,
            true,
            "Selected best match."));
    }

    private static CoordinateLookupCandidate MapGadmCandidate(GadmDivisionCandidateDiagnostic value)
    {
        var text = new TextNormalizer();
        return new CoordinateLookupCandidate(
            text.Required(value.Id, "unknown", CoordinateLookupProtocolBounds.MaxIdentifierLength),
            text.Required(value.Name, "Unnamed GADM division"),
            value.Selected,
            text.Required(value.Decision, "No diagnostic decision."),
            value.BoundingBoxContainsPoint,
            value.GeometryContainsPoint,
            null,
            null,
            null,
            null,
            null,
            value.AdminLevel,
            null,
            null,
            null,
            text.Optional(value.EnglishType),
            text.Optional(value.LocalType),
            null,
            null,
            null,
            value.BoundingBoxArea,
            [],
            truncatedTextCount: text.TruncatedCount);
    }

    private static CoordinateLookupCandidate MapGadmBest(
        GadmDivisionResult value,
        IReadOnlyList<CoordinateLookupCandidate> candidates)
    {
        CoordinateLookupCandidate? matching = candidates.FirstOrDefault(
            candidate => string.Equals(candidate.Id, value.Id, StringComparison.Ordinal));
        return matching ?? MapGadmCandidate(new GadmDivisionCandidateDiagnostic(
            value.Id,
            value.Name,
            value.EnglishType,
            value.LocalType,
            value.AdminLevel,
            value.BoundingBoxContainsPoint,
            value.GeometryContainsPoint,
            value.BoundingBoxArea,
            true,
            "Selected best match."));
    }

    private static CoordinateLookupCandidate MapAirportCandidate(OvertureInfrastructureCandidateDiagnostic value)
    {
        var text = new TextNormalizer();
        return new CoordinateLookupCandidate(
            text.Required(value.Id, "unknown", CoordinateLookupProtocolBounds.MaxIdentifierLength),
            text.Required(value.Name, "Unnamed airport"),
            value.Selected,
            text.Required(value.Decision, "No diagnostic decision."),
            value.BoundingBoxContainsPoint,
            value.GeometryContainsPoint,
            text.Optional(value.FeatureType),
            text.Optional(value.SubType),
            null,
            null,
            text.Optional(value.ClassName),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            value.DistanceMetres,
            null,
            null,
            value.Sources.Select(source => text.Required(
                source,
                "unknown",
                CoordinateLookupProtocolBounds.MaxIdentifierLength)).ToArray(),
            truncatedTextCount: text.TruncatedCount);
    }

    private static CoordinateLookupCandidate MapAirportBest(
        OvertureInfrastructureResult value,
        IReadOnlyList<CoordinateLookupCandidate> candidates)
    {
        CoordinateLookupCandidate? matching = candidates.FirstOrDefault(
            candidate => string.Equals(candidate.Id, value.Id, StringComparison.Ordinal));
        return matching ?? MapAirportCandidate(new OvertureInfrastructureCandidateDiagnostic(
            value.Id,
            value.Name,
            value.FeatureType,
            value.SubType,
            value.ClassName,
            value.DistanceMetres,
            value.BoundingBoxContainsPoint,
            value.GeometryContainsPoint,
            value.Sources,
            true,
            "Selected best match."));
    }

    private static CoordinateLookupCandidate MapPlaceCandidate(OvertureCandidateDiagnostic value)
    {
        var text = new TextNormalizer();
        return new CoordinateLookupCandidate(
            text.Required(value.Id, "unknown", CoordinateLookupProtocolBounds.MaxIdentifierLength),
            text.Required(value.Name, "Unnamed place"),
            value.Selected,
            text.Required(value.Decision, "No diagnostic decision."),
            value.BoundingBoxContainsPoint,
            null,
            null,
            null,
            text.Optional(value.Category),
            text.Optional(value.BasicCategory),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            text.Optional(value.OperatingStatus),
            value.DistanceMetres,
            value.Confidence,
            null,
            value.Sources.Select(source => text.Required(
                source,
                "unknown",
                CoordinateLookupProtocolBounds.MaxIdentifierLength)).ToArray(),
            truncatedTextCount: text.TruncatedCount);
    }

    private static CoordinateLookupCandidate MapPlaceBest(
        OverturePlaceResult value,
        IReadOnlyList<CoordinateLookupCandidate> candidates)
    {
        CoordinateLookupCandidate? matching = candidates.FirstOrDefault(
            candidate => string.Equals(candidate.Id, value.Id, StringComparison.Ordinal));
        return matching ?? MapPlaceCandidate(new OvertureCandidateDiagnostic(
            value.Id,
            value.Name,
            value.Category,
            value.BasicCategory,
            value.Confidence,
            value.OperatingStatus,
            value.DistanceMetres,
            value.BoundingBoxContainsPoint,
            value.Sources,
            true,
            "Selected best match."));
    }

    private static CoordinateLookupProfileSummary ProfileSummary(
        string? iso3,
        CityResolverProfile profile)
    {
        CoordinateLookupTieBreak tieBreak = profile.TieBreakMode switch
        {
            CityResolverTieBreakModes.LargestArea => CoordinateLookupTieBreak.LargestArea,
            _ => CoordinateLookupTieBreak.SmallestArea
        };
        string[] subtypes = profile.PreferredSubtypes
            .Where(static subtype => !string.IsNullOrWhiteSpace(subtype))
            .Select(static subtype => subtype.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(CoordinateLookupProtocolBounds.MaxPreferredSubtypes)
            .ToArray();
        return new CoordinateLookupProfileSummary(iso3, subtypes, tieBreak);
    }

    private static CoordinateLookupSourceResult FailedSource(WorkerJobSafeError? error) =>
        new(
            CoordinateLookupSourceState.Failed,
            null,
            null,
            null,
            [],
            [],
            error ?? DependencyError("source-failed", "The lookup source failed."),
            null,
            null,
            null);

    private static CoordinateLookupSourceResult EmptySource(CoordinateLookupSourceState state) =>
        new(state, null, null, null, [], [], null, null, null, null);

    private static CoordinateLookupSourceResult GadmSource(CoordinateLookupSourceState state) =>
        new(
            state,
            null,
            GadmDivisionsLogic.DatasetVersion,
            null,
            [],
            [],
            null,
            CoordinateLookupGadmAttribution.DatasetName,
            CoordinateLookupGadmAttribution.LicenseUrl,
            CoordinateLookupGadmAttribution.UsageNotice);

    private static WorkerJobSafeError DependencyError(string code, string message) =>
        new(code, WorkerJobFailureCategory.Dependency, message);

    private static string? NormalizeOptional(string? value, out int truncatedCount)
    {
        truncatedCount = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        if (normalized.Length <= CoordinateLookupProtocolBounds.MaxDisplayTextLength)
        {
            return normalized;
        }

        truncatedCount = 1;
        return normalized[..CoordinateLookupProtocolBounds.MaxDisplayTextLength];
    }

    private static string NormalizeRequired(
        string? value,
        string fallback,
        out int truncatedCount,
        int maximumLength = CoordinateLookupProtocolBounds.MaxDisplayTextLength)
    {
        truncatedCount = 0;
        string normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (normalized.Length <= maximumLength)
        {
            return normalized;
        }

        truncatedCount = 1;
        return normalized[..maximumLength];
    }

    private sealed class TextNormalizer
    {
        internal int TruncatedCount { get; private set; }

        internal string Required(
            string? value,
            string fallback,
            int maximumLength = CoordinateLookupProtocolBounds.MaxDisplayTextLength)
        {
            string normalized = NormalizeRequired(value, fallback, out int truncated, maximumLength);
            TruncatedCount += truncated;
            return normalized;
        }

        internal string? Optional(string? value)
        {
            string? normalized = NormalizeOptional(value, out int truncated);
            TruncatedCount += truncated;
            return normalized;
        }
    }

    private sealed record CacheOutcome(
        bool Ready,
        IReadOnlyList<CoordinateLookupCacheStatus> Statuses,
        WorkerJobSafeError? Error);

    private sealed record GadmCacheOutcome(
        IReadOnlyList<string> ReadyCodes,
        IReadOnlyList<CoordinateLookupCacheStatus> Statuses)
    {
        internal static GadmCacheOutcome Disabled() => new([], []);
    }

    private sealed record SourceOutcome<T>(T? Value, WorkerJobSafeError? Error)
        where T : class
    {
        internal static SourceOutcome<T> Success(T value) => new(value, null);
        internal static SourceOutcome<T> Failure(WorkerJobSafeError error) => new(null, error);
    }

    private sealed class CoordinateLookupReporterException(Exception innerException) :
        Exception("Coordinate lookup event reporting failed.", innerException);
}
