using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.LifecycleTelemetry;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

internal static class ProcessFailureMatrixTelemetry
{
    private const string Common = "job_id job_kind job_origin controller_process_id worker_process_id";
    private const string Memory = "memory_scope memory_sampling_method memory_sample_interval_ms memory_observation peak_working_set_bytes memory_sample_count memory_unavailable_reason";
    private sealed record Contract(string Name, string Prefix, string Fields);
    private static readonly IReadOnlyDictionary<int, Contract> Contracts = new Dictionary<int, Contract>
    {
        [6610] = new("WorkerJobLaunchStarted", "Worker launch started", ""),
        [6611] = new("WorkerJobProcessStarted", "Worker process started", "process_start_duration_ms"),
        [6612] = new("WorkerJobReady", "Worker ready", "readiness_duration_ms startup_duration_ms"),
        [6620] = new("WorkerJobCancellationRequested", "Worker cancellation requested", "cancellation_reason cancellation_phase grace_period_ms"),
        [6621] = new("WorkerJobCancellationGraceCompleted", "Worker cancellation grace completed", "cancellation_duration_ms terminal_observed exit_observed"),
        [6622] = new("WorkerJobCancellationEscalated", "Worker cancellation escalated", "grace_period_ms grace_elapsed_ms escalation_action"),
        [6623] = new("WorkerJobForcedStopCompleted", "Worker forced stop completed", "escalation_duration_ms kill_result"),
        [6630] = new("WorkerProtocolViolation", "Worker protocol violation", "protocol_direction protocol_phase violation_code sequence"),
        [6640] = new("WorkerJobTerminalObserved", "Worker terminal observed", "terminal_outcome terminal_sequence"),
        [6641] = new("WorkerJobProcessClassified", "Worker process classified", "ready_observed terminal_outcome process_classification exit_observation exit_code forced_stop total_duration_ms " + Memory),
        [6650] = new("WorkerEventCoalescingSaturated", "Worker event coalescing saturated", "finality_kind accepted_replaceable_count accepted_lossless_count replaced_count delivered_snapshot_count fifo_high_water enqueue_wait_count enqueue_wait_duration_ms projection_duration_ms cadence_notification_count terminal_flush_duration_ms stale_rejection_count abnormal_abandonment_count")
    };

    internal static void AssertCatalog(RecordingLifecycleLogs logs, WorkerProcessFixtureLease lease,
        WorkerJobKind kind, ProcessFailureMatrixRow row) =>
        AssertCatalog(logs, lease.Request.RunId, lease.ProcessId, lease.Root, kind, row);

    internal static void AssertCatalog(RecordingLifecycleLogs logs, Guid jobId, int? processId, string root,
        WorkerJobKind kind, ProcessFailureMatrixRow row)
    {
        var entries = logs.Entries.Where(e => Equals(e["job_id"], jobId)).ToArray();
        // Containment may observe native exit before requesting cancel. Its own facts
        // determine whether its best-effort pair exists; terminal/classification is fixed.
        bool requiresCancellation = row.Events.Contains(6620);
        CollectionAssert.AreEquivalent(row.Events, entries.Where(e => requiresCancellation || e.Event.Id is not (6620 or 6621))
            .Select(e => e.Event.Id).ToArray(), row.Fault + ": exact primary event cardinality; observed "
                + string.Join(",", entries.Select(e => e.Event.Id)));
        Assert.AreEqual(entries.Count(e => e.Event.Id == 6620), entries.Count(e => e.Event.Id is 6621 or 6623));
        Assert.IsTrue(entries.Count(e => e.Event.Id == 6620) <= 1);
        Assert.AreEqual(6610, entries[0].Event.Id);

        foreach (var entry in entries)
        {
            Assert.IsTrue(Contracts.TryGetValue(entry.Event.Id, out var contract), row.Fault);
            Assert.AreEqual("ImmichReverseGeo.Lifecycle", entry.Category);
            Assert.AreEqual(contract.Name, entry.Event.Name);
            string[] fields = (Common + " " + contract.Fields).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            CollectionAssert.AreEqual(fields.Append("{OriginalFormat}").ToArray(), entry.State.Select(f => f.Key).ToArray());
            Assert.AreEqual(contract.Prefix + string.Concat(fields.Select(f => $" {f}={{{f}}}")), entry.Template);
            var level = entry.Event.Id switch
            {
                6622 or 6623 or 6630 or 6650 => LogLevel.Warning,
                6640 when row.Terminal == "failed" => LogLevel.Warning,
                6641 when row.Classification is not ("completed" or "cancelled") => LogLevel.Warning,
                _ => LogLevel.Information
            };
            Assert.AreEqual(level, entry.Level);
            Assert.AreEqual(jobId, entry["job_id"]);
            Assert.AreEqual(kind.ToString(), entry["job_kind"]);
            Assert.AreEqual(kind switch
            {
                WorkerJobKind.ProcessAssets => "dashboard-manual",
                WorkerJobKind.CoordinateLookup => "lookup-ui",
                _ => "cache-ui"
            }, entry["job_origin"]);
            Assert.AreEqual(Environment.ProcessId, entry["controller_process_id"]);
            Assert.AreEqual(entry.Event.Id == 6610 ? null : processId, entry["worker_process_id"]);
            Assert.AreNotEqual(Environment.ProcessId, processId);
            Assert.IsNull(entry.Exception);
            Assert.AreEqual(0, entry.Scopes.Length);
            foreach (string surface in entry.State.Select(f => f.Value?.ToString() ?? "").Append(entry.Rendered))
            {
                foreach (string canary in new[] { "matrix-secret", "51.501", "password-payload", root, "--internal-worker",
                    "fixture-stderr", "matrix:stderr-trailing" })
                {
                    Assert.IsFalse(surface.Contains(canary, StringComparison.Ordinal), $"{row.Fault}/{entry.Event.Id}: redaction");
                }
            }

            foreach (var field in entry.State.Where(f => f.Key.EndsWith("_ms", StringComparison.Ordinal) && f.Value is not null))
            {
                Assert.IsTrue(Convert.ToInt64(field.Value) >= 0, field.Key);
            }
        }

        var final = entries.Single(e => e.Event.Id == 6641);
        Assert.AreEqual(row.Ready, final["ready_observed"]);
        Assert.AreEqual(row.Terminal, final["terminal_outcome"]);
        Assert.AreEqual(row.Classification, final["process_classification"]);
        if (row.ExitCode is not null || row.RawExit == MatrixRawExit.Absent)
        {
            Assert.AreEqual(row.ExitCode, final["exit_code"]);
        }
        else
        {
            Assert.IsNotNull(final["exit_code"], "Preserve the observed platform exit without fixing a universal kill code.");
        }
        Assert.AreEqual(row.RawExit switch
        {
            MatrixRawExit.Absent => "unavailable",
            MatrixRawExit.ExactManaged => "managed",
            _ => "unmapped"
        }, final["exit_observation"]);
        Assert.AreEqual(row.ForcedStop, final["forced_stop"]);
        AssertMemory(final);
        if (row.Ready)
        {
            var ready = entries.Single(e => e.Event.Id == 6612);
            Assert.IsTrue((long)ready["startup_duration_ms"]! >= (long)ready["readiness_duration_ms"]!);
            Assert.IsTrue((long)final["total_duration_ms"]! >= (long)ready["startup_duration_ms"]!);
        }

        if (row.Violation is not null)
        {
            var violation = entries.Single(e => e.Event.Id == 6630);
            Assert.AreEqual(row.Violation, violation["violation_code"]);
            Assert.AreEqual("worker-output", violation["protocol_direction"]);
        }
    }

