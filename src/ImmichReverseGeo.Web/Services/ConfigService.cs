using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class ConfigService : IProcessingRunConfiguration, IProcessingScheduleConfiguration
{
    private readonly ILogger<ConfigService> _logger;
    private readonly ISettingsDocumentStore _store;
    private readonly SemaphoreSlim _updateGate = new(1, 1);

    public ConfigService(ILogger<ConfigService> logger, string? configDir = null)
        : this(logger, new SettingsDocumentStore(Path.Combine(configDir ?? "/config", "settings.json")))
    {
    }

    internal ConfigService(ILogger<ConfigService> logger, ISettingsDocumentStore store)
    {
        _logger = logger;
        _store = store;
    }

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task<AppConfig> GetConfigAsync() => ReadConfigAsync(CancellationToken.None);

    async Task<AppConfig> IProcessingRunConfiguration.GetConfigAsync()
    {
        var config = await GetConfigAsync().ConfigureAwait(false);
        ProcessingConfigurationPolicy.ValidateBatchSize(config.Processing.BatchSize);
        return config;
    }

    private async Task<AppConfig> ReadConfigAsync(CancellationToken cancellationToken)
    {
        try
        {
            var document = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                _logger.LogInformation("No settings file found, using defaults");
                return new AppConfig();
            }

            var config = JsonSerializer.Deserialize<AppConfig>(document, _json)
                ?? throw new JsonException();
            return EnsureDefaults(config);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Unable to read settings. Check the saved settings file and configuration storage access.");
        }
    }

    async Task<ProcessingScheduleSnapshot> IProcessingScheduleConfiguration.GetSnapshotAsync()
    {
        var config = await GetConfigAsync().ConfigureAwait(false);
        return new ProcessingScheduleSnapshot(config.Schedule.Enabled, config.Schedule.Cron);
    }

    private async Task PublishConfigAsync(AppConfig config, CancellationToken cancellationToken)
    {
        try
        {
            await _store.PublishAsync(JsonSerializer.SerializeToUtf8Bytes(config, _json), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Unable to save settings. Check configuration storage access and free space, then retry.");
        }

        try
        {
            _logger.LogInformation("Settings saved");
        }
        catch (Exception)
        {
            // Publication is authoritative even when a diagnostic sink fails.
        }
    }

    public async Task<SettingsUpdate> SaveSettingsAsync(SettingsUpdate update, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(config =>
        {
            update.ApplyTo(config);
            ProcessingConfigurationPolicy.ValidateBatchSize(config.Processing.BatchSize);
        }, cancellationToken).ConfigureAwait(false);
        return update;
    }

    public Task SaveAppearanceModeAsync(string mode) => SaveAppearanceModeAsync(mode, CancellationToken.None);

    public Task SaveAppearanceModeAsync(string mode, CancellationToken cancellationToken)
    {
        string normalized = AppearanceModes.NormalizeMode(mode);
        return UpdateAsync(config => config.Appearance.Mode = normalized, cancellationToken);
    }

    public async Task<CityResolverSettingsUpdate> SaveCityResolverSettingsAsync(CityResolverSettingsUpdate update, CancellationToken cancellationToken = default)
    {
        var values = update.ToConfig();
        await UpdateAsync(config => config.Processing.CityResolver = values, cancellationToken).ConfigureAwait(false);
        return update;
    }

    private async Task UpdateAsync(Action<AppConfig> apply, CancellationToken cancellationToken)
    {
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
            apply(config);
            await PublishConfigAsync(config, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public DbSettings GetDbSettings() => new(
        Host: Environment.GetEnvironmentVariable("DB_HOST") ?? "database",
        Port: int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var p) ? p : 5432,
        Username: Environment.GetEnvironmentVariable("DB_USERNAME") ?? "postgres",
        Password: Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "",
        Database: Environment.GetEnvironmentVariable("DB_DATABASE_NAME") ?? "immich");

    private static AppConfig EnsureDefaults(AppConfig config)
    {
        config.Schedule ??= new ScheduleConfig();
        config.Processing ??= new ProcessingConfig();
        config.Processing.CityResolver ??= new CityResolverConfig();
        config.Processing.CityResolver.DefaultProfile ??= CityResolverProfile.CreateEmpty();
        config.Processing.CityResolver.CountryOverrides ??= new(StringComparer.OrdinalIgnoreCase);
        config.Appearance ??= new AppearanceConfig();
        if (string.IsNullOrWhiteSpace(config.Appearance.Mode))
        {
            config.Appearance.Mode = AppearanceModes.Auto;
        }

        return config;
    }
}
