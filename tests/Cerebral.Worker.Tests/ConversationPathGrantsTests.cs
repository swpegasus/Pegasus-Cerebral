using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Isolated SQLite/admission fixtures with a scope-checking stub; no model/provider/installed state.</summary>
public static class ConversationPathGrantsTests
{
    public static async Task RunAsync()
    {
        await RunPathPolicyAsync();
        await FreshChatDraftWorkerChecks.RunAsync();
    }

    public static async Task RunWorkingDirectoryGateAsync()
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Working-directory gate: " + label);
            checks++;
        }
        using var fixture = new Fixture();
        var first = await fixture.Owner("gate-first", "gate-chat", bound: false);
        Check(!first.Submission.Input.TryGetProperty("workingDirectory", out _),
            "first owner request stays pathless");
        var read = await fixture.Effect(first, "file.read", fixture.ProjectRoot);
        Check(read.State == WorkflowEffectState.Succeeded && fixture.Tools.Dispatches == 1,
            "a pathless request still dispatches an admitted read");
        var writeContext = fixture.Context(first, "file.write", fixture.ProjectRoot, "gate-write-effect");
        var write = await fixture.ToolHandler.ExecuteAsync(writeContext, default);
        if (write.Data is not { } result) throw new InvalidOperationException("Working-directory gate has no bound no-effect result.");
        var action = result.GetProperty("ownerAction");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(writeContext.Arguments.GetRawText()))).ToLowerInvariant();
        Check(write.State == WorkflowEffectState.Failed && fixture.Tools.Dispatches == 1 &&
            action.GetProperty("noEffect").GetBoolean() &&
            action.GetProperty("sourceRunId").GetString() == first.Id &&
            action.GetProperty("sourceRequestId").GetString() == first.Submission.RequestId &&
            action.GetProperty("effectId").GetString() == writeContext.EffectId &&
            action.GetProperty("toolName").GetString() == "file.write" &&
            action.GetProperty("argumentsSha256").GetString() == hash &&
            action.GetProperty("propertiesRevision").GetInt64() == 0,
            "first write returns exact owner-action binding before dispatcher execution");
        fixture.Finish(first);
        var properties = new ConversationProperties(fixture.State, fixture.Host, fixture.Events, fixture.Tools);
        var chosen = properties.Save("gate-chat", 0, null, true, fixture.ProjectRoot);
        Check(chosen.Root == fixture.ProjectRoot &&
            !first.Submission.Input.TryGetProperty("workingDirectory", out _),
            "saving a path does not rewrite the accepted pathless request");
        var oldWrite = await fixture.Effect(first, "file.write", fixture.ProjectRoot);
        Check(oldWrite.State == WorkflowEffectState.Failed && fixture.Tools.Dispatches == 1,
            "a saved path cannot rebind or replay the old write attempt");
        var next = await fixture.Owner("gate-next", "gate-chat", bound: false);
        Check(next.Submission.Input.GetProperty("workingDirectory").GetString() == fixture.ProjectRoot &&
            (await fixture.Effect(next, "file.write", fixture.ProjectRoot)).State == WorkflowEffectState.Succeeded &&
            fixture.Tools.Dispatches == 2,
            "only a fresh owner request receives the selected path and dispatches a write");
        Console.WriteLine($"Working-directory gate: {checks} deterministic host/tool checks passed.");
    }

    public static async Task RunPathPolicyAsync()
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException("Conversation paths: " + label); checks++; }
        void Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or
                InvalidDataException or KeyNotFoundException or NotSupportedException) { Check(true, label); return; }
            throw new InvalidOperationException("Conversation paths accepted invalid input: " + label);
        }
        using var fixture = new Fixture();
        SharedProjectsDefaults(fixture.ProjectRoot, fixture.ExternalRead, Check, Reject);
        var source = await fixture.Owner("path-first", "path-chat");
        var initial = fixture.Paths.Get("path-chat");
        var frozenInitial = source.Submission.SettingsSnapshot.GetRawText();
        Check(initial.Root == fixture.ProjectRoot && initial.RootState == "bound" && initial.Revision == 0 && initial.UpdatedAt is null &&
            initial.Grants.Count == 0 && initial.EffectiveGrants.Single() == new ConversationPathGrants.Grant(fixture.ProjectRoot, "write"),
            "a queued native owner run supplies one immutable root entry without a saved external record");
        var subscriber = fixture.Events.Subscribe();
        try
        {
            Check(fixture.Paths.Save(initial.Id, 0, []).Revision == 0 && !subscriber.Reader.TryRead(out _) &&
                fixture.State.Get<ConversationPathGrants.Stored>(ConversationPathGrants.RecordKind, initial.Id) is null,
                "a bound root's initial empty external policy is a no-op without a record or event");
            var rules = fixture.Rules();
            var first = fixture.Paths.Save(initial.Id, 0, rules);
            Check(first.Revision == 1 && first.UpdatedAt is not null && first.Grants.Count == 4 &&
                subscriber.Reader.TryRead(out var changed) && changed.Type == "conversation.paths.updated" &&
                changed.Data.GetProperty("revision").GetInt64() == 1 && !subscriber.Reader.TryRead(out _),
                "owner path publication commits one separate CAS record and one bounded notification");
            var noOp = fixture.Paths.Save(initial.Id, 1, rules.Reverse().ToArray());
            Check(noOp.Revision == 1 && noOp.UpdatedAt == first.UpdatedAt && !subscriber.Reader.TryRead(out _),
                "canonical reordered rules are a no-op with their exact revision and timestamp retained");
            Reject(() => fixture.Paths.Save(initial.Id, 0, rules), "stale revision fails even for unchanged rules");
            Reject(() => fixture.Paths.Save(initial.Id, 1, [new(fixture.ProjectRoot + Path.DirectorySeparatorChar, "read")]),
                "an external rule cannot replace the immutable root through a trailing path alias");
            Reject(() => fixture.Paths.Save(initial.Id, 1, [new(fixture.ExternalRead, "read"), new(fixture.ExternalRead, "write")]),
                "duplicate canonical paths cannot disguise conflicting modes");
            Reject(() => fixture.Paths.Save(initial.Id, 1, [new("../outside", "read")]), "relative path grants fail closed");
            Reject(() => fixture.Paths.Save(initial.Id, 1, [new(fixture.ExternalRead, "Read")]), "unknown mode spellings fail closed");
            Reject(() => fixture.Paths.Save(initial.Id, 1, Enumerable.Range(0, 33).Select(index =>
                new ConversationPathGrants.Grant(Path.Combine(fixture.ExternalRead, index.ToString()), "read")).ToArray()),
                "explicit rules remain bounded independently of the larger intersection representation");
            var profileBefore = Data(fixture.State.Get<AgentProfile>("agent", fixture.Profile.Id));
            var responseBefore = new ConversationProperties(fixture.State, fixture.Host, fixture.Events, fixture.Tools).Save(initial.Id, 0, 25);
            Check(fixture.Paths.Get(initial.Id).Revision == 1 && responseBefore.Revision == 1 &&
                JsonElement.DeepEquals(profileBefore, Data(fixture.State.Get<AgentProfile>("agent", fixture.Profile.Id))) &&
                source.Submission.SettingsSnapshot.GetRawText() == frozenInitial,
                "path and response revisions are independent and no reusable profile or accepted snapshot is rewritten");
            fixture.Finish(source);
            var accepted = await fixture.Owner("path-accepted", initial.Id);
            var acceptedSnapshot = WorkerExecutionSnapshot.Read(accepted.Submission.SettingsSnapshot);
            Check(acceptedSnapshot.ConversationPaths is { } binding && ConversationPathAuthority.Read(binding).Revision == 1 &&
                acceptedSnapshot.Profile.AllowedRoots.SequenceEqual([fixture.ProjectRoot]),
                "new owner admission freezes exact path policy without widening profile roots");
            Check((await fixture.Effect(accepted, "file.read", fixture.ExternalRead)).State == WorkflowEffectState.Succeeded &&
                fixture.Tools.LastContext?.PathAccess is not null && fixture.Tools.LastContext.Profile.AllowedRoots.SequenceEqual([fixture.ProjectRoot]),
                "an explicit external read rule is admitted independently of profile roots with a host-owned scope");
            var narrowedProfile = CerebralJson.Read<AgentProfile>(Data(fixture.Profile));
            narrowedProfile.AllowedRoots = [];
            fixture.State.Put("agent", narrowedProfile.Id, narrowedProfile);
            Check((await fixture.Effect(accepted, "file.read", fixture.ExternalRead)).State == WorkflowEffectState.Succeeded,
                "explicit owner path authority remains independent of reusable profile roots");
            narrowedProfile.AllowedTools = ["file.write"];
            fixture.State.Put("agent", narrowedProfile.Id, narrowedProfile);
            var ceilingDispatches = fixture.Tools.Dispatches;
            Check((await fixture.Effect(accepted, "file.read", fixture.ExternalRead)).State == WorkflowEffectState.Failed && fixture.Tools.Dispatches == ceilingDispatches,
                "owner path rules never bypass a current profile tool revocation");
            narrowedProfile.AllowedTools = ["file.read", "file.write"];
            narrowedProfile.Capabilities.Remove("filesystem.read");
            fixture.State.Put("agent", narrowedProfile.Id, narrowedProfile);
            Check((await fixture.Effect(accepted, "file.read", fixture.ExternalRead)).State == WorkflowEffectState.Failed && fixture.Tools.Dispatches == ceilingDispatches,
                "owner path rules never bypass a current profile capability revocation");
            fixture.State.Put("agent", fixture.Profile.Id, fixture.Profile);
            var before = fixture.Tools.Dispatches;
            Check((await fixture.Effect(accepted, "file.write", fixture.ExternalRead)).State == WorkflowEffectState.Failed && fixture.Tools.Dispatches == before,
                "read-only external content cannot dispatch a semantic write");
            Check((await fixture.Effect(accepted, "file.write", fixture.NestedRead)).State == WorkflowEffectState.Failed &&
                (await fixture.Effect(accepted, "file.write", fixture.NestedWrite)).State == WorkflowEffectState.Succeeded &&
                (await fixture.Effect(accepted, "file.write", fixture.ProjectRoot)).State == WorkflowEffectState.Succeeded,
                "most-specific nested RO and RW rules restrict an immutable RW root without flattening exceptions");
            Check((await fixture.Effect(source, "file.read", fixture.ExternalRead)).State == WorkflowEffectState.Failed,
                "a grant saved after original admission cannot enlarge the original frozen root-only authority");

            fixture.Paths.Save(initial.Id, 1, [new(fixture.ExternalRead, "write"), new(fixture.NewExternal, "write")]);
            fixture.Tools.FullAccessActive = true;
            before = fixture.Tools.Dispatches;
            Check((await fixture.Effect(accepted, "file.write", fixture.ExternalRead)).State == WorkflowEffectState.Failed &&
                (await fixture.Effect(accepted, "file.read", fixture.NewExternal)).State == WorkflowEffectState.Failed &&
                fixture.Tools.Dispatches == before && fixture.Tools.LastContext is { FullAccess: false },
                "later mode/root expansion and Full Access never enlarge accepted path or application authority");
            var firstRead = await fixture.Effect(accepted, "file.read", fixture.ExternalRead);
            Check(firstRead.State == WorkflowEffectState.Succeeded, "a current RW upgrade still preserves the accepted RO read ceiling");
            fixture.Tools.OnList = () => fixture.Paths.Save(initial.Id, 2, []);
            before = fixture.Tools.Dispatches;
            Check((await fixture.Effect(accepted, "file.read", fixture.ExternalRead)).State == WorkflowEffectState.Failed && fixture.Tools.Dispatches == before,
                "path revocation during tool discovery is rechecked immediately before the semantic effect");
            fixture.Tools.OnList = null;
            var retained = new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, "fixture.paths", "reconcile-effect", "fixture-job");
            var context = fixture.Context(accepted, "file.write", fixture.ExternalRead, "reconcile-effect");
            var previous = new WorkflowEffect(context.EffectId, WorkerWorkflowEffects.ToolCommand, 1, context.Arguments,
                WorkflowReplayPolicy.Never, WorkflowEffectState.Unknown, Data(new WorkerWorkflowEffects.ToolData("file.write", "pending", false, retained)));
            before = fixture.Tools.Dispatches;
            var reconciled = await fixture.ToolHandler.ReconcileAsync(context with { PreviousEffect = previous }, default);
            Check(reconciled.State == WorkflowEffectState.Succeeded && fixture.Tools.Dispatches == before && fixture.Tools.Reconciliations == 1,
                "exact retained recovery remains inspect-only after path revocation and never replays a write");
            var forged = accepted.Submission with { SettingsSnapshot = ConversationPathAuthority.Bind(accepted.Submission.SettingsSnapshot,
                ConversationPathAuthority.Read(acceptedSnapshot.ConversationPaths!.Value) with { SourceRunId = source.Id }) };
            Check((await fixture.ToolHandler.ExecuteAsync(fixture.Context(accepted, "file.read", fixture.ProjectRoot) with { Submission = forged }, default)).State == WorkflowEffectState.Failed,
                "a path binding from another exact source run cannot authorize an effect");
            var forgedScope = accepted.Submission with { SettingsSnapshot = ConversationPathAuthority.Bind(accepted.Submission.SettingsSnapshot,
                ConversationPathAuthority.Read(acceptedSnapshot.ConversationPaths!.Value) with
                { Scope = ConversationPathGrants.Normalize([new(fixture.NewExternal, ScopedPathAccess.Write)]) }) };
            Check((await fixture.ToolHandler.ExecuteAsync(fixture.Context(accepted, "file.read", fixture.NewExternal) with { Submission = forgedScope }, default)).State == WorkflowEffectState.Failed,
                "a fabricated context scope cannot replace exact immutable accepted path entries");
            fixture.Tools.FullAccessActive = false;
            fixture.Finish(accepted);
            fixture.Paths.Save(initial.Id, 3, rules);
            var parent = await fixture.Owner("path-parent", initial.Id);
            fixture.State.Put("agent", fixture.Helper.Id, fixture.Helper);
            var child = await fixture.Host.SubmitChildAsync(fixture.Request("path-child", "path-child-chat", fixture.Helper.Id,
                Data(new { workingDirectory = fixture.NewExternal, projectId = "project" })), parent.Id, fixture.Helper.Revision, default);
            var childBinding = ConversationPathAuthority.Read(WorkerExecutionSnapshot.Read(child.Submission.SettingsSnapshot).ConversationPaths!.Value);
            Check(childBinding.SourceRunId == parent.Id && childBinding.SourceConversationId == initial.Id && childBinding.SourceProfileId == fixture.Profile.Id &&
                (await fixture.Effect(child, "file.read", fixture.ExternalRead)).State == WorkflowEffectState.Succeeded &&
                (await fixture.Effect(child, "file.read", fixture.NewExternal)).State == WorkflowEffectState.Failed,
                "helper admission inherits the accepted parent's path source and cannot substitute broader helper roots");
            Reject(() => fixture.Paths.Save(child.Submission.ConversationId, 0, [new(fixture.NewExternal, "write")]),
                "an inherited-only chat cannot publish misleading independent owner path rules");

            var edition = new WorkflowDataEdition(WorkflowDataCatalog.Schema, "fixture.path-read", "1.0.0", "One scoped fixture read",
                Data(new { type = "object", additionalProperties = false }), Data(new { type = "object" }), ["filesystem.read"],
                [new("read", "file.read", Data(new { path = fixture.ExternalRead }))], Data(new { complete = true }));
            var entry = fixture.Host.RegisterWorkflowData(new(child.Id, edition), fixture.Helper).Entry;
            var dataId = "path-data-child";
            var dataRun = fixture.Host.Submit(new(dataId, WorkerWorkflows.WorkflowDataConversationId(dataId), fixture.Helper.Id,
                WorkflowDataModule.DefinitionId, WorkflowDataModule.DefinitionVersion, "Fixture path data run", null,
                Data(new WorkflowDataRunInput(child.Id, edition.Id, edition.Version, entry.Hash, WorkflowContract.EmptyObject))), fixture.Helper);
            Check(JsonElement.DeepEquals(WorkerExecutionSnapshot.Read(dataRun.Submission.SettingsSnapshot).ConversationPaths!.Value,
                WorkerExecutionSnapshot.Read(child.Submission.SettingsSnapshot).ConversationPaths!.Value),
                "real data-host admission copies the exact child path binding into its separate execution conversation");
            fixture.Paths.Save(initial.Id, 4, []);
            before = fixture.Tools.Dispatches;
            var failedData = await fixture.Host.Runtime.RunAsync(dataRun.Id);
            Check((await fixture.Effect(child, "file.read", fixture.ExternalRead)).State == WorkflowEffectState.Failed &&
                failedData.Status == WorkflowStatus.Failed && fixture.Tools.Dispatches == before && fixture.Models.Preparations == 0,
                "current source chat revocation blocks helper and real data successor effects without model preparation or replay");

            var persisted = fixture.Paths.Get(initial.Id);
            fixture.Restart();
            Check(fixture.Paths.Get(initial.Id).Revision == persisted.Revision && fixture.Paths.Get(initial.Id).UpdatedAt == persisted.UpdatedAt &&
                fixture.Paths.Get(initial.Id).Root == initial.Root && fixture.Paths.Get(initial.Id).Grants.Count == 0,
                "a new SQLite/backend instance retains exact path revision and immutable root");
            await OwnerRoutes(fixture, Check);
        }
        finally { fixture.Events.Unsubscribe(subscriber.Id); }

        using (var legacy = new Fixture())
        {
            var run = await legacy.Owner("unbound-path-run", "unbound-path-chat", bound: false);
            Check(WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot).ConversationPaths is null &&
                (await legacy.Effect(run, "file.read", legacy.ProjectRoot)).State == WorkflowEffectState.Succeeded,
                "an unbound native chat with no adopted policy preserves old profile-only authority without inventing a root");
            var explicitRules = legacy.Paths.Save(run.Submission.ConversationId, 0, [new(legacy.ExternalRead, "read")]);
            Check((await legacy.Effect(run, "file.read", legacy.ExternalRead)).State == WorkflowEffectState.Failed,
                "saving an external rule cannot enlarge an old unbound profile-only snapshot");
            legacy.Paths.Save(run.Submission.ConversationId, explicitRules.Revision, []);
            Check((await legacy.Effect(run, "file.read", legacy.ProjectRoot)).State == WorkflowEffectState.Failed &&
                (await legacy.Effect(run, "file.read", legacy.ExternalRead)).State == WorkflowEffectState.Failed,
                "a saved empty policy narrows old frozen authority without inventing a bound root");
            legacy.State.Put("session", "legacy-path-chat", new TaskSession { Id = "legacy-path-chat", ProfileId = legacy.Profile.Id });
            Reject(() => legacy.Paths.Save("legacy-path-chat", 0, []), "legacy-session path mutation is unsupported");
            Reject(() => legacy.Paths.Get("unknown-path-chat"), "path get never invents a native chat");
        }
        using (var emptyAdoption = new Fixture())
        {
            var run = await emptyAdoption.Owner("empty-policy-run", "empty-policy-chat", bound: false);
            var adopted = emptyAdoption.Paths.Save(run.Submission.ConversationId, 0, []);
            var noOp = emptyAdoption.Paths.Save(run.Submission.ConversationId, 1, []);
            Check(adopted.Revision == 1 && adopted.UpdatedAt is not null && adopted.Root is null && adopted.RootState == "unbound" &&
                adopted.EffectiveGrants.Count == 0 && noOp.Revision == 1 && noOp.UpdatedAt == adopted.UpdatedAt &&
                emptyAdoption.State.Get<ConversationPathGrants.Stored>(ConversationPathGrants.RecordKind, adopted.Id) is not null &&
                (await emptyAdoption.Effect(run, "file.read", emptyAdoption.ProjectRoot)).State == WorkflowEffectState.Failed,
                "an unbound owner's initial empty save adopts a durable deny-all scope; the next identical save is a no-op");
        }
        using (var fullAccess = new Fixture())
        {
            fullAccess.Tools.FullAccessActive = true;
            var run = await fullAccess.Owner("full-path-run", "full-path-chat");
            Check(WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot).FullAccess &&
                (await fullAccess.Effect(run, "file.read", fullAccess.ExternalRead)).State == WorkflowEffectState.Failed &&
                fullAccess.Tools.LastContext is { FullAccess: true, PathAccess: not null } && fullAccess.Tools.Dispatches == 0,
                "an already frozen active Full Access setting does not bypass a bound native chat's finite path scope");
        }
        using (var race = new Fixture())
        {
            var seed = await race.Owner("path-race-seed", "path-race-chat"); race.Finish(seed);
            race.Tools.DiscoveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            race.Tools.DiscoveryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = race.Owner("path-race-turn", seed.Submission.ConversationId);
            try
            {
                await race.Tools.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                race.Paths.Save(seed.Submission.ConversationId, 0, [new(race.ExternalRead, "read")]);
                race.Tools.DiscoveryRelease.TrySetResult();
                var rejected = false;
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (WorkflowConflictException) { rejected = true; }
                Check(rejected && race.Host.Store.TryGet("path-race-turn") is null && race.Models.Preparations == 0,
                    "a path revision changed during asynchronous fake discovery rejects stale admission before persistence");
            }
            finally { race.Tools.DiscoveryRelease.TrySetResult(); try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (WorkflowConflictException) { } }
        }
        Console.WriteLine($"Conversation path grants: {checks} isolated SQLite/admission/scope fixture checks passed; no installed state, inference or provider execution.");
    }

    private static void SharedProjectsDefaults(string projects, string external, Action<bool, string> check,
        Action<Action, string> reject)
    {
        const string chat = "cf3c3e4278944adca108145a73326d26";
        var resolver = typeof(InstallationPaths).GetMethod("ResolveRegisteredProjectsDirectory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        string? Registered(object? projectsValue, string? requestedInstall = null)
        {
            try { return (string?)resolver.Invoke(null, [external, projects, projectsValue, requestedInstall ?? external, projects]); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        check(Registered(projects + Path.DirectorySeparatorChar) == projects &&
            Registered(42, Path.Combine(external, "other-install")) is null,
            "only matching installed Program/Data metadata selects the canonical authoritative Projects path");
        foreach (var invalid in new object?[] { null, 42, "relative", "", projects + "\n" })
            reject(() => Registered(invalid), "matched registration rejects absent, wrongly typed or invalid Projects metadata without fallback");
        var first = ConversationWorkingDirectory.DefaultForChat(projects, chat);
        var second = ConversationWorkingDirectory.DefaultForChat(projects, "another-chat");
        check(first == Path.Combine(projects, "Source", chat) && first != second &&
            second == ConversationWorkingDirectory.DefaultForChat(projects, "another-chat") &&
            !Directory.Exists(first) && !Directory.Exists(second),
            "fresh chats select distinct stable subfolders without creating folders or starting at the shared root");
        check(ConversationWorkingDirectory.DefaultForChat(projects, "CON").StartsWith(
            Path.Combine(projects, "Source", "chat-"), StringComparison.Ordinal) &&
            ConversationWorkingDirectory.DefaultForChat(projects, "Case") !=
            ConversationWorkingDirectory.DefaultForChat(projects, "case"),
            "non-GUID chat identities cannot become reserved Windows names or collide through case folding");
        reject(() => ConversationWorkingDirectory.DefaultForChat(projects, "../escape"),
            "chat folder selection rejects traversal identifiers");
        reject(() => ConversationWorkingDirectory.DefaultForChat("relative", chat),
            "chat folder selection requires an absolute Projects root");
        var shared = ConversationPathGrants.Scope(first, [], projects);
        ScopedPathAccessPolicy.Require(second, shared, ScopedPathAccess.Write);
        check(shared.Grants.Single() == new ScopedPathGrant(projects, ScopedPathAccess.Write),
            "the shared root grants access across chat folders without assigning it as the working directory");
        reject(() => ScopedPathAccessPolicy.Require(external, shared, ScopedPathAccess.Read),
            "the Core default does not admit paths outside Projects");
        var readOnly = ConversationPathGrants.Scope(first, [new(projects, ScopedPathAccess.Read)], projects);
        ScopedPathAccessPolicy.Require(first, readOnly, ScopedPathAccess.Read);
        reject(() => ScopedPathAccessPolicy.Require(first, readOnly, ScopedPathAccess.Write),
            "an explicit read-only Core root is not overridden by the chat's starting directory");
        var source = Path.Combine(projects, "Source");
        var nested = ConversationPathGrants.Scope(first, [new(source, ScopedPathAccess.Read)], projects);
        reject(() => ScopedPathAccessPolicy.Require(first, nested, ScopedPathAccess.Write),
            "an intermediate read-only exception also restricts the chat starting folder");
        ScopedPathAccessPolicy.Require(Path.Combine(projects, "SharedCode"), nested, ScopedPathAccess.Write);
        check(nested.Grants.Count == 2, "a nested restriction preserves shared access to unrelated siblings");
        var extra = ConversationPathGrants.Scope(first, [new(external, ScopedPathAccess.Read)], projects);
        ScopedPathAccessPolicy.Require(external, extra, ScopedPathAccess.Read);
        reject(() => ScopedPathAccessPolicy.Require(external, extra, ScopedPathAccess.Write),
            "explicit outside references keep their selected read-only mode");
        var helper = new AgentProfile { WorkerId = "local", AllowedRoots = [] };
        check(ConversationPathGrants.ProfileScope(helper, projects).Grants.Single().Path == projects &&
            helper.AllowedRoots.Count == 0,
            "a local helper receives the Core ceiling without mutating an accepted profile snapshot");
        helper.WorkerId = "remote";
        check(ConversationPathGrants.ProfileScope(helper, projects).Grants.Count == 0,
            "a remote Worker never inherits this computer's local Projects path");
    }

    private static async Task OwnerRoutes(Fixture fixture, Action<bool, string> check)
    {
        await using var models = new ModelRuntime(fixture.State, fixture.Configuration.Get, fixture.Events);
        await using var actualTools = new ToolDispatcher(fixture.Configuration.Get);
        using var scheduler = new JobScheduler(fixture.State, fixture.Configuration, new NoAgent(), models, fixture.Events, NullLogger<JobScheduler>.Instance, fixture.Host);
        var dispatcher = new CommandDispatcher(fixture.State, fixture.Configuration, models, scheduler, actualTools, fixture.Events, recordedTools: fixture.Tools, workflows: fixture.Host);
        Task<CommandResult> Command(string operation, JsonElement args, string? actor = null) => dispatcher.ExecuteAsync(new(operation, args, actor));
        var current = fixture.Paths.Get("path-chat");
        check((await Command("conversations.paths.get", Data(new { id = current.Id }))).Success, "the exact owner path-get route is available");
        var saved = await Command("conversations.paths.save", Data(new { id = current.Id, expectedRevision = current.Revision,
            grants = new[] { new { path = fixture.ExternalRead, access = "read" } } }));
        check(saved.Success && saved.Data.GetProperty("revision").GetInt64() == current.Revision + 1, "owner path-save returns one exact CAS revision");
        fixture.State.Put("agent", "paths-admin", new AgentProfile { Id = "paths-admin", Capabilities = ["administration"] });
        foreach (var actor in new[] { fixture.Profile.Id, "paths-admin" })
            check(!(await Command("conversations.paths.get", Data(new { id = current.Id }), actor)).Success &&
                !(await Command("conversations.paths.save", Data(new { id = current.Id, expectedRevision = current.Revision + 1, grants = Array.Empty<object>() }), actor)).Success,
                "normal and administration agents cannot read or edit owner path grants: " + actor);
        foreach (var raw in new[]
        {
            """{"id":"path-chat","id":"path-chat","expectedRevision":6,"grants":[]}""",
            """{"id":"path-chat","expectedRevision":6,"grants":[{"path":"C:\\fixture","access":1}]}""",
            """{"id":"path-chat","expectedRevision":6,"grants":[{"path":"C:\\fixture","access":"read","access":"write"}]}""",
            """{"id":"path-chat","expectedRevision":6,"grants":[{"path":"C:\\fixture","access":"read","root":true}]}""",
            """{"id":"path-chat","expectedRevision":6,"grants":[],"profileId":"other"}"""
        }) check(!(await Command("conversations.paths.save", Parse(raw))).Success, "strict path route rejects duplicate, integer or unsupported fields");
        check(fixture.Paths.Get(current.Id).Revision == current.Revision + 1 && fixture.Models.Preparations == 0,
            "rejected path routes preserve saved revision and never execute a model");
    }

    private static JsonElement Data(object? value) => CerebralJson.Element(value);
    private static JsonElement Parse(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cerebral-chat-paths-" + Guid.NewGuid().ToString("N"));
        public string ProjectRoot { get; }
        public string ExternalRead { get; }
        public string ExternalWrite { get; }
        public string NestedRead { get; }
        public string NestedWrite { get; }
        public string NewExternal { get; }
        public StateStore State { get; private set; } = null!;
        public Configuration Configuration { get; private set; } = null!;
        public EventHub Events { get; } = new();
        public WorkerWorkflows Host { get; private set; } = null!;
        public ConversationPathGrants Paths { get; private set; } = null!;
        public ScopeTools Tools { get; } = new();
        public NoModels Models { get; } = new();
        public AgentProfile Profile { get; }
        public AgentProfile Helper { get; }
        public IWorkflowCommandHandler ToolHandler { get; private set; } = null!;
        public Fixture()
        {
            ProjectRoot = Path.Combine(root, "project"); ExternalRead = Path.Combine(root, "reference"); ExternalWrite = Path.Combine(root, "output");
            NestedRead = Path.Combine(ProjectRoot, "readonly"); NestedWrite = Path.Combine(NestedRead, "writable"); NewExternal = Path.Combine(root, "unaccepted");
            foreach (var path in new[] { ProjectRoot, ExternalRead, ExternalWrite, NestedRead, NestedWrite, NewExternal }) Directory.CreateDirectory(path);
            Profile = new() { Id = "paths-owner", Role = "lead", Revision = 1, FollowsDefaultModel = true, AutomaticCompaction = false,
                AllowedRoots = [ProjectRoot], AllowedTools = ["file.read", "file.write"], Capabilities = ["filesystem.read", "filesystem.write", "agents.delegate"] };
            Helper = new() { Id = "paths-helper", Role = "helper", Revision = 1, FollowsDefaultModel = true, AutomaticCompaction = false,
                AllowedRoots = [NewExternal], AllowedTools = ["file.read", "file.write"], Capabilities = ["filesystem.read", "filesystem.write"] };
            Restart(); State.Put("agent", Profile.Id, Profile);
            State.Put("navigation", "sidebar", new NavigationState { Nodes = [new() { Id = "project", Kind = "project", Path = ProjectRoot, Title = "Fixture project" }] });
        }
        public void Restart()
        {
            State = new(Path.Combine(root, "data")); Configuration = new(State); Configuration.Get();
            var effects = new WorkerWorkflowEffects(State, Configuration, Models, Tools, workflowHost: () => Host);
            Host = new(State, Configuration, Events, [new ConversationWorkflowModule(), effects],
                [ConversationWorkflowModule.PackageBinding, new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])], Tools);
            Paths = new(State, Host, Events); ToolHandler = effects.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.ToolCommand);
        }
        public ConversationPathGrants.Grant[] Rules() => [new(ExternalRead, "read"), new(ExternalWrite, "write"), new(NestedRead, "read"), new(NestedWrite, "write")];
        public SubmitWorkflowRequest Request(string id, string conversationId, string profileId, JsonElement input) =>
            new(id, conversationId, profileId, ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion, "Fixture owner request", null, input);
        public Task<WorkflowRun> Owner(string id, string conversationId, bool bound = true) => Host.SubmitAsync(Request(id, conversationId, Profile.Id,
            bound ? Data(new { workingDirectory = ProjectRoot, projectId = "project" }) : WorkflowContract.EmptyObject), null, default);
        public WorkflowCommandContext Context(WorkflowRun run, string tool, string path, string? effect = null) =>
            new(effect ?? "paths-effect-" + Guid.NewGuid().ToString("N"), run.Submission, Data(new WorkerWorkflowEffects.ToolArguments(tool, Data(new { path }), ProjectRoot))) { RunId = run.Id };
        public Task<WorkflowCommandResult> Effect(WorkflowRun run, string tool, string path) => ToolHandler.ExecuteAsync(Context(run, tool, path), default);
        public void Finish(WorkflowRun run) { var claimed = Host.Store.TryClaim(run.Id)!; Host.Store.ApplyStep(run.Id, claimed.Revision, new(claimed.State, new CompleteWorkflow(WorkflowContract.EmptyObject))); }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
    private sealed class ScopeTools : IToolDispatcher
    {
        public bool FullAccessActive { get; set; }
        public int Dispatches, Reconciliations;
        public Action? OnList;
        public ToolExecutionContext? LastContext;
        public TaskCompletionSource? DiscoveryEntered, DiscoveryRelease;
        public async Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token)
        {
            OnList?.Invoke(); DiscoveryEntered?.TrySetResult();
            if (DiscoveryRelease is { } release) await release.Task.WaitAsync(token);
            return [new("file.read", "Fixture read", Data(new { type = "object" }), "filesystem.read"),
                new("file.write", "Fixture write", Data(new { type = "object" }), "filesystem.write")];
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); LastContext = context;
            try { ScopedFilePolicy.PathInScope(call.Arguments.GetProperty("path").GetString()!, context,
                call.Name == "file.read" ? ScopedPathAccess.Read : ScopedPathAccess.Write); }
            catch (UnauthorizedAccessException) { return Task.FromResult(new ToolResult(false, "Fixture path denied")
                { Evidence = new(ToolExecutionOutcome.Rejected, "fixture.paths", context.EffectId) }); }
            Dispatches++; return Task.FromResult(new ToolResult(true, "Fixture scope admitted")
                { Evidence = new(ToolExecutionOutcome.Succeeded, "fixture.paths", context.EffectId) });
        }
        public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context, ToolExecutionEvidence? evidence, CancellationToken token)
        { Reconciliations++; return Task.FromResult(new ToolResult(true, "Fixture retained status") { Evidence = evidence! with { Outcome = ToolExecutionOutcome.Succeeded } }); }
    }
    private sealed class NoModels : IModelRuntimeAdapter
    {
        public int Preparations;
        public ModelAdapterDescriptor Descriptor => new("fixture.paths", "1.0.0", ["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        { Preparations++; throw new InvalidOperationException("Path fixtures must not prepare a model."); }
    }
    private sealed class NoAgent : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Path fixtures must not run an agent.");
    }
}
