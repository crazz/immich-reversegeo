using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using ImmichReverseGeo.Tests.ApplicationComposition;
using System.Text.Json;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.WorkerMemorySoak;

[TestClass]
[TestCategory("Change68")]
[TestCategory("Performance")]
[DoNotParallelize]
public sealed class WorkerMemorySoakTests
{
    [TestMethod]
    public async Task ConfiguredMixedSoak_RequiresRealWorkerFinalityBeforeEveryNextLaunch()
    {
        var options = WorkerMemorySoakOptions.Load(Environment.GetEnvironmentVariable("IMMICH_REVERSEGEO_SOAK_CONFIG"));
        var result = await WorkerMemorySoakRun.RunAsync(options);
        Console.WriteLine("Soak evidence: " + result.EvidenceDirectory);
        Assert.IsTrue(result.Result.Passed, "soak/failed: " + string.Join(",", result.Result.Violations));
    }

    [TestMethod]
    public async Task CancellationAndPrePublicationFailure_PreserveTheSameFinalityBarrier()
    {
        var defaults = WorkerMemorySoakOptions.Load(null);
        var result = await WorkerMemorySoakRun.RunAsync(defaults with
        {
            Configuration = WorkerMemorySoakConfiguration.Create(cancellation: true, failure: true)
        });
        Console.WriteLine("Soak evidence: " + result.EvidenceDirectory);
        Assert.IsTrue(result.Result.Passed, "soak/failed: " + string.Join(",", result.Result.Violations));
    }

