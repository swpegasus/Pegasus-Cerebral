using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Bounded parser/event checks plus the real host-effect seam with isolated SQLite and
/// deterministic adapters. No installed Worker, provider, model inference or PAO is exercised.</summary>
public static class WorkflowGenerationPreviewTests
{
    public static Task RunAsync() => RunCoreAsync(qualifiedCleanup: false);

    // The selected candidate compiles the qualified cleanup overlay; keep its result contract
    // explicit without making ordinary source tests depend on candidate-only implementation types.
    internal static Task RunQualifiedAsync() => RunCoreAsync(qualifiedCleanup: true);

    private static async Task RunCoreAsync(bool qualifiedCleanup)
    {

        var checks = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Generation preview: " + message); checks++; }
        WorkflowGenerationPreviewParser Parser(string text, params string[] operations)
        { var parser = new WorkflowGenerationPreviewParser(operations); parser.Append(text); return parser; }
        void Empty(string text)
        {
            var parser = Parser(text, "file.read");
            Check(parser.Read() is null, "nonpublic input must not be projected");
        }

        var partial = Parser("{\"action\":\"final\",\"text\":\"Hello");
        Check(partial.Read() == new WorkflowGenerationPreviewValue("final", Text: "Hello"), "an unfinished public text string is decoded");
        partial.Append(" world\"}");
        Check(partial.Read()?.Text == "Hello world", "a later snapshot includes the completed answer");
        var unicode = Parser("{\"action\":\"final\",\"text\":\"caf\\u00");
        Check(unicode.Read()?.Text == "caf", "a split Unicode escape holds back only its incomplete suffix");
        unicode.Append("e9\\nC:\\\\Notes\\\\one\"}");
        Check(unicode.Read()?.Text == "café\nC:\\Notes\\one", "escaped Unicode, newlines and paths use normal decoded text");
        var escaped = Parser("{\"action\":\"final\",\"text\":\"A \\\"");
        Check(escaped.Read()?.Text == "A \"", "a completed escaped quote inside an unfinished value is visible");
        escaped.Append("quote\\\"\"}");
        Check(escaped.Read()?.Text == "A \"quote\"", "escaped quotes remain unambiguous");
        var pair = Parser("{\"action\":\"final\",\"text\":\"A\\uD83D");
        Check(pair.Read()?.Text == "A", "a split encoded surrogate does not emit an invalid Unicode value");
        pair.Append("\\uDE00\"}");
        Check(pair.Read()?.Text == "A😀", "the completed encoded surrogate pair is decoded once");
        var rawPair = Parser("{\"action\":\"final\",\"text\":\"A" + '\ud83d');
        Check(rawPair.Read() is null && !rawPair.Disabled, "a callback split through a raw surrogate waits safely");
        rawPair.Append("\ude00\"}");
        Check(rawPair.Read()?.Text == "A😀", "a raw split pair becomes valid after the next fragment");
        var reordered = Parser("{\"text\":\"Answer\",\"action\":\"final\"}");
        Check(reordered.Read()?.Text == "Answer", "complete valid reordered fields can be projected");
        var missingAction = Parser("{\"text\":\"Not yet public");
        Check(missingAction.Read() is null, "text is not public until its action is recognized");

        Empty("Plain model text containing a private diagnostic");
        Empty("{\"action\":\"unknown\",\"text\":\"Do not expose\"}");
        Empty("{\"action\":\"final\",\"analysis\":\"private\",\"text\":\"Answer\"}");
        Empty("{\"action\":\"final\",\"text\":{\"nested\":\"Do not expose\"}}");
        Empty("{\"action\":\"tool\",\"arguments\":{\"text\":\"Do not expose\",\"action\":\"final\"}}");
        Empty("{\"action\":\"tool\",\"name\":\"unknown.tool\",\"arguments\":{}}");
        Empty("{\"action\":\"final\",\"text\":\"\",\"name\":\"file.read\"}");
        Empty("{\"action\":\"final\",\"text\":false}");
        Empty("{\"action\":\"final\",\"text\":\"Bad\\q\"}");
        var tool = Parser("{\"action\":\"tool\",\"name\":\"file.read\",\"arguments\":{\"path\":\"PRIVATE-ARGUMENT\",\"nested\":{\"text\":\"PRIVATE-NESTED\"}}}", "file.read");
        Check(tool.Read() == new WorkflowGenerationPreviewValue("tool", Operation: "file.read"), "only an exact known proposed operation name is projected");
        var duplicate = Parser("{\"action\":\"final\",\"text\":\"Draft");
        Check(duplicate.Read()?.Text == "Draft", "a provisional draft exists before invalid trailing input");
        duplicate.Append("\",\"action\":\"tool\"}");
        Check(duplicate.Read() is null && duplicate.Disabled, "duplicate fields disable and retract preview");
        var trailing = Parser("{\"action\":\"final\",\"text\":\"Answer\"}{}");
        Check(trailing.Read() is null && trailing.Disabled, "a second root object is rejected");
        var oversized = Parser(new string('x', WorkflowGenerationPreviewParser.MaximumEnvelopeCharacters + 1));
        Check(oversized.Disabled && oversized.Read() is null, "the raw temporary envelope is bounded");
        var largeAnswer = Parser("{\"action\":\"final\",\"text\":\"" + new string('é', 5000));
        var bounded = largeAnswer.Read();
        Check(bounded is { Truncated: false } && bounded.Text!.EnumerateRunes().Count() == 5000,
            "valid public text longer than 8KiB streams within the existing final-answer contract");
        var maximumAnswer = Parser("{\"action\":\"final\",\"text\":\"" + string.Concat(Enumerable.Repeat("\\u00e9", 32768)) + "\"}");
        Check(maximumAnswer.Read()?.Text?.Length == 32768 && !maximumAnswer.Disabled,
            "the existing maximum final answer fits even when every character is JSON escaped");
        var invalidAnswer = Parser("{\"action\":\"final\",\"text\":\"" + new string('x', 32769) + "\"}");
        Check(invalidAnswer.Read() is null && invalidAnswer.Disabled, "the public observer does not widen the final-answer contract");
        var deep = Parser("{\"action\":\"tool\",\"arguments\":" + new string('[', 40));
        Check(deep.Read() is null && deep.Disabled, "malformed/deep private argument structures do not leak into preview");

        // Inspect decoded public text at every encoded callback boundary. Each prefix is
        // harmless until completed, but its ambiguous tag suffix must never reach a draft.
        (string Encoded, string Decoded)[] reasoningMarkers =
        [
            ("<think>", "<think>"), ("</think>", "</think>"),
            ("<ThInK>", "<ThInK>"), ("</tHiNk>", "</tHiNk>"),
            (@"\u003cthink\u003e", "<think>"), (@"\u003c/think\u003e", "</think>"),
            (@"\u003c\/ThInK\u003e", "</ThInK>"),
            (@"\u003C\u0054\u0068\u0049\u006E\u004B\u003E", "<ThInK>"),
            (@"\u003c\u002f\u0054\u0068\u0049\u006E\u004B\u003e", "</ThInK>")
        ];
        foreach (var marker in reasoningMarkers)
        {
            for (var split = 0; split <= marker.Encoded.Length; split++)
            {
                var guarded = Parser("{\"action\":\"final\",\"text\":\"Visible " + marker.Encoded[..split]);
                var before = guarded.Read();
                Check(split == marker.Encoded.Length
                        ? guarded.Disabled && before is null
                        : !guarded.Disabled && before?.Text == "Visible ",
                    $"decoded {marker.Decoded} prefix stays private at encoded split {split}");
                guarded.Append(marker.Encoded[split..] + "PRIVATE-REASONING\"}");
                var completed = guarded.Read();
                Check(completed is null && guarded.Disabled,
                    $"completed decoded {marker.Decoded} rejects at encoded split {split}");
                guarded.Append("{\"action\":\"final\",\"text\":\"Must not resurrect\"}");
                Check(guarded.Read() is null && guarded.Disabled,
                    $"decoded {marker.Decoded} rejection is permanent at encoded split {split}");
            }
            var oneCharacter = Parser("{\"action\":\"final\",\"text\":\"Visible ");
            foreach (var character in marker.Encoded)
            {
                oneCharacter.Append(character.ToString());
                var preview = oneCharacter.Read();
                Check(preview is null || preview.Text == "Visible ",
                    $"character callbacks never expose a decoded {marker.Decoded} fragment");
            }
            Check(oneCharacter.Disabled && oneCharacter.Read() is null,
                $"character callbacks reject the completed decoded {marker.Decoded} marker");
        }
        var ordinaryAngleText = Parser("{\"action\":\"final\",\"text\":\"Visible <thistle> and </thread>\"}");
        Check(ordinaryAngleText.Read()?.Text == "Visible <thistle> and </thread>",
            "a disproved tag prefix remains ordinary public text");
        var invalidatedParser = Parser("{\"action\":\"final\",\"text\":\"Visible");
        Check(invalidatedParser.Read()?.Text == "Visible", "explicit parser invalidation starts with a genuine public draft");
        invalidatedParser.Invalidate(); invalidatedParser.Invalidate();
        invalidatedParser.Append("\"}");
        Check(invalidatedParser.Disabled && invalidatedParser.Read() is null,
            "explicit parser invalidation clears buffered text and cannot resurrect");

        var identity = new WorkflowGenerationPreviewIdentity("chat-a", "request-a", "run-a", "effect-a", "agent-a");
        long clock = 0;
        var events = new List<WorkflowGenerationPreviewEvent>();
        var observer = new WorkflowGenerationPreview(identity, ["file.read"], events.Add, () => clock);
        observer.Append("{\"action\":\"final\",\"text\":\"One");
        Check(events.Count == 1 && events[0].Phase == "draft" && events[0].Text == "One", "first recognized draft is immediate");
        clock = 100; observer.Append(" two");
        Check(events.Count == 1, "fast fragments are coalesced within the 250 ms cadence");
        clock = 250; observer.Append(" three");
        Check(events.Count == 2 && events[1].Text == "One two three", "a changed draft is a cumulative snapshot");
        clock = 500; observer.Append("");
        Check(events.Count == 2, "unchanged text does not add events");
        observer.Finish(ModelGenerationFinish.Length);
        Check(events.Count == 3 && events[^1] is { Phase: "unavailable", Text: null, Operation: null, Finish: null },
            "length retracts provisional text without a settled-only finish field");
        observer.Finish(ModelGenerationFinish.Stop); observer.Append("ignored");
        Check(events.Count == 3, "settlement is idempotent and ignores late provider fragments");
        Check(events.All(item => item.Schema == WorkflowGenerationPreview.Schema && item.ConversationId == "chat-a" &&
            item.RequestId == "request-a" && item.RunId == "run-a" && item.EffectId == "effect-a" && item.AgentId == "agent-a"),
            "every event retains exact generation and chat identity");
        var tailEvents = new List<WorkflowGenerationPreviewEvent>();
        clock = 0;
        var tail = new WorkflowGenerationPreview(identity, [], tailEvents.Add, () => clock);
        tail.Append("{\"action\":\"final\",\"text\":\"First");
        clock = 100; tail.Append(" final tail\"}");
        tail.Finish(ModelGenerationFinish.Stop);
        Check(tailEvents.Count == 3 && tailEvents[^2].Text == "First final tail" && tailEvents[^1].Phase == "settled",
            "settlement flushes the genuine final callback tail before its text-free terminal marker");

        foreach (var finish in Enum.GetValues<ModelGenerationFinish>())
        {
            var settled = new List<WorkflowGenerationPreviewEvent>();
            var candidate = new WorkflowGenerationPreview(identity, [], settled.Add, () => 1);
            candidate.Append("{\"action\":\"final\",\"text\":\"Public");
            candidate.Append(" withheld tail\"}");
            Check(settled.Count == 1, "the final callback tail is still within the publication cadence");
            candidate.Finish(finish);
            var stopped = finish == ModelGenerationFinish.Stop;
            Check(settled.Count == (stopped ? 3 : 2) &&
                settled[^1].Phase == (stopped ? "settled" : "unavailable") &&
                settled[^1].Finish == (stopped ? finish.ToString() : null) && settled[^1].Text is null && settled[^1].Operation is null,
                "only a complete Stop carries its settled finish; every other outcome retracts without flushing");
            Check(stopped
                    ? settled[^2].Text == "Public withheld tail"
                    : settled.All(item => item.Text != "Public withheld tail"),
                "a non-Stop outcome never publishes the coalesced callback tail");
            var count = settled.Count;
            candidate.Append("ignored"); candidate.Finish(ModelGenerationFinish.Stop); candidate.Invalidate();
            Check(settled.Count == count, "every terminal outcome permanently ignores later fragments and invalidation");
        }
        foreach (var incomplete in new[]
        {
            "{\"action\":\"final\",\"text\":\"Public",
            "{\"action\":\"final\",\"text\":\"Public\"",
            "{\"action\":\"tool\",\"name\":\"file.read\""
        })
        {
            var incompleteEvents = new List<WorkflowGenerationPreviewEvent>();
            var candidate = new WorkflowGenerationPreview(identity, ["file.read"], incompleteEvents.Add, () => 1);
            candidate.Append(incomplete);
            Check(incompleteEvents.Count == 1, "a public partial envelope can initially produce a provisional draft");
            candidate.Finish(ModelGenerationFinish.Stop);
            Check(incompleteEvents.Count == 2 &&
                incompleteEvents[^1] is { Phase: "unavailable", Text: null, Operation: null, Finish: null },
                "Stop with an incomplete string or object retracts rather than settling");
        }
        (string Label, string Fragment)[] lateInvalidity =
        [
            ("opening reasoning tag", "<think>PRIVATE-REASONING"),
            ("decoded closing reasoning tag", @"\u003c/ThInK\u003ePRIVATE-REASONING"),
            ("unknown field", "\",\"private\":\"PRIVATE-UNKNOWN\"}"),
            ("duplicate field", "\",\"text\":\"PRIVATE-DUPLICATE\"}"),
            ("unpaired encoded high surrogate", "\\uD800\"}"),
            ("unpaired encoded low surrogate", "\\uDC00\"}"),
            ("unpaired raw high surrogate", "\ud800\"}"),
            ("unpaired raw low surrogate", "\udc00\"}")
        ];
        foreach (var invalidity in lateInvalidity)
        {
            clock = 0;
            var retracted = new List<WorkflowGenerationPreviewEvent>();
            var candidate = new WorkflowGenerationPreview(identity, [], retracted.Add, () => clock);
            candidate.Append("{\"action\":\"final\",\"text\":\"Public");
            clock = 1; candidate.Append(invalidity.Fragment);
            Check(retracted.Count == 2 &&
                retracted[^1] is { Phase: "unavailable", Text: null, Operation: null, Finish: null },
                $"within-cadence {invalidity.Label} immediately retracts the prior draft");
            candidate.Append("\"}"); candidate.Invalidate(); candidate.Invalidate();
            candidate.Finish(ModelGenerationFinish.Stop);
            candidate.Append("{\"action\":\"final\",\"text\":\"Must not resurrect\"}");
            Check(retracted.Count == 2 && retracted[0].Text == "Public" &&
                !JsonSerializer.Serialize(retracted).Contains("PRIVATE-", StringComparison.Ordinal),
                $"within-cadence {invalidity.Label} never exposes private content or resurrects");
        }
        var explicitlyInvalidated = new List<WorkflowGenerationPreviewEvent>();
        clock = 0;
        var explicitObserver = new WorkflowGenerationPreview(identity, [], explicitlyInvalidated.Add, () => clock);
        explicitObserver.Append("{\"action\":\"final\",\"text\":\"Public");
        clock = 1; explicitObserver.Append(" unpublished tail");
        explicitObserver.Invalidate(); explicitObserver.Invalidate();
        explicitObserver.Append("\"}"); explicitObserver.Finish(ModelGenerationFinish.Stop);
        Check(explicitlyInvalidated.Count == 2 &&
            explicitlyInvalidated[0].Text == "Public" &&
            explicitlyInvalidated[^1] is { Phase: "unavailable", Text: null, Operation: null, Finish: null },
            "explicit publisher invalidation retracts once, clears a withheld tail and permanently disables publication");
        var invalidatedBeforeDraft = new List<WorkflowGenerationPreviewEvent>();
        var disabledBeforeDraft = new WorkflowGenerationPreview(identity, [], invalidatedBeforeDraft.Add, () => 1);
        disabledBeforeDraft.Invalidate(); disabledBeforeDraft.Invalidate();
        disabledBeforeDraft.Append("{\"action\":\"final\",\"text\":\"Never public\"}");
        disabledBeforeDraft.Finish(ModelGenerationFinish.Stop);
        Check(invalidatedBeforeDraft.Count == 0, "invalidation before any public draft remains silent and permanent");
        var channel = new GenerationDraftChannel();
        var channelBinding = new GenerationDraftBinding("channel-chat", "channel-request", "channel-run",
            "channel-effect", "channel-agent", "local");
        var channelSamples = new List<GenerationDraftSample>();
        channel.Append("before binding");
        Check(channel.TryBind(channelBinding, channelSamples.Add) &&
            !channel.TryBind(channelBinding, _ => throw new InvalidOperationException("must not rebind")) &&
            channelSamples.Count == 0,
            "the actual draft channel accepts one pre-generation binding without early publication");
        channel.Append("before generation"); channel.Start();
        Check(!channel.TryBind(channelBinding, channelSamples.Add),
            "the actual draft channel refuses binding after generation starts");
        channel.Append("first"); channel.Append("second"); channel.Complete(accepted: false);
        channel.Append("after close"); channel.Complete(accepted: false);
        Check(channelSamples.Count == 3 && channelSamples.All(sample => ReferenceEquals(sample.Binding, channelBinding)) &&
            channelSamples.Select(sample => sample.Sequence).SequenceEqual([1L, 2L, 3L]) &&
            channelSamples[0] is { Kind: GenerationDraftKind.Content, Content: "first" } &&
            channelSamples[1] is { Kind: GenerationDraftKind.Content, Content: "second" } &&
            channelSamples[2] is { Kind: GenerationDraftKind.Unavailable, Content: null },
            "the actual draft channel keeps exact identity, ordered sequence and one terminal invalidation without resurrection");
        foreach (var throwingPhase in new[] { "draft", "unavailable", "settled" })
        {
            var delivered = new List<WorkflowGenerationPreviewEvent>();
            var callbacks = 0; var publicationFailures = 0;
            var candidate = new WorkflowGenerationPreview(identity, [], item =>
            {
                callbacks++;
                if (item.Phase == throwingPhase)
                {
                    publicationFailures++;
                    throw new IOException("PRIVATE-OBSERVER-FAILURE");
                }
                delivered.Add(item);
            }, () => 1);
            candidate.Append("{\"action\":\"final\",\"text\":\"Public");
            if (throwingPhase == "unavailable") candidate.Invalidate();
            else { candidate.Append("\"}"); candidate.Finish(ModelGenerationFinish.Stop); }
            Check(publicationFailures == 1 && callbacks == (throwingPhase == "draft" ? 1 : 2) &&
                delivered.All(item => item.Phase == "draft" && item.Text == "Public"),
                $"an optional {throwingPhase} publisher exception does not escape the generation-facing seam");
            var calls = callbacks;
            candidate.Append("{\"action\":\"final\",\"text\":\"Never public\"}");
            candidate.Invalidate(); candidate.Finish(ModelGenerationFinish.Stop);
            Check(callbacks == calls,
                $"an optional {throwingPhase} publisher exception permanently detaches publication");
        }
        var invalidEvents = new List<WorkflowGenerationPreviewEvent>();
        var invalid = new WorkflowGenerationPreview(identity, [], invalidEvents.Add, () => clock);
        invalid.Append("{\"action\":\"final\",\"text\":\"Public");
        clock += 250; invalid.Append("\",\"private\":\"hidden\"}");
        Check(invalidEvents.Count == 2 && invalidEvents[^1] is { Phase: "unavailable", Text: null, Finish: null },
            "an invalid later envelope retracts its provisional draft without exposing the added field");
        var overflowEvents = new List<WorkflowGenerationPreviewEvent>();
        var overflow = new WorkflowGenerationPreview(identity, [], overflowEvents.Add, () => clock);
        overflow.Append("{\"action\":\"final\",\"text\":\"Public");
        overflow.Append(new string('x', WorkflowGenerationPreviewParser.MaximumEnvelopeCharacters));
        Check(overflowEvents.Count == 2 && overflowEvents[^1] is { Phase: "unavailable", Finish: null },
            "temporary buffer overflow immediately retracts a shown draft");
        var proposedEvents = new List<WorkflowGenerationPreviewEvent>();
        var proposed = new WorkflowGenerationPreview(identity, ["file.read"], proposedEvents.Add, () => clock);
        proposed.Append("{\"action\":\"tool\",\"name\":\"file.read\",\"arguments\":{\"credential\":\"PRIVATE-CANARY\"}}");
        Check(proposedEvents.Count == 1 && proposedEvents[0].Operation == "file.read" &&
            !JsonSerializer.Serialize(proposedEvents).Contains("PRIVATE-CANARY", StringComparison.Ordinal),
            "the event contains no tool arguments or opaque envelope");
        var silent = new List<WorkflowGenerationPreviewEvent>();
        var noPublicText = new WorkflowGenerationPreview(identity, [], silent.Add, () => clock);
        noPublicText.Append("{\"unrecognized\":\"private\"}"); noPublicText.Finish(ModelGenerationFinish.Stop);
        Check(silent.Count == 0, "nonpublic generation creates no fake progress events");

        checks += await HostSeamAsync(qualifiedCleanup);
        Console.WriteLine($"Generation preview: {checks} parser/event/isolated-host fixture checks passed; no live inference or private reasoning capture.");
    }

