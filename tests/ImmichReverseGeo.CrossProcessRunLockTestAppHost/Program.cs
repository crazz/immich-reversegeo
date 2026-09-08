using System.Text;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.RunOnce;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace ImmichReverseGeo.CrossProcessRunLockTestAppHost;

internal static class Program
{
    private const int MaximumDiagnosticCharacters = 220;

    private static async Task<int> Main(string[] args)
    {
        if (SignalRunOnceFixtureOptions.TryParse(args, out var signalOptions))
        {
            return await RunSignalFixtureAsync(signalOptions!).ConfigureAwait(false);
        }

        var standardError = Console.OpenStandardError();
        if (!CrossProcessRunLockAppHostOptions.TryParse(args, out var options, out var error))
        {
            await TryWriteDiagnosticAsync(standardError, "cross-process-run-lock-usage", error).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var outcomes = new WorkerProcessExitOutcomeAccumulator();
            var context = ApplicationCompositionContext.Create(
                CompositionEnvironment.Development,
                AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable("DATA_DIR"),
                Environment.GetEnvironmentVariable("CONFIG_DIR"));
            var builder = InternalWorkerHost.CreateBuilder(context, outcomes);
            builder.Services.RemoveAll<NpgsqlDataSource>();
            builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(options!.ConnectionString));
            builder.Services.RemoveAll<IProcessingRunDomainOperation>();
            builder.Services.AddSingleton<IProcessingRunDomainOperation, ControlledProcessingRunDomainOperation>();
            builder.Services.AddSingleton(options!);

            var host = builder.Build();
            return await InternalWorkerHost.RunHostAsync(host, outcomes).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TryWriteDiagnosticAsync(standardError, "cross-process-run-lock-failure", exception.GetType().Name).ConfigureAwait(false);
            return 5;
        }
    }

    private static async Task<int> RunSignalFixtureAsync(SignalRunOnceFixtureOptions options)
    {
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var builder = RunOnceApplication.CreateBuilder(
            DeploymentMode.RunOnce,
            [],
            Environment.GetEnvironmentVariable,
            Console.Out,
            Console.Error,
            outcomes);
        builder.Services.RemoveAll<IWorkerStartupInitializer>();
        builder.Services.AddSingleton<IWorkerStartupInitializer, SignalRunOnceStartupInitializer>();
        builder.Services.RemoveAll<IProcessingRunLock>();
        builder.Services.AddSingleton<IProcessingRunLock, SignalRunOnceProcessingRunLock>();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IHostedService, SignalRunOnceCleanupMarker>();

        var host = builder.Build();
        return await RunOnceApplication.RunHostAsync(host, outcomes).ConfigureAwait(false);
    }

    private static async Task TryWriteDiagnosticAsync(Stream standardError, string category, string detail)
    {
        try
        {
            var boundedDetail = detail.Length <= MaximumDiagnosticCharacters ? detail : detail[..MaximumDiagnosticCharacters];
            var bytes = Encoding.UTF8.GetBytes($"{category}: {boundedDetail}\n");
            await standardError.WriteAsync(bytes).ConfigureAwait(false);
            await standardError.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
