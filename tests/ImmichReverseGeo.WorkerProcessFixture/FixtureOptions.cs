using System.Globalization;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal enum FixtureScenario
{
    Ready,
    Success,
    NoWork,
    PreReadyCrash,
    PostReadyCrash,
    Malformed,
    Oversize,
    Unknown,
    InvalidSequence,
    TerminalMismatch,
    StandardErrorFlood,
    RawExit,
    CooperativeCancel,
    Unresponsive
}

internal enum MalformedKind
{
    Utf8,
    Json,
    Framing
}

internal enum UnknownKind
{
    Version,
    Category,
    Type
}

internal enum SequenceFault
{
    Gap,
    Replay
}

internal enum TerminalKind
{
    Completed,
    Cancelled,
    Failed
}

internal sealed record FixtureOptions(
    FixtureScenario Scenario,
    string ResourceRoot,
    string? CaptureName,
    int? StandardErrorBytes,
    int? ExitCode,
    MalformedKind? SelectedMalformedKind,
    UnknownKind? SelectedUnknownKind,
    SequenceFault? SelectedSequenceFault,
    TerminalKind? SelectedTerminalKind)
{
    internal const int MaximumStandardErrorBytes = 8 * 1024 * 1024;
    internal const int StandardErrorCapacity = 65_536;

    private static readonly IReadOnlyDictionary<string, FixtureScenario> ScenarioTokens =
        new Dictionary<string, FixtureScenario>(StringComparer.Ordinal)
        {
            ["ready"] = FixtureScenario.Ready,
            ["success"] = FixtureScenario.Success,
            ["no-work"] = FixtureScenario.NoWork,
            ["pre-ready-crash"] = FixtureScenario.PreReadyCrash,
            ["post-ready-crash"] = FixtureScenario.PostReadyCrash,
            ["malformed"] = FixtureScenario.Malformed,
            ["oversize"] = FixtureScenario.Oversize,
            ["unknown"] = FixtureScenario.Unknown,
            ["invalid-sequence"] = FixtureScenario.InvalidSequence,
            ["terminal-mismatch"] = FixtureScenario.TerminalMismatch,
            ["stderr-flood"] = FixtureScenario.StandardErrorFlood,
            ["raw-exit"] = FixtureScenario.RawExit,
            ["cooperative-cancel"] = FixtureScenario.CooperativeCancel,
            ["unresponsive"] = FixtureScenario.Unresponsive
        };

    internal static bool TryParse(string[] arguments, out FixtureOptions? options, out string error)
    {
        options = null;
        error = string.Empty;

        if (arguments.Length == 0 || arguments.Length % 2 != 0)
        {
            error = "Arguments must be supplied as option-value pairs.";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var name = arguments[index];
            var value = arguments[index + 1];
            if (!IsKnownOption(name))
            {
                error = $"Unknown option: {Bound(name)}.";
                return false;
            }

            if (!values.TryAdd(name, value))
            {
                error = $"Duplicate option: {name}.";
                return false;
            }

            if (string.IsNullOrEmpty(value) || value.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Option {name} requires a value.";
                return false;
            }
        }

        if (!values.TryGetValue("--scenario", out var scenarioValue) || !ScenarioTokens.TryGetValue(scenarioValue, out var scenario))
        {
            error = "A known --scenario value is required.";
            return false;
        }

        if (!values.TryGetValue("--resource-root", out var resourceRootValue) || !TryNormalizeResourceRoot(resourceRootValue, out var resourceRoot, out error))
        {
            if (string.IsNullOrEmpty(error))
            {
                error = "--resource-root is required.";
            }

            return false;
        }

        string? captureName = null;
        if (values.TryGetValue("--capture-name", out var captureNameValue))
        {
            if (!ScenarioReadsExecute(scenario))
            {
                error = "--capture-name is not valid for the selected scenario.";
                return false;
            }

            if (!TryValidateCaptureName(captureNameValue, out error))
            {
                return false;
            }

            captureName = captureNameValue;
        }

        if (!TryParseScenarioOption(values, "--malformed-kind", scenario == FixtureScenario.Malformed, ParseMalformedKind, out MalformedKind? malformedKind, out error)
            || !TryParseScenarioOption(values, "--unknown-kind", scenario == FixtureScenario.Unknown, ParseUnknownKind, out UnknownKind? unknownKind, out error)
            || !TryParseScenarioOption(values, "--sequence-fault", scenario == FixtureScenario.InvalidSequence, ParseSequenceFault, out SequenceFault? sequenceFault, out error)
            || !TryParseScenarioOption(values, "--terminal", scenario == FixtureScenario.TerminalMismatch, ParseTerminalKind, out TerminalKind? terminalKind, out error))
        {
            return false;
        }

        int? standardErrorBytes = null;
        var requiresStandardErrorBytes = scenario == FixtureScenario.StandardErrorFlood;
        if (!TryGetRequiredOption(values, "--stderr-bytes", requiresStandardErrorBytes, out var standardErrorBytesValue, out error))
        {
            return false;
        }

        if (standardErrorBytesValue is not null)
        {
            if (!TryParseCanonicalInt(standardErrorBytesValue, out var parsedStandardErrorBytes)
                || parsedStandardErrorBytes <= StandardErrorCapacity
                || parsedStandardErrorBytes > MaximumStandardErrorBytes)
            {
                error = $"--stderr-bytes must be from {StandardErrorCapacity + 1} through {MaximumStandardErrorBytes}.";
                return false;
            }

            standardErrorBytes = parsedStandardErrorBytes;
        }

        int? exitCode = null;
        var requiresExitCode = scenario is FixtureScenario.PreReadyCrash
            or FixtureScenario.PostReadyCrash
            or FixtureScenario.TerminalMismatch
            or FixtureScenario.RawExit;
        if (!TryGetRequiredOption(values, "--exit-code", requiresExitCode, out var exitCodeValue, out error))
        {
            return false;
        }

        if (exitCodeValue is not null)
        {
            if (!TryParseCanonicalInt(exitCodeValue, out var parsedExitCode) || parsedExitCode is < 0 or > 255)
            {
                error = "--exit-code must be from 0 through 255.";
                return false;
            }

            if ((scenario is FixtureScenario.PreReadyCrash or FixtureScenario.PostReadyCrash) && parsedExitCode == 0)
            {
                error = "Crash scenarios require a nonzero --exit-code.";
                return false;
            }

            exitCode = parsedExitCode;
        }

        if (scenario == FixtureScenario.TerminalMismatch
            && exitCode == ExpectedExitCode(terminalKind!.Value))
        {
            error = "Terminal mismatch requires an exit code that contradicts the selected terminal.";
            return false;
        }

        options = new FixtureOptions(
            scenario,
            resourceRoot,
            captureName,
            standardErrorBytes,
            exitCode,
            malformedKind,
            unknownKind,
            sequenceFault,
            terminalKind);
        return true;
    }

    private static bool IsKnownOption(string name)
    {
        return name is "--scenario"
            or "--resource-root"
            or "--capture-name"
            or "--stderr-bytes"
            or "--exit-code"
            or "--malformed-kind"
            or "--unknown-kind"
            or "--sequence-fault"
            or "--terminal";
    }

    private static bool ScenarioReadsExecute(FixtureScenario scenario)
    {
        return scenario is not FixtureScenario.PreReadyCrash and not FixtureScenario.RawExit;
    }

    private static bool TryNormalizeResourceRoot(string value, out string resourceRoot, out string error)
    {
        resourceRoot = string.Empty;
        error = string.Empty;

        try
        {
            if (!Path.IsPathFullyQualified(value))
            {
                error = "--resource-root must be an absolute path.";
                return false;
            }

            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            var suppliedPath = Path.TrimEndingDirectorySeparator(value);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(fullPath, suppliedPath, comparison))
            {
                error = "--resource-root must already be normalized.";
                return false;
            }

            if (string.Equals(fullPath, Path.GetPathRoot(fullPath), comparison))
            {
                error = "--resource-root must not be a filesystem root.";
                return false;
            }

            if (!Guid.TryParseExact(Path.GetFileName(fullPath), "D", out _))
            {
                error = "--resource-root must end in a canonical GUID directory name.";
                return false;
            }

            if (!Directory.Exists(fullPath))
            {
                error = "--resource-root must identify an existing directory.";
                return false;
            }

            resourceRoot = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            error = "--resource-root is not a valid path.";
            return false;
        }
    }

    private static bool TryValidateCaptureName(string value, out string error)
    {
        error = string.Empty;
        if (value.Length is < 1 or > 128
            || Path.IsPathRooted(value)
            || value is "." or ".."
            || value.EndsWith(' ')
            || value.EndsWith('.')
            || !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0
            || IsReservedWindowsFileName(value))
        {
            error = "--capture-name must be a safe filename of at most 128 characters.";
            return false;
        }

        return true;
    }

    private static bool IsReservedWindowsFileName(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(value).ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
    }

    private static bool TryParseScenarioOption<T>(
        IReadOnlyDictionary<string, string> values,
        string optionName,
        bool required,
        Func<string, T?> parser,
        out T? parsed,
        out string error)
        where T : struct
    {
        parsed = null;
        if (!TryGetRequiredOption(values, optionName, required, out var value, out error))
        {
            return false;
        }

        if (value is null)
        {
            return true;
        }

        parsed = parser(value);
        if (parsed is null)
        {
            error = $"Unknown {optionName} value.";
            return false;
        }

        return true;
    }

    private static bool TryGetRequiredOption(
        IReadOnlyDictionary<string, string> values,
        string optionName,
        bool required,
        out string? value,
        out string error)
    {
        error = string.Empty;
        if (values.TryGetValue(optionName, out value))
        {
            if (!required)
            {
                error = $"{optionName} is not valid for the selected scenario.";
                return false;
            }

            return true;
        }

        if (required)
        {
            error = $"{optionName} is required for the selected scenario.";
            return false;
        }

        return true;
    }

    private static bool TryParseCanonicalInt(string value, out int result)
    {
        result = 0;
        if (value.Length == 0 || value.Length > 1 && value[0] == '0')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private static MalformedKind? ParseMalformedKind(string value)
    {
        return value switch
        {
            "utf8" => MalformedKind.Utf8,
            "json" => MalformedKind.Json,
            "framing" => MalformedKind.Framing,
            _ => null
        };
    }

    private static UnknownKind? ParseUnknownKind(string value)
    {
        return value switch
        {
            "version" => UnknownKind.Version,
            "category" => UnknownKind.Category,
            "type" => UnknownKind.Type,
            _ => null
        };
    }

    private static SequenceFault? ParseSequenceFault(string value)
    {
        return value switch
        {
            "gap" => SequenceFault.Gap,
            "replay" => SequenceFault.Replay,
            _ => null
        };
    }

    private static TerminalKind? ParseTerminalKind(string value)
    {
        return value switch
        {
            "completed" => TerminalKind.Completed,
            "cancelled" => TerminalKind.Cancelled,
            "failed" => TerminalKind.Failed,
            _ => null
        };
    }

    private static int ExpectedExitCode(TerminalKind terminal)
    {
        return terminal switch
        {
            TerminalKind.Completed => ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitCodes.Completed,
            TerminalKind.Cancelled => ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitCodes.Cancelled,
            TerminalKind.Failed => ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitCodes.ExecutorFailure,
            _ => throw new ArgumentOutOfRangeException(nameof(terminal))
        };
    }

    private static string Bound(string value)
    {
        const int maximumLength = 64;
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }
}
