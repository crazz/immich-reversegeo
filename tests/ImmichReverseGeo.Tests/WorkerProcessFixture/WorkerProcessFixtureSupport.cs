using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.WorkerProcessFixture;

internal enum FixtureCleanupPhase
{
    Drain
}

internal sealed class WorkerProcessFixtureLease : IAsyncDisposable
{
    internal static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<Guid, WorkerProcessFixtureLease> Registry = new();
    private readonly object _disposeGate = new();
    private readonly Guid _registration = Guid.NewGuid();
    private readonly List<Task> _directDrains = [];
    private FixtureCleanupPhase? _injectedFailure;
    private Process? _borrowedProcess;
    private IChildProcess? _process;
    private Task<int>? _exitTask;
    private Task? _activeCleanupTask;
    private Exception? _adapterDisposeFailure;
    private bool _inputClosed;
    private bool _exitObserved;
    private bool _drainsFinalized;
    private bool _sessionDisposeAttempted;
    private bool _adapterDisposeAttempted;
    private bool _adapterDisposed;
    private bool _recordingDisposed;
    private bool _rootDeleted;
    private bool _resourcesReleased;

    private int _treeKillCalls;
    private readonly TaskCompletionSource<ChildProcessKillOutcome> _treeKillObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int TreeKillCalls => Volatile.Read(ref _treeKillCalls);
    internal Task<ChildProcessKillOutcome> TreeKillObserved => _treeKillObserved.Task;
    internal ChildWorkerLauncherOptions LauncherOptions { get; init; } = ChildWorkerLauncherOptions.Default;

    internal WorkerProcessFixtureLease(FixtureCleanupPhase? injectedFailure = null)
    {
        _injectedFailure = injectedFailure;
        Root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-worker-fixture", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(Root);
        Registry.TryAdd(_registration, this);
    }

    internal string Root { get; }
    internal string CapturePath => Path.Combine(Root, "request.ndjson");
    internal ProcessingRunRequest Request { get; } = new(Guid.NewGuid(), ProcessingRunTrigger.Manual);
    internal FixtureEventSink Sink { get; } = new();
    internal ChildWorkerSession? Session { get; private set; }
    internal int? ProcessId { get; private set; }
    internal bool ForcedCleanup { get; private set; }
    internal int ProcessDisposeCalls { get; private set; }
    internal MemoryStream WrittenInput { get; } = new();
    internal bool IsRegistered => Registry.ContainsKey(_registration);
    internal bool HasExited => _exitTask?.IsCompletedSuccessfully == true;
    internal Stream StandardInput => _process!.StandardInput;

