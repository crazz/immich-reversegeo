using System;
using System.Collections.Generic;
using System.Linq;

namespace ImmichReverseGeo.Core.Models;

public enum CityResolverTieBreakPreference
{
    Inherit,
    Tighter,
    Broader
}

public sealed class CityResolverProfileOverride
{
    public List<string> PreferredSubtypes { get; set; } = [];
    public CityResolverTieBreakPreference TieBreak { get; set; }

    public static CityResolverProfileOverride FromProfile(CityResolverProfile profile)
    {
        return new CityResolverProfileOverride
        {
            PreferredSubtypes = profile.PreferredSubtypes
                .Where(subtype => !string.IsNullOrWhiteSpace(subtype))
                .Select(subtype => subtype.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            TieBreak = string.IsNullOrWhiteSpace(profile.TieBreakMode)
                ? CityResolverTieBreakPreference.Inherit
                : string.Equals(profile.TieBreakMode, CityResolverTieBreakModes.LargestArea, StringComparison.OrdinalIgnoreCase)
                    ? CityResolverTieBreakPreference.Broader
                    : CityResolverTieBreakPreference.Tighter
        };
    }

    public CityResolverProfile ToProfile()
    {
        return new CityResolverProfile
        {
            PreferredSubtypes = [.. PreferredSubtypes],
            TieBreakMode = TieBreak switch
            {
                CityResolverTieBreakPreference.Inherit => string.Empty,
                CityResolverTieBreakPreference.Tighter => CityResolverTieBreakModes.SmallestArea,
                CityResolverTieBreakPreference.Broader => CityResolverTieBreakModes.LargestArea,
                _ => throw new ArgumentOutOfRangeException(nameof(TieBreak))
            }
        };
    }
}
