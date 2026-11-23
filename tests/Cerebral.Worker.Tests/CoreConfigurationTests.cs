using Cerebral.Core;
using System.Text.Json.Nodes;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class CoreConfigurationTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-core-configuration-" + Guid.NewGuid().ToString("N")));
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
        bool Rejects(Action action) { try { action(); return false; } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return true; } }
        try
        {
            var store = new StateStore(root); var configuration = new Configuration(store);
            var settings = configuration.Get();
            Check(settings.ProjectsDirectory == Path.Combine(root, "Projects") && Directory.Exists(settings.ProjectsDirectory),
                "first configuration creates the default project root outside replaceable output");
            Check(Directory.Exists(settings.ModelDirectory) && Directory.Exists(settings.SharedDirectory) &&
                  Directory.Exists(settings.ToolsDirectory) && Directory.Exists(Path.Combine(root, "PluginData")) &&
                  Directory.Exists(Path.Combine(root, "Artifacts", "Attachments")),
                "first configuration creates the Worker model, tool, plugin-data, shared and attachment roots");
            Check(settings.MaxLogicalProcessorCount == 0 && settings.AllowedLogicalProcessors.Count == 0,
                "new hosts reserve logical processor 0 through the empty-list default without imposing an additional maximum");
            Check(settings.WorkflowIdleExpiryDays == 7 && store.ReadWorkflowIdleExpiryDays() == 7,
                "interrupted workflow expiry defaults to seven days");
            var preservedNarrowRoot = Path.Combine(root, "NarrowProjects");
            store.Put("agent", "managed-local", new AgentProfile
            {
                Id = "managed-local", Revision = 3, Name = "Managed local", ModelId = "fixture",
                WorkerId = "local", ModelWorkerId = "local", AllowedRoots = [settings.ProjectsDirectory, preservedNarrowRoot]
            });
            store.Put("agent", "narrow-local", new AgentProfile
            {
                Id = "narrow-local", Revision = 4, Name = "Narrow local", ModelId = "fixture",
                WorkerId = "local", ModelWorkerId = "local", AllowedRoots = [preservedNarrowRoot]
            });
            store.Put("agent", "remote-root", new AgentProfile
            {
                Id = "remote-root", Revision = 5, Name = "Remote", ModelId = "fixture",
                WorkerId = "remote", ModelWorkerId = "remote", AllowedRoots = [settings.ProjectsDirectory]
            });
            var custom = Path.Combine(root, "ChosenProjects"); settings.ProjectsDirectory = custom;
            settings.JobHistoryLimit = 2; settings.JobHistoryDays = 7; configuration.Save(settings);
            Check(new Configuration(store).Get().ProjectsDirectory == custom && Directory.Exists(custom),
                "chosen project root survives configuration reload");
            Check(store.Get<AgentProfile>("agent", "managed-local") is { Revision: 4 } managed &&
                  managed.AllowedRoots.SequenceEqual([custom, preservedNarrowRoot]) &&
                  store.Get<AgentProfile>("agent", "narrow-local") is { Revision: 4 } narrow &&
                  narrow.AllowedRoots.SequenceEqual([preservedNarrowRoot]) &&
                  store.Get<AgentProfile>("agent", "remote-root") is { Revision: 5 } remote &&
                  remote.AllowedRoots.SequenceEqual([Path.Combine(root, "Projects")]),
                "changing the configured project root updates only exact managed roots on local profiles");
            var firstOpenCopy = configuration.Get(); var secondOpenCopy = configuration.Get();
            var expectedMemory = firstOpenCopy.ReservedMemoryMb + 64; var priorRevision = firstOpenCopy.Revision;
            firstOpenCopy.ReservedMemoryMb = expectedMemory; configuration.Save(firstOpenCopy);
            Check(firstOpenCopy.Revision == priorRevision + 1 && configuration.Get().Revision == firstOpenCopy.Revision,
                "successful settings save advances the persisted revision");
            secondOpenCopy.ReservedMemoryMb = expectedMemory + 64;
            Check(Rejects(() => configuration.Save(secondOpenCopy)) && configuration.Get().ReservedMemoryMb == expectedMemory,
                "stale settings copy is rejected without overwriting a newer save");
            var staleProjectRoot = Path.Combine(root, "MustNotBeCreatedByStaleDraft");
            secondOpenCopy.ProjectsDirectory = staleProjectRoot;
            Check(Rejects(() => configuration.Save(secondOpenCopy)) && !Directory.Exists(staleProjectRoot) &&
                  configuration.Get().ProjectsDirectory == custom,
                "expected-revision conflict is checked before a stale folder draft can create filesystem state");
            var autosaved = configuration.Get();
            autosaved.QueueCapacity = 47; autosaved.ReservedOwnerSlots = 0; autosaved.WorkflowIdleExpiryDays = 14;
            configuration.Save(autosaved);
            var restartedConfiguration = new Configuration(new StateStore(root));
            var afterRestart = restartedConfiguration.Get();
            Check(afterRestart.QueueCapacity == 47 && afterRestart.ReservedOwnerSlots == 0 && afterRestart.Revision == autosaved.Revision,
                "valid queue autosave values and their acknowledged revision survive a fresh SQLite-backed service store");
            Check(afterRestart.WorkflowIdleExpiryDays == 14 && new StateStore(root).ReadWorkflowIdleExpiryDays() == 14,
                "the configured expiry survives reopening and is visible to the scheduler");
            var invalidExpiry = configuration.Get();
            invalidExpiry.WorkflowIdleExpiryDays = -1;
            Check(Rejects(() => configuration.Save(invalidExpiry)) && store.ReadWorkflowIdleExpiryDays() == 14,
                "invalid expiry is rejected without changing the configured value");
            var processorSettings = configuration.Get();
            processorSettings.MaxLogicalProcessorCount = 2; processorSettings.AllowedLogicalProcessors = [3, 1];
            configuration.Save(processorSettings);
            var persistedProcessorSettings = new Configuration(new StateStore(root)).Get();
            Check(persistedProcessorSettings.MaxLogicalProcessorCount == 2 && persistedProcessorSettings.AllowedLogicalProcessors.SequenceEqual([1, 3]),
                "explicit logical processor selection is canonicalized as a set and survives a fresh SQLite-backed service store");
            foreach (var invalidPolicy in new[]
            {
                new HostSettings { MaxLogicalProcessorCount = -1 },
                new HostSettings { MaxLogicalProcessorCount = 1025 },
                new HostSettings { AllowedLogicalProcessors = [1, 1] },
                new HostSettings { AllowedLogicalProcessors = [-1] },
                new HostSettings { AllowedLogicalProcessors = [1024] }
            })
            {
                var invalid = configuration.Get();
                invalid.MaxLogicalProcessorCount = invalidPolicy.MaxLogicalProcessorCount;
                invalid.AllowedLogicalProcessors = invalidPolicy.AllowedLogicalProcessors;
                Check(Rejects(() => configuration.Save(invalid)),
                    "logical processor maximum, identifiers and duplicate entries use bounded service validation");
            }
            settings = configuration.Get(); settings.ProjectsDirectory = ""; configuration.Save(settings);
            Check(configuration.Get().ProjectsDirectory == custom, "older settings save without project field preserves the configured project root");
            settings = configuration.Get(); settings.ProjectsDirectory = Path.Combine(AppContext.BaseDirectory, "Projects");
            Check(Rejects(() => configuration.Save(settings)) && configuration.Get().ProjectsDirectory == custom,
                "project root cannot be moved inside replaceable program files");
            settings = configuration.Get(); settings.ProjectsDirectory = "relative";
            Check(Rejects(() => configuration.Save(settings)), "relative project root is rejected");
            settings = configuration.Get(); settings.JobHistoryLimit = 0;
            Check(Rejects(() => configuration.Save(settings)) && configuration.Get().JobHistoryLimit == 2,
                "invalid history quota leaves saved settings unchanged");
            settings = configuration.Get(); settings.JobHistoryDays = 3651;
            Check(Rejects(() => configuration.Save(settings)), "history age display bound is validated");
            settings = configuration.Get(); settings.EngineEventHistoryMegabytes = 0;
            Check(Rejects(() => configuration.Save(settings)) && configuration.Get().EngineEventHistoryMegabytes == 64,
                "Engine rolling-event count, MiB and age settings use bounded service validation");

            var pluginProfile = new AgentProfile { Id = "module-helper", Name = "Module helper", ModelId = "fixture" };
            pluginProfile.PluginSettings["fixture.module"] = CerebralJson.Element(new { firstTarget = 2048, secondTarget = 16384 });
            Configuration.ValidateProfile(pluginProfile);
            var persistedPlugin = CerebralJson.Read<AgentProfile>(CerebralJson.Element(pluginProfile));
            Check(persistedPlugin.PluginSettings["fixture.module"].GetProperty("firstTarget").GetInt32() == 2048,
                "opaque module-owned agent settings round-trip without adding module vocabulary to core");
            var malformedPlugin = CerebralJson.Read<AgentProfile>(CerebralJson.Element(pluginProfile));
            malformedPlugin.PluginSettings["fixture.module"] = CerebralJson.Element("not an object");
            Check(Rejects(() => Configuration.ValidateProfile(malformedPlugin)),
                "core bounds module settings while leaving their object schema to the owning module");

            var legacy = new StateStore(Path.Combine(root, "legacy"));
            var legacySettings = new HostSettings { ModelDirectory = Path.Combine(root, "existing-models"), SharedDirectory = Path.Combine(root, "existing-shared"), AccessKey = new string('a', 24) };
            var legacyBody = JsonNode.Parse(CerebralJson.Element(legacySettings).GetRawText())!.AsObject();
            legacyBody.Remove("workflowIdleExpiryDays");
            legacy.Put("settings", "host", legacyBody);
            Check(legacy.ReadWorkflowIdleExpiryDays() == 7, "an older saved host without the setting uses the seven-day default");
            var migrated = new Configuration(legacy).Get();
            Check(migrated.ProjectsDirectory == Path.Combine(root, "legacy", "Projects") && Directory.Exists(migrated.ProjectsDirectory) &&
                migrated.ModelDirectory == Path.Combine(root, "existing-models"), "legacy settings gain only the missing project default without moving the model repository");
            legacyBody["workflowIdleExpiryDays"] = true;
            legacy.Put("settings", "host", legacyBody);
            Check(legacy.ReadWorkflowIdleExpiryDays() == 0,
                "a corrupt Boolean policy cannot be interpreted as one day of idle expiry");

            var installedLegacy = new StateStore(Path.Combine(root, "installed-legacy"));
            var oldInstalledRoot = Path.Combine(installedLegacy.DirectoryPath, "Projects");
            var configuredInstalledRoot = Path.Combine(root, "CerebralProjects");
            installedLegacy.Put("settings", "host", new HostSettings
            {
                ModelDirectory = Path.Combine(root, "installed-models"), SharedDirectory = Path.Combine(root, "installed-shared"),
                ToolsDirectory = Path.Combine(root, "installed-tools"), ProjectsDirectory = configuredInstalledRoot,
                AccessKey = new string('b', 24)
            });
            installedLegacy.Put("agent", "legacy-lead", new AgentProfile
            {
                Id = "legacy-lead", Revision = 8, Name = "Cerebral", Role = "lead", ModelId = "fixture",
                WorkerId = "local", ModelWorkerId = "local", AllowedRoots = [oldInstalledRoot]
            });
            _ = new Configuration(installedLegacy).Get();
            Check(installedLegacy.Get<AgentProfile>("agent", "legacy-lead") is { Revision: > 8 } legacyLead &&
                  legacyLead.AllowedRoots.SequenceEqual([configuredInstalledRoot]),
                "an upgraded installation repairs the exact legacy data-tree grant to its configured project root");

            var activeStatuses = new[] { "queued", "running", "waiting-human", "waiting-helper", "remote-waiting", "remote-unconfirmed" };
            foreach (var status in activeStatuses) store.Put("job", status, new AgentJob { Id = status, Status = status });
            var terminalStatuses = new[] { "completed", "failed", "cancelled", "interrupted", "incomplete" };
            for (var i = 0; i < terminalStatuses.Length; i++)
            {
                var status = terminalStatuses[i];
                store.Put("job", status, new AgentJob { Id = status, Status = status, FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-i) });
            }
            store.Put("job", "old-completed", new AgentJob { Id = "old-completed", Status = "completed", FinishedAt = DateTimeOffset.UtcNow.AddDays(-60) });
            Check(store.ActiveJobCount() == activeStatuses.Length && store.ActiveJobs().Select(x => x.Status).ToHashSet().SetEquals(activeStatuses),
                "queue query includes active waits and execution but excludes every terminal state");
            Check(store.JobHistory(2, 7).Select(x => x.Id).SequenceEqual(["completed", "failed"]), "history shows only the requested newest terminal receipts");
            Check(!store.JobHistory(100, 7).Any(x => x.Id == "old-completed") && store.Get<AgentJob>("job", "old-completed") is not null,
                "history age controls visibility while exact durable receipt lookup remains available");
            Check(Rejects(() => store.JobHistory(0, 1)) && Rejects(() => store.JobHistory(1, 0)), "history query validates explicit bounds");
            foreach (var status in activeStatuses) { var job = store.Get<AgentJob>("job", status)!; job.Status = "completed"; store.Put("job", status, job); }
            Check(store.ActiveJobCount() == 0, "accepted work leaves the active queue immediately on terminal transition");

            var events = new EventHub();
            await using var runtime = new ModelRuntime(store, configuration.Get, events);
            await using var toolDispatcher = new Cerebral.Tools.ToolDispatcher(configuration.Get);
            using var scheduler = new JobScheduler(store, configuration, new NoExecutor(), runtime, events, NullLogger<JobScheduler>.Instance);
            var commands = new CommandDispatcher(store, configuration, runtime, scheduler, toolDispatcher, events);
            var disabledExpiry = configuration.Get();
            disabledExpiry.WorkflowIdleExpiryDays = 0;
            var disabledResult = await commands.ExecuteAsync(new CommandRequest("settings.save", CerebralJson.Element(disabledExpiry)));
            Check(disabledResult.Success && store.ReadWorkflowIdleExpiryDays() == 0,
                "the Configuration command can disable automatic expiry");
            var olderSettings = JsonNode.Parse(CerebralJson.Element(configuration.Get()).GetRawText())!.AsObject();
            olderSettings.Remove("workflowIdleExpiryDays");
            var olderResult = await commands.ExecuteAsync(new CommandRequest("settings.save", CerebralJson.Element(olderSettings)));
            Check(olderResult.Success && store.ReadWorkflowIdleExpiryDays() == 0,
                "older settings clients preserve an explicitly disabled expiry");
            var directDefaultEdit = configuration.Get(); directDefaultEdit.DefaultModelId = "must-use-default-switch";
            var directDefaultResult = await commands.ExecuteAsync(new CommandRequest("settings.save", CerebralJson.Element(directDefaultEdit)));
            Check(!directDefaultResult.Success && string.IsNullOrEmpty(configuration.Get().DefaultModelId),
                "generic settings save cannot bypass the transactional default-model switch");
            settings = configuration.Get(); settings.QueueCapacity = 1; configuration.Save(settings);
            var fixtureModelPath = Path.Combine(root, "fixture.gguf");
            await File.WriteAllBytesAsync(fixtureModelPath, "GGUF"u8.ToArray());
            store.Put("model", "fixture", new ModelRecord
            {
                Id = "fixture", Name = "Configuration fixture model", Path = fixtureModelPath,
                DeclaredType = "model", MetadataSource = "fixture"
            });
            store.Put("agent", "helper", new AgentProfile { Id = "helper", Name = "Helper", ModelId = "fixture", Role = "helper" });
            var request = new SubmitJobRequest { RequestId = "new-work", ProfileId = "helper", Prompt = "A bounded assignment" };
            var accepted = scheduler.Submit(request);
            Check(scheduler.Queue().Count == 1 && scheduler.Queue()[0].Id == accepted.Id,
                "terminal history consumes no active queue quota");
            Check(Rejects(() => scheduler.Submit(new() { ProfileId = "helper", Prompt = "Second assignment" })),
                "active capacity rejects excess work");
            scheduler.Cancel(accepted.Id);
            Check(scheduler.Queue().Count == 0 && scheduler.Submit(request).Status == "cancelled",
                "terminal job stays outside queue and exact request retry returns its receipt without duplicate execution");
            Check(Rejects(() => scheduler.Submit(new() { RequestId = "new-work", ProfileId = "helper", Prompt = "Different work" })),
                "durable idempotency still rejects a request identifier reused for different work");
            Check(scheduler.History().Count == 2, "scheduler applies saved history display quota");

            var balancedRequest = new SubmitJobRequest { RequestId = "balanced-work", ProfileId = "helper", Prompt = "Use a longer response.", ResponseSharePercent = 40 };
            var balanced = scheduler.Submit(balancedRequest);
            var balancedSession = store.Get<TaskSession>("session", balanced.SessionId)!;
            Check(balanced.ResponseSharePercent == 40 && balanced.Profile.ResponseTokens == 1638 && balancedSession.ResponseSharePercent == 40 &&
                store.Get<AgentProfile>("agent", "helper")!.ResponseTokens == 1024,
                "an accepted response balance is persisted with the conversation and job snapshot without changing the agent profile");
            var changedBalance = await commands.ExecuteAsync(new("sessions.response-balance", CerebralJson.Element(new { id = balanced.SessionId, responseSharePercent = 20 })));
            Check(changedBalance.Success && store.Get<TaskSession>("session", balanced.SessionId)!.ResponseSharePercent == 20 &&
                store.Get<AgentJob>("job", balanced.Id)!.Profile.ResponseTokens == 1638,
                "changing the conversation balance applies to later work without mutating an accepted assignment");
            Check(scheduler.Submit(balancedRequest).Id == balanced.Id &&
                Rejects(() => scheduler.Submit(new() { RequestId = "balanced-work", ProfileId = "helper", Prompt = "Use a longer response.", ResponseSharePercent = 30 })),
                "request idempotency accepts the original balance and rejects the same identifier with a different balance");
            scheduler.Cancel(balanced.Id);
            var inherited = scheduler.Submit(new() { RequestId = "balanced-follow-up", SessionId = balanced.SessionId, ProfileId = "helper", Prompt = "Continue with the saved balance." });
            Check(inherited.ResponseSharePercent == 20 && inherited.Profile.ResponseTokens == 819,
                "a follow-up inherits the saved conversation balance and resolves it against the selected profile context");
            scheduler.Cancel(inherited.Id);
            var resetBalance = await commands.ExecuteAsync(new("sessions.response-balance", CerebralJson.Element(new { id = balanced.SessionId, responseSharePercent = (int?)null })));
            Check(resetBalance.Success && store.Get<TaskSession>("session", balanced.SessionId)!.ResponseSharePercent is null,
                "the conversation can return to its agent-profile token balance");
            Check(Rejects(() => scheduler.Submit(new() { RequestId = "invalid-balance", ProfileId = "helper", Prompt = "Invalid", ResponseSharePercent = 51 })),
                "response balance percentages outside the service contract are rejected before admission");

            var workflowRequest = new SubmitJobRequest
            {
                RequestId = "plugin-workflow", ProfileId = "helper",
                Prompt = "Private plugin workflow context.", VisiblePrompt = "Plan my book.", WorkflowSource = "pegasus.books"
            };
            var workflow = scheduler.Submit(workflowRequest);
            var workflowSession = store.Get<TaskSession>("session", workflow.SessionId)!;
            Check(workflow.Prompt == "Private plugin workflow context." && workflow.VisiblePrompt == "Plan my book." &&
                  workflow.WorkflowSource == "pegasus.books" && workflowSession.Messages.Count == 1 &&
                  workflowSession.Messages[0].Content == "Plan my book." && workflowSession.Messages[0].Origin == "user",
                "trusted plugin workflow context stays on the backend assignment while only the exact user request enters conversation history");
            Check(scheduler.Submit(workflowRequest).Id == workflow.Id &&
                  Rejects(() => scheduler.Submit(new() { RequestId = workflow.Id, ProfileId = "helper", Prompt = workflow.Prompt,
                      VisiblePrompt = "Different visible request.", WorkflowSource = "pegasus.books" })) &&
                  Rejects(() => scheduler.Submit(new() { ProfileId = "helper", Prompt = "Private context without provenance", VisiblePrompt = "Visible" })),
                "private workflow admission is idempotent and requires exact visible text plus plugin provenance");
            scheduler.Cancel(workflow.Id);

            var session = new TaskSession { Id = "recency" }; store.Put("session", session.Id, session);
            var initialTime = store.Get<TaskSession>("session", session.Id)!.UpdatedAt;
            await Task.Delay(2); store.Append(session.Id, "user", "A new turn");
            Check(initialTime is not null && store.Get<TaskSession>("session", session.Id)!.UpdatedAt > initialTime,
                "persisted conversation mutations update observable sidebar recency");
            var oldSession = CerebralJson.Read<TaskSession>(CerebralJson.Element(new { id = "old" }));
            Check(oldSession.UpdatedAt is null, "legacy conversations have unknown recency rather than a fabricated current date");
            Console.WriteLine($"Core configuration: {checks} checks passed (real SQLite and temporary folders; scheduler admission only, no model inference).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Configuration tests never execute a model.");
    }
}
