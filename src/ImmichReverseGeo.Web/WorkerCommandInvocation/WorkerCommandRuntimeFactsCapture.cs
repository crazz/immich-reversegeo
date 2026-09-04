using System;
using System.IO;
using System.Reflection;

namespace ImmichReverseGeo.Web.WorkerCommandInvocation;

internal interface IWorkerCommandRuntimeFactsCapture
{
    WorkerCommandRuntimeFacts Capture();
}

internal interface IWorkerCommandRuntimeObservationSource
{
    string? GetProcessPath();

    WorkerCommandEntryAssemblyObservation GetEntryAssembly();

    string GetCurrentDirectory();

    bool IsWindows();

    WorkerTargetObservation ObserveTarget(string path);
}

internal sealed class WorkerCommandEntryAssemblyObservation
{
    internal WorkerCommandEntryAssemblyObservation(string? simpleIdentity, string? location)
    {
        SimpleIdentity = simpleIdentity;
        Location = location;
    }

    internal string? SimpleIdentity { get; }

    internal string? Location { get; }
}

/// <summary>
/// Captures ambient runtime facts once and returns only immutable resolver input.
/// </summary>
internal sealed class WorkerCommandRuntimeFactsCapture : IWorkerCommandRuntimeFactsCapture
{
    private readonly IWorkerCommandRuntimeObservationSource _source;

    internal WorkerCommandRuntimeFactsCapture(IWorkerCommandRuntimeObservationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public WorkerCommandRuntimeFacts Capture()
    {
        try
        {
            var processPath = _source.GetProcessPath();
            var entryAssembly = _source.GetEntryAssembly();
            var workingDirectory = _source.GetCurrentDirectory();
            var pathSemantics = _source.IsWindows() ? WorkerPathSemantics.Windows : WorkerPathSemantics.Unix;
            var processTarget = Observe(processPath);
            var entryTarget = Observe(entryAssembly.Location);
            var workingDirectoryTarget = Observe(workingDirectory);
            return new WorkerCommandRuntimeFacts(
                WorkerCommandInvocation.TrustedWebAssemblyIdentity,
                processPath,
                processTarget,
                entryAssembly.SimpleIdentity,
                entryAssembly.Location,
                entryTarget,
                workingDirectory,
                workingDirectoryTarget,
                pathSemantics);
        }
        catch (Exception exception) when (IsNonFatalObservationFailure(exception))
        {
            return new WorkerCommandRuntimeFacts(
                WorkerCommandInvocation.TrustedWebAssemblyIdentity,
                null,
                WorkerTargetObservation.Unavailable,
                null,
                null,
                WorkerTargetObservation.Unavailable,
                null,
                WorkerTargetObservation.Unavailable,
                WorkerPathSemantics.Unix);
        }
    }

    private WorkerTargetObservation Observe(string? path)
    {
        return string.IsNullOrEmpty(path) ? WorkerTargetObservation.Missing : _source.ObserveTarget(path);
    }

    private static bool IsNonFatalObservationFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException;
    }
}

/// <summary>
/// The sole production boundary that reads ambient runtime and filesystem facts.
/// </summary>
internal sealed class WorkerCommandAmbientRuntimeObservationSource : IWorkerCommandRuntimeObservationSource
{
    public string? GetProcessPath()
    {
        return Environment.ProcessPath;
    }

    public WorkerCommandEntryAssemblyObservation GetEntryAssembly()
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        return new WorkerCommandEntryAssemblyObservation(
            entryAssembly?.GetName().Name,
            entryAssembly?.Location);
    }

    public string GetCurrentDirectory()
    {
        return Directory.GetCurrentDirectory();
    }

    public bool IsWindows()
    {
        return OperatingSystem.IsWindows();
    }

    public WorkerTargetObservation ObserveTarget(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) == FileAttributes.Directory
                ? WorkerTargetObservation.Directory
                : WorkerTargetObservation.File;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return WorkerTargetObservation.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return WorkerTargetObservation.Unavailable;
        }
    }
}

internal interface IWorkerCommandInvocationBuilder
{
    WorkerCommandInvocationResolution Build();
}

/// <summary>
/// Lazily captures facts at invocation time before entering the pure resolver.
/// </summary>
internal sealed class WorkerCommandInvocationBuilder : IWorkerCommandInvocationBuilder
{
    private readonly IWorkerCommandRuntimeFactsCapture _capture;

    internal WorkerCommandInvocationBuilder(IWorkerCommandRuntimeFactsCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _capture = capture;
    }

    public WorkerCommandInvocationResolution Build()
    {
        return WorkerCommandInvocation.Resolve(_capture.Capture());
    }
}
