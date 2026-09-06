using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Web.Services;

/// <summary>
/// Verifies the explicitly selected backend before the Web host accepts processing work.
/// The temporary in-process selection remains a code-only rebuild/revert seam until Change 38.
/// </summary>
internal sealed class SelectedProcessingBackendStartupValidator : IHostedLifecycleService
{
    private readonly IServiceProvider _services;
    private readonly TemporaryProcessingBackendSelection _selection;

    internal SelectedProcessingBackendStartupValidator(
        IServiceProvider services,
        TemporaryProcessingBackendSelection selection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(selection);
        _services = services;
        _selection = selection;
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var backend = TemporaryProcessingBackendSelection.Validate(_selection.Backend);
        var keyedServices = _services.GetService<IServiceProviderIsKeyedService>();
        if (keyedServices is null || !keyedServices.IsKeyedService(typeof(IProcessingRunBackend), backend))
        {
            throw new InvalidOperationException("The selected processing backend is not registered for startup validation.");
        }

        if (backend == ProcessingBackendKind.ChildWorker)
        {
            ValidateChildWorkerServices();
            var resolution = _services.GetRequiredService<IWorkerCommandInvocationBuilder>().Build();
            if (resolution is WorkerCommandInvocationResolution.Failure failure)
            {
                throw new InvalidOperationException(
                    $"The child worker startup prerequisite is invalid ({failure.Code}). {failure.Remediation}");
            }

            ValidateChildWorkerRuntimeFiles(((WorkerCommandInvocationResolution.Success)resolution).Invocation.ApplicationAssemblyPath);
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    private void ValidateChildWorkerServices()
    {
        var services = _services.GetService<IServiceProviderIsService>();
        if (services is null
            || !services.IsService(typeof(IWorkerCommandInvocationBuilder))
            || !services.IsService(typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.IChildProcessFactory))
            || !services.IsService(typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.IChildWorkerLauncher))
            || !services.IsService(typeof(ImmichReverseGeo.Web.WorkerFailureRecovery.WorkerRunControlPlane)))
        {
            throw new InvalidOperationException("The child worker launcher services are not registered for startup validation.");
        }
    }

    private static void ValidateChildWorkerRuntimeFiles(string applicationAssemblyPath)
    {
        var runtimeConfig = Path.ChangeExtension(applicationAssemblyPath, ".runtimeconfig.json");
        var dependencies = Path.ChangeExtension(applicationAssemblyPath, ".deps.json");
        if (!File.Exists(runtimeConfig) || !File.Exists(dependencies))
        {
            throw new InvalidOperationException("The child worker runtime files are unavailable. Publish the complete Web application artifact and retry startup.");
        }
    }
}
