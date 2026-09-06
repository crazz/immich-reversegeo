using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Npgsql;

namespace ImmichReverseGeo.Tests.CrossProcessRunExclusion;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Change32")]
[DoNotParallelize]
public sealed class CrossProcessRunExclusionIntegrationTests
{
    private const string BusyFailure = "Another Immich ReverseGeo worker is already processing this database.";
    private const string DomainFailure = "Controlled Change32 domain failure.";
    private const string OwnershipLossFailure = "The database run lock was lost during processing.";

    [ClassCleanup]
    public static async Task ReapLastChanceResourcesAsync() =>
        await CrossProcessRunExclusionCase.ReapRemainingAsync();

    [TestMethod]
    public async Task ProductionInternalWorker_NoWorkCompletesWithRealDescriptorAndNoResidualLock()
    {
        await using CrossProcessRunExclusionCase @case = await CrossProcessRunExclusionCase.CreateAsync(CancellationToken.None);
        DatabaseEffectSnapshot before = await @case.ReadDatabaseEffectsAsync(CancellationToken.None);
        ParentWorker worker = await @case.StartProductionAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "--internal-worker" },
            worker.DescriptorArguments.ToArray(),
            "The production smoke must use the closed production role descriptor without a fixture selector.");
        await worker.WaitForEventAsync(@event => @event.Type == WorkerProtocolV1.RunStartedType, CancellationToken.None);
        WorkerProtocolEvent eligibility = await worker.WaitForEventAsync(
            @event => @event.Payload is EligibilityDeterminedPayload,
            CancellationToken.None);
        Assert.AreEqual(
            0L,
            ((EligibilityDeterminedPayload)eligibility.Payload).EligibleCount,
            "The disposable smoke database must reach the production no-work gate.");

        ChildWorkerCompletionObservation completion = await worker.CompleteAsync(CancellationToken.None);

        Assert.AreEqual(0, completion.ExitCode, "Production --internal-worker must retain its completed exit mapping.");
        AssertTerminalAndProjection(worker, WorkerProtocolV1.CompletedType, ProcessingRunOutcome.Completed, null);
        AssertCanonicalEventOrder(worker, expectEligibility: true, expectProtectedOperation: false);
        Assert.AreEqual(before, await @case.ReadDatabaseEffectsAsync(CancellationToken.None));
        await @case.AssertKeyFreeAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task HeldOwner_ContenderReportsBusyWithoutDomainEffects_ThenSameCoordinatorReacquires()
    {
        await using CrossProcessRunExclusionCase @case = await CrossProcessRunExclusionCase.CreateAsync(CancellationToken.None);
        DatabaseEffectSnapshot before = await @case.ReadDatabaseEffectsAsync(CancellationToken.None);
        ParentWorker owner = await @case.StartControlledAsync("held-success", CancellationToken.None);
        PostgresLockOwner heldBackend = await owner.ReadMarkerAsync(CancellationToken.None);

        ParentWorker contender = await @case.StartControlledAsync("held-success", CancellationToken.None);
        Assert.AreNotSame(owner.Coordinator, contender.Coordinator, "Database contention must not be masked by one coordinator's local admission gate.");
        await contender.WaitForEventAsync(@event => @event.Type == WorkerProtocolV1.RunStartedType, CancellationToken.None);
        ChildWorkerCompletionObservation busyCompletion = await contender.CompleteAsync(CancellationToken.None);

        Assert.AreEqual(3, busyCompletion.ExitCode, "The reserved busy terminal must map to exit 3.");
        AssertTerminalAndProjection(contender, WorkerProtocolV1.FailedType, ProcessingRunOutcome.Failed, BusyFailure);
        Assert.AreEqual(0, contender.Events.Count(@event => @event.Payload is EligibilityDeterminedPayload), "Busy admission must not enter eligibility.");
        Assert.AreEqual(0, contender.Events.Count(IsProtectedOperationLog), "Busy admission must not invoke the controlled domain operation.");
        Assert.IsFalse(contender.MarkerExists, "Busy admission must not publish the protected-operation canary.");
        AssertCanonicalEventOrder(contender, expectEligibility: false, expectProtectedOperation: false);
        Assert.AreEqual(before, await @case.ReadDatabaseEffectsAsync(CancellationToken.None), "Busy admission must not mutate the disposable database.");
        PostgresLockOwner? stillHeld = await @case.Database.FindOwnedBackendAsync(owner.ApplicationName, CancellationToken.None);
        Assert.IsNotNull(stillHeld, "The contender must not disturb the first worker's exact lock owner.");
        Assert.AreEqual(heldBackend.ProcessId, stillHeld.ProcessId);

        await owner.ReleaseAsync(1, CancellationToken.None);
        Assert.AreEqual(0, (await owner.CompleteAsync(CancellationToken.None)).ExitCode);
        AssertTerminalAndProjection(owner, WorkerProtocolV1.CompletedType, ProcessingRunOutcome.Completed, null);
        AssertCanonicalEventOrder(owner, expectEligibility: true, expectProtectedOperation: true);
        await @case.AssertKeyFreeAsync(CancellationToken.None);

        await AssertFreshOwnerCanCompleteAsync(@case, owner);
    }

    [TestMethod]
    public Task ControlledSuccess_ReleasesExactProductionKey_ForSameCoordinatorFreshProcess() =>
        AssertControlledTerminalReleasesAsync(
            "held-success",
            WorkerProtocolV1.CompletedType,
            ProcessingRunOutcome.Completed,
            0,
            null);

    [TestMethod]
    public Task ControlledDomainFailure_ReleasesExactProductionKey_ForSameCoordinatorFreshProcess() =>
        AssertControlledTerminalReleasesAsync(
            "domain-failure",
            WorkerProtocolV1.FailedType,
            ProcessingRunOutcome.Failed,
            4,
            DomainFailure);

    [TestMethod]
    public async Task CooperativeCancel_UsesExactCoordinatorStopAndCapturedCorrelation_ThenReacquires()
    {
        await using CrossProcessRunExclusionCase @case = await CrossProcessRunExclusionCase.CreateAsync(CancellationToken.None);
        ParentWorker owner = await @case.StartControlledAsync("cooperative-cancel", CancellationToken.None);
        await owner.WaitForProtectedOperationAsync(CancellationToken.None);
        owner.StopCooperatively();

        Assert.AreEqual(130, (await owner.CompleteAsync(CancellationToken.None)).ExitCode);
        Assert.AreEqual(0, owner.TreeKillCalls, "Cooperative cancellation must not terminate the process tree.");
        ChildWorkerCancellationFacts cancellation = owner.CancellationFacts
            ?? throw new AssertFailedException("The correlated coordinator stop must publish cancellation facts.");
        Assert.AreEqual(ChildWorkerTerminationIntent.Stop, cancellation.FirstIntent);
        Assert.IsTrue(cancellation.RequestAccepted, "The child must accept the captured cancel for its exact run.");
        Assert.IsFalse(cancellation.GraceExpired);
        Assert.IsFalse(cancellation.KillAttempted);
        Assert.IsNull(cancellation.KillOutcome);
        AssertTerminalAndProjection(owner, WorkerProtocolV1.CancelledType, ProcessingRunOutcome.Cancelled, null);
        AssertCanonicalEventOrder(owner, expectEligibility: true, expectProtectedOperation: true);
        await @case.AssertKeyFreeAsync(CancellationToken.None);

        await AssertFreshOwnerCanCompleteAsync(@case, owner);
    }

    [TestMethod]
    public async Task AbruptDeath_WithoutCoordinatorStop_UsesTypedCrashFinalityAndReacquires()
    {
        await using CrossProcessRunExclusionCase @case = await CrossProcessRunExclusionCase.CreateAsync(CancellationToken.None);
        ParentWorker owner = await @case.StartControlledAsync("held-success", CancellationToken.None);
        await owner.WaitForProtectedOperationAsync(CancellationToken.None);
        ChildProcessKillOutcome kill = owner.KillTree();
        Assert.IsTrue(
            kill is ChildProcessKillOutcome.Requested or ChildProcessKillOutcome.AlreadyExited,
            "The abrupt-death test may terminate only its registered process tree.");

        await owner.CompleteAsync(CancellationToken.None);

        Assert.AreEqual(0, CountTerminalEvents(owner), "An abruptly killed child must not invent a terminal frame.");
        ProcessingRunFinalizationReceipt receipt = AssertControlPlaneFailureProjection(owner);
        WorkerRunDecision decision = owner.ClassifyWithoutTerminalReceipt();
        Assert.AreEqual(ProcessingRunOutcome.Failed, decision.Outcome);
        Assert.AreEqual(WorkerRunAuthority.ControlPlane, decision.Authority);
        Assert.IsTrue(
            decision.Category is WorkerRunFailureCategory.MissingTerminal
                or WorkerRunFailureCategory.Crash
                or WorkerRunFailureCategory.UnmappedExit,
            $"Abrupt death must retain a typed crash/missing-terminal category, not {decision.Category}.");
        Assert.AreNotEqual(WorkerRunFailureCategory.ManagedCancellation, decision.Category);
        Assert.AreNotEqual(WorkerRunFailureCategory.ForcedTermination, decision.Category);
        Assert.IsFalse(decision.Retry);
        StringAssert.Contains(
            receipt.Result.FailureMessage,
            decision.Category.ToString().ToLowerInvariant(),
            "The actual control-plane receipt must expose the typed safe crash category.");
        Assert.IsNull(owner.CancellationFacts?.FirstIntent, "Direct process death must not be classified as coordinator cancellation.");
        AssertCanonicalEventOrder(owner, expectEligibility: true, expectProtectedOperation: true, expectTerminal: false);
        await @case.AssertKeyFreeAsync(CancellationToken.None);

        await AssertFreshOwnerCanCompleteAsync(@case, owner);
    }

    [TestMethod]
    public async Task OwnedBackendTermination_WhenSupported_ProducesInfrastructureFailureWithoutAnomalyAndReacquires()
    {
        await using CrossProcessRunExclusionCase @case = await CrossProcessRunExclusionCase.CreateAsync(CancellationToken.None);
        if (!@case.Capabilities.CanTerminateOwnedBackends)
        {
            Assert.Inconclusive("The configured Change32 PostgreSQL role cannot terminate its exact test-owned backend.");
        }

        ParentWorker owner = await @case.StartControlledAsync("ownership-loss", CancellationToken.None);
        PostgresLockOwner marker = await owner.ReadMarkerAsync(CancellationToken.None);
        Assert.IsTrue(await @case.Database.TerminateOwnedBackendAsync(
            marker.ProcessId,
            marker.ApplicationName,
            CancellationToken.None));

        Assert.AreEqual(5, (await owner.CompleteAsync(CancellationToken.None)).ExitCode);
        AssertTerminalAndProjection(owner, WorkerProtocolV1.FailedType, ProcessingRunOutcome.Failed, OwnershipLossFailure);
        AssertCanonicalEventOrder(owner, expectEligibility: true, expectProtectedOperation: true);
        await @case.AssertKeyFreeAsync(CancellationToken.None);

        await AssertFreshOwnerCanCompleteAsync(@case, owner);
    }

    [TestMethod]
    public async Task AssertionFailureAfterOwnerRegistration_UnconditionallyReapsEveryOwnedResource()
    {
        PostgresIntegrationSettings settings = CrossProcessRunExclusionCase.RequireSettings();
        CrossProcessRunExclusionCase? @case = null;
        ParentWorker? owner = null;
        string? root = null;
        string? databaseName = null;
        bool dedicatedDatabase = false;

        try
        {
            @case = await CrossProcessRunExclusionCase.CreateAsync(settings, CancellationToken.None);
            root = @case.Root;
            databaseName = @case.Database.DatabaseName;
            dedicatedDatabase = @case.Database.UsesDedicatedDatabase;
            await using (@case)
            {
                owner = await @case.StartControlledAsync("held-success", CancellationToken.None);
                await owner.WaitForProtectedOperationAsync(CancellationToken.None);
                Assert.Fail("Change32 injected assertion after registered held owner.");
            }
        }
        catch (AssertFailedException exception) when (exception.Message.Contains("injected assertion", StringComparison.Ordinal))
        {
        }

        Assert.IsNotNull(@case);
        Assert.IsNotNull(owner);
        Assert.IsFalse(@case.IsRegistered, "Successful unwind must remove the case from the last-chance registry.");
        Assert.IsTrue(@case.ResourcesReleased);
        Assert.IsTrue(owner.HasExited);
        Assert.AreEqual(1, owner.ProcessDisposeCalls);
        Assert.IsTrue(owner.ResourcesReleased);
        Assert.IsFalse(Directory.Exists(root));
        Assert.IsFalse(File.Exists(owner.MarkerPath));
        Assert.IsFalse(File.Exists(owner.CapturePath));
        if (!dedicatedDatabase)
        {
            await AssertDatabaseUnavailableAsync(settings, databaseName!);
        }
    }

    [TestMethod]
    public async Task DatabasePerCaseParallelOwners_RemainIsolatedDuringIndependentCleanup()
    {
        PostgresIntegrationSettings settings = CrossProcessRunExclusionCase.RequireSettings();
        PostgresIntegrationCapabilities capabilities = await settings.ProbeCapabilitiesAsync(CancellationToken.None);
        if (!capabilities.CanCreateDatabases)
        {
            await using CrossProcessRunExclusionCase serialized = await CrossProcessRunExclusionCase.CreateAsync(
                settings,
                CancellationToken.None);
            Assert.IsTrue(serialized.Database.UsesDedicatedDatabase, "The capability fallback must retain serialized dedicated-database ownership.");
            return;
        }

        Task<CrossProcessRunExclusionCase> firstCreation = CrossProcessRunExclusionCase.CreateAsync(settings, CancellationToken.None);
        Task<CrossProcessRunExclusionCase> secondCreation = CrossProcessRunExclusionCase.CreateAsync(settings, CancellationToken.None);
        CrossProcessRunExclusionCase[] cases = await Task.WhenAll(firstCreation, secondCreation);
        CrossProcessRunExclusionCase first = cases[0];
        CrossProcessRunExclusionCase second = cases[1];
        string firstRoot = first.Root;
        string secondRoot = second.Root;
        string firstDatabase = first.Database.DatabaseName;
        string secondDatabase = second.Database.DatabaseName;

        try
        {
            Task<ParentWorker> firstStart = first.StartControlledAsync("held-success", CancellationToken.None);
            Task<ParentWorker> secondStart = second.StartControlledAsync("held-success", CancellationToken.None);
            ParentWorker[] owners = await Task.WhenAll(firstStart, secondStart);
            ParentWorker firstOwner = owners[0];
            ParentWorker secondOwner = owners[1];
            PostgresLockOwner[] backends = await Task.WhenAll(
                firstOwner.ReadMarkerAsync(CancellationToken.None),
                secondOwner.ReadMarkerAsync(CancellationToken.None));

            Assert.AreNotEqual(first.Database.DatabaseName, second.Database.DatabaseName);
            Assert.AreNotEqual(first.Root, second.Root);
            Assert.AreNotEqual(firstOwner.WorkerRoot, secondOwner.WorkerRoot);
            Assert.AreNotEqual(firstOwner.ApplicationName, secondOwner.ApplicationName);
            Assert.AreNotEqual(firstOwner.ProcessId, secondOwner.ProcessId);
            Assert.AreNotEqual(firstOwner.Request.RunId, secondOwner.Request.RunId);
            Assert.AreNotEqual(backends[0].ProcessId, backends[1].ProcessId);
            Assert.AreEqual(new DatabaseEffectSnapshot(0, 0), await first.ReadDatabaseEffectsAsync(CancellationToken.None));
            Assert.AreEqual(new DatabaseEffectSnapshot(0, 0), await second.ReadDatabaseEffectsAsync(CancellationToken.None));

            await firstOwner.ReleaseAsync(1, CancellationToken.None);
            await firstOwner.CompleteAsync(CancellationToken.None);
            AssertTerminalAndProjection(firstOwner, WorkerProtocolV1.CompletedType, ProcessingRunOutcome.Completed, null);
            AssertCanonicalEventOrder(firstOwner, expectEligibility: true, expectProtectedOperation: true);
            await first.DisposeAsync();

            Assert.IsFalse(Directory.Exists(firstRoot));
            Assert.IsTrue(Directory.Exists(secondRoot), "Cleaning one database-per-case owner must not delete its peer's root.");
            Assert.IsTrue(secondOwner.MarkerExists, "Cleaning one case must not remove its peer's held marker.");
            Assert.IsFalse(secondOwner.HasExited, "Cleaning one case must not terminate its peer's registered process.");
            Assert.IsNotNull(await second.Database.FindOwnedBackendAsync(secondOwner.ApplicationName, CancellationToken.None));

            await secondOwner.ReleaseAsync(1, CancellationToken.None);
            await secondOwner.CompleteAsync(CancellationToken.None);
            AssertTerminalAndProjection(secondOwner, WorkerProtocolV1.CompletedType, ProcessingRunOutcome.Completed, null);
            AssertCanonicalEventOrder(secondOwner, expectEligibility: true, expectProtectedOperation: true);
            await second.AssertKeyFreeAsync(CancellationToken.None);
        }
        finally
        {
            await Task.WhenAll(
                first.DisposeAsync().AsTask(),
                second.DisposeAsync().AsTask());
        }

        Assert.IsFalse(Directory.Exists(firstRoot));
        Assert.IsFalse(Directory.Exists(secondRoot));
        await AssertDatabaseUnavailableAsync(settings, firstDatabase);
        await AssertDatabaseUnavailableAsync(settings, secondDatabase);
    }

    private static async Task AssertControlledTerminalReleasesAsync(
        string scenario,
        string terminalType,
        ProcessingRunOutcome outcome,
        int exitCode,
        string? failureMessage)
    {
        await using CrossProcessRunExclusionCase @case = await CrossProcessRunExclusionCase.CreateAsync(CancellationToken.None);
        ParentWorker owner = await @case.StartControlledAsync(scenario, CancellationToken.None);
        await owner.WaitForProtectedOperationAsync(CancellationToken.None);
        await owner.ReleaseAsync(1, CancellationToken.None);

        Assert.AreEqual(exitCode, (await owner.CompleteAsync(CancellationToken.None)).ExitCode);
        AssertTerminalAndProjection(owner, terminalType, outcome, failureMessage);
        AssertCanonicalEventOrder(owner, expectEligibility: true, expectProtectedOperation: true);
        await @case.AssertKeyFreeAsync(CancellationToken.None);

        await AssertFreshOwnerCanCompleteAsync(@case, owner);
    }

    private static async Task<ParentWorker> AssertFreshOwnerCanCompleteAsync(
        CrossProcessRunExclusionCase @case,
        ParentWorker previous)
    {
        ProcessingRunCoordinator coordinator = previous.Coordinator;
        ProcessingRunRequest previousRequest = previous.Request;
        ProcessingRunFinalizationReceipt previousReceipt = previous.Receipt!;
        int previousProcessId = previous.ProcessId;
        string previousApplicationName = previous.ApplicationName;
        string previousRoot = previous.WorkerRoot;
        int previousGeneration = previous.CoordinatorGeneration;

        ParentWorker reacquirer = await @case.StartProductionOnSameCoordinatorAsync(
            previous,
            CancellationToken.None);
        Assert.AreSame(coordinator, reacquirer.Coordinator, "Fresh-process admission must reuse the prior coordinator instance.");
        Assert.AreEqual(previousGeneration + 1, reacquirer.CoordinatorGeneration);
        Assert.AreNotEqual(previousRequest.RunId, reacquirer.Request.RunId);
        Assert.AreNotEqual(previousProcessId, reacquirer.ProcessId);
        Assert.AreNotEqual(previousApplicationName, reacquirer.ApplicationName);
        Assert.AreNotEqual(previousRoot, reacquirer.WorkerRoot);

        CollectionAssert.AreEqual(
            new[] { "--internal-worker" },
            reacquirer.DescriptorArguments.ToArray(),
            "Fresh lock admission must use the production internal-worker descriptor.");
        await reacquirer.WaitForEventAsync(@event => @event.Payload is EligibilityDeterminedPayload, CancellationToken.None);
        Assert.AreEqual(0, (await reacquirer.CompleteAsync(CancellationToken.None)).ExitCode);
        AssertTerminalAndProjection(reacquirer, WorkerProtocolV1.CompletedType, ProcessingRunOutcome.Completed, null);
        Assert.AreNotSame(previousReceipt, reacquirer.Receipt, "Each coordinator generation must commit a distinct receipt.");
        AssertCanonicalEventOrder(reacquirer, expectEligibility: true, expectProtectedOperation: false);
        await @case.AssertKeyFreeAsync(CancellationToken.None);
        return reacquirer;
    }

    private static void AssertTerminalAndProjection(
        ParentWorker worker,
        string expectedType,
        ProcessingRunOutcome expectedOutcome,
        string? expectedFailureMessage)
    {
        WorkerProtocolEvent[] terminals = worker.Events.Where(@event => WorkerProtocolV1.IsTerminal(@event.Type)).ToArray();
        Assert.AreEqual(1, terminals.Length, "Each managed parent worker must produce exactly one terminal event.");
        Assert.AreEqual(expectedType, terminals[0].Type);
        TerminalPayload payload = Assert.IsInstanceOfType<TerminalPayload>(terminals[0].Payload);
        Assert.AreEqual(0L, payload.ProcessedCount);
        Assert.AreEqual(0L, payload.UpdatedCount);
        Assert.AreEqual(0L, payload.SkippedCount);
        Assert.AreEqual(0L, payload.FailedCount);
        Assert.AreEqual(expectedFailureMessage, payload.FailureMessage);

        ProcessingRunFinalizationReceipt receipt = worker.Receipt
            ?? throw new AssertFailedException("A managed terminal must have one real finalization receipt.");
        Assert.AreEqual(expectedOutcome, receipt.Result.Outcome);
        Assert.AreEqual(ProcessingRunFinalizationOrigin.WorkerTerminal, receipt.Origin);
        Assert.AreEqual(0L, receipt.Result.ProcessedCount);
        Assert.AreEqual(0L, receipt.Result.UpdatedCount);
        Assert.AreEqual(0L, receipt.Result.SkippedCount);
        Assert.AreEqual(0L, receipt.Result.FailedCount);
        Assert.AreEqual(expectedFailureMessage, receipt.Result.FailureMessage);
        AssertProjectionState(worker, expectedOutcome);
    }

    private static ProcessingRunFinalizationReceipt AssertControlPlaneFailureProjection(ParentWorker worker)
    {
        ProcessingRunFinalizationReceipt receipt = worker.Receipt
            ?? throw new AssertFailedException("Abrupt death must have one synthesized finalization receipt.");
        Assert.AreEqual(ProcessingRunOutcome.Failed, receipt.Result.Outcome);
        Assert.AreEqual(ProcessingRunFinalizationOrigin.ControlPlane, receipt.Origin);
        Assert.AreEqual(0L, receipt.Result.ProcessedCount);
        Assert.AreEqual(0L, receipt.Result.UpdatedCount);
        Assert.AreEqual(0L, receipt.Result.SkippedCount);
        Assert.AreEqual(0L, receipt.Result.FailedCount);
        AssertProjectionState(worker, ProcessingRunOutcome.Failed);
        return receipt;
    }

    private static void AssertProjectionState(ParentWorker worker, ProcessingRunOutcome outcome)
    {
        IReadOnlyList<string> logsBeforeReplay = worker.ProjectedLog;
        Assert.AreEqual(
            1,
            logsBeforeReplay.Count(line => line.Contains("Run complete. Processed=", StringComparison.Ordinal)),
            "One coordinator generation must project exactly one summary mutation.");
        Assert.AreEqual(
            outcome == ProcessingRunOutcome.Failed ? 1 : 0,
            logsBeforeReplay.Count(line => line.Contains("[ERROR] Fatal:", StringComparison.Ordinal)),
            "Failure projection must contribute exactly one fatal mutation and non-failures none.");
        Assert.AreEqual(
            outcome == ProcessingRunOutcome.Cancelled ? 1 : 0,
            logsBeforeReplay.Count(line => line.EndsWith("Run cancelled.", StringComparison.Ordinal)),
            "Cancellation projection must contribute its summary once.");
        Assert.AreEqual(
            0,
            logsBeforeReplay.Count(line => line.Contains("[WARN]", StringComparison.Ordinal)),
            "Healthy terminal/finality evidence must not append a contradiction anomaly.");
        Assert.AreEqual(0L, worker.State.ProcessedThisRun);
        Assert.AreEqual(0L, worker.State.SkippedThisRun);
        Assert.AreEqual(outcome == ProcessingRunOutcome.Failed ? 1L : 0L, worker.State.ErrorsThisRun);
        Assert.AreEqual(1, worker.LauncherCalls, "The control plane must not schedule an automatic retry.");
        Assert.IsFalse(worker.State.IsRunning);
        Assert.IsNull(worker.State.CurrentActivity);

        worker.AssertProjectionReplayIsIdempotent();
        CollectionAssert.AreEqual(
            logsBeforeReplay.ToArray(),
            worker.ProjectedLog.ToArray(),
            "Finalization replay must not mutate state or append a second summary/fatal entry.");
    }

    private static void AssertCanonicalEventOrder(
        ParentWorker worker,
        bool expectEligibility,
        bool expectProtectedOperation,
        bool expectTerminal = true)
    {
        WorkerProtocolEvent[] events = worker.Events.ToArray();
        Assert.AreEqual(events.Length, events.Select(@event => @event.Sequence).Distinct().Count(), "Accepted worker event sequences must be unique.");
        CollectionAssert.AreEqual(
            events.Select(@event => @event.Sequence).OrderBy(sequence => sequence).ToArray(),
            events.Select(@event => @event.Sequence).ToArray(),
            "Accepted worker events must retain canonical sequence order.");
        Assert.AreEqual(1, events.Count(@event => @event.Type == WorkerProtocolV1.ReadyType), "The child must accept exactly one ready event.");
        Assert.AreEqual(1, events.Count(@event => @event.Type == WorkerProtocolV1.RunStartedType), "The accepted execute must cause exactly one run-started event.");
        int ready = Array.FindIndex(events, @event => @event.Type == WorkerProtocolV1.ReadyType);
        int started = Array.FindIndex(events, @event => @event.Type == WorkerProtocolV1.RunStartedType);
        int terminal = Array.FindIndex(events, @event => WorkerProtocolV1.IsTerminal(@event.Type));
        Assert.IsTrue(ready >= 0 && started > ready, "Ready and run-started must be causally ordered.");
        Assert.AreEqual(expectTerminal ? 1 : 0, events.Count(@event => WorkerProtocolV1.IsTerminal(@event.Type)));
        if (expectTerminal)
        {
            Assert.IsTrue(terminal > started, "A managed terminal must follow run-started.");
        }

        int eligibility = Array.FindIndex(events, @event => @event.Payload is EligibilityDeterminedPayload);
        Assert.AreEqual(expectEligibility ? 1 : 0, events.Count(@event => @event.Payload is EligibilityDeterminedPayload));
        if (expectEligibility)
        {
            Assert.IsTrue(eligibility > started && (!expectTerminal || eligibility < terminal));
        }

        int protectedOperation = Array.FindIndex(events, IsProtectedOperationLog);
        Assert.AreEqual(expectProtectedOperation ? 1 : 0, events.Count(IsProtectedOperationLog));
        if (expectProtectedOperation)
        {
            Assert.IsTrue(protectedOperation > eligibility && (!expectTerminal || protectedOperation < terminal));
        }
    }

    private static int CountTerminalEvents(ParentWorker worker) =>
        worker.Events.Count(@event => WorkerProtocolV1.IsTerminal(@event.Type));

    private static bool IsProtectedOperationLog(WorkerProtocolEvent @event) =>
        @event.Payload is LogEmittedPayload log
        && string.Equals(log.Message, "Change32 protected operation entered", StringComparison.Ordinal);

    private static async Task AssertDatabaseUnavailableAsync(
        PostgresIntegrationSettings settings,
        string databaseName)
    {
        string applicationName = "change32_verify_drop_" + Guid.NewGuid().ToString("N")[..18];
        var builder = new NpgsqlConnectionStringBuilder(settings.CreateConnectionString(databaseName, applicationName))
        {
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 5,
            IncludeErrorDetail = false
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(CancellationToken.None).WaitAsync(CrossProcessRunExclusionCase.Watchdog);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return;
        }

        Assert.Fail($"Owned Change32 database {databaseName} remained connectable after cleanup.");
    }
}
