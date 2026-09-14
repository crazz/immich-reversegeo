using System;

namespace ImmichReverseGeo.Spatial;

public enum GeometrySource
{
    Gadm,
    Overture
}

/// <summary>An opaque, invocation-local identity for one observed source file.</summary>
public sealed class GeometryGeneration
{
    internal GeometryGeneration(AdministrativeGeometryCache owner, GeometrySource source, string country, string stamp)
    {
        Owner = owner;
        Source = source;
        Country = country;
        Stamp = stamp;
    }

    internal AdministrativeGeometryCache Owner { get; }
    internal GeometrySource Source { get; }
    internal string Country { get; }
    internal string Stamp { get; }
    internal bool Retired { get; set; }
}

public readonly record struct SpatialCacheStatistics(
    long BudgetBytes, long AccountedBytes, int RetainedEntries, int PendingPreparations, int WaitingQueries,
    long Hits, long BlobLoads, long Preparations, long Evictions, long UnretainedEvaluations);
