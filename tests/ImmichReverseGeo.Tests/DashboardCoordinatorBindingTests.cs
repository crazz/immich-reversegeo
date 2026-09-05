using System.Reflection;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable BL0006

namespace ImmichReverseGeo.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DashboardCoordinatorBindingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    [DataRow(ProcessingRunAdmissionResult.Accepted)]
    [DataRow(ProcessingRunAdmissionResult.AlreadyRunning)]
    [DataRow(ProcessingRunAdmissionResult.Stopping)]
    public async Task RunNow_ExecutesInjectedNarrowCoordinatorPromptlyAndAlwaysClearsPending(ProcessingRunAdmissionResult result)
    {
        var coordinator = new RecordingManualCoordinator();
        var call = coordinator.Enqueue();
        var component = CreateComponent(coordinator, out var readiness);
        await using var renderer = new DashboardRenderer(component);
        await renderer.AttachAsync(component);

        var run = renderer.InvokeRenderedClickAsync(0);
        await call.Entered.Task.WaitAsync(TestTimeout);
        Assert.IsTrue(GetPending(component));
        Assert.IsFalse(run.IsCompleted);
        call.Completion.TrySetResult(result);
        await run.WaitAsync(TestTimeout);

        Assert.IsFalse(GetPending(component));
        Assert.AreEqual(1, readiness.Calls);
        Assert.AreEqual(1, coordinator.TriggerCount);
        Assert.AreEqual(result, call.ObservedResult);
        Assert.IsTrue(renderer.RenderCount >= 2);
        Assert.IsTrue(renderer.PendingStates.Contains(true));
        Assert.IsFalse(renderer.PendingStates.Last());
    }

    [TestMethod]
    public async Task RunNow_SetupArmDispatchOrProjectionFault_PropagatesOriginalAndClearsPendingInFinally()
    {
        foreach (var failure in new Exception[]
        {
            new InvalidOperationException("setup"),
            new InvalidOperationException("arm"),
            new InvalidOperationException("sync dispatch"),
            new InvalidOperationException("projection")
        })
        {
            var coordinator = new RecordingManualCoordinator();
            var call = coordinator.Enqueue();
            var component = CreateComponent(coordinator, out var readiness);
            await using var renderer = new DashboardRenderer(component);
            await renderer.AttachAsync(component);
            var run = renderer.InvokeRenderedClickAsync(0);
            await call.Entered.Task.WaitAsync(TestTimeout);
            call.Completion.TrySetException(failure);

            var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run.WaitAsync(TestTimeout));

            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, readiness.Calls);
            Assert.IsFalse(GetPending(component));
            Assert.IsTrue(renderer.RenderCount >= 2);
        Assert.IsTrue(renderer.PendingStates.Contains(true));
        Assert.IsFalse(renderer.PendingStates.Last());
        }
    }

    [TestMethod]
    public async Task StopBinding_ReturnsPromptlyShowsStoppingAndUsesTheSharedCoordinatorOperation()
    {
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new RecordingManualCoordinator { StopResult = stop.Task };
        var component = CreateComponent(coordinator, out var readiness);
        await using var renderer = new DashboardRenderer(component);
        await renderer.AttachAsync(component);

        var click = renderer.InvokeRenderedClickAsync(1);
        await click.WaitAsync(TestTimeout);

        Assert.AreEqual(1, coordinator.StopCount);
        Assert.AreEqual(0, coordinator.CancelCount);
        Assert.AreSame(stop.Task, GetStopOperation(component));
        StringAssert.Contains(await renderer.ReadTextAsync(), "Stopping…");
        Assert.IsFalse(stop.Task.IsCompleted, "The UI event must return after joining rather than awaiting settlement.");

        stop.TrySetResult();
        await stop.Task.WaitAsync(TestTimeout);
        Assert.IsFalse(typeof(ImmichReverseGeo.Web.Components.Pages.Dashboard)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(method => method.Name.Contains("RunOnce", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(
            new[]
            {
                nameof(IManualProcessingRunCoordinator.CancelActiveRun),
                nameof(IManualProcessingRunCoordinator.StopActiveRun),
                nameof(IManualProcessingRunCoordinator.TriggerManualAsync)
            },
            typeof(IManualProcessingRunCoordinator).GetMethods().Select(method => method.Name).Order().ToArray());
    }

    private static ImmichReverseGeo.Web.Components.Pages.Dashboard CreateComponent(
        RecordingManualCoordinator coordinator,
        out ReadinessProbe readiness)
    {
        readiness = new ReadinessProbe();
        var probe = readiness;
        var component = new ImmichReverseGeo.Web.Components.Pages.Dashboard();
        SetInjected(component, "State", new ProcessingState());
        SetInjected(component, "RunCoordinator", coordinator);
        typeof(ImmichReverseGeo.Web.Components.Pages.Dashboard)
            .GetField("_circuitReady", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, true);
        component.RunReadinessAsync = () =>
        {
            probe.Record();
            typeof(ImmichReverseGeo.Web.Components.Pages.Dashboard)
                .GetField("_dbError", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(component, null);
            return Task.CompletedTask;
        };
        return component;
    }

    private static bool GetPending(ImmichReverseGeo.Web.Components.Pages.Dashboard component)
    {
        return (bool)(typeof(ImmichReverseGeo.Web.Components.Pages.Dashboard)
            .GetField("_runPending", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(component) ?? false);
    }

    private static Task? GetStopOperation(ImmichReverseGeo.Web.Components.Pages.Dashboard component)
    {
        return (Task?)typeof(ImmichReverseGeo.Web.Components.Pages.Dashboard)
            .GetField("_stopOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(component);
    }

    private static object? GetInjected(object component, string name)
    {
        return component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(component);
    }

    private static void SetInjected(object component, string name, object value)
    {
        component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(component, value);
    }

    private sealed class DashboardRenderer : Renderer
    {
        private readonly ImmichReverseGeo.Web.Components.Pages.Dashboard _component;
        private int _componentId;

        public DashboardRenderer(ImmichReverseGeo.Web.Components.Pages.Dashboard component)
            : base(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
        {
            _component = component;
        }

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public int RenderCount { get; private set; }
        public List<bool> PendingStates { get; } = [];

        public Task AttachAsync(IComponent component)
        {
            return Dispatcher.InvokeAsync(async () =>
            {
                _componentId = AssignRootComponentId(component);
                await RenderRootComponentAsync(_componentId, ParameterView.Empty);
            });
        }

        public Task InvokeRenderedClickAsync(int clickIndex)
        {
            return Dispatcher.InvokeAsync(async () =>
            {
                var frames = GetCurrentRenderTreeFrames(_componentId);
                var callback = frames.Array
                    .Take(frames.Count)
                    .Where(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick")
                    .Select(frame => frame.AttributeValue)
                    .ElementAt(clickIndex)!;
                if (callback is Func<Task> asynchronous)
                {
                    await asynchronous().ConfigureAwait(false);
                }
                else if (callback is Action synchronous)
                {
                    synchronous();
                }
                else
                {
                    throw new AssertFailedException($"Unexpected rendered onclick type {callback.GetType().FullName}");
                }
            });
        }

        public Task<string> ReadTextAsync()
        {
            return Dispatcher.InvokeAsync(() =>
            {
                var frames = GetCurrentRenderTreeFrames(_componentId);
                return string.Concat(frames.Array
                    .Take(frames.Count)
                    .Where(frame => frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
                    .Select(frame => frame.FrameType == RenderTreeFrameType.Text
                        ? frame.TextContent
                        : frame.MarkupContent));
            });
        }

        protected override void HandleException(Exception exception)
        {
            throw exception;
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            RenderCount++;
            PendingStates.Add(GetPending(_component));
            return Task.CompletedTask;
        }
    }

    private sealed class ReadinessProbe
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public void Record() => Interlocked.Increment(ref _calls);
    }

    private sealed class RecordingManualCoordinator : IManualProcessingRunCoordinator
    {
        private readonly Queue<ManualCall> _queued = new();
        public List<ManualCall> Calls { get; } = [];
        public int TriggerCount { get; private set; }
        public int StopCount { get; private set; }
        public int CancelCount { get; private set; }
        public bool CancelResult { get; set; }
        public Task? StopResult { get; set; }

        public ManualCall Enqueue()
        {
            var call = new ManualCall();
            _queued.Enqueue(call);
            Calls.Add(call);
            return call;
        }

        public async Task<ProcessingRunAdmissionResult> TriggerManualAsync()
        {
            TriggerCount++;
            var call = _queued.Dequeue();
            call.Entered.TrySetResult();
            var result = await call.Completion.Task.ConfigureAwait(false);
            call.ObservedResult = result;
            return result;
        }

        public bool CancelActiveRun()
        {
            CancelCount++;
            return CancelResult;
        }

        public Task? StopActiveRun()
        {
            StopCount++;
            return StopResult;
        }
    }

    private sealed class ManualCall
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProcessingRunAdmissionResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProcessingRunAdmissionResult? ObservedResult { get; set; }
    }
}

#pragma warning restore BL0006
