using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Web.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
[TestCategory("Change55")]
public sealed class WebControlPlaneCompositionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProductionWebDescriptors_ExcludeHeavyGeodata(bool webOnly)
    {
        string root = Path.Combine(Path.GetTempPath(), "reversegeo-web-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var context = ApplicationCompositionContext.Create(
                CompositionEnvironment.Development,
                root,
                null,
                null,
                webOnly ? DeploymentMode.WebOnly : DeploymentMode.Standard);
            var services = new ServiceCollection();
            if (webOnly)
            {
                services.AddWebOnlyWebComposition(context);
            }
            else
            {
                services.AddStandardWebComposition(context);
            }

            string[] forbidden = services
                .Where(descriptor => WebBoundaryInspection.IsForbiddenAssembly(descriptor.ServiceType.Assembly.GetName().Name))
                .Select(descriptor => descriptor.ServiceType.FullName ?? descriptor.ServiceType.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.IsEmpty(forbidden, "Production Web registrations retain heavy geodata: " + string.Join(", ", forbidden));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CompiledWebAssembly_ExcludesHeavyGeodataReferences()
    {
        string[] forbidden = typeof(StandardWebApplication).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(WebBoundaryInspection.IsForbiddenAssembly)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.IsEmpty(forbidden, "Compiled Web references retain heavy geodata: " + string.Join(", ", forbidden));
    }

}
