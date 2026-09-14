using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Gadm.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ImmichReverseGeo.Tests.InternalWorkerHost;

public sealed partial class WorkerStdinRealSourceHostTests
{
    [TestMethod]
    [TestCategory("Change48")]
    public async Task V2RealSource_CoordinateNoCountryUsesHostLifecycleAndNeverInitializesAssetPersistence()
    {
        Guid jobId = Guid.Parse(RunIdText);
        var request = new CoordinateLookupRequest(
            0,
            0,
            true,
            true,
            true,
            new CoordinateLookupCityResolverOverrides(null, []));
        var execute = new WorkerJobControllerMessage(
            WorkerJobProtocolV2.RequestCategory,
            WorkerJobProtocolV2.ExecuteType,
            1,
            HostTime,
            jobId,
            WorkerJobKind.CoordinateLookup,
            new CoordinateLookupExecutePayload(request));
        var inputFactory = new HostInputFactory(new HostInputStream(
            WorkerJobProtocolCodec.SerializeControllerInput(execute)
                .Concat("\n"u8.ToArray())
                .ToArray()));
        var outputFactory = new FixedOutputFactory();
        var initializer = new CountingInitializer();
        var sources = new NoCountryCoordinateSources();
        string fixtureRoot = CreateFixtureRoot();
        var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();

        try
        {
            var builder = ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost.CreateBuilder(
                CreateContext(fixtureRoot),
                outcomes,
                InternalWorkerProtocolVersion.V2);
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, initializer);
            builder.Services.RemoveAll<IWorkerStandardInputStreamFactory>();
            builder.Services.AddSingleton<IWorkerStandardInputStreamFactory>(inputFactory);
            ReplaceSingleton<TimeProvider>(builder.Services, new FixedTimeProvider(HostTime));
            builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(outputFactory);
            ReplaceSingleton<ICoordinateLookupSources>(builder.Services, sources);

            int exitCode = await ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost.RunHostAsync(
                builder.Build(),
                outcomes).WaitAsync(Bound);

            Assert.AreEqual(0, exitCode, outputFactory.Output.Text);
            Assert.AreEqual(0, initializer.CallCount, "coordinate-does-not-initialize-skipped-assets");
            Assert.AreEqual(1, sources.CountryCalls);
            Assert.AreEqual(0, sources.CacheCalls);
            WorkerJobOutputMessage[] messages = outputFactory.Output.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(frame => WorkerJobProtocolCodec.Parse(Encoding.UTF8.GetBytes(frame)).Message!)
                .ToArray();
            CollectionAssert.AreEqual(
                Enumerable.Range(1, messages.Length).Select(static value => (long)value).ToArray(),
                messages.Select(static message => message.Sequence).ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    WorkerJobKind.ProcessAssets,
                    WorkerJobKind.CoordinateLookup,
                    WorkerJobKind.CacheMutation
                },
                Assert.IsInstanceOfType<WorkerJobReadyPayload>(messages[0].Payload)
                    .SupportedJobKinds.ToArray());
            Assert.AreEqual(WorkerJobProtocolV2.JobStartedType, messages[1].Type);
            Assert.IsTrue(messages.Skip(1).All(message => message.JobId == jobId));
            Assert.IsTrue(messages.Skip(1).All(message => message.JobKind == WorkerJobKind.CoordinateLookup));
            var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(messages[^1].Payload);
            Assert.AreEqual(WorkerJobTerminalOutcome.Completed, terminal.Outcome);
            Assert.AreEqual(
                CoordinateLookupCountryStatus.NoMatch,
                terminal.CoordinateLookupResult!.Country.Status);
            Assert.IsNull(terminal.ProcessAssetsResult);
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    private sealed class NoCountryCoordinateSources : ICoordinateLookupSources
    {
        internal int CountryCalls { get; private set; }
        internal int CacheCalls { get; private set; }

        public Task<BundledCountryLookupResult> FindCountryAsync(
            double latitude,
            double longitude,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CountryCalls++;
            return Task.FromResult(BundledCountryLookupResult.SpatialNoMatch("fixture no match"));
        }

        public CityResolverProfile ResolveCityProfile(
            CoordinateLookupCityResolverOverrides overrides,
            string? iso3) =>
            new CityResolverProfileCatalog().GetProfile(
                CoordinateLookupCityProfileConversions.ToConfig(overrides),
                iso3);

        public IReadOnlyList<string> ExpandGadmCandidateCodes(string iso3) => [iso3];

        public (Task Task, OvertureDivisionEnsureResult Result) GetOrStartOvertureCache(
            string iso3,
            CancellationToken cancellationToken)
        {
            CacheCalls++;
            throw new AssertFailedException("No-country lookup must not start Overture cache work.");
        }

        public bool HasOvertureCache(string iso3) => false;

        public Task<OvertureDivisionLookupDiagnostics> FindOvertureDivisionsAsync(
            double latitude,
            double longitude,
            string alpha2,
            string iso3,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("No-country lookup must not query Overture divisions.");

        public (Task Task, GadmDivisionEnsureResult Result) GetOrStartGadmCache(
            string iso3,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("No-country lookup must not start GADM cache work.");

        public bool HasGadmCache(string iso3) => false;

        public Task<GadmDivisionLookupDiagnostics> FindGadmDivisionsAsync(
            double latitude,
            double longitude,
            IReadOnlyList<string> iso3Codes,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("No-country lookup must not query GADM.");

        public Task<OvertureInfrastructureLookupDiagnostics> FindAirportAsync(
            double latitude,
            double longitude,
            string iso3,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("No-country lookup must not query airports.");

        public Task<OvertureLookupDiagnostics> FindPlacesAsync(
            double latitude,
            double longitude,
            string alpha2,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("No-country lookup must not query Places.");
    }
}
