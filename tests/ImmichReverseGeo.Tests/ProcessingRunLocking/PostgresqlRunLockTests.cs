using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using NpgsqlTypes;

namespace ImmichReverseGeo.Tests.ProcessingRunLocking;

[TestClass]
[TestCategory("Change31")]
public sealed class PostgresqlRunLockTests
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void LockIdentity_DocumentedDerivation_MatchesVersionOneConstant()
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(ProcessingRunLockIdentity.DerivationLabel));

        Assert.AreEqual(ProcessingRunLockIdentity.Sha256, Convert.ToHexStringLower(digest));
        Assert.AreEqual(ProcessingRunLockIdentity.Key, BinaryPrimitives.ReadInt64BigEndian(digest));
    }

    [TestMethod]
    public void Commands_AdvisoryOperations_UseExactParameterizedBigintContract()
    {
        Assert.AreEqual("SELECT pg_try_advisory_lock($1)", ProcessingRunLockCommand.Acquire.CommandText);
        Assert.AreEqual(NpgsqlDbType.Bigint, ProcessingRunLockCommand.Acquire.ParameterType);
        Assert.AreEqual(ProcessingRunLockIdentity.Key, ProcessingRunLockCommand.Acquire.ParameterValue);
        Assert.AreEqual("SELECT pg_advisory_unlock($1)", ProcessingRunLockCommand.Release.CommandText);
        Assert.AreEqual(NpgsqlDbType.Bigint, ProcessingRunLockCommand.Release.ParameterType);
        Assert.AreEqual(ProcessingRunLockIdentity.Key, ProcessingRunLockCommand.Release.ParameterValue);
        Assert.AreEqual("SELECT 1", ProcessingRunLockCommand.Probe.CommandText);
        Assert.IsNull(ProcessingRunLockCommand.Probe.ParameterType);
        Assert.IsNull(ProcessingRunLockCommand.Probe.ParameterValue);
    }

    [TestMethod]
    public async Task AcquireAsync_AvailableLock_ReturnsLeaseWithoutDisposingOwningSession()
    {
        RecordingRunLockSession session = new();
        PostgresqlProcessingRunLock runLock = CreateRunLock(session);

        ProcessingRunLockAcquisition acquisition = await runLock.AcquireAsync(CancellationToken.None);

        ProcessingRunLockAcquisition.Acquired acquired = Assert.IsInstanceOfType<ProcessingRunLockAcquisition.Acquired>(acquisition);
        Assert.AreEqual(0, session.DisposeCalls);
        CollectionAssert.AreEqual(new[] { "open", "acquire" }, session.Calls.ToArray());

        ProcessingRunLockRelease release = await acquired.Lease.ReleaseAsync();
        Assert.IsFalse(release.InfrastructureFailure);
    }

    [TestMethod]
    public async Task AcquireAsync_LockUnavailable_ReturnsBusyAndDisposesWithoutPoolClear()
    {
        RecordingRunLockSession session = new()
        {
            ExecuteBehavior = static (_, _) => Task.FromResult<object?>(false)
        };

        ProcessingRunLockAcquisition acquisition = await CreateRunLock(session).AcquireAsync(CancellationToken.None);

        _ = Assert.IsInstanceOfType<ProcessingRunLockAcquisition.Busy>(acquisition);
        Assert.AreEqual(1, session.DisposeCalls);
        Assert.AreEqual(0, session.ClearPoolCalls);
        CollectionAssert.AreEqual(new[] { "open", "acquire", "dispose" }, session.Calls.ToArray());
    }

    [TestMethod]
    public async Task AcquireAsync_RequestCancelledDuringOpen_ReturnsCancelledAndDisposesNormally()
    {
        using CancellationTokenSource cancellation = new();
        RecordingRunLockSession session = new()
        {
            OpenBehavior = token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            }
        };

        ProcessingRunLockAcquisition acquisition = await CreateRunLock(session).AcquireAsync(cancellation.Token);

        _ = Assert.IsInstanceOfType<ProcessingRunLockAcquisition.Cancelled>(acquisition);
        Assert.AreEqual(0, session.Commands.Count);
        Assert.AreEqual(0, session.ClearPoolCalls);
        Assert.AreEqual(1, session.DisposeCalls);
    }

    [TestMethod]
    public async Task AcquireAsync_RequestCancelledAfterCommandDispatch_ClearsPoolBeforeDisposal()
    {
        using CancellationTokenSource cancellation = new();
        RecordingRunLockSession session = new()
        {
            ExecuteBehavior = (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<object?>(token);
            }
        };

        ProcessingRunLockAcquisition acquisition = await CreateRunLock(session).AcquireAsync(cancellation.Token);

        _ = Assert.IsInstanceOfType<ProcessingRunLockAcquisition.Cancelled>(acquisition);
        CollectionAssert.AreEqual(
            new[] { "open", "acquire", "clear-pool", "dispose" },
            session.Calls.ToArray());
    }

    [TestMethod]
    public async Task AcquireAsync_UnrelatedCancellationException_ReturnsInfrastructureAndSanitizes()
    {
        RecordingRunLockSession session = new()
        {
            ExecuteBehavior = static (_, _) => Task.FromException<object?>(new OperationCanceledException())
        };

        ProcessingRunLockAcquisition acquisition = await CreateRunLock(session).AcquireAsync(CancellationToken.None);

        _ = Assert.IsInstanceOfType<ProcessingRunLockAcquisition.InfrastructureFailure>(acquisition);
        CollectionAssert.AreEqual(
            new[] { "open", "acquire", "clear-pool", "dispose" },
            session.Calls.ToArray());
    }

    [TestMethod]
    public async Task AcquireAsync_UnexpectedScalar_ReturnsInfrastructureAndSanitizes()
    {
        RecordingRunLockSession session = new()
        {
            ExecuteBehavior = static (_, _) => Task.FromResult<object?>(DBNull.Value)
        };

        ProcessingRunLockAcquisition acquisition = await CreateRunLock(session).AcquireAsync(CancellationToken.None);

        _ = Assert.IsInstanceOfType<ProcessingRunLockAcquisition.InfrastructureFailure>(acquisition);
        CollectionAssert.AreEqual(
            new[] { "open", "acquire", "clear-pool", "dispose" },
            session.Calls.ToArray());
    }

    [TestMethod]
    public async Task AcquireAsync_CancelledOpenWithDisposalFailure_UpgradesToInfrastructureAndSanitizes()
    {
        using CancellationTokenSource cancellation = new();
        RecordingRunLockSession session = new()
        {
            OpenBehavior = token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            },
            DisposeBehavior = static () => Task.FromException(new InvalidOperationException("dispose failed"))
        };

        ProcessingRunLockAcquisition acquisition = await CreateRunLock(session).AcquireAsync(cancellation.Token);

        _ = Assert.IsInstanceOfType<ProcessingRunLockAcquisition.InfrastructureFailure>(acquisition);
        CollectionAssert.AreEqual(new[] { "open", "dispose", "clear-pool" }, session.Calls.ToArray());
    }

    [TestMethod]
    public async Task AcquireAsync_CancellationRacesSuccessfulScalar_UnlocksBeforeReturningCancelled()
    {
        using CancellationTokenSource cancellation = new();
        RecordingRunLockSession session = new()
        {
            ExecuteBehavior = (command, _) =>
            {
                if (command == ProcessingRunLockCommand.Acquire)
                {
                    cancellation.Cancel();
                }

                return Task.FromResult<object?>(true);
            }
        };

        ProcessingRunLockAcquisition acquisition = await CreateRunLock(session).AcquireAsync(cancellation.Token);

        _ = Assert.IsInstanceOfType<ProcessingRunLockAcquisition.Cancelled>(acquisition);
        CollectionAssert.AreEqual(
            new[] { "open", "acquire", "release", "dispose" },
            session.Calls.ToArray());
    }

    private static PostgresqlProcessingRunLock CreateRunLock(RecordingRunLockSession session) =>
        new(
            new RecordingRunLockSessionFactory(session),
            new ManualRunLockTimeProvider(),
            MonitorInterval,
            CleanupTimeout);
}
