using ImmichReverseGeo.Web.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.CrossProcessRunExclusion;

internal static class CrossProcessAppHostLocator
{
    internal const string CrossProcessRunLockAppHostStageDirectoryName = "cross-process-run-lock-apphost";
    internal const string CrossProcessRunLockAppHostAssemblyName = "ImmichReverseGeo.CrossProcessRunLockTestAppHost";
    internal const string ProductionWorkerAppHostStageDirectoryName = "production-worker-apphost";
    internal const string ProductionWorkerAppHostAssemblyName = "ImmichReverseGeo.Web";

    internal static string CrossProcessRunLockAppHostDirectory => Path.Combine(
        AppContext.BaseDirectory,
        CrossProcessRunLockAppHostStageDirectoryName);

    internal static string CrossProcessRunLockAppHostExecutable => Path.Combine(
        CrossProcessRunLockAppHostDirectory,
        CrossProcessRunLockAppHostAssemblyName + ExecutableExtension);

    internal static string ProductionWorkerAppHostDirectory => Path.Combine(
        AppContext.BaseDirectory,
        ProductionWorkerAppHostStageDirectoryName);

    internal static string ProductionWorkerAppHostExecutable => Path.Combine(
        ProductionWorkerAppHostDirectory,
        ProductionWorkerAppHostAssemblyName + ExecutableExtension);

    internal static ChildProcessStartDescriptor CreateCrossProcessRunLockDescriptor(IReadOnlyList<string> arguments)
    {
        return CreateDescriptor(CrossProcessRunLockAppHostExecutable, CrossProcessRunLockAppHostDirectory, arguments);
    }

    internal static ChildProcessStartDescriptor CreateProductionWorkerDescriptor()
    {
        return CreateDescriptor(
            ProductionWorkerAppHostExecutable,
            ProductionWorkerAppHostDirectory,
            ["--internal-worker"]);
    }

    private static ChildProcessStartDescriptor CreateDescriptor(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !Path.IsPathFullyQualified(workingDirectory))
        {
            throw new InvalidOperationException("The staged apphost locator must resolve absolute paths.");
        }

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The staged apphost is unavailable. Build or publish the test project first.", executablePath);
        }

        return new ChildProcessStartDescriptor(
            executablePath,
            arguments,
            workingDirectory,
            ChildProcessEnvironmentPolicy.InheritCurrent);
    }

    private static string ExecutableExtension => OperatingSystem.IsWindows() ? ".exe" : string.Empty;
}