    [TestMethod]
    public async Task ExplicitLocalInputProfile_IsCopiedAndRemainsImmutableAcrossMixedWorkers()
    {
        string input = Path.Combine(Path.GetTempPath(), "soak-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(input);
        var preparation = new WorkerProcessFixtureLease();
        try
        {
            await preparation.LaunchAsync("real-memory-soak", InternalWorkerProtocolVersion.V2, capture: false);
            await preparation.CompleteAsync();
            foreach (string name in new[] { "assets.db", "gadm.db", "source.gpkg" })
            {
                File.Copy(Path.Combine(preparation.Root, "soak-input", name), Path.Combine(input, name));
            }
            await preparation.DisposeAsync();
            Assert.IsFalse(preparation.ForcedCleanup);
            var before = Directory.GetFiles(input).ToDictionary(path => path, WorkerMemorySoakOptions.HashFile);
            var result = await WorkerMemorySoakRun.RunAsync(WorkerMemorySoakOptions.Load(null) with { InputDirectory = input });
            Console.WriteLine("Soak evidence: " + result.EvidenceDirectory);
            Assert.IsTrue(result.Result.Passed, "soak/failed: " + string.Join(",", result.Result.Violations));
            foreach (var file in before)
            {
                Assert.AreEqual(file.Value, WorkerMemorySoakOptions.HashFile(file.Key), "Caller-owned local input was modified.");
            }
        }
        finally
        {
            await preparation.DisposeAsync();
            Directory.Delete(input, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExistingRegisteredDescendantAndCandidateLease_AreRejectedBeforeSafetyCleanup()
    {
        var clock = new MatrixClock();
        var host = await ProcessFailureMatrixHost.CreateAsync("unresponsive-tree", clock);
        try
        {
            Task run = host.RunAsync(WorkerJobKind.ProcessAssets);
            var lease = await host.Launcher.NextLaunchAsync();
            await MatrixFileSignal.WaitAsync(lease.Root, "descendant-pid.marker", "soak/registered-descendant");
            await host.Launcher.Tap(lease).WaitForLogAsync("matrix:armed");
            Assert.IsTrue(WorkerMemorySoakRun.RegisteredDescendantAlive(lease.Root));
            var artifacts = WorkerMemorySoakRun.Snapshot(lease.Root, out _);
            var failures = SoakEvidence.Validate(lease.ProcessId!.Value, new HashSet<int>(),
                SoakEvidenceTests.Clean with { DescendantAlive = WorkerMemorySoakRun.RegisteredDescendantAlive(lease.Root) },
                artifacts, WorkerJobTerminalOutcome.Completed, SoakCycle.Success);
            CollectionAssert.Contains(failures, SoakViolation.SurvivingDescendant);
            CollectionAssert.Contains(failures, SoakViolation.ArtifactLeak);
            Task stop = host.Processing.StopActiveRun()!;
            await clock.WaitForOneShotAsync(TimeSpan.FromSeconds(10), "soak/owned-production-stop-grace");
            await MatrixFileSignal.WaitAsync(lease.Root, "matrix-cancel-observed.marker", "soak/child-observed-stop");
            clock.Advance(TimeSpan.FromSeconds(10));
            await MatrixWait.ForAsync(stop, "soak/owned-stop-finality");
            await MatrixWait.ForAsync(run, "soak/owned-controller-finality");
            await lease.CompleteAsync();
            await host.JoinTelemetryAsync(lease);
            Assert.IsFalse(WorkerMemorySoakRun.RegisteredDescendantAlive(lease.Root));
            CollectionAssert.Contains(SoakEvidence.Validate(lease.ProcessId.Value, new HashSet<int>(),
                SoakEvidenceTests.Clean with { ForcedCleanup = lease.TreeKillCalls != 0 }, [],
                WorkerJobTerminalOutcome.Completed, SoakCycle.Success), SoakViolation.ForcedCleanup);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ActualWorkerNoNetworkBoundary_RecordsAndRejectsAnAttemptBeforeExternalWork()
    {
        await using var lease = new WorkerProcessFixtureLease();
        var dispatch = new CoordinateLookupWorkerJobDispatch(lease.Request.RunId,
            new CoordinateLookupRequest(47, 8, false, false, true, new(null, [])));
        var sink = new SoakSink();
        await lease.LaunchAsync("real-memory-soak-network-probe", dispatch, sink, InternalWorkerProtocolVersion.V2, false);
        var raw = await lease.CompleteAsync();
        Assert.AreEqual(WorkerJobTerminalOutcome.Failed, ((WorkerJobTerminalPayload)raw.JobTerminal!.Payload).Outcome);
        Assert.IsTrue(File.Exists(Path.Combine(lease.Root, "soak-network-forbidden.marker")));
        Assert.IsFalse(File.Exists(Path.Combine(lease.Root, "soak-geodata-queried.marker")));
        CollectionAssert.Contains(SoakEvidence.Validate(lease.ProcessId!.Value, new HashSet<int>(),
            SoakEvidenceTests.Clean with { NetworkActivation = true }, [], WorkerJobTerminalOutcome.Failed,
            SoakCycle.PrePublicationFailure), SoakViolation.NetworkActivation);
    }

    [TestMethod]
    public async Task ProductionWebRegistration_UsesTheExistingFailFastSentinel()
    {
        await using var host = await ProcessFailureMatrixHost.CreateAsync("memory-soak");
        var entry = ControlPlaneDependencyPolicy.HeavyTypes.First(item => item.Value == "country index");
        Assert.ThrowsExactly<AssertFailedException>(() => host.ProbeSoakBoundary(entry.Key));
        Assert.AreEqual(1, host.SoakSentinel.Events.Count);
        Assert.AreEqual("country index", host.SoakSentinel.Events.Single().Category);
        Assert.IsEmpty(host.Launcher.Leases);
    }

    [TestMethod]
    public async Task CompatibleZeroCeiling_FailsNumericallyAfterCleanRealWorkerFinalityAndRetainsCompleteEvidence()
    {
        var options = WorkerMemorySoakOptions.Load(null) with { Configuration = WorkerMemorySoakConfiguration.Create(measuredIterations: 5) };
        var capabilities = SoakCapabilities.Capture(options);
        var profile = new SoakThresholdProfile(capabilities.Platform, capabilities.Architecture, capabilities.Runtime,
            capabilities.Container, SoakMemoryScope.TestHostWorkingSet, SoakAggregation.Maximum, new string('B', 64),
            WorkerMemorySoakOptions.HashFile(WorkerProcessFixtureLease.FixtureExecutable), 2, true, 0)
        {
            WorkerRuntimeSha256 = capabilities.WorkerRuntimeSha256,
            InputProfileSha256 = capabilities.InputProfileSha256
        };
        string path = Path.Combine(Path.GetTempPath(), "soak-profile-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(profile, WorkerMemorySoakOptions.Json));
            var result = await WorkerMemorySoakRun.RunAsync(options with { ThresholdProfilePath = path });
            Assert.IsFalse(result.Result.Passed);
            Assert.AreEqual(10, result.Result.CompletedIterations);
            CollectionAssert.AreEqual(new[] { SoakViolation.ProfileThreshold }, result.Result.Violations);
            Assert.AreEqual(SoakStage.ProfileEvaluation, result.Result.Stage);
            using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.EvidenceDirectory, "summary.json")));
            Assert.AreEqual("Applied", summary.RootElement.GetProperty("profile").GetProperty("state").GetString());
            Assert.AreEqual(10, Directory.GetFiles(Path.Combine(result.EvidenceDirectory, "starts")).Length);
            Assert.IsTrue(File.Exists(Path.Combine(result.EvidenceDirectory, "manifest.json")));
            Console.WriteLine("Retained profile failure evidence: " + result.EvidenceDirectory);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task MissingSelectedProfile_RetainsBoundedFailureBeforeAnyWorkerLaunch()
    {
        var result = await WorkerMemorySoakRun.RunAsync(WorkerMemorySoakOptions.Load(null) with
        {
            ThresholdProfilePath = Path.Combine(Path.GetTempPath(), "secret-" + Guid.NewGuid().ToString("N") + ".json")
        });
        Assert.IsFalse(result.Result.Passed);
        Assert.AreEqual(0, result.Result.CompletedIterations);
        Assert.AreEqual(SoakStage.Configuration, result.Result.Stage);
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(result.EvidenceDirectory, "starts")));
        Assert.IsTrue(File.Exists(Path.Combine(result.EvidenceDirectory, "manifest.json")));
        string summary = File.ReadAllText(Path.Combine(result.EvidenceDirectory, "summary.json"));
        StringAssert.Contains(summary, "InvalidProfile");
        Assert.IsFalse(summary.Contains("secret-", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MissingLocalInputs_RetainsIncompleteIdentityAndFailureBeforeAnyWorkerLaunch()
    {
        var result = await WorkerMemorySoakRun.RunAsync(WorkerMemorySoakOptions.Load(null) with
        {
            InputDirectory = Path.Combine(Path.GetTempPath(), "private-input-" + Guid.NewGuid().ToString("N"))
        });
        Assert.IsFalse(result.Result.Passed);
        Assert.AreEqual(0, result.Result.CompletedIterations);
        Assert.AreEqual(SoakStage.Configuration, result.Result.Stage);
        CollectionAssert.AreEqual(new[] { SoakViolation.HarnessFailure }, result.Result.Violations);
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(result.EvidenceDirectory, "starts")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.EvidenceDirectory, "manifest.json")));
        Assert.IsFalse(manifest.RootElement.GetProperty("identityComplete").GetBoolean());
        Assert.IsTrue(File.Exists(Path.Combine(result.EvidenceDirectory, "summary.json")));
        Assert.IsTrue(File.Exists(Path.Combine(result.EvidenceDirectory, "test-result.json")));
        foreach (string file in Directory.GetFiles(result.EvidenceDirectory, "*.json"))
        {
            Assert.IsFalse(File.ReadAllText(file).Contains("private-input-", StringComparison.Ordinal));
        }
    }

    private sealed class SoakSink : IWorkerJobEventSink
    {
        ValueTask IWorkerJobEventSink.AcceptAsync(WorkerJobOutputMessage message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
