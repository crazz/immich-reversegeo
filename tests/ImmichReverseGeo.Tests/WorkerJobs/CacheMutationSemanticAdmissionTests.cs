using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerJobs;

[TestClass]
[TestCategory("Change51")]
public sealed class CacheMutationSemanticAdmissionTests
{
    private static readonly Guid JobId =
        Guid.Parse("51515151-aaaa-bbbb-cccc-515151515151");

    [TestMethod]
    public async Task CacheMutationWithoutSemanticValidator_FailsClosedBeforeLeaseAcceptance()
    {
        await using var source = CreateSource("CHE", semanticValidator: null);

        InitialProcessingRunAcquisition acquisition =
            await source.AcquireAsync(CancellationToken.None);
        var failure = Assert.IsInstanceOfType<InitialProcessingRunAcquisition.PreRequestFailure>(
            acquisition);

        Assert.AreEqual("worker-input-invalidpayload", failure.Failure.Category);
    }

    [TestMethod]
    [DataRow("CHE", true)]
    [DataRow("ZZZ", false)]
    public async Task CatalogValidator_AcceptsKnownAndRejectsUnknownIso3(
        string iso3,
        bool accepted)
    {
        var countries = CountryCodeService.CreateForTest(
            Path.Combine(AppContext.BaseDirectory, "data"));
        var semanticValidator = new CacheMutationRequestSemanticValidator(countries);
        await using var source = CreateSource(iso3, semanticValidator);

        InitialProcessingRunAcquisition acquisition =
            await source.AcquireAsync(CancellationToken.None);
        if (!accepted)
        {
            Assert.IsInstanceOfType<InitialProcessingRunAcquisition.PreRequestFailure>(acquisition);
            return;
        }

        var acceptedLease = Assert.IsInstanceOfType<InitialProcessingRunAcquisition.Accepted>(
            acquisition);
        try
        {
            var lease = Assert.IsInstanceOfType<WorkerStdinCacheMutationLease>(acceptedLease.Lease);
            Assert.AreEqual(JobId, lease.Context.JobId);
            Assert.AreEqual(
                "CHE",
                Assert.IsInstanceOfType<CacheMutationRequest>(lease.JobRequest).Iso3);
        }
        finally
        {
            await acceptedLease.Lease.DisposeAsync();
        }
    }

    private static WorkerStdinRequestSource CreateSource(
        string iso3,
        IWorkerJobRequestSemanticValidator? semanticValidator)
    {
        var request = new CacheMutationRequest(
            CacheMutationSource.Overture,
            CacheMutationOperation.Ensure,
            iso3);
        var message = new WorkerJobControllerMessage(
            WorkerJobProtocolV2.RequestCategory,
            WorkerJobProtocolV2.ExecuteType,
            1,
            DateTimeOffset.UtcNow,
            JobId,
            WorkerJobKind.CacheMutation,
            new CacheMutationExecutePayload(request));
        byte[] bytes = [.. WorkerJobProtocolCodec.SerializeControllerInput(message), (byte)'\n'];
        return new WorkerStdinRequestSource(
            new MemoryInputFactory(new MemoryStream(bytes)),
            NullLogger<WorkerStdinRequestSource>.Instance,
            InternalWorkerProtocolVersion.V2,
            [WorkerJobDescriptors.CacheMutation],
            semanticValidator);
    }

    private sealed class MemoryInputFactory(Stream stream) : IWorkerStandardInputStreamFactory
    {
        public Stream OpenStandardInput() => stream;
    }
}
