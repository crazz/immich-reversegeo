using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.CrossProcessRunExclusion;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ProcessFailureMatrixPostgresTests
{
    [ClassCleanup]
    public static Task ReapOwnedResourcesAsync() => CrossProcessRunExclusionCase.ReapRemainingAsync();

    [TestMethod]
    [DataRow("held-success", 0, "completed", "completed")]
    [DataRow("domain-failure", 4, "failed", "worker-failed")]
    [DataRow("cooperative-cancel", 130, "cancelled", "cancelled")]
    [DataRow("matrix-output-failure", 6, null, "protocol-failed")]
    [DataRow("abrupt-death", null, null, "crashed")]
    [DataRow("matrix-unlock-false", 5, "completed", "terminal-exit-mismatch")]
    [DataRow("matrix-unlock-failure", 5, "completed", "terminal-exit-mismatch")]
    [DataRow("matrix-unlock-ambiguous", 5, "completed", "terminal-exit-mismatch")]
    [DataRow("matrix-dispose-failure", 5, "completed", "terminal-exit-mismatch")]
    public async Task OwnedDatabaseSession_FinalizesAndNextExplicitProcessReacquires(
        string fault, int? expectedExit, string? terminal, string classification)
    {
        var owned = await CrossProcessRunExclusionCase.CreateAsync(CancellationToken.None);
        ParentWorker? worker = null;
        ParentWorker? next = null;
        await using (owned)
        {
            var before = await owned.ReadDatabaseEffectsAsync(CancellationToken.None);
            worker = await owned.StartControlledAsync(fault == "abrupt-death" ? "held-success" : fault, CancellationToken.None);
            var backend = await worker.ReadMarkerAsync(CancellationToken.None);
            Assert.IsFalse(worker.HasExited);
            Assert.IsNotNull(worker.Coordinator.ActiveRequest);
            if (fault == "cooperative-cancel")
            {
                worker.StopCooperatively();
            }
            else if (fault == "abrupt-death")
            {
                Assert.AreEqual(ChildProcessKillOutcome.Requested, worker.KillTree());
            }
            else
            {
                await worker.ReleaseAsync(1, CancellationToken.None);
            }

            var raw = await MatrixWait.ForAsync(worker.CompleteAsync(CancellationToken.None), "postgres/owner-finality/" + fault);
            await worker.JoinLifecycleTelemetryAsync();
            if (expectedExit.HasValue)
            {
                Assert.AreEqual(expectedExit, raw.ExitCode);
            }
            else
            {
                Assert.IsNotNull(raw.ExitCode, "Abrupt process exit is retained as platform-raw evidence.");
            }
            AssertTerminal(worker, terminal);
            Assert.AreEqual(before, await owned.ReadDatabaseEffectsAsync(CancellationToken.None));
            Assert.IsNull(await owned.Database.FindOwnedBackendAsync(worker.ApplicationName, CancellationToken.None));
            await owned.AssertKeyFreeAsync(CancellationToken.None);
            Assert.IsTrue(worker.IsFullyFinalized);
            Assert.AreEqual(1, worker.ProcessDisposeCalls);
            Assert.AreEqual(1, worker.LauncherCalls, "A failed worker never retries itself.");
            AssertTelemetry(worker, fault, expectedExit, terminal, classification);
            if (fault.StartsWith("matrix-unlock-", StringComparison.Ordinal) || fault == "matrix-dispose-failure")
            {
                string[] journal = await File.ReadAllLinesAsync(Path.Combine(worker.WorkerRoot, "matrix-session.log"));
                Assert.AreEqual(1, journal.Count(line => line == "acquired:" + backend.ProcessId));
                Assert.AreEqual(1, journal.Count(line => line == "clear-pool:" + backend.ProcessId));
                Assert.AreEqual(1, journal.Count(line => line == "disposed:" + backend.ProcessId));
                string probe = journal.Single(line => line.StartsWith("quarantine-probe:", StringComparison.Ordinal));
                Assert.AreNotEqual("quarantine-probe:" + backend.ProcessId, probe,
                    "After real pool quarantine/disposal, the same data source opens a different physical session.");
                Assert.IsTrue(Array.FindIndex(journal, line => line.StartsWith("quarantine-probe:", StringComparison.Ordinal))
                    > Array.IndexOf(journal, "disposed:" + backend.ProcessId));
                worker.AssertProjectionReplayIsIdempotent();
                Assert.AreEqual(ProcessingRunOutcome.Completed, worker.Receipt!.Result.Outcome,
                    "Cleanup exit 5 supplements the earlier committed Completed terminal.");
            }

            next = await owned.StartProductionOnSameCoordinatorAsync(worker, CancellationToken.None);
            Assert.AreNotEqual(worker.Request.RunId, next.Request.RunId);
            Assert.AreNotEqual(worker.ProcessId, next.ProcessId);
            Assert.AreSame(worker.Coordinator, next.Coordinator);
            Assert.AreEqual(0, (await next.CompleteAsync(CancellationToken.None)).ExitCode);
            await next.JoinLifecycleTelemetryAsync();
            AssertTerminal(next, "completed");
            AssertTelemetry(next, "explicit-reacquisition", 0, "completed", "completed");
            await owned.AssertKeyFreeAsync(CancellationToken.None);
        }
        AssertCleanup(owned, worker, next);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DatabaseConnectOrOwnedSessionLoss_ExitsFiveAndReleases(bool sessionLoss)
    {
        var owned = await CrossProcessRunExclusionCase.CreateAsync(CancellationToken.None);
        ParentWorker? worker = null;
        await using (owned)
        {
            if (sessionLoss && !owned.Capabilities.CanTerminateOwnedBackends)
            {
                Assert.Inconclusive("PostgreSQL capability unavailable: terminate the exact test-owned backend.");
            }
            worker = await owned.StartControlledAsync(sessionLoss ? "ownership-loss" : "matrix-connect-failure", CancellationToken.None);
            if (sessionLoss)
            {
                var backend = await worker.ReadMarkerAsync(CancellationToken.None);
                Assert.IsTrue(await owned.Database.TerminateOwnedBackendAsync(backend.ProcessId,
                    backend.ApplicationName, CancellationToken.None));
            }
            Assert.AreEqual(5, (await MatrixWait.ForAsync(worker.CompleteAsync(CancellationToken.None),
                "postgres/connect-or-session-loss-finality")).ExitCode);
            await worker.JoinLifecycleTelemetryAsync();
            AssertTerminal(worker, "failed");
            if (!sessionLoss)
            {
                Assert.IsFalse(File.Exists(Path.Combine(worker.WorkerRoot, "domain-entered.marker")));
                Assert.IsFalse(worker.Events.Any(e => e.Payload is EligibilityDeterminedPayload));
            }
            Assert.IsNull(await owned.Database.FindOwnedBackendAsync(worker.ApplicationName, CancellationToken.None));
            AssertTelemetry(worker, "database-unavailable", 5, "failed", "infrastructure-failed");
            await owned.AssertKeyFreeAsync(CancellationToken.None);
        }
        AssertCleanup(owned, worker);
    }

    [TestMethod]
    public async Task FixedProductionKeyBusy_DoesNotEnterDomainOrReleaseOtherSession()
    {
        var owned = await CrossProcessRunExclusionCase.CreateAsync(CancellationToken.None);
        ParentWorker? owner = null;
        ParentWorker? contender = null;
        await using (owned)
        {
            var before = await owned.ReadDatabaseEffectsAsync(CancellationToken.None);
            owner = await owned.StartControlledAsync("held-success", CancellationToken.None);
            var backend = await owner.ReadMarkerAsync(CancellationToken.None);
            contender = await owned.StartControlledAsync("held-success", CancellationToken.None);
            Assert.AreNotSame(owner.Coordinator, contender.Coordinator);
            Assert.AreEqual(3, (await contender.CompleteAsync(CancellationToken.None)).ExitCode);
            await contender.JoinLifecycleTelemetryAsync();
            AssertTerminal(contender, "failed");
            Assert.IsFalse(contender.MarkerExists);
            Assert.IsFalse(File.Exists(Path.Combine(contender.WorkerRoot, "domain-entered.marker")));
            Assert.IsFalse(contender.Events.Any(e => e.Payload is EligibilityDeterminedPayload));
            Assert.AreEqual(before, await owned.ReadDatabaseEffectsAsync(CancellationToken.None));
            Assert.AreEqual(backend.ProcessId,
                (await owned.Database.FindOwnedBackendAsync(owner.ApplicationName, CancellationToken.None))!.ProcessId);
            AssertTelemetry(contender, "advisory-busy", 3, "failed", "busy");
            await owner.ReleaseAsync(1, CancellationToken.None);
            Assert.AreEqual(0, (await owner.CompleteAsync(CancellationToken.None)).ExitCode);
            await owner.JoinLifecycleTelemetryAsync();
            AssertTelemetry(owner, "busy-owner-release", 0, "completed", "completed");
            await owned.AssertKeyFreeAsync(CancellationToken.None);
        }
        AssertCleanup(owned, owner, contender);
    }

    private static void AssertTerminal(ParentWorker worker, string? terminal)
    {
        var terminals = worker.Events.Where(e => e.Type is "completed" or "failed" or "cancelled").ToArray();
        Assert.AreEqual(terminal is null ? 0 : 1, terminals.Length);
        if (terminal is not null)
        {
            Assert.AreEqual(terminal, terminals.Single().Type);
        }
        Assert.IsTrue(worker.IsFullyFinalized);
        worker.AssertProjectionReplayIsIdempotent();
    }

    private static void AssertTelemetry(ParentWorker worker, string fault, int? exit, string? terminal, string classification)
    {
        int[] events = terminal is null ? [6610, 6611, 6612, 6630, 6641]
            : terminal == "cancelled" ? [6610, 6611, 6612, 6620, 6621, 6640, 6641]
            : [6610, 6611, 6612, 6640, 6641];
        ProcessFailureMatrixTelemetry.AssertCatalog(worker.LifecycleLogs, worker.Request.RunId, worker.ProcessId,
            worker.WorkerRoot, WorkerJobKind.ProcessAssets,
            new(fault, "postgres-session", exit.HasValue ? MatrixRawExit.ExactManaged : MatrixRawExit.PresentPlatformRaw,
                exit, true, terminal is null ? MatrixTerminalAuthority.None : MatrixTerminalAuthority.AcceptedWorkerTerminal,
                terminal, classification, terminal is null ? "invalid-lifecycle" : null, events,
                DatabaseLock: MatrixProbe.Required));
    }

    private static void AssertCleanup(CrossProcessRunExclusionCase owned, params ParentWorker?[] workers)
    {
        Assert.IsTrue(owned.ResourcesReleased);
        Assert.IsFalse(owned.IsRegistered);
        Assert.IsFalse(Directory.Exists(owned.Root));
        foreach (var worker in workers)
        {
            Assert.IsNotNull(worker);
            Assert.IsTrue(worker.HasExited);
            Assert.IsTrue(worker.ResourcesReleased);
            Assert.AreEqual(1, worker.ProcessDisposeCalls);
        }
    }
}
