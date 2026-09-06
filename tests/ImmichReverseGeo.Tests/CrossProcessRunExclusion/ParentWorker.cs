using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;
using WebWorkerCommandInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.CrossProcessRunExclusion;

internal sealed class ParentWorker : IAsyncDisposable
{
    private readonly object _disposeGate = new();
    private readonly CrossProcessRunExclusionCase _case;
    private readonly ChildProcessStartDescriptor _descriptor;
    private readonly NamedPipeServerStream? _releasePipe;
    private readonly string _applicationName;
    private readonly string? _markerPath;
    private readonly string _workerRoot;
    private readonly string _capturePath;
    private readonly ProcessCoordinatorFixture _fixture;
    private readonly PostgresWorkerEnvironment _environment;
    private readonly int _coordinatorGeneration;
    private readonly ConcurrentQueue<WorkerProtocolEvent> _events = new();
    private readonly SemaphoreSlim _eventAvailable = new(0);
    private readonly TaskCompletionSource<ChildWorkerSession> _sessionAvailable =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _logStartIndex;
    private Task? _activeCleanupTask;
    private IChildProcess? _process;
    private Task<int>? _exitTask;
    private int? _processId;
    private ChildWorkerSession? _session;
    private ProcessingRunRequest? _request;
    private ProcessingRunFinalizationReceipt? _receipt;
    private ChildWorkerCompletionObservation? _completion;
    private bool _exitObserved;
    private bool _inputClosed;
    private bool _releasePipeDisposed;
    private bool _eventSignalDisposed;
    private bool _resourcesReleased;
    private bool _finalityObservedBeforeDetach;
    private bool _coordinatorStopAccepted;
    private int _activeSinkCallbacks;

    private ParentWorker(
        CrossProcessRunExclusionCase @case,
        ChildProcessStartDescriptor descriptor,
        NamedPipeServerStream? releasePipe,
        string applicationName,
        string workerRoot,
        string? markerPath,
        ProcessCoordinatorFixture? fixture)
    {
        _case = @case;
        _descriptor = descriptor;
        _releasePipe = releasePipe;
        _applicationName = applicationName;
        _workerRoot = workerRoot;
        _capturePath = Path.Combine(workerRoot, "controller-input.ndjson");
        _markerPath = markerPath;
        _environment = PostgresWorkerEnvironment.Create(@case.Database.CreateConnectionString(applicationName), applicationName);
        _fixture = fixture ?? new ProcessCoordinatorFixture();
        _fixture.Prepare(this);
        _coordinatorGeneration = _fixture.Generation;
        _logStartIndex = State.GetRecentLog().Count;
    }

