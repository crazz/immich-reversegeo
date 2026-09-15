using System.Threading;
using NetTopologySuite;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Implementation;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Spatial;

/// <summary>Immutable point evaluation, including the original distance fallback.</summary>
internal sealed class AdministrativeGeometry
{
    private readonly Geometry? _geometry;
    private readonly IPreparedGeometry? _prepared;
    private readonly object? _evaluationSync;

    internal bool IsCompact => _evaluationSync is not null;

    private AdministrativeGeometry(Geometry? geometry, IPreparedGeometry? prepared, bool compact = false)
    {
        _geometry = geometry;
        _prepared = prepared;
        _evaluationSync = compact ? new object() : null;
    }

    internal static AdministrativeGeometry Read(byte[] wkb, bool prepare, CancellationToken cancellationToken)
        => ReadCore(wkb, prepare, false, cancellationToken);

    internal static AdministrativeGeometry ReadCompact(byte[] wkb, CancellationToken cancellationToken)
        => ReadCore(wkb, false, true, cancellationToken);

    private static AdministrativeGeometry ReadCore(byte[] wkb, bool prepare, bool compact, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Geometry geometry;
        try
        {
            var reader = compact
                ? new WKBReader(new NtsGeometryServices(PackedCoordinateSequenceFactory.DoubleFactory))
                : new WKBReader();
            geometry = reader.Read(wkb);
        }
        catch (ParseException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new AdministrativeGeometry(null, null);
        }
        catch (TopologyException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new AdministrativeGeometry(null, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        IPreparedGeometry? prepared = null;
        var eligible = (prepare || compact) && geometry is IPolygonal && !geometry.IsEmpty && IsEligible(geometry);
        if (prepare && eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            prepared = PreparedGeometryFactory.Prepare(geometry);
            // PreparedPolygon builds its point locator lazily. Initialize it while
            // the cache still owns the build reservation, before concurrent hits.
            prepared.Covers(geometry.Factory.CreatePoint(geometry.Coordinate));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new AdministrativeGeometry(geometry, prepared, compact && eligible);
    }

    internal bool Covers(Point point, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_evaluationSync is null)
        {
            return CoversCore(point, cancellationToken);
        }

        // One compact entry owns one evaluation workspace allowance. Do not
        // acquire the global cache lock or another reservation while leased.
        while (!Monitor.TryEnter(_evaluationSync, 50))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CoversCore(point, cancellationToken);
        }
        finally
        {
            Monitor.Exit(_evaluationSync);
        }
    }

    private bool CoversCore(Point point, CancellationToken cancellationToken)
    {
        var result = false;
        if (_geometry is not null)
        {
            try
            {
                var contained = IsCompact && !point.IsEmpty
                    ? SimplePointInAreaLocator.Locate(point.Coordinate, _geometry) != Location.Exterior
                    : (_prepared?.Covers(point) ?? _geometry.Covers(point));
                result = contained
                    || _geometry.Distance(point) <= 0.00015;
            }
            catch (ParseException)
            {
                result = false;
            }
            catch (TopologyException)
            {
                result = false;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static bool IsEligible(Geometry geometry)
    {
        try
        {
            return geometry.IsValid;
        }
        catch (TopologyException)
        {
            // A validation failure cannot change the original predicate's outcome.
            return false;
        }
    }
}
