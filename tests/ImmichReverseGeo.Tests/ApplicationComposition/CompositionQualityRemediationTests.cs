using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
public sealed class CompositionQualityRemediationTests
{
    [TestMethod]
    public async Task ReusableExecutor_UsesProcessingBackgroundLoggerCategory()
    {
        var services = new ServiceCollection();
        services.AddInternalWorkerComposition(ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            "/composition/logger-category",
            "/composition/logger-data",
            "/composition/logger-config"));
        var backgroundLogger = new CapturingLogger<ProcessingBackgroundService>();
        services.AddSingleton<ILogger<ProcessingBackgroundService>>(backgroundLogger);
        services.AddSingleton<IProcessingAssetRepository>(new ThrowingAssetRepository());

        using var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<ProcessingRunExecutor>();
        var result = await executor.ExecuteAsync(
            new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual),
            NoOpProcessingEventReporter.Instance,
            CancellationToken.None);

        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome, "executor-failure-outcome");
        Assert.IsTrue(backgroundLogger.ErrorMessages.Contains("Fatal error during processing run"), "processing-background-logger-category");
    }

    private sealed class ThrowingAssetRepository : IProcessingAssetRepository
    {
        public Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("test asset repository failure");
        }

        public Task<List<AssetRecord>> GetUnprocessedBatchAsync(AssetCursor cursor, int batchSize, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("not reached");
        }

        public Task WriteLocationAsync(Guid assetId, GeoResult geoResult, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("not reached");
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> ErrorMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                ErrorMessages.Add(formatter(state, exception));
            }
        }
    }
}
