using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;

namespace ImmichReverseGeo.Tests.WorkerNdjsonOutput;

[TestClass]
public sealed class WorkerNdjsonOutputBoundaryGuardsTests
{
    private static readonly string[] StructuralScanRoots =
    [
        "src/ImmichReverseGeo.Core",
        "src/ImmichReverseGeo.Web/WorkerHost",
        "src/ImmichReverseGeo.Web/Services",
        "src/ImmichReverseGeo.Web/Composition",
        "src/ImmichReverseGeo.Overture/Services",
        "src/ImmichReverseGeo.Gadm/Services"
    ];

    [TestMethod]
    public void WorkerNdjsonOutput_HasOneManagedStdoutOwnerAndNoExpandedWorkerResponsibilities()
    {
        var root = FindRoot();
        var sourceFiles = StructuralScanRoots
            .Select(path => Path.Combine(root, path))
            .SelectMany(path =>
            {
                Assert.IsTrue(Directory.Exists(path), "worker-ndjson:source:scan-root-exists:" + path);
                return Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories);
            })
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var source = string.Join("\n", sourceFiles.Select(File.ReadAllText));
        var emitter = Read(root, "src/ImmichReverseGeo.Web/WorkerHost/WorkerNdjsonOutput/WorkerNdjsonEmitter.cs");
        var reporter = Read(root, "src/ImmichReverseGeo.Web/WorkerHost/WorkerNdjsonOutput/WorkerNdjsonProcessingEventReporter.cs");
        var composition = Read(root, "src/ImmichReverseGeo.Web/Composition/InternalWorkerServiceCollectionExtensions.cs");
        var stdinExemptBoundarySource = emitter + "\n" + reporter;
        var fullBoundarySource = emitter + "\n" + reporter + "\n" + composition;
        Assert.AreEqual(6, StructuralScanRoots.Length, "worker-ndjson:source:six-boundary-roots");
        var host = Read(root, "src/ImmichReverseGeo.Web/WorkerHost/InternalWorkerHost.cs");
        var lifecycle = Read(root, "src/ImmichReverseGeo.Web/WorkerHost/InternalWorkerLifecycleService.cs");

        Assert.IsTrue(sourceFiles.Any(path => path.EndsWith("InternalWorkerServiceCollectionExtensions.cs", StringComparison.Ordinal)), "worker-ndjson:source:composition-included");
        var productionQueueCapacity = WorkerNdjsonEmitter.ProductionQueueCapacity;
        Assert.AreEqual(256, productionQueueCapacity, "worker-ndjson:queue:production-capacity");
        Assert.AreEqual(1, Count(emitter, "WorkerProtocolEventStreamValidator _validator = new();"), "worker-ndjson:canonical:one-validator-construction");
        Assert.AreEqual(1, Count(emitter, "WorkerProtocolMapper.Ready("), "worker-ndjson:canonical:one-ready-map");
        Assert.AreEqual(1, Count(emitter, "WorkerProtocolMapper.Map("), "worker-ndjson:canonical:one-event-map");
        Assert.AreEqual(1, Count(emitter, "WorkerProtocolCodec.Serialize("), "worker-ndjson:canonical:one-codec");
        Assert.AreEqual(1, Count(emitter, "_validator.Validate("), "worker-ndjson:canonical:one-validator");
        Assert.AreEqual(1, Count(emitter, "Channel.CreateBounded<EmissionCandidate>"), "worker-ndjson:queue:one-bounded-channel");
        Assert.AreEqual(1, Count(emitter, "FullMode = BoundedChannelFullMode.Wait"), "worker-ndjson:queue:wait-on-full");
        Assert.AreEqual(1, Count(emitter, "SingleReader = true"), "worker-ndjson:queue:single-reader");
        Assert.AreEqual(1, Count(emitter, "SingleWriter = false"), "worker-ndjson:queue:multi-producer");
        var enqueue = Slice(emitter, "private async ValueTask EnqueueAsync(", "private async Task ConsumeAsync()");
        Assert.AreEqual(1, Count(enqueue, "UnsafeRegister("), "worker-ndjson:cancellation:admission-only-registration");
        AssertOrder(enqueue, "candidate.CancelAdmission()", "_queue.Writer.TryWrite(candidate)", "candidate.TransferToWriter()");
        var admissionLock = enqueue.LastIndexOf("lock (_stateGate)", StringComparison.Ordinal);
        var admissionLockBody = ExtractBraceBlock(enqueue, admissionLock);
        AssertOrder(admissionLockBody,
            "candidate.CancelAdmission()",
            "candidate.ThrowIfAdmissionCancelled(cancellationToken)",
            "if (_queue.Writer.TryWrite(candidate))",
            "candidate.TransferToWriter();",
            "_runStartedAccepted = true;",
            "_terminalAccepted = true;",
            "_intakeClosed = true;",
            "return;");
        Assert.AreEqual(1, Count(admissionLockBody, "candidate.CancelAdmission()"), "worker-ndjson:cancellation:one-cancel-inside-lock");
        Assert.AreEqual(1, Count(admissionLockBody, "candidate.TransferToWriter();"), "worker-ndjson:cancellation:one-transfer-inside-lock");
        Assert.AreEqual(1, Count(emitter, "_nextSequence + 1"), "worker-ndjson:order:sequence-owned-by-writer");
        AssertOrder(emitter,
            "WorkerProtocolMapper.Map(",
            "WorkerProtocolCodec.Serialize(",
            "_validator.Validate(",
            "_stdout.WriteAsync(",
            "_stdout.FlushAsync(");
        Assert.AreEqual(1, Count(source, "Console.OpenStandardOutput()"), "worker-ndjson:stdout:single-owner");
        Assert.AreEqual(1, Count(emitter, "Console.OpenStandardOutput()"), "worker-ndjson:stdout:owner-is-emitter");
        Assert.IsFalse(source.Contains("IWorkerNdjsonProtocolAdapter", StringComparison.Ordinal), "worker-ndjson:protocol:no-adapter-seam");
        Assert.IsFalse(source.Contains("WorkerNdjsonProtocolAdapter", StringComparison.Ordinal), "worker-ndjson:protocol:no-adapter-implementation");
        foreach (var token in new[] { "Console.Out", "Console.SetOut", "Console.Write(", "Console.WriteLine", "TextWriter.Write" })
        {
            Assert.IsFalse(source.Contains(token, StringComparison.Ordinal), "worker-ndjson:stdout:forbidden:" + token);
        }

