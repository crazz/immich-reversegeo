using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
public sealed class ChildWorkerOnlyCompositionTests
{
    [TestMethod]
    public void WebComposition_ExposesOneUnkeyedChildBackendAndNoAuthoritativeExecutor()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddWebComposition(ApplicationCompositionContext.Create(
                CompositionEnvironment.Development,
                fixtureRoot,
                Path.Combine(fixtureRoot, "data"),
                Path.Combine(fixtureRoot, "config")));

            var childBackends = services
                .Where(descriptor => descriptor.ServiceType.Name == "IChildProcessingRunBackend")
                .ToArray();
            Assert.AreEqual(1, childBackends.Length, "one-child-backend-descriptor");
            Assert.AreEqual(ServiceLifetime.Scoped, childBackends[0].Lifetime, "child-backend-run-scope");
            Assert.IsFalse(childBackends[0].IsKeyedService, "child-backend-is-unkeyed");

            var forbiddenServiceNames = new[]
            {
                nameof(ProcessingRunExecutor),
                nameof(IProcessingRunExecutor),
                "IProcessingRunBackend",
                "InProcessProcessingRunBackend",
                "TemporaryProcessingBackendSelection"
            };
            foreach (var forbiddenServiceName in forbiddenServiceNames)
            {
                Assert.AreEqual(
                    0,
                    services.Count(descriptor => descriptor.ServiceType.Name == forbiddenServiceName),
                    "web-excludes-" + forbiddenServiceName);
            }

            Assert.IsFalse(
                services.Any(descriptor => descriptor.IsKeyedService),
                "web-processing-composition-has-no-keyed-descriptor");

            using var provider = services.BuildServiceProvider();
            Assert.IsNull(provider.GetService<ProcessingRunExecutor>(), "web-cannot-resolve-concrete-executor");
            Assert.IsNull(provider.GetService<IProcessingRunExecutor>(), "web-cannot-resolve-executor-contract");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CoordinatorConstructors_ExposeOnlyTheRunScopeFactoryAtTheDispatchBoundary()
    {
        var constructors = typeof(ProcessingRunCoordinator)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotEmpty(constructors, "coordinator-constructors");

        foreach (var constructor in constructors)
        {
            var parameters = constructor.GetParameters();
            Assert.AreEqual(
                1,
                parameters.Count(parameter => parameter.ParameterType == typeof(IServiceScopeFactory)),
                constructor.ToString());
            Assert.IsFalse(
                parameters.Any(parameter => parameter.ParameterType.Name is
                    "TemporaryProcessingBackendSelection" or
                    nameof(IProcessingRunExecutor) or
                    nameof(ProcessingRunExecutor)),
                constructor.ToString());
        }
    }

    [TestMethod]
    public void InternalWorkerComposition_OwnsOneAuthoritativeExecutorAndNoWebCoordinator()
    {
        var services = new ServiceCollection();
        services.AddInternalWorkerComposition(ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            "/composition/change38-worker",
            null,
            null));

        Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(ProcessingRunExecutor)), "worker-executor-owner");
        Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(IProcessingRunExecutor)), "worker-executor-alias");
        Assert.AreEqual(0, services.Count(descriptor => descriptor.ServiceType == typeof(ProcessingRunCoordinator)), "worker-excludes-web-coordinator");
        Assert.AreEqual(0, services.Count(descriptor => descriptor.ServiceType == typeof(ProcessingBackgroundService)), "worker-excludes-web-scheduler");
    }

    [TestMethod]
    public void ProductionSource_HasNoTransitionalBackendAndRegistersExecutionOnlyFromWorkerComposition()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var sourceRoot = Path.Combine(repositoryRoot, "src", "ImmichReverseGeo.Web");
        var sources = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(sourceRoot, path), File.ReadAllText);

        var forbiddenPatterns = new[]
        {
            @"\bProcessingBackendKind\b",
            @"\bTemporaryProcessingBackendSelection\b",
            @"\bIProcessingRunBackend\b",
            @"\bInProcessProcessingRunBackend\b",
            @"\bSelectedProcessingBackendStartupValidator\b",
            @"\bAddKeyedScoped\b",
            @"\bGetRequiredKeyedService\b"
        };
        foreach (var forbiddenPattern in forbiddenPatterns)
        {
            Assert.IsFalse(
                sources.Any(source => Regex.IsMatch(source.Value, forbiddenPattern, RegexOptions.CultureInvariant)),
                "production-source-forbids-" + forbiddenPattern);
        }

        var executionRegistrationCallers = sources
            .Where(source => source.Value.Contains("AddWorkerExecutionComposition", StringComparison.Ordinal))
            .Select(source => source.Key)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                Path.Combine("Composition", "InternalWorkerServiceCollectionExtensions.cs"),
                Path.Combine("Composition", "WorkerExecutionServiceCollectionExtensions.cs")
            },
            executionRegistrationCallers,
            "worker-only-execution-registration-callers");

        var allowedExecutorSources = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine("Composition", "WorkerExecutionServiceCollectionExtensions.cs"),
            Path.Combine("RunOnce", "RunOnceApplication.cs"),
            Path.Combine("Services", "ProcessingRunContracts.cs"),
            Path.Combine("Services", "ProcessingRunExecutor.cs"),
            Path.Combine("WorkerHost", "InternalWorkerLifecycleService.cs")
        };
        var unexpectedExecutorSources = sources
            .Where(source => Regex.IsMatch(source.Value, @"\bI?ProcessingRunExecutor\b", RegexOptions.CultureInvariant))
            .Select(source => source.Key)
            .Where(path => !allowedExecutorSources.Contains(path))
            .ToArray();
        Assert.HasCount(0, unexpectedExecutorSources, string.Join(Environment.NewLine, unexpectedExecutorSources));

        var retiredPhrases = new[]
        {
            "Backend selection remains",
            "in-process event run"
        };
        foreach (var retiredPhrase in retiredPhrases)
        {
            Assert.IsFalse(
                sources.Any(source => source.Value.Contains(retiredPhrase, StringComparison.OrdinalIgnoreCase)),
                "production-source-forbids-retired-phrase-" + retiredPhrase);
        }
    }
}
