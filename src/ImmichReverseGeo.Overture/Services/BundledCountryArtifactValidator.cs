using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImmichReverseGeo.Overture.Models;
using Microsoft.Data.Sqlite;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Services;

public static class BundledCountryArtifactValidator
{
    private const double CoordinateTolerance = 0.00015;
    private static readonly GeometryFactory GeometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
    private static readonly WKBReader WkbReader = new();

    public static CountryArtifactStatistics Validate(
        string databasePath,
        CountryIdentityCatalog identityCatalog,
        IReadOnlyList<CountryResolutionFixture>? mandatoryTerritories = null,
        IReadOnlyList<CountryResolutionFixture>? parentCountryControls = null)
    {
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Bundled country artifact was not found.", databasePath);
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=false");
        connection.Open();
        ValidateRequiredColumns(connection);
        var statistics = ValidateRows(connection, identityCatalog);

        foreach (var fixture in mandatoryTerritories ?? CountryResolutionFixtureCatalog.MandatoryTerritories)
        {
            ValidateFixture(connection, fixture, rejectSovereignNormalization: true);
        }

        foreach (var fixture in parentCountryControls ?? CountryResolutionFixtureCatalog.ParentCountryControls)
        {
            ValidateFixture(connection, fixture, rejectSovereignNormalization: false);
        }

        return statistics;
    }

    private static CountryArtifactStatistics ValidateRows(
        SqliteConnection connection,
        CountryIdentityCatalog identityCatalog)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, alpha3, country, subtype, geom_wkb,
                   bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax, source_country
            FROM division_area
            """;
        using var reader = command.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long rowCount = 0;
        long wkbBytes = 0;

        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!ids.Add(id))
            {
                throw new InvalidDataException($"Duplicate bundled country row ID '{id}'.");
            }

            var name = reader.GetString(1);
            var alpha3 = OvertureDataAccess.ReadNullableString(reader, 2);
            var alpha2 = OvertureDataAccess.ReadNullableString(reader, 3);
            var subtype = OvertureDataAccess.ReadNullableString(reader, 4);
            if (!string.Equals(subtype, "country", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(subtype, "dependency", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsupported bundled country subtype '{subtype ?? "<null>"}' for row '{id}'.");
            }

            if (string.IsNullOrWhiteSpace(alpha2))
            {
                throw new InvalidDataException($"Bundled country row '{id}' has no Alpha-2 identity.");
            }

            var sourceAlpha2 = OvertureDataAccess.ReadNullableString(reader, 10);
            if (string.IsNullOrWhiteSpace(sourceAlpha2))
            {
                throw new InvalidDataException($"Bundled country row '{id}' has no source Alpha-2 identity.");
            }

            var sourceIdentity = identityCatalog.ResolveSourceAlpha2(sourceAlpha2);
            if (sourceIdentity is not null && !string.Equals(sourceIdentity.Alpha2, alpha2, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Bundled country row '{id}' maps source identity '{sourceAlpha2}' to inconsistent canonical identity '{alpha2}'.");
            }

            if (sourceIdentity is null && !identityCatalog.IsExplicitlyNonIso(sourceAlpha2))
            {
                throw new InvalidDataException($"Bundled country row '{id}' uses unknown source identity '{sourceAlpha2}'.");
            }

            var identity = identityCatalog.FindByAlpha2(alpha2);
            if (identity is not null)
            {
                if (!string.Equals(identity.Alpha3, alpha3, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(identity.DisplayName, name, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Bundled country row '{id}' has inconsistent canonical identity for '{alpha2}'.");
                }

                identities.Add(identity.Alpha2);
            }
            else if (!identityCatalog.IsExplicitlyNonIso(alpha2))
            {
                throw new InvalidDataException($"Bundled country row '{id}' uses unmapped standard identity '{alpha2}'.");
            }
            else if (!string.IsNullOrWhiteSpace(alpha3))
            {
                throw new InvalidDataException($"Non-ISO bundled country row '{id}' unexpectedly has Alpha-3 '{alpha3}'.");
            }

            if (reader.IsDBNull(5))
            {
                throw new InvalidDataException($"Bundled country row '{id}' has no geometry.");
            }

            var wkb = OvertureDataAccess.ReadBlobValue(reader.GetValue(5));
            var geometry = WkbReader.Read(wkb);
            if (geometry.IsEmpty || !geometry.IsValid)
            {
                throw new InvalidDataException($"Bundled country row '{id}' has invalid geometry.");
            }

            var sourceBounds = ReadBounds(reader, id);
            if (!sourceBounds.Contains(geometry.EnvelopeInternal))
            {
                throw new InvalidDataException($"Bundled country row '{id}' has source bounds that do not contain its geometry.");
            }

            rowCount++;
            wkbBytes += wkb.LongLength;
        }

        if (rowCount == 0)
        {
            throw new InvalidDataException("Bundled country artifact contains no rows.");
        }

        var release = ReadMetadata(connection, "release");
        if (string.IsNullOrWhiteSpace(release))
        {
            throw new InvalidDataException("Bundled country artifact has no release metadata.");
        }

        return new CountryArtifactStatistics(release, rowCount, identities.Count, wkbBytes);
    }

    private static void ValidateFixture(
        SqliteConnection connection,
        CountryResolutionFixture fixture,
        bool rejectSovereignNormalization)
    {
        var candidates = QueryFixtureCandidates(connection, fixture.Latitude, fixture.Longitude);
        var best = candidates.Aggregate(
            (OvertureDivisionResult?)null,
            (current, candidate) => current is null || OvertureDivisionsLogic.ShouldPreferBundledCountryCandidate(candidate, current)
                ? candidate
                : current);

        if (best is null)
        {
            throw new InvalidDataException($"Mandatory country fixture '{fixture.Label}' has no spatial match.");
        }

        if (!string.Equals(best.Name, fixture.DisplayName, StringComparison.Ordinal)
            || !string.Equals(best.Country, fixture.Alpha2, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Country fixture '{fixture.Label}' resolved as '{best.Name}'/{best.Country ?? "<null>"} instead of '{fixture.DisplayName}'/{fixture.Alpha2}.");
        }

        if (rejectSovereignNormalization
            && !string.IsNullOrWhiteSpace(fixture.AdministeringSovereignAlpha2)
            && string.Equals(best.Country, fixture.AdministeringSovereignAlpha2, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Territory fixture '{fixture.Label}' normalized to sovereign '{best.Country}'.");
        }

        using var alpha3Command = connection.CreateCommand();
        alpha3Command.CommandText = "SELECT alpha3 FROM division_area WHERE id = $id";
        alpha3Command.Parameters.AddWithValue("$id", best.Id);
        var alpha3 = alpha3Command.ExecuteScalar()?.ToString();
        if (!string.Equals(alpha3, fixture.Alpha3, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Country fixture '{fixture.Label}' resolved with Alpha-3 '{alpha3 ?? "<null>"}' instead of '{fixture.Alpha3}'.");
        }
    }

    private static List<OvertureDivisionResult> QueryFixtureCandidates(
        SqliteConnection connection,
        double latitude,
        double longitude)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, subtype, class_name, admin_level, country,
                   is_land, is_territorial, geom_wkb,
                   bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax
            FROM division_area
            WHERE bbox_xmax >= $lon AND bbox_xmin <= $lon
              AND bbox_ymax >= $lat AND bbox_ymin <= $lat
            """;
        command.Parameters.AddWithValue("$lon", longitude);
        command.Parameters.AddWithValue("$lat", latitude);
        using var reader = command.ExecuteReader();
        var point = GeometryFactory.CreatePoint(new Coordinate(longitude, latitude));
        var candidates = new List<OvertureDivisionResult>();

