using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal sealed class FixtureRunner
{
    internal const string StandardErrorPrefix = "fixture-stderr-prefix\n";
    internal const string StandardErrorSuffix = "\nfixture-stderr-suffix\n";

    internal static readonly DateTimeOffset ReadyAtUtc = new(2000, 1, 2, 3, 4, 4, TimeSpan.Zero);
    private static readonly DateTimeOffset StartedAtUtc = new(2000, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset EndedAtUtc = new(2000, 1, 2, 3, 4, 6, TimeSpan.Zero);
    private static readonly byte[] PreReadyCrashDiagnostic = Encoding.UTF8.GetBytes("fixture:pre-ready-crash\n");
    private static readonly byte[] PostReadyCrashDiagnostic = Encoding.UTF8.GetBytes("fixture:post-ready-crash\n");
    private static readonly byte[] MalformedUtf8 = [0xff, (byte)'\n'];
    private static readonly byte[] MalformedJson = Encoding.UTF8.GetBytes("{]\n");
    private static readonly byte[] MalformedFraming = [(byte)'\n'];

    private readonly FixtureOptions _options;
    private readonly ControllerInputReader _input;
    private readonly FixtureProtocolOutput _output;
    private readonly Stream _standardError;

    internal FixtureRunner(
        FixtureOptions options,
        InternalWorkerProtocolVersion protocolVersion,
        Stream standardInput,
        Stream standardOutput,
        Stream standardError)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        _options = options;
        _input = new ControllerInputReader(standardInput, protocolVersion);
        _output = new FixtureProtocolOutput(standardOutput, protocolVersion);
        _standardError = standardError;
    }

    internal async Task<int> RunAsync()
    {
        if (_options.Scenario == FixtureScenario.PreReadyCrash)
        {
            await WriteStandardErrorAsync(PreReadyCrashDiagnostic).ConfigureAwait(false);
            return _options.ExitCode!.Value;
        }

        if (_options.Scenario == FixtureScenario.RawExit)
        {
            return _options.ExitCode!.Value;
        }

        await _output.WriteReadyAsync().ConfigureAwait(false);
        var executeFrame = await _input.ReadExecuteAsync().ConfigureAwait(false);
        await CaptureExecuteAsync(executeFrame.Bytes).ConfigureAwait(false);
        if (executeFrame.Dispatch is CoordinateLookupWorkerJobDispatch coordinateLookup)
        {
            return await RunCoordinateAsync(coordinateLookup).ConfigureAwait(false);
        }

        var request = executeFrame.Request;
        if (_options.IsProgressBurst)
        {
            return await RunProgressBurstAsync(request).ConfigureAwait(false);
        }

        return _options.Scenario switch
        {
            FixtureScenario.Ready => await RunNoWorkAsync(request).ConfigureAwait(false),
            FixtureScenario.Success => await RunSuccessAsync(request, "success").ConfigureAwait(false),
            FixtureScenario.SourceDegraded => await RunSuccessAsync(request, "source-degraded").ConfigureAwait(false),
            FixtureScenario.DomainFailure => await RunDomainFailureAsync(request).ConfigureAwait(false),
            FixtureScenario.NoWork => await RunNoWorkAsync(request).ConfigureAwait(false),
            FixtureScenario.PostReadyCrash => await RunPostReadyCrashAsync(request).ConfigureAwait(false),
            FixtureScenario.Malformed => await RunMalformedAsync().ConfigureAwait(false),
            FixtureScenario.Oversize => await RunOversizeAsync().ConfigureAwait(false),
            FixtureScenario.Unknown => await RunUnknownAsync(request).ConfigureAwait(false),
            FixtureScenario.InvalidSequence => await RunInvalidSequenceAsync(request).ConfigureAwait(false),
            FixtureScenario.TerminalMismatch => await RunTerminalMismatchAsync(request).ConfigureAwait(false),
            FixtureScenario.StandardErrorFlood => await RunStandardErrorFloodAsync(request).ConfigureAwait(false),
            FixtureScenario.CooperativeCancel => await RunCooperativeCancelAsync(request).ConfigureAwait(false),
            FixtureScenario.Unresponsive => await RunUnresponsiveAsync(request).ConfigureAwait(false),
            _ => throw new InvalidOperationException("The selected fixture scenario is not executable.")
        };
    }

    private async Task<int> RunProgressBurstAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, _options.ProgressCount).ConfigureAwait(false);
        Task stderr = WriteStandardErrorFloodAsync(262_177);
        long sequence = 4;
        try
        {
            for (long count = 1; count <= _options.ProgressCount; count++)
            {
                var progress = new ProgressChanged(request, new ProcessingProgress(count, count, 0, 0));
                if (_options.Scenario == FixtureScenario.ProgressBurstGap && count == _options.ProgressCount / 2)
                {
                    // Bypass only the fixture's writer validation for this intentional
                    // raw fault. The parent must reject it before coalescer intake.
                    var invalid = WorkerProtocolMapper.Map(progress, sequence + 1, StartedAtUtc.AddTicks(sequence));
                    await _output.WriteFrameAsync(_output.SerializeMapped(invalid)).ConfigureAwait(false);
                    return WorkerProcessExitCodes.Completed;
                }

                await _output.WriteValidAsync(WorkerProtocolMapper.Map(
                    progress, sequence, StartedAtUtc.AddTicks(sequence))).ConfigureAwait(false);
                sequence++;
                if (_options.BarrierEvery != 0 && count % _options.BarrierEvery == 0)
                {
                    await _output.WriteValidAsync(WorkerProtocolMapper.Map(
                        new ActivityStarted(request, request.RunId, "burst activity"),
                        sequence, StartedAtUtc.AddTicks(sequence))).ConfigureAwait(false);
                    sequence++;
                    foreach (var level in new[] { ProcessingLogLevel.Trace, ProcessingLogLevel.Information,
                        ProcessingLogLevel.Warning, ProcessingLogLevel.Error })
                    {
                        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
                            new LogEmitted(request, level, $"burst {count} {level}"),
                            sequence, StartedAtUtc.AddTicks(sequence))).ConfigureAwait(false);
                        sequence++;
                    }

                    await _output.WriteValidAsync(WorkerProtocolMapper.Map(
                        new ActivityEnded(request, request.RunId),
                        sequence, StartedAtUtc.AddTicks(sequence))).ConfigureAwait(false);
                    sequence++;
                }
            }

            if (_options.Scenario == FixtureScenario.ProgressBurstCrash)
            {
                return 42;
            }

            var outcome = ProcessingRunOutcome.Completed;
            if (_options.Scenario is FixtureScenario.ProgressBurstCancel or FixtureScenario.ProgressBurstUnresponsive)
            {
                await _output.WriteValidAsync(WorkerProtocolMapper.Map(
                    new LogEmitted(request, ProcessingLogLevel.Information, "fixture:burst-armed"),
                    sequence, StartedAtUtc.AddTicks(sequence))).ConfigureAwait(false);
                sequence++;
                var cancel = await _input.ReadCancelOrEndAsync().ConfigureAwait(false);
                if (_options.Scenario == FixtureScenario.ProgressBurstUnresponsive)
                {
                    await _output.WriteValidAsync(WorkerProtocolMapper.Map(
                        new LogEmitted(request, ProcessingLogLevel.Information, "fixture:burst-cancel-observed"),
                        sequence, StartedAtUtc.AddTicks(sequence))).ConfigureAwait(false);
                    sequence++;
                    await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.ConfigureAwait(false);
                }

                if (cancel is null)
                {
                    throw new FixtureInputException("Controller input ended before burst cancellation.");
                }

                outcome = ProcessingRunOutcome.Cancelled;
            }

            await EmitTerminalAsync(request, outcome, sequence, _options.ProgressCount,
                _options.ProgressCount, 0, 0).ConfigureAwait(false);
            _output.AssertComplete();
            return outcome == ProcessingRunOutcome.Cancelled
                ? WorkerProcessExitCodes.Cancelled
                : WorkerProcessExitCodes.Completed;
        }
        finally
        {
            await stderr.ConfigureAwait(false);
        }
    }

    private Task<int> RunCoordinateAsync(CoordinateLookupWorkerJobDispatch dispatch)
    {
        return _options.Scenario switch
        {
            FixtureScenario.Ready => RunCoordinateNoCountryAsync(dispatch),
            FixtureScenario.Success => RunCoordinateSuccessAsync(dispatch, sourceDegraded: false),
            FixtureScenario.SourceDegraded => RunCoordinateSuccessAsync(dispatch, sourceDegraded: true),
            FixtureScenario.NoWork => RunCoordinateNoCountryAsync(dispatch),
            FixtureScenario.DomainFailure => RunCoordinateDomainFailureAsync(dispatch),
            FixtureScenario.PostReadyCrash => RunCoordinatePostReadyCrashAsync(dispatch),
            FixtureScenario.Malformed => RunMalformedAsync(),
            FixtureScenario.Oversize => RunOversizeAsync(),
            FixtureScenario.CooperativeCancel => RunCoordinateCooperativeCancelAsync(dispatch),
            _ => throw new FixtureInputException("The selected scenario does not support CoordinateLookup.")
        };
    }

    private async Task<int> RunCoordinateNoCountryAsync(
        CoordinateLookupWorkerJobDispatch dispatch)
    {
        await EmitCoordinateStartedAsync(dispatch).ConfigureAwait(false);
        await EmitCoordinateProgressAsync(
            dispatch,
            3,
            CoordinateLookupProgressStep.Country,
            CoordinateLookupSourceState.NoMatch,
            null,
            "No bundled country matched.").ConfigureAwait(false);
        var result = CreateCoordinateResult(
            dispatch.Request,
            new CoordinateLookupCountryResult(
                CoordinateLookupCountryStatus.NoMatch,
                null,
                null,
                null,
                null,
                null),
            CoordinateLookupSourceState.Skipped,
            sourceDegraded: false);
        await EmitCoordinateTerminalAsync(
            dispatch,
            4,
            WorkerJobTerminalOutcome.Completed,
            result,
            null).ConfigureAwait(false);
        _output.AssertComplete();
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunCoordinateSuccessAsync(
        CoordinateLookupWorkerJobDispatch dispatch,
        bool sourceDegraded)
    {
        await EmitCoordinateStartedAsync(dispatch).ConfigureAwait(false);
        await EmitCoordinateProgressAsync(
            dispatch,
            3,
            CoordinateLookupProgressStep.Country,
            CoordinateLookupSourceState.Ready,
            "USA",
            "Bundled country matched.").ConfigureAwait(false);

        Guid activityId = dispatch.Context.JobId;
        await EmitCoordinateAsync(
            dispatch,
            4,
            new WorkerJobActivityStartedPayload(activityId, "Download Overture divisions for USA"),
            StartedAtUtc.AddTicks(2)).ConfigureAwait(false);
        await EmitCoordinateProgressAsync(
            dispatch,
            5,
            CoordinateLookupProgressStep.OvertureCache,
            sourceDegraded ? CoordinateLookupSourceState.Unavailable : CoordinateLookupSourceState.Ready,
            "USA",
            sourceDegraded ? "Overture cache was unavailable." : "Overture cache is ready.").ConfigureAwait(false);
        await EmitCoordinateAsync(
            dispatch,
            6,
            new WorkerJobActivityEndedPayload(activityId),
            StartedAtUtc.AddTicks(4)).ConfigureAwait(false);
        await EmitCoordinateProgressAsync(
            dispatch,
            7,
            CoordinateLookupProgressStep.OvertureAdministrative,
            sourceDegraded ? CoordinateLookupSourceState.Unavailable : CoordinateLookupSourceState.Ready,
            "USA",
            sourceDegraded ? "Overture administrative data was unavailable." : "Overture administrative data matched.").ConfigureAwait(false);
        await EmitCoordinateProgressAsync(
            dispatch,
            8,
            CoordinateLookupProgressStep.GadmAdministrative,
            dispatch.Request.PreferGadmAdministrativeAreas
                ? CoordinateLookupSourceState.Ready
                : CoordinateLookupSourceState.Disabled,
            "USA",
            dispatch.Request.PreferGadmAdministrativeAreas ? "GADM matched." : "GADM was disabled.").ConfigureAwait(false);
        await EmitCoordinateProgressAsync(
            dispatch,
            9,
            CoordinateLookupProgressStep.Airport,
            dispatch.Request.IncludeAirportInfrastructure
                ? CoordinateLookupSourceState.NoMatch
                : CoordinateLookupSourceState.Disabled,
            "USA",
            dispatch.Request.IncludeAirportInfrastructure ? "No airport matched." : "Airport lookup was disabled.").ConfigureAwait(false);
        await EmitCoordinateProgressAsync(
            dispatch,
            10,
            CoordinateLookupProgressStep.LivePlaces,
            dispatch.Request.IncludeLiveOverturePlaces
                ? CoordinateLookupSourceState.Ready
                : CoordinateLookupSourceState.Disabled,
            "USA",
            dispatch.Request.IncludeLiveOverturePlaces ? "Places diagnostics matched." : "Live Places was disabled.").ConfigureAwait(false);
        await EmitCoordinateProgressAsync(
            dispatch,
            11,
            CoordinateLookupProgressStep.FinalSelection,
            CoordinateLookupSourceState.Ready,
            "USA",
            "Final location selected.").ConfigureAwait(false);

        var result = CreateCoordinateResult(
            dispatch.Request,
            new CoordinateLookupCountryResult(
                CoordinateLookupCountryStatus.Matched,
                "USA",
                "US",
                "United States",
                "fixture-country",
                null),
            sourceDegraded ? CoordinateLookupSourceState.Unavailable : CoordinateLookupSourceState.Ready,
            sourceDegraded);
        await EmitCoordinateTerminalAsync(
            dispatch,
            12,
            WorkerJobTerminalOutcome.Completed,
            result,
            null).ConfigureAwait(false);
        _output.AssertComplete();
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunCoordinateDomainFailureAsync(
        CoordinateLookupWorkerJobDispatch dispatch)
    {
        await EmitCoordinateStartedAsync(dispatch).ConfigureAwait(false);
        await EmitCoordinateTerminalAsync(
            dispatch,
            3,
            WorkerJobTerminalOutcome.Failed,
            null,
            new WorkerJobSafeError(
                "fixture-domain-failure",
                WorkerJobFailureCategory.Internal,
                "The fixture lookup failed.")).ConfigureAwait(false);
        _output.AssertComplete();
        return WorkerProcessExitCodes.ExecutorFailure;
    }

    private async Task<int> RunCoordinatePostReadyCrashAsync(
        CoordinateLookupWorkerJobDispatch dispatch)
    {
        await EmitCoordinateStartedAsync(dispatch).ConfigureAwait(false);
        await EmitCoordinateAsync(
            dispatch,
            3,
            new WorkerJobLogPayload(
                "information",
                Marker("post-ready-crash", dispatch.Context.JobId)),
            StartedAtUtc.AddTicks(1)).ConfigureAwait(false);
        await WriteStandardErrorAsync(PostReadyCrashDiagnostic).ConfigureAwait(false);
        return _options.ExitCode!.Value;
    }

    private async Task<int> RunCoordinateCooperativeCancelAsync(
        CoordinateLookupWorkerJobDispatch dispatch)
    {
        await EmitCoordinateStartedAsync(dispatch).ConfigureAwait(false);
        await EmitCoordinateProgressAsync(
            dispatch,
            3,
            CoordinateLookupProgressStep.Country,
            CoordinateLookupSourceState.Ready,
            "USA",
            "Bundled country matched.").ConfigureAwait(false);
        Guid activityId = dispatch.Context.JobId;
        await EmitCoordinateAsync(
            dispatch,
            4,
            new WorkerJobActivityStartedPayload(activityId, "Download Overture divisions for USA"),
            StartedAtUtc.AddTicks(2)).ConfigureAwait(false);
        await EmitCoordinateAsync(
            dispatch,
            5,
            new WorkerJobLogPayload(
                "information",
                Marker("cooperative-cancel", dispatch.Context.JobId)),
            StartedAtUtc.AddTicks(3)).ConfigureAwait(false);

        var cancel = await _input.ReadCancelOrEndAsync().ConfigureAwait(false);
        if (cancel is null)
        {
            throw new FixtureInputException("Controller input ended before cooperative cancel.");
        }

        await EmitCoordinateAsync(
            dispatch,
            6,
            new WorkerJobActivityEndedPayload(activityId),
            StartedAtUtc.AddTicks(4)).ConfigureAwait(false);
        await EmitCoordinateTerminalAsync(
            dispatch,
            7,
            WorkerJobTerminalOutcome.Cancelled,
            null,
            null).ConfigureAwait(false);
        _output.AssertComplete();
        return WorkerProcessExitCodes.Cancelled;
    }

    private async Task EmitCoordinateStartedAsync(CoordinateLookupWorkerJobDispatch dispatch)
    {
        await _output.WriteValidAsync(WorkerJobProtocolMapper.JobStarted(
            dispatch.Context,
            "manual",
            StartedAtUtc,
            2)).ConfigureAwait(false);
    }

    private Task EmitCoordinateProgressAsync(
        CoordinateLookupWorkerJobDispatch dispatch,
        long sequence,
        CoordinateLookupProgressStep step,
        CoordinateLookupSourceState state,
        string? countryCode,
        string message) =>
        EmitCoordinateAsync(
            dispatch,
            sequence,
            new CoordinateLookupProgressPayload(step, state, countryCode, message),
            StartedAtUtc.AddTicks(sequence - 2));

    private Task EmitCoordinateAsync(
        CoordinateLookupWorkerJobDispatch dispatch,
        long sequence,
        WorkerJobOutputPayload payload,
        DateTimeOffset timestampUtc) =>
        _output.WriteValidAsync(WorkerJobProtocolMapper.Map(
            dispatch.Context,
            new WorkerJobHandlerEvent(timestampUtc, payload),
            sequence));

    private Task EmitCoordinateTerminalAsync(
        CoordinateLookupWorkerJobDispatch dispatch,
        long sequence,
        WorkerJobTerminalOutcome outcome,
        CoordinateLookupResult? result,
        WorkerJobSafeError? error) =>
        _output.WriteValidAsync(WorkerJobProtocolMapper.Terminal(
            dispatch.Context,
            new WorkerJobTerminalPayload(
                outcome,
                StartedAtUtc,
                EndedAtUtc,
                null,
                result,
                error),
            sequence));

    private static CoordinateLookupResult CreateCoordinateResult(
        CoordinateLookupRequest request,
        CoordinateLookupCountryResult country,
        CoordinateLookupSourceState overtureState,
        bool sourceDegraded)
    {
        string? iso3 = country.Iso3;
        var candidate = new CoordinateLookupCandidate(
            "fixture-division",
            "Fixture City",
            true,
            "selected by fixture profile",
            true,
            true,
            "division_area",
            "locality",
            null,
            null,
            "fixture-class",
            2,
            true,
            false,
            country.Name,
            null,
            null,
            null,
            null,
            null,
            42,
            ["fixture-source"]);
        WorkerJobSafeError? overtureError = sourceDegraded
            ? new WorkerJobSafeError(
                "fixture-source-unavailable",
                WorkerJobFailureCategory.Dependency,
                "The fixture source was unavailable.")
            : null;
        var overture = new CoordinateLookupSourceResult(
            overtureState,
            "fixture-release",
            null,
            sourceDegraded || iso3 is null ? null : candidate,
            sourceDegraded || iso3 is null ? [] : [candidate],
            iso3 is null ? [] : [new CoordinateLookupCacheStatus(iso3, overtureState, overtureError)],
            overtureError,
            "Overture Maps",
            null,
            null);
        var gadm = new CoordinateLookupSourceResult(
            request.PreferGadmAdministrativeAreas && iso3 is not null
                ? CoordinateLookupSourceState.Ready
                : request.PreferGadmAdministrativeAreas
                    ? CoordinateLookupSourceState.Skipped
                    : CoordinateLookupSourceState.Disabled,
            null,
            request.PreferGadmAdministrativeAreas && iso3 is not null ? "fixture-gadm-version" : null,
            null,
            [],
            [],
            null,
            CoordinateLookupGadmAttribution.DatasetName,
            CoordinateLookupGadmAttribution.LicenseUrl,
            CoordinateLookupGadmAttribution.UsageNotice);
        var airport = EmptyCoordinateSource(
            request.IncludeAirportInfrastructure && iso3 is not null
                ? CoordinateLookupSourceState.NoMatch
                : request.IncludeAirportInfrastructure
                    ? CoordinateLookupSourceState.Skipped
                    : CoordinateLookupSourceState.Disabled);
        var places = EmptyCoordinateSource(
            request.IncludeLiveOverturePlaces && iso3 is not null
                ? CoordinateLookupSourceState.Ready
                : request.IncludeLiveOverturePlaces
                    ? CoordinateLookupSourceState.Skipped
                    : CoordinateLookupSourceState.Disabled);
        var admin = new CoordinateLookupAdministrativeResult(
            sourceDegraded || iso3 is null ? null : "Fixture State",
            sourceDegraded || iso3 is null ? null : "Fixture City");
        var finalLocation = new CoordinateLookupFinalLocation(
            country.Name is null
                ? null
                : new CoordinateLookupAttributedValue(country.Name, CoordinateLookupFinalSource.BundledCountryDivisions),
            admin.State is null
                ? null
                : new CoordinateLookupAttributedValue(admin.State, CoordinateLookupFinalSource.CachedOvertureDivisions),
            admin.City is null
                ? null
                : new CoordinateLookupAttributedValue(admin.City, CoordinateLookupFinalSource.CachedOvertureDivisions));
        return new CoordinateLookupResult(
            request,
            StartedAtUtc,
            EndedAtUtc,
            country,
            overture,
            gadm,
            airport,
            places,
            admin,
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupProfileSummary(
                iso3,
                ["locality"],
                CoordinateLookupTieBreak.SmallestArea),
            sourceDegraded ? ["Overture degraded; independent country retained."] : ["Fixture selection completed."],
            finalLocation);
    }

    private static CoordinateLookupSourceResult EmptyCoordinateSource(
        CoordinateLookupSourceState state) =>
        new(
            state,
            null,
            null,
            null,
            [],
            [],
            null,
            null,
            null,
            null);

    private async Task<int> RunDomainFailureAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, 0).ConfigureAwait(false);
        await EmitTerminalAsync(request, ProcessingRunOutcome.Failed, 4, 0, 0, 0, 1).ConfigureAwait(false);
        _output.AssertComplete();
        return WorkerProcessExitCodes.ExecutorFailure;
    }

    private async Task<int> RunNoWorkAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, 0).ConfigureAwait(false);
        await EmitTerminalAsync(request, ProcessingRunOutcome.Completed, 4, 0, 0, 0, 0).ConfigureAwait(false);
        _output.AssertComplete();
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunSuccessAsync(ProcessingRunRequest request, string scenarioToken)
    {
        await EmitStartedAndEligibilityAsync(request, 1).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new ActivityStarted(request, request.RunId, "fixture-activity"),
            4,
            StartedAtUtc.AddTicks(2))).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new LogEmitted(request, ProcessingLogLevel.Information, Marker(scenarioToken, request.RunId)),
            5,
            StartedAtUtc.AddTicks(3))).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new ProgressChanged(request, new ProcessingProgress(1, 1, 0, 0)),
            6,
            StartedAtUtc.AddTicks(4))).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new ActivityEnded(request, request.RunId),
            7,
            StartedAtUtc.AddTicks(5))).ConfigureAwait(false);
        await EmitTerminalAsync(request, ProcessingRunOutcome.Completed, 8, 1, 1, 0, 0).ConfigureAwait(false);
        _output.AssertComplete();
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunPostReadyCrashAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, 0).ConfigureAwait(false);
        await EmitLogAsync(request, 4, Marker("post-ready-crash", request.RunId)).ConfigureAwait(false);
        await WriteStandardErrorAsync(PostReadyCrashDiagnostic).ConfigureAwait(false);
        return _options.ExitCode!.Value;
    }

    private async Task<int> RunMalformedAsync()
    {
        var bytes = _options.SelectedMalformedKind!.Value switch
        {
            MalformedKind.Utf8 => MalformedUtf8,
            MalformedKind.Json => MalformedJson,
            MalformedKind.Framing => MalformedFraming,
            _ => throw new InvalidOperationException("The selected malformed-output kind is not supported.")
        };

        await _output.WriteRawAsync(bytes).ConfigureAwait(false);
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunOversizeAsync()
    {
        await _output.WriteOversizedFrameAsync().ConfigureAwait(false);
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunUnknownAsync(ProcessingRunRequest request)
    {
        var valid = WorkerProtocolMapper.Map(new RunStarted(request, StartedAtUtc), 2);
        var unknown = _output.MutateUnknown(valid, _options.SelectedUnknownKind!.Value);
        await _output.WriteFrameAsync(unknown).ConfigureAwait(false);
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunInvalidSequenceAsync(ProcessingRunRequest request)
    {
        var invalidSequence = _options.SelectedSequenceFault == SequenceFault.Gap ? 3 : 1;
        var invalid = WorkerProtocolMapper.Map(new RunStarted(request, StartedAtUtc), invalidSequence);
        await _output.WriteFrameAsync(_output.SerializeMapped(invalid)).ConfigureAwait(false);
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunTerminalMismatchAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, 0).ConfigureAwait(false);
        var outcome = _options.SelectedTerminalKind!.Value switch
        {
            TerminalKind.Completed => ProcessingRunOutcome.Completed,
            TerminalKind.Cancelled => ProcessingRunOutcome.Cancelled,
            TerminalKind.Failed => ProcessingRunOutcome.Failed,
            _ => throw new InvalidOperationException("The selected terminal kind is not supported.")
        };

        await EmitTerminalAsync(request, outcome, 4, 0, 0, 0, 0).ConfigureAwait(false);
        _output.AssertComplete();
        return _options.ExitCode!.Value;
    }

    private async Task<int> RunStandardErrorFloodAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, 1).ConfigureAwait(false);
        await WriteStandardErrorFloodAsync(_options.StandardErrorBytes!.Value).ConfigureAwait(false);

        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new ActivityStarted(request, request.RunId, "fixture-activity"),
            4,
            StartedAtUtc.AddTicks(2))).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new LogEmitted(request, ProcessingLogLevel.Information, Marker("stderr-flood", request.RunId)),
            5,
            StartedAtUtc.AddTicks(3))).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new ProgressChanged(request, new ProcessingProgress(1, 1, 0, 0)),
            6,
            StartedAtUtc.AddTicks(4))).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new ActivityEnded(request, request.RunId),
            7,
            StartedAtUtc.AddTicks(5))).ConfigureAwait(false);
        await EmitTerminalAsync(request, ProcessingRunOutcome.Completed, 8, 1, 1, 0, 0).ConfigureAwait(false);
        _output.AssertComplete();
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunCooperativeCancelAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, 0).ConfigureAwait(false);
        await EmitLogAsync(request, 4, Marker("cooperative-cancel", request.RunId)).ConfigureAwait(false);

        var cancel = await _input.ReadCancelOrEndAsync().ConfigureAwait(false);
        if (cancel is null)
        {
            throw new FixtureInputException("Controller input ended before cooperative cancel.");
        }

        await EmitTerminalAsync(request, ProcessingRunOutcome.Cancelled, 5, 0, 0, 0, 0).ConfigureAwait(false);
        _output.AssertComplete();
        return WorkerProcessExitCodes.Cancelled;
    }

    private async Task<int> RunUnresponsiveAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, 0).ConfigureAwait(false);
        await EmitLogAsync(request, 4, Marker("unresponsive", request.RunId)).ConfigureAwait(false);

        var cancel = await _input.ReadCancelOrEndAsync().ConfigureAwait(false);
        var observation = cancel is null
            ? $"fixture:input-closed:{request.RunId:D}"
            : $"fixture:cancel-observed:{request.RunId:D}";
        await EmitLogAsync(request, 5, observation).ConfigureAwait(false);

        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await never.Task.ConfigureAwait(false);
        return WorkerProcessExitCodes.InfrastructureFailure;
    }

    private async Task EmitStartedAndEligibilityAsync(ProcessingRunRequest request, long eligibleCount)
    {
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(new RunStarted(request, StartedAtUtc), 2)).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new EligibilityDetermined(request, eligibleCount),
            3,
            StartedAtUtc.AddTicks(1))).ConfigureAwait(false);
    }

    private async Task EmitLogAsync(ProcessingRunRequest request, long sequence, string message)
    {
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new LogEmitted(request, ProcessingLogLevel.Information, message),
            sequence,
            StartedAtUtc.AddTicks(sequence - 2))).ConfigureAwait(false);
    }

    private async Task EmitTerminalAsync(
        ProcessingRunRequest request,
        ProcessingRunOutcome outcome,
        long sequence,
        long processedCount,
        long updatedCount,
        long skippedCount,
        long failedCount)
    {
        var failureMessage = outcome == ProcessingRunOutcome.Failed ? "fixture failure" : null;
        var result = new ProcessingRunResult(
            request,
            StartedAtUtc,
            EndedAtUtc,
            processedCount,
            updatedCount,
            skippedCount,
            failedCount,
            outcome,
            failureMessage);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(new RunFinished(request, result), sequence)).ConfigureAwait(false);
    }

    private async Task CaptureExecuteAsync(byte[] frame)
    {
        if (_options.CaptureName is null)
        {
            return;
        }

        var finalPath = Path.Combine(_options.ResourceRoot, _options.CaptureName);
        var temporaryPath = Path.Combine(
            _options.ResourceRoot,
            $".{_options.CaptureName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                }))
            {
                await stream.WriteAsync(frame).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, finalPath, overwrite: false);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }

            throw;
        }
    }

    private async Task WriteStandardErrorFloodAsync(int totalBytes)
    {
        var prefix = Encoding.UTF8.GetBytes(StandardErrorPrefix);
        var suffix = Encoding.UTF8.GetBytes(StandardErrorSuffix);
        var bodyBytes = totalBytes - prefix.Length - suffix.Length;
        if (bodyBytes < 0)
        {
            throw new InvalidOperationException("The requested stderr size cannot contain its fixed markers.");
        }

        await _standardError.WriteAsync(prefix).ConfigureAwait(false);
        var buffer = new byte[4096];
        var bodyOffset = 0;
        while (bodyOffset < bodyBytes)
        {
            var count = Math.Min(buffer.Length, bodyBytes - bodyOffset);
            for (var index = 0; index < count; index++)
            {
                buffer[index] = (byte)('a' + (bodyOffset + index) % 26);
            }

            await _standardError.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
            bodyOffset += count;
        }

        await _standardError.WriteAsync(suffix).ConfigureAwait(false);
        await _standardError.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteStandardErrorAsync(ReadOnlyMemory<byte> bytes)
    {
        await _standardError.WriteAsync(bytes).ConfigureAwait(false);
        await _standardError.FlushAsync().ConfigureAwait(false);
    }

    private static string Marker(string scenario, Guid runId)
    {
        return $"fixture:{scenario}:{runId:D}";
    }
}
