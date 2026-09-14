using ImmichReverseGeo.Core.WorkerJobs;
using System.Text.Json;

namespace ImmichReverseGeo.Tests.WorkerMemorySoak;

public enum SoakViolation
{
    DuplicatePid, MissingExit, MissingOutputDrain, MissingErrorDrain, MissingBridge,
    MissingDisposal, MissingOwnerRelease, ForcedCleanup, SurvivingProcess, SurvivingDescendant,
    LeakedHandle, ArtifactLeak, NetworkActivation, WebActivation, UnexpectedOutcome,
    InputChanged, ProfileThreshold, WorkloadNotVerified, HarnessFailure
}
public enum SoakMemoryScope { TestHostWorkingSet, FixtureWorkerWorkingSet, LinuxCgroupV2MemoryCurrent }
internal enum SoakMissingMemory { None, NoSample, ProcessExited, SampleFailed, AccessDenied, NotSupported }
internal enum SoakArtifactKind { ImmutableInput, ValidatedFinal, FixtureObservation, Unexpected }
internal enum SoakStage { Configuration, HostComposition, Launch, Startup, Cancellation, ControllerFinality, NativeExit, Settlement, Telemetry, Artifacts, Release, Disposal, ProfileEvaluation, Complete }

internal sealed record SoakMemory(SoakPhase Phase, int Order, SoakMemoryScope Scope, long Timestamp,
    long? Bytes, long SuccessfulSamples, SoakMissingMemory Unavailable);
internal sealed record SoakArtifact(SoakArtifactKind Kind, string IdentitySha256, long Length, string ContentsSha256);
internal sealed record SoakFinality(bool Exit, bool OutputDrain, bool ErrorDrain, bool Bridge,
    bool Disposed, bool OwnerReleased, bool ForcedCleanup, bool ProcessAlive, bool DescendantAlive,
    bool HandlesReleased, bool Registered, int WebActivations, bool NetworkActivation, bool WorkloadVerified);
internal sealed record SoakJobEvidence(SoakIteration Iteration, int ControllerPid, int WorkerPid, long Started,
    long Finished, WorkerJobTerminalOutcome? Outcome, int? ExitCode, SoakFinality Finality,
    SoakArtifact[] Artifacts, SoakMemory[] Memory, SoakViolation[] Violations);
internal sealed record SoakStartedJob(SoakIteration Iteration, int ControllerPid, int WorkerPid, long Timestamp);
internal sealed record SoakManifest(int Schema, int Seed, int Warmup, int Measured, SoakJobWeights Weights,
    bool Cancellation, bool Failure, int ControllerPid, SoakCapabilities Capabilities,
    string WorkerExecutableSha256, string[] InputSha256, string? ProfileSha256,
    string ControllerScope = "MSTest host containing production Standard Web composition",
    string WorkerScope = "fixture apphost containing production InternalWorkerHost and local external-data adapters",
    string Sampling = "Block 66 WorkingSet64: after start, every one second and opportunistically at finality; short workers may have only start/finality samples; not OS peak, process-tree, cgroup or system memory",
    bool IdentityComplete = true)
{
    public long TimestampFrequency => System.Diagnostics.Stopwatch.Frequency;
    public double ProcessingProportion => (double)Weights.Processing / Weights.Total;
    public double LookupProportion => (double)Weights.Lookup / Weights.Total;
    public double CacheProportion => (double)Weights.Cache / Weights.Total;
}
internal sealed record SoakTrend(SoakPhase Phase, WorkerJobKind Kind, SoakMemoryScope Scope,
    int Available, int Missing, long? Minimum, double? Median, long? Maximum, long? FirstLastDelta);
internal sealed record SoakSummary(int CompletedIterations, int WarmupSentinelBaseline,
    SoakTrend[] Trends, SoakProfileDecision Profile, SoakViolation[] Violations);
internal sealed record SoakTestResult(bool Passed, int CompletedIterations, SoakViolation[] Violations,
    SoakStage Stage = SoakStage.Complete, int LastIteration = 0);

// The public writing surface accepts only bounded evidence records. It never receives
// protocol objects, stderr, exception objects, configuration dictionaries or file paths.
internal sealed class SoakEvidence
{
    internal SoakEvidence(string outputRoot)
    {
        Directory.CreateDirectory(outputRoot);
        Root = Path.Combine(outputRoot, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "jobs"));
        Directory.CreateDirectory(Path.Combine(Root, "starts"));
    }

    internal string Root { get; }
    internal void Manifest(SoakManifest value) => Write("manifest.json", value);
    internal void Start(SoakStartedJob value) => Write($"starts/{value.Iteration.Order:D6}.json", value);
    internal void Job(SoakJobEvidence value) => Write($"jobs/{value.Iteration.Order:D6}.json", value);
    internal void Summary(SoakSummary value) => Write("summary.json", value);
    internal void Result(SoakTestResult value) => Write("test-result.json", value);

    private void Write<T>(string name, T value)
    {
        string destination = Path.Combine(Root, name);
        string temporary = destination + ".writing";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, WorkerMemorySoakOptions.Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, destination);
    }

    internal static SoakViolation[] Validate(int pid, ISet<int> earlierPids, SoakFinality finality,
        SoakArtifact[] artifacts, WorkerJobTerminalOutcome? observed, SoakCycle expected)
    {
        var failures = new List<SoakViolation>();
        if (pid <= 0 || !earlierPids.Add(pid)) { failures.Add(SoakViolation.DuplicatePid); }
        if (!finality.Exit) { failures.Add(SoakViolation.MissingExit); }
        if (!finality.OutputDrain) { failures.Add(SoakViolation.MissingOutputDrain); }
        if (!finality.ErrorDrain) { failures.Add(SoakViolation.MissingErrorDrain); }
        if (!finality.Bridge) { failures.Add(SoakViolation.MissingBridge); }
        if (!finality.Disposed || finality.Registered) { failures.Add(SoakViolation.MissingDisposal); }
        if (!finality.OwnerReleased) { failures.Add(SoakViolation.MissingOwnerRelease); }
        if (finality.ForcedCleanup) { failures.Add(SoakViolation.ForcedCleanup); }
        if (finality.ProcessAlive) { failures.Add(SoakViolation.SurvivingProcess); }
        if (finality.DescendantAlive) { failures.Add(SoakViolation.SurvivingDescendant); }
        if (!finality.HandlesReleased) { failures.Add(SoakViolation.LeakedHandle); }
        if (finality.WebActivations != 0) { failures.Add(SoakViolation.WebActivation); }
        if (finality.NetworkActivation) { failures.Add(SoakViolation.NetworkActivation); }
        if (!finality.WorkloadVerified) { failures.Add(SoakViolation.WorkloadNotVerified); }
        if (artifacts.Any(a => a.Kind == SoakArtifactKind.Unexpected)) { failures.Add(SoakViolation.ArtifactLeak); }
        var outcome = expected switch
        {
            SoakCycle.Success => WorkerJobTerminalOutcome.Completed,
            SoakCycle.CooperativeCancellation => WorkerJobTerminalOutcome.Cancelled,
            _ => WorkerJobTerminalOutcome.Failed
        };
        if (observed != outcome) { failures.Add(SoakViolation.UnexpectedOutcome); }
        return failures.ToArray();
    }
}
