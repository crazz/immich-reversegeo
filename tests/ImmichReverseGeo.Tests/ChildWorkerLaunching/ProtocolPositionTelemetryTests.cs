using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.LifecycleTelemetry;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.LifecycleTelemetry;
using ImmichReverseGeo.Web.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.ChildWorkerLaunching;

public sealed partial class ChildWorkerLaunchingTests
{
    [TestMethod]
    [TestCategory("Change66")]
    [DataRow("sequence", "events", "invalid-sequence", 9L)]
    [DataRow("correlation", "events", "invalid-correlation", 2L)]
    [DataRow("early-terminal", "terminal", "invalid-lifecycle", 2L)]
    [DataRow("drain", "drain", "invalid-lifecycle", 4L)]
    [DataRow("missing-terminal", "terminal", "invalid-lifecycle", null)]
    [DataRow("encoding", "ready", "invalid-encoding", null)]
    public async Task ProtocolViolation_CopiesOnlyParsedPositionAndRetainsFirstFailure(
        string scenario, string phase, string code, long? sequence)
    {
        using var logs = new RecordingLifecycleLogs();
        var process = new ByteProcess(16630);
        var launcher = new ChildWorkerLauncher(new RecordingFactory { Process = process },
            logs.CreateLogger(LifecycleEventCatalog.Category));
        var dispatch = new CacheMutationWorkerJobDispatch(Guid.NewGuid(),
            new CacheMutationRequest(CacheMutationSource.Overture, CacheMutationOperation.Refresh, "CHE"));
        var invocation = Assert.IsInstanceOfType<WorkerCommandInvocationResolution.Success>(new CacheInvocationBuilder().Build());
        var sink = new CacheEventSink();
        var launched = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(await launcher.LaunchAsync(
            invocation.Invocation, dispatch, sink,
            new ChildWorkerLauncherOptions { ReadyTimeout = Timeout.InfiniteTimeSpan }, CancellationToken.None));
        ChildWorkerSession session = launched.Session;
        try
        {
            var at = DateTimeOffset.UnixEpoch;
            if (scenario == "encoding")
            {
                process.StandardOutput.Write([0xff, 0xfe, (byte)'\n']);
                await session.Startup.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Ready(1, at,
                    new WorkerJobReadyPayload([WorkerJobKind.CacheMutation]))));
                await session.Startup.WaitAsync(TimeSpan.FromSeconds(5));
                if (scenario == "sequence")
                {
                    process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.JobStarted(dispatch.Context, "manual", at, 9)));
                }
                else if (scenario == "correlation")
                {
                    var wrong = new WorkerJobContext(Guid.NewGuid(), WorkerJobKind.CacheMutation, WorkerJobRequestOrigin.Manual);
                    process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.JobStarted(wrong, "manual", at, 2)));
                }
                else if (scenario == "early-terminal")
                {
                    process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Terminal(dispatch.Context,
                        new WorkerJobTerminalPayload(WorkerJobTerminalOutcome.Cancelled, at, at, null, null), 2)));
                }
                else
                {
                    process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.JobStarted(dispatch.Context, "manual", at, 2)));
                    if (scenario == "drain")
                    {
                        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Terminal(dispatch.Context,
                            new WorkerJobTerminalPayload(WorkerJobTerminalOutcome.Cancelled, at, at, null, null), 3)));
                        await sink.TerminalObserved.WaitAsync(TimeSpan.FromSeconds(5));
                        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.JobStarted(dispatch.Context, "manual", at, 4)));
                    }
                }
            }

            process.StandardOutput.Complete();
            process.StandardError.Complete();
            process.Exit(scenario == "drain" ? 130 : 6);
            await session.Settlement.WaitAsync(TimeSpan.FromSeconds(5));
            var violation = logs.Entries.Single(entry => entry.Event.Id == 6630);
            Assert.AreEqual(phase, violation["protocol_phase"]);
            Assert.AreEqual(code, violation["violation_code"]);
            Assert.AreEqual(sequence, violation["sequence"]);
            Assert.AreEqual("worker-output", violation["protocol_direction"]);
            Assert.IsNull(violation.Exception);
            Assert.HasCount(0, violation.Scopes);
            Assert.IsFalse(violation.Rendered.Contains("CHE", StringComparison.Ordinal));
            Assert.HasCount(scenario == "drain" ? 1 : 0, logs.Entries.Where(entry => entry.Event.Id == 6640));
        }
        finally
        {
            process.StandardOutput.Complete();
            process.StandardError.Complete();
            process.Exit(6);
            await session.DisposeAsync();
        }
    }
}
