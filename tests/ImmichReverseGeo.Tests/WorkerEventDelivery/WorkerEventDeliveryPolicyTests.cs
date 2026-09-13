using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerEventDelivery;

namespace ImmichReverseGeo.Tests.WorkerEventDelivery;

[TestClass]
[TestCategory("Change65")]
public sealed class WorkerEventDeliveryPolicyTests
{
    [TestMethod]
    public void Replaceability_RequiresCanonicalDescriptorAndAbsoluteProcessingPayload()
    {
        var context = new WorkerJobContext(Guid.NewGuid(), WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.Manual);
        var progress = WorkerJobProtocolMapper.Map(context,
            new WorkerJobHandlerEvent(DateTimeOffset.UtcNow, new ProcessAssetsProgressPayload(1, 1, 0, 0)), 4);
        Assert.IsTrue(WorkerEventDeliveryPolicy.IsReplaceable(WorkerJobDescriptors.ProcessAssets, progress));
        Assert.IsFalse(WorkerEventDeliveryPolicy.IsReplaceable(WorkerJobDescriptors.ProcessAssets with { }, progress),
            "copying descriptor metadata does not opt a different contract into replacement");
        Assert.IsFalse(WorkerEventDeliveryPolicy.IsReplaceable(WorkerJobDescriptors.CoordinateLookup, progress));
        Assert.IsFalse(WorkerEventDeliveryPolicy.IsReplaceable(WorkerJobDescriptors.CacheMutation, progress));
        var log = WorkerJobProtocolMapper.Map(context,
            new WorkerJobHandlerEvent(DateTimeOffset.UtcNow, new WorkerJobLogPayload("warning", "retained diagnostic")), 5);
        Assert.IsFalse(WorkerEventDeliveryPolicy.IsReplaceable(WorkerJobDescriptors.ProcessAssets, log),
            "lossless log retention is independent from bounded storage elsewhere");
    }

    [TestMethod]
    [DataRow(0, 100)]
    [DataRow(-1, 100)]
    [DataRow(256, 0)]
    [DataRow(256, -1)]
    public void InvalidInternalPolicy_FailsBeforeStartingDelivery(int capacity, int milliseconds)
    {
        var policy = new WorkerEventDeliveryPolicy
        {
            LosslessCapacity = capacity,
            NotificationCadence = TimeSpan.FromMilliseconds(milliseconds)
        };
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(policy.Validate);
    }

    [TestMethod]
    [DataRow(WorkerJobKind.CoordinateLookup)]
    [DataRow(WorkerJobKind.CacheMutation)]
    public void CapabilityScope_UsesTransportIdentityButCannotRebindTheOwner(WorkerJobKind kind)
    {
        var ownerContext = new WorkerJobContext(Guid.NewGuid(), kind, WorkerJobRequestOrigin.Manual);
        var sink = new AcceptedCapabilityEventSink(ownerContext, _ => true);
        Assert.ThrowsExactly<InvalidOperationException>(() => sink.BindDeliveryScope(new(
            InternalWorkerProtocolVersion.V2, new WorkerJobContext(Guid.NewGuid(), kind, WorkerJobRequestOrigin.Manual))));
        var transportContext = ownerContext with { };
        Assert.AreNotSame(ownerContext, transportContext);
        var scope = new WorkerEventDeliveryScope(InternalWorkerProtocolVersion.V2, transportContext);
        sink.BindDeliveryScope(scope);
        sink.BindDeliveryScope(scope);
        Assert.ThrowsExactly<InvalidOperationException>(() => sink.BindDeliveryScope(new(
            InternalWorkerProtocolVersion.V2, transportContext)), "another equal-identity scope is still another session owner");
    }
}
