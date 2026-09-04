using System.IO;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.ApplicationRole;
using ImmichReverseGeo.Web.WorkerHost;
using PublicRole = ImmichReverseGeo.Core.ApplicationRole.PublicApplicationRole;

namespace ImmichReverseGeo.Tests.ApplicationRole;

[TestClass]
public sealed class ApplicationRoleStartupTests
{
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

    private static void ThrowIfWebContinuationIsReached(IReadOnlyList<string> arguments)
    {
        throw new AssertFailedException($"The Web continuation must not be reached: {string.Join(",", arguments)}");
    }
}
