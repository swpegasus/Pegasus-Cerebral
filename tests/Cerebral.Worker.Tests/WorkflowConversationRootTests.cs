using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real isolated Worker admission and SQLite persistence. Tools are absent, and no
/// workflow policy, provider, model, installed service or live filesystem tool is executed.</summary>
public static class WorkflowConversationRootTests
{
    public static Task RunAsync()
    {
        var temporaryParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.GetFullPath(Path.Combine(temporaryParent, "cerebral-conversation-roots-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Conversation root fixture: " + message);
            checks++;
            Console.WriteLine("PASS: " + message);
        }

        try
        {
            var directoryA = Directory.CreateDirectory(Path.Combine(root, "SourceA")).FullName;
            var directoryB = Directory.CreateDirectory(Path.Combine(root, "SourceB")).FullName;
            var state = new StateStore(Path.Combine(root, "data"));
            var configuration = new Configuration(state);
            configuration.Get();
            var profile = new AgentProfile
            {
                Id = "root-fixture", Revision = 1, Name = "Root fixture", ModelId = "fixture-model",
                Role = "lead", WorkerId = "local", ModelWorkerId = "local", FollowsDefaultModel = false,
                FollowsModelBudgets = false, AutomaticCompaction = false,
                AllowedRoots = [root], AllowedTools = [], Capabilities = ["diagnostics"],
                ContextTokens = 4096, ResponseTokens = 512
            };
            state.Put("agent", profile.Id, profile);
            WorkerWorkflows OpenHost() => new(state, configuration, new EventHub(), [new ConversationWorkflowModule()],
                [ConversationWorkflowModule.PackageBinding,
                    new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])], tools: null);
            var host = OpenHost();
            JsonElement Input(string? directory) => directory is null
                ? WorkflowContract.EmptyObject : CerebralJson.Element(new { workingDirectory = directory });
            SubmitWorkflowRequest Request(string id, string conversation, string? directory) => new(id, conversation,
                profile.Id, ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Fixed synthetic root-binding fixture.", null, Input(directory));
            WorkflowRun Finish(WorkflowRun run)
            {
                var claimed = host.Store.TryClaim(run.Id)
                    ?? throw new InvalidOperationException("Fixture run could not be claimed.");
                return host.Store.ApplyStep(claimed.Id, claimed.Revision,
                    new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject)));
            }
            WorkflowRun Direct(string id, string conversation, string? directory, EventActor? caller = null,
                string? parent = null, string? definition = null) => host.Store.Submit(new(id, conversation,
                    profile.Id, definition ?? ConversationWorkflowModule.DefinitionId,
                    ConversationWorkflowModule.DefinitionVersion, "Fixed synthetic original-source fixture.", null,
                    Input(directory), WorkflowContract.EmptyObject, Caller: caller) { ParentRunId = parent });
            void RejectWithoutAdmission(SubmitWorkflowRequest request, string expectedDirectory)
            {
                var revision = host.Journal.GetRevision(request.ConversationId);
                var rejected = false;
                try { host.Submit(request, null); }
                catch (WorkflowConflictException error)
                {
                    rejected = error.Message.Contains(expectedDirectory, StringComparison.Ordinal) &&
                        error.Message.Contains("working directory", StringComparison.OrdinalIgnoreCase);
                }
                Check(rejected && host.Store.TryGet(request.RequestId) is null &&
                    host.Journal.GetRevision(request.ConversationId) == revision,
                    request.RequestId + ": root conflict is actionable and creates no run, effect or journal event");
            }

            Check(host.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, "bound-chat") is null,
                "a fresh chat has no original owner root");
            var originalText = directoryA + Path.DirectorySeparatorChar;
            var firstRequest = Request("root-first", "bound-chat", originalText);
            var first = host.Submit(firstRequest, null);
            Check(first.Submission.Caller is { Kind: EventActorKind.User } && first.Submission.ParentRunId is null &&
                JsonElement.DeepEquals(firstRequest.Input, first.Submission.Input) && first.Effect is null,
                "first owner admission preserves exact input without deriving directory authority");
            var savedSettings = first.Submission.SettingsSnapshot.GetRawText();
            var savedInput = first.Submission.Input.GetRawText();
            Check(host.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, "bound-chat")?.RequestId == first.Id,
                "bounded source lookup returns the first accepted root owner submission");
            Finish(first);
            RejectWithoutAdmission(Request("root-different", "bound-chat", directoryB), directoryA);
            var inherited = host.Submit(Request("root-omitted", "bound-chat", null), null);
            Check(inherited.Submission.Input.GetProperty("workingDirectory").GetString() == directoryA && inherited.Effect is null,
                "an omitted directory inherits the saved chat identity before current registry settings are consulted");
            Finish(inherited);

            var defaultRetryRequest = Request("root-default-retry", "default-retry-chat", directoryB);
            var defaultRetry = host.Submit(defaultRetryRequest, null);
            var defaultRetryFinished = Finish(defaultRetry);
            var retryRevision = host.Journal.GetRevision(defaultRetryRequest.ConversationId);
            var recovered = host.Submit(defaultRetryRequest with { Input = Input(null) }, null);
            Check(recovered.Id == defaultRetry.Id && recovered.Revision == defaultRetryFinished.Revision &&
                host.Journal.GetRevision(defaultRetryRequest.ConversationId) == retryRevision && recovered.Effect is null,
                "a retained request recovers its accepted directory without registration and creates no new run or journal event");

            var same = host.Submit(Request("root-same", "bound-chat", directoryA), null);
            Check(same.Submission.Input.GetProperty("workingDirectory").GetString() == directoryA && same.Effect is null,
                "unchanged canonical root admits a new owner turn without executing a tool");
            Finish(same);
            var withSeparator = directoryA + Path.DirectorySeparatorChar;
            var equivalent = host.Submit(Request("root-trailing", "bound-chat", withSeparator), null);
            Check(equivalent.Submission.Input.GetProperty("workingDirectory").GetString() == withSeparator,
                "trailing-separator equality admits while preserving the submitted input representation");
            Finish(equivalent);
            if (OperatingSystem.IsWindows())
            {
                var alternateCase = directoryA.ToUpperInvariant();
                var caseRun = host.Submit(Request("root-case", "bound-chat", alternateCase), null);
                Check(caseRun.Submission.Input.GetProperty("workingDirectory").GetString() == alternateCase,
                    "Windows canonical root comparison is case-insensitive without rewriting input");
                Finish(caseRun);
            }

            profile.AllowedRoots = [];
            profile.Revision++;
            state.Put("agent", profile.Id, profile);
            var beforeRetry = host.Journal.GetRevision("bound-chat");
            var retry = host.Submit(firstRequest, null);
            Check(retry.Id == first.Id && retry.Submission.SettingsSnapshot.GetRawText() == savedSettings &&
                retry.Submission.Input.GetRawText() == savedInput && host.Journal.GetRevision("bound-chat") == beforeRetry,
                "exact request retry retains its prior receipt and frozen settings after profile changes");
            var changedRetryRejected = false;
            try { host.Submit(firstRequest with { Input = Input(directoryB) }, null); }
            catch (WorkflowConflictException) { changedRetryRejected = true; }
            Check(changedRetryRejected && host.Journal.GetRevision("bound-chat") == beforeRetry &&
                host.Store.Get(first.Id).Submission.Input.GetRawText() == savedInput,
                "duplicate request identity still rejects changed input rather than replacing it with the old payload");
            var revokedTurn = host.Submit(Request("root-revoked-profile", "bound-chat", directoryA), null);
            Check(revokedTurn.Submission.SettingsSnapshot.GetProperty("allowedRoots").GetArrayLength() == 0 &&
                state.Get<AgentProfile>("agent", profile.Id)!.AllowedRoots.Count == 0,
                "a stable working directory does not restore revoked profile filesystem roots");
            Finish(revokedTurn);
            profile.AllowedRoots = [root]; profile.Revision++; state.Put("agent", profile.Id, profile);

            var legacy = host.Submit(Request("unbound-original", "unbound-chat", null), null);
            Finish(legacy);
            RejectWithoutAdmission(Request("unbound-later-a", "unbound-chat", directoryA), "unbound");
            RejectWithoutAdmission(Request("unbound-later-b", "unbound-chat", directoryB), "unbound");
            Check(host.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, "unbound-chat") is { } unbound &&
                unbound.RequestId == legacy.Id && !unbound.Input.TryGetProperty("workingDirectory", out _) &&
                host.Store.TryGet("unbound-later-a") is null && host.Store.TryGet("unbound-later-b") is null,
                "an originally unbound chat requires an owner Properties choice before a later explicit directory");

            var foreign = Direct("other-definition", "nonowner-chat", directoryB, new(EventActorKind.User, "owner"),
                definition: "fixture.other:execute");
            Finish(foreign);
            var service = Direct("service-before-owner", "nonowner-chat", directoryB, new(EventActorKind.Service, "fixture.service"));
            host.Store.Steer(new("service-owner-steering", service.Id, "Fixed synthetic owner steering."));
            Check(host.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, "nonowner-chat") is null,
                "owner steering of a Service root cannot manufacture an original owner submission");
            Finish(service);
            var agent = host.Submit(Request("agent-before-owner", "nonowner-chat", directoryB), profile);
            host.Store.Steer(new("agent-owner-steering", agent.Id, "Fixed synthetic owner steering."));
            Check(agent.Submission.Caller is { Kind: EventActorKind.Agent } &&
                host.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, "nonowner-chat") is null,
                "an Agent root and its owner steering cannot establish the chat working directory");
            Finish(agent);
            var genuineOwner = host.Submit(Request("genuine-owner", "nonowner-chat", directoryA), null);
            Check(host.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, "nonowner-chat") is { } owner &&
                owner.RequestId == genuineOwner.Id && owner.Caller is { Kind: EventActorKind.User },
                "first genuine owner ignores earlier other-definition, Service and Agent roots");
            Finish(genuineOwner);
            RejectWithoutAdmission(Request("owner-after-nonowner", "nonowner-chat", directoryB), directoryA);

            var parent = Direct("root-parent", "parent-chat", directoryA, new(EventActorKind.User, "owner"));
            var child = Direct("delegated-child", "child-chat", directoryA,
                new(EventActorKind.Service, "cerebral.coordination"), parent.Id);
            host.Store.Steer(new("child-owner-steering", child.Id, "Fixed synthetic owner steering."));
            Check(host.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, "child-chat") is null &&
                child.Submission.ParentRunId == parent.Id,
                "a genuine delegated child and its owner steering never lock an owner chat root");
            Finish(child);
            var childContextOwner = host.Submit(Request("owner-after-child", "child-chat", directoryB), null);
            Check(host.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, "child-chat")?.RequestId == childContextOwner.Id &&
                host.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, "parent-chat")?.RequestId == parent.Id,
                "a later root owner establishes its own cwd without changing the parent original source");
            Finish(childContextOwner);
            Finish(parent);

            var implicitOwner = Direct("legacy-null-caller", "null-caller-chat", directoryA);
            Check(host.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, "null-caller-chat") is { } implicitSubmission &&
                implicitSubmission.RequestId == implicitOwner.Id && implicitSubmission.Caller is null,
                "legacy null Caller is the Engine's existing implicit User encoding");
            Finish(implicitOwner);
            RejectWithoutAdmission(Request("null-caller-continuation", "null-caller-chat", directoryB), directoryA);

            host = OpenHost();
            var recoveredOriginal = host.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, "bound-chat");
            Check(recoveredOriginal is not null && recoveredOriginal.RequestId == first.Id &&
                recoveredOriginal.Input.GetRawText() == savedInput &&
                recoveredOriginal.SettingsSnapshot.GetRawText() == savedSettings &&
                recoveredOriginal.Input.GetProperty("workingDirectory").GetString() == originalText,
                "reopening private SQLite preserves exact original input and accepted settings independently of later turns");

            Console.WriteLine($"Conversation root admission checks passed ({checks}; isolated Worker/SQLite fixtures, tools absent, no inference or provider execution).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var checkedRoot = Path.GetFullPath(root);
            var checkedParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(checkedRoot));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!Path.IsPathFullyQualified(checkedRoot) || checkedParent is null ||
                !string.Equals(Path.TrimEndingDirectorySeparator(checkedParent), temporaryParent, comparison))
                throw new InvalidOperationException("Refusing cleanup outside the exact owned fixture parent.");
            Directory.Delete(checkedRoot, recursive: true);
        }
        return Task.CompletedTask;
    }
}
