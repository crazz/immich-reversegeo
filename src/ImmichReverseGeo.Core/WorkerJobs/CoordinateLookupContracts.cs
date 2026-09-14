using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Core.WorkerJobs;

public static class CoordinateLookupProtocolBounds
{
    public const int MaxCountryProfiles = 32;
    public const int MaxPreferredSubtypes = 16;
    public const int MaxSubtypeLength = 48;
    public const int MaxCandidatesPerSource = 6;
    public const int MaxRecordSourcesPerCandidate = 4;
    public const int MaxCacheStatuses = 16;
    public const int MaxTraceEntries = 64;
    public const int MaxIdentifierLength = 128;
    public const int MaxDisplayTextLength = WorkerJobProtocolV2.MaxSafeTextLength;
}

public static class CoordinateLookupGadmAttribution
{
    public const string DatasetName = "GADM";
    public const string LicenseUrl = "https://gadm.org/license.html";
    public const string UsageNotice =
        "GADM data is limited to academic and other non-commercial use.";
}

public enum CoordinateLookupTieBreak
{
    SmallestArea,
    LargestArea
}

public sealed record CoordinateLookupCityProfile
{
    public IReadOnlyList<string> PreferredSubtypes { get; }
    public CoordinateLookupTieBreak? TieBreak { get; }

    public CoordinateLookupCityProfile(
        IReadOnlyList<string> preferredSubtypes,
        CoordinateLookupTieBreak? tieBreak)
    {
        ArgumentNullException.ThrowIfNull(preferredSubtypes);
        if (tieBreak is not null && !Enum.IsDefined(tieBreak.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(tieBreak));
        }

        var values = preferredSubtypes.ToArray();
        if (values.Length > CoordinateLookupProtocolBounds.MaxPreferredSubtypes)
        {
            throw new ArgumentException("A city profile contains too many preferred subtypes.", nameof(preferredSubtypes));
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in values)
        {
            RequireCanonicalToken(
                value,
                CoordinateLookupProtocolBounds.MaxSubtypeLength,
                nameof(preferredSubtypes));
            if (!unique.Add(value))
            {
                throw new ArgumentException("Preferred city subtypes must be unique.", nameof(preferredSubtypes));
            }
        }

        PreferredSubtypes = Array.AsReadOnly(values);
        TieBreak = tieBreak;
    }

    internal static void RequireCanonicalToken(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value != value.Trim()
            || value != value.ToLowerInvariant())
        {
            throw new ArgumentException("The value must be a bounded canonical lower-case token.", parameterName);
        }

        foreach (char character in value)
        {
            if (character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '-'
                and not '_')
            {
                throw new ArgumentException("The value must be a bounded canonical lower-case token.", parameterName);
            }
        }
    }
}

public sealed record CoordinateLookupCountryProfile
{
    public string CountryCode { get; }
    public CoordinateLookupCityProfile Profile { get; }

    public CoordinateLookupCountryProfile(string countryCode, CoordinateLookupCityProfile profile)
    {
        RequireIso3(countryCode, nameof(countryCode));
        ArgumentNullException.ThrowIfNull(profile);
        CountryCode = countryCode;
        Profile = profile;
    }

    internal static void RequireIso3(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 3 || value.Any(static character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("A country code must be canonical ISO3 upper case.", parameterName);
        }
    }
}

public sealed record CoordinateLookupCityResolverOverrides
{
    public CoordinateLookupCityProfile? DefaultProfile { get; }
    public IReadOnlyList<CoordinateLookupCountryProfile> CountryProfiles { get; }

    public CoordinateLookupCityResolverOverrides(
        CoordinateLookupCityProfile? defaultProfile,
        IReadOnlyList<CoordinateLookupCountryProfile> countryProfiles)
    {
        ArgumentNullException.ThrowIfNull(countryProfiles);
        var values = countryProfiles.ToArray();
        if (values.Length > CoordinateLookupProtocolBounds.MaxCountryProfiles)
        {
            throw new ArgumentException("Too many country city profiles were supplied.", nameof(countryProfiles));
        }

        for (var index = 0; index < values.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(values[index]);
            if (index > 0
                && string.CompareOrdinal(values[index - 1].CountryCode, values[index].CountryCode) >= 0)
            {
                throw new ArgumentException(
                    "Country city profiles must be unique and sorted by canonical ISO3 code.",
                    nameof(countryProfiles));
            }
        }

        DefaultProfile = defaultProfile;
        CountryProfiles = Array.AsReadOnly(values);
    }
}

