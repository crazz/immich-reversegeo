using System.Collections.Generic;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Invocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.WorkerCommandInvocation;

[TestClass]
public sealed class WorkerCommandInvocationTests
{
    private const string WebIdentity = "ImmichReverseGeo.Web";
    private const string Token = "--internal-worker";
    private const string SafeDescriptorText = "Child process start descriptor.";
    private const string SafeFailureText = "Worker command invocation resolution failed.";
    private const string SafeRemediation = "Correct the required runtime layout fact and retry resolution.";
    private const string HostileCanary = "Password=credential-4912;CONFIG_DIR=/secret;StackTrace";

    [TestMethod]
    public void UnixFrameworkDependentSnapshot_ReturnsExactDescriptor()
    {
        var descriptor = AssertSuccess(Invocation.Resolve(Facts(
            WorkerPathSemantics.Unix,
            "/usr/share/dotnet/dotnet",
            "/workspace with spaces/ImmichReverseGeo.Web.dll",
            "/workspace with spaces")), "unix-framework-success");

        AssertDescriptor(descriptor, "/usr/share/dotnet/dotnet", "/workspace with spaces/ImmichReverseGeo.Web.dll", "/workspace with spaces", "unix-framework-success");
    }

    [TestMethod]
    public void DockerFrameworkDependentSnapshot_ReturnsExactDescriptor()
    {
        var descriptor = AssertSuccess(Invocation.Resolve(Facts(
            WorkerPathSemantics.Unix,
            "/usr/bin/dotnet",
            "/app/ImmichReverseGeo.Web.dll",
            "/app")), "docker-framework-success");

        AssertDescriptor(descriptor, "/usr/bin/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", "docker-framework-success");
    }

    [TestMethod]
    public void WindowsFrameworkDependentSnapshot_ReturnsExactDescriptor()
    {
        const string processPath = "C:\\Program Files\\dotnet\\dotnet.exe";
        const string entryPath = "C:\\App Root\\ImmichReverseGeo.Web.dll";
        const string workingDirectory = "C:\\App Root";
        var descriptor = AssertSuccess(Invocation.Resolve(Facts(
            WorkerPathSemantics.Windows,
            processPath,
            entryPath,
            workingDirectory)), "windows-framework-success");

        AssertDescriptor(descriptor, processPath, entryPath, workingDirectory, "windows-framework-success");
        Assert.IsFalse(descriptor.Arguments[0].Contains('"'), "windows-entry-unquoted");
        Assert.IsFalse(descriptor.Arguments[1].Contains('"'), "windows-token-unquoted");
    }

    [TestMethod]
    public void WindowsMixedCaseDotnetExecutable_ReturnsExactDescriptor()
    {
        const string executable = "C:\\Program Files\\dotnet\\DoTnEt.ExE";
        const string entry = "C:\\App\\ImmichReverseGeo.Web.dll";
        const string cwd = "C:\\App";
        var descriptor = AssertSuccess(Invocation.Resolve(Facts(WorkerPathSemantics.Windows, executable, entry, cwd)), "windows-mixed-case-dotnet");
        AssertDescriptor(descriptor, executable, entry, cwd, "windows-mixed-case-dotnet");
    }

    [TestMethod]
    [DynamicData(nameof(ApphostSuccessCases))]
    public void ApphostSnapshots_ReturnExactSingleTokenDescriptor(object item)
    {
        var testCase = (ApphostSuccessCase)item;
        var descriptor = AssertSuccess(Invocation.Resolve(testCase.Facts), testCase.Label);

        AssertApphostDescriptor(descriptor, testCase.Executable, testCase.WorkingDirectory, testCase.Label);
    }

