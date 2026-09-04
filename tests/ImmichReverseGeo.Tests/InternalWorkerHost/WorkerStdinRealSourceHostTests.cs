using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Tests.InternalWorkerHost;

[TestClass]
[TestCategory("Change23")]
public sealed class WorkerStdinRealSourceHostTests
{
    private const string RunIdText = "84b94b54-b7c5-49cf-a7dd-2b4a006ba1ae";
    private const string Timestamp = "2026-01-02T03:04:05.0000000Z";
    private const string RawStdoutSentinel = "RAW_STDOUT_SENTINEL";
    private static readonly DateTimeOffset HostTime = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task RealSource_PreRequestFailureHandsExactSafeObjectThroughTransitionalFinality()
    {
        const string rawSentinel = "RAW_FRAME_SENTINEL";
        const string payloadSentinel = "PAYLOAD_SENTINEL";
        const string credentialSentinel = "PASSWORD_SENTINEL";
        const string connectionSentinel = "Host=db;Password=CONNECTION_SENTINEL";
        const string sqlSentinel = "SELECT_SQL_SENTINEL";
        const string secretSentinel = "SECRET_SENTINEL";
        var sentinels = new[]
        {
            rawSentinel,
            payloadSentinel,
            credentialSentinel,
            connectionSentinel,
            sqlSentinel,
            secretSentinel
        };
        var hostile = string.Join("|", sentinels);
        var input = new HostInputStream(Encoding.UTF8.GetBytes("{\"payload\":\"" + hostile + "\",}\n"));
        var inputFactory = new HostInputFactory(input);
        var outputFactory = new FixedOutputFactory();
        var executor = new GatedReportingExecutor(Task.CompletedTask);
        var fixtureRoot = CreateFixtureRoot();
        var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
        using var errorWriter = new StringWriter();

        try
        {
            var builder = ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost.CreateBuilder(
                CreateContext(fixtureRoot),
                outcomes);
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer());
            ReplaceSingleton<IWorkerReadinessPublisher>(builder.Services, new CompletedReadiness());
            builder.Services.RemoveAll<IWorkerStandardInputStreamFactory>();
            builder.Services.AddSingleton<IWorkerStandardInputStreamFactory>(inputFactory);
            ReplaceSingleton<TimeProvider>(builder.Services, new FixedTimeProvider(HostTime));
            builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(outputFactory);
            ReplaceSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(builder.Services, executor);
            var transitional = new TransitionalWorkerPreRequestFinality();
            var forwarding = new ForwardingPreRequestFinality(transitional);
            ReplaceSingleton(builder.Services, transitional);
            ReplaceSingleton<IWorkerPreRequestFinality>(builder.Services, forwarding);
            var host = builder.Build();

            await ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost.RunHostAsync(host, outcomes).WaitAsync(Bound);
            var exitCode = InternalWorkerProcessExitBoundary.Complete(outcomes.Fact, errorWriter);

            Assert.AreEqual(2, exitCode, "stdin-host-pre-request:invalid-input-exit");
            Assert.AreEqual(1, inputFactory.OpenCount, "stdin-host-pre-request:one-open");
            Assert.AreEqual(0, executor.CallCount, "stdin-host-pre-request:zero-work");
            Assert.AreEqual(0, outputFactory.OpenCount, "stdin-host-pre-request:zero-stdout-open");
            var recordedOutcome = forwarding.Outcome;
            var transitionalOutcome = transitional.Outcome;
            Assert.IsNotNull(recordedOutcome, "stdin-host-pre-request:recorded-outcome");
            Assert.IsNotNull(transitionalOutcome, "stdin-host-pre-request:transitional-outcome");
            Assert.AreSame(recordedOutcome, transitionalOutcome, "stdin-host-pre-request:exact-outcome-handoff");
            Assert.IsNotNull(recordedOutcome.SafeFailure, "stdin-host-pre-request:safe-failure-present");
            Assert.AreSame(recordedOutcome.SafeFailure, transitionalOutcome.SafeFailure, "stdin-host-pre-request:exact-failure-handoff");
            Assert.AreEqual("worker-input-malformedjson", recordedOutcome.Category, "stdin-host-pre-request:exact-category");
            var stdout = outputFactory.Output.Text;
            var stderr = errorWriter.ToString();
            foreach (var sentinel in sentinels)
            {
                Assert.IsFalse(recordedOutcome.Category.Contains(sentinel, StringComparison.Ordinal), "stdin-host-pre-request:no-sentinel-" + sentinel);
                Assert.IsFalse(recordedOutcome.SafeFailure.Category.Contains(sentinel, StringComparison.Ordinal), "stdin-host-pre-request:no-failure-sentinel-" + sentinel);
                Assert.IsFalse(stdout.Contains(sentinel, StringComparison.Ordinal), "stdin-host-pre-request:no-stdout-sentinel-" + sentinel);
                Assert.IsFalse(stderr.Contains(sentinel, StringComparison.Ordinal), "stdin-host-pre-request:no-stderr-sentinel-" + sentinel);
            }
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [DataRow("clean-eof")]
    [DataRow("partial-eof")]
    [DataRow("frame-failure")]
    [DataRow("validation-failure")]
    [DataRow("reader-failure")]
    public async Task RealSource_AcceptedInputRowsKeepOneExecutorAndOneReporterTerminal(string row)
    {
        var execute = Execute();
        var logger = new HostRecordingLogger<WorkerStdinRequestSource>();
        HostInputStream input;
        Task inputOutcome;
        string? expectedInputCategory;
        switch (row)
        {
            case "clean-eof":
                input = new HostInputStream(execute.Concat("\n"u8.ToArray()).ToArray());
                inputOutcome = input.EndObserved.Task;
                expectedInputCategory = null;
                break;
            case "partial-eof":
                input = new HostInputStream(execute.Concat("\n{"u8.ToArray()).ToArray());
                inputOutcome = logger.Logged.Task;
                expectedInputCategory = "worker-input-invalidframing";
                break;
            case "frame-failure":
                input = new HostInputStream(execute.Concat(Encoding.UTF8.GetBytes("\n{\"raw\":\"" + RawStdoutSentinel + "\",}\n")).ToArray());
                inputOutcome = logger.Logged.Task;
                expectedInputCategory = "worker-input-malformedjson";
                break;
            case "validation-failure":
                input = new HostInputStream(JoinLines(execute, Cancel(4)));
                inputOutcome = logger.Logged.Task;
                expectedInputCategory = "worker-input-invalidsequence";
                break;
            case "reader-failure":
                input = new HostInputStream(execute.Concat("\n"u8.ToArray()).ToArray(), throwOnRead: 1);
                inputOutcome = logger.Logged.Task;
                expectedInputCategory = "worker-input-reader-failure";
                break;
            default:
                throw new AssertFailedException("stdin-host-row-unknown:" + row);
        }

        var fixtureRoot = CreateFixtureRoot();
        var inputFactory = new HostInputFactory(input);
        var outputFactory = new FixedOutputFactory();
        var executor = new GatedReportingExecutor(inputOutcome);
        try
        {
            var builder = ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost.CreateBuilder(
                CreateContext(fixtureRoot),
                new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator());
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer());
            builder.Services.RemoveAll<IWorkerStandardInputStreamFactory>();
            builder.Services.AddSingleton<IWorkerStandardInputStreamFactory>(inputFactory);
            ReplaceSingleton<TimeProvider>(builder.Services, new FixedTimeProvider(HostTime));
            builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(outputFactory);
            builder.Services.RemoveAll<ILogger<WorkerStdinRequestSource>>();
            builder.Services.AddSingleton<ILogger<WorkerStdinRequestSource>>(logger);
            ReplaceSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(builder.Services, executor);
            using var host = builder.Build();

            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(Bound);

            Assert.AreEqual(1, inputFactory.OpenCount, "stdin-host-" + row + ":one-input-open");
            Assert.AreEqual(1, executor.CallCount, "stdin-host-" + row + ":one-executor");
            Assert.IsNotNull(executor.Request, "stdin-host-" + row + ":request-present");
            Assert.AreEqual(Guid.Parse(RunIdText), executor.Request.RunId, "stdin-host-" + row + ":run-id");
            Assert.AreEqual(ProcessingRunTrigger.RunOnce, executor.Request.Trigger, "stdin-host-" + row + ":trigger");
            Assert.AreSame(executor.Request, executor.Result!.Request, "stdin-host-" + row + ":result-request-identity");
            Assert.IsFalse(executor.TokenWasCancelled, "stdin-host-" + row + ":execution-not-cancelled");
            if (expectedInputCategory is not null)
            {
                Assert.AreEqual(1, logger.Entries.Count, "stdin-host-" + row + ":one-safe-input-log");
                Assert.AreEqual(expectedInputCategory, logger.Entries[0], "stdin-host-" + row + ":exact-input-category");
            }

            var stdout = outputFactory.Output.Text;
            var frames = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(4, frames.Length, "stdin-host-" + row + ":ready-started-eligibility-terminal-only");
            Assert.AreEqual(
                "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-01-02T03:04:05.0000000Z\",\"runId\":null,\"payload\":{}}",
                frames[0],
                "stdin-host-" + row + ":ready-exact-frame");
            Assert.IsTrue(frames[0].Contains("\"type\":\"ready\"", StringComparison.Ordinal), "stdin-host-" + row + ":ready-first");
            Assert.IsTrue(frames[1].Contains("\"type\":\"run-started\"", StringComparison.Ordinal), "stdin-host-" + row + ":run-started-second");
            Assert.IsTrue(frames[2].Contains("\"type\":\"eligibility-determined\"", StringComparison.Ordinal), "stdin-host-" + row + ":eligibility-third");
            Assert.IsTrue(frames[3].Contains("\"category\":\"terminal\"", StringComparison.Ordinal), "stdin-host-" + row + ":terminal-fourth");
            Assert.AreEqual(1, frames.Count(frame => frame.Contains("\"category\":\"terminal\"", StringComparison.Ordinal)), "stdin-host-" + row + ":one-terminal");
            Assert.AreEqual(0, frames.Count(frame => frame.Contains("ack", StringComparison.OrdinalIgnoreCase)), "stdin-host-" + row + ":no-ack");
            Assert.AreEqual(0, frames.Count(frame => frame.Contains("worker-input-", StringComparison.Ordinal)), "stdin-host-" + row + ":no-input-diagnostic-stdout");
            Assert.IsFalse(stdout.Contains(RawStdoutSentinel, StringComparison.Ordinal), "stdin-host-" + row + ":real-stdout-excludes-raw-input-sentinel");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    private static byte[] Execute()
    {
        return Encoding.UTF8.GetBytes($"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"{Timestamp}\",\"runId\":\"{RunIdText}\",\"payload\":{{\"trigger\":\"run-once\"}}}}");
    }

    private static byte[] Cancel(long sequence)
    {
        return Encoding.UTF8.GetBytes($"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"control\",\"type\":\"cancel\",\"sequence\":{sequence},\"timestampUtc\":\"{Timestamp}\",\"runId\":\"{RunIdText}\",\"payload\":{{}}}}");
    }

    private static byte[] JoinLines(params byte[][] frames)
    {
        return frames.SelectMany(frame => frame.Concat("\n"u8.ToArray())).ToArray();
    }

    private static ApplicationCompositionContext CreateContext(string root)
    {
        return ApplicationCompositionContext.Create(CompositionEnvironment.Development, root, null, null);
    }

    private static string CreateFixtureRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteFixtureRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ReplaceSingleton<T>(IServiceCollection services, T instance)
        where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(instance);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return value;
        }
    }

