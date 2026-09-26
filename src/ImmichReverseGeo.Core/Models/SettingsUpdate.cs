using System;
using System.Linq;

namespace ImmichReverseGeo.Core.Models;

public sealed class CityResolverSettingsUpdate
{
    private readonly CityResolverConfig _values;

    public CityResolverSettingsUpdate(CityResolverConfig values)
    {
        _values = Copy(values);
    }

    public CityResolverConfig ToConfig() => Copy(_values);

    private static CityResolverConfig Copy(CityResolverConfig values)
    {
        return new CityResolverConfig
        {
            DefaultProfile = values.DefaultProfile.Clone(),
            CountryOverrides = values.CountryOverrides.ToDictionary(
                pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase)
        };
    }
}

public sealed record SettingsUpdate(
    bool ScheduleEnabled,
    string ScheduleCron,
    int BatchSize,
    int BatchDelayMs,
    int MaxDegreeOfParallelism,
    bool UseAirportInfrastructure,
    bool UseGadmAdministrativeAreas,
    bool PreferGadmAdministrativeAreas,
    bool UseGadmTerritoryFallbacks,
    bool VerboseLogging)
{
    public static SettingsUpdate FromConfig(AppConfig config)
    {
        return new SettingsUpdate(
            config.Schedule.Enabled,
            config.Schedule.Cron,
            config.Processing.BatchSize,
            config.Processing.BatchDelayMs,
            config.Processing.MaxDegreeOfParallelism,
            config.Processing.UseAirportInfrastructure,
            config.Processing.UseGadmAdministrativeAreas,
            config.Processing.PreferGadmAdministrativeAreas,
            config.Processing.UseGadmTerritoryFallbacks,
            config.Processing.VerboseLogging);
    }

    public void ApplyTo(AppConfig config)
    {
        config.Schedule.Enabled = ScheduleEnabled;
        config.Schedule.Cron = ScheduleCron;
        config.Processing.BatchSize = BatchSize;
        config.Processing.BatchDelayMs = BatchDelayMs;
        config.Processing.MaxDegreeOfParallelism = MaxDegreeOfParallelism;
        config.Processing.UseAirportInfrastructure = UseAirportInfrastructure;
        config.Processing.UseGadmAdministrativeAreas = UseGadmAdministrativeAreas;
        config.Processing.PreferGadmAdministrativeAreas = PreferGadmAdministrativeAreas;
        config.Processing.UseGadmTerritoryFallbacks = UseGadmTerritoryFallbacks;
        config.Processing.VerboseLogging = VerboseLogging;
    }
}
