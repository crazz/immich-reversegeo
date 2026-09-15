using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ImmichReverseGeo.Spatial;

/// <summary>
/// Optional SQLite candidate acceleration. Source tables and geometry evaluation
/// remain authoritative; commands and readers never become retained state.
/// </summary>
public static class AdministrativeCandidateIndex
{
    public const string VersionKey = "reversegeo.candidates.version";
    private const string Metadata = "rg_candidates";
    private const string Bounds = "rg_candidate_bounds";
    private const string BoundsColumns = "bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax";
    private static readonly ConditionalWeakTable<GeometryGeneration, Eligibility> EligibilityByGeneration = new();

    public static bool NeedsBuild(DbConnection connection, GeometrySource source, CancellationToken ct)
    {
        var marker = ReadMarker(connection, null, ct);
        return !IsUnknownVersion(marker) && !Validate(connection, null, source, marker, ct);
    }

    public static bool Build(DbConnection connection, GeometrySource source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var marker = ReadMarker(connection, null, ct);
        if (IsUnknownVersion(marker))
        {
            return false;
        }

        using var transaction = connection.BeginTransaction();
        var layout = GetLayout(connection, transaction, source, ct);
        // Published geodata is immutable. Rebuilding auxiliaries is done only in
        // an owned unpublished database, never on a reader's live source file.
        Execute(connection, transaction, $"DROP TABLE IF EXISTS {Bounds}; DROP TABLE IF EXISTS {Metadata};", ct);
        Execute(connection, transaction, $"""
            CREATE TABLE {Metadata} (
                source_rowid INTEGER PRIMARY KEY,
                {layout.Definitions},
                wkb_length INTEGER,
                bbox_xmin REAL NOT NULL, bbox_ymin REAL NOT NULL,
                bbox_xmax REAL NOT NULL, bbox_ymax REAL NOT NULL);
            INSERT INTO {Metadata}
                SELECT rowid, {layout.SourceColumns}, length(geom_wkb), {BoundsColumns}
                FROM {layout.Table};
            CREATE VIRTUAL TABLE {Bounds} USING rtree(id, min_x, max_x, min_y, max_y);
            """, ct);
        if (ScalarLong(connection, transaction, $"""
            SELECT count(*) FROM {Metadata}
            WHERE bbox_xmin > bbox_xmax OR bbox_ymin > bbox_ymax
               OR abs(bbox_xmin) > 1e38 OR abs(bbox_xmax) > 1e38
               OR abs(bbox_ymin) > 1e38 OR abs(bbox_ymax) > 1e38
               OR typeof(bbox_xmin) NOT IN ('real','integer')
               OR typeof(bbox_ymin) NOT IN ('real','integer')
               OR typeof(bbox_xmax) NOT IN ('real','integer')
               OR typeof(bbox_ymax) NOT IN ('real','integer')
            """, ct) != 0)
        {
            return false;
        }

        Execute(connection, transaction, $"""
            INSERT INTO {Bounds} SELECT source_rowid, bbox_xmin, bbox_xmax, bbox_ymin, bbox_ymax FROM {Metadata};
            """, ct);
        marker = "1:" + Guid.NewGuid().ToString("N");
        using (var command = Command(connection, transaction,
                   $"INSERT OR REPLACE INTO _meta(key,value) VALUES ('{VersionKey}', $version)"))
        {
            AddParameter(command, "$version", marker);
            Execute(command, ct);
        }

        if (!Validate(connection, transaction, source, marker, ct))
        {
            return false;
        }

        ct.ThrowIfCancellationRequested();
        transaction.Commit();
        return true;
    }

    public static bool TryConfigure(DbCommand candidateCommand, GeometrySource source,
        GeometryGeneration? generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var state = generation is null || generation.Retired
            ? new Eligibility()
            : EligibilityByGeneration.GetValue(generation, static _ => new Eligibility());
        try
        {
            var connection = candidateCommand.Connection!;
            var transaction = candidateCommand.Transaction;
            var marker = ReadMarker(connection, transaction, ct);
            if (!IsSupportedMarker(marker))
            {
                return false;
            }

            Layout layout;
            Dictionary<long, string> orders;
            lock (state)
            {
                ct.ThrowIfCancellationRequested();
                if (state.Marker != marker)
                {
                    state.Marker = marker;
                    state.Usable = Validate(connection, transaction, source, marker, ct);
                    state.Layout = state.Usable ? GetLayout(connection, transaction, source, ct) : null;
                    state.Orders = state.Usable ? ReadOrders(connection, transaction, state.Layout!, ct) : null;
                }

                if (!state.Usable)
                {
                    return false;
                }

                layout = state.Layout!;
                orders = state.Orders!;
            }

            var order = ReadLegacyOrder(candidateCommand, orders, ct);
            if (order is null)
            {
                return false;
            }

            var columns = string.Join(", ", layout.Columns.Select(static column => "c." + column));
            candidateCommand.CommandText = $"""
                SELECT {columns}, c.wkb_length,
                       c.bbox_xmin, c.bbox_ymin, c.bbox_xmax, c.bbox_ymax
                FROM {Bounds} r CROSS JOIN {Metadata} c ON c.source_rowid = r.id
                WHERE r.max_x >= $lon AND r.min_x <= $lon
                  AND r.max_y >= $lat AND r.min_y <= $lat
                  AND c.bbox_xmax >= $lon AND c.bbox_xmin <= $lon
                  AND c.bbox_ymax >= $lat AND c.bbox_ymin <= $lat
                ORDER BY {order}
                """;
            return true;
        }
        catch (DbException exception)
        {
            RethrowIfCritical(exception);
            ct.ThrowIfCancellationRequested();
            lock (state)
            {
                state.Usable = false;
            }

            return false;
        }
    }

