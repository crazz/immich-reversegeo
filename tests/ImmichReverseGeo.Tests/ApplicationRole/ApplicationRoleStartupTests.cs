using System.IO;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.ApplicationRole;
using ImmichReverseGeo.Web.WorkerHost;
using PublicRole = ImmichReverseGeo.Core.ApplicationRole.PublicApplicationRole;

namespace ImmichReverseGeo.Tests.ApplicationRole;

[TestClass]
public sealed class ApplicationRoleStartupTests
{
    public static IEnumerable<object[]> ReservedPrivateFailureCases()
    {
        yield return [new[] { "--internal-worker=malformed" }, "invalid-internal-worker-syntax"];
        yield return [new[] { "--internal-worker", "--internal-worker" }, "duplicate-internal-worker-selector"];
        yield return [new[] { "--internal-worker", "--help" }, "unexpected-internal-worker-argument"];
    }

    [TestMethod]
    [TestCategory("Change23")]
    public void DefaultOperation_InvalidSelection_WritesSafeDiagnosticSetsExitTwoOnceAndDoesNotEnterWebContinuation()
    {
        using var errorWriter = new StringWriter();
        var exitCodes = new List<int>();

        ApplicationRoleStartup.Begin(
            ["--internal-worker=credential-4912"],
            errorWriter,
            ThrowIfWebContinuationIsReached,
            exitCodes.Add);

        CollectionAssert.AreEqual(new[] { 2 }, exitCodes);
        Assert.AreEqual(
            $"Application role selection failed: invalid-internal-worker-syntax. Supported private syntax: --internal-worker.{Environment.NewLine}",
            errorWriter.ToString());
    }

    [TestMethod]
    [TestCategory("Change23")]
    public void InvalidSelection_OrdinaryDiagnosticPrecedesExactlyOneTopLevelFinalSummary()
    {
        using var errorWriter = new StringWriter();
        var exitCodes = new List<int>();

        ApplicationRoleStartup.Begin(
            ["--internal-worker=RAW_ARGUMENT_SENTINEL"],
            errorWriter,
            ThrowIfWebContinuationIsReached,
            ThrowIfWebContinuationIsReached,
            _ => exitCodes.Add(InternalWorkerProcess.CompleteInvalidInvocation(errorWriter)));

        CollectionAssert.AreEqual(new[] { 2 }, exitCodes, "invalid-boundary-exit-code");
        var stderr = errorWriter.ToString();
        Assert.IsTrue(stderr.StartsWith("Application role selection failed:", StringComparison.Ordinal), "invalid-role-log-first");
        Assert.AreEqual(1, stderr.Split(WorkerProcessExitDiagnostic.FinalSummaryMarker, StringSplitOptions.None).Length - 1, "invalid-summary-once");
        Assert.IsTrue(stderr.EndsWith(
            WorkerProcessExitFact.InputInvalid().Diagnostic.FormatFinalSummary() + Environment.NewLine,
            StringComparison.Ordinal),
            "invalid-summary-last");
        Assert.IsFalse(stderr.Contains("RAW_ARGUMENT_SENTINEL", StringComparison.Ordinal), "invalid-summary-does-not-echo-raw-argument");
    }

    [TestMethod]
    public void DefaultOperation_ExactInternalWorker_DoesNotEnterWebContinuationOrSetExitCode()
    {
        using var errorWriter = new StringWriter();
        var exitCodes = new List<int>();

        ApplicationRoleStartup.Begin(
            ["--internal-worker"],
            errorWriter,
            ThrowIfWebContinuationIsReached,
            exitCodes.Add);

        CollectionAssert.AreEqual(Array.Empty<int>(), exitCodes);
        Assert.AreEqual(string.Empty, errorWriter.ToString());
    }

    [TestMethod]
    public void DualContinuationOperation_InvalidSelection_InvokesNeitherContinuation()
    {
        using var errorWriter = new StringWriter();
        var exitCodes = new List<int>();

        ApplicationRoleStartup.Begin(
            ["--internal-worker=invalid"],
            errorWriter,
            ThrowIfWebContinuationIsReached,
            ThrowIfWebContinuationIsReached,
            exitCodes.Add);

        CollectionAssert.AreEqual(new[] { 2 }, exitCodes, "dual-invalid-exit-code");
    }