public static class CoordinateLookupCityProfileConversions
{
    public static CoordinateLookupCityResolverOverrides Snapshot(CityResolverConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        CoordinateLookupCityProfile? defaultProfile = SnapshotProfile(config.DefaultProfile);
        CoordinateLookupCountryProfile[] countries = config.CountryOverrides
            .Select(static pair => new KeyValuePair<string, CityResolverProfile>(
                pair.Key.ToUpperInvariant(),
                pair.Value))
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new CoordinateLookupCountryProfile(
                pair.Key,
                SnapshotProfile(pair.Value)))
            .ToArray();
        return new CoordinateLookupCityResolverOverrides(defaultProfile, countries);
    }

    public static CityResolverConfig ToConfig(CoordinateLookupCityResolverOverrides snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var config = new CityResolverConfig
        {
            DefaultProfile = ToMutableProfile(snapshot.DefaultProfile)
        };
        foreach (CoordinateLookupCountryProfile country in snapshot.CountryProfiles)
        {
            config.CountryOverrides.Add(country.CountryCode, ToMutableProfile(country.Profile));
        }

        return config;
    }

    private static CoordinateLookupCityProfile SnapshotProfile(CityResolverProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        CoordinateLookupTieBreak? tieBreak = profile.TieBreakMode switch
        {
            "" => null,
            CityResolverTieBreakModes.SmallestArea => CoordinateLookupTieBreak.SmallestArea,
            CityResolverTieBreakModes.LargestArea => CoordinateLookupTieBreak.LargestArea,
            _ => throw new ArgumentException("The city profile tie-break is not canonical.", nameof(profile))
        };
        return new CoordinateLookupCityProfile(profile.PreferredSubtypes, tieBreak);
    }

    private static CityResolverProfile ToMutableProfile(CoordinateLookupCityProfile? profile)
    {
        if (profile is null)
        {
            return CityResolverProfile.CreateEmpty();
        }

        return new CityResolverProfile
        {
            PreferredSubtypes = [.. profile.PreferredSubtypes],
            TieBreakMode = profile.TieBreak switch
            {
                null => string.Empty,
                CoordinateLookupTieBreak.SmallestArea => CityResolverTieBreakModes.SmallestArea,
                CoordinateLookupTieBreak.LargestArea => CityResolverTieBreakModes.LargestArea,
                _ => throw new ArgumentOutOfRangeException(nameof(profile))
            }
        };
    }
}

public sealed record CoordinateLookupRequest : IWorkerJobRequest
{
    public double Latitude { get; }
    public double Longitude { get; }
    public bool IncludeAirportInfrastructure { get; }
    public bool IncludeLiveOverturePlaces { get; }
    public bool PreferGadmAdministrativeAreas { get; }
    public CoordinateLookupCityResolverOverrides CityResolverOverrides { get; }

    public CoordinateLookupRequest(
        double latitude,
        double longitude,
        bool includeAirportInfrastructure,
        bool includeLiveOverturePlaces,
        bool preferGadmAdministrativeAreas,
        CoordinateLookupCityResolverOverrides cityResolverOverrides)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude));
        }

        if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude));
        }

        ArgumentNullException.ThrowIfNull(cityResolverOverrides);
        Latitude = latitude;
        Longitude = longitude;
        IncludeAirportInfrastructure = includeAirportInfrastructure;
        IncludeLiveOverturePlaces = includeLiveOverturePlaces;
        PreferGadmAdministrativeAreas = preferGadmAdministrativeAreas;
        CityResolverOverrides = cityResolverOverrides;
    }
}

public enum CoordinateLookupCountryStatus
{
    Matched,
    NoMatch,
    MappingFailed,
    Failed
}

public enum CoordinateLookupSourceState
{
    Disabled,
    Skipped,
    Ready,
    NoMatch,
    Unavailable,
    Failed
}

public enum CoordinateLookupFinalSource
{
    BundledCountryDivisions,
    CachedOvertureDivisions,
    CachedGadmDivisions,
    BundledAirportGeometryMatch,
    BundledAirportFallback
}