    internal CrossProcessRunExclusionCase OwnerCase => _case;
    internal ProcessCoordinatorFixture Fixture => _fixture;
    internal int ProcessId => _processId ?? throw new InvalidOperationException("The Change32 worker has not started.");
    internal string ApplicationName => _applicationName;
    internal string WorkerRoot => _workerRoot;
    internal string CapturePath => _capturePath;
    internal string? MarkerPath => _markerPath;
    internal int CoordinatorGeneration => _coordinatorGeneration;
    internal IReadOnlyList<string> DescriptorArguments => _descriptor.Arguments;
    internal ProcessingRunCoordinator Coordinator => _fixture.Coordinator;
    internal ProcessingState State => _fixture.State;
    internal ProcessingRunRequest Request => _request ?? throw new InvalidOperationException("The Change32 worker has no admitted request.");
    internal IReadOnlyCollection<WorkerProtocolEvent> Events => _events.ToArray();
    internal IReadOnlyList<string> ProjectedLog => State.GetRecentLog().Skip(_logStartIndex).ToArray();
    internal ChildWorkerCompletionObservation Completion => _completion ?? throw new InvalidOperationException("The Change32 worker has not completed.");
    internal ChildWorkerCancellationFacts? CancellationFacts => _session?.CancellationFacts;
    // The raw session facts can include a disposal-origin stop after process exit.
    // This view records only a successful explicit StopCooperatively claim by the harness.
    internal ChildWorkerCancellationFacts? CoordinatorCancellationFacts =>
        _coordinatorStopAccepted ? CancellationFacts : null;
    internal int TreeKillCalls { get; private set; }
    internal int LauncherCalls { get; private set; }
    internal int ProcessDisposeCalls => (_process as CapturedChildProcess)?.DisposeCalls ?? 0;
    internal bool HasExited => _exitTask?.IsCompletedSuccessfully == true;
    internal bool IsFullyFinalized
    {
        get
        {
            ChildWorkerSession? session = _session;
            return _completion is not null
                && _exitObserved
                && _finalityObservedBeforeDetach
                && session is not null
                && session.EvidenceFinality.IsCompletedSuccessfully
                && session.Settlement.IsCompletedSuccessfully
                && session.Completion.IsCompletedSuccessfully
                && ProcessDisposeCalls == 1
                && Volatile.Read(ref _activeSinkCallbacks) == 0
                && _fixture.Coordinator.ActiveRequest is null;
        }
    }
    internal bool ResourcesReleased => _resourcesReleased;
    internal bool MarkerExists => _markerPath is not null && File.Exists(_markerPath);
    internal ProcessingRunFinalizationReceipt? Receipt => _receipt
        ?? (_request is null ? null : _fixture.Reporter.GetFinalizationReceipt(_request));

