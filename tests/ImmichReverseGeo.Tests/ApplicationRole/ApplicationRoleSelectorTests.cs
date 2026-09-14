using ImmichReverseGeo.Core.ApplicationRole;
using Role = ImmichReverseGeo.Core.ApplicationRole.ApplicationRole;
using PublicRole = ImmichReverseGeo.Core.ApplicationRole.PublicApplicationRole;

namespace ImmichReverseGeo.Tests.ApplicationRole;

[TestClass]
public sealed class ApplicationRoleSelectorTests
{
    private const string HostileAssignmentValue = "Server=db.example;User Id=app;Password=credential-4912;IMMICH_REVERSEGEO_MODE=environment-secret;System.InvalidOperationException at StackTrace";
    private const int MaximumDiagnosticLength = 160;

    public static IEnumerable<object[]> SuccessCases()
    {
        yield return [new SuccessCase("default-empty-web", [], PublicRole.Web, Role.Web, [], [])];
        yield return [new SuccessCase("default-unrelated-one-web", ["--urls"], PublicRole.Web, Role.Web, ["--urls"], ["--urls"])];
        yield return [new SuccessCase("default-unrelated-many-web", ["--urls", "http://127.0.0.1:5122", "--feature:enabled"], PublicRole.Web, Role.Web, ["--urls", "http://127.0.0.1:5122", "--feature:enabled"], ["--urls", "http://127.0.0.1:5122", "--feature:enabled"])];
        yield return [new SuccessCase("run-once-unrelated-one", ["--environment"], PublicRole.RunOnce, Role.RunOnce, ["--environment"], ["--environment"])];
        yield return [new SuccessCase("run-once-unrelated-many", ["--urls", "http://127.0.0.1:5122", "--feature:enabled"], PublicRole.RunOnce, Role.RunOnce, ["--urls", "http://127.0.0.1:5122", "--feature:enabled"], ["--urls", "http://127.0.0.1:5122", "--feature:enabled"])];
        yield return [new SuccessCase("duplicate-ordinary-aspnet-options", ["--urls", "http://one", "--urls", "http://two"], PublicRole.Web, Role.Web, ["--urls", "http://one", "--urls", "http://two"], ["--urls", "http://one", "--urls", "http://two"])];
        yield return [new SuccessCase("missing-value-ordinary-aspnet-option", ["--urls"], PublicRole.Web, Role.Web, ["--urls"], ["--urls"])];
        yield return [new SuccessCase("help-long-preserved", ["--help"], PublicRole.Web, Role.Web, ["--help"], ["--help"])];
        yield return [new SuccessCase("help-short-preserved", ["-h"], PublicRole.Web, Role.Web, ["-h"], ["-h"])];
        yield return [new SuccessCase("version-long-preserved", ["--version"], PublicRole.Web, Role.Web, ["--version"], ["--version"])];
        yield return [new SuccessCase("version-short-preserved", ["-v"], PublicRole.Web, Role.Web, ["-v"], ["-v"])];
        yield return [new SuccessCase("positional-run-once-preserved", ["run-once"], PublicRole.Web, Role.Web, ["run-once"], ["run-once"])];
        yield return [new SuccessCase("positional-web-preserved", ["web"], PublicRole.Web, Role.Web, ["web"], ["web"])];
        yield return [new SuccessCase("positional-role-option-preserved", ["--role", "run-once"], PublicRole.Web, Role.Web, ["--role", "run-once"], ["--role", "run-once"])];
        yield return [new SuccessCase("non-reserved-lookalike-prefixes-preserved", ["--internal-workerish", "--internal-worker-", "--internal-workerish=value"], PublicRole.Web, Role.Web, ["--internal-workerish", "--internal-worker-", "--internal-workerish=value"], ["--internal-workerish", "--internal-worker-", "--internal-workerish=value"])];
        yield return [new SuccessCase("sole-exact-private-selector-over-web", ["--internal-worker"], PublicRole.Web, Role.InternalWorker, [], ["--internal-worker"])];
        yield return [new SuccessCase("sole-exact-private-selector-over-run-once", ["--internal-worker"], PublicRole.RunOnce, Role.InternalWorker, [], ["--internal-worker"])];
    }