    [TestMethod]
    public void DualContinuationOperation_TypedRunOnce_InvokesNeitherContinuation()
    {
        using var errorWriter = new StringWriter();
        var exitCodes = new List<int>();

        ApplicationRoleStartup.Begin(
            [],
            PublicRole.RunOnce,
            errorWriter,
            ThrowIfWebContinuationIsReached,
            ThrowIfWebContinuationIsReached,
            exitCodes.Add);

        CollectionAssert.AreEqual(Array.Empty<int>(), exitCodes, "dual-runonce-exit-code");
        Assert.AreEqual(string.Empty, errorWriter.ToString(), "dual-runonce-diagnostic");
    }

    [TestMethod]
    public void InternalWorkerOperation_ExactSelector_InvokesWorkerOnceWithNoForwardedArguments()
    {
        using var errorWriter = new StringWriter();
        var exitCodes = new List<int>();
        var workerCalls = 0;
        IReadOnlyList<string>? workerArguments = null;

        ApplicationRoleStartup.Begin(
            ["--internal-worker"],
            errorWriter,
            ThrowIfWebContinuationIsReached,
            arguments =>
            {
                workerCalls++;
                workerArguments = arguments;
            },
            exitCodes.Add);

        Assert.AreEqual(1, workerCalls, "internal-worker-call-count");
        CollectionAssert.AreEqual(Array.Empty<string>(), workerArguments?.ToArray(), "internal-worker-forwarded-arguments");
        CollectionAssert.AreEqual(Array.Empty<int>(), exitCodes, "internal-worker-exit-codes");
        Assert.AreEqual(string.Empty, errorWriter.ToString(), "internal-worker-diagnostic");
    }

    [TestMethod]
    public void TypedCandidateOperation_RunOnce_DoesNotEnterWebContinuationOrSetExitCode()
    {
        using var errorWriter = new StringWriter();
        var exitCodes = new List<int>();

        ApplicationRoleStartup.Begin(
            [],
            PublicRole.RunOnce,
            errorWriter,
            ThrowIfWebContinuationIsReached,
            exitCodes.Add);

        CollectionAssert.AreEqual(Array.Empty<int>(), exitCodes);
        Assert.AreEqual(string.Empty, errorWriter.ToString());
    }

