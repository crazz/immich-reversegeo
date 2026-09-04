using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.WorkerHost;

internal static class InternalWorkerHost
{
    internal static HostApplicationBuilder CreateBuilder(
        ApplicationCompositionContext context,
        WorkerProcessExitOutcomeAccumulator outcomes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outcomes);

        var builder = CreateRawBuilder();
        Configure(builder, context, outcomes);
        return builder;
    }

    internal static IHost Build(
        ApplicationCompositionContext context,
        WorkerProcessExitOutcomeAccumulator outcomes)
    {
        return Build(CreateBuilder(context, outcomes));
    }

    internal static Task<int> RunProductionAsync(WorkerProcessExitOutcomeAccumulator outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        try
        {
            return RunAsync(
                Directory.GetCurrentDirectory(),
                Environment.GetEnvironmentVariable("DATA_DIR"),
                Environment.GetEnvironmentVariable("CONFIG_DIR"),
                outcomes);
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            outcomes.Add(WorkerProcessExitFact.StartupInfrastructure());
            return Task.FromResult(outcomes.Fact.ExitCode);
        }
    }

    internal static async Task<int> RunAsync(
        string contentRoot,
        string? dataDirectory,
        string? configDirectory,
        WorkerProcessExitOutcomeAccumulator outcomes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentNullException.ThrowIfNull(outcomes);

        try
        {
            var builder = CreateRawBuilder();
            var environment = builder.Environment.IsDevelopment()
                ? CompositionEnvironment.Development
                : CompositionEnvironment.Production;
            var context = ApplicationCompositionContext.Create(
                environment,
                contentRoot,
                dataDirectory,
                configDirectory);
            Configure(builder, context, outcomes);
            return await RunHostAsync(Build(builder), outcomes);
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            outcomes.Add(WorkerProcessExitFact.StartupInfrastructure());
            return outcomes.Fact.ExitCode;
        }
    }

    internal static Task<int> RunHostAsync(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return RunHostAsync(
            host,
            host.Services.GetRequiredService<WorkerProcessExitOutcomeAccumulator>());
    }

    internal static async Task<int> RunHostAsync(
        IHost host,
        WorkerProcessExitOutcomeAccumulator outcomes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(outcomes);

        ILogger? logger = null;
        InternalWorkerLifecycleService? lifecycle = null;
        OutOfMemoryException? firstFatalOutOfMemory = null;
        var servicesAvailable = true;

        try
        {
            var registeredOutcomes = host.Services.GetRequiredService<WorkerProcessExitOutcomeAccumulator>();
            if (!ReferenceEquals(registeredOutcomes, outcomes))
            {
                throw new InvalidOperationException("The worker host must use the caller-owned outcome accumulator.");
            }

            logger = host.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(InternalWorkerHost));
            lifecycle = host.Services.GetService<InternalWorkerLifecycleService>();
        }
        catch (OutOfMemoryException exception)
        {
            firstFatalOutOfMemory = exception;
            servicesAvailable = false;
        }
        catch
        {
            servicesAvailable = false;
            outcomes.Add(WorkerProcessExitFact.StartupInfrastructure());
        }

        if (servicesAvailable)
        {
            try
            {
                await host.StartAsync();
                await host.WaitForShutdownAsync();
                if (lifecycle?.ExecuteTask is { } lifecycleTask)
                {
                    await lifecycleTask;
                }
            }
            catch (OutOfMemoryException exception)
            {
                firstFatalOutOfMemory ??= exception;
            }
            catch
            {
                outcomes.Add(WorkerProcessExitFact.StartupInfrastructure());
            }
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
        catch (OutOfMemoryException exception)
        {
            firstFatalOutOfMemory ??= exception;
        }
        catch
        {
            outcomes.Add(WorkerProcessExitFact.CleanupInfrastructure());
            LogSafely(logger, "worker-host-dispose-failed");
        }

        if (firstFatalOutOfMemory is not null)
        {
            ExceptionDispatchInfo.Capture(firstFatalOutOfMemory).Throw();
        }

        return outcomes.Fact.ExitCode;
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

    private static void Configure(
        HostApplicationBuilder builder,
        ApplicationCompositionContext context,
        WorkerProcessExitOutcomeAccumulator outcomes)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddInternalWorkerComposition(context);
        builder.Services.AddInternalWorkerHostServices(
            new WorkerNdjsonStandardOutputStreamFactory(),
            outcomes);
    }
}

internal static class InternalWorkerProcess
{
    internal static int Run(
        IReadOnlyList<string> selectedArguments,
        TextWriter errorWriter,
        Func<WorkerProcessExitOutcomeAccumulator, Task<int>> runWorkerAsync)
    {
        ArgumentNullException.ThrowIfNull(selectedArguments);
        ArgumentNullException.ThrowIfNull(errorWriter);
        ArgumentNullException.ThrowIfNull(runWorkerAsync);

        if (selectedArguments.Count != 0)
        {
            throw new InvalidOperationException("Internal worker arguments must have been consumed before host construction.");
        }

        var outcomes = new WorkerProcessExitOutcomeAccumulator();

        try
        {
            runWorkerAsync(outcomes).GetAwaiter().GetResult();
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            outcomes.Add(WorkerProcessExitFact.StartupInfrastructure());
        }

        if (!outcomes.HasFact)
        {
            outcomes.Add(WorkerProcessExitFact.StartupInfrastructure());
        }

        return InternalWorkerProcessExitBoundary.Complete(outcomes.Fact, errorWriter);
    }

    internal static int CompleteInvalidInvocation(TextWriter errorWriter)
    {
        return InternalWorkerProcessExitBoundary.Complete(WorkerProcessExitFact.InputInvalid(), errorWriter);
    }
}

internal static class InternalWorkerProcessExitBoundary
{
    internal static int Complete(WorkerProcessExitFact fact, TextWriter errorWriter)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(errorWriter);

        if (fact.ExitCode != WorkerProcessExitCodes.Completed)
        {
            try
            {
                errorWriter.WriteLine(fact.Diagnostic.FormatFinalSummary());
                errorWriter.Flush();
            }
            catch
            {
            }
        }

        return fact.ExitCode;
    }
}
