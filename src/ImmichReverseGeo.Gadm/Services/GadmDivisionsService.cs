using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Gadm.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Gadm.Services;

public class GadmDivisionsService
{
    private static readonly WKBReader WkbReader = new();
    private static readonly GeometryFactory GeometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    private readonly ILogger<GadmDivisionsService> _logger;
    private readonly string _dataDir;
    private readonly Func<string, double, double, CancellationToken, GadmDivisionLookupDiagnostics> _queryOperation;
    private readonly Func<byte[], Point, bool> _geometryContainsOperation;
    private readonly Action<GadmLookupCheckpoint>? _checkpoint;

    public GadmDivisionsService(ILogger<GadmDivisionsService> logger, string dataDir)
    {
        _logger = logger;
        _dataDir = dataDir;
        _geometryContainsOperation = GeometryContains;
        _queryOperation = QueryDivisionAreasFromSqlite;
    }

    internal GadmDivisionsService(
        ILogger<GadmDivisionsService> logger,
        string dataDir,
        Func<string, double, double, CancellationToken, GadmDivisionLookupDiagnostics> queryOperation)
        : this(logger, dataDir)
    {
        _queryOperation = queryOperation ?? throw new ArgumentNullException(nameof(queryOperation));
    }

    internal GadmDivisionsService(
        ILogger<GadmDivisionsService> logger,
        string dataDir,
        Func<byte[], Point, bool> geometryContainsOperation)
        : this(logger, dataDir)
    {
        _geometryContainsOperation = geometryContainsOperation ?? throw new ArgumentNullException(nameof(geometryContainsOperation));
    }

    public async Task<GadmAdministrativeResult?> ResolveAdministrativeGeoAsync(
        double lat,
        double lon,
        string iso3,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var diagnostics = await FindContainingDivisionAreasAsync(lat, lon, iso3, ct);
        ct.ThrowIfCancellationRequested();
        if (diagnostics.Error is not null || diagnostics.Candidates.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }

        var state = GadmDivisionsLogic.SelectStateName(diagnostics.Candidates);
        _checkpoint?.Invoke(GadmLookupCheckpoint.StateSelected);
        ct.ThrowIfCancellationRequested();

        var city = GadmDivisionsLogic.SelectCityName(diagnostics.Candidates);
        _checkpoint?.Invoke(GadmLookupCheckpoint.CitySelected);
        ct.ThrowIfCancellationRequested();

        var result = new GadmAdministrativeResult(state, city);
        ct.ThrowIfCancellationRequested();
        return result;
    }

    internal GadmDivisionsService(
        ILogger<GadmDivisionsService> logger,
        string dataDir,
        Action<GadmLookupCheckpoint> checkpoint)
        : this(logger, dataDir)
    {
        _checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
    }