    internal static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "worker-process-fixture");
    internal static string FixtureExecutable => Path.Combine(FixtureDirectory,
        "ImmichReverseGeo.WorkerProcessFixture" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

    internal ChildProcessStartDescriptor Descriptor(IReadOnlyList<string> arguments)
    {
        Assert.IsTrue(Path.IsPathFullyQualified(FixtureExecutable), "Fixture locator must be absolute.");
        Assert.IsTrue(File.Exists(FixtureExecutable), $"Build or publish must stage the fixture at {FixtureExecutable}.");
        return new ChildProcessStartDescriptor(FixtureExecutable, arguments, FixtureDirectory, ChildProcessEnvironmentPolicy.InheritCurrent);
    }

    internal string[] Arguments(string scenario, bool capture = true, params string[] options)
    {
        var arguments = new List<string> { "--scenario", scenario, "--resource-root", Root };
        if (capture)
        {
            arguments.AddRange(["--capture-name", "request.ndjson"]);
        }
        arguments.AddRange(options);
        return arguments.ToArray();
    }

    internal async Task<ChildWorkerSession> LaunchAsync(string scenario, bool capture = true, params string[] options)
    {
        var descriptor = Descriptor(Arguments(scenario, capture, options));
        var launcher = new ChildWorkerLauncher(new RegisteredFactory(this));
        var result = await launcher.LaunchDescriptorAsync(descriptor, Request, Sink,
            LauncherOptions, CancellationToken.None).AsTask().WaitAsync(Watchdog);
        Session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result).Session;
        Assert.AreEqual(ProcessId, Session.ProcessId);
        return Session;
    }

    internal async Task<DirectFixture> StartDirectAsync(IReadOnlyList<string> arguments)
    {
        var process = await new RegisteredFactory(this).StartAsync(Descriptor(arguments), CancellationToken.None);
        Assert.IsNotNull(process);
        var direct = new DirectFixture(process, Sink);
        _directDrains.Add(direct.OutputDrain);
        _directDrains.Add(direct.ErrorDrain);
        return direct;
    }

    internal async Task<ChildWorkerCompletionObservation> CompleteAsync()
    {
        var result = await Session!.Completion.WaitAsync(Watchdog);
        Assert.IsTrue(result.ExitObserved, "Real OS exit must be observed.");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(result.StandardOutputFinality);
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(result.StandardErrorFinality);
        Assert.AreEqual(Request.RunId, result.RunId);
        return result;
    }

    internal void AssertExactCapture()
    {
        CollectionAssert.AreEqual(WrittenInput.ToArray(), File.ReadAllBytes(CapturePath), "Capture must preserve the exact input frame, including LF.");
        Assert.AreEqual(1, Directory.GetFiles(Root).Length, "Atomic publication must leave no candidate behind.");
        var decoded = WorkerProtocolCodec.ParseControllerInput(File.ReadAllBytes(CapturePath));
        Assert.IsTrue(decoded.IsSuccess);
        Assert.AreEqual(Request, Assert.IsInstanceOfType<ExecuteRequestPayload>(decoded.Message!.Payload).Request);
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
        if (_process is not null)
        {
            CloseInput(failures);
            await KillAndConfirmExitAsync(failures);
            await FinalizeDrainsAsync(failures);
            await DisposeSessionAndAdapterAsync(failures);
        }

        ReleaseResources(failures);

        if (failures.Count > 0)
        {
            throw new AggregateException($"Fixture cleanup failed for PID {ProcessLabel}.", failures);
        }
    }

    private void CloseInput(List<Exception> failures)
    {
        if (_inputClosed || _adapterDisposeAttempted)
        {
            return;
        }

        try
        {
            _process!.StandardInput.Dispose();
            _inputClosed = true;
        }
        catch (ObjectDisposedException)
        {
            _inputClosed = true;
        }
        catch (Exception exception)
        {
            failures.Add(CleanupFailure("close-input", exception));
        }
    }

    private async Task KillAndConfirmExitAsync(List<Exception> failures)
    {
        if (_exitObserved)
        {
            return;
        }

        if (_adapterDisposeAttempted)
        {
            if (_exitTask?.IsCompletedSuccessfully == true)
            {
                _exitObserved = true;
                _inputClosed = true;
            }
            else
            {
                failures.Add(CleanupFailure("process-identity", new InvalidOperationException("The process adapter was disposed before OS exit was confirmed.")));
            }

            return;
        }

        if (_exitTask is null || _borrowedProcess is null)
        {
            failures.Add(CleanupFailure("process-identity", new InvalidOperationException("The exact started fixture process was not retained.")));
            return;
        }

        try
        {
            if (!_exitTask.IsCompleted && !_borrowedProcess.HasExited)
            {
                _borrowedProcess.Kill(entireProcessTree: true);
                ForcedCleanup = true;
            }
        }
        catch (InvalidOperationException) when (_borrowedProcess.HasExited)
        {
        }
        catch (Exception exception)
        {
            failures.Add(CleanupFailure("kill", exception));
        }

        try
        {
            await _exitTask.WaitAsync(Watchdog);
            _exitObserved = true;
            _inputClosed = true;
        }
        catch (Exception exception)
        {
            failures.Add(CleanupFailure("wait-for-exit", exception));
        }
    }

    private async Task FinalizeDrainsAsync(List<Exception> failures)
    {
        if (!_exitObserved || _drainsFinalized)
        {
            return;
        }

        Task? drainTask = null;
        try
        {
            ThrowInjectedFailure(FixtureCleanupPhase.Drain, "drain");
            drainTask = Session is not null ? Session.Completion : Task.WhenAll(_directDrains);
            await drainTask.WaitAsync(Watchdog);
            _drainsFinalized = true;
        }
        catch (Exception exception)
        {
            if (drainTask?.IsCompleted == true)
            {
                _drainsFinalized = true;
            }

            if (exception is not TimeoutException || drainTask?.IsCompletedSuccessfully != true)
            {
                failures.Add(CleanupFailure("drain", exception));
            }
        }
    }

    private async Task DisposeSessionAndAdapterAsync(List<Exception> failures)
    {
        if (!_exitObserved || !_drainsFinalized)
        {
            return;
        }

        if (Session is not null && !_sessionDisposeAttempted)
        {
            _sessionDisposeAttempted = true;
            try
            {
                await Session.DisposeAsync().AsTask().WaitAsync(Watchdog);
            }
            catch (Exception exception)
            {
                failures.Add(CleanupFailure("dispose-session", exception));
            }
        }

        if (!_adapterDisposeAttempted)
        {
            try
            {
                await _process!.DisposeAsync().AsTask().WaitAsync(Watchdog);
            }
            catch (Exception exception)
            {
                if (_adapterDisposeFailure is null)
                {
                    failures.Add(CleanupFailure("dispose-adapter", exception));
                }
            }
        }

        if (_adapterDisposeAttempted && !_adapterDisposed && _adapterDisposeFailure is not null
            && !failures.Contains(_adapterDisposeFailure))
        {
            failures.Add(_adapterDisposeFailure);
        }
    }

    private void ReleaseResources(List<Exception> failures)
    {
        if (_process is not null && (!_exitObserved || !_drainsFinalized || !_adapterDisposed))
        {
            return;
        }

        if (!_recordingDisposed)
        {
            try
            {
                WrittenInput.Dispose();
                _recordingDisposed = true;
            }
            catch (Exception exception)
            {
                failures.Add(CleanupFailure("dispose-recording", exception));
            }
        }

        if (_recordingDisposed && !_rootDeleted)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                _rootDeleted = true;
            }
            catch (Exception exception)
            {
                failures.Add(CleanupFailure("delete-resource-root", exception));
            }
        }

        if (_rootDeleted)
        {
            Registry.TryRemove(_registration, out _);
            _resourcesReleased = true;
        }
    }

    internal static async Task ReapRemainingAsync()
    {
        var remaining = Registry.Values.ToArray();
        await ReapAsync(remaining);
    }

    internal static async Task ReapAsync(IEnumerable<WorkerProcessFixtureLease> leases)
    {
        var remaining = leases.Distinct().ToArray();
        var reapTasks = remaining.Select(ReapOneAsync).ToArray();
        Exception?[] failures;
        try
        {
            failures = await Task.WhenAll(reapTasks).WaitAsync(Watchdog);
        }
        catch (TimeoutException exception)
        {
            var unreaped = remaining.Where(lease => lease.IsRegistered).Select(lease => lease.ProcessLabel).ToArray();
            throw new TimeoutException($"Fixture reaper timed out; unreaped PIDs: {string.Join(", ", unreaped)}.", exception);
        }

        var observedFailures = failures.Where(failure => failure is not null).Cast<Exception>().ToList();
        observedFailures.AddRange(remaining
            .Where(lease => lease.IsRegistered)
            .Select(lease => new InvalidOperationException($"Fixture PID {lease.ProcessLabel} remained registered after cleanup.")));
        if (observedFailures.Count > 0)
        {
            throw new AggregateException(observedFailures);
        }
    }

    private static async Task<Exception?> ReapOneAsync(WorkerProcessFixtureLease lease)
    {
        try
        {
            await lease.DisposeAsync().AsTask().WaitAsync(Watchdog);
            return null;
        }
        catch (Exception exception)
        {
            return new InvalidOperationException($"Could not reap fixture PID {lease.ProcessLabel}.", exception);
        }
    }

    private string ProcessLabel => ProcessId?.ToString() ?? "not-started";

    private Exception CleanupFailure(string phase, Exception exception)
    {
        return new InvalidOperationException($"Fixture cleanup phase '{phase}' failed for PID {ProcessLabel}.", exception);
    }

    private void ThrowInjectedFailure(FixtureCleanupPhase phase, string operation)
    {
        if (_injectedFailure == phase)
        {
            _injectedFailure = null;
            throw new InvalidOperationException($"Injected fixture cleanup failure before {operation}.");
        }
    }

    private void MarkAdapterDisposalStarted()
    {
        lock (_disposeGate)
        {
            if (!_adapterDisposeAttempted)
            {
                _adapterDisposeAttempted = true;
                ProcessDisposeCalls++;
            }
        }
    }

    private void MarkAdapterDisposalSucceeded()
    {
        lock (_disposeGate)
        {
            _adapterDisposed = true;
        }
    }

    private void MarkAdapterDisposalFailed(Exception exception)
    {
        lock (_disposeGate)
        {
            _adapterDisposeFailure ??= CleanupFailure("dispose-adapter", exception);
        }
    }

    private sealed class RegisteredFactory(WorkerProcessFixtureLease owner) : IChildProcessFactory
    {
        public async ValueTask<IChildProcess?> StartAsync(ChildProcessStartDescriptor descriptor, CancellationToken cancellationToken)
        {
            var process = await new SystemChildProcessFactory().StartAsync(descriptor, cancellationToken);
            if (process is null)
            {
                return null;
            }

            if (process is not SystemChildProcessFactory.SystemChildProcess systemProcess)
            {
                await process.DisposeAsync();
                Assert.Fail("Fixture cleanup requires the exact SystemChildProcess adapter instance.");
                return null;
            }

            owner.ProcessId = process.ProcessId;
            owner._borrowedProcess = systemProcess.NativeProcess;
            var registered = new RegisteredProcess(process, owner);
            owner._process = registered;
            owner._exitTask = registered.ExitTask;
            return owner._process;
        }
    }

    private sealed class RegisteredProcess : IChildProcess
    {
        private readonly object _disposeGate = new();
        private readonly IChildProcess _inner;
        private readonly WorkerProcessFixtureLease _owner;
        private Task? _disposeTask;

        internal RegisteredProcess(IChildProcess inner, WorkerProcessFixtureLease owner)
        {
            _inner = inner;
            _owner = owner;
            ExitTask = inner.WaitForExitAsync();
            StandardInput = new RecordingInputStream(inner.StandardInput, owner.WrittenInput);
        }

        internal Task<int> ExitTask { get; }
        public int ProcessId => _inner.ProcessId;
        public Stream StandardInput { get; }
        public Stream StandardOutput => _inner.StandardOutput;
        public Stream StandardError => _inner.StandardError;
        public Task<int> WaitForExitAsync() => ExitTask;
        public ChildProcessExitState GetExitState() => _inner.GetExitState();
        public ChildProcessKillOutcome KillProcessTree()
        {
            Interlocked.Increment(ref _owner._treeKillCalls);
            var outcome = _inner.KillProcessTree();
            _owner._treeKillObserved.TrySetResult(outcome);
            return outcome;
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            _owner.MarkAdapterDisposalStarted();
            try
            {
                await _inner.DisposeAsync();
                _owner.MarkAdapterDisposalSucceeded();
            }
            catch (Exception exception)
            {
                _owner.MarkAdapterDisposalFailed(exception);
                throw;
            }
        }
    }

    private sealed class RecordingInputStream(Stream inner, MemoryStream recording) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            recording.Write(buffer, offset, count);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            recording.Write(buffer.Span);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