    internal static Task<ParentWorker> CreateControlledAsync(
        CrossProcessRunExclusionCase @case,
        string scenario,
        ProcessCoordinatorFixture? fixture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.Combine(@case.Root, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        string marker = Path.Combine(root, "owner.json");
        string applicationName = CreateSafeToken("change32", 48);
        string pipeName = CreateSafeToken("c32", 96);
        var releasePipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        IReadOnlyList<string> arguments = ["--scenario", scenario, "--resource-root", root, "--marker-path", marker, "--release-pipe", pipeName];
        try
        {
            return Task.FromResult(new ParentWorker(
                @case,
                CrossProcessAppHostLocator.CreateCrossProcessRunLockDescriptor(arguments),
                releasePipe,
                applicationName,
                root,
                marker,
                fixture));
        }
        catch
        {
            releasePipe.Dispose();
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    internal static Task<ParentWorker> CreateProductionAsync(
        CrossProcessRunExclusionCase @case,
        ProcessCoordinatorFixture? fixture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.Combine(@case.Root, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        try
        {
            return Task.FromResult(new ParentWorker(
                @case,
                CrossProcessAppHostLocator.CreateProductionWorkerDescriptor(),
                null,
                CreateSafeToken("change32production", 55),
                root,
                null,
                fixture));
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_releasePipe is null)
        {
            await _environment.ProbeEffectiveProductionConnectionAsync(cancellationToken).WaitAsync(CrossProcessRunExclusionCase.Watchdog, cancellationToken);
        }

        ProcessingRunAdmissionResult admission = await _fixture.Coordinator.TriggerManualAsync().WaitAsync(CrossProcessRunExclusionCase.Watchdog, cancellationToken);
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, admission, "The real parent coordinator must admit its independent worker.");
        await WaitForEventAsync(@event => @event.Type == WorkerProtocolV1.ReadyType, cancellationToken);
        ChildWorkerSession session = await _sessionAvailable.Task.WaitAsync(CrossProcessRunExclusionCase.Watchdog, cancellationToken);
        await session.ExecuteRequestAccepted.WaitAsync(CrossProcessRunExclusionCase.Watchdog, cancellationToken);
        Assert.AreEqual(Request.RunId, session.RunId, "The admitted request must own the exact started session.");
        AssertCapturedControllerInput(WorkerProtocolV1.ExecuteType);
    }

    internal async Task WaitForProtectedOperationAsync(CancellationToken cancellationToken)
    {
        await WaitForEventAsync(@event => @event.Type == WorkerProtocolV1.RunStartedType, cancellationToken);
        WorkerProtocolEvent eligibility = await WaitForEventAsync(
            @event => @event.Payload is EligibilityDeterminedPayload,
            cancellationToken);
        Assert.AreEqual(
            0L,
            ((EligibilityDeterminedPayload)eligibility.Payload).EligibleCount,
            "The controlled apphost must execute the real empty-database domain gate before holding.");
        await WaitForEventAsync(@event => @event.Payload is LogEmittedPayload log
            && string.Equals(log.Message, "Change32 protected operation entered", StringComparison.Ordinal), cancellationToken);
        Assert.IsNotNull(_markerPath, "Only controllable workers publish Change32 markers.");
        Assert.IsTrue(File.Exists(_markerPath), "The protected-operation log must be paired with an atomic marker.");
    }

    internal async Task<PostgresLockOwner> ReadMarkerAsync(CancellationToken cancellationToken)
    {
        await WaitForProtectedOperationAsync(cancellationToken);
        using JsonDocument marker = JsonDocument.Parse(await File.ReadAllTextAsync(_markerPath!, cancellationToken));
        int pid = marker.RootElement.GetProperty("backendProcessId").GetInt32();
        string applicationName = marker.RootElement.GetProperty("applicationName").GetString()!;
        Assert.AreEqual(_applicationName, applicationName, "Marker must identify this exact child database session.");
        PostgresLockOwner? actualOwner = await _case.Database
            .FindOwnedBackendAsync(applicationName, cancellationToken)
            .WaitAsync(CrossProcessRunExclusionCase.Watchdog, cancellationToken);
        Assert.IsNotNull(actualOwner, "The published backend must still own the exact production lock.");
        Assert.AreEqual(pid, actualOwner.ProcessId, "The marker and live production-key owner must identify the same backend.");
        return new PostgresLockOwner(pid, applicationName);
    }

    internal async Task ReleaseAsync(byte releaseByte, CancellationToken cancellationToken)
    {
        if (_releasePipe is null)
        {
            throw new InvalidOperationException("Production workers do not have a controlled release gate.");
        }

        await _releasePipe.WaitForConnectionAsync(cancellationToken).WaitAsync(CrossProcessRunExclusionCase.Watchdog, cancellationToken);
        await _releasePipe.WriteAsync(new byte[] { releaseByte }, cancellationToken)
            .AsTask()
            .WaitAsync(CrossProcessRunExclusionCase.Watchdog, cancellationToken);
        await _releasePipe.FlushAsync(cancellationToken)
            .WaitAsync(CrossProcessRunExclusionCase.Watchdog, cancellationToken);
    }

    internal async Task<ChildWorkerCompletionObservation> CompleteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(CrossProcessRunExclusionCase.Watchdog, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw CreateCompletionTimeout("coordinator-idle");
        }

        ChildWorkerSession session = await _sessionAvailable.Task.WaitAsync(CrossProcessRunExclusionCase.Watchdog, cancellationToken);
        ChildWorkerCompletionObservation completion = await session.Completion.WaitAsync(CrossProcessRunExclusionCase.Watchdog, cancellationToken);
        _completion = completion;
        _receipt = _fixture.Reporter.GetFinalizationReceipt(Request);
        _exitObserved = _exitTask?.IsCompletedSuccessfully == true;
        Assert.IsTrue(completion.ExitObserved, "Child OS exit must be observed before coordinator idle is asserted.");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality);
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality);
        Assert.IsTrue(session.EvidenceFinality.IsCompletedSuccessfully, "Evidence finality must precede coordinator idle.");
        Assert.IsTrue(session.Settlement.IsCompletedSuccessfully, "Session settlement must precede coordinator idle.");
        Assert.IsTrue(session.Completion.IsCompletedSuccessfully, "Both stream drains must finish before coordinator idle.");
        Assert.IsTrue(_finalityObservedBeforeDetach, "The coordinator detach boundary must observe full child finality and disposal.");
        Assert.AreEqual(0, Volatile.Read(ref _activeSinkCallbacks), "No event projection callback may outlive coordinator ownership.");
        Assert.AreEqual(1, ProcessDisposeCalls, "The real process adapter must be disposed exactly once before coordinator idle.");
        Assert.AreEqual(1, LauncherCalls, "One admitted request must launch exactly one worker without retry.");
        Assert.IsFalse(State.IsRunning, "Projected processing state must be idle after terminal finality.");
        Assert.IsNull(State.CurrentActivity, "Projected activities must be closed after terminal finality.");
        Assert.IsNull(_fixture.Coordinator.ActiveRequest, "The matching coordinator handle must be released after finality.");
        AssertCapturedControllerInput(_coordinatorStopAccepted
            ? WorkerProtocolV1.CancelType
            : WorkerProtocolV1.ExecuteType);
        return completion;
    }