public enum CoordinateLookupProgressStep
{
    Country,
    OvertureCache,
    OvertureAdministrative,
    GadmCache,
    GadmAdministrative,
    Airport,
    LivePlaces,
    FinalSelection
}

public sealed record CoordinateLookupProgressPayload : WorkerJobOutputPayload
{
    public CoordinateLookupProgressStep Step { get; }
    public CoordinateLookupSourceState State { get; }
    public string? CountryCode { get; }
    public string Message { get; }

    public CoordinateLookupProgressPayload(
        CoordinateLookupProgressStep step,
        CoordinateLookupSourceState state,
        string? countryCode,
        string message)
    {
        if (!Enum.IsDefined(step))
        {
            throw new ArgumentOutOfRangeException(nameof(step));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (countryCode is not null)
        {
            CoordinateLookupCountryProfile.RequireIso3(countryCode, nameof(countryCode));
        }

        WorkerJobProtocolV2.RequireSafeText(message, nameof(message));
        Step = step;
        State = state;
        CountryCode = countryCode;
        Message = message;
    }
}

public sealed record CoordinateLookupCountryResult
{
    public CoordinateLookupCountryStatus Status { get; }
    public string? Iso3 { get; }
    public string? Alpha2 { get; }
    public string? Name { get; }
    public string? SourceId { get; }
    public WorkerJobSafeError? Error { get; }

    public CoordinateLookupCountryResult(
        CoordinateLookupCountryStatus status,
        string? iso3,
        string? alpha2,
        string? name,
        string? sourceId,
        WorkerJobSafeError? error)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (iso3 is not null)
        {
            CoordinateLookupCountryProfile.RequireIso3(iso3, nameof(iso3));
        }

        if (alpha2 is not null
            && (alpha2.Length != 2 || alpha2.Any(static character => character is < 'A' or > 'Z')))
        {
            throw new ArgumentException("An alpha2 country code must be canonical upper case.", nameof(alpha2));
        }

        RequireOptionalText(name, nameof(name));
        RequireOptionalText(sourceId, nameof(sourceId), CoordinateLookupProtocolBounds.MaxIdentifierLength);
        if (status == CoordinateLookupCountryStatus.Matched
            && (iso3 is null || alpha2 is null || name is null || sourceId is null || error is not null))
        {
            throw new ArgumentException("A matched country requires complete identity and no error.");
        }

        if (status != CoordinateLookupCountryStatus.Matched
            && (iso3 is not null || alpha2 is not null || name is not null || sourceId is not null))
        {
            throw new ArgumentException("An unmatched country must not carry partial identity.");
        }

        Status = status;
        Iso3 = iso3;
        Alpha2 = alpha2;
        Name = name;
        SourceId = sourceId;
        Error = error;
    }

    internal static void RequireOptionalText(
        string? value,
        string parameterName,
        int maximumLength = CoordinateLookupProtocolBounds.MaxDisplayTextLength)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength))
        {
            throw new ArgumentException("Optional protocol text must be non-blank and bounded.", parameterName);
        }
    }
}

public sealed record CoordinateLookupCacheStatus
{
    public string CountryCode { get; }
    public CoordinateLookupSourceState State { get; }
    public WorkerJobSafeError? Error { get; }

    public CoordinateLookupCacheStatus(
        string countryCode,
        CoordinateLookupSourceState state,
        WorkerJobSafeError? error)
    {
        CoordinateLookupCountryProfile.RequireIso3(countryCode, nameof(countryCode));
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        CountryCode = countryCode;
        State = state;
        Error = error;
    }
}

public sealed record CoordinateLookupCandidate
{
    public string Id { get; }
    public string Name { get; }
    public bool Selected { get; }
    public string Decision { get; }
    public bool? BoundingBoxContainsCoordinate { get; }
    public bool? GeometryContainsCoordinate { get; }
    public string? FeatureType { get; }
    public string? Subtype { get; }
    public string? Category { get; }
    public string? BasicCategory { get; }
    public string? Classification { get; }
    public int? AdminLevel { get; }
    public bool? IsLand { get; }
    public bool? IsTerritorial { get; }
    public string? Country { get; }
    public string? EnglishType { get; }
    public string? LocalType { get; }
    public string? OperatingStatus { get; }
    public double? DistanceMeters { get; }
    public double? Confidence { get; }
    public double? BoundingBoxArea { get; }
    public IReadOnlyList<string> RecordSources { get; }
    public int OmittedRecordSourceCount { get; }
    public int TruncatedTextCount { get; }

