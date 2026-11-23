using Cerebral.Engine.Routing;
using Cerebral.Contracts.Routing;

namespace Cerebral.Engine.Tests;

public static class RoutePipelineTests
{
    public static async Task RunAsync()
    {
        await OrdersHooksAndPreservesOriginalAsync();
        RejectsInvalidDependencyGraphs();
        await StopsOnHoldAndRejectAsync();
        await DoesNotAdoptLateTimedOutResultsAsync();
        await PropagatesCancellationWithoutLateAdoptionAsync();
        await RejectsInvalidHookOutputAtomicallyAsync();
    }

    private static async Task OrdersHooksAndPreservesOriginalAsync()
    {
        var observed = new List<string>();
        var hooks = new IRequestRouteHook[]
        {
            new Hook(new("sample:context", RequestRouteStage.Context), (context, _) =>
            {
                observed.Add("context");
                Check(context.Input.OriginalText == "  hello  " && context.Input.Text == "hello!",
                    "later hooks receive preserved original text and adopted current text");
                return ValueTask.FromResult(RouteHookResult.Continue(contributions:
                    [new ContextContribution("project", "selected project context")]));
            }),
            new Hook(new("sample:trim", RequestRouteStage.Transform, before: ["sample:suffix"]), (context, _) =>
            {
                observed.Add("trim");
                return ValueTask.FromResult(RouteHookResult.Continue(context.Input.Text.Trim()));
            }),
            new Hook(new("sample:suffix", RequestRouteStage.Transform), (context, _) =>
            {
                observed.Add("suffix");
                return ValueTask.FromResult(RouteHookResult.Continue(context.Input.Text + "!"));
            }),
            new Hook(new("sample:alpha", RequestRouteStage.Ingress), (context, _) =>
            {
                observed.Add("alpha");
                return ValueTask.FromResult(RouteHookResult.Continue(contributions:
                    [new LabelContribution("source", "composer")]));
            })
        };

        var pipeline = new RequestRoutePipeline(hooks);
        var outcome = await pipeline.RouteAsync(new("request-1", "  hello  "));

        Check(outcome.Disposition == RouteDisposition.Continue, "route continues");
        Check(outcome.Snapshot.OriginalText == "  hello  ", "original input is preserved");
        Check(outcome.Snapshot.Text == "hello!", "validated replacements are adopted");
        Check(observed.SequenceEqual(["alpha", "trim", "suffix", "context"]), "stage and dependency order is deterministic");
        Check(outcome.Snapshot.Contributions.Length == 2 &&
              outcome.Snapshot.Contributions[0].HookId == "sample:alpha" &&
              outcome.Snapshot.Contributions[1].HookId == "sample:context",
            "contributions retain engine-owned hook attribution");

        var reverse = new RequestRoutePipeline(hooks.Reverse());
        Check(pipeline.OrderedDescriptors.Select(x => x.Id).SequenceEqual(reverse.OrderedDescriptors.Select(x => x.Id)),
            "input enumeration order does not change hook order");
    }

    private static void RejectsInvalidDependencyGraphs()
    {
        Throws<RouteConfigurationException>(() => new RequestRoutePipeline([
            Passive("sample:duplicate"), Passive("sample:duplicate")]), "duplicate hook IDs are rejected");

        Throws<RouteConfigurationException>(() => new RequestRoutePipeline([
            new Hook(new("sample:missing", RequestRouteStage.Transform, after: ["sample:absent"]), Continue)]),
            "missing dependencies are rejected");

        Throws<RouteConfigurationException>(() => new RequestRoutePipeline([
            new Hook(new("sample:first", RequestRouteStage.Transform, after: ["sample:second"]), Continue),
            new Hook(new("sample:second", RequestRouteStage.Transform, after: ["sample:first"]), Continue)]),
            "dependency cycles are rejected");

        Throws<RouteConfigurationException>(() => new RequestRoutePipeline([
            new Hook(new("sample:ingress", RequestRouteStage.Ingress), Continue),
            new Hook(new("sample:transform", RequestRouteStage.Transform, after: ["sample:ingress"]), Continue)]),
            "cross-stage dependencies are rejected");

        Throws<RouteConfigurationException>(() => new RequestRoutePipeline([
            Passive("not-namespaced")]), "hook IDs must be namespaced");
    }

