using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;
using ImmichReverseGeo.Core.Countries;
using ImmichReverseGeo.Web.Composition;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
[TestCategory("Change56")]
public sealed class ControlPlaneStaticPolicyTests
{
    private static readonly ConcurrentDictionary<(string Path, DateTime Modified, long Length), string[]> RestoreCache = new();
    private static readonly string[] ApprovedRestore =
    [
        "Cronos", "ImmichReverseGeo.Core", "Microsoft.AspNetCore.App.Internal.Assets", "Microsoft.Data.Sqlite",
        "Microsoft.Data.Sqlite.Core", "Npgsql", "SQLitePCLRaw.bundle_e_sqlite3", "SQLitePCLRaw.core",
        "SQLitePCLRaw.lib.e_sqlite3", "SQLitePCLRaw.provider.e_sqlite3"
    ];

    [TestMethod]
    public void EveryControlPlaneProjectAndRestoreClosure_HasOnlyReviewedEdges()
    {
        string root = WebControlPlaneGuardTests.FindRoot();
        foreach (string name in new[] { "ImmichReverseGeo.Web", "ImmichReverseGeo.Core" })
        {
            string directory = Path.Combine(root, "src", name);
            XDocument project = XDocument.Load(Path.Combine(directory, name + ".csproj"));
            string[] projects = project.Descendants("ProjectReference").Select(e =>
                Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/'))).ToArray();
            CollectionAssert.AreEquivalent(name.EndsWith("Web", StringComparison.Ordinal) ? new[] { "ImmichReverseGeo.Core" } : [], projects);
            string[] packages = project.Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!).ToArray();
            CollectionAssert.AreEquivalent(name.EndsWith("Web", StringComparison.Ordinal) ? new[] { "Cronos", "Npgsql", "Microsoft.Data.Sqlite" } : [], packages);
            Assert.IsEmpty(project.Descendants("Reference").ToArray(), "Unreviewed assembly Reference bypasses project/package closure.");
            string[] restored = RestoreNames(Path.Combine(directory, "obj", "project.assets.json"));
            Assert.IsEmpty(CheckEdges(BoundaryRole.Standard, name, restored, name.EndsWith("Web", StringComparison.Ordinal) ? ApprovedRestore : [], "restore"));
        }
    }

