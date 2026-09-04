using System.Collections.Generic;
using System.IO;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Invocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.WorkerCommandInvocation;

[TestClass]
public sealed class WorkerCommandInvocationBuilderTests
{
    private const string HostileCanary = "Password=credential-4912;Server=db.example;SELECT secret FROM users;StackTrace";
    private const string ExpectedReservedProtocolVariable = "IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION";

    [TestMethod]
    public void Builder_CapturesEveryAmbientFactAndExactTargetOnceThenUsesPureResolver()
    {
        var source = CountingSource.ValidUnix();
        var builder = new WorkerCommandInvocationBuilder(new WorkerCommandRuntimeFactsCapture(source));

        var resolution = builder.Build();
        var descriptor = Assert.IsInstanceOfType<WorkerCommandInvocationResolution.Success>(resolution).Invocation.Descriptor;

        Assert.AreEqual(1, source.ProcessPathCalls, "process-path-once");
        Assert.AreEqual(1, source.EntryAssemblyCalls, "entry-assembly-once");
        Assert.AreEqual(1, source.CurrentDirectoryCalls, "current-directory-once");
        Assert.AreEqual(1, source.OperatingSystemCalls, "operating-system-once");
        Assert.AreEqual(1, source.TargetCalls["/usr/share/dotnet/dotnet"], "process-target-once");
        Assert.AreEqual(1, source.TargetCalls["/app/ImmichReverseGeo.Web.dll"], "entry-target-once");
        Assert.AreEqual(1, source.TargetCalls["/app"], "cwd-target-once");
        Assert.AreEqual("/usr/share/dotnet/dotnet", descriptor.ExecutablePath, "exact-executable");
        CollectionAssert.AreEqual(new[] { "/app/ImmichReverseGeo.Web.dll", "--internal-worker" }, descriptor.Arguments.ToArray(), "exact-arguments");
        Assert.AreEqual("/app", descriptor.WorkingDirectory, "exact-cwd");
    }

    [TestMethod]
    public void ProductionObservation_MalformedHostilePathNormalizesToUnavailable()
    {
        var hostilePath = "/" + HostileCanary + "\0";
        var observation = new WorkerCommandAmbientRuntimeObservationSource().ObserveTarget(hostilePath);
        Assert.AreSame(WorkerTargetObservation.Unavailable, observation, "malformed-path-unavailable");

        var source = CountingSource.ValidUnix();
        source.ProcessPath = hostilePath;
        source.TargetExceptionAtCall = 1;
        var resolution = new WorkerCommandInvocationBuilder(new WorkerCommandRuntimeFactsCapture(source)).Build();
        var failure = Assert.IsInstanceOfType<WorkerCommandInvocationResolution.Failure>(resolution);
        Assert.IsFalse(resolution is WorkerCommandInvocationResolution.Success, "malformed-path-no-success");
        Assert.AreEqual(WorkerCommandInvocationFailureCategory.RuntimeObservationFailure, failure.Category, "malformed-path-category");
        Assert.AreEqual("runtime-observation-failure", failure.Code, "malformed-path-code");
        Assert.AreEqual("Correct the required runtime layout fact and retry resolution.", failure.Remediation, "malformed-path-remediation");
        Assert.AreEqual("Worker command invocation resolution failed.", failure.ToString(), "malformed-path-safe-text");
        Assert.IsFalse(failure.Code.Contains(HostileCanary, StringComparison.Ordinal), "malformed-path-code-redaction");
        Assert.IsFalse(failure.Remediation.Contains(HostileCanary, StringComparison.Ordinal), "malformed-path-remediation-redaction");
        Assert.IsFalse(failure.ToString().Contains(HostileCanary, StringComparison.Ordinal), "malformed-path-text-redaction");
        Assert.AreEqual(1, source.ProcessPathCalls, "malformed-path-process-once");
        Assert.AreEqual(1, source.TargetCalls[hostilePath], "malformed-path-target-once");
        Assert.AreEqual(1, source.EntryAssemblyCalls, "malformed-path-entry-captured-before-target");
        Assert.AreEqual(1, source.CurrentDirectoryCalls, "malformed-path-cwd-captured-before-target");
        Assert.AreEqual(1, source.OperatingSystemCalls, "malformed-path-os-captured-before-target");
    }

