using System;
using System.IO;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.RunOnce;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ImmichReverseGeo.Web.Composition;

internal static class RunOnceServiceCollectionExtensions
{
    internal static IServiceCollection AddRunOnceComposition(
        this IServiceCollection services,
        ApplicationCompositionContext context,
        WorkerProcessExitOutcomeAccumulator outcomes,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        if (!ReferenceEquals(context.DeploymentMode, DeploymentMode.RunOnce))
        {
            throw new ArgumentException("Run-once composition requires the resolved Run-once deployment mode.", nameof(context));
        }

        services.AddInternalWorkerComposition(context);
        services.AddSingleton(context);
        services.AddSingleton(outcomes);
        services.AddSingleton(sp => new RunOnceInfrastructureDependencies(
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<ImmichDbRepository>(),
            sp.GetRequiredService<SkippedAssetsRepository>()));
        services.RemoveAll<IProcessingRunConfiguration>();
        services.AddSingleton<RunOnceProcessingRunConfiguration>();
        services.AddSingleton<IProcessingRunConfiguration>(sp => sp.GetRequiredService<RunOnceProcessingRunConfiguration>());
        services.RemoveAll<IProcessingAssetRepository>();
        services.AddSingleton<RunOnceProcessingAssetRepository>();
        services.AddSingleton<IProcessingAssetRepository>(sp => sp.GetRequiredService<RunOnceProcessingAssetRepository>());
        services.RemoveAll<IProcessingSkippedStore>();
        services.AddSingleton<RunOnceProcessingSkippedStore>();
        services.AddSingleton<IProcessingSkippedStore>(sp => sp.GetRequiredService<RunOnceProcessingSkippedStore>());
        services.AddSingleton(sp => new PostgresqlProcessingRunLock(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProcessingRunLock>(sp => sp.GetRequiredService<PostgresqlProcessingRunLock>());
        services.AddSingleton<IProcessingRunDomainOperation, DefaultProcessingRunDomainOperation>();
        services.AddSingleton<SkippedAssetsWorkerStartupInitializer>();
        services.AddSingleton<IWorkerStartupInitializer>(sp => sp.GetRequiredService<SkippedAssetsWorkerStartupInitializer>());
        services.AddSingleton(sp => new RunOnceProcessingEventReporter(standardOutput, standardError));
        services.AddSingleton<IProcessingEventReporter>(sp => sp.GetRequiredService<RunOnceProcessingEventReporter>());
        return services;
    }
}