    private static async Task<int> HostSeamAsync(bool qualifiedCleanup)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-generation-preview-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Generation preview host: " + message); checks++; }
        try
        {
            var store = new StateStore(Path.Combine(root, "data"));
            var config = new Configuration(store);
            var profile = new AgentProfile
            {
                Id = "preview-agent", Name = "Preview fixture", ModelId = "preview-model",
                AutomaticCompaction = false, FollowsModelBudgets = false, ContextTokens = 8192,
                ResponseTokens = 512, AllowedRoots = [root], AllowedTools = ["*"], Capabilities = ["files"]
            };
            store.Put("agent", profile.Id, profile);
            store.Put("model", profile.ModelId, new ModelRecord { Id = profile.ModelId, Path = Path.Combine(root, "fixture.gguf"),
                AutomaticContextSizing = false, ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192 });
            var dispatcher = new Tools();
            var snapshot = await WorkerExecutionSnapshot.CaptureAsync(store, profile, config, dispatcher, [], default);
            var submission = new WorkflowSubmission("preview-request", "preview-chat", profile.Id, "fixture:preview", "1.0.0",
                "PRIVATE-USER-PROMPT", "PRIVATE-ASSIGNMENT", Element(new { }), snapshot);
            var hub = new EventHub();
            Check(!hub.HasServiceSubscribers, "an idle event hub needs no preview parsing");
            var subscription = hub.Subscribe();
            Check(hub.HasServiceSubscribers, "the owner event feed enables ephemeral observation");
            try
            {
                var models = new Models();
                var bridge = new WorkerWorkflowEffects(store, config, models, dispatcher, diagnosticEvents: hub);
                var command = bridge.Commands.Single(item => item.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && item.Descriptor.Version == 1);
                var context = new WorkflowCommandContext("preview-effect", submission,
                    Element(new WorkerWorkflowEffects.GenerateArguments([], ToolNames: ["file.read"]))) { RunId = "preview-run" };
                async Task<(WorkflowCommandResult Result, List<ServiceEvent> Events)> Execute()
                {
                    var result = await command.ExecuteAsync(context, default);
                    var events = new List<ServiceEvent>();
                    while (subscription.Reader.TryRead(out var item)) events.Add(item);
                    return (result, events);
                }
                var generated = await Execute();
                Check(generated.Result.State == WorkflowEffectState.Succeeded && models.Generated == 1 && models.Disposed == 1,
                    "a streamed host generation keeps its terminal effect and disposes the actual fixture lease");
                Check(generated.Events.Count == 2 && generated.Events.All(item => item.Type == WorkflowGenerationPreview.EventType && item.JobId == "preview-run"),
                    "real host callback integration emits one draft and one clear on the normal ephemeral channel");
                var first = generated.Events[0].Data.Deserialize<WorkflowGenerationPreviewEvent>(CerebralJson.Options)!;
                var terminal = generated.Events[^1].Data.Deserialize<WorkflowGenerationPreviewEvent>(CerebralJson.Options)!;
                Check(first.ConversationId == submission.ConversationId && first.EffectId == context.EffectId &&
                    first.AgentId == profile.Id && first.Text == "Public draft" && terminal.Phase == "settled",
                    "host capture uses exact frozen workflow identities");
                Check(!JsonSerializer.Serialize(generated.Events, CerebralJson.Options).Contains("PRIVATE-", StringComparison.Ordinal) &&
                    dispatcher.Executions == 0 && !hub.HasDiagnosticSubscribers,
                    "preview does not use the raw diagnostic stream, expose input/arguments or execute a proposed action");

                models.Callbacks = false;
                var remoteLike = await Execute();
                Check(remoteLike.Result.State == WorkflowEffectState.Succeeded && remoteLike.Events.Count == 0,
                    "a terminal-only adapter does not manufacture token streaming");
                models.Callbacks = true; models.ThrowAfterCallback = true;
                var unknown = await Execute();
                Check(unknown.Result.State == WorkflowEffectState.Unknown && unknown.Events.Count == 2 &&
                    unknown.Result.Data!.Value.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options)!
                        .Generation.Finish == ModelGenerationFinish.Unknown &&
                    unknown.Events[^1].Data.Deserialize<WorkflowGenerationPreviewEvent>(CerebralJson.Options)
                        is { Phase: "unavailable", Finish: null } &&
                    models.Disposed == 3, "an adapter exception retains its canonical Unknown finish and emits a compatible unavailable retraction");

                models.ThrowAfterCallback = false; models.Output = "{\"action\":\"tool\",\"name\":\"file.write\",\"arguments\":{\"text\":\"PRIVATE-ARGUMENT\"}}";
                var unadvertised = await Execute();
                Check(unadvertised.Result.State == WorkflowEffectState.Succeeded && unadvertised.Events.Count == 0,
                    "snapshot-granted but unadvertised tool names are excluded from preview");

                async Task<(WorkflowCommandResult Result, List<WorkflowGenerationPreviewEvent> Events)> ExecuteDraft(DraftModels draft)
                {
                    var draftBridge = new WorkerWorkflowEffects(store, config, draft, dispatcher, diagnosticEvents: hub);
                    var draftCommand = draftBridge.Commands.Single(item => item.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && item.Descriptor.Version == 1);
                    var result = await draftCommand.ExecuteAsync(context, default);
                    var events = new List<WorkflowGenerationPreviewEvent>();
                    while (subscription.Reader.TryRead(out var item))
                        if (item.Type == WorkflowGenerationPreview.EventType)
                            events.Add(item.Data.Deserialize<WorkflowGenerationPreviewEvent>(CerebralJson.Options)!);
                    return (result, events);
                }
                void ReceiptPreserved(DraftModels draft, WorkflowCommandResult result, WorkflowEffectState state,
                    bool outputOmitted = false)
                {
                    var data = result.Data!.Value.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options)!;
                    Check(result.State == state && data.Generation.Finish == draft.Terminal.Finish &&
                        data.Generation.GeneratedTokens == 7 && data.Generation.EvaluatedTokens == 42 &&
                        data.OutputOmitted == outputOmitted && draft.Generated == 1 && draft.Disposed == 1 &&
                        draft.CanonicalCallbacks == 1,
                        "optional draft observation preserves actual terminal classification, usage, callback and preparation cleanup");
                }
                var boundDraft = new DraftModels(DraftMode.Accepted);
                var bound = await ExecuteDraft(boundDraft);
                ReceiptPreserved(boundDraft, bound.Result, WorkflowEffectState.Succeeded);
                Check(bound.Events.Count == 2 && bound.Events[0] is { Phase: "draft", Text: "Raw public draft" } &&
                    bound.Events[1] is { Phase: "settled", Finish: "Stop" } &&
                    boundDraft.BindCalls == 1 && boundDraft.BoundBeforeGenerate &&
                    boundDraft.Binding == new GenerationDraftBinding(submission.ConversationId, submission.RequestId,
                        context.RunId!, context.EffectId, profile.Id, "local") &&
                    boundDraft.Order.SequenceEqual(["prepare", "bind", "generate", "dispose"]),
                    "the host binds exact immutable draft identity once before inference and suppresses duplicate canonical preview");
                Check(!JsonSerializer.Serialize(bound.Events).Contains("PRIVATE-", StringComparison.Ordinal) &&
                    dispatcher.Executions == 0,
                    "raw draft bytes and pre-bind callbacks pass only through the public parser without proposed-tool execution");

