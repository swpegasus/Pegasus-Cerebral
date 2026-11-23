using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Cerebral.Engine.Activation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static partial class RemoteWorkerManagementTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }

        await using (var h = new Harness())
        {
            var settings = h.Configuration.Get(); settings.Workers[0].AccessKey = ""; h.Configuration.Save(settings);
            var rejected = await h.Call("workers.models.list", new { workerId = "gb10" });
            Check(!rejected.Success && h.FactoryCalls == 0 && rejected.Error!.Contains("peer token", StringComparison.OrdinalIgnoreCase),
                "peer-only registrations cannot create an owner management client");
        }
        await using (var h = new Harness())
        {
            var settings = h.Configuration.Get(); settings.Workers[0].RequiresConfiguration = true; h.Configuration.Save(settings);
            Check(!(await h.Call("workers.models.list", new { workerId = "gb10" })).Success && h.FactoryCalls == 0,
                "unreviewed worker registration stays inactive before any connection");
        }
        foreach (var mismatch in new[] { "controller", "product", "protocol", "coordinator", "identity", "unassigned" })
        {
            await using var h = new Harness(); h.Remote.Mismatch = mismatch;
            var result = await h.Call("workers.models.load", new { workerId = "gb10", id = "remote-model" });
            Check(!result.Success && !h.Remote.Calls.Contains("models.load"), "verified owner preflight blocks " + mismatch + " mismatch before model mutation");
        }
        await using (var h = new Harness())
        {
            var listed = await h.Call("workers.models.list", new { workerId = "gb10" });
            Check(listed.Success && CerebralJson.Read<List<ModelRecord>>(listed.Data).Single().Path == "/srv/cerebral/models/model.gguf" && h.ExplicitOwnerKeyObserved,
                "owner route lists a Linux-path model while a peer token is also present");
            var scanned = await h.Call("workers.models.scan", new { workerId = "gb10" });
            var scan = CerebralJson.Read<ModelRepositoryScan>(scanned.Data);
            Check(scanned.Success && scan.Directory == "/srv/cerebral/models" &&
                scan.Models.Single().Path == "/srv/cerebral/models/discovered.gguf" &&
                scan.Issues.SequenceEqual(["fixture repository warning"]) && scan.Truncated,
                "remote scan preserves the typed repository directory, discovered models, issues and worker truncation flag");

            h.Remote.Scan = new ModelRepositoryScan
            {
                Directory = "/srv/cerebral/models",
                Models = Enumerable.Range(0, 257).Select(index => new DiscoveredModel
                {
                    Path = $"/srv/cerebral/models/model-{index}.gguf",
                    Name = $"Model {index}",
                    SizeBytes = index + 1
                }).ToList(),
                Issues = Enumerable.Range(0, 65).Select(index => "Issue " + index).ToList()
            };
            var projectedResult = await h.Call("workers.models.scan", new { workerId = "gb10" });
            var projected = CerebralJson.Read<ModelRepositoryScan>(projectedResult.Data);
            Check(projectedResult.Success && projected.Models.Count == 256 && projected.Issues.Count == 64 &&
                projected.Issues[^1].Contains("omitted", StringComparison.Ordinal) && projected.Truncated,
                "coordinator scan projection bounds models and issues and truthfully marks omitted remote results");
            var unknown = await h.Call("workers.models.list", new { workerId = "gb10", operation = "settings.save" });
            Check(!unknown.Success && h.Remote.Calls.Count(x => x == "models.list") == 1,
                "closed management fields reject arbitrary command forwarding");
            var actor = new AgentProfile { Id = "admin", Name = "Admin fixture", ModelId = "old", Capabilities = ["administration"] };
            h.Store.Put("agent", actor.Id, actor);
            var agent = await h.Commands.ExecuteAsync(new("workers.models.load", CerebralJson.Element(new { workerId = "gb10", id = "remote-model" }), actor.Id));
            var inference = await h.Commands.ExecuteAsync(new("inference.prepare", CerebralJson.Element(new { }), actor.Id));
            Check(!agent.Success && !inference.Success && !h.Remote.Calls.Contains("models.load"),
                "administration agents cannot invoke owner model management or prepared inference");
            var acquisition = await h.Call("workers.models.import", new { workerId = "gb10", request = new { path = "/srv/model.gguf", name = "Model" } });
            Check(acquisition.Success && h.Remote.Calls.Contains("models.import"), "fixed remote import preserves worker OS paths");
            var download = await h.Call("workers.models.download", new { workerId = "gb10", request = new { url = "https://example.invalid/model.gguf", name = "Model", fileName = "model.gguf" } });
            Check(download.Success && h.Remote.Calls.Contains("models.download"), "current UI download shape forwards url, name and fileName through its fixed route");
            var previousDownloads = h.Remote.Calls.Count(x => x == "models.download");
            var arbitrary = await h.Call("workers.models.download", new { workerId = "gb10", request = new { operation = "settings.save" } });
            Check(!arbitrary.Success && h.Remote.Calls.Count(x => x == "models.download") == previousDownloads, "acquisition request does not accept arbitrary operation fields");
        }
        await using (var h = new Harness())
        {
            h.Remote.Model.State = "loaded";
            var changed = await h.Call("workers.models.idle.set", new { workerId = "gb10", id = "remote-model", idleMinutes = 30 });
            var policy = CerebralJson.Read<ModelRecord>(changed.Data);
            Check(changed.Success && policy.IdleMinutes == 30 && policy.State == "loaded" &&
                policy.Path == "/srv/cerebral/models/model.gguf" && h.ExplicitOwnerKeyObserved &&
                h.Remote.Calls.Count(x => x == "models.idle.set") == 1 &&
                !h.Remote.Calls.Any(x => x is "models.load" or "models.unload" or "models.save"),
                "paired-owner idle editing preserves the loaded remote model without an unload/save cycle");
            foreach (var invalid in new object[]
            {
                new { workerId = "gb10", id = "remote-model", idleMinutes = 10081 },
                new { workerId = "gb10", id = "remote-model", idleMinutes = "30" },
                new { workerId = "gb10", id = "remote-model", idleMinutes = 0, unexpected = true }
            })
                Check(!(await h.Call("workers.models.idle.set", invalid)).Success &&
                    h.Remote.Calls.Count(x => x == "models.idle.set") == 1,
                    "invalid remote idle values or extra fields cannot reach the paired model");
            var actor = new AgentProfile { Id = "idle-admin", Capabilities = ["administration"] };
            h.Store.Put("agent", actor.Id, actor);
            Check(!(await h.Commands.ExecuteAsync(new("workers.models.idle.set", CerebralJson.Element(new
                { workerId = "gb10", id = "remote-model", idleMinutes = 0 }), actor.Id))).Success &&
                h.Remote.Calls.Count(x => x == "models.idle.set") == 1,
                "agent administration does not grant remote owner idle-policy mutation");
        }
        await using (var h = new Harness())
        {
            h.Remote.Scan.Models = Enumerable.Range(0, 513).Select(index => new DiscoveredModel
            {
                Path = $"/srv/cerebral/models/model-{index}.gguf",
                Name = $"Model {index}",
                SizeBytes = index + 1
            }).ToList();
            var result = await h.Call("workers.models.scan", new { workerId = "gb10" });
            Check(!result.Success, "remote scan rejects a model list beyond the worker scanner admission contract");
        }
        await using (var h = new Harness())
        {
            h.Remote.Scan.Directory = new string('x', 32_769);
            var result = await h.Call("workers.models.scan", new { workerId = "gb10" });
            Check(!result.Success, "remote scan rejects an oversized directory before returning it to the coordinator");
        }
        await using (var h = new Harness())
        {
            h.Remote.Scan.Issues = Enumerable.Range(0, 257).Select(index => "Issue " + index).ToList();
            var result = await h.Call("workers.models.scan", new { workerId = "gb10" });
            Check(!result.Success, "remote scan rejects an issue list beyond the bounded admission contract");
        }
        await using (var h = new Harness())
        {
            await h.Scheduler.PauseLocalResourcesAsync(false, default);
            var switched = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10" });
            var settings = h.Configuration.Get();
            var lead = h.Store.Get<AgentProfile>("agent", "lead")!;
            var follower = h.Store.Get<AgentProfile>("agent", "follower")!;
            var pinned = h.Store.Get<AgentProfile>("agent", "pinned")!;
            Check(switched.Success && settings.DefaultModelId == "remote-model" && settings.DefaultModelWorkerId == "gb10" &&
                lead.ModelWorkerId == "gb10" && lead.WorkerId == "local" && follower.ModelWorkerId == "gb10" && follower.WorkerId == "gb10" &&
                pinned.ModelId == "old" && pinned.ModelWorkerId == "local" && pinned.Revision == 1,
                "remote default atomically updates lead/followers, preserves execution workers and leaves pinned models intact");
            Check(h.Scheduler.GetResourcePauseStatus().State == LocalResourceState.Paused && !h.Runtime.IsLoaded("old") && !h.Runtime.IsLoaded("remote-model"),
                "remote default readiness does not reload the paused local GPU");
            var savedFollower = await h.Call("agents.save", follower);
            Check(savedFollower.Success && CerebralJson.Read<AgentProfile>(savedFollower.Data).ModelWorkerId == "gb10",
                "default-following agent saves validate remote ID and placement without local file probes");
            settings = h.Configuration.Get(); settings.DefaultModelWorkerId = "local";
            Check(!(await h.Call("settings.save", settings)).Success && h.Configuration.Get().DefaultModelWorkerId == "gb10",
                "general settings cannot bypass the default placement switch");
            var conflicting = h.Store.Get<AgentProfile>("agent", "follower")!; conflicting.ModelWorkerId = "local";
            var conflict = await h.Call("agents.save", conflicting);
            Check(!conflict.Success && h.Store.Get<AgentProfile>("agent", "follower")!.ModelWorkerId == "gb10",
                "default-following profiles cannot save a same-ID model from the wrong worker");
            var backup = new ConfigurationBackups(h.Store, () => false).Export(owner: true);
            Check(backup.Document.SchemaVersion == ConfigurationBackupContract.SchemaVersion && backup.Document.Host.DefaultModelWorkerId == "gb10",
                "portable backup explicitly records default model placement");
            var reopened = new Configuration(new StateStore(h.Directory));
            Check(reopened.Get().DefaultModelWorkerId == "gb10", "default placement survives reopening the durable SQLite store");
        }
        await using (var h = new Harness(withWorkflows: true))
        {
            var waiting = await h.AddWaitingWorkflowAsync("waiting-retained");
            var frozen = CerebralJson.Element(waiting).GetRawText();
            await h.Scheduler.PauseLocalResourcesAsync(false, default);
            var ordinary = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10" });
            Check(!ordinary.Success && !h.Remote.Calls.Contains("models.load-tracked"),
                "ordinary default switching remains conservative for a paused waiting conversation");
            var switched = await h.Call("models.default.switch", new
                { modelId = "remote-model", modelWorkerId = "gb10", retainWaitingBindings = true });
            var after = h.Workflows!.Store.Get(waiting.Id);
            var accepted = WorkerExecutionSnapshot.Read(after.Submission.SettingsSnapshot);
            Check(switched.Success && switched.Data.GetProperty("retainedWaitingWorkflowIds").EnumerateArray().Single().GetString() == waiting.Id &&
                h.Configuration.Get().DefaultModelWorkerId == "gb10" && CerebralJson.Element(after).GetRawText() == frozen &&
                accepted.Profile.ModelWorkerId == "local" && accepted.Model?.ModelWorkerId == "local",
                "explicit new-admission switch preserves the exact accepted waiting run while future lead work moves remote");
            Check(h.Scheduler.GetResourcePauseStatus().State == LocalResourceState.Paused &&
                h.Store.Get<ModelRecord>("model", "old") is not null && !h.Runtime.IsLoaded("old"),
                "retained switch keeps the local pause and old model registration without loading local weights");
        }
        await using (var h = new Harness(withWorkflows: true))
        {
            await h.AddWaitingWorkflowAsync("waiting-unpaused");
            var result = await h.Call("models.default.switch", new
                { modelId = "remote-model", modelWorkerId = "gb10", retainWaitingBindings = true });
            Check(!result.Success && !h.Remote.Calls.Contains("models.load-tracked") && h.Configuration.Get().DefaultModelId == "old",
                "new-admission switching rejects a waiting run until local execution is durably paused and drained");
            var actor = new AgentProfile { Id = "switch-admin", Name = "Switch admin", Capabilities = ["administration"] };
            h.Store.Put("agent", actor.Id, actor);
            var denied = await h.Commands.ExecuteAsync(new("models.default.switch", CerebralJson.Element(new
                { modelId = "remote-model", modelWorkerId = "gb10", retainWaitingBindings = true }), actor.Id));
            Check(!denied.Success && h.FactoryCalls == 1,
                "administration agents cannot invoke the owner-only retained-waiting default switch");
            var malformed = await h.Call("models.default.switch", new
                { modelId = "remote-model", modelWorkerId = "gb10", retainWaitingBindings = "true" });
            Check(!malformed.Success && h.FactoryCalls == 1,
                "retained-waiting intent requires an exact Boolean and cannot reach remote readiness on malformed input");
            var extra = await h.Call("models.default.switch", new
                { modelId = "remote-model", modelWorkerId = "gb10", retainWaitingBindings = true, unexpected = true });
            Check(!extra.Success && h.FactoryCalls == 1,
                "retained-waiting intent rejects unknown fields before remote readiness");
        }
        await using (var h = new Harness(withWorkflows: true))
        {
            await h.AddUnknownWorkflowAsync("waiting-uncertain");
            await h.Scheduler.PauseLocalResourcesAsync(false, default);
            var result = await h.Call("models.default.switch", new
                { modelId = "remote-model", modelWorkerId = "gb10", retainWaitingBindings = true });
            Check(!result.Success && !h.Remote.Calls.Contains("models.load-tracked"),
                "an uncertain waiting effect cannot be exempted from default-switch safety");
        }
        await using (var h = new Harness(withWorkflows: true))
        {
            h.AddWaitingWorkflowWithSnapshot("waiting-unsupported-snapshot",
                CerebralJson.Element(new { resolvedSettingsVersion = int.MaxValue }));
            await h.Scheduler.PauseLocalResourcesAsync(false, default);
            var result = await h.Call("models.default.switch", new
                { modelId = "remote-model", modelWorkerId = "gb10", retainWaitingBindings = true });
            Check(!result.Success && !h.Remote.Calls.Contains("models.load-tracked") &&
                result.Error!.Contains("frozen model binding", StringComparison.OrdinalIgnoreCase),
                "an unsupported frozen execution snapshot cannot be exempted from default-switch safety");
        }
        await using (var h = new Harness(withWorkflows: true))
        {
            var waiting = await h.AddWaitingWorkflowAsync("waiting-race");
            await h.Scheduler.PauseLocalResourcesAsync(false, default);
            h.Remote.BeforeCount = () => h.Answer(waiting.Id);
            var result = await h.Call("models.default.switch", new
                { modelId = "remote-model", modelWorkerId = "gb10", retainWaitingBindings = true });
            Check(!result.Success && h.Configuration.Get().DefaultModelId == "old" &&
                h.Workflows!.Store.Get(waiting.Id).Status == WorkflowStatus.Queued,
                "a waiting-run revision and state change during remote readiness prevents the default commit");
        }
        await using (var h = new Harness(withWorkflows: true))
        {
            await h.AddWaitingWorkflowAsync("waiting-pause-race");
            await h.Scheduler.PauseLocalResourcesAsync(false, default);
            h.Remote.BeforeCount = () => h.Scheduler.ResumeLocalResourcesAsync().GetAwaiter().GetResult();
            var result = await h.Call("models.default.switch", new
                { modelId = "remote-model", modelWorkerId = "gb10", retainWaitingBindings = true });
            Check(!result.Success && h.Configuration.Get().DefaultModelId == "old" &&
                h.Scheduler.GetResourcePauseStatus().State == LocalResourceState.Active,
                "a changed pause boundary during remote readiness prevents the default commit");
        }
        await using (var h = new Harness())
        {
            var paused = await h.Scheduler.PauseLocalResourcesAsync(false, default);
            Task<ResourcePauseStatus>? resume = null;
            using var resumeAttempted = new ManualResetEventSlim();
            var capturedGeneration = await Task.Run(() => h.Scheduler.WithQuiescentLocalPause(null, updatedAt =>
            {
                resume = Task.Run(async () =>
                {
                    resumeAttempted.Set();
                    return await h.Scheduler.ResumeLocalResourcesAsync();
                });
                if (!resumeAttempted.Wait(TimeSpan.FromSeconds(2)))
                    throw new TimeoutException("The concurrent resume fixture did not start.");
                _ = h.Store.List<AgentProfile>("agent");
                if (resume.IsCompleted)
                    throw new InvalidOperationException("Resume crossed the quiescent pause boundary before its state-store callback completed.");
                return updatedAt;
            })).WaitAsync(TimeSpan.FromSeconds(5));
            var resumed = await (resume ?? throw new InvalidOperationException("The concurrent resume fixture was not created."))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Check(capturedGeneration == paused.UpdatedAt && resumed.State == LocalResourceState.Active,
                "quiescent pause serialization uses one gate-to-store order and a concurrent resume completes without deadlock");
        }
        await using (var h = new Harness(withWorkflows: true))
        {
            var uncertain = await h.AddUnknownWorkflowAsync("waiting-reconcile-contention");
            await h.Scheduler.PauseLocalResourcesAsync(false, default);
            await h.Scheduler.WaitForResourcePauseTransitionAsync(TimeSpan.FromSeconds(5));
            Task<WorkflowRun>? reconcile = null;
            using var reconcileAttempted = new ManualResetEventSlim();
            _ = await Task.Run(() => h.Scheduler.WithQuiescentLocalPause(null, updatedAt =>
            {
                reconcile = Task.Run(async () =>
                {
                    reconcileAttempted.Set();
                    return await h.Scheduler.ReconcileWorkflowAsync(uncertain.Id, default);
                });
                if (!reconcileAttempted.Wait(TimeSpan.FromSeconds(2)))
                    throw new TimeoutException("The concurrent reconciliation fixture did not start.");
                _ = h.Store.List<AgentProfile>("agent");
                if (reconcile.IsCompleted)
                    throw new InvalidOperationException("Reconciliation crossed the quiescent pause boundary before its state-store callback completed.");
                return updatedAt;
            })).WaitAsync(TimeSpan.FromSeconds(5));
            var rejected = false;
            try { _ = await (reconcile ?? throw new InvalidOperationException("The reconciliation fixture was not created."))
                .WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (WorkflowConflictException error) when (error.Message.Contains("paused", StringComparison.OrdinalIgnoreCase))
            { rejected = true; }
            Check(rejected && h.Workflows!.Store.Get(uncertain.Id).Effect?.State == WorkflowEffectState.Unknown,
                "quiescent pause serialization blocks concurrent workflow reconciliation without deadlock or effect replay");
        }
        await using (var h = new Harness())
        {
            h.Remote.Model.ContextTokens = 48128;
            h.Remote.Model.MaximumContextTokens = 48128;
            h.Remote.Model.RecommendedContextTokens = 16384;
            h.Remote.Model.AutomaticContextSizing = true;
            await h.Scheduler.PauseLocalResourcesAsync(false, default);
            var result = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10" });
            Check(result.Success && h.Store.Get<AgentProfile>("agent", "lead")!.ContextTokens == 48128 &&
                h.Store.Get<AgentProfile>("agent", "follower")!.ContextTokens == 48128,
                "unchanged context on a cold remote load uses the verified loaded budget instead of unloaded recommendations");
            Check(h.Store.Get<AgentProfile>("agent", "pinned")!.Revision == 1 &&
                h.Scheduler.GetResourcePauseStatus().State == LocalResourceState.Paused && !h.Runtime.IsLoaded("old"),
                "loaded-budget refresh preserves pinned profiles and the paused local GPU");
        }
        await using (var h = new Harness())
        {
            h.Remote.FailCount = true;
            var result = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10" });
            Check(!result.Success && h.Configuration.Get().DefaultModelId == "old" && h.Configuration.Get().DefaultModelWorkerId == "local" &&
                h.Store.Get<AgentProfile>("agent", "lead")!.Revision == 1 && h.Remote.Calls.Contains("models.unload"),
                "failed remote readiness preserves saved assignments and cleans up only its confirmed started model");
        }
        await using (var h = new Harness())
        {
            h.Remote.FittedContext = 16384;
            var result = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10" });
            Check(result.Success && h.Store.Get<AgentProfile>("agent", "lead")!.ContextTokens == 16384 &&
                h.Store.Get<AgentProfile>("agent", "follower")!.ContextTokens == 16384,
                "confirmed automatic remote context fitting rebases inherited budgets in the same default switch");
        }
        await using (var h = new Harness())
        {
            h.Remote.ChangeConfigurationOnCount = true;
            var result = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10" });
            Check(!result.Success && h.Configuration.Get().DefaultModelId == "old",
                "remote configuration changes cannot masquerade as automatic context fitting");
        }
        await using (var h = new Harness())
        {
            var lead = h.Store.Get<AgentProfile>("agent", "lead")!; lead.RequiredModelCapabilities = [ModelCapabilityIds.ImageInput]; h.Store.Put("agent", lead.Id, lead);
            var result = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10" });
            Check(result.Success && h.Remote.Calls.Contains("models.verify-image") && h.Store.Get<AgentProfile>("agent", "lead")!.ModelWorkerId == "gb10",
                "an image-required default verifies the remote runtime through the fixed owner command without opening remote paths locally");
        }
        await using (var h = new Harness())
        {
            h.Remote.BeforeCount = () =>
            {
                var lead = h.Store.Get<AgentProfile>("agent", "lead")!; lead.Purpose = "New owner edit"; lead.Revision++;
                h.Store.Put("agent", lead.Id, lead);
            };
            var result = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10" });
            Check(!result.Success && h.Configuration.Get().DefaultModelId == "old" && h.Store.Get<AgentProfile>("agent", "lead")!.Purpose == "New owner edit" &&
                h.Store.Get<AgentProfile>("agent", "follower")!.Revision == 1,
                "owner edits during remote load prevent a partial or stale default commit");
        }
        await using (var h = new Harness())
        {
            h.Remote.BeforeCount = () =>
            {
                var settings = h.Configuration.Get(); settings.Workers[0].AccessKey = new string('b', 48); h.Configuration.Save(settings);
            };
            var result = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10" });
            Check(!result.Success && h.Configuration.Get().DefaultModelId == "old" && !h.Remote.Calls.Contains("models.unload"),
                "owner-key revocation prevents commit and further cleanup dispatch to a stale owner connection");
        }
        await using (var h = new Harness())
        {
            var originalSettingsRevision = h.Configuration.Get().Revision;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(h.Directory, "cerebral.db") }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER fixture_commit_failure BEFORE UPDATE OF body ON records WHEN NEW.kind='agent' AND NEW.id='lead' BEGIN SELECT RAISE(ABORT, 'fixture commit failure'); END;";
                command.ExecuteNonQuery();
            }
            var result = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10" });
            var reopened = new StateStore(h.Directory);
            var settings = new Configuration(reopened).Get();
            Check(!result.Success && settings.DefaultModelId == "old" && settings.DefaultModelWorkerId == "local" && settings.Revision == originalSettingsRevision &&
                reopened.Get<AgentProfile>("agent", "follower")!.Revision == 1 && reopened.Get<AgentProfile>("agent", "lead")!.Revision == 1,
                "SQLite failure during follower commit rolls back host placement and every earlier profile write after reopening");
        }
        await using (var h = new Harness())
        {
            h.Store.Put("job", "active", new AgentJob { Id = "active", ProfileId = "follower", Profile = h.Store.Get<AgentProfile>("agent", "follower")!, Status = "queued" });
            var result = await h.Call("models.default.switch", new { modelId = "remote-model", modelWorkerId = "gb10" });
            Check(!result.Success && !h.Remote.Calls.Contains("models.load-tracked"), "accepted follower work blocks default reassignment before remote load");
        }
        await CheckInferenceAsync(Check);
        checks += await RunRemoteModelRecoveryChecksAsync();
        Console.WriteLine($"Remote owner management: {checks} deterministic fake-transport/real-SQLite checks passed; no remote connection or model process was used.");
    }

    private static Dictionary<string, object?> GenerationRequest(string workerId = "gb10") => new()
    {
        ["workerId"] = workerId, ["modelId"] = "remote-model",
        ["messages"] = new[] { new ModelMessage("system", "Interpret the request."), new ModelMessage("user", "Preserve café and line breaks.\nSecond line.") },
        ["maxTokens"] = 768, ["temperature"] = 0.1, ["json"] = false,
        ["responseSchema"] = null, ["reasoningMode"] = "off", ["reasoningTokens"] = 128
    };

    private static async Task CheckInferenceAsync(Action<bool, string> check)
    {
        await using (var h = new Harness())
        {
            var catalog = new SkillCatalog(h.Store);
            var skill = catalog.Install(new SkillManifest { Id = "remote-fence", Name = "Remote fence", Version = "1.0.0",
                Description = "Check remote input admission.", Triggers = ["remote-fence"] }, "REMOTE-SKILL-CANARY", "fixture", null);
            skill = catalog.Configure(skill.Summary.Id, true, [new("global")], skill.Summary.Revision);
            var profile = h.Store.Get<AgentProfile>("agent", "lead")!;
            var projection = catalog.CaptureModelProjection(catalog.Resolve(profile, null), profile, null, "remote-fence");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Remote.BeforeManagementStatusAsync = async token =>
            { entered.TrySetResult(true); await release.Task.WaitAsync(token); };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using (SkillDispatchFence.Enter(() => catalog.RequireProjectionCurrent(projection, profile, null)))
            {
                var pending = h.Commands.ExecuteAsync(new("workers.inference.generate", CerebralJson.Element(GenerationRequest())), deadline.Token);
                try
                {
                    await entered.Task.WaitAsync(deadline.Token);
                    catalog.Configure(skill.Summary.Id, false, skill.Summary.Bindings, skill.Summary.Revision);
                }
                finally { release.TrySetResult(true); }
                var result = await pending;
                check(!result.Success && !h.Remote.Calls.Contains("inference.generate") && h.Remote.Disposals == 1 &&
                      ModelExecutionAccounting.For(h.Store).Snapshot().Entries.Length == 0,
                    "revocation during remote connection flows to the dispatch fence and retires unsent ownership without inference");
            }
            SkillDispatchFence.RequireCurrent();
        }
        await using (var h = new Harness())
        {
            var result = await h.Call("workers.inference.generate", GenerationRequest());
            var sent = h.Remote.GenerationArguments!.Value;
            check(result.Success && result.Data.GetProperty("text").GetString() == h.Remote.GenerationText &&
                  sent.EnumerateObject().Count() == 8 && !sent.TryGetProperty("workerId", out _) &&
                  sent.GetProperty("modelId").GetString() == "remote-model" && sent.GetProperty("maxTokens").GetInt32() == 768 &&
                  sent.GetProperty("messages")[1].GetProperty("content").GetString() == "Preserve café and line breaks.\nSecond line." &&
                  h.ExplicitOwnerKeyObserved && h.Remote.Calls.Count(x => x == "worker.management.status") == 2 &&
                  h.Remote.Calls.Count(x => x == "inference.generate") == 1 && !h.Runtime.IsLoaded("old") && !h.Runtime.IsLoaded("remote-model"),
                "fixed remote owner inference preserves arguments and generated text, revalidates pairing, and never loads a local model");
            var actor = new AgentProfile { Id = "inference-admin", Capabilities = ["administration"] };
            h.Store.Put("agent", actor.Id, actor);
            check(!(await h.Commands.ExecuteAsync(new("workers.inference.generate", CerebralJson.Element(GenerationRequest()), actor.Id))).Success &&
                  h.Remote.Calls.Count(x => x == "inference.generate") == 1,
                "administration agents cannot invoke remote owner inference");
        }
        await using (var h = new Harness())
        {
            foreach (var worker in new[] { "local", "missing-worker" })
                check(!(await h.Call("workers.inference.generate", GenerationRequest(worker))).Success && h.FactoryCalls == 0,
                    "remote inference rejects " + worker + " before transport creation instead of falling back locally");
            foreach (var mutation in new Action<Dictionary<string, object?>>[]
            {
                args => args["operation"] = "settings.save",
                args => args.Remove("maxTokens"),
                args => args["modelId"] = "../other",
                args => args["maxTokens"] = AgentTokenLimits.MaximumResponseTokens + 1,
                args => args["maxTokens"] = "768",
                args => args["temperature"] = 2.01,
                args => args["json"] = "false",
                args => args["reasoningMode"] = "guess",
                args => args["reasoningTokens"] = AgentTokenLimits.MaximumReasoningTokens + 1,
                args => args["responseSchema"] = new[] { "object" },
                args => args["messages"] = Array.Empty<ModelMessage>(),
                args => args["messages"] = Enumerable.Repeat(new ModelMessage("user", ""), 10_001).ToArray(),
                args => args["messages"] = new[] { new ModelMessage("developer", "Unexpected role") },
                args => args["messages"] = new[] { new { role = "user", content = (string?)null } },
                args => args["messages"] = new[] { new ModelMessage("user", "Nul\0content") },
                args => args["messages"] = new[] { new { role = "user", content = "Text", extra = "unsupported" } },
                args => args["messages"] = new[] { new ModelMessage("user", new string('x', 1_048_577)) }
            })
            {
                var invalid = GenerationRequest(); mutation(invalid);
                check(!(await h.Call("workers.inference.generate", invalid)).Success && h.FactoryCalls == 0,
                    "invalid or oversized remote inference arguments stay outside the owner transport");
            }
            var serialized = CerebralJson.Element(GenerationRequest()).GetRawText();
            using var document = JsonDocument.Parse("{\"workerId\":\"gb10\"," + serialized[1..]);
            check(!(await h.Call("workers.inference.generate", document.RootElement)).Success && h.FactoryCalls == 0,
                "duplicate inference fields cannot bypass the fixed route admission");
        }
        await using (var h = new Harness())
        {
            h.Remote.Mismatch = "controller";
            check(!(await h.Call("workers.inference.generate", GenerationRequest())).Success && !h.Remote.Calls.Contains("inference.generate"),
                "a mismatched coordinator pairing prevents remote generation dispatch");
        }
        await using (var h = new Harness())
        {
            h.Remote.BeforeGenerate = () => h.Remote.Mismatch = "identity";
            check(!(await h.Call("workers.inference.generate", GenerationRequest())).Success && h.Remote.Calls.Count(x => x == "inference.generate") == 1,
                "a changed worker identity after generation prevents adoption of its result");
        }
        await using (var h = new Harness())
        {
            h.Remote.BeforeGenerate = () => { var settings = h.Configuration.Get(); settings.Workers[0].AccessKey = new string('b', 48); h.Configuration.Save(settings); };
            check(!(await h.Call("workers.inference.generate", GenerationRequest())).Success && h.Remote.Calls.Count(x => x == "health") == 1,
                "revoking the owner connection during generation blocks result adoption and further stale transport requests");
        }
        await using (var h = new Harness())
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            check(!(await h.Commands.ExecuteAsync(new("workers.inference.generate", CerebralJson.Element(GenerationRequest())), cancelled.Token)).Success && h.FactoryCalls == 0,
                "cancelled remote inference never creates an owner connection");
            using var pending = new CancellationTokenSource(); h.Remote.WaitForGenerationCancellation = true; h.Remote.BeforeGenerate = pending.Cancel;
            check(!(await h.Commands.ExecuteAsync(new("workers.inference.generate", CerebralJson.Element(GenerationRequest())), pending.Token)).Success &&
                  h.Remote.GenerationToken.IsCancellationRequested && h.Remote.Calls.Count(x => x == "inference.generate") == 1,
                "in-flight cancellation reaches the actual remote generation transport without local retry");
        }
        await using (var h = new Harness())
        {
            h.Remote.GenerationResult = CerebralJson.Element(new { text = "Generated text", operation = "unexpected" });
            check(!(await h.Call("workers.inference.generate", GenerationRequest())).Success,
                "a remote result outside the fixed generated-text shape is rejected");
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "cerebral-remote-management-" + Guid.NewGuid().ToString("N"));
        public StateStore Store { get; }
        public Configuration Configuration { get; }
        public ModelRuntime Runtime { get; }
        public JobScheduler Scheduler { get; }
        public CommandDispatcher Commands { get; }
        public WorkerWorkflows? Workflows { get; }
        public EngineController? Engine { get; }
        public FakeOwner Remote { get; }
        public EventHub Events { get; } = new();
        public int FactoryCalls;
        public bool ExplicitOwnerKeyObserved;
        private readonly ToolDispatcher tools;
        public Harness(bool withWorkflows = false, bool withEngine = false)
        {
            if (withEngine && !withWorkflows) throw new ArgumentException("Engine recovery fixtures require workflow hosting.");
            System.IO.Directory.CreateDirectory(Directory);
            Store = new(Directory); Configuration = new(Store); var events = Events;
            var settings = Configuration.Get(); settings.DefaultModelId = "old";
            settings.ModelDirectory = Path.Combine(Directory, "models"); settings.ProjectsDirectory = Path.Combine(Directory, "projects");
            settings.SharedDirectory = Path.Combine(Directory, "shared"); settings.ToolsDirectory = Path.Combine(Directory, "tools");
            settings.Workers = [new() { Id = "gb10", Name = "GB10", Url = "http://127.0.0.1:2667", AccessKey = new string('a', 48), PeerToken = "fixture-peer-token" }];
            Configuration.Save(settings);
            Remote = new(settings.InstallationId);
            Store.Put("model", "old", new ModelRecord { Id = "old", Name = "Old", Path = Path.Combine(Directory, "old.gguf"), Architecture = "llama", MetadataSource = "gguf", ChatTemplate = "alpaca" });
            foreach (var profile in new[]
            {
                new AgentProfile { Id = "lead", Name = "Lead", Role = "lead", ModelId = "old", Revision = 1, FollowsDefaultModel = true },
                new AgentProfile { Id = "follower", Name = "Follower", ModelId = "old", WorkerId = "gb10", Revision = 1, FollowsDefaultModel = true },
                new AgentProfile { Id = "pinned", Name = "Pinned", ModelId = "old", Revision = 1, FollowsDefaultModel = false }
            }) Store.Put("agent", profile.Id, profile);
            Runtime = new(Store, Configuration.Get, events); tools = new(Configuration.Get);
            if (withEngine)
            {
                var engineRoot = Path.Combine(Directory, "engines");
                EngineHostTests.Pack(engineRoot, "A", true); EngineHostTests.Pack(engineRoot, "B", false);
                Engine = new(engineRoot, Path.Combine(Directory, "engine-state"));
                Engine.InitializeAsync().GetAwaiter().GetResult();
            }
            if (withWorkflows)
            {
                var effects = new WorkerWorkflowEffects(Store, Configuration, Runtime, tools);
                Workflows = new WorkerWorkflows(Store, Configuration, events, [new ConversationWorkflowModule(), effects],
                    [ConversationWorkflowModule.PackageBinding, new(effects.Id, WorkerWorkflowEffects.ModuleVersion, true, [])], tools,
                    engine: Engine);
            }
            Scheduler = new(Store, Configuration, new NoExecutor(), Runtime, events, NullLogger<JobScheduler>.Instance,
                Workflows, resourceModels: new NoModels(), resourcePeers: new NoPeers());
            Commands = new(Store, Configuration, Runtime, Scheduler, tools, events, remoteWorkerClientFactory: worker =>
            { FactoryCalls++; ExplicitOwnerKeyObserved = worker.AccessKey.Length == 48 && worker.PeerToken.Length > 0; return Remote; },
                workflows: Workflows);
        }
        public Task<CommandResult> Call(string operation, object args) => Commands.ExecuteAsync(new(operation, CerebralJson.Element(args)));
        public async Task<WorkflowRun> AddWaitingWorkflowAsync(string id)
        {
            if (Workflows is null) throw new InvalidOperationException("Workflow fixture was not enabled.");
            var run = await Workflows.SubmitAsync(new(id, "chat-" + id, "lead", ConversationWorkflowModule.DefinitionId,
                ConversationWorkflowModule.DefinitionVersion, "Wait for the owner answer.", null, WorkflowContract.EmptyObject), null, default);
            var claimed = Workflows.Store.TryClaim(run.Id) ?? throw new InvalidOperationException("Fixture workflow was not claimable.");
            return Workflows.Store.ApplyStep(run.Id, claimed.Revision, new(claimed.State,
                new AskWorkflowQuestions([new("answer", "Answer")] )));
        }
        public async Task<WorkflowRun> AddUnknownWorkflowAsync(string id)
        {
            if (Workflows is null) throw new InvalidOperationException("Workflow fixture was not enabled.");
            var run = await Workflows.SubmitAsync(new(id, "chat-" + id, "lead", ConversationWorkflowModule.DefinitionId,
                ConversationWorkflowModule.DefinitionVersion, "Retain an uncertain effect.", null, WorkflowContract.EmptyObject), null, default);
            var claimed = Workflows.Store.TryClaim(run.Id) ?? throw new InvalidOperationException("Fixture workflow was not claimable.");
            var descriptor = new WorkflowCommandDescriptor("fixture.effect", 1, "Fixture", WorkflowContract.EmptyObject,
                WorkflowContract.EmptyObject, [], WorkflowReplayPolicy.Never);
            var prepared = Workflows.Store.ApplyStep(run.Id, claimed.Revision, new(claimed.State,
                new InvokeWorkflowCommand(descriptor.Id, descriptor.Version, WorkflowContract.EmptyObject)), descriptor);
            var dispatched = Workflows.Store.DispatchEffect(run.Id, prepared.Revision);
            return Workflows.Store.CompleteEffect(run.Id, dispatched.Effect!.Id,
                new(WorkflowEffectState.Unknown, Error: "Fixture outcome is intentionally unknown."));
        }
        public WorkflowRun AddWaitingWorkflowWithSnapshot(string id, JsonElement snapshot)
        {
            if (Workflows is null) throw new InvalidOperationException("Workflow fixture was not enabled.");
            var run = Workflows.Store.Submit(new(id, "chat-" + id, "lead", ConversationWorkflowModule.DefinitionId,
                ConversationWorkflowModule.DefinitionVersion, "Wait with a frozen execution snapshot.", null,
                WorkflowContract.EmptyObject, snapshot));
            var claimed = Workflows.Store.TryClaim(run.Id) ?? throw new InvalidOperationException("Fixture workflow was not claimable.");
            return Workflows.Store.ApplyStep(run.Id, claimed.Revision, new(claimed.State,
                new AskWorkflowQuestions([new("answer", "Answer")])));
        }
        public WorkflowRun Answer(string id)
        {
            if (Workflows is null) throw new InvalidOperationException("Workflow fixture was not enabled.");
            var waiting = Workflows.Store.Get(id);
            var questions = waiting.Questions ?? throw new InvalidOperationException("Fixture has no questions.");
            return Workflows.Store.Answer(new("answer-" + id, id, questions.Id, questions.Revision,
                new Dictionary<string, string> { ["answer"] = "Continue" }));
        }
        public async ValueTask DisposeAsync()
        {
            Scheduler.Dispose(); await tools.DisposeAsync(); await Runtime.DisposeAsync();
            if (Engine is not null) await Engine.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (!Path.GetFullPath(Directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected test root.");
            System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class FakeOwner(string controller) : IRemoteWorkerOwnerClient
    {
        public string? Mismatch;
        public bool FailCount;
        public int? FittedContext;
        public bool ChangeConfigurationOnCount;
        public Action? BeforeCount;
        public Action? BeforeGenerate;
        public Action? BeforeUnload;
        public Func<CancellationToken, Task>? BeforeManagementStatusAsync;
        public int Disposals;
        public TaskCompletionSource<bool>? UnloadStarted;
        public TaskCompletionSource<bool>? ReleaseUnload;
        public bool WaitForGenerationCancellation;
        public CancellationToken GenerationToken;
        public JsonElement? GenerationArguments;
        public JsonElement? GenerationResult;
        public string GenerationText = "Remote fixture café.\nSecond line.";
        public List<string> Calls = [];
        public ModelRepositoryScan Scan = new()
        {
            Directory = "/srv/cerebral/models",
            Models =
            [
                new DiscoveredModel
                {
                    Path = "/srv/cerebral/models/discovered.gguf",
                    Name = "Discovered",
                    SizeBytes = 4096,
                    Architecture = "qwen3",
                    Quantization = "Q4_K_M"
                }
            ],
            Issues = ["fixture repository warning"],
            Truncated = true
        };
        public ModelRecord Model = new() { Id = "remote-model", Name = "Remote model", Path = "/srv/cerebral/models/model.gguf", Architecture = "llama",
            MetadataSource = "gguf", ContextTokens = 8192, RecommendedContextTokens = 8192, ChatTemplate = "alpaca", State = "unloaded",
            CapabilityStatus = [new() { Id = ModelCapabilityIds.TextGeneration, Usable = true }] };
        public async Task<T> GetAsync<T>(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls.Add(operation);
            if (operation == "worker.management.status" && BeforeManagementStatusAsync is not null)
                await BeforeManagementStatusAsync(cancellationToken);
            if (operation == "inference.generate")
            {
                GenerationArguments = CerebralJson.Element(arguments); GenerationToken = cancellationToken;
                BeforeGenerate?.Invoke();
                if (WaitForGenerationCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return CerebralJson.Read<T>(GenerationResult ?? CerebralJson.Element(new { text = GenerationText }));
            }
            if (operation == "models.unload")
            {
                BeforeUnload?.Invoke(); UnloadStarted?.TrySetResult(true);
                if (ReleaseUnload is not null) await ReleaseUnload.Task.WaitAsync(cancellationToken);
                return CerebralJson.Read<T>(CerebralJson.Element(Unloaded()));
            }
            object result = operation switch
            {
                "health" => new HostInfo(Mismatch == "product" ? "Other" : "Pegasus Cerebral", "fixture", Mismatch == "protocol" ? 99 : 1,
                    "GB10", "Arm64", "Linux", Mismatch == "coordinator", "/srv/cerebral", InstallationId: "remote-installation"),
                "worker.management.status" => new WorkerManagementStatus(Mismatch == "identity" ? "other-installation" : "remote-installation",
                    Mismatch == "unassigned" ? "awaiting-claim" : "assigned", Mismatch == "controller" ? "other-controller" : controller, "Fixture", null),
                "models.list" => new List<ModelRecord> { Model },
                "models.scan" => Scan,
                "models.inspect" or "models.import" or "models.download" or "models.save" => Model,
                "models.idle.set" => Idle(CerebralJson.Element(arguments)),
                "models.load" => Loaded(),
                "models.load-tracked" => new WorkerModelLoadResult(Loaded(), true),
                "inference.count" => Count(),
                "models.verify-image" => ImageProof(),
                _ => throw new InvalidOperationException("Unexpected fixture operation: " + operation)
            };
            return CerebralJson.Read<T>(CerebralJson.Element(result));
        }
        private ModelRecord Loaded() { Model.State = "loaded"; if (FittedContext is { } context) Model.ContextTokens = context; return Model; }
        private ModelRecord Idle(JsonElement args)
        {
            if (args.EnumerateObject().Count() != 2 || args.GetProperty("id").GetString() != Model.Id)
                throw new InvalidOperationException("Incorrect fixed remote idle request.");
            Model.IdleMinutes = args.GetProperty("idleMinutes").GetInt32(); return Model;
        }
        private object Unloaded() { Model.State = "unloaded"; return new { unloaded = Model.Id }; }
        private int Count() { BeforeCount?.Invoke(); if (ChangeConfigurationOnCount) Model.MaximumContextTokens = 32768; if (FailCount) throw new InvalidOperationException("Fixture template failure."); return 24; }
        private ModelImageVerification ImageProof()
        {
            Model.CapabilityStatus.Add(new() { Id = ModelCapabilityIds.ImageInput, Usable = true });
            return new(Model.Id, "fixture-instance", "fixture-adapter", "1", DateTimeOffset.UtcNow, 24, 2);
        }
        public void Dispose() { Disposals++; }
    }
    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new Exception("No fixture agent execution.");
    }
    private sealed class NoModels : IResourcePauseModelControl
    {
        public Task<IReadOnlyList<string>> BeginPauseAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task UnloadPausedAsync(CancellationToken token) => Task.CompletedTask;
        public Task ResumePausedAsync(IReadOnlyList<string> ids, CancellationToken token) => Task.CompletedTask;
        public Task ReopenPausedAdmissionAsync(Action durableCommit, CancellationToken token) { durableCommit(); return Task.CompletedTask; }
    }
    private sealed class NoPeers : IResourcePausePeerProbe
    {
        public Task<IReadOnlyList<ResourcePausePeerStatus>> ProbeAsync(IReadOnlyList<WorkerRegistration> workers, CancellationToken token) => Task.FromResult<IReadOnlyList<ResourcePausePeerStatus>>([]);
    }
}