    internal static async Task AssertCoalescingAsync(RecordingLifecycleLogs logs, WorkerProcessFixtureLease lease,
        WorkerEventDeliveryObservation observation, ReadModelNotificationCadence.OwnerObservation owner)
    {
        var entries = logs.Entries.Where(e => e.Event.Id == 6650 && Equals(e["job_id"], lease.Request.RunId)).ToArray();
        bool saturated = observation.EnqueueWaits > 0 || observation.ReplacedSnapshots > 0;
        Assert.AreEqual(saturated ? 1 : 0, entries.Length);
        if (!saturated)
        {
            return;
        }

        var entry = entries.Single();
        long ordinary = await MatrixWait.ForAsync(owner.FinalOrdinaryDispatched, "coalescing/frozen-owner-notifications");
        var expected = new Dictionary<string, object?>
        {
            ["finality_kind"] = observation.Finality == WorkerEventDeliveryFinality.Terminal ? "terminal" : "nonterminal",
            ["accepted_replaceable_count"] = observation.AcceptedSnapshots,
            ["accepted_lossless_count"] = observation.AcceptedLossless,
            ["replaced_count"] = observation.ReplacedSnapshots,
            ["delivered_snapshot_count"] = observation.DeliveredSnapshots,
            ["fifo_high_water"] = observation.FifoHighWater,
            ["enqueue_wait_count"] = observation.EnqueueWaits,
            ["enqueue_wait_duration_ms"] = observation.EnqueueWaitMilliseconds,
            ["projection_duration_ms"] = observation.ProjectionMilliseconds,
            ["cadence_notification_count"] = ordinary,
            ["terminal_flush_duration_ms"] = observation.Finality == WorkerEventDeliveryFinality.Terminal
                ? observation.TerminalFlushMilliseconds : null,
            ["stale_rejection_count"] = observation.StaleRejected,
            ["abnormal_abandonment_count"] = observation.AbandonedItems
        };
        foreach (var (field, value) in expected)
        {
            Assert.AreEqual(value, entry[field], field);
        }
    }

    private static void AssertMemory(RecordingLifecycleLogs.Entry final)
    {
        Assert.AreEqual("worker-process-only", final["memory_scope"]);
        Assert.AreEqual("parent-periodic-working-set-max-v1", final["memory_sampling_method"]);
        Assert.AreEqual(1000, final["memory_sample_interval_ms"]);
        if (final["memory_observation"] is "available")
        {
            Assert.IsTrue((long)final["peak_working_set_bytes"]! >= 0);
            Assert.IsTrue((long)final["memory_sample_count"]! > 0);
            Assert.IsNull(final["memory_unavailable_reason"]);
        }
        else
        {
            Assert.AreEqual("unavailable", final["memory_observation"]);
            Assert.IsNull(final["peak_working_set_bytes"]);
            Assert.AreEqual(0L, final["memory_sample_count"]);
            CollectionAssert.Contains(new[] { "no-sample", "process-exited", "access-denied", "not-supported", "sample-failed" },
                final["memory_unavailable_reason"]);
        }
    }
}
