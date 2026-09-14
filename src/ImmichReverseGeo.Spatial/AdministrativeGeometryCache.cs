using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetTopologySuite.Geometries;

namespace ImmichReverseGeo.Spatial;

/// <summary>
/// Owns bounded geometry reuse for one heavy invocation. Providers retain their
/// SQL and result selection; loaders and points never become retained cache state.
/// </summary>
public sealed class AdministrativeGeometryCache : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<(GeometrySource, string), GeometryGeneration> _generations = new();
    private readonly Dictionary<Key, Entry> _entries = new();
    private readonly Dictionary<Key, TaskCompletionSource<bool>> _pending = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly long _budget;
    private long _accounted;
    private long _hits;
    private long _blobLoads;
    private long _preparations;
    private long _evictions;
    private long _unretained;
    private int _operations;
    private int _waiting;
    private bool _disposed;

    public AdministrativeGeometryCache() : this(SpatialMemoryPolicy.DefaultBudget)
    {
    }

    public AdministrativeGeometryCache(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budget = budgetBytes;
    }

    public GeometryGeneration ObserveGeneration(GeometrySource source, string country, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        var file = new FileInfo(filePath);
        var stamp = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{file.CreationTimeUtc.Ticks}";
        var key = (source, country.ToUpperInvariant());
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_generations.TryGetValue(key, out var current))
            {
                if (current.Stamp == stamp)
                {
                    return current;
                }

                RetireGeneration(current);
            }

            // Generation metadata must also stay bounded, even for unusual inputs.
            if (_generations.Count >= 512)
            {
                RetireGeneration(_generations.Values.First());
            }

            var generation = new GeometryGeneration(this, source, key.Item2, stamp);
            _generations[key] = generation;
            return generation;
        }
    }

    public bool Covers(GeometryGeneration generation, string areaId, long blobLength,
        Func<byte[]> loadGeometry, Point point, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(loadGeometry);
        ArgumentNullException.ThrowIfNull(point);
        if (!ReferenceEquals(generation.Owner, this))
        {
            throw new ArgumentException("Generation belongs to another spatial cache.", nameof(generation));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _operations++;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            return Evaluate(new Key(generation, areaId), blobLength, loadGeometry, point, linked.Token);
        }
        finally
        {
            lock (_sync)
            {
                _operations--;
                Monitor.PulseAll(_sync);
            }
        }
    }

    public SpatialCacheStatistics GetStatistics()
    {
        lock (_sync)
        {
            return new SpatialCacheStatistics(_budget, _accounted, _entries.Count, _pending.Count, _waiting,
                _hits, _blobLoads, _preparations, _evictions, _unretained);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _shutdown.Cancel();
        lock (_sync)
        {
            while (_operations != 0)
            {
                Monitor.Wait(_sync);
            }

            foreach (var entry in _entries.Values.ToArray())
            {
                RetireEntry(entry);
            }

            foreach (var generation in _generations.Values)
            {
                generation.Retired = true;
            }

            _generations.Clear();
        }

        _buildGate.Dispose();
        _shutdown.Dispose();
    }

    private bool Evaluate(Key key, long blobLength, Func<byte[]> loader, Point point, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Entry? hit;
            TaskCompletionSource<bool>? work = null;
            var owner = false;
            lock (_sync)
            {
                hit = AcquireHit(key);
                if (hit is null && !_pending.TryGetValue(key, out work))
                {
                    work = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pending.Add(key, work);
                    owner = true;
                }
            }

            if (hit is not null)
            {
                return EvaluateLease(hit, point, ct);
            }

            if (owner)
            {
                return PrepareAndEvaluate(key, blobLength, loader, point, ct, work!);
            }

            // A waiter's cancellation never removes or cancels the owner's task.
            Interlocked.Increment(ref _waiting);
            try
            {
                work!.Task.WaitAsync(ct).GetAwaiter().GetResult();
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }
    }

    private bool PrepareAndEvaluate(Key key, long blobLength, Func<byte[]> loader, Point point,
        CancellationToken ct, TaskCompletionSource<bool> work)
    {
        Entry? entry = null;
        try
        {
            bool fallbackResult = false;
            _buildGate.Wait(ct);
            try
            {
                var reservation = Reserve(key, blobLength, out var retainedBytes);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref _blobLoads);
                    var bytes = loader();
                    // A metadata/blob mismatch must never bypass admission.
                    var retain = reservation > 0 && bytes.LongLength == blobLength;
                    var geometry = AdministrativeGeometry.Read(bytes, retain, ct);
                    if (retain)
                    {
                        lock (_sync)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!key.Generation.Retired)
                            {
                                // Allocate before changing either collection. Once
                                // inserted, linking the existing node cannot allocate.
                                var prepared = new Entry(key, geometry, retainedBytes);
                                prepared.Node = new LinkedListNode<Entry>(prepared);
                                _entries.Add(key, prepared);
                                _lru.AddLast(prepared.Node);
                                _accounted -= reservation - retainedBytes;
                                reservation = 0;
                                entry = prepared;
                                _preparations++;
                            }
                        }
                    }

                    if (entry is null)
                    {
                        Interlocked.Increment(ref _unretained);
                        fallbackResult = geometry.Covers(point, ct);
                    }
                }
                finally
                {
                    lock (_sync)
                    {
                        _accounted -= reservation;
                    }
                }
            }
            finally
            {
                _buildGate.Release();
            }

            CompletePreparation(key, work, null);
            return entry is not null ? entry.Geometry.Covers(point, ct) : fallbackResult;
        }
        catch (Exception error)
        {
            CompletePreparation(key, work, error);
            throw;
        }
        finally
        {
            // The owner's lease also covers publication/notification failures,
            // not just exceptions raised by the subsequent predicate.
            if (entry is not null)
            {
                ReleaseLease(entry);
            }
        }
    }

    private long Reserve(Key key, long blobLength, out long retainedBytes)
    {
        var estimated = SpatialMemoryPolicy.TryEstimate(blobLength, out retainedBytes, out var temporaryBytes);
        lock (_sync)
        {
            var needed = estimated ? retainedBytes + temporaryBytes : long.MaxValue;
            var node = _lru.First;
            while (node is not null && (!estimated || needed > _budget - _accounted))
            {
                var next = node.Next;
                if (node.Value.Users == 0)
                {
                    RetireEntry(node.Value);
                }

                node = next;
            }

            if (!estimated || key.Generation.Retired || needed > _budget - _accounted)
            {
                return 0;
            }

            _accounted += needed;
            return needed;
        }
    }

    private Entry? AcquireHit(Key key)
    {
        if (key.Generation.Retired || !_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        entry.Users++;
        _hits++;
        _lru.Remove(entry.Node!);
        _lru.AddLast(entry.Node!);
        return entry;
    }

    private bool EvaluateLease(Entry entry, Point point, CancellationToken ct)
    {
        try
        {
            return entry.Geometry.Covers(point, ct);
        }
        finally
        {
            ReleaseLease(entry);
        }
    }

    private void ReleaseLease(Entry entry)
    {
        lock (_sync)
        {
            entry.Users--;
            if (entry.Users == 0 && entry.Retired)
            {
                _accounted -= entry.Bytes;
            }
        }
    }

    private void CompletePreparation(Key key, TaskCompletionSource<bool> work, Exception? error)
    {
        lock (_sync)
        {
            if (!_pending.TryGetValue(key, out var current) || !ReferenceEquals(current, work))
            {
                return;
            }

            _pending.Remove(key);
            if (error is null)
            {
                work.TrySetResult(true);
            }
            else
            {
                work.TrySetException(error);
                // The owner rethrows directly; observe its task even with no waiters.
                _ = work.Task.Exception;
            }
        }
    }

    private void RetireGeneration(GeometryGeneration generation)
    {
        generation.Retired = true;
        _generations.Remove((generation.Source, generation.Country));
        foreach (var entry in _entries.Values.Where(e => ReferenceEquals(e.Key.Generation, generation)).ToArray())
        {
            RetireEntry(entry);
        }
    }

    private void RetireEntry(Entry entry)
    {
        _entries.Remove(entry.Key);
        _lru.Remove(entry.Node!);
        entry.Retired = true;
        _evictions++;
        if (entry.Users == 0)
        {
            _accounted -= entry.Bytes;
        }
    }

    private readonly record struct Key(GeometryGeneration Generation, string AreaId);

    private sealed class Entry(Key key, AdministrativeGeometry geometry, long bytes)
    {
        internal Key Key { get; } = key;
        internal AdministrativeGeometry Geometry { get; } = geometry;
        internal long Bytes { get; } = bytes;
        internal int Users { get; set; } = 1;
        internal bool Retired { get; set; }
        internal LinkedListNode<Entry>? Node { get; set; }
    }
}
