namespace ImmichReverseGeo.Overture.Models;

public enum BundledCountryLookupStatus
{
    Matched,
    SpatialNoMatch,
    IdentityMappingFailure
}

public sealed record BundledCountryLookupResult(
    BundledCountryLookupStatus Status,
    string? Iso3 = null,
    string? CountryName = null,
    string? Alpha2 = null,
    string? SourceId = null,
    string? FailureReason = null)
{
    public bool IsMatched => Status == BundledCountryLookupStatus.Matched;

    public static BundledCountryLookupResult Matched(
        string iso3,
        string countryName,
        string alpha2,
        string sourceId)
    {
        return new BundledCountryLookupResult(
            BundledCountryLookupStatus.Matched,
            iso3,
            countryName,
            alpha2,
            sourceId);
    }

    public static BundledCountryLookupResult SpatialNoMatch(string? reason = null)
    {
        return new BundledCountryLookupResult(
            BundledCountryLookupStatus.SpatialNoMatch,
            FailureReason: reason);
    }

    public static BundledCountryLookupResult IdentityMappingFailure(
        string? countryName,
        string? alpha2,
        string sourceId,
        string reason)
    {
        return new BundledCountryLookupResult(
            BundledCountryLookupStatus.IdentityMappingFailure,
            CountryName: countryName,
            Alpha2: alpha2,
            SourceId: sourceId,
            FailureReason: reason);
    }
}
