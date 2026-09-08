using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.WorkerJobs;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ImmichReverseGeo.Tests.InternalWorkerHost;

public sealed partial class WorkerStdinRealSourceHostTests
{
    [TestMethod]
    [TestCategory("Change47")]
    public async Task V2RealSource_LongDiagnosticsAreBoundedWithoutAbortingHandler()
    {
        var request = new ProcessingRunRequest(
            Guid.Parse(RunIdText),
            ProcessingRunTrigger.RunOnce);
        DiagnosticBoundaryText nearLimit = DiagnosticBoundaryTextFactory.CreateLargestV1Log(
            request,
            7,
            HostTime);
        string activity = new('a', 257);
        string diagnostic = new('d', 257);
        var inputFactory = new HostInputFactory(new HostInputStream(
            VersionedExecute(request, InternalWorkerProtocolVersion.V2)
                .Concat("\n"u8.ToArray())
                .ToArray()));
        var outputFactory = new ReviewOutputFactory();
        var initializer = new CountingInitializer();
        var executor = new ReviewDiagnosticExecutor(activity, diagnostic, nearLimit.Value);
        var fixtureRoot = CreateFixtureRoot();
        var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();

        try
        {
            var builder = ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost.CreateBuilder(
                CreateContext(fixtureRoot),
                outcomes,
                InternalWorkerProtocolVersion.V2);
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, initializer);
            builder.Services.RemoveAll<IWorkerStandardInputStreamFactory>();
            builder.Services.AddSingleton<IWorkerStandardInputStreamFactory>(inputFactory);
            ReplaceSingleton<TimeProvider>(builder.Services, new FixedTimeProvider(HostTime));
            builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(outputFactory);
            ReplaceSingleton<IProcessingRunExecutor>(builder.Services, executor);

            int exitCode = await ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost.RunHostAsync(
                builder.Build(),
                outcomes).WaitAsync(Bound);

            Assert.AreEqual(0, exitCode, "v2-diagnostic-handler-completed");
            Assert.AreEqual(1, initializer.CallCount, "v2-diagnostic-initialized-once");
            Assert.AreEqual(1, executor.CallCount, "v2-diagnostic-executed-once");
            byte[][] frames = SplitFrames(outputFactory.Bytes);
            Assert.IsTrue(
                frames.All(frame => frame.Length <= WorkerJobProtocolV2.MaxMessageBytes),
                "v2-diagnostic-all-frames-bounded");
            WorkerJobOutputMessage[] messages = frames.Select(ParseV2).ToArray();
            WorkerJobActivityStartedPayload activityPayload = messages
                .Select(message => message.Payload)
                .OfType<WorkerJobActivityStartedPayload>()
                .Single();
            Assert.AreEqual(activity, activityPayload.Label, "v2-diagnostic-257-activity-exact");
            WorkerJobLogPayload[] logs = messages
                .Select(message => message.Payload)
                .OfType<WorkerJobLogPayload>()
                .ToArray();
            Assert.AreEqual(2, logs.Length, "v2-diagnostic-two-logs");
            Assert.AreEqual(diagnostic, logs[0].Message, "v2-diagnostic-257-log-exact");
            Assert.IsLessThan(nearLimit.Value.Length, logs[1].Message.Length, "v2-diagnostic-expanded-envelope-normalized");
            Assert.EndsWith("…", logs[1].Message, "v2-diagnostic-normalization-marker");
            Assert.StartsWith(logs[1].Message[..^1], nearLimit.Value, "v2-diagnostic-normalization-prefix");
            WorkerJobTerminalPayload terminal = messages
                .Select(message => message.Payload)
                .OfType<WorkerJobTerminalPayload>()
                .Single();
            Assert.AreEqual(WorkerJobTerminalOutcome.Completed, terminal.Outcome, "v2-diagnostic-completed-terminal");
            Assert.IsNotNull(terminal.ProcessAssetsResult, "v2-diagnostic-typed-result");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change47")]
    public async Task V2RealSource_NonCancellableDescriptorIgnoresCorrelatedControls()
    {
        var request = new ProcessingRunRequest(
            Guid.Parse(RunIdText),
            ProcessingRunTrigger.RunOnce);
        byte[] execute = VersionedExecute(request, InternalWorkerProtocolVersion.V2)
            .Concat("\n"u8.ToArray())
            .ToArray();
        byte[] controls = SerializeCancel(request, 2)
            .Concat("\n"u8.ToArray())
            .Concat(SerializeCancel(request, 3))
            .Concat("\n"u8.ToArray())
            .ToArray();
        var input = new ControlledReviewInputStream(execute, controls);
        var inputFactory = new ControlledReviewInputFactory(input);
        var outputFactory = new ReviewOutputFactory();
        var initializer = new CountingInitializer();
        var executor = new ReviewNonCancellableExecutor(input);
        var descriptor = new WorkerJobDescriptor(
            WorkerJobKind.ProcessAssets,
            typeof(ProcessAssetsRequest),
            typeof(ProcessAssetsResult),
            WorkerJobDescriptors.ProcessAssets.Arbitration with
            {
                IsCancellable = false
            });
        var fixtureRoot = CreateFixtureRoot();
        var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();

        try
        {
            var builder = ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost.CreateBuilder(
                CreateContext(fixtureRoot),
                outcomes,
                InternalWorkerProtocolVersion.V2);
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, initializer);
            builder.Services.RemoveAll<IWorkerStandardInputStreamFactory>();
            builder.Services.AddSingleton<IWorkerStandardInputStreamFactory>(inputFactory);
            ReplaceSingleton<TimeProvider>(builder.Services, new FixedTimeProvider(HostTime));
            builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(outputFactory);
            ReplaceSingleton<IProcessingRunExecutor>(builder.Services, executor);
            builder.Services.RemoveAll<IWorkerJobHandlerRegistration>();
            builder.Services.AddSingleton<IWorkerJobHandlerRegistration>(
                new WorkerJobHandlerRegistration<ProcessAssetsRequest, ProcessAssetsResult>(
                    descriptor,
                    sp => sp.GetRequiredService<ProcessAssetsWorkerJobHandler>()));
            builder.Services.RemoveAll<WorkerJobHandlerRegistry>();
            builder.Services.AddSingleton(sp => new WorkerJobHandlerRegistry(
                sp.GetServices<IWorkerJobHandlerRegistration>()));
            using var host = builder.Build();

            Task<int> run = ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost.RunHostAsync(
                host,
                outcomes);
            try
            {
                await executor.Started.Task.WaitAsync(Bound);
                input.ReleaseControls();
                int exitCode = await run.WaitAsync(Bound);

                Assert.AreEqual(0, exitCode, "v2-noncancellable-normal-exit");
                Assert.AreEqual(1, initializer.CallCount, "v2-noncancellable-initialized-once");
                Assert.AreEqual(1, executor.CallCount, "v2-noncancellable-executed-once");
                Assert.IsFalse(executor.TokenWasCancelled, "v2-noncancellable-handler-token-remains-live");
                Assert.IsTrue(input.EndObserved.Task.IsCompleted, "v2-noncancellable-both-controls-consumed");
                WorkerJobOutputMessage[] messages = SplitFrames(outputFactory.Bytes)
                    .Select(ParseV2)
                    .ToArray();
                CollectionAssert.AreEqual(
                    Enumerable.Range(1, messages.Length).Select(value => (long)value).ToArray(),
                    messages.Select(message => message.Sequence).ToArray(),
                    "v2-noncancellable-output-sequence-contiguous-with-no-ack");
                Assert.AreEqual(0, messages.Count(message => message.Type.Contains("ack", StringComparison.OrdinalIgnoreCase)), "v2-noncancellable-no-control-ack");
                WorkerJobTerminalPayload terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(messages[^1].Payload);
                Assert.AreEqual(WorkerJobTerminalOutcome.Completed, terminal.Outcome, "v2-noncancellable-normal-terminal");
            }
            finally
            {
                input.ReleaseControls();
                try
                {
                    await run.WaitAsync(Bound);
                }
                catch
                {
                    // Cleanup must not replace the test's first failure.
                }
            }
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    private static byte[] SerializeCancel(ProcessingRunRequest request, long sequence) =>
        WorkerJobProtocolCodec.SerializeControllerInput(new WorkerJobControllerMessage(
            WorkerJobProtocolV2.ControlCategory,
            WorkerJobProtocolV2.CancelType,
            sequence,
            HostTime,
            request.RunId,
            WorkerJobKind.ProcessAssets,
            new WorkerJobCancelPayload()));

    private static WorkerJobOutputMessage ParseV2(byte[] frame)
    {
        WorkerJobProtocolParseResult parsed = WorkerJobProtocolCodec.Parse(frame);
        Assert.IsTrue(parsed.IsSuccess, "review-v2-output-valid");
        return parsed.Message!;
    }

    private static byte[][] SplitFrames(byte[] bytes) => Encoding.UTF8.GetString(bytes)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(Encoding.UTF8.GetBytes)
        .ToArray();

    private sealed class ReviewDiagnosticExecutor(
        string activity,
        string diagnostic,
        string nearLimitDiagnostic) : IProcessingRunExecutor
    {
        internal int CallCount { get; private set; }

        public async Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            CallCount++;
            IProcessingRunEventSession session = await reporter.OpenRunAsync(
                request,
                HostTime,
                cancellationToken);
            await session.DetermineEligibilityAsync(0, cancellationToken);
            await using (IAsyncDisposable scope = await session.BeginActivityAsync(activity, cancellationToken))
            {
                await session.ReportLogAsync(
                    ProcessingLogLevel.Information,
                    diagnostic,
                    cancellationToken);
            }

            await session.ReportLogAsync(
                ProcessingLogLevel.Information,
                nearLimitDiagnostic,
                cancellationToken);
            var result = new ProcessingRunResult(
                request,
                HostTime,
                HostTime,
                0,
                0,
                0,
                0,
                ProcessingRunOutcome.Completed,
                null);
            await session.FinishAsync(result);
            return result;
        }
    }

    private sealed class ReviewNonCancellableExecutor(ControlledReviewInputStream input) : IProcessingRunExecutor
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int CallCount { get; private set; }

        internal bool TokenWasCancelled { get; private set; }

        public async Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Started.TrySetResult();
            await input.EndObserved.Task.WaitAsync(Bound);
            TokenWasCancelled = cancellationToken.IsCancellationRequested;
            IProcessingRunEventSession session = await reporter.OpenRunAsync(
                request,
                HostTime,
                CancellationToken.None);
            await session.DetermineEligibilityAsync(0, CancellationToken.None);
            var result = new ProcessingRunResult(
                request,
                HostTime,
                HostTime,
                0,
                0,
                0,
                0,
                ProcessingRunOutcome.Completed,
                null);
            await session.FinishAsync(result);
            return result;
        }
    }

    private sealed class ControlledReviewInputStream(byte[] execute, byte[] controls) : Stream
    {
        private readonly byte[][] _chunks = [execute, controls];
        private readonly TaskCompletionSource _releaseControls = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _chunkIndex;
        private int _offset;

        internal TaskCompletionSource EndObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseControls() => _releaseControls.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_chunkIndex == 1 && _offset == 0)
            {
                await _releaseControls.Task.WaitAsync(cancellationToken);
            }

            if (_chunkIndex == _chunks.Length)
            {
                EndObserved.TrySetResult();
                return 0;
            }

            byte[] chunk = _chunks[_chunkIndex];
            int count = Math.Min(buffer.Length, chunk.Length - _offset);
            chunk.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            if (_offset == chunk.Length)
            {
                _chunkIndex++;
                _offset = 0;
            }

            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => execute.Length + controls.Length;
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ReviewOutputFactory : IWorkerNdjsonOutputStreamFactory
    {
        private readonly MemoryStream _output = new();

        internal byte[] Bytes => _output.ToArray();

        public Stream OpenStandardOutput() => _output;
    }

    private sealed class ControlledReviewInputFactory(ControlledReviewInputStream input) : IWorkerStandardInputStreamFactory
    {
        public Stream OpenStandardInput() => input;
    }
}
