using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Services;

public class OvertureDivisionsService
{
    private static readonly WKBReader WkbReader = new();
    private static readonly GeometryFactory GeometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    private readonly ILogger<OvertureDivisionsService> _logger;
    private readonly OverturePlacesService _overturePlacesService;
    private readonly string _dataDir;
    private readonly string _bundledDataDir;
    private readonly Func<string, string?> _alpha2ToIso3;
    private readonly object _bundledCountryCacheLock = new();
    private STRtree<BundledCountryArea>? _bundledCountryIndex;

    public OvertureDivisionsService(
        ILogger<OvertureDivisionsService> logger,
        OverturePlacesService overturePlacesService,
        string dataDir,
        string bundledDataDir,
        Func<string, string?> alpha2ToIso3)
    {
        _logger = logger;
        _overturePlacesService = overturePlacesService;
        _dataDir = dataDir;
        _bundledDataDir = bundledDataDir;
        _alpha2ToIso3 = alpha2ToIso3;
    }

    public Task<BundledCountryLookupResult> FindBundledCountryAsync(
        double lat,
        double lon,
        CancellationToken ct = default)
    {
        var bundledPath = Path.Combine(_bundledDataDir, "defaults", "overture-country-divisions.db");
        if (!File.Exists(bundledPath))
        {
            return Task.FromResult(BundledCountryLookupResult.SpatialNoMatch(
                "Bundled Overture country artifact was not found."));
        }

        var best = FindBundledCountryFromMemory(bundledPath, lat, lon);
        if (best is null)
        {
            return Task.FromResult(BundledCountryLookupResult.SpatialNoMatch(
                "Bundled Overture spatial coverage found no match."));
        }

        if (string.IsNullOrWhiteSpace(best.Country))
        {
            return Task.FromResult(BundledCountryLookupResult.IdentityMappingFailure(
                best.Name,
                null,
                best.Id,
                "Matched bundled geometry has no Alpha-2 identity."));
        }

        var mappedAlpha3 = _alpha2ToIso3(best.Country);
        if (string.IsNullOrWhiteSpace(best.Alpha3)
            || string.IsNullOrWhiteSpace(mappedAlpha3)
            || !string.Equals(best.Alpha3, mappedAlpha3, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(BundledCountryLookupResult.IdentityMappingFailure(
                best.Name,
                best.Country,
                best.Id,
                $"Matched bundled identity '{best.Country}' has missing or inconsistent Alpha-3 mapping."));
        }

        return Task.FromResult(BundledCountryLookupResult.Matched(
            best.Alpha3,
            best.Name,
            best.Country,
            best.Id));
    }

    private OvertureDivisionResult? FindBundledCountryFromMemory(string bundledPath, double lat, double lon)
    {
        var countryIndex = GetBundledCountryIndex(bundledPath);
        var point = GeometryFactory.CreatePoint(new Coordinate(lon, lat));
        var queryEnvelope = new Envelope(point.EnvelopeInternal);
        queryEnvelope.ExpandBy(0.00015);
        OvertureDivisionResult? bestUsable = null;
        OvertureDivisionResult? bestUnusable = null;

        foreach (var country in countryIndex.Query(queryEnvelope))
        {
            var boundingBoxContains = lon >= country.BoundingBoxXMin
                                      && lon <= country.BoundingBoxXMax
                                      && lat >= country.BoundingBoxYMin
                                      && lat <= country.BoundingBoxYMax;
            var exactGeometryContains = country.PreparedGeometry.Covers(point);
            var geometryContains = exactGeometryContains || country.Geometry.Distance(point) <= 0.00015;
            var candidate = new OvertureDivisionResult(
                country.Id,
                country.Name,
                country.SubType,
                country.ClassName,
                country.AdminLevel,
                country.Country,
                country.Alpha3,
                country.IsLand,
                country.IsTerritorial,
                boundingBoxContains,
                exactGeometryContains,
                geometryContains,
                country.BoundingBoxArea);

            if (!candidate.GeometryContainsPoint)
            {
                continue;
            }

            var mappedAlpha3 = string.IsNullOrWhiteSpace(candidate.Country)
                ? null
                : _alpha2ToIso3(candidate.Country);
            var hasUsableIdentity = !string.IsNullOrWhiteSpace(candidate.Alpha3)
                                    && !string.IsNullOrWhiteSpace(mappedAlpha3)
                                    && string.Equals(candidate.Alpha3, mappedAlpha3, StringComparison.OrdinalIgnoreCase);
            if (hasUsableIdentity)
            {
                if (bestUsable is null || OvertureDivisionsLogic.ShouldPreferBundledCountryCandidate(candidate, bestUsable))
                {
                    bestUsable = candidate;
                }
            }
            else if (bestUnusable is null || OvertureDivisionsLogic.ShouldPreferBundledCountryCandidate(candidate, bestUnusable))
            {
                bestUnusable = candidate;
            }
        }

        return bestUsable ?? bestUnusable;
    }

    private STRtree<BundledCountryArea> GetBundledCountryIndex(string bundledPath)
    {
        if (_bundledCountryIndex is not null)
        {
            return _bundledCountryIndex;
        }

        lock (_bundledCountryCacheLock)
        {
            if (_bundledCountryIndex is not null)
            {
                return _bundledCountryIndex;
            }

            _bundledCountryIndex = LoadBundledCountryIndex(bundledPath);
            return _bundledCountryIndex;
        }
    }

    private static STRtree<BundledCountryArea> LoadBundledCountryIndex(string bundledPath)
    {
        var index = new STRtree<BundledCountryArea>();

        using var conn = new SqliteConnection($"Data Source={bundledPath};Pooling=false");
        conn.Open();
        var adminLevelColumn = HasColumn(conn, "division_area", "admin_level") ? "admin_level" : "NULL AS admin_level";
        var alpha3Column = HasColumn(conn, "division_area", "alpha3") ? "alpha3" : "NULL AS alpha3";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                id,
                name,
                subtype,
                class_name,
                {adminLevelColumn},
                country,
                {alpha3Column},
                is_land,
                is_territorial,
                geom_wkb,
                bbox_xmin,
                bbox_ymin,
                bbox_xmax,
                bbox_ymax
            FROM division_area
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(9))
            {
                continue;
            }

            var geometry = WkbReader.Read(OvertureDataAccess.ReadBlobValue(reader.GetValue(9)));
            var preparedGeometry = PreparedGeometryFactory.Prepare(geometry);
            var xmin = reader.GetDouble(10);
            var ymin = reader.GetDouble(11);
            var xmax = reader.GetDouble(12);
            var ymax = reader.GetDouble(13);

            var country = new BundledCountryArea(
                reader.GetString(0),
                reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                OvertureDataAccess.ReadNullableString(reader, 5),
                OvertureDataAccess.ReadNullableString(reader, 6),
                OvertureDataAccess.ReadSqliteBool(reader, 7),
                OvertureDataAccess.ReadSqliteBool(reader, 8),
                geometry,
                preparedGeometry,
                xmin,
                ymin,
                xmax,
                ymax,
                Math.Abs((xmax - xmin) * (ymax - ymin)));
            index.Insert(new Envelope(xmin, xmax, ymin, ymax), country);
        }

