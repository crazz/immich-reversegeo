using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ImmichReverseGeo.Web.Services;

internal static class ExecutorBackedChildTestComposition
{
    internal static IServiceCollection AddProcessingServicesForTests(this IServiceCollection services)
    {
        services.AddWorkerExecutionComposition();
        return services.AddExecutorBackedChildControlPlaneServices();
    }

    internal static IServiceCollection AddExecutorBackedChildControlPlaneServices(this IServiceCollection services)
    {
        services.AddProcessingControlPlaneServices();
        services.RemoveAll<IChildProcessingRunBackend>();
        services.AddScoped<IChildProcessingRunBackend, ExecutorBackedChildTestBackend>();
        return services;
    }
}

internal sealed class ExecutorBackedChildTestBackend(IProcessingRunExecutor executor) : IChildProcessingRunBackend
{
    public Task<ProcessingRunResult> ExecuteAsync(
        ProcessingRunRequest request,
        IProcessingEventReporter reporter,
        CancellationToken cancellationToken)
    {
        return executor.ExecuteAsync(request, reporter, cancellationToken);
    }
}
