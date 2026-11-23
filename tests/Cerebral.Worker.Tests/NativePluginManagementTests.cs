using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;
using Cerebral.Worker;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Isolated SQLite/engine metadata fixtures. No native plugin, model, owner data or Desktop process is used.</summary>
public static class NativePluginManagementTests
{
    private const string Kind = "native-plugin-management";
    private static readonly string CatalogHash = new('a', 64);
    private static readonly string PackageHash = new('b', 64);

    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL native plugin management: " + name);
            checks++;
        }
        bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or
                InvalidOperationException or KeyNotFoundException or JsonException) { return true; }
        }

        using (var f = new Fixture())
        {
            var context = f.Context();
            var origin = f.Host.Store.Get(context.JobId);
            Check(f.Store.GetSessionMetadata(f.SessionId) is null && origin.Status == WorkflowStatus.Completed &&
                origin.Submission.DefinitionId == ConversationWorkflowModule.DefinitionId &&
                origin.Submission.ParentRunId is null && origin.Submission.Caller?.Kind == EventActorKind.User,
                "metadata fixture has a completed native root Conversation without a legacy TaskSession");
            var first = f.Requests.Create(context, "list", "note");
            Check(first.Id == context.EffectId && first.Status == "pending" &&
                first.SessionId == f.SessionId && first.ProfileRevision == f.Profile.Revision &&
                first.ExpiresAt > first.CreatedAt && first.ExpiresAt - first.CreatedAt < TimeSpan.FromSeconds(31),
                "list binds the host effect, exact conversation/revision and bounded delivery deadline");
            var reopened = new NativePluginManagementRequests(new StateStore(f.Store.DirectoryPath), f.Events);
            Check(reopened.Read(context, first.Id, NativePluginManagementRequests.ListTool).Status == "pending",
                "durable pending state survives reopening the isolated SQLite store");
            var subscription = f.Events.Subscribe();
            Check(f.Requests.Create(context, "list", "note").Id == first.Id && !subscription.Reader.TryRead(out _),
                "identical admission reuses its receipt without a second event");
            f.Events.Unsubscribe(subscription.Id);
            Check(Rejects(() => f.Requests.Create(context, "list", "different")) &&
                Rejects(() => f.Requests.Create(context with { JobId = Id() }, "list", "note")),
                "same effect cannot borrow another argument or workflow identity");
            Check(Rejects(() => f.Requests.Create(context with { EffectId = null }, "list")) &&
                Rejects(() => f.Requests.Create(context with { EffectId = Guid.Empty.ToString("N") }, "list")) &&
                Rejects(() => f.Requests.Create(context with { EffectId = "not-an-id" }, "list")),
                "model-selected, absent and empty host effect identifiers are rejected");
            Check(Rejects(() => f.Requests.Read(context, Id(), NativePluginManagementRequests.StatusTool)) &&
                Rejects(() => f.Requests.Read(context, "not-a-receipt", NativePluginManagementRequests.StatusTool)),
                "unknown or fabricated receipt coordinates are not admitted");

            foreach (var mutation in new Action<AgentProfile>[] {
                p => p.Role = "compactor", p => p.WorkerId = "remote",
                p => p.Capabilities.Clear(), p => p.AllowedTools.Clear(), p => p.Revision++ })
            {
                var altered = Copy(f.Profile); mutation(altered);
                Check(Rejects(() => f.Requests.Create(context with { Profile = altered, EffectId = Id() }, "list")) &&
                    Rejects(() => f.Requests.Read(context with { Profile = altered }, first.Id, NativePluginManagementRequests.StatusTool)),
                    "accepted role/locality/grant/revision mismatch blocks both admission and retained read");
                f.Store.Put("agent", altered.Id, altered);
                Check(Rejects(() => f.Requests.Create(f.Context(), "list")) &&
                    Rejects(() => f.Requests.Read(context, first.Id, NativePluginManagementRequests.StatusTool)),
                    "current role/locality/grant/revision revocation defeats stale accepted authority");
                f.Restore();
            }
            var unbound = context with { JobId = Id(), EffectId = Id() };
            Check(Rejects(() => f.Requests.Create(unbound, "list")) &&
                Rejects(() => f.Requests.Read(unbound, first.Id, NativePluginManagementRequests.StatusTool)),
                "matching profile and conversation fields cannot replace a durable workflow origin");
            var other = Copy(f.Profile); other.Id = "other-lead"; f.Store.Put("agent", other.Id, other);
            Check(Rejects(() => f.Requests.Create(context with { Profile = other }, "list", "note")) &&
                Rejects(() => f.Requests.Read(context with { Profile = other }, first.Id, NativePluginManagementRequests.StatusTool)),
                "another configured lead cannot borrow an existing retained effect or receipt");
            Check(Rejects(() => f.Requests.Create(context with { SessionId = Id() }, "list", "note")) &&
                Rejects(() => f.Requests.Read(context with { SessionId = Id() }, first.Id, NativePluginManagementRequests.StatusTool)) &&
                Rejects(() => f.Requests.GetForOwner(first.Id, Id())),
                "foreign conversation cannot replay an existing effect or read its retained receipt");
            var offloaded = Copy(f.Profile); offloaded.ModelWorkerId = "paired-gb10"; f.Store.Put("agent", offloaded.Id, offloaded);
            var offloadedContext = f.Context(offloaded);
            Check(f.Requests.Create(offloadedContext, "list").Status == "pending" &&
                WorkerExecutionSnapshot.Read(f.Host.Store.Get(offloadedContext.JobId).Submission.SettingsSnapshot).Profile.ModelWorkerId == offloaded.ModelWorkerId,
                "local lead authority remains local when its inference model is offloaded");
            f.Restore();
            var wildcard = Copy(f.Profile); wildcard.AllowedTools = ["*"]; f.Store.Put("agent", wildcard.Id, wildcard);
            Check(f.Requests.Create(f.Context(wildcard), "list").Status == "pending",
                "an explicit current wildcard still authorizes the fixed native management tool");
            f.Restore();

            Check(Rejects(() => f.Requests.Claim(first.Id, f.SessionId, "claim", _ => { })) &&
                Rejects(() => f.Requests.Claim(first.Id, Id(), Id(), _ => { })),
                "owner claims require the exact conversation and bounded claim identity");
            var validations = 0;
            var claims = await Task.WhenAll(
                Task.Run(() => f.Requests.Claim(first.Id, f.SessionId, Id(), _ => Interlocked.Increment(ref validations))),
                Task.Run(() => f.Requests.Claim(first.Id, f.SessionId, Id(), _ => Interlocked.Increment(ref validations))));
            var winner = claims.Single(x => x.Claimed).Request;
            Check(claims.Count(x => x.Claimed) == 1 && validations == 1 &&
                !reopened.Claim(first.Id, f.SessionId, Id(), _ => validations++).Claimed,
                "competing owners claim once and a reopened claimed receipt never dispatches again");
            Check(Rejects(() => f.Requests.Ready(first.Id, f.SessionId, Id(), _ => { })) &&
                Rejects(() => f.Requests.Complete(first.Id, f.SessionId, Id(), "completed", Observation(), null)),
                "readiness and completion require the exact winning owner claim");
            Check(f.Requests.Ready(first.Id, f.SessionId, winner.ClaimId!, _ => validations++).Id == first.Id && validations == 2,
                "readiness invokes host preflight while preserving claimed state");
            Check(Rejects(() => f.Requests.Complete(first.Id, f.SessionId, winner.ClaimId!, "completed",
                Observation(changed: ["note.fixture"]), null)),
                "read-only catalog completion cannot claim any mutations");
            var done = f.Requests.Complete(first.Id, f.SessionId, winner.ClaimId!, "completed", Observation(), null);
            subscription = f.Events.Subscribe();
            Check(done.Status == "completed" &&
                f.Requests.Complete(first.Id, f.SessionId, winner.ClaimId!, "completed", Copy(Observation()), null).Status == "completed" &&
                !subscription.Reader.TryRead(out _), "structurally equal terminal replay is idempotent and emits no second outcome");
            f.Events.Unsubscribe(subscription.Id);
            Check(Rejects(() => f.Requests.Complete(first.Id, f.SessionId, winner.ClaimId!, "rejected", null, "other")) &&
                f.Requests.Create(context, "list", "note").Status == "completed",
                "terminal receipts cannot silently change outcome or requeue their effect");
        }

        using (var f = new Fixture())
        {
            var proof = f.Proof();
            var context = f.Context();
            var selection = Selection();
            var change = f.Requests.Create(context, "set-enabled", catalogFingerprint: CatalogHash,
                selection: selection, enabled: false, catalogRequestId: proof.Id);
            Check(change.CatalogRequestId == proof.Id && change.Selection == selection &&
                change.CatalogFingerprint == CatalogHash && change.Enabled == false &&
                change.ExpiresAt - change.CreatedAt > TimeSpan.FromMinutes(9),
                "mutation binds a completed catalog proof and exact generation/package/state");
            Check(Rejects(() => f.Requests.Create(f.Context(), "set-enabled", catalogFingerprint: CatalogHash,
                selection: selection, enabled: false, catalogRequestId: Id())),
                "a fabricated catalog receipt cannot authorize a mutation");
            var pending = f.Requests.Create(f.Context(), "list");
            Check(Rejects(() => f.Requests.Create(f.Context(), "set-enabled", catalogFingerprint: CatalogHash,
                selection: selection, enabled: false, catalogRequestId: pending.Id)),
                "a pending catalog cannot serve as state proof");
            foreach (var bad in new[] { selection with { Id = "invented-plugin" },
                selection with { Generation = "other-generation" }, selection with { PackageFingerprint = new('c', 64) },
                selection with { ExpectedEnabled = false } })
                Check(Rejects(() => f.Requests.Create(f.Context(), "set-enabled", catalogFingerprint: CatalogHash,
                    selection: bad, enabled: false, catalogRequestId: proof.Id)),
                    "invented ID or forged generation/package/expected state cannot be substituted");
            Check(Rejects(() => f.Requests.Create(f.Context(), "set-enabled", catalogFingerprint: new('c', 64),
                selection: selection, enabled: false, catalogRequestId: proof.Id)),
                "catalog fingerprint must match the retained list observation");
            var errorProof = f.Proof(Observation(rows: [Entry() with { Error = "Invalid installed package." }]));
            Check(Rejects(() => f.Requests.Create(f.Context(), "set-enabled", catalogFingerprint: CatalogHash,
                selection: selection, enabled: false, catalogRequestId: errorProof.Id)),
                "invalid installed packages cannot be made selectable by model input");

            var declined = f.Requests.Reject(change.Id, f.SessionId);
            var subscription = f.Events.Subscribe();
            Check(declined.Status == "rejected" && f.Requests.Reject(change.Id, f.SessionId).Status == "rejected" &&
                !subscription.Reader.TryRead(out _) &&
                !f.Requests.Claim(change.Id, f.SessionId, Id(), _ => throw new Exception("Declined changes must not execute.")).Claimed,
                "owner decline is durable, idempotent and never invokes execution");
            f.Events.Unsubscribe(subscription.Id);
            Check(f.Requests.Create(context, "set-enabled", catalogFingerprint: CatalogHash,
                selection: selection, enabled: false, catalogRequestId: proof.Id).Status == "rejected",
                "repeated declined effect cannot request approval again");
            var applied = f.Requests.Create(f.Context(), "set-enabled", catalogFingerprint: CatalogHash,
                selection: selection, enabled: false, catalogRequestId: proof.Id);
            var claim = f.Requests.Claim(applied.Id, f.SessionId, Id(), _ => { }).Request;
            Check(Rejects(() => f.Requests.Reject(applied.Id, f.SessionId)), "claimed delivery cannot be downgraded to an unexecuted owner decline");
            foreach (var row in new[] { Entry() with { Enabled = true, Loaded = false },
                Entry() with { Enabled = false, Loaded = true }, Entry() with { Enabled = false, Loaded = false, Error = "Load failure." },
                Entry() with { Enabled = false, Loaded = false, Generation = "replacement" },
                Entry() with { Enabled = false, Loaded = false, PackageFingerprint = new('c', 64) } })
                Check(Rejects(() => f.Requests.Complete(applied.Id, f.SessionId, claim.ClaimId!, "completed",
                    Observation(rows: [row], changed: ["note.fixture"]), null)),
                    "completion refuses wrong enabled/loaded/error/generation/package evidence");
            Check(Rejects(() => f.Requests.Complete(applied.Id, f.SessionId, claim.ClaimId!, "completed",
                Observation(rows: [], changed: ["note.fixture"]), null)),
                "completion requires the selected installed plugin to be observed");
            var observed = Observation(rows: [Entry() with { Enabled = false, Loaded = false }], changed: ["note.fixture"]);
            Check(f.Requests.Complete(applied.Id, f.SessionId, claim.ClaimId!, "completed", observed, null).Status == "completed",
                "completion accepts exact disabled catalog and unloaded runtime evidence");
        }

        using (var f = new Fixture())
        {
            var pending = f.Requests.Create(f.Context(), "list");
            var current = Copy(f.Profile); current.Revision++; f.Store.Put("agent", current.Id, current);
            Check(!f.Requests.Claim(pending.Id, f.SessionId, Id(), _ => throw new Exception("Stale revision must not deliver.")).Claimed &&
                f.Requests.GetForOwner(pending.Id, f.SessionId).Status == "rejected",
                "profile revision change before claim prevents native delivery");
            f.Restore();
            var revoked = f.Requests.Create(f.Context(), "list");
            var claim = f.Requests.Claim(revoked.Id, f.SessionId, Id(), _ => { }).Request;
            current = Copy(f.Profile); current.AllowedTools.Clear(); f.Store.Put("agent", current.Id, current);
            Check(Rejects(() => f.Requests.Ready(revoked.Id, f.SessionId, claim.ClaimId!, _ => { })),
                "revocation after claim is rechecked immediately before execution");
            f.Restore();
            var cancelContext = f.Context(); var cancel = f.Requests.Create(cancelContext, "list");
            Check(f.Requests.Interrupt(cancelContext, cancel.Id, true).Status == "cancelled" &&
                !f.Requests.Claim(cancel.Id, f.SessionId, Id(), _ => { }).Claimed &&
                f.Requests.Create(cancelContext, "list").Status == "cancelled",
                "cancellation before delivery is durable and never requeues");
            var unknownContext = f.Context(); var unknown = f.Requests.Create(unknownContext, "list");
            var unknownClaim = f.Requests.Claim(unknown.Id, f.SessionId, Id(), _ => { }).Request;
            Check(f.Requests.Interrupt(unknownContext, unknown.Id, true).Status == "uncertain" &&
                !f.Requests.Claim(unknown.Id, f.SessionId, Id(), _ => { }).Claimed &&
                Rejects(() => f.Requests.Ready(unknown.Id, f.SessionId, unknownClaim.ClaimId!, _ => { })),
                "interrupted claimed work is unconfirmed and cannot execute a second time");
            Check(f.Requests.Complete(unknown.Id, f.SessionId, unknownClaim.ClaimId!, "completed", Observation(), null).Status == "completed",
                "truthful late evidence may settle an unconfirmed claim without replay");
            var expiryContext = f.Context(); var expiry = f.Requests.Create(expiryContext, "list"); f.Expire(expiry);
            Check(!f.Requests.Claim(expiry.Id, f.SessionId, Id(), _ => { }).Claimed &&
                (await f.Requests.WaitAsync(expiryContext, expiry.Id, default)).Status == "rejected",
                "expired unclaimed requests have a bounded terminal rejection");
            var waitContext = f.Context(); var wait = f.Requests.Create(waitContext, "list");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check((await f.Requests.WaitAsync(waitContext, wait.Id, cancelled.Token)).Status == "cancelled",
                "cancelled wait terminates before native delivery");
            var afterClaimContext = f.Context(); var afterClaim = f.Requests.Create(afterClaimContext, "list");
            var afterClaimWinner = f.Requests.Claim(afterClaim.Id, f.SessionId, Id(), _ => { }).Request; f.Expire(afterClaimWinner);
            Check(Rejects(() => f.Requests.Ready(afterClaim.Id, f.SessionId, afterClaimWinner.ClaimId!, _ => { })) &&
                (await f.Requests.WaitAsync(afterClaimContext, afterClaim.Id, default)).Status == "uncertain",
                "expiry after claim blocks readiness and retains unknown execution state");
        }
        using (var f = new Fixture())
        {
            var unfinished = Enumerable.Range(0, 8).Select(_ => f.Requests.Create(f.Context(), "list")).ToArray();
            Check(Rejects(() => f.Requests.Create(f.Context(), "list")), "eight unfinished requests enforce the per-conversation admission bound");
            f.Expire(unfinished[0]);
            var replacement = f.Requests.Create(f.Context(), "list");
            Check(replacement.Status == "pending" && f.Requests.GetForOwner(unfinished[0].Id, f.SessionId).Status == "rejected" &&
                f.Store.List<NativePluginManagementRequest>(Kind).Count(x => x.Status is "pending" or "claimed") == 8,
                "expired pending requests retire before the queue bound is evaluated");
        }

        foreach (var bad in new[] {
            new NativePluginManagementResult(null!, []),
            new NativePluginManagementResult(new(CatalogHash, null!), []),
            new NativePluginManagementResult(new(CatalogHash, []), null!),
            Observation(rows: [null!]),
            Observation(rows: [Entry() with { Id = null! }]),
            Observation(rows: [Entry() with { Name = null! }]),
            Observation(rows: [Entry() with { Version = null! }]),
            Observation(rows: Enumerable.Range(0, 101).Select(i => Entry() with { Id = "fixture." + i }).ToArray()),
            Observation(rows: [Entry(), Entry()]), Observation(changed: ["note.fixture", "note.fixture"]),
            Observation(omitted: -1), Observation(hash: "BAD"),
            Observation(rows: [Entry() with { Id = "invalid/id" }]),
            Observation(rows: [Entry() with { Name = new('n', 201) }]),
            Observation(rows: [Entry() with { Version = new('v', 101) }]),
            Observation(rows: [Entry() with { Generation = new('g', 129) }]),
            Observation(rows: [Entry() with { PackageFingerprint = new('B', 64) }]),
            Observation(rows: [Entry() with { Error = "line\nbreak" }]),
            Observation(changed: ["invalid/id"]),
            Observation(rows: Enumerable.Range(0, 100).Select(i => Entry() with { Id = "large." + i, Error = new('e', 1000) }).ToArray())
        })
            Check(Rejects(() => NativePluginManagementRequests.ValidateResult(bad)),
                "invalid row identity, duplicate entries, inconsistent limits or oversized result is rejected");
        Check(!Rejects(() => NativePluginManagementRequests.ValidateResult(
            Observation(rows: Enumerable.Range(0, 100).Select(i => Entry() with { Id = "bounded." + i }).ToArray()))),
            "100 bounded path-free catalog metadata rows remain supported");
        Check(Rejects(() => NativePluginManagementRequests.ValidateInput("list", new('q', 101), null, null, null)) &&
            Rejects(() => NativePluginManagementRequests.ValidateInput("list", "bad\nquery", null, null, null)) &&
            Rejects(() => NativePluginManagementRequests.ValidateInput("set-enabled", null, CatalogHash,
                Selection() with { Generation = "bad\ngeneration" }, false)) &&
            Rejects(() => NativePluginManagementRequests.ValidateInput("set-enabled", null, CatalogHash,
                Selection() with { Id = null! }, false)) &&
            Rejects(() => NativePluginManagementRequests.ValidateInput("set-enabled", null, CatalogHash,
                Selection() with { Generation = null! }, false)) &&
            Rejects(() => NativePluginManagementRequests.ValidateInput("unknown", null, null, null, null)),
            "operation and query boundaries reject unknown or oversized/control metadata");

        using (var f = new Fixture())
        {
            var active = f.Executing(NativePluginManagementRequests.ListTool);
            Check(f.Store.GetSessionMetadata(f.SessionId) is null &&
                f.Host.Store.GetConversation(ConversationWorkflowModule.DefinitionId, f.SessionId)?.ProfileId == f.Profile.Id &&
                !Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, active, NativePluginManagementRequests.ListTool)),
                "real native workflow conversation owns its dispatched effect without a legacy TaskSession");
            var foreignLead = Copy(f.Profile); foreignLead.Id = "foreign-native-lead"; f.Store.Put("agent", foreignLead.Id, foreignLead);
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, active with { Profile = foreignLead },
                NativePluginManagementRequests.ListTool)),
                "native durable conversation profile binding rejects another configured lead without legacy metadata");
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, active with { EffectId = Id() },
                NativePluginManagementRequests.ListTool)), "another effect cannot borrow live workflow execution");
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, active with { SessionId = Id() },
                NativePluginManagementRequests.ListTool)), "another conversation cannot borrow live workflow execution");
            var wrongRevision = Copy(f.Profile); wrongRevision.Revision++;
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, active with { Profile = wrongRevision },
                NativePluginManagementRequests.ListTool)), "accepted workflow profile revision is frozen");
            var old = f.Executing(NativePluginManagementRequests.ListTool, includeManagement: false);
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, old, NativePluginManagementRequests.ListTool)),
                "old accepted snapshots cannot inherit newly registered management tools");
            var wrongAcceptedIdentity = f.Executing(NativePluginManagementRequests.ListTool,
                snapshotChange: snapshot => { var p = Copy(snapshot.Profile); p.Id = "foreign-accepted-profile"; return snapshot with { Profile = p }; });
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, wrongAcceptedIdentity, NativePluginManagementRequests.ListTool)),
                "foreign profile identity inside accepted settings cannot borrow the current root submission");
            var noAllowedTools = f.Executing(NativePluginManagementRequests.ListTool,
                snapshotChange: snapshot => snapshot with { AllowedTools = [] });
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, noAllowedTools, NativePluginManagementRequests.ListTool)),
                "accepted tool descriptor cannot override a missing accepted allow-list grant");
            var wrongCommand = f.Executing(NativePluginManagementRequests.ListTool, effectCommand: "fixture.foreign-command");
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, wrongCommand, NativePluginManagementRequests.ListTool)),
                "matching effect identity under another engine command does not authorize a management tool");
            var wrongVersion = f.Executing(NativePluginManagementRequests.ListTool, effectVersion: 2);
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, wrongVersion, NativePluginManagementRequests.ListTool)),
                "another engine command version does not authorize a management tool");
            var wrongTool = f.Executing(NativePluginManagementRequests.ListTool, effectToolName: NativePluginManagementRequests.SetTool);
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, wrongTool, NativePluginManagementRequests.ListTool)),
                "dispatched engine tool name must match the management call");
            var prepared = f.Executing(NativePluginManagementRequests.ListTool, dispatch: false);
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, prepared, NativePluginManagementRequests.ListTool)),
                "prepared but undispatched effects cannot deliver a management operation");
            var noCapability = f.Executing(NativePluginManagementRequests.ListTool,
                snapshotChange: snapshot => snapshot with { Capabilities = [] });
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, noCapability, NativePluginManagementRequests.ListTool)),
                "accepted capabilities independently gate the new management tool");
            var compactor = f.Executing(NativePluginManagementRequests.ListTool,
                snapshotChange: snapshot => { var p = Copy(snapshot.Profile); p.Role = "compactor"; return snapshot with { Profile = p }; });
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, compactor, NativePluginManagementRequests.ListTool)),
                "accepted compactor context cannot use installed management");
            var remote = f.Executing(NativePluginManagementRequests.ListTool,
                snapshotChange: snapshot => { var p = Copy(snapshot.Profile); p.WorkerId = "paired"; return snapshot with { Profile = p }; });
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, remote, NativePluginManagementRequests.ListTool)),
                "accepted remote execution cannot use this Desktop owner bridge");
            foreach (var caller in new EventActor?[] { null, new(EventActorKind.Agent, "another-agent"), new(EventActorKind.Service, "fixture.service") })
            {
                var foreign = f.Executing(NativePluginManagementRequests.ListTool, caller: caller, replaceCaller: true);
                Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, foreign, NativePluginManagementRequests.ListTool)),
                    "service, agent and absent callers cannot borrow the user-owned root bridge");
            }
            var child = f.Executing(NativePluginManagementRequests.ListTool, parentRunId: Id());
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, child, NativePluginManagementRequests.ListTool)),
                "delegated child workflows cannot use installed plugin owner management");
            f.Host.Store.Cancel(active.JobId);
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, active, NativePluginManagementRequests.ListTool)),
                "cancelled durable workflow blocks delivery even before the retained request catches up");
            var unknown = f.Executing(NativePluginManagementRequests.ListTool);
            f.Host.Store.CompleteEffect(unknown.JobId, unknown.EffectId!, new(WorkflowEffectState.Unknown, WorkflowContract.EmptyObject, "Fixture response lost."));
            Check(Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, unknown, NativePluginManagementRequests.ListTool)),
                "inactive unconfirmed engine effects cannot dispatch a new owner operation");
        }

        using (var f = new Fixture())
        {
            var helper = Copy(f.Profile); helper.Role = "helper"; f.Store.Put("agent", helper.Id, helper);
            var context = f.Executing(NativePluginManagementRequests.ListTool, acceptedProfile: helper);
            var accepted = WorkerExecutionSnapshot.Read(f.Host.Store.Get(context.JobId).Submission.SettingsSnapshot);
            Check(accepted.Profile.Role == "helper" &&
                !Rejects(() => NativePluginManagementRequests.RequireExecuting(f.Host, context, NativePluginManagementRequests.ListTool)),
                "a genuinely accepted local root helper retains its explicit installed-management grants");
            var request = f.Requests.Create(context, "list");
            Check(request.Status == "pending" &&
                f.Requests.Read(context, request.Id, NativePluginManagementRequests.StatusTool).Id == request.Id,
                "root helper admission and retained reads use the same durable conversation origin");
        }

        using (var f = new Fixture())
        {
            var listContext = f.Executing(NativePluginManagementRequests.ListTool);
            var call = new ToolCall(NativePluginManagementRequests.ListTool, CerebralJson.Element(new { query = "note" }));
            var foreignResult = await f.Tool.ExecuteAsync(call, listContext with { SessionId = Id() }, default);
            Check(!foreignResult.Success && foreignResult.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
                f.Store.List<NativePluginManagementRequest>(Kind).Count == 0,
                "foreign native conversation is rejected by the actual tool before any receipt or owner delivery");
            var task = f.Tool.ExecuteAsync(call, listContext, default);
            var queued = f.Requests.GetForOwner(listContext.EffectId!, f.SessionId);
            void ValidateNativeOwner(NativePluginManagementRequest _) =>
                NativePluginManagementRequests.RequireExecuting(f.Host, listContext, NativePluginManagementRequests.ListTool);
            var owner = f.Requests.Claim(queued.Id, f.SessionId, Id(), ValidateNativeOwner).Request;
            Check(owner.Status == "claimed" && f.Store.GetSessionMetadata(f.SessionId) is null &&
                f.Requests.Ready(queued.Id, f.SessionId, owner.ClaimId!, ValidateNativeOwner).Status == "claimed",
                "native owner claim and readiness validate the durable dispatched workflow without creating legacy state");
            var many = Enumerable.Range(0, 30).Select(i => Entry() with { Id = "note." + i }).ToArray();
            f.Requests.Complete(queued.Id, f.SessionId, owner.ClaimId!, "completed", Observation(rows: many, omitted: 2), null);
            var projected = await task;
            using var projection = JsonDocument.Parse(projected.Content);
            Check(projected.Success && projected.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, ProviderId: NativePluginManagementToolExtension.Provider } &&
                projection.RootElement.GetProperty("plugins").GetArrayLength() == 25 &&
                projection.RootElement.GetProperty("omitted").GetInt32() == 7 &&
                !projected.Content.Contains(CatalogHash, StringComparison.Ordinal) &&
                !projected.Content.Contains(PackageHash, StringComparison.Ordinal) &&
                !projected.Content.Contains(f.Root, StringComparison.Ordinal),
                "completed list model projection is compact, 25-row bounded and excludes package fingerprints and paths");
            var setContext = f.Executing(NativePluginManagementRequests.SetTool);
            var setCall = new ToolCall(NativePluginManagementRequests.SetTool,
                CerebralJson.Element(new { catalogRequestId = queued.Id, pluginId = "note.0", enabled = false }));
            var setTask = f.Tool.ExecuteAsync(setCall, setContext, default);
            var set = f.Requests.GetForOwner(setContext.EffectId!, f.SessionId);
            Check(set.Selection is { Id: "note.0", ExpectedEnabled: true } &&
                set.Selection.Generation == Entry().Generation && set.Selection.PackageFingerprint == PackageHash &&
                set.CatalogFingerprint == CatalogHash, "set tool resolves opaque package/state proof internally from the retained catalog");
            f.Requests.Reject(set.Id, f.SessionId);
            var declined = await setTask;
            Check(!declined.Success && declined.Evidence?.Outcome == ToolExecutionOutcome.Rejected,
                "owner decline returns a truthful rejected tool receipt rather than a false completed mutation");

            var disabledProof = f.Proof(Observation(rows: [Entry() with { Enabled = false, Loaded = false }]));
            var enableContext = f.Executing(NativePluginManagementRequests.SetTool);
            var enableTask = f.Tool.ExecuteAsync(new(NativePluginManagementRequests.SetTool,
                CerebralJson.Element(new { catalogRequestId = disabledProof.Id, pluginId = Entry().Id, enabled = true })),
                enableContext, default);
            var enableRequest = f.Requests.GetForOwner(enableContext.EffectId!, f.SessionId);
            var enableClaim = f.Requests.Claim(enableRequest.Id, f.SessionId, Id(), _ => { }).Request;
            Check(enableRequest.Selection?.ExpectedEnabled == false && enableRequest.Enabled == true,
                "enabling binds the previously observed disabled state from the exact catalog receipt");
            f.Requests.Complete(enableRequest.Id, f.SessionId, enableClaim.ClaimId!, "completed",
                Observation(changed: [Entry().Id]), null);
            var enabled = await enableTask;
            Check(enabled.Success && enabled.Evidence?.Outcome == ToolExecutionOutcome.Succeeded &&
                enabled.Evidence.RequestId == enableRequest.Id,
                "successful set tool requires and projects exact enabled and loaded fixture evidence");

            var statusContext = f.Executing(NativePluginManagementRequests.StatusTool);
            var count = f.Store.List<NativePluginManagementRequest>(Kind).Count;
            var subscription = f.Events.Subscribe();
            var status = await f.Tool.ExecuteAsync(new(NativePluginManagementRequests.StatusTool,
                CerebralJson.Element(new { requestId = set.Id })), statusContext, default);
            Check(!status.Success && status.Evidence?.RequestId == set.Id &&
                f.Store.List<NativePluginManagementRequest>(Kind).Count == count && !subscription.Reader.TryRead(out _),
                "status reads retained evidence without queueing, replaying or publishing another action");
            f.Events.Unsubscribe(subscription.Id);
            foreach (var malformed in new[] {
                new ToolCall(NativePluginManagementRequests.SetTool, CerebralJson.Element(new { catalogRequestId = queued.Id, pluginId = "invented", enabled = false })),
                new ToolCall(NativePluginManagementRequests.SetTool, CerebralJson.Element(new { catalogRequestId = Id(), pluginId = "note.0", enabled = false })),
                new ToolCall(NativePluginManagementRequests.SetTool, CerebralJson.Element(new { catalogRequestId = queued.Id, pluginId = "note.0", enabled = "false" })),
                new ToolCall(NativePluginManagementRequests.ListTool, CerebralJson.Element(new { root = f.Root })),
                new ToolCall(NativePluginManagementRequests.ListTool, JsonDocument.Parse("{\"query\":\"a\",\"query\":\"b\"}").RootElement.Clone()),
                new ToolCall(NativePluginManagementRequests.StatusTool, CerebralJson.Element(new { requestId = set.Id, retry = true })),
                new ToolCall(NativePluginManagementRequests.ListTool, CerebralJson.Element(new[] { "list" }))
            })
            {
                var toolContext = f.Executing(malformed.Name);
                var rejected = await f.Tool.ExecuteAsync(malformed, toolContext, default);
                Check(!rejected.Success && rejected.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
                    rejected.Evidence.RequestId is null && f.Store.List<NativePluginManagementRequest>(Kind).Count == count,
                    "malformed fields, arbitrary paths, duplicate fields, guessed IDs and forged proofs never enter durable delivery");
            }
            var pendingContext = f.Executing(NativePluginManagementRequests.ListTool);
            var pending = f.Requests.Create(pendingContext, "list", "pending");
            var pendingCall = new ToolCall(NativePluginManagementRequests.ListTool, CerebralJson.Element(new { query = "pending" }));
            var evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, NativePluginManagementToolExtension.Provider,
                RequestId: pending.Id, State: "unconfirmed");
            var reconciled = await f.Tool.ReconcileAsync(pendingCall, pendingContext, evidence, default);
            Check(!reconciled.Success && f.Requests.GetForOwner(pending.Id, f.SessionId).Status == "rejected" &&
                !f.Requests.Claim(pending.Id, f.SessionId, Id(), _ => { }).Claimed,
                "recovery of an unclaimed durable effect retires it without new delivery");
            var unknownContext = f.Executing(NativePluginManagementRequests.ListTool);
            var unknownRequest = f.Requests.Create(unknownContext, "list");
            f.Requests.Claim(unknownRequest.Id, f.SessionId, Id(), _ => { });
            evidence = evidence with { RequestId = unknownRequest.Id };
            var unknownResult = await f.Tool.ReconcileAsync(new(NativePluginManagementRequests.ListTool, WorkflowContract.EmptyObject),
                unknownContext, evidence, default);
            Check(!unknownResult.Success && unknownResult.Evidence?.Outcome == ToolExecutionOutcome.Unknown &&
                f.Requests.GetForOwner(unknownRequest.Id, f.SessionId).Status == "uncertain" &&
                !f.Requests.Claim(unknownRequest.Id, f.SessionId, Id(), _ => { }).Claimed,
                "recovery of a claimed effect remains unknown and never replays");
            Check(!(await f.Tool.ReconcileAsync(new(NativePluginManagementRequests.ListTool, WorkflowContract.EmptyObject),
                unknownContext, evidence with { ProviderId = "forged-provider" }, default)).Success &&
                !(await f.Tool.ReconcileAsync(new(NativePluginManagementRequests.ListTool, WorkflowContract.EmptyObject),
                unknownContext, evidence with { RequestId = Id() }, default)).Success &&
                !(await f.Tool.ReconcileAsync(new(NativePluginManagementRequests.ListTool, CerebralJson.Element(new { query = "different" })),
                unknownContext, evidence, default)).Success,
                "recovery rejects foreign provider/receipt identity and changed immutable arguments");
        }
        // Exercise the real dispatcher: direct extension fixtures cannot catch its default deadline.
        using (var f = new Fixture())
        {
            Check(f.Tool.GetDefaultTimeoutSeconds(NativePluginManagementRequests.SetTool) == 600 &&
                f.Tool.GetDefaultTimeoutSeconds(NativePluginManagementRequests.ListTool) == 30 &&
                f.Tool.GetDefaultTimeoutSeconds(NativePluginManagementRequests.StatusTool) == 30 &&
                Rejects(() => f.Tool.GetDefaultTimeoutSeconds("unadvertised")),
                "native default deadlines are exact, fixed and bounded without model execution hints");
            await using var dispatcher = new ToolDispatcher(() => new HostSettings(), [f.Tool], f.Store.DirectoryPath);
            var listContext = f.Executing(NativePluginManagementRequests.ListTool);
            var listing = dispatcher.ExecuteAsync(new(NativePluginManagementRequests.ListTool, WorkflowContract.EmptyObject),
                listContext, default);
            var list = f.Requests.GetForOwner(listContext.EffectId!, f.SessionId);
            var claim = f.Requests.Claim(list.Id, f.SessionId, Id(), _ =>
                NativePluginManagementRequests.RequireExecuting(f.Host, listContext, NativePluginManagementRequests.ListTool));
            f.Requests.Ready(list.Id, f.SessionId, claim.Request.ClaimId!, _ =>
                NativePluginManagementRequests.RequireExecuting(f.Host, listContext, NativePluginManagementRequests.ListTool));
            f.Requests.Complete(list.Id, f.SessionId, claim.Request.ClaimId!, "completed",
                Observation(rows: [Entry() with { Enabled = false, Loaded = false }]), null);
            Check((await listing.WaitAsync(TimeSpan.FromSeconds(5))).Success,
                "real dispatcher lists only through a currently executing native owner effect");
            var context = f.Executing(NativePluginManagementRequests.SetTool);
            var call = new ToolCall(NativePluginManagementRequests.SetTool,
                CerebralJson.Element(new { catalogRequestId = list.Id, pluginId = Entry().Id, enabled = true }));
            var count = f.Store.List<NativePluginManagementRequest>(Kind).Count;
            var forgedTimeout = await dispatcher.ExecuteAsync(call with
                { Arguments = CerebralJson.Element(new { catalogRequestId = list.Id, pluginId = Entry().Id,
                    enabled = true, timeoutSeconds = 600 }) }, context, default);
            Check(!forgedTimeout.Success && forgedTimeout.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
                f.Store.List<NativePluginManagementRequest>(Kind).Count == count,
                "native deadline cannot be injected through extra model JSON arguments");

            // One wall-clock regression crosses the old 30-second boundary. A normal extension
            // runs alongside it to prove the unchanged default without adding another long wait.
            var ordinary = new DeadlineFixtureExtension();
            await using var ordinaryDispatcher = new ToolDispatcher(() => new HostSettings(), [ordinary], f.Store.DirectoryPath);
            var ordinaryTask = ordinaryDispatcher.ExecuteAsync(new(ordinary.Name, WorkflowContract.EmptyObject),
                DeadlineContext(ordinary.Name), default);
            var review = dispatcher.ExecuteAsync(call, context, default);
            await ordinary.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromSeconds(31.5));
            Check(!review.IsCompleted &&
                f.Requests.GetForOwner(context.EffectId!, f.SessionId).Status == "pending" &&
                f.Host.Store.Get(context.JobId).Effect is { State: WorkflowEffectState.Dispatched },
                "owner review survives the former 30-second timer while retaining the exact dispatched workflow effect");
            var ordinaryResult = await ordinaryTask.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!ordinaryResult.Success && ordinary.CancellationObserved &&
                ordinaryResult.Evidence?.Outcome == ToolExecutionOutcome.Unknown &&
                ordinaryResult.Content.Contains("timed out", StringComparison.Ordinal),
                "extensions without an owned policy retain the bounded 30-second dispatch timeout");
            var owner = f.Requests.Claim(context.EffectId!, f.SessionId, Id(), _ =>
                NativePluginManagementRequests.RequireExecuting(f.Host, context, NativePluginManagementRequests.SetTool));
            Check(owner.Claimed && f.Requests.Ready(context.EffectId!, f.SessionId, owner.Request.ClaimId!, _ =>
                NativePluginManagementRequests.RequireExecuting(f.Host, context, NativePluginManagementRequests.SetTool)).Status == "claimed",
                "owner approval remains valid after thirty seconds and still checks the exact executing effect");
            f.Requests.Complete(context.EffectId!, f.SessionId, owner.Request.ClaimId!, "completed",
                Observation(changed: [Entry().Id]), null);
            var result = await review.WaitAsync(TimeSpan.FromSeconds(5));
            Check(result.Success && result.Evidence is { Outcome: ToolExecutionOutcome.Succeeded,
                ProviderId: NativePluginManagementToolExtension.Provider } && result.Evidence.RequestId == context.EffectId,
                "delayed owner completion returns the retained native proof through the real dispatcher");
            var statusContext = f.Executing(NativePluginManagementRequests.StatusTool);
            count = f.Store.List<NativePluginManagementRequest>(Kind).Count;
            var status = await dispatcher.ExecuteAsync(new(NativePluginManagementRequests.StatusTool,
                CerebralJson.Element(new { requestId = context.EffectId })), statusContext, default);
            Check(status.Success && status.Evidence?.RequestId == context.EffectId &&
                f.Store.List<NativePluginManagementRequest>(Kind).Count == count,
                "real dispatcher status reads completed evidence without another owner action");
        }

        foreach (var mode in new[] { "caller-pending", "caller-claimed", "engine-claimed", "lifetime-pending", "lifetime-claimed" })
        {
            using var f = new Fixture();
            var proof = f.Proof(Observation(rows: [Entry() with { Enabled = false, Loaded = false }]));
            var context = f.Executing(NativePluginManagementRequests.SetTool);
            var subscription = f.Events.Subscribe();
            using var caller = new CancellationTokenSource();
            using var engine = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, engine.Token);
            var dispatcher = new ToolDispatcher(() => new HostSettings(), [f.Tool], f.Store.DirectoryPath);
            var disposed = false;
            try
            {
                var task = dispatcher.ExecuteAsync(new(NativePluginManagementRequests.SetTool,
                    CerebralJson.Element(new { catalogRequestId = proof.Id, pluginId = Entry().Id, enabled = true })),
                    context, linked.Token);
                var request = f.Requests.GetForOwner(context.EffectId!, f.SessionId);
                var claimed = mode.EndsWith("-claimed", StringComparison.Ordinal);
                if (claimed) Check(f.Requests.Claim(request.Id, f.SessionId, Id(), _ =>
                    NativePluginManagementRequests.RequireExecuting(f.Host, context, NativePluginManagementRequests.SetTool)).Claimed,
                    mode + " fixture owner claims only the live exact effect");
                if (mode.StartsWith("lifetime", StringComparison.Ordinal))
                {
                    await dispatcher.DisposeAsync(); disposed = true;
                }
                else if (mode.StartsWith("engine", StringComparison.Ordinal))
                {
                    f.Host.Store.Cancel(context.JobId);
                    engine.Cancel();
                }
                else caller.Cancel();
                var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
                var retained = f.Requests.GetForOwner(request.Id, f.SessionId);
                Check(!result.Success && result.Evidence?.RequestId == request.Id &&
                    result.Evidence.Outcome == (claimed ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Cancelled) &&
                    retained.Status == (claimed ? "uncertain" : "cancelled") && retained.Result is null &&
                    !f.Requests.Claim(request.Id, f.SessionId, Id(), _ => { }).Claimed,
                    mode + " cancellation is preserved with truthful retained state and no delivery replay");
                NativePluginManagementRequest? notification = null;
                while (subscription.Reader.TryRead(out var item))
                    if (item.Type == NativePluginManagementRequests.EventType &&
                        item.Data.GetProperty("id").GetString() == request.Id)
                        notification = CerebralJson.Read<NativePluginManagementRequest>(item.Data);
                Check(notification?.Status == retained.Status,
                    mode + " publishes the terminal update needed to revoke the owner card");
            }
            finally
            {
                if (!disposed) await dispatcher.DisposeAsync();
                f.Events.Unsubscribe(subscription.Id);
            }
        }

        foreach (var seconds in new[] { -1, 0, 601, int.MaxValue })
        {
            using var f = new Fixture();
            var extension = new OwnedDeadlineFixtureExtension(seconds);
            await using var dispatcher = new ToolDispatcher(() => new HostSettings(), [extension], f.Store.DirectoryPath);
            var result = await dispatcher.ExecuteAsync(new(extension.Name, WorkflowContract.EmptyObject),
                DeadlineContext(extension.Name), default);
            Check(!result.Success && result.Evidence?.Outcome == ToolExecutionOutcome.Rejected && extension.Executions == 0,
                "invalid owned deadline " + seconds + " is rejected before extension dispatch");
        }
        using (var f = new Fixture())
        {
            var extension = new OwnedDeadlineFixtureExtension(600);
            await using var dispatcher = new ToolDispatcher(() => new HostSettings(), [extension], f.Store.DirectoryPath);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = await dispatcher.ExecuteAsync(new(extension.Name, CerebralJson.Element(new { timeoutSeconds = 1 })),
                DeadlineContext(extension.Name), default).WaitAsync(TimeSpan.FromSeconds(5));
            Check(!result.Success && extension.Executions == 1 && extension.CancellationObserved &&
                result.Evidence?.Outcome == ToolExecutionOutcome.Unknown && clock.Elapsed < TimeSpan.FromSeconds(5),
                "explicit schema-supported one-second timeout still overrides a 600-second extension default");
            var invalid = await dispatcher.ExecuteAsync(new(extension.Name, CerebralJson.Element(new { timeoutSeconds = 601 })),
                DeadlineContext(extension.Name), default);
            Check(!invalid.Success && invalid.Evidence?.Outcome == ToolExecutionOutcome.Rejected && extension.Executions == 1,
                "explicit caller timeout remains bounded and cannot extend an owned deadline beyond 600 seconds");
        }

        Console.WriteLine("Native plugin management fixture checks passed: " + checks +
            ". Evidence: isolated SQLite/event and engine metadata; no live Desktop, native plugin, model or owner database.");
    }

    private static string Id() => Guid.NewGuid().ToString("N");
    private static T Copy<T>(T value) => CerebralJson.Read<T>(CerebralJson.Element(value));
    private static NativePluginCatalogEntry Entry() => new("note.fixture", "Note fixture", "1.0.0",
        "1.0.0-immutable-fixture", PackageHash, true, true, null);
    private static NativePluginSelection Selection() => new(Entry().Id, true, Entry().Generation!, PackageHash);
    private static NativePluginManagementResult Observation(IReadOnlyList<NativePluginCatalogEntry>? rows = null,
        IReadOnlyList<string>? changed = null, int omitted = 0, string? hash = null) =>
        new(new(hash ?? CatalogHash, rows ?? [Entry()], omitted), changed ?? []);

    private static ToolExecutionContext DeadlineContext(string name) => new(new AgentProfile
    {
        Id = "deadline-fixture", Capabilities = ["fixture.timeout"], AllowedTools = [name]
    }, Id(), Id(), EffectId: Id()) { EnforceProfileScope = true };

    private class DeadlineFixtureExtension : IToolExtension
    {
        public string Name => "fixture.deadline";
        public int Executions { get; private set; }
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<ToolDescriptor> Tools { get; } =
        [
            new("fixture.deadline", "Isolated cancellable deadline fixture.", CerebralJson.Element(new
            {
                type = "object", properties = new { timeoutSeconds = new { type = "integer" } },
                additionalProperties = false
            }), "fixture.timeout")
        ];
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            Executions++; Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
            throw new InvalidOperationException("An infinite fixture wait must end through cancellation.");
        }
    }

    private sealed class OwnedDeadlineFixtureExtension(int seconds) : DeadlineFixtureExtension, IToolExecutionTimeoutPolicy
    {
        public int GetDefaultTimeoutSeconds(string toolName) => toolName == Name ? seconds :
            throw new ArgumentException("Unknown fixture tool.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Cerebral.NativePluginManagementTests-" + Id()));
        public string SessionId { get; } = Id();
        public StateStore Store { get; }
        public EventHub Events { get; } = new();
        public AgentProfile Profile { get; } = new()
        {
            Id = "native-management-lead", Revision = 7, Role = "lead", WorkerId = "local",
            Capabilities = [NativePluginManagementRequests.Capability],
            AllowedTools = [NativePluginManagementRequests.ListTool, NativePluginManagementRequests.SetTool, NativePluginManagementRequests.StatusTool]
        };
        public NativePluginManagementRequests Requests { get; }
        public NativePluginManagementToolExtension Tool { get; }
        public WorkerWorkflows Host { get; }
        public Fixture()
        {
            Store = new(Path.Combine(Root, "state")); Restore();
            // Native WorkflowStore conversations deliberately have no legacy TaskSession record.
            var configuration = new Configuration(Store);
            Host = new(Store, configuration, Events, [], []);
            Requests = new(Store, Events);
            Tool = new(Store, Events, () => Host);
        }
        public void Restore() => Store.Put("agent", Profile.Id, Profile);
        public ToolExecutionContext Context(AgentProfile? acceptedProfile = null)
        {
            FinishPriorFixtureRuns();
            return WorkflowUiOriginTestRuns.Metadata(Store,
                new(Copy(acceptedProfile ?? Profile), SessionId, Id(), EffectId: Id()) { EnforceProfileScope = true },
                Tool.Tools);
        }
        public void Expire(NativePluginManagementRequest request)
        {
            var value = Copy(request); value.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1); Store.Put(Kind, value.Id, value);
        }
        public NativePluginManagementRequest Proof(NativePluginManagementResult? result = null)
        {
            var context = Context();
            var request = Requests.Create(context, "list");
            var claim = Requests.Claim(request.Id, SessionId, Id(), _ => { }).Request;
            return Requests.Complete(request.Id, SessionId, claim.ClaimId!, "completed", result ?? Observation(), null);
        }
        private void FinishPriorFixtureRuns()
        {
            // A fixture tool result does not advance its metadata engine; retire that prior turn explicitly.
            foreach (var prior in Host.Store.ListActive())
            {
                var stopped = Host.Store.Cancel(prior.Id);
                if (!stopped.IsTerminal && stopped.Effect is { State: WorkflowEffectState.Dispatched } effect)
                    Host.Store.CompleteEffect(stopped.Id, effect.Id,
                        new(WorkflowEffectState.Cancelled, WorkflowContract.EmptyObject, "Fixture turn ended."));
            }
        }
        public ToolExecutionContext Executing(string name, bool includeManagement = true,
            Func<WorkerExecutionSnapshot.Snapshot, WorkerExecutionSnapshot.Snapshot>? snapshotChange = null,
            EventActor? caller = null, bool replaceCaller = false, string? parentRunId = null,
            string? effectCommand = null, int effectVersion = 1, string? effectToolName = null, bool dispatch = true,
            AgentProfile? acceptedProfile = null)
        {
            FinishPriorFixtureRuns();
            var accepted = Copy(acceptedProfile ?? Profile);
            var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, accepted, "", "", null,
                includeManagement ? Tool.Tools : [], accepted.Capabilities, accepted.AllowedTools, [], false, "fixture-routing", [], []);
            if (snapshotChange is not null) snapshot = snapshotChange(snapshot);
            var empty = WorkflowContract.EmptyObject;
            if (parentRunId is not null)
            {
                var parent = Host.Store.Submit(new WorkflowSubmission(Id(), Id(), Profile.Id,
                    "fixture.native-management.parent", "1.0.0", "Fixture parent metadata only.", null, empty,
                    CerebralJson.Element(snapshot), Caller: new(EventActorKind.User, "fixture-owner")));
                parentRunId = parent.Id;
                caller = new(EventActorKind.Service, "cerebral.coordination"); replaceCaller = true;
            }
            var submission = new WorkflowSubmission(Id(), SessionId, accepted.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Fixture metadata admission only.", null, empty, CerebralJson.Element(snapshot),
                Caller: replaceCaller ? caller : new(EventActorKind.User, "fixture-owner")) { ParentRunId = parentRunId };
            var run = Host.Store.TryClaim(Host.Store.Submit(submission).Id)!;
            var descriptor = new WorkflowCommandDescriptor(effectCommand ?? WorkerWorkflowEffects.ToolCommand, effectVersion,
                "Metadata-only dispatched tool effect.", empty, empty, []);
            var arguments = CerebralJson.Element(new WorkerWorkflowEffects.ToolArguments(effectToolName ?? name, empty));
            run = Host.Store.ApplyStep(run.Id, run.Revision,
                new(empty, new InvokeWorkflowCommand(descriptor.Id, descriptor.Version, arguments)), descriptor);
            if (dispatch) run = Host.Store.DispatchEffect(run.Id, run.Revision);
            return new(Copy(accepted), SessionId, run.Id, EffectId: run.Effect!.Id) { EnforceProfileScope = true };
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            var target = Path.GetFullPath(Root);
            var expected = Path.GetFullPath(Path.GetTempPath());
            if (!target.StartsWith(expected, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(target).StartsWith("Cerebral.NativePluginManagementTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup path escaped its exact task-owned temporary root.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}
