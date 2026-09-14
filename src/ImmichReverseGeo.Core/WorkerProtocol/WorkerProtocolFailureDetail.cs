namespace ImmichReverseGeo.Core.WorkerProtocol;

/// <summary>Local validation evidence; never serialized as part of the worker protocol.</summary>
public enum WorkerProtocolFailureDetail
{
    None,
    Readiness,
    ProgressConsistency,
    TerminalConsistency,
    ActivityCardinality,
    MissingTerminal
}
