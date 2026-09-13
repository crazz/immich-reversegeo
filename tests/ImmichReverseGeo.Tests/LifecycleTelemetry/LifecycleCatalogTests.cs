using System;
using System.Collections.Generic;
using System.Linq;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.LifecycleTelemetry;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using Microsoft.Extensions.Logging;
using Role = ImmichReverseGeo.Core.ApplicationRole.ApplicationRole;

namespace ImmichReverseGeo.Tests.LifecycleTelemetry;

[TestClass]
[TestCategory("Change66")]
public sealed class LifecycleCatalogTests
{
    private static readonly Guid JobId = Guid.Parse("23156108-24be-437c-bfc6-e9337d4beb8b");
    private const string Common = "job_id job_kind job_origin controller_process_id worker_process_id";
    private const string RoleFields = "application_role deployment_mode process_id";
    private const string MemoryFields = "memory_scope memory_sampling_method memory_sample_interval_ms memory_observation peak_working_set_bytes memory_sample_count memory_unavailable_reason";

    [TestMethod]
    public void AllEvents_ExposeExactStableNamesLevelsAndApplicationFields()
    {
        using var logs = new RecordingLifecycleLogs();
        ILogger logger = logs.CreateLogger(LifecycleEventCatalog.Category);
        var role = new RoleLogContext(Role.Web, DeploymentMode.Standard, 51);
        var before = WorkerJobLogContext.Create(new(JobId, WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.Scheduled), 51);
        var owned = before.WithProcess(72);
        LifecycleEventCatalog.ModeSelected(logger, role);
        LifecycleEventCatalog.RoleStarting(logger, role);
        LifecycleEventCatalog.RoleReady(logger, role, 17);
        LifecycleEventCatalog.RoleStopping(logger, role, RoleStopReason.Completed);
        LifecycleEventCatalog.RoleStopped(logger, role, RoleStopReason.Completed, 25, 8);
        LifecycleEventCatalog.LaunchStarted(logger, before);
        LifecycleEventCatalog.ProcessStarted(logger, owned, 2);
        LifecycleEventCatalog.WorkerReady(logger, owned, 3, 5);
        LifecycleEventCatalog.CancellationRequested(logger, owned, ChildWorkerTerminationIntent.Stop, WorkerCancellationPhase.Running);
        LifecycleEventCatalog.GraceCompleted(logger, owned, 4, true);
        LifecycleEventCatalog.Escalated(logger, owned, 10000);
        LifecycleEventCatalog.ForcedStopCompleted(logger, owned, 6, ChildProcessKillOutcome.Requested);
        LifecycleEventCatalog.ProtocolViolation(logger, owned, WorkerProtocolLogDirection.WorkerOutput,
            WorkerProtocolLogPhase.Events, WorkerProtocolFailureCode.InvalidSequence, 7);
        LifecycleEventCatalog.TerminalObserved(logger, owned, WorkerJobTerminalOutcome.Completed, 9);
        LifecycleEventCatalog.ProcessClassified(logger, owned, true, WorkerJobTerminalOutcome.Completed,
            WorkerLogClassification.Completed, 0, false, 18, new(3456, 3, ChildWorkingSetUnavailable.NoSample));
        LifecycleEventCatalog.CoalescingSaturated(logger, owned, Observation(), 12);

        (int Id, string Name, LogLevel Level, string Fields)[] expected =
        [
            (6601, "DeploymentModeSelected", LogLevel.Information, RoleFields),
            (6602, "RoleProcessStarting", LogLevel.Information, RoleFields),
            (6603, "RoleProcessReady", LogLevel.Information, RoleFields + " readiness_kind startup_duration_ms"),
            (6604, "RoleProcessStopping", LogLevel.Information, RoleFields + " stop_reason"),
            (6605, "RoleProcessStopped", LogLevel.Information, RoleFields + " process_outcome process_duration_ms stop_duration_ms"),
            (6610, "WorkerJobLaunchStarted", LogLevel.Information, Common),
            (6611, "WorkerJobProcessStarted", LogLevel.Information, Common + " process_start_duration_ms"),
            (6612, "WorkerJobReady", LogLevel.Information, Common + " readiness_duration_ms startup_duration_ms"),
            (6620, "WorkerJobCancellationRequested", LogLevel.Information, Common + " cancellation_reason cancellation_phase grace_period_ms"),
            (6621, "WorkerJobCancellationGraceCompleted", LogLevel.Information, Common + " cancellation_duration_ms terminal_observed exit_observed"),
            (6622, "WorkerJobCancellationEscalated", LogLevel.Warning, Common + " grace_period_ms grace_elapsed_ms escalation_action"),
            (6623, "WorkerJobForcedStopCompleted", LogLevel.Warning, Common + " escalation_duration_ms kill_result"),
            (6630, "WorkerProtocolViolation", LogLevel.Warning, Common + " protocol_direction protocol_phase violation_code sequence"),
            (6640, "WorkerJobTerminalObserved", LogLevel.Information, Common + " terminal_outcome terminal_sequence"),
            (6641, "WorkerJobProcessClassified", LogLevel.Information, Common + " ready_observed terminal_outcome process_classification exit_observation exit_code forced_stop total_duration_ms " + MemoryFields),
            (6650, "WorkerEventCoalescingSaturated", LogLevel.Warning, Common + " finality_kind accepted_replaceable_count accepted_lossless_count replaced_count delivered_snapshot_count fifo_high_water enqueue_wait_count enqueue_wait_duration_ms projection_duration_ms cadence_notification_count terminal_flush_duration_ms stale_rejection_count abnormal_abandonment_count")
        ];
        Assert.AreEqual(expected.Length, logs.Entries.Length);
        foreach (var (entry, contract) in logs.Entries.Zip(expected))
        {
            Assert.AreEqual(contract.Id, entry.Event.Id);
            Assert.AreEqual(contract.Name, entry.Event.Name);
            Assert.AreEqual(contract.Level, entry.Level);
            CollectionAssert.AreEquivalent(contract.Fields.Split(' '), entry.State
                .Where(field => field.Key != "{OriginalFormat}").Select(field => field.Key).ToArray());
            Assert.IsFalse(string.IsNullOrEmpty(entry.Template));
            Assert.IsNull(entry.Exception);
            Assert.AreEqual(0, entry.Scopes.Length);
            if (entry.Event.Id >= 6610)
            {
                Assert.AreEqual(JobId, entry["job_id"]);
                Assert.AreEqual("ProcessAssets", entry["job_kind"]);
                Assert.AreEqual("scheduler", entry["job_origin"]);
                Assert.AreEqual(51, entry["controller_process_id"]);
                Assert.AreEqual(entry.Event.Id == 6610 ? null : (object)72, entry["worker_process_id"]);
            }
        }
    }

