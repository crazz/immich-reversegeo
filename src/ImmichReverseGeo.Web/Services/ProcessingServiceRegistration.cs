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
        services.AddOptions<HostOptions>()
            .Validate(WorkerHostShutdownBudget.IsValid, WorkerHostShutdownBudget.ValidationMessage);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ProcessingState>();
        services.AddSingleton<ProcessingStateEventReporter>();
        services.AddSingleton(sp => new ImmichReverseGeo.Web.WorkerEventStateBridge.WorkerEventStateBridgeFactory(
            sp.GetRequiredService<ProcessingStateEventReporter>()));
        services.AddSingleton<IProcessingEventReporter>(sp => sp.GetRequiredService<ProcessingStateEventReporter>());
        services.AddSingleton<IProcessingScheduleConfiguration>(sp => sp.GetRequiredService<ConfigService>());
        services.AddScoped<IChildProcessingRunBackend>(
            sp => new ChildWorkerProcessingRunBackend(
                sp.GetRequiredService<ImmichReverseGeo.Web.WorkerFailureRecovery.WorkerRunControlPlane>(),
                sp.GetRequiredService<ProcessingRunCoordinator>()));
        services.AddSingleton(sp =>
        {
            _ = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostOptions>>().Value;
            return new ProcessingRunCoordinator(
                sp.GetRequiredService<ProcessingState>(),
                sp.GetRequiredService<ProcessingStateEventReporter>(),
                sp.GetRequiredService<IScheduledRunWorkGate>(),
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

}
