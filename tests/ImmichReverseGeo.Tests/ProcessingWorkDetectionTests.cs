using System.Reflection;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Tests.ApplicationComposition;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change57")]
public sealed class ProcessingWorkDetectionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [TestMethod]
    public void DashboardStatisticsKeepTheirIndependentExactRepositoryCount()
    {
        MethodInfo loadStats = typeof(ImmichReverseGeo.Web.Components.Pages.Dashboard)
            .GetMethod("LoadStatsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Type stateMachine = loadStats.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()!.StateMachineType;
        MethodInfo moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodBase[] calls = BoundaryIlMetadata.Read(moveNext).OfType<MethodBase>().ToArray();
        Assert.AreEqual(1, calls.Count(m => m.DeclaringType == typeof(ImmichDbRepository)
            && m.Name == nameof(ImmichDbRepository.GetUnprocessedCountAsync)),
            "The compiled Dashboard statistics path must retain its own exact-count call.");
        Assert.IsFalse(calls.Any(m => m.DeclaringType == typeof(IProcessingWorkDetector)),
            "A bool-only scheduled observation cannot supply Dashboard statistics.");
    }

    [TestMethod]
    public void ContractsHaveOnlyTheReviewedImmutableShape()
    {
        AssertShape<ProcessingWorkDetectionRequest>(
            ("Trigger", typeof(ProcessingRunTrigger)), ("Snapshot", typeof(ProcessingWorkDetectionSnapshot)));
        AssertShape<ProcessingWorkDetectionSnapshot>(
            ("Purpose", typeof(ProcessingWorkDetectionPurpose)), ("Coverage", typeof(ProcessingWorkDetectionCoverage)));
        AssertShape<ProcessingWorkDetectionResult>(
            ("HasWork", typeof(bool)), ("Diagnostics", typeof(ProcessingWorkDetectionDiagnostics)));
        AssertShape<ProcessingWorkDetectionDiagnostics>(
            ("ImplementationKind", typeof(ProcessingWorkDetectorKind)),
            ("Coverage", typeof(ProcessingWorkDetectionCoverage)), ("UsedFallback", typeof(bool)));
        CollectionAssert.AreEqual(new[] { "ScheduledLaunch" }, Enum.GetNames<ProcessingWorkDetectionPurpose>());
        CollectionAssert.AreEqual(new[] { "FullEligibility" }, Enum.GetNames<ProcessingWorkDetectionCoverage>());
        CollectionAssert.AreEqual(new[] { "CountBacked", "Existence" }, Enum.GetNames<ProcessingWorkDetectorKind>());
        Assert.IsFalse(typeof(IProcessingWorkDetector).IsPublic);
        MethodInfo method = typeof(IProcessingWorkDetector).GetMethods().Single();
        Assert.AreEqual("DetectAsync", method.Name);
        Assert.AreEqual(typeof(Task<ProcessingWorkDetectionResult>), method.ReturnType);
        CollectionAssert.AreEqual(new[] { typeof(ProcessingWorkDetectionRequest), typeof(CancellationToken) },
            method.GetParameters().Select(p => p.ParameterType).ToArray());
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(64)]
    [DataRow(int.MaxValue)]
    public void UnsupportedPurposeAndCoverageCannotIntroduceIncrementalOrNasState(int value)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ProcessingWorkDetectionSnapshot(
            (ProcessingWorkDetectionPurpose)value, ProcessingWorkDetectionCoverage.FullEligibility));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ProcessingWorkDetectionSnapshot(
            ProcessingWorkDetectionPurpose.ScheduledLaunch, (ProcessingWorkDetectionCoverage)value));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ProcessingWorkDetectionDiagnostics(
            ProcessingWorkDetectorKind.CountBacked, (ProcessingWorkDetectionCoverage)value, false));
    }

    [TestMethod]
    public void OnlyScheduledTriggerAndBoundedDiagnosticKindsAreAccepted()
    {
        foreach (ProcessingRunTrigger trigger in new[] { ProcessingRunTrigger.Manual, ProcessingRunTrigger.RunOnce, (ProcessingRunTrigger)(-1) })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ProcessingWorkDetectionRequest(
                trigger, ProcessingWorkDetectionSnapshot.Current));
        }
        foreach (int kind in new[] { -1, 2, int.MaxValue })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ProcessingWorkDetectionDiagnostics(
                (ProcessingWorkDetectorKind)kind, ProcessingWorkDetectionCoverage.FullEligibility, false));
        }
        Assert.ThrowsExactly<ArgumentNullException>(() => new ProcessingWorkDetectionRequest(ProcessingRunTrigger.Scheduled, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ProcessingWorkDetectionResult(false, null!));
        ProcessingWorkDetectionRequest request = ProcessingWorkDetectorStub.Request();
        Assert.AreSame(ProcessingWorkDetectionSnapshot.Current, request.Snapshot);
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, request.Trigger);
    }

    [TestMethod]
    [DataRow(0L, false)]
    [DataRow(1L, true)]
    [DataRow(long.MaxValue, true)]
    public async Task AdapterCallsOneCountWithExactTokenAndConstantSafeMetadata(long count, bool expected)
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var adapter = new CountBackedProcessingWorkDetector(token =>
        {
            Assert.AreEqual(cancellation.Token, token);
            Interlocked.Increment(ref calls);
            return Task.FromResult(count);
        });
        Assert.AreEqual(0, calls);
        ProcessingWorkDetectionResult result = await adapter.DetectAsync(
            ProcessingWorkDetectorStub.Request(), cancellation.Token).WaitAsync(Bound);
        Assert.AreEqual(expected, result.HasWork);
        Assert.AreEqual(ProcessingWorkDetectorKind.CountBacked, result.Diagnostics.ImplementationKind);
        Assert.AreEqual(ProcessingWorkDetectionCoverage.FullEligibility, result.Diagnostics.Coverage);
        Assert.IsFalse(result.Diagnostics.UsedFallback);
        Assert.AreEqual(1, calls);
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => adapter.DetectAsync(null!, cancellation.Token));
        Assert.AreEqual(1, calls, "Invalid requests never reach the count boundary.");
    }

    [TestMethod]
    public async Task SingletonAdapterHasIndependentConcurrentReadsAndRetainsNoInvocationState()
    {
        using var firstToken = new CancellationTokenSource();
        using var secondToken = new CancellationTokenSource();
        var firstRead = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new System.Collections.Concurrent.ConcurrentQueue<CancellationToken>();
        var adapter = new CountBackedProcessingWorkDetector(token =>
        {
            entered.Enqueue(token);
            return token == firstToken.Token ? firstRead.Task : secondRead.Task;
        });
        Task<ProcessingWorkDetectionResult> first = adapter.DetectAsync(ProcessingWorkDetectorStub.Request(), firstToken.Token);
        Task<ProcessingWorkDetectionResult> second = adapter.DetectAsync(ProcessingWorkDetectorStub.Request(), secondToken.Token);
        CollectionAssert.AreEqual(new[] { firstToken.Token, secondToken.Token }, entered.ToArray(), "Both real adapter reads entered before completion.");
        secondRead.SetResult(0);
        Assert.IsFalse((await second.WaitAsync(Bound)).HasWork);
        Assert.IsFalse(first.IsCompleted, "First read is held by its explicit uncompleted source after both reads entered.");
        firstRead.SetResult(3);
        Assert.IsTrue((await first.WaitAsync(Bound)).HasWork);
        FieldInfo[] fields = typeof(CountBackedProcessingWorkDetector).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.AreEqual(1, fields.Length);
        Assert.IsTrue(fields[0].IsInitOnly);
        Assert.AreEqual(typeof(Func<CancellationToken, Task<long>>), fields[0].FieldType);
    }

    [TestMethod]
    public async Task StandardConcreteAndInterfaceShareOneLazyNonHostedSingleton()
    {
        using var fixture = ControlPlaneRolePolicyTests.RoleDescriptors.Create(BoundaryRole.Standard);
        var counter = new Counter();
        fixture.Services.RemoveAll<IScheduledRunWorkCounter>();
        fixture.Services.AddSingleton<IScheduledRunWorkCounter>(counter);
        using ServiceProvider provider = fixture.Services.BuildServiceProvider();
        var concrete = provider.GetRequiredService<CountBackedProcessingWorkDetector>();
        var detector = provider.GetRequiredService<IProcessingWorkDetector>();
        Assert.AreSame(concrete, detector);
        Assert.AreSame(detector, provider.GetRequiredService<IProcessingWorkDetector>());
        Assert.AreEqual(0, counter.Calls);
        foreach (Type type in new[] { typeof(CountBackedProcessingWorkDetector), typeof(IProcessingWorkDetector) })
        {
            Assert.AreEqual(ServiceLifetime.Singleton, fixture.Services.Single(d => d.ServiceType == type).Lifetime);
            Assert.IsFalse(typeof(IHostedService).IsAssignableFrom(type));
            Assert.IsFalse(typeof(IDisposable).IsAssignableFrom(type));
        }
        Assert.IsTrue((await detector.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None)).HasWork);
        Assert.AreEqual(1, counter.Calls);
        foreach (BoundaryRole role in new[] { BoundaryRole.WebOnly, BoundaryRole.InternalWorker, BoundaryRole.RunOnce })
        {
            using var other = ControlPlaneRolePolicyTests.RoleDescriptors.Create(role);
            Assert.IsFalse(other.Services.Any(d => d.ServiceType == typeof(IProcessingWorkDetector)
                || d.ServiceType == typeof(CountBackedProcessingWorkDetector)), role.ToString());
        }
    }

    [TestMethod]
    public async Task SharedFakesCaptureExactRequestsAndSupportIndependentCompletionCancellationAndFailure()
    {
        var gate = new GatedProcessingWorkDetector();
        using var cancellation = new CancellationTokenSource();
        ProcessingWorkDetectionRequest firstRequest = ProcessingWorkDetectorStub.Request();
        ProcessingWorkDetectionRequest secondRequest = ProcessingWorkDetectorStub.Request();
        Task<ProcessingWorkDetectionResult> first = gate.DetectAsync(firstRequest, cancellation.Token);
        Task<ProcessingWorkDetectionResult> second = gate.DetectAsync(secondRequest, CancellationToken.None);
        var firstCall = await gate.NextAsync().WaitAsync(Bound);
        var secondCall = await gate.NextAsync().WaitAsync(Bound);
        Assert.AreSame(firstRequest, firstCall.Request);
        Assert.AreSame(firstRequest.Snapshot, firstCall.Request.Snapshot);
        Assert.AreEqual(cancellation.Token, firstCall.Token);
        Assert.AreSame(secondRequest, secondCall.Request);
        secondCall.Release(ProcessingWorkDetectorStub.Result(true, ProcessingWorkDetectorKind.Existence, true));
        Assert.IsTrue((await second.WaitAsync(Bound)).HasWork);
        cancellation.Cancel();
        firstCall.Cancel();
        OperationCanceledException cancelled = await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        Assert.AreEqual(cancellation.Token, cancelled.CancellationToken);
        Assert.AreEqual(2, gate.Calls.Length);
        var failure = new InvalidOperationException("detector-fault-witness");
        var fault = ProcessingWorkDetectorStub.Throwing(failure);
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fault.DetectAsync(firstRequest, CancellationToken.None)));
        Assert.AreEqual(1, fault.Calls.Length);
        await Assert.ThrowsAsync<OperationCanceledException>(() => ProcessingWorkDetectorStub.Cancelled().DetectAsync(firstRequest, cancellation.Token));
    }

    private static void AssertShape<T>(params (string Name, Type Type)[] expected)
    {
        PropertyInfo[] properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        CollectionAssert.AreEquivalent(expected.Select(p => p.Name).ToArray(), properties.Select(p => p.Name).ToArray());
        foreach ((string name, Type type) in expected)
        {
            PropertyInfo property = properties.Single(p => p.Name == name);
            Assert.AreEqual(type, property.PropertyType);
            Assert.IsNull(property.SetMethod, name + " cannot bypass constructor validation through init or with.");
        }
        Assert.IsTrue(typeof(T).IsSealed);
        Assert.IsFalse(typeof(T).IsPublic);
    }

    private sealed class Counter : IScheduledRunWorkCounter
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(1L);
        }
    }
}
