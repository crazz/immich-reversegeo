using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Web.Services;

public interface IProcessingRunConfiguration
{
    Task<AppConfig> GetConfigAsync();
}

public interface IProcessingAssetRepository
{
    Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default);
    Task<List<AssetRecord>> GetUnprocessedBatchAsync(AssetCursor cursor, int batchSize, CancellationToken cancellationToken = default);
    Task WriteLocationAsync(Guid assetId, GeoResult geoResult, CancellationToken cancellationToken = default);
}

public interface IProcessingSkippedStore
{
    Task<HashSet<Guid>> GetAllAsync();
    Task AddAsync(Guid assetId);
}