    public CoordinateLookupCandidate(
        string id,
        string name,
        bool selected,
        string decision,
        bool? boundingBoxContainsCoordinate,
        bool? geometryContainsCoordinate,
        string? featureType,
        string? subtype,
        string? category,
        string? basicCategory,
        string? classification,
        int? adminLevel,
        bool? isLand,
        bool? isTerritorial,
        string? country,
        string? englishType,
        string? localType,
        string? operatingStatus,
        double? distanceMeters,
        double? confidence,
        double? boundingBoxArea,
        IReadOnlyList<string> recordSources,
        int omittedRecordSourceCount = 0,
        int truncatedTextCount = 0)
    {
        RequireText(id, CoordinateLookupProtocolBounds.MaxIdentifierLength, nameof(id));
        RequireText(name, CoordinateLookupProtocolBounds.MaxDisplayTextLength, nameof(name));
        RequireText(decision, CoordinateLookupProtocolBounds.MaxDisplayTextLength, nameof(decision));
        CoordinateLookupCountryResult.RequireOptionalText(featureType, nameof(featureType));
        CoordinateLookupCountryResult.RequireOptionalText(subtype, nameof(subtype));
        CoordinateLookupCountryResult.RequireOptionalText(category, nameof(category));
        CoordinateLookupCountryResult.RequireOptionalText(basicCategory, nameof(basicCategory));
        CoordinateLookupCountryResult.RequireOptionalText(classification, nameof(classification));
        CoordinateLookupCountryResult.RequireOptionalText(country, nameof(country));
        CoordinateLookupCountryResult.RequireOptionalText(englishType, nameof(englishType));
        CoordinateLookupCountryResult.RequireOptionalText(localType, nameof(localType));
        CoordinateLookupCountryResult.RequireOptionalText(operatingStatus, nameof(operatingStatus));
        if (adminLevel is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(adminLevel));
        }

