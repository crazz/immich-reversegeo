using System.Reflection;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable BL0006

namespace ImmichReverseGeo.Tests.LookupWorkerRouting;

[TestClass]
[TestCategory("Change49")]
[DoNotParallelize]
public sealed class LookupPageRenderingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task Lookup_RendersLifecycleControlsRetainedResultsSafeErrorsAndBoundedDiagnostics()
    {
        string configDirectory = Path.Combine(
            Path.GetTempPath(),
            $"immich-reversegeo-change49-render-{Guid.NewGuid():N}");
        var lifetime = new CoordinateLookupPageControllerHostLifetime();
        var factory = new CoordinateLookupPageControllerFactory(
            new NeverAdmissionGate(),
            new NeverWorkerClient(),
            new RenderingSettingsProvider(),
            lifetime);
        var page = new ImmichReverseGeo.Web.Components.Pages.Lookup();
        SetInjected(page, "ControllerFactory", factory);
        SetInjected(
            page,
            "Config",
            new ConfigService(NullLogger<ConfigService>.Instance, configDirectory));
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(page);

        WebStatusRenderingTests.RenderSnapshot idle = await renderer.ReadAsync();
        Assert.AreEqual(0, DisabledMutableControlCount(idle.Frames));
        Assert.IsFalse(HasButton(idle.Frames, "Cancel"));

        CoordinateLookupPageController controller = GetController(page);
        foreach ((
            CoordinateLookupPagePhase Phase,
            string Label,
            string? Activity,
            int DisabledControls,
            bool ShowCancel) row in new[]
        {
            (CoordinateLookupPagePhase.Admitting, "Checking isolated worker availability.", (string?)null, 7, false),
            (CoordinateLookupPagePhase.Running, "The isolated lookup worker is running.", "Rendering activity", 7, true),
            (CoordinateLookupPagePhase.Cancelled, "The lookup was cancelled.", (string?)null, 0, false),
            (CoordinateLookupPagePhase.Unavailable, "The isolated lookup worker is unavailable.", (string?)null, 0, false)
        })
        {
            await RenderStateAsync(
                renderer,
                controller,
                new CoordinateLookupPageState(
                    row.Phase,
                    $"{row.Phase} status",
                    null,
                    row.Phase == CoordinateLookupPagePhase.Running
                        ? "11111111-2222-3333-4444-555555555555"
                        : null,
                    null,
                    row.Activity,
                    null,
                    false,
                    row.Phase == CoordinateLookupPagePhase.Running,
                    row.Phase == CoordinateLookupPagePhase.Cancelled));
            WebStatusRenderingTests.RenderSnapshot lifecycle = await renderer.ReadAsync();
            StringAssert.Contains(lifecycle.Text, row.Label);
            Assert.AreEqual(row.DisabledControls, DisabledMutableControlCount(lifecycle.Frames));
            Assert.AreEqual(
                row.ShowCancel,
                HasButton(lifecycle.Frames, "Cancel", disabled: false));
            if (row.Activity is not null)
            {
                StringAssert.Contains(lifecycle.Text, $"Current activity: {row.Activity}");
            }
        }

        await RenderStateAsync(
            renderer,
            controller,
            new CoordinateLookupPageState(
                CoordinateLookupPagePhase.Starting,
                "Starting isolated lookup…",
                null,
                "11111111-2222-3333-4444-555555555555",
                null,
                null,
                null,
                false,
                true,
                false));
        WebStatusRenderingTests.RenderSnapshot starting = await renderer.ReadAsync();
        StringAssert.Contains(starting.Text, "Starting the isolated lookup worker.");
        Assert.AreEqual(7, DisabledMutableControlCount(starting.Frames));
        Assert.IsTrue(HasButton(starting.Frames, "Cancel", disabled: false));

        await RenderStateAsync(
            renderer,
            controller,
            new CoordinateLookupPageState(
                CoordinateLookupPagePhase.CancelRequested,
                "Cancelling lookup…",
                null,
                "11111111-2222-3333-4444-555555555555",
                CoordinateLookupProgressStep.Country,
                "Loading cached divisions",
                null,
                false,
                true,
                false));
        WebStatusRenderingTests.RenderSnapshot cancelling = await renderer.ReadAsync();
        StringAssert.Contains(
            cancelling.Text,
            "Waiting for the active lookup worker to stop and finish cleanup.");
        Assert.AreEqual(8, DisabledMutableControlCount(cancelling.Frames));
        Assert.IsTrue(HasButton(cancelling.Frames, "Cancelling…", disabled: true));

        CoordinateLookupResult boundedResult = Result();
        await RenderStateAsync(
            renderer,
            controller,
            new CoordinateLookupPageState(
                CoordinateLookupPagePhase.Completed,
                "Lookup completed.",
                null,
                "11111111-2222-3333-4444-555555555555",
                null,
                null,
                boundedResult,
                false,
                false,
                true));
        WebStatusRenderingTests.RenderSnapshot completed = await renderer.ReadAsync();
        StringAssert.Contains(completed.Text, "The lookup completed.");
        Assert.AreEqual(0, DisabledMutableControlCount(completed.Frames));
        Assert.IsFalse(HasButton(completed.Frames, "Cancel"));
        AssertBoundedFactsVisible(completed.Text);

        await RenderStateAsync(
            renderer,
            controller,
            new CoordinateLookupPageState(
                CoordinateLookupPagePhase.Busy,
                "Lookup could not start because another background job is running. Try again after it finishes.",
                null,
                null,
                null,
                null,
                boundedResult,
                true,
                false,
                false));
        WebStatusRenderingTests.RenderSnapshot busy = await renderer.ReadAsync();
        StringAssert.Contains(
            busy.Text,
            "Lookup could not start because another background job is running.");
        StringAssert.Contains(
            busy.Text,
            "Showing the last completed lookup while the latest attempt was not admitted");

        const string hostilePrefix = "hostile <script>alert(2)</script> ";
        string hostileError = string.Concat(
            hostilePrefix,
            new string(
                'x',
                WorkerJobProtocolV2.MaxSafeTextLength - hostilePrefix.Length));
        await RenderStateAsync(
            renderer,
            controller,
            new CoordinateLookupPageState(
                CoordinateLookupPagePhase.Failed,
                "Lookup failed.",
                hostileError,
                null,
                null,
                null,
                Result(zeroBounds: true, gadmUnavailable: true),
                true,
                false,
                true));
        WebStatusRenderingTests.RenderSnapshot failed = await renderer.ReadAsync();
        AssertZeroBoundsStayQuiet(failed.Text);
        StringAssert.Contains(failed.Text, CoordinateLookupGadmAttribution.UsageNotice);
        StringAssert.Contains(failed.Text, "Official GADM license");
        StringAssert.Contains(
            failed.Text,
            "GADM data is unavailable: hostile <script>alert(1)</script>");
        Assert.IsTrue(failed.Frames.Any(frame =>
            frame.FrameType == RenderTreeFrameType.Text
            && string.Equals(frame.TextContent, hostileError, StringComparison.Ordinal)));
        Assert.IsFalse(failed.Frames.Any(frame =>
            frame.FrameType == RenderTreeFrameType.Markup
            && (frame.MarkupContent?.Contains("<script>", StringComparison.Ordinal) ?? false)));
        StringAssert.Contains(
            failed.Text,
            "The lookup failed safely.");
    }

    private static async Task RenderStateAsync(
        WebStatusRenderingTests.ComponentRenderer renderer,
        CoordinateLookupPageController controller,
        CoordinateLookupPageState state)
    {
        typeof(CoordinateLookupPageController)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller, state);
        Task rendered = renderer.NextRenderAsync();
        var changed = (Action)typeof(CoordinateLookupPageController)
            .GetField("_stateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(controller)!;
        changed();
        await rendered.WaitAsync(Bound);
        await renderer.Dispatcher.InvokeAsync(() => { });
    }

    private static void AssertBoundedFactsVisible(string text)
    {
        StringAssert.Contains(text, "7 additional trace lines omitted by the worker transport bound.");
        StringAssert.Contains(text, "8 result text values truncated by the worker transport bound.");
        foreach (var offset in new[] { 0, 10, 20, 30 })
        {
            StringAssert.Contains(
                text,
                $"{4 + offset} additional candidates omitted by the worker transport bound.");
            StringAssert.Contains(
                text,
                $"{5 + offset} additional cache statuses omitted by the worker transport bound.");
            StringAssert.Contains(
                text,
                $"{6 + offset} source text values truncated by the worker transport bound.");
            StringAssert.Contains(
                text,
                $"{2 + offset} additional record sources omitted by the worker transport bound.");
            StringAssert.Contains(
                text,
                $"{3 + offset} candidate text values truncated by the worker transport bound.");
        }
    }

    private static void AssertZeroBoundsStayQuiet(string text)
    {
        Assert.IsFalse(text.Contains("additional trace lines omitted", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("result text values truncated", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("additional candidates omitted", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("additional cache statuses omitted", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("source text values truncated", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("additional record sources omitted", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("candidate text values truncated", StringComparison.Ordinal));
    }

    private static int DisabledMutableControlCount(RenderTreeFrame[] frames)
    {
        return Elements(frames)
            .Count(element =>
                element.Name is "input" or "button"
                && HasAttribute(frames, element, "disabled", true));
    }

    private static bool HasButton(
        RenderTreeFrame[] frames,
        string text,
        bool? disabled = null)
    {
        return Elements(frames).Any(element =>
            element.Name == "button"
            && string.Concat(frames
                .Skip(element.Index)
                .Take(element.Length)
                .Where(frame => frame.FrameType == RenderTreeFrameType.Text)
                .Select(frame => frame.TextContent))
                .Contains(text, StringComparison.Ordinal)
            && (disabled is null
                || HasAttribute(frames, element, "disabled", true) == disabled.Value));
    }

    private static bool HasAttribute(
        RenderTreeFrame[] frames,
        Element element,
        string name,
        bool value)
    {
        return frames
            .Skip(element.Index + 1)
            .Take(element.Length - 1)
            .TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
            .Any(frame => string.Equals(frame.AttributeName, name, StringComparison.Ordinal)
                && Equals(frame.AttributeValue, value));
    }

    private static IEnumerable<Element> Elements(RenderTreeFrame[] frames)
    {
        for (var index = 0; index < frames.Length; index++)
        {
            RenderTreeFrame frame = frames[index];
            if (frame.FrameType == RenderTreeFrameType.Element)
            {
                yield return new Element(index, frame.ElementName, frame.ElementSubtreeLength);
            }
        }
    }

    private static CoordinateLookupPageController GetController(object page)
    {
        return (CoordinateLookupPageController)page.GetType()
            .GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page)!;
    }

    private static void SetInjected(object page, string name, object value)
    {
        page.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(page, value);
    }

    private static CoordinateLookupResult Result(
        bool zeroBounds = false,
        bool gadmUnavailable = false)
    {
        DateTimeOffset started = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        CoordinateLookupSourceResult overture = Source("Overture", 0, zeroBounds);
        CoordinateLookupSourceResult gadm = Source(
            "GADM",
            10,
            zeroBounds,
            gadmUnavailable
                ? new WorkerJobSafeError(
                    "gadm-unavailable",
                    WorkerJobFailureCategory.Dependency,
                    "hostile <script>alert(1)</script>")
                : null,
            CoordinateLookupGadmAttribution.DatasetName,
            CoordinateLookupGadmAttribution.LicenseUrl,
            CoordinateLookupGadmAttribution.UsageNotice);
        CoordinateLookupSourceResult airport = Source("Airport", 20, zeroBounds);
        CoordinateLookupSourceResult places = Source("Places", 30, zeroBounds);
        return new CoordinateLookupResult(
            new CoordinateLookupRequest(
                47.4,
                8.5,
                true,
                true,
                true,
                new CoordinateLookupCityResolverOverrides(null, [])),
            started,
            started.AddSeconds(1),
            new CoordinateLookupCountryResult(
                CoordinateLookupCountryStatus.Matched,
                "USA",
                "US",
                "United States",
                "country",
                null),
            overture,
            gadm,
            airport,
            places,
            new CoordinateLookupAdministrativeResult("State", "City"),
            new CoordinateLookupAdministrativeResult("GADM State", "GADM City"),
            new CoordinateLookupProfileSummary(
                "USA",
                ["locality"],
                CoordinateLookupTieBreak.SmallestArea),
            ["trace"],
            new CoordinateLookupFinalLocation(
                new CoordinateLookupAttributedValue(
                    "United States",
                    CoordinateLookupFinalSource.BundledCountryDivisions),
                new CoordinateLookupAttributedValue(
                    "State",
                    CoordinateLookupFinalSource.CachedOvertureDivisions),
                new CoordinateLookupAttributedValue(
                    "City",
                    CoordinateLookupFinalSource.CachedOvertureDivisions)),
            omittedTraceCount: zeroBounds ? 0 : 7,
            truncatedTextCount: zeroBounds ? 0 : 8);
    }

    private static CoordinateLookupSourceResult Source(
        string name,
        int offset,
        bool zeroBounds,
        WorkerJobSafeError? error = null,
        string? attribution = null,
        string? licenseUrl = null,
        string? usageNotice = null)
    {
        CoordinateLookupCandidate candidate = Candidate(name, offset, zeroBounds);
        return new CoordinateLookupSourceResult(
            error is null
                ? CoordinateLookupSourceState.Ready
                : CoordinateLookupSourceState.Unavailable,
            "2026-09",
            name == "GADM" ? "4.1" : null,
            candidate,
            [candidate],
            [new CoordinateLookupCacheStatus(
                "USA",
                error is null
                    ? CoordinateLookupSourceState.Ready
                    : CoordinateLookupSourceState.Failed,
                error)],
            error,
            attribution,
            licenseUrl,
            usageNotice,
            omittedCandidateCount: zeroBounds ? 0 : 4 + offset,
            omittedCacheCount: zeroBounds ? 0 : 5 + offset,
            truncatedTextCount: zeroBounds ? 0 : 6 + offset);
    }

    private static CoordinateLookupCandidate Candidate(
        string name,
        int offset,
        bool zeroBounds)
    {
        return new CoordinateLookupCandidate(
            name.ToLowerInvariant(),
            $"{name} candidate",
            true,
            "selected",
            null,
            null,
            "division_area",
            "locality",
            null,
            null,
            null,
            1,
            null,
            null,
            "United States",
            null,
            null,
            null,
            null,
            null,
            null,
            ["fixture"],
            omittedRecordSourceCount: zeroBounds ? 0 : 2 + offset,
            truncatedTextCount: zeroBounds ? 0 : 3 + offset);
    }

    private sealed record Element(int Index, string Name, int Length);

    private sealed class RenderingSettingsProvider : ICoordinateLookupSettingsSnapshotProvider
    {
        public ValueTask<CoordinateLookupCityResolverOverrides> GetAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CoordinateLookupCityResolverOverrides(null, []));
    }

    private sealed class NeverAdmissionGate : IWorkerJobAdmissionGate
    {
        public WorkerJobAdmissionResult TryAdmit(WorkerJobDispatch dispatch) =>
            throw new AssertFailedException("Rendering does not admit a worker.");
    }

    private sealed class NeverWorkerClient : ICoordinateLookupWorkerClient
    {
        public ValueTask<CoordinateLookupWorkerStartResult> StartAsync(
            IWorkerJobAdmissionLease admission,
            CoordinateLookupRequest request,
            IWorkerJobEventSink eventSink,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("Rendering does not start a worker.");
    }
}