    [TestMethod]
    [DataRow(WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.Manual, "dashboard-manual")]
    [DataRow(WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.Scheduled, "scheduler")]
    [DataRow(WorkerJobKind.CoordinateLookup, WorkerJobRequestOrigin.Manual, "lookup-ui")]
    [DataRow(WorkerJobKind.CacheMutation, WorkerJobRequestOrigin.Manual, "cache-ui")]
    public void Context_PreservesIdentityAndOnlyReplacesTheOwnedPid(WorkerJobKind kind, WorkerJobRequestOrigin origin, string expected)
    {
        var context = WorkerJobLogContext.Create(new(JobId, kind, origin), 12);
        var owned = context.WithProcess(17);
        Assert.AreEqual(JobId, owned.JobId);
        Assert.AreEqual(expected, owned.Origin);
        Assert.AreEqual(WorkerJobKindNames.Format(kind), owned.Kind);
        Assert.AreEqual(12, owned.ControllerProcessId);
        Assert.AreEqual(17, owned.WorkerProcessId);
        Assert.IsNull(context.WorkerProcessId);
    }

    [TestMethod]
    public void RunOnceOrigin_IsNeverSilentlyNormalizedToAChildOrigin()
    {
        Assert.ThrowsExactly<ArgumentException>(() => WorkerJobLogContext.Create(
            new(JobId, WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.RunOnce), 12));
    }

    [TestMethod]
    public void InternalWorker_HasNullModeAndNoModeSelectionEvent()
    {
        using var logs = new RecordingLifecycleLogs();
        var context = new RoleLogContext(Role.InternalWorker, null, 13);
        var logger = logs.CreateLogger(LifecycleEventCatalog.Category);
        LifecycleEventCatalog.ModeSelected(logger, context);
        LifecycleEventCatalog.RoleStarting(logger, context);
        LifecycleEventCatalog.RoleReady(logger, context, 20);
        CollectionAssert.AreEqual(new[] { 6602, 6603 }, logs.Entries.Select(entry => entry.Event.Id).ToArray());
        Assert.IsTrue(logs.Entries.All(entry => entry["deployment_mode"] is null));
        Assert.AreEqual("internal-worker", logs.Entries[1]["application_role"]);
        Assert.AreEqual("worker-protocol-ready", logs.Entries[1]["readiness_kind"]);
        Assert.ThrowsExactly<ArgumentException>(() => new RoleLogContext(Role.InternalWorker, DeploymentMode.Standard, 13));
    }

    [TestMethod]
    [DataRow((int)RoleStopReason.Completed, "completed", LogLevel.Information)]
    [DataRow((int)RoleStopReason.HostShutdown, "cancelled", LogLevel.Information)]
    [DataRow((int)RoleStopReason.StartupFailure, "failed", LogLevel.Warning)]
    [DataRow((int)RoleStopReason.FatalFailure, "failed", LogLevel.Warning)]
    public void RoleStop_MapsOutcomeAndLevel(int reason, string expected, LogLevel level)
    {
        using var logs = new RecordingLifecycleLogs();
        LifecycleEventCatalog.RoleStopped(logs.CreateLogger(LifecycleEventCatalog.Category),
            new(Role.RunOnce, DeploymentMode.RunOnce, 10), (RoleStopReason)reason, 40, 7);
        Assert.AreEqual(expected, logs.Entries.Single()["process_outcome"]);
        Assert.AreEqual(level, logs.Entries.Single().Level);
    }

