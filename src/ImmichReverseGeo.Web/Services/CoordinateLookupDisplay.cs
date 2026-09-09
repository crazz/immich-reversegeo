using System.Collections.Generic;
using System.Linq;
using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Web.Services;

internal sealed class CoordinateLookupDisplayResult
{
    internal CoordinateLookupDisplayResult(CoordinateLookupResult result)
    {
        CountryStatus = result.Country.Status;
        CountryError = result.Country.Error?.Message;
        Iso3 = result.Country.Iso3;
        CountryName = result.Country.Name;
        Alpha2 = result.Country.Alpha2;
        AirportInfrastructureEnabled = result.Request.IncludeAirportInfrastructure;
        LivePlacesEnabled = result.Request.IncludeLiveOverturePlaces;
        GadmEnabled = result.Request.PreferGadmAdministrativeAreas;
        OvertureDivisionDiagnostics = new CoordinateLookupDisplaySource(result.OvertureDivisions);
        OvertureDivision = OvertureDivisionDiagnostics.BestMatch;
        OvertureDivisionState = result.OvertureAdministrative.State;
        OvertureDivisionCity = result.OvertureAdministrative.City;
        GadmDivisionDiagnostics = new CoordinateLookupDisplaySource(result.GadmDivisions);
        GadmDivision = GadmDivisionDiagnostics.BestMatch;
        GadmDivisionState = result.GadmAdministrative.State;
        GadmDivisionCity = result.GadmAdministrative.City;
        OvertureInfrastructureDiagnostics = new CoordinateLookupDisplaySource(result.AirportInfrastructure);
        OvertureInfrastructure = OvertureInfrastructureDiagnostics.BestMatch;
        OvertureDiagnostics = new CoordinateLookupDisplaySource(result.LiveOverturePlaces);
        OverturePoi = OvertureDiagnostics.BestMatch;
        FinalCountry = result.FinalLocation.Country?.Value;
        FinalState = result.FinalLocation.State?.Value;
        FinalStateSource = DescribeFinalSource(result.FinalLocation.State);
        FinalCity = result.FinalLocation.City?.Value;
        FinalCitySource = DescribeFinalSource(result.FinalLocation.City);
        FinalCountrySource = DescribeFinalSource(result.FinalLocation.Country);
        CityResolverSummary = $"{string.Join(" > ", result.Profile.PreferredSubtypes)} ({result.Profile.TieBreak})";
        Trace = result.Trace;
        OmittedTraceCount = result.OmittedTraceCount;
        TruncatedTextCount = result.TruncatedTextCount;
    }

    internal CoordinateLookupCountryStatus CountryStatus { get; }
    internal string? CountryError { get; }
    internal string? Iso3 { get; }
    internal string? CountryName { get; }
    internal string? Alpha2 { get; }
    internal bool AirportInfrastructureEnabled { get; }
    internal bool LivePlacesEnabled { get; }
    internal bool GadmEnabled { get; }
    internal bool OvertureDivisionCacheReady => OvertureDivisionDiagnostics.Caches.Any(
        static cache => cache.State == CoordinateLookupSourceState.Ready);
    internal CoordinateLookupDisplayCandidate? OvertureDivision { get; }
    internal CoordinateLookupDisplaySource OvertureDivisionDiagnostics { get; }
    internal string? OvertureDivisionState { get; }
    internal string? OvertureDivisionCity { get; }
    internal bool GadmDivisionCacheReady => GadmDivisionDiagnostics.Caches.Any(
        static cache => cache.State == CoordinateLookupSourceState.Ready);
    internal CoordinateLookupDisplayCandidate? GadmDivision { get; }
    internal CoordinateLookupDisplaySource GadmDivisionDiagnostics { get; }
    internal string? GadmDivisionState { get; }
    internal string? GadmDivisionCity { get; }
    internal CoordinateLookupDisplayCandidate? OvertureInfrastructure { get; }
    internal CoordinateLookupDisplaySource OvertureInfrastructureDiagnostics { get; }
    internal CoordinateLookupDisplayCandidate? OverturePoi { get; }
    internal CoordinateLookupDisplaySource OvertureDiagnostics { get; }
    internal string? FinalCountry { get; }
    internal string? FinalState { get; }
    internal string? FinalStateSource { get; }
    internal string? FinalCity { get; }
    internal string? FinalCitySource { get; }
    internal string? FinalCountrySource { get; }
    internal string CityResolverSummary { get; }
    internal IReadOnlyList<string> Trace { get; }
    internal int OmittedTraceCount { get; }
    internal int TruncatedTextCount { get; }

