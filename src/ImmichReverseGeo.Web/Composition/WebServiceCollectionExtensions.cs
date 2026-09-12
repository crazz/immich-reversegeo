using System;
using System.IO;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Web.Composition;

internal static class WebServiceCollectionExtensions
{
    internal static IServiceCollection AddStandardWebComposition(
        this IServiceCollection services,
        ApplicationCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(context.DeploymentMode, DeploymentMode.Standard))
        {
            throw new ArgumentException("Standard Web composition requires the resolved Standard deployment mode.", nameof(context));
        }

        return services.AddWebComposition(context);
    }

    internal static IServiceCollection AddWebComposition(
        this IServiceCollection services,
        ApplicationCompositionContext context)
    {
        return AddWebCompositionCore(services, context, scheduledRunsEnabled: true);
    }

    internal static IServiceCollection AddWebOnlyWebComposition(
        this IServiceCollection services,
        ApplicationCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(context.DeploymentMode, DeploymentMode.WebOnly))
        {
            throw new ArgumentException("Web-only composition requires the resolved Web-only deployment mode.", nameof(context));
        }

        return AddWebCompositionCore(services, context, scheduledRunsEnabled: false);
    }

    private static IServiceCollection AddWebCompositionCore(
        IServiceCollection services,
        ApplicationCompositionContext context,
        bool scheduledRunsEnabled)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        services.AddSharedComposition(context);
        services.AddSingleton(context);

        if (ReferenceEquals(context.DeploymentMode, DeploymentMode.Standard)
            || ReferenceEquals(context.DeploymentMode, DeploymentMode.WebOnly))
        {
            services.AddSingleton(_ => new ProcessAssetsWebStatus(context.DeploymentMode));
            services.AddSingleton<IProcessAssetsWebStatus>(sp =>
                sp.GetRequiredService<ProcessAssetsWebStatus>());
            services.AddSingleton<IProcessAssetsWorkerStatusSink>(sp =>
                sp.GetRequiredService<ProcessAssetsWebStatus>());
        }

        services.AddSingleton<SkippedAssetsRepository>();
        services.AddSingleton<ImmichDbRepository>();

        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var dataProtectionDirectory = Path.Combine(context.ConfigDirectory, "dataprotection-keys");
        Directory.CreateDirectory(dataProtectionDirectory);
        services.AddDataProtection()
            .SetApplicationName("ImmichReverseGeo")
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory));

        services.AddSingleton(sp => new WorkerJobCoordinator(
            WorkerJobDescriptors.Registered,
            sp.GetService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IWorkerJobAdmissionGate>(sp =>
            sp.GetRequiredService<WorkerJobCoordinator>());
        services.AddSingleton<IWorkerJobArbitrationDiagnostics>(sp =>
            sp.GetRequiredService<WorkerJobCoordinator>());
        services.AddHostedService(sp => sp.GetRequiredService<WorkerJobCoordinator>());
        services.AddSingleton<IImmichLocationResetStore>(sp =>
            sp.GetRequiredService<ImmichDbRepository>());
        services.AddSingleton<ILocationValueOptionsReader>(sp =>
            sp.GetRequiredService<ImmichDbRepository>());
        services.AddSingleton<ISkippedAssetsMaintenanceStore>(sp =>
            sp.GetRequiredService<SkippedAssetsRepository>());
        services.AddSingleton<ISkippedAssetsCountReader>(sp =>
            sp.GetRequiredService<SkippedAssetsRepository>());
        services.AddSingleton<DatabaseMaintenanceController>();
        services.AddSingleton<IDatabaseMaintenanceController>(sp =>
            sp.GetRequiredService<DatabaseMaintenanceController>());
        services.AddSingleton<PhysicalCacheDeletionFileSystem>();
        services.AddSingleton<ICacheDeletionFileSystem>(sp =>
            sp.GetRequiredService<PhysicalCacheDeletionFileSystem>());
        services.AddSingleton<CacheDeletionCommand>();
        services.AddSingleton<CacheDeletionPageControllerFactory>();
        services.AddSingleton<CacheInventoryOptions>();
        services.AddSingleton<PhysicalCacheInventoryFileSystem>();
        services.AddSingleton<ICacheInventoryFileSystem>(sp =>
            sp.GetRequiredService<PhysicalCacheInventoryFileSystem>());
        services.AddSingleton<CacheInventorySqliteMetadataReader>();
        services.AddSingleton<ICacheInventoryMetadataReader>(sp =>
            sp.GetRequiredService<CacheInventorySqliteMetadataReader>());
        services.AddSingleton<CacheInventoryStorageScanner>();
        services.AddSingleton<ICacheInventoryStorageScanner>(sp =>
            sp.GetRequiredService<CacheInventoryStorageScanner>());
        services.AddSingleton<CacheInventoryService>();
        services.AddSingleton<ICacheInventory>(sp =>
            sp.GetRequiredService<CacheInventoryService>());
        services.AddSingleton<ICacheInventoryInvalidator>(sp =>
            sp.GetRequiredService<CacheInventoryService>());
        services.AddSingleton<CacheInventoryDeletionOperations>();
        services.AddSingleton<CacheInventoryDeletionPageControllerFactory>();

        if (scheduledRunsEnabled)
        {
            services.AddSingleton<IScheduledRunWorkProbe>(sp => new RepositoryScheduledRunWorkProbe(
                () => sp.GetRequiredService<ImmichDbRepository>()));
            services.AddSingleton(sp => new ExistenceProcessingWorkDetector(
                sp.GetRequiredService<IScheduledRunWorkProbe>().HasUnprocessedAssetsAsync));
            services.AddSingleton<IProcessingWorkDetector>(sp => sp.GetRequiredService<ExistenceProcessingWorkDetector>());
            services.AddProcessingControlPlaneServices();
        }
        else
        {
            services.AddManualProcessingControlPlaneServices();
        }
        services.AddSingleton<SystemChildProcessFactory>();
        services.AddSingleton<IChildProcessFactory>(sp => sp.GetRequiredService<SystemChildProcessFactory>());
        services.AddSingleton(sp => new ChildWorkerLauncher(sp.GetRequiredService<IChildProcessFactory>()));
        services.AddSingleton<IChildWorkerLauncher>(sp => sp.GetRequiredService<ChildWorkerLauncher>());
        services.AddSingleton<WorkerCommandAmbientRuntimeObservationSource>();
        services.AddSingleton<IWorkerCommandRuntimeObservationSource>(sp => sp.GetRequiredService<WorkerCommandAmbientRuntimeObservationSource>());
        services.AddSingleton(sp => new WorkerCommandRuntimeFactsCapture(sp.GetRequiredService<IWorkerCommandRuntimeObservationSource>()));
        services.AddSingleton<IWorkerCommandRuntimeFactsCapture>(sp => sp.GetRequiredService<WorkerCommandRuntimeFactsCapture>());
        services.AddSingleton(sp => new WorkerCommandInvocationBuilder(sp.GetRequiredService<IWorkerCommandRuntimeFactsCapture>()));
        services.AddSingleton<IWorkerCommandInvocationBuilder>(sp => sp.GetRequiredService<WorkerCommandInvocationBuilder>());
        services.AddSingleton<ConfigCoordinateLookupSettingsSnapshotProvider>();
        services.AddSingleton<ICoordinateLookupSettingsSnapshotProvider>(sp =>
            sp.GetRequiredService<ConfigCoordinateLookupSettingsSnapshotProvider>());
        services.AddSingleton<CoordinateLookupWorkerClient>();
        services.AddSingleton<ICoordinateLookupWorkerClient>(sp =>
            sp.GetRequiredService<CoordinateLookupWorkerClient>());
        services.AddSingleton<CoordinateLookupPageControllerHostLifetime>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<CoordinateLookupPageControllerHostLifetime>());
        services.AddSingleton<CoordinateLookupPageControllerFactory>();
        services.AddSingleton<CacheMutationWorkerClient>();
        services.AddSingleton<CacheMutationPageControllerHostLifetime>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<CacheMutationPageControllerHostLifetime>());
        services.AddSingleton<CacheMutationPageControllerFactory>();
        services.AddSingleton<CacheInventoryMutationWorkerClient>(sp => new(
            sp.GetRequiredService<CacheMutationWorkerClient>(),
            sp.GetRequiredService<ICacheInventoryInvalidator>()));
        services.AddSingleton<ICacheMutationWorkerClient>(sp =>
            sp.GetRequiredService<CacheInventoryMutationWorkerClient>());
        services.AddSingleton(sp => new ImmichReverseGeo.Web.WorkerFailureRecovery.WorkerRunControlPlane(
            sp.GetRequiredService<IWorkerCommandInvocationBuilder>(),
            sp.GetRequiredService<IChildWorkerLauncher>(),
            sp.GetRequiredService<ProcessingStateEventReporter>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IProcessAssetsWorkerStatusSink>()));
        services.AddSingleton(sp => new ChildWorkerStartupValidator(sp));
        services.AddHostedService(sp => sp.GetRequiredService<ChildWorkerStartupValidator>());
        return services;
    }
}
