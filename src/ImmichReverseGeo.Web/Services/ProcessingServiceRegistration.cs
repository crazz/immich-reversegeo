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
        services.AddSingleton<IProcessingScheduleConfiguration>(sp => sp.GetRequiredService<ConfigService>());
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
        services.AddSingleton(sp => new ProcessingRunCoordinator(
            sp.GetRequiredService<ProcessingState>(),
            sp.GetRequiredService<ProcessingStateEventReporter>(),
            sp.GetRequiredService<IProcessingRunExecutor>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<ProcessingRunCoordinator>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessingRunCoordinator>.Instance,
            Guid.NewGuid,
            sp.GetService<IProcessingRunCoordinatorObserver>(),
            sp.GetService<IHostApplicationLifetime>()));
        services.AddSingleton<IManualProcessingRunCoordinator>(sp => sp.GetRequiredService<ProcessingRunCoordinator>());
        services.AddSingleton<IScheduledRunTrigger>(sp => sp.GetRequiredService<ProcessingRunCoordinator>());
        services.AddHostedService(sp => sp.GetRequiredService<ProcessingRunCoordinator>());
        services.AddSingleton(sp => new ProcessingBackgroundService(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProcessingBackgroundService>>(),
            sp.GetRequiredService<ProcessingState>(),
            sp.GetRequiredService<IProcessingScheduleConfiguration>(),
            sp.GetRequiredService<SkippedAssetsRepository>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IScheduledRunTrigger>()));
        services.AddHostedService(sp => sp.GetRequiredService<ProcessingBackgroundService>());
        return services;
    }
}