    private sealed class CompletedInitializer : IWorkerStartupInitializer
    {
        public Task InitialiseAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class CompletedReadiness : IWorkerReadinessPublisher
    {
        public Task PublishAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ForwardingPreRequestFinality(TransitionalWorkerPreRequestFinality inner) : IWorkerPreRequestFinality
    {
        internal WorkerPreRequestOutcome? Outcome { get; private set; }

        public async Task CompleteAsync(WorkerPreRequestOutcome outcome, CancellationToken cancellationToken)
        {
            Outcome = outcome;
            await inner.CompleteAsync(outcome, cancellationToken);
        }
    }

    private sealed class GatedReportingExecutor(Task inputOutcome) : ImmichReverseGeo.Web.Services.IProcessingRunExecutor
    {
        internal int CallCount { get; private set; }

        internal ProcessingRunRequest? Request { get; private set; }

        internal ProcessingRunResult? Result { get; private set; }

        internal bool TokenWasCancelled { get; private set; }

        public async Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            await inputOutcome.WaitAsync(Bound);
            TokenWasCancelled = cancellationToken.IsCancellationRequested;
            var startedAtUtc = HostTime;
            var session = await reporter.OpenRunAsync(request, startedAtUtc, cancellationToken);
            await session.DetermineEligibilityAsync(0, cancellationToken);
            Result = new ProcessingRunResult(
                request,
                startedAtUtc,
                HostTime,
                0,
                0,
                0,
                0,
                ProcessingRunOutcome.Completed,
                null);
            await session.FinishAsync(Result);
            return Result;
        }
    }

