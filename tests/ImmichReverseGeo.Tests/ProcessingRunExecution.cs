using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Tests;

internal static class ProcessingRunExecution
{
    internal static async Task<ProcessingRunResult> RunOnceAsync(ILogger<ProcessingBackgroundService> logger, ProcessingState state, ProcessingOperations operations, CancellationToken cancellationToken)
    {
        var reporter = new ProcessingStateEventReporter(state);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        reporter.Arm(request);
        return await RunOnceAsync(logger, state, reporter, request, operations, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ProcessingRunResult> RunOnceAsync(ILogger<ProcessingBackgroundService> logger, ProcessingState state, IProcessingEventReporter reporter, ProcessingRunRequest request, ProcessingOperations operations, CancellationToken cancellationToken)
    {
        var executor = new ProcessingRunExecutor(logger, operations, operations, operations, operations, operations, operations, new FixedUtcTimeProvider());
        try
        {
            return await executor.ExecuteAsync(request, reporter, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            if (reporter is ProcessingStateEventReporter stateReporter)
            {
                stateReporter.Abandon(request, failure);
            }
            throw;
        }
    }

    internal static bool IsLoggerOnlyNoCitySkip(GeoResult geoResult) => ProcessingRunExecutor.IsLoggerOnlyNoCitySkip(geoResult);

    internal sealed class ProcessingOperations(
        Func<CancellationToken, Task<long>> count,
        Func<Task<AppConfig>> config,
        Func<Task<HashSet<Guid>>> getSkipped,
        Func<AssetCursor, int, CancellationToken, Task<List<AssetRecord>>> getBatch,
        Func<double, double, ProcessingConfig, IProcessingRunEventSession, CancellationToken, Task<AdministrativeAreaResolution?>> resolve,
        Func<double, double, string?, CancellationToken, Task<OvertureInfrastructureLookupDiagnostics>> infrastructure,
        Func<Guid, Task> addSkipped,
        Func<Guid, GeoResult, CancellationToken, Task> write)
        : IProcessingRunConfiguration, IProcessingScheduleConfiguration, IProcessingAssetRepository,
          IProcessingSkippedStore, IProcessingAdministrativeResolver, IProcessingInfrastructureLookup, IProcessingRunDelay
    {
        public Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default) => count(cancellationToken);
        public Task<AppConfig> GetConfigAsync() => config();
        async Task<ProcessingScheduleSnapshot> IProcessingScheduleConfiguration.GetSnapshotAsync()
        {
            var appConfig = await config().ConfigureAwait(false);
            return new ProcessingScheduleSnapshot(appConfig.Schedule.Enabled, appConfig.Schedule.Cron);
        }
        public Task<HashSet<Guid>> GetAllAsync() => getSkipped();
        public Task<List<AssetRecord>> GetUnprocessedBatchAsync(AssetCursor cursor, int batchSize, CancellationToken cancellationToken = default) => getBatch(cursor, batchSize, cancellationToken);
        public Task<AdministrativeAreaResolution?> ResolveAsync(double latitude, double longitude, ProcessingConfig processingConfig, IProcessingRunEventSession session, CancellationToken cancellationToken = default) => resolve(latitude, longitude, processingConfig, session, cancellationToken);
        public Task<OvertureInfrastructureLookupDiagnostics> FindNearestInfrastructureAsync(double latitude, double longitude, string? iso3, CancellationToken cancellationToken = default) => infrastructure(latitude, longitude, iso3, cancellationToken);
        public Task AddAsync(Guid assetId) => addSkipped(assetId);
        public Task WriteLocationAsync(Guid assetId, GeoResult geoResult, CancellationToken cancellationToken = default) => write(assetId, geoResult, cancellationToken);
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => throw new AssertFailedException($"Unexpected processing delay {delay}.");
    }
}
