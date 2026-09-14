using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Web.Services;

internal interface ICacheInventoryStorageScanner
{
    Task<CacheInventorySnapshot> ScanAsync(long generation, CancellationToken cancellationToken);

    Task<CacheInventoryExactResult> ScanExactAsync(
        CacheMutationSource source,
        string iso3,
        CancellationToken cancellationToken);
}

internal sealed class CacheInventoryService : ICacheInventory, ICacheInventoryInvalidator, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ICacheInventoryStorageScanner _scanner;
    private readonly SemaphoreSlim _physicalScanAdmission = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CacheInventorySnapshot? _snapshot;
    private Task<CacheInventorySnapshot>? _inFlight;
    private readonly HashSet<Task> _exactOperations = [];
    private long _generation;
    private bool _dirty = true;
    private bool _disposed;

    public CacheInventoryService(ICacheInventoryStorageScanner scanner)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    }

    public Task<CacheInventorySnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default) =>
        GetOrStartScanAsync(forceRefresh: false, cancellationToken);

    public Task<CacheInventorySnapshot> RefreshAsync(
        CancellationToken cancellationToken = default) =>
        GetOrStartScanAsync(forceRefresh: true, cancellationToken);

    public Task<CacheInventoryExactResult> GetExactAsync(
        CacheMutationSource source,
        string iso3,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        CacheMutationRequest.RequireCanonicalIso3(iso3, nameof(iso3));
        var completion = new TaskCompletionSource<CacheInventoryExactResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            _exactOperations.Add(completion.Task);
        }

        _ = RunExactAsync(source, iso3, completion);
        return completion.Task.WaitAsync(cancellationToken);
    }

    public void InvalidateKey(CacheMutationSource source, string iso3)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        CacheMutationRequest.RequireCanonicalIso3(iso3, nameof(iso3));
        Invalidate();
    }

    public void InvalidateSource(CacheMutationSource source)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        Invalidate();
    }

    public void InvalidateAll() => Invalidate();

    public async ValueTask DisposeAsync()
    {
        Task[] operations;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            operations = _exactOperations
                .Concat(_inFlight is null ? [] : [_inFlight])
                .ToArray();
        }

        _lifetimeCancellation.Cancel();
        if (operations.Length > 0)
        {
            try
            {
                await Task.WhenAll(operations).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _physicalScanAdmission.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private Task<CacheInventorySnapshot> GetOrStartScanAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ScanSelection selection = SelectScan(forceRefresh);
        StartScan(selection);
        return AwaitCurrentAsync(selection.Scan, cancellationToken);
    }

    private ScanSelection SelectScan(bool forceRefresh)
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_inFlight is not null)
            {
                return new ScanSelection(_inFlight, _generation, null);
            }

            if (!forceRefresh && !_dirty && _snapshot is not null)
            {
                return new ScanSelection(Task.FromResult(_snapshot), _generation, null);
            }

            if (forceRefresh && !_dirty)
            {
                _generation++;
            }

            _dirty = false;
            var owner = new TaskCompletionSource<CacheInventorySnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = owner.Task;
            return new ScanSelection(owner.Task, _generation, owner);
        }
    }

    private void StartScan(ScanSelection selection)
    {
        if (selection.Owner is not null)
        {
            _ = ScanAndPublishAsync(selection.Generation, selection.Owner);
        }
    }

    private async Task<CacheInventorySnapshot> AwaitCurrentAsync(
        Task<CacheInventorySnapshot> initialScan,
        CancellationToken cancellationToken)
    {
        Task<CacheInventorySnapshot> scan = initialScan;
        while (true)
        {
            CacheInventorySnapshot scanned = await scan
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (!_dirty
                    && _snapshot is not null
                    && _snapshot.Generation == _generation
                    && _snapshot.Generation >= scanned.Generation)
                {
                    return _snapshot;
                }
            }

            ScanSelection selection = SelectScan(forceRefresh: false);
            StartScan(selection);
            scan = selection.Scan;
        }
    }

    private async Task ScanAndPublishAsync(
        long scanGeneration,
        TaskCompletionSource<CacheInventorySnapshot> completion)
    {
        try
        {
            CacheInventorySnapshot scanned = await RunPhysicalInspectionAsync(
                cancellationToken => _scanner.ScanAsync(scanGeneration, cancellationToken))
                .ConfigureAwait(false);
            lock (_gate)
            {
                if (!_disposed && !_dirty && scanGeneration == _generation)
                {
                    _snapshot = scanned;
                }

                if (ReferenceEquals(_inFlight, completion.Task))
                {
                    _inFlight = null;
                }
            }

            completion.TrySetResult(scanned);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlight, completion.Task))
                {
                    _inFlight = null;
                }

                _dirty = true;
            }

            completion.TrySetCanceled(_lifetimeCancellation.Token);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlight, completion.Task))
                {
                    _inFlight = null;
                }

                _dirty = true;
            }

            completion.TrySetException(exception);
        }
    }

    private async Task RunExactAsync(
        CacheMutationSource source,
        string iso3,
        TaskCompletionSource<CacheInventoryExactResult> completion)
    {
        try
        {
            CacheInventoryExactResult result = await RunPhysicalInspectionAsync(
                cancellationToken => _scanner.ScanExactAsync(source, iso3, cancellationToken))
                .ConfigureAwait(false);
            lock (_gate)
            {
                _exactOperations.Remove(completion.Task);
            }

            completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            lock (_gate)
            {
                _exactOperations.Remove(completion.Task);
            }

            completion.TrySetCanceled(_lifetimeCancellation.Token);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _exactOperations.Remove(completion.Task);
            }

            completion.TrySetException(exception);
        }
    }

    private async Task<T> RunPhysicalInspectionAsync<T>(
        Func<CancellationToken, Task<T>> inspection)
    {
        await _physicalScanAdmission
            .WaitAsync(_lifetimeCancellation.Token)
            .ConfigureAwait(false);
        try
        {
            return await inspection(_lifetimeCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _physicalScanAdmission.Release();
        }
    }

    private void Invalidate()
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_inFlight is not null || !_dirty)
            {
                _generation++;
            }

            _dirty = true;
        }
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record ScanSelection(
        Task<CacheInventorySnapshot> Scan,
        long Generation,
        TaskCompletionSource<CacheInventorySnapshot>? Owner);
}
