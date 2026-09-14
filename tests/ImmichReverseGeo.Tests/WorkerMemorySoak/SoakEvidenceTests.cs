using System.Runtime.InteropServices;
using System.Text.Json;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.ApplicationComposition;

namespace ImmichReverseGeo.Tests.WorkerMemorySoak;

[TestClass]
[TestCategory("Change68")]
[TestCategory("Performance")]
public sealed class SoakEvidenceTests
{
    internal static SoakFinality Clean => new(true, true, true, true, true, true,
        false, false, false, true, false, 0, false, true);

    [TestMethod]
    [DataRow(SoakViolation.MissingExit)]
    [DataRow(SoakViolation.MissingOutputDrain)]
    [DataRow(SoakViolation.MissingErrorDrain)]
    [DataRow(SoakViolation.MissingBridge)]
    [DataRow(SoakViolation.MissingDisposal)]
    [DataRow(SoakViolation.MissingOwnerRelease)]
    [DataRow(SoakViolation.ForcedCleanup)]
    public void MissingFinality_AlwaysRejectsNextLaunchEvenWithNoMemoryProfile(SoakViolation missing)
    {
        var finality = missing switch
        {
            SoakViolation.MissingExit => Clean with { Exit = false },
            SoakViolation.MissingOutputDrain => Clean with { OutputDrain = false },
            SoakViolation.MissingErrorDrain => Clean with { ErrorDrain = false },
            SoakViolation.MissingBridge => Clean with { Bridge = false },
            SoakViolation.MissingDisposal => Clean with { Disposed = false },
            SoakViolation.MissingOwnerRelease => Clean with { OwnerReleased = false },
            _ => Clean with { ForcedCleanup = true }
        };
        CollectionAssert.AreEqual(new[] { missing }, SoakEvidence.Validate(6801, new HashSet<int>(), finality,
            [], WorkerJobTerminalOutcome.Completed, SoakCycle.Success));
    }

    [TestMethod]
    public void ReusedOrStillLiveIdentity_IsRejectedIndependentlyOfTheOtherFinalityFields()
    {
        int pid = Environment.ProcessId;
        var alive = Clean with { ProcessAlive = WorkerMemorySoakRun.IsAlive(pid) };
        var result = SoakEvidence.Validate(pid, new HashSet<int> { pid }, alive,
            [], WorkerJobTerminalOutcome.Completed, SoakCycle.Success);
        CollectionAssert.AreEquivalent(new[] { SoakViolation.DuplicatePid, SoakViolation.SurvivingProcess }, result);
    }

    [TestMethod]
    [DataRow("candidate.tmp")]
    [DataRow("cache.download")]
    [DataRow("cache.db-wal")]
    [DataRow("cache.db-shm")]
    [DataRow("cache.db-journal")]
    [DataRow("staging.tmp.owner")]
    [DataRow("unknown.marker")]
    public void RealAttemptArtifact_IsRejectedAndItsPayloadDoesNotEnterRetainedFailureEvidence(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), "soak-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string canary = "credential-password=secret; 47.12345,8.12345; raw-stderr-frame /private/external/config";
        try
        {
            File.WriteAllText(Path.Combine(root, name), canary);
            var artifacts = WorkerMemorySoakRun.Snapshot(root, out bool handles);
            Assert.IsTrue(handles, "The artifact is closed but still must fail ownership checks.");
            var violations = SoakEvidence.Validate(6802, new HashSet<int>(), Clean, artifacts,
                WorkerJobTerminalOutcome.Completed, SoakCycle.Success);
            CollectionAssert.AreEqual(new[] { SoakViolation.ArtifactLeak }, violations);
            var evidence = new SoakEvidence(Path.Combine(WorkerMemorySoakOptions.Load(null).OutputRoot, "negative-harness"));
            evidence.Job(new(new(1, SoakPhase.Measured, WorkerJobKind.CacheMutation, SoakCycle.Success),
                Environment.ProcessId, 6802, 1, 2, WorkerJobTerminalOutcome.Completed, 0, Clean, artifacts, [], violations));
            evidence.Result(new(false, 1, violations));
            foreach (string file in Directory.GetFiles(evidence.Root, "*.json", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                Assert.IsFalse(text.Contains(canary, StringComparison.Ordinal));
                Assert.IsFalse(text.Contains(root, StringComparison.Ordinal));
                Assert.IsFalse(text.Contains(name, StringComparison.Ordinal));
                using var document = JsonDocument.Parse(text);
            }
            Assert.IsEmpty(Directory.GetFiles(evidence.Root, "*.writing", SearchOption.AllDirectories));
            Console.WriteLine("Retained negative soak evidence: " + evidence.Root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RealOpenFixtureHandle_CannotBeHiddenByAnExpectedFinalFilename()
    {
        string root = Path.Combine(Path.GetTempPath(), "soak-handle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var held = File.Open(Path.Combine(root, "assets-result.db"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            held.WriteByte(1);
            held.Flush();
            var artifacts = WorkerMemorySoakRun.Snapshot(root, out bool handles);
            Assert.IsFalse(handles);
            var failures = SoakEvidence.Validate(6803, new HashSet<int>(), Clean with { HandlesReleased = handles }, artifacts,
                WorkerJobTerminalOutcome.Completed, SoakCycle.Success);
            CollectionAssert.Contains(failures, SoakViolation.LeakedHandle);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ExistingWebSentinel_RecordsActivationAndCannotBeWaivedByAnIncompatibleProfile()
    {
        var sentinel = new BoundaryRuntimeSentinel(BoundaryRole.Standard);
        Assert.ThrowsExactly<AssertFailedException>(() => sentinel.Forbid<object>("soak", "native/DuckDB"));
        var profile = SoakThresholdProfileTests.Profile(SoakMemoryScope.LinuxCgroupV2MemoryCurrent) with { Platform = SoakPlatform.Linux };
        var decision = profile.Evaluate(new(SoakPlatform.MacOS, Architecture.Arm64, "10.0.0", false, false),
            new string('A', 64), []);
        Assert.AreEqual(SoakProfileState.IncompatiblePlatform, decision.State);
        CollectionAssert.Contains(SoakEvidence.Validate(6804, new HashSet<int>(), Clean with { WebActivations = sentinel.Events.Count },
            [], WorkerJobTerminalOutcome.Completed, SoakCycle.Success), SoakViolation.WebActivation);
    }

    [TestMethod]
    public void EmptyAttemptDirectory_CannotDisappearInNormalRootDisposalWithoutAFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "soak-empty-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "candidate-staging"));
        try
        {
            var artifacts = WorkerMemorySoakRun.Snapshot(root, out bool handles);
            Assert.IsTrue(handles);
            CollectionAssert.Contains(SoakEvidence.Validate(6806, new HashSet<int>(), Clean, artifacts,
                WorkerJobTerminalOutcome.Completed, SoakCycle.Success), SoakViolation.ArtifactLeak);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void NetworkSentinelObservation_RejectsTheRunEvenWhenTheWorkerReportedCompleted()
    {
        CollectionAssert.AreEqual(new[] { SoakViolation.NetworkActivation }, SoakEvidence.Validate(6805, new HashSet<int>(),
            Clean with { NetworkActivation = true }, [], WorkerJobTerminalOutcome.Completed, SoakCycle.Success));
    }
}
