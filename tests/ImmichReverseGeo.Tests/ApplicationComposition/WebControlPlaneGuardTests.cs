using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Countries;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Composition;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetTopologySuite.Index.Strtree;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
[TestCategory("Change55")]
public sealed class WebControlPlaneGuardTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProductionDescriptorsFactoriesAndEveryComponent_HaveNoHeavyPath(bool webOnly)
    {
        string root = Path.Combine(Path.GetTempPath(), "web-graph-" + Guid.NewGuid().ToString("N"));
        try
        {
            var context = ApplicationCompositionContext.Create(CompositionEnvironment.Development, root, null, null,
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
            Type[] components = typeof(StandardWebApplication).Assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(IComponent).IsAssignableFrom(type)).ToArray();
            Assert.IsGreaterThanOrEqualTo(10, components.Length, "complete compiled component inventory");
            var inspection = new WebBoundaryInspection();
            inspection.Inspect(services, components);
            Assert.IsGreaterThanOrEqualTo(30, inspection.InspectedFactories, "production factories inspected without invoking them");
            Assert.IsEmpty(inspection.Failures, string.Join(Environment.NewLine, inspection.Failures));

            string output = Path.Combine(FindRoot(), "_out", "execution", "56", "ownership", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, webOnly ? "web-only.json" : "standard.json"), JsonSerializer.Serialize(new
            {
                Components = components.Select(type => type.FullName).Order(StringComparer.Ordinal),
                Descriptors = services.Select(descriptor => new
                {
                    Service = descriptor.ServiceType.ToString(),
                    Lifetime = descriptor.Lifetime.ToString(),
                    Implementation = descriptor.ImplementationType?.ToString() ?? descriptor.ImplementationInstance?.GetType().ToString(),
                    Factory = descriptor.ImplementationFactory?.Method.ToString(),
                    Owner = descriptor.ImplementationFactory?.Method.DeclaringType?.FullName,
                    Category = descriptor.ServiceType.Assembly == typeof(StandardWebApplication).Assembly
                        ? "Web control plane" : descriptor.ServiceType.Assembly == typeof(CountryIdentity).Assembly
                        ? "shared lightweight" : "framework or allowed storage"
                }),
                inspection.InspectedFactories
            }, new JsonSerializerOptions { WriteIndented = true }));
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
    [DataRow("descriptor", "forbidden assembly/type")]
    [DataRow("constructor", "constructor")]
    [DataRow("lazy-factory", "factory")]
    [DataRow("hosted-alias", "factory")]
    [DataRow("keyed-factory", "factory")]
    [DataRow("open-generic", "forbidden assembly/type")]
    [DataRow("reflection", "opaque activation")]
    [DataRow("component", "inject")]
    public void DependencyGuard_DetectsDeliberateHiddenEdgesWithoutActivation(string kind, string expectedPath)
    {
        int activations = 0;
        var services = new ServiceCollection();
        Type[] components = [];
        switch (kind)
        {
            case "descriptor":
                services.AddSingleton<OvertureDivisionsService>();
                break;
            case "constructor":
                services.AddSingleton<ConstructorProbe>();
                break;
            case "lazy-factory":
                services.AddSingleton<object>(sp => new Lazy<object>(() =>
                {
                    Interlocked.Increment(ref activations);
                    return sp.GetRequiredService<OvertureDivisionsService>();
                }));
                break;
            case "hosted-alias":
                services.AddSingleton<IHostedService>(sp => (IHostedService)(object)sp.GetRequiredService<OvertureDivisionsService>());
                break;
            case "keyed-factory":
                services.AddKeyedSingleton<object>("hidden", (sp, _) => sp.GetRequiredService<OvertureDivisionsService>());
                break;
            case "open-generic":
                services.AddSingleton(typeof(IEnumerable<>), typeof(STRtree<>));
                break;
            case "reflection":
                services.AddSingleton(_ => Activator.CreateInstance(typeof(object))!);
                break;
            case "component":
                components = [typeof(ComponentProbe)];
                break;
            default:
                Assert.Fail("Unknown negative case " + kind);
                break;
        }
        var inspection = new WebBoundaryInspection();
        inspection.Inspect(services, components);
        Assert.IsTrue(inspection.Failures.Any(failure => failure.Contains(expectedPath, StringComparison.Ordinal)),
            kind + ": " + string.Join(Environment.NewLine, inspection.Failures));
        Assert.AreEqual(0, activations, "classification cannot execute a lazy factory");
    }

    [TestMethod]
    public void ProductionSourceProjectsRestoreAssetsAndCompiledClosure_ExcludeHeavyDependencies()
    {
        string web = Path.Combine(FindRoot(), "src", "ImmichReverseGeo.Web");
        foreach (string path in Directory.EnumerateFiles(web, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".razor")
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj")))
        {
            string? failure = ForbiddenSource(File.ReadAllText(path), path);
            Assert.IsNull(failure, failure);
        }
        string projectPath = Path.Combine(web, "ImmichReverseGeo.Web.csproj");
        Assert.IsNull(ForbiddenProject(XDocument.Load(projectPath), projectPath));
        string assetsPath = Path.Combine(web, "obj", "project.assets.json");
        using (var assets = JsonDocument.Parse(File.ReadAllText(assetsPath)))
        {
            Assert.IsNull(ForbiddenNames(assets.RootElement.GetProperty("libraries").EnumerateObject()
                .Select(property => property.Name.Split('/')[0]), "restore " + assetsPath));
        }
        foreach (Assembly assembly in new[] { typeof(StandardWebApplication).Assembly, typeof(CountryIdentity).Assembly })
        {
            Assert.IsNull(ForbiddenNames(assembly.GetReferencedAssemblies().Select(reference => reference.Name!),
                "compiled " + assembly.GetName().Name));
        }
        Assert.AreEqual(typeof(CountryIdentity).Assembly, typeof(CountryIdentityCatalog).Assembly);
        Assert.AreEqual(typeof(CountryIdentity).Assembly, typeof(GadmCountryCodeMapper).Assembly);
    }

    [TestMethod]
    public void StagedApplication_IncludesTheBlazorBootstrapAndLightweightCatalog()
    {
        string artifact = Path.Combine(AppContext.BaseDirectory, "production-worker-apphost");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(artifact,
            "ImmichReverseGeo.Web.staticwebassets.endpoints.json")));
        Assert.IsTrue(manifest.RootElement.GetProperty("Endpoints").EnumerateArray()
            .Any(endpoint => endpoint.GetProperty("Route").GetString()!
                .StartsWith("_framework/blazor.web", StringComparison.Ordinal)),
            "Executable without local Razor files must still ship the Blazor bootstrap.");
        Assert.IsTrue(File.Exists(Path.Combine(artifact, "bundled-data", "iso3166.json")),
            "Core identity resource must retain the deployed bundled-data path.");
    }

    [TestMethod]
    public void StaticGuards_RejectPoisonedSourceProjectRestoreAndCompiledInputsWithTheirPath()
    {
        Assert.AreEqual("source Probe.razor -> heavy import or activation", ForbiddenSource("@using ImmichReverseGeo.Gadm.Services", "Probe.razor"));
        Assert.AreEqual("project Probe.csproj -> ImmichReverseGeo.Overture", ForbiddenProject(
            XDocument.Parse("<Project><ItemGroup><ProjectReference Include='../ImmichReverseGeo.Overture/ImmichReverseGeo.Overture.csproj'/></ItemGroup></Project>"), "Probe.csproj"));
        Assert.AreEqual("restore Probe.assets -> DuckDB.NET.Data.Full", ForbiddenNames(["Microsoft.Data.Sqlite", "DuckDB.NET.Data.Full"], "restore Probe.assets"));
        foreach (string package in new[] { "DuckDB.NET.Data.Full", "NetTopologySuite", "GeoJSON4STJ" })
        {
            Assert.AreEqual("project Probe.csproj -> " + package, ForbiddenProject(
                XDocument.Parse($"<Project><ItemGroup><PackageReference Include='{package}'/></ItemGroup></Project>"), "Probe.csproj"));
            Assert.AreEqual("restore Probe.assets -> " + package, ForbiddenNames([package], "restore Probe.assets"));
        }
        Assert.AreEqual("source Probe.cs -> heavy import or activation", ForbiddenSource("using GeoJSON4STJ;", "Probe.cs"));
        Assert.AreEqual("compiled Probe.dll -> ImmichReverseGeo.Overture", ForbiddenNames(
            [typeof(OvertureDivisionsService).Assembly.GetName().Name!], "compiled Probe.dll"));
        Assert.IsNull(ForbiddenNames(["ImmichReverseGeo.Core", "Npgsql", "Microsoft.Data.Sqlite", "Cronos"], "allowed"));
    }

    private static string? ForbiddenSource(string source, string path)
    {
        return ControlPlaneDependencyPolicy.InspectSource(source, path, BoundaryRole.Standard) is not null
            ? "source " + path + " -> heavy import or activation" : null;
    }

    private static string? ForbiddenProject(XDocument project, string path)
    {
        return ForbiddenNames(project.Descendants().Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference")
            .Select(element => element.Name.LocalName == "ProjectReference"
                ? Path.GetFileNameWithoutExtension(((string?)element.Attribute("Include") ?? "").Replace('\\', '/'))
                : (string?)element.Attribute("Include") ?? ""), "project " + path);
    }

    private static string? ForbiddenNames(IEnumerable<string> names, string path)
    {
        string? forbidden = names.FirstOrDefault(WebBoundaryInspection.IsForbiddenAssembly);
        return forbidden is null ? null : path + " -> " + forbidden;
    }

    internal static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "immich-reversegeo.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new AssertFailedException("Source checkout not found for Change55 boundary guards.");
    }

    private sealed class ConstructorProbe(OvertureDivisionsService source)
    {
        internal OvertureDivisionsService Source { get; } = source;
    }

    private sealed class ComponentProbe : ComponentBase
    {
        [Inject] private OvertureDivisionsService Source { get; set; } = null!;
    }
}
