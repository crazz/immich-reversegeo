using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.CrossProcessRunExclusion;

/// <summary>
/// Preserves the real child-process adapter while teeing controller input to one
/// case-owned file. The session remains the sole disposal owner.
/// </summary>
internal sealed class CapturedChildProcess : IChildProcess
{
    private readonly IChildProcess _inner;
    private readonly object _disposeGate = new();
    private readonly CapturedInputStream _standardInput;
    private Task? _disposeTask;

    internal CapturedChildProcess(IChildProcess inner, string capturePath)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(capturePath);
        _inner = inner;
        _standardInput = new CapturedInputStream(inner.StandardInput, capturePath);
    }

    internal int DisposeCalls { get; private set; }

    public int ProcessId => _inner.ProcessId;

    public Stream StandardInput => _standardInput;

    public Stream StandardOutput => _inner.StandardOutput;

    public Stream StandardError => _inner.StandardError;

    public Task<int> WaitForExitAsync() => _inner.WaitForExitAsync();

    public ChildProcessExitState GetExitState() => _inner.GetExitState();

    public ChildProcessKillOutcome KillProcessTree() => _inner.KillProcessTree();

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
        DisposeCalls++;
        var failures = new List<Exception>();

        try
        {
            await _standardInput.DisposeAsync();
        }
        catch (Exception error)
        {
            failures.Add(error);
        }

        try
        {
            await _inner.DisposeAsync();
        }
        catch (Exception error)
        {
            failures.Add(error);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Captured child-process disposal failed.", failures);
        }
    }

    private sealed class CapturedInputStream : Stream
    {
        private readonly Stream _destination;
        private readonly FileStream _capture;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        internal CapturedInputStream(Stream destination, string capturePath)
        {
            _destination = destination;
            _capture = new FileStream(
                capturePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !_disposed;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            _gate.Wait();
            try
            {
                _capture.Flush(flushToDisk: true);
                _destination.Flush();
            }
            finally
            {
                _gate.Release();
            }
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await _capture.FlushAsync(cancellationToken);
                await _destination.FlushAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _gate.Wait();
            try
            {
                _capture.Write(buffer, offset, count);
                _destination.Write(buffer, offset, count);
            }
            finally
            {
                _gate.Release();
            }
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await _capture.WriteAsync(buffer, cancellationToken);
                await _destination.WriteAsync(buffer, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _gate.Wait();
                try
                {
                    if (!_disposed)
                    {
                        _disposed = true;
                        var failures = new List<Exception>();
                        try
                        {
                            _destination.Dispose();
                        }
                        catch (Exception error)
                        {
                            failures.Add(error);
                        }

                        try
                        {
                            _capture.Dispose();
                        }
                        catch (Exception error)
                        {
                            failures.Add(error);
                        }

                        if (failures.Count > 0)
                        {
                            throw new AggregateException("Captured controller-input disposal failed.", failures);
                        }
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                await _gate.WaitAsync();
                try
                {
                    if (!_disposed)
                    {
                        _disposed = true;
                        var failures = new List<Exception>();
                        try
                        {
                            await _destination.DisposeAsync();
                        }
                        catch (Exception error)
                        {
                            failures.Add(error);
                        }

                        try
                        {
                            await _capture.DisposeAsync();
                        }
                        catch (Exception error)
                        {
                            failures.Add(error);
                        }

                        if (failures.Count > 0)
                        {
                            throw new AggregateException("Captured controller-input disposal failed.", failures);
                        }
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }

            GC.SuppressFinalize(this);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
