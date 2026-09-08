using System;
using System.Collections.Generic;
using System.Linq;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Core.WorkerJobs;

public enum WorkerJobExecutionPhase
{
    BeforeInvocation,
    Executing,
    Terminal
}

public enum WorkerJobCancelDisposition
{
    LatchedBeforeInvocation,
    CooperativeCancellationRequested,
    AlreadyCancelledNoOp,
    NotCancellableNoOp,
    TerminalNoOp
}

public sealed record WorkerJobControllerValidationResult
{
    public WorkerJobControllerMessage? Message { get; }
    public WorkerJobCancelDisposition? CancelDisposition { get; }
    public WorkerProtocolFailure? Failure { get; }
    public bool IsSuccess => Message is not null;

    private WorkerJobControllerValidationResult(
        WorkerJobControllerMessage? message,
        WorkerJobCancelDisposition? cancelDisposition,
        WorkerProtocolFailure? failure)
    {
        Message = message;
        CancelDisposition = cancelDisposition;
        Failure = failure;
    }

    public static WorkerJobControllerValidationResult Success(
        WorkerJobControllerMessage message,
        WorkerJobCancelDisposition? cancelDisposition = null) =>
        new(message, cancelDisposition, null);

    public static WorkerJobControllerValidationResult Failed(
        WorkerProtocolFailureCode code,
        string diagnostic) =>
        new(null, null, new WorkerProtocolFailure(code, diagnostic));
}

public sealed record WorkerJobControllerInputSnapshot(
    long LastSequence,
    DateTimeOffset? LastTimestampUtc,
    Guid? JobId,
    WorkerJobKind? JobKind,
    WorkerJobDescriptor? Descriptor,
    ProcessAssetsRequest? Request,
    bool CancellationRequested);

public sealed class WorkerJobControllerInputValidator
{
    private readonly IReadOnlyDictionary<WorkerJobKind, WorkerJobDescriptor> _supportedDescriptors;
    private long _lastSequence;
    private DateTimeOffset? _lastTimestampUtc;
    private Guid? _jobId;
    private WorkerJobKind? _jobKind;
    private WorkerJobDescriptor? _descriptor;
    private ProcessAssetsRequest? _request;
    private bool _cancellationRequested;

    public WorkerJobControllerInputValidator(IEnumerable<WorkerJobDescriptor> supportedDescriptors)
    {
        ArgumentNullException.ThrowIfNull(supportedDescriptors);
        var byKind = new Dictionary<WorkerJobKind, WorkerJobDescriptor>();
        foreach (WorkerJobDescriptor descriptor in supportedDescriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (!byKind.TryAdd(descriptor.Kind, descriptor))
            {
                throw new ArgumentException(
                    "Supported worker-job descriptors must have unique kinds.",
                    nameof(supportedDescriptors));
            }
        }

        _supportedDescriptors = byKind;
    }

    public WorkerJobControllerInputSnapshot Snapshot => new(
        _lastSequence,
        _lastTimestampUtc,
        _jobId,
        _jobKind,
        _descriptor,
        _request,
        _cancellationRequested);

    public WorkerJobControllerValidationResult Validate(
        WorkerJobControllerMessage message,
        bool isReady,
        WorkerJobExecutionPhase executionPhase)
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

        if (_lastSequence == 0)
        {
            if (message.Type != WorkerJobProtocolV2.ExecuteType || message.Sequence != 1)
            {
                return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "The first controller message must be execute at sequence one.");
            }

