using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Tests.WorkerMemorySoak;

internal enum SoakPlatform { MacOS, Linux, Windows, Unsupported }
internal enum SoakAggregation { Maximum, FirstLastDelta }
internal enum SoakProfileState { NoProfile, InvalidProfile, IncompatiblePlatform, CapabilityUnavailable, InsufficientSamples, Applied }
internal sealed record SoakCapabilities(SoakPlatform Platform, Architecture Architecture, string Runtime,
    bool Container, bool CgroupV2)
{
    public string WorkerRuntimeSha256 { get; init; } = "";
    public string InputProfileSha256 { get; init; } = "";
    internal static SoakCapabilities Capture(WorkerMemorySoakOptions options) => Capture() with
    {
        WorkerRuntimeSha256 = WorkerMemorySoakOptions.HashRuntime(),
        InputProfileSha256 = WorkerMemorySoakOptions.HashInputProfile(options)
    };
    internal static SoakCapabilities Capture() => new(
        OperatingSystem.IsMacOS() ? SoakPlatform.MacOS : OperatingSystem.IsLinux() ? SoakPlatform.Linux
        : OperatingSystem.IsWindows() ? SoakPlatform.Windows : SoakPlatform.Unsupported,
        RuntimeInformation.ProcessArchitecture, Environment.Version.ToString(),
        File.Exists("/.dockerenv") || File.Exists("/run/.containerenv"), CgroupMemoryPath() is not null);

    internal static string? CgroupMemoryPath()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        try
        {
            string? membership = File.ReadLines("/proc/self/cgroup").SingleOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal));
            if (membership is null)
            {
                return null;
            }
            string relative = membership[3..].TrimStart('/');
            // Resolve only the current process' standard unified hierarchy. Other
            // mount layouts are unavailable, never guessed from arbitrary files.
            string path = Path.GetFullPath(Path.Combine("/sys/fs/cgroup", relative, "memory.current"));
            return path.StartsWith("/sys/fs/cgroup/", StringComparison.Ordinal) && File.Exists(path) ? path : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static SoakMemory Sample(SoakPhase phase, int order, SoakMemoryScope scope)
    {
        long timestamp = Stopwatch.GetTimestamp();
        try
        {
            long bytes;
            if (scope == SoakMemoryScope.LinuxCgroupV2MemoryCurrent)
            {
                string? path = CgroupMemoryPath();
                if (path is null)
                {
                    return new(phase, order, scope, timestamp, null, 0, SoakMissingMemory.NotSupported);
                }
                if (!long.TryParse(File.ReadAllText(path).Trim(), out bytes) || bytes < 0)
                {
                    return new(phase, order, scope, timestamp, null, 0, SoakMissingMemory.SampleFailed);
                }
            }
            else
            {
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                bytes = process.WorkingSet64;
            }
            return new(phase, order, scope, timestamp, bytes, 1, SoakMissingMemory.None);
        }
        catch (UnauthorizedAccessException)
        {
            return new(phase, order, scope, timestamp, null, 0, SoakMissingMemory.AccessDenied);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return new(phase, order, scope, timestamp, null, 0, SoakMissingMemory.SampleFailed);
        }
    }
}

internal sealed record SoakProfileDecision(SoakProfileState State, SoakMemoryScope? Scope,
    SoakAggregation? Aggregation, string? CalibrationSha256, int Samples, long? ObservedBytes,
    long? LimitBytes, bool Exceeded, string Units = "bytes",
    string CgroupScope = "current test-host cgroup including descendants and any other members; not per-worker RSS");

internal sealed record SoakThresholdProfile(SoakPlatform Platform, Architecture Architecture, string Runtime,
    bool Container, SoakMemoryScope Scope, SoakAggregation Aggregation, string CalibrationSha256,
    string WorkerExecutableSha256, int MinimumSamples, bool ExcludeWarmup, long LimitBytes)
{
    public string WorkerRuntimeSha256 { get; init; } = "";
    public string InputProfileSha256 { get; init; } = "";
    internal static SoakThresholdProfile Load(string path)
    {
        var profile = WorkerMemorySoakOptions.Read<SoakThresholdProfile>(path);
        profile.Validate();
        return profile;
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(Platform) || !Enum.IsDefined(Architecture) || !Enum.IsDefined(Scope)
            || !Enum.IsDefined(Aggregation) || !Version.TryParse(Runtime, out var version)
            || version.ToString() != Runtime || MinimumSamples is < 2 or > 200_000 || !ExcludeWarmup
            || LimitBytes < 0 || !IsHash(CalibrationSha256) || !IsHash(WorkerExecutableSha256)
            || !IsHash(WorkerRuntimeSha256) || !IsHash(InputProfileSha256))
        {
            throw new ArgumentException("soak/invalid-threshold-profile");
        }
    }

    private static bool IsHash(string? value) => value is not null
        && Regex.IsMatch(value, "\\A[0-9A-F]{64}\\z", RegexOptions.CultureInvariant);

    internal SoakProfileDecision Evaluate(SoakCapabilities capabilities, string executableSha256, IEnumerable<SoakMemory> observations)
    {
        Validate();
        if (capabilities.Platform != Platform || capabilities.Architecture != Architecture
            || capabilities.Runtime != Runtime || capabilities.Container != Container || executableSha256 != WorkerExecutableSha256
            || capabilities.WorkerRuntimeSha256 != WorkerRuntimeSha256 || capabilities.InputProfileSha256 != InputProfileSha256)
        {
            return Decision(SoakProfileState.IncompatiblePlatform, 0);
        }
        if (Scope == SoakMemoryScope.LinuxCgroupV2MemoryCurrent && !capabilities.CgroupV2)
        {
            return Decision(SoakProfileState.CapabilityUnavailable, 0);
        }
        long[] values = observations.Where(o => o.Phase == SoakPhase.Measured && o.Scope == Scope && o.Bytes.HasValue)
            .OrderBy(o => o.Timestamp).Select(o => o.Bytes!.Value).ToArray();
        if (values.Length < MinimumSamples)
        {
            return Decision(SoakProfileState.InsufficientSamples, values.Length);
        }
        long observed = Aggregation == SoakAggregation.Maximum ? values.Max() : values[^1] - values[0];
        return Decision(SoakProfileState.Applied, values.Length, observed);
    }

    private SoakProfileDecision Decision(SoakProfileState state, int count, long? observed = null) =>
        new(state, Scope, Aggregation, CalibrationSha256, count, observed, LimitBytes,
            state == SoakProfileState.Applied && observed > LimitBytes);

    internal static SoakTrend[] Trends(IEnumerable<SoakJobEvidence> jobs) => jobs.SelectMany(job =>
        job.Memory.Select(memory => (job.Iteration.Kind, Memory: memory)))
        .GroupBy(row => (row.Memory.Phase, row.Kind, row.Memory.Scope)).Select(group =>
        {
            var all = group.OrderBy(row => row.Memory.Timestamp).ToArray();
            var values = all.Where(row => row.Memory.Bytes.HasValue).Select(row => row.Memory.Bytes!.Value).ToArray();
            var sorted = values.Order().ToArray();
            double? median = sorted.Length == 0 ? null : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
                : sorted[sorted.Length / 2 - 1] / 2.0 + sorted[sorted.Length / 2] / 2.0;
            return new SoakTrend(group.Key.Phase, group.Key.Kind, group.Key.Scope, values.Length,
                all.Length - values.Length, sorted.Length == 0 ? null : sorted[0], median,
                sorted.Length == 0 ? null : sorted[^1], values.Length < 2 ? null : values[^1] - values[0]);
        }).ToArray();
}
