using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Conversation;
using Cerebral.Core;
using Cerebral.Engine;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real isolated SQLite and descriptor-only catalog fixtures. No provider, model, native
/// package, installed Source, process build or control action is used.</summary>
public static class PluginQaSourceCatalogTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-qa-source-catalog-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("QA source catalog: " + message);
            checks++;
        }
        try
        {
            var store = new StateStore(Path.Combine(root, "data"));
            var configuration = new Configuration(store);
            var profile = new AgentProfile
            {
                Id = "qa-profile", Name = "QA fixture", Role = "lead", WorkerId = "local", ModelId = "qa-fixture-model",
                AutomaticCompaction = false, FollowsModelBudgets = false, ContextTokens = 8192, ResponseTokens = 512,
                AllowedRoots = [root], AllowedTools = ["*"], Capabilities = ["filesystem.read", "filesystem.write", "process.execute"]
            };
            store.Put("agent", profile.Id, profile);
            store.Put("model", profile.ModelId, new ModelRecord
            {
                Id = profile.ModelId, Path = Path.Combine(root, "fixture.gguf"), AutomaticContextSizing = false,
                ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192
            });
            var dispatcher = new Tools();
            var snapshot = await WorkerExecutionSnapshot.CaptureAsync(store, profile, configuration, dispatcher, [], default);
            var journal = new EventJournal(Path.Combine(store.DirectoryPath, "engine.db"));
            var workflows = new WorkflowStore(journal);
            var bridge = new WorkerWorkflowEffects(store, configuration, new NoModels(), dispatcher);
            _ = new WorkflowRegistry([bridge]);
            var catalog = bridge.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.CatalogCommand && command.Descriptor.Version == 1);
            WorkflowRun Submit(string id, EventActor? caller = null, string? conversation = null) =>
                workflows.Submit(new(id, conversation ?? "qa-conversation", profile.Id,
                    ConversationAgentHarness.Id, ConversationAgentHarness.Version,
                    "Unchanged original feature request", "Untrusted assignment prose cannot supply a source coordinate.",
                    WorkflowContract.EmptyObject, snapshot) { Caller = caller });
            WorkflowRun Finish(WorkflowRun run)
            {
                var claimed = workflows.TryClaim(run.Id) ?? throw new InvalidOperationException("Catalog fixture run was not claimable.");
                return workflows.ApplyStep(run.Id, claimed.Revision, new(WorkflowContract.EmptyObject,
                    new CompleteWorkflow(CerebralJson.Element(new { summary = "Fixture lifecycle complete." }))));
            }
            // Automatic QA follows a terminal original run; keep the real single-active-conversation invariant.
            var original = Finish(Submit("qa-original"));
            var presentationId = new string('a', 32);
            var runId = "plugin-tests-" + presentationId;
            var qa = Submit(runId, new(EventActorKind.Service, "cerebral.plugin-tests"));
            var sourceRoot = Path.Combine(root, "GeneratedPlugin");
            Directory.CreateDirectory(sourceRoot);
            var presentation = new InterfaceRequest
            {
                Id = presentationId, Source = "workflow", Kind = "plugin", Status = "completed",
                JobId = original.Id, SessionId = original.Submission.ConversationId, ProfileId = profile.Id,
                Path = Path.Combine(sourceRoot, "desktop", "bin", "Release", "desktop-plugin.json"),
                GeneratedSourceRoot = sourceRoot,
                Plugin = new("fixture.plugin", "1.0.0", new string('e', 32), Path.Combine(root, "installed", "desktop-plugin.json"),
                    [new("sidebar", "fixture.plugin:sidebar")], ControlTestsAllowed: true),
                PluginTests = new(runId, original.Id, 1, "admitted", runId)
            };
            void Save(InterfaceRequest value) => store.Put("interface", presentationId, value);
            InterfaceRequest Copy() => CerebralJson.Read<InterfaceRequest>(CerebralJson.Element(presentation));
            Save(presentation);
            async Task<WorkflowCommandResult> Read(WorkflowRun run, object? arguments = null)
            {
                var result = await catalog.ExecuteAsync(new("catalog-" + Guid.NewGuid().ToString("N"), run.Submission,
                    CerebralJson.Element(arguments ?? new { })) { RunId = run.Id }, default);
                if (result.State == WorkflowEffectState.Succeeded)
                    WorkflowSchema.Validate(result.Data!.Value, catalog.Descriptor.OutputSchema);
                return result;
            }
            var actual = await Read(qa);
            Check(actual.State == WorkflowEffectState.Succeeded &&
                actual.Data!.Value.GetProperty("pluginSource").GetProperty("schemaVersion").GetInt32() == 1 &&
                actual.Data.Value.GetProperty("pluginSource").GetProperty("presentationId").GetString() == presentationId &&
                actual.Data.Value.GetProperty("pluginSource").GetProperty("root").GetString() == sourceRoot,
                "catalog reads the exact owner-reviewed source coordinate from the service-bound SQLite receipt");
            Check(actual.Data!.Value.GetProperty("pluginSource").EnumerateObject().Count() == 3 &&
                !actual.Data.Value.GetRawText().Contains("Unchanged original feature request", StringComparison.Ordinal) &&
                !actual.Data.Value.GetRawText().Contains("successfulBuild", StringComparison.Ordinal) && dispatcher.Executions == 0,
                "metadata contains only version/presentation/root without source, prior binding, credits or execution");
            var exact = await Read(qa, new { names = new[] { "plugin.projects" } });
            Check(exact.State == WorkflowEffectState.Succeeded &&
                JsonElement.DeepEquals(actual.Data!.Value.GetProperty("pluginSource"), exact.Data!.Value.GetProperty("pluginSource")) &&
                exact.Data.Value.GetProperty("tools")[0].TryGetProperty("inputSchema", out _),
                "exact-schema lookup retains the same bounded private source reference");

            foreach (var mutate in new Action<InterfaceRequest>[]
            {
                value => value.SessionId = "foreign-conversation",
                value => value.ProfileId = "foreign-profile",
                value => value.PluginTests = value.PluginTests! with { RequestId = "plugin-tests-" + new string('b', 32) },
                value => value.PluginTests = value.PluginTests! with { RunId = "foreign-run" },
                value => value.PluginTests = value.PluginTests! with { OriginRunId = "missing-origin" },
                value => value.Plugin = value.Plugin! with { ControlTestsAllowed = false },
                value => value.Status = "pending",
                value => value.GeneratedSourceRoot = Path.GetFullPath(Path.Combine(root, "..", "outside-qa-root")),
                value => value.GeneratedSourceRoot = "relative-root",
                value => value.GeneratedSourceRoot = new string('x', 2049)
            })
            {
                var changed = Copy();
                mutate(changed); Save(changed);
                var rejected = await Read(qa);
                Check(rejected.State == WorkflowEffectState.Failed && dispatcher.Executions == 0,
                    "foreign, unconsented, malformed or out-of-scope source-coordinate evidence fails closed");
            }
            Save(presentation);
            var noRoot = Copy(); noRoot.GeneratedSourceRoot = null; Save(noRoot);
            var imported = await Read(qa);
            Check(imported.State == WorkflowEffectState.Succeeded &&
                !imported.Data!.Value.TryGetProperty("pluginSource", out _),
                "historical/imported presentations without an admitted generated root never invent a source coordinate");
            Save(presentation);
            qa = Finish(qa);
            var user = Submit("plugin-tests-" + new string('c', 32), new(EventActorKind.User, "owner"));
            var userCatalog = await Read(user);
            Check(userCatalog.State == WorkflowEffectState.Succeeded &&
                !userCatalog.Data!.Value.TryGetProperty("pluginSource", out _),
                "a caller-selected QA-looking ID or prose does not create host source metadata");
            user = Finish(user);
            var missing = Submit("plugin-tests-" + new string('d', 32), new(EventActorKind.Service, "cerebral.plugin-tests"));
            Check((await Read(missing)).State == WorkflowEffectState.Failed,
                "service labels still require the exact retained owner-review receipt");

            var narrowed = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
            narrowed.AllowedRoots = [];
            store.Put("agent", narrowed.Id, narrowed);
            Check((await Read(qa)).State == WorkflowEffectState.Failed && dispatcher.Executions == 0,
                "currently revoked root authority prevents even the private coordinate handoff");
            store.Put("agent", profile.Id, profile);
            narrowed = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
            narrowed.AllowedTools = ["file.read"];
            store.Put("agent", narrowed.Id, narrowed);
            var revokedInventory = await Read(qa);
            Check(revokedInventory.State == WorkflowEffectState.Succeeded &&
                revokedInventory.Data!.Value.GetProperty("tools").EnumerateArray().All(tool =>
                    tool.GetProperty("name").GetString() != "plugin.projects") &&
                (await Read(qa, new { names = new[] { "plugin.projects" } })).State == WorkflowEffectState.Failed,
                "the coordinate grants no inventory capability and exact revoked schemas remain unavailable");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(root).StartsWith("cerebral-qa-source-catalog-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact owned QA catalog fixture root.");
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"QA source catalog: {checks} SQLite/descriptor-only checks passed; no model or provider execution.");
    }

    private sealed class Tools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public int Executions;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var schema = CerebralJson.Element(new { type = "object", additionalProperties = false });
            return Task.FromResult<IReadOnlyList<ToolDescriptor>>([
                new("plugin.projects", "Current exact source/build inventory", schema, "filesystem.read"),
                new("file.read", "Read scoped source", schema, "filesystem.read"),
                new("file.write", "Write scoped source", schema, "filesystem.write")
            ]);
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            Executions++;
            throw new InvalidOperationException("Catalog fixture must never execute a tool.");
        }
    }

    private sealed class NoModels : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("qa-catalog-fixture", "1.0.0", ["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) =>
            throw new InvalidOperationException("Catalog fixture must never prepare inference.");
    }
}
