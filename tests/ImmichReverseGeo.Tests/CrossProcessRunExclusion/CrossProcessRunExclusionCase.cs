using System.Collections.Concurrent;
using Npgsql;

namespace ImmichReverseGeo.Tests.CrossProcessRunExclusion;

/// <summary>
/// Parent-side composition for the process-boundary cases.  It deliberately uses the
/// production launcher, coordinator, bridge, and finalizer: this layer owns only test
/// resources, deterministic gates, and observation.
/// </summary>
internal sealed class CrossProcessRunExclusionCase : IAsyncDisposable
{
    internal static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<Guid, CrossProcessRunExclusionCase> Registry = new();
    private readonly object _disposeGate = new();
    private readonly Guid _registration = Guid.NewGuid();
    private readonly PostgresIntegrationDatabase _database;
    private readonly List<ParentWorker> _workers = [];
    private Task? _activeCleanupTask;
    private Task? _databaseCleanupTask;
    private bool _databaseDisposed;
    private bool _rootDeleted;
    private bool _resourcesReleased;

    private CrossProcessRunExclusionCase(
        PostgresIntegrationDatabase database,
        PostgresIntegrationCapabilities capabilities)
    {
        _database = database;
        Capabilities = capabilities;
        Root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(Root);
        Registry.TryAdd(_registration, this);
    }

    internal string Root { get; }
    internal PostgresIntegrationDatabase Database => _database;
    internal PostgresIntegrationCapabilities Capabilities { get; }
    internal bool IsRegistered => Registry.ContainsKey(_registration);
    internal bool ResourcesReleased => _resourcesReleased;

    internal static Task<CrossProcessRunExclusionCase> CreateAsync(CancellationToken cancellationToken) =>
        CreateAsync(RequireSettings(), cancellationToken);

