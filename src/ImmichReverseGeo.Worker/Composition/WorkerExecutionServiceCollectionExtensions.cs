using System;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ImmichReverseGeo.Web.Composition;

internal static class WorkerExecutionServiceCollectionExtensions
{
    internal static IServiceCollection AddWorkerExecutionComposition(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<AdministrativeAreaResolverService>();
        services.AddSingleton<IProcessingRunConfiguration>(sp => sp.GetRequiredService<ConfigService>());
        services.AddSingleton<IProcessingAssetRepository>(sp => sp.GetRequiredService<ImmichDbRepository>());
        services.AddSingleton<IProcessingSkippedStore>(sp => sp.GetRequiredService<SkippedAssetsRepository>());
        services.AddSingleton<IProcessingAdministrativeResolver>(sp => sp.GetRequiredService<AdministrativeAreaResolverService>());
        services.AddSingleton<ProcessingInfrastructureLookup>();
        services.AddSingleton<IProcessingInfrastructureLookup>(sp => sp.GetRequiredService<ProcessingInfrastructureLookup>());
        services.TryAddSingleton(TimeProvider.System);
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
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IProcessingRunLock>(),
            sp.GetService<WorkerProcessExitOutcomeAccumulator>(),
            sp.GetService<IProcessingRunDomainOperation>()));
        services.AddSingleton<IProcessingRunExecutor>(sp => sp.GetRequiredService<ProcessingRunExecutor>());
        return services;
    }
}
