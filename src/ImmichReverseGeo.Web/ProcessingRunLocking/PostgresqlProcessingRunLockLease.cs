using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.ProcessingRunLocking;

internal sealed class PostgresqlProcessingRunLockLease : IProcessingRunLockLease
{
    private readonly TimeSpan _cleanupTimeout;
    private readonly object _releaseGate = new();
    private readonly TimeSpan _monitorInterval;
    private readonly CancellationTokenSource _monitorStop = new();
    private readonly Task _monitorTask;
    private readonly CancellationTokenSource _ownershipLost = new();
    private readonly IProcessingRunLockSession _session;
    private readonly TimeProvider _timeProvider;
    private int _isOwnershipLost;
    private Task<ProcessingRunLockRelease>? _releaseTask;

    internal PostgresqlProcessingRunLockLease(
        IProcessingRunLockSession session,
        TimeProvider timeProvider,
        TimeSpan monitorInterval,
        TimeSpan cleanupTimeout)
    {
        _session = session;
        _timeProvider = timeProvider;
        _monitorInterval = monitorInterval;
        _cleanupTimeout = cleanupTimeout;
        _session.StateChanged += HandleStateChanged;
        _monitorTask = MonitorOwnershipAsync();
    }

    public CancellationToken OwnershipLost => _ownershipLost.Token;

    public bool IsOwnershipLost => Volatile.Read(ref _isOwnershipLost) != 0;

    public Task<ProcessingRunLockRelease> ReleaseAsync()
    {
        lock (_releaseGate)
        {
            return _releaseTask ??= ReleaseCoreAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ = await ReleaseAsync().ConfigureAwait(false);
    }

    private async Task MonitorOwnershipAsync()
    {
        while (true)
        {
            try
            {
                await Task.Delay(_monitorInterval, _timeProvider, _monitorStop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_monitorStop.IsCancellationRequested)
            {
                return;
            }

            if (_session.State != ConnectionState.Open)
            {
                MarkOwnershipLost();
                return;
            }

            try
            {
                object? scalar = await _session.ExecuteScalarAsync(
                    ProcessingRunLockCommand.Probe,
                    _monitorStop.Token).ConfigureAwait(false);
                if (scalar is not int value || value != 1)
                {
                    MarkOwnershipLost();
                    return;
                }
            }
            catch (OperationCanceledException) when (_monitorStop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (!ProcessingRunLockSessionCleanup.IsFatal(exception))
            {
                MarkOwnershipLost();
                return;
            }
        }
    }

    private async Task<ProcessingRunLockRelease> ReleaseCoreAsync()
    {
        bool infrastructureFailure = IsOwnershipLost;
        bool monitorStopped = false;
        bool unlockConfirmed = false;
        using CancellationTokenSource cleanupTimeout = new(_cleanupTimeout, _timeProvider);

        _monitorStop.Cancel();
        try
        {
            await _monitorTask.WaitAsync(cleanupTimeout.Token).ConfigureAwait(false);
            monitorStopped = true;
        }
        catch (OperationCanceledException)
        {
            infrastructureFailure = true;
        }
        catch (Exception exception) when (!ProcessingRunLockSessionCleanup.IsFatal(exception))
        {
            MarkOwnershipLost();
            infrastructureFailure = true;
        }

        if (monitorStopped)
        {
            try
            {
                object? scalar = await _session.ExecuteScalarAsync(
                    ProcessingRunLockCommand.Release,
                    cleanupTimeout.Token).ConfigureAwait(false);
                unlockConfirmed = scalar is true;
                if (!unlockConfirmed)
                {
                    MarkOwnershipLost();
                    infrastructureFailure = true;
                }
            }
            catch (Exception exception) when (!ProcessingRunLockSessionCleanup.IsFatal(exception))
            {
                infrastructureFailure = true;
            }
        }

        _session.StateChanged -= HandleStateChanged;
        infrastructureFailure |= IsOwnershipLost;
        infrastructureFailure |= await ProcessingRunLockSessionCleanup.DisposeAsync(
            _session,
            clearPoolBeforeDisposal: !unlockConfirmed).ConfigureAwait(false);

        return new ProcessingRunLockRelease(infrastructureFailure);
    }

    private void HandleStateChanged(object sender, StateChangeEventArgs eventArgs)
    {
        if (eventArgs.CurrentState != ConnectionState.Open)
        {
            MarkOwnershipLost();
        }
    }

    private void MarkOwnershipLost()
    {
        if (Interlocked.Exchange(ref _isOwnershipLost, 1) == 0)
        {
            _ownershipLost.Cancel();
        }
    }
}
