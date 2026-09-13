using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.LifecycleTelemetry;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.LifecycleTelemetry;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventDelivery;

namespace ImmichReverseGeo.Tests.ChildWorkerLaunching;

public sealed partial class ChildWorkerLaunchingTests
{
    [TestMethod]
    [TestCategory("Change66")]
    [DataRow(false, "terminal", false, "cancelled")]
    [DataRow(true, "terminal", false, "cancelled")]
    [DataRow(false, "terminal", true, "infrastructure-failed")]
    [DataRow(true, "terminal", true, "infrastructure-failed")]
    [DataRow(false, "no-terminal", false, "missing-terminal")]
    [DataRow(true, "no-terminal", false, "missing-terminal")]
    [DataRow(false, "no-terminal", true, "infrastructure-failed")]
    [DataRow(true, "no-terminal", true, "infrastructure-failed")]
    [DataRow(false, "rejected-terminal", false, "infrastructure-failed")]
    [DataRow(true, "rejected-terminal", false, "infrastructure-failed")]
    [DataRow(false, "protocol", true, "protocol-failed")]
    [DataRow(true, "protocol", true, "protocol-failed")]
    public async Task CapabilityOwner_EmitsFinalClassificationAfterCleanupEvenWhenCleanupFaults(
        bool cache, string ending, bool cleanupFails, string classification)
    {
        var logs = new RecordingLifecycleLogs();
        var raw = new ByteProcess(16601);
        var failure = new IOException("secret cleanup password sql path stack");
        var process = new TelemetryDisposalProcess(raw, cleanupFails ? failure : null);
        var launcher = new ChildWorkerLauncher(new RecordingFactory { Process = process },
            logs.CreateLogger(LifecycleEventCatalog.Category));
        await using var coordinator = new WorkerJobCoordinator(
            [WorkerJobDescriptors.CoordinateLookup, WorkerJobDescriptors.CacheMutation]);
        Guid jobId = Guid.NewGuid();
        WorkerJobDispatch dispatch = cache
            ? new CacheMutationWorkerJobDispatch(jobId, new CacheMutationRequest(
                CacheMutationSource.Overture, CacheMutationOperation.Refresh, "CHE"))
            : new CoordinateLookupWorkerJobDispatch(jobId, new CoordinateLookupRequest(
                47.4, 8.5, false, false, false, new CoordinateLookupCityResolverOverrides(null, [])));
        var admission = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(coordinator.TryAdmit(dispatch));
        await using var lease = admission.Lease;
        var sink = new AcceptedCapabilityEventSink(lease.Context,
            message => ending != "rejected-terminal" || message.Type != WorkerJobProtocolV2.TerminalType);
        Task completion;
        Func<ValueTask> dispose;
        if (cache)
        {
            var client = new CacheMutationWorkerClient(new CacheInvocationBuilder(), launcher, TimeProvider.System);
            var started = Assert.IsInstanceOfType<CacheMutationWorkerStartResult.Started>(await client.StartAsync(
                lease, ((CacheMutationWorkerJobDispatch)dispatch).Request, sink, CancellationToken.None));
            completion = started.Session.Completion;
            dispose = started.Session.DisposeAsync;
        }
        else
        {
            var client = new CoordinateLookupWorkerClient(new CacheInvocationBuilder(), launcher, TimeProvider.System);
            var started = Assert.IsInstanceOfType<CoordinateLookupWorkerStartResult.Started>(await client.StartAsync(
                lease, ((CoordinateLookupWorkerJobDispatch)dispatch).Request, sink, CancellationToken.None));
            completion = started.Session.Completion;
            dispose = started.Session.DisposeAsync;
        }

        bool sendsTerminal = ending is "terminal" or "rejected-terminal";
        int exitCode = sendsTerminal ? 130 : 0;
        try
        {
            DateTimeOffset at = DateTimeOffset.UnixEpoch;
            raw.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Ready(1, at,
                new WorkerJobReadyPayload([dispatch.Context.JobKind]))));
            await raw.StandardInput.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            raw.StandardOutput.Write(Frame(WorkerJobProtocolMapper.JobStarted(dispatch.Context, "manual", at, 2)));
            if (sendsTerminal)
            {
                raw.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Terminal(dispatch.Context,
                    new WorkerJobTerminalPayload(WorkerJobTerminalOutcome.Cancelled, at, at, null, null), 3)));
            }
            else if (ending == "protocol")
            {
                raw.StandardOutput.Write("{secret malformed private payload}\n"u8.ToArray());
            }

            raw.StandardOutput.Complete();
            raw.Exit(exitCode);
            Assert.IsFalse(completion.IsCompleted, "Final classification must still await stderr and cleanup.");
            Assert.HasCount(0, logs.Entries.Where(entry => entry.Event.Id == 6641));
            raw.StandardError.Write("secret stderr token connection-string\n"u8.ToArray());
            raw.StandardError.Complete();
            if (cleanupFails)
            {
                var thrown = await Assert.ThrowsExactlyAsync<IOException>(() => completion.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreSame(failure, thrown, "Keep the original owned cleanup exception.");
            }
            else
            {
                await completion.WaitAsync(TimeSpan.FromSeconds(5));
            }

            var final = logs.Entries.Single(entry => entry.Event.Id == 6641);
            Assert.AreEqual(classification, final["process_classification"]);
            Assert.AreEqual(ending == "terminal" ? "cancelled" : null, final["terminal_outcome"]);
            Assert.AreEqual(1, raw.DisposeCalls);
            Assert.AreEqual("available", final["memory_observation"]);
            Assert.AreEqual(4096L, final["peak_working_set_bytes"]);
            Assert.IsTrue((long)final["memory_sample_count"]! >= 2);
            Assert.HasCount(ending == "terminal" ? 1 : 0, logs.Entries.Where(entry => entry.Event.Id == 6640));
            Assert.HasCount(0, logs.Entries.Where(entry => entry.Event.Id == 6650));
            foreach (var entry in logs.Entries)
            {
                Assert.AreEqual(jobId, entry["job_id"]);
                Assert.AreEqual(cache ? "CacheMutation" : "CoordinateLookup", entry["job_kind"]);
                Assert.AreEqual(cache ? "cache-ui" : "lookup-ui", entry["job_origin"]);
                Assert.AreEqual(entry.Event.Id == 6610 ? null : 16601, entry["worker_process_id"]);
                Assert.IsNull(entry.Exception);
                Assert.HasCount(0, entry.Scopes);
                Assert.IsFalse(entry.Rendered.Contains("secret", StringComparison.OrdinalIgnoreCase));
                Assert.IsFalse(entry.Rendered.Contains("47.4", StringComparison.Ordinal));
                Assert.IsFalse(entry.Rendered.Contains("CHE", StringComparison.Ordinal));
            }
        }
        finally
        {
            raw.StandardOutput.Complete();
            raw.StandardError.Complete();
            raw.Exit(exitCode);
            try
            {
                await dispose();
            }
            catch (IOException exception) when (cleanupFails && ReferenceEquals(exception, failure))
            {
            }
        }
    }

    private sealed class TelemetryDisposalProcess(ByteProcess inner, IOException? failure) : IChildProcess
    {
        public int ProcessId => inner.ProcessId;
        public Stream StandardInput => ((IChildProcess)inner).StandardInput;
        public Stream StandardOutput => ((IChildProcess)inner).StandardOutput;
        public Stream StandardError => ((IChildProcess)inner).StandardError;
        public Task<int> WaitForExitAsync() => inner.WaitForExitAsync();
        public ChildProcessExitState GetExitState() => inner.GetExitState();
        public ChildProcessKillOutcome KillProcessTree() => inner.KillProcessTree();
        public ChildWorkingSetObservation ReadWorkingSet() => ChildWorkingSetObservation.Available(4096);
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            if (failure is not null)
            {
                throw failure;
            }
        }
    }
}
