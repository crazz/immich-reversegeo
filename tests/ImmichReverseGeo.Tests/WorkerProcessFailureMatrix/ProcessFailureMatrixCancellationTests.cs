using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
[DoNotParallelize]
public sealed class ProcessFailureMatrixCancellationTests
{
    [TestMethod]
    [DataRow(WorkerJobKind.ProcessAssets, false)]
    [DataRow(WorkerJobKind.ProcessAssets, true)]
    [DataRow(WorkerJobKind.CoordinateLookup, false)]
    [DataRow(WorkerJobKind.CoordinateLookup, true)]
    [DataRow(WorkerJobKind.CacheMutation, false)]
    [DataRow(WorkerJobKind.CacheMutation, true)]
    public async Task ActualControllerStop_UsesOneTenSecondDeadlineAndJoinsFinality(WorkerJobKind kind, bool forced)
    {
        string fault = forced ? "unresponsive" : "cooperative-cancel";
        var clock = new CancellationTestClock();
        var host = await ProcessFailureMatrixHost.CreateAsync(fault, clock);
        try
        {
            Task run = host.RunAsync(kind);
            var lease = await MatrixWait.ForAsync(host.Launcher.Started, "cancel/native-launch");
            var tap = host.Launcher.Tap(lease);
            await tap.WaitForLogAsync("matrix:armed");
            Assert.IsNotNull(host.Coordinator.ActiveOwner);
            Assert.IsFalse(run.IsCompleted);
            int oneShotTimers = clock.OneShotTimerCount;
            Task stop = Stop(host, kind);
            await MatrixWait.ForAsync(clock.WaitForOneShotTimerCreatedAsync(oneShotTimers), "cancel/exact-grace-timer");
            Assert.AreEqual(oneShotTimers + 1, clock.OneShotTimerCount);

            if (forced)
            {
                await tap.WaitForLogAsync("matrix:cancel-observed");
                Task repeatedUiStop = Stop(host, kind);
                // Lookup/cache disable repeated UI cancel immediately. The underlying
                // session still joins the same cancellation and finality operation.
                if (kind != WorkerJobKind.ProcessAssets)
                {
                    Assert.IsTrue(repeatedUiStop.IsCompletedSuccessfully);
                }

                Task joinedStop = lease.Session!.RequestStop();
                Assert.AreSame(joinedStop, lease.Session.RequestStop());
                Assert.IsFalse(joinedStop.IsCompleted);
                clock.Advance(TimeSpan.FromMilliseconds(9_999));
                Assert.AreEqual(0, lease.TreeKillCalls, "No early escalation at 9,999ms.");
                Assert.IsFalse(lease.HasExited);
                Assert.IsFalse(stop.IsCompleted);
                Assert.IsFalse(run.IsCompleted);
                Assert.AreEqual(0, host.Releases);
                Assert.IsFalse(host.Logs.Entries.Any(e => e.Event.Id is 6641 or 6622 or 6623));
                clock.Advance(TimeSpan.FromMilliseconds(1));
                Assert.AreEqual(ChildProcessKillOutcome.Requested,
                    await MatrixWait.ForAsync(lease.TreeKillObserved, "cancel/10000ms-tree-kill"));
                await MatrixWait.ForAsync(joinedStop, "cancel/repeated-stop-finality");
            }

            await MatrixWait.ForAsync(stop, "cancel/controller-stop-finality");
            await MatrixWait.ForAsync(run, "cancel/controller-result-finality");
            var raw = await MatrixWait.ForAsync(lease.CompleteAsync(), "cancel/raw-exit-and-drains");
            await host.JoinTelemetryAsync(lease);
            var facts = lease.Session!.CancellationFacts!;
            Assert.AreEqual(ChildWorkerTerminationIntent.Stop, facts.FirstIntent);
            Assert.IsTrue(facts.RequestAccepted);
            Assert.AreEqual(TimeSpan.FromSeconds(10), facts.DeadlineUtc - facts.FirstStopAtUtc);
            Assert.AreEqual(forced, facts.GraceExpired);
            Assert.AreEqual(forced, facts.KillAttempted);
            Assert.AreEqual(forced ? 1 : 0, lease.TreeKillCalls);
            Assert.AreEqual(!forced, raw.JobTerminal is not null);
            Assert.IsNotNull(raw.ExitCode);
            if (!forced)
            {
                Assert.AreEqual(130, raw.ExitCode);
                Assert.AreEqual(WorkerJobTerminalOutcome.Cancelled,
                    ((WorkerJobTerminalPayload)raw.JobTerminal!.Payload).Outcome);
            }

            AssertCanonicalCancel(lease.WrittenInput.ToArray(), raw.JobId);
            Assert.IsTrue(lease.HasExited);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.IsNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(1, host.Releases);
            Assert.AreEqual(0, host.ForbiddenHeavyResolutions);
            switch (kind)
            {
                case WorkerJobKind.ProcessAssets:
                    Assert.AreEqual(ProcessingRunOutcome.Cancelled, host.Reporter.GetFinalizationReceipt(lease.Request)!.Result.Outcome);
                    break;
                case WorkerJobKind.CoordinateLookup:
                    Assert.AreEqual(CoordinateLookupPagePhase.Cancelled, host.Lookup.State.Phase);
                    break;
                case WorkerJobKind.CacheMutation:
                    Assert.AreEqual(CacheMutationPagePhase.Cancelled, host.Cache.State.Phase);
                    break;
            }

            var row = forced
                ? new ProcessFailureMatrixRow(fault, "stop-deadline", MatrixRawExit.PresentPlatformRaw, null, true,
                    MatrixTerminalAuthority.None, null, "forced-stop", "invalid-lifecycle",
                    [6610, 6611, 6612, 6620, 6622, 6623, 6630, 6641], ForcedStop: true)
                : new ProcessFailureMatrixRow(fault, "cooperative-stop", MatrixRawExit.ExactManaged, 130, true,
                    MatrixTerminalAuthority.AcceptedWorkerTerminal, "cancelled", "cancelled", null,
                    [6610, 6611, 6612, 6620, 6621, 6640, 6641]);
            ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, kind, row);
            Assert.AreEqual(raw.ExitCode, host.Logs.Entries.Single(e => e.Event.Id == 6641)["exit_code"]);
            if (forced)
            {
                var entries = host.Logs.Entries;
                Assert.IsTrue(Array.FindIndex(entries, e => e.Event.Id == 6622) < Array.FindIndex(entries, e => e.Event.Id == 6623));
                Assert.AreEqual(10_000L, entries.Single(e => e.Event.Id == 6622)["grace_elapsed_ms"]);
            }
        }
        finally
        {
            await host.DisposeAsync();
        }

