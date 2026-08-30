using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Gadm.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class AdministrativeAreaResolverService
{
    private readonly ILogger<AdministrativeAreaResolverService> _logger;
    private readonly CityResolverProfileCatalogService _cityResolverCatalog;
    private readonly OvertureDivisionsService _overtureDivisions;
    private readonly OvertureDivisionCacheService _overtureCache;
    private readonly GadmDivisionsService _gadmDivisions;
    private readonly GadmDivisionCacheService _gadmCache;

    public AdministrativeAreaResolverService(
        ILogger<AdministrativeAreaResolverService> logger,
        CityResolverProfileCatalogService cityResolverCatalog,
        OvertureDivisionsService overtureDivisions,
        OvertureDivisionCacheService overtureCache,
        GadmDivisionsService gadmDivisions,
        GadmDivisionCacheService gadmCache)
    {
        _logger = logger;
        _cityResolverCatalog = cityResolverCatalog;
        _overtureDivisions = overtureDivisions;
        _overtureCache = overtureCache;
        _gadmDivisions = gadmDivisions;
        _gadmCache = gadmCache;
    }

    public Task<AdministrativeAreaResolution?> ResolveAsync(
        double lat,
        double lon,
        ProcessingConfig config,
        CancellationToken ct = default)
    {
        return ResolveCoreAsync(lat, lon, config, null, ct);
    }

    public async Task<AdministrativeAreaResolution?> ResolveAsync(
        double lat,
        double lon,
        ProcessingConfig config,
        IProcessingRunEventSession session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return await ResolveCoreAsync(lat, lon, config, session, ct).ConfigureAwait(false);
    }

    private async Task<AdministrativeAreaResolution?> ResolveCoreAsync(
        double lat,
        double lon,
        ProcessingConfig config,
        IProcessingRunEventSession? session,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await ReportAsync(session, "Checking bundled Overture country coverage...", ct).ConfigureAwait(false);
        var countryLookup = await _overtureDivisions.FindBundledCountryAsync(lat, lon, ct).ConfigureAwait(false);
        if (countryLookup.Status == BundledCountryLookupStatus.SpatialNoMatch)
        {
            await ReportAsync(session, countryLookup.FailureReason ?? "Bundled Overture spatial coverage found no match.", ct).ConfigureAwait(false);
            return null;
        }

        if (countryLookup.Status == BundledCountryLookupStatus.IdentityMappingFailure)
        {
            await ReportAsync(session, countryLookup.FailureReason ?? "Bundled Overture country identity mapping failed.", ct).ConfigureAwait(false);
            return null;
        }

        var iso3 = countryLookup.Iso3!;
        var countryName = countryLookup.CountryName!;
        var alpha2 = countryLookup.Alpha2!;
        await ReportAsync(session, $"Country resolved as {countryName} ({iso3}).", ct).ConfigureAwait(false);
        var cityResolverProfile = _cityResolverCatalog.GetProfile(config.CityResolver, iso3);
        OvertureAdministrativeResult? overtureResult = null;
        GadmAdministrativeResult? gadmResult = null;

        if (!config.UseGadmAdministrativeAreas || !config.PreferGadmAdministrativeAreas)
        {
            overtureResult = await ResolveOvertureAsync(lat, lon, alpha2, iso3, cityResolverProfile, session, ct).ConfigureAwait(false);
        }

        if (config.UseGadmAdministrativeAreas)
        {
            gadmResult = await ResolveGadmAsync(lat, lon, iso3, config.UseGadmTerritoryFallbacks, session, ct).ConfigureAwait(false);
        }

        if (config.UseGadmAdministrativeAreas && config.PreferGadmAdministrativeAreas)
        {
            overtureResult ??= await ResolveOvertureAsync(lat, lon, alpha2, iso3, cityResolverProfile, session, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        var finalState = SelectPreferredValue(config, gadmResult?.State, overtureResult?.State);
        var finalCity = SelectPreferredValue(config, gadmResult?.City, overtureResult?.City);
        ct.ThrowIfCancellationRequested();
        return new AdministrativeAreaResolution(iso3, alpha2, countryName, new GeoResult(countryName, finalState, finalCity), overtureResult, gadmResult);
    }

    private async Task<OvertureAdministrativeResult?> ResolveOvertureAsync(
        double lat, double lon, string? alpha2, string iso3, CityResolverProfile cityResolverProfile,
        IProcessingRunEventSession? session, CancellationToken ct)
    {
        var (downloadTask, ensureResult) = _overtureCache.GetOrStartDownload(iso3, ct);
        if (ensureResult == OvertureDivisionEnsureResult.StartedDownload)
        {
            _logger.LogInformation("Preparing Overture divisions cache for {ISO3}", iso3);
        }

        var activity = await BeginCacheActivityAsync(session, GetOvertureCacheActivityMessage(iso3, ensureResult), ct).ConfigureAwait(false);
        try
        {
            await ReportAsync(session, GetOvertureCacheLogMessage(iso3, ensureResult), ct).ConfigureAwait(false);
            try
            {
                await downloadTask.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Overture division source became unavailable for {iso3}.", ex);
            }
        }
        finally
        {
            await EndCacheActivityAsync(activity).ConfigureAwait(false);
        }

        await ReportAsync(session, $"Overture administrative cache ready for {iso3}.", ct).ConfigureAwait(false);
        await ReportAsync(session, "Querying cached Overture administrative areas...", ct).ConfigureAwait(false);
        return await _overtureDivisions.ResolveAdministrativeGeoAsync(lat, lon, alpha2, iso3, cityResolverProfile, ct).ConfigureAwait(false);
    }

    private async Task<GadmAdministrativeResult?> ResolveGadmAsync(
        double lat, double lon, string iso3, bool useTerritoryFallbacks, IProcessingRunEventSession? session, CancellationToken ct)
    {
        var candidateCodes = useTerritoryFallbacks ? GadmCountryFallbackCatalog.ExpandCandidateCodes(iso3) : [iso3];
        await ReportAsync(session, $"Preparing GADM administrative caches for {string.Join(", ", candidateCodes)}...", ct).ConfigureAwait(false);
        var readyCodes = new List<string>();
        foreach (var code in candidateCodes)
        {
            var available = await PrepareGadmCacheAsync(code, session, ct).ConfigureAwait(false);
            if (available)
            {
                readyCodes.Add(code);
            }
        }

        if (readyCodes.Count == 0)
        {
            await ReportAsync(session, "No GADM administrative caches are available for this lookup.", ct).ConfigureAwait(false);
            return null;
        }

        await ReportAsync(session, $"Querying cached GADM administrative areas across {string.Join(", ", readyCodes)}...", ct).ConfigureAwait(false);
        var diagnostics = await _gadmDivisions.FindContainingDivisionAreasAsync(lat, lon, readyCodes, ct).ConfigureAwait(false);
        if (diagnostics.Error is not null || diagnostics.Candidates.Count == 0)
        {
            return null;
        }

        return new GadmAdministrativeResult(GadmDivisionsLogic.SelectStateName(diagnostics.Candidates), GadmDivisionsLogic.SelectCityName(diagnostics.Candidates));
    }

    private static string? SelectPreferredValue(ProcessingConfig config, string? gadmValue, string? overtureValue)
    {
        if (!config.UseGadmAdministrativeAreas)
        {
            return overtureValue;
        }

        if (config.PreferGadmAdministrativeAreas)
        {
            return gadmValue ?? overtureValue;
        }

        return overtureValue ?? gadmValue;
    }

    private async Task<bool> PrepareGadmCacheAsync(string code, IProcessingRunEventSession? session, CancellationToken ct)
    {
        Task downloadTask;
        GadmDivisionEnsureResult ensureResult;
        try
        {
            (downloadTask, ensureResult) = _gadmCache.GetOrStartDownload(code, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await ReportGadmUnavailableAsync(code, ex, session, ct).ConfigureAwait(false);
            return false;
        }

        var activity = await BeginCacheActivityAsync(session, GetGadmCacheActivityMessage(code, ensureResult), ct).ConfigureAwait(false);
        Exception? sourceFailure = null;
        try
        {
            await ReportAsync(session, GetGadmCacheLogMessage(code, ensureResult), ct).ConfigureAwait(false);
            try
            {
                await downloadTask.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sourceFailure = ex;
            }
        }
        finally
        {
            if (activity is not null)
            {
                await EndCacheActivityAsync(activity).ConfigureAwait(false);
            }
        }

        if (sourceFailure is OperationCanceledException && ct.IsCancellationRequested)
        {
            throw sourceFailure;
        }

        if (sourceFailure is OutOfMemoryException)
        {
            throw sourceFailure;
        }

        if (sourceFailure is not null)
        {
            await ReportGadmUnavailableAsync(code, sourceFailure, session, ct).ConfigureAwait(false);
            return false;
        }

        try
        {
            if (!_gadmCache.HasData(code))
            {
                return false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await ReportGadmUnavailableAsync(code, ex, session, ct).ConfigureAwait(false);
            return false;
        }

        await ReportAsync(session, $"GADM administrative cache ready for {code}.", ct).ConfigureAwait(false);
        return true;
    }

    private async Task ReportGadmUnavailableAsync(string code, Exception exception, IProcessingRunEventSession? session, CancellationToken ct)
    {
        _logger.LogWarning(exception, "GADM cache unavailable for {ISO3}", code);
        await ReportAsync(session, $"GADM administrative cache unavailable for {code}: {exception.Message}", ct).ConfigureAwait(false);
    }

    private static async Task ReportAsync(IProcessingRunEventSession? session, string message, CancellationToken ct)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            await session.ReportLogAsync(ProcessingLogLevel.Information, message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProcessingEventReportingException(ex);
        }
    }

    private static async ValueTask EndCacheActivityAsync(IAsyncDisposable? activity)
    {
        if (activity is null)
        {
            return;
        }

        try
        {
            await activity.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new ProcessingEventReportingException(ex);
        }
    }

    private static async ValueTask<IAsyncDisposable?> BeginCacheActivityAsync(IProcessingRunEventSession? session, string? activity, CancellationToken ct)
    {
        if (session is null || string.IsNullOrWhiteSpace(activity))
        {
            return null;
        }

        try
        {
            return await session.BeginActivityAsync(activity, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProcessingEventReportingException(ex);
        }
    }

    private static string? GetOvertureCacheActivityMessage(string iso3, OvertureDivisionEnsureResult result) => result switch
    {
        OvertureDivisionEnsureResult.StartedDownload => $"Downloading Overture administrative cache for {iso3}...",
        OvertureDivisionEnsureResult.AwaitedExistingDownload => $"Waiting for Overture administrative cache for {iso3}...",
        _ => null
    };

    private static string GetOvertureCacheLogMessage(string iso3, OvertureDivisionEnsureResult result) => result switch
    {
        OvertureDivisionEnsureResult.StartedDownload => $"Starting Overture administrative cache download for {iso3}.",
        OvertureDivisionEnsureResult.AwaitedExistingDownload => $"Waiting for in-flight Overture administrative cache download for {iso3}.",
        _ => $"Overture administrative cache already ready for {iso3}."
    };

    private static string? GetGadmCacheActivityMessage(string iso3, GadmDivisionEnsureResult result) => result switch
    {
        GadmDivisionEnsureResult.StartedDownload => $"Downloading GADM administrative cache for {iso3}...",
        GadmDivisionEnsureResult.AwaitedExistingDownload => $"Waiting for GADM administrative cache for {iso3}...",
        _ => null
    };

    private static string GetGadmCacheLogMessage(string iso3, GadmDivisionEnsureResult result) => result switch
    {
        GadmDivisionEnsureResult.StartedDownload => $"Starting GADM administrative cache download for {iso3}.",
        GadmDivisionEnsureResult.AwaitedExistingDownload => $"Waiting for in-flight GADM administrative cache download for {iso3}.",
        _ => $"GADM administrative cache already ready for {iso3}."
    };
}

public record AdministrativeAreaResolution(string Iso3, string? Alpha2, string CountryName, GeoResult GeoResult, OvertureAdministrativeResult? OvertureResult, GadmAdministrativeResult? GadmResult);
internal sealed class ProcessingEventReportingException(Exception reporterException) : Exception("Processing event reporting failed.", reporterException)
{
    public Exception ReporterException { get; } = reporterException;
}
