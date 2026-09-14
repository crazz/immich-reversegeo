using System.Reflection;
using System.Runtime.CompilerServices;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change14")]
public class ProcessingRunExecutorArchitectureTests
{
    [TestMethod]
    public void DirectFixtureDependencies_ContainNoForbiddenRuntimeTypeOrSleep()
    {
        var assembly = typeof(ProcessingRunExecutorArchitectureTests).Assembly;
        var forbiddenTypeFragments = new[]
        {
            "ProcessingBackgroundService", "ProcessingRunCoordinator", "Cronos", "ProcessingState",
            "Npgsql", "Sqlite", "Blazor", "OvertureDivisionsService", "OverturePlacesService", "IHostedService"
        };
        var forbiddenMethods = ForbiddenMethods();
        var activeRoots = ExecutorVerificationCatalog.Methods.Where(item => item.Active
            && item.DeclaringType != typeof(ProcessingRunExecutorArchitectureTests).FullName
            && item.DeclaringType != typeof(ProcessingRunExecutorManifestTests).FullName).Select(item =>
        {
            var type = assembly.GetType(item.DeclaringType, true)!;
            return (MethodBase)(type.GetMethod(item.MethodId, BindingFlags.Instance | BindingFlags.Public, null,
                item.ParameterTypes.Select(ExecutorVerificationCatalog.ResolveType).ToArray(), null)
                ?? throw new AssertFailedException($"Missing active root {item.DeclaringType}.{item.MethodId}."));
        });
        var supportRoots = ExecutorVerificationCatalog.SupportTypes.Where(type => type != typeof(TransitiveIlWalker)).SelectMany(type =>
            type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)));
        var walk = TransitiveIlWalker.Walk(activeRoots.Concat(supportRoots), assembly);

        var forbiddenReferences = walk.Members.OfType<MethodBase>()
            .Where(member => forbiddenMethods.Contains((member.DeclaringType?.FullName, member.Name))).ToArray();
        Assert.AreEqual(0, forbiddenReferences.Length, string.Join(Environment.NewLine, forbiddenReferences.Select(item => $"{item.DeclaringType?.FullName}.{item}")));
        var forbiddenFilesystem = walk.Members.Where(TransitiveIlWalker.IsForbiddenFilesystemMember).ToArray();
        Assert.AreEqual(0, forbiddenFilesystem.Length, string.Join(Environment.NewLine, forbiddenFilesystem));
        var synchronousWaits = walk.Methods.Where(TransitiveIlWalker.ContainsSynchronousAwaiterGetResult).ToArray();
        Assert.AreEqual(0, synchronousWaits.Length, string.Join(Environment.NewLine, synchronousWaits));
        Assert.IsFalse(walk.Members.Any(member => forbiddenTypeFragments.Any(fragment =>
            member.DeclaringType?.FullName?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true)));
        foreach (var type in ExecutorVerificationCatalog.SupportTypes)
        {
            var surface = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(field => field.FieldType)
                .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .SelectMany(ctor => ctor.GetParameters().Select(parameter => parameter.ParameterType)));
            Assert.IsFalse(surface.Any(candidate => forbiddenTypeFragments.Any(fragment =>
                candidate.FullName?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true)), type.FullName);
        }
    }

    [TestMethod]
    public void TransitiveIlWalker_DetectsForbiddenMembersHiddenInAsyncLambdaSentinel()
    {
        var method = typeof(ExcludedForbiddenIlSentinel).GetMethod("ExecuteAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var members = TransitiveIlWalker.Walk([method], typeof(ProcessingRunExecutorArchitectureTests).Assembly).Members.OfType<MethodBase>().ToArray();
        var forbidden = ForbiddenMethods();
        Assert.IsTrue(members.Any(member => forbidden.Contains((member.DeclaringType?.FullName, member.Name)) && member.Name == nameof(Task.Delay)));
        Assert.IsTrue(members.Any(member => member.DeclaringType == typeof(File) && member.Name == nameof(File.ReadAllText)));
        Assert.IsTrue(members.Any(member => member.DeclaringType == typeof(FileInfo) && TransitiveIlWalker.IsForbiddenFilesystemMember(member)));
        Assert.IsTrue(members.Any(member => member.DeclaringType == typeof(FileStream) && TransitiveIlWalker.IsForbiddenFilesystemMember(member)));
        Assert.IsTrue(members.Any(member => member.DeclaringType == typeof(Directory) && member.Name == nameof(Directory.EnumerateFiles)
            && TransitiveIlWalker.IsForbiddenFilesystemMember(member)));
        Assert.IsTrue(members.Where(TransitiveIlWalker.IsForbiddenFilesystemMember)
            .Select(member => member.DeclaringType).Distinct().Count() >= 4);
        Assert.IsTrue(TransitiveIlWalker.Walk([method], typeof(ProcessingRunExecutorArchitectureTests).Assembly)
            .Methods.Any(TransitiveIlWalker.ContainsSynchronousAwaiterGetResult));
    }

    [TestMethod]
    public void TransitiveIlWalker_IsolatesNeighboringAsyncRootsAndTheirStateMachines()
    {
        var assembly = typeof(ProcessingRunExecutorArchitectureTests).Assembly;
        var validRoot = typeof(ExcludedRootIsolationIlSentinel).GetMethod("ValidRootAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var unboundRoot = typeof(ExcludedRootIsolationIlSentinel).GetMethod("UnboundNeighborAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var valid = TransitiveIlWalker.Walk([validRoot], assembly);
        var unbound = TransitiveIlWalker.Walk([unboundRoot], assembly);

        Assert.IsTrue(valid.Members.OfType<MethodBase>().Any(member => member.DeclaringType == typeof(ExcludedRootIsolationIlSentinel) && member.Name == "RequiredMarker"));
        Assert.IsFalse(valid.Members.OfType<MethodBase>().Any(member => member.DeclaringType == typeof(ExcludedRootIsolationIlSentinel) && member.Name == "UnboundMarker"));
        Assert.IsFalse(valid.Members.OfType<MethodBase>().Any(member => member.DeclaringType == typeof(File) && member.Name == nameof(File.ReadAllText)));
        Assert.IsTrue(unbound.Members.OfType<MethodBase>().Any(member => member.DeclaringType == typeof(ExcludedRootIsolationIlSentinel) && member.Name == "UnboundMarker"));
        Assert.IsFalse(unbound.Members.OfType<MethodBase>().Any(member => member.DeclaringType == typeof(ExcludedRootIsolationIlSentinel) && member.Name == "RequiredMarker"));
        Assert.IsTrue(unbound.Members.OfType<MethodBase>().Any(member => member.DeclaringType == typeof(File) && member.Name == nameof(File.ReadAllText)));
    }

    private static HashSet<(string? Type, string Name)> ForbiddenMethods() =>
    [
        (typeof(Thread).FullName, nameof(Thread.Sleep)),
        (typeof(Task).FullName, nameof(Task.Delay)),
        (typeof(Task).FullName, nameof(Task.Wait)),
        (typeof(Directory).FullName, nameof(Directory.GetFiles)),
        (typeof(File).FullName, nameof(File.ReadAllText)),
        (typeof(File).FullName, nameof(File.Exists))
    ];

    [TestMethod]
    public void ExecutorCharacterizationFixture_UsesFixedUtcGatesAndOnlyApprovedInMemoryDependencies()
    {
        var fixture = new ExecutorFixture();
        Assert.IsInstanceOfType<FixedUtcTimeProvider>(fixture.TimeProvider);
        Assert.AreEqual(FixedUtcTimeProvider.Start, fixture.TimeProvider.GetUtcNow());
        Assert.AreEqual(FixedUtcTimeProvider.End, fixture.TimeProvider.GetUtcNow());
        Assert.AreEqual(TimeSpan.Zero, FixedUtcTimeProvider.Start.Offset);
        Assert.AreEqual(TimeSpan.Zero, FixedUtcTimeProvider.End.Offset);
        Assert.AreEqual(TimeSpan.FromSeconds(10), ExecutorFixture.Bound);

        var gate = new AsyncGate();
        var enteredField = typeof(AsyncGate).GetField("_entered", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var releaseField = typeof(AsyncGate).GetField("_release", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsInstanceOfType<TaskCompletionSource>(enteredField.GetValue(gate));
        Assert.IsInstanceOfType<TaskCompletionSource>(releaseField.GetValue(gate));

        Assert.IsNotNull(fixture.CountBehavior);
        Assert.IsNotNull(fixture.ConfigBehavior);
        Assert.IsNotNull(fixture.SkippedBehavior);
        Assert.IsNotNull(fixture.BatchBehavior);
        Assert.IsNotNull(fixture.ResolveBehavior);
        Assert.IsNotNull(fixture.AirportBehavior);
        Assert.IsNotNull(fixture.WriteBehavior);
        Assert.IsNotNull(fixture.AddSkippedBehavior);
        Assert.IsNotNull(fixture.DelayBehavior);
        Assert.IsNotNull(fixture.EventBehavior);
        Assert.AreEqual(67, ExecutorContractAuthority.Cases.Count);
        Assert.IsTrue(ExecutorContractAuthority.Cases.Values.All(contract => contract.NoExtras));
    }
}