            if (!_supportedDescriptors.ContainsKey(message.JobKind))
            {
                return Fail(WorkerProtocolFailureCode.UnsupportedType, "The job kind is not registered.");
            }
        }
        else if (WorkerProtocolSequence.ValidateSuccessor(_lastSequence, message.Sequence) is { } sequenceFailure)
        {
            return Fail(sequenceFailure.Code, sequenceFailure.Diagnostic);
        }

        if (_lastTimestampUtc is not null && message.TimestampUtc < _lastTimestampUtc)
        {
            return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Controller timestamps must not regress.");
        }

        if (_jobId is not null)
        {
            if (message.Type == WorkerJobProtocolV2.ExecuteType)
            {
                return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Only one execute request may be accepted.");
            }

            if (message.JobId != _jobId || message.JobKind != _jobKind)
            {
                return Fail(WorkerProtocolFailureCode.InvalidCorrelation, "Cancel correlation must match the accepted job.");
            }
        }

        WorkerJobCancelDisposition? cancelDisposition = message.Payload is WorkerJobCancelPayload
            ? _descriptor?.Arbitration.IsCancellable == false
                ? WorkerJobCancelDisposition.NotCancellableNoOp
                : GetCancelDisposition(executionPhase)
            : null;
        _lastSequence = message.Sequence;
        _lastTimestampUtc = message.TimestampUtc;
        _jobId = message.JobId;
        _jobKind = message.JobKind;
        if (message.Payload is ProcessAssetsExecutePayload execute)
        {
            _request = execute.Request;
            _descriptor = _supportedDescriptors[message.JobKind];
        }

        if (cancelDisposition is
            WorkerJobCancelDisposition.LatchedBeforeInvocation or
            WorkerJobCancelDisposition.CooperativeCancellationRequested)
        {
            _cancellationRequested = true;
        }

        return WorkerJobControllerValidationResult.Success(message, cancelDisposition);
    }

    private WorkerJobCancelDisposition GetCancelDisposition(WorkerJobExecutionPhase executionPhase) =>
        executionPhase switch
        {
            WorkerJobExecutionPhase.BeforeInvocation when !_cancellationRequested =>
                WorkerJobCancelDisposition.LatchedBeforeInvocation,
            WorkerJobExecutionPhase.Executing when !_cancellationRequested =>
                WorkerJobCancelDisposition.CooperativeCancellationRequested,
            WorkerJobExecutionPhase.Terminal => WorkerJobCancelDisposition.TerminalNoOp,
            _ => WorkerJobCancelDisposition.AlreadyCancelledNoOp
        };

    private static WorkerJobControllerValidationResult Fail(
        WorkerProtocolFailureCode code,
        string diagnostic) =>
        WorkerJobControllerValidationResult.Failed(code, diagnostic);
}

public sealed record WorkerJobOutputValidationResult
{
    public WorkerJobOutputMessage? Message { get; }
    public WorkerProtocolFailure? Failure { get; }
    public bool IsSuccess => Message is not null;

    private WorkerJobOutputValidationResult(
        WorkerJobOutputMessage? message,
        WorkerProtocolFailure? failure)
    {
        Message = message;
        Failure = failure;
    }

    public static WorkerJobOutputValidationResult Success(WorkerJobOutputMessage message) =>
        new(message, null);

    public static WorkerJobOutputValidationResult Failed(
        WorkerProtocolFailureCode code,
        string diagnostic) =>
        new(null, new WorkerProtocolFailure(code, diagnostic));
}

public sealed record WorkerJobOutputStreamSnapshot(
    long LastSequence,
    DateTimeOffset? LastTimestampUtc,
    bool ReadySeen,
    bool JobStarted,
    bool EligibilitySeen,
    Guid? JobId,
    WorkerJobKind? JobKind,
    bool TerminalSeen,
    int ActiveActivityCount);

public sealed class WorkerJobOutputStreamValidator
{
    private readonly Guid _expectedJobId;
    private readonly WorkerJobKind _expectedJobKind;
    private readonly HashSet<Guid> _activeActivities = [];
    private long _lastSequence;
    private DateTimeOffset? _lastTimestampUtc;
    private bool _readySeen;
    private bool _jobStarted;
    private bool _eligibilitySeen;
    private bool _terminalSeen;

    public WorkerJobOutputStreamValidator(Guid expectedJobId, WorkerJobKind expectedJobKind)
    {
        if (expectedJobId == Guid.Empty)
        {
            throw new ArgumentException("The expected job ID must not be empty.", nameof(expectedJobId));
        }

        _expectedJobId = expectedJobId;
        _expectedJobKind = expectedJobKind;
    }

