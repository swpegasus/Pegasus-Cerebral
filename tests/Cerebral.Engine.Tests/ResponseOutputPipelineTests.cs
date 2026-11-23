using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Routing;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Routing;
using Cerebral.Engine.Workflows;

namespace Cerebral.Engine.Tests;

/// <summary>Deterministic hook fixtures and temporary SQLite; no model, network, or desktop runtime.</summary>
public static class ResponseOutputPipelineTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, Json);

    public static async Task RunAsync()
    {
        await RoutesBoundedPresentationAndPreservesOriginalAsync();
        RejectsInvalidRegistries();
        await RejectsOversizedPresentationAsync();
        await HoldsLateResponseWithoutAdoptionAsync();
        await PropagatesCancellationWithoutLateAdoptionAsync();
        await CommitsSourceAndPresentationBeforeObservingAsync();
    }

    private static async Task RoutesBoundedPresentationAndPreservesOriginalAsync()
    {
        var order = new List<string>();
        var first = new ResponseHook(new("fixture.output:format", "1.2.0", OutputHookBoundary.ResponseBeforeCommit,
            before: ["fixture.output:route"]), (context, _) =>
        {
            order.Add("format");
            Check(Summary(context.Input.OriginalContent) == "original" && Summary(context.Input.Content) == "original",
                "response hook receives the immutable original and current presentation");
            return ValueTask.FromResult(ResponseOutputHookResult.Continue(
                [new StructuredContentPart("workflow.result", Data(new { summary = "presented" }))]));
        });
        var second = new ResponseHook(new("fixture.output:route", "2.0.0", OutputHookBoundary.ResponseBeforeCommit),
            (context, _) =>
            {
                order.Add("route");
                Check(Summary(context.Input.OriginalContent) == "original" && Summary(context.Input.Content) == "presented",
                    "later hooks see adopted presentation while source remains unchanged");
                return ValueTask.FromResult(ResponseOutputHookResult.Continue(humanVisibility: HumanVisibility.Sidebar));
            });
        var pipeline = new ResponseOutputPipeline([second, first]);
        var outcome = await pipeline.RouteResponseAsync(Request("ordered", HumanVisibility.Transcript));

        Check(outcome.Disposition == RouteDisposition.Continue && outcome.Snapshot.Derived &&
            Summary(outcome.Snapshot.OriginalContent) == "original" && Summary(outcome.Snapshot.Content) == "presented" &&
            outcome.Snapshot.HumanVisibility == HumanVisibility.Sidebar,
            "validated transformation and human route are adopted without replacing source content");
        Check(order.SequenceEqual(["format", "route"]) &&
            outcome.Snapshot.InvokedHooks.Select(item => item.Id + "@" + item.Version)
                .SequenceEqual(["fixture.output:format@1.2.0", "fixture.output:route@2.0.0"]),
            "response hooks use deterministic dependency order and snapshot exact versions");

        var unchanged = await new ResponseOutputPipeline().RouteResponseAsync(Request("unchanged", HumanVisibility.Activity));
        Check(!unchanged.Snapshot.Derived && unchanged.Snapshot.HumanVisibility == HumanVisibility.Activity,
            "an untransformed response preserves its current visibility");
    }

    private static void RejectsInvalidRegistries()
    {
        Throws<RouteConfigurationException>(() => new ResponseOutputPipeline([
            Passive("fixture.output:duplicate"), Passive("fixture.output:duplicate")]),
            "duplicate output-hook IDs are rejected");
        Throws<RouteConfigurationException>(() => new ResponseOutputPipeline([
            new ResponseHook(new("fixture.output:missing", "1.0.0", OutputHookBoundary.ResponseBeforeCommit,
                after: ["fixture.output:absent"]), Continue)]),
            "missing output-hook dependencies are rejected");
        Throws<RouteConfigurationException>(() => new ResponseOutputPipeline([
            new ResponseHook(new("fixture.output:wrong", "1.0.0", OutputHookBoundary.OutputAfterCommit), Continue)]),
            "a response hook cannot register in the committed-output boundary");
    }

    private static async Task HoldsLateResponseWithoutAdoptionAsync()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ResponseOutputPipeline([
            new ResponseHook(new("fixture.output:slow", "1.0.0", OutputHookBoundary.ResponseBeforeCommit,
                timeout: TimeSpan.FromMilliseconds(25)), async (_, _) =>
            {
                await Task.Delay(100);
                completed.TrySetResult();
                return ResponseOutputHookResult.Continue(
                    [new StructuredContentPart("workflow.result", Data(new { summary = "late" }))], HumanVisibility.Hidden);
            })
        ], options: new() { DefaultResponseTimeout = TimeSpan.FromSeconds(1), DefaultObserverTimeout = TimeSpan.FromSeconds(1),
            MaximumHookTimeout = TimeSpan.FromSeconds(2) });

        var outcome = await pipeline.RouteResponseAsync(Request("late", HumanVisibility.Transcript));
        Check(outcome.Disposition == RouteDisposition.Hold && !outcome.Snapshot.Derived &&
            Summary(outcome.Snapshot.Content) == "original" && outcome.Snapshot.HumanVisibility == HumanVisibility.Transcript,
            "a timed-out response hook holds before commit without adopting any late result");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(Summary(outcome.Snapshot.Content) == "original", "late completion cannot mutate the held snapshot");
    }

    private static async Task RejectsOversizedPresentationAsync()
    {
        var oversized = new string('x', ResponseOutputHookContract.MaximumContentUtf8Bytes);
        var pipeline = new ResponseOutputPipeline([
            new ResponseHook(new("fixture.output:oversized", "1.0.0", OutputHookBoundary.ResponseBeforeCommit), (_, _) =>
                ValueTask.FromResult(ResponseOutputHookResult.Continue([new TextContentPart(oversized)])))
        ]);
        var outcome = await pipeline.RouteResponseAsync(Request("oversized", HumanVisibility.Transcript));
        Check(outcome.Disposition == RouteDisposition.Hold && !outcome.Snapshot.Derived &&
            Summary(outcome.Snapshot.Content) == "original",
            "oversized presentation holds before commit and preserves the bounded original");
    }

    private static async Task PropagatesCancellationWithoutLateAdoptionAsync()
    {
        ResponseOutputSnapshot? captured = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ResponseOutputPipeline([
            new ResponseHook(new("fixture.output:cancel", "1.0.0", OutputHookBoundary.ResponseBeforeCommit), async (context, _) =>
            {
                captured = context.Input;
                await Task.Delay(100);
                completed.TrySetResult();
                return ResponseOutputHookResult.Continue([new TextContentPart("late cancellation replacement")]);
            })
        ]);
        using var cancellation = new CancellationTokenSource(20);
        await ThrowsAsync<OperationCanceledException>(() => pipeline.RouteResponseAsync(
            Request("cancelled", HumanVisibility.Transcript), cancellation.Token),
            "caller cancellation propagates from response routing");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(captured is { Derived: false } && Summary(captured.OriginalContent) == "original" &&
            Summary(captured.Content) == "original",
            "cancelled late response cannot mutate the immutable pre-invocation snapshot");
    }

    private static async Task CommitsSourceAndPresentationBeforeObservingAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-output-hook-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var journal = new EventJournal(Path.Combine(root, "engine.db"));
            var store = new WorkflowStore(journal);
            var registry = new WorkflowRegistry([new FinalModule()]);
            var observed = new RecordingObserver();
            var failed = new ThrowingObserver();
            var pipeline = new ResponseOutputPipeline([
                new ResponseHook(new("fixture.output:present", "1.0.0", OutputHookBoundary.ResponseBeforeCommit), (_, _) =>
                    ValueTask.FromResult(ResponseOutputHookResult.Continue(
                        [new StructuredContentPart("workflow.result", Data(new { summary = "presented" }))])))
            ], [observed, failed]);
            var reported = new List<AttributedOutputDiagnostic>();
            var runtime = new WorkflowRuntime(store, registry, responseOutput: pipeline, outputDiagnostic: reported.Add);
            var submitted = store.Submit(new("output-run", "output-conversation", "profile", FinalDefinition.Id,
                FinalDefinition.Version, "show the result", "hidden assignment", Data(new { }), Data(new { })));

            var completed = await runtime.RunAsync(submitted.Id);
            Check(completed.Status == WorkflowStatus.Completed &&
                completed.Result?.GetProperty("summary").GetString() == "original",
                "response transformation does not replace the authoritative workflow result");
            var events = journal.ReadPage("output-conversation", limit: 100).Events;
            var source = events.Single(item => item.Type == "workflow.result.source");
            var presentation = events.Single(item => item.Type == "workflow.result");
            Check(source.Projection.Human == HumanVisibility.Hidden && source.Projection.Model == ModelInclusion.Evidence &&
                Summary(source.Content) == "original" && EventProjection.IncludeInModel(source),
                "transformed source commits hidden from humans while remaining available to model context");
            Check(presentation.Projection.Human == HumanVisibility.Transcript && presentation.Projection.Model == ModelInclusion.Never &&
                Summary(presentation.Content) == "presented" && presentation.ParentEventId == source.Id &&
                !EventProjection.IncludeInModel(presentation),
                "derived presentation commits as a linked human-visible model-never projection");
            Check(observed.Context is { } context && context.SourceEvent.Sequence > 0 &&
                context.PresentationEvent.Sequence > context.SourceEvent.Sequence && completed.Revision > 1,
                "committed-output observers receive exact committed events only after the transaction");
            Check(reported.Any(item => item.Code == "fixture.notice" && !item.Failure) &&
                reported.Any(item => item.Code == "observer.failed" && item.Failure) && completed.Status == WorkflowStatus.Completed,
                "observer diagnostics use the host callback while observer failure cannot rewrite or fail committed output");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ResponseOutputRequest Request(string id, HumanVisibility visibility) =>
        new(id, "conversation", id, [new StructuredContentPart("workflow.result", Data(new { summary = "original" }))],
            new(ModelInclusion.Evidence, visibility));

    private static string? Summary(IReadOnlyList<ContentPart> content) => content.OfType<StructuredContentPart>()
        .Single().Value.GetProperty("summary").GetString();
    private static ResponseHook Passive(string id) => new(new(id, "1.0.0", OutputHookBoundary.ResponseBeforeCommit), Continue);
    private static ValueTask<ResponseOutputHookResult> Continue(ResponseOutputHookContext _, CancellationToken __) =>
        ValueTask.FromResult(ResponseOutputHookResult.Continue());

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Response/output hook test failed: " + message + ".");
        Console.WriteLine("PASS: " + message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { Console.WriteLine("PASS: " + message); return; }
        throw new InvalidOperationException("Response/output hook test failed: " + message + ".");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { Console.WriteLine("PASS: " + message); return; }
        throw new InvalidOperationException("Response/output hook test failed: " + message + ".");
    }

    private sealed class ResponseHook(OutputHookDescriptor descriptor,
        Func<ResponseOutputHookContext, CancellationToken, ValueTask<ResponseOutputHookResult>> invoke) : IResponseOutputHook
    {
        public OutputHookDescriptor Descriptor { get; } = descriptor;
        public ValueTask<ResponseOutputHookResult> InvokeAsync(ResponseOutputHookContext context, CancellationToken cancellationToken) =>
            invoke(context, cancellationToken);
    }

    private sealed class RecordingObserver : ICommittedOutputObserver
    {
        public OutputHookDescriptor Descriptor { get; } = new("fixture.output:observe", "1.0.0", OutputHookBoundary.OutputAfterCommit,
            before: ["fixture.output:throw"]);
        public CommittedOutputContext? Context { get; private set; }
        public ValueTask<OutputObserverResult> ObserveAsync(CommittedOutputContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return ValueTask.FromResult(new OutputObserverResult([new("fixture.notice", "Committed output was observed.")]));
        }
    }

    private sealed class ThrowingObserver : ICommittedOutputObserver
    {
        public OutputHookDescriptor Descriptor { get; } = new("fixture.output:throw", "1.0.0", OutputHookBoundary.OutputAfterCommit);
        public ValueTask<OutputObserverResult> ObserveAsync(CommittedOutputContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Intentional observer failure.");
    }

    private sealed record FinalModule : IWorkflowModule
    {
        public string Id => "fixture.output";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new FinalDefinition()];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
    }

    private sealed class FinalDefinition : IWorkflowDefinition
    {
        public const string Id = "fixture.output:final";
        public const string Version = "1.0.0";
        public WorkflowDefinitionDescriptor Descriptor { get; } = new(Id, Version, "Complete with a deterministic result.",
            Data(new { type = "object", additionalProperties = false }), []);
        public WorkflowStep Advance(WorkflowRun snapshot) => new(Data(new { done = true }),
            new CompleteWorkflow(Data(new { summary = "original" })));
    }
}
