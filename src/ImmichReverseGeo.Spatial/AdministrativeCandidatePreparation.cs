using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Spatial;

/// <summary>Bounds offline candidate preparation for immutable country caches.</summary>
public sealed class AdministrativeCandidatePreparation
{
    private static readonly SemaphoreSlim CopyGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, (string Stamp, bool Needed)> _observations = new(StringComparer.Ordinal);
    private readonly GeometrySource _source;
    private readonly Func<string, bool, DbConnection> _connectionFactory;

    public AdministrativeCandidatePreparation(GeometrySource source, Func<string, bool, DbConnection> connectionFactory)
    {
        _source = source;
        _connectionFactory = connectionFactory;
    }

    public bool IsNeeded(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stamp = Stamp(path);
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            if (_observations.TryGetValue(path, out var observed) && observed.Stamp == stamp)
            {
                return observed.Needed;
            }

            bool needed;
            try
            {
                using var connection = _connectionFactory(path, true);
                connection.Open();
                needed = AdministrativeCandidateIndex.NeedsBuild(connection, _source, ct);
            }
            catch (DbException exception)
            {
                AdministrativeCandidateIndex.RethrowIfCritical(exception);
                ct.ThrowIfCancellationRequested();
                // The coordinated optional operation will report this limitation
                // once and retain the already verified original geodata.
                needed = true;
            }

            Remember(path, stamp, needed);
            return needed;
        }
    }

    public bool BuildFile(string path, CancellationToken ct)
    {
        try
        {
            using var connection = _connectionFactory(path, false);
            connection.Open();
            if (AdministrativeCandidateIndex.Build(connection, _source, ct))
            {
                return true;
            }
        }
        catch (DbException exception)
        {
            AdministrativeCandidateIndex.RethrowIfCritical(exception);
            ct.ThrowIfCancellationRequested();
        }

        // Only an owned unpublished candidate reaches this method. Removing the
        // marker first keeps incomplete auxiliary data ineligible even if cleanup
        // itself cannot finish. Authoritative source validation still runs later.
        try
        {
            using var cleanupConnection = _connectionFactory(path, false);
            cleanupConnection.Open();
            using var cleanup = cleanupConnection.CreateCommand();
            cleanup.CommandText = $"DELETE FROM _meta WHERE key='{AdministrativeCandidateIndex.VersionKey}' AND value LIKE '1:%'";
            ct.ThrowIfCancellationRequested();
            cleanup.ExecuteNonQuery();
        }
        catch (DbException exception)
        {
            AdministrativeCandidateIndex.RethrowIfCritical(exception);
            // The builder transaction never committed an incomplete index. The
            // source validator decides whether this candidate can be published.
        }

        ct.ThrowIfCancellationRequested();
        return false;
    }

    public async Task<PreparedCopy> PrepareCopyAsync(string sourcePath, string candidatePath, CancellationToken ct)
    {
        await CopyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stamp = Stamp(sourcePath);
            // Published service caches have no active journal. External mutable
            // caches remain readable via the legacy path rather than copying an
            // incomplete database snapshot or forcing a checkpoint.
            if (File.Exists(sourcePath + "-wal") || File.Exists(sourcePath + "-journal"))
            {
                throw new IOException("The source cache has an active journal.");
            }

            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 131072, useAsync: true))
            await using (var destination = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true))
            {
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
                await destination.FlushAsync(ct).ConfigureAwait(false);
            }

            if (stamp != Stamp(sourcePath) || !await Task.Run(() => BuildFile(candidatePath, ct), ct).ConfigureAwait(false))
            {
                throw new IOException("Optional candidate preparation could not be completed.");
            }

            ct.ThrowIfCancellationRequested();
            return new PreparedCopy(sourcePath, stamp);
        }
        catch
        {
            CopyGate.Release();
            throw;
        }
    }

    public void RememberReady(string path)
    {
        lock (_sync)
        {
            Remember(path, Stamp(path), false);
        }
    }

    public void RememberFailure(string path, string originalStamp)
    {
        lock (_sync)
        {
            if (File.Exists(path) && Stamp(path) == originalStamp)
            {
                Remember(path, originalStamp, false);
            }
        }
    }

    public static string Stamp(string path)
    {
        var file = new FileInfo(path);
        return $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{file.CreationTimeUtc.Ticks}";
    }

    private void Remember(string path, string stamp, bool needed)
    {
        if (!_observations.ContainsKey(path) && _observations.Count >= 512)
        {
            _observations.Remove(_observations.Keys.First());
        }

        _observations[path] = (stamp, needed);
    }

    public sealed class PreparedCopy : IDisposable
    {
        private readonly string _path;
        private readonly string _stamp;
        private int _disposed;

        internal PreparedCopy(string path, string stamp)
        {
            _path = path;
            _stamp = stamp;
        }

        public bool SourceIsCurrent => File.Exists(_path) && Stamp(_path) == _stamp;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                CopyGate.Release();
            }
        }
    }
}
