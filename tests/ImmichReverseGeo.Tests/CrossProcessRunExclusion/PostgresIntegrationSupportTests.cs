namespace ImmichReverseGeo.Tests.CrossProcessRunExclusion;

[TestClass]
[TestCategory("Change32")]
public sealed class PostgresIntegrationSupportTests
{
    [TestMethod]
    public void ReadEnvironment_MissingSetting_ReturnsSecretFreeSetupFailure()
    {
        PostgresIntegrationSettingsResult result = PostgresIntegrationSettings.Parse(null);

        PostgresIntegrationSettingsFailure failure = result as PostgresIntegrationSettingsFailure
            ?? throw new AssertFailedException("Expected a typed PostgreSQL setup failure.");
        StringAssert.Contains(failure.Reason, PostgresIntegrationSettings.ConnectionStringEnvironmentVariable);
    }

    [TestMethod]
    public void Parse_MalformedSetting_ReturnsSecretFreeSetupFailure()
    {
        const string sentinelSecret = "do-not-echo-change32-secret";
        PostgresIntegrationSettingsResult result = PostgresIntegrationSettings.Parse(
            $"Host=localhost;Username=test;Password={sentinelSecret};Database=test;not-a-connection-key=value");

        PostgresIntegrationSettingsFailure failure = result as PostgresIntegrationSettingsFailure
            ?? throw new AssertFailedException("Expected a typed PostgreSQL setup failure.");
        StringAssert.Contains(failure.Reason, PostgresIntegrationSettings.ConnectionStringEnvironmentVariable);
        Assert.IsFalse(failure.Reason.Contains(sentinelSecret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Parse_RequiredDatabaseFieldsMissing_ReturnsSecretFreeSetupFailure()
    {
        const string sentinelSecret = "change32-password";
        PostgresIntegrationSettingsResult result = PostgresIntegrationSettings.Parse(
            $"Host=localhost;Password={sentinelSecret}");

        PostgresIntegrationSettingsFailure failure = result as PostgresIntegrationSettingsFailure
            ?? throw new AssertFailedException("Expected a typed PostgreSQL setup failure.");
        StringAssert.Contains(failure.Reason, "Host, Database, and Username");
        Assert.IsFalse(failure.Reason.Contains(sentinelSecret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void CreateConnectionString_UnsupportedApplicationName_RejectsWithoutLeakingConnectionSecret()
    {
        const string sentinelSecret = "change32-app-name-secret";
        PostgresIntegrationSettings settings = GetSettings(
            $"Host=localhost;Username=test;Password={sentinelSecret};Database=immich_reversegeo_test_unit");

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
            settings.CreateConnectionString(settings.DatabaseName, "bad application name"));

        Assert.IsFalse(exception.Message.Contains(sentinelSecret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void CreateConnectionString_NonAsciiOrOversizedApplicationName_IsRejected()
    {
        PostgresIntegrationSettings settings = GetSettings(
            "Host=localhost;Username=test;Database=immich_reversegeo_test_unit");

        Assert.ThrowsExactly<ArgumentException>(() =>
            settings.CreateConnectionString(settings.DatabaseName, "café"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            settings.CreateConnectionString(settings.DatabaseName, new string('a', 64)));
    }

    private static PostgresIntegrationSettings GetSettings(string connectionString)
    {
        PostgresIntegrationSettingsResult result = PostgresIntegrationSettings.Parse(connectionString);
        return (result as PostgresIntegrationSettingsAvailable)?.Settings
            ?? throw new AssertFailedException("Expected valid PostgreSQL integration settings.");
    }
}