    internal void AssertProjectionReplayIsIdempotent()
    {
        ProcessingRunFinalizationReceipt receipt = Receipt
            ?? throw new AssertFailedException("The exact admitted request must have a finalization receipt.");
        ProcessingRunFinalizationAttempt replay = _fixture.Reporter.TryFinalize(
            Request,
            receipt.Result,
            receipt.Origin);
        Assert.AreEqual(
            ProcessingRunFinalizationDisposition.ExistingWinner,
            replay.Disposition,
            "A second finalization attempt must observe the existing winner.");
        Assert.AreSame(receipt, replay.Receipt, "Finalization replay must return the identical receipt.");
        Assert.AreSame(Request, receipt.Request, "The receipt must retain the exact admitted request instance.");
        Assert.AreSame(Request, receipt.Result.Request, "The projected result must retain the exact admitted request instance.");
    }

    internal WorkerRunDecision ClassifyWithoutTerminalReceipt()
    {
        return WorkerRunEvidenceClassifier.Classify(new WorkerRunEvidence
        {
            Request = Request,
            LastPhase = WorkerRunTransportPhase.EvidenceFinal,
            Completion = Completion,
            Cancellation = CancellationFacts
        });
    }

    internal async Task<WorkerProtocolEvent> WaitForEventAsync(Func<WorkerProtocolEvent, bool> predicate, CancellationToken cancellationToken)
    {
        while (true)
        {
            WorkerProtocolEvent? observed = _events.FirstOrDefault(predicate);
            if (observed is not null)
            {
                return observed;
            }

            await _eventAvailable.WaitAsync(CrossProcessRunExclusionCase.Watchdog, cancellationToken);
        }
    }

    internal ChildProcessKillOutcome KillTree()
    {
        TreeKillCalls++;
        return _process!.KillProcessTree();
    }

    internal void StopCooperatively()
    {
        Assert.IsNotNull(_fixture.Coordinator.StopActiveRun(), "The active owner must receive the correlated coordinator stop.");
        _coordinatorStopAccepted = true;
    }

    internal async ValueTask<ChildWorkerLaunchResult> LaunchAsync(
        WorkerInvocation ignoredInvocation,
        ProcessingRunRequest request,
        IWorkerProtocolEventSink sink,
        ChildWorkerLauncherOptions options,
        CancellationToken cancellationToken)
    {
        LauncherCalls++;
        BindRequest(request);
        var launcher = new ChildWorkerLauncher(new RegisteredProcessFactory(this));
        var result = await launcher.LaunchDescriptorAsync(
            _descriptor,
            request,
            new TeeSink(this, sink),
            options,
            cancellationToken);
        if (result is ChildWorkerLaunchResult.Started started)
        {
            _session = started.Session;
            _sessionAvailable.TrySetResult(started.Session);
        }
        else
        {
            _sessionAvailable.TrySetException(new InvalidOperationException("The Change32 child worker did not start."));
        }

        return result;
    }

