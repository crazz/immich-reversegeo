using System;
using System.Collections.Generic;
using System.Linq;

namespace ImmichReverseGeo.Overture.Services;

public static class OvertureCountrySourceCodes
{
    public static IReadOnlyList<string> GetCodes(string alpha2)
    {
        if (alpha2 is null || alpha2.Length != 2 || !alpha2.All(char.IsAsciiLetter))
        {
            throw new ArgumentException("Expected a two-letter country code.", nameof(alpha2));
        }

        return alpha2.ToUpperInvariant() switch
        {
            "BQ" => ["BQ", "XE", "XS"],
            "FR" => ["FR", "CP"],
            "SJ" => ["SJ", "XJ"],
            var code => [code]
        };
    }

    public static string BuildCountryFilter(string alpha2)
    {
        var codes = GetCodes(alpha2);
        return $"lower(country) IN ({string.Join(", ", codes.Select(code => $"'{code.ToLowerInvariant()}'"))})";
    }

    public static string GetCoverageSignature(string alpha2) => string.Join(",", GetCodes(alpha2));
}