using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using ImmichReverseGeo.Web.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.WorkerHost;

internal static class InternalWorkerHost
{
    internal static HostApplicationBuilder CreateBuilder(ApplicationCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var builder = CreateRawBuilder();
        Configure(builder, context);
        return builder;
    }

    internal static IHost Build(ApplicationCompositionContext context)
    {
        return Build(CreateBuilder(context));
    }

    internal static async Task RunAsync(string contentRoot, string? dataDirectory, string? configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        var builder = CreateRawBuilder();
        var environment = builder.Environment.IsDevelopment()
            ? CompositionEnvironment.Development
            : CompositionEnvironment.Production;
        var context = ApplicationCompositionContext.Create(
            environment,
            contentRoot,
            dataDirectory,
            configDirectory);
        Configure(builder, context);
        await RunHostAsync(Build(builder));
    }

    internal static async Task RunHostAsync(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        Exception? primaryFailure = null;
        ILogger? logger = null;

        try
        {
            logger = host.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(InternalWorkerHost));
            await host.StartAsync();
            await host.WaitForShutdownAsync();
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        try
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }
        }
        catch
        {
            primaryFailure ??= new InvalidOperationException("worker-cleanup-failed");
            LogSafely(logger, "worker-host-dispose-failed");
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    private static IHost Build(HostApplicationBuilder builder)
    {
        return builder.Build();
    }

    private static HostApplicationBuilder CreateRawBuilder()
    {
        return Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
    }

    private static void LogSafely(ILogger? logger, string category)
    {
        try
        {
            logger?.LogWarning(category);
        }
        catch
        {
        }
    }

    private static void Configure(HostApplicationBuilder builder, ApplicationCompositionContext context)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddInternalWorkerComposition(context);
        builder.Services.AddInternalWorkerHostServices();
    }
}
