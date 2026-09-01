using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol;

[TestClass]
public class ControllerInputBoundaryGuardsTests
{
    [TestMethod]
    public void ControllerInput_UsesOnlyTheExistingRunIdentityAndTriggerContract()
    {
        var runId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var request = new ProcessingRunRequest(runId, ProcessingRunTrigger.Manual);
        var payload = new ExecuteRequestPayload(request);
        var message = new WorkerProtocolControllerMessage(WorkerProtocolV1.RequestCategory, WorkerProtocolV1.ExecuteType, 1, new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero), runId, payload);

        Assert.AreEqual(runId, request.RunId, "controller-input:boundary:request-run-id");
        Assert.AreEqual(ProcessingRunTrigger.Manual, request.Trigger, "controller-input:boundary:request-trigger");
        Assert.AreEqual(request, ((ExecuteRequestPayload)message.Payload).Request, "controller-input:boundary:execute-request");
        CollectionAssert.AreEqual(new[] { ProcessingRunTrigger.Manual, ProcessingRunTrigger.Scheduled, ProcessingRunTrigger.RunOnce }, Enum.GetValues<ProcessingRunTrigger>(), "controller-input:boundary:trigger-vocabulary");
    }

    [TestMethod]
    public void WorkerProtocolCodec_NullSerializeResolvesOriginalEventOverload()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => WorkerProtocolCodec.Serialize(null!), "controller-input:boundary:event-overload");
    }

    [TestMethod]
    public void ControllerInput_ExplicitSourceBoundaryDeclarationsRemainNarrow()
    {
        var root = FindRoot();
        var files = new[] { "src/ImmichReverseGeo.Core/Models/ProcessingRunRequest.cs", "src/ImmichReverseGeo.Core/Models/ProcessingRunTrigger.cs", "src/ImmichReverseGeo.Core/WorkerProtocol/WorkerProtocolV1.cs", "src/ImmichReverseGeo.Core/WorkerProtocol/WorkerProtocolControllerInput.cs", "src/ImmichReverseGeo.Core/WorkerProtocol/WorkerProtocolCodec.cs" };
        var source = string.Join("\n", files.Select(path => File.ReadAllText(Path.Combine(root, path))));
        var request = File.ReadAllText(Path.Combine(root, files[0]));
        var trigger = File.ReadAllText(Path.Combine(root, files[1]));

        Assert.AreEqual(1, Count(request, "public Guid RunId { get; }"), "controller-input:source:request-run-id");
        Assert.AreEqual(1, Count(request, "public ProcessingRunTrigger Trigger { get; }"), "controller-input:source:request-trigger");
        Assert.AreEqual(1, Count(trigger, "Manual,"), "controller-input:source:trigger-manual");
        Assert.AreEqual(1, Count(trigger, "Scheduled,"), "controller-input:source:trigger-scheduled");
        Assert.AreEqual(1, Count(trigger, "RunOnce"), "controller-input:source:trigger-run-once");
        foreach (var token in new[] { "JobId", "public Settings", "public Credentials", "public WorkSet", "public Reason", "public Token", "public Deadline", "public CommandId", "public Replacement", "ExecuteAccepted", "CancelAccepted", "Ack", "Console.", "ReadLine", "StandardInput", "stdin", "ProcessStartInfo", "Host", "Executor", "ProcessingState", "ExitCode", "CancellationTokenSource", "worker job" })
        {
            Assert.IsFalse(source.Contains(token, StringComparison.Ordinal), "controller-input:source:forbidden:" + token);
        }
    }

    private static int Count(string value, string token) => value.Split(token, StringSplitOptions.None).Length - 1;

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "immich-reversegeo.slnx")))
            {
                return directory.FullName;
            }
        }

        Assert.Fail("controller-input:source:root");
        return string.Empty;
    }
}
