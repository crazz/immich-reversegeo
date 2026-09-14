using System.Data;
using ImmichReverseGeo.Web.ProcessingRunLocking;

namespace ImmichReverseGeo.Tests.ProcessingRunLocking;

[TestClass]
[TestCategory("Change31")]
public sealed class PostgresqlRunLockLeaseTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task Monitor_AfterFiveSeconds_UsesParameterlessProbe()
    {
        TaskCompletionSource probed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingRunLockSession session = new()
        {
            ExecuteBehavior = (command, _) =>
            {
                if (command == ProcessingRunLockCommand.Probe)
                {
                    probed.TrySetResult();
                    return Task.FromResult<object?>(1);
                }

                return Task.FromResult<object?>(true);
            }
        };
        ManualRunLockTimeProvider timeProvider = new();
        PostgresqlProcessingRunLockLease lease = CreateLease(session, timeProvider);

        Assert.AreEqual(0, session.Commands.Count);
        timeProvider.Advance(MonitorInterval);
        await probed.Task.WaitAsync(Bound);

        ProcessingRunLockCommand probe = session.Commands.Single();
        Assert.AreEqual(ProcessingRunLockCommand.Probe, probe);
        Assert.IsNull(probe.ParameterType);
        Assert.IsNull(probe.ParameterValue);
        Assert.IsFalse(lease.IsOwnershipLost);
        _ = await lease.ReleaseAsync();
    }

    [TestMethod]
    public async Task ConnectionStateLoss_CancelsOwnershipTokenExactlyOnce()
    {
        RecordingRunLockSession session = new();
        ManualRunLockTimeProvider timeProvider = new();
        PostgresqlProcessingRunLockLease lease = CreateLease(session, timeProvider);
        int cancellationNotifications = 0;
        using CancellationTokenRegistration registration = lease.OwnershipLost.Register(
            () => Interlocked.Increment(ref cancellationNotifications));

        session.SetState(ConnectionState.Broken);
        session.SetState(ConnectionState.Closed);

        Assert.IsTrue(lease.IsOwnershipLost);
        Assert.IsTrue(lease.OwnershipLost.IsCancellationRequested);
        Assert.AreEqual(1, cancellationNotifications);
        ProcessingRunLockRelease release = await lease.ReleaseAsync();
        Assert.IsTrue(release.InfrastructureFailure);
    }

    [TestMethod]
    public async Task ProbeFailure_MarksOwnershipLostAndReleaseInfrastructureFailure()
    {
        TaskCompletionSource probeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingRunLockSession session = new()
        {
            ExecuteBehavior = (command, _) =>
            {
                if (command == ProcessingRunLockCommand.Probe)
                {
                    probeEntered.TrySetResult();
                    return Task.FromException<object?>(new InvalidOperationException("probe failed"));
                }

                return Task.FromResult<object?>(true);
            }
        };
        ManualRunLockTimeProvider timeProvider = new();
        PostgresqlProcessingRunLockLease lease = CreateLease(session, timeProvider);

        timeProvider.Advance(MonitorInterval);
        await probeEntered.Task.WaitAsync(Bound);
        await WaitForCancellationAsync(lease.OwnershipLost);

        Assert.IsTrue(lease.IsOwnershipLost);
        ProcessingRunLockRelease release = await lease.ReleaseAsync();
        Assert.IsTrue(release.InfrastructureFailure);
        Assert.AreEqual(1, session.DisposeCalls);
    }

    [TestMethod]
    public async Task ReleaseAsync_InFlightProbe_JoinsMonitorBeforeUnlockWithoutConcurrentCommands()
    {
        TaskCompletionSource probeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingRunLockSession session = new()
        {
            ExecuteBehavior = async (command, _) =>
            {
                if (command == ProcessingRunLockCommand.Probe)
                {
                    probeEntered.TrySetResult();
                    await releaseProbe.Task.ConfigureAwait(false);
                    return 1;
                }

                return true;
            }
        };
        ManualRunLockTimeProvider timeProvider = new();
        PostgresqlProcessingRunLockLease lease = CreateLease(session, timeProvider);

        timeProvider.Advance(MonitorInterval);
        await probeEntered.Task.WaitAsync(Bound);
        Task<ProcessingRunLockRelease> releasing = lease.ReleaseAsync();
        await Task.Yield();

        Assert.IsFalse(session.Calls.Contains("release"));
        Assert.AreEqual(1, session.MaxConcurrentCommands);
        releaseProbe.TrySetResult();

        ProcessingRunLockRelease release = await releasing.WaitAsync(Bound);
        Assert.IsFalse(release.InfrastructureFailure);
        Assert.AreEqual(1, session.MaxConcurrentCommands);
        CollectionAssert.AreEqual(new[] { "probe", "release", "dispose" }, session.Calls.ToArray());
    }

    [TestMethod]
    public async Task ReleaseAsync_TrueUnlock_IsOneSharedTaskAndDisposesExactlyOnce()
    {
        RecordingRunLockSession session = new();
        PostgresqlProcessingRunLockLease lease = CreateLease(session, new ManualRunLockTimeProvider());

        Task<ProcessingRunLockRelease> first = lease.ReleaseAsync();
        Task<ProcessingRunLockRelease> second = lease.ReleaseAsync();

        Assert.AreSame(first, second);
        ProcessingRunLockRelease release = await first;
        await lease.DisposeAsync();

        Assert.IsFalse(release.InfrastructureFailure);
        Assert.AreEqual(1, session.Commands.Count);
        Assert.AreEqual(ProcessingRunLockCommand.Release, session.Commands.Single());
        Assert.AreEqual(0, session.ClearPoolCalls);
        Assert.AreEqual(1, session.DisposeCalls);
        CollectionAssert.AreEqual(new[] { "release", "dispose" }, session.Calls.ToArray());
    }

    [TestMethod]
    public async Task ReleaseAsync_FalseUnlock_MarksInfrastructureAndClearsBeforeDisposal()
    {
        RecordingRunLockSession session = new()
        {
            ExecuteBehavior = static (_, _) => Task.FromResult<object?>(false)
        };
        PostgresqlProcessingRunLockLease lease = CreateLease(session, new ManualRunLockTimeProvider());

        ProcessingRunLockRelease release = await lease.ReleaseAsync();

        Assert.IsTrue(release.InfrastructureFailure);
        Assert.IsTrue(lease.IsOwnershipLost);
        Assert.AreEqual(1, session.DisposeCalls);
        CollectionAssert.AreEqual(new[] { "release", "clear-pool", "dispose" }, session.Calls.ToArray());
    }

    [TestMethod]
    public async Task ReleaseAsync_UnlockThrows_MarksInfrastructureAndClearsBeforeDisposal()
    {
        RecordingRunLockSession session = new()
        {
            ExecuteBehavior = static (_, _) =>
                Task.FromException<object?>(new InvalidOperationException("unlock failed"))
        };
        PostgresqlProcessingRunLockLease lease = CreateLease(session, new ManualRunLockTimeProvider());

        ProcessingRunLockRelease release = await lease.ReleaseAsync();

        Assert.IsTrue(release.InfrastructureFailure);
        Assert.AreEqual(1, session.DisposeCalls);
        CollectionAssert.AreEqual(new[] { "release", "clear-pool", "dispose" }, session.Calls.ToArray());
    }

    [TestMethod]
    public async Task ReleaseAsync_UnlockTimesOut_MarksInfrastructureAndClearsBeforeDisposal()
    {
        TaskCompletionSource unlockEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingRunLockSession session = new()
        {
            ExecuteBehavior = async (command, token) =>
            {
                Assert.AreEqual(ProcessingRunLockCommand.Release, command);
                unlockEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return true;
            }
        };
        ManualRunLockTimeProvider timeProvider = new();
        PostgresqlProcessingRunLockLease lease = CreateLease(session, timeProvider);

        Task<ProcessingRunLockRelease> releasing = lease.ReleaseAsync();
        await unlockEntered.Task.WaitAsync(Bound);
        timeProvider.Advance(CleanupTimeout);
        ProcessingRunLockRelease release = await releasing.WaitAsync(Bound);

        Assert.IsTrue(release.InfrastructureFailure);
        Assert.AreEqual(1, session.DisposeCalls);
        CollectionAssert.AreEqual(new[] { "release", "clear-pool", "dispose" }, session.Calls.ToArray());
    }

    [TestMethod]
    public async Task ReleaseAsync_DisposalThrows_MarksInfrastructureAndSanitizesAfterFailure()
    {
        RecordingRunLockSession session = new()
        {
            DisposeBehavior = static () => Task.FromException(new InvalidOperationException("dispose failed"))
        };
        PostgresqlProcessingRunLockLease lease = CreateLease(session, new ManualRunLockTimeProvider());

        ProcessingRunLockRelease release = await lease.ReleaseAsync();

        Assert.IsTrue(release.InfrastructureFailure);
        Assert.AreEqual(1, session.DisposeCalls);
        Assert.AreEqual(1, session.ClearPoolCalls);
        CollectionAssert.AreEqual(new[] { "release", "dispose", "clear-pool" }, session.Calls.ToArray());
    }

    [TestMethod]
    public async Task ReleaseAsync_MonitorJoinTimesOut_DoesNotRunUnlockConcurrently()
    {
        TaskCompletionSource probeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingRunLockSession session = new()
        {
            ExecuteBehavior = async (command, _) =>
            {
                Assert.AreEqual(ProcessingRunLockCommand.Probe, command);
                probeEntered.TrySetResult();
                await releaseProbe.Task.ConfigureAwait(false);
                return 1;
            }
        };
        ManualRunLockTimeProvider timeProvider = new();
        PostgresqlProcessingRunLockLease lease = CreateLease(session, timeProvider);

        timeProvider.Advance(MonitorInterval);
        await probeEntered.Task.WaitAsync(Bound);
        Task<ProcessingRunLockRelease> releasing = lease.ReleaseAsync();
        timeProvider.Advance(CleanupTimeout);
        ProcessingRunLockRelease release = await releasing.WaitAsync(Bound);

        Assert.IsTrue(release.InfrastructureFailure);
        Assert.IsFalse(session.Calls.Contains("release"));
        CollectionAssert.AreEqual(new[] { "probe", "clear-pool", "dispose" }, session.Calls.ToArray());
        releaseProbe.TrySetResult();
    }

    private static PostgresqlProcessingRunLockLease CreateLease(
        RecordingRunLockSession session,
        ManualRunLockTimeProvider timeProvider)
    {
        session.SetState(ConnectionState.Open);
        return new PostgresqlProcessingRunLockLease(
            session,
            timeProvider,
            MonitorInterval,
            CleanupTimeout);
    }

    private static Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(cancelled.SetResult);
        return cancelled.Task.WaitAsync(Bound);
    }
}
