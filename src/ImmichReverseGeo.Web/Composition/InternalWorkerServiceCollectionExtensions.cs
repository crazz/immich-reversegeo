using System;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

    internal static IServiceCollection AddInternalWorkerHostServices(
        this IServiceCollection services,
        IWorkerNdjsonOutputStreamFactory stdoutFactory,
        WorkerProcessExitOutcomeAccumulator outcomes)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(stdoutFactory);
        ArgumentNullException.ThrowIfNull(outcomes);

        services.AddSingleton(outcomes);
        services.AddSingleton<SkippedAssetsWorkerStartupInitializer>();
        services.AddSingleton<IWorkerStartupInitializer>(sp => sp.GetRequiredService<SkippedAssetsWorkerStartupInitializer>());
        services.AddSingleton<WorkerStdinTransportConfigured>();
        services.AddSingleton<IWorkerTransportAvailability>(sp => sp.GetRequiredService<WorkerStdinTransportConfigured>());
        services.AddSingleton<IWorkerStandardInputStreamFactory, WorkerStandardInputStreamFactory>();
        services.AddSingleton<WorkerStdinRequestSource>(sp => new WorkerStdinRequestSource(
            sp.GetRequiredService<IWorkerStandardInputStreamFactory>(),
            sp.GetRequiredService<ILogger<WorkerStdinRequestSource>>()));
        services.AddSingleton<IInitialProcessingRunAcquirer>(sp => sp.GetRequiredService<WorkerStdinRequestSource>());
        services.AddSingleton<WorkerStdinAcceptedRunFinality>();
        services.AddSingleton<IWorkerAcceptedRunFinality>(sp => sp.GetRequiredService<WorkerStdinAcceptedRunFinality>());
        services.AddSingleton<TransitionalWorkerPreRequestFinality>();
        services.AddSingleton<IWorkerPreRequestFinality>(sp => sp.GetRequiredService<TransitionalWorkerPreRequestFinality>());
        services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(stdoutFactory);
        services.AddSingleton<WorkerNdjsonEmitter>(sp => WorkerNdjsonEmitter.CreateProduction(
            sp.GetRequiredService<IWorkerNdjsonOutputStreamFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<WorkerNdjsonEmitter>>(),
            sp.GetRequiredService<WorkerProcessExitOutcomeAccumulator>()));
        services.AddSingleton<IWorkerReadinessPublisher>(sp => sp.GetRequiredService<WorkerNdjsonEmitter>());
        services.AddSingleton<WorkerNdjsonProcessingEventReporter>(sp => new WorkerNdjsonProcessingEventReporter(
            sp.GetRequiredService<WorkerNdjsonEmitter>()));
        services.AddSingleton<IProcessingEventReporter>(sp => sp.GetRequiredService<WorkerNdjsonProcessingEventReporter>());
        services.AddSingleton<InternalWorkerLifecycleService>();
        services.AddHostedService(sp => sp.GetRequiredService<InternalWorkerLifecycleService>());
        return services;
    }
}
