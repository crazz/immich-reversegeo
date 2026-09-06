using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace ImmichReverseGeo.Web.ProcessingRunLocking;

internal interface IProcessingRunLockSessionFactory
{
    IProcessingRunLockSession CreateSession();
}

internal interface IProcessingRunLockSession : IAsyncDisposable
{
    ConnectionState State { get; }

    event StateChangeEventHandler? StateChanged;

    ValueTask OpenAsync(CancellationToken cancellationToken);

    ValueTask<object?> ExecuteScalarAsync(
        ProcessingRunLockCommand command,
        CancellationToken cancellationToken);

    void ClearPool();
}

internal sealed class NpgsqlProcessingRunLockSessionFactory(NpgsqlDataSource dataSource)
    : IProcessingRunLockSessionFactory
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public IProcessingRunLockSession CreateSession() =>
        new NpgsqlProcessingRunLockSession(_dataSource, _dataSource.CreateConnection());
}

internal sealed class NpgsqlProcessingRunLockSession(
    NpgsqlDataSource dataSource,
    NpgsqlConnection connection)
    : IProcessingRunLockSession
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly NpgsqlConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public ConnectionState State => _connection.State;

    public event StateChangeEventHandler? StateChanged
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
        await using NpgsqlCommand npgsqlCommand = new(command.CommandText, _connection);
        if (command.ParameterType is NpgsqlDbType parameterType)
        {
            NpgsqlParameter parameter = new()
            {
                NpgsqlDbType = parameterType,
                Value = command.ParameterValue ?? DBNull.Value
            };
            npgsqlCommand.Parameters.Add(parameter);
        }
        else if (command.ParameterValue is not null)
        {
            throw new InvalidOperationException("A lock command value requires an explicit parameter type.");
        }

        return await npgsqlCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public void ClearPool() => _dataSource.Clear();

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
