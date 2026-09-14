using System;
using System.Diagnostics;

namespace ImmichReverseGeo.Web.WorkerCommandInvocation;

/// <summary>
/// Immutable observations consumed exclusively by worker-command resolution.
/// </summary>
[DebuggerDisplay("Worker command runtime facts.")]
internal sealed class WorkerCommandRuntimeFacts
{
    internal WorkerCommandRuntimeFacts(
        string? knownWebApplicationIdentity,
        string? processPath,
        WorkerTargetObservation processTarget,
        string? entryAssemblySimpleIdentity,
        string? entryAssemblyLocation,
        WorkerTargetObservation entryAssemblyTarget,
        string? workingDirectory,
        WorkerTargetObservation workingDirectoryTarget,
        WorkerPathSemantics pathSemantics)
    {
        ArgumentNullException.ThrowIfNull(processTarget);
        ArgumentNullException.ThrowIfNull(entryAssemblyTarget);
        ArgumentNullException.ThrowIfNull(workingDirectoryTarget);
        ArgumentNullException.ThrowIfNull(pathSemantics);

        KnownWebApplicationIdentity = knownWebApplicationIdentity;
        ProcessPath = processPath;
        ProcessTarget = processTarget;
        EntryAssemblySimpleIdentity = entryAssemblySimpleIdentity;
        EntryAssemblyLocation = entryAssemblyLocation;
        EntryAssemblyTarget = entryAssemblyTarget;
        WorkingDirectory = workingDirectory;
        WorkingDirectoryTarget = workingDirectoryTarget;
        PathSemantics = pathSemantics;
    }

    internal string? KnownWebApplicationIdentity { get; }

    internal string? ProcessPath { get; }

    internal WorkerTargetObservation ProcessTarget { get; }

    internal string? EntryAssemblySimpleIdentity { get; }

    internal string? EntryAssemblyLocation { get; }

    internal WorkerTargetObservation EntryAssemblyTarget { get; }

    internal string? WorkingDirectory { get; }

    internal WorkerTargetObservation WorkingDirectoryTarget { get; }

    internal WorkerPathSemantics PathSemantics { get; }

    public override string ToString()
    {
        return "Worker command runtime facts.";
    }

    internal bool HasObservationFailure => ProcessTarget.Kind == WorkerTargetObservationKind.Unavailable
        || EntryAssemblyTarget.Kind == WorkerTargetObservationKind.Unavailable
        || WorkingDirectoryTarget.Kind == WorkerTargetObservationKind.Unavailable;

    internal bool HasAmbiguousObservation => ProcessTarget.Kind == WorkerTargetObservationKind.Ambiguous
        || EntryAssemblyTarget.Kind == WorkerTargetObservationKind.Ambiguous
        || WorkingDirectoryTarget.Kind == WorkerTargetObservationKind.Ambiguous;
}

/// <summary>
/// A complete, typed filesystem observation supplied by the deterministic boundary.
/// </summary>
internal sealed class WorkerTargetObservation
{
    private WorkerTargetObservation(WorkerTargetObservationKind kind)
    {
        Kind = kind;
    }

    internal static WorkerTargetObservation Missing { get; } = new(WorkerTargetObservationKind.Missing);

    internal static WorkerTargetObservation File { get; } = new(WorkerTargetObservationKind.File);

    internal static WorkerTargetObservation Directory { get; } = new(WorkerTargetObservationKind.Directory);

    internal static WorkerTargetObservation Unavailable { get; } = new(WorkerTargetObservationKind.Unavailable);

    internal static WorkerTargetObservation Ambiguous { get; } = new(WorkerTargetObservationKind.Ambiguous);

    internal WorkerTargetObservationKind Kind { get; }
}

internal enum WorkerTargetObservationKind
{
    Missing,
    File,
    Directory,
    Unavailable,
    Ambiguous
}

/// <summary>
/// Deterministic operating-system path rules supplied with runtime facts.
/// </summary>
internal sealed class WorkerPathSemantics
{
    private WorkerPathSemantics(bool isWindows)
    {
        IsWindows = isWindows;
    }

    internal static WorkerPathSemantics Unix { get; } = new(false);

    internal static WorkerPathSemantics Windows { get; } = new(true);

    internal bool IsWindows { get; }

    internal bool IsAbsolute(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (!IsWindows)
        {
            return path[0] == '/';
        }

        return (path.Length >= 3
                && char.IsAsciiLetter(path[0])
                && path[1] == ':'
                && (path[2] == '\\' || path[2] == '/'))
            || (path.Length >= 2 && path[0] == '\\' && path[1] == '\\');
    }

    internal bool IsDotnetHost(string path)
    {
        return FileNameEquals(path, IsWindows ? "dotnet.exe" : "dotnet");
    }

    internal bool IsWebAppHost(string path, string webAssemblyIdentity)
    {
        return FileNameEquals(path, IsWindows ? $"{webAssemblyIdentity}.exe" : webAssemblyIdentity);
    }

    internal bool IsWebAppHostCandidate(string path, string webAssemblyIdentity)
    {
        var fileName = GetFileName(path);
        if (IsWindows && fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }

        return string.Equals(fileName, webAssemblyIdentity, StringComparison.OrdinalIgnoreCase)
            || (!IsWindows && string.Equals(fileName, $"{webAssemblyIdentity}.exe", StringComparison.OrdinalIgnoreCase));
    }

    internal bool FileNameEquals(string path, string expectedFileName)
    {
        return string.Equals(
            GetFileName(path),
            expectedFileName,
            IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private string GetFileName(string path)
    {
        var separatorIndex = path.LastIndexOfAny(IsWindows ? ['\\', '/'] : ['/']);
        return separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
    }
}
