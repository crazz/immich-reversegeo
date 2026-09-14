using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
[DoNotParallelize]
public sealed class ProcessFailureMatrixPreflightTests
{
    [TestMethod]
    [DataRow(0, "")]
    [DataRow(0, " ")]
    [DataRow(0, "STANDARD")]
    [DataRow(0, "web_only")]
    [DataRow(0, "matrix-secret-password-payload")]
    [DataRow(1, "")]
    [DataRow(1, " ")]
    [DataRow(1, "1")]
    [DataRow(1, "02")]
    [DataRow(1, "+2")]
    [DataRow(1, "2 ")]
    [DataRow(1, "matrix-secret-password-payload")]
    [DataRow(2, "duplicate")]
    [DataRow(2, "residual")]
    [DataRow(2, "assigned")]
    public async Task ActualApphost_InvalidSelectionExitsTwoBeforeHostOrWorkerOutput(int selection, string value)
    {
        var lease = new WorkerProcessFixtureLease();
        await using (lease)
        {
            Directory.CreateDirectory(Path.Combine(lease.Root, "data"));
            Directory.CreateDirectory(Path.Combine(lease.Root, "config"));
            await File.WriteAllTextAsync(Path.Combine(lease.Root, "data", "canary"), "data-unchanged");
            await File.WriteAllTextAsync(Path.Combine(lease.Root, "config", "canary"), "config-unchanged");
            var before = Snapshot(lease.Root);
            var entry = selection switch
            {
                0 => FixtureApplicationEntry.InvalidDeployment,
                1 => FixtureApplicationEntry.InvalidProtocol,
                _ => FixtureApplicationEntry.InvalidPrivateArguments
            };
            var process = await lease.StartApplicationAsync(entry, value);
            Assert.AreEqual(2, await MatrixWait.ForAsync(process.CompleteAsync(), "prehost/invalid-selection-completion"));
            Assert.AreEqual(0, (await process.StandardOutput).Length, "Pre-host rejection has no Ready, terminal or domain output.");
            string stderr = Encoding.UTF8.GetString(await process.StandardError);
            Assert.IsTrue(stderr.Length is > 0 and < 512, stderr);
            Assert.IsFalse(stderr.Contains("matrix-secret", StringComparison.Ordinal));
            Assert.IsFalse(stderr.Contains("ImmichReverseGeo.Lifecycle", StringComparison.Ordinal));
            Assert.IsFalse(stderr.Contains(lease.Root, StringComparison.Ordinal));
            if (selection == 0)
            {
                Assert.AreEqual(DeploymentModeResolver.InvalidModeDiagnostic + Environment.NewLine, stderr);
            }
            else
            {
                Assert.IsFalse(stderr.Contains(InternalWorkerProtocolVersionSelector.EnvironmentVariableName, StringComparison.Ordinal));
                StringAssert.Contains(stderr, "worker-exit-summary outcome=invalid-input");
            }
            CollectionAssert.AreEqual(before, Snapshot(lease.Root));
            Assert.IsTrue(lease.HasExited);
            Assert.AreEqual(0, lease.WrittenInput.Length);
            Assert.IsNull(lease.Session, "No controller/session owner is fabricated for an application rejected before hosting.");
        }
        AssertDisposed(lease);
    }

    [TestMethod]
    public async Task ActualApphost_ValidWorkerSelectionWithUnusableConfigurationExitsFive()
    {
        var lease = new WorkerProcessFixtureLease();
        await using (lease)
        {
            var process = await lease.StartApplicationAsync(FixtureApplicationEntry.BlockedConfiguration);
            await process.SendAndCloseAsync([]);
            int exit = await MatrixWait.ForAsync(process.CompleteAsync(), "startup/unusable-configuration-finality");
            string stderr = Encoding.UTF8.GetString(await process.StandardError);
            Assert.AreEqual(5, exit, stderr);
            Assert.AreEqual(0, (await process.StandardOutput).Length, "Startup failure cannot fabricate Ready or terminal.");
            StringAssert.Contains(stderr, "worker-exit-summary outcome=infrastructure-failure phase=startup");
            Assert.IsFalse(stderr.Contains(lease.Root, StringComparison.Ordinal));
            Assert.IsFalse(stderr.Contains("owned-unusable-configuration", StringComparison.Ordinal));
            Assert.AreEqual("{]owned-unusable-configuration", await File.ReadAllTextAsync(Path.Combine(lease.Root, "appsettings.json")));
            CollectionAssert.AreEqual(new[] { 6602, 6604, 6605 }, RoleEvents(stderr));
        }
        AssertDisposed(lease);
    }