        RequireFiniteNonNegative(distanceMeters, nameof(distanceMeters));
        RequireFiniteNonNegative(confidence, nameof(confidence));
        RequireFiniteNonNegative(boundingBoxArea, nameof(boundingBoxArea));
        ArgumentNullException.ThrowIfNull(recordSources);
        if (omittedRecordSourceCount < 0 || truncatedTextCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(omittedRecordSourceCount));
        }
        var rawSources = recordSources.ToArray();
        foreach (string source in rawSources)
        {
            RequireText(source, CoordinateLookupProtocolBounds.MaxIdentifierLength, nameof(recordSources));
        }

        var boundedSources = rawSources
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .Take(CoordinateLookupProtocolBounds.MaxRecordSourcesPerCandidate)
            .ToArray();
        Id = id;
        Name = name;
        Selected = selected;
        Decision = decision;
        BoundingBoxContainsCoordinate = boundingBoxContainsCoordinate;
        GeometryContainsCoordinate = geometryContainsCoordinate;
        FeatureType = featureType;
        Subtype = subtype;
        Category = category;
        BasicCategory = basicCategory;
        Classification = classification;
        AdminLevel = adminLevel;
        IsLand = isLand;
        IsTerritorial = isTerritorial;
        Country = country;
        EnglishType = englishType;
        LocalType = localType;
        OperatingStatus = operatingStatus;
        DistanceMeters = distanceMeters;
        Confidence = confidence;
        BoundingBoxArea = boundingBoxArea;
        RecordSources = Array.AsReadOnly(boundedSources);
        OmittedRecordSourceCount = checked(
            omittedRecordSourceCount
            + Math.Max(0, rawSources.Distinct(StringComparer.Ordinal).Count() - boundedSources.Length));
        TruncatedTextCount = truncatedTextCount;
    }

    internal static void RequireText(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException("Protocol text must be non-blank and bounded.", parameterName);
        }
    }

    private static void RequireFiniteNonNegative(double? value, string parameterName)
    {
        if (value is not null && (!double.IsFinite(value.Value) || value.Value < 0))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

public sealed record CoordinateLookupSourceResult
{
    public CoordinateLookupSourceState State { get; }
    public string? Release { get; }
    public string? Version { get; }
    public CoordinateLookupCandidate? BestMatch { get; }
    public IReadOnlyList<CoordinateLookupCandidate> Candidates { get; }
    public IReadOnlyList<CoordinateLookupCacheStatus> Caches { get; }
    public WorkerJobSafeError? Error { get; }
    public string? Attribution { get; }
    public string? LicenseUrl { get; }
    public string? UsageNotice { get; }
    public int OmittedCandidateCount { get; }
    public int OmittedCacheCount { get; }
    public int TruncatedTextCount { get; }

    public CoordinateLookupSourceResult(
        CoordinateLookupSourceState state,
        string? release,
        string? version,
        CoordinateLookupCandidate? bestMatch,
        IReadOnlyList<CoordinateLookupCandidate> candidates,
        IReadOnlyList<CoordinateLookupCacheStatus> caches,
        WorkerJobSafeError? error,
        string? attribution,
        string? licenseUrl,
        string? usageNotice,
        int omittedCandidateCount = 0,
        int omittedCacheCount = 0,
        int truncatedTextCount = 0)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        CoordinateLookupCountryResult.RequireOptionalText(release, nameof(release));
        CoordinateLookupCountryResult.RequireOptionalText(version, nameof(version));
        CoordinateLookupCountryResult.RequireOptionalText(attribution, nameof(attribution));
        CoordinateLookupCountryResult.RequireOptionalText(licenseUrl, nameof(licenseUrl));
        CoordinateLookupCountryResult.RequireOptionalText(usageNotice, nameof(usageNotice));
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(caches);
        if (omittedCandidateCount < 0 || omittedCacheCount < 0 || truncatedTextCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(omittedCandidateCount));
        }
        CoordinateLookupCandidate[] rawCandidates = candidates.ToArray();
        CoordinateLookupCacheStatus[] rawCaches = caches.ToArray();
        if (rawCandidates.Any(static candidate => candidate is null)
            || rawCaches.Any(static cache => cache is null))
        {
            throw new ArgumentException("Source diagnostics must not contain null values.");
        }

        CoordinateLookupCandidate[] boundedCandidates = rawCandidates
            .Take(CoordinateLookupProtocolBounds.MaxCandidatesPerSource)
            .ToArray();
        CoordinateLookupCacheStatus[] boundedCaches = rawCaches
            .Take(CoordinateLookupProtocolBounds.MaxCacheStatuses)
            .ToArray();
        State = state;
        Release = release;
        Version = version;
        BestMatch = bestMatch;
        Candidates = Array.AsReadOnly(boundedCandidates);
        Caches = Array.AsReadOnly(boundedCaches);
        Error = error;
        Attribution = attribution;
        LicenseUrl = licenseUrl;
        UsageNotice = usageNotice;
        OmittedCandidateCount = checked(
            omittedCandidateCount + rawCandidates.Length - boundedCandidates.Length);
        OmittedCacheCount = checked(
            omittedCacheCount + rawCaches.Length - boundedCaches.Length);
        TruncatedTextCount = truncatedTextCount;
    }
}

public sealed record CoordinateLookupAdministrativeResult
{
    public string? State { get; }
    public string? City { get; }

    public CoordinateLookupAdministrativeResult(string? state, string? city)
    {
        State = Require(state, nameof(state));
        City = Require(city, nameof(city));
    }

    private static string? Require(string? value, string parameterName)
    {
        CoordinateLookupCountryResult.RequireOptionalText(value, parameterName);
        return value;
    }
}

public sealed record CoordinateLookupProfileSummary
{
    public string? CountryCode { get; }
    public IReadOnlyList<string> PreferredSubtypes { get; }
    public CoordinateLookupTieBreak TieBreak { get; }

    public CoordinateLookupProfileSummary(
        string? countryCode,
        IReadOnlyList<string> preferredSubtypes,
        CoordinateLookupTieBreak tieBreak)
    {
        if (countryCode is not null)
        {
            CoordinateLookupCountryProfile.RequireIso3(countryCode, nameof(countryCode));
        }

        var profile = new CoordinateLookupCityProfile(preferredSubtypes, tieBreak);
        CountryCode = countryCode;
        PreferredSubtypes = profile.PreferredSubtypes;
        TieBreak = profile.TieBreak
            ?? throw new ArgumentException("A resolved profile summary requires a tie-break.", nameof(tieBreak));
    }
}

