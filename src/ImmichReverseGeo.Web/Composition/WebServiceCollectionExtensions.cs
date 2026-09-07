using System;
using System.IO;
using ImmichReverseGeo.Core.ApplicationRole;
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

        // Transitional Web dependency until Change 55 moves heavy work out of this root.
        services.AddReusableHeavyComposition();

        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var dataProtectionDirectory = Path.Combine(context.ConfigDirectory, "dataprotection-keys");
        Directory.CreateDirectory(dataProtectionDirectory);
        services.AddDataProtection()
            .SetApplicationName("ImmichReverseGeo")
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory));

        if (scheduledRunsEnabled)
        {
            services.AddSingleton<IScheduledRunWorkCounter>(sp => new RepositoryScheduledRunWorkCounter(
                () => sp.GetRequiredService<ImmichDbRepository>()));
            services.AddSingleton<IScheduledRunWorkGate>(sp => new CountBackedScheduledRunWorkGate(
                sp.GetRequiredService<IScheduledRunWorkCounter>().GetUnprocessedCountAsync));
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
        services.AddSingleton(sp => new ImmichReverseGeo.Web.WorkerFailureRecovery.WorkerRunControlPlane(
            sp.GetRequiredService<IWorkerCommandInvocationBuilder>(),
            sp.GetRequiredService<IChildWorkerLauncher>(),
            sp.GetRequiredService<ProcessingStateEventReporter>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ChildWorkerStartupValidator(sp));
        services.AddHostedService(sp => sp.GetRequiredService<ChildWorkerStartupValidator>());
        return services;
    }
}
