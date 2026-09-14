using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Web.Services;

internal sealed class ChildWorkerStartupValidator(IServiceProvider services) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        ValidateChildWorkerServices();
        var resolution = services.GetRequiredService<IWorkerCommandInvocationBuilder>().Build();
        if (resolution is WorkerCommandInvocationResolution.Failure failure)
        {
            throw new InvalidOperationException(
                $"The child worker startup prerequisite is invalid ({failure.Code}). {failure.Remediation}");
        }

        ValidateChildWorkerRuntimeFiles(((WorkerCommandInvocationResolution.Success)resolution).Invocation.ApplicationAssemblyPath);
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
        var registeredServices = services.GetService<IServiceProviderIsService>();
        if (registeredServices is null
            || !registeredServices.IsService(typeof(IChildProcessingRunBackend))
            || !registeredServices.IsService(typeof(IWorkerCommandInvocationBuilder))
            || !registeredServices.IsService(typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.IChildProcessFactory))
            || !registeredServices.IsService(typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.IChildWorkerLauncher))
            || !registeredServices.IsService(typeof(ImmichReverseGeo.Web.WorkerFailureRecovery.WorkerRunControlPlane)))
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
