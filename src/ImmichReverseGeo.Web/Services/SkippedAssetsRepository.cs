using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

/// <summary>
/// Tracks asset IDs that had no ADM0 boundary match, preventing endless reprocessing.
/// Stored in /data/skipped.db (SQLite).
/// </summary>
public class SkippedAssetsRepository(ILogger<SkippedAssetsRepository> logger, string dataDir)
    : IProcessingSkippedStore, ISkippedAssetsMaintenanceStore, ISkippedAssetsCountReader
{
    // DI constructor
    public SkippedAssetsRepository(ILogger<SkippedAssetsRepository> logger, StorageOptions dirs)
        : this(logger, dirs.DataDir) { }

    private readonly string _dbPath = Path.Combine(dataDir, "skipped.db");

    // Pooling=false: this is a singleton-owned file; pooling adds no benefit and forces
    // callers to ClearAllPools() before deleting the file on Windows (global side-effect).
    private string ConnectionString => $"Data Source={_dbPath};Pooling=false";

    private string ExistingConnectionString(SqliteOpenMode mode)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = mode,
            Pooling = false
        }.ToString();
    }

    public Task InitialiseAsync()
    {
        return InitialiseAsync(CancellationToken.None);
    }

    public async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Initialising skipped assets database at {Path}", _dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS skipped_assets (
                asset_id  TEXT PRIMARY KEY,
                skipped_at TEXT NOT NULL
            )
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddAsync(Guid assetId)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO skipped_assets (asset_id, skipped_at)
            VALUES ($id, $at)
            """;
        cmd.Parameters.AddWithValue("$id", assetId.ToString());
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<HashSet<Guid>> GetAllAsync()
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT asset_id FROM skipped_assets";
        var result = new HashSet<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(Guid.Parse(reader.GetString(0)));
        }

        return result;
    }

    public async Task<long> GetCountAsync()
    {
        await using var conn = await OpenExistingAsync(SqliteOpenMode.ReadOnly, CancellationToken.None);
        if (conn is null)
        {
            return 0;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM skipped_assets";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<long> ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenExistingAsync(SqliteOpenMode.ReadWrite, cancellationToken);
        if (conn is null)
        {
            return 0;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM skipped_assets";
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Cleared {Count} skipped assets", rows);
        return rows;
    }

    public async Task<long> RemoveAsync(
        IEnumerable<Guid> assetIds,
        CancellationToken cancellationToken = default)
    {
        var ids = assetIds
            .Distinct()
            .Select(id => id.ToString())
            .ToArray();

        if (ids.Length == 0)
        {
            return 0;
        }

        await using var conn = await OpenExistingAsync(SqliteOpenMode.ReadWrite, cancellationToken);
        if (conn is null)
        {
            return 0;
        }

        using var tx = conn.BeginTransaction();

        long removed = 0;
        foreach (var id in ids)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM skipped_assets WHERE asset_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            removed += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        logger.LogInformation("Removed {Count} skipped assets by id", removed);
        return removed;
    }

    private async Task<SqliteConnection?> OpenExistingAsync(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ExistingConnectionString(mode));
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 14 && IsConfirmedMissing())
        {
            await connection.DisposeAsync();
            return null;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private bool IsConfirmedMissing()
    {
        try
        {
            _ = File.GetAttributes(_dbPath);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }
}
