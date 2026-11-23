using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class CortexContinuityTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-continuity-" + Guid.NewGuid().ToString("N")));
        var assertions = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception("FAIL: " + description);
            assertions++; Console.WriteLine("PASS: " + description);
        }
        bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or InvalidOperationException) { return true; }
        }
        try
        {
            var off = Fixture.Create(root, "off", enabled: false);
            Check(!new ProjectKnowledgeSettings().AutomaticContinuity &&
                await off.Manager.BeginAsync(off.Job, off.Profile, default) is null && off.Tools.Calls.Count == 0 && off.Manager.Get(off.Session.Id) is null,
                "Cortex task continuity is opt-in and a disabled project makes no provider calls or continuity record");
            var invalid = off.Projects.Get("project"); invalid.AutomaticContinuity = true; invalid.WriteNamespace = null;
            Check(Rejects(() => off.Projects.Save(invalid)), "continuity cannot be enabled without an explicit write namespace");
            invalid.WriteNamespace = Space; invalid.ReadNamespaces = ["knowledge.shared"];
            Check(Rejects(() => off.Projects.Save(invalid)) && !off.Projects.Get("project").AutomaticContinuity,
                "continuity write namespace must also be readable and invalid settings preserve the existing opt-out");

            var skipped = Fixture.Create(root, "skipped");
            skipped.Job.Kind = "compact";
            Check(await skipped.Manager.BeginAsync(skipped.Job, skipped.Profile, default) is null, "compaction jobs do not open a separate Cortex task");
            skipped.Job.Kind = "agent"; skipped.Session.ProjectId = null; skipped.Store.Put("session", skipped.Session.Id, skipped.Session);
            Check(await skipped.Manager.BeginAsync(skipped.Job, skipped.Profile, default) is null && skipped.Tools.Calls.Count == 0,
                "unbound conversations remain entirely in Cerebral without inferred Cortex task scope");

            foreach (var missing in new[] { "mcp.cortex", "cortex.write", "cortex_start", "cortex_task", "cortex_finish" })
            {
                var denied = Fixture.Create(root, "grant-" + missing.Replace('.', '-'));
                denied.Profile.Capabilities.Remove(missing); denied.Profile.AllowedTools.Remove("mcp.cortex." + missing);
                var state = await denied.Manager.BeginAsync(denied.Job, denied.Profile, default);
                Check(state?.Status == "unavailable" && denied.Tools.Calls.Count == 0,
                    "continuity requires the explicit " + missing + " grant before any provider call");
            }

            var created = Fixture.Create(root, "create");
            created.Session.Title = string.Concat(Enumerable.Repeat("λ😀 title ", 100));
            created.Session.Checkpoint = "PRIVATE_CHECKPOINT_SENTINEL";
            created.Session.Messages.Add(new() { Sequence = 2, Role = "assistant", Content = "PRIVATE_TRANSCRIPT_SENTINEL", Origin = "assistant" });
            created.Store.Put("session", created.Session.Id, created.Session);
            created.Job.Prompt = string.Concat(Enumerable.Repeat("λ😀 objective ", 700));
            created.Job.Result = string.Concat(Enumerable.Repeat("λ😀 model result ", 700));
            created.Job.Evidence = Enumerable.Range(1, 8).Select(i => new ExecutionEvidence
            {
                Id = "evidence-" + i, Name = "configured-test", Success = false, Summary = new string('x', 900),
                SourceSessionId = created.Session.Id, MessageSequence = 1
            }).ToList();
            created.Store.Put("job", created.Job.Id, created.Job);
            var began = await created.Manager.BeginAsync(created.Job, created.Profile, default);
            Check(began is { Status: "ready", Revision: 1 } && created.Tools.Calls.Count == 2,
                "an absent Cortex task is created once and its confirmed revision is saved");
            var start = created.Tools.Calls[0]; var create = created.Tools.Calls[1];
            Check(start.Call.Name == "mcp.cortex.cortex_start" && Text(start, "taskId") == created.Session.Id &&
                start.Call.Arguments.GetProperty("namespaces").EnumerateArray().Select(x => x.GetString()).SequenceEqual([Space, "knowledge.shared"]) &&
                Text(start, "phaseId") == created.Job.Id && start.Call.Arguments.GetProperty("maxUtf8Bytes").GetInt32() == 4096,
                "start binds the exact Cerebral task ID and ordered write-first project namespaces");
            Check(Text(create, "action") == "create" && Text(create, "namespace") == Space && Text(create, "taskId") == created.Session.Id &&
                Text(create, "idempotencyKey").StartsWith("cerebral-create-", StringComparison.Ordinal) && Bytes(Text(create, "idempotencyKey")) <= 128 &&
                Bytes(Text(start, "intent")) <= 1024 && Bytes(Text(create, "title")) <= 180 && Bytes(Text(create, "content")) <= 4096,
                "creation uses the explicit namespace, stable task identity and bounded UTF-8 objective/title");
            var sameTask = Fixture.Create(root, "same-task-new-job", taskId: created.Session.Id);
            await sameTask.Manager.BeginAsync(sameTask.Job, sameTask.Profile, default);
            Check(Text(sameTask.Tools.Calls.Last(), "idempotencyKey") == Text(create, "idempotencyKey"),
                "the create key remains stable for one namespace/task coordinate despite a different assignment and brief");
            Check(created.Tools.Calls.All(x => x.Context.Cortex is { KnowledgeWriteApproved: false, ProjectId: "project" } && x.Context.SessionId == created.Session.Id),
                "task continuity uses project scope without manufacturing durable-knowledge approval");
            var attemptingWasSaved = false;
            created.Tools.OnCall = call =>
            {
                if (call.Call.Name.EndsWith("cortex_finish", StringComparison.Ordinal))
                    attemptingWasSaved = created.Manager.Get(created.Session.Id)?.Status == "attempting";
                return null;
            };
            await created.Manager.CheckpointAsync(created.Job, created.Profile, began, "completed");
            var finish = created.Tools.Calls.Last();
            Check(attemptingWasSaved && created.Manager.Get(created.Session.Id) is { Status: "checkpointed", Revision: 2 } &&
                Text(finish, "taskAction") == "complete" && finish.Call.Arguments.GetProperty("expectedRevision").GetInt32() == 1 &&
                Text(finish, "idempotencyKey").StartsWith("cerebral-", StringComparison.Ordinal) && Text(finish, "idempotencyKey") != Text(create, "idempotencyKey"),
                "finish persists its uncertainty boundary and uses the confirmed expected revision and an operation-specific key");
            Check(finish.Call.Arguments.GetProperty("knowledge").GetArrayLength() == 0 && !finish.Call.Arguments.TryGetProperty("blockedReason", out _) &&
                !CortexOperationPolicy.IsDurableWrite("cortex_finish", finish.Call.Arguments),
                "automatic continuity sends a task-only finish with an empty knowledge collection");
            Check(Bytes(Text(finish, "taskContent")) <= 4096 && Bytes(Text(finish, "evidence")) <= 2000 &&
                Text(finish, "taskContent").Contains("Model-reported result (verify against service evidence)", StringComparison.Ordinal) &&
                !string.Join("", created.Tools.Calls.Select(x => x.Call.Arguments.GetRawText())).Contains("PRIVATE_TRANSCRIPT_SENTINEL", StringComparison.Ordinal) &&
                !string.Join("", created.Tools.Calls.Select(x => x.Call.Arguments.GetRawText())).Contains("PRIVATE_CHECKPOINT_SENTINEL", StringComparison.Ordinal),
                "checkpoints carry bounded objective/result/evidence briefs without exporting the transcript or existing full checkpoint");
            Check(created.Store.Get<TaskSession>("session", created.Session.Id)!.Messages.Any(x => x.Content == "PRIVATE_TRANSCRIPT_SENTINEL") &&
                created.Store.Get<AgentJob>("job", created.Job.Id)!.Result == created.Job.Result,
                "Cerebral retains its source conversation and execution result independently of continuity receipts");
            var count = created.Tools.Calls.Count;
            await created.Manager.CheckpointAsync(created.Job, created.Profile, began, "completed");
            Check(created.Tools.Calls.Count == count, "a completed checkpoint object cannot automatically send the same finish again");

            foreach (var lifecycle in new[] { "open", "assigned", "claimed", "completed", "blocked" })
            {
                var resumed = Fixture.Create(root, "resume-" + lifecycle);
                resumed.Tools.ExistingStatus = lifecycle; resumed.Tools.Revision = 7;
                var ready = await resumed.Manager.BeginAsync(resumed.Job, resumed.Profile, default);
                Check(ready is { Status: "ready" } && ready.Revision == (lifecycle is "completed" or "blocked" ? 8 : 7) &&
                    resumed.Tools.Calls.Count == (lifecycle is "completed" or "blocked" ? 2 : 1),
                    "an existing " + lifecycle + " Cortex task is recovered without creating a duplicate");
                if (lifecycle is "completed" or "blocked")
                {
                    var reopen = resumed.Tools.Calls.Last();
                    Check(Text(reopen, "action") == "reopen" && reopen.Call.Arguments.GetProperty("expectedRevision").GetInt32() == 7 &&
                        Text(reopen, "taskId") == resumed.Session.Id && !string.IsNullOrEmpty(Text(reopen, "idempotencyKey")),
                        "reopening a " + lifecycle + " task uses its exact coordinate and expected revision");
                }
                resumed.Job.WaitingReason = new string('w', 1200);
                await resumed.Manager.CheckpointAsync(resumed.Job, resumed.Profile, ready, lifecycle == "blocked" ? "waiting-human" : "failed");
                var checkpoint = resumed.Tools.Calls.Last();
                Check(Text(checkpoint, "taskAction") == (lifecycle == "blocked" ? "block" : "update") &&
                    (lifecycle != "blocked" || Bytes(Text(checkpoint, "blockedReason")) <= 800),
                    "the checkpoint lifecycle records " + (lifecycle == "blocked" ? "human waiting with a bounded reason" : "a non-complete outcome as an update"));
            }

            var unchanged = Fixture.Create(root, "unchanged"); unchanged.Tools.ExistingStatus = "open"; unchanged.Tools.StartMode = "unchanged";
            var recoveredUnchanged = await unchanged.Manager.BeginAsync(unchanged.Job, unchanged.Profile, default);
            Check(recoveredUnchanged is { Status: "ready", Revision: 7 } && unchanged.Tools.Calls.Count == 2 && Text(unchanged.Tools.Calls.Last(), "action") == "get",
                "unchanged compact start explicitly reads the existing task rather than treating its omitted body as absence");
            var archived = Fixture.Create(root, "archived"); archived.Tools.ExistingStatus = "archived"; archived.Tools.StartMode = "unchanged";
            Check((await archived.Manager.BeginAsync(archived.Job, archived.Profile, default))?.Status == "unavailable" &&
                !archived.Tools.Calls.Any(x => x.Call.Arguments.TryGetProperty("action", out var action) && action.GetString() is "create" or "reopen"),
                "unchanged recovery cannot bypass an archived task lifecycle");

            var identity = Fixture.Create(root, "identity");
            identity.Tools.OnCall = call => Reply(new { ns = new[] { Space, "knowledge.shared" }, task = new { ns = Space, id = "different-task", rev = 3, status = "open" } });
            Check((await identity.Manager.BeginAsync(identity.Job, identity.Profile, default))?.Status == "unavailable" && identity.Tools.Calls.Count == 1,
                "a returned task with another ID is rejected before any task mutation");
            var namespaces = Fixture.Create(root, "namespaces");
            namespaces.Tools.OnCall = call => Reply(new { ns = new[] { "knowledge.shared", Space }, task = (object?)null });
            Check((await namespaces.Manager.BeginAsync(namespaces.Job, namespaces.Profile, default))?.Status == "unavailable" && namespaces.Tools.Calls.Count == 1,
                "an unexpected namespace order cannot be treated as permission to create a missing task");

            var unavailable = Fixture.Create(root, "unavailable");
            unavailable.Tools.OnCall = _ => new(false, "fixture connector unavailable");
            Check((await unavailable.Manager.BeginAsync(unavailable.Job, unavailable.Profile, default))?.Status == "unavailable" &&
                unavailable.Tools.Calls.Count == 1 && unavailable.Store.Get<AgentJob>("job", unavailable.Job.Id)!.Status == "running",
                "provider unavailability is reported while the independent Cerebral job state is preserved");

            var revoked = Fixture.Create(root, "revoked"); revoked.Tools.ExistingStatus = "open";
            var beforeRevocation = await revoked.Manager.BeginAsync(revoked.Job, revoked.Profile, default);
            revoked.Profile.Capabilities.Remove("cortex.write"); count = revoked.Tools.Calls.Count;
            await revoked.Manager.CheckpointAsync(revoked.Job, revoked.Profile, beforeRevocation, "completed");
            Check(revoked.Manager.Get(revoked.Session.Id)?.Status == "unavailable" && revoked.Tools.Calls.Count == count,
                "revoking the write grant after start prevents finish without another provider call");

            var changedProvider = Fixture.Create(root, "provider"); changedProvider.Tools.ExistingStatus = "open";
            var beforeProviderChange = await changedProvider.Manager.BeginAsync(changedProvider.Job, changedProvider.Profile, default);
            var host = changedProvider.Configuration.Get(); host.McpServers[0].Arguments.Add("--different-instance"); changedProvider.Configuration.Save(host);
            count = changedProvider.Tools.Calls.Count;
            await changedProvider.Manager.CheckpointAsync(changedProvider.Job, changedProvider.Profile, beforeProviderChange, "completed");
            Check(changedProvider.Manager.Get(changedProvider.Session.Id)?.Status == "unavailable" && changedProvider.Tools.Calls.Count == count,
                "changing provider configuration prevents a saved task revision from being applied to another provider");

            var changedSettings = Fixture.Create(root, "settings"); changedSettings.Tools.ExistingStatus = "open";
            var beforeSettingsChange = await changedSettings.Manager.BeginAsync(changedSettings.Job, changedSettings.Profile, default);
            var settings = changedSettings.Projects.Get("project"); settings.AutomaticContinuity = false; changedSettings.Projects.Save(settings);
            count = changedSettings.Tools.Calls.Count;
            await changedSettings.Manager.CheckpointAsync(changedSettings.Job, changedSettings.Profile, beforeSettingsChange, "completed");
            Check(changedSettings.Manager.Get(changedSettings.Session.Id)?.Status == "unavailable" && changedSettings.Tools.Calls.Count == count,
                "a changed project settings revision prevents checkpointing under the former consent");

            foreach (var race in new[]
            {
                (Name: "settings-start-create", Read: "start", Status: (string?)null, Provider: false),
                (Name: "provider-start-reopen", Read: "start", Status: (string?)"completed", Provider: true),
                (Name: "settings-get-reopen", Read: "get", Status: (string?)"blocked", Provider: false),
                (Name: "provider-get-reopen", Read: "get", Status: (string?)"completed", Provider: true)
            })
            {
                var reading = Fixture.Create(root, race.Name); reading.Tools.ExistingStatus = race.Status;
                if (race.Read == "get") reading.Tools.StartMode = "unchanged";
                reading.Tools.OnCall = call =>
                {
                    var selectedRead = race.Read == "start" ? call.Call.Name.EndsWith("cortex_start", StringComparison.Ordinal) :
                        call.Call.Name.EndsWith("cortex_task", StringComparison.Ordinal) && Text(call, "action") == "get";
                    if (!selectedRead) return null;
                    if (race.Provider)
                    {
                        var changedHost = reading.Configuration.Get(); changedHost.McpServers[0].Arguments.Add("--changed-during-read");
                        reading.Configuration.Save(changedHost);
                    }
                    else
                    {
                        var changedProject = reading.Projects.Get("project"); changedProject.AutomaticContinuity = false;
                        reading.Projects.Save(changedProject);
                    }
                    return null;
                };
                var refused = await reading.Manager.BeginAsync(reading.Job, reading.Profile, default);
                Check(refused?.Status == "unavailable" && reading.Tools.Calls.Count == (race.Read == "get" ? 2 : 1) &&
                    !reading.Tools.Calls.Any(x => x.Call.Arguments.TryGetProperty("action", out var action) && action.GetString() is "create" or "reopen"),
                    race.Name + ": a binding change during the awaited read prevents the subsequent task mutation");
            }

            foreach (var operation in new[] { "create", "reopen" })
            {
                var lateStart = Fixture.Create(root, "late-" + operation);
                lateStart.Tools.ExistingStatus = operation == "reopen" ? "completed" : null;
                lateStart.Tools.OnCall = call =>
                {
                    if (call.Call.Name.EndsWith("cortex_task", StringComparison.Ordinal) && Text(call, "action") == operation)
                    {
                        var changedSession = lateStart.Store.Get<TaskSession>("session", lateStart.Session.Id)!;
                        changedSession.ProjectId = null; lateStart.Store.Put("session", changedSession.Id, changedSession);
                    }
                    return null;
                };
                var confirmedOld = await lateStart.Manager.BeginAsync(lateStart.Job, lateStart.Profile, default);
                var retainedOld = lateStart.Manager.Get(lateStart.Session.Id);
                Check(confirmedOld is { Status: "started-old-binding" } && retainedOld is { Status: "started-old-binding", Namespace: Space } &&
                    retainedOld.TaskId == lateStart.Session.Id && retainedOld.ProjectId == "project" &&
                    retainedOld.Revision == (operation == "create" ? 1 : 8) && lateStart.Tools.Calls.Count == 2,
                    "a confirmed late " + operation + " retains its actual prior target and revision instead of reporting uncertainty");
                var beforeOldCheckpoint = lateStart.Tools.Calls.Count;
                await lateStart.Manager.CheckpointAsync(lateStart.Job, lateStart.Profile, confirmedOld, "completed");
                Check(lateStart.Tools.Calls.Count == beforeOldCheckpoint && lateStart.Manager.Get(lateStart.Session.Id)?.Status == "started-old-binding",
                    "a confirmed late " + operation + " cannot checkpoint the former project binding");
            }

            var rebound = Fixture.Create(root, "rebound"); rebound.Tools.ExistingStatus = "open";
            var beforeRebind = await rebound.Manager.BeginAsync(rebound.Job, rebound.Profile, default);
            var reboundSession = rebound.Store.Get<TaskSession>("session", rebound.Session.Id)!; reboundSession.ProjectId = null;
            rebound.Store.Put("session", reboundSession.Id, reboundSession); count = rebound.Tools.Calls.Count;
            await rebound.Manager.CheckpointAsync(rebound.Job, rebound.Profile, beforeRebind, "completed");
            Check(rebound.Manager.Get(rebound.Session.Id)?.Status == "unavailable" && rebound.Tools.Calls.Count == count,
                "a changed conversation binding prevents a checkpoint to the former project");

            var inFlight = Fixture.Create(root, "in-flight"); inFlight.Tools.ExistingStatus = "open";
            var beforeFlight = await inFlight.Manager.BeginAsync(inFlight.Job, inFlight.Profile, default);
            inFlight.Tools.OnCall = call =>
            {
                if (call.Call.Name.EndsWith("cortex_finish", StringComparison.Ordinal))
                { var changed = inFlight.Store.Get<TaskSession>("session", inFlight.Session.Id)!; changed.ProjectId = null; inFlight.Store.Put("session", changed.Id, changed); }
                return null;
            };
            await inFlight.Manager.CheckpointAsync(inFlight.Job, inFlight.Profile, beforeFlight, "completed");
            Check(inFlight.Manager.Get(inFlight.Session.Id) is { Status: "checkpointed-old-binding", Namespace: Space, Revision: 8 },
                "a confirmed finish racing a project change reports the actual old target instead of pretending no write occurred");

            var uncertain = Fixture.Create(root, "uncertain"); uncertain.Tools.ExistingStatus = "open";
            var beforeFailure = await uncertain.Manager.BeginAsync(uncertain.Job, uncertain.Profile, default);
            uncertain.Tools.OnCall = call => call.Call.Name.EndsWith("cortex_finish", StringComparison.Ordinal) ? throw new IOException("fixture transport lost after write") : null;
            await uncertain.Manager.CheckpointAsync(uncertain.Job, uncertain.Profile, beforeFailure, "completed");
            Check(uncertain.Manager.Get(uncertain.Session.Id)?.Status == "uncertain", "a lost finish response is retained as an uncertain write");
            count = uncertain.Tools.Calls.Count;
            await uncertain.Manager.CheckpointAsync(uncertain.Job, uncertain.Profile, beforeFailure, "completed");
            await uncertain.Manager.BeginAsync(uncertain.Job, uncertain.Profile, default);
            Check(uncertain.Tools.Calls.Count == count, "the same job does not automatically replay an uncertain finish through checkpoint or begin");

            var uncertainCreate = Fixture.Create(root, "uncertain-create");
            uncertainCreate.Tools.OnCall = call => call.Call.Name.EndsWith("cortex_task", StringComparison.Ordinal) ? throw new IOException("fixture create response lost") : null;
            Check((await uncertainCreate.Manager.BeginAsync(uncertainCreate.Job, uncertainCreate.Profile, default))?.Status == "uncertain",
                "an unconfirmed create is classified as uncertain rather than safe to recreate");
            count = uncertainCreate.Tools.Calls.Count;
            await uncertainCreate.Manager.BeginAsync(uncertainCreate.Job, uncertainCreate.Profile, default);
            Check(uncertainCreate.Tools.Calls.Count == count, "the same job cannot automatically replay an uncertain create");
            var interruptedBoundary = uncertainCreate.Manager.Get(uncertainCreate.Session.Id)!; interruptedBoundary.Status = "attempting";
            uncertainCreate.Store.Put("cortex-continuity", uncertainCreate.Session.Id, interruptedBoundary);
            await uncertainCreate.Manager.BeginAsync(uncertainCreate.Job, uncertainCreate.Profile, default);
            Check(uncertainCreate.Tools.Calls.Count == count && uncertainCreate.Manager.Get(uncertainCreate.Session.Id)?.Status == "uncertain",
                "a persisted attempting boundary recovered after interruption also prevents same-job write replay");
            uncertainCreate.Tools.OnCall = null; uncertainCreate.Tools.ExistingStatus = "open"; uncertainCreate.Tools.Revision = 9;
            var next = CerebralJson.Read<AgentJob>(CerebralJson.Element(uncertainCreate.Job)); next.Id += "-explicit-next";
            uncertainCreate.Store.Put("job", next.Id, next);
            var recovered = await uncertainCreate.Manager.BeginAsync(next, uncertainCreate.Profile, default);
            Check(recovered is { Status: "ready", Revision: 9 } && uncertainCreate.Tools.Calls.Count == count + 1,
                "a new explicit assignment can read the confirmed existing task without replaying the old create");

            var interrupted = Fixture.Create(root, "interrupted");
            using var cancellation = new CancellationTokenSource();
            interrupted.Tools.OnCall = call =>
            {
                if (!call.Call.Name.EndsWith("cortex_task", StringComparison.Ordinal)) return null;
                cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token);
            };
            var cancellationObserved = false;
            try { await interrupted.Manager.BeginAsync(interrupted.Job, interrupted.Profile, cancellation.Token); }
            catch (OperationCanceledException) { cancellationObserved = true; }
            Check(cancellationObserved && interrupted.Manager.Get(interrupted.Session.Id)?.Status == "uncertain",
                "cancelling a task mutation propagates cancellation and retains its uncertain external effect");

            var disposition = Fixture.Create(root, "disposition"); disposition.Tools.MutationDisposition = "not-applied";
            Check((await disposition.Manager.BeginAsync(disposition.Job, disposition.Profile, default))?.Status == "uncertain",
                "an unconfirmed mutation disposition cannot be treated as a successful task write");
            var wrongCreateRevision = Fixture.Create(root, "wrong-create-revision"); wrongCreateRevision.Tools.MutationRevision = 3;
            Check((await wrongCreateRevision.Manager.BeginAsync(wrongCreateRevision.Job, wrongCreateRevision.Profile, default))?.Status == "uncertain",
                "a create receipt cannot confirm an unexpected resulting task revision");
            var wrongFinishRevision = Fixture.Create(root, "wrong-finish-revision"); wrongFinishRevision.Tools.ExistingStatus = "open";
            var expectedFinishRevision = await wrongFinishRevision.Manager.BeginAsync(wrongFinishRevision.Job, wrongFinishRevision.Profile, default);
            wrongFinishRevision.Tools.MutationRevision = 7;
            await wrongFinishRevision.Manager.CheckpointAsync(wrongFinishRevision.Job, wrongFinishRevision.Profile, expectedFinishRevision, "completed");
            Check(wrongFinishRevision.Manager.Get(wrongFinishRevision.Session.Id)?.Status == "uncertain",
                "a finish receipt retaining the old revision cannot be treated as a committed checkpoint");
            var idempotent = Fixture.Create(root, "idempotent"); idempotent.Tools.MutationDisposition = "idempotent";
            Check((await idempotent.Manager.BeginAsync(idempotent.Job, idempotent.Profile, default))?.Status == "ready",
                "an explicitly idempotent mutation receipt confirms the existing task revision");
            Console.WriteLine($"Cortex continuity: {assertions} checks passed with deterministic tool replies and temporary SQLite state; no installed Cortex provider or model was invoked.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing continuity test cleanup outside its temporary workspace.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private const string Space = "project.continuity-fixture";
    private static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);
    private static string Text(RecordedCall call, string name) => call.Call.Arguments.GetProperty(name).GetString() ?? "";
    private static ToolResult Reply(object payload) => new(true, JsonSerializer.Serialize(new { structuredContent = payload }));
    private sealed record RecordedCall(ToolCall Call, ToolExecutionContext Context);

    private sealed class Fixture
    {
        public required StateStore Store { get; init; }
        public required ProjectKnowledge Projects { get; init; }
        public required Configuration Configuration { get; init; }
        public required CortexContinuity Manager { get; init; }
        public required ContinuityTools Tools { get; init; }
        public required AgentProfile Profile { get; init; }
        public required TaskSession Session { get; init; }
        public required AgentJob Job { get; init; }
        public static Fixture Create(string root, string name, bool enabled = true, string? taskId = null)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            var store = new StateStore(Path.Combine(directory, "state")); var events = new EventHub(); var config = new Configuration(store);
            var host = config.Get(); host.CortexNamespaces = ["knowledge.shared", Space]; host.CortexWriteNamespaces = [Space];
            host.McpServers = [new() { Id = "cortex", Executable = Environment.ProcessPath!, Enabled = true, ViaUserConnector = false }];
            config.Save(host);
            store.Put("navigation", "sidebar", new NavigationState { Nodes = [new() { Id = "project", Kind = "project", Title = "Fixture project", Path = directory }] });
            var projects = new ProjectKnowledge(store, config, events);
            projects.Save(new() { ProjectId = "project", CortexServerId = "cortex", ReadNamespaces = ["knowledge.shared", Space],
                WriteNamespace = Space, AutomaticContinuity = enabled });
            var profile = new AgentProfile
            {
                Id = "lead", Name = "Continuity fixture lead", Role = "lead", ModelId = "fixture-model", AllowedRoots = [directory],
                AllowedTools = ["mcp.cortex.cortex_start", "mcp.cortex.cortex_task", "mcp.cortex.cortex_finish"],
                Capabilities = ["mcp.cortex", "cortex.write"]
            };
            Cerebral.Worker.Configuration.ValidateProfile(profile); store.Put("agent", profile.Id, profile);
            var session = new TaskSession { Id = taskId ?? "session-" + name, ProfileId = profile.Id, ProjectId = "project", Title = "Verify continuity",
                Messages = [new() { Sequence = 1, Role = "user", Origin = "user", Content = "Keep this full conversation in Cerebral." }] };
            store.Put("session", session.Id, session);
            var job = new AgentJob { Id = "job-" + name, SessionId = session.Id, ProfileId = profile.Id, Profile = profile,
                Prompt = "Check the bounded task lifecycle.", Status = "running", StepCount = 3 };
            store.Put("job", job.Id, job);
            var tools = new ContinuityTools();
            return new() { Store = store, Projects = projects, Configuration = config, Manager = new(store, config, tools, events), Tools = tools, Profile = profile, Session = session, Job = job };
        }
    }

    private sealed class ContinuityTools : IToolDispatcher
    {
        public List<RecordedCall> Calls { get; } = [];
        public string? ExistingStatus { get; set; }
        public string StartMode { get; set; } = "full";
        public int Revision { get; set; } = 7;
        public string MutationDisposition { get; set; } = "appended";
        public int? MutationRevision { get; set; }
        public Func<RecordedCall, ToolResult?>? OnCall { get; set; }
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var recorded = new RecordedCall(call, context); Calls.Add(recorded);
            if (OnCall?.Invoke(recorded) is { } intercepted) return Task.FromResult(intercepted);
            var taskId = Text(recorded, "taskId");
            if (call.Name.EndsWith("cortex_start", StringComparison.Ordinal))
                return Task.FromResult(Reply(new
                {
                    ns = call.Arguments.GetProperty("namespaces"), mode = StartMode,
                    task = ExistingStatus is null || StartMode == "unchanged" ? null : new { ns = Space, id = taskId, rev = Revision, status = ExistingStatus }
                }));
            if (call.Name.EndsWith("cortex_task", StringComparison.Ordinal) && Text(recorded, "action") == "get")
                return Task.FromResult(Reply(new { effectiveNamespaces = new[] { Space },
                    task = new { @namespace = Space, taskId, revision = Revision, status = ExistingStatus } }));
            var revision = MutationRevision ?? (call.Arguments.TryGetProperty("expectedRevision", out var previous) ? previous.GetInt32() + 1 : 1);
            return Task.FromResult(Reply(new
            {
                disposition = MutationDisposition, effectiveNamespaces = new[] { Space },
                task = new { @namespace = Space, taskId, revision, status = call.Name.EndsWith("cortex_finish", StringComparison.Ordinal) ? "completed" : "open" }
            }));
        }
    }
}