    public static void RethrowIfCritical(DbException exception)
    {
        // SQLITE_NOMEM and SQLITE_IOERR_NOMEM are allocation failures, not an
        // unavailable optional index. Keep the worker's critical-memory policy.
        // https://www.sqlite.org/rescode.html
        if (exception is SqliteException sqlite &&
            (sqlite.SqliteErrorCode == 7 || sqlite.SqliteExtendedErrorCode == 3082))
        {
            throw new OutOfMemoryException("SQLite could not allocate memory for administrative candidates.", exception);
        }
    }

    public static void Disable(GeometryGeneration? generation)
    {
        if (generation is not null && EligibilityByGeneration.TryGetValue(generation, out var state))
        {
            lock (state)
            {
                state.Usable = false;
            }
        }
    }

    private static bool Validate(DbConnection connection, DbTransaction? transaction,
        GeometrySource source, string? marker, CancellationToken ct)
    {
        if (!IsSupportedMarker(marker))
        {
            return false;
        }

        try
        {
            var layout = GetLayout(connection, transaction, source, ct);
            var count = ScalarLong(connection, transaction, $"SELECT count(*) FROM {layout.Table}", ct);
            if (count == 0 || count != ScalarLong(connection, transaction, $"SELECT count(*) FROM {Metadata}", ct)
                || count != ScalarLong(connection, transaction, $"SELECT count(*) FROM {Bounds}", ct))
            {
                return false;
            }

            using (var integrity = Command(connection, transaction, $"SELECT rtreecheck('{Bounds}')"))
            {
                ct.ThrowIfCancellationRequested();
                if (!string.Equals(integrity.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            var sourceProjection = $"SELECT rowid, {layout.SourceColumns}, length(geom_wkb), {BoundsColumns} FROM {layout.Table}";
            var candidateProjection = $"SELECT source_rowid, {string.Join(", ", layout.Columns)}, wkb_length, {BoundsColumns} FROM {Metadata}";
            if (ScalarLong(connection, transaction,
                    $"SELECT EXISTS({sourceProjection} EXCEPT {candidateProjection}) OR EXISTS({candidateProjection} EXCEPT {sourceProjection})", ct) != 0)
            {
                return false;
            }

            return ScalarLong(connection, transaction, $"""
                SELECT count(*) FROM {Metadata} c LEFT JOIN {Bounds} r ON r.id=c.source_rowid
                WHERE r.id IS NULL OR NOT (r.min_x <= c.bbox_xmin AND r.max_x >= c.bbox_xmax
                    AND r.min_y <= c.bbox_ymin AND r.max_y >= c.bbox_ymax)
                """, ct) == 0;
        }
        catch (DbException exception)
        {
            RethrowIfCritical(exception);
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static Dictionary<long, string> ReadOrders(DbConnection connection, DbTransaction? transaction,
        Layout layout, CancellationToken ct)
    {
        var roots = new List<(long Root, string Name, string Type)>();
        using (var command = Command(connection, transaction,
                   "SELECT rootpage, name, type FROM sqlite_schema WHERE tbl_name=$table AND type IN ('table','index')"))
        {
            AddParameter(command, "$table", layout.Table);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                roots.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var result = new Dictionary<long, string>();
        foreach (var root in roots)
        {
            if (root.Type == "table")
            {
                result[root.Root] = "c.source_rowid";
                continue;
            }

            using var command = Command(connection, transaction, "SELECT name, desc, coll, key, cid FROM pragma_index_xinfo($index) ORDER BY seqno");
            AddParameter(command, "$index", root.Name);
            using var reader = command.ExecuteReader();
            var columns = new List<string>();
            var supported = true;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.GetInt32(3) == 0)
                {
                    supported &= reader.GetInt32(4) == -1;
                    continue;
                }

                supported &= !reader.IsDBNull(0) && reader.GetInt32(1) == 0 && reader.GetString(2) == "BINARY";
                columns.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
            }

            if (supported && columns.SequenceEqual(new[] { "bbox_ymin", "bbox_ymax" }))
            {
                result[root.Root] = "c.bbox_ymin, c.bbox_ymax, c.source_rowid";
            }
            else if (supported && columns.SequenceEqual(new[] { "bbox_xmin", "bbox_xmax" }))
            {
                result[root.Root] = "c.bbox_xmin, c.bbox_xmax, c.source_rowid";
            }
        }

        return result;
    }

    private static string? ReadLegacyOrder(DbCommand original, Dictionary<long, string> orders, CancellationToken ct)
    {
        using var version = Command(original.Connection!, original.Transaction, "SELECT sqlite_version()");
        if (!string.Equals(version.ExecuteScalar()?.ToString(), "3.53.3", StringComparison.Ordinal))
        {
            return null;
        }

        using var command = Command(original.Connection!, original.Transaction, "EXPLAIN " + original.CommandText);
        foreach (DbParameter parameter in original.Parameters)
        {
            var copy = command.CreateParameter();
            copy.ParameterName = parameter.ParameterName;
            copy.DbType = parameter.DbType;
            copy.Value = parameter.Value;
            command.Parameters.Add(copy);
        }

        var roots = new Dictionary<int, long>();
        int? nextCursor = null;
        var resultRows = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var opcode = reader.GetString(1);
            switch (opcode)
            {
                case "OpenRead":
                    if (reader.GetInt32(4) != 0 || !roots.TryAdd(reader.GetInt32(2), reader.GetInt64(3)))
                    {
                        return null;
                    }
                    break;
                case "Next":
                    if (nextCursor.HasValue)
                    {
                        return null;
                    }
                    nextCursor = reader.GetInt32(2);
                    break;
                case "ResultRow":
                    resultRows++;
                    break;
                case "Function":
                    if (reader.GetString(5) != "length(1)")
                    {
                        return null;
                    }
                    break;
                case "Init": case "Null": case "SeekGT": case "SeekGE": case "Rewind":
                case "Variable": case "IsNull": case "Affinity": case "IdxGT": case "IdxGE":
                case "DeferredSeek": case "Column": case "RealAffinity": case "Lt": case "Gt":
                case "Halt": case "Transaction": case "Goto": case "Real": case "Integer":
                    break;
                default:
                    // Includes reverse scans, alternative loops, sorters, virtual
                    // tables, and future VM changes outside the characterized plan.
                    return null;
            }
        }

        return resultRows == 1 && nextCursor.HasValue && roots.Count <= 2
            && roots.TryGetValue(nextCursor.Value, out var root) && orders.TryGetValue(root, out var order)
                ? order
                : null;
    }

    private static Layout GetLayout(DbConnection connection, DbTransaction? transaction, GeometrySource source, CancellationToken ct)
    {
        if (source == GeometrySource.Gadm)
        {
            return new Layout("gadm_area", ["id", "name", "english_type", "local_type", "admin_level"],
                "id TEXT NOT NULL UNIQUE, name TEXT NOT NULL, english_type TEXT, local_type TEXT, admin_level INTEGER NOT NULL",
                "id, name, english_type, local_type, admin_level");
        }

        if (source != GeometrySource.Overture)
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        var hasAdminLevel = ScalarLong(connection, transaction,
            "SELECT count(*) FROM pragma_table_info('division_area') WHERE name='admin_level'", ct) != 0;
        return new Layout("division_area", ["id", "name", "subtype", "class_name", "admin_level", "country", "is_land", "is_territorial"],
            "id TEXT NOT NULL UNIQUE, name TEXT NOT NULL, subtype TEXT, class_name TEXT, admin_level INTEGER, country TEXT, is_land INTEGER, is_territorial INTEGER",
            $"id, name, subtype, class_name, {(hasAdminLevel ? "admin_level" : "NULL")}, country, is_land, is_territorial");
    }

    private static string? ReadMarker(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        using var command = Command(connection, transaction, $"SELECT value FROM _meta WHERE key='{VersionKey}'");
        ct.ThrowIfCancellationRequested();
        var result = command.ExecuteScalar()?.ToString();
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private static bool IsSupportedMarker(string? marker) =>
        marker is not null && marker.StartsWith("1:", StringComparison.Ordinal) && Guid.TryParseExact(marker[2..], "N", out _);

    private static bool IsUnknownVersion(string? marker) =>
        marker is not null && !marker.StartsWith("1:", StringComparison.Ordinal);

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static long ScalarLong(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct)
    {
        using var command = Command(connection, transaction, sql);
        ct.ThrowIfCancellationRequested();
        var result = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private static void Execute(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct)
    {
        using var command = Command(connection, transaction, sql);
        Execute(command, ct);
    }

    private static void Execute(DbCommand command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        command.ExecuteNonQuery();
        ct.ThrowIfCancellationRequested();
    }

    private sealed record Layout(string Table, string[] Columns, string Definitions, string SourceColumns);

    private sealed class Eligibility
    {
        public string? Marker;
        public bool Usable;
        public Layout? Layout;
        public Dictionary<long, string>? Orders;
    }
}