    [TestMethod]
    public void Classification_ClosedLevelsMemoryAndExitShapes()
    {
        using var logs = new RecordingLifecycleLogs();
        var logger = logs.CreateLogger(LifecycleEventCatalog.Category);
        var owned = WorkerJobLogContext.Create(new(JobId, WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.Manual), 10).WithProcess(11);
        foreach (WorkerLogClassification classification in Enum.GetValues<WorkerLogClassification>())
        {
            WorkerJobTerminalOutcome? terminal = classification switch
            {
                WorkerLogClassification.Completed or WorkerLogClassification.TerminalExitMismatch => WorkerJobTerminalOutcome.Completed,
                WorkerLogClassification.Cancelled => WorkerJobTerminalOutcome.Cancelled,
                WorkerLogClassification.Busy or WorkerLogClassification.WorkerFailed => WorkerJobTerminalOutcome.Failed,
                _ => null
            };
            int? exit = classification switch
            {
                WorkerLogClassification.Completed or WorkerLogClassification.MissingTerminal => 0,
                WorkerLogClassification.Cancelled => 130,
                WorkerLogClassification.Busy => 3,
                WorkerLogClassification.WorkerFailed => 4,
                WorkerLogClassification.Crashed or WorkerLogClassification.ForcedStop => 234,
                WorkerLogClassification.TransportFailed => 6,
                WorkerLogClassification.StartupFailed or WorkerLogClassification.InfrastructureFailed => 5,
                _ => 2
            };
            LifecycleEventCatalog.ProcessClassified(logger, owned, classification != WorkerLogClassification.StartupFailed,
                terminal, classification, exit, classification == WorkerLogClassification.ForcedStop, 10,
                ChildWorkingSetSummary.NoSample);
        }

        foreach (var entry in logs.Entries)
        {
            Assert.AreEqual(entry["process_classification"] is "completed" or "cancelled" ? LogLevel.Information : LogLevel.Warning, entry.Level);
            Assert.AreEqual(entry["process_classification"] is "crashed" or "forced-stop" ? "unmapped" : "managed", entry["exit_observation"]);
            Assert.AreEqual("unavailable", entry["memory_observation"]);
            Assert.IsNull(entry["peak_working_set_bytes"]);
            Assert.AreEqual(0L, entry["memory_sample_count"]);
            Assert.AreEqual("no-sample", entry["memory_unavailable_reason"]);
        }
    }

    [TestMethod]
    public void Saturation_CopiesEveryAggregateAndOmitsUnsaturatedOrOpenSnapshots()
    {
        using var logs = new RecordingLifecycleLogs();
        var logger = logs.CreateLogger(LifecycleEventCatalog.Category);
        var owned = WorkerJobLogContext.Create(new(JobId, WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.Manual), 10).WithProcess(11);
        LifecycleEventCatalog.CoalescingSaturated(logger, owned, Observation() with { EnqueueWaits = 0, ReplacedSnapshots = 0 }, 12);
        LifecycleEventCatalog.CoalescingSaturated(logger, owned, Observation() with { Finality = WorkerEventDeliveryFinality.Open }, 12);
        Assert.AreEqual(0, logs.Entries.Length);
        LifecycleEventCatalog.CoalescingSaturated(logger, owned, Observation(), 12);
        var entry = logs.Entries.Single();
        object?[] expected = ["terminal", 101L, 102L, 103L, 104L, 106, 107L, 108L, 109L, 12L, 110L, 112L, 111L];
        string[] names = "finality_kind accepted_replaceable_count accepted_lossless_count replaced_count delivered_snapshot_count fifo_high_water enqueue_wait_count enqueue_wait_duration_ms projection_duration_ms cadence_notification_count terminal_flush_duration_ms stale_rejection_count abnormal_abandonment_count".Split(' ');
        CollectionAssert.AreEqual(expected, names.Select(name => entry[name]).ToArray());
        LifecycleEventCatalog.CoalescingSaturated(logger, owned, Observation() with { Finality = WorkerEventDeliveryFinality.Abandoned }, 12);
        Assert.AreEqual("nonterminal", logs.Entries.Last()["finality_kind"]);
        Assert.IsNull(logs.Entries.Last()["terminal_flush_duration_ms"]);
    }

    [TestMethod]
    public void LoggerFailure_DoesNotEscape()
    {
        LifecycleEventCatalog.RoleStarting(new ThrowingLogger(), new(Role.Web, DeploymentMode.Standard, 10));
    }

    private static WorkerEventDeliveryObservation Observation() => new(101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, WorkerEventDeliveryFinality.Terminal);

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => throw new InvalidOperationException("secret-scope");
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            throw new InvalidOperationException("secret-logger");
        }
    }
}