    public static IEnumerable<object[]> InputRows()
    {
        foreach (var version in new[] { InternalWorkerProtocolVersion.V1, InternalWorkerProtocolVersion.V2 })
        {
            foreach (string fault in new[] { "json", "version", "direction", "category", "sequence", "empty-payload", "trigger" })
            {
                yield return [version, WorkerJobKind.ProcessAssets, fault];
            }
        }
        yield return [InternalWorkerProtocolVersion.V2, WorkerJobKind.ProcessAssets, "unknown-envelope"];
        yield return [InternalWorkerProtocolVersion.V2, WorkerJobKind.ProcessAssets, "unknown-job-kind"];
        foreach (var kind in new[] { WorkerJobKind.ProcessAssets, WorkerJobKind.CoordinateLookup, WorkerJobKind.CacheMutation })
        {
            yield return [InternalWorkerProtocolVersion.V2, kind, "unknown-payload"];
        }
        foreach (string fault in new[] { "empty-payload", "latitude", "longitude" })
        {
            yield return [InternalWorkerProtocolVersion.V2, WorkerJobKind.CoordinateLookup, fault];
        }
        foreach (string fault in new[] { "empty-payload", "source", "operation", "iso3", "unknown-country" })
        {
            yield return [InternalWorkerProtocolVersion.V2, WorkerJobKind.CacheMutation, fault];
        }
    }

