using System.Reflection;
using System.Runtime.CompilerServices;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingRunModelsTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddMinutes(5);

    [TestMethod]
    public void Enums_ExposeExactlyTheDefinedVocabularyWithoutProtocolOrSerializationAnnotations()
    {
        AssertEnumSurface(
            typeof(ProcessingRunTrigger),
            nameof(ProcessingRunTrigger.Manual),
            nameof(ProcessingRunTrigger.Scheduled),
            nameof(ProcessingRunTrigger.RunOnce));
        AssertEnumSurface(
            typeof(ProcessingRunOutcome),
            nameof(ProcessingRunOutcome.Completed),
            nameof(ProcessingRunOutcome.Cancelled),
            nameof(ProcessingRunOutcome.Failed));
    }

    [TestMethod]
    [DataRow(ProcessingRunTrigger.Manual)]
    [DataRow(ProcessingRunTrigger.Scheduled)]
    [DataRow(ProcessingRunTrigger.RunOnce)]
    public void Request_PreservesNonEmptyIdentityAndDefinedTrigger(ProcessingRunTrigger trigger)
    {
        var runId = Guid.NewGuid();

        var request = new ProcessingRunRequest(runId, trigger);

        Assert.AreEqual(runId, request.RunId);
        Assert.AreEqual(trigger, request.Trigger);
    }

    [TestMethod]
    public void Request_RejectsEmptyIdentityAndUndefinedTrigger()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ProcessingRunRequest(Guid.Empty, ProcessingRunTrigger.Manual));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ProcessingRunRequest(Guid.NewGuid(), (ProcessingRunTrigger)99));
    }

    [TestMethod]
    public void Result_CompletedEmptyRun_PreservesEveryField()
    {
        var request = CreateRequest();

        var result = CreateResult(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed);

        AssertResult(result, request, 0, 0, 0, 0, ProcessingRunOutcome.Completed, null);
    }

    [TestMethod]
    public void Result_CompletedNonEmptyRun_PreservesEveryFieldAndHandledAssetFailures()
    {
        var request = CreateRequest(ProcessingRunTrigger.Scheduled);

        var result = CreateResult(request, 10, 6, 3, 1, ProcessingRunOutcome.Completed);

        AssertResult(result, request, 10, 6, 3, 1, ProcessingRunOutcome.Completed, null);
    }

    [TestMethod]
    public void Result_AllowsEqualUtcStartAndEndTimestamps()
    {
        var request = CreateRequest();

        var result = CreateResult(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed, startedAtUtc: Start, endedAtUtc: Start);

        Assert.AreEqual(Start, result.StartedAtUtc);
        Assert.AreEqual(Start, result.EndedAtUtc);
    }

    [TestMethod]
    public void Result_CancelledPartialRun_PreservesEveryField()
    {
        var request = CreateRequest(ProcessingRunTrigger.RunOnce);

        var result = CreateResult(request, 3, 1, 2, 0, ProcessingRunOutcome.Cancelled);

        AssertResult(result, request, 3, 1, 2, 0, ProcessingRunOutcome.Cancelled, null);
    }

    [TestMethod]
    public void Result_FailedPartialRun_PreservesEveryField()
    {
        var request = CreateRequest();

        var result = CreateResult(request, 4, 2, 1, 1, ProcessingRunOutcome.Failed, "The pass could not continue.");

        AssertResult(result, request, 4, 2, 1, 1, ProcessingRunOutcome.Failed, "The pass could not continue.");
    }

    [TestMethod]
    public void Result_FailedRunWithZeroPerAssetCounts_PreservesFailureDetail()
    {
        var request = CreateRequest();

        var result = CreateResult(request, 0, 0, 0, 0, ProcessingRunOutcome.Failed, "Eligibility lookup failed.");

        AssertResult(result, request, 0, 0, 0, 0, ProcessingRunOutcome.Failed, "Eligibility lookup failed.");
    }

    [TestMethod]
    public void Result_RejectsNonUtcAndReversedTimestamps()
    {
        var request = CreateRequest();
        var nonUtc = Start.ToOffset(TimeSpan.FromHours(1));

        Assert.ThrowsExactly<ArgumentException>(() => CreateResult(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed, startedAtUtc: nonUtc));
        Assert.ThrowsExactly<ArgumentException>(() => CreateResult(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed, endedAtUtc: nonUtc));
        Assert.ThrowsExactly<ArgumentException>(() => CreateResult(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed, startedAtUtc: End, endedAtUtc: Start));
    }

    [TestMethod]
    public void Result_RejectsNegativeCountsAggregateMismatchAndOverflow()
    {
        var request = CreateRequest();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateResult(request, -1, 0, 0, 0, ProcessingRunOutcome.Completed));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateResult(request, 0, -1, 0, 0, ProcessingRunOutcome.Completed));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateResult(request, 0, 0, -1, 0, ProcessingRunOutcome.Completed));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateResult(request, 0, 0, 0, -1, ProcessingRunOutcome.Completed));
        Assert.ThrowsExactly<ArgumentException>(() => CreateResult(request, 2, 1, 0, 0, ProcessingRunOutcome.Completed));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateResult(request, long.MaxValue, long.MaxValue, 1, 0, ProcessingRunOutcome.Completed));
    }

    [TestMethod]
    public void Result_RejectsNullRequestAndUndefinedOutcome()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new ProcessingRunResult(null!, Start, End, 0, 0, 0, 0, ProcessingRunOutcome.Completed, null));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateResult(CreateRequest(), 0, 0, 0, 0, (ProcessingRunOutcome)99));
    }

    [TestMethod]
    public void Result_EnforcesTerminalFailureDetailRules()
    {
        var request = CreateRequest();

        Assert.ThrowsExactly<ArgumentException>(() => CreateResult(request, 0, 0, 0, 0, ProcessingRunOutcome.Failed));
        Assert.ThrowsExactly<ArgumentException>(() => CreateResult(request, 0, 0, 0, 0, ProcessingRunOutcome.Failed, "  "));
        Assert.ThrowsExactly<ArgumentException>(() => CreateResult(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed, "detail"));
        Assert.ThrowsExactly<ArgumentException>(() => CreateResult(request, 0, 0, 0, 0, ProcessingRunOutcome.Cancelled, "detail"));
    }

    [TestMethod]
    public void Models_ExposeOnlyExpectedPublicContractSurface()
    {
        AssertRecordSurface(
            typeof(ProcessingRunRequest),
            [typeof(Guid), typeof(ProcessingRunTrigger)],
            nameof(ProcessingRunRequest.RunId),
            nameof(ProcessingRunRequest.Trigger));
        AssertRecordSurface(
            typeof(ProcessingRunResult),
            [
                typeof(ProcessingRunRequest),
                typeof(DateTimeOffset),
                typeof(DateTimeOffset),
                typeof(long),
                typeof(long),
                typeof(long),
                typeof(long),
                typeof(ProcessingRunOutcome),
                typeof(string)
            ],
            nameof(ProcessingRunResult.Request),
            nameof(ProcessingRunResult.StartedAtUtc),
            nameof(ProcessingRunResult.EndedAtUtc),
            nameof(ProcessingRunResult.ProcessedCount),
            nameof(ProcessingRunResult.UpdatedCount),
            nameof(ProcessingRunResult.SkippedCount),
            nameof(ProcessingRunResult.FailedCount),
            nameof(ProcessingRunResult.Outcome),
            nameof(ProcessingRunResult.FailureMessage));
    }

    private static ProcessingRunRequest CreateRequest(ProcessingRunTrigger trigger = ProcessingRunTrigger.Manual)
    {
        return new ProcessingRunRequest(Guid.NewGuid(), trigger);
    }

    private static ProcessingRunResult CreateResult(
        ProcessingRunRequest request,
        long processedCount,
        long updatedCount,
        long skippedCount,
        long failedCount,
        ProcessingRunOutcome outcome,
        string? failureMessage = null,
        DateTimeOffset? startedAtUtc = null,
        DateTimeOffset? endedAtUtc = null)
    {
        return new ProcessingRunResult(
            request,
            startedAtUtc ?? Start,
            endedAtUtc ?? End,
            processedCount,
            updatedCount,
            skippedCount,
            failedCount,
            outcome,
            failureMessage);
    }

    private static void AssertResult(
        ProcessingRunResult result,
        ProcessingRunRequest request,
        long processedCount,
        long updatedCount,
        long skippedCount,
        long failedCount,
        ProcessingRunOutcome outcome,
        string? failureMessage)
    {
        Assert.AreSame(request, result.Request);
        Assert.AreEqual(Start, result.StartedAtUtc);
        Assert.AreEqual(End, result.EndedAtUtc);
        Assert.AreEqual(processedCount, result.ProcessedCount);
        Assert.AreEqual(updatedCount, result.UpdatedCount);
        Assert.AreEqual(skippedCount, result.SkippedCount);
        Assert.AreEqual(failedCount, result.FailedCount);
        Assert.AreEqual(outcome, result.Outcome);
        Assert.AreEqual(failureMessage, result.FailureMessage);
    }

    private static void AssertEnumSurface(Type enumType, params string[] expectedNames)
    {
        CollectionAssert.AreEquivalent(expectedNames, Enum.GetNames(enumType));

        var fields = enumType.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(expectedNames, fields.Select(field => field.Name).ToArray());
        Assert.IsTrue(fields.All(field => field.IsLiteral && field.FieldType == enumType));
        AssertHasNoProhibitedAttributes(enumType.GetCustomAttributes(inherit: false));
        Assert.IsTrue(fields.All(field => !HasProhibitedAttribute(field.GetCustomAttributes(inherit: false))));
        Assert.AreEqual(0, enumType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Length);
        Assert.AreEqual(0, enumType.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length);
        Assert.AreEqual(0, enumType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Length);
    }

    private static void AssertRecordSurface(Type type, Type[] constructorParameterTypes, params string[] expectedPropertyNames)
    {
        AssertHasNoProhibitedAttributes(type.GetCustomAttributes(inherit: false));
        Assert.AreEqual(0, type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Length);
        Assert.AreEqual(0, type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Length);
        Assert.AreEqual(0, type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Length);

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(expectedPropertyNames, properties.Select(property => property.Name).ToArray());
        Assert.IsTrue(properties.All(property => property.GetMethod is not null && property.SetMethod is null));
        Assert.IsTrue(properties.All(property => !HasProhibitedAttribute(property.GetCustomAttributes(inherit: false))));

        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.AreEqual(1, constructors.Length);
        CollectionAssert.AreEqual(constructorParameterTypes, constructors[0].GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        Assert.IsFalse(HasProhibitedAttribute(constructors[0].GetCustomAttributes(inherit: false)));
        Assert.IsTrue(constructors[0].GetParameters().All(parameter => !HasProhibitedAttribute(parameter.GetCustomAttributes(inherit: false))));

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.IsTrue(methods.All(IsRecordGeneratedMethod));
        Assert.IsTrue(methods.All(method => !HasProhibitedAttribute(method.GetCustomAttributes(inherit: false))));
    }

    private static bool IsRecordGeneratedMethod(MethodInfo method)
    {
        return method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            || method.Name is nameof(ToString) or nameof(GetHashCode) or nameof(Equals) or "op_Equality" or "op_Inequality";
    }

    private static void AssertHasNoProhibitedAttributes(IEnumerable<object> attributes)
    {
        Assert.IsFalse(HasProhibitedAttribute(attributes));
    }

    private static bool HasProhibitedAttribute(IEnumerable<object> attributes)
    {
        return attributes.Any(IsProhibitedAttribute);
    }

    private static bool IsProhibitedAttribute(object attribute)
    {
        var name = attribute.GetType().Name;
        return name.Contains("Json", StringComparison.Ordinal)
            || name.Contains("Serializ", StringComparison.Ordinal)
            || name.Contains("DataContract", StringComparison.Ordinal)
            || name.Contains("DataMember", StringComparison.Ordinal)
            || name.Contains("Xml", StringComparison.Ordinal)
            || name.Contains("Proto", StringComparison.Ordinal)
            || name.Contains("MessagePack", StringComparison.Ordinal)
            || name.Contains("Protocol", StringComparison.Ordinal)
            || name.Contains("Wire", StringComparison.Ordinal)
            || name.Contains("Envelope", StringComparison.Ordinal)
            || name.Contains("Framing", StringComparison.Ordinal);
    }
}
