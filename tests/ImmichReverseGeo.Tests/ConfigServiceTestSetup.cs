using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

internal static class ConfigServiceTestSetup
{
    public static async Task SeedConfigAsync(this ConfigService service, AppConfig config)
    {
        await service.SaveSettingsAsync(SettingsUpdate.FromConfig(config));
        await service.SaveCityResolverSettingsAsync(new CityResolverSettingsUpdate(config.Processing.CityResolver));
        await service.SaveAppearanceModeAsync(config.Appearance.Mode);
    }
}