    public static IEnumerable<object[]> InvalidCases()
    {
        yield return [new InvalidCase("uppercase-case-variant", ["--INTERNAL-WORKER"], "invalid-internal-worker-syntax", "Application role selection failed: invalid-internal-worker-syntax. Supported private syntax: --internal-worker.", ["--INTERNAL-WORKER"])];
        yield return [new InvalidCase("mixed-case-variant", ["--Internal-Worker"], "invalid-internal-worker-syntax", "Application role selection failed: invalid-internal-worker-syntax. Supported private syntax: --internal-worker.", ["--Internal-Worker"])];
        yield return [new InvalidCase("alternate-mixed-case-variant", ["--internal-Worker"], "invalid-internal-worker-syntax", "Application role selection failed: invalid-internal-worker-syntax. Supported private syntax: --internal-worker.", ["--internal-Worker"])];
        yield return [new InvalidCase("empty-assignment", ["--internal-worker="], "invalid-internal-worker-syntax", "Application role selection failed: invalid-internal-worker-syntax. Supported private syntax: --internal-worker.", ["--internal-worker="])];
        yield return [new InvalidCase("hostile-nonempty-assignment", [$"--internal-worker={HostileAssignmentValue}"], "invalid-internal-worker-syntax", "Application role selection failed: invalid-internal-worker-syntax. Supported private syntax: --internal-worker.", [$"--internal-worker={HostileAssignmentValue}"])];
        yield return [new InvalidCase("case-varied-assignment-prefix", ["--INTERNAL-WORKER=value"], "invalid-internal-worker-syntax", "Application role selection failed: invalid-internal-worker-syntax. Supported private syntax: --internal-worker.", ["--INTERNAL-WORKER=value"])];
        yield return [new InvalidCase("exact-selector-before-extra", ["--internal-worker", "--urls"], "unexpected-internal-worker-argument", "Application role selection failed: unexpected-internal-worker-argument. Supported private syntax: --internal-worker.", ["--internal-worker", "--urls"])];
        yield return [new InvalidCase("exact-selector-after-extra", ["--urls", "--internal-worker"], "unexpected-internal-worker-argument", "Application role selection failed: unexpected-internal-worker-argument. Supported private syntax: --internal-worker.", ["--urls", "--internal-worker"])];
        yield return [new InvalidCase("exact-selector-plus-long-help", ["--internal-worker", "--help"], "unexpected-internal-worker-argument", "Application role selection failed: unexpected-internal-worker-argument. Supported private syntax: --internal-worker.", ["--internal-worker", "--help"])];
        yield return [new InvalidCase("exact-selector-plus-short-help", ["--internal-worker", "-h"], "unexpected-internal-worker-argument", "Application role selection failed: unexpected-internal-worker-argument. Supported private syntax: --internal-worker.", ["--internal-worker", "-h"])];
        yield return [new InvalidCase("exact-selector-plus-long-version", ["--internal-worker", "--version"], "unexpected-internal-worker-argument", "Application role selection failed: unexpected-internal-worker-argument. Supported private syntax: --internal-worker.", ["--internal-worker", "--version"])];
        yield return [new InvalidCase("exact-selector-plus-short-version", ["--internal-worker", "-v"], "unexpected-internal-worker-argument", "Application role selection failed: unexpected-internal-worker-argument. Supported private syntax: --internal-worker.", ["--internal-worker", "-v"])];
        yield return [new InvalidCase("two-exact-selectors", ["--internal-worker", "--internal-worker"], "duplicate-internal-worker-selector", "Application role selection failed: duplicate-internal-worker-selector. Supported private syntax: --internal-worker.", ["--internal-worker", "--internal-worker"])];
        yield return [new InvalidCase("three-exact-selectors", ["--internal-worker", "--internal-worker", "--internal-worker"], "duplicate-internal-worker-selector", "Application role selection failed: duplicate-internal-worker-selector. Supported private syntax: --internal-worker.", ["--internal-worker", "--internal-worker", "--internal-worker"])];
        yield return [new InvalidCase("duplicate-exact-selector-plus-extra", ["--internal-worker", "--urls", "--internal-worker"], "duplicate-internal-worker-selector", "Application role selection failed: duplicate-internal-worker-selector. Supported private syntax: --internal-worker.", ["--internal-worker", "--urls", "--internal-worker"])];
        yield return [new InvalidCase("malformed-reserved-form-alone", ["--internal-worker=value"], "invalid-internal-worker-syntax", "Application role selection failed: invalid-internal-worker-syntax. Supported private syntax: --internal-worker.", ["--internal-worker=value"])];
        yield return [new InvalidCase("multiple-malformed-reserved-forms", ["--Internal-Worker", "--internal-worker=value"], "invalid-internal-worker-syntax", "Application role selection failed: invalid-internal-worker-syntax. Supported private syntax: --internal-worker.", ["--Internal-Worker", "--internal-worker=value"])];
        yield return [new InvalidCase("exact-selector-plus-malformed-reserved-form", ["--internal-worker", "--internal-worker="], "invalid-internal-worker-syntax", "Application role selection failed: invalid-internal-worker-syntax. Supported private syntax: --internal-worker.", ["--internal-worker", "--internal-worker="])];
    }

