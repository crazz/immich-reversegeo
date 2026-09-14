using System.Collections.Concurrent;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Tests.LifecycleTelemetry;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using ImmichReverseGeo.Web.LifecycleTelemetry;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AcceptedDelivery = ImmichReverseGeo.Web.WorkerEventDelivery.WorkerEventDelivery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.ManualChildWorkerExecution;

[TestClass]
[TestCategory("Change34")]
[DoNotParallelize]
public sealed class ProcessFixtureManualCoordinatorTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [TestMethod]
    [TestCategory("Change66")]
    [DataRow("success", "completed", true)]
    [DataRow("pre-ready-crash", "startup-failed", false)]
    [DataRow("post-ready-crash", "crashed", true)]
    [DataRow("malformed", "protocol-failed", true)]
    [DataRow("terminal-mismatch", "terminal-exit-mismatch", true)]
    [DataRow("stderr-flood", "completed", true)]
    [DataRow("memory-unavailable", "completed", true)]
    public async Task RealProductionFinality_EmitsOneSafeClassification(string scenario, string classification, bool ready)
    {
        using var logs = new RecordingLifecycleLogs();
        string[] options = scenario switch
        {
            "pre-ready-crash" or "post-ready-crash" => ["--exit-code", "42"],
            "malformed" => ["--malformed-kind", "json"],
            "terminal-mismatch" => ["--terminal", "completed", "--exit-code", "3"],
            "stderr-flood" => ["--stderr-bytes", "262145"],
            _ => []
        };
        var plan = new ProcessFixturePlan(scenario == "memory-unavailable" ? "success" : scenario, ready, options)
        {
            UnavailableMemory = scenario == "memory-unavailable" ? ChildWorkingSetUnavailable.NotSupported : null
        };
        await using var fixture = ProcessFixtureHost.Create([plan], TimeProvider.System,
            logs.CreateLogger(LifecycleEventCatalog.Category));
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound));
        await fixture.Launcher.WaitForLaunchCountAsync(1).WaitAsync(Bound);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        var lease = fixture.Launcher.Leases.Single();
        var raw = await lease.CompleteAsync().WaitAsync(Bound);
        await lease.Session!.Telemetry!.CancellationObservation.WaitAsync(Bound);
        await lease.Session.Telemetry.CoalescingObservation.WaitAsync(Bound);
        var final = logs.Entries.Single(entry => entry.Event.Id == 6641);
        Assert.AreEqual(classification, final["process_classification"]);
        if (scenario == "memory-unavailable")
        {
            Assert.AreEqual("unavailable", final["memory_observation"]);
            Assert.AreEqual("not-supported", final["memory_unavailable_reason"]);
            Assert.IsNull(final["peak_working_set_bytes"]);
            Assert.AreEqual(0L, final["memory_sample_count"]);
        }
        Assert.AreEqual(ready, final["ready_observed"]);
        Assert.AreEqual(ready ? 1 : 0, logs.Entries.Count(entry => entry.Event.Id == 6612));
        Assert.AreEqual(final["terminal_outcome"] is null ? 0 : 1, logs.Entries.Count(entry => entry.Event.Id == 6640));
        foreach (var entry in logs.Entries)
        {
            Assert.AreEqual(lease.Request.RunId, entry["job_id"]);
            Assert.AreEqual("ProcessAssets", entry["job_kind"]);
            Assert.AreEqual("dashboard-manual", entry["job_origin"]);
            Assert.AreEqual(Environment.ProcessId, entry["controller_process_id"]);
            Assert.AreEqual(entry.Event.Id == 6610 ? null : lease.ProcessId, entry["worker_process_id"]);
            Assert.IsNull(entry.Exception);
            Assert.AreEqual(0, entry.Scopes.Length);
            Assert.IsFalse(entry.Rendered.Contains(lease.Root, StringComparison.Ordinal));
            Assert.IsFalse(entry.Rendered.Contains("--internal-worker", StringComparison.Ordinal));
            if (raw.StandardErrorTail.Bytes.Length > 0)
            {
                Assert.IsFalse(entry.Rendered.Contains(raw.StandardErrorTail.Text, StringComparison.Ordinal));
            }
        }

        Assert.AreEqual(1, lease.ProcessDisposeCalls);
        Assert.IsFalse(lease.ForcedCleanup);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.IsNotNull(raw.EventDelivery, "The fixture must preserve production accepted delivery through every wrapper.");
    }

    [TestMethod]
    [TestCategory("Change66")]
    public async Task RealProductionBurst_CopiesExactSaturationAfterCommittedTerminalAndFinality()
    {
        const int count = 4000;
        using var logs = new RecordingLifecycleLogs();
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = ProcessFixtureHost.Create(
            [new("progress-burst", true, ["--progress-count", count.ToString(), "--barrier-every", "0"])],
            TimeProvider.System, logs.CreateLogger(LifecycleEventCatalog.Category), processingEvent =>
            {
                if (processingEvent is EligibilityDetermined)
                {
                    entered.TrySetResult();
                    release.Wait();
                }
            });
        try
        {
            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound));
            await fixture.Launcher.WaitForLaunchCountAsync(1).WaitAsync(Bound);
            var lease = fixture.Launcher.Leases.Single();
            var session = lease.Session!;
            await entered.Task.WaitAsync(Bound);
            Assert.IsNotNull(session.EventDeliveryIntakeClosed);
            await session.EventDeliveryIntakeClosed.WaitAsync(Bound);
            await session.PhysicalExitConfirmed.WaitAsync(Bound);
            Assert.AreEqual(count - 1L, session.EventDeliveryObservation!.ReplacedSnapshots);
            Assert.AreEqual(0, logs.Entries.Count(entry => entry.Event.Id is 6640 or 6641 or 6650));
            release.Set();
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
            var raw = await lease.CompleteAsync().WaitAsync(Bound);
            await session.Telemetry!.CoalescingObservation.WaitAsync(Bound);
            var observed = raw.EventDelivery!;
            var saturated = logs.Entries.Single(entry => entry.Event.Id == 6650);
            Assert.AreEqual("terminal", saturated["finality_kind"]);
            Assert.AreEqual(observed.AcceptedSnapshots, saturated["accepted_replaceable_count"]);
            Assert.AreEqual(observed.AcceptedLossless, saturated["accepted_lossless_count"]);
            Assert.AreEqual(observed.ReplacedSnapshots, saturated["replaced_count"]);
            Assert.AreEqual(observed.DeliveredSnapshots, saturated["delivered_snapshot_count"]);
            Assert.AreEqual(observed.FifoHighWater, saturated["fifo_high_water"]);
            Assert.AreEqual(observed.EnqueueWaits, saturated["enqueue_wait_count"]);
            Assert.AreEqual(observed.EnqueueWaitMilliseconds, saturated["enqueue_wait_duration_ms"]);
            Assert.AreEqual(observed.ProjectionMilliseconds, saturated["projection_duration_ms"]);
            Assert.AreEqual(observed.TerminalFlushMilliseconds, saturated["terminal_flush_duration_ms"]);
            Assert.AreEqual(observed.StaleRejected, saturated["stale_rejection_count"]);
            Assert.AreEqual(observed.AbandonedItems, saturated["abnormal_abandonment_count"]);
            Assert.AreEqual(await fixture.State.CaptureNotificationOwner()!.FinalOrdinaryDispatched,
                saturated["cadence_notification_count"]);
            Assert.AreEqual("completed", logs.Entries.Single(entry => entry.Event.Id == 6641)["process_classification"]);
            Assert.AreEqual(count, fixture.State.ProcessedThisRun);
            Assert.AreEqual(ProcessingRunOutcome.Completed, fixture.Reporter.GetFinalizationReceipt(lease.Request)!.Result.Outcome);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.IsFalse(lease.ForcedCleanup);
        }
        finally
        {
            release.Set();
        }
    }

    public static IEnumerable<object[]> TerminalModes()
    {
        yield return ["success", ProcessingRunOutcome.Completed, true, Array.Empty<string>()];
        yield return ["no-work", ProcessingRunOutcome.Completed, true, Array.Empty<string>()];
        yield return ["pre-ready-crash", ProcessingRunOutcome.Failed, false, new[] { "--exit-code", "42" }];
        yield return ["post-ready-crash", ProcessingRunOutcome.Failed, true, new[] { "--exit-code", "42" }];
        yield return ["malformed", ProcessingRunOutcome.Failed, true, new[] { "--malformed-kind", "json" }];
        yield return ["oversize", ProcessingRunOutcome.Failed, true, Array.Empty<string>()];
        yield return ["unknown", ProcessingRunOutcome.Failed, true, new[] { "--unknown-kind", "type" }];
        yield return ["invalid-sequence", ProcessingRunOutcome.Failed, true, new[] { "--sequence-fault", "replay" }];
        yield return ["terminal-mismatch", ProcessingRunOutcome.Completed, true, new[] { "--terminal", "completed", "--exit-code", "3" }];
        yield return ["terminal-mismatch", ProcessingRunOutcome.Failed, true, new[] { "--terminal", "failed", "--exit-code", "3" }];
        yield return ["stderr-flood", ProcessingRunOutcome.Completed, true, new[] { "--stderr-bytes", "262145" }];
        yield return ["raw-exit", ProcessingRunOutcome.Failed, false, new[] { "--exit-code", "3" }];
        yield return ["raw-exit", ProcessingRunOutcome.Failed, false, new[] { "--exit-code", "42" }];
    }

    [TestMethod]
    [DynamicData(nameof(TerminalModes))]
    public async Task ManualChild_RealFixtureTerminalModeFinalizesOnceReleasesAndRetriggers(
        string scenario,
        ProcessingRunOutcome expectedOutcome,
        bool capturesExecute,
        string[] options)
    {
        await using var fixture = ProcessFixtureHost.Create(
            new ProcessFixturePlan(scenario, capturesExecute, options),
            ProcessFixturePlan.NoWork);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound), $"{scenario}-admitted");
        await fixture.Launcher.Entered.Task.WaitAsync(Bound);

        ProcessingRunRequest first = fixture.Launcher.Requests.Single();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        WorkerProcessFixtureLease firstLease = fixture.Launcher.Leases.Single();
        await firstLease.CompleteAsync().WaitAsync(Bound);

        ProcessingRunFinalizationReceipt firstReceipt = fixture.Reporter.GetFinalizationReceipt(first)
            ?? throw new AssertFailedException($"{scenario}-missing-finalization-receipt");
        Assert.AreEqual(expectedOutcome, firstReceipt.Result.Outcome, $"{scenario}-terminal-outcome");
        Assert.IsNull(fixture.Coordinator.ActiveRequest, $"{scenario}-matching-handle-released");
        Assert.IsFalse(fixture.State.IsRunning, $"{scenario}-state-idle");
        Assert.IsNull(fixture.State.CurrentActivity, $"{scenario}-activity-cleaned");
        Assert.AreEqual(1, fixture.Launcher.CallCount, $"{scenario}-one-launch");
        Assert.AreEqual(0, fixture.ForbiddenHeavyResolutionCount, $"{scenario}-web-heavy-services-not-resolved");
        if (capturesExecute)
        {
            firstLease.AssertExactCapture();
        }
        else
        {
            Assert.AreEqual(0, firstLease.WrittenInput.Length, $"{scenario}-no-execute-before-readiness");
        }

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound), $"{scenario}-retrigger-admitted");
        await fixture.Launcher.WaitForLaunchCountAsync(2).WaitAsync(Bound);
        ProcessingRunRequest second = fixture.Launcher.Requests.Last();
        Assert.AreNotEqual(Guid.Empty, second.RunId, $"{scenario}-second-id-nonempty");
        Assert.AreNotEqual(first.RunId, second.RunId, $"{scenario}-second-id-different");
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        Assert.AreEqual(2, fixture.Launcher.CallCount, $"{scenario}-one-launch-per-admitted-run");
        Assert.AreEqual(0, fixture.ForbiddenHeavyResolutionCount, $"{scenario}-retrigger-web-heavy-services-not-resolved");
        Assert.IsNull(fixture.Coordinator.ActiveRequest, $"{scenario}-retrigger-matching-release");
    }

    [TestMethod]
    public async Task ManualChild_RealFixtureCooperativeCancelUsesOneSessionAndRetriggersAfterDrainage()
    {
        using var logs = new RecordingLifecycleLogs();
        await using var fixture = ProcessFixtureHost.Create(
            [new ProcessFixturePlan("cooperative-cancel", true, Array.Empty<string>()), ProcessFixturePlan.NoWork],
            TimeProvider.System, logs.CreateLogger(LifecycleEventCatalog.Category));

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound));
        await fixture.Launcher.Entered.Task.WaitAsync(Bound);
        WorkerProcessFixtureLease firstLease = fixture.Launcher.Leases.Single();
        ProcessingRunRequest first = fixture.Launcher.Requests.Single();
        await firstLease.Sink.WaitForAsync(@event => @event.Payload is LogEmittedPayload log
            && log.Message == $"fixture:cooperative-cancel:{first.RunId:D}").WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunAdmissionResult.AlreadyRunning,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound));
        Assert.AreEqual(1, fixture.Launcher.CallCount, "A rejected duplicate must not launch another child.");
        Assert.AreEqual(0, fixture.ForbiddenHeavyResolutionCount, "A rejected duplicate must not resolve a heavy Web service.");

        ChildWorkingSetObservation nativeMemory = firstLease.ReadOwnedWorkingSet();
        Assert.IsTrue(nativeMemory.Bytes is >= 0, "The native adapter samples this live owned process.");
        Task stop = fixture.Coordinator.StopActiveRun()
            ?? throw new AssertFailedException("The cooperative child must expose an active Stop operation.");
        await stop.WaitAsync(Bound);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        await firstLease.CompleteAsync().WaitAsync(Bound);
        await firstLease.Session!.Telemetry!.CancellationObservation.WaitAsync(Bound);
        var ownLogs = logs.Entries.Where(entry => Equals(entry["job_id"], first.RunId)).ToArray();
        Assert.HasCount(1, ownLogs.Where(entry => entry.Event.Id == 6620));
        Assert.HasCount(1, ownLogs.Where(entry => entry.Event.Id == 6621));
        Assert.HasCount(0, ownLogs.Where(entry => entry.Event.Id is 6622 or 6623));
        Assert.AreEqual("cancelled", ownLogs.Single(entry => entry.Event.Id == 6641)["process_classification"]);
        Assert.AreEqual("available", ownLogs.Single(entry => entry.Event.Id == 6641)["memory_observation"]);
        ChildWorkingSetObservation afterExit = firstLease.ReadOwnedWorkingSet();
        Assert.IsNull(afterExit.Bytes);
        Assert.AreEqual(ChildWorkingSetUnavailable.ProcessExited, afterExit.Reason);

        ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(first)
            ?? throw new AssertFailedException("The cooperative child must commit one finalization receipt.");
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, receipt.Result.Outcome);
        Assert.AreEqual(1, fixture.Launcher.CallCount);
        Assert.AreEqual(0, firstLease.TreeKillCalls, "A cooperative terminal must not require forced termination.");
        Assert.AreEqual(0, fixture.ForbiddenHeavyResolutionCount);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound));
        await fixture.Launcher.WaitForLaunchCountAsync(2).WaitAsync(Bound);
        Assert.AreNotEqual(first.RunId, fixture.Launcher.Requests.Last().RunId);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        Assert.AreEqual(0, fixture.ForbiddenHeavyResolutionCount);
    }

    [TestMethod]
    public async Task ManualChild_RealFixtureUnresponsiveStopUsesFakeGraceThenOneForcedKill()
    {
        using var logs = new RecordingLifecycleLogs();
        var clock = new CancellationTestClock();
        await using var fixture = ProcessFixtureHost.Create(
            [new ProcessFixturePlan("unresponsive", true, Array.Empty<string>())],
            clock, logs.CreateLogger(LifecycleEventCatalog.Category));

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound));
        await fixture.Launcher.Entered.Task.WaitAsync(Bound);
        WorkerProcessFixtureLease lease = fixture.Launcher.Leases.Single();
        ProcessingRunRequest request = fixture.Launcher.Requests.Single();
        await lease.Sink.WaitForAsync(@event => @event.Payload is LogEmittedPayload log
            && log.Message == $"fixture:unresponsive:{request.RunId:D}").WaitAsync(Bound);

        Task? stop = null;
        var graceAdvanced = false;
        Exception? bodyFailure = null;
        try
        {
            int timerGeneration = clock.OneShotTimerCount;
            stop = fixture.Coordinator.StopActiveRun()
                ?? throw new AssertFailedException("The unresponsive child must expose an active Stop operation.");
            await clock.WaitForOneShotTimerCreatedAsync(timerGeneration).WaitAsync(Bound);
            await lease.Sink.WaitForAsync(@event => @event.Payload is LogEmittedPayload log
                && log.Message == $"fixture:cancel-observed:{request.RunId:D}").WaitAsync(Bound);
            Assert.IsFalse(lease.HasExited, "The complete cancel-observed frame must precede forced termination.");
            Assert.IsFalse(stop.IsCompleted, "The admitted run must remain owned before its grace deadline.");
            Assert.AreEqual(0, lease.TreeKillCalls);

            clock.Advance(TimeSpan.FromSeconds(10));
            graceAdvanced = true;
            Assert.AreEqual(
                ChildProcessKillOutcome.Requested,
                await lease.TreeKillObserved.WaitAsync(Bound));
            await stop.WaitAsync(Bound);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
            await lease.Session!.Telemetry!.CancellationObservation.WaitAsync(Bound);
            Assert.HasCount(1, logs.Entries.Where(entry => entry.Event.Id == 6620));
            Assert.HasCount(0, logs.Entries.Where(entry => entry.Event.Id == 6621));
            var escalated = logs.Entries.Single(entry => entry.Event.Id == 6622);
            Assert.AreEqual(10000L, escalated["grace_elapsed_ms"]);
            Assert.AreEqual("succeeded", logs.Entries.Single(entry => entry.Event.Id == 6623)["kill_result"]);
            var classified = logs.Entries.Single(entry => entry.Event.Id == 6641);
            Assert.AreEqual("forced-stop", classified["process_classification"]);
            Assert.AreEqual(true, classified["forced_stop"]);
            ChildWorkerCompletionObservation completion = await lease.CompleteAsync().WaitAsync(Bound);

            ChildWorkerCancellationFacts facts = lease.Session?.CancellationFacts
                ?? throw new AssertFailedException("The stopped child must retain its cancellation facts.");
            Assert.IsTrue(facts.RequestAccepted);
            Assert.AreEqual(ChildWorkerTerminationIntent.Stop, facts.FirstIntent);
            Assert.IsTrue(facts.GraceExpired);
            Assert.IsTrue(facts.KillAttempted);
            Assert.AreEqual(ChildProcessKillOutcome.Requested, facts.KillOutcome);
            Assert.AreEqual(1, lease.TreeKillCalls, "The shared grace deadline must issue one tree kill.");
            Assert.IsTrue(completion.ExitObserved);
            Assert.IsNotNull(completion.ExitCode, "Forced termination must retain the actual platform exit status.");
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality);
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality);
            var protocol = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(
                completion.FirstProtocolObservation);
            Assert.AreEqual(WorkerProtocolFailureDetail.MissingTerminal, protocol.Failure.Detail);

            ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(request)
                ?? throw new AssertFailedException("The forced child termination must commit a finalization receipt.");
            Assert.AreEqual(ProcessingRunFinalizationOrigin.ControlPlane, receipt.Origin);
            Assert.AreEqual(ProcessingRunOutcome.Cancelled, receipt.Result.Outcome);
            Assert.IsNull(receipt.Result.FailureMessage);
            Assert.AreSame(request, receipt.Result.Request);
            Assert.AreEqual(0, fixture.ForbiddenHeavyResolutionCount);
            Assert.IsNull(fixture.Coordinator.ActiveRequest);
            Assert.IsFalse(fixture.State.IsRunning);
            Assert.IsNull(fixture.State.LastError);
        }
        catch (Exception failure)
        {
            bodyFailure = failure;
            throw;
        }
        finally
        {
            if (stop is not null)
            {
                try
                {
                    if (!graceAdvanced && !stop.IsCompleted)
                    {
                        clock.Advance(TimeSpan.FromSeconds(10));
                    }

                    await stop.WaitAsync(Bound);
                    await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
                }
                catch when (bodyFailure is not null)
                {
                    // Preserve the first assertion while the fixture's async disposal
                    // reaps the exact process and reports any independent cleanup failure.
                }
            }
        }
    }

    private sealed record ProcessFixturePlan(string Scenario, bool Capture, string[] Options)
    {
        internal static ProcessFixturePlan NoWork { get; } = new("no-work", true, Array.Empty<string>());
        internal ChildWorkingSetUnavailable? UnavailableMemory { get; init; }
    }

    private sealed class ProcessFixtureHost : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        private ProcessFixtureHost(
            string root,
            ServiceProvider provider,
            ProcessFixtureLauncher launcher,
            ForbiddenHeavyResolutionGuard forbiddenHeavyResolutionGuard)
        {
            _root = root;
            _provider = provider;
            Launcher = launcher;
            ForbiddenHeavyResolutionGuard = forbiddenHeavyResolutionGuard;
            Coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
            Reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
            State = provider.GetRequiredService<ProcessingState>();
        }

        internal ProcessFixtureLauncher Launcher { get; }
        internal ForbiddenHeavyResolutionGuard ForbiddenHeavyResolutionGuard { get; }
        internal int ForbiddenHeavyResolutionCount => ForbiddenHeavyResolutionGuard.Count;
        internal ProcessingRunCoordinator Coordinator { get; }
        internal ProcessingStateEventReporter Reporter { get; }
        internal ProcessingState State { get; }

        internal static ProcessFixtureHost Create(params ProcessFixturePlan[] plans)
        {
            return Create(plans, TimeProvider.System);
        }

        internal static ProcessFixtureHost Create(ProcessFixturePlan first, TimeProvider timeProvider)
        {
            return Create([first], timeProvider);
        }

        internal static ProcessFixtureHost Create(ProcessFixturePlan[] plans, TimeProvider timeProvider,
            ILogger? lifecycleLogger = null, Action<ProcessingEvent>? beforeProjection = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change34-process", Guid.NewGuid().ToString("N"));
            var services = new ServiceCollection();
            var launcher = new ProcessFixtureLauncher(plans, lifecycleLogger);
            var forbiddenHeavyResolutionGuard = new ForbiddenHeavyResolutionGuard();
            try
            {
                services.AddWebComposition(ApplicationCompositionContext.Create(
                    CompositionEnvironment.Development,
                    root,
                    Path.Combine(root, "data"),
                    Path.Combine(root, "config")));
                services.RemoveAll<IWorkerCommandInvocationBuilder>();
                services.AddSingleton<IWorkerCommandInvocationBuilder, FixtureInvocationBuilder>();
                services.RemoveAll<IChildWorkerLauncher>();
                services.AddSingleton<IChildWorkerLauncher>(launcher);
                if (beforeProjection is not null)
                {
                    services.RemoveAll<ProcessingStateEventReporter>();
                    services.AddSingleton(sp => new ProcessingStateEventReporter(sp.GetRequiredService<ProcessingState>(), beforeProjection));
                }
                services.RemoveAll<AdministrativeAreaResolverService>();
                services.AddSingleton<AdministrativeAreaResolverService>(_ =>
                    forbiddenHeavyResolutionGuard.Reject<AdministrativeAreaResolverService>(
                        nameof(AdministrativeAreaResolverService)));
                services.RemoveAll<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>();
                services.AddSingleton<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>(_ =>
                    forbiddenHeavyResolutionGuard.Reject<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>(
                        nameof(ImmichReverseGeo.Overture.Services.OvertureDivisionsService)));
                services.RemoveAll<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>();
                services.AddSingleton<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>(_ =>
                    forbiddenHeavyResolutionGuard.Reject<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>(
                        nameof(ImmichReverseGeo.Gadm.Services.GadmDivisionsService)));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(timeProvider);
                return new ProcessFixtureHost(
                    root,
                    services.BuildServiceProvider(validateScopes: true),
                    launcher,
                    forbiddenHeavyResolutionGuard);
            }
            catch
            {
                launcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            List<Exception> failures = [];
            try
            {
                await Launcher.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                await Coordinator.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                await _provider.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("Manual child process fixture cleanup failed.", failures);
            }
        }
    }

    private sealed class ProcessFixtureLauncher(IEnumerable<ProcessFixturePlan> plans, ILogger? lifecycleLogger = null) : IChildWorkerLauncher, IAsyncDisposable
    {
        private readonly Queue<ProcessFixturePlan> _plans = new(plans);
        private readonly List<WorkerProcessFixtureLease> _leases = [];
        private readonly List<ProcessingRunRequest> _requests = [];
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _launchGate = new();
        private TaskCompletionSource<int> _launchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;
        private int _completedLaunchCount;
        private Exception? _launchFailure;

        internal TaskCompletionSource Entered => _entered;
        internal IReadOnlyList<WorkerProcessFixtureLease> Leases => _leases;
        internal IReadOnlyList<ProcessingRunRequest> Requests => _requests;
        internal int CallCount
        {
            get
            {
                lock (_launchGate)
                {
                    return _callCount;
                }
            }
        }

        public async ValueTask<ChildWorkerLaunchResult> LaunchAsync(
            WorkerInvocation invocation,
            WorkerJobDispatch dispatch,
            IWorkerJobEventSink eventSink,
            ChildWorkerLauncherOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            var processAssets = Assert.IsInstanceOfType<ProcessAssetsWorkerJobDispatch>(dispatch);
            ProcessingRunRequest request = processAssets.Request.ProcessingRequest;
            ArgumentNullException.ThrowIfNull(eventSink);
            if (_plans.Count == 0)
            {
                throw new InvalidOperationException("The fixture launcher received an unplanned child run.");
            }

            ProcessFixturePlan plan = _plans.Dequeue();
            var lease = new WorkerProcessFixtureLease
            {
                Request = request, LauncherOptions = options, LifecycleLogger = lifecycleLogger ?? NullLogger.Instance,
                WorkingSetUnavailableOverride = plan.UnavailableMemory
            };
            _leases.Add(lease);
            _requests.Add(request);
            lock (_launchGate)
            {
                _callCount++;
            }
            try
            {
                var forwardingSink = new ForwardingEventSink(
                    eventSink,
                    new ProcessAssetsWorkerJobEventSink(request, lease.Sink));
                ChildWorkerSession session = await lease.LaunchAsync(
                    plan.Scenario,
                    dispatch,
                    forwardingSink,
                    invocation.ProtocolVersion,
                    plan.Capture,
                    plan.Options);
                _entered.TrySetResult();
                SignalLaunchCompleted();
                return new ChildWorkerLaunchResult.Started(session);
            }
            catch (Exception exception)
            {
                _entered.TrySetResult();
                SignalLaunchFailed(exception);
                throw;
            }
        }

        internal async Task WaitForLaunchCountAsync(int expectedCount)
        {
            while (true)
            {
                Task<int> completed;
                lock (_launchGate)
                {
                    if (_launchFailure is not null)
                    {
                        throw new InvalidOperationException("A fixture child launch failed.", _launchFailure);
                    }

                    if (_completedLaunchCount >= expectedCount)
                    {
                        return;
                    }

                    completed = _launchCompleted.Task;
                }

                await completed.ConfigureAwait(false);
            }
        }

        private void SignalLaunchCompleted()
        {
            TaskCompletionSource<int> completed;
            int completedCount;
            lock (_launchGate)
            {
                completedCount = ++_completedLaunchCount;
                completed = _launchCompleted;
                _launchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            completed.TrySetResult(completedCount);
        }

        private void SignalLaunchFailed(Exception exception)
        {
            TaskCompletionSource<int> completed;
            int completedCount;
            lock (_launchGate)
            {
                _launchFailure = exception;
                completedCount = _completedLaunchCount;
                completed = _launchCompleted;
                _launchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            completed.TrySetResult(completedCount);
        }

        public async ValueTask DisposeAsync()
        {
            await WorkerProcessFixtureLease.ReapAsync(_leases);
        }

        private sealed class ForwardingEventSink(
            IWorkerJobEventSink inner,
            IWorkerJobEventSink recording) : IWorkerJobEventSink, IAcceptedWorkerEventSink
        {
            private IAcceptedWorkerEventSink Accepted => inner is ProcessAssetsWorkerJobEventSink processing
                ? processing.AcceptedDeliverySink ?? throw new InvalidOperationException("Production accepted delivery was lost.")
                : (IAcceptedWorkerEventSink)inner;
            public ReadModelNotificationCadence.OwnerObservation? NotificationOwnerObservation => Accepted.NotificationOwnerObservation;
            public void BindDeliveryScope(WorkerEventDeliveryScope scope) => Accepted.BindDeliveryScope(scope);
            public async ValueTask AcceptDeliveryAsync(AcceptedDelivery delivery, CancellationToken cancellationToken)
            {
                await Accepted.AcceptDeliveryAsync(delivery, cancellationToken);
                await recording.AcceptAsync(delivery.Input.Message, cancellationToken);
            }

            public async ValueTask AcceptAsync(WorkerJobOutputMessage message, CancellationToken cancellationToken)
            {
                await inner.AcceptAsync(message, cancellationToken);
                await recording.AcceptAsync(message, cancellationToken);
            }
        }
    }

    private sealed class ForbiddenHeavyResolutionGuard
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);

        internal T Reject<T>(string service)
        {
            Interlocked.Increment(ref _count);
            throw new AssertFailedException($"Child-worker composition must not resolve {service}.");
        }
    }

    private sealed class FixtureInvocationBuilder : IWorkerCommandInvocationBuilder
    {
        public WorkerCommandInvocationResolution Build()
        {
            var facts = new WorkerCommandRuntimeFacts(
                WorkerInvocation.TrustedWebAssemblyIdentity,
                "/fixture/dotnet",
                WorkerTargetObservation.File,
                WorkerInvocation.TrustedWebAssemblyIdentity,
                "/fixture/ImmichReverseGeo.Web.dll",
                WorkerTargetObservation.File,
                "/fixture",
                WorkerTargetObservation.Directory,
                WorkerPathSemantics.Unix);
            return WorkerInvocation.Resolve(facts);
        }
    }

}