    [TestMethod]
    [DynamicData(nameof(NonfatalCaptureFailureCases))]
    public void Capture_NonfatalFailuresUseCompleteClosedOracleAndStopLaterStages(object item)
    {
        var testCase = (CaptureFailureCase)item;
        var source = CountingSource.ValidUnix();
        testCase.Configure(source);

        var resolution = new WorkerCommandInvocationBuilder(new WorkerCommandRuntimeFactsCapture(source)).Build();
        var failure = Assert.IsInstanceOfType<WorkerCommandInvocationResolution.Failure>(resolution, testCase.Label);
        Assert.IsFalse(resolution is WorkerCommandInvocationResolution.Success, testCase.Label + "-no-success");
        Assert.AreEqual(WorkerCommandInvocationFailureCategory.RuntimeObservationFailure, failure.Category, testCase.Label + "-category");
        Assert.AreEqual("runtime-observation-failure", failure.Code, testCase.Label + "-code");
        Assert.AreEqual("Correct the required runtime layout fact and retry resolution.", failure.Remediation, testCase.Label + "-remediation");
        Assert.AreEqual("Worker command invocation resolution failed.", failure.ToString(), testCase.Label + "-safe-text");
        Assert.IsFalse(failure.Code.Contains(HostileCanary, StringComparison.Ordinal), testCase.Label + "-code-redaction");
        Assert.IsFalse(failure.Remediation.Contains(HostileCanary, StringComparison.Ordinal), testCase.Label + "-remediation-redaction");
        Assert.IsFalse(failure.ToString().Contains(HostileCanary, StringComparison.Ordinal), testCase.Label + "-text-redaction");
        Assert.AreEqual(testCase.ProcessCalls, source.ProcessPathCalls, testCase.Label + "-process-calls");
        Assert.AreEqual(testCase.EntryCalls, source.EntryAssemblyCalls, testCase.Label + "-entry-calls");
        Assert.AreEqual(testCase.DirectoryCalls, source.CurrentDirectoryCalls, testCase.Label + "-cwd-calls");
        Assert.AreEqual(testCase.OperatingSystemCalls, source.OperatingSystemCalls, testCase.Label + "-os-calls");
        Assert.AreEqual(testCase.ProcessTargetCalls, source.TargetCallCount("/usr/share/dotnet/dotnet"), testCase.Label + "-process-target-calls");
        Assert.AreEqual(testCase.EntryTargetCalls, source.TargetCallCount("/app/ImmichReverseGeo.Web.dll"), testCase.Label + "-entry-target-calls");
        Assert.AreEqual(testCase.WorkingDirectoryTargetCalls, source.TargetCallCount("/app"), testCase.Label + "-cwd-target-calls");
    }

    public static IEnumerable<object[]> NonfatalCaptureFailureCases()
    {
        yield return [new CaptureFailureCase("process-source-io", source => source.ProcessPathException = new IOException(HostileCanary), 1, 0, 0, 0, 0, 0, 0)];
        yield return [new CaptureFailureCase("entry-source-io", source => source.EntryException = new IOException(HostileCanary), 1, 1, 0, 0, 0, 0, 0)];
        yield return [new CaptureFailureCase("cwd-source-io", source => source.DirectoryException = new IOException(HostileCanary), 1, 1, 1, 0, 0, 0, 0)];
        yield return [new CaptureFailureCase("os-source-io", source => source.OperatingSystemException = new IOException(HostileCanary), 1, 1, 1, 1, 0, 0, 0)];
        yield return [new CaptureFailureCase("process-target-io", source => source.TargetExceptionAtCall = 1, 1, 1, 1, 1, 1, 0, 0)];
        yield return [new CaptureFailureCase("entry-target-io", source => source.TargetExceptionAtCall = 2, 1, 1, 1, 1, 1, 1, 0)];
        yield return [new CaptureFailureCase("cwd-target-io", source => source.TargetExceptionAtCall = 3, 1, 1, 1, 1, 1, 1, 1)];
    }