                foreach (var mode in new[] { DraftMode.Unavailable, DraftMode.WrongBinding, DraftMode.WrongSequence,
                    DraftMode.RepeatedSequence, DraftMode.NullContent, DraftMode.UnknownKind, DraftMode.ObserverThrows })
                {
                    var adapter = new DraftModels(mode);
                    var disabled = await ExecuteDraft(adapter);
                    ReceiptPreserved(adapter, disabled.Result, WorkflowEffectState.Succeeded);
                    Check(disabled.Events.Count == 2 &&
                        disabled.Events[0] is { Phase: "draft", Text: "Raw public draft" } &&
                        disabled.Events[1] is { Phase: "unavailable", Text: null, Operation: null, Finish: null } &&
                        !JsonSerializer.Serialize(disabled.Events).Contains("PRIVATE-", StringComparison.Ordinal),
                        $"an accepted draft channel with {mode} retracts permanently without canonical callback fallback");
                }
                var unsupportedDraft = new DraftModels(DraftMode.Unsupported);
                var unsupported = await ExecuteDraft(unsupportedDraft);
                ReceiptPreserved(unsupportedDraft, unsupported.Result, WorkflowEffectState.Succeeded);
                Check(unsupported.Events.Count == 2 &&
                    unsupported.Events[0] is { Phase: "draft", Text: "Canonical public answer" } &&
                    unsupported.Events[1].Phase == "settled" && unsupportedDraft.BindCalls == 1,
                    "an explicit unsupported draft binding retains legitimate legacy onText preview");
                var throwingDraft = new DraftModels(DraftMode.BindThrows);
                var bindFailure = await ExecuteDraft(throwingDraft);
                ReceiptPreserved(throwingDraft, bindFailure.Result, WorkflowEffectState.Succeeded);
                Check(bindFailure.Events.Count == 0 && throwingDraft.BindCalls == 1,
                    "a throwing optional binder invalidates observation while preserving its canonical callback and receipt");

