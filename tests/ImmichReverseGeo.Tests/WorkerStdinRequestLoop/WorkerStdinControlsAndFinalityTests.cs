using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Tests.WorkerStdinRequestLoop;

[TestClass]
public sealed class WorkerStdinControlsAndFinalityTests
{
    private const string RunIdText = "84b94b54-b7c5-49cf-a7dd-2b4a006ba1ae";
    private const string OtherRunIdText = "16897bc9-ffca-491c-825e-810f407ac86c";
    private const string Timestamp = "2026-01-02T03:04:05.0000000Z";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task Reader_MultibyteMaxPlusOneUsesIndependentUtf8ByteOracleAndDoesNotDrain()
    {
        const string label = "stdin-multibyte-max-plus-one-no-drain";
        const int expectedLimit = 1_048_576;
        var prefix = "{\"text\":\"";
        var suffix = "\"}";
        var fixedBytes = Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(suffix);
        var remainingBytes = expectedLimit + 1 - fixedBytes;
        var multibyteCount = remainingBytes / 2;
        var oneByteCount = remainingBytes % 2;
        var json = prefix + new string('é', multibyteCount) + new string('x', oneByteCount) + suffix;
        var bytes = Encoding.UTF8.GetBytes(json);

        Assert.AreEqual(expectedLimit + 1, bytes.Length, label + ":independent-byte-count");
        Assert.IsLessThan(expectedLimit, json.Length, label + ":character-count-below-limit");

        var chunks = Enumerable.Repeat(4096, 256).Append(1).ToArray();
        var input = new DeterministicInputStream(
            bytes.Concat("\nSHOULD_NOT_DRAIN"u8.ToArray()).ToArray(),
            chunks,
            throwOnRead: 257);
        await using var reader = new WorkerStdinFrameReader(input);
        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess, label + ":not-success");
        Assert.IsNull(result.Frame, label + ":no-frame");
        Assert.AreEqual(WorkerProtocolFailureCode.MessageTooLarge, result.FailureCode, label + ":exact-category");
        Assert.IsFalse(result.IsEndOfInput, label + ":not-end-of-input");
        Assert.IsFalse(result.IsReaderFailure, label + ":not-reader-failure");
        Assert.AreEqual(expectedLimit + 1, input.Position, label + ":position");
        Assert.AreEqual(257, input.ReadCount, label + ":read-count");
    }

    [TestMethod]
    public async Task Reader_ExactLimitPendingCrAndSecondObjectByteFailImmediatelyWithoutDrain()
    {
        const int expectedLimit = 1_048_576;
        var exact = CreateExactAsciiObject(expectedLimit);
        var rows = new (string Label, byte[] Bytes, int[] Chunks, long ExpectedPosition)[]
        {
            (
                "stdin-exact-limit-pending-cr-non-lf",
                exact.Concat("\rX\nPOISON"u8.ToArray()).ToArray(),
                Enumerable.Repeat(4096, 256).Concat([1, 1]).ToArray(),
                expectedLimit + 2L),
            (
                "stdin-exact-limit-two-object-bytes",
                exact.Concat("XY\nPOISON"u8.ToArray()).ToArray(),
                Enumerable.Repeat(4096, 256).Append(1).ToArray(),
                expectedLimit + 1L)
        };

        foreach (var row in rows)
        {
            var input = new DeterministicInputStream(row.Bytes, row.Chunks, throwOnRead: row.Chunks.Length);
            await using var reader = new WorkerStdinFrameReader(input);
            var result = await reader.ReadAsync(CancellationToken.None);

            Assert.IsFalse(result.IsSuccess, row.Label + ":not-success");
            Assert.IsNull(result.Frame, row.Label + ":no-frame");
            Assert.AreEqual(WorkerProtocolFailureCode.MessageTooLarge, result.FailureCode, row.Label + ":category");
            Assert.IsFalse(result.IsEndOfInput, row.Label + ":not-eof");
            Assert.IsFalse(result.IsReaderFailure, row.Label + ":not-reader-failure");
            Assert.AreEqual(row.ExpectedPosition, input.Position, row.Label + ":position");
            Assert.AreEqual(row.Chunks.Length, input.ReadCount, row.Label + ":read-count");
        }
    }

    [TestMethod]
    public async Task Reader_AcceptsEveryBoundaryInsideTwoThreeAndFourByteScalarsAndCrLf()
    {
        var scalars = new[] { "é", "雪", "😀" };
        foreach (var scalar in scalars)
        {
            var frame = Encoding.UTF8.GetBytes("{\"text\":\"" + scalar + "\"}");
            var scalarBytes = Encoding.UTF8.GetBytes(scalar);
            var scalarStart = FindSubsequence(frame, scalarBytes);
            for (var split = scalarStart + 1; split < scalarStart + scalarBytes.Length; split++)
            {
                var input = new DeterministicInputStream(frame.Concat("\r\n"u8.ToArray()).ToArray(), [split, 1, 4096]);
                await using var reader = new WorkerStdinFrameReader(input);
                var result = await reader.ReadAsync(CancellationToken.None);
                Assert.IsTrue(result.IsSuccess, "stdin-scalar-split-success-" + scalarBytes.Length + "-" + split);
                CollectionAssert.AreEqual(frame, result.Frame, "stdin-scalar-split-bytes-" + scalarBytes.Length + "-" + split);
            }

            var crLfInput = new DeterministicInputStream(frame.Concat("\r\n"u8.ToArray()).ToArray(), [frame.Length + 1, 1]);
            await using var crLfReader = new WorkerStdinFrameReader(crLfInput);
            Assert.IsTrue((await crLfReader.ReadAsync(CancellationToken.None)).IsSuccess, "stdin-crlf-split-" + scalarBytes.Length);
        }
    }

    [TestMethod]
    public async Task Source_SameReadExecuteAndRepeatedCancelsLatchOneExactTokenEffect()
    {
        var input = new SegmentedGateInputStream(
        [
            Execute().Concat("\n"u8.ToArray()).ToArray(),
            JoinLines(Cancel(2), Cancel(3))
        ]);
        await using var source = CreateSource(input);
        var accepted = await AcquireAcceptedAsync(source);
        var callbackCount = 0;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = accepted.Lease.CancellationToken.Register(() =>
        {
            Interlocked.Increment(ref callbackCount);
            cancelled.TrySetResult();
        });

        input.ReleaseNext();
        await cancelled.Task.WaitAsync(Bound);
        input.Complete();
        var finality = await accepted.Lease.SettleAsync(CancellationToken.None);

        Assert.IsTrue(accepted.Lease.CancellationToken.IsCancellationRequested, "stdin-repeated-cancel-token");
        Assert.AreEqual(1, callbackCount, "stdin-repeated-cancel-one-callback-effect");
        Assert.IsTrue(
            finality is WorkerInputPumpFinality.ControlsClosedFinality or WorkerInputPumpFinality.ExpectedShutdownFinality,
            "stdin-repeated-cancel-non-failure-finality");
        Assert.IsFalse(finality is WorkerInputPumpFinality.InputFailureFinality, "stdin-repeated-cancel-no-input-failure");
        Assert.IsFalse(finality is WorkerInputPumpFinality.ReaderFailureFinality, "stdin-repeated-cancel-no-reader-failure");
        Assert.AreSame(accepted.Lease.Request, ((IProcessingRunLease)accepted.Lease).Request, "stdin-repeated-cancel-request-identity");
    }

    [TestMethod]
    public async Task Source_CancelBeforeExecutionEntryPresentsAlreadyCancelledExactLeaseToken()
    {
        var input = new SegmentedGateInputStream([Execute().Concat("\n"u8.ToArray()).ToArray(), Cancel(2).Concat("\n"u8.ToArray()).ToArray()]);
        await using var source = CreateSource(input);
        var accepted = await AcquireAcceptedAsync(source);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = accepted.Lease.CancellationToken.Register(() => cancelled.TrySetResult());
        input.ReleaseNext();
        await cancelled.Task.WaitAsync(Bound);

        Assert.IsTrue(accepted.Lease.CancellationToken.IsCancellationRequested, "stdin-before-entry-exact-token-cancelled");
        accepted.Lease.NotifyExecutionStarting();
        input.Complete();
        await input.EndObserved.Task.WaitAsync(Bound);
        await accepted.Lease.SettleAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Source_CancelDuringExecutionRequestsTheSameToken()
    {
        var input = new SegmentedGateInputStream([Execute().Concat("\n"u8.ToArray()).ToArray(), Cancel(2).Concat("\n"u8.ToArray()).ToArray()]);
        await using var source = CreateSource(input);
        var accepted = await AcquireAcceptedAsync(source);
        var token = accepted.Lease.CancellationToken;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => cancelled.TrySetResult());
        accepted.Lease.NotifyExecutionStarting();
        input.ReleaseNext();
        await cancelled.Task.WaitAsync(Bound);

        Assert.AreEqual(token, accepted.Lease.CancellationToken, "stdin-during-execution-same-token");
        Assert.IsTrue(token.IsCancellationRequested, "stdin-during-execution-cancelled");
        input.Complete();
        await input.EndObserved.Task.WaitAsync(Bound);
        await accepted.Lease.SettleAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Source_RejectedPostAcceptRowsPreserveRequestAndNeverCancel()
    {
        var validCancel = Cancel(2);
        var rows = new (string Label, byte[] Frame, string ExpectedCategory)[]
        {
            ("malformed-json", "{"u8.ToArray(), "worker-input-malformedjson"),
            ("unknown-protocol", Replace(validCancel, "immich-reversegeo.worker", "unknown.worker"), "worker-input-unsupportedprotocol"),
            ("unsupported-version", Replace(validCancel, "\"version\":1", "\"version\":2"), "worker-input-unsupportedversion"),
            ("wrong-direction", Replace(validCancel, "controller-to-worker", "worker-to-controller"), "worker-input-unsupportedtype"),
            ("unknown-category", Replace(validCancel, "\"category\":\"control\"", "\"category\":\"mystery\""), "worker-input-unsupportedtype"),
            ("unknown-type", Replace(validCancel, "\"type\":\"cancel\"", "\"type\":\"mystery\""), "worker-input-unsupportedtype"),
            ("invalid-payload-shape", Replace(validCancel, "\"payload\":{}", "\"payload\":[]"), "worker-input-invalidenvelope"),
            ("incompatible-category-type", Replace(validCancel, "\"category\":\"control\"", "\"category\":\"request\""), "worker-input-unsupportedtype"),
            ("sequence-gap", Cancel(3), "worker-input-invalidsequence"),
            ("sequence-replay", Cancel(1), "worker-input-invalidsequence"),
            ("wrong-correlation", Cancel(2, OtherRunIdText), "worker-input-invalidcorrelation"),
            ("duplicate-execute", Replace(Execute(), "\"sequence\":1", "\"sequence\":2"), "worker-input-invalidlifecycle")
        };

        foreach (var row in rows)
        {
            var execute = Execute();
            var consumed = execute.Concat("\n"u8.ToArray()).Concat(row.Frame).Concat("\n"u8.ToArray()).ToArray();
            var input = new DeterministicInputStream(
                consumed.Concat("POISON"u8.ToArray()).ToArray(),
                [execute.Length + 1, row.Frame.Length + 1],
                throwOnRead: 2);
            var logger = new SignalLogger<WorkerStdinRequestSource>();
            await using var source = new WorkerStdinRequestSource(new SingleInputFactory(input), logger);
            var accepted = await AcquireAcceptedAsync(source);
            var request = accepted.Lease.Request;
            await logger.Logged.Task.WaitAsync(Bound);
            var finality = await accepted.Lease.SettleAsync(CancellationToken.None);

            Assert.IsFalse(accepted.Lease.CancellationToken.IsCancellationRequested, row.Label + ":no-cancel");
            Assert.AreSame(request, accepted.Lease.Request, row.Label + ":request-stable");
            Assert.IsInstanceOfType<WorkerInputPumpFinality.InputFailureFinality>(finality, row.Label + ":input-failure-row");
            var inputFailure = (WorkerInputPumpFinality.InputFailureFinality)finality;
            Assert.IsNotNull(inputFailure.Failure, row.Label + ":failure-present");
            Assert.AreEqual(row.ExpectedCategory, inputFailure.Failure.Category, row.Label + ":exact-category");
            Assert.AreEqual(2, input.ReadCount, row.Label + ":fatal-read-count");
            Assert.AreEqual(consumed.Length, input.Position, row.Label + ":poison-unread-position");
        }
    }

    [TestMethod]
    public async Task Source_PreRequestRejectionRowsReturnExactSafeFailuresWithoutResync()
    {
        var validExecute = Execute();
        var rows = new (string Label, byte[] Frame, string ExpectedCategory)[]
        {
            ("pre-malformed-json", "{"u8.ToArray(), "worker-input-malformedjson"),
            ("pre-unknown-protocol", Replace(validExecute, "immich-reversegeo.worker", "unknown.worker"), "worker-input-unsupportedprotocol"),
            ("pre-unsupported-version", Replace(validExecute, "\"version\":1", "\"version\":2"), "worker-input-unsupportedversion"),
            ("pre-wrong-direction", Replace(validExecute, "controller-to-worker", "worker-to-controller"), "worker-input-unsupportedtype"),
            ("pre-unknown-category", Replace(validExecute, "\"category\":\"request\"", "\"category\":\"mystery\""), "worker-input-unsupportedtype"),
            ("pre-unknown-type", Replace(validExecute, "\"type\":\"execute\"", "\"type\":\"mystery\""), "worker-input-unsupportedtype"),
            ("pre-invalid-payload", Replace(validExecute, "\"trigger\":\"run-once\"", "\"trigger\":\"unknown\""), "worker-input-invalidpayload"),
            ("pre-incompatible-category-type", Replace(validExecute, "\"category\":\"request\"", "\"category\":\"control\""), "worker-input-unsupportedtype"),
            ("pre-cancel-first-sequence-one", Cancel(1), "worker-input-invalidlifecycle"),
            ("pre-sequence-gap", Replace(validExecute, "\"sequence\":1", "\"sequence\":2"), "worker-input-invalidlifecycle")
        };

        foreach (var row in rows)
        {
            var consumed = row.Frame.Concat("\n"u8.ToArray()).ToArray();
            var input = new DeterministicInputStream(
                consumed.Concat("POISON"u8.ToArray()).ToArray(),
                [consumed.Length],
                throwOnRead: 1);
            var factory = new CountingInputFactory(input);
            var logger = new SignalLogger<WorkerStdinRequestSource>();
            await using var source = new WorkerStdinRequestSource(factory, logger);
            var acquisition = await source.AcquireAsync(CancellationToken.None).WaitAsync(Bound);

            Assert.IsFalse(acquisition is InitialProcessingRunAcquisition.Accepted, row.Label + ":no-lease");
            Assert.IsFalse(acquisition is InitialProcessingRunAcquisition.PreRequestEof, row.Label + ":not-eof");
            Assert.IsInstanceOfType<InitialProcessingRunAcquisition.PreRequestFailure>(acquisition, row.Label + ":failure-union");
            var failure = ((InitialProcessingRunAcquisition.PreRequestFailure)acquisition).Failure;
            Assert.IsNotNull(failure, row.Label + ":failure-present");
            Assert.AreEqual(row.ExpectedCategory, failure.Category, row.Label + ":exact-category");
            Assert.AreEqual(1, factory.OpenCount, row.Label + ":one-open");
            Assert.AreEqual(1, input.ReadCount, row.Label + ":one-fatal-read");
            Assert.AreEqual(consumed.Length, input.Position, row.Label + ":poison-unread-position");
        }
    }

    [TestMethod]
    public async Task Source_PreRequestEofReadFaultAndOpenFaultUseExactAcquisitionUnion()
    {
        var rows = new (string Label, DeterministicInputStream Input, Type ExpectedType, string? ExpectedCategory, int ExpectedReads)[]
        {
            ("pre-clean-eof", new DeterministicInputStream([], [1]), typeof(InitialProcessingRunAcquisition.PreRequestEof), null, 1),
            ("pre-partial-eof", new DeterministicInputStream("{"u8.ToArray(), [1]), typeof(InitialProcessingRunAcquisition.PreRequestFailure), "worker-input-invalidframing", 2),
            ("pre-read-fault-after-partial", new DeterministicInputStream("{"u8.ToArray(), [1], throwOnRead: 1, exceptionMessage: "RAW_READ_SENTINEL"), typeof(InitialProcessingRunAcquisition.PreRequestFailure), "worker-input-reader-failure", 1)
        };

        foreach (var row in rows)
        {
            var factory = new CountingInputFactory(row.Input);
            await using var source = new WorkerStdinRequestSource(
                factory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkerStdinRequestSource>.Instance);
            var acquisition = await source.AcquireAsync(CancellationToken.None).WaitAsync(Bound);

            Assert.AreEqual(row.ExpectedType, acquisition.GetType(), row.Label + ":exact-union");
            Assert.IsFalse(acquisition is InitialProcessingRunAcquisition.Accepted, row.Label + ":no-lease");
            if (row.ExpectedCategory is not null)
            {
                var failure = (InitialProcessingRunAcquisition.PreRequestFailure)acquisition;
                Assert.AreEqual(row.ExpectedCategory, failure.Failure.Category, row.Label + ":exact-category");
            }

            Assert.AreEqual(1, factory.OpenCount, row.Label + ":one-open");
            Assert.AreEqual(row.ExpectedReads, row.Input.ReadCount, row.Label + ":read-count");
        }

        var throwingFactory = new ThrowingInputFactory("OPEN_RAW_SENTINEL");
        await using var openFailureSource = new WorkerStdinRequestSource(
            throwingFactory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkerStdinRequestSource>.Instance);
        var openFailure = await openFailureSource.AcquireAsync(CancellationToken.None).WaitAsync(Bound);
        Assert.IsInstanceOfType<InitialProcessingRunAcquisition.PreRequestFailure>(openFailure, "pre-open-fault:failure-union");
        Assert.AreEqual(
            "worker-input-reader-failure",
            ((InitialProcessingRunAcquisition.PreRequestFailure)openFailure).Failure.Category,
            "pre-open-fault:exact-category");
        Assert.AreEqual(1, throwingFactory.OpenCount, "pre-open-fault:one-open");
        Assert.AreEqual(0, throwingFactory.ReadCount, "pre-open-fault:zero-read");
    }

    [TestMethod]
    public async Task Source_PostAcceptEofPartialCodecValidatorAndReadFaultProduceTypedRowsWithoutCancel()
    {
        var rows = new (string Label, Stream Input, Type FinalityType, string? ExpectedCategory)[]
        {
            ("clean-eof", new DeterministicInputStream(JoinLines(Execute()), [4096]), typeof(WorkerInputPumpFinality.ControlsClosedFinality), null),
            ("partial-eof", new DeterministicInputStream(Execute().Concat("\n{"u8.ToArray()).ToArray(), [4096]), typeof(WorkerInputPumpFinality.InputFailureFinality), "worker-input-invalidframing"),
            ("codec", new DeterministicInputStream(Execute().Concat("\n{]\n"u8.ToArray()).ToArray(), [4096]), typeof(WorkerInputPumpFinality.InputFailureFinality), "worker-input-malformedjson"),
            ("validator", new DeterministicInputStream(JoinLines(Execute(), Cancel(4)), [4096]), typeof(WorkerInputPumpFinality.InputFailureFinality), "worker-input-invalidsequence"),
            ("read", new DeterministicInputStream(Execute().Concat("\n"u8.ToArray()).ToArray(), [4096], throwOnRead: 1), typeof(WorkerInputPumpFinality.ReaderFailureFinality), null)
        };

        foreach (var row in rows)
        {
            var logger = new SignalLogger<WorkerStdinRequestSource>();
            await using var source = new WorkerStdinRequestSource(new SingleInputFactory(row.Input), logger);
            var accepted = await AcquireAcceptedAsync(source);
            if (row.Label == "clean-eof")
            {
                await ((ICompletionObservable)row.Input).Completed.Task.WaitAsync(Bound);
            }
            else
            {
                await logger.Logged.Task.WaitAsync(Bound);
            }

            var finality = await accepted.Lease.SettleAsync(CancellationToken.None);
            Assert.AreEqual(row.FinalityType, finality.GetType(), "stdin-post-accept-row-" + row.Label);
            if (row.ExpectedCategory is not null)
            {
                var inputFailure = (WorkerInputPumpFinality.InputFailureFinality)finality;
                Assert.IsNotNull(inputFailure.Failure, "stdin-post-accept-failure-present-" + row.Label);
                Assert.AreEqual(row.ExpectedCategory, inputFailure.Failure.Category, "stdin-post-accept-category-" + row.Label);
            }

            Assert.IsFalse(accepted.Lease.CancellationToken.IsCancellationRequested, "stdin-post-accept-no-cancel-" + row.Label);
        }
    }

    [TestMethod]
    public async Task Race_CancelCommitsBeforeTerminalAndKeepsItsCancellationEffect()
    {
        var input = new SegmentedGateInputStream([Execute().Concat("\n"u8.ToArray()).ToArray(), Cancel(2).Concat("\n"u8.ToArray()).ToArray()]);
        await using var source = CreateSource(input);
        var accepted = await AcquireAcceptedAsync(source);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = accepted.Lease.CancellationToken.Register(() => cancelled.TrySetResult());
        accepted.Lease.NotifyExecutionStarting();
        input.ReleaseNext();
        await cancelled.Task.WaitAsync(Bound);
        Assert.IsTrue(accepted.Lease.CancellationToken.IsCancellationRequested, "stdin-race-cancel-first-effect");

        await new WorkerStdinAcceptedRunFinality(source).CompleteAsync(
            accepted.Lease.Request,
            CreateResult(accepted.Lease.Request),
            CancellationToken.None).WaitAsync(Bound);
        var finality = await accepted.Lease.SettleAsync(CancellationToken.None);
        Assert.IsInstanceOfType<WorkerInputPumpFinality.ExpectedShutdownFinality>(finality, "stdin-race-cancel-first-terminal-shutdown");
    }

    [TestMethod]
    public async Task Race_TerminalCommitsBeforeDeliveredCancelAndCancelIsTerminalNoOp()
    {
        var input = new TerminalFirstRaceInputStream(
            Execute().Concat("\n"u8.ToArray()).ToArray(),
            Cancel(2).Concat("\n"u8.ToArray()).ToArray());
        await using var source = CreateSource(input);
        var accepted = await AcquireAcceptedAsync(source);
        accepted.Lease.NotifyExecutionStarting();
        await input.CancelReadStarted.Task.WaitAsync(Bound);

        var terminal = new WorkerStdinAcceptedRunFinality(source).CompleteAsync(
            accepted.Lease.Request,
            CreateResult(accepted.Lease.Request),
            CancellationToken.None);
        await input.Disposed.Task.WaitAsync(Bound);
        Assert.IsFalse(terminal.IsCompleted, "stdin-race-terminal-awaits-in-flight-read");
        input.ReleaseCancel();
        await terminal.WaitAsync(Bound);
        var finality = await accepted.Lease.SettleAsync(CancellationToken.None);

        Assert.IsFalse(accepted.Lease.CancellationToken.IsCancellationRequested, "stdin-race-terminal-first-no-cancel-effect");
        Assert.IsInstanceOfType<WorkerInputPumpFinality.ExpectedShutdownFinality>(finality, "stdin-race-terminal-first-finality");
        Assert.AreEqual(1, input.DisposeCount, "stdin-race-terminal-first-dispose-once");
    }

    [TestMethod]
    public async Task Source_ShutdownWaitsForReaderPublicationAndClosesCancellationInsensitiveStreamOnce()
    {
        var input = new PendingInputStream(ignoreCancellation: true);
        var factory = new GatedOpenInputFactory(input);
        await using var source = new WorkerStdinRequestSource(
            factory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkerStdinRequestSource>.Instance);

        var acquire = Task.Run(() => source.AcquireAsync(CancellationToken.None));
        await factory.OpenStarted.Task.WaitAsync(Bound);
        var shutdown = Task.Run(() => source.DisposeAsync().AsTask());
        Assert.IsFalse(shutdown.IsCompleted, "stdin-publication-shutdown-waits-for-open");
        factory.Release();

        var acquisition = await acquire.WaitAsync(Bound);
        await shutdown.WaitAsync(Bound);
        Assert.IsInstanceOfType<InitialProcessingRunAcquisition.PreRequestFailure>(acquisition, "stdin-publication-safe-pre-request-failure");
        Assert.AreEqual(
            "worker-input-reader-failure",
            ((InitialProcessingRunAcquisition.PreRequestFailure)acquisition).Failure.Category,
            "stdin-publication-safe-category");
        Assert.AreEqual(1, factory.OpenCount, "stdin-publication-one-open");
        Assert.AreEqual(1, input.DisposeCount, "stdin-publication-one-close");
        Assert.AreEqual(1, input.ReadCount, "stdin-publication-one-pump-read-no-restart");
    }

    [TestMethod]
    public async Task Finality_WrongCompleteAndFailRequestIdentityDoNotMutateShutdown()
    {
        foreach (var operation in new[] { "complete", "fail" })
        {
            var input = new PendingInputStream(ignoreCancellation: true);
            input.Queue(Execute().Concat("\n"u8.ToArray()).ToArray());
            await using var source = CreateSource(input);
            var accepted = await AcquireAcceptedAsync(source);
            accepted.Lease.NotifyExecutionStarting();
            await input.PendingRead.Task.WaitAsync(Bound);
            var finalityOwner = new WorkerStdinAcceptedRunFinality(source);
            var wrongRequest = new ProcessingRunRequest(Guid.Parse(OtherRunIdText), ProcessingRunTrigger.RunOnce);

            if (operation == "complete")
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    () => finalityOwner.CompleteAsync(wrongRequest, CreateResult(wrongRequest), CancellationToken.None),
                    "stdin-wrong-complete-request:rejected");
            }
            else
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    () => finalityOwner.FailAsync(wrongRequest, WorkerSafeFailure.AcceptedInfrastructure(), CancellationToken.None),
                    "stdin-wrong-fail-request:rejected");
            }

            Assert.AreEqual(0, input.DisposeCount, "stdin-wrong-" + operation + ":no-shutdown-mutation");
            Assert.IsFalse(accepted.Lease.CancellationToken.IsCancellationRequested, "stdin-wrong-" + operation + ":no-cancel");
            await finalityOwner.FailAsync(
                accepted.Lease.Request,
                WorkerSafeFailure.AcceptedInfrastructure(),
                CancellationToken.None).WaitAsync(Bound);
            Assert.AreEqual(1, input.DisposeCount, "stdin-wrong-" + operation + ":correct-request-one-shutdown");
        }
    }

    [TestMethod]
    public async Task Finality_CompleteVerifiesExactRequestAndResultThenStopsPendingSensitiveRead()
    {
        var input = new PendingInputStream(ignoreCancellation: false);
        await using var source = CreateSource(input);
        input.Queue(Execute().Concat("\n"u8.ToArray()).ToArray());
        var accepted = await AcquireAcceptedAsync(source);
        accepted.Lease.NotifyExecutionStarting();
        await input.PendingRead.Task.WaitAsync(Bound);
        var finalityOwner = new WorkerStdinAcceptedRunFinality(source);
        var result = CreateResult(accepted.Lease.Request);

        await finalityOwner.CompleteAsync(accepted.Lease.Request, result, CancellationToken.None).WaitAsync(Bound);
        var finality = await accepted.Lease.SettleAsync(CancellationToken.None);

        Assert.IsInstanceOfType<WorkerInputPumpFinality.ExpectedShutdownFinality>(finality, "stdin-terminal-sensitive-expected");
        Assert.AreEqual(1, input.DisposeCount, "stdin-terminal-sensitive-disposed-once");
        Assert.IsFalse(accepted.Lease.CancellationToken.IsCancellationRequested, "stdin-terminal-sensitive-no-synthetic-cancel");

        var otherRequest = new ProcessingRunRequest(Guid.Parse(OtherRunIdText), ProcessingRunTrigger.RunOnce);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => finalityOwner.CompleteAsync(accepted.Lease.Request, CreateResult(otherRequest), CancellationToken.None),
            "stdin-terminal-result-identity");
    }

    [TestMethod]
    public async Task Finality_FailStopsCancellationInsensitiveReadAndConcurrentCallersShareShutdown()
    {
        var input = new PendingInputStream(ignoreCancellation: true);
        await using var source = CreateSource(input);
        input.Queue(Execute().Concat("\n"u8.ToArray()).ToArray());
        var accepted = await AcquireAcceptedAsync(source);
        accepted.Lease.NotifyExecutionStarting();
        await input.PendingRead.Task.WaitAsync(Bound);
        var finalityOwner = new WorkerStdinAcceptedRunFinality(source);

        var fail = finalityOwner.FailAsync(accepted.Lease.Request, WorkerSafeFailure.AcceptedInfrastructure(), CancellationToken.None);
        var settle = accepted.Lease.SettleAsync(CancellationToken.None).AsTask();
        var dispose = accepted.Lease.DisposeAsync().AsTask();
        await Task.WhenAll(fail, settle, dispose).WaitAsync(Bound);

        Assert.IsInstanceOfType<WorkerInputPumpFinality.ExpectedShutdownFinality>(settle.Result, "stdin-terminal-insensitive-expected");
        Assert.AreEqual(1, input.DisposeCount, "stdin-terminal-insensitive-disposed-once");
        Assert.AreEqual(2, input.ReadCount, "stdin-terminal-insensitive-no-restart");
        await accepted.Lease.DisposeAsync();
        Assert.AreEqual(1, input.DisposeCount, "stdin-terminal-insensitive-repeated-dispose-no-restart");
    }

    [TestMethod]
    public async Task Finality_FirstPrimaryOutcomeSurvivesTerminalTeardown()
    {
        var rows = new (string Label, DeterministicInputStream Input, Type Expected)[]
        {
            ("controls-closed", new DeterministicInputStream(JoinLines(Execute()), [4096]), typeof(WorkerInputPumpFinality.ControlsClosedFinality)),
            ("input-failure", new DeterministicInputStream(Execute().Concat("\n{"u8.ToArray()).ToArray(), [4096]), typeof(WorkerInputPumpFinality.InputFailureFinality)),
            ("reader-failure", new DeterministicInputStream(Execute().Concat("\n"u8.ToArray()).ToArray(), [4096], throwOnRead: 1), typeof(WorkerInputPumpFinality.ReaderFailureFinality))
        };

        foreach (var row in rows)
        {
            var logger = new SignalLogger<WorkerStdinRequestSource>();
            await using var source = new WorkerStdinRequestSource(new SingleInputFactory(row.Input), logger);
            var accepted = await AcquireAcceptedAsync(source);
            accepted.Lease.NotifyExecutionStarting();
            if (row.Label == "controls-closed")
            {
                await row.Input.Completed.Task.WaitAsync(Bound);
            }
            else
            {
                await logger.Logged.Task.WaitAsync(Bound);
            }

            await new WorkerStdinAcceptedRunFinality(source).FailAsync(
                accepted.Lease.Request,
                WorkerSafeFailure.AcceptedInfrastructure(),
                CancellationToken.None);
            var finality = await accepted.Lease.SettleAsync(CancellationToken.None);
            Assert.AreEqual(row.Expected, finality.GetType(), "stdin-primary-preserved-" + row.Label);
        }
    }

    [TestMethod]
    public async Task Finality_FaultingCloseUnblocksPendingReadAndSurfacesSafeCleanupOnce()
    {
        var input = new FaultingPendingInputStream(Execute().Concat("\n"u8.ToArray()).ToArray());
        var source = new WorkerStdinRequestSource(
            new CountingInputFactory(input),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkerStdinRequestSource>.Instance);
        var accepted = await AcquireAcceptedAsync(source);
        accepted.Lease.NotifyExecutionStarting();
        await input.PendingRead.Task.WaitAsync(Bound);

        var cleanup = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => new WorkerStdinAcceptedRunFinality(source).FailAsync(
                accepted.Lease.Request,
                WorkerSafeFailure.AcceptedInfrastructure(),
                CancellationToken.None),
            "stdin-pending-close-fault:safe-cleanup-failure");
        Assert.AreEqual("worker-cleanup-failed", cleanup.Message, "stdin-pending-close-fault:safe-message");
        Assert.IsNull(cleanup.InnerException, "stdin-pending-close-fault:no-raw-inner");
        Assert.IsFalse(cleanup.ToString().Contains("PENDING_CLOSE_RAW_SENTINEL", StringComparison.Ordinal), "stdin-pending-close-fault:no-raw-sentinel");
        Assert.AreEqual(1, input.DisposeCount, "stdin-pending-close-fault:one-close-attempt");
        Assert.AreEqual(2, input.ReadCount, "stdin-pending-close-fault:pump-observed-once");
        Assert.IsFalse(accepted.Lease.CancellationToken.IsCancellationRequested, "stdin-pending-close-fault:no-synthetic-cancel");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => accepted.Lease.SettleAsync(CancellationToken.None).AsTask(),
            "stdin-pending-close-fault:cached-shutdown");
        Assert.AreEqual(2, input.ReadCount, "stdin-pending-close-fault:no-restart");
    }

    [TestMethod]
    public async Task Finality_CloseFaultKeepsCommittedPrimaryAndSurfacesOnlySafeCleanupFailure()
    {
        var rows = new (string Label, FaultingCloseInputStream Input, string? ExpectedPrimaryCategory)[]
        {
            ("cleanup-after-controls-closed", new FaultingCloseInputStream(JoinLines(Execute()), throwOnRead: int.MaxValue), null),
            ("cleanup-after-input-failure", new FaultingCloseInputStream(Execute().Concat("\n{"u8.ToArray()).ToArray(), throwOnRead: int.MaxValue), "worker-input-invalidframing"),
            ("cleanup-after-reader-failure", new FaultingCloseInputStream(Execute().Concat("\n"u8.ToArray()).ToArray(), throwOnRead: 1), "worker-input-reader-failure")
        };

        foreach (var row in rows)
        {
            var logger = new RecordingLogger<WorkerStdinRequestSource>();
            var source = new WorkerStdinRequestSource(new CountingInputFactory(row.Input), logger);
            var accepted = await AcquireAcceptedAsync(source);
            if (row.ExpectedPrimaryCategory is null)
            {
                await row.Input.Completed.Task.WaitAsync(Bound);
            }
            else
            {
                await logger.Logged.Task.WaitAsync(Bound);
                Assert.AreEqual(row.ExpectedPrimaryCategory, logger.Entries[0].Message, row.Label + ":primary-category-before-cleanup");
            }

            var cleanup = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => accepted.Lease.SettleAsync(CancellationToken.None).AsTask(),
                row.Label + ":safe-cleanup-failure");
            Assert.AreEqual("worker-cleanup-failed", cleanup.Message, row.Label + ":safe-cleanup-message");
            Assert.IsNull(cleanup.InnerException, row.Label + ":no-raw-inner-exception");
            Assert.IsFalse(cleanup.ToString().Contains("CLOSE_RAW_SENTINEL", StringComparison.Ordinal), row.Label + ":no-raw-close-sentinel");
            Assert.AreEqual(1, row.Input.DisposeCount, row.Label + ":close-once");
            var readsAfterFailure = row.Input.ReadCount;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => accepted.Lease.SettleAsync(CancellationToken.None).AsTask(),
                row.Label + ":cached-cleanup-failure");
            Assert.AreEqual(readsAfterFailure, row.Input.ReadCount, row.Label + ":no-pump-restart");
        }
    }

    [TestMethod]
    public async Task Diagnostics_RecordingLoggerContainsOnlyAllowlistedSafeFacts()
    {
        const string raw = "RAW_FRAME_SENTINEL";
        const string payload = "PAYLOAD_SENTINEL";
        const string credential = "PASSWORD_SENTINEL";
        const string connection = "Host=db;Password=CONNECTION_SENTINEL";
        const string sql = "SELECT_SQL_SENTINEL";
        const string secret = "SECRET_SENTINEL";
        var sentinels = new[] { raw, payload, credential, connection, sql, secret };
        var hostileText = string.Join("|", sentinels);
        var malformed = Encoding.UTF8.GetBytes("{\"payload\":\"" + hostileText + "\",}\n");
        var rows = new (string Label, DeterministicInputStream Input, string ExpectedCategory)[]
        {
            ("diagnostic-malformed-frame", new DeterministicInputStream(malformed, [malformed.Length]), "worker-input-malformedjson"),
            ("diagnostic-reader-exception", new DeterministicInputStream([], [1], throwOnRead: 0, exceptionMessage: hostileText), "worker-input-reader-failure")
        };

        foreach (var row in rows)
        {
            var logger = new RecordingLogger<WorkerStdinRequestSource>();
            await using var source = new WorkerStdinRequestSource(new CountingInputFactory(row.Input), logger);
            var acquisition = await source.AcquireAsync(CancellationToken.None).WaitAsync(Bound);
            await logger.Logged.Task.WaitAsync(Bound);
            Assert.IsInstanceOfType<InitialProcessingRunAcquisition.PreRequestFailure>(acquisition, row.Label + ":failure-union");
            var failure = ((InitialProcessingRunAcquisition.PreRequestFailure)acquisition).Failure;
            Assert.AreEqual(row.ExpectedCategory, failure.Category, row.Label + ":handoff-category");
            Assert.AreEqual(1, logger.Entries.Count, row.Label + ":one-log");
            var entry = logger.Entries[0];
            Assert.AreEqual(LogLevel.Warning, entry.Level, row.Label + ":warning-level");
            Assert.AreEqual(0, entry.EventId.Id, row.Label + ":event-id");
            Assert.IsNull(entry.EventId.Name, row.Label + ":event-name");
            if (entry.EventId.Name is not null)
            {
                Assert.IsLessThanOrEqualTo(64, entry.EventId.Name.Length, row.Label + ":bounded-event-name");
            }

            Assert.AreEqual(row.ExpectedCategory, entry.Message, row.Label + ":formatted-category-only");
            Assert.IsLessThanOrEqualTo(64, entry.Message.Length, row.Label + ":bounded-message");
            Assert.IsNull(entry.Exception, row.Label + ":null-exception");
            Assert.IsLessThanOrEqualTo(2, entry.State.Count, row.Label + ":bounded-state-count");
            Assert.IsTrue(entry.State.All(value => value.Length <= 128), row.Label + ":bounded-state-values");
            foreach (var sentinel in sentinels)
            {
                Assert.IsFalse(failure.Category.Contains(sentinel, StringComparison.Ordinal), row.Label + ":handoff-no-" + sentinel);
                Assert.IsFalse(entry.Message.Contains(sentinel, StringComparison.Ordinal), row.Label + ":message-no-" + sentinel);
                Assert.IsFalse((entry.EventId.Name ?? string.Empty).Contains(sentinel, StringComparison.Ordinal), row.Label + ":event-name-no-" + sentinel);
                Assert.IsTrue(entry.State.All(value => !value.Contains(sentinel, StringComparison.Ordinal)), row.Label + ":state-no-" + sentinel);
            }
        }
    }

    [TestMethod]
    public async Task Diagnostics_ThrowingLoggerDoesNotChangeReaderFailureOutcome()
    {
        var input = new DeterministicInputStream([], [1], throwOnRead: 0, exceptionMessage: "THROWING_LOGGER_RAW_SENTINEL");
        await using var source = new WorkerStdinRequestSource(
            new CountingInputFactory(input),
            new ThrowingLogger<WorkerStdinRequestSource>());

        var acquisition = await source.AcquireAsync(CancellationToken.None).WaitAsync(Bound);
        Assert.IsInstanceOfType<InitialProcessingRunAcquisition.PreRequestFailure>(acquisition, "throwing-logger:failure-union");
        Assert.AreEqual(
            "worker-input-reader-failure",
            ((InitialProcessingRunAcquisition.PreRequestFailure)acquisition).Failure.Category,
            "throwing-logger:outcome-invariant");
    }

    [TestMethod]
    public void SourceStructuralGuards_KeepOneBoundedStdinOwnerAndNoOutputOrExitAuthority()
    {
        var root = FindRoot();
        var activeRoots = new[]
        {
            "src/ImmichReverseGeo.Core",
            "src/ImmichReverseGeo.Web/WorkerHost",
            "src/ImmichReverseGeo.Web/Services",
            "src/ImmichReverseGeo.Web/Composition"
        };
        var activeFiles = activeRoots
            .Select(path => Path.Combine(root, path))
            .SelectMany(path => Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var activeSource = string.Join("\n", activeFiles.Select(File.ReadAllText));
        var requestSource = File.ReadAllText(Path.Combine(
            root,
            "src/ImmichReverseGeo.Web/WorkerHost/WorkerStdinRequestLoop/WorkerStdinRequestSource.cs"));
        var frameReader = File.ReadAllText(Path.Combine(
            root,
            "src/ImmichReverseGeo.Web/WorkerHost/WorkerStdinRequestLoop/WorkerStdinFrameReader.cs"));
        var composition = File.ReadAllText(Path.Combine(
            root,
            "src/ImmichReverseGeo.Web/Composition/InternalWorkerServiceCollectionExtensions.cs"));
        var availability = File.ReadAllText(Path.Combine(
            root,
            "src/ImmichReverseGeo.Web/WorkerHost/TransitionalWorkerTransport.cs"));

        Assert.AreEqual(1, Count(activeSource, "Console.OpenStandardInput()"), "stdin-structure-one-standard-input-open");
        Assert.AreEqual(1, Count(requestSource, "Console.OpenStandardInput()"), "stdin-structure-open-owned-by-change22-factory");
        Assert.AreEqual(0, Count(composition, "Console.OpenStandardInput()"), "stdin-structure-composition-does-not-open");
        Assert.IsFalse(availability.Contains("IWorkerStandardInputStreamFactory", StringComparison.Ordinal), "stdin-structure-availability-no-factory-dependency");
        foreach (var forbidden in new[]
        {
            "Console.In", "Console.SetIn", "ReadLine", "ReadLineAsync", "StreamReader", "TextReader"
        })
        {
            Assert.IsFalse(activeSource.Contains(forbidden, StringComparison.Ordinal), "stdin-active-roots-forbidden-" + forbidden);
        }

        foreach (var forbidden in new[]
        {
            "StringBuilder", "MemoryStream", "Console.Out", "OpenStandardOutput", "WorkerNdjsonEmitter",
            "Environment.Exit", "ExitCode", "ProcessStartInfo", "System.Diagnostics.Process", "Change23"
        })
        {
            Assert.IsFalse(requestSource.Contains(forbidden, StringComparison.Ordinal), "stdin-source-forbidden-" + forbidden);
        }

        Assert.AreEqual(1, Count(requestSource, "WorkerProtocolControllerInputValidator _validator = new();"), "stdin-structure-one-validator");
        Assert.AreEqual(1, Count(requestSource, "Task.Run(() => PumpLifetimeAsync"), "stdin-structure-one-pump-start");
        Assert.AreEqual(1, Count(requestSource, "private Task? _pumpTask;"), "stdin-structure-one-cached-pump-task");
        Assert.AreEqual(1, Count(requestSource, "private Task? _shutdownTask;"), "stdin-structure-one-cached-shutdown-task");
        Assert.AreEqual(1, Count(frameReader, "private const int ReadBufferBytes = 4096;"), "stdin-storage-fixed-read-size");
        Assert.AreEqual(1, Count(frameReader, "new byte[ReadBufferBytes]"), "stdin-storage-one-fixed-read-buffer");
        Assert.AreEqual(1, Count(frameReader, "new byte[WorkerProtocolV1.MaxMessageBytes + 1]"), "stdin-storage-exact-frame-buffer");
        Assert.AreEqual(1, Count(frameReader, "new char[2]"), "stdin-storage-fixed-decoder-state");
        foreach (var forbidden in new[] { "List<byte>", "Queue<byte>", "StringBuilder", "MemoryStream" })
        {
            Assert.IsFalse(frameReader.Contains(forbidden, StringComparison.Ordinal), "stdin-storage-no-growing-" + forbidden);
        }

        Assert.IsTrue(frameReader.Contains("Array.Clear(_readBuffer);", StringComparison.Ordinal), "stdin-cleanup-clear-read-buffer");
        Assert.IsTrue(frameReader.Contains("Array.Clear(_frameBuffer);", StringComparison.Ordinal), "stdin-cleanup-clear-frame-buffer");
        Assert.IsTrue(frameReader.Contains("Array.Clear(_decoderCharacters);", StringComparison.Ordinal), "stdin-cleanup-clear-decoder-buffer");
        Assert.IsTrue(frameReader.Contains("_decoder.Reset();", StringComparison.Ordinal), "stdin-cleanup-reset-decoder");
        Assert.IsTrue(requestSource.Contains("pumpCancellation?.Dispose();", StringComparison.Ordinal), "stdin-cleanup-pump-cts-disposed");
        var pumpAwait = requestSource.IndexOf("await pumpTask.ConfigureAwait(false);", StringComparison.Ordinal);
        var requestCancellationDispose = requestSource.IndexOf("_lease?.DisposeCancellationAfterPump();", StringComparison.Ordinal);
        Assert.IsTrue(pumpAwait >= 0 && requestCancellationDispose > pumpAwait, "stdin-cleanup-request-cts-after-pump");
        var nonSuccess = requestSource.IndexOf("if (!frameResult.IsSuccess)", StringComparison.Ordinal);
        var codec = requestSource.IndexOf("WorkerProtocolCodec.ParseControllerInput", StringComparison.Ordinal);
        Assert.IsTrue(nonSuccess >= 0 && codec > nonSuccess, "stdin-overflow-stops-before-codec");
        Assert.AreEqual(0, Count(requestSource, "IWorkerReadinessPublisher"), "stdin-structure-no-ready-output-dependency");
        Assert.AreEqual(0, Count(requestSource, "IProcessingRunExecutor"), "stdin-structure-no-executor-dependency");
    }

    private static WorkerStdinRequestSource CreateSource(Stream input)
    {
        return new WorkerStdinRequestSource(
            new SingleInputFactory(input),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkerStdinRequestSource>.Instance);
    }

    private static async Task<InitialProcessingRunAcquisition.Accepted> AcquireAcceptedAsync(WorkerStdinRequestSource source)
    {
        var acquisition = await source.AcquireAsync(CancellationToken.None).WaitAsync(Bound);
        Assert.IsInstanceOfType<InitialProcessingRunAcquisition.Accepted>(acquisition, "stdin-acquisition-accepted");
        return (InitialProcessingRunAcquisition.Accepted)acquisition;
    }

    private static ProcessingRunResult CreateResult(ProcessingRunRequest request)
    {
        return new ProcessingRunResult(
            request,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            0,
            0,
            0,
            0,
            ProcessingRunOutcome.Completed,
            null);
    }

    private static byte[] Execute()
    {
        return Encoding.UTF8.GetBytes($"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"{Timestamp}\",\"runId\":\"{RunIdText}\",\"payload\":{{\"trigger\":\"run-once\"}}}}");
    }

    private static byte[] Cancel(long sequence, string runId = RunIdText)
    {
        return Encoding.UTF8.GetBytes($"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"control\",\"type\":\"cancel\",\"sequence\":{sequence},\"timestampUtc\":\"{Timestamp}\",\"runId\":\"{runId}\",\"payload\":{{}}}}");
    }

    private static byte[] JoinLines(params byte[][] frames)
    {
        return frames.SelectMany(frame => frame.Concat("\n"u8.ToArray())).ToArray();
    }

    private static byte[] Replace(byte[] frame, string oldValue, string newValue)
    {
        var text = Encoding.UTF8.GetString(frame);
        Assert.AreEqual(1, Count(text, oldValue), "stdin-one-defect-replacement:" + oldValue);
        return Encoding.UTF8.GetBytes(text.Replace(oldValue, newValue, StringComparison.Ordinal));
    }

    private static byte[] CreateExactAsciiObject(int byteCount)
    {
        const string prefix = "{\"padding\":\"";
        const string suffix = "\"}";
        var padding = byteCount - Encoding.ASCII.GetByteCount(prefix) - Encoding.ASCII.GetByteCount(suffix);
        var bytes = Encoding.ASCII.GetBytes(prefix + new string('x', padding) + suffix);
        Assert.AreEqual(byteCount, bytes.Length, "stdin-exact-ascii-object-byte-count");
        return bytes;
    }

    private static int FindSubsequence(byte[] bytes, byte[] value)
    {
        for (var index = 0; index <= bytes.Length - value.Length; index++)
        {
            if (bytes.AsSpan(index, value.Length).SequenceEqual(value))
            {
                return index;
            }
        }

        Assert.Fail("stdin-scalar-subsequence");
        return -1;
    }

    private static int Count(string value, string token)
    {
        return value.Split(token, StringSplitOptions.None).Length - 1;
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "immich-reversegeo.slnx")))
            {
                return directory.FullName;
            }
        }

        Assert.Fail("stdin-structure-root");
        return string.Empty;
    }

    private interface ICompletionObservable
    {
        TaskCompletionSource Completed { get; }
    }

    private sealed class SingleInputFactory(Stream input) : IWorkerStandardInputStreamFactory
    {
        private int _openCount;

        public Stream OpenStandardInput()
        {
            Assert.AreEqual(1, Interlocked.Increment(ref _openCount), "stdin-factory-open-once");
            return input;
        }
    }

    private sealed class CountingInputFactory(Stream input) : IWorkerStandardInputStreamFactory
    {
        internal int OpenCount { get; private set; }

        public Stream OpenStandardInput()
        {
            OpenCount++;
            return input;
        }
    }

    private sealed class ThrowingInputFactory(string exceptionMessage) : IWorkerStandardInputStreamFactory
    {
        internal int OpenCount { get; private set; }

        internal int ReadCount => 0;

        public Stream OpenStandardInput()
        {
            OpenCount++;
            throw new IOException(exceptionMessage);
        }
    }

    private sealed class GatedOpenInputFactory(Stream input) : IWorkerStandardInputStreamFactory
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int OpenCount { get; private set; }

        public Stream OpenStandardInput()
        {
            OpenCount++;
            OpenStarted.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
            return input;
        }

        internal void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class DeterministicInputStream : Stream, ICompletionObservable
    {
        private readonly byte[] _bytes;
        private readonly int[] _chunks;
        private readonly int _throwOnRead;
        private readonly string _exceptionMessage;
        private int _offset;
        private int _chunkIndex;

        internal DeterministicInputStream(
            byte[] bytes,
            int[] chunks,
            int throwOnRead = int.MaxValue,
            string exceptionMessage = "reader-failure")
        {
            _bytes = bytes;
            _chunks = chunks;
            _throwOnRead = throwOnRead;
            _exceptionMessage = exceptionMessage;
        }

        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource EndObserved => Completed;

        internal int ReadCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _bytes.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
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

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadCount == _throwOnRead)
            {
                Completed.TrySetResult();
                throw new IOException(_exceptionMessage);
            }

            ReadCount++;
            if (_offset == _bytes.Length)
            {
                Completed.TrySetResult();
                return ValueTask.FromResult(0);
            }

            var requested = _chunks[Math.Min(_chunkIndex++, _chunks.Length - 1)];
            var length = Math.Min(Math.Min(requested, buffer.Length), _bytes.Length - _offset);
            _bytes.AsSpan(_offset, length).CopyTo(buffer.Span);
            _offset += length;
            return ValueTask.FromResult(length);
        }
    }

    private sealed class FaultingCloseInputStream(byte[] bytes, int throwOnRead) : Stream
    {
        private int _offset;

        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ReadCount { get; private set; }

        internal int DisposeCount { get; private set; }

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
                Completed.TrySetResult();
                throw new IOException("READ_RAW_SENTINEL");
            }

            ReadCount++;
            if (_offset == bytes.Length)
            {
                Completed.TrySetResult();
                return ValueTask.FromResult(0);
            }

            var length = Math.Min(buffer.Length, bytes.Length - _offset);
            bytes.AsSpan(_offset, length).CopyTo(buffer.Span);
            _offset += length;
            return ValueTask.FromResult(length);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.FromException(new IOException("CLOSE_RAW_SENTINEL"));
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

    private sealed class SegmentedGateInputStream(byte[][] segments) : Stream
    {
        private readonly SemaphoreSlim _release = new(1, int.MaxValue);
        private int _index;
        private bool _completed;

        internal TaskCompletionSource SecondSegmentDelivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource EndObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void ReleaseNext()
        {
            _release.Release();
        }

        internal void Complete()
        {
            _completed = true;
            _release.Release();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _release.WaitAsync(cancellationToken);
            if (_index < segments.Length)
            {
                var segment = segments[_index++];
                segment.CopyTo(buffer);
                if (_index == 2)
                {
                    SecondSegmentDelivered.TrySetResult();
                }

                return segment.Length;
            }

            if (_completed)
            {
                EndObserved.TrySetResult();
                return 0;
            }

            throw new InvalidOperationException("stdin-segment-release-without-data");
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

    private sealed class TerminalFirstRaceInputStream(byte[] execute, byte[] cancel) : Stream
    {
        private readonly TaskCompletionSource _releaseCancel = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readIndex;

        internal TaskCompletionSource CancelReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int DisposeCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void ReleaseCancel()
        {
            _releaseCancel.TrySetResult();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_readIndex++ == 0)
            {
                execute.CopyTo(buffer);
                return execute.Length;
            }

            CancelReadStarted.TrySetResult();
            await _releaseCancel.Task;
            cancel.CopyTo(buffer);
            return cancel.Length;
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
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

    private sealed class FaultingPendingInputStream(byte[] execute) : Stream
    {
        private readonly TaskCompletionSource<int> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _executeDelivered;

        internal TaskCompletionSource PendingRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int DisposeCount { get; private set; }

        internal int ReadCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (!_executeDelivered)
            {
                _executeDelivered = true;
                execute.CopyTo(buffer);
                return ValueTask.FromResult(execute.Length);
            }

            PendingRead.TrySetResult();
            return new ValueTask<int>(_pending.Task);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            _pending.TrySetException(new ObjectDisposedException(nameof(FaultingPendingInputStream)));
            return ValueTask.FromException(new IOException("PENDING_CLOSE_RAW_SENTINEL"));
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

    private sealed class PendingInputStream(bool ignoreCancellation) : Stream
    {
        private readonly TaskCompletionSource<int> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _queued;

        internal TaskCompletionSource PendingRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int DisposeCount { get; private set; }

        internal int ReadCount { get; private set; }

        internal void Queue(byte[] bytes)
        {
            _queued = bytes;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (_queued is not null)
            {
                var queued = _queued;
                _queued = null;
                queued.CopyTo(buffer);
                return ValueTask.FromResult(queued.Length);
            }

            PendingRead.TrySetResult();
            return ignoreCancellation
                ? new ValueTask<int>(_pending.Task)
                : new ValueTask<int>(_pending.Task.WaitAsync(cancellationToken));
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            _pending.TrySetException(new ObjectDisposedException(nameof(PendingInputStream)));
            return ValueTask.CompletedTask;
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal TaskCompletionSource Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<LogEntry> Entries { get; } = [];

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
            var stateValues = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.Select(value => value.Key + "=" + value.Value).ToList()
                : [state?.ToString() ?? string.Empty];
            Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), stateValues, exception));
            Logged.TrySetResult();
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
        List<string> State,
        Exception? Exception);

    private sealed class SignalLogger<T> : ILogger<T>
    {
        internal TaskCompletionSource Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            Logged.TrySetResult();
        }
    }

    private sealed class ThrowingLogger<T> : ILogger<T>
    {
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
            throw new InvalidOperationException("LOGGER_SECRET_SENTINEL");
        }
    }
}
