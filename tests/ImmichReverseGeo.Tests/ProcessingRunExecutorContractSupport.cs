using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Tests;

internal enum ExecutorCallKind
{
    Count, ConfigurationSnapshot, SkippedSnapshot, Batch, Admin, Airport,
    WriteAttempt, WriteAccepted, SkipAttempt, SkipAccepted, Delay, FallbackInput, GuardEvaluation
}

internal enum ExecutorEventKind
{
    RunStarted, EligibilityDetermined, LogEmitted, ProgressChanged, ActivityStarted, ActivityEnded, RunFinished
}

internal enum ExecutorEffectKind { Write, Skip, SpecificationEffect }
internal enum ContractSemantics { Ordered, ConcurrentSet }
internal enum TokenSourceKind { Call, AttemptedEvent }
internal enum TokenRole { Run, Asset, None, Foreign, Other }

internal sealed record ExecutorCallContract(
    ExecutorCallKind Kind,
    int? Ordinal = null,
    DateTimeOffset? CursorCreatedAtUtc = null,
    Guid? CursorId = null,
    int? BatchSize = null,
    Guid? AssetId = null,
    double? DelayMs = null,
    string? Detail = null);

internal sealed record ExecutorCallObservation(ExecutorCallContract Call, CancellationToken? Token, long Sequence);
internal sealed record ExecutorEffectContract(ExecutorEffectKind Kind, Guid AssetId, string? Country, string? State, string? City, string? Detail = null);
internal sealed record ExecutorLogContract(string Sink, string Level, string Message, string? ExceptionType, string? ExceptionMessage);
internal sealed record ExecutorEventContract(
    ExecutorEventKind Kind,
    DateTimeOffset? TimestampUtc = null,
    long? EligibleCount = null,
    string? Level = null,
    string? Message = null,
    long? ProcessedCount = null,
    long? UpdatedCount = null,
    long? SkippedCount = null,
    long? FailedCount = null,
    string? Label = null,
    string? Outcome = null,
    string? FailureMessage = null,
    bool RequestSame = true,
    bool? ResultSame = null);
internal sealed record ExecutorEventObservation(ExecutorEventContract Event, CancellationToken Token, Guid? AssetId, long Sequence);
internal sealed record ExecutorDispositionContract(int AssetOrdinal, string Outcome, long ProcessedCount, long UpdatedCount, long SkippedCount, long FailedCount);
internal sealed record ExecutorDispositionObservation(Guid AssetId, string Outcome, long ProcessedCount, long UpdatedCount, long SkippedCount, long FailedCount, long Sequence = 0);
internal sealed record ExecutorDispositionValue(Guid AssetId, string Outcome, long ProcessedCount, long UpdatedCount, long SkippedCount, long FailedCount);
internal sealed record ExecutorDispositionIdentity(Guid AssetId, string Outcome);
internal sealed record ExecutorCountState(long ProcessedCount, long UpdatedCount, long SkippedCount, long FailedCount);
internal enum ExecutorEdgePointKind { Call, AcceptedEvent, Disposition }
internal sealed record ExecutorEdgePointContract(ExecutorEdgePointKind Kind, int Index);
internal sealed record ExecutorCausalEdgeContract(ExecutorEdgePointContract Before, ExecutorEdgePointContract After);
internal sealed record ExecutorTokenContract(TokenSourceKind Source, int Index, TokenRole Role);
internal sealed record ExecutorCleanupContract(bool SessionConstructed, bool SessionReturned, bool TerminalAttempted, bool TerminalAccepted, bool ActivitiesBalanced);
internal sealed record ExecutorCleanupObservation(bool SessionConstructed, bool SessionReturned, bool TerminalAttempted, bool TerminalAccepted, bool ActivitiesBalanced);
internal sealed record ExecutorFallbackShapeContract(
    string? InputCountry, string? InputState, string? InputCity, bool InputHasMatch,
    string? OutputCountry, string? OutputState, string? OutputCity, bool OutputHasMatch,
    bool GuardMatched);
