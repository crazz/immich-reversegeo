namespace ImmichReverseGeo.Overture.Models;

public sealed record CountryResolutionFixture(
    string Label,
    double Latitude,
    double Longitude,
    string DisplayName,
    string Alpha3,
    string Alpha2,
    string? AdministeringSovereignAlpha2 = null);
