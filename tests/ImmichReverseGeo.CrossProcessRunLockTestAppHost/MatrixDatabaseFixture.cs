using System.Data;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ImmichReverseGeo.CrossProcessRunLockTestAppHost;

// Closed test modes wrap the established session/output seams. Every successful
// acquisition and every pool-clear/dispose operation still reaches Npgsql/Postgres.
internal static class MatrixDatabaseFixture
{
    private static int _brokenOutput;
    internal static void BreakOutput() => Volatile.Write(ref _brokenOutput, 1);

    internal static void Configure(IServiceCollection services, CrossProcessRunLockAppHostOptions options)
    {
        if (options.Scenario == CrossProcessRunLockScenario.MatrixConnectFailure)
        {
            var unavailable = new NpgsqlConnectionStringBuilder(options.ConnectionString)
            {
                Database = "immich_reversegeo_test_missing_" + Guid.NewGuid().ToString("N")
            };
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(unavailable.ConnectionString));
        }
        if (options.Scenario == CrossProcessRunLockScenario.MatrixOutputFailure)
        {
            services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            services.AddSingleton<IWorkerNdjsonOutputStreamFactory, MatrixOutputFactory>();
        }
        if (options.Scenario is CrossProcessRunLockScenario.MatrixUnlockFalse
            or CrossProcessRunLockScenario.MatrixUnlockFailure or CrossProcessRunLockScenario.MatrixUnlockAmbiguous
            or CrossProcessRunLockScenario.MatrixDisposeFailure)
        {
            var pooled = new NpgsqlConnectionStringBuilder(options.ConnectionString)
            {
                Pooling = true,
                MinPoolSize = 0,
                MaxPoolSize = 4
            };
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(pooled.ConnectionString));
            services.RemoveAll<IProcessingRunLock>();
            services.AddSingleton<IProcessingRunLock>(sp => new PostgresqlProcessingRunLock(
                new MatrixSessionFactory(sp.GetRequiredService<NpgsqlDataSource>(), options), TimeProvider.System,
                PostgresqlProcessingRunLock.DefaultMonitorInterval, PostgresqlProcessingRunLock.DefaultCleanupTimeout));
        }
    }

    private sealed class MatrixSessionFactory(NpgsqlDataSource source, CrossProcessRunLockAppHostOptions options)
        : IProcessingRunLockSessionFactory
    {
        public IProcessingRunLockSession CreateSession()
        {
            var connection = source.CreateConnection();
            return new MatrixSession(source, connection, new NpgsqlProcessingRunLockSession(source, connection), options);
        }
    }

    private sealed class MatrixSession(NpgsqlDataSource source, NpgsqlConnection connection,
        IProcessingRunLockSession inner, CrossProcessRunLockAppHostOptions options) : IProcessingRunLockSession
    {
        private bool _cleared;
        private bool _disposed;
        private int _backend;
        public ConnectionState State => inner.State;
        public event StateChangeEventHandler? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public async ValueTask OpenAsync(CancellationToken cancellationToken)
        {
            await inner.OpenAsync(cancellationToken).ConfigureAwait(false);
            _backend = connection.ProcessID;
            Record("opened", _backend);
        }

        public async ValueTask<object?> ExecuteScalarAsync(ProcessingRunLockCommand command, CancellationToken cancellationToken)
        {
            if (command != ProcessingRunLockCommand.Release)
            {
                var value = await inner.ExecuteScalarAsync(command, cancellationToken).ConfigureAwait(false);
                Record(command == ProcessingRunLockCommand.Acquire ? "acquired" : "probe", _backend);
                return value;
            }

            Record("release", _backend);
            switch (options.Scenario)
            {
                case CrossProcessRunLockScenario.MatrixUnlockFailure:
                    throw new IOException("matrix-secret-unlock-failure");
                case CrossProcessRunLockScenario.MatrixUnlockFalse:
                    // Leave the actual lock held; false forces real pool quarantine
                    // before the physical connection is returned or disposed.
                    return false;
                case CrossProcessRunLockScenario.MatrixUnlockAmbiguous:
                    await inner.ExecuteScalarAsync(command, cancellationToken).ConfigureAwait(false);
                    return null;
                default:
                    return await inner.ExecuteScalarAsync(command, cancellationToken).ConfigureAwait(false);
            }
        }

        public void ClearPool()
        {
            inner.ClearPool();
            _cleared = true;
            Record("clear-pool", _backend);
            if (_disposed)
            {
                ProbeNewPhysicalSession();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            _disposed = true;
            Record("disposed", _backend);
            if (options.Scenario == CrossProcessRunLockScenario.MatrixDisposeFailure)
            {
                throw new IOException("matrix-secret-disposal-failure");
            }
            if (_cleared)
            {
                ProbeNewPhysicalSession();
            }
        }

        private void ProbeNewPhysicalSession()
        {
            // ClearPool is synchronous by contract. This fixed bounded probe runs
            // after disposing the lease, against the same real data source.
            using var next = source.OpenConnection();
            if (next.ProcessID == _backend)
            {
                throw new InvalidOperationException("The quarantined physical session was reused.");
            }
            Record("quarantine-probe", next.ProcessID);
        }

        private void Record(string phase, int backend) => File.AppendAllText(
            Path.Combine(options.ResourceRoot, "matrix-session.log"), phase + ":" + backend + "\n");
    }

    private sealed class MatrixOutputFactory : IWorkerNdjsonOutputStreamFactory
    {
        public Stream OpenStandardOutput() => new MatrixOutput(Console.OpenStandardOutput());
    }

    private sealed class MatrixOutput(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfBroken();
            inner.Write(buffer, offset, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfBroken();
            return inner.WriteAsync(buffer, cancellationToken);
        }
        private static void ThrowIfBroken()
        {
            if (Volatile.Read(ref _brokenOutput) != 0)
            {
                throw new IOException("matrix-secret-output-failure");
            }
        }
    }
}
