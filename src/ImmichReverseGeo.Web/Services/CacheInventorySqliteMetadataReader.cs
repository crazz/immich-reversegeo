using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Web.Services;

internal sealed class CacheInventorySqliteMetadataReader : ICacheInventoryMetadataReader
{
    private static readonly string[] DownloadedAtFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    ];

    public async Task<CacheInventoryMetadataResult> ReadAsync(
        string databasePath,
        CacheMutationSource source,
        CacheInventoryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentNullException.ThrowIfNull(options);
        string expectedTable = source switch
        {
            CacheMutationSource.Overture => "division_area",
            CacheMutationSource.Gadm => "gadm_area",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        string versionKey = source == CacheMutationSource.Overture ? "release" : "version";

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = checked((int)Math.Ceiling(options.SqliteBusyTimeout.TotalSeconds))
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            SQLitePCL.raw.sqlite3_limit(
                connection.Handle,
                SQLitePCL.raw.SQLITE_LIMIT_LENGTH,
                options.MaxSqliteValueBytes);

            SchemaInspection schema = await InspectSchemaAsync(
                connection,
                expectedTable,
                options,
                cancellationToken).ConfigureAwait(false);
            if (!schema.IsWithinBound)
            {
                return Invalid(CacheInventoryDiagnosticCode.SchemaObjectLimitExceeded);
            }

            if (!schema.ExpectedTablePresent)
            {
                return Invalid(CacheInventoryDiagnosticCode.MissingExpectedTable);
            }

            if (!schema.MetadataTablePresent)
            {
                return Available(null, null);
            }

            if (!await HasExpectedMetadataShapeAsync(
                    connection,
                    options,
                    cancellationToken).ConfigureAwait(false))
            {
                return Invalid(CacheInventoryDiagnosticCode.InvalidMetadataSchema);
            }

            string? downloadedText = await ReadMetadataValueAsync(
                connection,
                "downloadedAt",
                options,
                cancellationToken).ConfigureAwait(false);
            string? version = await ReadMetadataValueAsync(
                connection,
                versionKey,
                options,
                cancellationToken).ConfigureAwait(false);
            if (!TryParseDownloaded(downloadedText, out DateTimeOffset? downloadedUtc)
                || version is { Length: 0 })
            {
                return Invalid(CacheInventoryDiagnosticCode.InvalidMetadataValue);
            }

            return Available(downloadedUtc, version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            if (exception.SqliteErrorCode == 18)
            {
                return Invalid(CacheInventoryDiagnosticCode.InvalidMetadataValue);
            }

            return IsTransient(exception.SqliteErrorCode)
                ? Unreadable()
                : Invalid(CacheInventoryDiagnosticCode.InvalidDatabase);
        }
    }

    private static async Task<SchemaInspection> InspectSchemaAsync(
        SqliteConnection connection,
        string expectedTable,
        CacheInventoryOptions options,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = checked((int)Math.Ceiling(options.SqliteBusyTimeout.TotalSeconds));
        command.CommandText = """
            SELECT substr(type, 1, 17), substr(name, 1, 129)
            FROM sqlite_schema
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", options.MaxSchemaObjects + 1);
        var objects = new List<SchemaObject>(options.MaxSchemaObjects + 1);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            objects.Add(new SchemaObject(reader.GetString(0), reader.GetString(1)));
        }

        if (objects.Count > options.MaxSchemaObjects
            || objects.Exists(value => value.Type.Length > 16 || value.Name.Length > 128))
        {
            return new SchemaInspection(false, false, false);
        }

        return new SchemaInspection(
            true,
            objects.Exists(value => value.Type == "table" && value.Name == expectedTable),
            objects.Exists(value => value.Type == "table" && value.Name == "_meta"));
    }

    private static async Task<bool> HasExpectedMetadataShapeAsync(
        SqliteConnection connection,
        CacheInventoryOptions options,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = checked((int)Math.Ceiling(options.SqliteBusyTimeout.TotalSeconds));
        command.CommandText = """
            SELECT cid, substr(name, 1, 33), upper(substr(type, 1, 17)), "notnull", pk
            FROM pragma_table_info('_meta')
            LIMIT 3;
            """;
        var rows = new List<MetadataColumn>(3);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new MetadataColumn(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4)));
        }

        return rows.Count == 2
            && rows[0] == new MetadataColumn(0, "key", "TEXT", 0, 1)
            && rows[1] == new MetadataColumn(1, "value", "TEXT", 1, 0);
    }

    private static async Task<string?> ReadMetadataValueAsync(
        SqliteConnection connection,
        string key,
        CacheInventoryOptions options,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = checked((int)Math.Ceiling(options.SqliteBusyTimeout.TotalSeconds));
        command.CommandText = """
            SELECT typeof(value), substr(CAST(value AS TEXT), 1, $take)
            FROM _meta
            WHERE key = $key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$take", options.MaxMetadataValueCharacters + 1);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0), "text", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        string value = reader.GetString(1);
        return value.Length <= options.MaxMetadataValueCharacters ? value : string.Empty;
    }

    private static bool TryParseDownloaded(
        string? value,
        out DateTimeOffset? downloadedUtc)
    {
        downloadedUtc = null;
        if (value is null)
        {
            return true;
        }

        if (!DateTimeOffset.TryParseExact(
                value,
                DownloadedAtFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            return false;
        }

        downloadedUtc = parsed.ToUniversalTime();
        return true;
    }

    private static bool IsTransient(int sqliteErrorCode) => sqliteErrorCode is 5 or 6 or 8 or 10 or 14;

    private static CacheInventoryMetadataResult Available(
        DateTimeOffset? downloadedUtc,
        string? version) =>
        new(CacheInventoryMetadataStatus.Available, downloadedUtc, version, null);

    private static CacheInventoryMetadataResult Invalid(
        CacheInventoryDiagnosticCode diagnosticCode) =>
        new(CacheInventoryMetadataStatus.Invalid, null, null, diagnosticCode);

    private static CacheInventoryMetadataResult Unreadable() =>
        new(
            CacheInventoryMetadataStatus.Unreadable,
            null,
            null,
            CacheInventoryDiagnosticCode.CandidateUnreadable);

    private sealed record SchemaInspection(
        bool IsWithinBound,
        bool ExpectedTablePresent,
        bool MetadataTablePresent);

    private sealed record SchemaObject(string Type, string Name);

    private sealed record MetadataColumn(
        int Cid,
        string Name,
        string Type,
        int NotNull,
        int PrimaryKeyOrder);
}