public sealed record CoordinateLookupAttributedValue
{
    public string Value { get; }
    public CoordinateLookupFinalSource Source { get; }

    public CoordinateLookupAttributedValue(string value, CoordinateLookupFinalSource source)
    {
        CoordinateLookupCandidate.RequireText(
            value,
            CoordinateLookupProtocolBounds.MaxDisplayTextLength,
            nameof(value));
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        Value = value;
        Source = source;
    }
}

public sealed record CoordinateLookupFinalLocation(
    CoordinateLookupAttributedValue? Country,
    CoordinateLookupAttributedValue? State,
    CoordinateLookupAttributedValue? City);

public sealed record CoordinateLookupResult : IWorkerJobResult
{
    public CoordinateLookupRequest Request { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset EndedAtUtc { get; }
    public CoordinateLookupCountryResult Country { get; }
    public CoordinateLookupSourceResult OvertureDivisions { get; }
    public CoordinateLookupSourceResult GadmDivisions { get; }
    public CoordinateLookupSourceResult AirportInfrastructure { get; }
    public CoordinateLookupSourceResult LiveOverturePlaces { get; }
    public CoordinateLookupAdministrativeResult OvertureAdministrative { get; }
    public CoordinateLookupAdministrativeResult GadmAdministrative { get; }
    public CoordinateLookupProfileSummary Profile { get; }
    public IReadOnlyList<string> Trace { get; }
    public int OmittedTraceCount { get; }
    public int TruncatedTextCount { get; }
    public CoordinateLookupFinalLocation FinalLocation { get; }

    public CoordinateLookupResult(
        CoordinateLookupRequest request,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        CoordinateLookupCountryResult country,
        CoordinateLookupSourceResult overtureDivisions,
        CoordinateLookupSourceResult gadmDivisions,
        CoordinateLookupSourceResult airportInfrastructure,
        CoordinateLookupSourceResult liveOverturePlaces,
        CoordinateLookupAdministrativeResult overtureAdministrative,
        CoordinateLookupAdministrativeResult gadmAdministrative,
        CoordinateLookupProfileSummary profile,
        IReadOnlyList<string> trace,
        CoordinateLookupFinalLocation finalLocation,
        int omittedTraceCount = 0,
        int truncatedTextCount = 0)
    {
        ArgumentNullException.ThrowIfNull(request);
        WorkerJobProtocolV2.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        WorkerJobProtocolV2.RequireUtc(endedAtUtc, nameof(endedAtUtc));
        if (endedAtUtc < startedAtUtc)
        {
            throw new ArgumentException("The terminal timestamp must not precede the start timestamp.", nameof(endedAtUtc));
        }

        ArgumentNullException.ThrowIfNull(country);
        ArgumentNullException.ThrowIfNull(overtureDivisions);
        ArgumentNullException.ThrowIfNull(gadmDivisions);
        ArgumentNullException.ThrowIfNull(airportInfrastructure);
        ArgumentNullException.ThrowIfNull(liveOverturePlaces);
        ArgumentNullException.ThrowIfNull(overtureAdministrative);
        ArgumentNullException.ThrowIfNull(gadmAdministrative);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(finalLocation);
        if (omittedTraceCount < 0 || truncatedTextCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(omittedTraceCount));
        }
        string[] rawTrace = trace.ToArray();
        foreach (string entry in rawTrace)
        {
            CoordinateLookupCandidate.RequireText(
                entry,
                CoordinateLookupProtocolBounds.MaxDisplayTextLength,
                nameof(trace));
        }

        string[] boundedTrace = rawTrace
            .Take(CoordinateLookupProtocolBounds.MaxTraceEntries)
            .ToArray();
        Request = request;
        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
        Country = country;
        OvertureDivisions = overtureDivisions;
        GadmDivisions = gadmDivisions;
        AirportInfrastructure = airportInfrastructure;
        LiveOverturePlaces = liveOverturePlaces;
        OvertureAdministrative = overtureAdministrative;
        GadmAdministrative = gadmAdministrative;
        Profile = profile;
        Trace = Array.AsReadOnly(boundedTrace);
        OmittedTraceCount = checked(
            omittedTraceCount + rawTrace.Length - boundedTrace.Length);
        TruncatedTextCount = truncatedTextCount;
        FinalLocation = finalLocation;
    }
}
