using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

internal enum MatrixRawExit { Absent, ExactManaged, PresentPlatformRaw }
internal enum MatrixTerminalAuthority { None, AcceptedWorkerTerminal }
internal enum MatrixProbe { Required, NotApplicable }

internal sealed record ProcessFailureMatrixCase(InternalWorkerProtocolVersion ProtocolVersion,
    WorkerJobKind JobKind, ProcessFailureMatrixRow Oracle)
{
    internal string Id => $"{ProtocolVersion}/{JobKind}/{Oracle.Fault}";

    internal static IEnumerable<ProcessFailureMatrixCase> LifecycleCases()
    {
        // Explicit supported applicability. V1 lookup/cache and their database-lock
        // equivalents do not exist and must never be generated as successful rows.
        (InternalWorkerProtocolVersion Version, WorkerJobKind Kind)[] supported =
        [
            (InternalWorkerProtocolVersion.V1, WorkerJobKind.ProcessAssets),
            (InternalWorkerProtocolVersion.V2, WorkerJobKind.ProcessAssets),
            (InternalWorkerProtocolVersion.V2, WorkerJobKind.CoordinateLookup),
            (InternalWorkerProtocolVersion.V2, WorkerJobKind.CacheMutation)
        ];
        foreach (var (version, kind) in supported)
        {
            foreach (var oracle in ProcessFailureMatrixRow.Lifecycle.Concat(ProcessFailureMatrixRow.Protocol))
            {
                yield return new(version, kind, oracle);
            }
            if (version == InternalWorkerProtocolVersion.V2)
            {
                foreach (var oracle in ProcessFailureMatrixRow.V2Compatibility.Append(ProcessFailureMatrixRow.V2UnknownKind))
                {
                    yield return new(version, kind, oracle);
                }
            }
            else
            {
                yield return new(version, kind, ProcessFailureMatrixRow.V1Additive);
            }
            if (kind == WorkerJobKind.ProcessAssets)
            {
                yield return new(version, kind, ProcessFailureMatrixRow.ProcessAssetsBareBusy);
            }
        }
    }
}