    [TestMethod]
    public void Capture_DoesNotNormalizeFatalRuntimeFailure()
    {
        var source = CountingSource.ValidUnix();
        var fatal = new OutOfMemoryException(HostileCanary);
        source.ProcessPathException = fatal;

        var exception = Assert.ThrowsExactly<OutOfMemoryException>(
            () => new WorkerCommandInvocationBuilder(new WorkerCommandRuntimeFactsCapture(source)).Build());
        Assert.AreSame(fatal, exception, "fatal-reference");
    }

    [TestMethod]
    public void EnvironmentPolicies_AreValueFreeInheritAllWithOnlyFixedWorkerRemoval()
    {
        var general = new ChildProcessStartDescriptor(
            "/fixture/host",
            new[] { "/fixture/worker.dll" },
            "/fixture",
            ChildProcessEnvironmentPolicy.InheritCurrent);
        var workerDescriptor = Assert.IsInstanceOfType<WorkerCommandInvocationResolution.Success>(
            new WorkerCommandInvocationBuilder(new WorkerCommandRuntimeFactsCapture(CountingSource.ValidUnix())).Build()).Invocation.Descriptor;

        Assert.AreEqual(ChildProcessEnvironmentPolicy.InheritCurrent, general.EnvironmentPolicy, "general-inherit-all");
        Assert.IsFalse(ReportsReservedRemoval(general.EnvironmentPolicy), "general-has-no-removal");
        Assert.IsTrue(ChildProcessEnvironmentPolicyDetails.RemovesReservedProtocolVersion(workerDescriptor.EnvironmentPolicy), "worker-only-removal");
        var exactReservedName = new string(ExpectedReservedProtocolVariable.ToCharArray());
        Assert.AreEqual(ChildProcessEnvironmentPolicyDetails.ReservedProtocolVersionVariable, exactReservedName, "exact-fixed-removal");
        CollectionAssert.AreEqual(new[] { "/app/ImmichReverseGeo.Web.dll", "--internal-worker" }, workerDescriptor.Arguments.ToArray(), "worker-arguments-contain-no-environment-data");
    }

    [TestMethod]
    public void ValidSuccessRepresentations_RedactHostilePathsArgumentsAndEnvironmentMarker()
    {
        var hostileDirectory = "/app/" + HostileCanary;
        var facts = new WorkerCommandRuntimeFacts(
            "ImmichReverseGeo.Web",
            hostileDirectory + "/dotnet",
            WorkerTargetObservation.File,
            "ImmichReverseGeo.Web",
            hostileDirectory + "/ImmichReverseGeo.Web.dll",
            WorkerTargetObservation.File,
            hostileDirectory,
            WorkerTargetObservation.Directory,
            WorkerPathSemantics.Unix);
        var success = Assert.IsInstanceOfType<WorkerCommandInvocationResolution.Success>(Invocation.Resolve(facts));
        var descriptor = success.Invocation.Descriptor;
        var representations = new[] { facts.ToString()!, success.ToString()!, success.Invocation.ToString()!, descriptor.ToString()! };

        foreach (var representation in representations)
        {
            Assert.IsFalse(representation.Contains(HostileCanary, StringComparison.Ordinal), "success-hostile-redaction");
            Assert.IsFalse(representation.Contains("--internal-worker", StringComparison.Ordinal), "success-argument-redaction");
            Assert.IsFalse(representation.Contains(ChildProcessEnvironmentPolicyDetails.ReservedProtocolVersionVariable, StringComparison.Ordinal), "success-environment-redaction");
        }
    }