    internal void RegisterStartedProcess(IChildProcess process)
    {
        // Registration happens synchronously inside StartAsync, before launcher/session awaits.
        _process = process;
        _processId = process.ProcessId;
        _exitTask = process.WaitForExitAsync();
    }

    internal void ReplaceRegisteredProcess(CapturedChildProcess process)
    {
        if (_processId != process.ProcessId || _exitTask is null)
        {
            throw new InvalidOperationException("The captured adapter must wrap the exact registered process.");
        }

        _process = process;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_resourcesReleased)
            {
                return ValueTask.CompletedTask;
            }

            if (_activeCleanupTask is null || _activeCleanupTask.IsCompleted)
            {
                _activeCleanupTask = DisposeCoreAsync();
            }

            return new ValueTask(_activeCleanupTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var failures = new List<Exception>();
        try
        {
            if (_releasePipe is not null
                && _releasePipe.IsConnected
                && _exitTask?.IsCompletedSuccessfully != true)
            {
                await _releasePipe.WriteAsync(new byte[] { 0 }).AsTask().WaitAsync(CrossProcessRunExclusionCase.Watchdog);
                await _releasePipe.FlushAsync().WaitAsync(CrossProcessRunExclusionCase.Watchdog);
            }
        }
        catch (IOException) when (_exitTask?.IsCompletedSuccessfully == true)
        {
        }
        catch (Exception exception)
        {
            failures.Add(CleanupFailure("release-gate", exception));
        }

        try
        {
            if (!_inputClosed && _process is not null)
            {
                _process.StandardInput.Dispose();
                _inputClosed = true;
            }
        }
        catch (Exception exception)
        {
            failures.Add(CleanupFailure("close-input", exception));
        }

        if (!_exitObserved && _exitTask?.IsCompletedSuccessfully == true)
        {
            _exitObserved = true;
        }

        if (!_exitObserved)
        {
            try
            {
                if (_process is not null && _process.GetExitState() != ChildProcessExitState.Exited)
                {
                    ChildProcessKillOutcome outcome = KillTree();
                    if (outcome is not ChildProcessKillOutcome.Requested and not ChildProcessKillOutcome.AlreadyExited)
                    {
                        throw new InvalidOperationException($"The registered process rejected scoped tree termination with {outcome}.");
                    }
                }

                if (_exitTask is not null)
                {
                    await _exitTask.WaitAsync(CrossProcessRunExclusionCase.Watchdog);
                    _exitObserved = true;
                }
            }
            catch (Exception exception)
            {
                failures.Add(CleanupFailure("reap-process", exception));
            }
        }

        if (_exitObserved)
        {
            try
            {
                if (_session is not null)
                {
                    await _session.EvidenceFinality.WaitAsync(CrossProcessRunExclusionCase.Watchdog);
                    await _session.Settlement.WaitAsync(CrossProcessRunExclusionCase.Watchdog);
                    _completion ??= await _session.Completion.WaitAsync(CrossProcessRunExclusionCase.Watchdog);
                    _receipt ??= _fixture.Reporter.GetFinalizationReceipt(Request);
                    await _session.DisposeAsync().AsTask().WaitAsync(CrossProcessRunExclusionCase.Watchdog);
                }
                else if (_process is not null)
                {
                    await _process.DisposeAsync().AsTask().WaitAsync(CrossProcessRunExclusionCase.Watchdog);
                }

                if (_exitTask is not null && !_exitTask.IsCompletedSuccessfully)
                {
                    throw new InvalidOperationException("The registered process did not reach a confirmed exit state.");
                }

                if (_process is CapturedChildProcess && ProcessDisposeCalls != 1)
                {
                    throw new InvalidOperationException("The registered process adapter was not disposed exactly once.");
                }
            }
            catch (Exception exception)
            {
                failures.Add(CleanupFailure("finalize-session", exception));
            }
        }

        try
        {
            await _fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(CrossProcessRunExclusionCase.Watchdog);
            if (_fixture.Coordinator.ActiveRequest is not null)
            {
                throw new InvalidOperationException("The coordinator retained its active request after worker cleanup.");
            }
        }
        catch (Exception exception)
        {
            failures.Add(CleanupFailure("join-coordinator", exception));
        }

        if (failures.Count == 0 && Volatile.Read(ref _activeSinkCallbacks) != 0)
        {
            failures.Add(CleanupFailure(
                "event-callbacks",
                new InvalidOperationException("A protocol projection callback remained active after stream finality.")));
        }

        if (failures.Count > 0)
        {
            throw new AggregateException($"Change32 worker cleanup failed for PID {ProcessIdentity}, app {_applicationName}.", failures);
        }

        if (!_releasePipeDisposed)
        {
            _releasePipe?.Dispose();
            _releasePipeDisposed = true;
        }

        if (!_eventSignalDisposed)
        {
            _eventAvailable.Dispose();
            _eventSignalDisposed = true;
        }

        _resourcesReleased = true;
    }

