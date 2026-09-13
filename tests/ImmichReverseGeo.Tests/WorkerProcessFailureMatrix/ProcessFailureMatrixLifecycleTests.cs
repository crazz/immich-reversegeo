using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
[DoNotParallelize]
public sealed class ProcessFailureMatrixLifecycleTests
{
    public static IEnumerable<object[]> Rows()
    {
        foreach (var @case in ProcessFailureMatrixCase.LifecycleCases())
        {
            yield return [@case.Id];
        }
    }

    [TestMethod]
    [DynamicData(nameof(Rows))]
    public async Task RealControllers_RetainRawEvidenceAndReleaseOnlyAfterOwnedFinality(string caseId)
    {
        var @case = ProcessFailureMatrixCase.LifecycleCases().Single(candidate => candidate.Id == caseId);
        var (version, kind, row) = @case;
        string fault = row.Fault;
        var host = await ProcessFailureMatrixHost.CreateAsync(fault, protocolVersion: version);
        try
        {
            await host.RunAsync(kind);
            var lease = host.Launcher.Leases.Single();
            var raw = await MatrixWait.ForAsync(lease.CompleteAsync(), fault + "/raw-exit-and-drains");
            await MatrixWait.ForAsync(lease.Session!.Settlement, fault + "/session-settlement");
            await host.JoinTelemetryAsync(lease);

            Assert.AreEqual(version, raw.ProtocolVersion, fault);
            Assert.AreEqual(kind, raw.JobKind, fault);
            Assert.AreEqual(lease.Request.RunId, raw.JobId, fault);
            Assert.AreEqual(row.ExitCode, raw.ExitCode, fault + ": raw OS exit");
            Assert.IsTrue(raw.ExitObserved, fault);
            Assert.AreEqual(row.Ready, raw.Startup is ChildWorkerStartupObservation.ReadyAccepted, fault);
            var accepted = host.Launcher.Tap(lease).Events;
            if (row.Phase is "before-started" or "before-ready")
            {
                Assert.IsFalse(raw.AcceptedRunStarted, "The invalid candidate must not become an accepted job-started event.");
                Assert.IsFalse(accepted.Any(e => e.Payload is not WorkerJobReadyPayload),
                    "No invalid payload, correlation, sequence or lifecycle event reaches the real read model.");
            }
            else
            {
                Assert.IsTrue(raw.AcceptedRunStarted);
            }

            if (row.Fault is "sequence-gap" or "sequence-replay" or "post-terminal")
            {
                Assert.IsFalse(accepted.Any(e => e.Payload is WorkerJobLogPayload), "The rejected log frame is never projected.");
            }
            Assert.AreEqual(row.Authority == MatrixTerminalAuthority.AcceptedWorkerTerminal,
                raw.JobTerminal is not null, fault + ": terminal authority");
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(raw.StandardOutputFinality, fault);
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(raw.StandardErrorFinality, fault);
            Assert.IsTrue(lease.HasExited, fault);
            Assert.AreEqual(1, lease.ProcessDisposeCalls, fault);
            Assert.AreEqual(0, lease.TreeKillCalls, fault);
            Assert.IsNotNull(raw.EventDelivery, fault + ": retain the real accepted-delivery bridge");
            Assert.AreEqual(0L, raw.EventDelivery.EnqueueWaits, fault + ": small unsaturated FIFO");
            Assert.AreEqual(0L, raw.EventDelivery.ReplacedSnapshots, fault + ": no replaceable pressure");
            await ProcessFailureMatrixTelemetry.AssertCoalescingAsync(host.Logs, lease, raw.EventDelivery,
                host.Launcher.Tap(lease).NotificationOwnerObservation!);
            Assert.IsNull(host.Coordinator.ActiveOwner, fault);
            Assert.AreEqual(1, host.Releases, fault + ": one admitted owner release");
            Assert.AreEqual(0, host.ForbiddenHeavyResolutions, fault);
            Assert.IsFalse(Directory.EnumerateFiles(host.Root, "*", SearchOption.AllDirectories).Any(),
                fault + ": Web must not mutate cache/config files");
            AssertFinalUi(host, kind, lease.Request);
            ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, kind, row);
        }
        finally
        {
            await host.DisposeAsync();
        }

        foreach (var lease in host.Launcher.Leases)
        {
            Assert.IsFalse(lease.ForcedCleanup, fault + ": emergency cleanup must not be the success path");
            Assert.IsFalse(lease.IsRegistered, fault);
            Assert.IsFalse(Directory.Exists(lease.Root), fault);
        }

        Assert.IsFalse(Directory.Exists(host.Root), fault);
    }

    private static void AssertFinalUi(ProcessFailureMatrixHost host, WorkerJobKind kind, ProcessingRunRequest request)
    {
        switch (kind)
        {
            case WorkerJobKind.ProcessAssets:
                var receipt = host.Reporter.GetFinalizationReceipt(request);
                Assert.IsNotNull(receipt);
                Assert.AreEqual(ProcessingRunOutcome.Failed, receipt.Result.Outcome);
                Assert.IsFalse(host.State.IsRunning);
                break;
            case WorkerJobKind.CoordinateLookup:
                Assert.AreEqual(CoordinateLookupPagePhase.Failed, host.Lookup.State.Phase);
                Assert.IsFalse(host.Lookup.State.HasAdmittedOperation);
                Assert.IsNull(host.Lookup.State.Result);
                break;
            case WorkerJobKind.CacheMutation:
                Assert.AreEqual(CacheMutationPagePhase.Failed, host.Cache.State.Phase);
                Assert.IsFalse(host.Cache.State.HasAdmittedOperation);
                Assert.IsNull(host.Cache.State.Result);
                break;
        }
    }
}