internal sealed record ExecutorForbiddenContract(bool AdditionalCalls, bool AdditionalEffects, bool AdditionalAttemptedEvents, bool AdditionalAcceptedEvents, bool AdditionalLogs, bool AdditionalAssets, bool AdditionalDispositions, ImmutableArray<string> Retries);
internal sealed record ExecutorResultContract(
    bool Returned,
    Guid? RequestRunId = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? EndedAtUtc = null,
    long? ProcessedCount = null,
    long? UpdatedCount = null,
    long? SkippedCount = null,
    long? FailedCount = null,
    string? Outcome = null,
    string? FailureMessage = null,
    PropagatedExceptionContract? PropagatedException = null);
internal sealed record PropagatedExceptionContract(string Type, string Message, bool SameReference);
internal sealed record ExecutorMethodBindingContract(string? DeclaringType, string MethodId, string BindingKind, string ParameterSignature, string? DynamicDataMember, ImmutableArray<ContractArgument> OrderedArguments);
internal sealed record ContractArgument(string Type, JsonElement Value);
internal sealed record ExecutorCaseContract(
    string CaseId,
    ImmutableArray<string> ScenarioIds,
    ImmutableArray<string> TaskIds,
    ExecutorMethodBindingContract Binding,
    ContractSemantics Semantics,
    ImmutableArray<ExecutorCallContract> Calls,
    ImmutableArray<ExecutorEffectContract> Effects,
    ImmutableArray<ExecutorEventContract> AttemptedEvents,
    ImmutableArray<ExecutorEventContract> AcceptedEvents,
    ImmutableArray<ExecutorLogContract> Logs,
    ExecutorResultContract Result,
    ImmutableArray<Guid> Assets,
    ImmutableArray<Guid> EffectIdentities,
    ImmutableArray<ExecutorDispositionContract> Dispositions,
    ImmutableArray<ExecutorCausalEdgeContract> CausalEdges,
    ImmutableArray<ExecutorSeamExceptionContract> SeamExceptions,
    ExecutorForbiddenContract Forbidden,
    ImmutableArray<ExecutorTokenContract> ExpectedTokens,
    ExecutorCleanupContract Cleanup,
    ImmutableArray<ExecutorFallbackShapeContract> FallbackShapes,
    bool NoExtras);
internal sealed record ExecutorMethodContract(string MethodId, string? DeclaringType, ImmutableArray<string> ParameterTypes, bool Active);
internal enum ExecutorProofKind { CompiledStructural, ExternalGate }
internal enum ExecutorProofClause
{
    FixtureIsolation, DirectExtractionReuse, HostCompositionOutsideFixture, StrictScopeReview,
    CompiledInventory, FocusedExecutorGate, CanonicalSuiteGate, ArchitectureGate
}
internal sealed record ExecutorProofBindingContract(
    string ProofId,
    ExecutorProofKind Kind,
    string? MethodId,
    string? GateId,
    ImmutableArray<string> ScenarioIds,
    ImmutableArray<string> TaskIds,
    ImmutableArray<ExecutorProofClause> SemanticClauses);
internal sealed record ExecutorBrooksAuditProvenanceContract(string Result, int HealthScore, ImmutableArray<string> TargetMisses, string RemediationAuthority);
internal sealed record ExecutorAuditProvenanceContract(string Result, ImmutableArray<string> TargetMisses, string RemediationAuthority);
internal sealed record ExecutorProvenanceContract(
    ImmutableArray<string> ApprovedSources,
    string RejectedDraftHistory,
    bool RuntimeObservationAuthority,
    int BehavioralSourceCount,
    string EquivalenceGate,
    ExecutorAuditProvenanceContract AuditRound3,
    ExecutorAuditProvenanceContract AuditRound4,
    ExecutorAuditProvenanceContract AuditRound5,
    ExecutorBrooksAuditProvenanceContract AuditRound6,
    ExecutorAuditProvenanceContract AuditRound7);
internal sealed record ExecutorContractDocument(
    string SchemaVersion,
    string Authority,
    string SourceChange,
    string Baseline,
    ExecutorProvenanceContract Provenance,
    ImmutableArray<string> ScenarioIds,
    ImmutableArray<string> TaskIds,
    ImmutableArray<string> ExternalGateIds,
    ImmutableArray<ExecutorMethodContract> Methods,
    ImmutableArray<ExecutorCaseContract> Contracts,
    ImmutableArray<ExecutorProofBindingContract> ProofBindings);