    [TestMethod]
    [DynamicData(nameof(SuccessCases))]
    public void SuccessMatrix_ReturnsOneCompleteSuccessArmAndPreservesCallerArguments(SuccessCase testCase)
    {
        var callerArguments = testCase.Input.ToArray();
        var result = ApplicationRoleSelector.Select(callerArguments, testCase.PublicRoleCandidate);

        Assert.IsInstanceOfType<ApplicationRoleSelectionResult.Success>(result, testCase.Label);
        Assert.IsFalse(result is ApplicationRoleSelectionResult.Failure, testCase.Label);

        var success = (ApplicationRoleSelectionResult.Success)result;
        Assert.AreSame(testCase.ExpectedRole, success.Role, testCase.Label);
        CollectionAssert.AreEqual(testCase.ExpectedArguments, success.Arguments.ToArray(), testCase.Label);
        CollectionAssert.AreEqual(testCase.ExpectedInputAfterSelection, callerArguments, testCase.Label);
    }

    [TestMethod]
    [DynamicData(nameof(InvalidCases))]
    public void InvalidMatrix_ReturnsOneCompleteSafeFailureArmAndPreservesCallerArguments(InvalidCase testCase)
    {
        var callerArguments = testCase.Input.ToArray();
        var result = ApplicationRoleSelector.Select(callerArguments);

        Assert.IsInstanceOfType<ApplicationRoleSelectionResult.Failure>(result, testCase.Label);
        Assert.IsFalse(result is ApplicationRoleSelectionResult.Success, testCase.Label);

        var failure = (ApplicationRoleSelectionResult.Failure)result;
        Assert.AreEqual(testCase.ExpectedCategory, failure.Category, testCase.Label);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Diagnostic), testCase.Label);
        Assert.IsTrue(failure.Diagnostic.Length <= MaximumDiagnosticLength, testCase.Label);
        Assert.AreEqual(testCase.ExpectedDiagnostic, failure.Diagnostic, testCase.Label);
        Assert.IsTrue(failure.Diagnostic.Contains("--internal-worker", StringComparison.Ordinal), testCase.Label);
        Assert.IsFalse(failure.Diagnostic.Contains("\r", StringComparison.Ordinal), testCase.Label);
        Assert.IsFalse(failure.Diagnostic.Contains("\n", StringComparison.Ordinal), testCase.Label);
        Assert.IsFalse(failure.Diagnostic.Contains(HostileAssignmentValue, StringComparison.Ordinal), testCase.Label);
        Assert.IsFalse(failure.Diagnostic.Contains("credential-4912", StringComparison.Ordinal), testCase.Label);
        Assert.IsFalse(failure.Diagnostic.Contains("Server=db.example", StringComparison.Ordinal), testCase.Label);
        Assert.IsFalse(failure.Diagnostic.Contains("Password=", StringComparison.Ordinal), testCase.Label);
        Assert.IsFalse(failure.Diagnostic.Contains("IMMICH_REVERSEGEO_MODE=environment-secret", StringComparison.Ordinal), testCase.Label);
        Assert.IsFalse(failure.Diagnostic.Contains("System.InvalidOperationException", StringComparison.Ordinal), testCase.Label);
        Assert.IsFalse(failure.Diagnostic.Contains("StackTrace", StringComparison.Ordinal), testCase.Label);
        Assert.IsFalse(failure.Diagnostic.Contains(" at ", StringComparison.Ordinal), testCase.Label);

        foreach (var rawArgument in testCase.Input)
        {
            if (!string.Equals(rawArgument, "--internal-worker", StringComparison.Ordinal))
            {
                Assert.IsFalse(failure.Diagnostic.Contains(rawArgument, StringComparison.Ordinal), testCase.Label);
            }
        }

        CollectionAssert.AreEqual(testCase.ExpectedInputAfterSelection, callerArguments, testCase.Label);
    }

    [TestMethod]
    public void RepeatedSelection_UsesSeparatelyAuthoredLiteralExpectations()
    {
        var firstCallerArguments = new[] { "--urls", "http://127.0.0.1:5122", "--help" };
        var secondCallerArguments = new[] { "--urls", "http://127.0.0.1:5122", "--help" };

        var first = ApplicationRoleSelector.Select(firstCallerArguments);
        var second = ApplicationRoleSelector.Select(secondCallerArguments);

        var firstSuccess = Assert.IsInstanceOfType<ApplicationRoleSelectionResult.Success>(first);
        var secondSuccess = Assert.IsInstanceOfType<ApplicationRoleSelectionResult.Success>(second);
        Assert.AreSame(Role.Web, firstSuccess.Role);
        Assert.AreSame(Role.Web, secondSuccess.Role);
        CollectionAssert.AreEqual(new[] { "--urls", "http://127.0.0.1:5122", "--help" }, firstSuccess.Arguments.ToArray());
        CollectionAssert.AreEqual(new[] { "--urls", "http://127.0.0.1:5122", "--help" }, secondSuccess.Arguments.ToArray());
        CollectionAssert.AreEqual(new[] { "--urls", "http://127.0.0.1:5122", "--help" }, firstCallerArguments);
        CollectionAssert.AreEqual(new[] { "--urls", "http://127.0.0.1:5122", "--help" }, secondCallerArguments);
    }

    [TestMethod]
    public void MutableListCallerStorage_IsNotMutated()
    {
        var callerArguments = new List<string> { "--urls", "http://127.0.0.1:5122", "--version" };

        var result = ApplicationRoleSelector.Select(callerArguments, PublicRole.RunOnce);

        var success = Assert.IsInstanceOfType<ApplicationRoleSelectionResult.Success>(result);
        Assert.AreSame(Role.RunOnce, success.Role);
        CollectionAssert.AreEqual(new[] { "--urls", "http://127.0.0.1:5122", "--version" }, success.Arguments.ToArray());
        CollectionAssert.AreEqual(new[] { "--urls", "http://127.0.0.1:5122", "--version" }, callerArguments);
    }

    [TestMethod]
    public void CallerArrayMutationAfterSuccess_DoesNotChangeSelectedArguments()
    {
        var callerArguments = new[] { "--urls", "http://127.0.0.1:5122" };

        var result = ApplicationRoleSelector.Select(callerArguments);
        callerArguments[1] = "http://127.0.0.1:9999";

        var success = Assert.IsInstanceOfType<ApplicationRoleSelectionResult.Success>(result);
        Assert.AreSame(Role.Web, success.Role);
        CollectionAssert.AreEqual(new[] { "--urls", "http://127.0.0.1:5122" }, success.Arguments.ToArray());
        CollectionAssert.AreEqual(new[] { "--urls", "http://127.0.0.1:9999" }, callerArguments);
    }

    [TestMethod]
    public void CallerListMutationAfterSuccess_DoesNotChangeSelectedArguments()
    {
        var callerArguments = new List<string> { "--urls", "http://127.0.0.1:5122" };

        var result = ApplicationRoleSelector.Select(callerArguments, PublicRole.RunOnce);
        callerArguments[1] = "http://127.0.0.1:9999";
        callerArguments.Add("--help");

        var success = Assert.IsInstanceOfType<ApplicationRoleSelectionResult.Success>(result);
        Assert.AreSame(Role.RunOnce, success.Role);
        CollectionAssert.AreEqual(new[] { "--urls", "http://127.0.0.1:5122" }, success.Arguments.ToArray());
        CollectionAssert.AreEqual(new[] { "--urls", "http://127.0.0.1:9999", "--help" }, callerArguments);
    }

    public sealed record SuccessCase(
        string Label,
        string[] Input,
        PublicApplicationRole PublicRoleCandidate,
        Role ExpectedRole,
        string[] ExpectedArguments,
        string[] ExpectedInputAfterSelection)
    {
        public override string ToString()
        {
            return Label;
        }
    }

    public sealed record InvalidCase(
        string Label,
        string[] Input,
        string ExpectedCategory,
        string ExpectedDiagnostic,
        string[] ExpectedInputAfterSelection)
    {
        public override string ToString()
        {
            return Label;
        }
    }
}
