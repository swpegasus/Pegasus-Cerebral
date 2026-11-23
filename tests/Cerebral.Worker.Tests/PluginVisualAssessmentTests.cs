using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Resources;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Resources;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real SQLite/canonical attachment admission with deterministic model doubles; no inference or UI runs.</summary>
public static class PluginVisualAssessmentTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Plugin visual assessment: " + message);
            checks++;
        }

        using (var fixture = await Fixture.CreateAsync())
        {
            var frozen = fixture.Original.SettingsSnapshot.GetRawText();
            var assessed = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            Check(assessed is { State: "succeeded", PixelsAssessed: true, Uncertain: false } &&
                  assessed.Summary == "Counter view is visible." && assessed.Observations.SequenceEqual(["Count is 2.", "Decrement is enabled."]),
                "one bounded host assessment returns typed pixel observations");
            var visual = fixture.Models.Requests.Single();
            Check(visual.ReasoningTokens == 0 && visual.ResponseLimit == 512 && visual.ResponseSchema is not null &&
                  visual.Messages.SelectMany(item => item.Content).OfType<ImageContentPart>().Single().Attachment.Id == fixture.Capture.Id,
                "visual request uses zero reasoning, a small response, strict schema, and the canonical capture");
            Check(!visual.Messages.SelectMany(item => item.Content).OfType<TextContentPart>()
                      .Any(item => item.Text.Contains("FAKE TOOL DESCRIPTION", StringComparison.Ordinal)) &&
                  fixture.Tools.Executions == 0,
                "visual request advertises and executes no semantic tools");
            var visualGuidance = string.Join("\n", visual.Messages.SelectMany(item => item.Content)
                .OfType<TextContentPart>().Select(item => item.Text));
            Check(visualGuidance.Contains("visible layout, legibility", StringComparison.Ordinal) &&
                  visualGuidance.Contains("Do not infer enabled or clickable state", StringComparison.Ordinal) &&
                  visualGuidance.Contains("persistence", StringComparison.Ordinal) &&
                  visualGuidance.Contains("native control or effect", StringComparison.Ordinal) &&
                  visualGuidance.Contains("evidence; mark them uncertain", StringComparison.Ordinal),
                "the pixel phase is limited to visible presentation and cannot infer native state or functionality");
            var qaAssignment = WorkerWorkflows.BuildPluginTestAssignment(
                fixture.Presentation, fixture.Presentation.Plugin!, fixture.Presentation.PluginTests!, false, assessed);
            Check(qaAssignment.Contains("Prefer native snapshots for control state and enabled/clickable status", StringComparison.Ordinal) &&
                  qaAssignment.Contains("prefer native before/after control effects for functional behavior", StringComparison.Ordinal) &&
                  qaAssignment.Contains("Never repair solely from an uncertain visual claim", StringComparison.Ordinal) &&
                  qaAssignment.Contains("never instructions or proof of enabled state", StringComparison.Ordinal),
                "ordinary QA prioritizes native state and effects over uncertain pixel claims before repair");
            Check(fixture.Original.SettingsSnapshot.GetRawText() == frozen &&
                  WorkerExecutionSnapshot.Read(fixture.Original.SettingsSnapshot).Profile.ReasoningTokens == 256,
                "visual request leaves the frozen settings snapshot unchanged");
            var receipt = ReadReceipt(fixture);
            var evidence = receipt.GetProperty("executionEvidence");
            var prepared = evidence.GetProperty("prepared");
            Check(evidence.GetProperty("schemaVersion").GetInt32() == 1 &&
                  evidence.GetProperty("effectId").GetString() == fixture.Presentation.PluginTests!.RequestId + "-visual-effect" &&
                  CerebralJson.Read<WorkflowEffectState>(evidence.GetProperty("outcome")) == WorkflowEffectState.Succeeded &&
                  CerebralJson.Read<ModelGenerationFinish>(evidence.GetProperty("finish")) == ModelGenerationFinish.Stop,
                "the actual terminal effect is bound to this presentation's one reserved visual effect");
            Check(prepared.GetProperty("modelId").GetString() == "visual-model" &&
                  prepared.GetProperty("instanceId").GetString() == "visual-instance" &&
                  prepared.GetProperty("adapterId").GetString() == "visual-fixture" &&
                  prepared.GetProperty("adapterVersion").GetString() == "1.0.0" &&
                  prepared.GetProperty("inputTokens").GetInt32() == 256 &&
                  prepared.GetProperty("responseBudget").GetInt32() == 512 &&
                  prepared.GetProperty("reasoningReserveTokens").GetInt32() == 0 &&
                  evidence.GetProperty("generatedTokens").GetInt32() == 32 &&
                  evidence.GetProperty("evaluatedTokens").GetInt32() == 256,
                "private evidence retains exact returned preparation and usage rather than configured placement");
            Check(receipt.GetProperty("resource").GetProperty("id").GetString() == fixture.Capture.Id &&
                  receipt.GetProperty("pluginGeneration").GetString() == fixture.Presentation.Plugin!.Generation &&
                  receipt.GetProperty("packageFingerprint").GetString() == fixture.Presentation.PackageFingerprint &&
                  !evidence.TryGetProperty("text", out _) && !evidence.TryGetProperty("image", out _) &&
                  !evidence.TryGetProperty("error", out _) && System.Text.Encoding.UTF8.GetByteCount(evidence.GetRawText()) < 8192,
                "exact immutable capture and generation association remains bounded without output or image bodies");

            var generate = fixture.Workflows.Registry.GetCommand(WorkerWorkflowEffects.GenerateCommand, 1);
            Check(!generate.Descriptor.InputSchema.GetProperty("properties")
                      .TryGetProperty("hostReasoningTokens", out _),
                "the host-only reasoning override is absent from the public workflow command schema");
            var forbidden = await generate.ExecuteAsync(new WorkflowCommandContext("forbidden-host-override",
                fixture.Original, CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [],
                    HostReasoningTokens: 128))) { RunId = fixture.Original.RequestId }, default);
            Check(forbidden.State == WorkflowEffectState.Failed && fixture.Models.Requests.Count == 1,
                "the private override accepts only zero and rejects a nonzero value before model preparation");
            var ordinary = await generate.ExecuteAsync(new WorkflowCommandContext("ordinary-effect", fixture.Original,
                CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [])))
                { RunId = fixture.Original.RequestId }, default);
            Check(ordinary.State == WorkflowEffectState.Succeeded && fixture.Models.Requests.Count == 2 &&
                  fixture.Models.Requests[1].ReasoningTokens == 256 &&
                  !fixture.Models.Requests[1].Messages.SelectMany(item => item.Content).OfType<ImageContentPart>().Any(),
                "the following ordinary generation retains frozen reasoning and receives no assessment image");

            var reopenedModels = new Models();
            var reopened = fixture.Reopen(reopenedModels);
            var replay = await reopened.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            Check(replay.PixelsAssessed && reopenedModels.Requests.Count == 0,
                "a reopened host returns the durable assessment without replaying inference");

            var changed = Copy(fixture.Presentation); changed.PackageFingerprint = new string('b', 64);
            fixture.State.Put("interface", changed.Id, changed);
            var rejected = false;
            try
            {
                await reopened.EnsurePluginVisualAssessmentAsync(
                    fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            }
            catch (PluginTestsDeferredException) { rejected = true; }
            Check(rejected && reopenedModels.Requests.Count == 0,
                "a changed owner review or package fingerprint cannot reuse the visual receipt");
        }

        using (var fixture = await Fixture.CreateAsync(ModelGenerationFinish.Unknown))
        {
            fixture.Models.GeneratedTokens = null; fixture.Models.EvaluatedTokens = null;
            var first = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            var secondModels = new Models();
            var second = await fixture.Reopen(secondModels).EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            Check(first.State == "unknown" && second.State == "unknown" &&
                  fixture.Models.Requests.Count == 1 && secondModels.Requests.Count == 0,
                "an uncertain provider outcome is explicit and never replayed after reopen");
            var evidence = ReadReceipt(fixture).GetProperty("executionEvidence");
            Check(CerebralJson.Read<WorkflowEffectState>(evidence.GetProperty("outcome")) == WorkflowEffectState.Unknown &&
                  CerebralJson.Read<ModelGenerationFinish>(evidence.GetProperty("finish")) == ModelGenerationFinish.Unknown &&
                  evidence.GetProperty("prepared").GetProperty("instanceId").GetString() == "visual-instance" &&
                  evidence.GetProperty("generatedTokens").ValueKind == JsonValueKind.Null &&
                  evidence.GetProperty("evaluatedTokens").ValueKind == JsonValueKind.Null,
                "uncertain inference retains its actual prepared instance and unknown terminal without completion credit");
        }

        using (var fixture = await Fixture.CreateAsync(projector: false))
        {
            var unavailable = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            Check(unavailable.State == "unavailable" && unavailable.Message!.Contains("no admitted image projector", StringComparison.Ordinal) &&
                  fixture.Models.Requests.Count == 0,
                "missing frozen projector is reported before model preparation");
            Check(!ReadReceipt(fixture).TryGetProperty("executionEvidence", out _),
                "preparation not dispatched has no fabricated instance or inference completion");
        }

        using (var fixture = await Fixture.CreateAsync(captureScope: "foreign"))
        {
            var unavailable = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            Check(unavailable.State == "unavailable" && unavailable.Message!.Contains("original profile and conversation scope", StringComparison.Ordinal) &&
                  fixture.Models.Requests.Count == 0,
                "foreign-scope canonical pixels remain unavailable without revealing or sending them");
        }

        using (var fixture = await Fixture.CreateAsync(receiptMismatch: true))
        {
            var unavailable = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            Check(unavailable.State == "unavailable" && fixture.Models.Requests.Count == 0,
                "forged capture metadata fails closed before provider preparation");
        }

        using (var fixture = await Fixture.CreateAsync(ModelGenerationFinish.Cancelled))
        {
            fixture.Models.GeneratedTokens = null; fixture.Models.EvaluatedTokens = null;
            var cancelled = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            var replayModels = new Models();
            var retained = await fixture.Reopen(replayModels).EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            Check(cancelled.State == "cancelled" && retained.State == "cancelled" && replayModels.Requests.Count == 0,
                "provider-confirmed cancellation is durable and cannot cause an automatic replay");
            var evidence = ReadReceipt(fixture).GetProperty("executionEvidence");
            Check(CerebralJson.Read<WorkflowEffectState>(evidence.GetProperty("outcome")) == WorkflowEffectState.Cancelled &&
                  CerebralJson.Read<ModelGenerationFinish>(evidence.GetProperty("finish")) == ModelGenerationFinish.Cancelled &&
                  evidence.GetProperty("cancellationConfirmed").GetBoolean() &&
                  evidence.GetProperty("generatedTokens").ValueKind == JsonValueKind.Null &&
                  evidence.GetProperty("evaluatedTokens").ValueKind == JsonValueKind.Null,
                "confirmed cancellation is retained separately from successful completion");
        }

        using (var fixture = await Fixture.CreateAsync(ModelGenerationFinish.Length))
        {
            var result = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            var evidence = ReadReceipt(fixture).GetProperty("executionEvidence");
            Check(result.State == "unavailable" && !result.PixelsAssessed &&
                  CerebralJson.Read<WorkflowEffectState>(evidence.GetProperty("outcome")) == WorkflowEffectState.Succeeded &&
                  CerebralJson.Read<ModelGenerationFinish>(evidence.GetProperty("finish")) == ModelGenerationFinish.Length,
                "an actual bounded generation can succeed while its incomplete visual assessment remains unavailable");
        }

        using (var fixture = await Fixture.CreateAsync())
        {
            fixture.Models.MetadataTransform = value => value with { ReasoningReserveTokens = 1 };
            var result = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            var evidence = ReadReceipt(fixture).GetProperty("executionEvidence");
            Check(result.State == "unavailable" && !result.PixelsAssessed &&
                  evidence.GetProperty("prepared").GetProperty("reasoningReserveTokens").GetInt32() == 1,
                "an unexpected returned reasoning reserve remains actual evidence without a reasoning-off verdict");
        }

        using (var fixture = await Fixture.CreateAsync(ModelGenerationFinish.Unknown))
        {
            fixture.Models.MetadataTransform = value => value with { ModelId = "foreign-model" };
            var result = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            var replayModels = new Models();
            var replay = await fixture.Reopen(replayModels).EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            Check(result.State == "unknown" && replay.State == "unknown" && replayModels.Requests.Count == 0 &&
                  !ReadReceipt(fixture).TryGetProperty("executionEvidence", out _),
                "invalid metadata on an unknown effect cannot manufacture proof or turn uncertainty into retry");
        }

        using (var fixture = await Fixture.CreateAsync())
        {
            fixture.Models.ResponseOverride = "invalid visual JSON";
            fixture.Models.GeneratedTokens = null; fixture.Models.EvaluatedTokens = null;
            var result = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            var evidence = ReadReceipt(fixture).GetProperty("executionEvidence");
            Check(result.State == "unavailable" && !result.PixelsAssessed &&
                  CerebralJson.Read<WorkflowEffectState>(evidence.GetProperty("outcome")) == WorkflowEffectState.Succeeded &&
                  CerebralJson.Read<ModelGenerationFinish>(evidence.GetProperty("finish")) == ModelGenerationFinish.Stop,
                "completed inference is retained even when its response fails bounded visual parsing");
            Check(evidence.GetProperty("generatedTokens").ValueKind == JsonValueKind.Null &&
                  evidence.GetProperty("evaluatedTokens").ValueKind == JsonValueKind.Null,
                "missing provider usage remains null and is never synthesized as zero");
        }

        using (var fixture = await Fixture.CreateAsync())
        {
            fixture.Models.Error = "PRIVATE_PROVIDER_ERROR";
            var result = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            var receipt = ReadReceipt(fixture); var evidence = receipt.GetProperty("executionEvidence");
            Check(result.State == "unavailable" && CerebralJson.Read<WorkflowEffectState>(evidence.GetProperty("outcome")) == WorkflowEffectState.Failed &&
                  evidence.GetProperty("hasError").GetBoolean() &&
                  !receipt.GetRawText().Contains("PRIVATE_PROVIDER_ERROR", StringComparison.Ordinal),
                "actual failed terminal and error presence are retained without private error body or false success");
        }

        using (var fixture = await Fixture.CreateAsync())
        {
            fixture.Models.ResponseOverride = new string('x', WorkflowContract.MaximumStateBytes + 1024);
            var result = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            var evidence = ReadReceipt(fixture).GetProperty("executionEvidence");
            Check(result.State == "unavailable" && evidence.GetProperty("outputOmitted").GetBoolean() &&
                  evidence.GetProperty("hasError").GetBoolean() && CerebralJson.Read<WorkflowEffectState>(evidence.GetProperty("outcome")) == WorkflowEffectState.Failed,
                "durable output omission preserves actual finish and explicitly separates host failure from visual success");
        }

        foreach (var transform in new Func<PreparedModelMetadata, PreparedModelMetadata>[]
                 { value => value with { ModelId = "foreign-model" },
                   value => value with { InstanceId = "" },
                   value => value with { CountBasis = new string('x', 4097) },
                   value => value with { InputTokens = value.EffectiveContext },
                   value => value with { AdapterVersion = "bad\nversion" } })
        {
            using var fixture = await Fixture.CreateAsync();
            fixture.Models.MetadataTransform = transform;
            var result = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            Check(result.State == "unavailable" && !result.PixelsAssessed &&
                  !ReadReceipt(fixture).TryGetProperty("executionEvidence", out _),
                "foreign, incomplete, unbounded or inconsistent prepared evidence cannot establish image assessment");
        }

        using (var fixture = await Fixture.CreateAsync())
        {
            await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            var legacy = JsonNode.Parse(ReadReceipt(fixture).GetRawText())!.AsObject();
            legacy.Remove("executionEvidence");
            fixture.State.Put("plugin-visual-assessment", fixture.Presentation.Id, legacy);
            var reopenedModels = new Models();
            var historical = await fixture.Reopen(reopenedModels).EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            Check(historical.PixelsAssessed && reopenedModels.Requests.Count == 0 &&
                  !ReadReceipt(fixture).TryGetProperty("executionEvidence", out _),
                "schema1 legacy assessments retain historical verdict without invented proof or provider replay");
        }

        foreach (var corrupt in new Action<JsonObject>[]
                 { value => value["effectId"] = "foreign-effect",
                   value => value["schemaVersion"] = 2,
                   value => value["outcome"] = "unknown",
                   value => value["prepared"]!["modelId"] = "foreign-model",
                   value => value["prepared"]!["reasoningReserveTokens"] = 1 })
        {
            using var fixture = await Fixture.CreateAsync();
            await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            var altered = JsonNode.Parse(ReadReceipt(fixture).GetRawText())!.AsObject();
            corrupt(altered["executionEvidence"]!.AsObject());
            fixture.State.Put("plugin-visual-assessment", fixture.Presentation.Id, altered);
            var reopenedModels = new Models(); var rejected = false;
            try
            {
                await fixture.Reopen(reopenedModels).EnsurePluginVisualAssessmentAsync(
                    fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && reopenedModels.Requests.Count == 0,
                "retained contradictory or foreign execution proof rejects reuse without inference replay");
        }

        using (var fixture = await Fixture.CreateAsync(waitForRelease: true))
        {
            var first = fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            await fixture.Models.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            var oneDispatch = fixture.Models.Requests.Count == 1;
            fixture.Models.Release.TrySetResult();
            var results = await Task.WhenAll(first, second);
            Check(oneDispatch && results.All(item => item.PixelsAssessed) && fixture.Models.Requests.Count == 1,
                "concurrent callers await one in-process assessment instead of admitting QA from a started receipt");
        }

        using (var fixture = await Fixture.CreateAsync(reasoningMode: "off"))
        {
            var direct = await fixture.Workflows.EnsurePluginVisualAssessmentAsync(
                fixture.Presentation, fixture.Original, fixture.Snapshot, default);
            Check(direct.State == "direct" && fixture.Models.Requests.Count == 0,
                "a reasoning-off profile keeps the established direct-image QA route without a second generation");
        }

        Console.WriteLine($"Plugin visual assessment checks passed: {checks}");
    }

    private static InterfaceRequest Copy(InterfaceRequest value) =>
        CerebralJson.Read<InterfaceRequest>(CerebralJson.Element(value));

    private static JsonElement ReadReceipt(Fixture fixture) =>
        fixture.State.Get<JsonElement>("plugin-visual-assessment", fixture.Presentation.Id);

    private sealed class Fixture : IDisposable
    {
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        private readonly string root;
        private readonly Configuration configuration;
        private readonly EventHub events = new();
        private readonly AttachmentStore attachments;
        public StateStore State { get; }
        public Tools Tools { get; }
        public Models Models { get; }
        public WorkerWorkflows Workflows { get; }
        public WorkflowSubmission Original { get; }
        public WorkerExecutionSnapshot.Snapshot Snapshot { get; }
        public InterfaceRequest Presentation { get; }
        public AttachmentReference Capture { get; }

        private Fixture(string root, StateStore state, Configuration configuration, AttachmentStore attachments,
            Tools tools, Models models, WorkerWorkflows workflows, WorkflowSubmission original,
            WorkerExecutionSnapshot.Snapshot snapshot, InterfaceRequest presentation, AttachmentReference capture)
        {
            this.root = root; State = state; this.configuration = configuration; this.attachments = attachments;
            Tools = tools;
            Models = models; Workflows = workflows; Original = original; Snapshot = snapshot;
            Presentation = presentation; Capture = capture;
        }

        public static async Task<Fixture> CreateAsync(ModelGenerationFinish finish = ModelGenerationFinish.Stop,
            bool projector = true, string captureScope = "original", bool receiptMismatch = false,
            bool waitForRelease = false, string reasoningMode = "on")
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                "Cerebral.PluginVisualAssessmentTests-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            var state = new StateStore(Path.Combine(root, "data"));
            var configuration = new Configuration(state);
            var settings = configuration.Get(); settings.QueueCapacity = 32; configuration.Save(settings);
            var profile = new AgentProfile
            {
                Id = "visual-lead", Name = "Visual lead", Role = "lead", WorkerId = "local",
                ModelWorkerId = "local", ModelId = "visual-model", FollowsModelBudgets = false,
                ContextTokens = 8192, ResponseTokens = 1024, ReasoningMode = reasoningMode, ReasoningTokens = 256,
                Temperature = 0.2, MaxSteps = 64, MaxSeconds = 300,
                AllowedRoots = [root], AllowedTools = ["fixture.read"], Capabilities = ["filesystem.read"]
            };
            var model = new ModelRecord
            {
                Id = profile.ModelId, Path = Path.Combine(root, "visual.gguf"), AutomaticContextSizing = false,
                ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192,
                ParallelRequests = 1, GpuLayers = 0,
                VisionProjectorPath = projector ? Path.Combine(root, "visual-mmproj.gguf") : null
            };
            state.Put("agent", profile.Id, profile); state.Put("model", model.Id, model);
            var descriptor = new ToolDescriptor("fixture.read", "FAKE TOOL DESCRIPTION",
                WorkflowContract.EmptyObject, "filesystem.read");
            ServicePluginPackageBinding[] bindings =
            [
                ConversationWorkflowModule.PackageBinding,
                new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])
            ];
            var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, profile,
                "Shared fixture guidance.", "Lead fixture guidance.", WorkerExecutionSnapshot.Bind(model),
                [descriptor], profile.Capabilities.ToArray(), profile.AllowedTools.ToArray(),
                profile.AllowedRoots.ToArray(), false, WorkerExecutionSnapshot.RoutingFingerprint(configuration.Get()),
                bindings, []);
            var settingsElement = CerebralJson.Element(snapshot);
            var original = new WorkflowSubmission("origin-run", "visual-chat", profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Create a visible counter with working decrement and persistence.", "Original authoring assignment.",
                CerebralJson.Element(new { workingDirectory = root }), settingsElement, 64,
                new EventActor(EventActorKind.User, "owner", "You"));

            var journal = new EventJournal(Path.Combine(state.DirectoryPath, "engine.db"));
            var attachments = new AttachmentStore(journal);
            var access = captureScope == "original"
                ? new AttachmentAccessContext(profile.Id, original.ConversationId)
                : new AttachmentAccessContext(profile.Id, "foreign-chat");
            using var source = new MemoryStream(Png, writable: false);
            var capture = await attachments.ImportAsync(access, Guid.NewGuid().ToString("N"),
                "PluginView.png", source);
            var resource = new InterfaceResourceReceipt(capture.Id, capture.Revision, capture.MediaType,
                receiptMismatch ? "Forged.png" : capture.OriginalName, capture.Length);
            var presentationId = Guid.NewGuid().ToString("N");
            var presentation = new InterfaceRequest
            {
                Id = presentationId, SessionId = original.ConversationId, JobId = "source-run",
                ProfileId = profile.Id, Source = "workflow", Kind = "plugin", Path = Path.Combine(root, "desktop-plugin.json"),
                PackageFingerprint = new string('a', 64), Status = "completed", Resource = resource,
                Plugin = new InterfacePluginReceipt("fixture.visual", "1.0.0", "1.0.0-" + Guid.NewGuid().ToString("N"),
                    Path.Combine(root, "installed", "desktop-plugin.json"),
                    [new InterfacePluginMountReceipt("sidebar", "fixture.visual.counter")], true),
                PluginTests = new InterfacePluginTestContinuation(InterfaceRequests.PluginTestPrefix + presentationId,
                    original.RequestId, 1)
            };
            state.Put("interface", presentation.Id, presentation);

            var models = new Models(finish, waitForRelease); var tools = new Tools();
            var effects = new WorkerWorkflowEffects(state, configuration, models, tools);
            void Admit(WorkflowSubmission submission)
            {
                foreach (var image in submission.AcceptedContent.OfType<ImageContentPart>())
                    attachments.ValidateReference(new(submission.ProfileId, submission.ConversationId), image.Attachment);
            }
            var workflows = new WorkerWorkflows(state, configuration, new EventHub(),
                [new ConversationWorkflowModule(), effects], bindings, tools, Admit, journal,
                attachments: attachments);
            return new(root, state, configuration, attachments, tools, models, workflows, original, snapshot,
                presentation, capture);
        }

        public WorkerWorkflows Reopen(Models models)
        {
            var effects = new WorkerWorkflowEffects(State, configuration, models, new Tools());
            void Admit(WorkflowSubmission submission)
            {
                foreach (var image in submission.AcceptedContent.OfType<ImageContentPart>())
                    attachments.ValidateReference(new(submission.ProfileId, submission.ConversationId), image.Attachment);
            }
            return new WorkerWorkflows(State, configuration, events,
                [new ConversationWorkflowModule(), effects],
                [ConversationWorkflowModule.PackageBinding,
                    new ServicePluginPackageBinding(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])],
                new Tools(), Admit, new EventJournal(Path.Combine(State.DirectoryPath, "engine.db")),
                attachments: attachments);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private sealed class Tools : IToolDispatcher
    {
        public int Executions;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            Executions++;
            throw new InvalidOperationException("Visual assessment must not dispatch a tool.");
        }
    }

    private sealed class Models(ModelGenerationFinish finish = ModelGenerationFinish.Stop,
        bool waitForRelease = false) : IModelRuntimeAdapter
    {
        private readonly bool wait = waitForRelease;
        public ModelAdapterDescriptor Descriptor { get; } = new("visual-fixture", "1.0.0", ["text", "image"], true);
        public List<ModelGenerationRequest> Requests { get; } = [];
        public Func<PreparedModelMetadata, PreparedModelMetadata>? MetadataTransform { get; set; }
        public string? ResponseOverride { get; set; }
        public string? Error { get; set; }
        public int? GeneratedTokens { get; set; } = 32;
        public int? EvaluatedTokens { get; set; } = 256;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request);
            return Task.FromResult<IPreparedModelGeneration>(new Prepared(this, request, finish));
        }

        private sealed class Prepared(Models owner, ModelGenerationRequest request,
            ModelGenerationFinish finish) : IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata { get; } = PrepareMetadata(owner, request);
            public async Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                owner.Entered.TrySetResult();
                if (owner.wait) await owner.Release.Task.WaitAsync(token);
                var text = JsonSerializer.Serialize(new
                {
                    summary = "Counter view is visible.",
                    observations = new[] { "Count is 2.", "Decrement is enabled." },
                    concerns = Array.Empty<string>(), uncertain = false
                }, CerebralJson.Options);
                return new ModelGenerationResult(finish, owner.ResponseOverride ?? text,
                    GeneratedTokens: owner.GeneratedTokens, EvaluatedTokens: owner.EvaluatedTokens,
                    CancellationConfirmed: finish == ModelGenerationFinish.Cancelled, Error: owner.Error);
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private static PreparedModelMetadata PrepareMetadata(Models owner, ModelGenerationRequest request)
            {
                var metadata = new PreparedModelMetadata("visual-instance", "visual-fixture", "1.0.0",
                    request.ModelId, 256, request.ContextLimit, request.ResponseLimit, "deterministic fixture")
                { ReasoningReserveTokens = request.ReasoningTokens };
                return owner.MetadataTransform?.Invoke(metadata) ?? metadata;
            }
        }
    }
}