        index.Build();
        return index;
    }

    public async Task<OvertureAdministrativeResult?> ResolveAdministrativeGeoAsync(
        double lat,
        double lon,
        string? alpha2,
        string? iso3,
        CityResolverProfile? cityResolverProfile = null,
        CancellationToken ct = default)
    {
        var diagnostics = await FindContainingDivisionAreasAsync(lat, lon, alpha2, iso3, ct);
        if (diagnostics.Error is not null || diagnostics.Candidates.Count == 0)
        {
            return null;
        }

        var state = OvertureDivisionsLogic.SelectStateName(diagnostics.Candidates);
        var city = OvertureDivisionsLogic.SelectCityName(diagnostics.Candidates, cityResolverProfile);
        return new OvertureAdministrativeResult(state, city);
    }

    public async Task<OvertureDivisionLookupDiagnostics> FindContainingDivisionAreasAsync(
        double lat,
        double lon,
        string? alpha2,
        string? iso3 = null,
        CancellationToken ct = default)
    {
        try
        {
            var cached = QueryCachedDivisionAreas(lat, lon, iso3);
            if (cached is not null)
            {
                return cached;
            }

            var release = await _overturePlacesService.GetLatestReleaseForOvertureAsync(ct);
            var result = await Task.Run(() => QueryDivisionAreas(lat, lon, alpha2, release), ct);
            return result with { Release = release };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Overture division lookup failed at ({Lat:F4}, {Lon:F4}): {Message}",
                lat,
                lon,
                ex.Message);
            return new OvertureDivisionLookupDiagnostics(null, [], null, ex.Message);
        }
    }

    private OvertureDivisionLookupDiagnostics? QueryCachedDivisionAreas(double lat, double lon, string? iso3)
    {
        if (string.IsNullOrWhiteSpace(iso3))
        {
            return null;
        }

        var dbPath = Path.Combine(_dataDir, "overture-divisions", $"{iso3}.db");
        if (!File.Exists(dbPath))
        {
            return null;
        }

        return QueryDivisionAreasFromSqlite(dbPath, lat, lon, "cached");
    }

    private static OvertureDivisionLookupDiagnostics QueryDivisionAreasFromSqlite(
        string dbPath,
        double lat,
        double lon,
        string selectionLabel)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();
        var adminLevelColumn = HasColumn(conn, "division_area", "admin_level") ? "admin_level" : "NULL AS admin_level";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                id,
                name,
                subtype,
                class_name,
                {adminLevelColumn},
                country,
                is_land,
                is_territorial,
                geom_wkb,
                bbox_xmin,
                bbox_ymin,
                bbox_xmax,
                bbox_ymax
            FROM division_area
            WHERE bbox_xmax >= $lon
              AND bbox_xmin <= $lon
              AND bbox_ymax >= $lat
              AND bbox_ymin <= $lat
            """;
        cmd.Parameters.AddWithValue("$lon", lon);
        cmd.Parameters.AddWithValue("$lat", lat);

        using var reader = cmd.ExecuteReader();
        var point = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326).CreatePoint(new Coordinate(lon, lat));
        var candidates = new List<OvertureDivisionCandidateDiagnostic>();
        OvertureDivisionResult? best = null;

        while (reader.Read())
        {
            var bboxContains = !reader.IsDBNull(9)
                               && !reader.IsDBNull(10)
                               && !reader.IsDBNull(11)
                               && !reader.IsDBNull(12)
                               && lon >= reader.GetDouble(9)
                               && lon <= reader.GetDouble(11)
                               && lat >= reader.GetDouble(10)
                               && lat <= reader.GetDouble(12);
            var geometryContains = bboxContains
                                   && !reader.IsDBNull(8)
                                   && OvertureDataAccess.TryGeometryContains(OvertureDataAccess.ReadBlobValue(reader.GetValue(8)), point);
            var bboxArea = bboxContains
                ? Math.Abs((reader.GetDouble(11) - reader.GetDouble(9)) * (reader.GetDouble(12) - reader.GetDouble(10)))
                : double.MaxValue;

            var candidate = new OvertureDivisionResult(
                reader.GetString(0),
                reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                OvertureDataAccess.ReadNullableString(reader, 5),
                null,
                OvertureDataAccess.ReadSqliteBool(reader, 6),
                OvertureDataAccess.ReadSqliteBool(reader, 7),
                bboxContains,
                geometryContains,
                geometryContains,
                bboxArea);

            var selected = false;
            var decision = $"considered: weaker than current {selectionLabel} best";
            if (best is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, best))
            {
                decision = best is null
                    ? $"selected: first {selectionLabel} division area"
                    : candidate.GeometryContainsPoint && !best.GeometryContainsPoint
                        ? $"selected: {selectionLabel} geometry containment outranked previous best"
                        : OvertureDivisionsLogic.GetSubtypeRank(candidate.SubType) < OvertureDivisionsLogic.GetSubtypeRank(best.SubType)
                            ? $"selected: {selectionLabel} subtype specificity outranked previous best"
                            : candidate.AdminLevel.HasValue && best.AdminLevel.HasValue && candidate.AdminLevel.Value < best.AdminLevel.Value
                                ? $"selected: {selectionLabel} lower admin level outranked previous best"
                            : candidate.IsTerritorial && !best.IsTerritorial
                                ? $"selected: {selectionLabel} territorial area outranked previous best"
                                : $"selected: {selectionLabel} tighter bounding area outranked previous best";
                best = candidate;
                selected = true;
            }

            candidates.Add(new OvertureDivisionCandidateDiagnostic(
                candidate.Id,
                candidate.Name,
                candidate.SubType,
                candidate.ClassName,
                candidate.AdminLevel,
                candidate.Country,
                candidate.IsLand,
                candidate.IsTerritorial,
                candidate.BoundingBoxContainsPoint,
                candidate.GeometryContainsPoint,
                candidate.BoundingBoxArea,
                selected,
                decision));
        }

        using var meta = conn.CreateCommand();
        meta.CommandText = "SELECT value FROM _meta WHERE key='release'";
        var release = meta.ExecuteScalar()?.ToString();
        return new OvertureDivisionLookupDiagnostics(best, candidates, release);
    }

    private static OvertureDivisionLookupDiagnostics QueryDivisionAreas(
        double lat,
        double lon,
        string? alpha2,
        string release)
    {
        var releaseUrl = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            OvertureDivisionsLogic.DivisionAreaReleaseUrlTemplate,
            release);

        using var conn = new DuckDBConnection("Data Source=:memory:");
        conn.Open();
        OvertureDataAccess.LoadAzureAndSpatial(conn);

        using var query = conn.CreateCommand();
        query.CommandText = OvertureDivisionsLogic.BuildDivisionAreaQuery(lat, lon, alpha2, releaseUrl);

        var candidates = new List<OvertureDivisionCandidateDiagnostic>();
        OvertureDivisionResult? best = null;

        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            var candidate = new OvertureDivisionResult(
                reader.GetString(0),
                reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                OvertureDataAccess.ReadNullableString(reader, 5),
                null,
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9),
                reader.GetBoolean(9),
                reader.GetDouble(10));

            var selected = false;
            var decision = "considered: weaker than current best";
            if (best is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, best))
            {
                decision = best is null
                    ? "selected: first containing division area"
                    : candidate.GeometryContainsPoint && !best.GeometryContainsPoint
                        ? "selected: geometry containment outranked previous best"
                    : OvertureDivisionsLogic.GetSubtypeRank(candidate.SubType) < OvertureDivisionsLogic.GetSubtypeRank(best.SubType)
                        ? "selected: more specific division subtype outranked previous best"
                    : candidate.AdminLevel.HasValue && best.AdminLevel.HasValue && candidate.AdminLevel.Value < best.AdminLevel.Value
                        ? "selected: lower admin level outranked previous best"
                    : candidate.IsTerritorial && !best.IsTerritorial
                        ? "selected: territorial area outranked previous best"
                        : "selected: tighter bounding area outranked previous best";
                best = candidate;
                selected = true;
            }

            candidates.Add(new OvertureDivisionCandidateDiagnostic(
                candidate.Id,
                candidate.Name,
                candidate.SubType,
                candidate.ClassName,
                candidate.AdminLevel,
                candidate.Country,
                candidate.IsLand,
                candidate.IsTerritorial,
                candidate.BoundingBoxContainsPoint,
                candidate.GeometryContainsPoint,
                candidate.BoundingBoxArea,
                selected,
                decision));
        }

        return new OvertureDivisionLookupDiagnostics(best, candidates, release);
    }

    private sealed record BundledCountryArea(
        string Id,
        string Name,
        string? SubType,
        string? ClassName,
        int? AdminLevel,
        string? Country,
        string? Alpha3,
        bool IsLand,
        bool IsTerritorial,
        Geometry Geometry,
        IPreparedGeometry PreparedGeometry,
        double BoundingBoxXMin,
        double BoundingBoxYMin,
        double BoundingBoxXMax,
        double BoundingBoxYMax,
        double BoundingBoxArea);

    private static bool HasColumn(SqliteConnection conn, string tableName, string columnName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
