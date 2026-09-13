using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal static class ProductionProcessingHostFixture
{
    internal static async Task<int> RunAsync(FixtureOptions options, InternalWorkerProtocolVersion version)
    {
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var context = ApplicationCompositionContext.Create(CompositionEnvironment.Development,
            options.ResourceRoot, options.ResourceRoot, options.ResourceRoot);
        var builder = InternalWorkerHost.CreateBuilder(context, outcomes, version);
        var gate = new AssetWorkGate(options.ResourceRoot);
        builder.Services.RemoveAll<IProcessingAssetRepository>();
        builder.Services.AddSingleton<IProcessingAssetRepository>(gate);
        builder.Services.RemoveAll<IProcessingRunConfiguration>();
        builder.Services.AddSingleton<IProcessingRunConfiguration>(gate);
        builder.Services.RemoveAll<IProcessingAdministrativeResolver>();
        builder.Services.AddSingleton<IProcessingAdministrativeResolver>(gate);
        builder.Services.RemoveAll<IProcessingInfrastructureLookup>();
        builder.Services.AddSingleton<IProcessingInfrastructureLookup>(gate);
        builder.Services.RemoveAll<IProcessingRunLock>();
        builder.Services.AddSingleton<IProcessingRunLock>(new HermeticRunLock(options.ResourceRoot));
        builder.Services.RemoveAll<NpgsqlDataSource>();
        builder.Services.AddSingleton<NpgsqlDataSource>(_ => throw new InvalidOperationException("Hermetic asset row resolved PostgreSQL."));
        return await InternalWorkerHost.RunHostAsync(builder.Build(), outcomes).ConfigureAwait(false);
    }

    // Keep the actual executor's count, batch, Parallel.ForEachAsync, reporter and
    // asset cancellation path. Only external asset/geodata boundaries are closed.
    private sealed class AssetWorkGate(string root) : IProcessingAssetRepository,
        IProcessingRunConfiguration, IProcessingAdministrativeResolver, IProcessingInfrastructureLookup
    {
        public Task<AppConfig> GetConfigAsync() => Task.FromResult(new AppConfig
        {
            Processing = new ProcessingConfig { BatchDelayMs = 0, MaxDegreeOfParallelism = 1 }
        });
        public Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task<List<AssetRecord>> GetUnprocessedBatchAsync(AssetCursor cursor, int batchSize,
            CancellationToken cancellationToken = default) => Task.FromResult(new List<AssetRecord>
            {
                new(Guid.Parse("5131b7ca-752f-4ab7-8214-45e3e8602b60"), 47, 8, DateTime.UnixEpoch)
            });
        public Task WriteLocationAsync(Guid assetId, GeoResult result, CancellationToken cancellationToken = default)
        {
            File.WriteAllText(Path.Combine(root, "unexpected-write.marker"), "write");
            throw new InvalidOperationException("Cancelled asset must not write metadata.");
        }
        public async Task<AdministrativeAreaResolution?> ResolveAsync(double latitude, double longitude,
            ProcessingConfig config, IProcessingRunEventSession session, CancellationToken cancellationToken = default)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "asset-work-entered.marker"), "entered", cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("The asset gate only completes through cancellation.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(Path.Combine(root, "asset-work-cancelled.marker"), "cancelled").ConfigureAwait(false);
                throw;
            }
        }
        public Task<OvertureInfrastructureLookupDiagnostics> FindNearestInfrastructureAsync(double latitude,
            double longitude, string? iso3, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Cancelled asset must not reach infrastructure lookup.");
    }

    // This normal-suite row declares PostgreSQL not applicable; real fixed-key
    // ownership is exercised separately by the Integration matrix.
    private sealed class HermeticRunLock(string root) : IProcessingRunLock
    {
        public ValueTask<ProcessingRunLockAcquisition> AcquireAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<ProcessingRunLockAcquisition>(new ProcessingRunLockAcquisition.Acquired(new Lease(root)));
        private sealed class Lease(string root) : IProcessingRunLockLease
        {
            public CancellationToken OwnershipLost => CancellationToken.None;
            public bool IsOwnershipLost => false;
            public Task<ProcessingRunLockRelease> ReleaseAsync()
            {
                File.WriteAllText(Path.Combine(root, "hermetic-lease-released.marker"), "released");
                return Task.FromResult(new ProcessingRunLockRelease(false));
            }
            public async ValueTask DisposeAsync() => await ReleaseAsync().ConfigureAwait(false);
        }
    }
}