        foreach (var lease in host.Launcher.Leases)
        {
            Assert.IsFalse(lease.ForcedCleanup);
            Assert.IsFalse(lease.IsRegistered);
            Assert.IsFalse(Directory.Exists(lease.Root));
        }

        Assert.IsFalse(Directory.Exists(host.Root));
        Assert.AreEqual(0, clock.ActiveTimerCount, "Page cadence timers are disposed with their page owners.");
    }

    private static Task Stop(ProcessFailureMatrixHost host, WorkerJobKind kind) => kind switch
    {
        WorkerJobKind.ProcessAssets => host.Processing.StopActiveRun()
            ?? throw new AssertFailedException("Processing must retain its active run until finality."),
        WorkerJobKind.CoordinateLookup => host.Lookup.CancelAsync(),
        WorkerJobKind.CacheMutation => host.Cache.CancelAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void AssertCanonicalCancel(byte[] input, Guid jobId)
    {
        var lines = Encoding.UTF8.GetString(input).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(2, lines.Length, "Exactly one execute and one correlated cancel.");
        for (int index = 0; index < lines.Length; index++)
        {
            var parsed = WorkerJobProtocolCodec.ParseControllerInput(Encoding.UTF8.GetBytes(lines[index]));
            Assert.IsTrue(parsed.IsSuccess);
            Assert.AreEqual(jobId, parsed.Message!.JobId);
            Assert.AreEqual(index + 1L, parsed.Message.Sequence);
            Assert.AreEqual(index == 0 ? "execute" : "cancel", parsed.Message.Type);
        }
    }
}
