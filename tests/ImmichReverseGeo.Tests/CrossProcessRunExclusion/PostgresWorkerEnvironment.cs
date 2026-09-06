using Npgsql;
using System.Collections;

namespace ImmichReverseGeo.Tests.CrossProcessRunExclusion;

/// <summary>
/// Builds the deliberately small database environment inherited by a Change32 child.
/// The connection string is parsed before it crosses the process boundary so command
/// arguments and protocol captures never contain credentials.
/// </summary>
internal sealed class PostgresWorkerEnvironment
{
    private const int ProbeTimeoutSeconds = 5;
    private static readonly string[] ClearedVariables =
    [
        "PGHOST", "PGHOSTADDR", "PGPORT", "PGDATABASE", "PGUSER", "PGPASSWORD",
        "PGPASSFILE", "PGSERVICE", "PGSERVICEFILE", "PGSSLMODE", "PGSSLROOTCERT",
        "PGSSLCERT", "PGSSLKEY", "PGGSSENCMODE", "PGAPPNAME",
        "DB_HOST", "DB_PORT", "DB_USERNAME", "DB_PASSWORD", "DB_DATABASE_NAME"
    ];

    private PostgresWorkerEnvironment(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<string> variablesToClear,
        string canonicalConnectionString,
        string effectiveProductionConnectionString,
        string applicationName)
    {
        Values = values;
        VariablesToClear = variablesToClear;
        CanonicalConnectionString = canonicalConnectionString;
        EffectiveProductionConnectionString = effectiveProductionConnectionString;
        ApplicationName = applicationName;
    }

    internal IReadOnlyDictionary<string, string> Values { get; }

    internal IReadOnlyList<string> VariablesToClear { get; }

    internal string CanonicalConnectionString { get; }

    internal string EffectiveProductionConnectionString { get; }

    private string ApplicationName { get; }

    internal static PostgresWorkerEnvironment Create(string connectionString, string applicationName)
    {
        ValidateApplicationName(applicationName);
        NpgsqlConnectionStringBuilder input;
        try
        {
            input = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new PostgresIntegrationSetupException("The Change32 PostgreSQL setting cannot be parsed for child-process use.");
        }

        RejectUnsupportedSettings(input);
        if (string.IsNullOrWhiteSpace(input.Host)
            || string.IsNullOrWhiteSpace(input.Username)
            || string.IsNullOrWhiteSpace(input.Database)
            || input.Port is < 1 or > 65535)
        {
            throw new PostgresIntegrationSetupException("The Change32 PostgreSQL setting must provide Host, Port, Username, and Database for child-process use.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DB_HOST"] = SerializedValue("Host", input.Host),
            ["DB_PORT"] = SerializedValue("Port", input.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ["DB_USERNAME"] = SerializedValue("Username", input.Username),
            ["DB_PASSWORD"] = SerializedValue("Password", input.Password ?? string.Empty),
            ["DB_DATABASE_NAME"] = SerializedValue("Database", input.Database)
        };
        string literalConnectionString = BuildProductionConnectionString(values);

        NpgsqlConnectionStringBuilder canonical = new(literalConnectionString)
        {
            ApplicationName = applicationName,
            Pooling = false,
            IncludeErrorDetail = false
        };
        AssertTarget(canonical, input);

        values[PostgresIntegrationSettings.ConnectionStringEnvironmentVariable] = canonical.ConnectionString;
        values["PGAPPNAME"] = applicationName;
        return new PostgresWorkerEnvironment(
            values,
            ClearedVariables,
            canonical.ConnectionString,
            literalConnectionString,
            applicationName);
    }

    internal async Task ProbeEffectiveProductionConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(EffectiveProductionConnectionString)
        {
            ApplicationName = ApplicationName,
            Pooling = false,
            Timeout = ProbeTimeoutSeconds,
            CommandTimeout = ProbeTimeoutSeconds,
            IncludeErrorDetail = false
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new PostgresIntegrationSetupException("The reconstructed Change32 child database settings cannot connect.");
        }
    }

    internal static string BuildProductionConnectionString(IReadOnlyDictionary<string, string> values)
    {
        return $"Host={Require(values, "DB_HOST")};Port={Require(values, "DB_PORT")};Username={Require(values, "DB_USERNAME")};Password={Require(values, "DB_PASSWORD")};Database={Require(values, "DB_DATABASE_NAME")};GSS Encryption Mode=Disable";
    }

    private static string Require(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out string? value) && value is not null
            ? value
            : throw new ArgumentException($"The required child database value '{key}' is absent.", nameof(values));
    }

    private static string SerializedValue(string key, string value)
    {
        var builder = new NpgsqlConnectionStringBuilder();
        builder[key] = value;
        string serialized = builder.ConnectionString;
        return serialized[(serialized.IndexOf('=') + 1)..];
    }

    private static void AssertTarget(NpgsqlConnectionStringBuilder canonical, NpgsqlConnectionStringBuilder intended)
    {
        if (!string.Equals(canonical.Host, intended.Host, StringComparison.Ordinal)
            || canonical.Port != intended.Port
            || !string.Equals(canonical.Username, intended.Username, StringComparison.Ordinal)
            || !string.Equals(canonical.Password, intended.Password, StringComparison.Ordinal)
            || !string.Equals(canonical.Database, intended.Database, StringComparison.Ordinal))
        {
            throw new PostgresIntegrationSetupException("The Change32 PostgreSQL setting cannot be represented safely for a child process.");
        }
    }

    private static void RejectUnsupportedSettings(NpgsqlConnectionStringBuilder builder)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Host", "Port", "Username", "User ID", "Password", "Database", "Initial Catalog",
            "Application Name", "Pooling", "Timeout", "Command Timeout", "Include Error Detail"
        };

        foreach (KeyValuePair<string, object?> entry in builder)
        {
            string key = entry.Key;
            if (string.Equals(key, "GSS Encryption Mode", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Convert.ToString(entry.Value, System.Globalization.CultureInfo.InvariantCulture), "Disable", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(key, "SSL Mode", StringComparison.OrdinalIgnoreCase)
                || !allowed.Contains(key))
            {
                throw new PostgresIntegrationSetupException("The Change32 child harness cannot safely preserve a configured PostgreSQL transport, authentication, or security setting.");
            }
        }
    }

    private static void ValidateApplicationName(string applicationName)
    {
        if (string.IsNullOrWhiteSpace(applicationName)
            || applicationName.Length > 63
            || applicationName.Any(character => !((character >= 'A' && character <= 'Z')
                || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9')
                || character is '-' or '_')))
        {
            throw new ArgumentException("The PostgreSQL application name must use safe ASCII and fit within 63 bytes.", nameof(applicationName));
        }
    }
}
