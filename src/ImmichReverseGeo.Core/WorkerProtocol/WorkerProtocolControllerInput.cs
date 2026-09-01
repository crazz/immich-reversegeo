using System;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Core.WorkerProtocol;

public abstract record WorkerProtocolControllerPayload;

public sealed record ExecuteRequestPayload : WorkerProtocolControllerPayload
{
    public ProcessingRunRequest Request { get; }

    public ExecuteRequestPayload(ProcessingRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }
}

public sealed record CancelControlPayload : WorkerProtocolControllerPayload;

public sealed record WorkerProtocolControllerMessage
{
    public string Category { get; }
    public string Type { get; }
    public long Sequence { get; }
    public DateTimeOffset TimestampUtc { get; }
    public Guid RunId { get; }
    public WorkerProtocolControllerPayload Payload { get; }

    public WorkerProtocolControllerMessage(string category, string type, long sequence, DateTimeOffset timestampUtc, Guid runId, WorkerProtocolControllerPayload payload)
    {
        if (!IsKnown(category, type))
        {
            throw new ArgumentException("The category and type combination is not defined.", nameof(type));
        }

        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        WorkerProtocolV1.RequireUtc(timestampUtc, nameof(timestampUtc));
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("Controller messages require a non-empty run ID.", nameof(runId));
        }

        ArgumentNullException.ThrowIfNull(payload);
        if (type == WorkerProtocolV1.ExecuteType && payload is ExecuteRequestPayload execute && execute.Request.RunId == runId)
        {
        }
        else if (type == WorkerProtocolV1.CancelType && payload is CancelControlPayload)
        {
        }
        else
        {
            throw new ArgumentException("The payload does not match the controller message type.", nameof(payload));
        }

        Category = category;
        Type = type;
        Sequence = sequence;
        TimestampUtc = timestampUtc;
        RunId = runId;
        Payload = payload;
    }

    public static bool IsKnown(string category, string type) =>
        (category, type) is
            (WorkerProtocolV1.RequestCategory, WorkerProtocolV1.ExecuteType) or
            (WorkerProtocolV1.ControlCategory, WorkerProtocolV1.CancelType);
}

public enum WorkerProtocolExecutionPhase
{
    BeforeInvocation,
    Executing,
    Terminal
}

public enum WorkerProtocolCancelDisposition
{
    LatchedBeforeInvocation,
    CooperativeCancellationRequested,
    AlreadyCancelledNoOp,
    TerminalNoOp
}

public sealed record WorkerProtocolControllerParseResult
{
    public WorkerProtocolControllerMessage? Message { get; }
    public WorkerProtocolCancelDisposition? CancelDisposition { get; }
    public WorkerProtocolFailure? Failure { get; }
    public bool IsSuccess => Message is not null;

    private WorkerProtocolControllerParseResult(WorkerProtocolControllerMessage? message, WorkerProtocolCancelDisposition? cancelDisposition, WorkerProtocolFailure? failure)
    {
        Message = message;
        CancelDisposition = cancelDisposition;
        Failure = failure;
    }

    public static WorkerProtocolControllerParseResult Success(WorkerProtocolControllerMessage message, WorkerProtocolCancelDisposition? cancelDisposition = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new WorkerProtocolControllerParseResult(message, cancelDisposition, null);
    }

    public static WorkerProtocolControllerParseResult Failed(WorkerProtocolFailureCode code, string diagnostic) => new(null, null, new WorkerProtocolFailure(code, diagnostic));
}

public enum WorkerProtocolControllerInputFinalization
{
    NoRequest,
    ControlsClosed
}

public sealed record WorkerProtocolControllerInputFinalizationResult
{
    public WorkerProtocolControllerInputFinalization? State { get; }
    public WorkerProtocolFailure? Failure { get; }
    public bool IsSuccess => State is not null;

    private WorkerProtocolControllerInputFinalizationResult(WorkerProtocolControllerInputFinalization? state, WorkerProtocolFailure? failure)
    {
        State = state;
        Failure = failure;
    }

    public static WorkerProtocolControllerInputFinalizationResult Success(WorkerProtocolControllerInputFinalization state) => new(state, null);
    public static WorkerProtocolControllerInputFinalizationResult Failed(string diagnostic) => new(null, new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidFraming, diagnostic));
}

