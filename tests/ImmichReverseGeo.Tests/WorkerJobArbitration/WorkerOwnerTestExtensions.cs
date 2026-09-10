using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImmichReverseGeo.Web.Services;

internal static class WorkerOwnerTestExtensions
{
    internal static WorkerJobBusyMetadata RequireWorker(
        this ExclusiveHeavyOwnerBusyMetadata? owner) =>
        Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.Worker>(owner).Job;

    internal static WorkerJobOwnerSnapshot RequireWorker(
        this ExclusiveHeavyOwnerSnapshot? owner) =>
        Assert.IsInstanceOfType<ExclusiveHeavyOwnerSnapshot.Worker>(owner).Job;
}
