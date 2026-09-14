using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Spatial;

/// <summary>Immutable point evaluation, including the original distance fallback.</summary>
internal sealed class AdministrativeGeometry
{
    private readonly Geometry? _geometry;
    private readonly IPreparedGeometry? _prepared;

    private AdministrativeGeometry(Geometry? geometry, IPreparedGeometry? prepared)
    {
        _geometry = geometry;
        _prepared = prepared;
    }

    internal static AdministrativeGeometry Read(byte[] wkb, bool prepare, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Geometry geometry;
        try
        {
            geometry = new WKBReader().Read(wkb);
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
        if (prepare && geometry is IPolygonal && !geometry.IsEmpty && IsEligible(geometry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            prepared = PreparedGeometryFactory.Prepare(geometry);
            // PreparedPolygon builds its point locator lazily. Initialize it while
            // the cache still owns the build reservation, before concurrent hits.
            prepared.Covers(geometry.Factory.CreatePoint(geometry.Coordinate));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new AdministrativeGeometry(geometry, prepared);
    }

    internal bool Covers(Point point, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = false;
        if (_geometry is not null)
        {
            try
            {
                result = (_prepared?.Covers(point) ?? _geometry.Covers(point))
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
