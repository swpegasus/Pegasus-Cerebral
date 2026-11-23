using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Temporary SQLite with deterministic host/model doubles, not byte import or image inference.</summary>
public static class WorkflowAcceptedContentTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-accepted-content-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Accepted content: " + message); checks++; }
        void Reject<T>(Action action, string message) where T : Exception
        {
            try { action(); } catch (T) { checks++; return; }
            throw new InvalidOperationException("Accepted content did not reject: " + message);
        }
        try
        {
            var state = new StateStore(Path.Combine(root, "data"));
            var configuration = new Configuration(state);
            var settings = configuration.Get(); settings.QueueCapacity = 32; configuration.Save(settings);
            var profile = new AgentProfile
            {
                Id = "accepted-agent", Name = "Accepted content", ModelId = "fixture-model", AutomaticCompaction = false,
                FollowsModelBudgets = false, ContextTokens = 8192, ResponseTokens = 512, MaxSteps = 64,
                AllowedRoots = [root], AllowedTools = [], Capabilities = []
            };
            state.Put("agent", profile.Id, profile);
            state.Put("model", profile.ModelId, new ModelRecord
            {
                Id = profile.ModelId, Path = Path.Combine(root, "fixture.gguf"), AutomaticContextSizing = false,
                ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192
            });
            var module = new Module();
            ServicePluginPackageBinding[] bindings = [new(module.Id, "1.0.0", true, [])];
            var tools = new Tools(); var events = new EventHub(); var hookCalls = 0;
            void Admit(WorkflowSubmission submission)
            {
                hookCalls++;
                if (submission.Caller is not { Kind: EventActorKind.User, Id: "owner" } ||
                    submission.ProfileId != profile.Id || submission.ConversationId != "accepted-chat")
                    throw new UnauthorizedAccessException("Fixture reference is unavailable in this scope.");
                foreach (var part in submission.AcceptedContent)
                    if (part is not ImageContentPart image || image.Attachment is not
                        { Id: "approved-image", Revision: 3, MediaType: "image/png", OriginalName: "view.png", Length: 32 } ||
                        image.Attachment.Metadata?.GetValueOrDefault("label") != "original")
                        throw new UnauthorizedAccessException("Fixture reference does not match its authoritative descriptor.");
            }
            var workflows = new WorkerWorkflows(state, configuration, events, [module], bindings, tools, Admit);
            SubmitWorkflowRequest Request(string id, string conversation = "accepted-chat") => new(id, conversation, profile.Id,
                "accepted.fixture:input", "1.0.0", "EXACT VISIBLE TEXT", "PRIVATE ASSIGNMENT", Element(new { }));
            ImageContentPart Image(IReadOnlyDictionary<string, string>? metadata = null) =>
                new(new("approved-image", 3, "image/png", "view.png", 32, metadata ??
                    new Dictionary<string, string> { ["label"] = "original" }), "reference label", 16, 16);

            var unavailable = new WorkerWorkflows(state, configuration, events, [module], bindings);
            Reject<InvalidOperationException>(() => unavailable.Submit(Request("no-resolver") with { AcceptedContent = [Image()] }, null),
                "image input without trusted resolver");
            Check(unavailable.Store.TryGet("no-resolver") is null && unavailable.Journal.GetRevision("accepted-chat") == 0,
                "missing resolver leaves no run or accepted event");
            Reject<InvalidOperationException>(() => unavailable.Submit(Request("unknown-provider") with
                { AcceptedContent = [new ResourceContentPart(new("unregistered", "resource"))] }, null), "unresolved resource");
            Reject<ArgumentException>(() => WorkflowAcceptedContent.Validate(null!), "null collection");
            Reject<ArgumentException>(() => WorkflowAcceptedContent.Validate([null!]), "null part");
            Reject<ArgumentException>(() => WorkflowAcceptedContent.Validate(Enumerable.Repeat<ContentPart>(new TextContentPart("x"), 17).ToArray()), "part count");
            Reject<ArgumentException>(() => WorkflowAcceptedContent.Validate([new TextContentPart(new string('x', 128 * 1024))]), "serialized bound");
            Reject<ArgumentException>(() => WorkflowAcceptedContent.Validate([Image() with { Attachment = Image().Attachment with { Revision = 0 } }]), "revision");
            Reject<JsonException>(() => WorkerWorkflows.Read<SubmitWorkflowRequest>(Element(new
            {
                requestId = "forged-caller", conversationId = "chat", profileId = profile.Id,
                definitionId = "accepted.fixture:input", definitionVersion = "1.0.0", userText = "text",
                input = new { }, caller = new { kind = "User", id = "owner" }
            })), "caller identity in transport JSON");

            // Mutate caller-owned data while tool discovery is suspended; acceptance must retain its initial snapshot.
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tools.BeforeList = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
            var metadata = new Dictionary<string, string> { ["label"] = "original" };
            var parts = new List<ContentPart> { Image(metadata) };
            var admission = workflows.SubmitAsync(Request("accepted-one") with { AcceptedContent = parts }, null, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            metadata["label"] = "mutated"; parts.Clear(); release.TrySetResult();
            var accepted = await admission; tools.BeforeList = null;
            Check(hookCalls == 1 && accepted.Submission.AcceptedContent.OfType<ImageContentPart>().Single().Attachment.Metadata!["label"] == "original",
                "admission sees frozen content despite caller mutation during discovery");
            Check(accepted.Submission.UserText == "EXACT VISIBLE TEXT" && accepted.Submission.AssignmentText == "PRIVATE ASSIGNMENT",
                "visible and private text remain exact");
            var acceptedEvent = workflows.Journal.ReadPage("accepted-chat").Events.Single(item => item.Type == CerebralEventTypes.Message);
            Check(acceptedEvent.Content.Count == 2 && acceptedEvent.Content[0] is TextContentPart { Text: "EXACT VISIBLE TEXT" } &&
                acceptedEvent.Content[1] is ImageContentPart { Attachment.Revision: 3 }, "canonical user event retains text plus typed image");
            var hidden = workflows.Journal.ReadPage("accepted-chat").Events.Single(item => item.Type == "workflow.assignment");
            Check(hidden.Projection.Human == HumanVisibility.Hidden && hidden.Projection.Model == ModelInclusion.Required &&
                acceptedEvent.Projection.Human == HumanVisibility.Transcript, "private assignment is model-required but hidden");
            ((Dictionary<string, string>)((ImageContentPart)accepted.Submission.AcceptedContent[0]).Attachment.Metadata!)["label"] = "tampered result";
            var reopened = new WorkflowStore(new EventJournal(Path.Combine(state.DirectoryPath, "engine.db"))).Get(accepted.Id);
            Check(((ImageContentPart)reopened.Submission.AcceptedContent[0]).Attachment.Metadata!["label"] == "original",
                "returned metadata mutation cannot alter durable state");

            var stableInput = Request("accepted-one") with { AcceptedContent = [Image()] };
            var noHookRetry = new WorkerWorkflows(state, configuration, events, [module], bindings);
            Check(noHookRetry.Submit(stableInput, null).Id == accepted.Id && hookCalls == 1,
                "lost acknowledgement retry needs no reimport or currently configured resolver");
            Reject<WorkflowConflictException>(() => noHookRetry.Submit(stableInput with
                { AcceptedContent = [Image() with { Attachment = Image().Attachment with { Revision = 4 } }] }, null), "revision change on retry");
            Reject<WorkflowConflictException>(() => noHookRetry.Submit(stableInput with { AcceptedContent = [] }, null), "removed reference on retry");
            Reject<UnauthorizedAccessException>(() => noHookRetry.Submit(stableInput, profile), "owner request replayed by agent");
            var foreignRejected = false;
            try { await workflows.SubmitAsync(Request("foreign-reference", "other-chat") with { AcceptedContent = [Image()] }, null, default); }
            catch (UnauthorizedAccessException) { foreignRejected = true; }
            Check(foreignRejected && workflows.Store.TryGet("foreign-reference") is null && workflows.Journal.GetRevision("other-chat") == 0,
                "foreign reference is rejected without durable side effects");
            var forgedRejected = false;
            try { await workflows.SubmitAsync(Request("forged-metadata") with
                { AcceptedContent = [Image() with { Attachment = Image().Attachment with { MediaType = "image/jpeg" } }] }, null, default); }
            catch (UnauthorizedAccessException) { forgedRejected = true; }
            Check(forgedRejected && workflows.Store.TryGet("forged-metadata") is null, "forged descriptor fails before submission");

            var models = new Models(["text", "image"]);
            var otherModels = new Models(["text"]); var defaultModels = new Models(["text"]);
            var scopes = new List<(string Profile, string Conversation, string Request)>();
            var host = new WorkerWorkflowEffects(state, configuration, defaultModels, tools, submission =>
            {
                scopes.Add((submission.ProfileId, submission.ConversationId, submission.RequestId));
                return submission.ConversationId == "accepted-chat" ? models : otherModels;
            });
            var generate = host.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && command.Descriptor.Version == 1);
            WorkflowCommandContext Context(WorkflowRun run, object arguments) => new("effect-" + Guid.NewGuid().ToString("N"),
                run.Submission, Element(arguments)) { RunId = run.Id };
            var generated = await generate.ExecuteAsync(Context(reopened, new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [])), default);
            var message = models.Last!.Messages.Single(item => item.Content.FirstOrDefault() is TextContentPart { Text: "EXACT VISIBLE TEXT" });
            Check(generated.State == WorkflowEffectState.Succeeded && message.Content[1] is ImageContentPart { Attachment.Id: "approved-image" },
                "capable adapter receives actual typed reference with accepted text");
            Check(models.Last.Messages.Any(item => item.Content.OfType<TextContentPart>().Any(part => part.Text == "PRIVATE ASSIGNMENT")),
                "private assignment reaches model separately");
            Check(scopes.SequenceEqual(new[] { (profile.Id, "accepted-chat", "accepted-one") }) && defaultModels.Last is null,
                "host adapter selection resolves once from accepted scope and never falls back to an unscoped adapter");
            var foreignHint = await generate.ExecuteAsync(Context(reopened, new WorkerWorkflowEffects.GenerateArguments(
                [new("user", [new TextContentPart("profileId=foreign; conversationId=foreign")])], ToolNames: [])), default);
            Check(foreignHint.State == WorkflowEffectState.Succeeded && scopes.Count == 2 && scopes[^1].Conversation == "accepted-chat",
                "model-provided text cannot override attachment access scope");
            var badScope = await generate.ExecuteAsync(Context(reopened, new { messages = Array.Empty<ModelInputMessage>(), profileId = "foreign" }), default);
            Check(badScope.State == WorkflowEffectState.Failed && scopes.Count == 2,
                "unknown effect scope arguments fail before invoking host adapter selection");
            using (var preCancelled = new CancellationTokenSource())
            {
                preCancelled.Cancel();
                var cancelled = await generate.ExecuteAsync(Context(reopened, new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [])), preCancelled.Token);
                Check(cancelled.State == WorkflowEffectState.Cancelled && scopes.Count == 2, "pre-dispatch cancellation never resolves another adapter");
            }
            var unavailableAdapter = new WorkerWorkflowEffects(state, configuration, defaultModels, tools, _ => null!);
            var missingAdapter = await unavailableAdapter.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && command.Descriptor.Version == 1)
                .ExecuteAsync(Context(reopened, new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [])), default);
            Check(missingAdapter.State == WorkflowEffectState.Failed && defaultModels.Last is null,
                "missing scoped adapter fails closed without implicit default fallback");
            var separate = await workflows.SubmitAsync(Request("separate-run", "separate-chat"), null, default);
            var separateResult = await generate.ExecuteAsync(Context(separate, new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [])), default);
            Check(separateResult.State == WorkflowEffectState.Succeeded && scopes[^1] == (profile.Id, "separate-chat", "separate-run") &&
                otherModels.Last is not null && !otherModels.Last.Messages.SelectMany(item => item.Content).OfType<ImageContentPart>().Any(),
                "another conversation gets its separately resolved adapter without prior image content");
            var currentModel = state.Get<ModelRecord>("model", profile.ModelId)!;
            var capturedModel = WorkerExecutionSnapshot.Read(reopened.Submission.SettingsSnapshot).Model!;
            Check(capturedModel.VisionProjectorPath is null && WorkerExecutionSnapshot.SameModel(capturedModel, currentModel),
                "legacy/default null projector keeps its original model binding");
            currentModel.VisionProjectorPath = Path.Combine(root, "projector.gguf"); state.Put("model", currentModel.Id, currentModel);
            var generationCount = models.Generated;
            var changedProjector = await generate.ExecuteAsync(Context(reopened, new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [])), default);
            Check(changedProjector.State == WorkflowEffectState.Failed && models.Generated == generationCount &&
                !WorkerExecutionSnapshot.SameModel(capturedModel, currentModel), "projector addition after admission prevents inference on different components");
            var projectorBinding = WorkerExecutionSnapshot.Bind(currentModel);
            currentModel.VisionProjectorPath = Path.Combine(root, "replacement-projector.gguf");
            Check(projectorBinding.VisionProjectorPath == Path.Combine(root, "projector.gguf") &&
                !WorkerExecutionSnapshot.SameModel(projectorBinding, currentModel), "captured projector path remains fixed after model mutation");
            var legacyBinding = JsonSerializer.Deserialize<WorkerExecutionSnapshot.ModelBinding>(
                """{"id":"legacy","path":"fixture","chatTemplate":null,"stopSequences":[],"maximumContextTokens":4096,"parallelRequests":1,"gpuLayers":0}""",
                CerebralJson.Options)!;
            Check(legacyBinding.VisionProjectorPath is null, "older serialized model bindings omit projector without losing compatibility");
            var legacyTool = JsonSerializer.Deserialize<ToolDescriptor>(
                """{"name":"plugin.projects","description":"Inspect admitted projects.","inputSchema":{"type":"object","additionalProperties":false},"capability":"plugins","modelVisible":true,"singleUsePerAssignment":false,"activationTools":["plugin.contract"]}""",
                CerebralJson.Options)!;
            var currentTool = legacyTool with
            {
                PrerequisiteTools = ["plugin.contract", "file.read", "file.edit", "process.run"]
            };
            Check(legacyTool.PrerequisiteTools.Count == 0 &&
                WorkerExecutionSnapshot.SameTool(legacyTool, currentTool) &&
                legacyTool.PrerequisiteTools.Count == 0,
                "older accepted tool descriptors permit new preparation hints without mutating their frozen hints");
            var frozenHintedTool = currentTool;
            Check(WorkerExecutionSnapshot.SameTool(frozenHintedTool,
                    currentTool with { PrerequisiteTools = ["different.live.hint"] }) &&
                frozenHintedTool.PrerequisiteTools.SequenceEqual(["plugin.contract", "file.read", "file.edit", "process.run"]),
                "live preparation hints cannot replace the descriptor hints frozen at admission");
            Check(!WorkerExecutionSnapshot.SameTool(legacyTool, currentTool with { Name = "plugin.scaffold" }),
                "tool name remains part of accepted execution identity");
            Check(!WorkerExecutionSnapshot.SameTool(legacyTool, currentTool with { Description = "Changed description." }),
                "tool description remains part of accepted execution identity");
            Check(!WorkerExecutionSnapshot.SameTool(legacyTool, currentTool with
                { InputSchema = Element(new { type = "object", required = new[] { "root" }, additionalProperties = false }) }),
                "tool input schema remains part of accepted execution identity");
            Check(!WorkerExecutionSnapshot.SameTool(legacyTool, currentTool with { Capability = "filesystem" }),
                "tool capability remains part of accepted execution identity");
            Check(!WorkerExecutionSnapshot.SameTool(legacyTool, currentTool with { ModelVisible = false }),
                "model visibility remains part of accepted execution identity");
            Check(!WorkerExecutionSnapshot.SameTool(legacyTool, currentTool with { SingleUsePerAssignment = true }),
                "single-use policy remains part of accepted execution identity");
            Check(!WorkerExecutionSnapshot.SameTool(legacyTool, currentTool with { ActivationTools = ["different.activation"] }),
                "activation gates remain part of accepted execution identity");
            currentModel.VisionProjectorPath = null; state.Put("model", currentModel.Id, currentModel);
            var textModels = new Models(["text"]);
            var textHost = new WorkerWorkflowEffects(state, configuration, textModels, tools);
            var rejected = await textHost.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && command.Descriptor.Version == 1)
                .ExecuteAsync(Context(reopened, new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [])), default);
            Check(rejected.State == WorkflowEffectState.Failed && rejected.Error!.Contains("does not support image", StringComparison.Ordinal) &&
                textModels.Last is null && workflows.Store.Get(reopened.Id).Submission.AcceptedContent.Count == 1,
                "unsupported adapter fails before inference without losing reference");

            var claimed = workflows.Store.TryClaim(reopened.Id)!;
            workflows.Store.ApplyStep(claimed.Id, claimed.Revision,
                new(WorkflowContract.EmptyObject, new CompleteWorkflow(Element(new { summary = "fixture" }))), null);
            var next = await workflows.SubmitAsync(Request("accepted-two"), null, default);
            var history = host.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.HistoryCommand);
            var page = await history.ExecuteAsync(Context(next, new { }), default);
            var data = page.Data!.Value.Deserialize<WorkerWorkflowEffects.HistoryData>(CerebralJson.Options)!;
            var prior = data.Context.Contributions.Single(item => item.Id == acceptedEvent.Id);
            Check(page.State == WorkflowEffectState.Succeeded && prior.BeforeCurrentInput &&
                prior.Messages.SelectMany(item => item.Content).OfType<ImageContentPart>().Single().Attachment.Revision == 3,
                "history preserves typed earlier image after SQLite reopen");
            Check(data.Context.Contributions.Any(item => item.Messages.SelectMany(message => message.Content).OfType<TextContentPart>()
                .Any(part => part.Text.Contains("PRIVATE ASSIGNMENT", StringComparison.Ordinal))), "hidden prior assignment remains in history");
            var continued = await generate.ExecuteAsync(Context(next, new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [], Context: data.Context)), default);
            Check(continued.State == WorkflowEffectState.Succeeded && models.Last!.Messages.SelectMany(item => item.Content).OfType<ImageContentPart>().Count() == 1,
                "context planner retains typed historical image exactly once");
            var exact = await history.ExecuteAsync(Context(next, new { eventId = acceptedEvent.Id }), default);
            Check(exact.State == WorkflowEffectState.Succeeded &&
                exact.Data!.Value.GetProperty("json").GetString()!.Contains("approved-image", StringComparison.Ordinal) &&
                !exact.Data.Value.GetProperty("json").GetString()!.Contains("storageKey", StringComparison.Ordinal), "exact history has opaque references, no storage paths");
            var emptyClaim = workflows.Store.TryClaim(next.Id)!;
            workflows.Store.ApplyStep(emptyClaim.Id, emptyClaim.Revision,
                new(WorkflowContract.EmptyObject, new CompleteWorkflow(Element(new { summary = "fixture" }))), null);
            var imageOnly = await workflows.SubmitAsync(Request("image-only") with { UserText = "", AcceptedContent = [Image()] }, null, default);
            Check(imageOnly.Submission.UserText == "" && workflows.Journal.ReadPage("accepted-chat").Events
                .Single(item => item.TurnId == imageOnly.Id && item.Type == CerebralEventTypes.Message).Content[0] is TextContentPart { Text: "" },
                "attachment-only admission preserves empty exact text without injecting a synthetic request");
            Reject<ArgumentException>(() => workflows.Store.Submit(imageOnly.Submission with
                { RequestId = "empty-text-only", ConversationId = "empty", AcceptedContent = [] }), "empty text-only request");
            Reject<ArgumentNullException>(() => workflows.Store.Submit(imageOnly.Submission with
                { RequestId = "null-text", ConversationId = "empty", UserText = null! }), "null text despite attachments");
            Check(tools.Executions == 0, "metadata handling never executes tools");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing cleanup outside owned temporary root.");
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"Accepted workflow content: {checks} SQLite/deterministic-host checks passed; no byte import or image inference.");
    }
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);
    private sealed class Module : IWorkflowModule
    {
        public string Id => "accepted.fixture";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new Definition()];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
        private sealed class Definition : IWorkflowDefinition
        {
            public WorkflowDefinitionDescriptor Descriptor { get; } = new("accepted.fixture:input", "1.0.0", "Fixture input",
                Element(new { type = "object", additionalProperties = false }), []);
            public WorkflowStep Advance(WorkflowRun snapshot) => new(WorkflowContract.EmptyObject, new CompleteWorkflow(Element(new { summary = "fixture" })));
        }
    }
    private sealed class Tools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public Func<CancellationToken, Task>? BeforeList;
        public int Executions;
        public async Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (BeforeList is not null) await BeforeList(token); return []; }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        { Executions++; throw new InvalidOperationException("No tool execution belongs in this fixture."); }
    }
    private sealed class Models(IReadOnlyList<string> capabilities) : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("accepted-fixture", "1.0.0", capabilities);
        public ModelGenerationRequest? Last;
        public int Generated;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Last = request; return Task.FromResult<IPreparedModelGeneration>(new Lease(this, request)); }
        private sealed class Lease(Models owner, ModelGenerationRequest request) : IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata { get; } = new("fixture", "accepted-fixture", "1.0.0", request.ModelId,
                42, request.ContextLimit, request.ResponseLimit, "deterministic fixture");
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            { token.ThrowIfCancellationRequested(); owner.Generated++; return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, "Fixture reply", 2, 42)); }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
