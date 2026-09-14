using System.Runtime.InteropServices;
using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Tests.WorkerMemorySoak;

[TestClass]
[TestCategory("Change68")]
[TestCategory("Performance")]
public sealed class SoakThresholdProfileTests
{
    internal static SoakThresholdProfile Profile(SoakMemoryScope scope) => new(SoakPlatform.Linux, Architecture.X64,
        "10.0.0", false, scope, SoakAggregation.Maximum, new string('B', 64), new string('A', 64), 2, true, 100)
        { WorkerRuntimeSha256 = new string('C', 64), InputProfileSha256 = new string('D', 64) };
    private static SoakCapabilities Capabilities => new(SoakPlatform.Linux, Architecture.X64, "10.0.0", false, true)
        { WorkerRuntimeSha256 = new string('C', 64), InputProfileSha256 = new string('D', 64) };
    private static SoakMemory Sample(long bytes, long timestamp, SoakMemoryScope scope, SoakPhase phase = SoakPhase.Measured) =>
        new(phase, 1, scope, timestamp, bytes, 1, SoakMissingMemory.None);

    [TestMethod]
    [DataRow(SoakMemoryScope.TestHostWorkingSet)]
    [DataRow(SoakMemoryScope.FixtureWorkerWorkingSet)]
    [DataRow(SoakMemoryScope.LinuxCgroupV2MemoryCurrent)]
    public void CompatibleCalibratedProfile_ExcludesWarmupAndEvaluatesOnlyItsDeclaredScope(SoakMemoryScope scope)
    {
        var profile = Profile(scope);
        var below = profile.Evaluate(Capabilities, new string('A', 64),
            [Sample(1000, 1, scope, SoakPhase.Warmup), Sample(80, 2, scope), Sample(90, 3, scope)]);
        Assert.AreEqual(SoakProfileState.Applied, below.State);
        Assert.AreEqual(2, below.Samples);
        Assert.AreEqual(90L, below.ObservedBytes);
        Assert.IsFalse(below.Exceeded);
        var above = profile.Evaluate(Capabilities, new string('A', 64), [Sample(80, 2, scope), Sample(101, 3, scope)]);
        Assert.IsTrue(above.Exceeded);
        Assert.AreEqual("bytes", above.Units);
    }

    [TestMethod]
    public void DeltaRule_UsesTimestampOrderWithoutMistakingMaximumForGrowth()
    {
        var profile = Profile(SoakMemoryScope.TestHostWorkingSet) with { Aggregation = SoakAggregation.FirstLastDelta, LimitBytes = 10 };
        var decision = profile.Evaluate(Capabilities, new string('A', 64),
            [Sample(1000, 2, profile.Scope), Sample(995, 3, profile.Scope), Sample(990, 1, profile.Scope)]);
        Assert.AreEqual(5L, decision.ObservedBytes);
        Assert.IsFalse(decision.Exceeded);
    }

    [TestMethod]
    [DataRow("platform")]
    [DataRow("architecture")]
    [DataRow("runtime")]
    [DataRow("container")]
    [DataRow("executable")]
    [DataRow("worker-runtime")]
    [DataRow("input-profile")]
    public void CalibrationMismatch_IsNotAppliedEvenWhenValuesExceedItsLimit(string field)
    {
        var capabilities = field switch
        {
            "platform" => Capabilities with { Platform = SoakPlatform.MacOS },
            "architecture" => Capabilities with { Architecture = Architecture.Arm64 },
            "runtime" => Capabilities with { Runtime = "10.0.1" },
            "container" => Capabilities with { Container = true },
            "worker-runtime" => Capabilities with { WorkerRuntimeSha256 = new string('E', 64) },
            "input-profile" => Capabilities with { InputProfileSha256 = new string('E', 64) },
            _ => Capabilities
        };
        var profile = Profile(SoakMemoryScope.TestHostWorkingSet);
        var result = profile.Evaluate(capabilities, new string(field == "executable" ? 'C' : 'A', 64),
            [Sample(999, 1, profile.Scope), Sample(999, 2, profile.Scope)]);
        Assert.AreEqual(SoakProfileState.IncompatiblePlatform, result.State);
        Assert.IsFalse(result.Exceeded);
    }

    [TestMethod]
    public void MissingCapabilityOrSamples_PreserveAnExplicitNotAppliedDecision()
    {
        var profile = Profile(SoakMemoryScope.LinuxCgroupV2MemoryCurrent);
        Assert.AreEqual(SoakProfileState.CapabilityUnavailable,
            profile.Evaluate(Capabilities with { CgroupV2 = false }, new string('A', 64), []).State);
        Assert.AreEqual(SoakProfileState.InsufficientSamples,
            profile.Evaluate(Capabilities, new string('A', 64), [Sample(1000, 1, profile.Scope)]).State);
    }

    [TestMethod]
    public void UnprofiledTrends_ReportGrowthAndUnavailableObservationsWithoutInventingAThreshold()
    {
        var scope = SoakMemoryScope.FixtureWorkerWorkingSet;
        var job = new SoakJobEvidence(new(1, SoakPhase.Measured, WorkerJobKind.ProcessAssets, SoakCycle.Success), 1, 2, 1, 3,
            WorkerJobTerminalOutcome.Completed, 0, SoakEvidenceTests.Clean, [],
            [Sample(10, 1, scope), Sample(30, 2, scope), new(SoakPhase.Measured, 1, scope, 3, null, 0, SoakMissingMemory.AccessDenied)], []);
        var trend = SoakThresholdProfile.Trends([job]).Single();
        Assert.AreEqual(20L, trend.FirstLastDelta);
        Assert.AreEqual(20.0, trend.Median);
        Assert.AreEqual(1, trend.Missing);
        Assert.IsEmpty(SoakEvidence.Validate(2, new HashSet<int>(), job.Finality, [], job.Outcome, SoakCycle.Success));
    }

    [TestMethod]
    [DataRow("{\"seed\":1,\"seed\":2}")]
    [DataRow("{\"unknownSecret\":\"password-payload\"}")]
    [DataRow("[]")]
    [DataRow("{broken")]
    public void InvalidOrAmbiguousConfiguration_ProducesOnlyABoundedFailure(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), "soak-config-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, json);
            var exception = Assert.ThrowsExactly<ArgumentException>(() => WorkerMemorySoakOptions.Load(path));
            StringAssert.StartsWith(exception.Message, "soak/");
            Assert.IsFalse(exception.Message.Contains("password-payload", StringComparison.Ordinal));
            Assert.IsFalse(exception.Message.Contains(path, StringComparison.Ordinal));
            Assert.IsNull(exception.InnerException);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ExplicitValidConfiguration_IsAppliedAndCannotRedirectOutputOutsideTheOwnedTree()
    {
        string path = Path.Combine(Path.GetTempPath(), "soak-config-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"seed":123,"warmupIterations":10,"measuredIterations":20,"cancellation":true,"failure":true}""");
            var options = WorkerMemorySoakOptions.Load(path);
            Assert.AreEqual(123, options.Configuration.Seed);
            Assert.AreEqual(10, options.Configuration.WarmupIterations);
            Assert.AreEqual(20, options.Configuration.MeasuredIterations);
            Assert.IsTrue(options.Configuration.Cancellation && options.Configuration.Failure);
            File.WriteAllText(path, """{"outputRoot":"../outside"}""");
            Assert.ThrowsExactly<ArgumentException>(() => WorkerMemorySoakOptions.Load(path));
        }
        finally { File.Delete(path); }
    }
}
