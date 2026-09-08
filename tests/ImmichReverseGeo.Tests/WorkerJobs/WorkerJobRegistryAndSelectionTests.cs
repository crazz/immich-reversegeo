using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Tests.WorkerJobs;

[TestClass]
public sealed class WorkerJobRegistryAndSelectionTests
{
    [TestMethod]
    [TestCategory("Change47")]
    public void ProtocolSelector_AcceptsOnlyAbsentOrExactTwoWithoutEchoingPrivateInput()
    {
        Assert.AreEqual(
            InternalWorkerProtocolVersion.V1,
            Assert.IsInstanceOfType<InternalWorkerProtocolVersionSelection.Success>(
                InternalWorkerProtocolVersionSelector.Select(_ => null)).Version);
        Assert.AreEqual(
            InternalWorkerProtocolVersion.V2,
            Assert.IsInstanceOfType<InternalWorkerProtocolVersionSelection.Success>(
                InternalWorkerProtocolVersionSelector.Select(_ => "2")).Version);

        foreach (string invalid in new[] { "", " ", "1", "02", "+2", "2 ", "3", "invalid-canary" })
        {
            Assert.AreSame(
                InternalWorkerProtocolVersionSelection.Invalid.Instance,
                InternalWorkerProtocolVersionSelector.Select(_ => invalid),
                $"present-invalid:{invalid.Length}");
        }

        Assert.IsFalse(
            InternalWorkerProtocolVersionSelector.InvalidSelectionDiagnostic.Contains(
                InternalWorkerProtocolVersionSelector.EnvironmentVariableName,
                StringComparison.Ordinal),
            "safe-diagnostic-does-not-name-private-selector");
        Assert.IsFalse(
            InternalWorkerProtocolVersionSelector.InvalidSelectionDiagnostic.Contains(
                "invalid-canary",
                StringComparison.Ordinal),
            "safe-diagnostic-does-not-echo-value");
    }

    [TestMethod]
    [TestCategory("Change47")]
    public void Registry_ValidatesMetadataWithoutResolvingHandlerAndAdvertisesOnlyProcessAssets()
    {
        var resolutionCalls = 0;
        var registration = new WorkerJobHandlerRegistration<ProcessAssetsRequest, ProcessAssetsResult>(
            WorkerJobDescriptors.ProcessAssets,
            _ =>
            {
                resolutionCalls++;
                return new Handler();
            });

        var registry = new WorkerJobHandlerRegistry([registration]);
        Assert.AreEqual(0, resolutionCalls, "construction-is-metadata-only");
        CollectionAssert.AreEqual(
            new[] { WorkerJobKind.ProcessAssets },
            registry.SupportedJobKinds.ToArray());
        Assert.IsFalse(registry.IsRegistered(WorkerJobKind.CoordinateLookup));
        Assert.IsFalse(registry.IsRegistered(WorkerJobKind.CacheMutation));
        var registryReady = (WorkerJobReadyPayload)registry
            .CreateReady(1, DateTimeOffset.UnixEpoch)
            .Payload;
        CollectionAssert.AreEqual(
            new[] { WorkerJobKind.ProcessAssets },
            registryReady.SupportedJobKinds.ToArray(),
            "ready-advertisement-is-derived-from-validated-registry");
        Assert.AreEqual(0, resolutionCalls, "ready-does-not-resolve-heavy-handler");

        Assert.IsFalse(
            registry.TryResolve(
                WorkerJobKind.CoordinateLookup,
                EmptyServices.Instance,
                out IWorkerJobHandlerAdapter? missing));
        Assert.IsNull(missing);
        Assert.AreEqual(0, resolutionCalls, "unregistered-kind-does-not-resolve-heavy-handler");

        Assert.IsTrue(registry.TryResolve(
            WorkerJobKind.ProcessAssets,
            EmptyServices.Instance,
            out IWorkerJobHandlerAdapter? handler));
        Assert.IsNotNull(handler);
        Assert.AreEqual(1, resolutionCalls, "registered-kind-resolves-lazily-after-lookup");
    }

    [TestMethod]
    [TestCategory("Change47")]
    public void Registry_RejectsDuplicatesAndIncompatibleDeclarationsBeforeResolution()
    {
        var first = new WorkerJobHandlerRegistration<ProcessAssetsRequest, ProcessAssetsResult>(
            WorkerJobDescriptors.ProcessAssets,
            _ => new Handler());
        var second = new WorkerJobHandlerRegistration<ProcessAssetsRequest, ProcessAssetsResult>(
            WorkerJobDescriptors.ProcessAssets,
            _ => new Handler());
        Assert.ThrowsExactly<ArgumentException>(() =>
            new WorkerJobHandlerRegistry([first, second]));

        var incompatible = new IncompatibleRegistration();
        Assert.ThrowsExactly<ArgumentException>(() =>
            new WorkerJobHandlerRegistry([incompatible]));
        Assert.AreEqual(0, incompatible.ResolveCalls, "incompatible-metadata-never-resolves-handler");
    }