        while (reader.Read())
        {
            var geometry = WkbReader.Read(OvertureDataAccess.ReadBlobValue(reader.GetValue(8)));
            var exactCoverage = geometry.Covers(point);
            var geometryContains = exactCoverage || geometry.Distance(point) <= CoordinateTolerance;
            if (!geometryContains)
            {
                continue;
            }

            var xmin = reader.GetDouble(9);
            var ymin = reader.GetDouble(10);
            var xmax = reader.GetDouble(11);
            var ymax = reader.GetDouble(12);
            candidates.Add(new OvertureDivisionResult(
                reader.GetString(0),
                reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                OvertureDataAccess.ReadNullableString(reader, 5),
                null,
                OvertureDataAccess.ReadSqliteBool(reader, 6),
                OvertureDataAccess.ReadSqliteBool(reader, 7),
                true,
                exactCoverage,
                geometryContains,
                Math.Abs((xmax - xmin) * (ymax - ymin))));
        }

        return candidates;
    }

    private static Envelope ReadBounds(SqliteDataReader reader, string id)
    {
        if (reader.IsDBNull(6) || reader.IsDBNull(7) || reader.IsDBNull(8) || reader.IsDBNull(9))
        {
            throw new InvalidDataException($"Bundled country row '{id}' has incomplete source bounds.");
        }

        return new Envelope(reader.GetDouble(6), reader.GetDouble(8), reader.GetDouble(7), reader.GetDouble(9));
    }

    private static string? ReadMetadata(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM _meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar()?.ToString();
    }

    private static void ValidateRequiredColumns(SqliteConnection connection)
    {
        var requiredColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "name", "source_name", "subtype", "class_name", "admin_level",
            "country", "source_country", "alpha3", "is_land", "is_territorial", "geom_wkb",
            "bbox_xmin", "bbox_ymin", "bbox_xmax", "bbox_ymax"
        };

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(division_area)";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            requiredColumns.Remove(reader.GetString(1));
        }

        if (requiredColumns.Count > 0)
        {
            throw new InvalidDataException($"Bundled country artifact is missing columns: {string.Join(", ", requiredColumns.Order())}.");
        }
    }
}

public sealed record CountryArtifactStatistics(
    string Release,
    long RowCount,
    int StandardIdentityCount,
    long WkbBytes);
