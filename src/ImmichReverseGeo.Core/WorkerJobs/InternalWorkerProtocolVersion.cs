using System;

namespace ImmichReverseGeo.Core.WorkerJobs;

public enum InternalWorkerProtocolVersion
{
    V1 = 1,
    V2 = 2
}

public abstract record InternalWorkerProtocolVersionSelection
{
    private InternalWorkerProtocolVersionSelection()
    {
    }

    public sealed record Success(InternalWorkerProtocolVersion Version) :
        InternalWorkerProtocolVersionSelection;

    public sealed record Invalid : InternalWorkerProtocolVersionSelection
    {
        public static Invalid Instance { get; } = new();

        private Invalid()
        {
        }
    }
}

public static class InternalWorkerProtocolVersionSelector
{
    public const string EnvironmentVariableName =
        "IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION";

    public const string InvalidSelectionDiagnostic =
        "The internal worker protocol selection is invalid.";

    public static InternalWorkerProtocolVersionSelection Select(
        Func<string, string?> environmentVariableReader)
    {
        ArgumentNullException.ThrowIfNull(environmentVariableReader);
        string? value = environmentVariableReader(EnvironmentVariableName);
        return value switch
        {
            null => new InternalWorkerProtocolVersionSelection.Success(
                InternalWorkerProtocolVersion.V1),
            "2" => new InternalWorkerProtocolVersionSelection.Success(
                InternalWorkerProtocolVersion.V2),
            _ => InternalWorkerProtocolVersionSelection.Invalid.Instance
        };
    }
}
