using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Web.RunOnce;

internal sealed class RunOnceInfrastructureDependencies(
    IProcessingRunConfiguration configuration,
    IProcessingAssetRepository assets,
    IProcessingSkippedStore skippedStore)
{
    internal IProcessingRunConfiguration Configuration { get; } =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    internal IProcessingAssetRepository Assets { get; } =
        assets ?? throw new ArgumentNullException(nameof(assets));

    internal IProcessingSkippedStore SkippedStore { get; } =
        skippedStore ?? throw new ArgumentNullException(nameof(skippedStore));
}

internal sealed class RunOnceProcessingRunConfiguration(
    RunOnceInfrastructureDependencies dependencies,
    WorkerProcessExitOutcomeAccumulator outcomes) : IProcessingRunConfiguration
{
    public Task<AppConfig> GetConfigAsync() => RunOnceInfrastructureOutcomeBoundary.ExecuteAsync(
        dependencies.Configuration.GetConfigAsync,
        outcomes);
}

internal sealed class RunOnceProcessingAssetRepository(
    RunOnceInfrastructureDependencies dependencies,
    WorkerProcessExitOutcomeAccumulator outcomes) : IProcessingAssetRepository
{
    public Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default) =>
        RunOnceInfrastructureOutcomeBoundary.ExecuteAsync(
            () => dependencies.Assets.GetUnprocessedCountAsync(cancellationToken),
            outcomes,
            cancellationToken);

    public Task<List<AssetRecord>> GetUnprocessedBatchAsync(
        AssetCursor cursor,
        int batchSize,
        CancellationToken cancellationToken = default) => RunOnceInfrastructureOutcomeBoundary.ExecuteAsync(
            () => dependencies.Assets.GetUnprocessedBatchAsync(cursor, batchSize, cancellationToken),
            outcomes,
            cancellationToken);

    public Task WriteLocationAsync(
        Guid assetId,
        GeoResult geoResult,
        CancellationToken cancellationToken = default) => RunOnceInfrastructureOutcomeBoundary.ExecuteAsync(
            () => dependencies.Assets.WriteLocationAsync(assetId, geoResult, cancellationToken),
            outcomes,
            cancellationToken);
}

internal sealed class RunOnceProcessingSkippedStore(
    RunOnceInfrastructureDependencies dependencies,
    WorkerProcessExitOutcomeAccumulator outcomes) : IProcessingSkippedStore
{
    public Task<HashSet<Guid>> GetAllAsync() => RunOnceInfrastructureOutcomeBoundary.ExecuteAsync(
        dependencies.SkippedStore.GetAllAsync,
        outcomes);

    public Task AddAsync(Guid assetId) => RunOnceInfrastructureOutcomeBoundary.ExecuteAsync(
        () => dependencies.SkippedStore.AddAsync(assetId),
        outcomes);
}

internal static class RunOnceInfrastructureOutcomeBoundary
{
    internal static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        WorkerProcessExitOutcomeAccumulator outcomes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            outcomes.Add(WorkerProcessExitFact.ExecutionInfrastructure());
            throw;
        }
    }

    internal static async Task ExecuteAsync(
        Func<Task> operation,
        WorkerProcessExitOutcomeAccumulator outcomes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            outcomes.Add(WorkerProcessExitFact.ExecutionInfrastructure());
            throw;
        }
    }
}