    [TestMethod]
    public void CapturedAndResolutionRepresentations_RedactEveryHostileFact()
    {
        var source = CountingSource.ValidUnix();
        source.ProcessPath = "/" + HostileCanary + "/dotnet";
        source.Entry = new WorkerCommandEntryAssemblyObservation("ImmichReverseGeo.Web", "/app/ImmichReverseGeo.Web.dll");
        var facts = new WorkerCommandRuntimeFactsCapture(source).Capture();
        var resolution = Invocation.Resolve(facts);

        Assert.IsFalse(facts.ToString()!.Contains(HostileCanary, StringComparison.Ordinal), "facts-redaction");
        Assert.IsFalse(resolution.ToString()!.Contains(HostileCanary, StringComparison.Ordinal), "result-redaction");
        var failure = Assert.IsInstanceOfType<WorkerCommandInvocationResolution.Failure>(resolution);
        Assert.IsFalse(failure.Code.Contains(HostileCanary, StringComparison.Ordinal), "code-redaction");
        Assert.IsFalse(failure.Remediation.Contains(HostileCanary, StringComparison.Ordinal), "remediation-redaction");
    }

    private static bool ReportsReservedRemoval(ChildProcessEnvironmentPolicy policy)
    {
        return ChildProcessEnvironmentPolicyDetails.RemovesReservedProtocolVersion(policy);
    }

    private sealed record CaptureFailureCase(
        string Label,
        Action<CountingSource> Configure,
        int ProcessCalls,
        int EntryCalls,
        int DirectoryCalls,
        int OperatingSystemCalls,
        int ProcessTargetCalls,
        int EntryTargetCalls,
        int WorkingDirectoryTargetCalls)
    {
        public override string ToString() => Label;
    }

    private sealed class CountingSource : IWorkerCommandRuntimeObservationSource
    {
        private readonly Dictionary<string, WorkerTargetObservation> _targets = new(StringComparer.Ordinal);

        internal string? ProcessPath { get; set; }

        internal WorkerCommandEntryAssemblyObservation Entry { get; set; } = new("ImmichReverseGeo.Web", "/app/ImmichReverseGeo.Web.dll");

        internal string CurrentDirectory { get; set; } = "/app";

        internal bool Windows { get; set; }

        internal Exception? ProcessPathException { get; set; }

        internal Exception? EntryException { get; set; }

        internal Exception? DirectoryException { get; set; }

        internal Exception? OperatingSystemException { get; set; }

        internal int? TargetExceptionAtCall { get; set; }

        internal int ProcessPathCalls { get; private set; }

        internal int EntryAssemblyCalls { get; private set; }

        internal int CurrentDirectoryCalls { get; private set; }

        internal int OperatingSystemCalls { get; private set; }

        internal Dictionary<string, int> TargetCalls { get; } = new(StringComparer.Ordinal);

        internal int TargetCallCount(string path)
        {
            return TargetCalls.TryGetValue(path, out var count) ? count : 0;
        }

        internal static CountingSource ValidUnix()
        {
            var source = new CountingSource
            {
                ProcessPath = "/usr/share/dotnet/dotnet"
            };
            source._targets[source.ProcessPath] = WorkerTargetObservation.File;
            source._targets[source.Entry.Location!] = WorkerTargetObservation.File;
            source._targets[source.CurrentDirectory] = WorkerTargetObservation.Directory;
            return source;
        }

        public string? GetProcessPath()
        {
            ProcessPathCalls++;
            if (ProcessPathException is not null)
            {
                throw ProcessPathException;
            }

            return ProcessPath;
        }

        public WorkerCommandEntryAssemblyObservation GetEntryAssembly()
        {
            EntryAssemblyCalls++;
            if (EntryException is not null)
            {
                throw EntryException;
            }

            return Entry;
        }

        public string GetCurrentDirectory()
        {
            CurrentDirectoryCalls++;
            if (DirectoryException is not null)
            {
                throw DirectoryException;
            }

            return CurrentDirectory;
        }

        public bool IsWindows()
        {
            OperatingSystemCalls++;
            if (OperatingSystemException is not null)
            {
                throw OperatingSystemException;
            }

            return Windows;
        }

        public WorkerTargetObservation ObserveTarget(string path)
        {
            TargetCalls[path] = TargetCalls.TryGetValue(path, out var count) ? count + 1 : 1;
            if (TargetExceptionAtCall == TargetCalls.Values.Sum())
            {
                throw new IOException(HostileCanary);
            }

            return _targets.TryGetValue(path, out var target) ? target : WorkerTargetObservation.Missing;
        }
    }
}
