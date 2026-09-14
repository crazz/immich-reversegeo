using ImmichReverseGeo.Web.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.CrossProcessRunExclusion;

[TestClass]
public sealed class CrossProcessAppHostLocatorTests
{
    [TestMethod]
    public void BuildStages_TestAppHostWithItsRuntimeFiles()
    {
        AssertStagedRuntime(
            CrossProcessAppHostLocator.CrossProcessRunLockAppHostDirectory,
            CrossProcessAppHostLocator.CrossProcessRunLockAppHostExecutable,
            CrossProcessAppHostLocator.CrossProcessRunLockAppHostAssemblyName);
    }

    [TestMethod]
    public void BuildStages_ProductionWorkerAppHostWithItsRuntimeFiles()
    {
        AssertStagedRuntime(
            CrossProcessAppHostLocator.ProductionWorkerAppHostDirectory,
            CrossProcessAppHostLocator.ProductionWorkerAppHostExecutable,
            CrossProcessAppHostLocator.ProductionWorkerAppHostAssemblyName);
    }

    [TestMethod]
    public void TestAppHostDescriptor_UsesTheAbsoluteStagedExecutableAndProvidedDiscreteArguments()
    {
        var arguments = new[]
        {
            "--scenario", "held-success",
            "--resource-root", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D")),
            "--marker-path", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D"), "marker.json"),
            "--release-pipe", "change32_locator_test"
        };

        var descriptor = CrossProcessAppHostLocator.CreateCrossProcessRunLockDescriptor(arguments);

        Assert.IsTrue(Path.IsPathFullyQualified(descriptor.ExecutablePath));
        Assert.AreEqual(CrossProcessAppHostLocator.CrossProcessRunLockAppHostExecutable, descriptor.ExecutablePath);
        Assert.AreEqual(CrossProcessAppHostLocator.CrossProcessRunLockAppHostDirectory, descriptor.WorkingDirectory);
        Assert.AreEqual(ChildProcessEnvironmentPolicy.InheritCurrent, descriptor.EnvironmentPolicy);
        CollectionAssert.AreEqual(arguments, descriptor.Arguments.ToArray());
    }

    [TestMethod]
    public void ProductionWorkerDescriptor_UsesOnlyTheInternalWorkerRoleSelector()
    {
        var descriptor = CrossProcessAppHostLocator.CreateProductionWorkerDescriptor();

        Assert.IsTrue(Path.IsPathFullyQualified(descriptor.ExecutablePath));
        Assert.AreEqual(CrossProcessAppHostLocator.ProductionWorkerAppHostExecutable, descriptor.ExecutablePath);
        Assert.AreEqual(CrossProcessAppHostLocator.ProductionWorkerAppHostDirectory, descriptor.WorkingDirectory);
        CollectionAssert.AreEqual(new[] { "--internal-worker" }, descriptor.Arguments.ToArray());
    }

    private static void AssertStagedRuntime(string directory, string executable, string assemblyName)
    {
        Assert.IsTrue(Path.IsPathFullyQualified(directory));
        Assert.IsTrue(Path.IsPathFullyQualified(executable));
        Assert.IsTrue(File.Exists(executable));
        Assert.IsTrue(File.Exists(Path.Combine(directory, assemblyName + ".dll")));
        Assert.IsTrue(File.Exists(Path.Combine(directory, assemblyName + ".deps.json")));
        Assert.IsTrue(File.Exists(Path.Combine(directory, assemblyName + ".runtimeconfig.json")));
    }
}