    private sealed class HostInputFactory(HostInputStream input) : IWorkerStandardInputStreamFactory
    {
        internal int OpenCount { get; private set; }

        public Stream OpenStandardInput()
        {
            OpenCount++;
            return input;
        }
    }

    private sealed class HostInputStream(byte[] bytes, int throwOnRead = int.MaxValue) : Stream
    {
        private int _offset;

        internal TaskCompletionSource EndObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ReadCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadCount == throwOnRead)
            {
                EndObserved.TrySetResult();
                throw new IOException("HOST_READER_RAW_SENTINEL");
            }

            ReadCount++;
            if (_offset == bytes.Length)
            {
                EndObserved.TrySetResult();
                return ValueTask.FromResult(0);
            }

            var length = Math.Min(buffer.Length, bytes.Length - _offset);
            bytes.AsSpan(_offset, length).CopyTo(buffer.Span);
            _offset += length;
            return ValueTask.FromResult(length);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FixedOutputFactory : IWorkerNdjsonOutputStreamFactory
    {
        internal FixedOutputStream Output { get; } = new();

        internal int OpenCount { get; private set; }

        public Stream OpenStandardOutput()
        {
            OpenCount++;
            return Output;
        }
    }

    private sealed class FixedOutputStream : Stream
    {
        private readonly byte[] _bytes = new byte[16_384];
        private int _count;

        internal string Text => Encoding.UTF8.GetString(_bytes, 0, _count);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _count;

        public override long Position
        {
            get => _count;
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_count + buffer.Length > _bytes.Length)
            {
                throw new AssertFailedException("stdin-host-output-fixed-capacity");
            }

            buffer.CopyTo(_bytes.AsMemory(_count));
            _count += buffer.Length;
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class HostRecordingLogger<T> : ILogger<T>
    {
        internal TaskCompletionSource Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
            Logged.TrySetResult();
        }
    }
}