    private void AssertCapturedControllerInput(string expectedLastType)
    {
        Assert.IsTrue(File.Exists(_capturePath), "The parent-owned stdin capture must exist after execute acceptance.");
        string[] frames = Encoding.UTF8
            .GetString(File.ReadAllBytes(_capturePath))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int expectedCount = expectedLastType == WorkerProtocolV1.CancelType ? 2 : 1;
        Assert.AreEqual(expectedCount, frames.Length, "Controller input must contain only the canonical execute and optional cancel frames.");
        for (var index = 0; index < frames.Length; index++)
        {
            WorkerProtocolControllerParseResult parsed = WorkerProtocolCodec.ParseControllerInput(
                Encoding.UTF8.GetBytes(frames[index]));
            Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
            Assert.AreEqual(index + 1L, parsed.Message!.Sequence, "Controller input sequence must be canonical.");
            Assert.AreEqual(Request.RunId, parsed.Message.RunId, "Every controller frame must correlate to the exact admitted run.");
            Assert.AreEqual(
                index == 0 ? WorkerProtocolV1.ExecuteType : WorkerProtocolV1.CancelType,
                parsed.Message.Type,
                "Controller input must retain the production request/control order.");
            if (index > 0)
            {
                Assert.IsInstanceOfType<CancelControlPayload>(
                    parsed.Message.Payload,
                    "Captured cancel input must use the production protocol payload type.");
            }
        }

        ExecuteRequestPayload execute = Assert.IsInstanceOfType<ExecuteRequestPayload>(
            WorkerProtocolCodec.ParseControllerInput(Encoding.UTF8.GetBytes(frames[0])).Message!.Payload);
        Assert.AreEqual(Request, execute.Request, "Captured execute must preserve the production request DTO.");
    }

    internal void BindRequest(ProcessingRunRequest request)
    {
        if (_request is not null && !ReferenceEquals(_request, request))
        {
            throw new InvalidOperationException("A Change32 worker cannot be rebound to another admitted request.");
        }

        _request = request;
    }

    internal void ObserveBeforeDetach(ProcessingRunRequest request)
    {
        ChildWorkerSession? session = _session;
        _finalityObservedBeforeDetach = session is not null
            && ReferenceEquals(request, _request)
            && _exitTask?.IsCompletedSuccessfully == true
            && session.EvidenceFinality.IsCompletedSuccessfully
            && session.Settlement.IsCompletedSuccessfully
            && session.Completion.IsCompletedSuccessfully
            && ProcessDisposeCalls == 1
            && Volatile.Read(ref _activeSinkCallbacks) == 0;
    }

    private void EnterSinkCallback() => Interlocked.Increment(ref _activeSinkCallbacks);

    private void ExitSinkCallback() => Interlocked.Decrement(ref _activeSinkCallbacks);