    [TestMethod]
    [DynamicData(nameof(InputRows))]
    public async Task ActualWorker_InvalidControllerInputExitsTwoWithoutAcceptedJobOrDomainWork(
        InternalWorkerProtocolVersion version, WorkerJobKind kind, string fault)
    {
        var lease = new WorkerProcessFixtureLease();
        await using (lease)
        {
            var process = await lease.StartApplicationAsync(version == InternalWorkerProtocolVersion.V1
                ? FixtureApplicationEntry.WorkerV1 : FixtureApplicationEntry.WorkerV2);
            byte[] frame = InvalidInput(lease, version, kind, fault);
            await process.SendAndCloseAsync(frame);
            int exit = await MatrixWait.ForAsync(process.CompleteAsync(), "preaccept/" + fault + "-native-finality");
            string stderr = Encoding.UTF8.GetString(await process.StandardError);
            Assert.AreEqual(2, exit, stderr);
            string[] lines = Encoding.UTF8.GetString(await process.StandardOutput).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(1, lines.Length, "Only readiness is allowed before rejecting the request. " + stderr);
            if (version == InternalWorkerProtocolVersion.V1)
            {
                var ready = WorkerProtocolCodec.Parse(Encoding.UTF8.GetBytes(lines[0]));
                Assert.IsTrue(ready.IsSuccess);
                Assert.IsInstanceOfType<ReadyPayload>(ready.Event!.Payload);
                Assert.IsNull(ready.Event.RunId);
            }
            else
            {
                var ready = WorkerJobProtocolCodec.Parse(Encoding.UTF8.GetBytes(lines[0]));
                Assert.IsTrue(ready.IsSuccess);
                Assert.IsInstanceOfType<WorkerJobReadyPayload>(ready.Message!.Payload);
                Assert.IsNull(ready.Message.JobId);
            }
            StringAssert.Contains(stderr, "worker-exit-summary outcome=invalid-input phase=input");
            CollectionAssert.AreEqual(new[] { 6602, 6603, 6604, 6605 }, RoleEvents(stderr));
            Assert.IsFalse(stderr.Contains("matrix-secret", StringComparison.Ordinal));
            Assert.IsFalse(stderr.Contains(lease.Request.RunId.ToString("D"), StringComparison.Ordinal));
            // V1 initializes its empty skipped repository before Ready. Its ordinary
            // startup log is outside the closed lifecycle catalog being redacted.
            string lifecycle = string.Join("\n", Regex.Matches(stderr,
                @"(?:info|warn|fail): ImmichReverseGeo\.Lifecycle\[\d+\]\r?\n[^\r\n]*")
                .Select(match => match.Value));
            Assert.IsFalse(lifecycle.Contains(lease.Root, StringComparison.Ordinal));
            Assert.IsFalse(lifecycle.Contains("matrix-secret", StringComparison.Ordinal));
            string[] files = Directory.GetFiles(lease.Root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(lease.Root, path)).Order(StringComparer.Ordinal).ToArray();
            if (version == InternalWorkerProtocolVersion.V1)
            {
                CollectionAssert.AreEqual(new[] { Path.Combine("data", "skipped.db") }, files);
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(lease.Root, "data", "skipped.db"),
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString());
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM skipped_assets";
                Assert.AreEqual(0L, await command.ExecuteScalarAsync(), "Rejected input adds no skipped assets.");
            }
            else
            {
                Assert.AreEqual(0, files.Length, "V2 rejection cannot initialize any job-specific repository.");
            }
            CollectionAssert.AreEqual(frame, lease.WrittenInput.ToArray());
        }
        AssertDisposed(lease);
    }

    private static byte[] InvalidInput(WorkerProcessFixtureLease lease, InternalWorkerProtocolVersion version,
        WorkerJobKind kind, string fault)
    {
        if (fault == "json")
        {
            return Encoding.UTF8.GetBytes("{]matrix-secret-password-payload\n");
        }
        byte[] canonical;
        if (version == InternalWorkerProtocolVersion.V1)
        {
            canonical = WorkerProtocolCodec.SerializeControllerInput(new WorkerProtocolControllerMessage(
                WorkerProtocolV1.RequestCategory, WorkerProtocolV1.ExecuteType, 1, DateTimeOffset.UtcNow,
                lease.Request.RunId, new ExecuteRequestPayload(lease.Request)));
        }
        else
        {
            var dispatch = ProcessFailureMatrixFixtureTests.Dispatch(lease, kind);
            WorkerJobControllerPayload payload = dispatch switch
            {
                ProcessAssetsWorkerJobDispatch p => new ProcessAssetsExecutePayload(p.Request),
                CoordinateLookupWorkerJobDispatch p => new CoordinateLookupExecutePayload(p.Request),
                CacheMutationWorkerJobDispatch p => new CacheMutationExecutePayload(p.Request),
                _ => throw new AssertFailedException("Unknown closed preflight kind.")
            };
            canonical = WorkerJobProtocolCodec.SerializeControllerInput(new WorkerJobControllerMessage(
                WorkerJobProtocolV2.RequestCategory, WorkerJobProtocolV2.ExecuteType, 1, DateTimeOffset.UtcNow,
                lease.Request.RunId, kind, payload));
        }
        var root = JsonNode.Parse(canonical)!.AsObject();
        var body = root["payload"]!.AsObject();
        switch (fault)
        {
            case "version": root["version"] = 99; break;
            case "direction": root["direction"] = "worker-to-controller"; break;
            case "category": root["category"] = "matrix-secret-category"; break;
            case "sequence": root["sequence"] = 2; break;
            case "empty-payload": root["payload"] = new JsonObject(); break;
            case "trigger": body["trigger"] = "matrix-secret-trigger"; break;
            case "unknown-envelope": root["matrix-secret-extra"] = "payload"; break;
            case "unknown-job-kind": root["jobKind"] = "matrix-secret-kind"; break;
            case "unknown-payload": body["matrix-secret-extra"] = "payload"; break;
            case "latitude": body["latitude"] = 91; break;
            case "longitude": body["longitude"] = 181; break;
            case "source": body["source"] = "matrix-secret-source"; break;
            case "operation": body["operation"] = "matrix-secret-operation"; break;
            case "iso3": body["iso3"] = "che"; break;
            case "unknown-country": body["iso3"] = "ZZZ"; break;
            default: throw new AssertFailedException("Unknown closed input fault.");
        }
        return Encoding.UTF8.GetBytes(root.ToJsonString() + "\n");
    }

    private static int[] RoleEvents(string stderr) => Regex.Matches(stderr, @"ImmichReverseGeo\.Lifecycle\[(\d+)\]")
        .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))
        .Order(StringComparer.Ordinal).ToArray();

    private static void AssertDisposed(WorkerProcessFixtureLease lease)
    {
        Assert.IsTrue(lease.HasExited);
        Assert.AreEqual(1, lease.ProcessDisposeCalls);
        Assert.IsFalse(lease.ForcedCleanup);
        Assert.IsFalse(lease.IsRegistered);
        Assert.IsFalse(Directory.Exists(lease.Root));
    }
}