    [TestMethod]
    [TestCategory("Change47")]
    public void Registry_RejectsSelfConsistentRegistrationThatDoesNotMatchCanonicalKindSchema()
    {
        var fakeDescriptor = new WorkerJobDescriptor(
            WorkerJobKind.ProcessAssets,
            typeof(FakeRequest),
            typeof(FakeResult),
            WorkerJobDescriptors.ProcessAssets.Arbitration);
        var fakeRegistration = new WorkerJobHandlerRegistration<FakeRequest, FakeResult>(
            fakeDescriptor,
            _ => new FakeHandler());

        Assert.ThrowsExactly<ArgumentException>(() =>
            new WorkerJobHandlerRegistry([fakeRegistration]));

        var reservedDescriptor = new WorkerJobDescriptor(
            WorkerJobKind.CoordinateLookup,
            typeof(FakeRequest),
            typeof(FakeResult),
            WorkerJobDescriptors.ProcessAssets.Arbitration);
        var reservedRegistration = new WorkerJobHandlerRegistration<FakeRequest, FakeResult>(
            reservedDescriptor,
            _ => new FakeHandler());
        Assert.ThrowsExactly<ArgumentException>(() =>
            new WorkerJobHandlerRegistry([reservedRegistration]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new WorkerJobDescriptor(
                (WorkerJobKind)999,
                typeof(FakeRequest),
                typeof(FakeResult),
                WorkerJobDescriptors.ProcessAssets.Arbitration));
    }

    [TestMethod]
    [TestCategory("Change47")]
    public void ProcessAssetsDispatch_UsesAdmittedIdentityOriginAndCanonicalMetadata()
    {
        var runId = Guid.Parse("12121212-1212-1212-1212-121212121212");
        foreach ((ProcessingRunTrigger Trigger, WorkerJobRequestOrigin Origin) item in new[]
        {
            (ProcessingRunTrigger.Manual, WorkerJobRequestOrigin.Manual),
            (ProcessingRunTrigger.Scheduled, WorkerJobRequestOrigin.Scheduled),
            (ProcessingRunTrigger.RunOnce, WorkerJobRequestOrigin.RunOnce)
        })
        {
            var request = new ProcessingRunRequest(runId, item.Trigger);
            var dispatch = new ProcessAssetsWorkerJobDispatch(request);

            Assert.AreSame(WorkerJobDescriptors.ProcessAssets, dispatch.Descriptor);
            Assert.AreSame(request, dispatch.Request.ProcessingRequest);
            Assert.AreEqual(runId, dispatch.Context.JobId);
            Assert.AreEqual(WorkerJobKind.ProcessAssets, dispatch.Context.JobKind);
            Assert.AreEqual(item.Origin, dispatch.Context.Origin);
            Assert.AreEqual(
                dispatch.Descriptor.Arbitration.IsCancellable,
                dispatch.IsCancellable,
                $"{item.Trigger}-cancellability-comes-from-descriptor");
        }
    }

    private sealed class Handler : IWorkerJobHandler<ProcessAssetsRequest, ProcessAssetsResult>
    {
        public ValueTask<ProcessAssetsResult> ExecuteAsync(
            WorkerJobContext context,
            ProcessAssetsRequest request,
            IWorkerJobEventReporter eventReporter,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UnixEpoch;
            return ValueTask.FromResult(
                new ProcessAssetsResult("manual", now, now, 0, 0, 0, 0));
        }
    }

    private sealed class IncompatibleRegistration : IWorkerJobHandlerRegistration
    {
        public int ResolveCalls { get; private set; }

        public WorkerJobDescriptor Descriptor => WorkerJobDescriptors.ProcessAssets;

        public Type DeclaredRequestType => typeof(ProcessAssetsRequest);

        public Type DeclaredResultType => typeof(FakeResult);

        public IWorkerJobHandlerAdapter Resolve(IServiceProvider services)
        {
            ResolveCalls++;
            throw new AssertFailedException("incompatible registration must not resolve");
        }
    }

    private sealed record FakeResult : IWorkerJobResult;

    private sealed record FakeRequest : IWorkerJobRequest;

    private sealed class FakeHandler : IWorkerJobHandler<FakeRequest, FakeResult>
    {
        public ValueTask<FakeResult> ExecuteAsync(
            WorkerJobContext context,
            FakeRequest request,
            IWorkerJobEventReporter eventReporter,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FakeResult());
    }

    private sealed class EmptyServices : IServiceProvider
    {
        internal static EmptyServices Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
