using System;

namespace ImmichReverseGeo.Core.ApplicationRole;

/// <summary>
/// Identifies the immutable public deployment mode selected before host construction.
/// </summary>
public sealed class DeploymentMode
{
    private DeploymentMode()
    {
    }

    public static DeploymentMode Standard { get; } = new();

    public static DeploymentMode WebOnly { get; } = new();

    public static DeploymentMode RunOnce { get; } = new();
}

/// <summary>
/// Represents the result of strict deployment-mode resolution.
/// </summary>
public abstract class DeploymentModeResolution
{
    private DeploymentModeResolution()
    {
    }

    internal static Success CreateSuccess(DeploymentMode mode)
    {
        return new Success(mode);
    }

    internal static Failure CreateFailure()
    {
        return new Failure();
    }

    public sealed class Success : DeploymentModeResolution
    {
        internal Success(DeploymentMode mode)
        {
            Mode = mode;
        }

        public DeploymentMode Mode { get; }
    }

    public sealed class Failure : DeploymentModeResolution
    {
        internal Failure()
        {
        }

        public string Diagnostic => DeploymentModeResolver.InvalidModeDiagnostic;
    }
}

/// <summary>
/// Resolves the sole public deployment-mode environment variable without normalizing it.
/// </summary>
public static class DeploymentModeResolver
{
    public const string EnvironmentVariableName = "IMMICH_REVERSEGEO_MODE";
    public const string InvalidModeDiagnostic =
        "invalid-deployment-mode: IMMICH_REVERSEGEO_MODE must be one of: standard, web-only, run-once.";

    public static DeploymentModeResolution Resolve(Func<string, string?> environmentVariableReader)
    {
        ArgumentNullException.ThrowIfNull(environmentVariableReader);
        return ResolveValue(environmentVariableReader(EnvironmentVariableName));
    }

    private static DeploymentModeResolution ResolveValue(string? value)
    {
        return value switch
        {
            null => DeploymentModeResolution.CreateSuccess(DeploymentMode.Standard),
            "standard" => DeploymentModeResolution.CreateSuccess(DeploymentMode.Standard),
            "web-only" => DeploymentModeResolution.CreateSuccess(DeploymentMode.WebOnly),
            "run-once" => DeploymentModeResolution.CreateSuccess(DeploymentMode.RunOnce),
            _ => DeploymentModeResolution.CreateFailure()
        };
    }
}
