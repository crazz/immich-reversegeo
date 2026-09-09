using System.Collections.Concurrent;
using System.Reflection;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.ChildWorkerLaunching;

public sealed partial class ChildWorkerLaunchingTests
{
    [TestMethod]
    [TestCategory("Change51")]
    [DataRow("progress-source", true, "source")]
    [DataRow("progress-operation", true, "operation")]
    [DataRow("progress-iso3", true, "iso3")]
    [DataRow("result-source", false, "source")]
    [DataRow("result-operation", false, "operation")]
    [DataRow("result-iso3", false, "iso3")]
    public async Task CacheMutationSession_RejectsOutputThatDiffersFromImmutableRequest(
        string label,
        bool isProgress,
        string mismatch)
    {
        var process = new ByteProcess(950);
        DateTimeOffset startedAt = new(2026, 9, 9, 11, 0, 0, TimeSpan.Zero);
        var dispatch = new CacheMutationWorkerJobDispatch(
            Guid.Parse("50515151-5151-5151-5151-515151515151"),
            new CacheMutationRequest(
                CacheMutationSource.Gadm,
                CacheMutationOperation.Refresh,
                "CHE"));
        var sink = new CacheEventSink();
        ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
            process,
            dispatch,
            sink,
            TestOptions(),
            new ChildWorkerObserverArmingAcknowledgements(),
            InternalWorkerProtocolVersion.V2);
        try
        {
            process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Ready(
                1,
                startedAt.AddSeconds(-1),
                new WorkerJobReadyPayload([WorkerJobKind.CacheMutation]))));
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(
                await session.Startup,
                $"{label}: ready-accepted");
            process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.JobStarted(
                dispatch.Context,
                "manual",
                startedAt,
                2)));
            process.StandardOutput.Write(Frame(isProgress
                ? CreateMismatchedProgress(dispatch.Context, startedAt, mismatch)
                : CreateMismatchedTerminal(dispatch.Context, startedAt, mismatch)));
            process.StandardOutput.Complete();
            process.StandardError.Complete();
            process.Exit(6);

            ChildWorkerCompletionObservation completion =
                await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            var failure = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(
                completion.FirstProtocolObservation,
                $"{label}: first-observation");
            Assert.AreEqual(
                WorkerProtocolFailureCode.InvalidCorrelation,
                failure.Failure.Code,
                $"{label}: exact-code");
            Assert.AreEqual(2, sink.Events.Length, $"{label}: invalid-row-not-delivered");
            Assert.IsNull(completion.JobTerminal, $"{label}: invalid-result-not-terminal");
        }
        finally
        {
            process.StandardOutput.Complete();
            process.StandardError.Complete();
            process.Exit(6);
            await session.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task CacheMutationClient_HoldsOutcomeUntilProcessAndBothStreamsReachFinality()
    {
        var process = new ByteProcess(951);
        var factory = new RecordingFactory { Process = process };
        var client = new CacheMutationWorkerClient(
            new CacheInvocationBuilder(),
            new ChildWorkerLauncher(factory),
            TimeProvider.System);
        var dispatch = new CacheMutationWorkerJobDispatch(
            Guid.Parse("51515151-5151-5151-5151-515151515151"),
            new CacheMutationRequest(
                CacheMutationSource.Overture,
                CacheMutationOperation.Refresh,
                "CHE"));
        var lease = new CacheAdmissionLease(dispatch);
        var sink = new CacheEventSink();

        CacheMutationWorkerStartResult start = await client.StartAsync(
            lease,
            dispatch.Request,
            sink,
            CancellationToken.None);
        var started = Assert.IsInstanceOfType<CacheMutationWorkerStartResult.Started>(start);
        ICacheMutationWorkerSession session = started.Session;
        ChildWorkerSession childSession = GetInnerSession(session);
        DateTimeOffset startedAt = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var context = dispatch.Context;
        var result = new CacheMutationResult(
            startedAt,
            startedAt.AddSeconds(1),
            new CacheMutationSourceResult(
                CacheMutationSource.Overture,
                CacheMutationOperation.Refresh,
                "CHE",
                CacheMutationDisposition.Published,
                2,
                startedAt,
                4096,
                "2026-09-03.0",
                null));

        byte[] ready = Frame(WorkerJobProtocolMapper.Ready(
            1,
            startedAt.AddSeconds(-1),
            new WorkerJobReadyPayload([WorkerJobKind.CacheMutation])));
        byte[] jobStarted = Frame(WorkerJobProtocolMapper.JobStarted(
            context,
            "manual",
            startedAt,
            2));
        byte[] terminal = Frame(WorkerJobProtocolMapper.Terminal(
            context,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                result.StartedAtUtc,
                result.EndedAtUtc,
                null,
                null,
                result,
                null),
            3));
        try
        {
            process.StandardOutput.Write(ready);
            process.StandardOutput.Write(jobStarted);
            process.StandardOutput.Write(terminal);

            await process.StandardOutput.WaitForConsumedAsync(
                ready.LongLength + jobStarted.LongLength + terminal.LongLength);
            await sink.TerminalObserved.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(session.Completion.IsCompleted, "terminal-alone-is-not-finality");

            process.Exit(0);
            await childSession.PhysicalExitConfirmed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(session.Completion.IsCompleted, "physical-exit-still-awaits-streams");
            process.StandardOutput.Complete();
            await GetStreamFinalityTask(childSession, "_standardOutputTask")
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(session.Completion.IsCompleted, "stdout-finality-still-awaits-stderr");
            Assert.AreEqual(0, process.DisposeCalls, "resources-remain-owned-before-all-finality");
            process.StandardError.Complete();
            await GetStreamFinalityTask(childSession, "_standardErrorTask")
                .WaitAsync(TimeSpan.FromSeconds(5));

            var outcome = Assert.IsInstanceOfType<CacheMutationWorkerOutcome.Completed>(
                await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(result, outcome.Result);
            Assert.AreEqual(3, sink.Events.Length);
            Assert.AreEqual(1, process.DisposeCalls, "settlement-disposes-owned-process-once");

            await session.DisposeAsync();
            Assert.AreEqual(1, process.DisposeCalls, "explicit-owner-dispose-is-idempotent");
        }
        finally
        {
            process.StandardOutput.Complete();
            process.StandardError.Complete();
            process.Exit(0);
            await session.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    [DataRow(0, "cache-missingterminal")]
    [DataRow(2, "cache-inconsistentexit")]
    [DataRow(3, "cache-inconsistentexit")]
    [DataRow(4, "cache-inconsistentexit")]
    [DataRow(5, "cache-inconsistentexit")]
    [DataRow(6, "cache-inconsistentexit")]
    [DataRow(130, "cache-inconsistentexit")]
    public async Task CacheMutationClient_NoTerminalPortableExitNeverFabricatesSuccessOrCacheBusy(
        int exitCode,
        string expectedCode)
    {
        var process = new ByteProcess(952 + exitCode);
        var factory = new RecordingFactory { Process = process };
        var client = new CacheMutationWorkerClient(
            new CacheInvocationBuilder(),
            new ChildWorkerLauncher(factory),
            TimeProvider.System);
        var dispatch = new CacheMutationWorkerJobDispatch(
            Guid.NewGuid(),
            new CacheMutationRequest(
                CacheMutationSource.Overture,
                CacheMutationOperation.Refresh,
                "CHE"));
        var lease = new CacheAdmissionLease(dispatch);
        var sink = new CacheEventSink();
        CacheMutationWorkerStartResult start = await client.StartAsync(
            lease,
            dispatch.Request,
            sink,
            CancellationToken.None);
        ICacheMutationWorkerSession session =
            Assert.IsInstanceOfType<CacheMutationWorkerStartResult.Started>(start).Session;
        ChildWorkerSession childSession = GetInnerSession(session);
        DateTimeOffset startedAt = new(2026, 9, 9, 12, 30, 0, TimeSpan.Zero);
        try
        {
            process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Ready(
                1,
                startedAt.AddSeconds(-1),
                new WorkerJobReadyPayload([WorkerJobKind.CacheMutation]))));
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(
                await childSession.Startup.WaitAsync(TimeSpan.FromSeconds(5)));
            process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.JobStarted(
                dispatch.Context,
                "manual",
                startedAt,
                2)));
            process.StandardOutput.Complete();
            process.StandardError.Complete();
            process.Exit(exitCode);

            var failed = Assert.IsInstanceOfType<CacheMutationWorkerOutcome.Failed>(
                await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(expectedCode, failed.Code);
            Assert.AreEqual(2, sink.Events.Length);
            Assert.IsFalse(sink.Events.Any(static value =>
                value.Payload is WorkerJobTerminalPayload));
            Assert.AreEqual(1, process.DisposeCalls);
        }
        finally
        {
            process.StandardOutput.Complete();
            process.StandardError.Complete();
            process.Exit(exitCode);
            await session.DisposeAsync();
        }
    }

    private sealed class CacheInvocationBuilder : IWorkerCommandInvocationBuilder
    {
        public WorkerCommandInvocationResolution Build() =>
            Build(InternalWorkerProtocolVersion.V2);

        public WorkerCommandInvocationResolution Build(
            InternalWorkerProtocolVersion protocolVersion)
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
            return WorkerInvocation.Resolve(facts, protocolVersion);
        }
    }

    private static WorkerJobOutputMessage CreateMismatchedProgress(
        WorkerJobContext context,
        DateTimeOffset startedAt,
        string mismatch)
    {
        CacheMutationSource source = mismatch == "source"
            ? CacheMutationSource.Overture
            : CacheMutationSource.Gadm;
        CacheMutationOperation operation = mismatch == "operation"
            ? CacheMutationOperation.Ensure
            : CacheMutationOperation.Refresh;
        string iso3 = mismatch == "iso3" ? "USA" : "CHE";
        CacheMutationGadmAttribution? attribution = source == CacheMutationSource.Gadm
            ? GadmAttribution("4.1")
            : null;
        return WorkerJobProtocolMapper.Map(
            context,
            new WorkerJobHandlerEvent(
                startedAt.AddSeconds(1),
                new CacheMutationProgressPayload(
                    CacheMutationProgressStep.Exporting,
                    source,
                    operation,
                    iso3,
                    "Exporting cache data.",
                    attribution)),
            3);
    }

    private static WorkerJobOutputMessage CreateMismatchedTerminal(
        WorkerJobContext context,
        DateTimeOffset startedAt,
        string mismatch)
    {
        CacheMutationSource source = mismatch == "source"
            ? CacheMutationSource.Overture
            : CacheMutationSource.Gadm;
        CacheMutationOperation operation = mismatch == "operation"
            ? CacheMutationOperation.Ensure
            : CacheMutationOperation.Refresh;
        string iso3 = mismatch == "iso3" ? "USA" : "CHE";
        CacheMutationGadmAttribution? attribution = source == CacheMutationSource.Gadm
            ? GadmAttribution("4.1")
            : null;
        var result = new CacheMutationResult(
            startedAt,
            startedAt.AddSeconds(1),
            new CacheMutationSourceResult(
                source,
                operation,
                iso3,
                CacheMutationDisposition.Published,
                1,
                startedAt,
                1024,
                "4.1",
                attribution));
        return WorkerJobProtocolMapper.Terminal(
            context,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                result.StartedAtUtc,
                result.EndedAtUtc,
                null,
                null,
                result,
                null),
            3);
    }

    private static CacheMutationGadmAttribution GadmAttribution(string version) =>
        new(
            CacheMutationGadmAttribution.OfficialDatasetName,
            version,
            CacheMutationGadmAttribution.OfficialLicenseUrl,
            CacheMutationGadmAttribution.NonCommercialUseNotice);

    private static ChildWorkerSession GetInnerSession(
        ICacheMutationWorkerSession session) =>
        Assert.IsInstanceOfType<ChildWorkerSession>(
            session.GetType()
                .GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(session));

    private static Task<ChildWorkerStreamFinality> GetStreamFinalityTask(
        ChildWorkerSession session,
        string fieldName) =>
        Assert.IsInstanceOfType<Task<ChildWorkerStreamFinality>>(
            typeof(ChildWorkerSession)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(session));

    private sealed class CacheAdmissionLease(
        CacheMutationWorkerJobDispatch dispatch) : IWorkerJobAdmissionLease
    {
        public WorkerJobContext Context => dispatch.Context;
        public WorkerJobDescriptor Descriptor => dispatch.Descriptor;
        public bool IsStopRequested { get; private set; }

        public bool TryBindOwnerStop(
            WorkerJobContext context,
            Func<Task> requestStopAsync) =>
            ReferenceEquals(context, Context);

        public bool TryAdvance(
            WorkerJobContext context,
            WorkerJobLifecycle lifecycle,
            int? childProcessId = null)
        {
            IsStopRequested |= lifecycle == WorkerJobLifecycle.Stopping;
            return ReferenceEquals(context, Context);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CacheEventSink : IWorkerJobEventSink
    {
        private readonly ConcurrentQueue<WorkerJobOutputMessage> _events = new();
        private readonly TaskCompletionSource _terminalObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal WorkerJobOutputMessage[] Events => _events.ToArray();
        internal Task TerminalObserved => _terminalObserved.Task;

        public ValueTask AcceptAsync(
            WorkerJobOutputMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Enqueue(message);
            if (message.Payload is WorkerJobTerminalPayload)
            {
                _terminalObserved.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }
}
