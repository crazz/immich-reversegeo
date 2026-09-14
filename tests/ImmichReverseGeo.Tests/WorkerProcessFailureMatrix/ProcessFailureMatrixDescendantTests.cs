using System.Diagnostics;
using System.Globalization;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
[DoNotParallelize]
public sealed class ProcessFailureMatrixDescendantTests
{
    [TestMethod]
    [DataRow(WorkerJobKind.ProcessAssets)]
    [DataRow(WorkerJobKind.CoordinateLookup)]
    [DataRow(WorkerJobKind.CacheMutation)]
    public async Task StopDeadline_KillsObservedDescendantAndReleasesItsRealCandidateLease(WorkerJobKind kind)
    {
        var clock = new MatrixClock();
        var host = await ProcessFailureMatrixHost.CreateAsync("unresponsive-tree", clock);
        Process? descendant = null;
        bool fallback = false;
        try
        {
            Task run = host.RunAsync(kind);
            var lease = await MatrixWait.ForAsync(host.Launcher.Started, "tree/native-root-launch");
            Task registered = MatrixFileSignal.WaitAsync(lease.Root, "descendant-pid.marker", "tree/descendant-registered");
            Task observed = await MatrixWait.ForAsync(Task.WhenAny(registered, lease.Session!.Completion), "tree/registration-or-early-exit");
            if (observed != registered)
            {
                var earlyExit = await lease.Session.Completion;
                Assert.Fail("Descendant fixture exited before registration: " + earlyExit.StandardErrorTail.Text);
            }
            await registered;
            int pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(lease.Root, "descendant-pid.marker")), CultureInfo.InvariantCulture);
            descendant = Process.GetProcessById(pid);
            // Acquire the OS handle while this exact process is alive; never rediscover it by PID during cleanup.
            _ = descendant.SafeHandle;
            Assert.IsFalse(descendant.HasExited);
            Assert.AreNotEqual(Environment.ProcessId, pid);
            Assert.AreNotEqual(lease.ProcessId, pid);
            await host.Launcher.Tap(lease).WaitForLogAsync("matrix:armed");
            string candidate = Path.Combine(lease.Root, "descendant-candidate.tmp");
            var ownership = new CacheCandidateOwnership();
            Assert.IsFalse(ownership.TryCleanupAbandoned(candidate), "The observed descendant must own a real native file lease.");
            Assert.IsTrue(File.Exists(candidate));

            Task stop = kind switch
            {
                WorkerJobKind.ProcessAssets => host.Processing.StopActiveRun()!,
                WorkerJobKind.CoordinateLookup => host.Lookup.CancelAsync(),
                WorkerJobKind.CacheMutation => host.Cache.CancelAsync(),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            await clock.WaitForOneShotAsync(TimeSpan.FromSeconds(10), "tree/production-grace");
            await MatrixFileSignal.WaitAsync(lease.Root, "matrix-cancel-observed.marker", "tree/child-observed-cancel");
            clock.Advance(TimeSpan.FromMilliseconds(9_999));
            Assert.IsFalse(descendant.HasExited);
            Assert.AreEqual(0, lease.TreeKillCalls);
            Assert.IsNotNull(host.Coordinator.ActiveOwner);
            Assert.IsFalse(stop.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.AreEqual(ChildProcessKillOutcome.Requested,
                await MatrixWait.ForAsync(lease.TreeKillObserved, "tree/whole-tree-escalation"));
            await MatrixWait.ForAsync(descendant.WaitForExitAsync(), "tree/exact-descendant-kernel-finality");
            await MatrixWait.ForAsync(stop, "tree/controller-stop-finality");
            await MatrixWait.ForAsync(run, "tree/controller-attempt-finality");
            var raw = await MatrixWait.ForAsync(lease.CompleteAsync(), "tree/root-exit-and-both-drains");
            Assert.IsTrue(descendant.HasExited);
            Assert.IsTrue(ownership.TryCleanupAbandoned(candidate), "The killed descendant must no longer hold its ownership lease.");
            Assert.IsFalse(File.Exists(candidate));
            Assert.IsFalse(File.Exists(candidate + ".owner"));
            Assert.IsNull(raw.JobTerminal);
            Assert.AreEqual(1, lease.TreeKillCalls);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.AreEqual(1, host.Releases);
            Assert.IsNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(0, host.ForbiddenHeavyResolutions);
            await host.JoinTelemetryAsync(lease);
            ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, kind,
                new("unresponsive-tree", "stop-deadline", MatrixRawExit.PresentPlatformRaw, null, true,
                    MatrixTerminalAuthority.None, null, "forced-stop", "invalid-lifecycle",
                    [6610, 6611, 6612, 6620, 6622, 6623, 6630, 6641], ForcedStop: true));
        }
        finally
        {
            try
            {
                if (descendant is not null && !descendant.HasExited)
                {
                    fallback = true;
                    descendant.Kill(entireProcessTree: true);
                    await MatrixWait.ForAsync(descendant.WaitForExitAsync(), "tree/fallback-exact-descendant-reap");
                }
            }
            finally
            {
                descendant?.Dispose();
                await host.DisposeAsync();
            }
        }

        Assert.IsFalse(fallback, "Fallback descendant cleanup is a failed matrix row.");
        Assert.AreEqual(0, clock.ActiveTimerCount);
        Assert.IsFalse(Directory.Exists(host.Root));
        foreach (var lease in host.Launcher.Leases)
        {
            Assert.IsFalse(lease.ForcedCleanup);
            Assert.IsFalse(lease.IsRegistered);
            Assert.IsFalse(Directory.Exists(lease.Root));
        }
    }
}
