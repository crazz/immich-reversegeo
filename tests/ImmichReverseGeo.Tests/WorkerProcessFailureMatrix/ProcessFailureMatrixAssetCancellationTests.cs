using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
[DoNotParallelize]
public sealed class ProcessFailureMatrixAssetCancellationTests
{
    [TestMethod]
    [DataRow(InternalWorkerProtocolVersion.V1)]
    [DataRow(InternalWorkerProtocolVersion.V2)]
    public async Task RealAssetLoop_CancelsOwnedResolverBeforeDisposalAndSingleFinality(InternalWorkerProtocolVersion version)
    {
        var host = await ProcessFailureMatrixHost.CreateAsync("real-processing-cancellation", protocolVersion: version);
        try
        {
            var run = host.RunAsync(WorkerJobKind.ProcessAssets);
            var lease = await host.Launcher.NextLaunchAsync();
            await MatrixFileSignal.WaitAsync(lease.Root, "asset-work-entered.marker", "asset/real-executor-resolver-entered");
            Assert.IsFalse(run.IsCompleted);
            Assert.AreEqual(0, host.Releases);
            await MatrixWait.ForAsync(host.Processing.StopActiveRun()!, "asset/actual-controller-stop");
            await MatrixWait.ForAsync(run, "asset/controller-finality");
            var raw = await MatrixWait.ForAsync(lease.CompleteAsync(), "asset/process-and-stream-finality");
            Assert.AreEqual(130, raw.ExitCode, raw.StandardErrorTail.Text);
            Assert.IsNull(raw.FirstProtocolObservation);
            Assert.AreEqual(WorkerJobTerminalOutcome.Cancelled, ((WorkerJobTerminalPayload)raw.JobTerminal!.Payload).Outcome);
            Assert.AreEqual(ProcessingRunOutcome.Cancelled, host.Reporter.GetFinalizationReceipt(lease.Request)!.Result.Outcome);
            Assert.IsTrue(File.Exists(Path.Combine(lease.Root, "asset-work-cancelled.marker")));
            Assert.IsTrue(File.Exists(Path.Combine(lease.Root, "hermetic-lease-released.marker")));
            Assert.IsFalse(File.Exists(Path.Combine(lease.Root, "unexpected-write.marker")));
            await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(lease.Root, "skipped.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString()))
            {
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM skipped_assets";
                Assert.AreEqual(0L, await command.ExecuteScalarAsync());
            }
            Assert.AreEqual(0, lease.TreeKillCalls);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.AreEqual(1, host.Releases);
            Assert.IsNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(0, host.ForbiddenHeavyResolutions);
            await host.JoinTelemetryAsync(lease);
            ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, WorkerJobKind.ProcessAssets,
                new("real-processing-cancellation", "asset-resolver-work", MatrixRawExit.ExactManaged, 130, true,
                    MatrixTerminalAuthority.AcceptedWorkerTerminal, "cancelled", "cancelled", null,
                    [6610, 6611, 6612, 6620, 6621, 6640, 6641]));
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