    public WorkerJobOutputStreamSnapshot Snapshot => new(
        _lastSequence,
        _lastTimestampUtc,
        _readySeen,
        _jobStarted,
        _eligibilitySeen,
        _jobStarted ? _expectedJobId : null,
        _jobStarted ? _expectedJobKind : null,
        _terminalSeen,
        _activeActivities.Count);

    public WorkerJobOutputValidationResult Validate(WorkerJobOutputMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_terminalSeen)
        {
            return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "No output may follow terminal.");
        }

        if (_lastSequence == 0)
        {
            if (message.Sequence != 1 || message.Type != WorkerJobProtocolV2.ReadyType)
            {
                return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Ready must be the first output at sequence one.");
            }
        }
        else if (WorkerProtocolSequence.ValidateSuccessor(_lastSequence, message.Sequence) is { } sequenceFailure)
        {
            return Fail(sequenceFailure.Code, sequenceFailure.Diagnostic);
        }

        if (_lastTimestampUtc is not null && message.TimestampUtc < _lastTimestampUtc)
        {
            return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Output timestamps must not regress.");
        }

        if (message.Type == WorkerJobProtocolV2.ReadyType)
        {
            if (_readySeen)
            {
                return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Ready may be emitted only once.");
            }

            var ready = (WorkerJobReadyPayload)message.Payload;
            if (!ready.SupportedJobKinds.Contains(_expectedJobKind))
            {
                return Fail(WorkerProtocolFailureCode.UnsupportedType, "Ready did not advertise the selected job kind.");
            }

            _readySeen = true;
            return Commit(message);
        }

        if (!_readySeen || message.JobId != _expectedJobId || message.JobKind != _expectedJobKind)
        {
            return Fail(WorkerProtocolFailureCode.InvalidCorrelation, "Output correlation does not match the active job.");
        }

        if (!_jobStarted && message.Type != WorkerJobProtocolV2.JobStartedType)
        {
            return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Job-started must be the first job output.");
        }

        switch (message.Payload)
        {
            case WorkerJobStartedPayload:
                if (_jobStarted)
                {
                    return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Job-started may be emitted only once.");
                }

                _jobStarted = true;
                break;
            case ProcessAssetsEligibilityPayload:
                if (_eligibilitySeen)
                {
                    return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Eligibility may be emitted only once.");
                }

                _eligibilitySeen = true;
                break;
            case ProcessAssetsProgressPayload when !_eligibilitySeen:
                return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Progress requires eligibility first.");
            case WorkerJobActivityStartedPayload started:
                if (!_activeActivities.Add(started.ActivityId))
                {
                    return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "An activity may be started only once while active.");
                }

                break;
            case WorkerJobActivityEndedPayload ended:
                if (!_activeActivities.Remove(ended.ActivityId))
                {
                    return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "An activity end requires an active activity.");
                }

                break;
            case WorkerJobTerminalPayload:
                if (_activeActivities.Count != 0)
                {
                    return Fail(WorkerProtocolFailureCode.InvalidLifecycle, "Terminal requires all activities to be closed.");
                }

                _terminalSeen = true;
                break;
        }

        return Commit(message);
    }

    public WorkerProtocolFailure? FinalizeOutput(bool hasPartialFrame)
    {
        if (hasPartialFrame)
        {
            return new WorkerProtocolFailure(
                WorkerProtocolFailureCode.InvalidFraming,
                "Output ended during a frame.");
        }

        if (!_terminalSeen)
        {
            return new WorkerProtocolFailure(
                WorkerProtocolFailureCode.InvalidLifecycle,
                "Output ended before terminal.",
                WorkerProtocolFailureDetail.MissingTerminal);
        }

        return null;
    }

    private WorkerJobOutputValidationResult Commit(WorkerJobOutputMessage message)
    {
        _lastSequence = message.Sequence;
        _lastTimestampUtc = message.TimestampUtc;
        return WorkerJobOutputValidationResult.Success(message);
    }

    private static WorkerJobOutputValidationResult Fail(
        WorkerProtocolFailureCode code,
        string diagnostic) =>
        WorkerJobOutputValidationResult.Failed(code, diagnostic);
}