    public Task<GadmDivisionLookupDiagnostics> FindContainingDivisionAreasAsync(
        double lat,
        double lon,
        string iso3,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var dbPath = Path.Combine(_dataDir, "gadm-divisions", $"{iso3}.db");
            if (!File.Exists(dbPath))
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new GadmDivisionLookupDiagnostics(null, [], GadmDivisionsLogic.DatasetVersion));
            }

            ct.ThrowIfCancellationRequested();
            var diagnostics = _queryOperation(dbPath, lat, lon, ct);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(diagnostics);
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
            _logger.LogWarning(
                "GADM division lookup failed at ({Lat:F4}, {Lon:F4}) for {ISO3}: {Message}",
                lat,
                lon,
                iso3,
                ex.Message);
            return Task.FromResult(new GadmDivisionLookupDiagnostics(null, [], GadmDivisionsLogic.DatasetVersion, ex.Message));
        }
    }

    public Task<GadmDivisionLookupDiagnostics> FindContainingDivisionAreasAsync(
        double lat,
        double lon,
        IEnumerable<string> iso3Codes,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var candidates = new List<GadmDivisionCandidateDiagnostic>();
            GadmDivisionResult? best = null;
            string? version = null;

            foreach (var iso3 in iso3Codes)
            {
                if (string.IsNullOrWhiteSpace(iso3))
                {
                    continue;
                }

                var dbPath = Path.Combine(_dataDir, "gadm-divisions", $"{iso3}.db");
                if (!File.Exists(dbPath))
                {
                    ct.ThrowIfCancellationRequested();
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                var partial = _queryOperation(dbPath, lat, lon, ct);
                ct.ThrowIfCancellationRequested();
                version ??= partial.Version;

                foreach (var candidate in partial.Candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    candidates.Add(candidate);
                }

                if (partial.BestMatch is not null
                    && (best is null || GadmDivisionsLogic.ShouldPreferDivisionCandidate(partial.BestMatch, best)))
                {
                    best = partial.BestMatch;
                }
            }

            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new GadmDivisionLookupDiagnostics(best, candidates, version ?? GadmDivisionsLogic.DatasetVersion));
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
            _logger.LogWarning(
                "GADM division multi-cache lookup failed at ({Lat:F4}, {Lon:F4}): {Message}",
                lat,
                lon,
                ex.Message);
            return Task.FromResult(new GadmDivisionLookupDiagnostics(null, [], GadmDivisionsLogic.DatasetVersion, ex.Message));
        }
    }

    private GadmDivisionLookupDiagnostics QueryDivisionAreasFromSqlite(
        string dbPath,
        double lat,
        double lon,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();
        ct.ThrowIfCancellationRequested();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                id,
                name,
                english_type,
                local_type,
                admin_level,
                geom_wkb,
                bbox_xmin,
                bbox_ymin,
                bbox_xmax,
                bbox_ymax
            FROM gadm_area
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
        var point = GeometryFactory.CreatePoint(new Coordinate(lon, lat));
        var candidates = new List<GadmDivisionCandidateDiagnostic>();
        GadmDivisionResult? best = null;

        while (true)
        {
            _checkpoint?.Invoke(GadmLookupCheckpoint.BeforeCandidateRowRead);
            ct.ThrowIfCancellationRequested();
            var hasRow = reader.Read();
            _checkpoint?.Invoke(GadmLookupCheckpoint.AfterCandidateRowRead);
            ct.ThrowIfCancellationRequested();
            if (!hasRow)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();
            var bboxContains = lon >= reader.GetDouble(6)
                               && lon <= reader.GetDouble(8)
                               && lat >= reader.GetDouble(7)
                               && lat <= reader.GetDouble(9);
            var geometryContains = bboxContains
                && TryGeometryContains((byte[])reader["geom_wkb"], point, _geometryContainsOperation);
            ct.ThrowIfCancellationRequested();
            var bboxArea = Math.Abs((reader.GetDouble(8) - reader.GetDouble(6)) * (reader.GetDouble(9) - reader.GetDouble(7)));

            var candidate = new GadmDivisionResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                bboxContains,
                geometryContains,
                bboxArea);

            var selected = false;
            var decision = "considered: weaker than current GADM best";
            if (best is null || GadmDivisionsLogic.ShouldPreferDivisionCandidate(candidate, best))
            {
                decision = best is null
                    ? "selected: first containing GADM area"
                    : candidate.GeometryContainsPoint && !best.GeometryContainsPoint
                        ? "selected: GADM geometry containment outranked previous best"
                        : candidate.AdminLevel > best.AdminLevel
                            ? "selected: deeper GADM admin level outranked previous best"
                            : "selected: tighter GADM bounding area outranked previous best";
                best = candidate;
                selected = true;
            }

            candidates.Add(new GadmDivisionCandidateDiagnostic(
                candidate.Id,
                candidate.Name,
                candidate.EnglishType,
                candidate.LocalType,
                candidate.AdminLevel,
                candidate.BoundingBoxContainsPoint,
                candidate.GeometryContainsPoint,
                candidate.BoundingBoxArea,
                selected,
                decision));
        }

        ct.ThrowIfCancellationRequested();
        using var meta = conn.CreateCommand();
        meta.CommandText = "SELECT value FROM _meta WHERE key = 'version'";
        ct.ThrowIfCancellationRequested();
        var version = meta.ExecuteScalar()?.ToString() ?? GadmDivisionsLogic.DatasetVersion;
        _checkpoint?.Invoke(GadmLookupCheckpoint.AfterMetadataScalar);
        ct.ThrowIfCancellationRequested();

        ct.ThrowIfCancellationRequested();
        return new GadmDivisionLookupDiagnostics(best, candidates, version);
    }

    internal enum GadmLookupCheckpoint
    {
        BeforeCandidateRowRead,
        AfterCandidateRowRead,
        AfterMetadataScalar,
        StateSelected,
        CitySelected
    }

    private static bool GeometryContains(byte[] wkb, Point point)
    {
        var geometry = WkbReader.Read(wkb);
        return geometry.Covers(point) || geometry.Distance(point) <= 0.00015;
    }

    private static bool TryGeometryContains(
        byte[] wkb,
        Point point,
        Func<byte[], Point, bool> geometryContainsOperation)
    {
        try
        {
            return geometryContainsOperation(wkb, point);
        }
        catch (ParseException)
        {
            return false;
        }
        catch (TopologyException)
        {
            return false;
        }
    }
}
