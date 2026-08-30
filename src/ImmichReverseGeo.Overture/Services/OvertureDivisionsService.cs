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
    private readonly Func<double, double, string?, CancellationToken, OvertureDivisionLookupDiagnostics?> _cachedDivisionQuery;
    private readonly Func<double, double, string?, string, CancellationToken, OvertureDivisionLookupDiagnostics> _divisionQuery;
    private readonly Func<byte[], Point, bool> _geometryContains;
    private readonly Func<SqliteConnection, string, string, CancellationToken, bool> _hasColumn;
    private Action<OvertureHasColumnCheckpoint>? _hasColumnCheckpoint;
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
        _geometryContains = OvertureDataAccess.TryGeometryContains;
        _hasColumn = HasColumn;
        _cachedDivisionQuery = QueryCachedDivisionAreas;
        _divisionQuery = QueryDivisionAreas;
    }

    internal OvertureDivisionsService(
        ILogger<OvertureDivisionsService> logger,
        OverturePlacesService overturePlacesService,
        string dataDir,
        string bundledDataDir,
        Func<string, string?> alpha2ToIso3,
        OvertureDivisionsTestHooks hooks)
        : this(logger, overturePlacesService, dataDir, bundledDataDir, alpha2ToIso3)
    {
        _cachedDivisionQuery = hooks.CachedDivisionQuery ?? _cachedDivisionQuery;
        _divisionQuery = hooks.DivisionQuery ?? _divisionQuery;
        _geometryContains = hooks.GeometryContains ?? _geometryContains;
        _hasColumnCheckpoint = hooks.HasColumnCheckpoint;
    }

    public Task<BundledCountryLookupResult> FindBundledCountryAsync(
        double lat,
        double lon,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var bundledPath = Path.Combine(_bundledDataDir, "defaults", "overture-country-divisions.db");
        if (!File.Exists(bundledPath))
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(BundledCountryLookupResult.SpatialNoMatch(
                "Bundled Overture country artifact was not found."));
        }

        var best = FindBundledCountryFromMemory(bundledPath, lat, lon, ct);
        ct.ThrowIfCancellationRequested();
        if (best is null)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(BundledCountryLookupResult.SpatialNoMatch(
                "Bundled Overture spatial coverage found no match."));
        }

        if (string.IsNullOrWhiteSpace(best.Country))
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(BundledCountryLookupResult.IdentityMappingFailure(
                best.Name,
                null,
                best.Id,
                "Matched bundled geometry has no Alpha-2 identity."));
        }

        var mappedAlpha3 = _alpha2ToIso3(best.Country);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(best.Alpha3)
            || string.IsNullOrWhiteSpace(mappedAlpha3)
            || !string.Equals(best.Alpha3, mappedAlpha3, StringComparison.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(BundledCountryLookupResult.IdentityMappingFailure(
                best.Name,
                best.Country,
                best.Id,
                $"Matched bundled identity '{best.Country}' has missing or inconsistent Alpha-3 mapping."));
        }

        ct.ThrowIfCancellationRequested();
        return Task.FromResult(BundledCountryLookupResult.Matched(
            best.Alpha3,
            best.Name,
            best.Country,
            best.Id));
    }

    private OvertureDivisionResult? FindBundledCountryFromMemory(string bundledPath, double lat, double lon, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var countryIndex = GetBundledCountryIndex(bundledPath, ct);
        var point = GeometryFactory.CreatePoint(new Coordinate(lon, lat));
        var queryEnvelope = new Envelope(point.EnvelopeInternal);
        queryEnvelope.ExpandBy(0.00015);
        OvertureDivisionResult? bestUsable = null;
        OvertureDivisionResult? bestUnusable = null;

        foreach (var country in countryIndex.Query(queryEnvelope))
        {
            ct.ThrowIfCancellationRequested();
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

    private STRtree<BundledCountryArea> GetBundledCountryIndex(string bundledPath, CancellationToken ct)
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

            ct.ThrowIfCancellationRequested();
            var index = LoadBundledCountryIndex(bundledPath, ct);
            ct.ThrowIfCancellationRequested();
            _bundledCountryIndex = index;
            return _bundledCountryIndex;
        }
    }

    private STRtree<BundledCountryArea> LoadBundledCountryIndex(string bundledPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var index = new STRtree<BundledCountryArea>();

        using var conn = new SqliteConnection($"Data Source={bundledPath};Pooling=false");
        conn.Open();
        ct.ThrowIfCancellationRequested();
        var adminLevelColumn = _hasColumn(conn, "division_area", "admin_level", ct) ? "admin_level" : "NULL AS admin_level";
        ct.ThrowIfCancellationRequested();
        var alpha3Column = _hasColumn(conn, "division_area", "alpha3", ct) ? "alpha3" : "NULL AS alpha3";
        ct.ThrowIfCancellationRequested();
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

        ct.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
        using var reader = cmd.ExecuteReader();
        ct.ThrowIfCancellationRequested();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var hasRow = reader.Read();
            ct.ThrowIfCancellationRequested();
            if (!hasRow)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();
            if (reader.IsDBNull(9))
            {
                continue;
            }

            var geometry = WkbReader.Read(OvertureDataAccess.ReadBlobValue(reader.GetValue(9)));
            ct.ThrowIfCancellationRequested();
            var preparedGeometry = PreparedGeometryFactory.Prepare(geometry);
            ct.ThrowIfCancellationRequested();
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

        ct.ThrowIfCancellationRequested();
        index.Build();
        ct.ThrowIfCancellationRequested();
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
        ct.ThrowIfCancellationRequested();
        var diagnostics = await FindContainingDivisionAreasAsync(lat, lon, alpha2, iso3, ct);
        ct.ThrowIfCancellationRequested();
        if (diagnostics.Error is not null || diagnostics.Candidates.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }

        var state = OvertureDivisionsLogic.SelectStateName(diagnostics.Candidates);
        var city = OvertureDivisionsLogic.SelectCityName(diagnostics.Candidates, cityResolverProfile);
        ct.ThrowIfCancellationRequested();
        return new OvertureAdministrativeResult(state, city);
    }

    public async Task<OvertureDivisionLookupDiagnostics> FindContainingDivisionAreasAsync(
        double lat,
        double lon,
        string? alpha2,
        string? iso3 = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var cached = _cachedDivisionQuery(lat, lon, iso3, ct);
            if (cached is not null)
            {
                ct.ThrowIfCancellationRequested();
                return cached;
            }

            var release = await _overturePlacesService.GetLatestReleaseForOvertureAsync(ct);
            var result = await Task.Run(() => _divisionQuery(lat, lon, alpha2, release, ct), ct);
            ct.ThrowIfCancellationRequested();
            return result with { Release = release };
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
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning(
                "Overture division lookup failed at ({Lat:F4}, {Lon:F4}): {Message}",
                lat,
                lon,
                ex.Message);
            return new OvertureDivisionLookupDiagnostics(null, [], null, ex.Message);
        }
    }

    private OvertureDivisionLookupDiagnostics? QueryCachedDivisionAreas(double lat, double lon, string? iso3, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(iso3))
        {
            return null;
        }

        var dbPath = Path.Combine(_dataDir, "overture-divisions", $"{iso3}.db");
        if (!File.Exists(dbPath))
        {
            return null;
        }

        return QueryDivisionAreasFromSqlite(dbPath, lat, lon, "cached", ct);
    }

    private OvertureDivisionLookupDiagnostics QueryDivisionAreasFromSqlite(
        string dbPath,
        double lat,
        double lon,
        string selectionLabel,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();
        ct.ThrowIfCancellationRequested();
        var adminLevelColumn = _hasColumn(conn, "division_area", "admin_level", ct) ? "admin_level" : "NULL AS admin_level";
        ct.ThrowIfCancellationRequested();
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

        ct.ThrowIfCancellationRequested();
        using var reader = cmd.ExecuteReader();
        ct.ThrowIfCancellationRequested();
        var point = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326).CreatePoint(new Coordinate(lon, lat));
        var candidates = new List<OvertureDivisionCandidateDiagnostic>();
        OvertureDivisionResult? best = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var hasRow = reader.Read();
            ct.ThrowIfCancellationRequested();
            if (!hasRow)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();
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
                                   && OvertureDataAccess.TryGeometryContains(
                                       OvertureDataAccess.ReadBlobValue(reader.GetValue(8)),
                                       point,
                                       _geometryContains);
            ct.ThrowIfCancellationRequested();
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
            ct.ThrowIfCancellationRequested();

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
        ct.ThrowIfCancellationRequested();
        var release = meta.ExecuteScalar()?.ToString();
        ct.ThrowIfCancellationRequested();
        return new OvertureDivisionLookupDiagnostics(best, candidates, release);
    }

    private static OvertureDivisionLookupDiagnostics QueryDivisionAreas(
        double lat,
        double lon,
        string? alpha2,
        string release,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var releaseUrl = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            OvertureDivisionsLogic.DivisionAreaReleaseUrlTemplate,
            release);

        using var conn = new DuckDBConnection("Data Source=:memory:");
        conn.Open();
        ct.ThrowIfCancellationRequested();
        OvertureDataAccess.LoadAzureAndSpatial(conn);
        ct.ThrowIfCancellationRequested();

        using var query = conn.CreateCommand();
        query.CommandText = OvertureDivisionsLogic.BuildDivisionAreaQuery(lat, lon, alpha2, releaseUrl);

        var candidates = new List<OvertureDivisionCandidateDiagnostic>();
        OvertureDivisionResult? best = null;

        ct.ThrowIfCancellationRequested();
        using var reader = query.ExecuteReader();
        ct.ThrowIfCancellationRequested();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var hasRow = reader.Read();
            ct.ThrowIfCancellationRequested();
            if (!hasRow)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();
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
            ct.ThrowIfCancellationRequested();

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

        ct.ThrowIfCancellationRequested();
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

    private bool HasColumn(SqliteConnection conn, string tableName, string columnName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        _hasColumnCheckpoint?.Invoke(OvertureHasColumnCheckpoint.BeforeExecuteReader);
        ct.ThrowIfCancellationRequested();
        using var reader = cmd.ExecuteReader();
        _hasColumnCheckpoint?.Invoke(OvertureHasColumnCheckpoint.AfterExecuteReader);
        ct.ThrowIfCancellationRequested();
        while (true)
        {
            _hasColumnCheckpoint?.Invoke(OvertureHasColumnCheckpoint.BeforeRowRead);
            ct.ThrowIfCancellationRequested();
            var hasRow = reader.Read();
            _hasColumnCheckpoint?.Invoke(OvertureHasColumnCheckpoint.AfterRowRead);
            ct.ThrowIfCancellationRequested();
            if (!hasRow)
            {
                break;
            }

            if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                return true;
            }
        }

        ct.ThrowIfCancellationRequested();
        return false;
    }
}

internal sealed class OvertureDivisionsTestHooks
{
    public Func<double, double, string?, CancellationToken, OvertureDivisionLookupDiagnostics?>? CachedDivisionQuery { get; init; }
    public Func<double, double, string?, string, CancellationToken, OvertureDivisionLookupDiagnostics>? DivisionQuery { get; init; }
    public Func<byte[], Point, bool>? GeometryContains { get; init; }
    public Action<OvertureHasColumnCheckpoint>? HasColumnCheckpoint { get; init; }
}

internal enum OvertureHasColumnCheckpoint
{
    BeforeExecuteReader,
    AfterExecuteReader,
    BeforeRowRead,
    AfterRowRead
}
