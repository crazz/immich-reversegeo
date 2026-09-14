using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text.Json;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
[DoNotParallelize]
public sealed class ProcessFailureMatrixCacheTests
{
    [TestMethod]
    public async Task DelayedExactTaskContinuation_CannotRemoveReplacementAndOwnsNoFiles()
    {
        var map = new ConcurrentDictionary<string, Lazy<Task>>();
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new Lazy<Task>(() => releaseOld.Task);
        var replacement = new Lazy<Task>(() => Task.CompletedTask);
        map["CHE"] = old;
        bool removed = true;
        Func<Lazy<Task>, bool> remove = value =>
            ImmichReverseGeo.Overture.Services.OvertureDivisionCacheService.RemoveExact(map, "CHE", value);
        Task delayedCleanup = old.Value.ContinueWith(_ => removed = remove(old),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        map["CHE"] = replacement;
        releaseOld.TrySetResult();
        await MatrixWait.ForAsync(delayedCleanup, "cache-map/old-exact-task-continuation");
        Assert.IsFalse(removed);
        Assert.AreSame(replacement, map["CHE"]);
        Assert.IsFalse(replacement.IsValueCreated);
        await replacement.Value;
        Assert.IsTrue(remove(replacement));
        Assert.IsTrue(map.IsEmpty);
    }

    [TestMethod]
    [DataRow("preparation")]
    [DataRow("transfer")]
    [DataRow("export")]
    [DataRow("validation")]
    [DataRow("metadata")]
    [DataRow("readability")]
    [DataRow("handle-close")]
    [DataRow("pre-replace")]
    public async Task RealCacheFailure_PreservesOldFinalAndRepairedExplicitRetryOwnsNewAttempt(string stage)
    {
        var host = await CreateAsync();
        try
        {
            var seed = await RunAsync(host, "success");
            Assert.AreEqual(0, seed.Raw.ExitCode, seed.Raw.StandardErrorTail.Text);
            string final = Path.Combine(host.Root, "gadm-divisions", "CHE.db");
            SetVersion(final, "matrix-old-version");
            string oldHash = Hash(final);
            await AssertReadableAsync(host.Root);
            AssertCleanFiles(host.Root);

            var failed = await RunAsync(host, stage);
            Assert.AreEqual(4, failed.Raw.ExitCode, failed.Raw.StandardErrorTail.Text);
            Assert.IsNull(failed.Raw.FirstProtocolObservation);
            Assert.AreEqual(WorkerJobTerminalOutcome.Failed, ((WorkerJobTerminalPayload)failed.Raw.JobTerminal!.Payload).Outcome);
            Assert.AreEqual(CacheMutationPagePhase.Failed, host.Cache.State.Phase);
            Assert.AreEqual(oldHash, Hash(final), stage + ": old final must survive before publication");
            var failedEvents = ReadEvents(host.Root, failed.Lease);
            Assert.AreEqual(1, failedEvents.Count(e => e.Phase == stage), "Reach the selected fault, not an earlier failure.");
            Assert.IsFalse(failedEvents.Any(e => e.Phase == "published"));
            AssertCandidateFinality(host.Root, failedEvents, stage == "preparation" ? 0 : 2);
            await AssertReadableAsync(host.Root);
            AssertCleanFiles(host.Root);
            Assert.AreEqual(2, host.Launcher.Leases.Length, "No automatic retry after seed and failed request.");
            Assert.AreEqual(2, host.Releases);
            Assert.IsNull(host.Coordinator.ActiveOwner);
            Assert.IsTrue(failed.Lease.Session!.Settlement.IsCompletedSuccessfully);
            Assert.AreEqual(1, failed.Lease.ProcessDisposeCalls);

            // Select the repaired source only after the failed native process,
            // accepted bridge, streams, handles, temporary files and owner are final.
            var repaired = await RunAsync(host, "success");
            Assert.AreEqual(0, repaired.Raw.ExitCode, repaired.Raw.StandardErrorTail.Text);
            Assert.AreEqual(WorkerJobTerminalOutcome.Completed, ((WorkerJobTerminalPayload)repaired.Raw.JobTerminal!.Payload).Outcome);
            Assert.AreEqual(CacheMutationPagePhase.Completed, host.Cache.State.Phase);
            Assert.AreNotEqual(oldHash, Hash(final));
            Assert.AreNotEqual(failed.Lease.Request.RunId, repaired.Lease.Request.RunId);
            Assert.AreNotEqual(failed.Lease.ProcessId, repaired.Lease.ProcessId);
            var repairedEvents = ReadEvents(host.Root, repaired.Lease);
            Assert.AreEqual(1, repairedEvents.Count(e => e.Phase == "handles-closed"));
            Assert.AreEqual(1, repairedEvents.Count(e => e.Phase == "published"));
            AssertCandidateFinality(host.Root, repairedEvents, 2);
            Assert.IsFalse(failedEvents.Where(e => e.Phase == "candidate").Select(e => e.Path)
                .Intersect(repairedEvents.Where(e => e.Phase == "candidate").Select(e => e.Path)).Any());
            Assert.AreEqual(3, host.Releases);
            Assert.AreEqual(3, host.Launcher.Leases.Length);
            await AssertReadableAsync(host.Root);
            AssertCleanFiles(host.Root);
        }
        finally
        {
            await host.DisposeAsync();
        }
        AssertDisposed(host);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealPublicationCancellation_PreservesExactlyThePublishedGeneration(bool afterPublication)
    {
        var host = await CreateAsync();
        try
        {
            await RunAsync(host, "success");
            string final = Path.Combine(host.Root, "gadm-divisions", "CHE.db");
            SetVersion(final, "matrix-old-version");
            string oldHash = Hash(final);
            string stage = afterPublication ? "after-publication-cancel" : "before-publication-cancel";
            host.Launcher.CacheStage = stage;
            Task run = host.RunAsync(WorkerJobKind.CacheMutation);
            var lease = await host.Launcher.NextLaunchAsync();
            await MatrixFileSignal.WaitAsync(host.Root, $"matrix-cache-{lease.ProcessId}-cancel.marker", "cache/selected-publication-boundary");
            string boundaryHash = Hash(final);
            Assert.AreEqual(afterPublication, oldHash != boundaryHash);
            Assert.IsNotNull(host.Coordinator.ActiveOwner);
            Assert.IsFalse(run.IsCompleted);
            await MatrixWait.ForAsync(host.Cache.CancelAsync(), "cache/cooperative-cancel-finality");
            await MatrixWait.ForAsync(run, "cache/cancelled-controller-finality");
            var raw = await MatrixWait.ForAsync(lease.CompleteAsync(), "cache/cancelled-raw-exit-and-drains");
            Assert.AreEqual(130, raw.ExitCode, raw.StandardErrorTail.Text);
            Assert.AreEqual(WorkerJobTerminalOutcome.Cancelled, ((WorkerJobTerminalPayload)raw.JobTerminal!.Payload).Outcome);
            Assert.AreEqual(CacheMutationPagePhase.Cancelled, host.Cache.State.Phase);
            Assert.AreEqual(boundaryHash, Hash(final), "Cancellation cannot undo a published generation or invent publication.");
            Assert.AreEqual(0, lease.TreeKillCalls);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.AreEqual(2, host.Releases);
            Assert.IsNull(host.Coordinator.ActiveOwner);
            var events = ReadEvents(host.Root, lease);
            Assert.AreEqual(1, events.Count(e => e.Phase == stage));
            Assert.AreEqual(afterPublication ? 1 : 0, events.Count(e => e.Phase == "published"));
            AssertCandidateFinality(host.Root, events, 2);
            AssertCleanFiles(host.Root);
            await AssertReadableAsync(host.Root);
            await host.JoinTelemetryAsync(lease);
            ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, WorkerJobKind.CacheMutation,
                new(stage, "publication", MatrixRawExit.ExactManaged, 130, true, MatrixTerminalAuthority.AcceptedWorkerTerminal,
                    "cancelled", "cancelled", null, [6610, 6611, 6612, 6620, 6621, 6640, 6641]));
            Assert.AreEqual(0, host.ForbiddenHeavyResolutions);
        }
        finally
        {
            await host.DisposeAsync();
        }
        AssertDisposed(host);
    }

    private static async Task<ProcessFailureMatrixHost> CreateAsync()
    {
        var host = await ProcessFailureMatrixHost.CreateAsync("cache-matrix");
        try
        {
            Directory.CreateDirectory(Path.Combine(host.Root, "bundled-data"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "data", "iso3166.json"),
                Path.Combine(host.Root, "bundled-data", "iso3166.json"));
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private static async Task<(WorkerProcessFixtureLease Lease, ChildWorkerCompletionObservation Raw)> RunAsync(
        ProcessFailureMatrixHost host, string stage)
    {
        Assert.IsNull(host.Coordinator.ActiveOwner);
        host.Launcher.CacheStage = stage;
        Task run = host.RunAsync(WorkerJobKind.CacheMutation);
        var lease = await host.Launcher.NextLaunchAsync();
        await MatrixWait.ForAsync(run, "cache/explicit-" + stage + "-attempt-finality");
        var raw = await MatrixWait.ForAsync(lease.CompleteAsync(), "cache/" + stage + "-native-drains");
        Assert.IsTrue(lease.Session!.Settlement.IsCompletedSuccessfully);
        Assert.AreEqual(1, lease.ProcessDisposeCalls);
        Assert.AreEqual(0, lease.TreeKillCalls);
        Assert.IsNull(host.Coordinator.ActiveOwner);
        Assert.AreEqual(0, host.ForbiddenHeavyResolutions);
        await host.JoinTelemetryAsync(lease);
        bool completed = stage == "success";
        ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, WorkerJobKind.CacheMutation,
            new(stage, "cache-work", MatrixRawExit.ExactManaged, completed ? 0 : 4, true,
                MatrixTerminalAuthority.AcceptedWorkerTerminal, completed ? "completed" : "failed",
                completed ? "completed" : "worker-failed", null, [6610, 6611, 6612, 6640, 6641]));
        foreach (var entry in host.Logs.Entries)
        {
            Assert.IsFalse(entry.Rendered.Contains(host.Root, StringComparison.Ordinal));
            Assert.IsFalse(entry.Rendered.Contains("matrix-cache-secret", StringComparison.Ordinal));
        }
        return (lease, raw);
    }

    private sealed record CacheEvent(string Phase, string? Path, int ProcessId);

    private static CacheEvent[] ReadEvents(string root, WorkerProcessFixtureLease lease) =>
        File.ReadAllLines(Path.Combine(root, $"matrix-cache-{lease.ProcessId}.ndjson")).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            var value = json.RootElement;
            Assert.AreEqual(lease.ProcessId, value.GetProperty("processId").GetInt32());
            return new CacheEvent(value.GetProperty("phase").GetString()!, value.GetProperty("path").GetString(),
                value.GetProperty("processId").GetInt32());
        }).ToArray();

