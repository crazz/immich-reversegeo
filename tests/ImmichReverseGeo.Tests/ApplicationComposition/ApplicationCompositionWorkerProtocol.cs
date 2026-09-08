using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

internal static class ApplicationCompositionWorkerProtocol
{
    internal static InternalWorkerProtocolVersion SelectedVersion(
        ChildProcessStartDescriptor descriptor) => descriptor.EnvironmentPolicy switch
        {
            ChildProcessEnvironmentPolicy.InheritCurrentAndRemoveReservedProtocolVersion =>
                InternalWorkerProtocolVersion.V1,
            ChildProcessEnvironmentPolicy.InheritCurrentAndSetReservedProtocolVersionV2 =>
                InternalWorkerProtocolVersion.V2,
            _ => throw new AssertFailedException(
                "A production child descriptor must explicitly select v1 or v2.")
        };

    internal static byte[] ReadyFrame(ChildProcessStartDescriptor descriptor)
    {
        if (SelectedVersion(descriptor) == InternalWorkerProtocolVersion.V1)
        {
            return SessionTestSupport.Frame(
                WorkerProtocolMapper.Ready(1, SessionTestSupport.Start));
        }

        return Frame(WorkerJobProtocolCodec.Serialize(WorkerJobProtocolMapper.Ready(
            1,
            SessionTestSupport.Start,
            new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets]))));
    }

    internal static byte[] ProcessingFrame(
        ChildProcessStartDescriptor descriptor,
        ProcessingEvent processingEvent,
        long sequence)
    {
        WorkerProtocolEvent v1Event = WorkerProtocolMapper.Map(
            processingEvent,
            sequence,
            SessionTestSupport.Start);
        if (SelectedVersion(descriptor) == InternalWorkerProtocolVersion.V1)
        {
            return SessionTestSupport.Frame(v1Event);
        }

        WorkerJobOutputMessage v2Message = ProcessAssetsWorkerJobProjection.MapV1(
            processingEvent.Request,
            v1Event);
        return Frame(WorkerJobProtocolCodec.Serialize(v2Message));
    }

    private static byte[] Frame(byte[] objectBytes)
    {
        var frame = new byte[objectBytes.Length + 1];
        objectBytes.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';
        return frame;
    }
}
