using System.Reflection;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class ProjectKnowledgeTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-project-knowledge-" + Guid.NewGuid().ToString("N")));
        var assertions = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); assertions++; Console.WriteLine("PASS: " + name); }
        bool Rejects(Action action) { try { action(); return false; } catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException) { return true; } }
        try
        {
            var a = Directory.CreateDirectory(Path.Combine(root, "A")).FullName;
            var b = Directory.CreateDirectory(Path.Combine(root, "B")).FullName;
            var store = new StateStore(Path.Combine(root, "state")); var events = new EventHub(); var config = new Configuration(store);
            var host = config.Get(); host.McpServers = [new() { Id = "cortex", Executable = Environment.ProcessPath!, Enabled = true }];
            config.Save(host);
            store.Put("navigation", "sidebar", new NavigationState { Nodes = [
                new() { Id = "project-a", Kind = "project", Title = "A", Path = a },
                new() { Id = "project-b", Kind = "project", Title = "B", Path = b }] });
            var projects = new ProjectKnowledge(store, config, events);
            var defaults = projects.Get("project-a");
            Check(defaults.Revision == 1 && defaults.AutomaticRetrieval && defaults.RequireKnowledgeApproval &&
                  defaults.CortexServerId == "cortex" && defaults.ReadNamespaces.SequenceEqual(["project.pegasus-cerebral", "knowledge.shared"]) && defaults.WriteNamespace is null,
                "new projects automatically inherit the reviewed Cortex read connection while writes stay disabled");
            defaults.ReadNamespaces = ["project.pegasus-cerebral"]; defaults.WriteNamespace = "project.pegasus-cerebral";
            var settings = projects.Save(defaults);
            Check(settings.Revision == 2 && projects.List().Count == 1, "project knowledge settings persist with one revisioned record");
            var staleSettings = CerebralJson.Read<ProjectKnowledgeSettings>(CerebralJson.Element(settings)); staleSettings.Revision = 1;
            Check(Rejects(() => projects.Save(staleSettings)) && projects.Get("project-a").Revision == 2, "stale project settings cannot overwrite the saved configuration");
            Check(Rejects(() => projects.Save(new() { ProjectId = "project-a", Revision = 2, CortexServerId = "cortex", ReadNamespaces = ["private.other"] })),
                "project reads cannot exceed the host Cortex namespace allowlist");
            Check(Rejects(() => projects.Save(new() { ProjectId = "project-a", Revision = 2, CortexServerId = "cortex", ReadNamespaces = ["knowledge.shared"], WriteNamespace = "knowledge.shared" })),
                "project writes cannot use a namespace granted only for reading");
            var fixtureModelPath = Path.Combine(root, "fixture.gguf");
            await File.WriteAllBytesAsync(fixtureModelPath, "GGUF"u8.ToArray());
            store.Put("model", "fixture", new ModelRecord
            {
                Id = "fixture", Name = "Project knowledge fixture model", Path = fixtureModelPath,
                DeclaredType = "model", MetadataSource = "fixture"
            });
            var profile = new AgentProfile { Id = "lead", Name = "Fixture lead", Role = "lead", ModelId = "fixture", AllowedRoots = [root],
                AllowedTools = ["mcp.cortex.cortex_search", "mcp.cortex.cortex_read", "mcp.cortex.cortex_knowledge"],
                Capabilities = ["mcp.cortex", "cortex.write"], MaxSteps = 20 };
            store.Put("agent", profile.Id, profile);
            var helper = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile)); helper.Id = "helper"; helper.Role = "helper"; store.Put("agent", helper.Id, helper);
            var fake = new KnowledgeTools(); await using var runtime = new ModelRuntime(store, config.Get, events);
            using var scheduler = new JobScheduler(store, config, new NoExecution(), runtime, events, NullLogger<JobScheduler>.Instance);
            var lead = scheduler.Submit(new() { RequestId = "lead-project", ProfileId = "lead", ProjectId = "project-a", Prompt = "Build the selected application." });
            var session = store.Get<TaskSession>("session", lead.SessionId)!;
            TaskSession AppendUser(string content)
            {
                store.Append(session.Id, "user", content);
                return store.Get<TaskSession>("session", session.Id)!;
            }
            Check(session.ProjectId == "project-a" && store.Get<NavigationState>("navigation", "sidebar")!.Nodes.Single(x => x.TargetId == session.Id).ParentId == "project-a",
                "submitting a selected project persists its binding and sidebar grouping");
            var workspace = new WorkspaceManagement(store, events, scheduler);
            var removing = workspace.GetNavigation(); removing.Nodes.RemoveAll(x => x.Id == "project-a");
            var boundRemovalDenied = false;
            try { workspace.SaveNavigation(removing); }
            catch (InvalidOperationException ex) { boundRemovalDenied = ex.Message.Contains("bound conversations", StringComparison.Ordinal); }
            Check(boundRemovalDenied && workspace.GetNavigation().Nodes.Any(x => x.Id == "project-a"),
                "sidebar changes cannot remove a project that still owns saved conversations");
            var context = projects.ContextFor(session, profile, lead.Id);
            Check(context.WorkingDirectory == a && context.Cortex!.ReadNamespaces.SequenceEqual(["project.pegasus-cerebral"]) && !context.Cortex.KnowledgeWriteApproved,
                "project tool context uses the selected path and narrowed knowledge scope without implied approval");
            settings.RequireKnowledgeApproval = false; settings = projects.Save(settings);
            var moduleProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
            moduleProfile.PluginSettings["fixture.module"] = CerebralJson.Element(new { enabled = true });
            Check(projects.ContextFor(session, moduleProfile, lead.Id).Cortex is { RequireKnowledgeApproval: false, KnowledgeWriteApproved: false },
                "module metadata leaves Cortex task and knowledge policy under the selected project's configuration");
            settings.RequireKnowledgeApproval = true; settings = projects.Save(settings);
            var remoteGrant = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile)); remoteGrant.Capabilities.Add("filesystem.read");
            projects.RestrictUnmappedRemoteGrant(session, remoteGrant);
            Check(!remoteGrant.Capabilities.Contains("mcp.cortex") && remoteGrant.Capabilities.Contains("filesystem.read"),
                "an unmapped remote assignment loses the selected Cortex provider while keeping native worker grants");
            await using var directTools = new ToolDispatcher(config.Get);
            var commands = new CommandDispatcher(store, config, runtime, scheduler, directTools, events);
            var wrongOwner = await commands.ExecuteAsync(new("tools.call", CerebralJson.Element(new { profileId = "helper", sessionId = session.Id, name = "mcp.cortex.cortex_search", arguments = new { } })));
            Check(!wrongOwner.Success, "direct tool calls cannot borrow another profile's project conversation");
            var attemptedWrite = await commands.ExecuteAsync(new("tools.call", CerebralJson.Element(new { profileId = "lead", sessionId = session.Id, name = "mcp.cortex.cortex_knowledge", arguments = new { action = "create" }, knowledgeWriteApproved = true })));
            Check(!attemptedWrite.Success && fake.Calls.Count == 0, "direct tool callers cannot manufacture project knowledge approval through arguments");
            var narrow = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile)); narrow.AllowedRoots = [b];
            Check(projects.ContextFor(session, narrow, lead.Id).WorkingDirectory is null,
                "an ungranted project path never silently falls back to another project directory");
            var child = scheduler.Submit(new() { ProfileId = "helper", ParentJobId = lead.Id, Prompt = "Read the project." });
            Check(store.Get<TaskSession>("session", child.SessionId)!.ProjectId == "project-a" && !store.Get<NavigationState>("navigation", "sidebar")!.Nodes.Any(x => x.TargetId == child.SessionId),
                "local helpers inherit the project without creating ordinary conversation entries");
            Check(Rejects(() => projects.BindSession(session.Id, "project-b")), "active conversation work prevents project rebinding");
            lead.Status = "completed"; store.Put("job", lead.Id, lead);
            Check(Rejects(() => projects.BindSession(session.Id, "project-b")), "an active descendant helper also prevents project rebinding");
            scheduler.Cancel(child.Id);
            session.RemoteSessionIds["old"] = "old-remote"; store.Put("session", session.Id, session);
            session = projects.BindSession(session.Id, "project-b");
            Check(session.ProjectId == "project-b" && session.RemoteSessionIds.Count == 0 && store.Get<NavigationState>("navigation", "sidebar")!.Nodes.Single(x => x.TargetId == session.Id).ParentId == "project-b",
                "idle explicit project changes clear old remote associations and update grouping together");
            Check(Rejects(() => scheduler.Submit(new() { ProfileId = "lead", SessionId = session.Id, ProjectId = "project-a", Prompt = "resume" })),
                "resume cannot silently change an existing conversation's project");
            var continued = scheduler.Submit(new() { ProfileId = "lead", SessionId = session.Id, Prompt = "continue" });
            Check(store.Get<TaskSession>("session", continued.SessionId)!.ProjectId == "project-b", "omitting project on resume preserves the saved binding");
            scheduler.Cancel(continued.Id);
            Check(Rejects(() => scheduler.Submit(new() { ProfileId = "lead", ProjectId = "project-a", DelegatedProfile = profile, Prompt = "remote" })),
                "a delegated request cannot silently interpret a coordinator project ID as a remote mapping");
            session = projects.BindSession(session.Id, "project-a");
            var retrievalJob = scheduler.Submit(new() { ProfileId = "lead", SessionId = session.Id, Prompt = "Find prior UI guidance." });
            session = store.Get<TaskSession>("session", session.Id)!;
            var retrieval = new CortexContext(store, config, fake, events);
            var descriptions = await fake.ListAsync(profile, default);
            var first = await retrieval.EnsureAsync(retrievalJob, session, profile, descriptions, default);
            Check(first!.Status == "loaded" && first.References.Count == 3 && fake.Calls.Count == 4,
                "automatic retrieval performs one bounded search followed by at most three exact typed reads");
            Check(fake.Calls[0].Arguments.GetProperty("text").GetString() == "Find prior UI guidance." &&
                fake.Calls[0].Arguments.GetProperty("namespaces").EnumerateArray().Single().GetString() == "project.pegasus-cerebral" &&
                fake.Calls.Skip(1).All(x => x.Arguments.GetProperty("recordType").GetString() == "knowledge" && x.Arguments.TryGetProperty("recordId", out _) && !x.Arguments.TryGetProperty("maxUtf8Bytes", out _)),
                "retrieval uses the latest actual user input, selected namespaces, and the installed exact-read coordinate format");
            Check(first.Context.Contains("EXACT_BODY_ONLY", StringComparison.Ordinal) && !first.Context.Contains("PREVIEW_ONLY", StringComparison.Ordinal) &&
                Encoding.UTF8.GetByteCount(first.Context) <= CortexContext.MaxContextBytes && first.Context.Contains("passive reference", StringComparison.Ordinal),
                "working knowledge uses bounded exact bodies with source coordinates and passive-content labeling");
            var newerBody = CerebralJson.Element(new { structuredContent = new { recordType = "knowledge", recordId = "guide", revision = 3,
                record = new { @namespace = "project.pegasus-cerebral", recordId = "guide", revision = 3, state = "active", content = "EXACT NEWER BODY" } } }).GetRawText();
            Check(CortexContext.ReadReference(newerBody, new("project.pegasus-cerebral", "guide", "2")).Reference.Revision == "3",
                "a body newer than its search preview retains the actual complete read revision");
            foreach (var field in new[] { "truncated", "partial", "excerpt", "metadataOnly", "bodyOmitted", "complete", "fullBodyIncluded" })
            {
                var incomplete = System.Text.Json.Nodes.JsonNode.Parse(newerBody)!;
                incomplete["structuredContent"]![field] = field is not ("complete" or "fullBodyIncluded");
                Check(RejectsRead(incomplete.ToJsonString()), "automatic knowledge admission rejects explicit " + field + " metadata");
            }
            var missingRevision = System.Text.Json.Nodes.JsonNode.Parse(newerBody)!;
            missingRevision["structuredContent"]!.AsObject().Remove("revision");
            Check(RejectsRead(missingRevision.ToJsonString()), "automatic knowledge admission requires a positive matching read revision");
            bool RejectsRead(string body)
            {
                try { CortexContext.ReadReference(body, new("project.pegasus-cerebral", "guide")); return false; }
                catch (InvalidDataException) { return true; }
            }
            Check(!store.Get<TaskSession>("session", session.Id)!.Messages.Any(x => x.Content.Contains("EXACT_BODY_ONLY", StringComparison.Ordinal)),
                "session receipts retain source IDs and status without duplicating every retrieved body");
            await retrieval.EnsureAsync(retrievalJob, store.Get<TaskSession>("session", session.Id)!, profile, descriptions, default);
            Check(fake.Calls.Count == 4, "unchanged user input and project settings reuse the current bounded job reference set");
            session = AppendUser("Now focus on the build warning.");
            var second = await retrieval.EnsureAsync(retrievalJob, session, profile, descriptions, default);
            Check(second!.UserSequence == session.Messages[^1].Sequence && fake.Calls.Count == 8 && store.List<CortexRetrievalStatus>("cortex-context").Count == 1,
                "steering replaces the current reference set instead of creating a checkpoint archive");
            var longIds = Enumerable.Range(1, 3).Select(i => "source-" + i + "-" +
                string.Concat(Enumerable.Repeat("\u03bb\U0001F600", 120))).ToArray();
            fake.Records = longIds.Select((id, i) => (Id: id, Body: new string((char)('a' + i), 2290) + "_FULL_TAIL_" + i))
                .ToDictionary(x => x.Id, x => x.Body, StringComparer.Ordinal);
            session = AppendUser("Check knowledge with long source coordinates.");
            var longHeaders = await retrieval.EnsureAsync(retrievalJob, session, profile, descriptions, default);
            Check(longHeaders!.Status == "loaded" && longHeaders.References.Count == 3 &&
                Encoding.UTF8.GetByteCount(longHeaders.Context) <= CortexContext.MaxContextBytes &&
                longHeaders.References.All(x => x.Excerpt && x.SourceBytes == Encoding.UTF8.GetByteCount(fake.Records[x.Id]) &&
                    longHeaders.Context.Contains("recordId=" + x.Id + "; revision=2; excerpt=True\n", StringComparison.Ordinal) &&
                    !longHeaders.Context.Contains(fake.Records[x.Id], StringComparison.Ordinal)),
                "actual Unicode coordinate headers are budgeted before bodies and every shortened source is marked as an excerpt");
            fake.Records[longIds[0]] = "A complete short source with its exact tail.";
            fake.Records[longIds[1]] = new string('b', 4000) + "_SECOND_FULL_TAIL";
            fake.Records[longIds[2]] = new string('c', 4000) + "_THIRD_FULL_TAIL";
            session = AppendUser("Check complete and excerpted knowledge together.");
            var mixedBodies = await retrieval.EnsureAsync(retrievalJob, session, profile, descriptions, default);
            Check(mixedBodies!.References.Any(x => !x.Excerpt) && mixedBodies.References.Any(x => x.Excerpt) &&
                mixedBodies.References.All(x => x.SourceBytes == Encoding.UTF8.GetByteCount(fake.Records[x.Id]) &&
                    (x.Excerpt || mixedBodies.Context.Contains(fake.Records[x.Id], StringComparison.Ordinal))) &&
                new UTF8Encoding(false, true).GetByteCount(mixedBodies.Context) <= CortexContext.MaxContextBytes,
                "complete-source metadata matches the full included body while excerpt sizes preserve valid UTF-8");
            var oversizedId = "oversized-" + new string('z', CortexContext.MaxContextBytes);
            fake.Records = new(StringComparer.Ordinal) { [oversizedId] = "Should be omitted.", ["fits"] = "Surviving complete body." };
            session = AppendUser("Check an unrepresentable knowledge coordinate.");
            var omittedHeader = await retrieval.EnsureAsync(retrievalJob, session, profile, descriptions, default);
            Check(omittedHeader!.Status == "partial" && omittedHeader.References.Count == 1 &&
                omittedHeader.References[0].Id == "fits" && !omittedHeader.References[0].Excerpt &&
                omittedHeader.Context.Contains(fake.Records["fits"], StringComparison.Ordinal) &&
                !omittedHeader.Context.Contains(oversizedId, StringComparison.Ordinal) &&
                omittedHeader.Message.Contains("source omitted", StringComparison.Ordinal),
                "a coordinate that cannot fit is omitted truthfully without trimming other complete source headers");
            fake.Records.Remove("fits");
            session = AppendUser("Check when no source coordinate can fit.");
            var noneFit = await retrieval.EnsureAsync(retrievalJob, session, profile, descriptions, default);
            Check(noneFit!.Status == "unavailable" && noneFit.References.Count == 0 && noneFit.Context == "" &&
                fake.Records[oversizedId] == "Should be omitted.",
                "unrepresentable sources produce no misleading reference metadata and do not mutate full provider bodies");
            fake.Records = null;
            fake.ReadFails = true; session = AppendUser("Check recovery guidance.");
            var failed = await retrieval.EnsureAsync(retrievalJob, session, profile, descriptions, default);
            Check(failed!.Status == "unavailable" && failed.References.Count == 0 && failed.Context.Length == 0 && failed.Message.Contains("unavailable", StringComparison.Ordinal),
                "failed exact reads do not adopt search summaries or falsely report retrieved knowledge");
            fake.ReadFails = false; fake.SearchOutsideScope = true; session = AppendUser("Check another detail.");
            var denied = await retrieval.EnsureAsync(retrievalJob, session, profile, descriptions, default);
            Check(denied!.Status == "unavailable" && denied.Context == "", "out-of-scope returned search coordinates are rejected before exact retrieval");
            fake.SearchOutsideScope = false;
            var before = fake.Calls.Count; session = AppendUser("Try a missing read grant.");
            var unavailable = await retrieval.EnsureAsync(retrievalJob, session, profile, descriptions.Take(1).ToList(), default);
            Check(unavailable!.Status == "unavailable" && fake.Calls.Count == before, "an unavailable tool/grant produces a truthful receipt without invoking a substitute");
            settings.AutomaticRetrieval = false; settings = projects.Save(settings);
            var disabled = await retrieval.EnsureAsync(retrievalJob, store.Get<TaskSession>("session", session.Id)!, profile, descriptions, default);
            Check(disabled!.Status == "disabled" && fake.Calls.Count == before && disabled.Context.Length == 0, "disabling automatic retrieval removes current references without a provider call");
            Check(Encoding.UTF8.GetByteCount(CortexContext.BoundUtf8(string.Concat(Enumerable.Repeat("λ😀", 3000)), 4096)) <= 4096,
                "knowledge excerpts remain bounded in UTF-8 bytes without splitting encoded characters");
            Check(!CortexOperationPolicy.IsDurableWrite("mcp.cortex.cortex_knowledge", CerebralJson.Element(new { action = "read" })) &&
                CortexOperationPolicy.IsDurableWrite("mcp.cortex.cortex_knowledge", CerebralJson.Element(new { action = "revise" })) &&
                !CortexOperationPolicy.IsDurableWrite("cortex_finish", CerebralJson.Element(new { knowledge = Array.Empty<object>() })) &&
                CortexOperationPolicy.IsDurableWrite("cortex_finish", CerebralJson.Element(new { knowledge = new[] { new { content = "proposed" } } })),
                "the approval selector protects durable knowledge writes while allowing ordinary reads and task-only continuity");
            scheduler.Cancel(retrievalJob.Id);

            // Reproduce the answer/save race at the scheduler's state merge boundary without running a model.
            var race = scheduler.Submit(new() { ProfileId = "lead", Prompt = "approval race" });
            var stale = CerebralJson.Read<AgentJob>(CerebralJson.Element(race)); stale.Status = "waiting-human";
            var live = scheduler.Get(race.Id); live.Status = "queued"; store.Put("job", live.Id, live);
            store.Put("question", "answered", new UserQuestion { Id = "answered", JobId = race.Id, SessionId = race.SessionId, Required = true, Answer = "yes" });
            var save = typeof(JobScheduler).GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)!;
            save.Invoke(scheduler, [stale]);
            Check(scheduler.Get(race.Id).Status == "queued", "a fast answer cannot be overwritten by a stale waiting-human execution snapshot");
            live = scheduler.Get(race.Id); live.CancelRequested = true; store.Put("job", live.Id, live); stale.Status = "waiting-human";
            save.Invoke(scheduler, [stale]);
            Check(scheduler.Get(race.Id).CancelRequested && scheduler.Get(race.Id).Status != "queued", "cancellation takes precedence over answered-question requeue");
            Console.WriteLine($"Project knowledge: {assertions} checks passed with deterministic MCP results and real local state; no Cortex provider or model was invoked.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Refusing cleanup outside the test workspace.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private sealed class NoExecution : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Project tests do not execute models.");
    }
    private sealed class KnowledgeTools : IToolDispatcher
    {
        public List<ToolCall> Calls { get; } = [];
        public bool ReadFails { get; set; }
        public bool SearchOutsideScope { get; set; }
        public Dictionary<string, string>? Records { get; set; }
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) => Task.FromResult<IReadOnlyList<ToolDescriptor>>(
            new[] { "cortex_search", "cortex_read" }.Select(name => new ToolDescriptor("mcp.cortex." + name, "deterministic fixture", CerebralJson.Element(new { type = "object" }), "mcp.cortex")).ToArray());
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(call);
            object payload;
            if (call.Name.EndsWith("cortex_search", StringComparison.Ordinal))
                payload = new { schema = "cortex.search.compact/1", ns = new[] { "project.pegasus-cerebral" }, items = (Records?.Keys.AsEnumerable() ?? Enumerable.Range(1, 3).Select(i => "guidance-" + i)).Select(id =>
                    new { ns = SearchOutsideScope ? "private.other" : "project.pegasus-cerebral", type = "knowledge", id, rev = 2, title = "Guidance", summary = "PREVIEW_ONLY" }).ToArray() };
            else
            {
                if (ReadFails) return Task.FromResult(new ToolResult(false, "connector unavailable"));
                var id = call.Arguments.GetProperty("recordId").GetString()!;
                payload = new { recordType = "knowledge", recordId = id, revision = 2,
                    record = new { @namespace = "project.pegasus-cerebral", recordId = id, revision = 2, state = "active", content = Records is null ? "EXACT_BODY_ONLY: " + new string('x', 3000) : Records[id] } };
            }
            return Task.FromResult(new ToolResult(true, JsonSerializer.Serialize(new { structuredContent = payload })));
        }
    }
}
