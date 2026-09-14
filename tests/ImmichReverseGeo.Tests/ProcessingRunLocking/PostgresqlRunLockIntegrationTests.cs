using ImmichReverseGeo.Web.ProcessingRunLocking;
using Npgsql;
using NpgsqlTypes;

namespace ImmichReverseGeo.Tests.ProcessingRunLocking;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Change31")]
[DoNotParallelize]
public sealed class PostgresqlRunLockIntegrationTests
{
    private const long AdvisoryLockKey = -7970420658158250032L;
    private static readonly TimeSpan _cleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly List<IAsyncDisposable> _dataSources = [];

    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public async Task CleanupAsync()
    {
        List<Exception> failures = [];

        for (int index = _dataSources.Count - 1; index >= 0; index--)
        {
            try
            {
                await _dataSources[index].DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        _dataSources.Clear();

        if (failures.Count != 0)
        {
            throw new AggregateException(failures);
        }
    }

    [TestMethod]
    public async Task AcquireAsync_WhenIndependentSessionAlreadyOwnsKey_ReturnsBusyAsync()
    {
        NpgsqlDataSource ownerDataSource = CreatePooledDataSource();
        NpgsqlDataSource contenderDataSource = CreatePooledDataSource();
        PostgresqlProcessingRunLock ownerLock = new(ownerDataSource, TimeProvider.System);
        PostgresqlProcessingRunLock contenderLock = new(contenderDataSource, TimeProvider.System);

        IProcessingRunLockLease ownerLease = GetAcquiredLease(
            await ownerLock.AcquireAsync(TestContext.CancellationToken));
        try
        {
            ProcessingRunLockAcquisition contender = await contenderLock.AcquireAsync(TestContext.CancellationToken);

            Assert.IsTrue(contender is ProcessingRunLockAcquisition.Busy);
        }
        finally
        {
            await ownerLease.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ReleaseAsync_WhenExplicitUnlockSucceeds_AllowsIndependentSessionToAcquireAsync()
    {
        NpgsqlDataSource ownerDataSource = CreatePooledDataSource();
        NpgsqlDataSource contenderDataSource = CreatePooledDataSource();
        PostgresqlProcessingRunLock ownerLock = new(ownerDataSource, TimeProvider.System);
        PostgresqlProcessingRunLock contenderLock = new(contenderDataSource, TimeProvider.System);

        IProcessingRunLockLease ownerLease = GetAcquiredLease(
            await ownerLock.AcquireAsync(TestContext.CancellationToken));
        bool ownerLeaseReleased = false;
        try
        {
            ProcessingRunLockRelease release = await ownerLease.ReleaseAsync();
            ownerLeaseReleased = true;

            Assert.IsFalse(release.InfrastructureFailure);

            IProcessingRunLockLease contenderLease = GetAcquiredLease(
                await contenderLock.AcquireAsync(TestContext.CancellationToken));
            try
            {
                Assert.IsFalse(contenderLease.IsOwnershipLost);
            }
            finally
            {
                await contenderLease.DisposeAsync();
            }
        }
        finally
        {
            if (!ownerLeaseReleased)
            {
                await ownerLease.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task OwnerPhysicalSessionClose_ReleasesAdvisoryLockAsync()
    {
        string connectionString = CreateUnpooledConnectionString();
        using CancellationTokenSource cleanupTimeout = new(_cleanupTimeout, TimeProvider.System);
        NpgsqlConnection ownerConnection = new(connectionString);
        await using NpgsqlConnection waiterConnection = new(connectionString);
        Task? waiterTask = null;
        bool ownerDisposed = false;
        bool waiterOwnsLock = false;

        try
        {
            await ownerConnection.OpenAsync(TestContext.CancellationToken);
            await waiterConnection.OpenAsync(TestContext.CancellationToken);
            Assert.IsTrue(await TryAcquireAsync(ownerConnection, TestContext.CancellationToken));
            Assert.IsFalse(await TryAcquireAsync(waiterConnection, TestContext.CancellationToken));

            waiterTask = AcquireBlockingAsync(waiterConnection, cleanupTimeout.Token);
            await ownerConnection.DisposeAsync();
            ownerDisposed = true;

            await waiterTask.WaitAsync(cleanupTimeout.Token);
            waiterOwnsLock = true;
        }
        finally
        {
            try
            {
                if (!ownerDisposed)
                {
                    await ownerConnection.DisposeAsync();
                }
            }
            finally
            {
                if (waiterOwnsLock)
                {
                    Assert.IsTrue(await ReleaseAsync(waiterConnection, cleanupTimeout.Token));
                }
                else if (waiterTask is not null)
                {
                    cleanupTimeout.Cancel();
                    try
                    {
                        await waiterTask;
                    }
                    catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
                    {
                    }
                }
            }
        }
    }

    [TestMethod]
    public async Task NormalPooledLeaseRelease_LeavesKeyAvailableToIndependentDataSourceAsync()
    {
        NpgsqlDataSource ownerDataSource = CreatePooledDataSource();
        PostgresqlProcessingRunLock ownerLock = new(ownerDataSource, TimeProvider.System);

        IProcessingRunLockLease ownerLease = GetAcquiredLease(
            await ownerLock.AcquireAsync(TestContext.CancellationToken));
        bool ownerLeaseReleased = false;
        try
        {
            ProcessingRunLockRelease release = await ownerLease.ReleaseAsync();
            ownerLeaseReleased = true;

            Assert.IsFalse(release.InfrastructureFailure);

            await using NpgsqlConnection independentConnection = new(CreateUnpooledConnectionString());
            await independentConnection.OpenAsync(TestContext.CancellationToken);
            bool independentAcquired = await TryAcquireAsync(
                independentConnection,
                TestContext.CancellationToken);
            try
            {
                Assert.IsTrue(independentAcquired);
            }
            finally
            {
                if (independentAcquired)
                {
                    Assert.IsTrue(await ReleaseAsync(independentConnection, TestContext.CancellationToken));
                }
            }
        }
        finally
        {
            if (!ownerLeaseReleased)
            {
                await ownerLease.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public Task ReleaseAsync_WhenUnlockReturnsFalse_ClearsPooledSessionBeforeDisposalAsync() =>
        AssertAmbiguousUnlockClearsPooledSessionAsync(throwFromUnlock: false);

    [TestMethod]
    public Task ReleaseAsync_WhenUnlockThrows_ClearsPooledSessionBeforeDisposalAsync() =>
        AssertAmbiguousUnlockClearsPooledSessionAsync(throwFromUnlock: true);

    private async Task AssertAmbiguousUnlockClearsPooledSessionAsync(bool throwFromUnlock)
    {
        NpgsqlDataSource ownerDataSource = CreatePooledDataSource(noResetOnClose: true);
        FaultingReleaseSessionFactory factory = new(ownerDataSource, throwFromUnlock);
        PostgresqlProcessingRunLock runLock = new(
            factory,
            TimeProvider.System,
            TimeSpan.FromDays(1),
            _cleanupTimeout);
        using CancellationTokenSource cleanupTimeout = new(_cleanupTimeout, TimeProvider.System);
        await using NpgsqlConnection waiterConnection = new(CreateUnpooledConnectionString());
        Task? waiterTask = null;
        IProcessingRunLockLease? lease = null;
        bool waiterOwnsLock = false;

        try
        {
            lease = GetAcquiredLease(await runLock.AcquireAsync(TestContext.CancellationToken));
            await waiterConnection.OpenAsync(TestContext.CancellationToken);
            Assert.IsFalse(await TryAcquireAsync(waiterConnection, TestContext.CancellationToken));

            waiterTask = AcquireBlockingAsync(waiterConnection, cleanupTimeout.Token);
            ProcessingRunLockRelease release = await lease.ReleaseAsync();

            Assert.IsTrue(release.InfrastructureFailure);
            Assert.IsNotNull(factory.Session);
            Assert.IsTrue(factory.Session.WasPoolCleared);

            await waiterTask.WaitAsync(cleanupTimeout.Token);
            waiterOwnsLock = true;
        }
        finally
        {
            try
            {
                if (waiterOwnsLock)
                {
                    Assert.IsTrue(await ReleaseAsync(waiterConnection, cleanupTimeout.Token));
                }
                else if (waiterTask is not null)
                {
                    cleanupTimeout.Cancel();
                    try
                    {
                        await waiterTask;
                    }
                    catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
                    {
                    }
                }
            }
            finally
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }
            }
        }
    }

    private static IProcessingRunLockLease GetAcquiredLease(ProcessingRunLockAcquisition acquisition)
    {
        Assert.IsTrue(acquisition is ProcessingRunLockAcquisition.Acquired);
        return ((ProcessingRunLockAcquisition.Acquired)acquisition).Lease;
    }

    private NpgsqlDataSource CreatePooledDataSource(bool noResetOnClose = false)
    {
        NpgsqlConnectionStringBuilder builder = new(GetRequiredConnectionString())
        {
            ApplicationName = $"ImmichReverseGeo.Change31.{Guid.NewGuid():N}",
            Pooling = true,
            MaxPoolSize = 1,
            NoResetOnClose = noResetOnClose
        };

        NpgsqlDataSource dataSource = new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
        _dataSources.Add(dataSource);
        return dataSource;
    }

    private static string CreateUnpooledConnectionString()
    {
        NpgsqlConnectionStringBuilder builder = new(GetRequiredConnectionString())
        {
            ApplicationName = $"ImmichReverseGeo.Change31.{Guid.NewGuid():N}",
            Pooling = false
        };

        return builder.ConnectionString;
    }

    private static string GetRequiredConnectionString()
    {
        string? connectionString = Environment.GetEnvironmentVariable(
            "IMMICH_REVERSEGEO_TEST_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive(
                "Set IMMICH_REVERSEGEO_TEST_POSTGRES_CONNECTION_STRING to run Change31 PostgreSQL integration tests.");
        }

        return connectionString;
    }

    private static async Task<bool> TryAcquireAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new("SELECT pg_try_advisory_lock($1)", connection);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Bigint,
            Value = AdvisoryLockKey
        });

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<bool> ReleaseAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new("SELECT pg_advisory_unlock($1)", connection);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Bigint,
            Value = AdvisoryLockKey
        });

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task AcquireBlockingAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new("SELECT pg_advisory_lock($1)", connection);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Bigint,
            Value = AdvisoryLockKey
        });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class FaultingReleaseSessionFactory(NpgsqlDataSource dataSource, bool throwFromUnlock)
        : IProcessingRunLockSessionFactory
    {
        private readonly NpgsqlDataSource _dataSource = dataSource;
        private readonly bool _throwFromUnlock = throwFromUnlock;

        public FaultingReleaseSession? Session { get; private set; }

        public IProcessingRunLockSession CreateSession()
        {
            Session = new FaultingReleaseSession(_dataSource, _dataSource.CreateConnection(), _throwFromUnlock);
            return Session;
        }
    }

    private sealed class FaultingReleaseSession(
        NpgsqlDataSource dataSource,
        NpgsqlConnection connection,
        bool throwFromUnlock)
        : IProcessingRunLockSession
    {
        private readonly NpgsqlDataSource _dataSource = dataSource;
        private readonly NpgsqlConnection _connection = connection;
        private readonly bool _throwFromUnlock = throwFromUnlock;

        public System.Data.ConnectionState State => _connection.State;

        public bool WasPoolCleared { get; private set; }

        public event System.Data.StateChangeEventHandler? StateChanged
        {
            add => _connection.StateChange += value;
            remove => _connection.StateChange -= value;
        }

        public ValueTask OpenAsync(CancellationToken cancellationToken) =>
            new(_connection.OpenAsync(cancellationToken));

        public async ValueTask<object?> ExecuteScalarAsync(
            ProcessingRunLockCommand command,
            CancellationToken cancellationToken)
        {
            if (command == ProcessingRunLockCommand.Release)
            {
                if (_throwFromUnlock)
                {
                    throw new InvalidOperationException("Synthetic unlock failure.");
                }

                return false;
            }

            await using NpgsqlCommand npgsqlCommand = new(command.CommandText, _connection);
            if (command.ParameterType is NpgsqlDbType parameterType)
            {
                npgsqlCommand.Parameters.Add(new NpgsqlParameter
                {
                    NpgsqlDbType = parameterType,
                    Value = command.ParameterValue ?? DBNull.Value
                });
            }

            return await npgsqlCommand.ExecuteScalarAsync(cancellationToken);
        }

        public void ClearPool()
        {
            WasPoolCleared = true;
            _dataSource.Clear();
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