                var erroredDraft = new DraftModels(DraftMode.Accepted)
                { Terminal = new(ModelGenerationFinish.Stop, DraftModels.CanonicalText, 7, 42, Error: "fixture terminal rejection") };
                var errored = await ExecuteDraft(erroredDraft);
                ReceiptPreserved(erroredDraft, errored.Result, WorkflowEffectState.Failed);
                Check(errored.Events.Count == 2 && errored.Events[^1] is { Phase: "unavailable", Finish: null } &&
                    errored.Events.All(item => item.Phase != "settled"),
                    "a Stop carrying an error retracts after effect classification instead of settling its earlier draft");
                var omittedDraft = new DraftModels(DraftMode.Accepted)
                { Terminal = new(ModelGenerationFinish.Stop, new string('x', WorkflowContract.MaximumStateBytes + 1), 7, 42) };
                var omitted = await ExecuteDraft(omittedDraft);
                ReceiptPreserved(omittedDraft, omitted.Result, WorkflowEffectState.Failed, outputOmitted: true);
                Check(omitted.Events.Count == 2 && omitted.Events[^1] is { Phase: "unavailable", Finish: null } &&
                    omitted.Events.All(item => item.Phase != "settled") &&
                    omitted.Result.Data!.Value.GetProperty("generation").GetProperty("text").GetString() == "",
                    "an omitted oversized Stop output retains usage but never settles a provisional draft");
                foreach (var emptyText in new[] { "", " \r\n\t" })
                {
                    var emptyDraft = new DraftModels(DraftMode.Accepted)
                    { Terminal = new(ModelGenerationFinish.Stop, emptyText, 7, 42) };
                    var empty = await ExecuteDraft(emptyDraft);
                    ReceiptPreserved(emptyDraft, empty.Result, WorkflowEffectState.Succeeded);
                    Check(empty.Events.Count == 2 && empty.Events[^1] is { Phase: "unavailable", Finish: null } &&
                        empty.Events.All(item => item.Phase != "settled"),
                        "an empty or whitespace-only canonical Stop cannot certify a raw provisional draft");
                }
                var cleanupDraft = new DraftModels(DraftMode.Accepted) { DisposeThrows = true };
                var cleanupFailure = await ExecuteDraft(cleanupDraft);
                var cleanupDataPreserved = qualifiedCleanup
                    ? cleanupFailure.Result.Data is { } retained &&
                        retained.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options) is { } data &&
                        data.Generation.Finish == ModelGenerationFinish.Stop &&
                        data.Generation.GeneratedTokens == 7 && data.Generation.EvaluatedTokens == 42
                    : cleanupFailure.Result.Data is null;
                Check(cleanupFailure.Result.State == (qualifiedCleanup ? WorkflowEffectState.Unknown : WorkflowEffectState.Failed) &&
                    cleanupDataPreserved && cleanupDraft.Generated == 1 && cleanupDraft.Disposed == 1 &&
                    cleanupFailure.Events.Count == 2 && cleanupFailure.Events[^1] is { Phase: "unavailable", Finish: null } &&
                    cleanupFailure.Events.All(item => item.Phase != "settled"),
                    "cleanup failure keeps the selected direct/qualified disposition and retracts before any settlement");

                var imageProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
                imageProfile.Id = "preview-image-agent";
                imageProfile.RequiredModelCapabilities = [ModelCapabilityIds.ImageInput];
                store.Put("agent", imageProfile.Id, imageProfile);
                var imageSnapshot = await WorkerExecutionSnapshot.CaptureAsync(store, imageProfile, config, dispatcher, [], default);
                var imageSubmission = submission with
                {
                    RequestId = "preview-image-request", ConversationId = "preview-image-chat",
                    ProfileId = imageProfile.Id, SettingsSnapshot = imageSnapshot
                };
                var imageContext = new WorkflowCommandContext("preview-image-effect", imageSubmission,
                    Element(new WorkerWorkflowEffects.GenerateArguments([], ToolNames: ["file.read"]))) { RunId = "preview-image-run" };
                var readinessDraft = new DraftModels(DraftMode.Accepted);
                var outerReadiness = new ThrowingPreviewReadiness();
                var readinessAcquisitions = 0;
                var readinessBridge = new WorkerWorkflowEffects(store, config, readinessDraft, dispatcher,
                    requiredCapabilityAdmission: (acceptedSubmission, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (!ReferenceEquals(acceptedSubmission, imageSubmission))
                            throw new InvalidOperationException("Readiness must use the exact accepted fixture submission.");
                        readinessAcquisitions++;
                        return Task.FromResult<IDisposable?>(outerReadiness);
                    }, diagnosticEvents: hub);
                var readinessCommand = readinessBridge.Commands.Single(item => item.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && item.Descriptor.Version == 1);
                var readinessResult = await readinessCommand.ExecuteAsync(imageContext, default);
                var readinessEvents = new List<WorkflowGenerationPreviewEvent>();
                while (subscription.Reader.TryRead(out var item))
                    if (item.Type == WorkflowGenerationPreview.EventType)
                        readinessEvents.Add(item.Data.Deserialize<WorkflowGenerationPreviewEvent>(CerebralJson.Options)!);
                var readinessDataPreserved = qualifiedCleanup
                    ? readinessResult.Data is { } retainedReadiness &&
                        retainedReadiness.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options) is { } readinessData &&
                        readinessData.Generation.Finish == ModelGenerationFinish.Stop &&
                        readinessData.Generation.Text == DraftModels.CanonicalText &&
                        readinessData.Generation.GeneratedTokens == 7 && readinessData.Generation.EvaluatedTokens == 42
                    : readinessResult.Data is null;
                Check(WorkerExecutionSnapshot.Read(imageSnapshot).Profile.RequiredModelCapabilities.Contains(ModelCapabilityIds.ImageInput) &&
                    readinessAcquisitions == 1 && outerReadiness.Disposals == 1 &&
                    readinessDraft.Generated == 1 && readinessDraft.CanonicalCallbacks == 1 && readinessDraft.Disposed == 1,
                    "the natural image-required admission seam retains one outer readiness lease through one fixture generation and preparation cleanup");
                Check(readinessResult.State == (qualifiedCleanup ? WorkflowEffectState.Unknown : WorkflowEffectState.Failed) &&
                    readinessDataPreserved && readinessEvents.Count == 2 &&
                    readinessEvents[0] is { Phase: "draft", Text: "Raw public draft" } &&
                    readinessEvents[^1] is { Phase: "unavailable", Finish: null } && readinessEvents.All(item => item.Phase != "settled"),
                    "outer readiness disposal failure preserves direct/qualified disposition and retracts after the last cleanup boundary");
            }
            finally { hub.Unsubscribe(subscription.Id); }
            Check(!hub.HasServiceSubscribers, "the owner subscription is cleaned up");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(root).StartsWith("cerebral-generation-preview-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact owned fixture root.");
            Directory.Delete(root, true);
        }
        return checks;
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);
    private sealed class Tools : IToolDispatcher
    {
        internal int Executions;
        public bool FullAccessActive => false;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([
                new("file.read", "Read fixture", Element(new { type = "object" }), "files"),
                new("file.write", "Write fixture", Element(new { type = "object" }), "files")]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        { Executions++; throw new InvalidOperationException("Preview must never execute a tool."); }
    }
    private sealed class ThrowingPreviewReadiness : IDisposable
    {
        internal int Disposals;
        public void Dispose()
        {
            Disposals++;
            throw new IOException("PRIVATE-OUTER-READINESS-CLEANUP");
        }
    }

    private enum DraftMode
    {
        Accepted, Unsupported, BindThrows, Unavailable, WrongBinding, WrongSequence,
        RepeatedSequence, NullContent, UnknownKind, ObserverThrows
    }

    private sealed class DraftModels(DraftMode mode) : IModelRuntimeAdapter
    {
        internal const string CanonicalText = "{\"action\":\"final\",\"text\":\"Canonical public answer\"}";
        internal ModelGenerationResult Terminal = new(ModelGenerationFinish.Stop, CanonicalText, 7, 42);
        internal bool DisposeThrows, BoundBeforeGenerate;
        internal int BindCalls, Generated, Disposed, CanonicalCallbacks;
        internal GenerationDraftBinding? Binding;
        internal readonly List<string> Order = [];
        public ModelAdapterDescriptor Descriptor { get; } = new("preview-draft-fixture", "1.0.0", ["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            Order.Add("prepare");
            return Task.FromResult<IPreparedModelGeneration>(new DraftLease(this, mode, request));
        }
        private sealed class DraftLease : IPreparedModelGeneration, IBoundGenerationDraft
        {
            private readonly DraftModels owner;
            private readonly DraftMode mode;
            private readonly GenerationDraftChannel channel = new();
            private Action<GenerationDraftSample>? observer;
            private GenerationDraftBinding? binding;
            public PreparedModelMetadata Metadata { get; }
            internal DraftLease(DraftModels owner, DraftMode mode, ModelGenerationRequest request)
            {
                this.owner = owner; this.mode = mode;
                Metadata = new("preview-draft-instance", "preview-draft-fixture", "1.0.0",
                    request.ModelId, 42, request.ContextLimit, request.ResponseLimit, "deterministic bound fixture");
            }
            public bool TryBindDraft(GenerationDraftBinding value, Action<GenerationDraftSample> target)
            {
                owner.Order.Add("bind"); owner.BindCalls++;
                owner.BoundBeforeGenerate = owner.Generated == 0;
                owner.Binding = value; binding = value; observer = target;
                // A binder cannot publish before its optional binding decision returns.
                target(new(value, 1, GenerationDraftKind.Content,
                    "{\"action\":\"final\",\"text\":\"PRIVATE-BIND-CALLBACK"));
                if (mode == DraftMode.BindThrows) throw new IOException("PRIVATE-BIND-FAILURE");
                if (mode == DraftMode.Unsupported) return false;
                if (mode == DraftMode.ObserverThrows)
                    return channel.TryBind(value, sample =>
                    {
                        target(sample);
                        throw new IOException("PRIVATE-OBSERVER-FAILURE");
                    });
                return true;
            }
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            {
                owner.Order.Add("generate"); owner.Generated++;
                if (mode == DraftMode.ObserverThrows)
                {
                    channel.Start();
                    channel.Append("{\"action\":\"final\",\"text\":\"Raw public draft");
                    channel.Append("\"}"); channel.Complete(accepted: true);
                }
                else if (mode is not (DraftMode.Unsupported or DraftMode.BindThrows))
                {
                    observer?.Invoke(new(binding!, 1, GenerationDraftKind.Content,
                        "{\"action\":\"final\",\"text\":\"Raw public draft"));
                    GenerationDraftSample sample = mode switch
                    {
                        DraftMode.Unavailable => new GenerationDraftSample(binding!, 2, GenerationDraftKind.Unavailable),
                        DraftMode.WrongBinding => new(binding! with { EffectId = "other-effect" }, 2, GenerationDraftKind.Content, "\"}"),
                        DraftMode.WrongSequence => new(binding!, 3, GenerationDraftKind.Content, "\"}"),
                        DraftMode.RepeatedSequence => new(binding!, 1, GenerationDraftKind.Content, "\"}"),
                        DraftMode.NullContent => new(binding!, 2, GenerationDraftKind.Content),
                        DraftMode.UnknownKind => new(binding!, 2, (GenerationDraftKind)99, "\"}"),
                        _ => new(binding!, 2, GenerationDraftKind.Content, "\"}")
                    };
                    observer?.Invoke(sample);
                    if (mode != DraftMode.Accepted)
                        observer?.Invoke(new(binding!, 3, GenerationDraftKind.Content, "PRIVATE-LATE-CONTENT"));
                }
                owner.CanonicalCallbacks++; onText?.Invoke(owner.Terminal.Text);
                return Task.FromResult(owner.Terminal);
            }
            public ValueTask DisposeAsync()
            {
                owner.Order.Add("dispose"); owner.Disposed++;
                return owner.DisposeThrows
                    ? ValueTask.FromException(new IOException("PRIVATE-DISPOSAL-FAILURE"))
                    : ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Models : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("preview-fixture", "1.0.0", ["text"]);
        internal bool Callbacks = true, ThrowAfterCallback;
        internal int Generated, Disposed;
        internal string? Output;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) =>
            Task.FromResult<IPreparedModelGeneration>(new Lease(this, request));
        private sealed class Lease(Models owner, ModelGenerationRequest request) : IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata { get; } = new("preview-instance", "preview-fixture", "1.0.0",
                request.ModelId, 42, request.ContextLimit, request.ResponseLimit, "deterministic fixture");
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            {
                owner.Generated++;
                var text = owner.Output ?? "{\"action\":\"final\",\"text\":\"Public draft\"}";
                if (owner.Callbacks)
                {
                    if (owner.Output is not null) onText?.Invoke(text);
                    else { onText?.Invoke("{\"action\":\"final\",\"text\":\"Public draft"); onText?.Invoke("\"}"); }
                }
                if (owner.ThrowAfterCallback) throw new IOException("PRIVATE-PROVIDER-DIAGNOSTIC");
                return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, text, 7, 42));
            }
            public ValueTask DisposeAsync() { owner.Disposed++; return ValueTask.CompletedTask; }
        }
    }
}
