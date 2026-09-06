using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Web.Services;

// Test-only child boundary composition for coordinator fixtures.
internal sealed class ProcessingRunBackendTestScopeFactory : IServiceScopeFactory
{
    private readonly IProcessingRunExecutor _executor;

    private ProcessingRunBackendTestScopeFactory(IProcessingRunExecutor executor)
    {
        _executor = executor;
    }

    internal static IServiceScopeFactory Create(IProcessingRunExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return new ProcessingRunBackendTestScopeFactory(executor);
    }

    public IServiceScope CreateScope()
    {
        return new TestScope(new ExecutorBackedChildBackend(_executor));
    }

    private sealed class TestScope(IChildProcessingRunBackend backend) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } = new TestScopeServiceProvider(backend);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestScopeServiceProvider(IChildProcessingRunBackend backend) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return serviceType == typeof(IChildProcessingRunBackend) ? backend : null;
        }
    }

    private sealed class ExecutorBackedChildBackend(IProcessingRunExecutor executor) : IChildProcessingRunBackend
    {
        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            return executor.ExecuteAsync(request, reporter, cancellationToken);
        }
    }
}
