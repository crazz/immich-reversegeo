using System;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

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
        services.AddWorkerExecutionComposition();
        return services;
    }

    internal static IServiceCollection AddInternalWorkerHostServices(
        this IServiceCollection services,
        IWorkerNdjsonOutputStreamFactory stdoutFactory,
        WorkerProcessExitOutcomeAccumulator outcomes,
        InternalWorkerProtocolVersion protocolVersion = InternalWorkerProtocolVersion.V1)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(stdoutFactory);
        ArgumentNullException.ThrowIfNull(outcomes);
        if (!Enum.IsDefined(protocolVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        }

        services.AddSingleton(outcomes);
        services.AddSingleton(sp => new PostgresqlProcessingRunLock(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProcessingRunLock>(sp => sp.GetRequiredService<PostgresqlProcessingRunLock>());
        services.AddSingleton<IProcessingRunDomainOperation, DefaultProcessingRunDomainOperation>();
        services.AddSingleton<SkippedAssetsWorkerStartupInitializer>();
        services.AddSingleton<IWorkerStartupInitializer>(sp => sp.GetRequiredService<SkippedAssetsWorkerStartupInitializer>());
        services.AddSingleton<WorkerStdinTransportConfigured>();
        services.AddSingleton<IWorkerTransportAvailability>(sp => sp.GetRequiredService<WorkerStdinTransportConfigured>());
        services.AddSingleton<IWorkerStandardInputStreamFactory, WorkerStandardInputStreamFactory>();
        if (protocolVersion == InternalWorkerProtocolVersion.V2)
        {
            services.AddSingleton<ICacheMutationSourceOperation>(sp =>
                sp.GetRequiredService<OvertureDivisionCacheService>());
            services.AddSingleton<ICacheMutationSourceOperation>(sp =>
                sp.GetRequiredService<GadmDivisionCacheService>());
            services.AddSingleton<IWorkerCacheMutationOperation>(sp =>
                new WorkerCacheMutationOperation(
                    sp.GetServices<ICacheMutationSourceOperation>()));
            services.AddSingleton(sp => new ProcessAssetsWorkerJobHandler(
                sp.GetRequiredService<IWorkerStartupInitializer>(),
                sp.GetRequiredService<IProcessingRunExecutor>(),
                sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IWorkerJobHandlerRegistration>(
                new WorkerJobHandlerRegistration<ProcessAssetsRequest, ProcessAssetsResult>(
                    WorkerJobDescriptors.ProcessAssets,
                    sp => sp.GetRequiredService<ProcessAssetsWorkerJobHandler>()));
            services.AddSingleton(sp => new CoordinateLookupWorkerJobHandler(
                sp.GetRequiredService<CoordinateLookupOperation>(),
                sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IWorkerJobHandlerRegistration>(
                new WorkerJobHandlerRegistration<CoordinateLookupRequest, CoordinateLookupResult>(
                    WorkerJobDescriptors.CoordinateLookup,
                    sp => sp.GetRequiredService<CoordinateLookupWorkerJobHandler>()));
            services.AddSingleton(sp => new CacheMutationWorkerJobHandler(
                sp.GetRequiredService<IWorkerCacheMutationOperation>(),
                sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IWorkerJobHandlerRegistration>(
                new WorkerJobHandlerRegistration<CacheMutationRequest, CacheMutationResult>(
                    WorkerJobDescriptors.CacheMutation,
                    sp => sp.GetRequiredService<CacheMutationWorkerJobHandler>()));
            services.AddSingleton<IWorkerJobRequestSemanticValidator>(sp =>
                new CacheMutationRequestSemanticValidator(
                    sp.GetRequiredService<CountryCodeService>()));
            services.AddSingleton(sp => new WorkerJobHandlerRegistry(
                sp.GetServices<IWorkerJobHandlerRegistration>()));
        }

        services.AddSingleton<WorkerStdinRequestSource>(sp =>
        {
            WorkerJobHandlerRegistry? registry = protocolVersion == InternalWorkerProtocolVersion.V2
                ? sp.GetRequiredService<WorkerJobHandlerRegistry>()
                : null;
            return new WorkerStdinRequestSource(
                sp.GetRequiredService<IWorkerStandardInputStreamFactory>(),
                sp.GetRequiredService<ILogger<WorkerStdinRequestSource>>(),
                protocolVersion,
                registry?.SupportedJobDescriptors,
                protocolVersion == InternalWorkerProtocolVersion.V2
                    ? sp.GetRequiredService<IWorkerJobRequestSemanticValidator>()
                    : null);
        });
        services.AddSingleton<IInitialProcessingRunAcquirer>(sp => sp.GetRequiredService<WorkerStdinRequestSource>());
        services.AddSingleton<WorkerStdinAcceptedRunFinality>();
        services.AddSingleton<IWorkerAcceptedRunFinality>(sp => sp.GetRequiredService<WorkerStdinAcceptedRunFinality>());
        services.AddSingleton<TransitionalWorkerPreRequestFinality>();
        services.AddSingleton<IWorkerPreRequestFinality>(sp => sp.GetRequiredService<TransitionalWorkerPreRequestFinality>());
        services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(stdoutFactory);
        services.AddSingleton<WorkerNdjsonEmitter>(sp =>
        {
            WorkerJobReadyPayload? ready = protocolVersion == InternalWorkerProtocolVersion.V2
                ? new WorkerJobReadyPayload(
                    sp.GetRequiredService<WorkerJobHandlerRegistry>().SupportedJobKinds)
                : null;
            return WorkerNdjsonEmitter.CreateProduction(
                sp.GetRequiredService<IWorkerNdjsonOutputStreamFactory>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<WorkerNdjsonEmitter>>(),
                sp.GetRequiredService<WorkerProcessExitOutcomeAccumulator>(),
                protocolVersion,
                ready);
        });
        services.AddSingleton<IWorkerReadinessPublisher>(sp => sp.GetRequiredService<WorkerNdjsonEmitter>());
        if (protocolVersion == InternalWorkerProtocolVersion.V1)
        {
            services.AddSingleton<WorkerNdjsonProcessingEventReporter>(sp => new WorkerNdjsonProcessingEventReporter(
                sp.GetRequiredService<WorkerNdjsonEmitter>()));
            services.AddSingleton<IProcessingEventReporter>(sp => sp.GetRequiredService<WorkerNdjsonProcessingEventReporter>());
        }

        services.AddSingleton(sp => new InternalWorkerLifecycleService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetRequiredService<ILogger<InternalWorkerLifecycleService>>(),
            sp.GetRequiredService<WorkerProcessExitOutcomeAccumulator>(),
            protocolVersion));
        services.AddHostedService(sp => sp.GetRequiredService<InternalWorkerLifecycleService>());
        return services;
    }
}
