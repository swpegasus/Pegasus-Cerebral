using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Cerebral.Programming;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real owner host/client admission and Engine SQLite with a deterministic verification
/// receipt. No model, build, desktop presentation, installation or external provider runs.</summary>
public static class OwnerFollowupPresentationTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "Cerebral.OwnerFollowupPresentation-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Owner Work follow-up: " + label);
            checks++;
            Console.WriteLine("PASS: " + label);
        }
        void Refused(Action action, string label)
        {
            try { action(); }
            catch (UnauthorizedAccessException) { Check(true, label); return; }
            throw new InvalidOperationException("Owner Work follow-up accepted an invalid origin: " + label);
        }

        try
        {
            var project = Path.Combine(root, "project");
            Directory.CreateDirectory(project);
            var manifest = Path.Combine(project, "desktop-plugin.json");
            File.Copy(typeof(InterfaceEntryFixture).Assembly.Location, Path.Combine(project, "fixture.dll"));
            File.WriteAllText(manifest, CerebralJson.Element(new
            {
                schemaVersion = 1, contract = "pegasus.cerebral.desktop", apiVersion = 1,
                id = "acceptance.owner-followup", name = "Owner follow-up fixture", version = "1.0.0",
                description = "Inert compiled metadata only.", assembly = "fixture.dll",
                entryType = typeof(InterfaceEntryFixture).FullName
            }).GetRawText());
            var fingerprint = NativeDesktopPluginPackage.ComputeFingerprint(manifest);
            var state = new StateStore(Path.Combine(root, "state"));
            var config = new Configuration(state);
            var settings = config.Get();
            settings.DefaultModelId = "fixture-model";
            settings.ProjectsDirectory = project;
            settings.QueueCapacity = 8;
            state.Put("settings", "host", settings);
            var model = Path.Combine(root, "fixture.gguf");
            File.WriteAllBytes(model, "GGUF"u8.ToArray());
            state.Put("model", "fixture-model", new ModelRecord
            {
                Id = "fixture-model", Path = model, AutomaticContextSizing = false,
                ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192,
                ParallelRequests = 1, GpuLayers = 0
            });
            var profile = new AgentProfile
            {
                Id = "owner-work-profile", Name = "Owner Work fixture", Role = "helper", WorkerId = "local",
                ModelWorkerId = "local", ModelId = "fixture-model", AutomaticCompaction = false,
                FollowsModelBudgets = false, ContextTokens = 8192, ResponseTokens = 1024,
                MaxSteps = 64, MaxSeconds = 300, AllowedRoots = [project],
                Capabilities = ["filesystem.read", "filesystem.write", "process.execute", "desktop.plugins.manage"],
                AllowedTools = ["file.read", "file.write", "process.run", "plugin.verify", "plugin.present"]
            };
            state.Put("agent", profile.Id, profile);
            const string projectId = "owner-work-project";
            state.Put("navigation", "sidebar", new NavigationState
            { Nodes = [new() { Id = projectId, Kind = "project", Path = project, Title = "Owner Work fixture" }] });
            var events = new EventHub();
            var interfaceTool = new InterfaceToolExtension(state, events, () => 2);
            await using var tools = new ToolDispatcher(config.Get,
                new IToolExtension[] { interfaceTool,
                    new PluginScaffoldToolExtension(null, typeof(IServicePlugin).Assembly.Location) }, state.DirectoryPath);
            using var plugin = new ProgrammingServicePlugin();
            var harness = ((IAgentHarnessModule)plugin).Harnesses.Single(item =>
                item.Descriptor.Id == ProgrammingAgentHarness.Id && item.Descriptor.Version == ProgrammingPrerequisiteProgressHarness.Version);
            var module = new HarnessModule(new AgentWorkflowDefinition(harness));
            var effects = new WorkerWorkflowEffects(state, config, new NoModel(), tools);
            var bindings = new ServicePluginPackageBinding[]
            {
                new(module.Id, "1.6.12", true, []),
                new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])
            };
            var host = new WorkerWorkflows(state, config, events, [module, effects], bindings, tools);
            var conversation = Guid.NewGuid().ToString("N");
            var input = CerebralJson.Element(new
            {
                rootPath = project, projectFile = "Fixture.csproj", configuration = "Release",
                selectedTools = new[] { "file.read", "file.write", "process.run", "plugin.verify", "plugin.present" },
                requiredChecks = new[] { "build" }, presentManifest = "desktop-plugin.json"
            });
            async Task<CommandResult> Command(CommandRequest request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                switch (request.Operation)
                {
                    case "jobs.workflow.submit":
                        return CommandResult.Ok(await host.SubmitAsync(
                            CerebralJson.Read<SubmitWorkflowRequest>(request.Arguments), null, token));
                    case "jobs.workflow.get":
                        return CommandResult.Ok(host.Store.Get(request.Arguments.GetProperty("id").GetString()!));
                    case "jobs.workflow.events.previous":
                        return CommandResult.Ok(host.Journal.ReadPreviousPage(conversation,
                            request.Arguments.TryGetProperty("beforeSequence", out var before) ? before.GetInt64() : null,
                            request.Arguments.GetProperty("limit").GetInt32()));
                    case "jobs.workflow.events":
                        return CommandResult.Ok(host.Journal.ReadPage(conversation,
                            request.Arguments.GetProperty("afterSequence").GetInt64(),
                            request.Arguments.GetProperty("limit").GetInt32()));
                    default:
                        throw new InvalidOperationException("The focused client fixture permits only submission and exact reads.");
                }
            }
            var client = new WorkflowConversationClient(conversation, Command);
            WorkflowConversationSubmission Submission(string id, string text) =>
                new(id, profile.Id, ProgrammingAgentHarness.Id, ProgrammingPrerequisiteProgressHarness.Version,
                    text, input, StepLimit: 64) { OwnerProjectId = projectId };
            await client.SubmitAsync(Submission(conversation, "Create the fixture plugin."));
            var original = host.Store.Get(conversation);
            Check(original.Submission.Caller?.Kind == EventActorKind.User &&
                original.Submission.ProjectId == projectId && original.Submission.RequestId == conversation,
                "real client and host commit the original direct owner Work root with its project");
            WorkflowUiOriginTestRuns.Finish(host.Store, original.Id);
            await client.RefreshAsync();
            var nextId = Guid.NewGuid().ToString("N");
            await client.SubmitAsync(Submission(nextId, "Correct the retained plugin and present it."));
            var next = host.Store.Get(nextId);
            Check(next.Id != conversation && next.Submission.ConversationId == conversation &&
                next.Submission.Caller?.Kind == EventActorKind.User &&
                next.Submission.ProfileId == original.Submission.ProfileId &&
                next.Submission.ProjectId == original.Submission.ProjectId &&
                JsonElement.DeepEquals(next.Submission.Input, original.Submission.Input) &&
                host.Store.GetOwnerWorkflowConversation(conversation, nextId,
                    ProgrammingAgentHarness.Id, ProgrammingPrerequisiteProgressHarness.Version).AcceptedRunId == nextId,
                "new direct request is the latest accepted owner turn in the original Work conversation");
            var context = new ToolExecutionContext(profile, conversation, nextId, project);
            var verify = WorkflowUiOriginTestRuns.Live(state, context,
                new(PluginScaffoldToolExtension.VerifyToolName, CerebralJson.Element(new { root = project })));
            var verifiedContent = CerebralJson.Element(new
            {
                root = project, verified = true,
                desktop = new { manifest, packageFingerprint = fingerprint }
            }).GetRawText();
            var verifiedData = new WorkerWorkflowEffects.ToolData(PluginScaffoldToolExtension.VerifyToolName,
                verifiedContent, false, new(ToolExecutionOutcome.Succeeded, "cerebral.plugin-sdk", State: "verified"));
            host.Store.CompleteEffect(nextId, verify.EffectId!,
                new(WorkflowEffectState.Succeeded, CerebralJson.Element(verifiedData)));
            var presentCall = new ToolCall(InterfaceToolExtension.PluginToolName,
                CerebralJson.Element(new { path = manifest, expectedPackageFingerprint = fingerprint }));
            var present = WorkflowUiOriginTestRuns.Live(state, context, presentCall);
            var requests = new InterfaceRequests(state, events);
            var queued = requests.CreateForWorkflow(present, "plugin", manifest, fingerprint);
            Check(queued.Source == "workflow" && queued.Status == "pending" &&
                queued.SessionId == conversation && queued.JobId == nextId &&
                queued.ProfileId == profile.Id && queued.PackageFingerprint == fingerprint &&
                requests.List(conversation).Count == 1,
                "current exact verify receipt and dispatched effect queue one review for the non-root owner follow-up");
            Check(requests.CreateForWorkflow(present, "plugin", manifest, fingerprint).Id == queued.Id,
                "identical dispatched presentation recovers the same retained review ID");
            Refused(() => WorkflowUiOrigin.ResolvePresentation(state, present with { SessionId = Guid.NewGuid().ToString("N") },
                "plugin", manifest, fingerprint, false), "foreign conversation cannot borrow the follow-up effect");
            Refused(() => WorkflowUiOrigin.ResolvePresentation(state, present with { JobId = Guid.NewGuid().ToString("N") },
                "plugin", manifest, fingerprint, false), "foreign run cannot borrow the follow-up effect");
            Refused(() => WorkflowUiOrigin.ResolvePresentation(state, present with { EffectId = Guid.NewGuid().ToString("N") },
                "plugin", manifest, fingerprint, false), "foreign effect cannot borrow the follow-up run");
            Refused(() => WorkflowUiOrigin.ResolvePresentation(state, present,
                "plugin", Path.Combine(project, "other-plugin.json"), fingerprint, false),
                "a different manifest cannot borrow the accepted input or dispatched arguments");
            Refused(() => WorkflowUiOrigin.ResolvePresentation(state, present,
                "plugin", manifest, new string('a', 64), false),
                "a different fingerprint cannot borrow the dispatched presentation");
            var engineDb = Path.Combine(state.DirectoryPath, "engine.db");
            string ReadOriginal()
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = engineDb }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT submission FROM workflow_runs WHERE id=$id;";
                command.Parameters.AddWithValue("$id", conversation);
                return command.ExecuteScalar() as string ??
                    throw new InvalidOperationException("The fixture's original owner run is missing.");
            }
            void ReplaceOriginal(string json)
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = engineDb }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE workflow_runs SET submission=$submission WHERE id=$id;";
                command.Parameters.AddWithValue("$submission", json);
                command.Parameters.AddWithValue("$id", conversation);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The fixture's original owner row changed.");
            }
            var savedOriginal = ReadOriginal();
            try
            {
                var foreignCaller = JsonNode.Parse(savedOriginal)!.AsObject();
                foreignCaller["caller"]!["id"] = "foreign-owner";
                ReplaceOriginal(foreignCaller.ToJsonString());
                Refused(() => WorkflowUiOrigin.ResolvePresentation(state, present,
                    "plugin", manifest, fingerprint, false),
                    "a different original owner caller cannot authorize a later Work turn");
                ReplaceOriginal(savedOriginal);
                var absentRoot = JsonNode.Parse(savedOriginal)!.AsObject();
                absentRoot["caller"]!["kind"] = "Service";
                ReplaceOriginal(absentRoot.ToJsonString());
                Refused(() => WorkflowUiOrigin.ResolvePresentation(state, present,
                    "plugin", manifest, fingerprint, false),
                    "an original run no longer owned by the user cannot anchor a later Work turn");
            }
            finally { ReplaceOriginal(savedOriginal); }
            Check(requests.List(conversation).Single().Id == queued.Id,
                "original-root tampering tests do not add or replace the owner review");
            host.Store.CompleteEffect(nextId, present.EffectId!,
                new(WorkflowEffectState.Unknown, Error: "Fixture-only uncertain presentation outcome."));
            Refused(() => WorkflowUiOrigin.ResolvePresentation(state, present,
                "plugin", manifest, fingerprint, false),
                "an uncertain effect cannot queue another owner review");
            Check(requests.List(conversation).Single().Id == queued.Id,
                "refused foreign and uncertain attempts never add or replace the original review");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("Cerebral.OwnerFollowupPresentation-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its unique temporary root.");
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"Owner Work follow-up: {checks} host/client/SQLite checks passed with a deterministic verify receipt; no model, build, native UI or installation.");
    }

    private sealed class NoModel : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor => new("fixture.owner-followup", "1.0.0", ["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) =>
            throw new InvalidOperationException("Owner follow-up origin tests must not prepare a model.");
    }

    private sealed class HarnessModule(IWorkflowDefinition definition) : IWorkflowModule
    {
        public string Id => "pegasus.programming";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [definition];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
    }
}
