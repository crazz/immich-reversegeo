using System.Text;
using System.Text.Json.Nodes;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerEventDelivery;

namespace ImmichReverseGeo.Tests.ChildWorkerLaunching;

public sealed partial class ChildWorkerLaunchingTests
{
    [TestMethod]
    [TestCategory("Change65")]
    [DataRow(1, "identity", WorkerProtocolFailureCode.InvalidCorrelation)]
    [DataRow(2, "identity", WorkerProtocolFailureCode.InvalidCorrelation)]
    [DataRow(2, "kind", WorkerProtocolFailureCode.InvalidCorrelation)]
    [DataRow(1, "payload", WorkerProtocolFailureCode.InvalidPayload)]
    [DataRow(2, "payload", WorkerProtocolFailureCode.InvalidPayload)]
    [DataRow(1, "regression", WorkerProtocolFailureCode.InvalidSequence)]
    [DataRow(2, "regression", WorkerProtocolFailureCode.InvalidSequence)]
    [DataRow(1, "post-terminal", WorkerProtocolFailureCode.InvalidLifecycle)]
    [DataRow(2, "post-terminal", WorkerProtocolFailureCode.InvalidLifecycle)]
    public async Task SelectedDelivery_RawViolationCannotBecomeAcceptedProjection(
        int versionValue, string fault, WorkerProtocolFailureCode expected)
    {
        var version = Version(versionValue);
        await using var bridge = new BridgeTestCase();
        var process = new ByteProcess(6500 + versionValue);
        var dispatch = new ProcessAssetsWorkerJobDispatch(bridge.Request);
        var session = await ChildWorkerSession.CreateAsync(process, dispatch,
            new ProcessAssetsWorkerJobEventSink(bridge.Request, bridge.Bridge),
            TestOptions() with { EventDeliveryPolicy = new WorkerEventDeliveryPolicy { LosslessCapacity = 1 } },
            new ChildWorkerObserverArmingAcknowledgements(), version);
        try
        {
            var frames = VersionedFrames(version, bridge.Request, ProcessingRunOutcome.Completed, includeTerminal: true);
            process.StandardOutput.Write(frames[0]);
            await session.Startup.WaitAsync(VersionParityBound);
            if (fault == "post-terminal")
            {
                foreach (var frame in frames.Skip(1))
                {
                    process.StandardOutput.Write(frame);
                }
            }

            var invalid = JsonNode.Parse(Encoding.UTF8.GetString(fault == "post-terminal" ? frames[^1] : frames[1]))!;
            switch (fault)
            {
                case "identity":
                    invalid[versionValue == 1 ? "runId" : "jobId"] = Guid.NewGuid().ToString("D");
                    break;
                case "kind":
                    invalid["jobKind"] = WorkerJobKindNames.CoordinateLookup;
                    break;
                case "payload":
                    invalid["payload"]!["trigger"] = "";
                    break;
                case "regression":
                    invalid["sequence"] = 1;
                    break;
                case "post-terminal":
                    invalid["sequence"] = 10;
                    break;
                default:
                    throw new AssertFailedException("Unknown raw boundary fault.");
            }

            process.StandardOutput.Write(Encoding.UTF8.GetBytes(invalid.ToJsonString() + "\n"));
            process.StandardOutput.Complete();
            process.StandardError.Complete();
            process.Exit(0);
            var completion = await session.Completion.WaitAsync(VersionParityBound);
            await session.Settlement.WaitAsync(VersionParityBound);
            var failure = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(completion.FirstProtocolObservation);
            Assert.AreEqual(expected, failure.Failure.Code, $"v{versionValue}/{fault}: primary protocol authority is preserved");
            var delivery = completion.EventDelivery;
            Assert.IsNotNull(delivery);
            Assert.AreEqual(fault == "post-terminal" ? 7L : 1L, delivery.AcceptedLossless);
            Assert.AreEqual(fault == "post-terminal" ? 2L : 0L, delivery.AcceptedSnapshots);
            Assert.AreEqual(0L, delivery.AbandonedItems);
            Assert.AreEqual(fault == "post-terminal", bridge.Bridge.IsTerminal);
            Assert.AreEqual(fault == "post-terminal" ? 1L : 0L, bridge.State.ProcessedThisRun);
            Assert.AreEqual(fault == "post-terminal", bridge.Adapter.GetFinalizationReceipt(bridge.Request) is not null,
                "only the earlier valid terminal may establish a receipt");
        }
        finally
        {
            process.StandardOutput.ReleaseReads();
            process.StandardError.ReleaseReads();
            process.StandardOutput.Complete();
            process.StandardError.Complete();
            process.Exit(5);
            await session.DisposeAsync().AsTask().WaitAsync(VersionParityBound);
        }

        AssertDisposedExactlyOnce(process, "selected-delivery-raw-boundary");
    }
}