        foreach (var token in new[] { "ReadLine", "StandardInput", "Console.OpenStandardInput" })
        {
            Assert.IsFalse(stdinExemptBoundarySource.Contains(token, StringComparison.Ordinal), "worker-ndjson:stdin-exempt-boundary:forbidden:" + token);
        }

        foreach (var token in new[] { "ExitCode", "ProcessStartInfo", "progress-coalesc", "Coalesce", "IWorkerNdjsonProtocolAdapter", "WorkerNdjsonProtocolAdapter", "TestHook", "ForTest", "Callback" })
        {
            Assert.IsFalse(fullBoundarySource.Contains(token, StringComparison.Ordinal), "worker-ndjson:full-boundary:forbidden:" + token);
        }

        var emitterSubsystem = emitter + "\n" + reporter;
        foreach (var token in new[] { "Func<", "Action<", " delegate ", "TestHook", "ForTest", "Callback" })
        {
            Assert.IsFalse(emitterSubsystem.Contains(token, StringComparison.Ordinal), "worker-ndjson:test-seam:forbidden:" + token);
        }

        Assert.AreEqual(1, Count(emitter, "CancellationTokenSource _lifetimeCancellation = new();"), "worker-ndjson:cancellation:one-owned-writer-lifetime");
        Assert.AreEqual(0, Count(emitter, "CreateLinkedTokenSource"), "worker-ndjson:cancellation:no-producer-linked-lifetime");
        Assert.AreEqual(1, Count(lifecycle, "CreateLinkedTokenSource"), "worker-ndjson:cancellation:one-lifecycle-linkage");
        Assert.IsTrue(host.Contains("builder.Logging.ClearProviders()", StringComparison.Ordinal), "worker-ndjson:stderr:clear-providers");
        Assert.IsTrue(host.Contains("LogToStandardErrorThreshold = LogLevel.Trace", StringComparison.Ordinal), "worker-ndjson:stderr:console-routing");
    }

    private static string Read(string root, string path) => File.ReadAllText(Path.Combine(root, path));

    private static int Count(string value, string token) => value.Split(token, StringSplitOptions.None).Length - 1;

    private static string Slice(string value, string startToken, string endToken)
    {
        var start = value.IndexOf(startToken, StringComparison.Ordinal);
        var end = value.IndexOf(endToken, start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start, "worker-ndjson:slice:" + startToken);
        return value[start..end];
    }

    private static string ExtractBraceBlock(string value, int lockIndex)
    {
        Assert.IsTrue(lockIndex >= 0, "worker-ndjson:lock:present");
        var start = value.IndexOf('{', lockIndex);
        Assert.IsTrue(start >= 0, "worker-ndjson:lock:open-brace");
        var depth = 0;
        for (var index = start; index < value.Length; index++)
        {
            if (value[index] == '{')
            {
                depth++;
            }
            else if (value[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return value[start..(index + 1)];
                }
            }
        }

        Assert.Fail("worker-ndjson:lock:unbalanced");
        return string.Empty;
    }

    private static void AssertOrder(string value, params string[] tokens)
    {
        var previous = -1;
        foreach (var token in tokens)
        {
            var current = value.IndexOf(token, StringComparison.Ordinal);
            Assert.IsTrue(current >= 0, "worker-ndjson:order:missing:" + token);
            Assert.IsTrue(current > previous, "worker-ndjson:order:wrong:" + token);
            previous = current;
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "immich-reversegeo.slnx")))
            {
                return directory.FullName;
            }
        }

        Assert.Fail("worker-ndjson:source:root");
        return string.Empty;
    }
}
