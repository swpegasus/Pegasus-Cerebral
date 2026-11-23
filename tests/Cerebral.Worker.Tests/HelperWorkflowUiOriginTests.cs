using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Isolated durable-run/path/package metadata fixtures; no desktop, model or provider execution.</summary>
public static class HelperWorkflowUiOriginTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Helper UI origin: " + name);
            checks++; Console.WriteLine("PASS: " + name);
        }
        bool Refused(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or IOException or KeyNotFoundException) { return true; }
        }

        using (var f = new Fixture())
        {
            var instructions = new SystemInstructions(f.Store);
            var defaults = instructions.Get("helper");
            Check(defaults.Content.Contains("direct user assignment in your own conversation", StringComparison.Ordinal) &&
                  defaults.Content.Contains("delegated assignment", StringComparison.Ordinal) &&
                  defaults.Content.Contains("nested delegation is unavailable", StringComparison.Ordinal) &&
                  defaults.Content.Contains("Report no unperformed check as passed", StringComparison.Ordinal),
                "default helper guidance distinguishes direct and delegated assignments without granting more authority");
            var custom = "Fixture-owned custom helper guidance; preserve exactly.";
            instructions.Save(custom, defaults.Sha256, "helper");
            Check(new SystemInstructions(f.Store).Get("helper").Content == custom,
                "a saved custom helper override remains exact instead of being rewritten by the new default");

            var context = f.Dispatch();
            var scoped = WorkflowUiOrigin.ResolvePresentation(f.Store, context, "plugin", f.Manifest, null, true);
            Check(scoped.Profile.Role == "helper" && scoped.Profile.WorkerId == "local" &&
                  scoped.Profile.ModelWorkerId == "fixture-remote-model" && scoped.EnforceProfileScope &&
                  scoped.PathAccess is not null && !scoped.FullAccess,
                "root helper uses real accepted/current local execution and scoped paths independently of its model worker");
            Check(Refused(() => WorkflowUiOrigin.ResolvePresentation(f.Store,
                      context with { EffectId = Id() }, "plugin", f.Manifest, null, true)) &&
                  Refused(() => WorkflowUiOrigin.ResolvePresentation(f.Store,
                      context with { JobId = Id() }, "plugin", f.Manifest, null, true)) &&
                  Refused(() => WorkflowUiOrigin.ResolvePresentation(f.Store,
                      context with { SessionId = Id() }, "plugin", f.Manifest, null, true)),
                "invented effect, run and conversation cannot borrow a root helper's presentation authority");
            Check(Refused(() => WorkflowUiOrigin.ResolvePresentation(f.Store, context,
                      "plugin", f.OutsideManifest, null, true)),
                "presentation arguments cannot be redirected after exact effect admission");
            Check(Refused(() => f.Requests.CreateForWorkflow(context with
                  { PathAccess = new(ScopedPathAccessPolicy.Version, []), EnforceProfileScope = true }, "plugin", f.Manifest)),
                "an already narrower actual plugin context remains a ceiling after durable origin reconstruction");
            var narrowerProfile = Copy(context.Profile);
            narrowerProfile.AllowedRoots = [Path.GetDirectoryName(f.OutsideManifest)!];
            Check(Refused(() => f.Requests.CreateForWorkflow(context with
                  { Profile = narrowerProfile, PathAccess = null }, "plugin", f.Manifest)),
                "a supplied narrower profile read-root ceiling survives plugin origin reconstruction independently of PathAccess");
            var queued = f.Requests.CreateForWorkflow(context, "plugin", f.Manifest);
            Check(queued.Status == "pending" && queued.Source == "workflow" &&
                  queued.ProfileId == f.Profile.Id && f.Requests.List(f.Conversation).Count == 1,
                "a granted root helper queues exactly one plugin review without claiming native presentation");
            Check(f.Requests.CreateForWorkflow(context, "plugin", f.Manifest).Id == queued.Id,
                "the same dispatched request recovers the same pending review");
            f.Finish(context.JobId);
            var renamed = Copy(f.Profile); renamed.Name = "Harmless display edit"; renamed.Revision++;
            f.Store.Put("agent", renamed.Id, renamed);
            var claim = f.Requests.Claim(queued.Id, Id());
            Check(claim.Claimed && !f.Requests.Claim(queued.Id, Id()).Claimed,
                "completed presenter remains claimable once after harmless profile edits; claimed delivery never replays");
            var failed = f.Requests.Complete(queued.Id, claim.Request.ClaimId!, false,
                "Fixture-only owner review failure.", failureStage: InterfaceFailureStage.NativeLoad);
            var retry = f.Requests.RetryPlugin(failed.Id);
            Check(retry.Id != failed.Id && retry.Status == "pending" &&
                  f.Requests.RetryPlugin(failed.Id).Id == retry.Id &&
                  f.Requests.GetForOwner(failed.Id).Status == "failed",
                "explicit owner retry preserves the failed receipt and deduplicates its replacement review");
            Check(f.Requests.GetForContext(failed.Id, context).Id == failed.Id,
                "authenticated receipt reads remain available after the original effect and run finish");
            var revoked = Copy(renamed); revoked.AllowedTools.Remove(InterfaceToolExtension.PluginToolName);
            f.Store.Put("agent", revoked.Id, revoked);
            Check(!f.Requests.Claim(retry.Id, Id()).Claimed &&
                  f.Requests.GetForOwner(retry.Id).Status == "failed",
                "current presentation-tool revocation prevents later delivery without widening the frozen grants");
        }

        using (var f = new Fixture())
        {
            var context = f.Dispatch();
            var finishedEffect = f.Workflows.CompleteEffect(context.JobId, context.EffectId!,
                new(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject));
            Check(finishedEffect.Status == WorkflowStatus.Queued,
                "actual Engine effect completion queues the fixture before its next decision");
            var next = WorkflowUiOriginTestRuns.Live(f.Store, context,
                new(InterfaceToolExtension.PluginToolName, CerebralJson.Element(new { path = f.Manifest })));
            Check(next.EffectId != context.EffectId && f.Workflows.Get(context.JobId) is
                { Status: WorkflowStatus.Running, Effect: { State: WorkflowEffectState.Dispatched } },
                "shared live fixture reclaims the queued revision before preparing a distinct next effect");
            f.Finish(context.JobId);
            Check(f.Workflows.Get(context.JobId).Status == WorkflowStatus.Completed,
                "shared finish reclaims the confirmed effect before an ordinary completion step");
        }
        using (var f = new Fixture())
        {
            var context = f.Dispatch();
            var unknown = f.Workflows.CompleteEffect(context.JobId, context.EffectId!,
                new(WorkflowEffectState.Unknown, WorkflowContract.EmptyObject, "Fixture-only unconfirmed effect."));
            Check(Refused(() => f.Finish(context.JobId)) &&
                  f.Workflows.Get(context.JobId).Revision == unknown.Revision &&
                  f.Workflows.Get(context.JobId).Effect!.State == WorkflowEffectState.Unknown,
                "shared finish refuses an uncertain waiting effect without reclaim, overwrite or replay");
        }

        foreach (var prerequisite in new[] { "file.read", "process.run" })
        {
            using var f = new Fixture();
            f.Profile.AllowedTools.Remove(prerequisite); f.Save();
            var context = f.Dispatch();
            Check(Refused(() => f.Requests.CreateForWorkflow(context, "plugin", f.Manifest)) &&
                  f.Requests.List(f.Conversation).Count == 0,
                "real catalog capture cannot restore a configured missing presentation prerequisite: " + prerequisite);
        }
        using (var f = new Fixture())
        {
            var settings = new HostSettings(); f.Store.Put("settings", "host", settings);
            foreach (var id in new[] { "first-fixture-lead", "second-fixture-lead" })
            {
                var lead = Copy(f.Profile); lead.Id = id; lead.Role = "lead";
                f.Store.Put("agent", id, lead);
            }
            var before = CerebralJson.Element(f.Store.List<AgentProfile>("agent")).GetRawText();
            WorkflowUiOriginTestRuns.Metadata(f.Store, new(f.Profile, Id(), Id(), f.Project));
            Check(CerebralJson.Element(f.Store.List<AgentProfile>("agent")).GetRawText() == before &&
                  f.Store.Get<HostSettings>("settings", "host")!.DefaultModelId == "",
                "metadata capture with two fixture leads never runs default-model migration or changes profile revisions");
        }

        foreach (var invalid in new[] { "compactor", "remote-execution", "service", "child", "cancelled", "prepared" })
        {
            using var f = new Fixture();
            if (invalid == "compactor") { f.Profile.Role = "compactor"; f.Save(); }
            if (invalid == "remote-execution") { f.Profile.WorkerId = "remote"; f.Save(); }
            var caller = invalid == "child" ? new EventActor(EventActorKind.Service, "cerebral.coordination") :
                invalid == "service" ? new EventActor(EventActorKind.Service, "fixture-unapproved") : null;
            var context = f.Dispatch(caller: caller, child: invalid == "child", dispatch: invalid != "prepared");
            if (invalid == "cancelled") f.Workflows.Cancel(context.JobId);
            if (invalid == "child")
            {
                var admitted = f.Workflows.Get(context.JobId);
                var parent = f.Workflows.Get(admitted.Submission.ParentRunId!);
                Check(admitted.Status == WorkflowStatus.Running &&
                      admitted.Effect?.State == WorkflowEffectState.Dispatched &&
                      admitted.Submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.coordination" } &&
                      !parent.IsTerminal && !parent.CancelRequested && parent.Submission.ParentRunId is null &&
                      parent.Submission.ConversationId != admitted.Submission.ConversationId &&
                      parent.Submission.ProjectId == admitted.Submission.ProjectId,
                    "host coordination admits a real dispatched child of a live distinct root before UI refusal");
            }
            Check(Refused(() => f.Requests.CreateForWorkflow(context, "plugin", f.Manifest)) &&
                  f.Requests.List(f.Conversation).Count == 0,
                "unqualified origin or effect refuses before UI persistence: " + invalid);
        }

        using (var f = new Fixture())
        {
            var outside = f.Dispatch(path: f.OutsideManifest);
            Check(Refused(() => f.Requests.CreateForWorkflow(outside with { FullAccess = true,
                      PathAccess = new(ScopedPathAccessPolicy.Version,
                          [new(f.Profile.AllowedRoots.Single(), ScopedPathAccess.Write)]) }, "plugin", f.OutsideManifest)),
                "a package inside profile roots but outside the accepted conversation ceiling is refused");
        }
        using (var f = new Fixture(fullAccess: true))
        {
            var context = f.Dispatch();
            Check(WorkflowUiOrigin.ResolvePresentation(f.Store, context, "plugin", f.Manifest, null, true).FullAccess &&
                  !WorkflowUiOrigin.ResolvePresentation(f.Store, context, "plugin", f.Manifest, null, false).FullAccess &&
                  !WorkflowUiOrigin.ResolvePresentation(f.Store, context with { FullAccess = false },
                      "plugin", f.Manifest, null, true).FullAccess,
                "workflow Full Access retains the existing accepted-and-current host conjunction");
        }
        using (var f = new Fixture())
        {
            f.Profile.Role = "lead"; f.Profile.AllowedTools.Add(InterfaceToolExtension.ToolName); f.Save();
            var context = f.Dispatch(path: f.OutsideManifest, kind: "file");
            Check(Refused(() => f.Requests.CreateForWorkflow(context, "file", f.OutsideManifest)) &&
                  f.Requests.List(f.Conversation).Count == 0,
                "ordinary lead file presentation retains its narrower workflow read ceiling before legacy kind validation");
        }
        using (var f = new Fixture())
        {
            f.Profile.Role = "lead"; f.Profile.AllowedTools.Add(InterfaceToolExtension.ToolName); f.Save();
            var context = f.Dispatch(kind: "file") with
                { PathAccess = new(ScopedPathAccessPolicy.Version, []), EnforceProfileScope = true };
            Check(Refused(() => f.Requests.CreateForWorkflow(context, "file", f.Manifest)) &&
                  f.Requests.List(f.Conversation).Count == 0,
                "an already narrower actual ordinary workflow context is preserved through legacy-kind validation");
        }
        using (var f = new Fixture())
        {
            f.Profile.Role = "lead"; f.Profile.AllowedTools.Add(InterfaceToolExtension.ToolName); f.Save();
            var context = f.Dispatch(kind: "file");
            var narrower = Copy(context.Profile); narrower.AllowedRoots = [Path.GetDirectoryName(f.OutsideManifest)!];
            Check(Refused(() => f.Requests.CreateForWorkflow(context with
                  { Profile = narrower, PathAccess = null }, "file", f.Manifest)) &&
                  f.Requests.List(f.Conversation).Count == 0,
                "ordinary workflow presentation retains an independent supplied profile read-root ceiling");
        }

        using (var f = await PluginReviewContinuationTests.Fixture.CreateAsync(
            acceptedProfile: profile => profile.Role = "helper"))
        {
            var reviewed = f.Present(f.Origin.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var retained = f.Read(reviewed.Id);
            Check(retained.PluginTests is { Status: "admitted", RunId: not null },
                "a granted root helper's exact owner-approved generation admits the ordinary bounded QA continuation");
            var qa = f.Workflows.Store.Get(retained.PluginTests!.RunId!);
            var context = new ToolExecutionContext(f.Profile, f.ConversationId, qa.Id, f.Project, EffectId: Id());
            Check(f.Presentations.GetForContext(reviewed.Id, context).Id == reviewed.Id &&
                  f.Presentations.ResolvePresentationReference(InterfaceRequests.ReviewedReference, context) == reviewed.Id,
                "QA literal and concrete receipt IDs share exact approved durable lineage");
            var foreign = new InterfaceRequest { Source = "workflow", SessionId = f.ConversationId,
                JobId = f.Origin.Id, ProfileId = f.Profile.Id, Kind = "plugin", Path = reviewed.Path,
                Status = "completed", Plugin = reviewed.Plugin, PluginTests = reviewed.PluginTests };
            f.Store.Put("interface", foreign.Id, foreign);
            Check(Refused(() => f.Presentations.GetForContext(foreign.Id, context)) &&
                  Refused(() => WorkflowUiOrigin.ResolveRead(f.Store, context,
                      InterfaceToolExtension.StatusToolName, "filesystem.read", rootOnly: true)),
                "QA cannot select another same-conversation receipt or inherit root-only installed management");
            var forged = context with { JobId = InterfaceRequests.PluginTestPrefix + Id() };
            Check(Refused(() => f.Presentations.GetForContext(reviewed.Id, forged)),
                "a service-looking QA request ID without its durable admission grants nothing");
        }
        Console.WriteLine("Helper workflow UI origin: " + checks +
            " isolated SQLite/durable-run/scoped-package checks passed; no model, provider, desktop or native execution.");
    }

    private static string Id() => Guid.NewGuid().ToString("N");
    private static T Copy<T>(T value) => CerebralJson.Read<T>(CerebralJson.Element(value));

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Cerebral.HelperUiOrigin-" + Id());
        private readonly bool fullAccess;
        public string Project { get; }
        public string Manifest { get; }
        public string OutsideManifest { get; }
        public string Conversation { get; } = Id();
        public AgentProfile Profile { get; }
        public StateStore Store { get; }
        public WorkflowStore Workflows { get; }
        public InterfaceRequests Requests { get; }
        public Fixture(bool fullAccess = false)
        {
            this.fullAccess = fullAccess;
            Project = Path.Combine(root, "project"); Directory.CreateDirectory(Project);
            Manifest = Package(Project); OutsideManifest = Package(Path.Combine(root, "outside"));
            Store = new(Path.Combine(root, "state"));
            Workflows = new(new EventJournal(Path.Combine(Store.DirectoryPath, "engine.db")));
            Profile = new AgentProfile { Id = "explicit-root-helper", Revision = 7, Role = "helper", WorkerId = "local",
                ModelWorkerId = "fixture-remote-model", AllowedRoots = [root],
                Capabilities = ["filesystem.read", "filesystem.write", "process.execute", "desktop.plugins.manage"],
                AllowedTools = ["file.read", "process.run", InterfaceToolExtension.PluginToolName,
                    InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName] };
            Save(); Requests = new(Store, new EventHub(), () => this.fullAccess);
        }
        public void Save() => Store.Put("agent", Profile.Id, Profile);
        public ToolExecutionContext Dispatch(string? path = null, EventActor? caller = null,
            bool child = false, bool dispatch = true, string kind = "plugin")
        {
            var id = Id(); var empty = WorkflowContract.EmptyObject;
            var tools = WorkflowUiOriginTestRuns.Catalog(Store, Profile);
            var snapshot = CerebralJson.Element(new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version,
                Copy(Profile), "", "", null, tools, Profile.Capabilities.ToArray(), Profile.AllowedTools.ToArray(),
                Profile.AllowedRoots.ToArray(), fullAccess,
                WorkflowUiOriginTestRuns.Routing(Store), [], []));
            snapshot = ConversationPathAuthority.Bind(snapshot, new ConversationPathAuthority.Binding(
                ScopedPathAccessPolicy.Version, id, Conversation, Profile.Id, null, 0,
                ConversationPathGrants.Normalize([new(Project, ScopedPathAccess.Write)]))
                { Initial = new(id, Profile.Id, Profile.Revision, null, Project) });
            string? parent = null;
            if (child)
            {
                var parentContext = new ToolExecutionContext(Profile, Id(), Id(), Project);
                WorkflowUiOriginTestRuns.Live(Store, parentContext,
                    new(InterfaceToolExtension.PluginToolName, CerebralJson.Element(new { path = Manifest })));
                parent = parentContext.JobId;
            }
            var run = Workflows.Submit(new(id, Conversation, Profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Fixture owner request.", null, CerebralJson.Element(new { workingDirectory = Project }), snapshot,
                Caller: caller ?? new(EventActorKind.User, "fixture-owner")) { ParentRunId = parent });
            run = Workflows.TryClaim(run.Id)!;
            var descriptor = new WorkflowCommandDescriptor(WorkerWorkflowEffects.ToolCommand, 1,
                "Fixture effect only.", empty, empty, []);
            var arguments = CerebralJson.Element(new WorkerWorkflowEffects.ToolArguments(
                kind == "plugin" ? InterfaceToolExtension.PluginToolName : InterfaceToolExtension.ToolName,
                kind == "plugin" ? CerebralJson.Element(new { path = path ?? Manifest }) :
                    CerebralJson.Element(new { kind, path = path ?? Manifest })));
            run = Workflows.ApplyStep(run.Id, run.Revision,
                new(empty, new InvokeWorkflowCommand(descriptor.Id, descriptor.Version, arguments)), descriptor);
            if (dispatch) run = Workflows.DispatchEffect(run.Id, run.Revision);
            return new(Copy(Profile), Conversation, id, Project, FullAccess: fullAccess, EffectId: run.Effect!.Id)
                { EnforceProfileScope = true };
        }
        public void Finish(string id) => WorkflowUiOriginTestRuns.Finish(Workflows, id);
        private static string Package(string path)
        {
            Directory.CreateDirectory(path);
            File.Copy(typeof(InterfaceEntryFixture).Assembly.Location, Path.Combine(path, "fixture.dll"));
            var manifest = Path.Combine(path, "desktop-plugin.json");
            File.WriteAllText(manifest, CerebralJson.Element(new { schemaVersion = 1,
                contract = "pegasus.cerebral.desktop", apiVersion = 1, id = "acceptance.helper-origin",
                name = "Helper origin fixture", version = "1.0.0", description = "Never loaded.",
                assembly = "fixture.dll", entryType = typeof(InterfaceEntryFixture).FullName }).GetRawText());
            return manifest;
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            var target = Path.GetFullPath(root);
            if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(target).StartsWith("Cerebral.HelperUiOrigin-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its own temporary root.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}