    [TestMethod]
    public void GeneralDescriptor_CopiesMutableArgumentsAndRejectsOrderedMutation()
    {
        var callerArguments = new List<string> { "/fixture/worker.dll", "--fixture" };
        var descriptor = new ChildProcessStartDescriptor(
            "/fixture/host",
            callerArguments,
            "/fixture",
            ChildProcessEnvironmentPolicy.InheritCurrent);
        callerArguments[0] = HostileCanary;
        callerArguments.Add("--later");

        CollectionAssert.AreEqual(new[] { "/fixture/worker.dll", "--fixture" }, descriptor.Arguments.ToArray(), "general-descriptor-copied-arguments");
        Assert.AreEqual(ChildProcessEnvironmentPolicy.InheritCurrent, descriptor.EnvironmentPolicy, "general-descriptor-inherit-current-policy");
        Assert.IsTrue(descriptor.Arguments is IList<string>, "general-descriptor-ordered-surface");
        var immutableArguments = (IList<string>)descriptor.Arguments;
        Assert.ThrowsExactly<NotSupportedException>(() => immutableArguments[0] = HostileCanary, "general-descriptor-rejects-mutation");
        Assert.AreEqual(SafeDescriptorText, descriptor.ToString(), "general-descriptor-safe-text");
    }

    [TestMethod]
    [DynamicData(nameof(FailureCases))]
    public void OneDefectFailures_ReturnExactClosedRedactedFailure(object item)
    {
        var testCase = (FailureCase)item;
        var resolution = Invocation.Resolve(testCase.Facts);
        var failure = Assert.IsInstanceOfType<WorkerCommandInvocationResolution.Failure>(resolution, testCase.Label);
        Assert.IsFalse(resolution is WorkerCommandInvocationResolution.Success, testCase.Label);
        Assert.AreEqual(testCase.Category, failure.Category, testCase.Label);
        Assert.AreEqual(testCase.Code, failure.Code, testCase.Label);
        Assert.AreEqual(SafeRemediation, failure.Remediation, testCase.Label);
        Assert.AreEqual(SafeFailureText, failure.ToString(), testCase.Label);
        AssertSafeFailureRepresentations(failure, testCase.Label, testCase.RawSensitiveValues);
    }

    [TestMethod]
    [DynamicData(nameof(PrecedenceCases))]
    public void AdjacentMultiplyInvalidFacts_UseLockedPrecedence(object item)
    {
        var testCase = (PrecedenceCase)item;
        var failure = AssertFailure(Invocation.Resolve(testCase.Facts), testCase.Label);
        Assert.AreEqual(testCase.Category, failure.Category, testCase.Label);
        Assert.AreEqual(testCase.Code, failure.Code, testCase.Label);
        Assert.AreEqual(SafeRemediation, failure.Remediation, testCase.Label);
        Assert.AreEqual(SafeFailureText, failure.ToString(), testCase.Label);
        AssertSafeFailureRepresentations(failure, testCase.Label, []);
    }

