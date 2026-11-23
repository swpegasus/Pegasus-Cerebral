using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic host-boundary checks. These do not exercise an inference server or PAO.</summary>
public static class WorkflowEffectBridgeTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-workflow-effects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Workflow effect bridge: " + description);
            checks++;
        }
        try
        {
            var store = new StateStore(Path.Combine(root, "data"));
            var configuration = new Configuration(store);
            configuration.Get();
            var profile = new AgentProfile
            {
                Id = "bridge-agent", Name = "Bridge agent", Instructions = "frozen-guidance", ModelId = "fixture",
                FollowsModelBudgets = false, ContextTokens = 4096, ResponseTokens = 256,
                AllowedTools = ["file.read", "file.write", "process.run"], Capabilities = ["files", "process"],
                AllowedRoots = [root], PluginSettings = new() { ["private-module-setting"] = Element("not-for-model-context") }
            };
            store.Put("agent", profile.Id, profile);
            store.Put("model", "fixture", new ModelRecord
            {
                Id = "fixture", Path = Path.Combine(root, "fixture.gguf"), AutomaticContextSizing = false,
                ContextTokens = 4096, MaximumContextTokens = 4096, RecommendedContextTokens = 4096
            });
            var models = new FakeModels();
            var tools = new FakeTools();
            var diagnostics = new EventHub();
            using var diagnosticLease = diagnostics.SubscribeDiagnostics();
            var bridge = new WorkerWorkflowEffects(store, configuration, models, tools, diagnosticEvents: diagnostics);
            var generate = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && x.Descriptor.Version == 1);
            var tool = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.ToolCommand);
            _ = new WorkflowRegistry([bridge]);
            Check(bridge.Workflows.Count == 0 && bridge.Commands.Select(command => command.Descriptor.Id).ToHashSet(StringComparer.Ordinal).SetEquals(
                [WorkerWorkflowEffects.GenerateCommand, WorkerWorkflowEffects.ToolCommand, WorkerWorkflowEffects.CompactCommand,
                 "cerebral.host:receipt", "cerebral.host:history", "cerebral.host:catalog", WorkerWorkflowEffects.DelegateCommand]),
                "host commands register exact effects, including coordination, without pretending to be workflow definitions");
            async Task<WorkflowSubmission> Capture()
            {
                var snapshot = await WorkerExecutionSnapshot.CaptureAsync(store, profile, configuration, tools,
                    [new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])], default);
                return new("request", "conversation", profile.Id, "fixture:workflow", "1.0.0", "EXACT USER REQUEST",
                    "EXACT PRIVATE ASSIGNMENT", Element(new { }), snapshot);
            }
            var submission = await Capture();
            WorkflowCommandContext Generation(WorkerWorkflowEffects.GenerateArguments? args = null) => new("generation-effect", submission,
                Element(args ?? new WorkerWorkflowEffects.GenerateArguments([new("user", [new TextContentPart("history")])], ToolNames: ["file.read"])));
            WorkflowCommandContext Tool(string name = "file.read") => new("tool-effect", submission,
                Element(new WorkerWorkflowEffects.ToolArguments(name, Element(new { path = root }), root)));
            void SaveProfile() => store.Put("agent", profile.Id, profile);

            profile.Instructions = "changed-after-admission";
            profile.AllowedTools.Add("new.tool");
            SaveProfile();
            var frozen = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot);
            Check(frozen.Profile.Instructions == "frozen-guidance" && !frozen.AllowedTools.Contains("new.tool") &&
                frozen.Profile.PluginSettings.Count == 0 && !submission.SettingsSnapshot.GetRawText().Contains("not-for-model-context", StringComparison.Ordinal),
                "snapshot freezes accepted instructions and grants, excluding opaque module settings");
            var generated = await generate.ExecuteAsync(Generation(), default);
            var data = generated.Data!.Value.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options)!;
            var allText = string.Join("\n", models.LastRequest!.Messages.SelectMany(x => x.Content).OfType<TextContentPart>().Select(x => x.Text));
            Check(generated.State == WorkflowEffectState.Succeeded && data.Generation.GeneratedTokens == 7 && data.Prepared.InputTokens == 42,
                "completed generation retains measured usage and preparation evidence");
            Check(allText.Contains("frozen-guidance", StringComparison.Ordinal) && !allText.Contains("changed-after-admission", StringComparison.Ordinal) &&
                allText.Contains("EXACT USER REQUEST", StringComparison.Ordinal) && allText.Contains("EXACT PRIVATE ASSIGNMENT", StringComparison.Ordinal),
                "hidden exact user and assignment text remain in model context with frozen guidance");
            Check(allText.Contains("file.read", StringComparison.Ordinal) && !allText.Contains("file.write", StringComparison.Ordinal),
                "per-step tool selection omits unused schemas");
            WorkflowSchema.Validate(generated.Data.Value, generate.Descriptor.OutputSchema);
            Check(models.Disposals == 1, "prepared capacity is released after generation");
            using (var diagnosticDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                var observed = new List<EngineDiagnosticRecord>();
                await using var records = diagnosticLease.ReadAsync(diagnosticDeadline.Token).GetAsyncEnumerator();
                while (observed.Count < 3 && await records.MoveNextAsync()) observed.Add(records.Current);
                Check(observed.Select(item => item.Phase).SequenceEqual(["generation.prepared", "model.visible-fragment", "model.terminal"]) &&
                    observed.All(item => item.ChatId == "conversation" && item.RequestId == "request" && item.AgentId == "bridge-agent" &&
                        item.ModelId == "fixture" && item.EffectId == "generation-effect") &&
                    observed.Last().GeneratedTokens == 7 && observed.First().InputTokens == 42,
                    "read-only generation observations preserve exact chat/effect identity and measured budgets/usage");
                Check(observed.Single(item => item.Text is not null).Text == "{}" &&
                    !JsonSerializer.Serialize(observed).Contains("EXACT PRIVATE ASSIGNMENT", StringComparison.Ordinal),
                    "diagnostic fragments exclude frozen prompts and private assignments");
            }
            diagnosticLease.Dispose();
            models.PublishVisibleText = false;
            using (var terminalLease = diagnostics.SubscribeDiagnostics())
            using (var terminalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                var terminalOnly = await generate.ExecuteAsync(Generation(), default);
                var terminalData = terminalOnly.Data!.Value.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options)!;
                var observed = new List<EngineDiagnosticRecord>();
                await using var records = terminalLease.ReadAsync(terminalDeadline.Token).GetAsyncEnumerator();
                while (observed.Count < 2 && await records.MoveNextAsync()) observed.Add(records.Current);
                Check(observed.Select(item => item.Phase).SequenceEqual(["generation.prepared", "model.terminal"]) &&
                    observed.Last().Text == "{}" && terminalData.FirstTokenMilliseconds is null,
                    "terminal-only providers retain visible final text without fabricating fragments or first-token timing");
                Check(observed.All(item => item.ChatId == "conversation" && item.RequestId == "request" &&
                        item.EffectId == "generation-effect") && observed.Last().GeneratedTokens == 7,
                    "terminal-only capture retains actual chat/effect identity and provider usage");
                Check(!JsonSerializer.Serialize(observed).Contains("EXACT PRIVATE ASSIGNMENT", StringComparison.Ordinal),
                    "terminal fallback never copies prepared private assignments");
            }
            models.PublishVisibleText = true;
            await generate.ExecuteAsync(Generation(new([
                new("system", [new TextContentPart("harness policy")]),
                new("assistant", [new TextContentPart("prior action")]),
                new("user", [new TextContentPart("latest failure evidence")])], ToolNames: [])), default);
            var ordered = models.LastRequest.Messages.Select(message => string.Concat(message.Content.OfType<TextContentPart>().Select(x => x.Text))).ToArray();
            Check(ordered.Length == 7 && ordered[1] == "harness policy" &&
                ordered[2].StartsWith("Host-provided lookup coordinates", StringComparison.Ordinal) &&
                models.LastRequest.Messages[2].Role == "system" &&
                System.Text.Encoding.UTF8.GetByteCount(ordered[2]) <= 3072 &&
                ordered[3] == "EXACT USER REQUEST" &&
                ordered[4].StartsWith("Internal workflow assignment:", StringComparison.Ordinal) &&
                models.LastRequest.Messages[4].Content.OfType<TextContentPart>().Last().Text == "EXACT PRIVATE ASSIGNMENT" &&
                ordered[5] == "prior action" && ordered[6] == "latest failure evidence",
                "immutable request precedes history so current tool feedback remains the latest model input");
            models.PreparationFailure = new ModelContextLimitException(new("fixture", "fixture", "1", "fixture", 3900, 4096, 0, "fixture") { DesiredResponseBudget = 1024, SafetyReserveTokens = 128 });
            var preparationFailure = await generate.ExecuteAsync(Generation(), default);
            Check(preparationFailure.State == WorkflowEffectState.Failed && preparationFailure.Error!.Contains("3900", StringComparison.Ordinal) &&
                preparationFailure.Error.Contains("4096", StringComparison.Ordinal), "budget failure preserves actionable measured capacity");
            models.PreparationFailure = new LlamaProtocolException("Model apply-template failed with HTTP 500.");
            preparationFailure = await generate.ExecuteAsync(Generation(), default);
            Check(preparationFailure.Error == "Model apply-template failed with HTTP 500.", "safe adapter protocol diagnostic survives the host bridge");
            models.PreparationFailure = new IOException("Untrusted provider body containing private fixture text");
            preparationFailure = await generate.ExecuteAsync(Generation(), default);
            Check(!preparationFailure.Error!.Contains("private fixture", StringComparison.Ordinal), "raw external exception bodies are not forwarded as agent diagnostics");
            models.PreparationFailure = null;

            await generate.ExecuteAsync(Generation(new([], ResponseSharePercent: 50, ToolNames: [])), default);
            Check(models.LastRequest.ResponseLimit == 2048 && models.LastRequest.ResponseSharePercent == 50,
                "explicit output share can exceed the default response budget within half the context");
            await generate.ExecuteAsync(Generation(new([], ResponseSharePercent: 26, ToolNames: [])), default);
            Check(models.LastRequest.ResponseLimit == 1064, "integer response share is passed as a bounded desired budget");
            var before = models.Preparations;
            Check((await generate.ExecuteAsync(Generation(new([], ResponseSharePercent: 51)), default)).State == WorkflowEffectState.Failed && models.Preparations == before,
                "share outside configured contract is rejected before preparation");
            Check((await generate.ExecuteAsync(Generation(new([], ToolNames: ["new.tool"])), default)).State == WorkflowEffectState.Failed && models.Preparations == before,
                "new grants cannot introduce uncaptured tools to accepted work");
            var image = new ImageContentPart(new("fixture-image", 1, "image/png", "fixture.png", 100));
            Check((await generate.ExecuteAsync(Generation(new([new("user", [image])])), default)).State == WorkflowEffectState.Failed && models.Preparations == before,
                "unsupported image input is rejected rather than silently dropped or flattened");

            foreach (var (finish, confirmed, expected) in new[]
            {
                (ModelGenerationFinish.Length, false, WorkflowEffectState.Succeeded),
                (ModelGenerationFinish.Unknown, false, WorkflowEffectState.Unknown),
                (ModelGenerationFinish.Cancelled, false, WorkflowEffectState.Unknown),
                (ModelGenerationFinish.Cancelled, true, WorkflowEffectState.Cancelled)
            })
            {
                models.Result = new(finish, "partial payload", 7, 42, confirmed);
                generated = await generate.ExecuteAsync(Generation(), default);
                data = generated.Data!.Value.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options)!;
                Check(generated.State == expected && data.Generation.Finish == finish && data.Generation.Text == "partial payload" && data.Generation.GeneratedTokens == 7,
                    "terminal mapping retains partial evidence for " + finish + "/" + confirmed);
            }
            models.ThrowDuringGeneration = true;
            Check((await generate.ExecuteAsync(Generation(), default)).State == WorkflowEffectState.Unknown,
                "post-dispatch cancellation exception cannot masquerade as confirmed cancellation");
            models.ThrowDuringGeneration = false;
            models.Result = new(ModelGenerationFinish.Stop, "{}", 7, 42, Error: "Adapter reported failure after Stop.");
            generated = await generate.ExecuteAsync(Generation(), default);
            Check(generated.State == WorkflowEffectState.Failed && generated.Data!.Value.GetProperty("generation").GetProperty("generatedTokens").GetInt32() == 7,
                "a provider error cannot turn a Stop action into successful executable model output");
            var priorGeneration = new WorkflowEffect("generation-effect", WorkerWorkflowEffects.GenerateCommand, 1,
                Generation().Arguments, WorkflowReplayPolicy.Never, WorkflowEffectState.Unknown, generated.Data, ProviderReceiptId: "retained-generation");
            var reconciledGeneration = await generate.ReconcileAsync(Generation() with { PreviousEffect = priorGeneration }, default);
            Check(reconciledGeneration.State == WorkflowEffectState.Unknown && reconciledGeneration.Data?.GetRawText() == generated.Data?.GetRawText() &&
                reconciledGeneration.ProviderReceiptId == "retained-generation", "unknown lookup preserves earlier partial inference and usage evidence");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); before = models.Preparations;
                Check((await generate.ExecuteAsync(Generation(), cancelled.Token)).State == WorkflowEffectState.Cancelled && models.Preparations == before,
                    "cancellation before dispatch avoids inference");
            }
            models.Result = new(ModelGenerationFinish.Stop, new string('x', WorkflowContract.MaximumStateBytes + 100), 9, 42);
            generated = await generate.ExecuteAsync(Generation(), default);
            data = generated.Data!.Value.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options)!;
            Check(generated.State == WorkflowEffectState.Failed && data.OutputOmitted && data.Generation.Text.Length == 0 && data.Generation.GeneratedTokens == 9,
                "oversized model JSON is omitted with usage retained, never truncated into an executable action");
            models.Result = new(ModelGenerationFinish.Stop, "{}", 7, 42);

            profile.ReasoningMode = "on"; profile.ReasoningTokens = 512; SaveProfile(); submission = await Capture();
            before = models.Preparations;
            Check((await generate.ExecuteAsync(Generation(), default)).State == WorkflowEffectState.Failed && models.Preparations == before,
                "unsupported prepared reasoning is rejected before loading or inference");
            models.SupportsReasoning = true;
            profile.ReasoningTokens = 1024; SaveProfile();
            await generate.ExecuteAsync(Generation(), default);
            Check(models.LastRequest!.ReasoningTokens == 512,
                "reasoning uses the frozen admitted reservation rather than enlarged live profile settings");
            profile.ReasoningMode = "off"; SaveProfile(); submission = await Capture();
            await generate.ExecuteAsync(Generation(), default);
            Check(models.LastRequest!.ReasoningTokens == 0,
                "off mode remains compatible and carries no hidden reasoning reservation");
            models.SupportsReasoning = false;

            var result = await tool.ExecuteAsync(Tool(), default);
            Check(result.State == WorkflowEffectState.Succeeded && tools.LastContext is { EffectId: "tool-effect", SessionId: "conversation", JobId: "request", EnforceProfileScope: true },
                "host identities and strict accepted-scope flag reach the dispatcher");
            WorkflowSchema.Validate(result.Data!.Value, tool.Descriptor.OutputSchema);
            Check(tool.GetFailureBudgetKey(Tool("process.run")) == WorkerWorkflowEffects.LaunchFailureBudget && tool.GetFailureBudgetKey(Tool()) is null,
                "process execution is placed in the bounded launch-failure category");
            WorkflowCommandContext Process(string executableId, string verb) => new("process-effect", submission,
                Element(new WorkerWorkflowEffects.ToolArguments("process.run", Element(new { executableId, arguments = new[] { verb }, workingDirectory = root }))));
            Check(tool.GetFailureBudgetKey(Process("dotnet", "build")) is null && tool.GetFailureBudgetKey(Process("dotnet", "test")) is null,
                "build and test repair do not consume the executable launch-failure budget");
            Check(tool.GetFailureBudgetKey(Process("dotnet", "run")) == WorkerWorkflowEffects.LaunchFailureBudget &&
                tool.GetFailureBudgetKey(Process("application.exe", "")) == WorkerWorkflowEffects.LaunchFailureBudget,
                "dotnet run and generated executable launches remain bounded");
            profile.AllowedRoots = [Path.Combine(root, "narrow")]; SaveProfile();
            await tool.ExecuteAsync(Tool(), default);
            Check(tools.LastContext!.Profile.AllowedRoots.SequenceEqual(profile.AllowedRoots), "execution roots narrow to the current and accepted intersection");
            profile.Capabilities.Remove("files"); SaveProfile(); before = tools.Executions;
            Check((await tool.ExecuteAsync(Tool(), default)).State == WorkflowEffectState.Failed && tools.Executions == before,
                "revoked capability prevents dispatch");
            profile.Capabilities.Add("files"); SaveProfile();
            tools.OnList = () => { profile.AllowedTools.Remove("file.read"); SaveProfile(); };
            Check((await tool.ExecuteAsync(Tool(), default)).State == WorkflowEffectState.Failed && tools.Executions == before,
                "grant revocation while discovery awaits is checked before dispatch");
            tools.OnList = null; profile.AllowedTools.Add("file.read"); SaveProfile();
            tools.ChangedDescriptor = true;
            Check((await tool.ExecuteAsync(Tool(), default)).State == WorkflowEffectState.Failed && tools.Executions == before,
                "changed semantic schemas cannot silently replace the admitted tool");
            tools.ChangedDescriptor = false;

            tools.FullAccessActive = true;
            await tool.ExecuteAsync(Tool(), default);
            Check(!tools.LastContext!.FullAccess, "enabling application full access later cannot promote accepted work");
            submission = await Capture();
            await tool.ExecuteAsync(Tool(), default);
            Check(tools.LastContext!.FullAccess, "accepted full access remains available while still active");
            tools.FullAccessActive = false;
            await tool.ExecuteAsync(Tool(), default);
            Check(!tools.LastContext!.FullAccess, "turning off full access takes effect before the next tool");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); before = tools.Executions;
                Check((await tool.ExecuteAsync(Tool(), cancelled.Token)).State == WorkflowEffectState.Cancelled && tools.Executions == before,
                    "cancellation before dispatch avoids the tool side effect");
            }
            tools.OnList = () => tools.FullAccessActive = !tools.FullAccessActive;
            var admissionRejected = false;
            try { await Capture(); } catch (InvalidOperationException) { admissionRejected = true; }
            Check(admissionRejected, "access transition during admission requires a new snapshot");
            tools.OnList = null; tools.FullAccessActive = false;

            foreach (var (outcome, expected) in new[]
            {
                (ToolExecutionOutcome.Failed, WorkflowEffectState.Failed),
                (ToolExecutionOutcome.Rejected, WorkflowEffectState.Failed),
                (ToolExecutionOutcome.Unknown, WorkflowEffectState.Unknown),
                (ToolExecutionOutcome.Cancelled, WorkflowEffectState.Cancelled)
            })
            {
                tools.Result = new(false, "actual provider diagnostic") { Evidence = new(outcome, "fixture", "tool-effect", "job-1") };
                result = await tool.ExecuteAsync(Tool(), default);
                Check(result.State == expected && result.ProviderReceiptId == "job-1" && result.Data!.Value.GetProperty("content").GetString() == "actual provider diagnostic",
                    "tool terminal evidence and diagnostic retained for " + outcome);
            }
            tools.ThrowDuringExecution = true;
            Check((await tool.ExecuteAsync(Tool(), default)).State == WorkflowEffectState.Unknown, "tool exception after dispatch remains unknown");
            tools.ThrowDuringExecution = false;
            tools.Result = new(true, new string('x', WorkflowContract.MaximumStateBytes * 2)) { Evidence = new(ToolExecutionOutcome.Succeeded, "fixture", "tool-effect", "job-2") };
            result = await tool.ExecuteAsync(Tool(), default);
            Check(result.State == WorkflowEffectState.Succeeded && result.Data!.Value.GetProperty("truncated").GetBoolean() &&
                JsonSerializer.SerializeToUtf8Bytes(result.Data.Value).Length <= WorkflowContract.MaximumStateBytes && result.ProviderReceiptId == "job-2",
                "bounded tool text retains successful receipt without changing the terminal outcome");

            var failedBuildOutput = JsonSerializer.Serialize(new { exitCode = 1, stdout =
                "C:\\fixture\\Panel.cs(1,1): error CS1061: No Dispose member [C:\\fixture\\Plugin.csproj]\n" +
                "C:\\fixture\\Panel.cs(2,1): error CS0747: Invalid initializer member [C:\\fixture\\Plugin.csproj]\n" +
                new string('x', WorkflowContract.MaximumStateBytes * 2) });
            tools.Result = new(false, failedBuildOutput)
            {
                Evidence = new(ToolExecutionOutcome.Failed, "fixture", "tool-effect", "job-build", ExitCode: 1)
            };
            result = await tool.ExecuteAsync(Tool("process.run"), default);
            WorkflowSchema.Validate(result.Data!.Value, tool.Descriptor.OutputSchema);
            Check(result.State == WorkflowEffectState.Failed && result.Data.Value.GetProperty("truncated").GetBoolean() &&
                result.Error?.Contains("CS1061", StringComparison.Ordinal) == true && result.Error.Contains("CS0747", StringComparison.Ordinal) &&
                result.Error.Split('\n').Length == 2 && result.Error.Length < 4096 && result.ProviderReceiptId == "job-build",
                "large provider JSON preserves bounded distinct compiler corrections before its durable content is shortened");

            var evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, "fixture", "tool-effect", "job-pending");
            var context = Tool();
            var previous = new WorkflowEffect(context.EffectId, WorkerWorkflowEffects.ToolCommand, 1, context.Arguments,
                WorkflowReplayPolicy.Never, WorkflowEffectState.Unknown, Element(new WorkerWorkflowEffects.ToolData("file.read", "pending", false, evidence)));
            tools.Result = new(true, "finished") { Evidence = evidence with { Outcome = ToolExecutionOutcome.Succeeded } };
            before = tools.Executions;
            result = await tool.ReconcileAsync(context with { PreviousEffect = previous }, default);
            Check(result.State == WorkflowEffectState.Succeeded && tools.Executions == before && tools.Reconciliations == 1 && tools.PreviousEvidence == evidence,
                "matching retained evidence is reconciled without redispatch");
            Check((await tool.ReconcileAsync(context with { PreviousEffect = previous with { Id = "other-effect" } }, default)).State == WorkflowEffectState.Unknown && tools.Reconciliations == 1,
                "unrelated effect receipt cannot be used as reconciliation evidence");
            Check((await generate.ReconcileAsync(Generation(), default)).State == WorkflowEffectState.Unknown,
                "unrecoverable inference is not replayed");

            var model = store.Get<ModelRecord>("model", "fixture")!;
            model.Path = Path.Combine(root, "replacement.gguf"); store.Put("model", model.Id, model); before = models.Preparations;
            Check((await generate.ExecuteAsync(Generation(), default)).State == WorkflowEffectState.Failed && models.Preparations == before,
                "changed model binding fails before preparation");
            var settings = configuration.Get(); settings.ExternalCommandsEnabled = !settings.ExternalCommandsEnabled; configuration.Save(settings);
            before = tools.Executions;
            Check((await tool.ExecuteAsync(Tool(), default)).State == WorkflowEffectState.Failed && tools.Executions == before,
                "routing changes cannot silently redirect accepted execution");
            Check(models.Disposals == models.Preparations, "every prepared generation was disposed across terminal outcomes");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"Workflow effect bridge deterministic checks passed ({checks}; fake model and dispatcher, no runtime or PAO execution).");
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);

    private sealed class FakeModels : IModelRuntimeAdapter
    {
        public bool SupportsReasoning;
        public ModelAdapterDescriptor Descriptor => new("fixture", "1.0.0", ["text"], SupportsReasoning);
        public int Preparations, Disposals;
        public ModelGenerationRequest? LastRequest;
        public ModelGenerationResult Result = new(ModelGenerationFinish.Stop, "{}", 7, 42);
        public bool ThrowDuringGeneration;
        public bool PublishVisibleText = true;
        public Exception? PreparationFailure;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PreparationFailure is not null) return Task.FromException<IPreparedModelGeneration>(PreparationFailure);
            Preparations++; LastRequest = request;
            return Task.FromResult<IPreparedModelGeneration>(new Prepared(this, request));
        }
        private sealed class Prepared(FakeModels owner, ModelGenerationRequest request) : IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata { get; } = new("fixture-instance", "fixture", "1.0.0", request.ModelId, 42,
                request.ContextLimit, request.ResponseLimit, "fixture exact count");
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken cancellationToken)
            {
                if (owner.ThrowDuringGeneration) throw new OperationCanceledException();
                if (owner.PublishVisibleText) onText?.Invoke(owner.Result.Text);
                return Task.FromResult(owner.Result);
            }
            public ValueTask DisposeAsync() { owner.Disposals++; return ValueTask.CompletedTask; }
        }
    }

    private sealed class FakeTools : IToolDispatcher
    {
        public bool FullAccessActive { get; set; }
        public bool ChangedDescriptor, ThrowDuringExecution;
        public Action? OnList;
        public int Executions, Reconciliations;
        public ToolExecutionContext? LastContext;
        public ToolExecutionEvidence? PreviousEvidence;
        public ToolResult Result = new(true, "done");
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken cancellationToken)
        {
            OnList?.Invoke();
            return Task.FromResult<IReadOnlyList<ToolDescriptor>>([
                new("file.read", ChangedDescriptor ? "changed" : "read", Element(new { type = "object" }), "files"),
                new("file.write", "write", Element(new { type = "object" }), "files"),
                new("process.run", "run", Element(new { type = "object" }), "process")]);
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            Executions++; LastContext = context;
            if (ThrowDuringExecution) throw new OperationCanceledException();
            return Task.FromResult(Result);
        }
        public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context, ToolExecutionEvidence? previousEvidence, CancellationToken cancellationToken)
        {
            Reconciliations++; LastContext = context; PreviousEvidence = previousEvidence;
            return Task.FromResult(Result);
        }
    }
}
