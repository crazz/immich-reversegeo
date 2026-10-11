using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Models;

namespace ImmichReverseGeo.Overture.Services;

public static class OvertureDivisionsLogic
{
    private static readonly string[] StateSubtypes = ["region", "macroregion", "county", "macrocounty", "dependency"];

    public const string DivisionAreaReleaseUrlTemplate = "az://overturemapswestus2.blob.core.windows.net/release/{0}/theme=divisions/type=division_area/*.parquet";
    public const int QueryLimit = 200;

    public static string BuildDivisionAreaQuery(double lat, double lon, string? alpha2, string releaseUrl)
    {
        var countryClause = string.IsNullOrWhiteSpace(alpha2)
            ? string.Empty
            : $"  AND {OvertureCountrySourceCodes.BuildCountryFilter(alpha2)}\n";

        return $"""
            SELECT
                id,
                COALESCE(names.common['en'], names.primary, id) AS name,
                subtype,
                class,
                admin_level,
                country,
                COALESCE(is_land, true) AS is_land,
                COALESCE(is_territorial, false) AS is_territorial,
                bbox.xmax >= {lon.ToString(CultureInfo.InvariantCulture)}
                    AND bbox.xmin <= {lon.ToString(CultureInfo.InvariantCulture)}
                    AND bbox.ymax >= {lat.ToString(CultureInfo.InvariantCulture)}
                    AND bbox.ymin <= {lat.ToString(CultureInfo.InvariantCulture)} AS bbox_contains_point,
                ST_Intersects(geometry, ST_Point({lon.ToString(CultureInfo.InvariantCulture)}, {lat.ToString(CultureInfo.InvariantCulture)})) AS geometry_contains_point,
                ABS((bbox.xmax - bbox.xmin) * (bbox.ymax - bbox.ymin)) AS bbox_area,
                division_id,
                hierarchy_json
            FROM {BuildDivisionAreaSource(releaseUrl, alpha2)}
            WHERE names.primary IS NOT NULL
              AND bbox.xmax >= {lon.ToString(CultureInfo.InvariantCulture)}
              AND bbox.xmin <= {lon.ToString(CultureInfo.InvariantCulture)}
              AND bbox.ymax >= {lat.ToString(CultureInfo.InvariantCulture)}
              AND bbox.ymin <= {lat.ToString(CultureInfo.InvariantCulture)}
            {countryClause}LIMIT {QueryLimit}
            """;
    }

    // Keep area containment and its default parent hierarchy in the same release.
    public static string BuildDivisionAreaSource(string releaseUrl, string? alpha2)
    {
        var divisionUrl = releaseUrl.Replace("type=division_area/", "type=division/", StringComparison.Ordinal);
        var countryFilter = string.IsNullOrWhiteSpace(alpha2)
            ? string.Empty
            : $"WHERE {OvertureCountrySourceCodes.BuildCountryFilter(alpha2)}";
        return $"""
            (
                SELECT a.*, to_json(d.hierarchies[1]) AS hierarchy_json
                FROM (SELECT * FROM read_parquet('{releaseUrl}', hive_partitioning = 1) {countryFilter}) a
                LEFT JOIN (SELECT id, hierarchies FROM read_parquet('{divisionUrl}', hive_partitioning = 1) {countryFilter}) d
                    ON a.division_id = d.id
            )
            """;
    }

    public static bool ShouldPreferDivisionCandidate(
        OvertureDivisionResult candidate,
        OvertureDivisionResult currentBest)
    {
        return CompareDivisionCandidates(candidate, currentBest, preferDependency: false) < 0;
    }

    public static bool ShouldPreferBundledCountryCandidate(
        OvertureDivisionResult candidate,
        OvertureDivisionResult currentBest)
    {
        return CompareDivisionCandidates(candidate, currentBest, preferDependency: true) < 0;
    }