    internal static async Task<CrossProcessRunExclusionCase> CreateAsync(
        PostgresIntegrationSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            PostgresIntegrationCapabilities capabilities = await settings
                .ProbeCapabilitiesAsync(cancellationToken)
                .WaitAsync(Watchdog, cancellationToken);
            PostgresIntegrationDatabase database = await settings.CreateCaseAsync(Guid.NewGuid(), cancellationToken).WaitAsync(Watchdog, cancellationToken);
            return new CrossProcessRunExclusionCase(database, capabilities);
        }
        catch (PostgresIntegrationSetupException exception)
        {
            Assert.Fail(exception.Message);
            throw;
        }
    }

    internal async Task<ParentWorker> StartControlledAsync(string scenario, CancellationToken cancellationToken)
    {
        ParentWorker worker = await ParentWorker.CreateControlledAsync(
            this,
            scenario,
            fixture: null,
            cancellationToken: cancellationToken);
        _workers.Add(worker);
        await worker.StartAsync(cancellationToken);
        return worker;
    }

    internal async Task<ParentWorker> StartProductionAsync(CancellationToken cancellationToken)
    {
        ParentWorker worker = await ParentWorker.CreateProductionAsync(
            this,
            fixture: null,
            cancellationToken: cancellationToken);
        _workers.Add(worker);
        await worker.StartAsync(cancellationToken);
        return worker;
    }

    internal async Task<ParentWorker> StartProductionOnSameCoordinatorAsync(
        ParentWorker completedWorker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completedWorker);
        if (!ReferenceEquals(completedWorker.OwnerCase, this) || !completedWorker.IsFullyFinalized)
        {
            throw new InvalidOperationException(
                "A Change32 coordinator can be reused only after its prior worker reached full finality in this case.");
        }

        ParentWorker worker = await ParentWorker.CreateProductionAsync(
            this,
            completedWorker.Fixture,
            cancellationToken);
        _workers.Add(worker);
        await worker.StartAsync(cancellationToken);
        return worker;
    }

    internal async Task AssertKeyFreeAsync(CancellationToken cancellationToken)
    {
        await _database.AssertProductionKeyFreeAsync(cancellationToken).WaitAsync(Watchdog, cancellationToken);
    }

    internal async Task<DatabaseEffectSnapshot> ReadDatabaseEffectsAsync(CancellationToken cancellationToken)
    {
        string applicationName = "change32_snapshot_" + Guid.NewGuid().ToString("N")[..20];
        await using var connection = new NpgsqlConnection(_database.CreateConnectionString(applicationName));
        await connection.OpenAsync(cancellationToken).WaitAsync(Watchdog, cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT (SELECT COUNT(*) FROM public.asset), (SELECT COUNT(*) FROM public.asset_exif)",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).WaitAsync(Watchdog, cancellationToken);
        Assert.IsTrue(await reader.ReadAsync(cancellationToken));
        return new DatabaseEffectSnapshot(reader.GetInt64(0), reader.GetInt64(1));
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_resourcesReleased)
            {
                return ValueTask.CompletedTask;
            }

            if (_activeCleanupTask is null || _activeCleanupTask.IsCompleted)
            {
                _activeCleanupTask = DisposeCoreAsync();
            }

            return new ValueTask(_activeCleanupTask);
        }
    }

    internal static async Task ReapRemainingAsync()
    {
        CrossProcessRunExclusionCase[] cases = Registry.Values.ToArray();
        Exception?[] failures = await Task.WhenAll(cases.Select(async @case =>
        {
            try
            {
                await @case.DisposeAsync().AsTask().WaitAsync(Watchdog);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        })).WaitAsync(Watchdog);
        Exception[] observed = failures.Where(failure => failure is not null).Cast<Exception>().ToArray();
        if (observed.Length > 0)
        {
            throw new AggregateException("Change32 last-chance cleanup failed.", observed);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var failures = new List<Exception>();
        foreach (ParentWorker worker in _workers)
        {
            try
            {
                await worker.DisposeAsync().AsTask().WaitAsync(Watchdog);
            }
            catch (Exception exception)
            {
                _ = exception;
                failures.Add(new InvalidOperationException(
                    $"Change32 worker cleanup remained incomplete for database {_database.DatabaseName}."));
            }
        }

        if (failures.Count == 0 && !_databaseDisposed)
        {
            try
            {
                await _database.AssertProductionKeyFreeAsync(CancellationToken.None).WaitAsync(Watchdog);
            }
            catch (Exception exception)
            {
                _ = exception;
                failures.Add(new InvalidOperationException(
                    $"Change32 cleanup could not prove the production key free in database {_database.DatabaseName}."));
            }
        }

        // Database cleanup comes after every retained process and stream has reached finality.
        if (failures.Count == 0 && !_databaseDisposed)
        {
            _databaseCleanupTask ??= _database.DisposeAsync().AsTask();
            try
            {
                await _databaseCleanupTask.WaitAsync(Watchdog);
                _databaseDisposed = true;
            }
            catch (Exception exception)
            {
                _ = exception;
                string disposition = _databaseCleanupTask.IsCompleted
                    ? "completed unsuccessfully"
                    : "remains in flight";
                failures.Add(new InvalidOperationException(
                    $"Change32 database cleanup {disposition} for {_database.DatabaseName}."));
            }
        }

        if (failures.Count == 0 && _databaseDisposed && !_rootDeleted)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                _rootDeleted = true;
            }
            catch (Exception exception)
            {
                _ = exception;
                failures.Add(new InvalidOperationException(
                    $"Change32 cleanup could not delete resource root {Path.GetFileName(Root)}."));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Change32 case cleanup failed.", failures);
        }

        Registry.TryRemove(_registration, out _);
        _resourcesReleased = true;
    }

    internal static PostgresIntegrationSettings RequireSettings()
    {
        return PostgresIntegrationSettings.ReadEnvironment() switch
        {
            PostgresIntegrationSettingsAvailable available => available.Settings,
            PostgresIntegrationSettingsFailure failure => FailMissingSettings(failure.Reason),
            _ => FailMissingSettings("The Change32 PostgreSQL setting could not be read.")
        };
    }

    private static PostgresIntegrationSettings FailMissingSettings(string reason)
    {
        Assert.Fail(reason);
        throw new InvalidOperationException(reason);
    }
}

internal sealed record DatabaseEffectSnapshot(long AssetCount, long AssetExifCount);
