using System.Text.Json;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Spatial;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.CandidateTests;

public sealed partial class LocalCandidatePreparationTests
{
    private const GeometrySource Source = GeometrySource.Overture;

    private static Adapter CreateAdapter(Fixture fixture,
        Func<CancellationToken, Task>? beforePublication = null, ICacheFilePublisher? publisher = null)
    {
        var service = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance, fixture.Root, _ => "CH",
            new OvertureDivisionCacheTestHooks
            {
                SourceOperation = (_, _) => throw new AssertFailedException("Local preparation reached the remote source boundary."),
                BeforePublication = beforePublication,
                FilePublisher = publisher
            });
        var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, fixture.Root, fixture.Root);
        var reader = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, fixture.Root, fixture.Root, _ => "CHE");
        return new Adapter(service, () =>
        {
            var admission = service.GetOrStartDownload("CHE");
            return (admission.Task, admission.Result.ToString());
        }, async (lat, lon) => JsonSerializer.Serialize(await reader.FindContainingDivisionAreasAsync(lat, lon, "CH", "CHE")));
    }
}
