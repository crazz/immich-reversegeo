using System;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Web.Composition;

/// <summary>
/// Registers reusable execution and geodata services for future Web and worker roots.
/// </summary>
internal static class ReusableHeavyServiceCollectionExtensions
{
    internal static IServiceCollection AddReusableHeavyComposition(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddReusableHeavyOwners();
        services.AddProcessingExecutionServices();
        return services;
    }

    internal static IServiceCollection AddReusableHeavyOwners(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<AdministrativeAreaResolverService>();
        services.AddSingleton(sp => new OvertureDivisionCacheService(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OvertureDivisionCacheService>>(),
            sp.GetRequiredService<StorageOptions>(),
            sp.GetRequiredService<CountryCodeService>().Iso3ToAlpha2));
        services.AddSingleton(sp => new OverturePlacesService(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OverturePlacesService>>(),
            sp.GetRequiredService<StorageOptions>().DataDir,
            sp.GetRequiredService<StorageOptions>().BundledDataDir));
        services.AddSingleton(sp => new OvertureDivisionsService(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OvertureDivisionsService>>(),
            sp.GetRequiredService<OverturePlacesService>(),
            sp.GetRequiredService<StorageOptions>().DataDir,
            sp.GetRequiredService<StorageOptions>().BundledDataDir,
            alpha2 => sp.GetRequiredService<CountryCodeService>().Alpha2ToIso3(alpha2)));
        services.AddSingleton(sp => new GadmDivisionCacheService(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GadmDivisionCacheService>>(),
            sp.GetRequiredService<StorageOptions>()));
        services.AddSingleton(sp => new GadmDivisionsService(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GadmDivisionsService>>(),
            sp.GetRequiredService<StorageOptions>().DataDir));
        services.AddSingleton<SkippedAssetsRepository>();
        services.AddSingleton<ImmichDbRepository>();
        return services;
    }
}
