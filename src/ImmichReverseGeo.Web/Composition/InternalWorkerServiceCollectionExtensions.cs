using System;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Web.Composition;

/// <summary>
/// Registers the builder-neutral execution graph consumed by the future internal-worker host.
/// </summary>
internal static class InternalWorkerServiceCollectionExtensions
{
    internal static IServiceCollection AddInternalWorkerComposition(
        this IServiceCollection services,
        ApplicationCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        services.AddSharedComposition(context);
        services.AddReusableHeavyComposition();
        return services;
    }

    internal static IServiceCollection AddInternalWorkerHostServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<SkippedAssetsWorkerStartupInitializer>();
        services.AddSingleton<IWorkerStartupInitializer>(sp => sp.GetRequiredService<SkippedAssetsWorkerStartupInitializer>());
        services.AddSingleton<WorkerTransportNotConfigured>();
        services.AddSingleton<IWorkerTransportAvailability>(sp => sp.GetRequiredService<WorkerTransportNotConfigured>());
        services.AddSingleton<TransitionalWorkerPreRequestFinality>();
        services.AddSingleton<IWorkerPreRequestFinality>(sp => sp.GetRequiredService<TransitionalWorkerPreRequestFinality>());
        services.AddHostedService<InternalWorkerLifecycleService>();
        return services;
    }
}
