using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Private SQLite instruction/snapshot fixtures with an empty deterministic dispatcher.
/// No installed state, model, service host, provider, or tool execution is exercised.</summary>
public static class AuthenticatedRoleInstructionTests
{
    // Callable by the coordinated test runner; no new Program.cs ownership is required.
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-authenticated-role-" + Guid.NewGuid().ToString("N"));
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Authenticated role instructions: " + name);
            checks++;
        }
        try
        {
            var store = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(store);
            configuration.Get();
            var instructions = new SystemInstructions(store);
            var owner = new EventActor(EventActorKind.User, "owner", "Worker");
            var coordinator = new EventActor(EventActorKind.Service, "cerebral.collaboration", "Main");
            var worker = new EventActor(EventActorKind.Service, "cerebral.coordination", "Coordinator");
            string Selected(string role, EventActor caller, string? parent = null) =>
                instructions.CaptureForAssignment(role, caller, parent)
                    .Single(item => item.Role == (role is "lead" or "compactor" ? role : "helper")).Content;

            Check(Selected("lead", owner) == SystemInstructions.CerebralContent &&
                Selected("lead", coordinator) == SystemInstructions.CoordinatorContent &&
                Selected("lead", worker, "accepted-parent") == SystemInstructions.AgentContent,
                "one profile role receives distinct Main, coordinator and delegated-worker defaults from caller/parent");
            Check(Selected("helper", owner) == SystemInstructions.CerebralContent &&
                Selected("helper", coordinator) == SystemInstructions.CoordinatorContent,
                "the authenticated audience is independent of the profile's lead/helper storage slot");
            Check(Selected("lead", new(EventActorKind.Agent, "owner", "Main")) == SystemInstructions.AgentContent &&
                Selected("lead", new(EventActorKind.Service, "owner", "Main")) == SystemInstructions.AgentContent &&
                Selected("lead", new(EventActorKind.User, "cerebral.collaboration", "Coordinator")) == SystemInstructions.AgentContent &&
                Selected("lead", new(EventActorKind.Service, "Cerebral.Collaboration", "Coordinator")) == SystemInstructions.AgentContent &&
                Selected("lead", new(EventActorKind.Service, "unrelated-service", "Main")) == SystemInstructions.AgentContent,
                "display labels, wrong actor kinds, unknown services and similar IDs cannot select Main/coordinator");
            Check(Selected("lead", owner, "accepted-parent") == SystemInstructions.AgentContent &&
                Selected("lead", coordinator, "accepted-parent") == SystemInstructions.AgentContent &&
                Selected("lead", coordinator, "") == SystemInstructions.AgentContent,
                "a parent prevents top-level Main/coordinator defaults, including malformed empty parent metadata");
            Check(Selected("compactor", coordinator) == SystemInstructions.CompactorContent,
                "a compactor retains its data-only role instead of inheriting coordination guidance");
            Check(instructions.CaptureEffective().Select(item => item.Role)
                    .SequenceEqual(new[] { "system", "lead", "helper", "compactor" }) &&
                instructions.CaptureEffective().All(item => item.Content == SystemInstructions.DefaultForRole(item.Role)) &&
                store.List<JsonElement>(SystemInstructions.RecordKind).Count == 0 &&
                !Directory.Exists(Path.Combine(store.DirectoryPath, "Instructions")),
                "the public four-layer capture stays compatible and assignment reads create no overrides or legacy files");
            Check(new[] { SystemInstructions.CerebralContent, SystemInstructions.CoordinatorContent,
                    SystemInstructions.AgentContent, SystemInstructions.CompactorContent }
                    .All(content => Encoding.UTF8.GetByteCount(content) <= SystemInstructions.MaximumBuiltInRoleUtf8Bytes),
                "all built-in assignment defaults fit the existing role budget");

            var profile = new AgentProfile
            {
                Id = "role-fixture-profile", Name = "Coordinator", Role = "lead", Revision = 3,
                Instructions = "FIXTURE-PROFILE-INSTRUCTIONS", ModelId = "role-fixture-model",
                AutomaticCompaction = false, FollowsModelBudgets = false
            };
            store.Put("agent", profile.Id, profile);
            store.Put("model", profile.ModelId, new ModelRecord
            {
                Id = profile.ModelId, Path = Path.Combine(root, "not-loaded.gguf"),
                AutomaticContextSizing = false, ContextTokens = 4096, MaximumContextTokens = 4096
            });
            var tools = new EmptyTools();
            Task<JsonElement> Capture(EventActor caller, string? parent = null) =>
                WorkerExecutionSnapshot.CaptureWithLocalGenerationAsync(store, profile, configuration, tools, [],
                    default, requireCompactor: false, projectId: null, toolReceiptConsumer: null,
                    localGenerationConsumer: null, authenticatedCaller: caller, parentRunId: parent);
            var mainJson = await Capture(owner);
            var coordinatorJson = await Capture(coordinator);
            var workerJson = await Capture(worker, "accepted-parent");
            var mainSnapshot = WorkerExecutionSnapshot.Read(mainJson);
            var coordinatorSnapshot = WorkerExecutionSnapshot.Read(coordinatorJson);
            var workerSnapshot = WorkerExecutionSnapshot.Read(workerJson);
            Check(mainSnapshot.RoleInstructions == SystemInstructions.CerebralContent &&
                coordinatorSnapshot.RoleInstructions == SystemInstructions.CoordinatorContent &&
                workerSnapshot.RoleInstructions == SystemInstructions.AgentContent,
                "snapshot admission freezes the selected text in the existing RoleInstructions field");
            Check(new[] { mainSnapshot, coordinatorSnapshot, workerSnapshot }.All(snapshot =>
                    snapshot.SharedInstructions == SystemInstructions.DefaultContent &&
                    snapshot.Profile.Instructions == profile.Instructions &&
                    snapshot.Profile.Role == profile.Role && snapshot.Profile.Name == profile.Name &&
                    snapshot.ResolvedSettingsVersion == WorkerExecutionSnapshot.Version) &&
                store.Get<AgentProfile>("agent", profile.Id)!.Instructions == profile.Instructions,
                "shared base, profile fields, stored profile and snapshot edition are preserved");
            var publicSnapshot = WorkerExecutionSnapshot.Read(await WorkerExecutionSnapshot.CaptureAsync(
                store, profile, configuration, tools, [], default));
            Check(publicSnapshot.RoleInstructions == SystemInstructions.DefaultForRole(profile.Role),
                "the existing public capture overload retains its role-only fallback without caller metadata");

            // Source identity, not text equality, determines whether an explicit override exists.
            instructions.Save(SystemInstructions.CerebralContent, instructions.Get("lead").Sha256, "lead");
            Check(Selected("lead", coordinator) == SystemInstructions.CerebralContent,
                "an explicit SQLite override identical to the Main default is not replaced for a coordinator");
            instructions.Save("", instructions.Get("lead").Sha256, "lead");
            instructions.Save("FIXTURE-SHARED-OVERRIDE", instructions.Get().Sha256);
            var overridden = WorkerExecutionSnapshot.Read(await Capture(coordinator));
            Check(overridden.RoleInstructions == "" && overridden.SharedInstructions == "FIXTURE-SHARED-OVERRIDE" &&
                overridden.Profile.Instructions == "FIXTURE-PROFILE-INSTRUCTIONS",
                "blank role override, saved shared base and profile addendum remain exact at admission");
            Check(WorkerExecutionSnapshot.Read(coordinatorJson).RoleInstructions == SystemInstructions.CoordinatorContent &&
                WorkerExecutionSnapshot.Read(mainJson).SharedInstructions == SystemInstructions.DefaultContent,
                "new saves never reinterpret or rewrite already frozen snapshots");
            var retained = CerebralJson.Element(mainSnapshot with { RoleInstructions = "PREEXISTING-FROZEN-ROLE" });
            Check(WorkerExecutionSnapshot.Read(retained).RoleInstructions == "PREEXISTING-FROZEN-ROLE",
                "existing serialized role text remains opaque to the snapshot reader");
            instructions.Save("FIXTURE-HELPER-OVERRIDE", instructions.Get("helper").Sha256, "helper");
            Check(Selected("helper", owner) == "FIXTURE-HELPER-OVERRIDE" &&
                Selected("helper", coordinator) == "FIXTURE-HELPER-OVERRIDE",
                "explicit helper overrides retain precedence for either authenticated audience");
            Check(tools.Executions == 0, "instruction selection and snapshot capture execute no tools");

            var legacyStore = new StateStore(Path.Combine(root, "legacy"));
            var legacy = new SystemInstructions(legacyStore);
            Directory.CreateDirectory(Path.GetDirectoryName(legacy.PathForRole("lead"))!);
            var oldLead = Path.Combine(legacyStore.DirectoryPath, "Instructions", "ORCHESTRATOR.md");
            await File.WriteAllTextAsync(oldLead, "FIXTURE-LEGACY-LEAD");
            Check(legacy.CaptureForAssignment("lead", coordinator, null).Single(item => item.Role == "lead").Content == "FIXTURE-LEGACY-LEAD",
                "an explicit historical lead Markdown fallback is not replaced by an assignment default");
            await File.WriteAllTextAsync(legacy.PathForRole("lead"), "FIXTURE-CANONICAL-LEAD");
            Check(legacy.CaptureForAssignment("lead", worker, "accepted-parent").Single(item => item.Role == "lead").Content == "FIXTURE-CANONICAL-LEAD" &&
                await File.ReadAllTextAsync(oldLead) == "FIXTURE-LEGACY-LEAD" &&
                legacyStore.List<JsonElement>(SystemInstructions.RecordKind).Count == 0,
                "canonical legacy content keeps precedence without rewriting the old file or creating SQL overrides");
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class EmptyTools : IToolDispatcher
    {
        public int Executions { get; private set; }
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            Executions++;
            throw new InvalidOperationException("The role fixture must not execute tools.");
        }
    }
}