internal sealed class FixtureEventSink : IWorkerProtocolEventSink
{
    private readonly ConcurrentQueue<WorkerProtocolEvent> _events = new();
    private readonly Channel<WorkerProtocolEvent> _notifications = Channel.CreateUnbounded<WorkerProtocolEvent>();
    internal WorkerProtocolEvent[] Events => _events.ToArray();

    public ValueTask AcceptAsync(WorkerProtocolEvent @event, CancellationToken cancellationToken)
    {
        _events.Enqueue(@event);
        _notifications.Writer.TryWrite(@event);
        return ValueTask.CompletedTask;
    }

    internal async Task<WorkerProtocolEvent> WaitForAsync(Func<WorkerProtocolEvent, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(WorkerProcessFixtureLease.Watchdog);
        while (await _notifications.Reader.WaitToReadAsync(timeout.Token))
        {
            while (_notifications.Reader.TryRead(out var @event))
            {
                if (predicate(@event))
                {
                    return @event;
                }
            }
        }
        throw new InvalidOperationException("Fixture output ended before its expected handshake.");
    }
}

internal sealed class DirectFixture
{
    private readonly IChildProcess _process;
    private readonly FixtureEventSink _sink;
    private readonly MemoryStream _error = new();

    internal DirectFixture(IChildProcess process, FixtureEventSink sink)
    {
        _process = process;
        _sink = sink;
        OutputDrain = ReadOutputAsync();
        ErrorDrain = DrainErrorAsync();
    }

