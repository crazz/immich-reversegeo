using System.Reflection;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests;

internal static class WorkerProtocolV1TestData
{
    internal static readonly Guid RunId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    internal static readonly Guid ActivityId = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
    internal static readonly DateTimeOffset Start = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset Midpoint = new(2026, 8, 29, 12, 0, 1, TimeSpan.Zero);
    internal static readonly DateTimeOffset End = new(2026, 8, 29, 12, 0, 5, TimeSpan.Zero);

    internal static WorkerProtocolEvent Ready(long sequence = 1) =>
        new(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.ReadyType, sequence, Start, null, new ReadyPayload());

    internal static WorkerProtocolEvent Started(long sequence = 2) =>
        new(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.RunStartedType, sequence, Start, RunId, new RunStartedPayload("manual", Start));

    internal static WorkerProtocolEvent Eligible(long sequence = 3) =>
        new(WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.EligibilityDeterminedType, sequence, Midpoint, RunId, new EligibilityDeterminedPayload(1));

    internal static WorkerProtocolEvent Completed(long sequence = 4) =>
        new(WorkerProtocolV1.TerminalCategory, WorkerProtocolV1.CompletedType, sequence, End, RunId, new CompletedPayload("manual", Start, End, 0, 0, 0, 0));
}

[TestClass]
public class WorkerProtocolV1Tests
{
    [TestMethod]
    public void TypedPayloads_PreserveValidFacts()
    {
        var started = new RunStartedPayload("manual", WorkerProtocolV1TestData.Start);
        var eligibility = new EligibilityDeterminedPayload(long.MaxValue);
        var progress = new ProgressChangedPayload(3, 1, 1, 1);
        var activity = new ActivityStartedPayload(WorkerProtocolV1TestData.ActivityId, "download");
        var activityEnded = new ActivityEndedPayload(WorkerProtocolV1TestData.ActivityId);
        var log = new LogEmittedPayload("warning", "message");
        var completed = new CompletedPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0);
        var cancelled = new CancelledPayload("scheduled", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0);
        var failed = new FailedPayload("run-once", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 3, 1, 1, 1, "failure");

