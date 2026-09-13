using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
[DoNotParallelize]
public sealed class ProcessFailureMatrixLookupTests
{
    [TestMethod]
    [DataRow("real-coordinate-success", 0, "completed")]
    [DataRow("real-coordinate-domain-failure", 4, "failed")]
    [DataRow("real-coordinate-cancellation", 130, "cancelled")]
    public async Task ActualLookupHost_NeverAcquiresProcessingLockAcrossAllOutcomes(string fault, int exit, string terminal)
    {
        var host = await ProcessFailureMatrixHost.CreateAsync(fault);
        try
        {
            Task run = host.RunAsync(WorkerJobKind.CoordinateLookup);
            var lease = await host.Launcher.NextLaunchAsync();
            if (exit == 130)
            {
                await MatrixFileSignal.WaitAsync(lease.Root, "owned-cache-started.marker", "lookup/owned-source-work-active");
                await MatrixWait.ForAsync(host.Lookup.CancelAsync(), "lookup/cooperative-owned-source-cancel");
            }
            await MatrixWait.ForAsync(run, "lookup/actual-host-controller-finality");
            var raw = await MatrixWait.ForAsync(lease.CompleteAsync(), "lookup/actual-host-native-finality");
            Assert.AreEqual(exit, raw.ExitCode, raw.StandardErrorTail.Text);
            Assert.AreNotEqual(3, raw.ExitCode);
            Assert.IsNull(raw.FirstProtocolObservation);
            Assert.AreEqual(terminal, ((WorkerJobTerminalPayload)raw.JobTerminal!.Payload).Outcome.ToString().ToLowerInvariant());
            Assert.IsTrue(File.Exists(Path.Combine(lease.Root, "source-called.marker")));
            Assert.IsTrue(File.Exists(Path.Combine(lease.Root, "advisory-lock-probe-installed.marker")));
            Assert.IsFalse(File.Exists(Path.Combine(lease.Root, "advisory-lock-accessed.marker")));
            Assert.IsFalse(File.Exists(Path.Combine(lease.Root, "persistence-accessed.marker")));
            Assert.AreEqual(0, lease.TreeKillCalls);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.AreEqual(1, host.Releases);
            Assert.IsNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(0, host.ForbiddenHeavyResolutions);
            await host.JoinTelemetryAsync(lease);
            ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, WorkerJobKind.CoordinateLookup,
                new(fault, "actual-source-work", MatrixRawExit.ExactManaged, exit, true,
                    MatrixTerminalAuthority.AcceptedWorkerTerminal, terminal, exit == 4 ? "worker-failed" : terminal, null,
                    exit == 130 ? [6610, 6611, 6612, 6620, 6621, 6640, 6641] : [6610, 6611, 6612, 6640, 6641]));
        }
        finally
        {
            await host.DisposeAsync();
        }
        Assert.IsFalse(Directory.Exists(host.Root));
        foreach (var lease in host.Launcher.Leases)
        {
            Assert.IsFalse(lease.ForcedCleanup);
            Assert.IsFalse(lease.IsRegistered);
            Assert.IsFalse(Directory.Exists(lease.Root));
        }
    }
}