    private static string? DescribeFinalSource(CoordinateLookupAttributedValue? value)
    {
        return value?.Source switch
        {
            CoordinateLookupFinalSource.BundledCountryDivisions => "Bundled Overture country divisions",
            CoordinateLookupFinalSource.CachedOvertureDivisions => "Cached Overture divisions",
            CoordinateLookupFinalSource.CachedGadmDivisions => "Cached GADM divisions",
            CoordinateLookupFinalSource.BundledAirportGeometryMatch => "Bundled airport infrastructure (geometry match)",
            CoordinateLookupFinalSource.BundledAirportFallback => "Bundled airport infrastructure (fallback)",
            _ => null
        };
    }
}

internal sealed class CoordinateLookupDisplaySource
{
    internal CoordinateLookupDisplaySource(CoordinateLookupSourceResult source)
    {
        State = source.State;
        Release = source.Release;
        Version = source.Version;
        BestMatch = source.BestMatch is null
            ? null
            : new CoordinateLookupDisplayCandidate(source.BestMatch);
        Candidates = source.Candidates
            .Select(static candidate => new CoordinateLookupDisplayCandidate(candidate))
            .ToArray();
        Caches = source.Caches;
        Error = source.Error?.Message;
        Attribution = source.Attribution;
        LicenseUrl = source.LicenseUrl;
        UsageNotice = source.UsageNotice;
        OmittedCandidateCount = source.OmittedCandidateCount;
        OmittedCacheCount = source.OmittedCacheCount;
        TruncatedTextCount = source.TruncatedTextCount;
    }

    internal CoordinateLookupSourceState State { get; }
    internal string? Release { get; }
    internal string? Version { get; }
    internal CoordinateLookupDisplayCandidate? BestMatch { get; }
    internal IReadOnlyList<CoordinateLookupDisplayCandidate> Candidates { get; }
    internal IReadOnlyList<CoordinateLookupCacheStatus> Caches { get; }
    internal string? Error { get; }
    internal string? Attribution { get; }
    internal string? LicenseUrl { get; }
    internal string? UsageNotice { get; }
    internal int OmittedCandidateCount { get; }
    internal int OmittedCacheCount { get; }
    internal int TruncatedTextCount { get; }
}

internal sealed class CoordinateLookupDisplayCandidate
{
    internal CoordinateLookupDisplayCandidate(CoordinateLookupCandidate candidate)
    {
        Id = candidate.Id;
        Name = candidate.Name;
        Selected = candidate.Selected;
        Decision = candidate.Decision;
        BoundingBoxContainsPoint = candidate.BoundingBoxContainsCoordinate;
        GeometryContainsPoint = candidate.GeometryContainsCoordinate;
        FeatureType = candidate.FeatureType;
        SubType = candidate.Subtype;
        Category = candidate.Category;
        BasicCategory = candidate.BasicCategory;
        ClassName = candidate.Classification;
        AdminLevel = candidate.AdminLevel;
        IsTerritorial = candidate.IsTerritorial;
        Country = candidate.Country;
        EnglishType = candidate.EnglishType;
        LocalType = candidate.LocalType;
        OperatingStatus = candidate.OperatingStatus;
        DistanceMetres = candidate.DistanceMeters;
        Confidence = candidate.Confidence;
        BoundingBoxArea = candidate.BoundingBoxArea;
        Sources = candidate.RecordSources;
        OmittedRecordSourceCount = candidate.OmittedRecordSourceCount;
        TruncatedTextCount = candidate.TruncatedTextCount;
    }

    internal string Id { get; }
    internal string Name { get; }
    internal bool Selected { get; }
    internal string Decision { get; }
    internal bool? BoundingBoxContainsPoint { get; }
    internal bool? GeometryContainsPoint { get; }
    internal string? FeatureType { get; }
    internal string? SubType { get; }
    internal string? Category { get; }
    internal string? BasicCategory { get; }
    internal string? ClassName { get; }
    internal int? AdminLevel { get; }
    internal bool? IsTerritorial { get; }
    internal string? Country { get; }
    internal string? EnglishType { get; }
    internal string? LocalType { get; }
    internal string? OperatingStatus { get; }
    internal double? DistanceMetres { get; }
    internal double? Confidence { get; }
    internal double? BoundingBoxArea { get; }
    internal IReadOnlyList<string> Sources { get; }
    internal int OmittedRecordSourceCount { get; }
    internal int TruncatedTextCount { get; }
}