    internal Task OutputDrain { get; }
    internal Task ErrorDrain { get; }
    internal string ErrorText => Encoding.UTF8.GetString(_error.ToArray());
    internal async Task<int> CompleteAsync()
    {
        var exit = await _process.WaitForExitAsync().WaitAsync(WorkerProcessFixtureLease.Watchdog);
        await Task.WhenAll(OutputDrain, ErrorDrain).WaitAsync(WorkerProcessFixtureLease.Watchdog);
        return exit;
    }

    internal async Task SendAsync(ProcessingRunRequest request, bool cancel = false)
    {
        var message = new WorkerProtocolControllerMessage(
            cancel ? WorkerProtocolV1.ControlCategory : WorkerProtocolV1.RequestCategory,
            cancel ? WorkerProtocolV1.CancelType : WorkerProtocolV1.ExecuteType,
            cancel ? 2 : 1, DateTimeOffset.UtcNow, request.RunId,
            cancel ? new CancelControlPayload() : new ExecuteRequestPayload(request));
        var frame = WorkerProtocolCodec.SerializeControllerInput(message).Concat(new byte[] { (byte)'\n' }).ToArray();
        await _process.StandardInput.WriteAsync(frame);
        await _process.StandardInput.FlushAsync();
    }

    private async Task ReadOutputAsync()
    {
        var validator = new WorkerProtocolEventStreamValidator();
        var buffer = new byte[4096];
        using var frame = new MemoryStream();
        while (true)
        {
            var read = await _process.StandardOutput.ReadAsync(buffer);
            if (read == 0)
            {
                Assert.AreEqual(0, frame.Length, "Direct conformance output ended in a partial frame.");
                return;
            }
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    var parsed = WorkerProtocolCodec.Parse(frame.ToArray());
                    Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
                    var accepted = validator.Validate(parsed.Event!);
                    Assert.IsTrue(accepted.IsSuccess, accepted.Failure?.Diagnostic);
                    await _sink.AcceptAsync(accepted.Event!, CancellationToken.None);
                    frame.SetLength(0);
                }
                else
                {
                    Assert.IsLessThanOrEqualTo(WorkerProtocolV1.MaxMessageBytes, frame.Length + 1);
                    frame.WriteByte(buffer[i]);
                }
            }
        }
    }

    private async Task DrainErrorAsync()
    {
        var buffer = new byte[4096];
        while (true)
        {
            var read = await _process.StandardError.ReadAsync(buffer);
            if (read == 0)
            {
                return;
            }
            var remaining = Math.Max(0, 65_536 - (int)_error.Length);
            _error.Write(buffer, 0, Math.Min(read, remaining));
        }
    }
}

[TestClass]
public sealed class WorkerProcessFixtureCleanup
{
    [AssemblyCleanup]
    public static Task ReapRemainingAsync() => WorkerProcessFixtureLease.ReapRemainingAsync();
}
