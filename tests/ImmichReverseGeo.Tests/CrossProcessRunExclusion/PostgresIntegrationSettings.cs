using Npgsql;

namespace ImmichReverseGeo.Tests.CrossProcessRunExclusion;

internal abstract record PostgresIntegrationSettingsResult;

internal sealed record PostgresIntegrationSettingsAvailable(PostgresIntegrationSettings Settings)
    : PostgresIntegrationSettingsResult;

internal sealed record PostgresIntegrationSettingsFailure(string Reason)
    : PostgresIntegrationSettingsResult;

internal sealed class PostgresIntegrationSettings
{
    internal const string ConnectionStringEnvironmentVariable =
        "IMMICH_REVERSEGEO_TEST_POSTGRES_CONNECTION_STRING";

    private const int BoundedTimeoutSeconds = 5;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _setupGate = new(1, 1);
    private Task<PostgresIntegrationCapabilities>? _capabilityTask;

    private PostgresIntegrationSettings(string connectionString, string databaseName)
    {
        _connectionString = connectionString;
        DatabaseName = databaseName;
    }

    internal string DatabaseName { get; }

    internal static PostgresIntegrationSettingsResult ReadEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable));

    internal static PostgresIntegrationSettingsResult Parse(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new PostgresIntegrationSettingsFailure(
                $"Set {ConnectionStringEnvironmentVariable} before running Change32 PostgreSQL integration tests.");
        }

        try
        {
            NpgsqlConnectionStringBuilder builder = new(connectionString)
            {
                IncludeErrorDetail = false
            };

            if (string.IsNullOrWhiteSpace(builder.Host)
                || string.IsNullOrWhiteSpace(builder.Database)
                || string.IsNullOrWhiteSpace(builder.Username))
            {
                return new PostgresIntegrationSettingsFailure(
                    $"{ConnectionStringEnvironmentVariable} must specify Host, Database, and Username.");
            }

            return new PostgresIntegrationSettingsAvailable(
                new PostgresIntegrationSettings(builder.ConnectionString, builder.Database));
        }
        catch (ArgumentException)
        {
            return new PostgresIntegrationSettingsFailure(
                $"{ConnectionStringEnvironmentVariable} is not a valid PostgreSQL connection string.");
        }
    }

    internal async Task<NpgsqlConnection> OpenAsync(
        string applicationName,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = new(CreateConnectionString(DatabaseName, applicationName));
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await connection.DisposeAsync();
            throw;
        }
        catch
        {
            await connection.DisposeAsync();
            throw new PostgresIntegrationSetupException("Could not connect to the configured Change32 PostgreSQL server.");
        }
    }

    internal async Task<PostgresIntegrationCapabilities> ProbeCapabilitiesAsync(CancellationToken cancellationToken)
    {
        await _setupGate.WaitAsync(cancellationToken);
        try
        {
            _capabilityTask ??= PostgresIntegrationDatabase.ProbeCapabilitiesAsync(this, CancellationToken.None);
        }
        finally
        {
            _setupGate.Release();
        }

        return await _capabilityTask.WaitAsync(cancellationToken);
    }

    internal Task<PostgresIntegrationDatabase> CreateCaseAsync(
        Guid caseId,
        CancellationToken cancellationToken) =>
        PostgresIntegrationDatabase.CreateCaseAsync(this, caseId, cancellationToken);

    internal string CreateConnectionString(string databaseName, string applicationName)
    {
        ValidateApplicationName(applicationName);
        NpgsqlConnectionStringBuilder builder = new(_connectionString)
        {
            Database = databaseName,
            ApplicationName = applicationName,
            Timeout = BoundedTimeoutSeconds,
            CommandTimeout = BoundedTimeoutSeconds,
            IncludeErrorDetail = false,
            Pooling = false
        };

        return builder.ConnectionString;
    }

    internal static void ValidateApplicationName(string applicationName)
    {
        if (string.IsNullOrWhiteSpace(applicationName)
            || applicationName.Length > 63
            || applicationName.Any(character => !((character >= 'A' && character <= 'Z')
                || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9')
                || character is '.' or '_' or '-')))
        {
            throw new ArgumentException(
                "The PostgreSQL application name must use only ASCII letters, digits, '.', '_', or '-', and fit within 63 bytes.",
                nameof(applicationName));
        }
    }
}

internal sealed class PostgresIntegrationSetupException(string message) : Exception(message);

internal sealed record PostgresIntegrationCapabilities(
    bool CanCreateDatabases,
    bool CanInspectBackends,
    bool CanTerminateOwnedBackends);
