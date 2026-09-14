using ImmichReverseGeo.Web.ProcessingRunLocking;
using Npgsql;
using NpgsqlTypes;

namespace ImmichReverseGeo.Tests.CrossProcessRunExclusion;

internal sealed class PostgresIntegrationDatabase : IAsyncDisposable
{
    private const string DatabasePrefix = "immich_reversegeo_test_";
    private static readonly SemaphoreSlim DedicatedDatabaseGate = new(1, 1);

    private readonly PostgresIntegrationSettings _settings;
    private readonly bool _createdDatabase;
    private readonly bool _holdsDedicatedDatabaseGate;
    private readonly string _controlApplicationName;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private MinimalSchemaState _minimalSchemaState;

    private PostgresIntegrationDatabase(
        PostgresIntegrationSettings settings,
        string databaseName,
        bool createdDatabase,
        bool holdsDedicatedDatabaseGate,
        string controlApplicationName)
    {
        _settings = settings;
        DatabaseName = databaseName;
        _createdDatabase = createdDatabase;
        _holdsDedicatedDatabaseGate = holdsDedicatedDatabaseGate;
        _controlApplicationName = controlApplicationName;
    }

    internal string DatabaseName { get; }

    internal bool UsesDedicatedDatabase => !_createdDatabase;

    internal string CreateConnectionString(string applicationName) =>
        _settings.CreateConnectionString(DatabaseName, applicationName);

