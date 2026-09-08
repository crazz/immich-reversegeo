using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.RunOnce;

internal static class RunOnceApplication
{
    internal static HostApplicationBuilder CreateBuilder(
        DeploymentMode deploymentMode,
        IReadOnlyList<string> arguments,
        Func<string, string?> environmentVariableReader,
        TextWriter standardOutput,
        TextWriter standardError,
        WorkerProcessExitOutcomeAccumulator outcomes)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environmentVariableReader);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentNullException.ThrowIfNull(outcomes);
        if (!ReferenceEquals(deploymentMode, DeploymentMode.RunOnce))
        {
            throw new ArgumentException("Run-once requires the resolved Run-once deployment mode.", nameof(deploymentMode));
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = arguments.ToArray()
        });
        var environment = builder.Environment.IsDevelopment()
            ? CompositionEnvironment.Development
            : CompositionEnvironment.Production;
        var context = ApplicationCompositionContext.Create(
            environment,
            builder.Environment.ContentRootPath,
            environmentVariableReader("DATA_DIR"),
            environmentVariableReader("CONFIG_DIR"),
            deploymentMode);

        builder.Logging.ClearProviders();
        builder.Services.AddRunOnceComposition(
            context,
            outcomes,
            standardOutput,
            standardError);
        return builder;
    }

    internal static async Task<int> RunProductionAsync(
        DeploymentMode deploymentMode,
        IReadOnlyList<string> arguments,
        Func<string, string?> environmentVariableReader,
        TextWriter standardOutput,
        TextWriter standardError,
        WorkerProcessExitOutcomeAccumulator outcomes)
    {
        IHost? host = null;
        try
        {
            host = CreateBuilder(
                deploymentMode,
                arguments,
                environmentVariableReader,
                standardOutput,
                standardError,
                outcomes).Build();
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            outcomes.Add(WorkerProcessExitFact.StartupInfrastructure());
        }

        return host is null
            ? outcomes.Fact.ExitCode
            : await RunHostAsync(host, outcomes).ConfigureAwait(false);
    }

    internal static async Task<int> RunHostAsync(
        IHost host,
        WorkerProcessExitOutcomeAccumulator outcomes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(outcomes);

        IHostApplicationLifetime? lifetime = null;
        IAsyncDisposable? scopeDisposal = null;
        CancellationTokenRegistration stoppingRegistration = default;
        OutOfMemoryException? firstFatal = null;
        var hostStartAttempted = false;
        var executionEntered = false;
        var stopOrigin = new RunOnceStopOrigin();

        try
        {
            var registeredOutcomes = host.Services.GetRequiredService<WorkerProcessExitOutcomeAccumulator>();
            if (!ReferenceEquals(registeredOutcomes, outcomes))
            {
                throw new InvalidOperationException("The Run-once host must use the caller-owned outcome accumulator.");
            }

            lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            stoppingRegistration = lifetime.ApplicationStopping.Register(stopOrigin.ObserveStopping);

            hostStartAttempted = true;
            await host.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var initializer = host.Services.GetRequiredService<IWorkerStartupInitializer>();
            await initializer.InitialiseAsync(lifetime.ApplicationStopping).ConfigureAwait(false);
            lifetime.ApplicationStopping.ThrowIfCancellationRequested();

            var scope = host.Services.CreateAsyncScope();
            scopeDisposal = scope;
            var executor = scope.ServiceProvider.GetRequiredService<IProcessingRunExecutor>();
            var reporter = scope.ServiceProvider.GetRequiredService<IProcessingEventReporter>();
            lifetime.ApplicationStopping.ThrowIfCancellationRequested();

            var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
            executionEntered = true;
            var result = await executor.ExecuteAsync(
                request,
                reporter,
                lifetime.ApplicationStopping).ConfigureAwait(false);
            outcomes.Add(MapResult(result));
        }
        catch (OperationCanceledException) when (lifetime?.ApplicationStopping.IsCancellationRequested == true)
        {
            outcomes.Add(WorkerProcessExitFact.ShutdownCancelled());
        }
        catch (OutOfMemoryException exception)
        {
            firstFatal ??= exception;
        }
        catch
        {
            outcomes.Add(executionEntered
                ? WorkerProcessExitFact.ExecutionInfrastructure()
                : WorkerProcessExitFact.StartupInfrastructure());
        }
        finally
        {
            try
            {
                if (scopeDisposal is not null)
                {
                    await scopeDisposal.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OutOfMemoryException exception)
            {
                firstFatal ??= exception;
            }
            catch
            {
                outcomes.Add(WorkerProcessExitFact.CleanupInfrastructure());
            }

            if (stopOrigin.BeginSelfStop())
            {
                outcomes.Add(WorkerProcessExitFact.ShutdownCancelled());
            }

            try
            {
                lifetime?.StopApplication();
            }
            catch (OutOfMemoryException exception)
            {
                firstFatal ??= exception;
            }
            catch
            {
                outcomes.Add(WorkerProcessExitFact.CleanupInfrastructure());
            }

            try
            {
                if (hostStartAttempted)
                {
                    await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (OutOfMemoryException exception)
            {
                firstFatal ??= exception;
            }
            catch
            {
                outcomes.Add(WorkerProcessExitFact.CleanupInfrastructure());
            }

            stoppingRegistration.Dispose();

            try
            {
                if (host is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    host.Dispose();
                }
            }
            catch (OutOfMemoryException exception)
            {
                firstFatal ??= exception;
            }
            catch
            {
                outcomes.Add(WorkerProcessExitFact.CleanupInfrastructure());
            }
        }

        if (firstFatal is not null)
        {
            ExceptionDispatchInfo.Capture(firstFatal).Throw();
        }

        if (!outcomes.HasFact)
        {
            outcomes.Add(WorkerProcessExitFact.StartupInfrastructure());
        }

        return outcomes.Fact.ExitCode;
    }

    private sealed class RunOnceStopOrigin
    {
        private readonly object _gate = new();
        private bool _externalStoppingObserved;
        private bool _selfStopStarted;

        internal void ObserveStopping()
        {
            lock (_gate)
            {
                if (!_selfStopStarted)
                {
                    _externalStoppingObserved = true;
                }
            }
        }

        internal bool BeginSelfStop()
        {
            lock (_gate)
            {
                _selfStopStarted = true;
                return _externalStoppingObserved;
            }
        }
    }

    private static WorkerProcessExitFact MapResult(ProcessingRunResult result)
    {
        return result.Outcome switch
        {
            ProcessingRunOutcome.Completed => WorkerProcessExitFact.Completed(),
            ProcessingRunOutcome.Cancelled => WorkerProcessExitFact.ShutdownCancelled(),
            ProcessingRunOutcome.Failed => WorkerProcessExitFact.ExecutionFailure(),
            _ => WorkerProcessExitFact.ExecutionInfrastructure()
        };
    }
}