    private Exception CleanupFailure(string phase, Exception failure)
    {
        _ = failure;
        return new InvalidOperationException(
            $"Change32 cleanup phase {phase} failed for PID {ProcessIdentity}, app {_applicationName}, database {_case.Database.DatabaseName}.");
    }

    private TimeoutException CreateCompletionTimeout(string phase)
    {
        ChildWorkerSession? session = _session;
        string processState;
        try
        {
            processState = _process?.GetExitState().ToString() ?? "not-started";
        }
        catch
        {
            processState = "unavailable";
        }

        WorkerProtocolEvent? lastEvent = _events.LastOrDefault();
        int terminalCount = _events.Count(@event => WorkerProtocolV1.IsTerminal(@event.Type));
        return new TimeoutException(
            $"Change32 completion watchdog expired at {phase} for PID {ProcessIdentity}, app {_applicationName}, database {_case.Database.DatabaseName}; " +
            $"process={processState}, last-event={lastEvent?.Type ?? "none"}, terminal-count={terminalCount}, " +
            $"execute-accepted={session?.ExecuteRequestAccepted.IsCompletedSuccessfully == true}, " +
            $"evidence-final={session?.EvidenceFinality.IsCompleted == true}, " +
            $"settled={session?.Settlement.IsCompleted == true}, completion={session?.Completion.IsCompleted == true}, " +
            $"receipt={Receipt is not null}, callbacks={Volatile.Read(ref _activeSinkCallbacks)}, active-request={_fixture.Coordinator.ActiveRequest is not null}.");
    }

    private string ProcessIdentity => _processId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not-started";

    private static string CreateSafeToken(string prefix, int maximumLength)
    {
        string value = prefix + "_" + Guid.NewGuid().ToString("N");
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private sealed class RegisteredProcessFactory(ParentWorker owner) : IChildProcessFactory
    {
        public ValueTask<IChildProcess?> StartAsync(ChildProcessStartDescriptor descriptor, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessStartInfo startInfo = SystemChildProcessFactory.CreateStartInfo(descriptor);
            foreach (string key in startInfo.Environment.Keys.Where(key => key.StartsWith("PG", StringComparison.OrdinalIgnoreCase) || key.StartsWith("DB_", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                startInfo.Environment.Remove(key);
            }
            foreach (string key in owner._environment.VariablesToClear)
            {
                startInfo.Environment.Remove(key);
            }
            foreach ((string key, string value) in owner._environment.Values)
            {
                startInfo.Environment[key] = value;
            }

            string childRoot = Path.Combine(owner._case.Root, "child-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(childRoot);
            startInfo.Environment["DATA_DIR"] = Path.Combine(childRoot, "data");
            startInfo.Environment["CONFIG_DIR"] = Path.Combine(childRoot, "config");
            var nativeProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            try
            {
                if (!nativeProcess.Start())
                {
                    nativeProcess.Dispose();
                    return ValueTask.FromResult<IChildProcess?>(null);
                }
            }
            catch
            {
                nativeProcess.Dispose();
                throw;
            }

            var systemProcess = new SystemChildProcessFactory.SystemChildProcess(nativeProcess);
            owner.RegisterStartedProcess(systemProcess);
            var capturedProcess = new CapturedChildProcess(systemProcess, owner._capturePath);
            owner.ReplaceRegisteredProcess(capturedProcess);
            return ValueTask.FromResult<IChildProcess?>(capturedProcess);
        }
    }

    private sealed class TeeSink(ParentWorker owner, IWorkerProtocolEventSink inner) : IWorkerProtocolEventSink
    {
        public async ValueTask AcceptAsync(WorkerProtocolEvent @event, CancellationToken cancellationToken)
        {
            owner.EnterSinkCallback();
            try
            {
                await inner.AcceptAsync(@event, cancellationToken);
                owner._events.Enqueue(@event);
                owner._eventAvailable.Release();
            }
            finally
            {
                owner.ExitSinkCallback();
            }
        }
    }
}
