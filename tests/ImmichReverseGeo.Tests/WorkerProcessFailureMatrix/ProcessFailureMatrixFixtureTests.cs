using System.Collections.Concurrent;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.LifecycleTelemetry;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.LifecycleTelemetry;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

// This foundation gate checks the fixture's raw protocol and process observations.
// Controller classification/admission assertions belong to the composed matrix rows.
[TestClass]
[TestCategory("Change67")]
public sealed class ProcessFailureMatrixFixtureTests
{
    [TestMethod]
    [DataRow(WorkerJobKind.ProcessAssets, "domain-failure")]
    [DataRow(WorkerJobKind.CoordinateLookup, "domain-failure")]
    [DataRow(WorkerJobKind.CacheMutation, "domain-failure")]
    [DataRow(WorkerJobKind.ProcessAssets, "additive-properties")]
    public async Task ClosedFixture_FailedTerminalPreservesKindIdentityExitAndFinalDrains(
        WorkerJobKind kind, string fault)
    {
        using var logs = new RecordingLifecycleLogs();
        var lease = new WorkerProcessFixtureLease
        {
            LifecycleLogger = logs.CreateLogger(LifecycleEventCatalog.Category)
        };
        string label = $"{kind}/{fault}";
        try
        {
            var sink = new MatrixJobSink();
            ChildWorkerSession session = kind == WorkerJobKind.ProcessAssets
                ? await lease.LaunchAsync("failure-matrix", true, "--matrix-fault", fault)
                : await lease.LaunchAsync("failure-matrix", Dispatch(lease, kind), sink,
                    InternalWorkerProtocolVersion.V2, true, "--matrix-fault", fault);
            var completion = await MatrixWait.ForAsync(lease.CompleteAsync(), label + "/exit-and-drains");
            await MatrixWait.ForAsync(session.Settlement, label + "/session-disposal");

            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(completion.Startup, label);
            Assert.AreEqual(4, completion.ExitCode, label + ": accepted domain failure must exit4");
            Assert.IsNull(completion.FirstProtocolObservation, label + ": fixture must emit valid known fields");
            Assert.AreEqual(kind, completion.JobKind, label);
            Assert.AreEqual(lease.Request.RunId, completion.JobId, label);
            Assert.AreEqual(lease.ProcessId, completion.ProcessId, label);
            Assert.IsTrue(lease.HasExited, label + ": observe the real OS exit");
            Assert.AreEqual(1, lease.ProcessDisposeCalls, label + ": exact native adapter disposal");
            Assert.AreEqual(0, lease.TreeKillCalls, label + ": normal failure is orderly");
            CollectionAssert.AreEqual(lease.WrittenInput.ToArray(), File.ReadAllBytes(lease.CapturePath), label);

            if (kind == WorkerJobKind.ProcessAssets)
            {
                Assert.AreEqual(WorkerProtocolV1.FailedType, completion.Terminal!.Type, label);
            }
            else
            {
                var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(completion.JobTerminal!.Payload, label);
                Assert.AreEqual(WorkerJobTerminalOutcome.Failed, terminal.Outcome, label);
                Assert.AreEqual(1, sink.Events.Count(e => e.Payload is WorkerJobTerminalPayload), label);
                Assert.IsFalse(sink.Events.Any(e =>
                    System.Text.Encoding.UTF8.GetString(WorkerJobProtocolCodec.Serialize(e))
                        .Contains("futureMatrixProperty", StringComparison.Ordinal)),
                    label + ": additive fields must not survive canonical serialization");
            }

            Assert.AreEqual(1, logs.Entries.Count(e => e.Event.Id == 6640), label + ": accepted terminal telemetry once");
            Assert.IsFalse(logs.Entries.Any(e => e.Event.Id == 6630), label + ": no fabricated violation");
            foreach (var entry in logs.Entries)
            {
                Assert.AreEqual(lease.Request.RunId, entry["job_id"], label);
                Assert.AreEqual(kind.ToString(), entry["job_kind"], label);
                Assert.IsFalse(entry.Rendered.Contains("matrix-secret", StringComparison.Ordinal), label);
                Assert.IsNull(entry.Exception, label);
                Assert.AreEqual(0, entry.Scopes.Length, label);
            }
        }
        finally
        {
            await MatrixWait.ForAsync(lease.DisposeAsync().AsTask(), label + "/unconditional-cleanup");
        }

        Assert.IsFalse(lease.ForcedCleanup, label + ": no emergency reap on a normal row");
        Assert.IsFalse(lease.IsRegistered, label + ": registry ownership released");
        Assert.IsFalse(Directory.Exists(lease.Root), label + ": isolated root removed");
    }

    internal static WorkerJobDispatch Dispatch(WorkerProcessFixtureLease lease, WorkerJobKind kind) => kind switch
    {
        WorkerJobKind.ProcessAssets => new ProcessAssetsWorkerJobDispatch(lease.Request),
        WorkerJobKind.CoordinateLookup => new CoordinateLookupWorkerJobDispatch(lease.Request.RunId,
            new CoordinateLookupRequest(47, 8, false, false, false,
                new CoordinateLookupCityResolverOverrides(null, []))),
        WorkerJobKind.CacheMutation => new CacheMutationWorkerJobDispatch(lease.Request.RunId,
            new CacheMutationRequest(CacheMutationSource.Gadm, CacheMutationOperation.Refresh, "CHE")),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private sealed class MatrixJobSink : IWorkerJobEventSink
    {
        private readonly ConcurrentQueue<WorkerJobOutputMessage> _events = new();
        internal WorkerJobOutputMessage[] Events => _events.ToArray();
        public ValueTask AcceptAsync(WorkerJobOutputMessage message, CancellationToken cancellationToken)
        {
            _events.Enqueue(message);
            return ValueTask.CompletedTask;
        }
    }
}

internal static class MatrixWait
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    internal static async Task<T> ForAsync<T>(Task<T> task, string phase)
    {
        try
        {
            return await task.WaitAsync(Watchdog);
        }
        catch (TimeoutException failure)
        {
            throw new AssertFailedException($"Process matrix watchdog expired: {phase}.", failure);
        }
    }

    internal static async Task ForAsync(Task task, string phase)
    {
        try
        {
            await task.WaitAsync(Watchdog);
        }
        catch (TimeoutException failure)
        {
            throw new AssertFailedException($"Process matrix watchdog expired: {phase}.", failure);
        }
    }
}