    [TestMethod]
    public void ResolvedDescriptor_RemainsUnchangedAfterCallerMutationAndRepeatedResolution()
    {
        var callerArguments = new List<string> { "/independent/fixture.dll", "--fixture" };
        var independentlyConstructed = new ChildProcessStartDescriptor(
            "/independent/host",
            callerArguments,
            "/independent",
            ChildProcessEnvironmentPolicy.InheritCurrent);
        var facts = Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app");
        var first = AssertSuccess(Invocation.Resolve(facts), "first-resolution");
        callerArguments[0] = HostileCanary;
        callerArguments.Clear();
        var second = AssertSuccess(Invocation.Resolve(facts), "second-resolution");

        AssertDescriptor(first, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", "first-resolution");
        AssertDescriptor(second, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", "second-resolution");
        CollectionAssert.AreEqual(new[] { "/independent/fixture.dll", "--fixture" }, independentlyConstructed.Arguments.ToArray(), "caller-mutation-isolation");
    }

    [TestMethod]
    public void DeclaredFailureCategories_MapIndependentlyAndUnknownCategoryThrows()
    {
        var expectedCodes = new (WorkerCommandInvocationFailureCategory Category, string Code)[]
        {
            (WorkerCommandInvocationFailureCategory.RuntimeObservationFailure, "runtime-observation-failure"),
            (WorkerCommandInvocationFailureCategory.ProcessPathUnavailable, "process-path-unavailable"),
            (WorkerCommandInvocationFailureCategory.RequiredPathInvalid, "required-path-invalid"),
            (WorkerCommandInvocationFailureCategory.RequiredTargetMissing, "required-target-missing"),
            (WorkerCommandInvocationFailureCategory.RequiredTargetWrongType, "required-target-wrong-type"),
            (WorkerCommandInvocationFailureCategory.WorkingDirectoryUnavailable, "working-directory-unavailable"),
            (WorkerCommandInvocationFailureCategory.WorkingDirectoryInvalid, "working-directory-invalid"),
            (WorkerCommandInvocationFailureCategory.WorkingDirectoryMissing, "working-directory-missing"),
            (WorkerCommandInvocationFailureCategory.WorkingDirectoryWrongType, "working-directory-wrong-type"),
            (WorkerCommandInvocationFailureCategory.EntryApplicationIdentityMismatch, "entry-application-identity-mismatch"),
            (WorkerCommandInvocationFailureCategory.ApphostExecutableIdentityMismatch, "apphost-executable-identity-mismatch"),
            (WorkerCommandInvocationFailureCategory.UnsupportedLayout, "unsupported-layout"),
            (WorkerCommandInvocationFailureCategory.AmbiguousLayout, "ambiguous-layout")
        };

        foreach (var expected in expectedCodes)
        {
            Assert.AreEqual(expected.Code, WorkerCommandInvocationFailureDetails.Code(expected.Category), expected.Code);
        }

        var expectedFixedRemediation = new string(SafeRemediation.ToCharArray());
        Assert.AreEqual(WorkerCommandInvocationFailureDetails.Remediation, expectedFixedRemediation, "fixed-remediation-source");
        Assert.ThrowsExactly<InvalidOperationException>(
            () => WorkerCommandInvocationFailureDetails.Code((WorkerCommandInvocationFailureCategory)int.MaxValue),
            "unknown-failure-category");
    }

    public static IEnumerable<object[]> ApphostSuccessCases()
    {
        const string unixExecutable = "/srv/東京 app/#;$/ImmichReverseGeo.Web";
        const string unixEntry = "/srv/東京 app/#;$/ImmichReverseGeo.Web.dll";
        const string unixWorkingDirectory = "/srv/東京 app/#;$";
        yield return Row(new ApphostSuccessCase("unix-apphost-unicode-space", Facts(WorkerPathSemantics.Unix, unixExecutable, unixEntry, unixWorkingDirectory), unixExecutable, unixWorkingDirectory));

        const string windowsExecutable = "\\\\server\\Share\\東京 App\\[worker]\\IMMICHREVERSEGEO.WEB.EXE";
        const string windowsEntry = "C:/App Root/[worker]/IMMICHREVERSEGEO.WEB.DLL";
        const string windowsWorkingDirectory = "\\\\server\\Share\\東京 App\\[worker]";
        yield return Row(new ApphostSuccessCase("windows-unc-apphost-case-separators", Facts(WorkerPathSemantics.Windows, windowsExecutable, windowsEntry, windowsWorkingDirectory), windowsExecutable, windowsWorkingDirectory));
    }

    public static IEnumerable<object[]> FailureCases()
    {
        yield return Row(new FailureCase("observation-unavailable", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", processTarget: WorkerTargetObservation.Unavailable), WorkerCommandInvocationFailureCategory.RuntimeObservationFailure, "runtime-observation-failure", []));
        yield return Row(new FailureCase("null-process-path", Facts(WorkerPathSemantics.Unix, null, "/app/ImmichReverseGeo.Web.dll", "/app"), WorkerCommandInvocationFailureCategory.ProcessPathUnavailable, "process-path-unavailable", []));
        yield return Row(new FailureCase("empty-process-path", Facts(WorkerPathSemantics.Unix, string.Empty, "/app/ImmichReverseGeo.Web.dll", "/app"), WorkerCommandInvocationFailureCategory.ProcessPathUnavailable, "process-path-unavailable", []));
        yield return Row(new FailureCase("relative-process-path-hostile", Facts(WorkerPathSemantics.Unix, "relative/" + HostileCanary, "/app/ImmichReverseGeo.Web.dll", "/app"), WorkerCommandInvocationFailureCategory.RequiredPathInvalid, "required-path-invalid", [HostileCanary, "relative/"]));
        yield return Row(new FailureCase("missing-process-target", Facts(WorkerPathSemantics.Unix, "/missing/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", processTarget: WorkerTargetObservation.Missing), WorkerCommandInvocationFailureCategory.RequiredTargetMissing, "required-target-missing", ["/missing/dotnet"]));
        yield return Row(new FailureCase("process-target-directory", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", processTarget: WorkerTargetObservation.Directory), WorkerCommandInvocationFailureCategory.RequiredTargetWrongType, "required-target-wrong-type", []));
        yield return Row(new FailureCase("null-working-directory", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", null), WorkerCommandInvocationFailureCategory.WorkingDirectoryUnavailable, "working-directory-unavailable", []));
        yield return Row(new FailureCase("empty-working-directory", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", string.Empty), WorkerCommandInvocationFailureCategory.WorkingDirectoryUnavailable, "working-directory-unavailable", []));
        yield return Row(new FailureCase("relative-working-directory", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", "relative/cwd"), WorkerCommandInvocationFailureCategory.WorkingDirectoryInvalid, "working-directory-invalid", ["relative/cwd"]));
        yield return Row(new FailureCase("missing-working-directory", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", workingDirectoryTarget: WorkerTargetObservation.Missing), WorkerCommandInvocationFailureCategory.WorkingDirectoryMissing, "working-directory-missing", []));
        yield return Row(new FailureCase("working-directory-file", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", workingDirectoryTarget: WorkerTargetObservation.File), WorkerCommandInvocationFailureCategory.WorkingDirectoryWrongType, "working-directory-wrong-type", []));
        yield return Row(new FailureCase("known-web-identity-mismatch", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", knownIdentity: "Untrusted.Web"), WorkerCommandInvocationFailureCategory.EntryApplicationIdentityMismatch, "entry-application-identity-mismatch", ["Untrusted.Web"]));
        yield return Row(new FailureCase("actual-microsoft-testing-platform-entry-identity-mismatch", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", actualIdentity: "Microsoft.Testing.Platform"), WorkerCommandInvocationFailureCategory.EntryApplicationIdentityMismatch, "entry-application-identity-mismatch", ["Microsoft.Testing.Platform"]));
        yield return Row(new FailureCase("null-entry-location", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", null, "/app"), WorkerCommandInvocationFailureCategory.RequiredPathInvalid, "required-path-invalid", []));
        yield return Row(new FailureCase("apphost-null-entry-location", Facts(WorkerPathSemantics.Unix, "/app/ImmichReverseGeo.Web", null, "/app"), WorkerCommandInvocationFailureCategory.RequiredPathInvalid, "required-path-invalid", []));
        yield return Row(new FailureCase("apphost-empty-entry-location", Facts(WorkerPathSemantics.Unix, "/app/ImmichReverseGeo.Web", string.Empty, "/app"), WorkerCommandInvocationFailureCategory.RequiredPathInvalid, "required-path-invalid", []));
        yield return Row(new FailureCase("apphost-relative-entry-location", Facts(WorkerPathSemantics.Unix, "/app/ImmichReverseGeo.Web", "relative/ImmichReverseGeo.Web.dll", "/app"), WorkerCommandInvocationFailureCategory.RequiredPathInvalid, "required-path-invalid", ["relative/"]));
        yield return Row(new FailureCase("apphost-missing-entry-target", Facts(WorkerPathSemantics.Unix, "/app/ImmichReverseGeo.Web", "/app/ImmichReverseGeo.Web.dll", "/app", entryTarget: WorkerTargetObservation.Missing), WorkerCommandInvocationFailureCategory.RequiredTargetMissing, "required-target-missing", []));
        yield return Row(new FailureCase("apphost-entry-target-directory", Facts(WorkerPathSemantics.Unix, "/app/ImmichReverseGeo.Web", "/app/ImmichReverseGeo.Web.dll", "/app", entryTarget: WorkerTargetObservation.Directory), WorkerCommandInvocationFailureCategory.RequiredTargetWrongType, "required-target-wrong-type", []));
        yield return Row(new FailureCase("apphost-entry-filename-mismatch", Facts(WorkerPathSemantics.Unix, "/app/ImmichReverseGeo.Web", "/app/copied.dll", "/app"), WorkerCommandInvocationFailureCategory.EntryApplicationIdentityMismatch, "entry-application-identity-mismatch", ["/app/copied.dll"]));
        yield return Row(new FailureCase("apphost-actual-entry-identity-mismatch", Facts(WorkerPathSemantics.Unix, "/app/ImmichReverseGeo.Web", "/app/ImmichReverseGeo.Web.dll", "/app", actualIdentity: "Microsoft.Testing.Platform"), WorkerCommandInvocationFailureCategory.EntryApplicationIdentityMismatch, "entry-application-identity-mismatch", ["Microsoft.Testing.Platform"]));
        yield return Row(new FailureCase("entry-location-relative", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "relative/ImmichReverseGeo.Web.dll", "/app"), WorkerCommandInvocationFailureCategory.RequiredPathInvalid, "required-path-invalid", ["relative/"]));
        yield return Row(new FailureCase("entry-target-missing", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", entryTarget: WorkerTargetObservation.Missing), WorkerCommandInvocationFailureCategory.RequiredTargetMissing, "required-target-missing", []));
        yield return Row(new FailureCase("entry-target-directory", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", entryTarget: WorkerTargetObservation.Directory), WorkerCommandInvocationFailureCategory.RequiredTargetWrongType, "required-target-wrong-type", []));
        yield return Row(new FailureCase("entry-file-name-mismatch", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/copied.dll", "/app"), WorkerCommandInvocationFailureCategory.EntryApplicationIdentityMismatch, "entry-application-identity-mismatch", ["/app/copied.dll"]));
        yield return Row(new FailureCase("ambiguous-entry-observation", Facts(WorkerPathSemantics.Unix, "/usr/share/dotnet/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", entryTarget: WorkerTargetObservation.Ambiguous), WorkerCommandInvocationFailureCategory.AmbiguousLayout, "ambiguous-layout", []));
        yield return Row(new FailureCase("mtp-testhost-with-copied-nearby-web-dll", Facts(WorkerPathSemantics.Unix, "/tests/ImmichReverseGeo.Tests", "/tests/ImmichReverseGeo.Web.dll", "/tests", actualIdentity: "Microsoft.Testing.Platform"), WorkerCommandInvocationFailureCategory.EntryApplicationIdentityMismatch, "entry-application-identity-mismatch", ["ImmichReverseGeo.Tests", "Microsoft.Testing.Platform"]));
        yield return Row(new FailureCase("unix-dotnet-case-mismatch", Facts(WorkerPathSemantics.Unix, "/usr/bin/DotNet", "/app/ImmichReverseGeo.Web.dll", "/app"), WorkerCommandInvocationFailureCategory.UnsupportedLayout, "unsupported-layout", ["/usr/bin/DotNet"]));
        yield return Row(new FailureCase("unix-web-dll-case-mismatch", Facts(WorkerPathSemantics.Unix, "/usr/bin/dotnet", "/app/immichreversegeo.web.dll", "/app"), WorkerCommandInvocationFailureCategory.EntryApplicationIdentityMismatch, "entry-application-identity-mismatch", ["/app/immichreversegeo.web.dll"]));
        yield return Row(new FailureCase("unix-apphost-case-only-candidate", Facts(WorkerPathSemantics.Unix, "/app/immichreversegeo.web", "/app/ImmichReverseGeo.Web.dll", "/app"), WorkerCommandInvocationFailureCategory.ApphostExecutableIdentityMismatch, "apphost-executable-identity-mismatch", ["/app/immichreversegeo.web"]));
        yield return Row(new FailureCase("unix-dotnet-exe-wrong-platform-mode", Facts(WorkerPathSemantics.Unix, "/usr/bin/dotnet.exe", "/app/ImmichReverseGeo.Web.dll", "/app"), WorkerCommandInvocationFailureCategory.UnsupportedLayout, "unsupported-layout", ["/usr/bin/dotnet.exe"]));
        yield return Row(new FailureCase("windows-dotnet-without-exe-wrong-platform-mode", Facts(WorkerPathSemantics.Windows, "C:\\dotnet\\dotnet", "C:\\app\\ImmichReverseGeo.Web.dll", "C:\\app"), WorkerCommandInvocationFailureCategory.UnsupportedLayout, "unsupported-layout", ["C:\\dotnet\\dotnet"]));
        yield return Row(new FailureCase("unix-apphost-exe-wrong-platform-extension", Facts(WorkerPathSemantics.Unix, "/app/ImmichReverseGeo.Web.exe", "/app/ImmichReverseGeo.Web.dll", "/app"), WorkerCommandInvocationFailureCategory.ApphostExecutableIdentityMismatch, "apphost-executable-identity-mismatch", ["/app/ImmichReverseGeo.Web.exe"]));
        yield return Row(new FailureCase("windows-apphost-without-exe", Facts(WorkerPathSemantics.Windows, "C:\\app\\ImmichReverseGeo.Web", "C:\\app\\ImmichReverseGeo.Web.dll", "C:\\app"), WorkerCommandInvocationFailureCategory.ApphostExecutableIdentityMismatch, "apphost-executable-identity-mismatch", ["C:\\app\\ImmichReverseGeo.Web"]));
        yield return Row(new FailureCase("sibling-dll-and-apphost-bait-not-scanned", Facts(WorkerPathSemantics.Unix, "/tests/runner", "/tests/ImmichReverseGeo.Web.dll", "/tests"), WorkerCommandInvocationFailureCategory.UnsupportedLayout, "unsupported-layout", ["/tests/runner"]));
    }

    public static IEnumerable<object[]> PrecedenceCases()
    {
        yield return Row(new PrecedenceCase("runtime-before-process-availability", Facts(WorkerPathSemantics.Unix, null, "/app/ImmichReverseGeo.Web.dll", "/app", entryTarget: WorkerTargetObservation.Unavailable), WorkerCommandInvocationFailureCategory.RuntimeObservationFailure, "runtime-observation-failure"));
        yield return Row(new PrecedenceCase("process-availability-before-process-invalid", Facts(WorkerPathSemantics.Unix, null, "/app/ImmichReverseGeo.Web.dll", "relative/cwd"), WorkerCommandInvocationFailureCategory.ProcessPathUnavailable, "process-path-unavailable"));
        yield return Row(new PrecedenceCase("process-invalid-before-process-missing", Facts(WorkerPathSemantics.Unix, "relative/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", processTarget: WorkerTargetObservation.Missing), WorkerCommandInvocationFailureCategory.RequiredPathInvalid, "required-path-invalid"));
        yield return Row(new PrecedenceCase("process-missing-before-process-type", Facts(WorkerPathSemantics.Unix, "/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", processTarget: WorkerTargetObservation.Missing, workingDirectoryTarget: WorkerTargetObservation.File), WorkerCommandInvocationFailureCategory.RequiredTargetMissing, "required-target-missing"));
        yield return Row(new PrecedenceCase("process-type-before-cwd-availability", Facts(WorkerPathSemantics.Unix, "/dotnet", "/app/ImmichReverseGeo.Web.dll", null, processTarget: WorkerTargetObservation.Directory), WorkerCommandInvocationFailureCategory.RequiredTargetWrongType, "required-target-wrong-type"));
        yield return Row(new PrecedenceCase("cwd-availability-before-cwd-invalid", Facts(WorkerPathSemantics.Unix, "/dotnet", "/app/ImmichReverseGeo.Web.dll", null), WorkerCommandInvocationFailureCategory.WorkingDirectoryUnavailable, "working-directory-unavailable"));
        yield return Row(new PrecedenceCase("cwd-invalid-before-cwd-missing", Facts(WorkerPathSemantics.Unix, "/dotnet", "/app/ImmichReverseGeo.Web.dll", "relative/cwd", workingDirectoryTarget: WorkerTargetObservation.Missing), WorkerCommandInvocationFailureCategory.WorkingDirectoryInvalid, "working-directory-invalid"));
        yield return Row(new PrecedenceCase("cwd-missing-before-cwd-type", Facts(WorkerPathSemantics.Unix, "/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", workingDirectoryTarget: WorkerTargetObservation.Missing, actualIdentity: "ImmichReverseGeo.Tests"), WorkerCommandInvocationFailureCategory.WorkingDirectoryMissing, "working-directory-missing"));
        yield return Row(new PrecedenceCase("cwd-type-before-entry-identity", Facts(WorkerPathSemantics.Unix, "/dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", workingDirectoryTarget: WorkerTargetObservation.File, actualIdentity: "ImmichReverseGeo.Tests"), WorkerCommandInvocationFailureCategory.WorkingDirectoryWrongType, "working-directory-wrong-type"));
        yield return Row(new PrecedenceCase("entry-identity-before-entry-path", Facts(WorkerPathSemantics.Unix, "/dotnet", "relative/ImmichReverseGeo.Web.dll", "/app", actualIdentity: "ImmichReverseGeo.Tests"), WorkerCommandInvocationFailureCategory.EntryApplicationIdentityMismatch, "entry-application-identity-mismatch"));
        yield return Row(new PrecedenceCase("entry-path-before-entry-target", Facts(WorkerPathSemantics.Unix, "/dotnet", "relative/ImmichReverseGeo.Web.dll", "/app", entryTarget: WorkerTargetObservation.Missing), WorkerCommandInvocationFailureCategory.RequiredPathInvalid, "required-path-invalid"));
        yield return Row(new PrecedenceCase("entry-target-before-entry-name", Facts(WorkerPathSemantics.Unix, "/dotnet", "/app/copied.dll", "/app", entryTarget: WorkerTargetObservation.Missing), WorkerCommandInvocationFailureCategory.RequiredTargetMissing, "required-target-missing"));
        yield return Row(new PrecedenceCase("entry-target-type-before-entry-name", Facts(WorkerPathSemantics.Unix, "/dotnet", "/app/copied.dll", "/app", entryTarget: WorkerTargetObservation.Directory), WorkerCommandInvocationFailureCategory.RequiredTargetWrongType, "required-target-wrong-type"));
        yield return Row(new PrecedenceCase("entry-name-before-ambiguity", Facts(WorkerPathSemantics.Unix, "/dotnet", "/app/copied.dll", "/app", processTarget: WorkerTargetObservation.Ambiguous), WorkerCommandInvocationFailureCategory.EntryApplicationIdentityMismatch, "entry-application-identity-mismatch"));
        yield return Row(new PrecedenceCase("ambiguity-before-unsupported-layout", Facts(WorkerPathSemantics.Unix, "/app/not-dotnet", "/app/ImmichReverseGeo.Web.dll", "/app", entryTarget: WorkerTargetObservation.Ambiguous), WorkerCommandInvocationFailureCategory.AmbiguousLayout, "ambiguous-layout"));
    }

    private static object[] Row<T>(T testCase)
    {
        return [testCase!];
    }

    private static WorkerCommandRuntimeFacts Facts(
        WorkerPathSemantics semantics,
        string? processPath,
        string? entryPath,
        string? workingDirectory,
        string? knownIdentity = WebIdentity,
        string? actualIdentity = WebIdentity,
        WorkerTargetObservation? processTarget = null,
        WorkerTargetObservation? entryTarget = null,
        WorkerTargetObservation? workingDirectoryTarget = null)
    {
        return new WorkerCommandRuntimeFacts(
            knownIdentity,
            processPath,
            processTarget ?? WorkerTargetObservation.File,
            actualIdentity,
            entryPath,
            entryTarget ?? WorkerTargetObservation.File,
            workingDirectory,
            workingDirectoryTarget ?? WorkerTargetObservation.Directory,
            semantics);
    }

    private static ChildProcessStartDescriptor AssertSuccess(WorkerCommandInvocationResolution resolution, string label)
    {
        var success = Assert.IsInstanceOfType<WorkerCommandInvocationResolution.Success>(resolution, label);
        Assert.IsFalse(resolution is WorkerCommandInvocationResolution.Failure, label);
        return success.Invocation.Descriptor;
    }

    private static WorkerCommandInvocationResolution.Failure AssertFailure(WorkerCommandInvocationResolution resolution, string label)
    {
        var failure = Assert.IsInstanceOfType<WorkerCommandInvocationResolution.Failure>(resolution, label);
        Assert.IsFalse(resolution is WorkerCommandInvocationResolution.Success, label);
        return failure;
    }

    private static void AssertDescriptor(ChildProcessStartDescriptor descriptor, string executable, string assembly, string workingDirectory, string label)
    {
        Assert.AreEqual(executable, descriptor.ExecutablePath, label + "-executable");
        Assert.AreEqual(2, descriptor.Arguments.Count, label + "-argument-count");
        CollectionAssert.AreEqual(new[] { assembly, Token }, descriptor.Arguments.ToArray(), label + "-argument-order");
        Assert.AreEqual(Token, descriptor.Arguments[^1], label + "-token-final");
        Assert.AreEqual(1, descriptor.Arguments.Count(argument => string.Equals(argument, Token, StringComparison.Ordinal)), label + "-token-once");
        Assert.AreEqual(workingDirectory, descriptor.WorkingDirectory, label + "-working-directory");
        Assert.AreEqual(ChildProcessEnvironmentPolicy.InheritCurrentAndRemoveReservedProtocolVersion, descriptor.EnvironmentPolicy, label + "-environment-policy");
        Assert.IsTrue(descriptor.RedirectStandardInput, label + "-stdin");
        Assert.IsTrue(descriptor.RedirectStandardOutput, label + "-stdout");
        Assert.IsTrue(descriptor.RedirectStandardError, label + "-stderr");
        Assert.IsFalse(descriptor.UseShellExecute, label + "-shell");
        Assert.IsTrue(descriptor.CreateNoWindow, label + "-window");
        Assert.AreEqual(SafeDescriptorText, descriptor.ToString(), label + "-safe-text");
    }

    private static void AssertApphostDescriptor(ChildProcessStartDescriptor descriptor, string executable, string workingDirectory, string label)
    {
        Assert.AreEqual(executable, descriptor.ExecutablePath, label + "-executable");
        Assert.AreEqual(1, descriptor.Arguments.Count, label + "-argument-count");
        CollectionAssert.AreEqual(new[] { Token }, descriptor.Arguments.ToArray(), label + "-argument-order");
        Assert.AreEqual(Token, descriptor.Arguments[^1], label + "-token-final");
        Assert.AreEqual(1, descriptor.Arguments.Count(argument => string.Equals(argument, Token, StringComparison.Ordinal)), label + "-token-once");
        Assert.AreEqual(workingDirectory, descriptor.WorkingDirectory, label + "-working-directory");
        Assert.AreEqual(ChildProcessEnvironmentPolicy.InheritCurrentAndRemoveReservedProtocolVersion, descriptor.EnvironmentPolicy, label + "-environment-policy");
        Assert.IsTrue(descriptor.RedirectStandardInput, label + "-stdin");
        Assert.IsTrue(descriptor.RedirectStandardOutput, label + "-stdout");
        Assert.IsTrue(descriptor.RedirectStandardError, label + "-stderr");
        Assert.IsFalse(descriptor.UseShellExecute, label + "-shell");
        Assert.IsTrue(descriptor.CreateNoWindow, label + "-window");
        Assert.AreEqual(SafeDescriptorText, descriptor.ToString(), label + "-safe-text");
    }

    private static void AssertSafeFailureRepresentations(WorkerCommandInvocationResolution.Failure failure, string label, IReadOnlyList<string> sensitiveValues)
    {
        var representations = new[] { failure.ToString(), failure.Code, failure.Remediation };
        foreach (var representation in representations)
        {
            Assert.IsFalse(representation.Contains(HostileCanary, StringComparison.Ordinal), label + "-hostile-redaction");
            Assert.IsFalse(representation.Contains("System.InvalidOperationException", StringComparison.Ordinal), label + "-exception-redaction");
            Assert.IsFalse(representation.Contains("StackTrace", StringComparison.Ordinal), label + "-stack-redaction");
            Assert.IsFalse(representation.Contains(Token, StringComparison.Ordinal), label + "-argument-redaction");
            foreach (var sensitiveValue in sensitiveValues)
            {
                Assert.IsFalse(representation.Contains(sensitiveValue, StringComparison.Ordinal), label + "-raw-redaction");
            }
        }
    }

    private sealed record ApphostSuccessCase(
        string Label,
        WorkerCommandRuntimeFacts Facts,
        string Executable,
        string WorkingDirectory)
    {
        public override string ToString() => Label;
    }

    private sealed record FailureCase(
        string Label,
        WorkerCommandRuntimeFacts Facts,
        WorkerCommandInvocationFailureCategory Category,
        string Code,
        IReadOnlyList<string> RawSensitiveValues)
    {
        public override string ToString() => Label;
    }

    private sealed record PrecedenceCase(
        string Label,
        WorkerCommandRuntimeFacts Facts,
        WorkerCommandInvocationFailureCategory Category,
        string Code)
    {
        public override string ToString() => Label;
    }
}
