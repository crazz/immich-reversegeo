using System.Text.Json;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Spatial;
using ImmichReverseGeo.Gadm.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.CandidateTests;

public sealed partial class LocalCandidatePreparationTests
{
    private const GeometrySource Source = GeometrySource.Gadm;

    private static Adapter CreateAdapter(Fixture fixture,
        Func<CancellationToken, Task>? beforePublication = null, ICacheFilePublisher? publisher = null)
    {
        var service = new GadmDivisionCacheService(
            NullLogger<GadmDivisionCacheService>.Instance, fixture.Root,
            new GadmDivisionCacheTestHooks
            {
                SourceOperation = (_, _) => throw new AssertFailedException("Local preparation reached the remote source boundary."),
                BeforePublication = beforePublication,
                FilePublisher = publisher
            });
        var reader = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, fixture.Root);
        return new Adapter(service, () =>
        {
            var admission = service.GetOrStartDownload("CHE");
            return (admission.Task, admission.Result.ToString());
        }, async (lat, lon) => JsonSerializer.Serialize(await reader.FindContainingDivisionAreasAsync(lat, lon, "CHE")));
    }
}