    [TestMethod]
    public void CompiledControlPlaneAndBootstrap_AreInspectedWithoutLoadingAssemblies()
    {
        string[] approvedAssemblies = new[]
        {
            Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "Microsoft.NETCore.App.deps.json"),
            Path.Combine(Path.GetDirectoryName(typeof(Microsoft.AspNetCore.Builder.WebApplication).Assembly.Location)!, "Microsoft.AspNetCore.App.deps.json")
        }.SelectMany(FrameworkAssemblyNames)
            .Concat(["ImmichReverseGeo.Core", "Cronos", "Npgsql", "Microsoft.Data.Sqlite", "SQLitePCLRaw.core"])
            .ToArray();
        foreach (string path in new[] { typeof(StandardWebApplication).Assembly.Location, typeof(CountryIdentity).Assembly.Location })
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            MetadataReader metadata = pe.GetMetadataReader();
            foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
            {
                string name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                IReadOnlyList<BoundaryDiagnostic> failures = CheckEdges(BoundaryRole.Standard, path, [name], approvedAssemblies, "compiled reference");
                Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
            }
            // Every type reference is covered, including generated bases, generic signatures and private injection types.
            foreach (TypeReferenceHandle handle in metadata.TypeReferences)
            {
                TypeReference type = metadata.GetTypeReference(handle);
                if (type.ResolutionScope.Kind == HandleKind.AssemblyReference)
                {
                    string assembly = metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);
                    IReadOnlyList<BoundaryDiagnostic> failures = CheckEdges(BoundaryRole.Standard, path + " -> " + metadata.GetString(type.Name),
                        [assembly], approvedAssemblies, "compiled type reference");
                    Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
                }
            }
        }

        string host = Path.Combine(AppContext.BaseDirectory, "production-worker-apphost", "ImmichReverseGeo.Web.dll");
        using var hostStream = File.OpenRead(host);
        using var hostPe = new PEReader(hostStream);
        MetadataReader hostMetadata = hostPe.GetMetadataReader();
        var actual = new HashSet<string>();
        string[] bootstrapTypes =
        [
            "ImmichReverseGeo.Web.WorkerHost.InternalWorkerProcess", "ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost",
            "ImmichReverseGeo.Web.RunOnce.RunOnceProcess", "ImmichReverseGeo.Web.RunOnce.RunOnceApplication"
        ];
        foreach (AssemblyReferenceHandle handle in hostMetadata.AssemblyReferences)
        {
            string name = hostMetadata.GetString(hostMetadata.GetAssemblyReference(handle).Name);
            Assert.IsTrue(ControlPlaneDependencyPolicy.HeavyAssembly(name) is null || name == "ImmichReverseGeo.Worker", "unapproved bootstrap assembly " + name);
        }
        foreach (TypeReferenceHandle handle in hostMetadata.TypeReferences)
        {
            TypeReference type = hostMetadata.GetTypeReference(handle);
            if (type.ResolutionScope.Kind == HandleKind.AssemblyReference)
            {
                string assembly = hostMetadata.GetString(hostMetadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);
                if (ControlPlaneDependencyPolicy.HeavyAssembly(assembly) is not null)
                {
                    string name = hostMetadata.GetString(type.Namespace) + "." + hostMetadata.GetString(type.Name);
                    Assert.IsTrue(bootstrapTypes.Contains(name, StringComparer.Ordinal), "unapproved bootstrap type/field " + name);
                }
            }
        }
        foreach (MemberReferenceHandle handle in hostMetadata.MemberReferences)
        {
            MemberReference member = hostMetadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }
            TypeReference type = hostMetadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference)
            {
                continue;
            }
            string assembly = hostMetadata.GetString(hostMetadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);
            if (ControlPlaneDependencyPolicy.HeavyAssembly(assembly) is not null)
            {
                actual.Add(assembly + ":" + hostMetadata.GetString(type.Namespace) + "." + hostMetadata.GetString(type.Name) + "." + hostMetadata.GetString(member.Name));
            }
        }
        CollectionAssert.AreEquivalent(new[]
        {
            "ImmichReverseGeo.Worker:ImmichReverseGeo.Web.WorkerHost.InternalWorkerProcess.CompleteInvalidInvocation",
            "ImmichReverseGeo.Worker:ImmichReverseGeo.Web.WorkerHost.InternalWorkerProcess.Run",
            "ImmichReverseGeo.Worker:ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost.RunProductionAsync",
            "ImmichReverseGeo.Worker:ImmichReverseGeo.Web.RunOnce.RunOnceProcess.Run",
            "ImmichReverseGeo.Worker:ImmichReverseGeo.Web.RunOnce.RunOnceApplication.RunProductionAsync"
        }, actual.ToArray(), "Only explicit disposable-role validation/dispatch/bootstrap members may reference Worker: " + string.Join(", ", actual));
    }

    [TestMethod]
    public void DirectAndTransitiveSyntheticEdges_ReportRoleOwnerCategoryAndOrderedPath()
    {
        Assert.AreEqual("UnapprovedStaticEdge", CheckEdges(BoundaryRole.Standard, "poisoned allowance",
            ["ImmichReverseGeo.Worker"], ["ImmichReverseGeo.Worker"], "compiled").Single().Rule,
            "A broad allowance cannot waive an explicit heavy denial.");
        foreach (BoundaryRole role in new[] { BoundaryRole.Standard, BoundaryRole.WebOnly })
        {
            foreach (string kind in new[] { "project", "package", "restore", "compiled" })
            {
                foreach (string name in new[] { "ImmichReverseGeo.Worker", "ImmichReverseGeo.Overture", "ImmichReverseGeo.Gadm", "DuckDB.NET.Data.Full", "NetTopologySuite", "GeoJSON4STJ", "UnreviewedProvider" })
                {
                    BoundaryDiagnostic failure = CheckEdges(role, "Web -> approved wrapper", [name], ApprovedRestore, kind).Single();
                    Assert.AreEqual("UnapprovedStaticEdge", failure.Rule);
                    Assert.AreEqual(role, failure.Role);
                    Assert.AreEqual(name, failure.Offender);
                    CollectionAssert.AreEqual(new[] { "Web", "approved wrapper", kind }, failure.Path.ToArray());
                    Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Category));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Remediation));
                }
            }
        }
    }

    internal static IReadOnlyList<BoundaryDiagnostic> CheckEdges(BoundaryRole role, string owner,
        IEnumerable<string> edges, IEnumerable<string> approved, string kind)
    {
        var allowed = approved.ToHashSet(StringComparer.Ordinal);
        return ControlPlaneDependencyPolicy.Sort(edges.Where(name => ControlPlaneDependencyPolicy.HeavyAssembly(name) is not null || !allowed.Contains(name)).Select(name =>
            ControlPlaneDependencyPolicy.Diagnostic("UnapprovedStaticEdge", role, owner,
                ControlPlaneDependencyPolicy.HeavyAssembly(name) ?? "unreviewed dependency", name, owner + " -> " + kind)));
    }

    private static IEnumerable<string> FrameworkAssemblyNames(string path)
    {
        // The installed framework's own manifest, not every DLL beside the test executable.
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("targets").EnumerateObject().SelectMany(target => target.Value.EnumerateObject())
            .Where(library => library.Value.TryGetProperty("runtime", out _))
            .SelectMany(library => library.Value.GetProperty("runtime").EnumerateObject())
            .Select(asset => Path.GetFileNameWithoutExtension(asset.Name)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] RestoreNames(string path)
    {
        var info = new FileInfo(path);
        return RestoreCache.GetOrAdd((info.FullName, info.LastWriteTimeUtc, info.Length), key =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(key.Path));
            return document.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name.Split('/')[0]).Order(StringComparer.Ordinal).ToArray();
        });
    }
}
