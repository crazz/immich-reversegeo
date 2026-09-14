using Npgsql;

namespace ImmichReverseGeo.Tests.CrossProcessRunExclusion;

[TestClass]
public sealed class PostgresWorkerEnvironmentTests
{
    [TestMethod]
    public void Create_RoundTripsSpecialPasswordAndConnectionTarget()
    {
        string source = new NpgsqlConnectionStringBuilder
        {
            Host = "host",
            Port = 5433,
            Username = "user",
            Password = "semi;quote\"value",
            Database = "database"
        }.ConnectionString;

        PostgresWorkerEnvironment environment = PostgresWorkerEnvironment.Create(source, "change32_owner_01");
        var parsed = new NpgsqlConnectionStringBuilder(environment.CanonicalConnectionString);

        Assert.AreEqual("host", parsed.Host);
        Assert.AreEqual(5433, parsed.Port);
        Assert.AreEqual("user", parsed.Username);
        Assert.AreEqual("semi;quote\"value", parsed.Password);
        Assert.AreEqual("database", parsed.Database);
        Assert.AreEqual("change32_owner_01", parsed.ApplicationName);
        var effective = new NpgsqlConnectionStringBuilder(PostgresWorkerEnvironment.BuildProductionConnectionString(environment.Values));
        Assert.AreEqual("host", effective.Host);
        Assert.AreEqual(5433, effective.Port);
        Assert.AreEqual("user", effective.Username);
        Assert.AreEqual("semi;quote\"value", effective.Password);
        Assert.AreEqual("database", effective.Database);
        CollectionAssert.Contains(environment.VariablesToClear.ToArray(), "PGPASSWORD");
        CollectionAssert.Contains(environment.VariablesToClear.ToArray(), "DB_PASSWORD");
    }

    [TestMethod]
    public void Create_RejectsNonDefaultSslWithoutRenderingTheConnectionString()
    {
        PostgresIntegrationSetupException error = Assert.ThrowsExactly<PostgresIntegrationSetupException>(
            () => PostgresWorkerEnvironment.Create("Host=host;Username=user;Password=secret;Database=db;SSL Mode=Require", "change32_owner_02"));

        Assert.IsFalse(error.Message.Contains("secret", StringComparison.Ordinal));
        StringAssert.Contains(error.Message, "transport, authentication, or security setting");
    }

    [TestMethod]
    public void Create_RejectsUnsafeApplicationName()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => PostgresWorkerEnvironment.Create("Host=host;Username=user;Password=password;Database=db", "change32.owner"));
    }
}