    private static async Task StopsOnHoldAndRejectAsync()
    {
        var afterHold = false;
        var holdPipeline = new RequestRoutePipeline([
            new Hook(new("sample:hold", RequestRouteStage.Ingress, before: ["sample:after"]), (_, _) => ValueTask.FromResult(
                RouteHookResult.Hold("waiting for a required selection", [new DiagnosticContribution("selection", "Required selection is missing.")]))),
            new Hook(new("sample:after", RequestRouteStage.Ingress), (_, _) => { afterHold = true; return ValueTask.FromResult(RouteHookResult.Continue()); })
        ]);
        var held = await holdPipeline.RouteAsync(new("hold-request", "text"));
        Check(held.Disposition == RouteDisposition.Hold && held.StoppedByHookId == "sample:hold" && !afterHold,
            "hold stops the route before later hooks");
        Check(held.Snapshot.Contributions.Length == 1, "valid hold contributions are adopted");

        var rejectPipeline = new RequestRoutePipeline([
            new Hook(new("sample:reject", RequestRouteStage.Ingress), (_, _) => ValueTask.FromResult(RouteHookResult.Reject("unsupported input")))
        ]);
        var rejected = await rejectPipeline.RouteAsync(new("reject-request", "text"));
        Check(rejected.Disposition == RouteDisposition.Reject && rejected.Reason == "unsupported input",
            "explicit rejection is returned");
    }

    private static async Task DoesNotAdoptLateTimedOutResultsAsync()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new RequestRoutePipeline([
            new Hook(new("sample:slow", RequestRouteStage.Transform, timeout: TimeSpan.FromMilliseconds(30)), async (_, _) =>
            {
                await Task.Delay(120);
                completed.SetResult();
                return RouteHookResult.Continue("late replacement", [new LabelContribution("late", "true")]);
            })
        ], new() { DefaultHookTimeout = TimeSpan.FromSeconds(1), MaximumHookTimeout = TimeSpan.FromSeconds(2) });

        var outcome = await pipeline.RouteAsync(new("timeout-request", "original"));
        Check(outcome.Disposition == RouteDisposition.Hold && outcome.Snapshot.Text == "original" &&
              outcome.Snapshot.Contributions.IsEmpty, "a timeout adopts none of the late result");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(outcome.Snapshot.Text == "original" && outcome.Snapshot.Contributions.IsEmpty,
            "late completion cannot mutate the returned snapshot");
    }

    private static async Task PropagatesCancellationWithoutLateAdoptionAsync()
    {
        TextRouteSnapshot? captured = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new RequestRoutePipeline([
            new Hook(new("sample:cancel", RequestRouteStage.Transform), async (context, _) =>
            {
                captured = context.Input;
                await Task.Delay(100);
                completed.SetResult();
                return RouteHookResult.Continue("late cancellation replacement");
            })
        ]);
        using var cancellation = new CancellationTokenSource(20);
        await ThrowsAsync<OperationCanceledException>(() => pipeline.RouteAsync(new("cancel-request", "original"), cancellation.Token),
            "caller cancellation propagates");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(captured is not null && captured.Text == "original" && captured.OriginalText == "original",
            "a cancelled hook held only an immutable pre-invocation snapshot");
    }

    private static async Task RejectsInvalidHookOutputAtomicallyAsync()
    {
        var pipeline = new RequestRoutePipeline([
            new Hook(new("sample:invalid", RequestRouteStage.Transform), (_, _) => ValueTask.FromResult(
                RouteHookResult.Continue("replacement", [new LabelContribution("", "bad")]))),
        ]);
        var outcome = await pipeline.RouteAsync(new("invalid-request", "original"));
        Check(outcome.Disposition == RouteDisposition.Hold && outcome.Snapshot.Text == "original" &&
              outcome.Snapshot.Contributions.IsEmpty, "an invalid result is rejected before any portion is adopted");
    }

    private static Hook Passive(string id) => new(new(id, RequestRouteStage.Ingress), Continue);
    private static ValueTask<RouteHookResult> Continue(RouteHookContext _, CancellationToken __) =>
        ValueTask.FromResult(RouteHookResult.Continue());

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Routing test failed: " + message + ".");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Routing test failed: " + message + ".");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Routing test failed: " + message + ".");
    }

    private sealed class Hook(
        RouteHookDescriptor descriptor,
        Func<RouteHookContext, CancellationToken, ValueTask<RouteHookResult>> invoke) : IRequestRouteHook
    {
        public RouteHookDescriptor Descriptor { get; } = descriptor;
        public ValueTask<RouteHookResult> InvokeAsync(RouteHookContext context, CancellationToken cancellationToken) =>
            invoke(context, cancellationToken);
    }
}
