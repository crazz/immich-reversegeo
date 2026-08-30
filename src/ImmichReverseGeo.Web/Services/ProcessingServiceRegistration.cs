using System;
using ImmichReverseGeo.Core.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Web.Services;

internal static class ProcessingServiceRegistration
{
    internal static IServiceCollection AddProcessingServices(this IServiceCollection services)
    {
        services.AddSingleton<ProcessingState>();
        services.AddSingleton<ProcessingStateEventReporter>();
        services.AddSingleton<IProcessingEventReporter>(sp => sp.GetRequiredService<ProcessingStateEventReporter>());
        services.AddSingleton<IProcessingRunConfiguration>(sp => sp.GetRequiredService<ConfigService>());
        services.AddSingleton<IProcessingAssetRepository>(sp => sp.GetRequiredService<ImmichDbRepository>());
        services.AddSingleton<IProcessingSkippedStore>(sp => sp.GetRequiredService<SkippedAssetsRepository>());
        services.AddSingleton<IProcessingAdministrativeResolver>(sp => sp.GetRequiredService<AdministrativeAreaResolverService>());
        services.AddSingleton<ProcessingInfrastructureLookup>();
        services.AddSingleton<IProcessingInfrastructureLookup>(sp => sp.GetRequiredService<ProcessingInfrastructureLookup>());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ProcessingRunDelay>();
        services.AddSingleton<IProcessingRunDelay>(sp => sp.GetRequiredService<ProcessingRunDelay>());
        services.AddSingleton(sp => new ProcessingRunExecutor(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProcessingBackgroundService>>(),
            sp.GetRequiredService<IProcessingRunConfiguration>(),
            sp.GetRequiredService<IProcessingAssetRepository>(),
            sp.GetRequiredService<IProcessingSkippedStore>(),
            sp.GetRequiredService<IProcessingAdministrativeResolver>(),
            sp.GetRequiredService<IProcessingInfrastructureLookup>(),
            sp.GetRequiredService<IProcessingRunDelay>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProcessingRunExecutor>(sp => sp.GetRequiredService<ProcessingRunExecutor>());
        services.AddSingleton<ProcessingBackgroundService>();
        services.AddHostedService(sp => sp.GetRequiredService<ProcessingBackgroundService>());
        return services;
    }
}
