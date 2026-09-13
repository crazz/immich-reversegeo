using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.ApplicationComposition;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using ImmichReverseGeo.Web.WorkerEventStateBridge;

namespace ImmichReverseGeo.Tests.WorkerEventDelivery;

[TestClass]
[TestCategory("Change65")]
[DoNotParallelize]
public sealed class WorkerEventDeliveryMeasurementTests
{
    [TestMethod]
    [DataRow(InternalWorkerProtocolVersion.V1, 10000, 0)]
    [DataRow(InternalWorkerProtocolVersion.V2, 1000, 0)]
    [DataRow(InternalWorkerProtocolVersion.V2, 10000, 0)]
    [DataRow(InternalWorkerProtocolVersion.V2, 10000, 100)]
    public async Task RepresentativeProcessAndBlazorBurst_RecordsBoundedDeliveryCost(
        InternalWorkerProtocolVersion version, int count, int barrierEvery)
    {
        var samples = new List<Measurement>();
        for (int trial = 1; trial <= 3; trial++)
        {
            samples.Add(await MeasureAsync(version, count, barrierEvery, trial));
        }

        var report = new
        {
            Version = version.ToString(),
            InputSnapshots = count,
            LosslessBarrierEvery = barrierEvery,
            LosslessCapacity = WorkerEventDeliveryPolicy.DefaultLosslessCapacity,
            NotificationCadenceMilliseconds = WorkerEventDeliveryPolicy.DefaultNotificationCadence.TotalMilliseconds,
            WorkerEventDeliveryPolicy.ProductionEnabled,
            Environment = new
            {
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                System.Environment.ProcessorCount,
                AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
            },
            Scope = "Parent managed allocations include setup, raw validation, delivery and real Logs rendering. Retained delta is full-GC live managed memory at completed final rendering before fixture cleanup; it excludes child/native RSS. Wall time includes child startup and final rendering. No universal performance threshold is asserted.",
            Samples = samples
        };
        string directory = Path.Combine(WebControlPlaneGuardTests.FindRoot(), "_out", "worker-event-delivery-measurements");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{version}-{count}-{barrierEvery}-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<Measurement> MeasureAsync(
        InternalWorkerProtocolVersion version, int count, int barrierEvery, int trial)
    {
        long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var elapsed = Stopwatch.StartNew();
        var policy = new WorkerEventDeliveryPolicy();
        await using var state = new ProcessingState(TimeProvider.System, policy);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var reporter = new ProcessingStateEventReporter(state);
        Assert.IsTrue(reporter.Arm(request));
        state.MarkPending();
        await using var bridge = new WorkerEventStateBridgeFactory(reporter).Create(request);
        var logs = new ImmichReverseGeo.Web.Components.Pages.Logs();
        logs.GetType().GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .SetValue(logs, state);
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(logs);
        await using var lease = new WorkerProcessFixtureLease
        {
            Request = request,
            LauncherOptions = new ChildWorkerLauncherOptions { EventDeliveryPolicy = policy }
        };
        ChildWorkerSession session = await lease.LaunchAsync("progress-burst", bridge, version, true,
            "--progress-count", count.ToString(CultureInfo.InvariantCulture),
            "--barrier-every", barrierEvery.ToString(CultureInfo.InvariantCulture));
        var completion = await lease.CompleteAsync();
        await session.Settlement.WaitAsync(WorkerProcessFixtureLease.Watchdog);
        while (true)
        {
            Task next = renderer.NextRenderAsync();
            if ((await renderer.ReadAsync()).Text.Contains($"Run complete. Processed={count} Skipped=0 Errors=0", StringComparison.Ordinal))
            {
                break;
            }

            await next.WaitAsync(WorkerProcessFixtureLease.Watchdog);
        }

        elapsed.Stop();
        var delivery = completion.EventDelivery!;
        var notifications = state.NotificationObservation!;
        Assert.AreEqual(0, completion.ExitCode);
        Assert.IsNull(completion.FirstProtocolObservation);
        Assert.AreEqual(WorkerEventDeliveryFinality.Terminal, delivery.Finality);
        Assert.AreEqual(count, delivery.AcceptedSnapshots);
        Assert.AreEqual(count, state.ProcessedThisRun);
        Assert.AreEqual(4L + (barrierEvery == 0 ? 0 : count / barrierEvery * 6), delivery.DeliveredLossless);
        Assert.IsTrue(delivery.FifoHighWater <= policy.LosslessCapacity);
        Assert.AreEqual(0L, delivery.AbandonedItems);
        Assert.AreEqual(1L, notifications.FinalAccepted);
        Assert.AreEqual(1L, notifications.FinalDispatched);
        Assert.IsNotNull(reporter.GetFinalizationReceipt(request));
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        long retained = GC.GetTotalMemory(forceFullCollection: true) - managedBefore;
        GC.KeepAlive(lease);
        GC.KeepAlive(renderer);
        GC.KeepAlive(state);
        double seconds = elapsed.Elapsed.TotalSeconds;
        return new(trial, elapsed.Elapsed.TotalMilliseconds,
            (delivery.AcceptedSnapshots + delivery.AcceptedLossless) / seconds,
            delivery.DeliveredSnapshots / seconds,
            notifications.OrdinaryDispatched / seconds,
            renderer.RenderCount, allocated, retained, delivery, notifications);
    }

    private sealed record Measurement(
        int Trial, double WallMilliseconds, double AcceptedEventsPerSecond,
        double DeliveredSnapshotsPerSecond, double OrdinaryNotificationsPerSecond,
        int RenderBatches, long ParentAllocatedBytes, long ParentLiveManagedDeltaBytes,
        WorkerEventDeliveryObservation Delivery, NotificationCadenceObservation Notifications);
}
