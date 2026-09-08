using System.Collections.Concurrent;
using System.Reflection;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change44")]
public sealed class ProcessAssetsWebStatusTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void Startup_UsesOnlyTheResolvedWebModeAndBeginsIdle()
    {
        var standard = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var webOnly = new ProcessAssetsWebStatus(DeploymentMode.WebOnly);

        Assert.AreEqual(WebDeploymentMode.Standard, standard.Current.Mode);
        Assert.AreEqual(InternalSchedulingPolicy.Available, standard.Current.SchedulePolicy);
        Assert.AreEqual("Standard", standard.Current.ModeLabel);
        Assert.AreEqual(ProcessAssetsWorkerState.Idle, standard.Current.Worker);
        Assert.AreEqual(0L, standard.Current.Revision);
        Assert.IsNull(standard.Current.FailureSummary);

        Assert.AreEqual(WebDeploymentMode.WebOnly, webOnly.Current.Mode);
        Assert.AreEqual(InternalSchedulingPolicy.DisabledByDeploymentMode, webOnly.Current.SchedulePolicy);
        Assert.AreEqual("Web-only", webOnly.Current.ModeLabel);
        Assert.AreEqual(ProcessAssetsWorkerState.Idle, webOnly.Current.Worker);
        Assert.AreEqual(0L, webOnly.Current.Revision);

        _ = Assert.ThrowsExactly<ArgumentException>(() => new ProcessAssetsWebStatus(DeploymentMode.RunOnce));
    }

    [TestMethod]
    public async Task ExactSessionTransitions_RetainFailureUntilReplacementAdmissionAndRejectStaleUpdates()
    {
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var sink = (IProcessAssetsWorkerStatusSink)status;
        var delivered = new ConcurrentQueue<ProcessAssetsWebStatusSnapshot>();
        var failedDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = status.Subscribe(snapshot =>
        {
            delivered.Enqueue(snapshot);
            if (snapshot.Worker == ProcessAssetsWorkerState.Failed)
            {
                failedDelivered.TrySetResult();
            }
            if (snapshot.Worker == ProcessAssetsWorkerState.Starting && snapshot.Revision == 5)
            {
                replacementDelivered.TrySetResult();
            }
        });
        var first = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var replacement = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);

        sink.Admit(first, cancellationAlreadyWon: false);
        sink.ObserveTransport(first, WorkerRunTransportPhase.Resolving);
        sink.ObserveTransport(first, WorkerRunTransportPhase.Ready);
        sink.ObserveTransport(first, WorkerRunTransportPhase.Accepted);
        sink.ObserveCancellation(first);
        sink.ObserveTransport(first, WorkerRunTransportPhase.Accepted);
        sink.ObserveFinality(first, ProcessingRunOutcome.Failed, WorkerRunFailureCategory.Crash);
        sink.ObserveTransport(first, WorkerRunTransportPhase.Accepted);
        sink.ObserveCancellation(first);
        sink.Release(first);

        await failedDelivered.Task.WaitAsync(Bound);
        Assert.AreEqual(ProcessAssetsWorkerState.Failed, status.Current.Worker);
        Assert.AreEqual(4L, status.Current.Revision);
        Assert.AreEqual(WorkerRunDiagnostics.Describe(WorkerRunFailureCategory.Crash), status.Current.FailureSummary);

        sink.Admit(replacement, cancellationAlreadyWon: false);
        sink.ObserveTransport(first, WorkerRunTransportPhase.Accepted);
        sink.ObserveFinality(first, ProcessingRunOutcome.Completed, WorkerRunFailureCategory.Terminal);
        sink.Release(first);

        await replacementDelivered.Task.WaitAsync(Bound);
        Assert.AreEqual(ProcessAssetsWorkerState.Starting, status.Current.Worker);
        Assert.AreEqual(5L, status.Current.Revision);
        Assert.IsNull(status.Current.FailureSummary);
        CollectionAssert.AreEqual(
            new[]
            {
                ProcessAssetsWorkerState.Starting,
                ProcessAssetsWorkerState.Running,
                ProcessAssetsWorkerState.Cancelling,
                ProcessAssetsWorkerState.Failed,
                ProcessAssetsWorkerState.Starting
            },
            delivered.Select(snapshot => snapshot.Worker).ToArray());
        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3, 4, 5 },
            delivered.Select(snapshot => snapshot.Revision).ToArray());

        sink.ObserveFinality(replacement, ProcessingRunOutcome.Completed, WorkerRunFailureCategory.Terminal);
        sink.ObserveTransport(replacement, WorkerRunTransportPhase.Accepted);
        sink.ObserveCancellation(replacement);
        Assert.AreEqual(ProcessAssetsWorkerState.Starting, status.Current.Worker);
        Assert.AreEqual(5L, status.Current.Revision);

        sink.Release(replacement);
        Assert.AreEqual(ProcessAssetsWorkerState.Idle, status.Current.Worker);
        Assert.AreEqual(6L, status.Current.Revision);
    }

    [TestMethod]
    public async Task CancelledBeforeAdmission_DisplaysCancellingWithoutTransientStarting()
    {
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var delivered = new TaskCompletionSource<ProcessAssetsWebStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = status.Subscribe(snapshot => delivered.TrySetResult(snapshot));
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);

        ((IProcessAssetsWorkerStatusSink)status).Admit(request, cancellationAlreadyWon: true);

        var snapshot = await delivered.Task.WaitAsync(Bound);
        Assert.AreEqual(ProcessAssetsWorkerState.Cancelling, snapshot.Worker);
        Assert.AreEqual(1L, snapshot.Revision);
        Assert.AreEqual(ProcessAssetsWorkerState.Cancelling, status.Current.Worker);
    }

    [TestMethod]
    public async Task DelayedEarlierTransportObserver_CannotRegressAcceptedWorker()
    {
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var sink = (IProcessAssetsWorkerStatusSink)status;
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var readyObserverEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReadyObserver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finality = new WorkerRunFinalityState(phase =>
        {
            if (phase == WorkerRunTransportPhase.Ready)
            {
                readyObserverEntered.TrySetResult();
                releaseReadyObserver.Task.GetAwaiter().GetResult();
            }

            sink.ObserveTransport(request, phase);
        });
        sink.Admit(request, cancellationAlreadyWon: false);

        var delayedReady = Task.Run(() => finality.AdvanceTransport(WorkerRunTransportPhase.Ready));
        try
        {
            await readyObserverEntered.Task.WaitAsync(Bound);
            finality.AdvanceTransport(WorkerRunTransportPhase.Accepted);
            Assert.AreEqual(ProcessAssetsWorkerState.Running, status.Current.Worker);
            var acceptedRevision = status.Current.Revision;

            releaseReadyObserver.TrySetResult();
            await delayedReady.WaitAsync(Bound);

            Assert.AreEqual(ProcessAssetsWorkerState.Running, status.Current.Worker);
            Assert.AreEqual(acceptedRevision, status.Current.Revision);
        }
        finally
        {
            releaseReadyObserver.TrySetResult();
            await delayedReady.WaitAsync(Bound);
        }
    }

    [TestMethod]
    public async Task ReentrantAndConcurrentPublications_DeliverEffectiveRevisionsOnceInOrder()
    {
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var sink = (IProcessAssetsWorkerStatusSink)status;
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var delivered = new ConcurrentQueue<ProcessAssetsWebStatusSnapshot>();
        var cancellingDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visibilityViolation = 0;
        using var subscription = status.Subscribe(snapshot =>
        {
            if (status.Current.Revision < snapshot.Revision)
            {
                Interlocked.Exchange(ref visibilityViolation, 1);
            }
            delivered.Enqueue(snapshot);
            if (snapshot.Worker == ProcessAssetsWorkerState.Starting)
            {
                sink.ObserveTransport(request, WorkerRunTransportPhase.Accepted);
            }
            else if (snapshot.Worker == ProcessAssetsWorkerState.Cancelling)
            {
                cancellingDelivered.TrySetResult();
            }
        });

        sink.Admit(request, cancellationAlreadyWon: false);
        await Task.WhenAll(
            Task.Run(() => sink.ObserveCancellation(request)),
            Task.Run(() => sink.ObserveTransport(request, WorkerRunTransportPhase.Accepted)));
        await cancellingDelivered.Task.WaitAsync(Bound);

        ProcessAssetsWebStatusSnapshot[] snapshots = delivered.ToArray();
        CollectionAssert.AreEqual(
            Enumerable.Range(1, snapshots.Length).Select(value => (long)value).ToArray(),
            snapshots.Select(snapshot => snapshot.Revision).ToArray());
        Assert.AreEqual(snapshots.Length, snapshots.Select(snapshot => snapshot.Revision).Distinct().Count());
        Assert.AreEqual(ProcessAssetsWorkerState.Starting, snapshots[0].Worker);
        Assert.AreEqual(ProcessAssetsWorkerState.Cancelling, snapshots[^1].Worker);
        Assert.IsTrue(snapshots.Length is 2 or 3);
        Assert.AreEqual(ProcessAssetsWorkerState.Cancelling, status.Current.Worker);
        Assert.AreEqual(0, visibilityViolation);
    }

    [TestMethod]
    public void RawPhaseMapping_IsExhaustiveAndRejectsAnUnknownFutureValue()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                ProcessAssetsWorkerState.Starting,
                ProcessAssetsWorkerState.Starting,
                ProcessAssetsWorkerState.Starting,
                ProcessAssetsWorkerState.Starting,
                ProcessAssetsWorkerState.Starting,
                ProcessAssetsWorkerState.Running,
                ProcessAssetsWorkerState.Running,
                ProcessAssetsWorkerState.Running,
                ProcessAssetsWorkerState.Running
            },
            Enum.GetValues<WorkerRunTransportPhase>()
                .Select(phase => ProcessAssetsWebStatus.MapTransportPhase(
                    phase,
                    ProcessAssetsWorkerState.Running,
                    cancellationWon: false))
                .ToArray());

        Assert.AreEqual(
            ProcessAssetsWorkerState.Cancelling,
            ProcessAssetsWebStatus.MapTransportPhase(
                WorkerRunTransportPhase.Accepted,
                ProcessAssetsWorkerState.Cancelling,
                cancellationWon: true));

        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ProcessAssetsWebStatus.MapTransportPhase(
                (WorkerRunTransportPhase)int.MaxValue,
                ProcessAssetsWorkerState.Idle,
                cancellationWon: false));
    }

    [TestMethod]
    public void PublicSnapshotAndLookupDataPages_ExposeNoIdentityProcessOrGenericJobContract()
    {
        CollectionAssert.AreEquivalent(
            new[] { "Mode", "SchedulePolicy", "Worker", "FailureSummary", "Revision" },
            typeof(ProcessAssetsWebStatusSnapshot)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(property => property.Name)
                .ToArray());

        foreach (Type page in new[]
        {
            typeof(ImmichReverseGeo.Web.Components.Pages.Lookup),
            typeof(ImmichReverseGeo.Web.Components.Pages.Data)
        })
        {
            Assert.IsFalse(page
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(property => property.PropertyType == typeof(IProcessAssetsWebStatus)
                    || property.PropertyType == typeof(IProcessAssetsWorkerStatusSink)));
        }

        Assert.IsFalse(Enum.GetNames<ProcessAssetsWorkerState>()
            .Any(name => name.Contains("Lookup", StringComparison.Ordinal)
                || name.Contains("Cache", StringComparison.Ordinal)
                || name.Contains("Job", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RunOnceComposition_RegistersNoWebStatusSurface()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "immich-reversegeo-change44-run-once",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            services.AddRunOnceComposition(
                ApplicationCompositionContext.Create(
                    CompositionEnvironment.Development,
                    root,
                    Path.Combine(root, "data"),
                    Path.Combine(root, "config"),
                    DeploymentMode.RunOnce),
                new WorkerProcessExitOutcomeAccumulator(),
                TextWriter.Null,
                TextWriter.Null);

            Assert.IsFalse(services.Any(descriptor =>
                descriptor.ServiceType == typeof(ProcessAssetsWebStatus)
                || descriptor.ServiceType == typeof(IProcessAssetsWebStatus)
                || descriptor.ServiceType == typeof(IProcessAssetsWorkerStatusSink)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void WebOnlyComposition_UsesResolvedModeAndRestartCreatesFreshIdleStatus()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "immich-reversegeo-change44-web-only",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var context = ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            root,
            Path.Combine(root, "data"),
            Path.Combine(root, "config"),
            DeploymentMode.WebOnly);
        try
        {
            var firstServices = new ServiceCollection();
            firstServices.AddWebOnlyWebComposition(context);
            using ServiceProvider firstProvider = firstServices.BuildServiceProvider();
            var first = firstProvider.GetRequiredService<ProcessAssetsWebStatus>();
            Assert.AreSame(first, firstProvider.GetRequiredService<IProcessAssetsWebStatus>());
            Assert.AreSame(first, firstProvider.GetRequiredService<IProcessAssetsWorkerStatusSink>());
            Assert.AreEqual(WebDeploymentMode.WebOnly, first.Current.Mode);
            Assert.AreEqual(InternalSchedulingPolicy.DisabledByDeploymentMode, first.Current.SchedulePolicy);

            var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
            var firstSink = (IProcessAssetsWorkerStatusSink)first;
            firstSink.Admit(request, cancellationAlreadyWon: false);
            firstSink.ObserveFinality(
                request,
                ProcessingRunOutcome.Failed,
                WorkerRunFailureCategory.Crash);
            firstSink.Release(request);
            Assert.AreEqual(ProcessAssetsWorkerState.Failed, first.Current.Worker);

            var restartedServices = new ServiceCollection();
            restartedServices.AddWebOnlyWebComposition(context);
            using ServiceProvider restartedProvider = restartedServices.BuildServiceProvider();
            var restarted = restartedProvider.GetRequiredService<ProcessAssetsWebStatus>();
            Assert.AreNotSame(first, restarted);
            Assert.AreEqual(WebDeploymentMode.WebOnly, restarted.Current.Mode);
            Assert.AreEqual(InternalSchedulingPolicy.DisabledByDeploymentMode, restarted.Current.SchedulePolicy);
            Assert.AreEqual(ProcessAssetsWorkerState.Idle, restarted.Current.Worker);
            Assert.AreEqual(0L, restarted.Current.Revision);
            Assert.IsNull(restarted.Current.FailureSummary);
            Assert.AreEqual(ProcessAssetsWorkerState.Failed, first.Current.Worker);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
