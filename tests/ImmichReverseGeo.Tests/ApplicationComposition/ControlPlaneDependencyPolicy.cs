using ImmichReverseGeo.Core.Countries;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.AspNetCore.Components;
using Npgsql;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

public enum BoundaryRole { Standard, WebOnly, InternalWorker, RunOnce }

internal sealed record BoundaryDiagnostic(
    string Rule, BoundaryRole Role, string Root, string Category, string Offender,
    IReadOnlyList<string> Path, string Remediation)
{
    public override string ToString() => $"{Rule} [{Role}] {Root}: {Category}: {string.Join(" -> ", Path)} -> {Offender}. {Remediation}";
}

internal sealed record BoundaryPolicyEntry(Type Contract, string Category, string Rationale, string Owner);

// A permitted contract is a traversal entry, never a terminal exemption for its implementation.
internal static class ControlPlaneDependencyPolicy
{
    // Exact reviewed registration surface. New services require closure and adjacent-negative evidence.
    internal static readonly Type[] ApprovedWebServices =
    [
        typeof(ImmichReverseGeo.Core.Models.StorageOptions),
        typeof(ImmichReverseGeo.Core.Processing.IProcessingEventReporter),
        typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.ChildWorkerLauncher),
        typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.IChildProcessFactory),
        typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.IChildWorkerLauncher),
        typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.SystemChildProcessFactory),
        typeof(ImmichReverseGeo.Web.Composition.ApplicationCompositionContext),
        typeof(ImmichReverseGeo.Web.Services.CacheDeletionCommand),
        typeof(ImmichReverseGeo.Web.Services.CacheDeletionPageControllerFactory),
        typeof(ImmichReverseGeo.Web.Services.CacheInventoryDeletionOperations),
        typeof(ImmichReverseGeo.Web.Services.CacheInventoryDeletionPageControllerFactory),
        typeof(ImmichReverseGeo.Web.Services.CacheInventoryMutationWorkerClient),
        typeof(ImmichReverseGeo.Web.Services.CacheInventoryOptions),
        typeof(ImmichReverseGeo.Web.Services.CacheInventoryService),
        typeof(ImmichReverseGeo.Web.Services.CacheInventorySqliteMetadataReader),
        typeof(ImmichReverseGeo.Web.Services.CacheInventoryStorageScanner),
        typeof(ImmichReverseGeo.Web.Services.CacheMutationPageControllerFactory),
        typeof(ImmichReverseGeo.Web.Services.CacheMutationPageControllerHostLifetime),
        typeof(ImmichReverseGeo.Web.Services.CacheMutationWorkerClient),
        typeof(ImmichReverseGeo.Web.Services.ChildWorkerStartupValidator),
        typeof(ImmichReverseGeo.Web.Services.CityResolverProfileCatalogService),
        typeof(ImmichReverseGeo.Web.Services.ConfigCoordinateLookupSettingsSnapshotProvider),
        typeof(ImmichReverseGeo.Web.Services.ConfigService),
        typeof(ImmichReverseGeo.Web.Services.CoordinateLookupPageControllerFactory),
        typeof(ImmichReverseGeo.Web.Services.CoordinateLookupPageControllerHostLifetime),
        typeof(ImmichReverseGeo.Web.Services.CoordinateLookupWorkerClient),
        typeof(ImmichReverseGeo.Web.Services.CountryCodeService),
        typeof(ImmichReverseGeo.Web.Services.DatabaseMaintenanceController),
        typeof(ImmichReverseGeo.Web.Services.ICacheDeletionFileSystem),
        typeof(ImmichReverseGeo.Web.Services.ICacheInventory),
        typeof(ImmichReverseGeo.Web.Services.ICacheInventoryFileSystem),
        typeof(ImmichReverseGeo.Web.Services.ICacheInventoryInvalidator),
        typeof(ImmichReverseGeo.Web.Services.ICacheInventoryMetadataReader),
        typeof(ImmichReverseGeo.Web.Services.ICacheInventoryStorageScanner),
        typeof(ImmichReverseGeo.Web.Services.ICacheMutationWorkerClient),
        typeof(ImmichReverseGeo.Web.Services.IChildProcessingRunBackend),
        typeof(ImmichReverseGeo.Web.Services.ICoordinateLookupSettingsSnapshotProvider),
        typeof(ImmichReverseGeo.Web.Services.ICoordinateLookupWorkerClient),
        typeof(ImmichReverseGeo.Web.Services.IDatabaseMaintenanceController),
        typeof(ImmichReverseGeo.Web.Services.IImmichLocationResetStore),
        typeof(ImmichReverseGeo.Web.Services.ILocationValueOptionsReader),
        typeof(ImmichReverseGeo.Web.Services.IManualProcessingRunCoordinator),
        typeof(ImmichReverseGeo.Web.Services.IProcessAssetsWebStatus),
        typeof(ImmichReverseGeo.Web.Services.IProcessAssetsWorkerStatusSink),
        typeof(ImmichReverseGeo.Web.Services.IProcessingScheduleConfiguration),
        typeof(ImmichReverseGeo.Web.Services.IScheduledRunTrigger),
        typeof(ImmichReverseGeo.Web.Services.IScheduledRunWorkCounter),
        typeof(ImmichReverseGeo.Web.Services.IScheduledRunWorkGate),
        typeof(ImmichReverseGeo.Web.Services.ISkippedAssetsCountReader),
        typeof(ImmichReverseGeo.Web.Services.ISkippedAssetsMaintenanceStore),
        typeof(ImmichReverseGeo.Web.Services.IWorkerJobAdmissionGate),
        typeof(ImmichReverseGeo.Web.Services.IWorkerJobArbitrationDiagnostics),
        typeof(ImmichReverseGeo.Web.Services.ImmichDbRepository),
        typeof(ImmichReverseGeo.Web.Services.PhysicalCacheDeletionFileSystem),
        typeof(ImmichReverseGeo.Web.Services.PhysicalCacheInventoryFileSystem),
        typeof(ImmichReverseGeo.Web.Services.ProcessAssetsWebStatus),
        typeof(ImmichReverseGeo.Web.Services.ProcessingBackgroundService),
        typeof(ImmichReverseGeo.Web.Services.ProcessingRunCoordinator),
        typeof(ImmichReverseGeo.Web.Services.ProcessingState),
        typeof(ImmichReverseGeo.Web.Services.ProcessingStateEventReporter),
        typeof(ImmichReverseGeo.Web.Services.SkippedAssetsRepository),
        typeof(ImmichReverseGeo.Web.Services.WorkerJobCoordinator),
        typeof(ImmichReverseGeo.Web.WorkerCommandInvocation.IWorkerCommandInvocationBuilder),
        typeof(ImmichReverseGeo.Web.WorkerCommandInvocation.IWorkerCommandRuntimeFactsCapture),
        typeof(ImmichReverseGeo.Web.WorkerCommandInvocation.IWorkerCommandRuntimeObservationSource),
        typeof(ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandAmbientRuntimeObservationSource),
        typeof(ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocationBuilder),
        typeof(ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandRuntimeFactsCapture),
        typeof(ImmichReverseGeo.Web.WorkerEventStateBridge.WorkerEventStateBridgeFactory),
        typeof(ImmichReverseGeo.Web.WorkerFailureRecovery.WorkerRunControlPlane),
    ];

    internal static IReadOnlyList<BoundaryDiagnostic> InspectWebRoots(BoundaryRole role, IEnumerable<Type> types) =>
        Sort(types.Where(IsApplication).Where(type => !ApprovedWebServices.Contains(type)).Select(type =>
            Diagnostic("UnreviewedRoot", role, type.FullName!, "control-plane registration", type.FullName!, "production descriptor -> implementation/instance")));

    internal static readonly Type[] WebOnlyTypes =
    [
        typeof(WebApplicationBuilder), typeof(WebApplication), typeof(IServer), typeof(EndpointDataSource),
            typeof(IDataProtectionProvider), typeof(IAntiforgery), typeof(IPostConfigureOptions<RazorComponentsServiceOptions>),
            typeof(IConfigureOptions<CircuitOptions>), typeof(ProcessingState), typeof(ProcessingStateEventReporter),
            typeof(ProcessingBackgroundService), typeof(ProcessingRunCoordinator), typeof(IManualProcessingRunCoordinator),
            typeof(IChildProcessingRunBackend), typeof(IChildWorkerLauncher), typeof(ChildWorkerStartupValidator),
            typeof(IProcessingScheduleConfiguration), typeof(IScheduledRunTrigger), typeof(IScheduledRunWorkGate),
            typeof(IScheduledRunWorkCounter), typeof(WorkerCommandAmbientRuntimeObservationSource),
            typeof(IWorkerCommandRuntimeObservationSource), typeof(WorkerCommandRuntimeFactsCapture),
            typeof(IWorkerCommandRuntimeFactsCapture), typeof(WorkerCommandInvocationBuilder),
            typeof(IWorkerCommandInvocationBuilder), typeof(WorkerJobCoordinator),
            typeof(IWorkerJobAdmissionGate), typeof(IWorkerJobArbitrationDiagnostics),
            typeof(ConfigCoordinateLookupSettingsSnapshotProvider),
            typeof(ICoordinateLookupSettingsSnapshotProvider), typeof(CoordinateLookupWorkerClient),
            typeof(ICoordinateLookupWorkerClient), typeof(CoordinateLookupPageControllerHostLifetime),
            typeof(CoordinateLookupPageControllerFactory), typeof(CacheDeletionCommand),
            typeof(ICacheDeletionFileSystem), typeof(PhysicalCacheDeletionFileSystem),
            typeof(CacheDeletionPageControllerFactory), typeof(CacheInventoryOptions),
            typeof(ICacheInventoryFileSystem), typeof(PhysicalCacheInventoryFileSystem),
            typeof(ICacheInventoryMetadataReader), typeof(CacheInventorySqliteMetadataReader),
            typeof(ICacheInventoryStorageScanner), typeof(CacheInventoryStorageScanner),
            typeof(ICacheInventory), typeof(ICacheInventoryInvalidator),
            typeof(CacheInventoryService), typeof(CacheInventoryDeletionOperations),
            typeof(CacheInventoryDeletionPageControllerFactory),
            typeof(CacheInventoryMutationWorkerClient),
        typeof(IDatabaseMaintenanceController), typeof(DatabaseMaintenanceController), typeof(CacheMutationPageControllerFactory)
    ];
    internal const string Remediation = "Keep heavy execution in the disposable role; review the exact contract and its complete closure.";
    internal static readonly IReadOnlyDictionary<Type, string> HeavyTypes = new Dictionary<Type, string>
    {
        [typeof(IProcessingRunExecutor)] = "in-process executor",
        [typeof(ProcessingRunExecutor)] = "in-process executor",
        [typeof(IProcessingAdministrativeResolver)] = "administrative resolver",
        [typeof(AdministrativeAreaResolverService)] = "administrative resolver",
        [typeof(OvertureDivisionsService)] = "country index",
        [typeof(OvertureDivisionCacheService)] = "Overture query/download/export/mutation",
        [typeof(OverturePlacesService)] = "airport infrastructure",
        [typeof(IProcessingInfrastructureLookup)] = "airport infrastructure",
        [typeof(ProcessingInfrastructureLookup)] = "airport infrastructure",
        [typeof(GadmDivisionsService)] = "GADM query/geometry",
        [typeof(GadmDivisionCacheService)] = "GADM download/export/mutation",
        [typeof(ProcessAssetsWorkerJobHandler)] = "processing handler",
        [typeof(CoordinateLookupWorkerJobHandler)] = "lookup handler",
        [typeof(CacheMutationWorkerJobHandler)] = "cache mutation handler"
    };

    internal static readonly BoundaryPolicyEntry[] Lightweight =
    [
        new(typeof(ProcessAssetsRequest), "transport", "Immutable request; handlers remain Worker-owned.", "Core.WorkerJobs"),
        new(typeof(CoordinateLookupWorkerClient), "job client", "Owns transport, not coordinate resolution.", "Web Lookup"),
        new(typeof(ICacheInventory), "inventory", "Bounded metadata snapshots.", "Web Data"),
        new(typeof(CacheInventorySqliteMetadataReader), "metadata SQLite", "Schema and bounded metadata only; no geometry and no pooled handles.", "Web Data"),
        new(typeof(SkippedAssetsRepository), "skipped SQLite", "Only skipped-asset control state.", "Web processing"),
        new(typeof(IDatabaseMaintenanceController), "maintenance", "Admission and existing reset stores.", "Web Reset"),
        new(typeof(CacheDeletionCommand), "deletion", "Coordinated file deletion, not geodata execution.", "Web Data"),
        new(typeof(ConfigService), "configuration", "Configuration and immutable processing snapshots.", "Web Settings"),
        new(typeof(ProcessingState), "UI state", "In-memory status and event projection.", "Web Dashboard"),
        new(typeof(CountryIdentityCatalog), "identity", "Bounded ISO/territory resource without polygons.", "Core Countries"),
        new(typeof(CountryCodeService), "country display", "Geometry-free identity adapter.", "Web Settings"),
        new(typeof(CityResolverProfileCatalogService), "profiles", "Bounded configuration catalog, no resolver.", "Web Settings"),
        new(typeof(ImmichDbRepository), "lazy PostgreSQL", "Repository methods are invoked only by admitted operations or work detection.", "Web processing"),
        new(typeof(NpgsqlDataSource), "lazy provider", "Building DI must not open a connection.", "Shared composition")
    ];

    internal static bool IsWeb(BoundaryRole role) => role is BoundaryRole.Standard or BoundaryRole.WebOnly;

    internal static BoundaryDiagnostic? InspectSource(string source, string owner, BoundaryRole role)
    {
        source = source.Replace("[assembly: InternalsVisibleTo(\"ImmichReverseGeo.Worker\")]", "", StringComparison.Ordinal);
        return Regex.IsMatch(source, @"ImmichReverseGeo\.(Overture|Gadm|Worker)\b|\b(DuckDB|NetTopologySuite|GeoJSON\w*)\b|\b(Assembly\.Load|NativeLibrary\.Load|DllImport)\b", RegexOptions.CultureInvariant)
            ? Diagnostic("SourceDependency", role, owner, "heavy namespace or opaque activation", owner, "source -> heavy import or activation") : null;
    }

    internal static string? HeavyAssembly(string? name)
    {
        return name switch
        {
            "ImmichReverseGeo.Worker" => "worker implementation",
            "ImmichReverseGeo.Overture" => "Overture geodata",
            "ImmichReverseGeo.Gadm" => "GADM geodata",
            _ when name?.StartsWith("DuckDB", StringComparison.Ordinal) == true => "native/DuckDB",
            _ when name?.StartsWith("NetTopologySuite", StringComparison.Ordinal) == true => "geometry/index/prepared geometry",
            _ when name?.StartsWith("GeoJSON", StringComparison.Ordinal) == true => "geometry reader",
            _ => null
        };
    }

    internal static string? ForbiddenType(Type type, BoundaryRole role)
    {
        if (IsWeb(role))
        {
            return HeavyTypes.GetValueOrDefault(type) ?? HeavyAssembly(type.Assembly.GetName().Name);
        }
        if (typeof(IComponent).IsAssignableFrom(type))
        {
            return "Web presentation";
        }
        return WebOnlyTypes.Contains(type) ? "Web control ownership" : null;
    }

    internal static bool IsApplication(Type? type)
    {
        string? name = type?.Assembly.GetName().Name;
        return name is "ImmichReverseGeo.Web.ControlPlane" or "ImmichReverseGeo.Core"
            or "ImmichReverseGeo.Worker" or "ImmichReverseGeo.Overture" or "ImmichReverseGeo.Gadm"
            or "ImmichReverseGeo.Tests";
    }

    internal static BoundaryDiagnostic Diagnostic(string rule, BoundaryRole role, string root,
        string category, string offender, string path) =>
        new(rule, role, root, category, offender, path.Split(" -> "), Remediation);

    internal static IReadOnlyList<BoundaryDiagnostic> Sort(IEnumerable<BoundaryDiagnostic> diagnostics) => diagnostics
        .GroupBy(d => (d.Rule, d.Role, d.Root, d.Category, d.Offender))
        .Select(group => group.OrderBy(d => d.Path.Count).ThenBy(d => d.ToString(), StringComparer.Ordinal).First())
        .OrderBy(d => d.ToString(), StringComparer.Ordinal).ToArray();
}