    [TestMethod]
    public void DefaultOperation_NoArguments_InvokesWebContinuationOnceWithEmptyArgumentsAndNoExitCode()
    {
        using var errorWriter = new StringWriter();
        var exitCodes = new List<int>();
        var invocationCount = 0;
        string[]? receivedArguments = null;

        ApplicationRoleStartup.Begin(
            [],
            errorWriter,
            arguments =>
            {
                invocationCount++;
                receivedArguments = arguments.ToArray();
            },
            ThrowIfWebContinuationIsReached,
            exitCodes.Add);

        Assert.AreEqual(1, invocationCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), receivedArguments);
        CollectionAssert.AreEqual(Array.Empty<int>(), exitCodes);
        Assert.AreEqual(string.Empty, errorWriter.ToString());
    }

    [TestMethod]
    public void DefaultOperation_OrdinaryArguments_InvokesWebContinuationOnceUnchangedInOrderAndNoExitCode()
    {
        using var errorWriter = new StringWriter();
        var exitCodes = new List<int>();
        var invocationCount = 0;
        string[]? receivedArguments = null;

        ApplicationRoleStartup.Begin(
            ["--urls", "http://127.0.0.1:5122", "--help", "--urls", "http://127.0.0.1:5123"],
            errorWriter,
            arguments =>
            {
                invocationCount++;
                receivedArguments = arguments.ToArray();
            },
            ThrowIfWebContinuationIsReached,
            exitCodes.Add);

        Assert.AreEqual(1, invocationCount);
        CollectionAssert.AreEqual(
            new[] { "--urls", "http://127.0.0.1:5122", "--help", "--urls", "http://127.0.0.1:5123" },
            receivedArguments);
        CollectionAssert.AreEqual(Array.Empty<int>(), exitCodes);
        Assert.AreEqual(string.Empty, errorWriter.ToString());
    }

    [TestMethod]
    public void ModeAwareOperation_PublicModesReadOnceMapToTheExistingTypedCandidatesAndPreserveArguments()
    {
        var cases = new[]
        {
            ("standard", DeploymentMode.Standard, true),
            ("web-only", DeploymentMode.WebOnly, true),
            ("run-once", DeploymentMode.RunOnce, false)
        };

        foreach (var (value, expectedMode, expectWeb) in cases)
        {
            using var errorWriter = new StringWriter();
            var readCount = 0;
            var webCalls = 0;
            var workerCalls = 0;
            var runOnceCalls = 0;
            DeploymentMode? selectedMode = null;
            IReadOnlyList<string>? selectedArguments = null;
            var privateExitCodes = new List<int>();
            var modeExitCodes = new List<int>();

            ApplicationRoleStartup.Begin(
                ["--urls", "http://127.0.0.1:5122", "--help"],
                name =>
                {
                    readCount++;
                    Assert.AreEqual(DeploymentModeResolver.EnvironmentVariableName, name, value);
                    return value;
                },
                errorWriter,
                (mode, arguments) =>
                {
                    webCalls++;
                    selectedMode = mode;
                    selectedArguments = arguments;
                },
                _ => workerCalls++,
                (mode, arguments) =>
                {
                    runOnceCalls++;
                    selectedMode = mode;
                    selectedArguments = arguments;
                },
                privateExitCodes.Add,
                modeExitCodes.Add);

            Assert.AreEqual(1, readCount, value + "-read-count");
            Assert.AreSame(expectedMode, selectedMode, value + "-mode");
            CollectionAssert.AreEqual(
                new[] { "--urls", "http://127.0.0.1:5122", "--help" },
                selectedArguments?.ToArray(),
                value + "-arguments");
            Assert.AreEqual(expectWeb ? 1 : 0, webCalls, value + "-web-calls");
            Assert.AreEqual(expectWeb ? 0 : 1, runOnceCalls, value + "-run-once-calls");
            Assert.AreEqual(0, workerCalls, value + "-worker-calls");
            CollectionAssert.AreEqual(Array.Empty<int>(), privateExitCodes, value + "-private-exit");
            CollectionAssert.AreEqual(Array.Empty<int>(), modeExitCodes, value + "-mode-exit");
            Assert.AreEqual(string.Empty, errorWriter.ToString(), value + "-diagnostic");
        }
    }

    [TestMethod]
    public void ModeAwareOperation_MissingModeDefaultsToStandardAndRetainsTheResolvedSnapshot()
    {
        using var errorWriter = new StringWriter();
        var readCount = 0;
        DeploymentMode? selectedMode = null;

        ApplicationRoleStartup.Begin(
            [],
            name =>
            {
                readCount++;
                Assert.AreEqual(DeploymentModeResolver.EnvironmentVariableName, name);
                return null;
            },
            errorWriter,
            (mode, _) => selectedMode = mode,
            ThrowIfWebContinuationIsReached,
            (_, _) => ThrowIfWebContinuationIsReached([]),
            ThrowIfExitCodeIsSet,
            ThrowIfExitCodeIsSet);

        Assert.AreEqual(1, readCount);
        Assert.AreSame(DeploymentMode.Standard, selectedMode);
        Assert.AreEqual(string.Empty, errorWriter.ToString());
    }

    [TestMethod]
    public void ModeAwareOperation_InvalidPublicModeFailsBeforeAnyHostContinuationWithoutLeakingTheValue()
    {
        const string canary = "mode-secret-4912";
        using var errorWriter = new StringWriter();
        var readCount = 0;
        var webHostStarts = 0;
        var workerHostStarts = 0;
        var runOnceHostStarts = 0;
        var privateExitCodes = new List<int>();
        var modeExitCodes = new List<int>();

        ApplicationRoleStartup.Begin(
            ["--urls", "http://127.0.0.1:5122"],
            _ =>
            {
                readCount++;
                return canary;
            },
            errorWriter,
            (_, _) => webHostStarts++,
            _ => workerHostStarts++,
            (_, _) => runOnceHostStarts++,
            privateExitCodes.Add,
            modeExitCodes.Add);

        Assert.AreEqual(1, readCount, "mode-read-count");
        Assert.AreEqual(0, webHostStarts, "web-builder-di-logging-path");
        Assert.AreEqual(0, workerHostStarts, "worker-host-path");
        Assert.AreEqual(0, runOnceHostStarts, "run-once-host-path");
        CollectionAssert.AreEqual(Array.Empty<int>(), privateExitCodes, "private-exit-codes");
        CollectionAssert.AreEqual(new[] { 2 }, modeExitCodes, "mode-exit-codes");
        Assert.AreEqual(DeploymentModeResolver.InvalidModeDiagnostic + Environment.NewLine, errorWriter.ToString(), "single-constant-diagnostic");
        Assert.IsFalse(errorWriter.ToString().Contains(canary, StringComparison.Ordinal), "canary-redaction");
    }

    [TestMethod]
    public void ModeAwareOperation_ValidInternalWorkerBypassesAnInvalidModeSource()
    {
        using var errorWriter = new StringWriter();
        var readCount = 0;
        var workerCalls = 0;
        IReadOnlyList<string>? workerArguments = null;

        ApplicationRoleStartup.Begin(
            ["--internal-worker"],
            _ =>
            {
                readCount++;
                return "mode-secret-4912";
            },
            errorWriter,
            (_, _) => ThrowIfWebContinuationIsReached([]),
            arguments =>
            {
                workerCalls++;
                workerArguments = arguments;
            },
            (_, _) => ThrowIfWebContinuationIsReached([]),
            ThrowIfExitCodeIsSet,
            ThrowIfExitCodeIsSet);

        Assert.AreEqual(0, readCount, "mode-read-count");
        Assert.AreEqual(1, workerCalls, "worker-calls");
        CollectionAssert.AreEqual(Array.Empty<string>(), workerArguments?.ToArray(), "worker-arguments");
        Assert.AreEqual(string.Empty, errorWriter.ToString(), "diagnostic");
    }

    [TestMethod]
    [DynamicData(nameof(ReservedPrivateFailureCases))]
    public void ModeAwareOperation_ReservedPrivateFailureWinsBeforeAnInvalidModeRead(
        string[] arguments,
        string expectedCategory)
    {
        const string canary = "mode-secret-4912";
        using var errorWriter = new StringWriter();
        var readCount = 0;
        var privateExitCodes = new List<int>();
        var modeExitCodes = new List<int>();
        var hostStarts = 0;

        ApplicationRoleStartup.Begin(
            arguments,
            _ =>
            {
                readCount++;
                return canary;
            },
            errorWriter,
            (_, _) => hostStarts++,
            _ => hostStarts++,
            (_, _) => hostStarts++,
            privateExitCodes.Add,
            modeExitCodes.Add);

        Assert.AreEqual(0, readCount, "mode-read-count");
        Assert.AreEqual(0, hostStarts, "host-starts");
        CollectionAssert.AreEqual(new[] { 2 }, privateExitCodes, "private-exit-codes");
        CollectionAssert.AreEqual(Array.Empty<int>(), modeExitCodes, "mode-exit-codes");
        Assert.AreEqual(
            $"Application role selection failed: {expectedCategory}. Supported private syntax: --internal-worker.{Environment.NewLine}",
            errorWriter.ToString(),
            "private-diagnostic");
        Assert.IsFalse(errorWriter.ToString().Contains(canary, StringComparison.Ordinal), "canary-redaction");
    }

    private static void ThrowIfWebContinuationIsReached(IReadOnlyList<string> arguments)
    {
        throw new AssertFailedException($"The Web continuation must not be reached: {string.Join(",", arguments)}");
    }

    private static void ThrowIfExitCodeIsSet(int exitCode)
    {
        throw new AssertFailedException($"An exit code must not be set: {exitCode}");
    }
}
