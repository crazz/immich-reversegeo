using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace ImmichReverseGeo.Web.ProcessingRunLocking;

internal sealed class PostgresqlProcessingRunLock : IProcessingRunLock
{
    internal static readonly TimeSpan DefaultMonitorInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _cleanupTimeout;
    private readonly IProcessingRunLockSessionFactory _sessionFactory;
    private readonly TimeSpan _monitorInterval;
    private readonly TimeProvider _timeProvider;

    internal PostgresqlProcessingRunLock(NpgsqlDataSource dataSource, TimeProvider timeProvider)
        : this(
            new NpgsqlProcessingRunLockSessionFactory(dataSource),
            timeProvider,
            DefaultMonitorInterval,
            DefaultCleanupTimeout)
    {
    }

    internal PostgresqlProcessingRunLock(
        IProcessingRunLockSessionFactory sessionFactory,
        TimeProvider timeProvider,
        TimeSpan monitorInterval,
        TimeSpan cleanupTimeout)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(monitorInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cleanupTimeout, TimeSpan.Zero);

        _sessionFactory = sessionFactory;
        _timeProvider = timeProvider;
        _monitorInterval = monitorInterval;
        _cleanupTimeout = cleanupTimeout;
    }

    public async ValueTask<ProcessingRunLockAcquisition> AcquireAsync(CancellationToken cancellationToken)
    {
        IProcessingRunLockSession session;
        try
        {
            session = _sessionFactory.CreateSession();
        }
        catch (Exception exception) when (!ProcessingRunLockSessionCleanup.IsFatal(exception))
        {
            return new ProcessingRunLockAcquisition.InfrastructureFailure();
        }

        try
        {
            await session.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CompleteWithoutLeaseAsync(
                session,
                new ProcessingRunLockAcquisition.Cancelled(),
                clearPoolBeforeDisposal: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (!ProcessingRunLockSessionCleanup.IsFatal(exception))
        {
            return await CompleteWithoutLeaseAsync(
                session,
                new ProcessingRunLockAcquisition.InfrastructureFailure(),
                clearPoolBeforeDisposal: false).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return await CompleteWithoutLeaseAsync(
                session,
                new ProcessingRunLockAcquisition.Cancelled(),
                clearPoolBeforeDisposal: false).ConfigureAwait(false);
        }

        object? scalar;
        try
        {
            scalar = await session.ExecuteScalarAsync(
                ProcessingRunLockCommand.Acquire,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CompleteWithoutLeaseAsync(
                session,
                new ProcessingRunLockAcquisition.Cancelled(),
                clearPoolBeforeDisposal: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (!ProcessingRunLockSessionCleanup.IsFatal(exception))
        {
            return await CompleteWithoutLeaseAsync(
                session,
                new ProcessingRunLockAcquisition.InfrastructureFailure(),
                clearPoolBeforeDisposal: true).ConfigureAwait(false);
        }

        if (scalar is not bool acquired)
        {
            return await CompleteWithoutLeaseAsync(
                session,
                new ProcessingRunLockAcquisition.InfrastructureFailure(),
                clearPoolBeforeDisposal: true).ConfigureAwait(false);
        }

        if (!acquired)
        {
            ProcessingRunLockAcquisition outcome = cancellationToken.IsCancellationRequested
                ? new ProcessingRunLockAcquisition.Cancelled()
                : new ProcessingRunLockAcquisition.Busy();
            return await CompleteWithoutLeaseAsync(
                session,
                outcome,
                clearPoolBeforeDisposal: false).ConfigureAwait(false);
        }

        PostgresqlProcessingRunLockLease lease = new(
            session,
            _timeProvider,
            _monitorInterval,
            _cleanupTimeout);
        if (!cancellationToken.IsCancellationRequested)
        {
            return new ProcessingRunLockAcquisition.Acquired(lease);
        }

        ProcessingRunLockRelease release = await lease.ReleaseAsync().ConfigureAwait(false);
        return release.InfrastructureFailure
            ? new ProcessingRunLockAcquisition.InfrastructureFailure()
            : new ProcessingRunLockAcquisition.Cancelled();
    }

    private static async ValueTask<ProcessingRunLockAcquisition> CompleteWithoutLeaseAsync(
        IProcessingRunLockSession session,
        ProcessingRunLockAcquisition outcome,
        bool clearPoolBeforeDisposal)
    {
        bool cleanupFailed = await ProcessingRunLockSessionCleanup.DisposeAsync(
            session,
            clearPoolBeforeDisposal).ConfigureAwait(false);
        return cleanupFailed
            ? new ProcessingRunLockAcquisition.InfrastructureFailure()
            : outcome;
    }
}
