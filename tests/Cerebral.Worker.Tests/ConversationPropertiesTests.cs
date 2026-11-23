using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class ConversationPropertiesTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Conversation properties: " + label);
            checks++; Console.WriteLine("PASS: " + label);
        }
        void Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or
                KeyNotFoundException or NotSupportedException or InvalidDataException)
            { Check(true, label); return; }
            throw new InvalidOperationException("Conversation properties accepted invalid input: " + label);
        }

        using var fixture = new Fixture();
        var bound = fixture.Submit("bound-first", "bound-chat", Data(new { workingDirectory = fixture.ProjectRoot }), "project");
        var originalInput = bound.Submission.Input.GetRawText();
        var originalJournalRevision = fixture.Host.Journal.GetRevision("bound-chat");
        var subscriber = fixture.Events.Subscribe();
        try
        {
            var initial = fixture.Properties.Get("bound-chat");
            Check(initial.Id == "bound-chat" && initial.Title == "Owner fixture request" && initial.ProfileId == fixture.Profile.Id &&
                initial.ProfileName == fixture.Profile.Name && initial.ProfileRole == fixture.Profile.Role && initial.ProjectId == "project" &&
                initial.RootState == "bound" && initial.Root == Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixture.ProjectRoot)),
                "a saved queued native chat exposes its original owner root before any scheduler advance");
            Check(initial.Revision == 0 && initial.UpdatedAt is null && initial.ResponseSharePercent is null &&
                initial.PropertyState == "default" && initial.SettingsSource == "profile" &&
                fixture.State.Get<ConversationProperties.Settings>(ConversationProperties.RecordKind, initial.Id) is null &&
                fixture.Host.Store.Get(bound.Id).Revision == bound.Revision && fixture.Host.Journal.GetRevision(initial.Id) == originalJournalRevision &&
                !subscriber.Reader.TryRead(out _),
                "get neither creates preferences nor mutates native workflow state or emits a change event");
            Check(initial.CurrentRoots.SequenceEqual(fixture.Profile.AllowedRoots) && initial.Capabilities.SequenceEqual(fixture.Profile.Capabilities) &&
                initial.Tools.SequenceEqual(fixture.Profile.AllowedTools) && !initial.CurrentFullAccess && fixture.Tools.ProviderCalls == 0,
                "permissions display contains current exact profile roots/capabilities/tool grants without provider discovery or execution");

            var inheritedNoOp = fixture.Properties.Save(initial.Id, 0, null);
            Check(inheritedNoOp.Revision == 0 && inheritedNoOp.PropertyState == "default" &&
                fixture.State.Get<ConversationProperties.Settings>(ConversationProperties.RecordKind, initial.Id) is null && !subscriber.Reader.TryRead(out _),
                "saving an unchanged inherited default is a no-op without a record, revision or event");
            var first = fixture.Properties.Save(initial.Id, 0, 15);
            Check(first.Revision == 1 && first.ResponseSharePercent == 15 && first.UpdatedAt is not null &&
                first.PropertyState == "saved" && first.SettingsSource == "conversation" &&
                subscriber.Reader.TryRead(out var firstEvent) && firstEvent.Type == "conversation.properties.updated" &&
                firstEvent.Data.GetProperty("conversationId").GetString() == initial.Id && firstEvent.Data.GetProperty("revision").GetInt64() == 1 &&
                !subscriber.Reader.TryRead(out _),
                "CAS saves one bounded preference and emits one exact conversation/revision notification");
            var noOp = fixture.Properties.Save(initial.Id, first.Revision, first.ResponseSharePercent);
            Check(noOp.Revision == first.Revision && noOp.UpdatedAt == first.UpdatedAt && !subscriber.Reader.TryRead(out _),
                "an unchanged saved value preserves timestamp/revision and publishes no duplicate event");
            Reject(() => fixture.Properties.Save(initial.Id, 0, 15), "a stale revision is rejected even for the same value");
            var second = fixture.Properties.Save(initial.Id, first.Revision, 30);
            Check(second.Revision == 2 && second.ResponseSharePercent == 30 && subscriber.Reader.TryRead(out _) && !subscriber.Reader.TryRead(out _),
                "an exact current revision can replace the saved preference once");

            var profileBeforeChange = Data(fixture.Profile);
            fixture.Profile.AllowedRoots = [fixture.OtherRoot]; fixture.Profile.Capabilities = ["diagnostics"];
            fixture.Profile.AllowedTools = ["workflow.catalog.*"]; fixture.Tools.FullAccessActive = true;
            fixture.State.Put("agent", fixture.Profile.Id, fixture.Profile);
            var current = fixture.Properties.Get(initial.Id);
            Check(current.Root == initial.Root && current.CurrentRoots.SequenceEqual([fixture.OtherRoot]) &&
                current.Capabilities.SequenceEqual(["diagnostics"]) && current.Tools.SequenceEqual(["workflow.catalog.*"]) && current.CurrentFullAccess,
                "current grant changes remain distinct from the original root and display exact wildcard grants/Full Access state");
            fixture.Restart();
            var restarted = fixture.Properties.Get(initial.Id);
            Check(restarted.Revision == second.Revision && restarted.ResponseSharePercent == second.ResponseSharePercent &&
                restarted.UpdatedAt == second.UpdatedAt && restarted.Root == initial.Root && restarted.CurrentFullAccess,
                "a new backend and SQLite store instance retain exact saved properties and original identity");
            var reset = fixture.Properties.Save(initial.Id, restarted.Revision, null);
            Check(reset.Revision == 3 && reset.ResponseSharePercent is null && reset.PropertyState == "saved" && reset.SettingsSource == "profile" &&
                fixture.Properties.ReadSettings(initial.Id).Revision == reset.Revision &&
                subscriber.Reader.TryRead(out _) && !subscriber.Reader.TryRead(out _),
                "resetting inherits the profile while retaining a new CAS revision rather than deleting history");
            var storedFields = Data(fixture.State.Get<ConversationProperties.Settings>(ConversationProperties.RecordKind, initial.Id))
                .EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            Check(storedFields.SetEquals(["revision", "updatedAt", "responseSharePercent"]) &&
                fixture.Host.Store.Get(bound.Id).Submission.Input.GetRawText() == originalInput &&
                fixture.Host.Store.Get(bound.Id).Revision == bound.Revision &&
                !JsonElement.DeepEquals(profileBeforeChange, Data(fixture.Profile)) &&
                JsonElement.DeepEquals(Data(fixture.Profile), Data(fixture.State.Get<AgentProfile>("agent", fixture.Profile.Id))),
                "the property record contains only preference metadata and never rewrites original input, workflow state or current profile grants");
            var lower = fixture.Properties.Save(initial.Id, reset.Revision, 5);
            var upper = fixture.Properties.Save(initial.Id, lower.Revision, 50);
            Check(lower.ResponseSharePercent == 5 && upper.ResponseSharePercent == 50 && upper.Revision == 5,
                "both supported response-share boundaries persist with exact revision increments");
            while (subscriber.Reader.TryRead(out _)) { }
            foreach (var invalid in new[] { -1, 0, 4, 51, 100 })
                Reject(() => fixture.Properties.Save(initial.Id, upper.Revision, invalid), "unsupported response share is rejected: " + invalid);
            Reject(() => fixture.Properties.Save(initial.Id, -1, 20), "negative expected revision is rejected");
            Check(fixture.Properties.Get(initial.Id).Revision == upper.Revision && !subscriber.Reader.TryRead(out _),
                "invalid writes leave the saved revision and event stream unchanged");

            var unbound = fixture.Submit("unbound-first", "unbound-chat", WorkflowContract.EmptyObject, "project");
            Check(fixture.Properties.Get(unbound.Submission.ConversationId) is { RootState: "unbound", Root: null, ProjectId: "project" },
                "a project-owned chat with no original directory remains explicitly unbound without a navigation path fallback");
            fixture.Finish(unbound);
            fixture.Submit("unbound-later", unbound.Submission.ConversationId, Data(new { workingDirectory = fixture.OtherRoot }), "project");
            Check(fixture.Properties.Get(unbound.Submission.ConversationId) is { RootState: "unbound", Root: null } &&
                fixture.Properties.Save(unbound.Submission.ConversationId, 0, 20).RootState == "unbound",
                "a later directory and a preference save cannot bind an originally unbound chat");

            var service = fixture.Submit("service-first", "service-before-owner", Data(new { workingDirectory = fixture.OtherRoot }), "project",
                new(EventActorKind.Service, "fixture.service"));
            fixture.Host.Steer(new("service-owner-steering", service.Id, "Owner steering of service work"), null);
            fixture.Finish(fixture.Host.Store.Get(service.Id));
            var owner = fixture.Submit("genuine-owner", service.Submission.ConversationId, Data(new { workingDirectory = fixture.ProjectRoot }), "project");
            Check(fixture.Properties.Get(owner.Submission.ConversationId).Root == initial.Root &&
                fixture.Host.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, owner.Submission.ConversationId)?.RequestId == owner.Id,
                "an earlier service run and its owner steering cannot supply the original owner root");

            fixture.State.Put("session", "legacy-chat", new TaskSession { Id = "legacy-chat", ProfileId = fixture.Profile.Id, ResponseSharePercent = 25 });
            Reject(() => fixture.Properties.Get("legacy-chat"), "legacy route properties are rejected instead of presenting a fabricated native root");
            Reject(() => fixture.Properties.Save("legacy-chat", 0, 10), "legacy route preferences cannot be mutated by the native properties API");
            Check(fixture.State.GetSessionMetadata("legacy-chat")?.ResponseSharePercent == 25 &&
                fixture.State.Get<ConversationProperties.Settings>(ConversationProperties.RecordKind, "legacy-chat") is null,
                "legacy rejection preserves the original setting and creates no native preference record");
            foreach (var id in new[] { "", "bad\nchat", "../chat", new string('x', 129) })
            {
                Reject(() => fixture.Properties.Get(id), "malformed native conversation ID cannot be read");
                Reject(() => fixture.Properties.Save(id, 0, 20), "malformed native conversation ID cannot be saved");
            }
            Reject(() => fixture.Properties.Get("missing-chat"), "an unknown conversation cannot be invented by get");
            Reject(() => fixture.Properties.Save("missing-chat", 0, 20), "an unknown conversation cannot be created by save");
            Check(fixture.State.Get<ConversationProperties.Settings>(ConversationProperties.RecordKind, "missing-chat") is null && fixture.Tools.ProviderCalls == 0,
                "rejected operations perform no provider calls or phantom preference writes");
            fixture.State.Delete("agent", fixture.Profile.Id);
            Reject(() => fixture.Properties.Save(initial.Id, upper.Revision, 35), "an unavailable current profile prevents a misleading permission projection before persistence");
            Check(fixture.State.Get<ConversationProperties.Settings>(ConversationProperties.RecordKind, initial.Id)?.Revision == upper.Revision,
                "a failed projection does not commit a preference or consume its CAS revision");
            fixture.State.Put("agent", fixture.Profile.Id, fixture.Profile);

            var oversized = CerebralJson.Read<AgentProfile>(Data(fixture.Profile));
            oversized.AllowedTools = Enumerable.Repeat("file.read", 1025).ToList();
            fixture.State.Put("agent", fixture.Profile.Id, oversized);
            Reject(() => fixture.Properties.Get(initial.Id), "an oversized grant array is rejected instead of silently truncating current permissions");
            Reject(() => fixture.Properties.Save(initial.Id, upper.Revision, 35), "oversized permissions cannot consume a preference revision");
            oversized.AllowedTools = [new string('x', 512 * 1024)];
            fixture.State.Put("agent", fixture.Profile.Id, oversized);
            Reject(() => fixture.Properties.Get(initial.Id), "oversized serialized Properties are rejected with a bounded diagnostic");
            Check(fixture.State.Get<ConversationProperties.Settings>(ConversationProperties.RecordKind, initial.Id)?.Revision == upper.Revision,
                "presentation bounds preserve exact stored preferences without truncating any grant");
            fixture.State.Put("agent", fixture.Profile.Id, fixture.Profile);

            var admissionDefault = ConversationProperties.ReadAdmissionSettings(fixture.State, "new-unsaved-chat");
            Check(admissionDefault is { Revision: 0, UpdatedAt: null, ResponseSharePercent: null } &&
                fixture.Host.Store.GetConversation(ConversationWorkflowModule.DefinitionId, "new-unsaved-chat") is null &&
                fixture.State.Get<ConversationProperties.Settings>(ConversationProperties.RecordKind, "new-unsaved-chat") is null,
                "admission can read an absent default without inventing a saved conversation or preference");
            foreach (var invalid in new[]
            {
                new ConversationProperties.Settings(0, DateTimeOffset.UtcNow, 20),
                new ConversationProperties.Settings(1, null, 20),
                new ConversationProperties.Settings(1, DateTimeOffset.UtcNow, 51)
            })
            {
                fixture.State.Put(ConversationProperties.RecordKind, "corrupt-admission", invalid);
                Reject(() => ConversationProperties.ReadAdmissionSettings(fixture.State, "corrupt-admission"),
                    "admission rejects an invalid stored preference revision, timestamp or share");
            }
            fixture.State.Delete(ConversationProperties.RecordKind, "corrupt-admission");
            fixture.Finish(bound);
            var saved25 = fixture.Properties.Save(initial.Id, upper.Revision, 25);
            var missingInline = fixture.Request("preferences-25", initial.Id, Data(new { workingDirectory = fixture.ProjectRoot }));
            var accepted25 = fixture.Host.Submit(missingInline, null);
            Check(accepted25.Submission.Input.GetProperty("responseSharePercent").GetInt32() == 25 &&
                !missingInline.Input.TryGetProperty("responseSharePercent", out _),
                "tools-null Worker owner admission freezes the saved response preference without mutating caller input");
            var saved35 = fixture.Properties.Save(initial.Id, saved25.Revision, 35);
            var retried25 = fixture.Host.Submit(missingInline, null);
            Check(retried25.Revision == accepted25.Revision && retried25.Submission.Input.GetProperty("responseSharePercent").GetInt32() == 25,
                "the same omitted-share request retries its original frozen default after preferences change");
            Reject(() => fixture.Host.Submit(missingInline with { Input = Data(new { workingDirectory = fixture.ProjectRoot, responseSharePercent = 35 }) }, null),
                "an explicit changed response share cannot reuse an accepted request identity");
            fixture.Finish(accepted25);
            var explicit20 = fixture.Host.Submit(fixture.Request("preferences-explicit", initial.Id,
                Data(new { workingDirectory = fixture.ProjectRoot, responseSharePercent = 20 })), null);
            Check(explicit20.Submission.Input.GetProperty("responseSharePercent").GetInt32() == 20 &&
                fixture.Properties.Get(initial.Id).ResponseSharePercent == 35,
                "an explicit owner-turn share overrides the saved default without replacing it");
            fixture.Finish(explicit20);
            fixture.Properties.Save(initial.Id, saved35.Revision, null);
            var inherited = fixture.Host.Submit(fixture.Request("preferences-inherited", initial.Id,
                Data(new { workingDirectory = fixture.ProjectRoot })), null);
            Check(!inherited.Submission.Input.TryGetProperty("responseSharePercent", out _),
                "a reset preference preserves the profile default without inserting an extra input field");

            var actorSeed = fixture.Submit("actor-seed", "actor-preferences-chat", WorkflowContract.EmptyObject, "project");
            fixture.Properties.Save(actorSeed.Submission.ConversationId, 0, 30); fixture.Finish(actorSeed);
            var agentRun = fixture.Host.Submit(fixture.Request("agent-no-inherit", actorSeed.Submission.ConversationId, WorkflowContract.EmptyObject), fixture.Profile);
            Check(agentRun.Submission.Caller?.Kind == EventActorKind.Agent && !agentRun.Submission.Input.TryGetProperty("responseSharePercent", out _),
                "agent-originated root work does not inherit owner chat preferences");
            fixture.Finish(agentRun);
            var serviceRun = await fixture.Host.SubmitServiceAsync(fixture.Request("service-no-inherit", actorSeed.Submission.ConversationId,
                WorkflowContract.EmptyObject), "fixture.service", null, default);
            Check(serviceRun.Submission.Caller?.Kind == EventActorKind.Service && !serviceRun.Submission.Input.TryGetProperty("responseSharePercent", out _),
                "service-originated root work does not inherit owner chat preferences");

            var childSeed = fixture.Submit("child-seed", "child-preferences-chat", WorkflowContract.EmptyObject, "project");
            fixture.Properties.Save(childSeed.Submission.ConversationId, 0, 30); fixture.Finish(childSeed);
            var parent = fixture.Parent();
            var child = await fixture.Host.SubmitChildAsync(fixture.Request("child-no-inherit", childSeed.Submission.ConversationId,
                WorkflowContract.EmptyObject), parent.Id, fixture.Profile.Revision, default);
            Check(child.Submission.ParentRunId == parent.Id && child.Submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.coordination" } &&
                !child.Submission.Input.TryGetProperty("responseSharePercent", out _),
                "a real child admission retains host lineage without inheriting owner presentation preferences");

            var configuration = new Configuration(fixture.State);
            await using var models = new ModelRuntime(fixture.State, configuration.Get, fixture.Events);
            await using var actualTools = new ToolDispatcher(configuration.Get);
            var executor = new NoModelExecutor();
            using var scheduler = new JobScheduler(fixture.State, configuration, executor, models, fixture.Events,
                NullLogger<JobScheduler>.Instance, fixture.Host);
            var dispatcher = new CommandDispatcher(fixture.State, configuration, models, scheduler, actualTools, fixture.Events,
                recordedTools: fixture.Tools, workflows: fixture.Host);
            async Task<CommandResult> Command(string operation, JsonElement arguments, string? actorId = null) =>
                await dispatcher.ExecuteAsync(new(operation, arguments, actorId));
            var ownerGet = await Command("conversations.properties.get", Data(new { id = initial.Id }));
            Check(ownerGet.Success && ownerGet.Data.GetProperty("id").GetString() == initial.Id &&
                ownerGet.Data.GetProperty("root").GetString() == initial.Root,
                "the actual owner command route returns the exact private Properties projection");
            var ownerRevision = ownerGet.Data.GetProperty("revision").GetInt64();
            var ownerSave = await Command("conversations.properties.save", Data(new { id = initial.Id, expectedRevision = ownerRevision, responseSharePercent = 25 }));
            Check(ownerSave.Success && ownerSave.Data.GetProperty("responseSharePercent").GetInt32() == 25 &&
                ownerSave.Data.GetProperty("revision").GetInt64() == ownerRevision + 1,
                "the actual owner save route applies one exact CAS revision");
            var admin = new AgentProfile { Id = "properties-admin", Capabilities = ["administration"] }; fixture.State.Put("agent", admin.Id, admin);
            foreach (var actorId in new[] { fixture.Profile.Id, admin.Id })
            {
                Check(!(await Command("conversations.properties.get", Data(new { id = initial.Id }), actorId)).Success,
                    "agent properties read is denied before generic administration rights: " + actorId);
                Check(!(await Command("conversations.properties.save", Data(new { id = initial.Id,
                    expectedRevision = ownerRevision + 1, responseSharePercent = 30 }), actorId)).Success,
                    "agent properties save is denied before generic administration rights: " + actorId);
            }
            foreach (var invalidArguments in new[]
            {
                Parse("""{"id":"bound-chat","id":"bound-chat"}"""),
                Data(new { id = initial.Id, root = fixture.OtherRoot }),
                Parse("[]")
            })
                Check(!(await Command("conversations.properties.get", invalidArguments)).Success,
                    "owner get rejects duplicate/unsupported/nonobject fields");
            foreach (var invalidArguments in new[]
            {
                Parse("""{"id":"bound-chat","expectedRevision":0,"responseSharePercent":25,"responseSharePercent":30}"""),
                Data(new { id = initial.Id, expectedRevision = ownerRevision + 1 }),
                Data(new { id = initial.Id, expectedRevision = "1", responseSharePercent = 25 }),
                Data(new { id = initial.Id, expectedRevision = 1.5, responseSharePercent = 25 }),
                Data(new { id = initial.Id, expectedRevision = ownerRevision + 1, responseSharePercent = 25.5 }),
                Data(new { id = initial.Id, expectedRevision = ownerRevision + 1, responseSharePercent = "25" }),
                Data(new { id = initial.Id, expectedRevision = ownerRevision + 1, responseSharePercent = 25, currentRoots = new[] { fixture.OtherRoot } })
            })
                Check(!(await Command("conversations.properties.save", invalidArguments)).Success,
                    "owner save rejects duplicate/missing/noninteger/permission-edit fields");
            Check(fixture.Properties.Get(initial.Id).Revision == ownerRevision + 1 && executor.Calls == 0 && fixture.Tools.ProviderCalls == 0,
                "owner/agent/schema checks never dispatch a provider, start a scheduler model or consume a rejected CAS revision");
            var ownerReset = await Command("conversations.properties.save", Data(new { id = initial.Id,
                expectedRevision = ownerRevision + 1, responseSharePercent = (int?)null }));
            Check(ownerReset.Success && ownerReset.Data.GetProperty("responseSharePercent").ValueKind == JsonValueKind.Null &&
                ownerReset.Data.GetProperty("revision").GetInt64() == ownerRevision + 2 &&
                ownerReset.Data.GetProperty("settingsSource").GetString() == "profile",
                "the owner route accepts explicit null to reset the saved default with an exact revision");
        }
        finally { fixture.Events.Unsubscribe(subscriber.Id); }

        using (var race = new Fixture())
        {
            var seed = race.Submit("race-seed", "race-chat", Data(new { workingDirectory = race.ProjectRoot }), "project");
            race.Properties.Save(seed.Submission.ConversationId, 0, 10); race.Finish(seed);
            race.Profile.AutomaticCompaction = false; race.Profile.FollowsModelBudgets = false;
            race.Profile.AllowedTools = ["file.read"];
            race.State.Put("agent", race.Profile.Id, race.Profile);
            race.EnableDiscoveryHost();
            race.Tools.AllowDiscovery = true;
            race.Tools.DiscoveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            race.Tools.DiscoveryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = race.Host.SubmitAsync(race.Request("race-new-turn", seed.Submission.ConversationId,
                Data(new { workingDirectory = race.ProjectRoot, projectId = "project" })), null, default);
            try
            {
                await race.Tools.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                race.Properties.Save(seed.Submission.ConversationId, 1, 20);
                race.Tools.DiscoveryRelease.TrySetResult();
                var rejected = false;
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected && race.Host.Store.TryGet("race-new-turn") is null && race.Properties.Get(seed.Submission.ConversationId).Revision == 2 &&
                    race.Properties.Get(seed.Submission.ConversationId).ResponseSharePercent == 20 && race.Tools.ProviderCalls == 1 &&
                    race.Host.Store.Get(seed.Id).Status == WorkflowStatus.Completed,
                    "a preference changed during bounded fake tool discovery rejects stale asynchronous admission before any new run is persisted");
            }
            finally
            {
                race.Tools.DiscoveryRelease.TrySetResult();
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
            }
        }
        using (var editable = new Fixture())
        {
            editable.Profile.AllowedTools = ["file.read"];
            editable.State.Put("agent", editable.Profile.Id, editable.Profile);
            var fresh = editable.Host.Submit(editable.Request("editable-first", "editable-chat", WorkflowContract.EmptyObject), null);
            Check(!fresh.Submission.Input.TryGetProperty("workingDirectory", out _) &&
                editable.Properties.Get("editable-chat") is { Root: null, RootState: "unbound" },
                "a new owner chat stays pathless; its identifier does not become a directory");
            editable.Finish(fresh);
            var paths = new ConversationPathGrants(editable.State, editable.Host, editable.Events);
            var oldPathRevision = paths.Get("editable-chat").Revision;
            Reject(() => editable.Properties.Save("editable-chat", 0, null, true, editable.OtherRoot),
                "a chosen path must be an existing directory within the current profile roots");
            editable.Profile.AllowedRoots = [editable.ProjectRoot, editable.OtherRoot];
            editable.State.Put("agent", editable.Profile.Id, editable.Profile);
            var chosen = editable.Properties.Save("editable-chat", 0, null, true, editable.OtherRoot);
            Check(chosen.Root == editable.OtherRoot && chosen.Revision == 1 &&
                editable.Properties.ReadSettings("editable-chat") is { WorkingDirectoryOverrideSet: true, WorkingDirectory: not null } &&
                paths.Get("editable-chat").Revision == oldPathRevision + 1,
                "owner path choice advances both Properties and the path editor's stale-save version");
            Reject(() => paths.Save("editable-chat", oldPathRevision, []),
                "an open path editor cannot save against a changed working folder");
            var preserved = editable.Properties.Save("editable-chat", chosen.Revision, 25);
            Check(preserved.Root == editable.OtherRoot && preserved.Revision == 2 &&
                paths.Get("editable-chat").Revision == oldPathRevision + 1,
                "a response-only save preserves the chosen path without advancing path rules");
            var next = editable.Host.Submit(editable.Request("editable-next", "editable-chat", WorkflowContract.EmptyObject), null);
            Check(next.Submission.Input.GetProperty("workingDirectory").GetString() == editable.OtherRoot &&
                !fresh.Submission.Input.TryGetProperty("workingDirectory", out _),
                "only a fresh owner request receives the selected path; the original remains frozen");
            editable.Finish(next);
            var cleared = editable.Properties.Save("editable-chat", preserved.Revision, 25, true, null);
            Check(cleared.Root is null && cleared.RootState == "unbound" &&
                paths.Get("editable-chat").Revision == oldPathRevision + 2,
                "explicit null clears the future path and invalidates the path editor again");
            var afterClear = editable.Host.Submit(editable.Request("editable-cleared", "editable-chat", WorkflowContract.EmptyObject), null);
            Check(!afterClear.Submission.Input.TryGetProperty("workingDirectory", out _) &&
                next.Submission.Input.GetProperty("workingDirectory").GetString() == editable.OtherRoot,
                "a cleared path affects future admission without rewriting a completed request");
        }
        Console.WriteLine($"Conversation properties: {checks} isolated SQLite/backend checks passed; no provider, model, GPU or installed state.");
    }

    private static JsonElement Data(object? value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);
    private static JsonElement Parse(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cerebral-properties-" + Guid.NewGuid().ToString("N"));
        public string ProjectRoot { get; }
        public string OtherRoot { get; }
        public StateStore State { get; private set; } = null!;
        public WorkerWorkflows Host { get; private set; } = null!;
        public ConversationProperties Properties { get; private set; } = null!;
        public EventHub Events { get; } = new();
        public FakeTools Tools { get; } = new();
        public AgentProfile Profile { get; }

        public Fixture()
        {
            ProjectRoot = Path.Combine(root, "project"); OtherRoot = Path.Combine(root, "other");
            Directory.CreateDirectory(ProjectRoot); Directory.CreateDirectory(OtherRoot);
            Profile = new() { Id = "properties-agent", Name = "Properties fixture", Role = "helper", Revision = 1,
                Instructions = "Private fixture instructions must not be projected.", Capabilities = ["filesystem.read", "diagnostics"],
                AllowedTools = ["file.*"], AllowedRoots = [ProjectRoot] };
            Restart(); State.Put("agent", Profile.Id, Profile);
            State.Put("navigation", "sidebar", new NavigationState { Nodes =
                [new() { Id = "project", Kind = "project", Path = OtherRoot, Title = "Different navigation path" }] });
        }
        public void Restart()
        {
            State = new(root); var configuration = new Configuration(State);
            Host = new(State, configuration, Events, [new ConversationWorkflowModule()],
                [ConversationWorkflowModule.PackageBinding, new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])]);
            Properties = new(State, Host, Events, Tools);
        }
        public void EnableDiscoveryHost()
        {
            Host = new(State, new Configuration(State), Events, [new ConversationWorkflowModule()],
                [ConversationWorkflowModule.PackageBinding, new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])], Tools);
            Properties = new(State, Host, Events, Tools);
        }
        public SubmitWorkflowRequest Request(string id, string conversationId, JsonElement input) => new(id, conversationId,
            Profile.Id, ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
            "Owner next fixture request", null, input);
        public WorkflowRun Parent()
        {
            var profile = new AgentProfile { Id = "properties-parent", Role = "lead", Capabilities = ["agents.delegate"] };
            State.Put("agent", profile.Id, profile);
            var configuration = new Configuration(State);
            var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, profile, "", "", null, [],
                profile.Capabilities, profile.AllowedTools, profile.AllowedRoots, false,
                WorkerExecutionSnapshot.RoutingFingerprint(configuration.Get()),
                [ConversationWorkflowModule.PackageBinding, new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])], []);
            return Host.Store.Submit(new("preferences-parent", "preferences-parent-chat", profile.Id, ConversationWorkflowModule.DefinitionId,
                ConversationWorkflowModule.DefinitionVersion, "Owner fixture parent", null, WorkflowContract.EmptyObject,
                Data(snapshot), 16, new(EventActorKind.User, "owner")) { ProjectId = "project" });
        }
        public WorkflowRun Submit(string id, string conversationId, JsonElement input, string? projectId, EventActor? actor = null) =>
            Host.Store.Submit(new(id, conversationId, Profile.Id, ConversationWorkflowModule.DefinitionId,
                ConversationWorkflowModule.DefinitionVersion, "Owner fixture request", null, input, WorkflowContract.EmptyObject,
                16, actor ?? new(EventActorKind.User, "owner")) { ProjectId = projectId });
        public void Finish(WorkflowRun run)
        {
            var claimed = Host.Store.TryClaim(run.Id) ?? throw new InvalidOperationException("Fixture run cannot be claimed.");
            Host.Store.ApplyStep(claimed.Id, claimed.Revision, new(claimed.State, new CompleteWorkflow(WorkflowContract.EmptyObject)));
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private sealed class FakeTools : IToolDispatcher
    {
        public bool FullAccessActive { get; set; }
        public int ProviderCalls { get; private set; }
        public bool AllowDiscovery { get; set; }
        public TaskCompletionSource? DiscoveryEntered { get; set; }
        public TaskCompletionSource? DiscoveryRelease { get; set; }
        public async Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken cancellationToken)
        {
            ProviderCalls++;
            if (!AllowDiscovery) throw new InvalidOperationException("Properties cannot discover provider tools.");
            DiscoveryEntered?.TrySetResult();
            if (DiscoveryRelease is { } release) await release.Task.WaitAsync(cancellationToken);
            return [];
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        { ProviderCalls++; throw new InvalidOperationException("Properties cannot execute provider tools."); }
    }

    private sealed class NoModelExecutor : IAgentExecutor
    {
        public int Calls { get; private set; }
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Properties cannot execute a model job."); }
    }
}
