using System;
using ImmichReverseGeo.Core.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Web.Services;

internal static class ProcessingServiceRegistration
{
    internal static IServiceCollection AddProcessingControlPlaneServices(this IServiceCollection services)
    {
        AddProcessingCoordinatorServices(services, scheduledRunsSupported: true);
        services.AddSingleton<IProcessingScheduleConfiguration>(sp => sp.GetRequiredService<ConfigService>());
        services.AddSingleton<IScheduledRunTrigger>(sp => sp.GetRequiredService<ProcessingRunCoordinator>());
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

    internal static IServiceCollection AddManualProcessingControlPlaneServices(this IServiceCollection services)
    {
        AddProcessingCoordinatorServices(services, scheduledRunsSupported: false);
        return services;
    }

    private static void AddProcessingCoordinatorServices(
        IServiceCollection services,
        bool scheduledRunsSupported)
    {
        services.AddOptions<HostOptions>()
            .Validate(WorkerHostShutdownBudget.IsValid, WorkerHostShutdownBudget.ValidationMessage);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new WorkerJobCoordinator(
            ImmichReverseGeo.Core.WorkerJobs.WorkerJobDescriptors.Registered,
            timeProvider: sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ProcessingState>();
        services.AddSingleton<ProcessingStateEventReporter>();
        services.AddSingleton(sp => new ImmichReverseGeo.Web.WorkerEventStateBridge.WorkerEventStateBridgeFactory(
            sp.GetRequiredService<ProcessingStateEventReporter>()));
        services.AddSingleton<IProcessingEventReporter>(sp => sp.GetRequiredService<ProcessingStateEventReporter>());
        services.AddScoped<IChildProcessingRunBackend>(
            sp => new ChildWorkerProcessingRunBackend(
                sp.GetRequiredService<ImmichReverseGeo.Web.WorkerFailureRecovery.WorkerRunControlPlane>(),
                sp.GetRequiredService<ProcessingRunCoordinator>()));
        services.AddSingleton(sp =>
        {
            _ = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostOptions>>().Value;
            var state = sp.GetRequiredService<ProcessingState>();
            var reporter = sp.GetRequiredService<ProcessingStateEventReporter>();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ProcessingRunCoordinator>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessingRunCoordinator>.Instance;
            var observer = sp.GetService<IProcessingRunCoordinatorObserver>();
            var lifetime = sp.GetService<IHostApplicationLifetime>();
            var timeProvider = sp.GetRequiredService<TimeProvider>();
            return scheduledRunsSupported
                ? new ProcessingRunCoordinator(
                    state,
                    reporter,
                    sp.GetRequiredService<IScheduledRunWorkGate>(),
                    scopeFactory,
                    logger,
                    Guid.NewGuid,
                    observer,
                    sp.GetRequiredService<WorkerJobCoordinator>(),
                    lifetime,
                    timeProvider)
                : ProcessingRunCoordinator.CreateManualOnly(
                    state,
                    reporter,
                    scopeFactory,
                    logger,
                    Guid.NewGuid,
                    observer,
                    sp.GetRequiredService<WorkerJobCoordinator>(),
                    lifetime,
                    timeProvider);
        });
        services.AddSingleton<IManualProcessingRunCoordinator>(sp => sp.GetRequiredService<ProcessingRunCoordinator>());
        services.AddHostedService(sp => sp.GetRequiredService<ProcessingRunCoordinator>());
    }

}
