using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerEventDelivery;

namespace ImmichReverseGeo.Tests.WorkerMemorySoak;

internal sealed record SoakRunResult(string EvidenceDirectory, SoakTestResult Result);

internal static class WorkerMemorySoakRun
{
    internal static async Task<SoakRunResult> RunAsync(WorkerMemorySoakOptions options)
    {
        var evidence = new SoakEvidence(options.OutputRoot);
        var capabilities = SoakCapabilities.Capture();
        var violations = new List<SoakViolation>();
        var seen = new HashSet<int>();
        int completed = 0;
        int baseline = 0;
        int lastIteration = 0;
        var stage = SoakStage.Configuration;
        ProcessFailureMatrixHost? host = null;
        SoakThresholdProfile? profile = null;
        string executableHash = "";
        var config = options.Configuration;
        string[] inputHashes = [];
        string? profileHash = null;
        try
        {
            capabilities = SoakCapabilities.Capture(options);
            executableHash = WorkerMemorySoakOptions.HashFile(WorkerProcessFixtureLease.FixtureExecutable);
            inputHashes = WorkerMemorySoakOptions.InputHashes(options);
            profileHash = options.ThresholdProfilePath is null ? null : WorkerMemorySoakOptions.HashFile(options.ThresholdProfilePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            violations.Add(SoakViolation.HarnessFailure);
        }
        evidence.Manifest(new(1, config.Seed, config.WarmupIterations, config.MeasuredIterations, config.Weights,
            config.Cancellation, config.Failure, Environment.ProcessId, capabilities, executableHash, inputHashes,
            profileHash, IdentityComplete: violations.Count == 0));
        try
        {
            if (violations.Count != 0)
            {
                throw new ArgumentException("soak/identity-preparation-failed");
            }
            profile = options.ThresholdProfilePath is null ? null : SoakThresholdProfile.Load(options.ThresholdProfilePath);
            stage = SoakStage.HostComposition;
            host = await ProcessFailureMatrixHost.CreateAsync("memory-soak", startHost: true);
            Directory.CreateDirectory(Path.Combine(host.Root, "bundled-data"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "data", "iso3166.json"), Path.Combine(host.Root, "bundled-data", "iso3166.json"));
            host.Launcher.SoakInputDirectory = options.InputDirectory;
            foreach (var iteration in config.CreateSequence())
            {
                lastIteration = iteration.Order;
                if (iteration.Order == config.WarmupIterations + 1)
                {
                    baseline = host.SoakSentinel.Events.Count;
                    if (baseline != 0)
                    {
                        violations.Add(SoakViolation.WebActivation);
                        break;
                    }
                }
                var record = await RunIterationAsync(host, iteration, seen, baseline, evidence, value => stage = value);
                evidence.Job(record);
                completed++;
                violations.AddRange(record.Violations);
                if (violations.Count != 0)
                {
                    break;
                }
            }
        }
        catch
        {
            // Arbitrary library/fixture exceptions never enter the evidence bundle.
            violations.Add(SoakViolation.HarnessFailure);
        }
        finally
        {
            if (host is not null)
            {
                var remaining = host.Launcher.Leases;
                try
                {
                    await host.DisposeAsync();
                }
                catch
                {
                    violations.Add(SoakViolation.ForcedCleanup);
                }
                if (remaining.Any(lease => lease.ForcedCleanup || lease.IsRegistered || !lease.HasExited))
                {
                    violations.Add(SoakViolation.ForcedCleanup);
                }
            }
        }
        // Aggregate after the final measured sample; do not retain raw fixture
        // streams, telemetry entries or completed leases between measured jobs.
        var jobs = Directory.GetFiles(Path.Combine(evidence.Root, "jobs"), "*.json").Order(StringComparer.Ordinal)
            .Select(path => JsonSerializer.Deserialize<SoakJobEvidence>(File.ReadAllBytes(path), WorkerMemorySoakOptions.Json)!).ToArray();
        var decision = profile?.Evaluate(capabilities, executableHash, jobs.SelectMany(job => job.Memory))
            ?? new SoakProfileDecision(options.ThresholdProfilePath is null ? SoakProfileState.NoProfile : SoakProfileState.InvalidProfile,
                null, null, null, 0, null, null, false);
        if (decision.Exceeded)
        {
            violations.Add(SoakViolation.ProfileThreshold);
            stage = SoakStage.ProfileEvaluation;
        }
        var failures = violations.Distinct().ToArray();
        evidence.Summary(new(completed, baseline, SoakThresholdProfile.Trends(jobs), decision, failures));
        var result = new SoakTestResult(failures.Length == 0 && completed == config.WarmupIterations + config.MeasuredIterations,
            completed, failures, failures.Length == 0 ? SoakStage.Complete : stage, lastIteration);
        evidence.Result(result);
        return new(evidence.Root, result);
    }

    private static async Task<SoakJobEvidence> RunIterationAsync(ProcessFailureMatrixHost host,
        SoakIteration iteration, ISet<int> seen, int sentinelBaseline, SoakEvidence evidence, Action<SoakStage> stage)
    {
        Assert.IsNull(host.Coordinator.ActiveOwner, "soak/prior-owner-still-active");
        Assert.IsEmpty(host.Launcher.Leases, "soak/prior-lease-still-retained");
        host.Launcher.SoakCancellation = iteration.Cycle == SoakCycle.CooperativeCancellation;
        host.Launcher.CacheStage = iteration.Cycle == SoakCycle.PrePublicationFailure ? "pre-replace" : "success";
        long started = Stopwatch.GetTimestamp();
        var memory = new List<SoakMemory>
        {
            SoakCapabilities.Sample(iteration.Phase, iteration.Order, SoakMemoryScope.TestHostWorkingSet),
            SoakCapabilities.Sample(iteration.Phase, iteration.Order, SoakMemoryScope.LinuxCgroupV2MemoryCurrent)
        };
        int previousReleases = host.Releases;
        stage(SoakStage.Launch);
        Task run = host.RunAsync(iteration.Kind);
        var lease = await host.Launcher.NextLaunchAsync();
        evidence.Start(new(iteration, Environment.ProcessId, lease.ProcessId!.Value, Stopwatch.GetTimestamp()));
        stage(SoakStage.Startup);
        await MatrixWait.ForAsync(lease.Session!.Startup, "soak/production-ready");
        var immutable = InputHashes(lease.Root);
        if (iteration.Cycle == SoakCycle.CooperativeCancellation)
        {
            stage(SoakStage.Cancellation);
            await MatrixFileSignal.WaitAsync(lease.Root, "asset-work-entered.marker", "soak/production-asset-cancellation-boundary");
            await MatrixWait.ForAsync(host.Processing.StopActiveRun()!, "soak/cooperative-stop");
        }
        stage(SoakStage.ControllerFinality);
        await MatrixWait.ForAsync(run, "soak/controller-finality");
        stage(SoakStage.NativeExit);
        var raw = await lease.CompleteAsync();
        stage(SoakStage.Settlement);
        await MatrixWait.ForAsync(lease.Session.Settlement, "soak/process-stream-bridge-disposal-finality");
        stage(SoakStage.Telemetry);
        await host.JoinTelemetryAsync(lease);
        var child = lease.Session.WorkingSetObservation;
        stage(SoakStage.Artifacts);
        var artifacts = Snapshot(lease.Root, out bool handlesReleased);
        bool changedInput = immutable.Any(pair => !File.Exists(pair.Key) || WorkerMemorySoakOptions.HashFile(pair.Key) != pair.Value);
        bool forbiddenNetwork = File.Exists(Path.Combine(lease.Root, "soak-network-forbidden.marker"));
        bool descendantAlive = RegisteredDescendantAlive(lease.Root);
        bool processAlive = IsAlive(lease.ProcessId!.Value);
        bool bridge = lease.Session.EventDeliveryObservation?.Finality == WorkerEventDeliveryFinality.Terminal;
        bool forced = lease.ForcedCleanup || lease.TreeKillCalls != 0 || artifacts.Any(a => a.Kind == SoakArtifactKind.Unexpected);
        var terminal = raw.JobTerminal?.Payload as WorkerJobTerminalPayload;
        bool workload = iteration.Cycle == SoakCycle.CooperativeCancellation
            ? File.Exists(Path.Combine(lease.Root, "asset-work-cancelled.marker"))
            : iteration.Kind == WorkerJobKind.CacheMutation ? VerifyCacheWork(lease.Root, lease.ProcessId.Value, iteration.Cycle)
            : File.Exists(Path.Combine(lease.Root, "soak-geodata-queried.marker"))
                && (iteration.Kind == WorkerJobKind.CoordinateLookup
                    ? terminal?.CoordinateLookupResult is not null
                    : terminal?.ProcessAssetsResult is { ProcessedCount: > 0, FailedCount: 0, SkippedCount: 0 } assets
                        && assets.UpdatedCount == assets.ProcessedCount);
        stage(SoakStage.Release);
        await host.Launcher.ReleaseSoakLeaseAsync(lease);
        var finality = new SoakFinality(raw.ExitObserved,
            raw.StandardOutputFinality is ChildWorkerStreamFinality.EndOfStream,
            raw.StandardErrorFinality is ChildWorkerStreamFinality.EndOfStream, bridge,
            lease.ProcessDisposeCalls == 1 && !Directory.Exists(lease.Root),
            host.Coordinator.ActiveOwner is null && host.Releases == previousReleases + 1,
            forced || lease.ForcedCleanup, processAlive, descendantAlive, handlesReleased, lease.IsRegistered,
            host.SoakSentinel.Events.Count - sentinelBaseline + host.ForbiddenHeavyResolutions, forbiddenNetwork, workload);
        var outcome = (raw.JobTerminal?.Payload as WorkerJobTerminalPayload)?.Outcome;
        var failures = SoakEvidence.Validate(lease.ProcessId.Value, seen, finality, artifacts, outcome, iteration.Cycle).ToList();
        int expectedExit = iteration.Cycle == SoakCycle.Success ? 0 : iteration.Cycle == SoakCycle.CooperativeCancellation ? 130 : 4;
        if (raw.ExitCode != expectedExit || raw.FirstProtocolObservation is not null) { failures.Add(SoakViolation.UnexpectedOutcome); }
        if (changedInput) { failures.Add(SoakViolation.InputChanged); }
        memory.Add(new(iteration.Phase, iteration.Order, SoakMemoryScope.FixtureWorkerWorkingSet, Stopwatch.GetTimestamp(),
            child.PeakBytes, child.SuccessfulSamples, child.PeakBytes.HasValue ? SoakMissingMemory.None
                : Enum.Parse<SoakMissingMemory>(child.UnavailableReason.ToString())));
        memory.Add(SoakCapabilities.Sample(iteration.Phase, iteration.Order, SoakMemoryScope.TestHostWorkingSet));
        memory.Add(SoakCapabilities.Sample(iteration.Phase, iteration.Order, SoakMemoryScope.LinuxCgroupV2MemoryCurrent));
        return new(iteration, Environment.ProcessId, lease.ProcessId.Value, started, Stopwatch.GetTimestamp(),
            outcome, raw.ExitCode, finality, artifacts, memory.ToArray(), failures.ToArray());
    }

    private static Dictionary<string, string> InputHashes(string root)
    {
        string input = Path.Combine(root, "soak-input");
        var hashes = Directory.Exists(input) ? Directory.GetFiles(input).ToDictionary(path => path, WorkerMemorySoakOptions.HashFile)
            : new Dictionary<string, string>();
        string identities = Path.Combine(root, "bundled-data", "iso3166.json");
        if (File.Exists(identities)) { hashes.Add(identities, WorkerMemorySoakOptions.HashFile(identities)); }
        return hashes;
    }

    private static bool VerifyCacheWork(string root, int pid, SoakCycle cycle)
    {
        string path = Path.Combine(root, $"matrix-cache-{pid}.ndjson");
        if (!File.Exists(path)) { return false; }
        var phases = File.ReadLines(path).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("phase").GetString();
        }).ToArray();
        return phases.Count(p => p == "exported") == 1 && phases.Count(p => p == "validated") == 1
            && phases.Count(p => p == "handles-closed") == 1 && phases.Count(p => p == "released") == 2
            && phases.Count(p => p == "published") == (cycle == SoakCycle.Success ? 1 : 0)
            && phases.Count(p => p == "pre-replace") == (cycle == SoakCycle.PrePublicationFailure ? 1 : 0);
    }

    internal static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    internal static bool RegisteredDescendantAlive(string root)
    {
        // The closed block-67 fixture registers every descendant before handing
        // off work. These soak modes do not spawn descendants; a registered one
        // is still checked independently of the direct worker PID.
        string marker = Path.Combine(root, "descendant-pid.marker");
        return File.Exists(marker) && (!int.TryParse(File.ReadAllText(marker), out int pid) || IsAlive(pid));
    }

    internal static SoakArtifact[] Snapshot(string root, out bool handlesReleased)
    {
        handlesReleased = true;
        var artifacts = new List<SoakArtifact>();
        foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
            if (relative is not ("soak-input" or "bundled-data" or "gadm-divisions"))
            {
                artifacts.Add(new(SoakArtifactKind.Unexpected,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("directory:" + relative))),
                    0, Convert.ToHexString(SHA256.HashData([]))));
            }
        }
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string name = Path.GetFileName(file);
            var kind = relative is "soak-input/assets.db" or "soak-input/gadm.db" or "soak-input/source.gpkg" or "bundled-data/iso3166.json"
                ? SoakArtifactKind.ImmutableInput
                : relative is "gadm-divisions/CHE.db" or "assets-result.db" or "skipped.db" ? SoakArtifactKind.ValidatedFinal
                : relative is "network-substituted.marker" or "soak-input-ready.marker" or "soak-geodata-queried.marker"
                    or "hermetic-lease-released.marker" or "advisory-lock-probe-installed.marker"
                    or "asset-work-entered.marker" or "asset-work-cancelled.marker"
                    || (name.StartsWith("matrix-cache-", StringComparison.Ordinal)
                    && name.EndsWith(".ndjson", StringComparison.Ordinal)) ? SoakArtifactKind.FixtureObservation
                : SoakArtifactKind.Unexpected;
            try
            {
                using var stream = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                artifacts.Add(new(kind, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative))),
                    stream.Length, Convert.ToHexString(SHA256.HashData(stream))));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                handlesReleased = false;
                artifacts.Add(new(SoakArtifactKind.Unexpected, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative))),
                    0, Convert.ToHexString(SHA256.HashData([]))));
            }
        }
        return artifacts.ToArray();
    }
}
