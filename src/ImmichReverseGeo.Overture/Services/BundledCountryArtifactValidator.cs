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
            ValidateFixture(connection, fixture);
        }

        foreach (var fixture in parentCountryControls ?? CountryResolutionFixtureCatalog.ParentCountryControls)
        {
            ValidateFixture(connection, fixture);
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
        CountryResolutionFixture fixture)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT geom_wkb FROM division_area
            WHERE country = $alpha2 AND alpha3 = $alpha3 AND name = $name
              AND bbox_xmax >= $lon AND bbox_xmin <= $lon
              AND bbox_ymax >= $lat AND bbox_ymin <= $lat
            """;
        command.Parameters.AddWithValue("$alpha2", fixture.Alpha2);
        command.Parameters.AddWithValue("$alpha3", fixture.Alpha3);
        command.Parameters.AddWithValue("$name", fixture.DisplayName);
        command.Parameters.AddWithValue("$lon", fixture.Longitude);
        command.Parameters.AddWithValue("$lat", fixture.Latitude);
        using var reader = command.ExecuteReader();
        var point = GeometryFactory.CreatePoint(new Coordinate(fixture.Longitude, fixture.Latitude));
        while (reader.Read())
        {
            var geometry = WkbReader.Read(OvertureDataAccess.ReadBlobValue(reader.GetValue(0)));
            if (geometry.Covers(point))
            {
                return;
            }
        }

        throw new InvalidDataException(
            $"Country fixture '{fixture.Label}' has no exact boundary coverage for '{fixture.DisplayName}'/{fixture.Alpha2}/{fixture.Alpha3}.");
    }

    private static Envelope ReadBounds(SqliteDataReader reader, string id)
    {
        if (reader.IsDBNull(6) || reader.IsDBNull(7) || reader.IsDBNull(8) || reader.IsDBNull(9))
        {
            throw new InvalidDataException($"Bundled country row '{id}' has incomplete source bounds.");
        }

        var xmin = reader.GetDouble(6);
        var ymin = reader.GetDouble(7);
        var xmax = reader.GetDouble(8);
        var ymax = reader.GetDouble(9);
        if (!double.IsFinite(xmin) || !double.IsFinite(ymin) || !double.IsFinite(xmax) || !double.IsFinite(ymax)
            || xmin > xmax || ymin > ymax)
        {
            throw new InvalidDataException($"Bundled country row '{id}' has invalid source bounds.");
        }

        return new Envelope(xmin, xmax, ymin, ymax);
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