        Assert.AreEqual("manual", started.Trigger);
        Assert.AreEqual(long.MaxValue, eligibility.EligibleCount);
        Assert.AreEqual(3L, progress.ProcessedCount);
        Assert.AreEqual(WorkerProtocolV1TestData.ActivityId, activity.ActivityId);
        Assert.AreEqual(WorkerProtocolV1TestData.ActivityId, activityEnded.ActivityId);
        Assert.AreEqual("warning", log.Level);
        Assert.IsNull(completed.FailureMessage);
        Assert.IsNull(cancelled.FailureMessage);
        Assert.AreEqual("failure", failed.FailureMessage);
    }

    [TestMethod]
    public void TypedPayloads_RejectInvalidIndependentInvariants()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new RunStartedPayload("automatic", WorkerProtocolV1TestData.Start));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new EligibilityDeterminedPayload(-1));
        Assert.ThrowsExactly<ArgumentException>(() => new ProgressChangedPayload(3, 1, 1, 0));
        Assert.ThrowsExactly<ArgumentException>(() => new ActivityStartedPayload(Guid.Empty, "download"));
        Assert.ThrowsExactly<ArgumentException>(() => new ActivityStartedPayload(WorkerProtocolV1TestData.ActivityId, " "));
        Assert.ThrowsExactly<ArgumentException>(() => new LogEmittedPayload("verbose", "message"));
        Assert.ThrowsExactly<ArgumentException>(() => new LogEmittedPayload("error", " "));
        Assert.ThrowsExactly<ArgumentException>(() => new CompletedPayload("manual", WorkerProtocolV1TestData.End, WorkerProtocolV1TestData.Start, 0, 0, 0, 0));
        Assert.ThrowsExactly<ArgumentException>(() => new FailedPayload("manual", WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.End, 0, 0, 0, 0, " "));
    }

    [TestMethod]
    public void ProtocolBoundary_HasNoForbiddenMembersReferencesOrSourceDependencies()
    {
        var assembly = typeof(WorkerProtocolCodec).Assembly;
        var protocolTypes = assembly.GetTypes().Where(type => type.Namespace == "ImmichReverseGeo.Core.WorkerProtocol").ToArray();
        var roots = protocolTypes.SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)));
        var reachableImplementations = TransitiveIlWalker.Walk(roots, assembly);

        Assert.IsTrue(IsForbiddenType(typeof(System.IO.StreamReader)));
        Assert.IsTrue(IsForbiddenType(typeof(System.IO.TextReader)));
        Assert.IsTrue(IsForbiddenType(typeof(System.IO.TextWriter)));
        Assert.IsFalse(protocolTypes.Any(IsForbiddenType));
        Assert.IsFalse(protocolTypes.SelectMany(TypeMembers).Any(HasForbiddenMemberSignature));
        Assert.IsFalse(reachableImplementations.Members.Any(IsForbiddenImplementationMember));
    }

    [TestMethod]
    public void TypedEvents_EnforceCorrelationPayloadAndTimestampRelationships()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new WorkerProtocolEvent("lifecycle", "ready", 1, WorkerProtocolV1TestData.Start, WorkerProtocolV1TestData.RunId, new ReadyPayload()));
        Assert.ThrowsExactly<ArgumentException>(() => new WorkerProtocolEvent("lifecycle", "run-started", 1, WorkerProtocolV1TestData.Start, null, new RunStartedPayload("manual", WorkerProtocolV1TestData.Start)));
        Assert.ThrowsExactly<ArgumentException>(() => new WorkerProtocolEvent("lifecycle", "run-started", 1, WorkerProtocolV1TestData.Midpoint, WorkerProtocolV1TestData.RunId, new RunStartedPayload("manual", WorkerProtocolV1TestData.Start)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new WorkerProtocolEvent("lifecycle", "ready", 0, WorkerProtocolV1TestData.Start, null, new ReadyPayload()));
    }

    private static IEnumerable<MemberInfo> TypeMembers(Type type) => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

    private static bool HasForbiddenMemberSignature(MemberInfo member)
    {
        return member switch
        {
            MethodInfo method => IsForbiddenType(method.ReturnType) || method.GetParameters().Any(parameter => IsForbiddenType(parameter.ParameterType)),
            ConstructorInfo constructor => constructor.GetParameters().Any(parameter => IsForbiddenType(parameter.ParameterType)),
            PropertyInfo property => IsForbiddenType(property.PropertyType),
            FieldInfo field => IsForbiddenType(field.FieldType),
            EventInfo @event => IsForbiddenType(@event.EventHandlerType!),
            _ => false
        };
    }

    private static bool IsForbiddenType(Type type)
    {
        if (type.HasElementType)
        {
            return IsForbiddenType(type.GetElementType()!);
        }

        if (type.IsGenericType && type.GetGenericArguments().Any(IsForbiddenType))
        {
            return true;
        }

        return type == typeof(Console)
            || typeof(System.IO.Stream).IsAssignableFrom(type)
            || typeof(System.IO.TextReader).IsAssignableFrom(type)
            || typeof(System.IO.TextWriter).IsAssignableFrom(type)
            || type == typeof(System.Diagnostics.Process)
            || type == typeof(System.Diagnostics.ProcessStartInfo)
            || type.Namespace is "System.IO.Pipes" or "Microsoft.AspNetCore" or "Microsoft.Extensions.Hosting" or "ImmichReverseGeo.Web"
            || type.Namespace?.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("Microsoft.Extensions.Hosting.", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("ImmichReverseGeo.Web.", StringComparison.Ordinal) == true;
    }

    private static bool IsForbiddenImplementationMember(MemberInfo member)
    {
        if (member.DeclaringType is not null && IsForbiddenType(member.DeclaringType))
        {
            return true;
        }

        return member.DeclaringType == typeof(Environment) && member.Name is nameof(Environment.Exit) or nameof(Environment.FailFast);
    }
}
