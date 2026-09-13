using System;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerEventDelivery;

namespace ImmichReverseGeo.Tests.WorkerEventDelivery;

[TestClass]
[TestCategory("Change65")]
public sealed class WorkerEventDeliverySessionTests
{
    [TestMethod]
    [DataRow("malformed", "--malformed-kind", "utf8", WorkerProtocolFailureCode.InvalidEncoding)]
    [DataRow("malformed", "--malformed-kind", "json", WorkerProtocolFailureCode.MalformedJson)]
    [DataRow("malformed", "--malformed-kind", "framing", WorkerProtocolFailureCode.InvalidFraming)]
    [DataRow("unknown", "--unknown-kind", "version", WorkerProtocolFailureCode.UnsupportedVersion)]
    [DataRow("unknown", "--unknown-kind", "category", WorkerProtocolFailureCode.UnsupportedType)]
    [DataRow("unknown", "--unknown-kind", "type", WorkerProtocolFailureCode.UnsupportedType)]
    [DataRow("invalid-sequence", "--sequence-fault", "gap", WorkerProtocolFailureCode.InvalidSequence)]
    [DataRow("invalid-sequence", "--sequence-fault", "replay", WorkerProtocolFailureCode.InvalidSequence)]
    [DataRow("oversize", null, null, WorkerProtocolFailureCode.MessageTooLarge)]
    public async Task InvalidRealOutput_IsRejectedBeforeSelectedDeliveryInBothVersions(
        string scenario, string? option, string? value, WorkerProtocolFailureCode expected)
    {
        foreach (var version in new[] { InternalWorkerProtocolVersion.V1, InternalWorkerProtocolVersion.V2 })
        {
            await using var bridge = new BridgeTestCase();
            var lease = new WorkerProcessFixtureLease
            {
                Request = bridge.Request,
                LauncherOptions = new ChildWorkerLauncherOptions
                {
                    EventDeliveryPolicy = new WorkerEventDeliveryPolicy { LosslessCapacity = 1 }
                }
            };
            await using (lease)
            {
                var session = await lease.LaunchAsync(scenario, bridge.Bridge, version, true,
                    option is null ? [] : [option, value!]);
                var completion = await lease.CompleteAsync();
                await session.Settlement.WaitAsync(WorkerProcessFixtureLease.Watchdog);
                var failure = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(completion.FirstProtocolObservation);
                Assert.AreEqual(expected, failure.Failure.Code, $"{version}: original primary failure is retained");
                Assert.IsNull(completion.Terminal);
                var delivery = completion.EventDelivery;
                Assert.IsNotNull(delivery, "explicit policy reached the actual child-worker session");
                Assert.AreEqual(1L, delivery.AcceptedLossless, "only valid ready entered delivery");
                Assert.AreEqual(1L, delivery.DeliveredLossless);
                Assert.AreEqual(0L, delivery.AcceptedSnapshots);
                Assert.AreEqual(0L, delivery.ReplacedSnapshots);
                Assert.AreEqual(0L, delivery.AbandonedItems, "invalid raw data never became an abandoned accepted event");
                Assert.IsNull(delivery.TerminalFlushMilliseconds);
                Assert.IsFalse(bridge.Bridge.IsTerminal);
                Assert.AreEqual(0L, bridge.State.ProcessedThisRun);
                Assert.IsNull(bridge.Adapter.GetFinalizationReceipt(bridge.Request));
                lease.AssertExactCapture();
            }

            Assert.IsFalse(lease.ForcedCleanup);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.IsFalse(lease.IsRegistered);
        }
    }

    [TestMethod]
    [DataRow(InternalWorkerProtocolVersion.V1)]
    [DataRow(InternalWorkerProtocolVersion.V2)]
    public async Task RealSuccessFixture_JoinsAcceptedDeliveryBeforeSessionFinality(InternalWorkerProtocolVersion version)
    {
        await using var bridge = new BridgeTestCase();
        var lease = new WorkerProcessFixtureLease
        {
            Request = bridge.Request,
            LauncherOptions = new ChildWorkerLauncherOptions
            {
                EventDeliveryPolicy = new WorkerEventDeliveryPolicy { LosslessCapacity = 1 }
            }
        };
        await using (lease)
        {
            var session = await lease.LaunchAsync("success", bridge.Bridge, version);
            var completion = await lease.CompleteAsync();
            await session.Settlement.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(completion.Startup);
            Assert.AreEqual(0, completion.ExitCode);
            Assert.IsNull(completion.FirstProtocolObservation);
            Assert.IsInstanceOfType<CompletedPayload>(completion.Terminal!.Payload);
            Assert.IsNotNull(completion.EventDelivery, "the explicit accepted-delivery policy reached the production session");
            Assert.AreEqual(WorkerEventDeliveryFinality.Terminal, completion.EventDelivery.Finality);
            Assert.AreEqual(1L, completion.EventDelivery.AcceptedSnapshots);
            Assert.AreEqual(1L, completion.EventDelivery.DeliveredSnapshots);
            Assert.AreEqual(7L, completion.EventDelivery.DeliveredLossless);
            Assert.AreEqual(0L, completion.EventDelivery.AbandonedItems);
            Assert.IsTrue(bridge.Bridge.IsTerminal);
            Assert.IsFalse(bridge.State.IsRunning);
            Assert.AreEqual(1L, bridge.State.ProcessedThisRun);
            Assert.IsNull(bridge.State.CurrentActivity);
            Assert.IsNotNull(bridge.Adapter.GetFinalizationReceipt(bridge.Request));
            lease.AssertExactCapture();
        }

        Assert.IsFalse(lease.ForcedCleanup);
        Assert.AreEqual(1, lease.ProcessDisposeCalls);
        Assert.IsFalse(lease.IsRegistered);
    }
}
