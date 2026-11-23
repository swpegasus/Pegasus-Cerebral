using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static partial class RemoteWorkerManagementTests
{
    public static async Task RunRetainedAdmissionsAsync()
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var waiting = await h.AddWaitingWorkflowAsync("retained-question");
            var unknown = await h.AddUnknownWorkflowAsync("retained-unknown");
            var beforeWaiting = CerebralJson.Element(waiting).GetRawText();
            var beforeUnknown = CerebralJson.Element(unknown).GetRawText();
            var activeDenied = await h.Call("models.default.switch", new { modelId="remote-model", modelWorkerId="gb10", retainAcceptedBindings=true });
            Check(!activeDenied.Success && !h.Remote.Calls.Contains("models.load-tracked"), "An active engine admitted a retained-binding route change.");
            h.Engine!.Pause(h.Engine.Status().Epoch);
            var switched = await h.Call("models.default.switch", new { modelId="remote-model", modelWorkerId="gb10", retainAcceptedBindings=true });
            Check(switched.Success && h.Configuration.Get().DefaultModelWorkerId=="gb10", switched.Error ?? "Future admissions did not move to remote.");
            Check(CerebralJson.Element(h.Workflows!.Store.Get(waiting.Id)).GetRawText()==beforeWaiting, "A retained question changed during default routing.");
            Check(CerebralJson.Element(h.Workflows.Store.Get(unknown.Id)).GetRawText()==beforeUnknown && h.Workflows.Store.Get(unknown.Id).Effect!.State==WorkflowEffectState.Unknown,
                "An uncertain receipt changed or was replayed.");
            var effective = WorkerExecutionSnapshot.EffectiveProfile(h.Store, WorkerExecutionSnapshot.Read(h.Workflows.Store.Get(waiting.Id).Submission.SettingsSnapshot), "lead");
            Check(effective.ModelWorkerId=="local" && effective.ModelId=="old" && h.Engine.Status().Paused, "Accepted work was rebound or the owner's pause was released.");
            Check(h.Store.Get<AgentProfile>("agent","pinned")!.Revision==1, "A pinned agent changed.");
        }
        await using (var h = new Harness(withWorkflows:true, withEngine:true))
        {
            h.Engine!.Pause(h.Engine.Status().Epoch);
            h.AddWaitingWorkflowWithSnapshot("unsupported-binding", CerebralJson.Element(new { resolvedSettingsVersion=int.MaxValue }));
            var rejected = await h.Call("models.default.switch", new { modelId="remote-model", modelWorkerId="gb10", retainAcceptedBindings=true });
            Check(!rejected.Success && !h.Remote.Calls.Contains("models.load-tracked") && h.Configuration.Get().DefaultModelId=="old", "An unsupported accepted binding bypassed route validation.");
        }
        await using (var h = new Harness(withWorkflows:true, withEngine:true))
        {
            h.Engine!.Pause(h.Engine.Status().Epoch);
            h.Remote.BeforeCount = () => h.Engine.Resume(h.Engine.Status().Epoch);
            var rejected = await h.Call("models.default.switch", new { modelId="remote-model", modelWorkerId="gb10", retainAcceptedBindings=true });
            Check(!rejected.Success && h.Configuration.Get().DefaultModelId=="old", "A changed engine pause committed a route change.");
        }
        await using (var h = new Harness(withWorkflows:true, withEngine:true))
        {
            h.Engine!.Pause(h.Engine.Status().Epoch);
            var invalid = await h.Call("models.default.switch", new { modelId="remote-model", modelWorkerId="gb10", retainAcceptedBindings="yes" });
            Check(!invalid.Success && h.FactoryCalls==0, "Malformed retained-binding intent reached a remote host.");
            var mixed = await h.Call("models.default.switch", new { modelId="remote-model", modelWorkerId="gb10", retainAcceptedBindings=true, retainWaitingBindings=true });
            Check(!mixed.Success && h.FactoryCalls==0, "Conflicting retained-binding modes reached a remote host.");
        }
        checks += await RunRetainedGenerationFixturesAsync();
        Console.WriteLine($"Retained future-admission routing: {checks} deterministic engine/SQLite/fake remote and resumed-generation checks passed; no native model or owner data.");
    }

    private static async Task<int> RunRetainedGenerationFixturesAsync()
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var old = h.Store.Get<ModelRecord>("model", "old")!;
            old.ContextTokens = old.RecommendedContextTokens = old.MaximumContextTokens = 8192;
            old.AutomaticContextSizing = false; h.Store.Put("model", old.Id, old);
            var lead = h.Store.Get<AgentProfile>("agent", "lead")!;
            lead.ContextTokens = 8192; lead.ResponseTokens = 512; lead.FollowsModelBudgets = false;
            h.Store.Put("agent", lead.Id, lead);
            var waiting = await h.AddWaitingWorkflowAsync("retained-generation");
            var frozen = CerebralJson.Element(waiting).GetRawText();
            var adapter = new RetainedModels();
            var bridge = new WorkerWorkflowEffects(h.Store, h.Configuration, adapter, new ContextCompactionTests.NoTools());
            var generate = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && x.Descriptor.Version == 1);
            var compact = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.CompactCommand && x.Descriptor.Version == 1);
            Task<WorkflowCommandResult> Generate(WorkflowSubmission? submission = null, IReadOnlyList<string>? tools = null) =>
                generate.ExecuteAsync(new("retained-fixture-generation", submission ?? waiting.Submission,
                    CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([], ToolNames: tools))), default);
            h.Engine!.Pause(h.Engine.Status().Epoch);
            var switched = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10", retainAcceptedBindings = true });
            Check(switched.Success, switched.Error ?? "Retained generation setup failed.");
            Check(h.Store.Get<JsonElement>(RetainedModelBindingAuthorizations.RecordKind, waiting.Id).ValueKind == JsonValueKind.Object,
                "The explicit switch saved no retained route authorization.");
            Check(CerebralJson.Element(h.Workflows!.Store.Get(waiting.Id)).GetRawText() == frozen, "The separate route permission rewrote accepted work.");

            // Exercise a resumed durable question -> prepared/dispatched command -> real host
            // generation handler -> durable receipt, using a deterministic adapter only.
            h.Engine.Resume(h.Engine.Status().Epoch);
            h.Answer(waiting.Id);
            var claimed = h.Workflows.Store.TryClaim(waiting.Id)!;
            var prepared = h.Workflows.Store.ApplyStep(waiting.Id, claimed.Revision,
                new(claimed.State, new InvokeWorkflowCommand(generate.Descriptor.Id, 1,
                    CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([])))), generate.Descriptor);
            var dispatched = h.Workflows.Store.DispatchEffect(waiting.Id, prepared.Revision);
            var generated = await generate.ExecuteAsync(new(dispatched.Effect!.Id, dispatched.Submission, dispatched.Effect.Arguments), default);
            var completed = h.Workflows.Store.CompleteEffect(waiting.Id, dispatched.Effect.Id, generated);
            Check(generated.State == WorkflowEffectState.Succeeded && completed.Effect!.State == WorkflowEffectState.Succeeded &&
                adapter.Generations == 1 && adapter.ModelIds.All(x => x == "old") && adapter.Active == 0,
                generated.Error ?? "Resumed generation did not execute its exact frozen model through a released fake lease.");
            var history = new ContextContribution("retained-history", "fixture", "history", 1, ContextRequirement.Reducible,
                [new("user", [new TextContentPart(new string('h', 20_000))])], "retained-history-effect");
            var compacted = await compact.ExecuteAsync(new("retained-fixture-compaction", waiting.Submission,
                CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([], Context: new(2, 0, [history])))), default);
            Check(compacted.State == WorkflowEffectState.Succeeded && adapter.Generations > 1 && adapter.ModelIds.All(x => x == "old") && adapter.Active == 0,
                compacted.Error ?? "The retained compactor failed to generate its frozen model checkpoint.");

            var preparations = adapter.Preparations;
            var denied = await Generate(waiting.Submission with { RequestId = "another-run" });
            Check(denied.State == WorkflowEffectState.Failed && adapter.Preparations == preparations, "Another run borrowed a retention permission.");
            var changedSnapshot = WorkerExecutionSnapshot.Read(waiting.Submission.SettingsSnapshot) with { RoleInstructions = "Changed fixture guidance." };
            denied = await Generate(waiting.Submission with { SettingsSnapshot = CerebralJson.Element(changedSnapshot) });
            Check(denied.State == WorkflowEffectState.Failed && adapter.Preparations == preparations, "Different accepted settings borrowed a retention permission.");
            denied = await Generate(tools: ["not-admitted"]);
            Check(denied.State == WorkflowEffectState.Failed && adapter.Preparations == preparations, "A retained model permission widened tool grants.");
            var current = h.Store.Get<AgentProfile>("agent", "lead")!;
            current.Capabilities.Clear(); h.Store.Put("agent", current.Id, current);
            Check(!h.Workflows.HasCapabilities(waiting.Submission, ["diagnostics"]) &&
                WorkerExecutionSnapshot.EffectiveProfile(h.Store, WorkerExecutionSnapshot.Read(waiting.Submission.SettingsSnapshot), "lead").Capabilities.Count == 0,
                "Retained model routing restored a revoked capability.");
            var originalSlots = old.ParallelRequests; old.ParallelRequests++; h.Store.Put("model", old.Id, old);
            denied = await Generate();
            Check(denied.State == WorkflowEffectState.Failed && adapter.Preparations == preparations, "A retention permission hid a changed model definition.");
            old.ParallelRequests = originalSlots; h.Store.Put("model", old.Id, old);
            var settings = h.Configuration.Get(); settings.ExternalCommandsEnabled = !settings.ExternalCommandsEnabled; h.Configuration.Save(settings);
            denied = await Generate();
            Check(denied.State == WorkflowEffectState.Failed && adapter.Preparations == preparations, "A retention permission bypassed current provider routing.");
            settings = h.Configuration.Get(); settings.ExternalCommandsEnabled = !settings.ExternalCommandsEnabled; h.Configuration.Save(settings);

            // Simulate an old host's storage-only switch. Runtime must fail until a new,
            // explicit SAME-target owner switch records exact current admissions.
            h.Store.Delete(RetainedModelBindingAuthorizations.RecordKind, waiting.Id);
            denied = await Generate();
            Check(denied.State == WorkflowEffectState.Failed && adapter.Preparations == preparations, "An old mismatch was implicitly adopted.");
            h.Engine.Pause(h.Engine.Status().Epoch);
            switched = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10", retainAcceptedBindings = true });
            Check(switched.Success && h.Configuration.Get().DefaultModelWorkerId == "gb10", switched.Error ?? "A same-target explicit adoption failed.");
            generated = await Generate();
            Check(generated.State == WorkflowEffectState.Succeeded && adapter.ModelIds.All(x => x == "old"), "Explicit same-target adoption changed the accepted model.");

            // Non-route profile saves keep the exception; route saves and deletion revoke it.
            current = h.Store.Get<AgentProfile>("agent", "lead")!; current.Name = "Edited fixture name";
            var saved = await h.Call("agents.save", current);
            Check(saved.Success && (await Generate()).State == WorkflowEffectState.Succeeded, saved.Error ?? "A name edit revoked an unchanged route.");
            current = h.Store.Get<AgentProfile>("agent", "lead")!; current.Role = "helper";
            saved = await h.Call("agents.save", current);
            preparations = adapter.Preparations;
            denied = await Generate();
            Check(saved.Success && denied.State == WorkflowEffectState.Failed && adapter.Preparations == preparations,
                saved.Error ?? "An ordinary role/route edit retained a stale permission.");
            var deleted = await h.Call("agents.delete", new { id = "lead" });
            current.Revision = 0; current.Role = "lead";
            saved = await h.Call("agents.save", current);
            denied = await Generate();
            Check(deleted.Success && saved.Success && denied.State == WorkflowEffectState.Failed && adapter.Preparations == preparations,
                "Delete/recreate revived an old retained binding.");
        }
        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var compactProfile = new AgentProfile { Id = "separate-compactor", Name = "Fixture compactor", Role = "compactor", ModelId = "old", Revision = 1, FollowsDefaultModel = true };
            h.Store.Put("agent", compactProfile.Id, compactProfile);
            var pinned = h.Store.Get<AgentProfile>("agent", "pinned")!;
            pinned.CompactionProfileId = compactProfile.Id; h.Store.Put("agent", pinned.Id, pinned);
            var run = await h.Workflows!.SubmitAsync(new("retained-compactor-only", "chat-retained-compactor-only", pinned.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Continue the fixture request.", null, WorkflowContract.EmptyObject), null, default);
            var claimed = h.Workflows.Store.TryClaim(run.Id)!;
            var waiting = h.Workflows.Store.ApplyStep(run.Id, claimed.Revision,
                new(claimed.State, new AskWorkflowQuestions([new("answer", "Answer")])));
            h.Engine!.Pause(h.Engine.Status().Epoch);
            var switched = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10", retainAcceptedBindings = true });
            Check(switched.Success && h.Store.Get<AgentProfile>("agent", "pinned")!.Revision == 1 &&
                h.Store.Get<JsonElement>(RetainedModelBindingAuthorizations.RecordKind, waiting.Id).ValueKind == JsonValueKind.Object,
                switched.Error ?? "A compactor-only affected run was omitted from exact retention.");
            var adapter = new RetainedModels();
            var bridge = new WorkerWorkflowEffects(h.Store, h.Configuration, adapter, new ContextCompactionTests.NoTools());
            var compact = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.CompactCommand && x.Descriptor.Version == 1);
            var args = CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([], Context: new(2, 0,
                [new("separate-history", "fixture", "history", 1, ContextRequirement.Reducible,
                    [new("user", [new TextContentPart(new string('h', 8_000))])], "separate-effect")])));
            var compacted = await compact.ExecuteAsync(new("separate-compact", waiting.Submission, args), default);
            Check(compacted.State == WorkflowEffectState.Succeeded && adapter.Generations > 0 && adapter.Active == 0,
                compacted.Error ?? "A separately retained compactor did not generate a checkpoint.");
            var generations = adapter.Generations;
            adapter.BeforePrepare = request =>
            {
                if (request.ResponseSchema is { } schema && schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("checkpoint", out _))
                    RetainedModelBindingAuthorizations.RevokeForProfile(h.Store, compactProfile.Id);
            };
            compacted = await compact.ExecuteAsync(new("separate-compact-revoked", waiting.Submission, args), default);
            Check(compacted.State == WorkflowEffectState.Failed && adapter.Generations == generations && adapter.Active == 0,
                "A compactor route revoked during prepare escaped to inference or retained an unused lease.");
        }
        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var waiting = await h.AddWaitingWorkflowAsync("retained-prepare-revocation");
            h.Engine!.Pause(h.Engine.Status().Epoch);
            var switched = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10", retainAcceptedBindings = true });
            Check(switched.Success, switched.Error ?? "Prepare revocation fixture setup failed.");
            var adapter = new RetainedModels { BeforePrepare = _ => RetainedModelBindingAuthorizations.RevokeForProfile(h.Store, "lead") };
            var bridge = new WorkerWorkflowEffects(h.Store, h.Configuration, adapter, new ContextCompactionTests.NoTools());
            var generate = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && x.Descriptor.Version == 1);
            var denied = await generate.ExecuteAsync(new("revoked-during-prepare", waiting.Submission,
                CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([]))), default);
            Check(denied.State == WorkflowEffectState.Failed && adapter.Preparations == 1 && adapter.Generations == 0 && adapter.Active == 0,
                "A primary route revoked during prepare escaped to inference or retained an unused lease.");
        }
        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var waiting = await h.AddWaitingWorkflowAsync("retained-revision-race");
            h.Engine!.Pause(h.Engine.Status().Epoch);
            h.Remote.BeforeCount = () => h.Answer(waiting.Id);
            var rejected = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10", retainAcceptedBindings = true });
            Check(!rejected.Success && h.Configuration.Get().DefaultModelId == "old" &&
                h.Store.Get<JsonElement>(RetainedModelBindingAuthorizations.RecordKind, waiting.Id).ValueKind == JsonValueKind.Undefined,
                "An accepted-run revision race committed a permission or profile switch.");
        }
        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var waiting = await h.AddWaitingWorkflowAsync("retained-atomic-rollback");
            h.Engine!.Pause(h.Engine.Status().Epoch);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(h.Directory, "cerebral.db") }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER reject_retention BEFORE INSERT ON records WHEN NEW.kind='retained-model-binding' BEGIN SELECT RAISE(ABORT,'fixture marker failure'); END;";
                command.ExecuteNonQuery();
            }
            var rejected = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10", retainAcceptedBindings = true });
            Check(!rejected.Success && h.Configuration.Get().DefaultModelId == "old" && h.Store.Get<AgentProfile>("agent", "lead")!.Revision == 1 &&
                h.Store.Get<JsonElement>(RetainedModelBindingAuthorizations.RecordKind, waiting.Id).ValueKind == JsonValueKind.Undefined,
                "A permission write failure did not roll back settings and profiles atomically.");
        }
        return checks;
    }

    private sealed class RetainedModels : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("retained-fixture", "1", ["text"]);
        public int Active, Generations, Preparations;
        public List<string> ModelIds { get; } = [];
        public Action<ModelGenerationRequest>? BeforePrepare;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Active != 0) throw new InvalidOperationException("A retained fake lease was not released before another prepare.");
            var text = string.Join("\n", request.Messages.SelectMany(x => x.Content).OfType<TextContentPart>().Select(x => x.Text));
            var input = text.Length / 4 + 64;
            var metadata = new PreparedModelMetadata("retained-fixture-slot", Descriptor.AdapterId, Descriptor.Version, request.ModelId,
                input, request.ContextLimit, request.ResponseLimit, "deterministic fixture tokenizer")
                { DesiredResponseBudget = request.ResponseLimit, SafetyReserveTokens = 128 };
            Preparations++; ModelIds.Add(request.ModelId);
            if (input + request.ResponseLimit + 128 > request.ContextLimit) throw new ModelContextLimitException(metadata with { ResponseBudget = 0 });
            BeforePrepare?.Invoke(request);
            Active++; return Task.FromResult<IPreparedModelGeneration>(new Prepared(this, metadata));
        }
        private sealed class Prepared(RetainedModels owner, PreparedModelMetadata metadata) : IPreparedModelGeneration
        {
            private bool disposed;
            public PreparedModelMetadata Metadata => metadata;
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested(); owner.Generations++;
                const string output = "{\"checkpoint\":\"Fixture history retained; continue the accepted request.\"}";
                onText?.Invoke(output); return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, output, 24, metadata.InputTokens));
            }
            public ValueTask DisposeAsync() { if (!disposed) { owner.Active--; disposed = true; } return ValueTask.CompletedTask; }
        }
    }
}