    internal static async Task<PostgresIntegrationCapabilities> ProbeCapabilitiesAsync(
        PostgresIntegrationSettings settings,
        CancellationToken cancellationToken)
    {
        Guid probeId = Guid.NewGuid();
        string probeDatabaseName = CreateDatabaseName(probeId);
        try
        {
            await using NpgsqlConnection maintenance = await settings.OpenAsync(
                CreateApplicationName("setup", probeId), cancellationToken);
            if (!await VerifyBackendInspectionAsync(maintenance, cancellationToken))
            {
                throw new PostgresIntegrationSetupException(
                    "The configured Change32 PostgreSQL role cannot inspect its scoped backend.");
            }

            bool canTerminate = await ProbeRegisteredBackendTerminationAsync(
                settings, maintenance, probeId, cancellationToken);
            if (await DatabaseExistsAsync(maintenance, probeDatabaseName, cancellationToken))
            {
                throw new PostgresIntegrationSetupException(
                    "A generated Change32 PostgreSQL capability-probe database already exists.");
            }

            bool created = false;
            bool createAttempted = false;
            try
            {
                createAttempted = true;
                if (!await TryCreateDatabaseAsync(maintenance, probeDatabaseName, cancellationToken))
                {
                    return new PostgresIntegrationCapabilities(false, true, canTerminate);
                }

                created = true;
                await using (NpgsqlConnection probe = new(settings.CreateConnectionString(
                    probeDatabaseName, CreateApplicationName("probe", probeId))))
                {
                    await probe.OpenAsync(cancellationToken);
                    await VerifyAdvisoryLockAccessAsync(probe, cancellationToken);
                    if (!await VerifyBackendInspectionAsync(probe, cancellationToken))
                    {
                        throw new PostgresIntegrationSetupException(
                            "The configured Change32 PostgreSQL role cannot inspect the isolated capability-probe backend.");
                    }
                }

                await DropDatabaseAsync(settings, probeDatabaseName, cancellationToken);
                created = false;
            }
            catch
            {
                if (created || createAttempted)
                {
                    if (!await ReconcileAndDropOwnedDatabaseAsync(settings, probeDatabaseName))
                    {
                        throw new PostgresIntegrationSetupException(
                            "Change32 PostgreSQL capability-probe cleanup left an unconfirmed database resource.");
                    }
                }

                throw;
            }

            return new PostgresIntegrationCapabilities(true, true, canTerminate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresIntegrationSetupException)
        {
            throw;
        }
        catch
        {
            throw new PostgresIntegrationSetupException(
                "Could not verify the required Change32 PostgreSQL capabilities.");
        }
    }

    internal static async Task<PostgresIntegrationDatabase> CreateCaseAsync(
        PostgresIntegrationSettings settings,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        PostgresIntegrationCapabilities capabilities = await settings.ProbeCapabilitiesAsync(cancellationToken);
        string controlApplicationName = CreateApplicationName("case", caseId);
        if (capabilities.CanCreateDatabases)
        {
            string databaseName = CreateDatabaseName(caseId);
            try
            {
                await CreateOwnedDatabaseAsync(settings, databaseName, cancellationToken);
                PostgresIntegrationDatabase database = new(
                    settings, databaseName, true, false, controlApplicationName);
                try
                {
                    await database.CreateMinimalSchemaAsync(cancellationToken);
                    return database;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await DisposeFailedProvisioningAsync(database);
                    throw;
                }
                catch
                {
                    await database.DisposeAsync();
                    throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PostgresIntegrationSetupException)
            {
                throw;
            }
            catch
            {
                throw new PostgresIntegrationSetupException(
                    "Could not provision an isolated Change32 PostgreSQL test database.");
            }
        }

        if (!settings.DatabaseName.StartsWith(DatabasePrefix, StringComparison.Ordinal))
        {
            throw new PostgresIntegrationSetupException(
                $"Database creation is unavailable, so {PostgresIntegrationSettings.ConnectionStringEnvironmentVariable} must name a dedicated database beginning with {DatabasePrefix}.");
        }

        await DedicatedDatabaseGate.WaitAsync(cancellationToken);
        PostgresIntegrationDatabase dedicated = new(
            settings, settings.DatabaseName, false, true, controlApplicationName);
        try
        {
            await dedicated.VerifyDedicatedDatabaseAsync(cancellationToken);
            await dedicated.CreateMinimalSchemaAsync(cancellationToken);
            return dedicated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeFailedProvisioningAsync(dedicated);
            throw;
        }
        catch
        {
            await dedicated.DisposeAsync();
            throw;
        }
    }

    internal async Task AssertProductionKeyFreeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = await OpenCaseConnectionAsync(
                _controlApplicationName, cancellationToken);
            if (await PostgresLockOwnerQuery.FindAsync(
                connection, ProcessingRunLockIdentity.Key, cancellationToken) is not null)
            {
                throw new PostgresIntegrationSetupException(
                    "The production advisory-run-lock key already has an owner in the Change32 test database.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresIntegrationSetupException)
        {
            throw;
        }
        catch
        {
            throw new PostgresIntegrationSetupException(
                "Could not inspect the production advisory-run-lock owner in the Change32 test database.");
        }
    }

    internal async Task<PostgresLockOwner?> FindOwnedBackendAsync(
        string exactApplicationName,
        CancellationToken cancellationToken)
    {
        PostgresIntegrationSettings.ValidateApplicationName(exactApplicationName);
        try
        {
            await using NpgsqlConnection connection = await OpenCaseConnectionAsync(
                _controlApplicationName, cancellationToken);
            PostgresLockOwner? owner = await PostgresLockOwnerQuery.FindAsync(
                connection, ProcessingRunLockIdentity.Key, cancellationToken);
            if (owner is null)
            {
                return null;
            }

            if (!string.Equals(owner.ApplicationName, exactApplicationName, StringComparison.Ordinal))
            {
                throw new PostgresIntegrationSetupException(
                    "The production advisory-run-lock key is owned by an unexpected backend.");
            }

            return owner;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresIntegrationSetupException)
        {
            throw;
        }
        catch
        {
            throw new PostgresIntegrationSetupException(
                "Could not inspect the scoped Change32 PostgreSQL advisory-lock owner.");
        }
    }

    internal async Task<bool> TerminateOwnedBackendAsync(
        int processId,
        string exactApplicationName,
        CancellationToken cancellationToken)
    {
        PostgresIntegrationSettings.ValidateApplicationName(exactApplicationName);
        try
        {
            await using NpgsqlConnection connection = await OpenCaseConnectionAsync(
                _controlApplicationName, cancellationToken);
            return await PostgresLockOwnerQuery.TryTerminateExactOwnerAsync(
                connection, ProcessingRunLockIdentity.Key, processId, exactApplicationName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new PostgresIntegrationSetupException(
                "Could not terminate the scoped Change32 PostgreSQL advisory-lock owner.");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task VerifyDedicatedDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = await OpenCaseConnectionAsync(
                _controlApplicationName, cancellationToken);
            string currentDatabase = Convert.ToString(await ExecuteScalarAsync(
                connection, "SELECT current_database()", cancellationToken)) ?? string.Empty;
            bool ownedByCurrentRole = await ExecuteScalarAsync(
                connection,
                "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = current_database() AND datdba = (SELECT usesysid FROM pg_user WHERE usename = current_user))",
                cancellationToken) is true;
            if (!string.Equals(currentDatabase, DatabaseName, StringComparison.Ordinal)
                || !ownedByCurrentRole
                || await PublicSchemaTableCountAsync(connection, cancellationToken) != 0)
            {
                throw new PostgresIntegrationSetupException(
                    "The dedicated Change32 PostgreSQL database must match the configured name, be owned by the configured role, and have an empty public schema before a case starts.");
            }

            await VerifyAdvisoryLockAccessAsync(connection, cancellationToken);
            await AssertProductionKeyFreeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresIntegrationSetupException)
        {
            throw;
        }
        catch
        {
            throw new PostgresIntegrationSetupException(
                "Could not verify the dedicated Change32 PostgreSQL test database.");
        }
    }

    private async Task CreateMinimalSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = await OpenCaseConnectionAsync(
                _controlApplicationName, cancellationToken);
            if (await PublicSchemaTableCountAsync(connection, cancellationToken) != 0)
            {
                throw new PostgresIntegrationSetupException(
                    "The Change32 PostgreSQL test database must have an empty public schema before minimal tables are created.");
            }

            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using NpgsqlCommand command = new(
                """
                CREATE TABLE public.asset (
                    id uuid PRIMARY KEY,
                    "createdAt" timestamptz NOT NULL,
                    "deletedAt" timestamptz NULL
                );
                CREATE TABLE public.asset_exif (
                    "assetId" uuid PRIMARY KEY REFERENCES public.asset(id),
                    city text NULL,
                    state text NULL,
                    country text NULL,
                    latitude double precision NULL,
                    longitude double precision NULL
                )
                """,
                connection,
                transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _minimalSchemaState = MinimalSchemaState.Created;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReconcileMinimalSchemaAsync();
            throw;
        }
        catch (PostgresIntegrationSetupException)
        {
            throw;
        }
        catch
        {
            await ReconcileMinimalSchemaAsync();
            throw new PostgresIntegrationSetupException(
                _minimalSchemaState == MinimalSchemaState.Unknown
                    ? "Minimal Change32 PostgreSQL schema creation left an unrecognized public schema state."
                    : "Could not create the minimal Change32 PostgreSQL test schema.");
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            bool cleanupFailed = false;
            bool keyConfirmedFree = false;
            try
            {
                await AssertProductionKeyFreeAsync(CancellationToken.None);
                keyConfirmedFree = true;
            }
            catch
            {
                cleanupFailed = true;
            }

            if (keyConfirmedFree
                && !_createdDatabase
                && _minimalSchemaState == MinimalSchemaState.Created)
            {
                try
                {
                    await DropMinimalSchemaAsync();
                }
                catch
                {
                    cleanupFailed = true;
                }
            }

            if (keyConfirmedFree
                && !_createdDatabase
                && _minimalSchemaState == MinimalSchemaState.Unknown)
            {
                cleanupFailed = true;
            }

            if (keyConfirmedFree && !_createdDatabase && _minimalSchemaState == MinimalSchemaState.None)
            {
                try
                {
                    await AssertProductionKeyFreeAsync(CancellationToken.None);
                }
                catch
                {
                    cleanupFailed = true;
                    keyConfirmedFree = false;
                }
            }

            if (keyConfirmedFree && _createdDatabase)
            {
                try
                {
                    await DropDatabaseAsync(_settings, DatabaseName, CancellationToken.None);
                }
                catch
                {
                    cleanupFailed = true;
                }
            }

            if (cleanupFailed)
            {
                throw new PostgresIntegrationSetupException(
                    "Change32 PostgreSQL test cleanup left an owned resource behind.");
            }
        }
        finally
        {
            if (_holdsDedicatedDatabaseGate)
            {
                DedicatedDatabaseGate.Release();
            }
        }
    }

    private static async Task DisposeFailedProvisioningAsync(PostgresIntegrationDatabase database)
    {
        try
        {
            await database.DisposeAsync();
        }
        catch
        {
            throw new PostgresIntegrationSetupException(
                "Failed Change32 PostgreSQL case provisioning could not clean up its owned database resources.");
        }
    }

    private async Task DropMinimalSchemaAsync()
    {
        try
        {
            await using NpgsqlConnection connection = await OpenCaseConnectionAsync(
                _controlApplicationName, CancellationToken.None);
            if (!await HasOnlyMinimalSchemaAsync(connection, CancellationToken.None))
            {
                _minimalSchemaState = MinimalSchemaState.Unknown;
                throw new PostgresIntegrationSetupException(
                    "The Change32 PostgreSQL public schema changed before owned minimal tables could be removed.");
            }

            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(CancellationToken.None);
            await using NpgsqlCommand command = new(
                "DROP TABLE public.asset_exif; DROP TABLE public.asset;", connection, transaction);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            _minimalSchemaState = MinimalSchemaState.None;
        }
        catch (PostgresIntegrationSetupException)
        {
            throw;
        }
        catch
        {
            await ReconcileMinimalSchemaAsync();
            throw new PostgresIntegrationSetupException(
                "Could not remove the owned Change32 PostgreSQL minimal schema.");
        }
    }

    private async Task ReconcileMinimalSchemaAsync()
    {
        try
        {
            await using NpgsqlConnection connection = await OpenCaseConnectionAsync(
                _controlApplicationName, CancellationToken.None);
            long count = await PublicSchemaTableCountAsync(connection, CancellationToken.None);
            _minimalSchemaState = count switch
            {
                0 => MinimalSchemaState.None,
                2 when await HasOnlyMinimalSchemaAsync(connection, CancellationToken.None) => MinimalSchemaState.Created,
                _ => MinimalSchemaState.Unknown
            };
        }
        catch
        {
            _minimalSchemaState = MinimalSchemaState.Unknown;
        }
    }

    private async Task<NpgsqlConnection> OpenCaseConnectionAsync(
        string applicationName,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = new(CreateConnectionString(applicationName));
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
            throw new PostgresIntegrationSetupException(
                "Could not open the owned Change32 PostgreSQL test database.");
        }
    }

    private static async Task CreateOwnedDatabaseAsync(
        PostgresIntegrationSettings settings,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await settings.OpenAsync(
            CreateApplicationName("createdb", Guid.NewGuid()), cancellationToken);
        if (await DatabaseExistsAsync(connection, databaseName, cancellationToken))
        {
            throw new PostgresIntegrationSetupException(
                "A generated Change32 PostgreSQL case database already exists.");
        }

        try
        {
            if (!await TryCreateDatabaseAsync(connection, databaseName, cancellationToken))
            {
                throw new PostgresIntegrationSetupException(
                    "The configured Change32 PostgreSQL role cannot create an isolated case database.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!await ReconcileAndDropOwnedDatabaseAsync(settings, databaseName))
            {
                throw new PostgresIntegrationSetupException(
                    "Cancelled Change32 PostgreSQL database creation left an unconfirmed database resource.");
            }

            throw;
        }
        catch
        {
            if (!await ReconcileAndDropOwnedDatabaseAsync(settings, databaseName))
            {
                throw new PostgresIntegrationSetupException(
                    "Failed Change32 PostgreSQL database creation left an unconfirmed database resource.");
            }

            throw;
        }
    }

    private static async Task<bool> TryCreateDatabaseAsync(
        NpgsqlConnection connection,
        string databaseName,
        CancellationToken cancellationToken)
    {
        string quotedName = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
        await using NpgsqlCommand command = new($"CREATE DATABASE {quotedName}", connection);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState is "42501" or "0A000")
        {
            return false;
        }
    }

    private static async Task DropDatabaseAsync(
        PostgresIntegrationSettings settings,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await settings.OpenAsync(
            CreateApplicationName("dropdb", Guid.NewGuid()), cancellationToken);
        string quotedName = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
        await using NpgsqlCommand command = new($"DROP DATABASE {quotedName}", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ReconcileAndDropOwnedDatabaseAsync(
        PostgresIntegrationSettings settings,
        string databaseName)
    {
        try
        {
            await using NpgsqlConnection connection = await settings.OpenAsync(
                CreateApplicationName("reconcile", Guid.NewGuid()), CancellationToken.None);
            await using NpgsqlCommand ownership = new(
                """
                SELECT CASE
                    WHEN NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = $1) THEN 0
                    WHEN EXISTS (
                        SELECT 1 FROM pg_database
                        WHERE datname = $1
                          AND datdba = (SELECT usesysid FROM pg_user WHERE usename = current_user)) THEN 1
                    ELSE -1
                END
                """,
                connection);
            ownership.Parameters.AddWithValue(NpgsqlDbType.Text, databaseName);
            int ownershipState = Convert.ToInt32(await ownership.ExecuteScalarAsync(CancellationToken.None));
            if (ownershipState == 0)
            {
                return true;
            }

            if (ownershipState != 1)
            {
                return false;
            }

            string quotedName = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
            await using NpgsqlCommand drop = new($"DROP DATABASE {quotedName}", connection);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
            return true;
        }
        catch
        {
            // No unknown database is removed after an ambiguous CREATE outcome.
            return false;
        }
    }

    private static async Task VerifyAdvisoryLockAccessAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand acquire = new("SELECT pg_try_advisory_lock($1)", connection);
        acquire.Parameters.AddWithValue(NpgsqlDbType.Bigint, ProcessingRunLockIdentity.Key);
        if (await acquire.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new PostgresIntegrationSetupException(
                "The production advisory-run-lock key already has an owner in the Change32 test database.");
        }

        try
        {
            if (await PostgresLockOwnerQuery.FindAsync(
                connection, ProcessingRunLockIdentity.Key, cancellationToken) is null)
            {
                throw new PostgresIntegrationSetupException(
                    "The configured Change32 PostgreSQL role cannot inspect the production advisory-run-lock owner.");
            }
        }
        finally
        {
            await using NpgsqlCommand release = new("SELECT pg_advisory_unlock($1)", connection);
            release.Parameters.AddWithValue(NpgsqlDbType.Bigint, ProcessingRunLockIdentity.Key);
            if (await release.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw new PostgresIntegrationSetupException(
                    "The configured Change32 PostgreSQL role could not release the production advisory-run-lock key.");
            }
        }
    }

    private static async Task<bool> VerifyBackendInspectionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = pg_backend_pid())", connection);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<bool> ProbeRegisteredBackendTerminationAsync(
        PostgresIntegrationSettings settings,
        NpgsqlConnection terminatingConnection,
        Guid probeId,
        CancellationToken cancellationToken)
    {
        string applicationName = CreateApplicationName("termprobe", probeId);
        try
        {
            await using NpgsqlConnection owned = await settings.OpenAsync(applicationName, cancellationToken);
            await using NpgsqlCommand getProcessId = new("SELECT pg_backend_pid()", owned);
            int processId = Convert.ToInt32(await getProcessId.ExecuteScalarAsync(cancellationToken));
            await using NpgsqlCommand terminate = new(
                """
                SELECT pg_terminate_backend($1)
                WHERE EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE pid = $1 AND application_name = $2 AND datname = current_database())
                """,
                terminatingConnection);
            terminate.Parameters.AddWithValue(NpgsqlDbType.Integer, processId);
            terminate.Parameters.AddWithValue(NpgsqlDbType.Text, applicationName);
            return await terminate.ExecuteScalarAsync(cancellationToken) is true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> DatabaseExistsAsync(
        NpgsqlConnection connection,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = $1)", connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, databaseName);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<object?> ExecuteScalarAsync(
        NpgsqlConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(commandText, connection);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<long> PublicSchemaTableCountAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public'", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> HasOnlyMinimalSchemaAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<string> names = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names.Count == 2 && names[0] == "asset" && names[1] == "asset_exif";
    }

    private static string CreateDatabaseName(Guid caseId)
    {
        string databaseName = DatabasePrefix + caseId.ToString("N");
        if (databaseName.Length > 63 || databaseName.Any(character => !((character >= 'a' && character <= 'z')
            || (character >= '0' && character <= '9') || character == '_')))
        {
            throw new InvalidOperationException("Generated Change32 PostgreSQL database name is invalid.");
        }

        return databaseName;
    }

    private static string CreateApplicationName(string purpose, Guid id) =>
        $"immich-rg-c32-{purpose}-{id:N}";

    private enum MinimalSchemaState
    {
        None,
        Created,
        Unknown
    }
}