internal static class ExecutorContractAuthority
{
    private const string ResourceName = "ImmichReverseGeo.Tests.processing-run-executor-contracts.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static ExecutorContractDocument Document { get; } = Load();
    internal static IReadOnlyDictionary<string, ExecutorCaseContract> Cases { get; } =
        Document.Contracts.ToDictionary(item => item.CaseId, StringComparer.Ordinal);

    internal static string ReadEmbeddedJsonForSchemaTest()
    {
        using var stream = typeof(ExecutorContractAuthority).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new AssertFailedException($"Missing embedded executor contract resource {ResourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static ExecutorContractDocument DeserializeForSchemaTest(string json) =>
        JsonSerializer.Deserialize<ExecutorContractDocument>(json, Options)
        ?? throw new AssertFailedException("Authority schema sentinel document was empty.");

    private static ExecutorContractDocument Load()
    {
        using var stream = typeof(ExecutorContractAuthority).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new AssertFailedException($"Missing embedded executor contract resource {ResourceName}.");
        var document = JsonSerializer.Deserialize<ExecutorContractDocument>(stream, Options)
            ?? throw new AssertFailedException("Embedded executor contract document is empty.");
        Assert.AreEqual("8.0.0", document.SchemaVersion);
        Assert.AreEqual(67, document.Contracts.Length);
        Assert.AreEqual(67, document.Contracts.Select(item => item.CaseId).Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(7, document.ProofBindings.Length);
        return document;
    }
}

internal sealed record SeamExceptionObservation(ExecutorCallKind Kind, Guid? AssetId, Exception Exception);
internal sealed record ExecutorSeamExceptionContract(ExecutorCallKind Kind, int? AssetOrdinal, string Type, string? Message, TokenRole? CancellationOwner);
internal sealed record ExecutorCaseObservation(
    ProcessingRunRequest Request,
    ProcessingRunResult? Result,
    Exception? EscapedException,
    Exception? ExpectedEscapedException,
    IReadOnlyList<ExecutorCallObservation> Calls,
    IReadOnlyList<ExecutorEffectContract> Effects,
    IReadOnlyList<ExecutorEventObservation> Attempts,
    IReadOnlyList<ExecutorEventObservation> Events,
    IReadOnlyList<ExecutorLogContract> Logs,
    IReadOnlyList<Guid> FetchedAssets,
    IReadOnlyList<ExecutorDispositionObservation> Dispositions,
    IReadOnlyList<SeamExceptionObservation> SeamExceptions,
    CancellationToken RunToken,
    CancellationToken? ForeignToken,
    int MaximumActive,
    ExecutorCleanupObservation Cleanup);

internal sealed class ExecutorEventCorrelation
{
    private readonly ConcurrentDictionary<Guid, string> _activityLabels = new();
    private readonly ConcurrentDictionary<Guid, byte> _activeActivities = new();
    private readonly ConcurrentDictionary<ProcessingEvent, Guid> _assetIdentities = new(ReferenceEqualityComparer.Instance);

    internal ExecutorEventContract Create(ProcessingEvent processingEvent, ProcessingRunRequest request, ProcessingRunResult? returnedResult)
    {
        return processingEvent switch
        {
            RunStarted started => new(ExecutorEventKind.RunStarted, TimestampUtc: started.StartedAtUtc, RequestSame: ReferenceEquals(started.Request, request)),
            EligibilityDetermined eligibility => new(ExecutorEventKind.EligibilityDetermined, EligibleCount: eligibility.EligibleCount, RequestSame: ReferenceEquals(eligibility.Request, request)),
            LogEmitted log => new(ExecutorEventKind.LogEmitted, Level: log.Level.ToString(), Message: log.Message, RequestSame: ReferenceEquals(log.Request, request)),
            ProgressChanged progress => new(ExecutorEventKind.ProgressChanged, ProcessedCount: progress.Progress.ProcessedCount, UpdatedCount: progress.Progress.UpdatedCount, SkippedCount: progress.Progress.SkippedCount, FailedCount: progress.Progress.FailedCount, RequestSame: ReferenceEquals(progress.Request, request)),
            ActivityStarted activity => new(ExecutorEventKind.ActivityStarted, Label: activity.Label, RequestSame: ReferenceEquals(activity.Request, request)),
            ActivityEnded activity => new(ExecutorEventKind.ActivityEnded, Label: ActivityLabel(activity.ActivityId), RequestSame: ReferenceEquals(activity.Request, request)),
            RunFinished finished => new(ExecutorEventKind.RunFinished, ProcessedCount: finished.Result.ProcessedCount, UpdatedCount: finished.Result.UpdatedCount, SkippedCount: finished.Result.SkippedCount, FailedCount: finished.Result.FailedCount, Outcome: finished.Result.Outcome.ToString(), FailureMessage: finished.Result.FailureMessage, RequestSame: ReferenceEquals(finished.Request, request), ResultSame: returnedResult is null ? null : ReferenceEquals(finished.Result, returnedResult)),
            _ => throw new AssertFailedException($"Unknown processing event {processingEvent.GetType().FullName}.")
        };
    }

    internal void Correlate(ProcessingEvent processingEvent, Guid assetId) => _assetIdentities[processingEvent] = assetId;
    internal Guid? TakeCorrelatedAsset(ProcessingEvent processingEvent) =>
        _assetIdentities.TryRemove(processingEvent, out var assetId) ? assetId : null;
    internal void Reject(ProcessingEvent processingEvent)
    {
        _assetIdentities.TryRemove(processingEvent, out _);
        if (processingEvent is ActivityStarted started)
        {
            _activeActivities.TryRemove(started.ActivityId, out _);
            _activityLabels.TryRemove(started.ActivityId, out _);
        }
    }
    internal void ObserveActivity(ProcessingEvent processingEvent)
    {
        if (processingEvent is ActivityStarted started)
        {
            _activityLabels[started.ActivityId] = started.Label;
            _activeActivities[started.ActivityId] = 0;
        }
        else if (processingEvent is ActivityEnded ended)
        {
            Assert.IsTrue(_activeActivities.TryRemove(ended.ActivityId, out _), $"Activity {ended.ActivityId} ended without a matching start.");
        }
    }
    internal void Complete()
    {
        Assert.AreEqual(0, _assetIdentities.Count, "Pending event/asset correlations leaked beyond the fixture run.");
        Assert.AreEqual(0, _activeActivities.Count, "Pending activity correlations leaked beyond the fixture run.");
        _activityLabels.Clear();
    }
    internal int PendingCount => _assetIdentities.Count + _activeActivities.Count + _activityLabels.Count;
    internal int ActiveActivityCount => _activeActivities.Count;
    private string ActivityLabel(Guid id) => _activityLabels.TryGetValue(id, out var label)
        ? label : throw new AssertFailedException($"Activity {id} ended without a matching start.");
}
internal static class ExecutorCaseContractEngine
{
    internal static void Verify(string caseId, ExecutorCaseObservation observation)
    {
        Assert.IsTrue(ExecutorContractAuthority.Cases.TryGetValue(caseId, out var contract), $"Unknown authoritative executor case {caseId}.");
        var c = contract!;
        Assert.IsTrue(c.NoExtras, caseId);
        Assert.IsTrue(c.Forbidden.AdditionalCalls && c.Forbidden.AdditionalEffects && c.Forbidden.AdditionalAttemptedEvents
            && c.Forbidden.AdditionalAcceptedEvents && c.Forbidden.AdditionalLogs && c.Forbidden.AdditionalAssets && c.Forbidden.AdditionalDispositions, caseId);
        CollectionAssert.AreEqual(new[] { "Batch", "Persistence", "ReporterTerminal", "Rollback", "Compensation", "CrossStoreTransaction" }, c.Forbidden.Retries.ToArray(), caseId);
        VerifyBehavioralCommonForSchemaTest(c, caseId);

        Compare(c.Calls, observation.Calls.Select(item => item.Call), c.Semantics, caseId + " calls");
        Compare(c.Effects, observation.Effects, c.Semantics, caseId + " effects");
        Compare(c.AttemptedEvents, observation.Attempts.Select(item => item.Event), c.Semantics, caseId + " attempted events");
        Compare(c.AcceptedEvents, observation.Events.Select(item => item.Event), c.Semantics, caseId + " accepted events");
        Compare(c.Logs, observation.Logs, c.Semantics, caseId + " logs");
        Compare(c.Assets, observation.FetchedAssets, ContractSemantics.Ordered, caseId + " assets");
        Compare(c.EffectIdentities, observation.Effects.Select(item => item.AssetId), c.Semantics, caseId + " effect identities");
        var expectedDispositions = c.Dispositions.Select(item =>
        {
            Assert.IsTrue(item.AssetOrdinal > 0 && item.AssetOrdinal <= observation.FetchedAssets.Count,
                $"{caseId}: disposition asset ordinal {item.AssetOrdinal} is outside the exact fetched asset set.");
            return new ExecutorDispositionValue(observation.FetchedAssets[item.AssetOrdinal - 1], item.Outcome,
                item.ProcessedCount, item.UpdatedCount, item.SkippedCount, item.FailedCount);
        });
        if (c.Semantics == ContractSemantics.ConcurrentSet)
        {
            Compare(expectedDispositions.Select(item => new ExecutorDispositionIdentity(item.AssetId, item.Outcome)),
                observation.Dispositions.Select(item => new ExecutorDispositionIdentity(item.AssetId, item.Outcome)), c.Semantics, caseId + " disposition identities");
            Compare(expectedDispositions.Select(item => new ExecutorCountState(item.ProcessedCount, item.UpdatedCount, item.SkippedCount, item.FailedCount)),
                observation.Dispositions.Select(item => new ExecutorCountState(item.ProcessedCount, item.UpdatedCount, item.SkippedCount, item.FailedCount)), c.Semantics, caseId + " disposition counts");
        }
        else
        {
            Compare(expectedDispositions, observation.Dispositions.Select(item => new ExecutorDispositionValue(item.AssetId, item.Outcome,
                item.ProcessedCount, item.UpdatedCount, item.SkippedCount, item.FailedCount)), c.Semantics, caseId + " dispositions");
        }
        VerifyResult(c.Result, observation, caseId);
        VerifyTokens(c, observation);
        VerifySeamExceptions(c, observation);
        VerifyCancellationExceptions(c, observation);
        VerifyCausalEdges(c, observation);
        VerifyCleanup(c.Cleanup, observation, caseId);
    }

    internal static void VerifyBehavioralCommonForSchemaTest(ExecutorCaseContract contract, string caseId)
    {
        Assert.AreEqual(0, contract.FallbackShapes.Length, caseId + " behavioral fallback shapes");
    }

    internal static void VerifyStructuralCommonForSchemaTest(ExecutorCaseContract c, string caseId)
    {
        Assert.AreEqual(caseId, c.CaseId);
        CollectionAssert.AreEqual(new[] { "S16" }, c.ScenarioIds.ToArray(), caseId);
        CollectionAssert.AreEqual(new[] { "5.1", "5.6" }, c.TaskIds.ToArray(), caseId);
        Assert.AreEqual("ImmichReverseGeo.Tests.ProcessingRunExecutorChange11Tests", c.Binding.DeclaringType, caseId);
        Assert.AreEqual("WithFallbackCity_MakesLoggerOnlyNoCityExecutorBranchUnreachableForEveryHasMatchShape", c.Binding.MethodId, caseId);
        Assert.AreEqual("no-argument", c.Binding.BindingKind, caseId);
        Assert.AreEqual("()", c.Binding.ParameterSignature, caseId);
        Assert.IsNull(c.Binding.DynamicDataMember, caseId);
        Assert.AreEqual(0, c.Binding.OrderedArguments.Length, caseId);
        Assert.AreEqual(ContractSemantics.Ordered, c.Semantics, caseId);
        Assert.AreEqual(0, c.Assets.Length, caseId);
        Assert.AreEqual(0, c.EffectIdentities.Length, caseId);
        Assert.IsTrue(c.Forbidden.AdditionalCalls, caseId);
        Assert.IsTrue(c.Forbidden.AdditionalEffects, caseId);
        Assert.IsTrue(c.Forbidden.AdditionalAttemptedEvents, caseId);
        Assert.IsTrue(c.Forbidden.AdditionalAcceptedEvents, caseId);
        Assert.IsTrue(c.Forbidden.AdditionalLogs, caseId);
        Assert.IsTrue(c.Forbidden.AdditionalAssets, caseId);
        Assert.IsTrue(c.Forbidden.AdditionalDispositions, caseId);
        CollectionAssert.AreEqual(new[] { "Batch", "Persistence", "ReporterTerminal", "Rollback", "Compensation", "CrossStoreTransaction" }, c.Forbidden.Retries.ToArray(), caseId);
        Assert.AreEqual(0, c.ExpectedTokens.Length, caseId);
        Assert.IsFalse(c.Cleanup.SessionConstructed, caseId);
        Assert.IsFalse(c.Cleanup.SessionReturned, caseId);
        Assert.IsFalse(c.Cleanup.TerminalAttempted, caseId);
        Assert.IsFalse(c.Cleanup.TerminalAccepted, caseId);
        Assert.IsTrue(c.Cleanup.ActivitiesBalanced, caseId);
        Assert.AreEqual(0, c.CausalEdges.Length, caseId);
        Assert.AreEqual(0, c.SeamExceptions.Length, caseId);
        Assert.IsTrue(c.NoExtras, caseId);
    }

    internal static void VerifyStructural(string caseId, IReadOnlyList<ExecutorFallbackShapeContract> observations)
    {
        var c = ExecutorContractAuthority.Cases[caseId];
        VerifyStructuralCommonForSchemaTest(c, caseId);
        Assert.AreEqual(5, c.FallbackShapes.Length);
        Compare(c.FallbackShapes, observations, ContractSemantics.Ordered, caseId + " fallback shapes");
        Assert.AreEqual(0, c.Calls.Length);
        Assert.AreEqual(0, c.Effects.Length);
        Assert.AreEqual(0, c.AttemptedEvents.Length);
        Assert.AreEqual(0, c.AcceptedEvents.Length);
        Assert.AreEqual(0, c.Logs.Length);
        Assert.AreEqual(0, c.Dispositions.Length);
        Assert.IsTrue(c.Result.Returned);
        Assert.IsNull(c.Result.RequestRunId);
        Assert.IsNull(c.Result.StartedAtUtc);
        Assert.IsNull(c.Result.EndedAtUtc);
        Assert.IsNull(c.Result.ProcessedCount);
        Assert.IsNull(c.Result.UpdatedCount);
        Assert.IsNull(c.Result.SkippedCount);
        Assert.IsNull(c.Result.FailedCount);
        Assert.IsNull(c.Result.Outcome);
        Assert.IsNull(c.Result.FailureMessage);
        Assert.IsNull(c.Result.PropagatedException);
    }

    private static void VerifyResult(ExecutorResultContract expected, ExecutorCaseObservation actual, string caseId)
    {
        Assert.AreEqual(expected.Returned, actual.Result is not null, caseId + " returned result");
        if (!expected.Returned)
        {
            Assert.IsNull(expected.RequestRunId, caseId);
            Assert.IsNull(expected.StartedAtUtc, caseId);
            Assert.IsNull(expected.EndedAtUtc, caseId);
            Assert.IsNull(expected.ProcessedCount, caseId);
            Assert.IsNull(expected.UpdatedCount, caseId);
            Assert.IsNull(expected.SkippedCount, caseId);
            Assert.IsNull(expected.FailedCount, caseId);
            Assert.IsNull(expected.Outcome, caseId);
            Assert.IsNull(expected.FailureMessage, caseId);
            Assert.IsNull(actual.Result, caseId);
            Assert.IsNotNull(expected.PropagatedException, caseId);
            Assert.IsNotNull(actual.EscapedException, caseId);
            Assert.AreEqual(expected.PropagatedException!.Type, actual.EscapedException!.GetType().FullName, caseId);
            Assert.AreEqual(expected.PropagatedException.Message, actual.EscapedException.Message, caseId);
            if (expected.PropagatedException.SameReference)
            {
                Assert.AreSame(actual.ExpectedEscapedException, actual.EscapedException, caseId);
            }
            return;
        }
        Assert.IsNull(expected.PropagatedException, caseId);
        Assert.IsNotNull(actual.Result, caseId);
        var result = actual.Result!;
        Assert.AreSame(actual.Request, result.Request, caseId);
        Assert.AreEqual(expected.RequestRunId, result.Request.RunId, caseId);
        Assert.AreEqual(expected.StartedAtUtc, result.StartedAtUtc, caseId);
        Assert.AreEqual(expected.EndedAtUtc, result.EndedAtUtc, caseId);
        Assert.AreEqual(expected.ProcessedCount, result.ProcessedCount, caseId);
        Assert.AreEqual(expected.UpdatedCount, result.UpdatedCount, caseId);
        Assert.AreEqual(expected.SkippedCount, result.SkippedCount, caseId);
        Assert.AreEqual(expected.FailedCount, result.FailedCount, caseId);
        Assert.AreEqual(expected.Outcome, result.Outcome.ToString(), caseId);
        Assert.AreEqual(expected.FailureMessage, result.FailureMessage, caseId);
    }

    private static void VerifyTokens(ExecutorCaseContract c, ExecutorCaseObservation o)
    {
        foreach (var expected in c.ExpectedTokens)
        {
            var token = expected.Source switch
            {
                TokenSourceKind.Call => o.Calls[expected.Index].Token ?? throw new AssertFailedException($"{c.CaseId} call {expected.Index} has no token."),
                TokenSourceKind.AttemptedEvent => o.Attempts[expected.Index].Token,
                _ => throw new AssertFailedException($"{c.CaseId} unknown token source.")
            };
            var source = $"{c.CaseId} {expected.Source}[{expected.Index}]";
            var assetId = expected.Source switch
            {
                TokenSourceKind.Call => o.Calls[expected.Index].Call.AssetId,
                TokenSourceKind.AttemptedEvent => o.Attempts[expected.Index].AssetId,
                _ => null
            };
            switch (expected.Role)
            {
                case TokenRole.Run: Assert.AreEqual(o.RunToken, token, source); break;
                case TokenRole.Asset:
                    Assert.AreNotEqual(o.RunToken, token, source);
                    var assetCalls = o.Calls.Where(item => item.Call.AssetId.HasValue && item.Token.HasValue
                        && (!assetId.HasValue || item.Call.AssetId == assetId)).ToArray();
                    Assert.IsTrue(assetCalls.Length > 0, source);
                    Assert.IsTrue(assetCalls.All(item => item.Token!.Value == token), source);
                    break;
                case TokenRole.None: Assert.AreEqual(CancellationToken.None, token, source); break;
                case TokenRole.Foreign: Assert.IsTrue(o.ForeignToken.HasValue, c.CaseId); Assert.AreEqual(o.ForeignToken.Value, token, c.CaseId); Assert.AreNotEqual(o.RunToken, token, c.CaseId); break;
                case TokenRole.Other: Assert.AreNotEqual(o.RunToken, token, c.CaseId); break;
            }
        }
    }

    private static void VerifySeamExceptions(ExecutorCaseContract contract, ExecutorCaseObservation observation)
    {
        Assert.AreEqual(contract.SeamExceptions.Length, observation.SeamExceptions.Count, contract.CaseId);
        for (var index = 0; index < contract.SeamExceptions.Length; index++)
        {
            var expected = contract.SeamExceptions[index];
            var actual = observation.SeamExceptions[index];
            Assert.AreEqual(expected.Kind, actual.Kind, contract.CaseId);
            var expectedAsset = expected.AssetOrdinal.HasValue ? observation.FetchedAssets[expected.AssetOrdinal.Value - 1] : (Guid?)null;
            Assert.AreEqual(expectedAsset, actual.AssetId, contract.CaseId);
            if (expected.Type == "OperationCanceledException")
            {
                Assert.IsInstanceOfType<OperationCanceledException>(actual.Exception, contract.CaseId);
                var token = ((OperationCanceledException)actual.Exception).CancellationToken;
                switch (expected.CancellationOwner)
                {
                    case TokenRole.Run: Assert.AreEqual(observation.RunToken, token, contract.CaseId); break;
                    case TokenRole.Asset:
                        Assert.IsTrue(observation.Calls.Where(item => item.Call.AssetId == expectedAsset && item.Token.HasValue)
                            .Any(item => item.Token!.Value == token), contract.CaseId);
                        Assert.AreNotEqual(observation.RunToken, token, contract.CaseId);
                        break;
                    case TokenRole.Foreign: Assert.AreEqual(observation.ForeignToken, token, contract.CaseId); break;
                    default: throw new AssertFailedException($"{contract.CaseId}: missing exact OCE ownership role.");
                }
            }
            else
            {
                Assert.AreEqual(expected.Type, actual.Exception.GetType().FullName, contract.CaseId);
            }
            if (expected.Message is not null)
            {
                Assert.AreEqual(expected.Message, actual.Exception.Message, contract.CaseId);
            }
        }
    }

    private static void VerifyCancellationExceptions(ExecutorCaseContract contract, ExecutorCaseObservation observation)
    {
        var assetTokens = observation.Calls.Where(item => item.Call.AssetId.HasValue && item.Token.HasValue)
            .Select(item => item.Token!.Value).Distinct().ToArray();
        foreach (var seam in observation.SeamExceptions.Where(item => item.Exception is OperationCanceledException))
        {
            var exceptionToken = ((OperationCanceledException)seam.Exception).CancellationToken;
            if (observation.RunToken.IsCancellationRequested)
            {
                Assert.IsTrue(exceptionToken == observation.RunToken || assetTokens.Contains(exceptionToken),
                    $"{contract.CaseId}: active OCE token has neither run nor batch-asset ownership.");
            }
            else
            {
                Assert.IsTrue(observation.ForeignToken.HasValue, $"{contract.CaseId}: foreign OCE owner token was not supplied.");
                Assert.AreEqual(observation.ForeignToken!.Value, exceptionToken, contract.CaseId);
                Assert.AreNotEqual(observation.RunToken, exceptionToken, contract.CaseId);
                Assert.IsFalse(assetTokens.Contains(exceptionToken), contract.CaseId);
            }
        }
    }

    private static void VerifyCausalEdges(ExecutorCaseContract contract, ExecutorCaseObservation observation)
    {
        long Resolve(ExecutorEdgePointContract point)
        {
            return point.Kind switch
            {
                ExecutorEdgePointKind.Call => observation.Calls.Single(item => item.Call == contract.Calls[point.Index]).Sequence,
                ExecutorEdgePointKind.AcceptedEvent => observation.Events.Single(item => item.Event == contract.AcceptedEvents[point.Index]).Sequence,
                ExecutorEdgePointKind.Disposition => ResolveDisposition(point.Index),
                _ => throw new AssertFailedException($"{contract.CaseId}: unknown causal edge point {point.Kind}.")
            };
        }

        long ResolveDisposition(int index)
        {
            var expected = contract.Dispositions[index];
            var assetId = observation.FetchedAssets[expected.AssetOrdinal - 1];
            return observation.Dispositions.Single(item => item.AssetId == assetId && item.Outcome == expected.Outcome).Sequence;
        }

        foreach (var edge in contract.CausalEdges)
        {
            var before = Resolve(edge.Before);
            var after = Resolve(edge.After);
            Assert.IsTrue(before > 0 && before < after,
                $"{contract.CaseId}: causal edge {edge.Before.Kind}[{edge.Before.Index}] < {edge.After.Kind}[{edge.After.Index}] failed ({before}, {after}).");
        }
    }

    private static void VerifyCleanup(ExecutorCleanupContract expected, ExecutorCaseObservation actual, string caseId)
    {
        Assert.AreEqual(expected.SessionConstructed, actual.Cleanup.SessionConstructed, caseId);
        Assert.AreEqual(expected.SessionReturned, actual.Cleanup.SessionReturned, caseId);
        Assert.AreEqual(expected.TerminalAttempted, actual.Cleanup.TerminalAttempted, caseId);
        Assert.AreEqual(expected.TerminalAccepted, actual.Cleanup.TerminalAccepted, caseId);
        Assert.AreEqual(expected.ActivitiesBalanced, actual.Cleanup.ActivitiesBalanced, caseId);
    }

    private static void Compare<T>(IEnumerable<T> expected, IEnumerable<T> actual, ContractSemantics semantics, string message)
    {
        var left = expected.ToArray();
        var right = actual.ToArray();
        if (semantics == ContractSemantics.ConcurrentSet)
        {
            CollectionAssert.AreEquivalent(left, right, message);
        }
        else
        {
            CollectionAssert.AreEqual(left, right, message);
        }
    }
}
