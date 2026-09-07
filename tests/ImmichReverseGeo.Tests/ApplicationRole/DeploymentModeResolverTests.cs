using ImmichReverseGeo.Core.ApplicationRole;

namespace ImmichReverseGeo.Tests.ApplicationRole;

[TestClass]
public sealed class DeploymentModeResolverTests
{
    public static IEnumerable<object[]> AcceptedValues()
    {
        yield return ["missing", null!, DeploymentMode.Standard];
        yield return ["standard", "standard", DeploymentMode.Standard];
        yield return ["web-only", "web-only", DeploymentMode.WebOnly];
        yield return ["run-once", "run-once", DeploymentMode.RunOnce];
    }

    public static IEnumerable<object[]> RejectedValues()
    {
        yield return ["empty", ""];
        yield return ["whitespace", " "];
        yield return ["padded", " standard "];
        yield return ["case-varied", "Standard"];
        yield return ["unknown", "worker-secret-4912"];
    }

    [TestMethod]
    [DynamicData(nameof(AcceptedValues))]
    public void Resolve_AcceptsOnlyExactValues(string label, string? value, DeploymentMode expectedMode)
    {
        var result = DeploymentModeResolver.Resolve(_ => value);

        var success = Assert.IsInstanceOfType<DeploymentModeResolution.Success>(result, label);
        Assert.AreSame(expectedMode, success.Mode, label);
    }

    [TestMethod]
    [DynamicData(nameof(RejectedValues))]
    public void Resolve_RejectsEveryPresentNonExactValue(string label, string value)
    {
        var result = DeploymentModeResolver.Resolve(_ => value);

        var failure = Assert.IsInstanceOfType<DeploymentModeResolution.Failure>(result, label);
        Assert.AreEqual(DeploymentModeResolver.InvalidModeDiagnostic, failure.Diagnostic, label);

        if (label == "unknown")
        {
            Assert.IsFalse(failure.Diagnostic.Contains(value, StringComparison.Ordinal), label);
        }
    }

    [TestMethod]
    public void Resolve_EnvironmentAccessorReadsOnlyTheModeVariableOnce()
    {
        var readCount = 0;

        var result = DeploymentModeResolver.Resolve(name =>
        {
            readCount++;
            Assert.AreEqual(DeploymentModeResolver.EnvironmentVariableName, name);
            return "web-only";
        });

        var success = Assert.IsInstanceOfType<DeploymentModeResolution.Success>(result);
        Assert.AreSame(DeploymentMode.WebOnly, success.Mode);
        Assert.AreEqual(1, readCount);
    }
}
