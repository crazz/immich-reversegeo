using Npgsql;
using NpgsqlTypes;

namespace ImmichReverseGeo.Tests.CrossProcessRunExclusion;

internal sealed record PostgresLockOwner(int ProcessId, string ApplicationName);

/// <summary>
/// Locates the one-int64 advisory-lock owner in the current database without
/// exposing connection details. This source is linked into both Change32 hosts.
/// </summary>
internal static class PostgresLockOwnerQuery
{
    private const string FindCommandText = """
        SELECT activity.pid, activity.application_name
        FROM pg_locks AS advisory_lock
        INNER JOIN pg_stat_activity AS activity ON activity.pid = advisory_lock.pid
        WHERE advisory_lock.locktype = 'advisory'
          AND advisory_lock.database = (SELECT oid FROM pg_database WHERE datname = current_database())
          AND advisory_lock.classid = $1
          AND advisory_lock.objid = $2
          AND advisory_lock.objsubid = 1
          AND advisory_lock.granted
        ORDER BY activity.pid
        LIMIT 2
        """;

    private const string TerminateCommandText = """
        SELECT pg_terminate_backend($1)
        WHERE EXISTS (
            SELECT 1
            FROM pg_locks AS advisory_lock
            INNER JOIN pg_stat_activity AS activity ON activity.pid = advisory_lock.pid
            WHERE advisory_lock.locktype = 'advisory'
              AND advisory_lock.database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND advisory_lock.classid = $2
              AND advisory_lock.objid = $3
              AND advisory_lock.objsubid = 1
              AND advisory_lock.granted
              AND activity.pid = $1
              AND activity.application_name = $4
        )
        """;

    internal static async Task<PostgresLockOwner?> FindAsync(
        NpgsqlConnection connection,
        long productionKey,
        CancellationToken cancellationToken)
    {
        (uint highKeyPart, uint lowKeyPart) = SplitKey(productionKey);

        await using NpgsqlCommand command = new(FindCommandText, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Oid, highKeyPart);
        command.Parameters.AddWithValue(NpgsqlDbType.Oid, lowKeyPart);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        PostgresLockOwner owner = new(reader.GetInt32(0), reader.GetString(1));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("More than one backend owns the exact PostgreSQL advisory-lock identity.");
        }

        return owner;
    }

    internal static async Task<bool> TryTerminateExactOwnerAsync(
        NpgsqlConnection connection,
        long actualProductionKey,
        int processId,
        string exactApplicationName,
        CancellationToken cancellationToken)
    {
        (uint highKeyPart, uint lowKeyPart) = SplitKey(actualProductionKey);

        await using NpgsqlCommand command = new(TerminateCommandText, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, processId);
        command.Parameters.AddWithValue(NpgsqlDbType.Oid, highKeyPart);
        command.Parameters.AddWithValue(NpgsqlDbType.Oid, lowKeyPart);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, exactApplicationName);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static (uint HighKeyPart, uint LowKeyPart) SplitKey(long actualProductionKey) =>
        (unchecked((uint)(actualProductionKey >> 32)), unchecked((uint)actualProductionKey));
}
