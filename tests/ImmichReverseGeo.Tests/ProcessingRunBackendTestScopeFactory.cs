using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Web.Services;

// Test-only keyed scope composition for legacy coordinator fixtures.
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
        return new TestScope(new InProcessProcessingRunBackend(_executor));
    }

    private sealed class TestScope(IProcessingRunBackend backend) : IServiceScope, IAsyncDisposable
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

    private sealed class TestScopeServiceProvider(IProcessingRunBackend backend) : IKeyedServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return null;
        }

        public object? GetKeyedService(Type serviceType, object? serviceKey)
        {
            return serviceType == typeof(IProcessingRunBackend)
                && Equals(serviceKey, ProcessingBackendKind.InProcess)
                ? backend
                : null;
        }

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
        {
            return GetKeyedService(serviceType, serviceKey)
                ?? throw new InvalidOperationException("The requested keyed test service is not registered.");
        }
    }
}