    private static int CompareDivisionCandidates(
        OvertureDivisionResult candidate,
        OvertureDivisionResult currentBest,
        bool preferDependency)
    {
        var comparison = ComparePreferredBoolean(
            candidate.ExactGeometryContainsPoint,
            currentBest.ExactGeometryContainsPoint);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = ComparePreferredBoolean(
            candidate.GeometryContainsPoint,
            currentBest.GeometryContainsPoint);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = ComparePreferredBoolean(
            candidate.BoundingBoxContainsPoint,
            currentBest.BoundingBoxContainsPoint);
        if (comparison != 0)
        {
            return comparison;
        }

        if (preferDependency)
        {
            comparison = GetDependencyRank(candidate.SubType).CompareTo(GetDependencyRank(currentBest.SubType));
            if (comparison != 0)
            {
                return comparison;
            }
        }

        comparison = GetSubtypeRank(candidate.SubType).CompareTo(GetSubtypeRank(currentBest.SubType));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = (candidate.AdminLevel ?? int.MaxValue).CompareTo(currentBest.AdminLevel ?? int.MaxValue);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = ComparePreferredBoolean(candidate.IsTerritorial, currentBest.IsTerritorial);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = candidate.BoundingBoxArea.CompareTo(currentBest.BoundingBoxArea);
        if (comparison != 0)
        {
            return comparison;
        }

        return string.Compare(candidate.Id, currentBest.Id, StringComparison.Ordinal);
    }

    private static int ComparePreferredBoolean(bool candidate, bool currentBest)
    {
        return currentBest.CompareTo(candidate);
    }

    private static int GetDependencyRank(string? subtype)
    {
        return string.Equals(subtype, "dependency", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    public static int GetSubtypeRank(string? subtype) =>
        subtype?.ToLowerInvariant() switch
        {
            "microhood" => 0,
            "neighborhood" => 1,
            "macrohood" => 2,
            "borough" => 3,
            "locality" => 4,
            "localadmin" => 5,
            "county" => 6,
            "macrocounty" => 7,
            "region" => 8,
            "macroregion" => 9,
            "dependency" => 10,
            "country" => 11,
            _ => 50
        };

    public static string? SelectStateName(IEnumerable<OvertureDivisionCandidateDiagnostic> candidates)
    {
        return SelectPreferredCandidate(candidates, StateSubtypes)?.Name;
    }

    public static string? SelectCityName(
        IEnumerable<OvertureDivisionCandidateDiagnostic> candidates,
        CityResolverProfile? resolverProfile = null) => SelectCityCandidate(candidates.ToList(), resolverProfile)?.Name;

    public static OvertureAdministrativeResult SelectAdministrativeAreas(
        IEnumerable<OvertureDivisionCandidateDiagnostic> candidates,
        CityResolverProfile? resolverProfile = null)
    {
        var areas = candidates.ToList();
        var city = SelectCityCandidate(areas, resolverProfile);
        var hierarchy = city is { GeometryContainsPoint: true } ? ParseHierarchy(city) : [];
        foreach (var subtype in StateSubtypes)
        {
            // The city itself is not its parent state, including county-like city profiles.
            var ancestor = hierarchy.FirstOrDefault(h => h.SubType.Equals(subtype, StringComparison.OrdinalIgnoreCase)
                && h.DivisionId != city?.DivisionId);
            if (ancestor is not null)
            {
                // Prefer the existing localized polygon name when the matching parent is present.
                var parent = areas.FirstOrDefault(c => c.DivisionId == ancestor.DivisionId
                    && c.GeometryContainsPoint && c.Country == city?.Country);
                return new OvertureAdministrativeResult(parent?.Name ?? ancestor.Name, city?.Name);
            }
        }

        return new OvertureAdministrativeResult(hierarchy.Count > 0 ? null : SelectStateName(areas), city?.Name);
    }

    public static IReadOnlyList<OvertureDivisionAncestor> ParseHierarchy(OvertureDivisionCandidateDiagnostic candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.DivisionId) || string.IsNullOrWhiteSpace(candidate.HierarchyJson))
        {
            return [];
        }

        try
        {
            using var json = JsonDocument.Parse(candidate.HierarchyJson);
            var ancestors = new List<OvertureDivisionAncestor>();
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                var id = entry.GetProperty("division_id").GetString();
                var name = entry.GetProperty("name").GetString();
                var subtype = entry.GetProperty("subtype").GetString();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(subtype)
                    || ancestors.Any(h => h.DivisionId == id))
                {
                    return [];
                }

                ancestors.Add(new OvertureDivisionAncestor(id, name, subtype));
            }