// An oracle declared from the contracts, never calculated by the production classifier.
internal sealed record ProcessFailureMatrixRow(
    string Fault, string Phase, MatrixRawExit RawExit, int? ExitCode, bool Ready,
    MatrixTerminalAuthority Authority, string? Terminal, string Classification,
    string? Violation, int[] Events, MatrixProbe Child = MatrixProbe.Required,
    MatrixProbe Streams = MatrixProbe.Required, MatrixProbe Coordinator = MatrixProbe.Required,
    MatrixProbe DatabaseLock = MatrixProbe.NotApplicable, MatrixProbe CachePublication = MatrixProbe.NotApplicable,
    bool ForcedStop = false, MatrixProbe OwnedArtifacts = MatrixProbe.Required)
{
    internal static readonly ProcessFailureMatrixRow[] Lifecycle =
    [
        new("domain-failure", "accepted-work", MatrixRawExit.ExactManaged, 4, true,
            MatrixTerminalAuthority.AcceptedWorkerTerminal, "failed", "worker-failed", null, [6610, 6611, 6612, 6640, 6641]),
        new("pre-ready-crash", "before-ready", MatrixRawExit.PresentPlatformRaw, 42, false,
            MatrixTerminalAuthority.None, null, "startup-failed", "invalid-lifecycle", [6610, 6611, 6630, 6641]),
        new("post-ready-crash", "accepted-work", MatrixRawExit.PresentPlatformRaw, 42, true,
            MatrixTerminalAuthority.None, null, "crashed", "invalid-lifecycle", [6610, 6611, 6612, 6630, 6641]),
        new("missing-terminal", "accepted-work", MatrixRawExit.ExactManaged, 0, true,
            MatrixTerminalAuthority.None, null, "missing-terminal", "invalid-lifecycle", [6610, 6611, 6612, 6630, 6641]),
        // Raw mapped numbers alone do not grant managed-failure authority.
        new("output-exit", "accepted-work", MatrixRawExit.ExactManaged, 6, true,
            MatrixTerminalAuthority.None, null, "protocol-failed", "invalid-lifecycle", [6610, 6611, 6612, 6630, 6641]),
        new("infrastructure-exit", "accepted-work", MatrixRawExit.ExactManaged, 5, true,
            MatrixTerminalAuthority.None, null, "protocol-failed", "invalid-lifecycle", [6610, 6611, 6612, 6630, 6641]),
        new("invalid-input-exit", "accepted-work", MatrixRawExit.ExactManaged, 2, true,
            MatrixTerminalAuthority.None, null, "protocol-failed", "invalid-lifecycle", [6610, 6611, 6612, 6630, 6641]),
        new("cancelled-exit", "accepted-work", MatrixRawExit.ExactManaged, 130, true,
            MatrixTerminalAuthority.None, null, "protocol-failed", "invalid-lifecycle", [6610, 6611, 6612, 6630, 6641]),
        new("terminal-mismatch", "after-terminal", MatrixRawExit.ExactManaged, 0, true,
            MatrixTerminalAuthority.AcceptedWorkerTerminal, "failed", "terminal-exit-mismatch", null, [6610, 6611, 6612, 6640, 6641]),
        new("post-terminal", "after-terminal", MatrixRawExit.ExactManaged, 4, true,
            MatrixTerminalAuthority.AcceptedWorkerTerminal, "failed", "protocol-failed", "invalid-lifecycle", [6610, 6611, 6612, 6640, 6630, 6641])
    ];

    internal static readonly ProcessFailureMatrixRow[] V2Compatibility =
    [
        new("additive-envelope", "before-started", MatrixRawExit.ExactManaged, 6, true,
            MatrixTerminalAuthority.None, null, "protocol-failed", "invalid-envelope", [6610, 6611, 6612, 6630, 6641]),
        new("additive-payload", "before-started", MatrixRawExit.ExactManaged, 6, true,
            MatrixTerminalAuthority.None, null, "protocol-failed", "invalid-payload", [6610, 6611, 6612, 6630, 6641])
    ];

    internal static readonly ProcessFailureMatrixRow[] Protocol =
    [
        Corrupt("invalid-utf8", "invalid-encoding"),
        Corrupt("malformed-json", "malformed-json"),
        Corrupt("truncated-json", "invalid-framing"),
        Corrupt("blank-frame", "invalid-framing"),
        Corrupt("bom-frame", "invalid-encoding"),
        Corrupt("oversized-frame", "message-too-large"),
        Corrupt("non-protocol-text", "malformed-json"),
        Corrupt("unknown-protocol", "unsupported-protocol"),
        Corrupt("unknown-version", "unsupported-version"),
        Corrupt("unknown-direction", "unsupported-type"),
        Corrupt("unknown-category", "unsupported-type"),
        Corrupt("unknown-type", "unsupported-type"),
        Corrupt("category-type-mismatch", "unsupported-type"),
        Corrupt("duplicate-property", "invalid-envelope"),
        Corrupt("invalid-payload", "invalid-payload"),
        Corrupt("wrong-correlation", "invalid-correlation"),
        Corrupt("sequence-gap", "invalid-sequence", "accepted-work"),
        Corrupt("sequence-replay", "invalid-sequence", "accepted-work"),
        Corrupt("duplicate-ready", "invalid-sequence"),
        new("missing-ready", "before-ready", MatrixRawExit.ExactManaged, 6, false,
            MatrixTerminalAuthority.None, null, "protocol-failed", "invalid-lifecycle", [6610, 6611, 6630, 6641])
    ];

    internal static readonly ProcessFailureMatrixRow V2UnknownKind = Corrupt("unknown-job-kind", "invalid-envelope");
    // Exit 3 belongs only to ProcessAssets. This raw number lacks the busy terminal
    // and therefore cannot manufacture contention authority or a managed outcome.
    internal static readonly ProcessFailureMatrixRow ProcessAssetsBareBusy = new("busy-exit", "accepted-work",
        MatrixRawExit.ExactManaged, 3, true, MatrixTerminalAuthority.None, null,
        "protocol-failed", "invalid-lifecycle", [6610, 6611, 6612, 6630, 6641]);
    internal static readonly ProcessFailureMatrixRow V1Additive = new("additive-properties", "accepted-work",
        MatrixRawExit.ExactManaged, 4, true, MatrixTerminalAuthority.AcceptedWorkerTerminal, "failed",
        "worker-failed", null, [6610, 6611, 6612, 6640, 6641]);

    private static ProcessFailureMatrixRow Corrupt(string fault, string violation, string phase = "before-started") =>
        new(fault, phase, MatrixRawExit.ExactManaged, 6, true, MatrixTerminalAuthority.None,
            null, "protocol-failed", violation, [6610, 6611, 6612, 6630, 6641]);
}