    private static void AssertCandidateFinality(string root, CacheEvent[] events, int releases)
    {
        string[] candidates = events.Where(e => e.Phase == "candidate").Select(e => e.Path!).ToArray();
        Assert.IsTrue(candidates.Length > 0);
        Assert.AreEqual(releases, events.Count(e => e.Phase == "released"));
        foreach (string candidate in candidates)
        {
            Assert.IsTrue(candidate.StartsWith(Path.Combine(root, "gadm-divisions") + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Assert.IsFalse(File.Exists(candidate), "Attempt candidate must be removed before retry.");
            Assert.IsFalse(File.Exists(candidate + ".owner"));
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void SetVersion(string path, string version)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE _meta SET value = $version WHERE key = 'version'";
        command.Parameters.AddWithValue("$version", version);
        Assert.AreEqual(1, command.ExecuteNonQuery());
    }

    private static async Task AssertReadableAsync(string root)
    {
        var reader = new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, root);
        var found = await MatrixWait.ForAsync(reader.FindContainingDivisionAreasAsync(47, 8, "CHE"), "cache/real-spatial-read");
        Assert.IsNull(found.Error);
        Assert.AreEqual(2, found.Candidates.Count);
        Assert.AreEqual("CHE.1_1", found.BestMatch!.Id);
    }

    private static void AssertCleanFiles(string root)
    {
        Assert.IsTrue(File.Exists(Path.Combine(root, "advisory-lock-probe-installed.marker")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "advisory-lock-accessed.marker")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "persistence-accessed.marker")));
        CollectionAssert.AreEqual(new[] { "CHE.db" }, Directory.GetFiles(Path.Combine(root, "gadm-divisions"))
            .Select(Path.GetFileName).ToArray());
        foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            using var probe = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    private static void AssertDisposed(ProcessFailureMatrixHost host)
    {
        Assert.IsFalse(Directory.Exists(host.Root));
        foreach (var lease in host.Launcher.Leases)
        {
            Assert.IsTrue(lease.HasExited);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.IsFalse(lease.ForcedCleanup);
            Assert.IsFalse(lease.IsRegistered);
            Assert.IsFalse(Directory.Exists(lease.Root));
        }
    }
}