public sealed record WorkerProtocolControllerInputSnapshot(long LastSequence, DateTimeOffset? LastTimestampUtc, ProcessingRunRequest? Request, bool CancellationRequested, bool ControlsClosed);

public sealed class WorkerProtocolControllerInputValidator
{
    private long _lastSequence;
    private DateTimeOffset? _lastTimestampUtc;
    private Guid? _runId;
    private ProcessingRunRequest? _request;
    private bool _cancellationRequested;
    private bool _controlsClosed;

    public WorkerProtocolControllerInputSnapshot Snapshot => new(_lastSequence, _lastTimestampUtc, _request, _cancellationRequested, _controlsClosed);

    public WorkerProtocolControllerParseResult Validate(WorkerProtocolControllerMessage message, bool isReady, WorkerProtocolExecutionPhase executionPhase)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!Enum.IsDefined(executionPhase))
        {
            return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Controller execution phase is not defined.");
        }

        if (!isReady)
        {
            return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Controller input requires ready before consumption.");
        }

        if (_controlsClosed)
        {
            return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Controller input is closed.");
        }

        if (_lastSequence == 0)
        {
            if (message.Type != WorkerProtocolV1.ExecuteType || message.Sequence != 1)
            {
                return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "The first controller message must be execute at sequence one.");
            }
        }
        else if (WorkerProtocolSequence.ValidateSuccessor(_lastSequence, message.Sequence) is { } sequenceFailure)
        {
            return WorkerProtocolControllerParseResult.Failed(sequenceFailure.Code, sequenceFailure.Diagnostic);
        }

        if (_lastTimestampUtc is not null && message.TimestampUtc < _lastTimestampUtc)
        {
            return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Controller timestamps must not regress.");
        }

        if (_runId is not null)
        {
            if (message.Type == WorkerProtocolV1.ExecuteType)
            {
                return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Only one execute request may be accepted.");
            }

            if (message.RunId != _runId)
            {
                return Fail(WorkerProtocolFailureCode.InvalidCorrelation, "Cancel correlation must match the accepted run.");
            }
        }

        WorkerProtocolCancelDisposition? cancelDisposition = message.Payload is CancelControlPayload
            ? GetCancelDisposition(executionPhase)
            : null;
        _lastSequence = message.Sequence;
        _lastTimestampUtc = message.TimestampUtc;
        _runId = message.RunId;
        if (message.Payload is ExecuteRequestPayload execute)
        {
            _request = execute.Request;
        }
        if (cancelDisposition is WorkerProtocolCancelDisposition.LatchedBeforeInvocation or WorkerProtocolCancelDisposition.CooperativeCancellationRequested)
        {
            _cancellationRequested = true;
        }

        return WorkerProtocolControllerParseResult.Success(message, cancelDisposition);
    }

    public WorkerProtocolControllerInputFinalizationResult FinalizeInput(bool hasPartialFrame)
    {
        if (hasPartialFrame)
        {
            return WorkerProtocolControllerInputFinalizationResult.Failed("Controller input ended during a frame.");
        }

        if (_request is null)
        {
            return WorkerProtocolControllerInputFinalizationResult.Success(WorkerProtocolControllerInputFinalization.NoRequest);
        }

        _controlsClosed = true;
        return WorkerProtocolControllerInputFinalizationResult.Success(WorkerProtocolControllerInputFinalization.ControlsClosed);
    }

    private WorkerProtocolCancelDisposition GetCancelDisposition(WorkerProtocolExecutionPhase executionPhase)
    {
        return executionPhase switch
        {
            WorkerProtocolExecutionPhase.BeforeInvocation when !_cancellationRequested => WorkerProtocolCancelDisposition.LatchedBeforeInvocation,
            WorkerProtocolExecutionPhase.Executing when !_cancellationRequested => WorkerProtocolCancelDisposition.CooperativeCancellationRequested,
            WorkerProtocolExecutionPhase.Terminal => WorkerProtocolCancelDisposition.TerminalNoOp,
            _ => WorkerProtocolCancelDisposition.AlreadyCancelledNoOp
        };
    }

    private static WorkerProtocolControllerParseResult Fail(WorkerProtocolFailureCode code, string diagnostic) => WorkerProtocolControllerParseResult.Failed(code, diagnostic);
}
