using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ConfigServiceTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup() => _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(int.MinValue)]
    public async Task NonPositiveBatchSize_ProducesSafePassFailureWithoutAssetDispositions(int batchSize)
    {
        Directory.CreateDirectory(_tempDir);
        string document = $$"""{"processing":{"batchSize":{{batchSize}}},"private-canary":"not-for-output"}""";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "settings.json"), document);
        var service = new ConfigService(NullLogger<ConfigService>.Instance, _tempDir);
        var fixture = new ExecutorFixture().EnableReporter().EnableCount(1).EnableSnapshots();
        fixture.ConfigBehavior = ((IProcessingRunConfiguration)service).GetConfigAsync;

        var result = await fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(ExecutorFixture.Bound);

        fixture.AssertTerminal(result);
        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual("Batch Size must be positive.", result.FailureMessage);
        ExecutorAssertions.Counts(result, 0, 0, 0, 0);
        Assert.AreEqual(1, fixture.ConfigCalls);
        Assert.AreEqual(0, fixture.BatchCalls);
        Assert.AreEqual(0, fixture.Resolutions.Count);
        Assert.AreEqual(0, fixture.WriteAttempts);
        Assert.AreEqual(0, fixture.SkippedInsertAttempts);
        Assert.AreEqual(0, fixture.Delays.Count);
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<ProgressChanged>().Count());
        var terminal = fixture.Reporter.Events.OfType<RunFinished>().Single();
        Assert.AreSame(result, terminal.Result);
        Assert.AreEqual("Batch Size must be positive.", terminal.Result.FailureMessage);
        var fatal = fixture.Logger.Entries.Single();
        Assert.IsFalse(fatal.Exception!.ToString().Contains("not-for-output", StringComparison.Ordinal));
        Assert.AreEqual(document, await File.ReadAllTextAsync(Path.Combine(_tempDir, "settings.json")));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task PositiveBatchSize_WithNonPositiveDelayRetainsSettingsAndProcessesWithoutDelay(int delay)
    {
        var service = new ConfigService(NullLogger<ConfigService>.Instance, _tempDir);
        var update = SettingsUpdate.FromConfig(new AppConfig()) with
        {
            BatchSize = 1,
            BatchDelayMs = delay,
            MaxDegreeOfParallelism = 0,
            UseAirportInfrastructure = false
        };
        await service.SaveSettingsAsync(update);
        var fixture = new ExecutorFixture().EnableReporter().EnableCount(1).EnableSnapshots()
            .EnablePages().EnableAdmin().EnableWrite();
        fixture.ConfigBehavior = ((IProcessingRunConfiguration)service).GetConfigAsync;
        fixture.SetPages([ExecutorFixture.Asset(1)], []);

        var result = await fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(ExecutorFixture.Bound);

        ExecutorAssertions.Completed(result, 1, 1, 0, 0);
        Assert.AreEqual(0, fixture.Delays.Count);
        CollectionAssert.AreEqual(new[] { 1, 1 }, fixture.BatchSizes.ToArray());
        var saved = await service.GetConfigAsync();
        Assert.AreEqual(1, saved.Processing.BatchSize);
        Assert.AreEqual(delay, saved.Processing.BatchDelayMs);
        Assert.AreEqual(0, saved.Processing.MaxDegreeOfParallelism);
    }

    [TestMethod]
    public async Task GetConfig_NoFile_ReturnsDefaults()
    {
        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var cfg = await svc.GetConfigAsync();
        Assert.AreEqual("0 * * * *", cfg.Schedule.Cron);
        Assert.AreEqual(50, cfg.Processing.BatchSize);
        Assert.IsTrue(cfg.Processing.UseAirportInfrastructure);
        Assert.AreEqual(0, cfg.Processing.CityResolver.CountryOverrides.Count);
        Assert.AreEqual(AppearanceModes.Auto, cfg.Appearance.Mode);
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTrips()
    {
        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var cfg = await svc.GetConfigAsync();
        cfg.Processing.BatchSize = 99;
        cfg.Processing.UseAirportInfrastructure = false;
        await svc.SeedConfigAsync(cfg);

        var svc2 = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var loaded = await svc2.GetConfigAsync();
        Assert.AreEqual(99, loaded.Processing.BatchSize);
        Assert.IsFalse(loaded.Processing.UseAirportInfrastructure);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task SaveGroups_WithEnvironmentCredentialsAndDeploymentMode_DoNotPersistThem()
    {
        string[] names = ["IMMICH_REVERSEGEO_MODE", "DB_HOST", "DB_PORT", "DB_USERNAME", "DB_PASSWORD", "DB_DATABASE_NAME"];
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (string name in names)
            {
                Environment.SetEnvironmentVariable(name, "environment-only-" + name);
            }

            var service = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
            await service.SeedConfigAsync(new AppConfig());

            string settings = await File.ReadAllTextAsync(Path.Combine(_tempDir, "settings.json"));
            Assert.IsFalse(settings.Contains("deploymentMode", StringComparison.OrdinalIgnoreCase));
            foreach (string name in names)
            {
                Assert.IsFalse(settings.Contains(name, StringComparison.OrdinalIgnoreCase));
                Assert.IsFalse(settings.Contains("environment-only-" + name, StringComparison.Ordinal));
            }
        }
        finally
        {
            foreach (var entry in previous)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }
    }

    [TestMethod]
    public async Task GetConfig_LegacySettingsWithoutCityResolver_AddsCompatibleDefaults()
    {
        Directory.CreateDirectory(_tempDir);
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "schedule": {
                "cron": "0 * * * *",
                "enabled": true
              },
              "processing": {
                "batchSize": 25,
                "batchDelayMs": 250,
                "maxDegreeOfParallelism": 2,
                "useAirportInfrastructure": false,
                "verboseLogging": true
              }
            }
            """);

        byte[] original = await File.ReadAllBytesAsync(settingsPath);
        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);

        var loaded = await svc.GetConfigAsync();

        Assert.IsNotNull(loaded.Processing.CityResolver);
        Assert.IsNotNull(loaded.Processing.CityResolver.DefaultProfile);
        Assert.AreEqual(0, loaded.Processing.CityResolver.DefaultProfile.PreferredSubtypes.Count);
        Assert.AreEqual(string.Empty, loaded.Processing.CityResolver.DefaultProfile.TieBreakMode);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(settingsPath));
        Assert.IsNotNull(loaded.Processing.CityResolver.CountryOverrides);
        Assert.AreEqual(0, loaded.Processing.CityResolver.CountryOverrides.Count);
        Assert.AreEqual(25, loaded.Processing.BatchSize);
        Assert.IsFalse(loaded.Processing.UseAirportInfrastructure);
        Assert.IsTrue(loaded.Processing.VerboseLogging);
        Assert.AreEqual(AppearanceModes.Auto, loaded.Appearance.Mode);
    }

    [TestMethod]
    [DoNotParallelize]
    public void GetDbSettings_ReadsEnvVars()
    {
        var oldHost = Environment.GetEnvironmentVariable("DB_HOST");
        var oldPort = Environment.GetEnvironmentVariable("DB_PORT");
        var oldUsername = Environment.GetEnvironmentVariable("DB_USERNAME");
        var oldPassword = Environment.GetEnvironmentVariable("DB_PASSWORD");
        var oldDatabaseName = Environment.GetEnvironmentVariable("DB_DATABASE_NAME");

        try
        {
            Environment.SetEnvironmentVariable("DB_HOST", "testhost");
            Environment.SetEnvironmentVariable("DB_PORT", "5433");
            Environment.SetEnvironmentVariable("DB_USERNAME", "testuser");
            Environment.SetEnvironmentVariable("DB_PASSWORD", "testpass");
            Environment.SetEnvironmentVariable("DB_DATABASE_NAME", "testdb");

            var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
            var db = svc.GetDbSettings();

            Assert.AreEqual("testhost", db.Host);
            Assert.AreEqual(5433, db.Port);
            Assert.AreEqual("testuser", db.Username);
            Assert.AreEqual("testdb", db.Database);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DB_HOST", oldHost);
            Environment.SetEnvironmentVariable("DB_PORT", oldPort);
            Environment.SetEnvironmentVariable("DB_USERNAME", oldUsername);
            Environment.SetEnvironmentVariable("DB_PASSWORD", oldPassword);
            Environment.SetEnvironmentVariable("DB_DATABASE_NAME", oldDatabaseName);
        }
    }
}