            // Only accept a chain ending at this area owner; never borrow an unrelated hierarchy.
            return ancestors.Count > 0
                && ancestors[0].SubType.Equals("country", StringComparison.OrdinalIgnoreCase)
                && ancestors[^1].DivisionId == candidate.DivisionId ? ancestors : [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return [];
        }
    }

    private static OvertureDivisionCandidateDiagnostic? SelectCityCandidate(
        List<OvertureDivisionCandidateDiagnostic> candidates,
        CityResolverProfile? resolverProfile)
    {
        var profile = (resolverProfile ?? new CityResolverProfile()).Clone().Normalize();
        var selected = SelectPreferredCandidate(candidates, profile.PreferredSubtypes, profile.TieBreakMode);
        var selectedRank = selected is not { GeometryContainsPoint: true }
            ? int.MaxValue
            : GetPreferredSubtypeOrder(selected.SubType, profile.PreferredSubtypes);
        if (selectedRank == 0)
        {
            return selected;
        }

        var promoted = new List<OvertureDivisionCandidateDiagnostic>();
        foreach (var candidate in candidates.Where(c => c.GeometryContainsPoint && c.BoundingBoxContainsPoint))
        {
            var hierarchy = ParseHierarchy(candidate);
            for (var i = 0; i < hierarchy.Count - 1; i++)
            {
                var ancestor = hierarchy[i];
                if (GetPreferredSubtypeOrder(ancestor.SubType, profile.PreferredSubtypes) >= selectedRank)
                {
                    continue;
                }

                promoted.Add(candidate with
                {
                    Name = ancestor.Name,
                    SubType = ancestor.SubType,
                    AdminLevel = null,
                    DivisionId = ancestor.DivisionId,
                    HierarchyJson = JsonSerializer.Serialize(hierarchy.Take(i + 1).Select(h => new
                    {
                        division_id = h.DivisionId,
                        name = h.Name,
                        subtype = h.SubType
                    }))
                });
            }
        }

        return SelectPreferredCandidate(promoted, profile.PreferredSubtypes, profile.TieBreakMode) ?? selected;
    }

    private static OvertureDivisionCandidateDiagnostic? SelectPreferredCandidate(
        IEnumerable<OvertureDivisionCandidateDiagnostic> candidates,
        IReadOnlyList<string> preferredSubtypes,
        string tieBreakMode = CityResolverTieBreakModes.SmallestArea)
    {
        var applicable = candidates
            .Where(c => c.BoundingBoxContainsPoint)
            .Where(c => preferredSubtypes.Contains(c.SubType ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (applicable.Count == 0)
        {
            return null;
        }

        var geometries = applicable.Where(c => c.GeometryContainsPoint).ToList();
        var pool = geometries.Count > 0 ? geometries : applicable;

        var ordered = pool
            .OrderBy(c => GetPreferredSubtypeOrder(c.SubType, preferredSubtypes))
            .ThenBy(c => c.AdminLevel ?? int.MaxValue)
            .ThenByDescending(c => c.IsTerritorial);

        ordered = string.Equals(tieBreakMode, CityResolverTieBreakModes.LargestArea, StringComparison.OrdinalIgnoreCase)
            ? ordered.ThenByDescending(c => c.BoundingBoxArea)
            : ordered.ThenBy(c => c.BoundingBoxArea);

        return ordered.FirstOrDefault();
    }

    private static int GetPreferredSubtypeOrder(string? subtype, IReadOnlyList<string> preferredSubtypes)
    {
        for (var i = 0; i < preferredSubtypes.Count; i++)
        {
            if (string.Equals(preferredSubtypes[i], subtype, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
