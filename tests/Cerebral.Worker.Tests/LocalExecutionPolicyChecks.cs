using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic contract and preparation checks. No model, pipe, SDK or GPU is used.</summary>
internal static class LocalExecutionPolicyChecks
{
    internal static async Task<string[]> RunAsync()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        void Refuse(Action action, string name)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException)
            { checks.Add(name); return; }
            throw new InvalidOperationException(name);
        }

        var binding = new ModelRuntimeSupportBinding("worker-fixture", "worker.local.llama.cpp",
            "fixture.native.text", "1.0.0", "model-fixture", "fixture.binding/1",
            new string('a', 64), "instance-fixture");
        var policy = new ModelExecutionPolicy(1, "fixture.native.text", 1, binding,
            "generation-fixture", "text", false, 512, 300000, 1,
            300000 - 1 - AgentTokenLimits.ContextSafetyTokens, "fixture-native-software/1");
        var request = new ModelGenerationRequest("model-fixture",
            [new("user", [new TextContentPart("small measured prompt")])], 262144, 131073);
        Refuse(request.Validate, "legacy_request_retains_131072_visible_ceiling");
        request.Validate(policy);
        Check(request.ResponseLimit == 131073 &&
              LlamaPreparedGeneration.NativeMaximumVisibleTokens == 1_048_447,
            "policy_request_exceeds_legacy_ceiling_without_claiming_actual_fit");
        Refuse(() => (request with { ModelId = "different" }).Validate(policy),
            "selected_policy_rejects_different_request_model");
        Refuse(() => (request with { ReasoningTokens = 128 }).Validate(policy),
            "selected_text_policy_rejects_reasoning");
        Refuse(() => (request with { ResponseSharePercent = 50 }).Validate(policy),
            "selected_text_policy_rejects_implicit_share");

        var choice = new WorkflowLocalGenerationChoice(1, "local.native.text", 262144, 131073);
        choice.Validate();
        Refuse(() => (choice with { Route = "remote" }).Validate(), "choice_route_closed");
        var declared = new WorkflowLocalGenerationConsumer(1, "cerebral.host:generate", 2, "local.native.text");
        declared.Validate();
        Refuse(() => (declared with { CommandVersion = 1 }).Validate(), "consumer_command_version_closed");

        var profile = new AgentProfile { Id = "local-policy-fixture", WorkerId = "local", ModelWorkerId = "local" };
        var original = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, profile,
            "", "", null, [], [], [], [], false, "fixture-routing", [], []);
        var oldWire = CerebralJson.Element(original);
        Check(!oldWire.TryGetProperty("localGenerationConsumer", out _) &&
              WorkerExecutionSnapshot.Read(oldWire).LocalGenerationConsumer is null,
            "old_edition6_omission_is_unsupported_without_migration");
        var oldRoundTrip = WorkerExecutionSnapshot.Bounded(WorkerExecutionSnapshot.Read(oldWire));
        Check(!oldRoundTrip.TryGetProperty("localGenerationConsumer", out _),
            "old_edition6_roundtrip_retains_omission");

        var opted = WorkerExecutionSnapshot.Bounded(original with { LocalGenerationConsumer = declared });
        Check(WorkerExecutionSnapshot.Read(opted).LocalGenerationConsumer == declared,
            "edition6_exact_consumer_roundtrip");
        JsonObject Node() => JsonNode.Parse(opted.GetRawText())!.AsObject();
        JsonElement Element(JsonObject node) => JsonSerializer.SerializeToElement(node, CerebralJson.Options);
        var withNull = Node(); withNull["localGenerationConsumer"] = null;
        Check(WorkerExecutionSnapshot.Read(Element(withNull)).LocalGenerationConsumer is null &&
              !WorkerExecutionSnapshot.Bounded(WorkerExecutionSnapshot.Read(Element(withNull)))
                  .TryGetProperty("localGenerationConsumer", out _),
            "explicit_null_is_unsupported_and_omitted_on_write");
        var extra = Node(); extra["localGenerationConsumer"]!["unexpected"] = true;
        Refuse(() => WorkerExecutionSnapshot.Read(Element(extra)), "edition6_consumer_rejects_extra_field");
        var badVersion = Node(); badVersion["localGenerationConsumer"]!["commandVersion"] = 1;
        Refuse(() => WorkerExecutionSnapshot.Read(Element(badVersion)), "edition6_consumer_rejects_wrong_command");
        foreach (var version in new[] { 2, 3, 4, 5 })
        {
            var legacy = Node(); legacy["resolvedSettingsVersion"] = version;
            legacy.Remove("approvedPluginSettings"); legacy.Remove("toolReceiptConsumer"); legacy.Remove("selectedToolOwners");
            try { _ = WorkerExecutionSnapshot.Read(Element(legacy)); }
            catch (InvalidDataException error) when (error.Message.Contains("local generation consumer binding", StringComparison.Ordinal))
            { checks.Add($"legacy_{version}_rejects_local_consumer_field"); continue; }
            throw new InvalidOperationException($"legacy_{version}_rejects_local_consumer_field");
        }

        var narrowed = new ShrinkingAdapter(policy);
        var prepared = await WorkerPreparedBudgets.ForPreparation(narrowed)
            .PrepareAsync(request with { ContextLimit = 300000 }, default);
        Check(narrowed.Requests.Count == 2 && narrowed.Requests[1].ContextLimit == 262144 &&
              narrowed.Requests[1].ResponseLimit == 131073 && prepared.Metadata.ResponseBudget == 131073,
            "measured_shrink_reprepares_without_legacy_output_recap");
        LocalModelExecutionPolicy.Require(prepared, policy);
        LocalModelExecutionPolicy.ValidateMetadata(policy, narrowed.Requests[1], prepared.Metadata, false);
        await prepared.DisposeAsync();
        Check(narrowed.Disposals == 1, "selected_preparation_disposed_once");

        await CheckPendingResponseBalanceAsync(Check, declared, choice, policy);
        return checks.ToArray();
    }

    private static async Task CheckPendingResponseBalanceAsync(Action<bool, string> check,
        WorkflowLocalGenerationConsumer declared, WorkflowLocalGenerationChoice choice,
        ModelExecutionPolicy policy)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-local-v2-balance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new StateStore(root);
            var configuration = new Configuration(store);
            configuration.Get();
            var profile = new AgentProfile { Id = "local-v2-balance", WorkerId = "local", ModelWorkerId = "local",
                ModelId = policy.Binding.ModelId, FollowsModelBudgets = false, ContextTokens = choice.ContextTokens,
                ResponseTokens = choice.VisibleOutputTokens, ReasoningMode = "off", AllowedTools = [], Capabilities = [] };
            store.Put("agent", profile.Id, profile);
            store.Put("model", profile.ModelId, new ModelRecord { Id = profile.ModelId,
                Path = Path.Combine(root, "fixture.gguf"), MaximumContextTokens = choice.ContextTokens,
                ParallelRequests = 1, GpuLayers = 0 });
            var model = new WorkerExecutionSnapshot.ModelBinding(policy.Binding.ModelId,
                Path.Combine(root, "fixture.gguf"), null, [], choice.ContextTokens, 1, 0);
            var definitionPackage = new ServicePluginPackageBinding("fixture.local", "1.0.0", true, []);
            var hostPackage = new ServicePluginPackageBinding(WorkerWorkflowEffects.ModuleId,
                WorkerWorkflowEffects.ModuleVersion, true, []);
            var packages = new WorkflowPackageBindings([definitionPackage, hostPackage]);
            var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, profile,
                "", "", model, [], [], [], [], false,
                WorkerExecutionSnapshot.RoutingFingerprint(configuration.Get()),
                packages.Capture("fixture.local:definition"), new SkillCatalog(store).Resolve(profile, null))
                { LocalGenerationConsumer = declared };
            var submission = new WorkflowSubmission("local-v2-balance-rid", "local-v2-balance-conversation",
                profile.Id, "fixture.local:definition", "1.0.0", "fixture request", null,
                WorkflowContract.EmptyObject, WorkerExecutionSnapshot.Bounded(snapshot)) { LocalGeneration = choice };
            var tools = new ContextCompactionTests.NoTools();
            var adapter = new ShrinkingAdapter(policy);
            var selectionCalls = 0;
            WorkerWorkflows? host = null;
            var effects = new WorkerWorkflowEffects(store, configuration, adapter, tools,
                modelAdapterForSubmission: _ => { selectionCalls++; return adapter; },
                workflowHost: () => host!);
            host = new WorkerWorkflows(store, configuration, new EventHub(),
                [new LocalFixtureModule(declared), effects], [definitionPackage, hostPackage], tools);
            var buildRequest = typeof(WorkerWorkflowEffects).GetMethod("BuildGenerationRequest",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var requested = new WorkerWorkflowEffects.GenerateArguments(
                [new("user", [new TextContentPart("The next generation hit its response limit.")])],
                choice.VisibleOutputTokens);
            ModelGenerationRequest Assemble(WorkflowSubmission accepted, ModelExecutionPolicy? selected) =>
                (ModelGenerationRequest)buildRequest.Invoke(effects,
                    [accepted, selected is null ? requested with { ResponseLimit = 64 } : requested,
                     adapter, null, selected])!;
            var fixedRequest = Assemble(submission, policy);
            var fixedSystem = ((TextContentPart)fixedRequest.Messages[0].Content[0]).Text;
            check(fixedRequest.ResponseLimit == choice.VisibleOutputTokens &&
                  fixedSystem.Contains("smaller complete action", StringComparison.Ordinal) &&
                  !fixedSystem.Contains("conversation.response-balance", StringComparison.Ordinal),
                "v2_request_assembly_uses_fixed_choice_guidance_without_mutable_balance_skill");
            var ordinaryRequest = Assemble(submission with { LocalGeneration = null }, null);
            var ordinarySystem = ((TextContentPart)ordinaryRequest.Messages[0].Content[0]).Text;
            check(ordinarySystem.Contains("conversation.response-balance", StringComparison.Ordinal),
                "v1_request_assembly_preserves_owner_enabled_balance_guidance");
            host.RequireLocalGenerationAccepted(submission);
            var automatic = submission with { SettingsSnapshot = WorkerExecutionSnapshot.Bounded(
                snapshot with { Model = model with { MaximumContextTokens = 0 } }) };
            host.RequireLocalGenerationAccepted(automatic);
            check(true, "automatic_model_maximum_is_unreported_not_zero_capacity");
            var restricted = submission with { SettingsSnapshot = WorkerExecutionSnapshot.Bounded(
                snapshot with { Model = model with { MaximumContextTokens = choice.ContextTokens - 1 } }) };
            var restrictedRefused = false;
            try { host.RequireLocalGenerationAccepted(restricted); }
            catch (UnauthorizedAccessException) { restrictedRefused = true; }
            check(restrictedRefused, "positive_configured_model_maximum_still_bounds_choice");
            var staged = await new ConversationToolExtension(store, new EventHub()).ExecuteAsync(
                new(ConversationToolExtension.ResponseBalanceToolName,
                    CerebralJson.Element(new { responseSharePercent = 26 })),
                new(profile, submission.ConversationId, submission.RequestId, null,
                    FullAccess: false, EffectId: "local-v2-balance-stage") { EnforceProfileScope = true }, default);
            var pending = ConversationToolExtension.ReadWorkflowResponseBalance(store, submission);
            check(staged.Success && pending is not null, "actual_store_stages_pending_balance_for_opted_run");
            var command = effects.Commands.Single(item => item.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand &&
                item.Descriptor.Version == 2);
            var result = await command.ExecuteAsync(new("local-v2-balance-effect", submission,
                CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([]))), default);
            check(result.State == WorkflowEffectState.Failed &&
                  result.Error?.Contains("pending response-balance preference conflicts", StringComparison.Ordinal) == true &&
                  ConversationToolExtension.ReadWorkflowResponseBalance(store, submission) == pending,
                "v2_handler_refuses_pending_balance_and_preserves_store_record");
            check(selectionCalls == 0 && adapter.Requests.Count == 0 && adapter.Generations == 0,
                "v2_pending_balance_has_zero_selection_preparation_and_generation_callbacks");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class LocalFixtureModule(WorkflowLocalGenerationConsumer declared) : IWorkflowModule
    {
        public string Id => "fixture.local";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new Definition(declared)];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];

        private sealed class Definition(WorkflowLocalGenerationConsumer declared) : IWorkflowDefinition
        {
            public WorkflowDefinitionDescriptor Descriptor { get; } = new("fixture.local:definition", "1.0.0",
                "Local v2 fixture", CerebralJson.Element(new { type = "object", additionalProperties = false }), [])
                { LocalGenerationConsumer = declared };
            public WorkflowStep Advance(WorkflowRun snapshot) =>
                new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject));
        }
    }

    private sealed class ShrinkingAdapter(ModelExecutionPolicy policy) : IModelRuntimeAdapter, IModelExecutionPolicyFacet
    {
        internal readonly List<ModelGenerationRequest> Requests = [];
        internal int Disposals, Generations;
        public ModelExecutionPolicy ExecutionPolicy => policy;
        public ModelAdapterDescriptor Descriptor { get; } = new("fixture.native.text", "1.0.0", ["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            Requests.Add(request);
            var metadata = new PreparedModelMetadata(policy.Binding.InstanceId!, policy.Binding.AdapterId,
                policy.Binding.AdapterVersion, request.ModelId, 10, 262144,
                Requests.Count == 1 ? 0 : request.ResponseLimit, "fixture-token-count")
                { DesiredResponseBudget = request.ResponseLimit, SafetyReserveTokens = AgentTokenLimits.ContextSafetyTokens };
            if (Requests.Count == 1) throw new ModelContextLimitException(metadata);
            return Task.FromResult<IPreparedModelGeneration>(new Prepared(this, policy, metadata));
        }

        private sealed class Prepared(ShrinkingAdapter owner, ModelExecutionPolicy policy,
            PreparedModelMetadata metadata) : IPreparedModelGeneration, IModelExecutionPolicyFacet
        {
            public ModelExecutionPolicy ExecutionPolicy => policy;
            public PreparedModelMetadata Metadata => metadata;
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            { owner.Generations++; return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, "small output", 2, 10)); }
            public ValueTask DisposeAsync() { owner.Disposals++; return ValueTask.CompletedTask; }
        }
    }
}
