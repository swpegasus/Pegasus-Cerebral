using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Task-owned SQLite and deterministic admission/tool stubs only; never prepares a model or starts a host.</summary>
public static class FreshChatDraftWorkerChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var labels = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Fresh draft: " + label);
            labels.Add(label);
        }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or
                InvalidDataException or KeyNotFoundException or NotSupportedException) { Check(true, label); return; }
            throw new InvalidOperationException("Fresh draft accepted invalid input: " + label);
        }
        async Task Case(Func<Fixture, Task> body)
        {
            Fixture? fixture = null; Exception? primary = null;
            try { fixture = new(); await body(fixture); }
            catch (Exception error) { primary = error; }
            finally
            {
                if (fixture is not null)
                {
                    try { fixture.Dispose(); }
                    catch (Exception cleanup)
                    {
                        if (primary is null) primary = cleanup;
                        else primary.Data["FreshDraftCleanupType"] = cleanup.GetType().FullName;
                    }
                }
            }
            if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        }

        await Case(async f =>
        {
            var run = await f.Host.SubmitAsync(f.Request("legacy-first", "legacy-chat"), null, default);
            var wire = Data(f.Paths.Get("legacy-chat"));
            Check(wire.EnumerateObject().Count() == 9 && !wire.TryGetProperty("initial", out _) &&
                !wire.TryGetProperty("initialState", out _), "unmarked owner path replies retain the exact legacy nine-field shape");
            Check(!WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot).ConversationPaths!.Value.TryGetProperty("initial", out _),
                "unmarked accepted path bindings do not gain a null initial-provenance field");
            Check(!run.Submission.SettingsSnapshot.TryGetProperty("workflowData", out _),
                "ordinary captured settings do not acquire a null DATA-edition field");
        });

        await Case(async f =>
        {
            var prepared = f.Prepare("first", "chat");
            Check(prepared.Revision == 1 && prepared.InitialState == "prepared" && prepared.RootState == "prepared" &&
                prepared.Root == f.ProjectRoot && prepared.Initial == f.Initial("first") &&
                f.Host.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, "chat") is null,
                "preparation is durable without pretending an accepted original conversation exists");
            var retry = f.Paths.Save("chat", 0, f.Rules().Reverse().ToArray(), f.Initial("first"));
            Check(retry.Revision == 1 && retry.UpdatedAt == prepared.UpdatedAt &&
                retry.Grants.SequenceEqual(prepared.Grants) && f.Paths.Get("chat").Revision == 1,
                "same first RID and normalized payload retrieve the immutable preparation");
            await Reject(() => { f.Prepare("different", "chat"); return Task.CompletedTask; }, "a replacement first RID requires a new conversation");
            await Reject(() => { f.Paths.Save("chat", 0, [], f.Initial("first")); return Task.CompletedTask; }, "one first RID cannot acquire different grants");
            await Reject(() => { f.Paths.Save("chat", 1, f.Rules(), f.Initial("first")); return Task.CompletedTask; }, "initial preparation requires its original zero CAS revision");
            await Reject(() => { f.Paths.Save("chat", 1, []); return Task.CompletedTask; }, "ordinary saved-chat editing cannot mutate an unsent preparation");
            var stored = f.State.Get<ConversationPathGrants.Stored>(ConversationPathGrants.RecordKind, "chat")!;
            Check(stored.SchemaVersion == 2 && stored.InitialPreparation is { Revision: 1 } &&
                stored.InitialPreparation.ProfileFingerprint.Length == 64,
                "new stored authority is explicitly versioned with immutable preparation provenance");
            f.Restart();
            Check(f.Paths.Get("chat").InitialState == "prepared" && f.Paths.Get("chat").Grants.SequenceEqual(prepared.Grants),
                "a fresh service view recovers the same unsubmitted preparation");
            Check(f.Models.Preparations == 0, "preparing and reading path authority never prepares inference");
        });

        await Case(async f =>
        {
            f.Prepare("first", "chat");
            foreach (var request in new[]
            {
                f.Request("other", "chat"), f.Request("first", "chat", root: f.ExternalRead),
                f.Request("first", "chat", project: null), f.Request("first", "chat", profile: f.Helper.Id)
            })
                await Reject(async () => { await f.Host.SubmitAsync(request, null, default); }, "fresh acceptance requires the complete prepared RID/profile/project/root tuple");
            await Reject(async () => { await f.Host.SubmitAsync(f.Request("first", "chat"), f.Profile, default); },
                "an agent caller cannot borrow owner-prepared first-submit authority");
            Check(f.Host.Store.TryGet("first") is null && f.Host.Store.TryGet("other") is null && f.Models.Preparations == 0,
                "tuple refusals precede any accepted run or inference");
            var run = await f.Host.SubmitAsync(f.Request("first", "chat"), null, default);
            var snapshot = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot);
            var binding = ConversationPathAuthority.Read(snapshot.ConversationPaths!.Value);
            Check(binding.Initial == f.Initial("first") && binding.SourceRunId == run.Id &&
                snapshot.Profile.Id == f.Profile.Id && snapshot.Profile.Revision == f.Profile.Revision &&
                snapshot.Profile.MaxSteps == f.Profile.MaxSteps && snapshot.Profile.MaxSeconds == f.Profile.MaxSeconds,
                "ordinary submission freezes exact selected profile limits and private initial path provenance");
            Check(f.Paths.Get("chat").InitialState == "accepted" && f.Paths.Get("chat").RootState == "bound" &&
                f.Paths.Get("chat").Root == f.ProjectRoot, "accepted readback derives the immutable original root");
            Check((await f.Effect(run, "file.read", f.ExternalRead)).State == WorkflowEffectState.Succeeded &&
                (await f.Effect(run, "file.write", f.ExternalRead)).State == WorkflowEffectState.Failed,
                "initial finite read authority does not imply write authority");
            f.Tools.FullAccessActive = true;
            Check((await f.Effect(run, "file.write", f.NestedRead)).State == WorkflowEffectState.Failed &&
                (await f.Effect(run, "file.write", f.NestedWrite)).State == WorkflowEffectState.Succeeded,
                "nested readonly and writable boundaries survive FullAccess");
            var frozen = run.Submission.SettingsSnapshot.GetRawText();
            f.Finish(run);
            var changed = Copy(f.Profile); changed.Revision++; changed.MaxSteps++; f.State.Put("agent", changed.Id, changed);
            var continued = await f.Host.SubmitAsync(f.Request("second", "chat"), null, default);
            Check(continued.Id == "second" && ConversationPathAuthority.Read(
                WorkerExecutionSnapshot.Read(continued.Submission.SettingsSnapshot).ConversationPaths!.Value).Initial == f.Initial("first"),
                "normal later turns retain initial lineage without requiring the first RID or old profile revision");
            Check(run.Submission.SettingsSnapshot.GetRawText() == frozen, "later acceptance never rewrites the first snapshot");
            f.State.Delete("agent", f.Profile.Id);
            var retrieved = await f.Host.SubmitAsync(f.Request("first", "chat"), null, default);
            Check(retrieved.Id == run.Id && retrieved.Submission.SettingsSnapshot.GetRawText() == frozen,
                "accepted RID retrieval precedes current profile lookup and never prepares again");
            Check(f.Paths.Save("chat", 0, f.Rules(), f.Initial("first")).Revision == 1,
                "the original preparation receipt remains retrievable after acceptance and profile removal");
        });

        await Case(async f =>
        {
            f.Tools.FullAccessActive = true;
            f.Paths.Save("deny-chat", 0, [], f.Initial("deny-first", root: null, project: null));
            var run = await f.Host.SubmitAsync(f.Request("deny-first", "deny-chat", root: null, project: null), null, default);
            var binding = ConversationPathAuthority.Read(WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot).ConversationPaths!.Value);
            Check(binding.Initial is not null && binding.Scope.Grants.Count == 0 &&
                (await f.Effect(run, "file.read", f.ProjectRoot)).State == WorkflowEffectState.Failed && f.Tools.Dispatches == 0,
                "an explicit unbound empty preparation is finite deny-all even under FullAccess");
        });

        await Case(async f =>
        {
            var redundant = Copy(f.Profile); redundant.AllowedRoots.Add(Path.Combine(f.ProjectRoot, "redundant-child"));
            f.State.Put("agent", redundant.Id, redundant);
            Check(f.Prepare("redundant-first", "redundant-chat").Revision == 1,
                "redundant nested positive profile roots do not falsely reject an already covered finite scope");
            await Reject(() => { f.Paths.Save("outside", 0, [new(f.Outside, "read")], f.Initial("outside-first")); return Task.CompletedTask; },
                "external grants cannot exceed the selected profile root ceiling");
            await Reject(() => { f.Paths.Save("outside-root", 0, [], f.Initial("outside-root-first", root: f.Outside)); return Task.CompletedTask; },
                "the implicit working-root write grant is also inside the profile ceiling");
            await Reject(() => { f.Paths.Save("stale", 0, [], f.Initial("stale-first") with { ExpectedProfileRevision = 99 }); return Task.CompletedTask; },
                "stale selected profile revision is refused before preparation persistence");
            var remote = Copy(f.Profile); remote.WorkerId = "remote"; f.State.Put("agent", remote.Id, remote);
            await Reject(() => { f.Prepare("remote-first", "remote-chat"); return Task.CompletedTask; }, "new initial execution cannot use a remote profile");
            f.State.Put("agent", f.Profile.Id, f.Profile);
            var helperInitial = f.Initial("helper-first") with { ProfileId = f.Helper.Id, ExpectedProfileRevision = f.Helper.Revision };
            f.Paths.Save("helper-chat", 0, [], helperInitial);
            var helper = await f.Host.SubmitAsync(f.Request("helper-first", "helper-chat", profile: f.Helper.Id), null, default);
            Check(WorkerExecutionSnapshot.Read(helper.Submission.SettingsSnapshot).Profile.Role == "helper" &&
                helper.Submission.ParentRunId is null, "explicit owner helper selection does not manufacture child or service lineage");
        });

        await Case(async f =>
        {
            f.Prepare("drift-first", "drift-chat");
            f.Tools.OnList = () => { var changed = Copy(f.Profile); changed.Revision++; f.State.Put("agent", changed.Id, changed); };
            await Reject(async () => { await f.Host.SubmitAsync(f.Request("drift-first", "drift-chat"), null, default); },
                "profile drift across asynchronous discovery refuses before winning acceptance");
            Check(f.Host.Store.TryGet("drift-first") is null && f.Models.Preparations == 0, "profile drift leaves no accepted work");
        });

        await Case(async f =>
        {
            f.Prepare("scope-first", "scope-chat");
            f.Tools.OnList = () =>
            {
                var prior = f.State.Get<ConversationPathGrants.Stored>(ConversationPathGrants.RecordKind, "scope-chat")!;
                f.State.Put(ConversationPathGrants.RecordKind, "scope-chat", prior with { Grants = [] });
            };
            await Reject(async () => { await f.Host.SubmitAsync(f.Request("scope-first", "scope-chat"), null, default); },
                "same-revision grant mutation cannot pass the final full preparation fingerprint recheck");
            Check(f.Host.Store.TryGet("scope-first") is null, "same-revision preparation drift creates no run");
        });

        await Case(async f =>
        {
            f.Prepare("parent-first", "parent-chat");
            var parent = await f.Host.SubmitAsync(f.Request("parent-first", "parent-chat"), null, default);
            var child = await f.Host.SubmitChildAsync(f.Request("child", "child-chat", profile: f.Helper.Id), parent.Id, f.Helper.Revision, default);
            var childBinding = ConversationPathAuthority.Read(WorkerExecutionSnapshot.Read(child.Submission.SettingsSnapshot).ConversationPaths!.Value);
            Check(childBinding.Initial == f.Initial("parent-first") && childBinding.SourceConversationId == "parent-chat" &&
                (await f.Effect(child, "file.read", f.ExternalRead)).State == WorkflowEffectState.Succeeded &&
                (await f.Effect(child, "file.write", f.ExternalWrite)).State == WorkflowEffectState.Failed,
                "delegated descendants inherit finite initial source authority rather than broader helper roots");
            var edition = new WorkflowDataEdition(WorkflowDataCatalog.Schema, "fixture.fresh-read", "1.0.0", "Scoped read",
                Data(new { type = "object", additionalProperties = false }), Data(new { type = "object" }), ["filesystem.read"],
                [new("read", "file.read", Data(new { path = f.ExternalRead }))], Data(new { complete = true }));
            var entry = f.Host.RegisterWorkflowData(new(child.Id, edition), f.Helper).Entry;
            var dataId = "data-child";
            var data = f.Host.Submit(new(dataId, WorkerWorkflows.WorkflowDataConversationId(dataId), f.Helper.Id,
                WorkflowDataModule.DefinitionId, WorkflowDataModule.DefinitionVersion, "Fixture data", null,
                Data(new WorkflowDataRunInput(child.Id, edition.Id, edition.Version, entry.Hash, WorkflowContract.EmptyObject))), f.Helper);
            Check(JsonElement.DeepEquals(WorkerExecutionSnapshot.Read(data.Submission.SettingsSnapshot).ConversationPaths!.Value,
                WorkerExecutionSnapshot.Read(child.Submission.SettingsSnapshot).ConversationPaths!.Value),
                "data descendants retain the same initial-provenance binding");
            var dataSnapshot = data.Submission.SettingsSnapshot;
            var frozenData = dataSnapshot.GetProperty("workflowData");
            var decodedData = WorkerExecutionSnapshot.Read(dataSnapshot);
            var encodedData = WorkerExecutionSnapshot.Bounded(decodedData);
            Check(decodedData.WorkflowData is { } retainedData && JsonElement.DeepEquals(retainedData, frozenData) &&
                JsonElement.DeepEquals(WorkerExecutionSnapshot.Read(encodedData).WorkflowData!.Value, frozenData) &&
                JsonElement.DeepEquals(encodedData.GetProperty("conversationPaths"), dataSnapshot.GetProperty("conversationPaths")),
                "the exact host-frozen DATA entry and finite authority survive Read and bounded reserialization");
            JsonElement Changed(Action<JsonObject> change)
            {
                var node = JsonNode.Parse(dataSnapshot.GetRawText())!.AsObject(); change(node); return Data(node);
            }
            JsonElement WithRawData(string raw) => Parse("{" + string.Join(",", dataSnapshot.EnumerateObject()
                .Where(field => field.Name != "workflowData")
                .Select(field => JsonSerializer.Serialize(field.Name) + ":" + field.Value.GetRawText())) +
                ",\"workflowData\":" + raw + "}");
            void RejectSnapshot(JsonElement value, string reason, string label)
            {
                try { _ = WorkerExecutionSnapshot.Read(value); }
                catch (InvalidDataException error)
                {
                    Check(error.Message.Contains(reason, StringComparison.Ordinal), label); return;
                }
                throw new InvalidOperationException("Fresh draft snapshot accepted invalid input: " + label);
            }
            RejectSnapshot(Changed(node => node["workflowData"] = null), "host-frozen workflow data entry",
                "a present-null DATA entry cannot masquerade as absent admission content");
            RejectSnapshot(Changed(node => node["workflowData"]!["hash"] = new string('0', 64)), "content hash does not match",
                "a well-shaped but incorrect DATA hash fails existing canonical-content validation");
            RejectSnapshot(Changed(node => node["workflowData"]!["extra"] = true), "entry is incompatible",
                "the known DATA member does not admit unknown entry fields");
            RejectSnapshot(Changed(node => node["workflowData"]!["edition"]!["stages"] = new JsonArray()), "entry is incompatible",
                "the known DATA member still enforces the catalog's bounded nonempty linear edition");
            RejectSnapshot(WithRawData("{\"projectId\":\"project\"," + frozenData.GetRawText()[1..]), "duplicate or invalid fields",
                "duplicate DATA coordinates fail before typed deserialization can collapse them");
            RejectSnapshot(Changed(node => node["unknownExecutionAuthority"] = true), "closed explicit contribution collection",
                "the known DATA field does not permit arbitrary current snapshot extensions");
            RejectSnapshot(Parse(dataSnapshot.GetRawText()[..^1] + ",\"workflowData\":" + frozenData.GetRawText() + "}"),
                "duplicate or invalid fields", "duplicate top-level DATA members remain refused");
            RejectSnapshot(Changed(node => node.Remove("contextContributions")), "closed explicit contribution collection",
                "known DATA does not bypass the explicit current contribution collection");
            foreach (var oldVersion in new[] { 2, 3 })
            {
                RejectSnapshot(Changed(node => node["resolvedSettingsVersion"] = oldVersion), "cannot acquire later extension state",
                    "legacy edition " + oldVersion + " cannot acquire the later DATA field through reflection");
                RejectSnapshot(Changed(node =>
                {
                    node["resolvedSettingsVersion"] = oldVersion; node.Remove("workflowData"); node["unknownExecutionAuthority"] = true;
                }), "cannot acquire later extension state",
                    "legacy edition " + oldVersion + " retains its arbitrary-extension refusal");
            }
            var editionFour = WorkerExecutionSnapshot.Read(Changed(node => node["resolvedSettingsVersion"] = 4));
            Check(editionFour.WorkflowData is null && editionFour.LegacyExtensions.TryGetValue("workflowData", out var oldData) &&
                JsonElement.DeepEquals(oldData, frozenData),
                "edition four retains its previous opaque extension semantics without new typed DATA admission");
            var opaqueFour = WorkerExecutionSnapshot.Read(Changed(node =>
            {
                node["resolvedSettingsVersion"] = 4; node.Remove("workflowData"); node["oldOpaqueExtension"] = true;
            }));
            Check(opaqueFour.LegacyExtensions.ContainsKey("oldOpaqueExtension"),
                "edition four keeps its existing unknown opaque-extension behavior");
            RejectSnapshot(Changed(node =>
            {
                node["resolvedSettingsVersion"] = 4; node["contextContributions"] = new JsonArray(new JsonObject());
            }), "cannot carry later context selections",
                "edition four still refuses a later nonempty contribution collection");
            var revoked = Copy(f.Profile); revoked.AllowedRoots = []; f.State.Put("agent", revoked.Id, revoked);
            Check((await f.Effect(parent, "file.read", f.ExternalRead)).State == WorkflowEffectState.Failed &&
                (await f.Effect(child, "file.read", f.ExternalRead)).State == WorkflowEffectState.Failed &&
                (await f.Effect(data, "file.read", f.ExternalRead)).State == WorkflowEffectState.Failed,
                "current source-profile root revocation narrows initial parent, delegated and data work");
        });

        await Case(async f =>
        {
            await using var models = new ModelRuntime(f.State, f.Configuration.Get, f.Events);
            await using var tools = new ToolDispatcher(f.Configuration.Get);
            using var scheduler = new JobScheduler(f.State, f.Configuration, new NoAgent(), models, f.Events, NullLogger<JobScheduler>.Instance, f.Host);
            var dispatcher = new CommandDispatcher(f.State, f.Configuration, models, scheduler, tools, f.Events, recordedTools: f.Tools, workflows: f.Host);
            Task<CommandResult> Command(JsonElement args, string? actor = null) => dispatcher.ExecuteAsync(new("conversations.paths.save", args, actor));
            var good = Data(new { id = "wire-chat", expectedRevision = 0, grants = Array.Empty<object>(), initial = f.Initial("wire-first") });
            Check((await Command(good)).Success && f.Paths.Get("wire-chat").InitialState == "prepared", "actual owner command accepts one closed complete initial object");
            Check(!(await Command(good, f.Profile.Id)).Success, "ordinary agent cannot prepare owner authority through the new payload");
            foreach (var rawInitial in new[] { "null", "{}", "{\"firstRequestId\":\"x\"}",
                "{\"firstRequestId\":\"x\",\"profileId\":\"p\",\"expectedProfileRevision\":1,\"projectId\":null,\"workingDirectory\":null,\"extra\":true}",
                "{\"firstRequestId\":\"x\",\"firstRequestId\":\"y\",\"profileId\":\"p\",\"expectedProfileRevision\":1,\"projectId\":null,\"workingDirectory\":null}",
                "{\"firstRequestId\":\"x\",\"profileId\":\"p\",\"expectedProfileRevision\":\"1\",\"projectId\":null,\"workingDirectory\":null}" })
            {
                var raw = "{\"id\":\"bad-wire\",\"expectedRevision\":0,\"grants\":[],\"initial\":" + rawInitial + "}";
                var reply = await Command(Parse(raw));
                Check(!reply.Success && reply.Error is { } error &&
                    (error.Contains("Initial chat authority", StringComparison.Ordinal) ||
                    error.Contains("All five initial chat fields", StringComparison.Ordinal)),
                    "actual boundary refuses null, partial, duplicate, unknown or untyped metadata before profile lookup");
            }
            Check(f.Models.Preparations == 0, "owner route parsing never prepares a model");
        });
        Console.WriteLine($"Fresh chat draft: {labels.Count} isolated SQLite/admission/scope fixture checks passed; no installed state or model execution.");
        return labels;
    }

    private static JsonElement Data(object? value) => CerebralJson.Element(value);
    private static JsonElement Parse(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static AgentProfile Copy(AgentProfile profile) => CerebralJson.Read<AgentProfile>(Data(profile));
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cerebral-fresh-draft-" + Guid.NewGuid().ToString("N"));
        public string ProjectRoot { get; }
        public string ExternalRead { get; }
        public string ExternalWrite { get; }
        public string NestedRead { get; }
        public string NestedWrite { get; }
        public string Outside { get; }
        public StateStore State { get; private set; } = null!;
        public Configuration Configuration { get; private set; } = null!;
        public EventHub Events { get; } = new();
        public WorkerWorkflows Host { get; private set; } = null!;
        public ConversationPathGrants Paths { get; private set; } = null!;
        public ScopeTools Tools { get; } = new();
        public NoModels Models { get; } = new();
        public AgentProfile Profile { get; }
        public AgentProfile Helper { get; }
        private IWorkflowCommandHandler toolHandler = null!;
        public Fixture()
        {
            ProjectRoot = Path.Combine(root, "project"); ExternalRead = Path.Combine(root, "reference"); ExternalWrite = Path.Combine(root, "output");
            NestedRead = Path.Combine(ProjectRoot, "readonly"); NestedWrite = Path.Combine(NestedRead, "writable"); Outside = Path.Combine(root, "outside");
            try
            {
            foreach (var path in new[] { ProjectRoot, ExternalRead, ExternalWrite, NestedRead, NestedWrite, Outside }) Directory.CreateDirectory(path);
            Profile = new() { Id = "fresh-owner", Role = "lead", Revision = 1, FollowsDefaultModel = true, AutomaticCompaction = false,
                MaxSteps = 64, MaxSeconds = 600, AllowedRoots = [ProjectRoot, ExternalRead, ExternalWrite],
                AllowedTools = ["file.read", "file.write"], Capabilities = ["filesystem.read", "filesystem.write", "agents.delegate"] };
            Helper = new() { Id = "fresh-helper", Role = "helper", Revision = 8, FollowsDefaultModel = true, AutomaticCompaction = false,
                MaxSteps = 64, MaxSeconds = 600, AllowedRoots = [root], AllowedTools = ["file.read", "file.write"],
                Capabilities = ["filesystem.read", "filesystem.write"] };
            Restart(); State.Put("agent", Profile.Id, Profile); State.Put("agent", Helper.Id, Helper);
            State.Put("navigation", "sidebar", new NavigationState { Nodes = [new() { Id = "project", Kind = "project", Path = ProjectRoot, Title = "Fixture project" }] });
            Configuration.Get();
            Profile = State.Get<AgentProfile>("agent", Profile.Id)!;
            Helper = State.Get<AgentProfile>("agent", Helper.Id)!;
            }
            catch (Exception primary)
            {
                try { Dispose(); } catch (Exception cleanup) { primary.Data["FreshDraftConstructorCleanupType"] = cleanup.GetType().FullName; }
                throw;
            }
        }
        public void Restart()
        {
            State = new(Path.Combine(root, "data")); Configuration = new(State); Configuration.Get();
            var effects = new WorkerWorkflowEffects(State, Configuration, Models, Tools, workflowHost: () => Host);
            Host = new(State, Configuration, Events, [new ConversationWorkflowModule(), effects],
                [ConversationWorkflowModule.PackageBinding, new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])], Tools);
            Paths = new(State, Host, Events); toolHandler = effects.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.ToolCommand);
        }
        public ConversationPathGrants.Grant[] Rules() => [new(ExternalRead, "read"), new(NestedRead, "read"), new(NestedWrite, "write")];
        public ConversationPathGrants.Initial Initial(string request, string? root = "", string? project = "project") =>
            new(request, Profile.Id, Profile.Revision, project, root == "" ? ProjectRoot : root);
        public ConversationPathGrants.Snapshot Prepare(string request, string conversation) => Paths.Save(conversation, 0, Rules(), Initial(request));
        public SubmitWorkflowRequest Request(string request, string conversation, string? root = "", string? project = "project", string? profile = null)
        {
            var input = new Dictionary<string, object?>();
            if (root is not null) input["workingDirectory"] = root == "" ? ProjectRoot : root;
            if (project is not null) input["projectId"] = project;
            return new(request, conversation, profile ?? Profile.Id, ConversationWorkflowModule.DefinitionId,
                ConversationWorkflowModule.DefinitionVersion, "Fixture owner request", null, Data(input));
        }
        public Task<WorkflowCommandResult> Effect(WorkflowRun run, string tool, string path) => toolHandler.ExecuteAsync(new(
            "fresh-effect-" + Guid.NewGuid().ToString("N"), run.Submission,
            Data(new WorkerWorkflowEffects.ToolArguments(tool, Data(new { path }), ProjectRoot))) { RunId = run.Id }, default);
        public void Finish(WorkflowRun run)
        {
            var claimed = Host.Store.TryClaim(run.Id)!;
            Host.Store.ApplyStep(run.Id, claimed.Revision, new(claimed.State, new CompleteWorkflow(WorkflowContract.EmptyObject)));
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (!root.StartsWith(Path.Combine(Path.GetTempPath(), "cerebral-fresh-draft-"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup scope changed.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    private sealed class ScopeTools : IToolDispatcher
    {
        public bool FullAccessActive { get; set; }
        public int Dispatches;
        public Action? OnList;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); OnList?.Invoke();
            return Task.FromResult<IReadOnlyList<ToolDescriptor>>([new("file.read", "Fixture read", Data(new { type = "object" }), "filesystem.read"),
                new("file.write", "Fixture write", Data(new { type = "object" }), "filesystem.write")]);
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try { ScopedFilePolicy.PathInScope(call.Arguments.GetProperty("path").GetString()!, context,
                call.Name == "file.read" ? ScopedPathAccess.Read : ScopedPathAccess.Write); }
            catch (UnauthorizedAccessException)
            {
                return Task.FromResult(new ToolResult(false, "Fixture path denied")
                { Evidence = new(ToolExecutionOutcome.Rejected, "fixture.fresh-draft", context.EffectId) });
            }
            Dispatches++;
            return Task.FromResult(new ToolResult(true, "Fixture scope admitted")
            { Evidence = new(ToolExecutionOutcome.Succeeded, "fixture.fresh-draft", context.EffectId) });
        }
    }
    private sealed class NoModels : IModelRuntimeAdapter
    {
        public int Preparations;
        public ModelAdapterDescriptor Descriptor => new("fixture.fresh-draft", "1.0.0", ["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        { Preparations++; throw new InvalidOperationException("Fresh draft fixtures must not prepare a model."); }
    }
    private sealed class NoAgent : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Fresh draft fixtures must not execute an agent.");
    }
}
