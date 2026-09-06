using Npgsql;

namespace ImmichReverseGeo.CrossProcessRunLockTestAppHost;

internal sealed record CrossProcessRunLockAppHostOptions(
    CrossProcessRunLockScenario Scenario,
    string ResourceRoot,
    string MarkerPath,
    string ReleasePipeName,
    string ConnectionString,
    string ApplicationName)
{
    internal const string TestConnectionStringEnvironmentVariable = "IMMICH_REVERSEGEO_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly IReadOnlyDictionary<string, CrossProcessRunLockScenario> ScenarioTokens =
        new Dictionary<string, CrossProcessRunLockScenario>(StringComparer.Ordinal)
        {
            ["held-success"] = CrossProcessRunLockScenario.HeldSuccess,
            ["domain-failure"] = CrossProcessRunLockScenario.DomainFailure,
            ["cooperative-cancel"] = CrossProcessRunLockScenario.CooperativeCancel,
            ["ownership-loss"] = CrossProcessRunLockScenario.OwnershipLoss
        };

    internal static bool TryParse(string[] arguments, out CrossProcessRunLockAppHostOptions? options, out string error)
    {
        options = null;
        error = string.Empty;
        if (arguments.Length != 8)
        {
            error = "Exactly four option-value pairs are required.";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var name = arguments[index];
            var value = arguments[index + 1];
            if (name is not "--scenario" and not "--resource-root" and not "--marker-path" and not "--release-pipe")
            {
                error = "An unknown option was supplied.";
                return false;
            }

            if (string.IsNullOrEmpty(value) || value.StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(name, value))
            {
                error = "Each known option must have one non-empty value.";
                return false;
            }
        }

        if (!values.TryGetValue("--scenario", out var scenarioToken)
            || !ScenarioTokens.TryGetValue(scenarioToken, out var scenario))
        {
            error = "A known --scenario value is required.";
            return false;
        }

        if (!values.TryGetValue("--resource-root", out var suppliedRoot)
            || !TryNormalizeResourceRoot(suppliedRoot, out var resourceRoot, out error))
        {
            return false;
        }

        if (!values.TryGetValue("--marker-path", out var suppliedMarker)
            || !TryNormalizeMarkerPath(suppliedMarker, resourceRoot, out var markerPath, out error))
        {
            return false;
        }

        if (!values.TryGetValue("--release-pipe", out var releasePipe)
            || !IsSafePipeName(releasePipe))
        {
            error = "--release-pipe must be a safe unique pipe name.";
            return false;
        }

        if (!TryGetConnectionSettings(out var connectionString, out var applicationName, out error))
        {
            return false;
        }

        options = new CrossProcessRunLockAppHostOptions(
            scenario,
            resourceRoot,
            markerPath,
            releasePipe,
            connectionString,
            applicationName);
        return true;
    }

    private static bool TryNormalizeResourceRoot(string suppliedRoot, out string resourceRoot, out string error)
    {
        resourceRoot = string.Empty;
        error = string.Empty;
        try
        {
            if (!Path.IsPathFullyQualified(suppliedRoot))
            {
                error = "--resource-root must be an absolute path.";
                return false;
            }

            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(suppliedRoot));
            var suppliedPath = Path.TrimEndingDirectorySeparator(suppliedRoot);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(fullPath, suppliedPath, comparison)
                || string.Equals(fullPath, Path.GetPathRoot(fullPath), comparison)
                || !Guid.TryParseExact(Path.GetFileName(fullPath), "D", out _)
                || !Directory.Exists(fullPath))
            {
                error = "--resource-root must be an existing normalized GUID directory.";
                return false;
            }

            resourceRoot = fullPath;
            return true;
        }
        catch (Exception)
        {
            error = "--resource-root is invalid.";
            return false;
        }
    }

    private static bool TryNormalizeMarkerPath(string suppliedMarker, string resourceRoot, out string markerPath, out string error)
    {
        markerPath = string.Empty;
        error = string.Empty;
        try
        {
            if (!Path.IsPathFullyQualified(suppliedMarker))
            {
                error = "--marker-path must be an absolute path.";
                return false;
            }

            var fullPath = Path.GetFullPath(suppliedMarker);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(fullPath), resourceRoot, comparison)
                || !string.Equals(fullPath, suppliedMarker, comparison)
                || string.IsNullOrEmpty(Path.GetFileName(fullPath))
                || File.Exists(fullPath))
            {
                error = "--marker-path must be a new normalized file directly in --resource-root.";
                return false;
            }

            markerPath = fullPath;
            return true;
        }
        catch (Exception)
        {
            error = "--marker-path is invalid.";
            return false;
        }
    }

    private static bool IsSafePipeName(string value)
    {
        if (value.Length is < 1 or > 128)
        {
            return false;
        }

        return value.All(IsSafeAsciiIdentifierCharacter);
    }

    private static bool TryGetConnectionSettings(out string connectionString, out string applicationName, out string error)
    {
        connectionString = string.Empty;
        applicationName = string.Empty;
        error = string.Empty;
        var configured = Environment.GetEnvironmentVariable(TestConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            error = $"{TestConnectionStringEnvironmentVariable} is required.";
            return false;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(configured);
            var configuredApplicationName = builder.ApplicationName;
            if (string.IsNullOrWhiteSpace(builder.Database)
                || string.IsNullOrWhiteSpace(configuredApplicationName)
                || !IsSafeApplicationName(configuredApplicationName))
            {
                error = $"{TestConnectionStringEnvironmentVariable} must select a database and safe PostgreSQL application name.";
                return false;
            }

            connectionString = builder.ConnectionString;
            applicationName = configuredApplicationName;
            return true;
        }
        catch (ArgumentException)
        {
            error = $"{TestConnectionStringEnvironmentVariable} is malformed.";
            return false;
        }
    }

    private static bool IsSafeApplicationName(string value)
    {
        if (value.Length is < 1 or > 63)
        {
            return false;
        }

        return value.All(IsSafeAsciiIdentifierCharacter);
    }

    private static bool IsSafeAsciiIdentifierCharacter(char character)
    {
        return character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-' or '_';
    }
}

internal enum CrossProcessRunLockScenario
{
    HeldSuccess,
    DomainFailure,
    CooperativeCancel,
    OwnershipLoss
}
