using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Overture.Models;

namespace ImmichReverseGeo.Web.Services;

public interface IProcessingRunExecutor
{
    Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken);
}

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

public interface IProcessingAdministrativeResolver
{
    Task<AdministrativeAreaResolution?> ResolveAsync(double latitude, double longitude, ProcessingConfig config, IProcessingRunEventSession session, CancellationToken cancellationToken = default);
}

public interface IProcessingInfrastructureLookup
{
    Task<OvertureInfrastructureLookupDiagnostics> FindNearestInfrastructureAsync(double latitude, double longitude, string? iso3, CancellationToken cancellationToken = default);
}

public interface IProcessingRunDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class ProcessingInfrastructureLookup(ImmichReverseGeo.Overture.Services.OverturePlacesService places) : IProcessingInfrastructureLookup
{
    public Task<OvertureInfrastructureLookupDiagnostics> FindNearestInfrastructureAsync(double latitude, double longitude, string? iso3, CancellationToken cancellationToken = default)
    {
        return places.FindNearestInfrastructureWithDiagnosticsAsync(latitude, longitude, iso3, cancellationToken);
    }
}

internal sealed class ProcessingRunDelay(TimeProvider timeProvider) : IProcessingRunDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, timeProvider, cancellationToken);
    }
}
