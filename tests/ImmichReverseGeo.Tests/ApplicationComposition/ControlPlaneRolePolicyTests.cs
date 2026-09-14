using System.Reflection;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
[TestCategory("Change56")]
public sealed class ControlPlaneRolePolicyTests
{
    [TestMethod]
    [DataRow(BoundaryRole.Standard)]
    [DataRow(BoundaryRole.WebOnly)]
    [DataRow(BoundaryRole.InternalWorker)]
    [DataRow(BoundaryRole.RunOnce)]
    public void FourProductionRoles_ExcludeForeignOwnershipAndKeepTheirRequiredGraph(BoundaryRole role)
    {
        using var fixture = RoleDescriptors.Create(role);
        IReadOnlyList<BoundaryDiagnostic> failures = Inspect(role, fixture.Services);
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void EveryRequiredHeavyCategory_IsSensitiveToRemovingItsProductionDescriptor()
    {
        foreach (BoundaryRole role in new[] { BoundaryRole.InternalWorker, BoundaryRole.RunOnce })
        {
            using var fixture = RoleDescriptors.Create(role);
            foreach ((Type required, string category) in Required(role))
            {
                ServiceDescriptor[] removed = fixture.Services.Where(d => d.ServiceType != required).ToArray();
                BoundaryDiagnostic failure = Inspect(role, removed).Single(d => d.Rule == "MissingRequiredCategory" && d.Offender == required.FullName);
                Assert.AreEqual(role, failure.Role);
                Assert.AreEqual(category, failure.Category);
                Assert.AreEqual("production descriptor", failure.Path[0]);
            }
            var polluted = fixture.Services.Append(ServiceDescriptor.Singleton<ICacheInventory, CacheInventoryService>()).ToArray();
            Assert.IsTrue(Inspect(role, polluted).Any(d => d.Category == "Web control ownership"));
        }
    }

    [TestMethod]
    public void KeepingUnusedHeavyRegistrations_DoesNotSatisfyExecutorReachability()
    {
        using var fixture = RoleDescriptors.Create(BoundaryRole.RunOnce);
        ServiceDescriptor[] disconnected = fixture.Services.Where(d => d.ServiceType != typeof(IProcessingRunExecutor))
            .Append(ServiceDescriptor.Singleton<IProcessingRunExecutor>(_ => null!)).ToArray();
        IReadOnlyList<BoundaryDiagnostic> failures = Inspect(BoundaryRole.RunOnce, disconnected);
        Assert.IsFalse(failures.Any(d => d.Rule == "MissingRequiredCategory"), "All required service declarations still exist.");
        Assert.IsTrue(failures.Any(d => d.Rule == "MissingReachableCategory" && d.Offender == typeof(OvertureDivisionsService).FullName),
            "Only severing the executor-to-resolver connection must fail the reachability assertion.");
    }

    internal static IEnumerable<KeyValuePair<Type, string>> Required(BoundaryRole role)
    {
        foreach ((Type type, string category) in ControlPlaneDependencyPolicy.HeavyTypes)
        {
            if (type == typeof(ProcessAssetsWorkerJobHandler) || type == typeof(CoordinateLookupWorkerJobHandler)
                || type == typeof(CacheMutationWorkerJobHandler))
            {
                if (role == BoundaryRole.InternalWorker)
                {
                    yield return new(type, category);
                }
            }
            else
            {
                yield return new(type, category);
            }
        }
    }

    internal static IReadOnlyList<BoundaryDiagnostic> Inspect(BoundaryRole role, IEnumerable<ServiceDescriptor> descriptors)
    {
        ServiceDescriptor[] registrations = descriptors.ToArray();
        var failures = new List<BoundaryDiagnostic>();
        if (ControlPlaneDependencyPolicy.IsWeb(role))
        {
            var inspection = new WebBoundaryInspection(role);
            inspection.Inspect(registrations, typeof(StandardWebApplication).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(Microsoft.AspNetCore.Components.IComponent).IsAssignableFrom(t)));
            return ControlPlaneDependencyPolicy.Sort(inspection.Diagnostics.Concat(ControlPlaneDependencyPolicy.InspectWebRoots(role,
                registrations.SelectMany(d => new[] { d.ServiceType,
                    d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType,
                    (d.IsKeyedService ? d.KeyedImplementationInstance : d.ImplementationInstance)?.GetType() }).OfType<Type>())));
        }
        foreach (ServiceDescriptor descriptor in registrations)
        {
            Delegate? factory = descriptor.IsKeyedService ? descriptor.KeyedImplementationFactory : descriptor.ImplementationFactory;
            Type[] types = new[] { descriptor.ServiceType, descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType,
                (descriptor.IsKeyedService ? descriptor.KeyedImplementationInstance : descriptor.ImplementationInstance)?.GetType() }
                .OfType<Type>().Concat(factory is null ? [] : BoundaryIlMetadata.FactoryDependencies(factory)).ToArray();
            foreach (Type type in types)
            {
                if (ControlPlaneDependencyPolicy.ForbiddenType(type, role) is string category)
                {
                    failures.Add(ControlPlaneDependencyPolicy.Diagnostic("ForbiddenDependency", role, descriptor.ServiceType.FullName!,
                        category, type.FullName!, "production descriptor -> implementation/factory"));
                }
            }
        }
        foreach ((Type required, string category) in Required(role))
        {
            if (!registrations.Any(d => d.ServiceType == required))
            {
                failures.Add(ControlPlaneDependencyPolicy.Diagnostic("MissingRequiredCategory", role, role.ToString(), category,
                    required.FullName!, "production descriptor"));
            }
        }
        foreach (Type type in Reachable(registrations, registrations.Select(d => d.ServiceType)))
        {
            if (ControlPlaneDependencyPolicy.ForbiddenType(type, role) is string category)
            {
                failures.Add(ControlPlaneDependencyPolicy.Diagnostic("ForbiddenDependency", role, role.ToString(), category,
                    type.FullName!, "production descriptor -> constructor/factory closure"));
            }
        }
        HashSet<Type> reachable = Reachable(registrations, role == BoundaryRole.InternalWorker
            ? [typeof(ProcessAssetsWorkerJobHandler), typeof(CoordinateLookupWorkerJobHandler), typeof(CacheMutationWorkerJobHandler)]
            : [typeof(IProcessingRunExecutor)]);
        foreach ((Type required, string category) in Required(role))
        {
            if (!reachable.Contains(required))
            {
                failures.Add(ControlPlaneDependencyPolicy.Diagnostic("MissingReachableCategory", role, role.ToString(), category,
                    required.FullName!, "entry handler/executor -> descriptor/factory/constructor closure"));
            }
        }
        // Required native/index capabilities are tied to registered source owners, not arbitrary package presence.
        AssertCapability(typeof(OvertureDivisionsService), "geometry/index/prepared geometry", "NetTopologySuite");
        AssertCapability(typeof(OvertureDivisionCacheService), "native/DuckDB", "DuckDB.NET.Data");
        AssertCapability(typeof(GadmDivisionCacheService), "GADM export", "ImmichReverseGeo.Gadm.Services.GadmCacheExporter");
        return ControlPlaneDependencyPolicy.Sort(failures);

        void AssertCapability(Type owner, string category, string assembly)
        {
            if (!registrations.Any(d => d.ServiceType == owner)
                || !BoundaryProviderPolicy.Methods(owner).SelectMany(BoundaryIlMetadata.Read).OfType<MemberInfo>()
                    .Any(member => member.DeclaringType?.Assembly.GetName().Name == assembly || member.DeclaringType?.FullName == assembly))
            {
                failures.Add(ControlPlaneDependencyPolicy.Diagnostic("MissingCapability", role, owner.FullName!, category,
                    assembly, "production descriptor -> source owner -> compiled capability"));
            }
        }
    }

