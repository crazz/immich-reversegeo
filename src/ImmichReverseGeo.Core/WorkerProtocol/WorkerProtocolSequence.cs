namespace ImmichReverseGeo.Core.WorkerProtocol;

internal static class WorkerProtocolSequence
{
    internal static WorkerProtocolFailure? ValidateSuccessor(long priorSequence, long sequence)
    {
        return priorSequence == long.MaxValue || sequence != priorSequence + 1
            ? new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidSequence, "Event sequence must advance by exactly one.")
            : null;
    }
}
