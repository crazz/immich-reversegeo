using System;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Web.Services;

internal static class ProcessingServiceRegistration
{
    internal static IServiceCollection AddProcessingServices(this IServiceCollection services)
    {
        return services.AddProcessingServices(ProcessingBackendKind.ChildWorker);
    }

    internal static IServiceCollection AddProcessingServices(
        this IServiceCollection services,
        ProcessingBackendKind backend)
    {
        TemporaryProcessingBackendSelection.Validate(backend);
        services.AddProcessingExecutionServices();
        services.AddProcessingControlPlaneServices(backend);
        return services;
    }

    internal static IServiceCollection AddProcessingControlPlaneServices(this IServiceCollection services)
    {
        return services.AddProcessingControlPlaneServices(ProcessingBackendKind.ChildWorker);
    }

    internal static IServiceCollection AddProcessingControlPlaneServices(
        this IServiceCollection services,
        ProcessingBackendKind backend)
    {
        TemporaryProcessingBackendSelection.Validate(backend);
        services.AddOptions<HostOptions>()
            .Validate(WorkerHostShutdownBudget.IsValid, WorkerHostShutdownBudget.ValidationMessage);
        services.AddSingleton<ProcessingState>();
        services.AddSingleton<ProcessingStateEventReporter>();
        services.AddSingleton(sp => new ImmichReverseGeo.Web.WorkerEventStateBridge.WorkerEventStateBridgeFactory(
            sp.GetRequiredService<ProcessingStateEventReporter>()));
        services.AddSingleton<IProcessingEventReporter>(sp => sp.GetRequiredService<ProcessingStateEventReporter>());
        services.AddSingleton<IProcessingScheduleConfiguration>(sp => sp.GetRequiredService<ConfigService>());
        services.AddSingleton(new TemporaryProcessingBackendSelection(backend));
        // Temporary Phase 5 seam: blocks 34 and 35 select ChildWorker internally, block 36
        // can resolve neither backend, block 37 changes this default to ChildWorker while
        // retaining explicit InProcess fallback, and block 38 removes the selector plus it.
        services.AddKeyedScoped<IProcessingRunBackend, InProcessProcessingRunBackend>(ProcessingBackendKind.InProcess);
        services.AddKeyedScoped<IProcessingRunBackend>(
            ProcessingBackendKind.ChildWorker,
            (sp, _) => new ChildWorkerProcessingRunBackend(
                sp.GetRequiredService<ImmichReverseGeo.Web.WorkerFailureRecovery.WorkerRunControlPlane>(),
                sp.GetRequiredService<ProcessingRunCoordinator>()));
        services.AddSingleton(sp =>
        {
            _ = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostOptions>>().Value;
            return new ProcessingRunCoordinator(
                sp.GetRequiredService<ProcessingState>(),
                sp.GetRequiredService<ProcessingStateEventReporter>(),
                sp.GetRequiredService<IScheduledRunWorkGate>(),
                sp.GetRequiredService<TemporaryProcessingBackendSelection>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<ProcessingRunCoordinator>>()
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessingRunCoordinator>.Instance,
                Guid.NewGuid,
                sp.GetService<IProcessingRunCoordinatorObserver>(),
                sp.GetService<IHostApplicationLifetime>(),
                sp.GetRequiredService<TimeProvider>());
        });
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

    internal static IServiceCollection AddProcessingExecutionServices(this IServiceCollection services)
    {
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
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IProcessingRunLock>(),
            sp.GetService<WorkerProcessExitOutcomeAccumulator>(),
            sp.GetService<IProcessingRunDomainOperation>()));
        services.AddSingleton<IProcessingRunExecutor>(sp => sp.GetRequiredService<ProcessingRunExecutor>());
        return services;
    }
}