    internal static HashSet<Type> Reachable(IEnumerable<ServiceDescriptor> descriptors, IEnumerable<Type> roots)
    {
        var registrations = descriptors.GroupBy(d => d.ServiceType).ToDictionary(g => g.Key, g => g.ToArray());
        var found = new HashSet<Type>();
        var queue = new Queue<Type>(roots);
        while (queue.TryDequeue(out Type? type))
        {
            if (!found.Add(type))
            {
                continue;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Logging.ILogger<>))
            {
                continue; // T is a diagnostic category, not an activation edge. The logger itself is still inspected.
            }
            foreach (Type argument in type.GetGenericArguments())
            {
                queue.Enqueue(argument);
            }
            if (registrations.TryGetValue(type, out ServiceDescriptor[]? entries))
            {
                foreach (ServiceDescriptor entry in entries)
                {
                    if ((entry.IsKeyedService ? entry.KeyedImplementationType : entry.ImplementationType) is Type implementation)
                    {
                        queue.Enqueue(implementation);
                    }
                    Delegate? factory = entry.IsKeyedService ? entry.KeyedImplementationFactory : entry.ImplementationFactory;
                    if (factory is not null)
                    {
                        foreach (Type dependency in BoundaryIlMetadata.FactoryDependencies(factory))
                        {
                            queue.Enqueue(dependency);
                        }
                    }
                }
            }
            if (ControlPlaneDependencyPolicy.IsApplication(type))
            {
                foreach (Type dependency in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
                {
                    queue.Enqueue(dependency);
                }
            }
        }
        return found;
    }

    internal sealed class RoleDescriptors : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "boundary56-" + Guid.NewGuid().ToString("N"));
        internal ServiceCollection Services { get; } = new();

        internal static RoleDescriptors Create(BoundaryRole role)
        {
            var fixture = new RoleDescriptors();
            var context = ApplicationCompositionContext.Create(CompositionEnvironment.Development, fixture._root, null, null,
                role == BoundaryRole.WebOnly ? DeploymentMode.WebOnly : role == BoundaryRole.RunOnce ? DeploymentMode.RunOnce : DeploymentMode.Standard);
            switch (role)
            {
                case BoundaryRole.Standard:
                    fixture.Services.AddStandardWebComposition(context);
                    break;
                case BoundaryRole.WebOnly:
                    fixture.Services.AddWebOnlyWebComposition(context);
                    break;
                case BoundaryRole.InternalWorker:
                    fixture.Services.AddInternalWorkerComposition(context);
                    fixture.Services.AddInternalWorkerHostServices(new NoOutput(), new WorkerProcessExitOutcomeAccumulator(), InternalWorkerProtocolVersion.V2);
                    break;
                case BoundaryRole.RunOnce:
                    fixture.Services.AddRunOnceComposition(context, new WorkerProcessExitOutcomeAccumulator(), TextWriter.Null, TextWriter.Null);
                    break;
            }
            return fixture;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class NoOutput : IWorkerNdjsonOutputStreamFactory
    {
        public Stream OpenStandardOutput() => throw new AssertFailedException("Metadata inspection cannot open a worker transport.");
    }
}
