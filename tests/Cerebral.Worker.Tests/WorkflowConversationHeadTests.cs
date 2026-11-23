using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Read-only conversation-head following, identity rejection and serialized action races.</summary>
internal static class WorkflowConversationHeadTests
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new Exception("FAIL: " + label); checks++; }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException)
            { checks++; return; }
            throw new Exception("FAIL: " + label);
        }

        var fixture = new Fixture();
        var client = await fixture.Open();
        var anchor = client.Snapshot.Events[0];
        await client.ExpandRecentTranscriptAsync();
        Check(client.Snapshot.OriginalMessage?.Id == anchor.Id, "exact accepted original message is pinned before child follow");
        var before = fixture.Calls.Count;
        Check(client.IsRelevant(new(20, "workflow.updated", Fixture.ChildId,
                  CerebralJson.Element(new { conversationId = Fixture.ConversationId, definitionId = ConversationControlClient.ChatDefinitionId }), DateTimeOffset.UnixEpoch)) &&
              !client.IsRelevant(new(21, "workflow.updated", Fixture.ChildId,
                  CerebralJson.Element(new { conversationId = "foreign", definitionId = ConversationControlClient.ChatDefinitionId }), DateTimeOffset.UnixEpoch)) &&
              !client.IsRelevant(new(22, "workflow.updated", Fixture.ChildId,
                  CerebralJson.Element(new { conversationId = Fixture.ConversationId, definitionId = "foreign:definition" }), DateTimeOffset.UnixEpoch)) &&
              !client.IsRelevant(new(23, "workflow.updated", Fixture.ChildId, CerebralJson.Element("invalid"), DateTimeOffset.UnixEpoch)) &&
              fixture.Calls.Count == before && client.Snapshot.RunId == Fixture.ParentId,
            "child wakeup coordinates indicate relevance without trusting status or causing reads");
        fixture.Head = fixture.Summary(fixture.Child);
        fixture.ShowChildEvents = true;
        await client.RefreshConversationAsync(() => true);
        Check(client.Snapshot.Run is { Id: Fixture.ChildId, Status: WorkflowStatus.Running } &&
              client.Snapshot.OriginalMessage?.Id == anchor.Id && client.Snapshot.Transcript.Count() == 3,
            "terminal ordinary Chat adopts an authoritative later service child and retains original transcript");
        Check(fixture.Calls.All(call => call.ProfileId is null) &&
              !fixture.Calls.Any(call => call.Operation is "jobs.workflow.submit" or "jobs.workflow.answer" or "jobs.workflow.cancel"),
            "automatic follow is bounded owner reads only with no replay, submit or mutation");

        foreach (var invalid in new[]
        {
            fixture.Summary(fixture.Child) with { Id = "foreign-conversation" },
            fixture.Summary(fixture.Child) with { ProfileId = "foreign-owner" },
            fixture.Summary(fixture.Child) with { Status = (WorkflowStatus)999 },
            fixture.Summary(fixture.Child) with { LatestRunId = "bad id" }
        })
        {
            var bad = new Fixture { Head = invalid };
            var selected = await bad.Open();
            var oldEvents = selected.Snapshot.Events;
            await Reject(() => selected.RefreshConversationAsync(() => true), "foreign or malformed head is rejected");
            Check(selected.Snapshot.RunId == Fixture.ParentId && ReferenceEquals(selected.Snapshot.Events, oldEvents) &&
                  bad.Calls.Count(call => call.Operation == "jobs.workflow.get") == 2,
                "invalid head retains transcript and never reads a foreign candidate");
        }

        foreach (var change in new Func<WorkflowSubmission, WorkflowSubmission>[]
        {
            submission => submission with { ConversationId = "foreign-conversation" },
            submission => submission with { DefinitionId = "foreign:definition" },
            submission => submission with { DefinitionVersion = "99.0.0" },
            submission => submission with { ProfileId = "foreign-owner" },
            submission => submission with { ProjectId = "foreign-project" },
            submission => submission with { ConversationSequence = 0 },
            submission => submission with { RequestId = "foreign-request" }
        })
        {
            var bad = new Fixture();
            bad.Child = bad.Child with { Submission = change(bad.Child.Submission) };
            bad.Head = bad.Summary(bad.Child);
            var selected = await bad.Open();
            var oldEvents = selected.Snapshot.Events;
            await Reject(() => selected.RefreshConversationAsync(() => true), "foreign candidate identity or admission boundary is rejected");
            Check(selected.Snapshot.RunId == Fixture.ParentId && ReferenceEquals(selected.Snapshot.Events, oldEvents),
                "candidate rejection never clears retained transcript");
        }

        var userHead = new Fixture();
        userHead.Child = userHead.Child with { Submission = userHead.Child.Submission with { Caller = new(EventActorKind.User) } };
        userHead.Head = userHead.Summary(userHead.Child);
        var userClient = await userHead.Open();
        await userClient.RefreshConversationAsync(() => true);
        Check(userClient.Snapshot.RunId == Fixture.ParentId, "automatic service follow does not redirect to a separately submitted user run");

        var held = new Fixture();
        var heldClient = await held.Open();
        held.Head = held.Summary(held.Child);
        await heldClient.RefreshConversationAsync(() => false);
        Check(!held.Calls.Any(call => call.Operation == "conversations.get"), "host history/manual-inspection guard skips head lookup");
        held.Parent = held.Parent with { Status = WorkflowStatus.Running, Revision = 2 };
        await heldClient.RefreshConversationAsync(() => true);
        Check(!held.Calls.Any(call => call.Operation == "conversations.get"), "active selected work is never replaced by a child head");

        var uncertain = new Fixture();
        var uncertainClient = await uncertain.Open();
        uncertain.FailCancel = true;
        await Reject(() => uncertainClient.CancelAsync(), "transport failure records uncertain exact cancellation");
        uncertain.Head = uncertain.Summary(uncertain.Child);
        uncertain.FailCancel = false;
        await uncertainClient.RefreshConversationAsync(() => true);
        Check(!uncertainClient.Snapshot.MutationUncertain && uncertainClient.Snapshot.RunId == Fixture.ParentId &&
              !uncertain.Calls.Any(call => call.Operation == "conversations.get"),
            "refresh can resolve uncertainty without adopting a child in the same operation");

        async Task Race(string action)
        {
            var racing = new Fixture();
            var selected = await racing.Open();
            racing.Head = racing.Summary(racing.Child);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            racing.HeadRead = async token =>
            { entered.TrySetResult(); await release.Task.WaitAsync(token); return racing.Head; };
            var follow = selected.RefreshConversationAsync(() => true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = action switch
            {
                "cancel" => selected.CancelAsync(),
                "inspect" => selected.RefreshAsync(latestEvents: false),
                "open" => selected.OpenAsync(Fixture.ParentId),
                _ => throw new InvalidOperationException()
            };
            release.TrySetResult();
            await follow; await queued;
            Check(selected.Snapshot.RunId == Fixture.ParentId &&
                  !racing.Calls.Any(call => call.Operation == "jobs.workflow.get" &&
                      call.Arguments.GetProperty("id").GetString() == Fixture.ChildId),
                "queued " + action + " suppresses child adoption before the serialized gate");
            if (action == "cancel")
                Check(racing.Calls.Single(call => call.Operation == "jobs.workflow.cancel").Arguments.GetProperty("id").GetString() == Fixture.ParentId,
                    "queued cancellation keeps its captured exact run identity");
        }
        await Race("cancel"); await Race("inspect"); await Race("open");

        var lateHistory = new Fixture();
        var historyClient = await lateHistory.Open();
        lateHistory.Head = lateHistory.Summary(lateHistory.Child);
        var mayFollow = true;
        var retainedPage = historyClient.Snapshot.Events;
        lateHistory.HeadRead = _ => { mayFollow = false; return Task.FromResult(lateHistory.Head); };
        await historyClient.RefreshConversationAsync(() => mayFollow);
        Check(historyClient.Snapshot.RunId == Fixture.ParentId && ReferenceEquals(retainedPage, historyClient.Snapshot.Events),
            "host inspection guard is rechecked after asynchronous head reads and retains the displayed page");

        var unpinned = new Fixture();
        var unpinnedClient = await unpinned.Open();
        unpinned.Head = unpinned.Summary(unpinned.Child);
        unpinned.ShowChildEvents = true;
        await unpinnedClient.RefreshConversationAsync(() => true);
        Check(unpinnedClient.Snapshot.RunId == Fixture.ChildId && unpinnedClient.Snapshot.OriginalMessage?.Id == "parent-request",
            "automatic child follow pins the original accepted request before adopting child metadata");

        var exact = new Fixture();
        var exactClient = await exact.Open();
        exact.Head = exact.Summary(exact.Child);
        await exactClient.RefreshAsync();
        Check(exactClient.Snapshot.RunId == Fixture.ParentId && !exact.Calls.Any(call => call.Operation == "conversations.get"),
            "ordinary exact-run Refresh retains its selected run without querying a conversation head");
        return checks;
    }

    private sealed class Fixture
    {
        internal const string ConversationId = "follow-conversation";
        internal const string ParentId = "follow-parent";
        internal const string ChildId = "follow-child";
        internal WorkflowRun Parent = Run(ParentId, 0, WorkflowStatus.Completed, new(EventActorKind.User));
        internal WorkflowRun Child = Run(ChildId, 2, WorkflowStatus.Running, new(EventActorKind.Service, "cerebral.plugin-tests", "Cerebral plugin tests"));
        internal ConversationSummary Head;
        internal Func<CancellationToken, Task<ConversationSummary>>? HeadRead;
        internal bool ShowChildEvents, FailCancel;
        internal List<CommandRequest> Calls { get; } = [];
        internal Fixture() => Head = Summary(Parent);
        internal ConversationSummary Summary(WorkflowRun run) => new(ConversationId, "Fixture", "lead", null,
            run.Id, run.Status, DateTimeOffset.UnixEpoch);
        internal async Task<WorkflowConversationClient> Open()
        {
            var client = new WorkflowConversationClient(ConversationId, Execute);
            await client.OpenAsync(ParentId);
            return client;
        }
        private async Task<CommandResult> Execute(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(request);
            return request.Operation switch
            {
                "jobs.workflow.get" => CommandResult.Ok(request.Arguments.GetProperty("id").GetString() == ParentId ? Parent : Child),
                "conversations.get" => CommandResult.Ok(HeadRead is null ? Head : await HeadRead(token)),
                "jobs.workflow.events.previous" => CommandResult.Ok(new { events = Events(),
                    snapshotSequence = ShowChildEvents ? 3 : 2, nextBeforeSequence = (long?)null }),
                "jobs.workflow.cancel" => FailCancel ? throw new IOException("Fixture cancellation transport lost.") : CommandResult.Ok(Parent),
                _ => throw new InvalidOperationException("Unexpected head-follow fixture operation: " + request.Operation)
            };
        }
        private CerebralEvent[] Events() => [
            Message("parent-request", ParentId, 1, new(EventActorKind.User), "Build my plugin."),
            Message("parent-response", ParentId, 2, new(EventActorKind.Agent), "Built."),
            .. ShowChildEvents ? new[] { Message("child-request", ChildId, 3, Child.Submission.Caller!, "Build my plugin.") } : []
        ];
        private static CerebralEvent Message(string id, string turn, long sequence, EventActor actor, string text) =>
            new(id, ConversationId, turn, sequence, CerebralEventTypes.Message, actor, [new TextContentPart(text)],
                new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.Service), DateTimeOffset.UnixEpoch);
        private static WorkflowRun Run(string id, long sequence, WorkflowStatus status, EventActor caller) => new()
        {
            Id = id,
            Submission = new(id, ConversationId, "lead", ConversationControlClient.ChatDefinitionId,
                ConversationControlClient.ChatDefinitionVersion, "Build my plugin.", null, WorkflowContract.EmptyObject,
                WorkflowContract.EmptyObject) { ConversationSequence = sequence, Caller = caller },
            Revision = 1, Status = status, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch
        };
    }
}
