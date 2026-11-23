using Cerebral.Contracts.Workflows;

namespace Cerebral.Orchestrator;

/// <summary>Pure transition projection and controlled fake activation. No WPF, tray, network or work execution.</summary>
public static class WorkAttentionChecks
{
    public static int Verify() => Task.Run(VerifyAsync).GetAwaiter().GetResult();

    private static async Task<int> VerifyAsync()
    {
        var count = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); count++; }
        static WorkAttentionRunState Run(string id, long revision, WorkflowStatus status = WorkflowStatus.Running) =>
            new("main-a", id, revision, status, null, null, []);
        static WorkAttentionRunState Question(string id, long revision, long questionRevision = 1) =>
            new("main-a", id, revision, WorkflowStatus.Waiting, "question-a", questionRevision, ["field-a"]);
        var projection = new MainWorkAttention();
        WorkAttentionTarget? Observe(WorkAttentionRunState state, bool background = true, bool eligible = true) =>
            projection.Observe(1, "owner-a", "main-a", 1, state, background, eligible);

        Check(Observe(Run("old", 5, WorkflowStatus.Completed)) is null, "Hydrating an old completion never emits a notice.");
        Check(Observe(Run("live", 1)) is null, "A normal active run does not emit attention.");
        var completion = Observe(Run("live", 2, WorkflowStatus.Completed));
        Check(completion is { Kind: WorkAttentionKind.Completion, MainConversationId: "main-a", ConversationId: "main-a",
            RunId: "live", RunRevision: 2 }, "A genuine later completion captures the original exact run.");
        Check(Observe(Run("live", 3, WorkflowStatus.Completed)) is null, "Receipt refresh/revision changes cannot repeat a run completion.");
        Check(Observe(Run("live", 1, WorkflowStatus.Failed)) is null, "An older run revision cannot fabricate another terminal outcome.");
        Check(Observe(Question("question-run", 2)) is { QuestionId: "question-a", QuestionRevision: 1, QuestionFieldId: "field-a" },
            "A new question captures the exact set, revision and unanswered field.");
        Check(Observe(Question("question-run", 3)) is null, "The same question revision is deduplicated across run refreshes.");
        Check(Observe(Question("question-run", 4, 2)) is { QuestionRevision: 2 }, "A new question revision is separately identified.");
        Check(Observe(Question("question-run", 4, 3)) is null, "Contradictory same-run-revision state is rejected.");
        Check(Observe(Run("foreground", 1, WorkflowStatus.Completed), background: false) is null &&
            Observe(Run("foreground", 2, WorkflowStatus.Completed)) is null,
            "A completion already visible in foreground Main cannot reappear merely because Main is later hidden.");
        Check(Observe(Run("coordinated", 1)) is null &&
            Observe(Run("coordinated", 2, WorkflowStatus.Completed), eligible: false) is null &&
            Observe(Run("coordinated", 2, WorkflowStatus.Completed), eligible: true) is not null,
            "A known active collaboration defers its parent acknowledgment until observed work finishes.");
        var hydration = new MainWorkAttention();
        Check(hydration.Observe(1, "owner-a", "main-a", 1, Question("old-question", 1), true) is null,
            "Initial hydration of a question establishes a baseline without a flood.");
        Check(hydration.Observe(1, "owner-a", "main-a", 2, Question("old-question", 2, 2), true) is null,
            "Selecting a view establishes a new hydration baseline.");
        Check(hydration.Observe(2, "owner-b", "main-a", 2, Question("old-question", 3, 3), true) is null,
            "A new owner connection starts from a new baseline.");
        for (var index = 0; index < 600; index++) Observe(Run("bounded-" + index, 1, WorkflowStatus.Completed));
        Check(projection.SeenCount <= MainWorkAttention.MaximumSeen, "Retained deduplication state is bounded.");

        var blank = new MainWorkAttention();
        Check(blank.Observe(1, "owner-a", "main-a", 1, null, true) is null &&
            blank.Observe(1, "owner-a", "main-a", 1, Run("fast-first", 1, WorkflowStatus.Completed), true) is not null,
            "An already observed blank new chat can notify a fast first completion without confusing it with historical hydration.");

        var target = completion!;
        var readCount = 0; var navigationCount = 0; string? opened = null;
        Task<WorkAttentionRunState?> Read(WorkAttentionTarget exact, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); readCount++;
            Check(ReferenceEquals(exact, target), "Activation passes the originally captured target into the read.");
            return Task.FromResult<WorkAttentionRunState?>(Run("live", 4, WorkflowStatus.Completed));
        }
        Task<bool> Navigate(WorkAttentionTarget exact, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); navigationCount++; opened = exact.MainConversationId + "/" + exact.RunId;
            return Task.FromResult(true);
        }
        var result = await WorkAttentionActivation.ActivateAsync(target, () => true, Read, Navigate, CancellationToken.None);
        Check(result.Navigated && opened == "main-a/live" && readCount == 1 && navigationCount == 1,
            "A click while another chat is selected still reads and opens only the original exact target.");

        async Task Reject(WorkAttentionTarget notice, WorkAttentionRunState? state, string name)
        {
            var calls = 0;
            var rejected = await WorkAttentionActivation.ActivateAsync(notice, () => true,
                (_, _) => Task.FromResult(state), (_, _) => { calls++; return Task.FromResult(true); }, CancellationToken.None);
            Check(!rejected.Navigated && calls == 0, name);
        }
        await Reject(target, null, "A removed or unavailable target cannot navigate any fallback.");
        await Reject(target, Run("foreign", 8, WorkflowStatus.Completed), "A different run cannot satisfy a notification.");
        await Reject(target, Run("live", 8, WorkflowStatus.Completed) with { ConversationId = "foreign" },
            "An identically named run in another context cannot satisfy a notification.");
        await Reject(target, Run("live", 1, WorkflowStatus.Completed), "An older saved revision is refused.");
        await Reject(target, Run("live", 8), "A stale completion cannot retarget current running work.");
        var questionTarget = new WorkAttentionTarget(1, "owner-a", "main-a", "main-a", "question-run", 2,
            WorkAttentionKind.Question, WorkflowStatus.Waiting, QuestionId: "question-a", QuestionRevision: 1, QuestionFieldId: "field-a");
        await Reject(questionTarget, Question("question-run", 3, 2), "A revised question is not the original question revision.");
        await Reject(questionTarget, Question("question-run", 3) with { UnansweredFields = [] },
            "An answered field is resolved and cannot be refocused as unanswered.");
        await Reject(questionTarget, Run("question-run", 3, WorkflowStatus.Completed), "A completed run cannot revive a question.");
        await Reject(target with { MainConversationId = "foreign" }, Run("live", 8, WorkflowStatus.Completed),
            "A malformed original Main/context relationship is refused.");

        var current = true;
        var held = new TaskCompletionSource<WorkAttentionRunState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var raced = WorkAttentionActivation.ActivateAsync(target, () => current, (_, _) => held.Task, Navigate, CancellationToken.None);
        current = false; held.SetResult(Run("live", 8, WorkflowStatus.Completed));
        var before = navigationCount;
        Check(!(await raced).Navigated && navigationCount == before,
            "An owner or human view change during a held read cancels activation before navigation.");
        var blocked = await WorkAttentionActivation.ActivateAsync(target, () => true,
            (_, _) => Task.FromResult<WorkAttentionRunState?>(Run("live", 8, WorkflowStatus.Completed)),
            (_, _) => Task.FromResult(false), CancellationToken.None);
        Check(!blocked.Navigated, "Draft/attachment admission can refuse navigation without selecting other work.");
        var noRead = 0;
        var stale = await WorkAttentionActivation.ActivateAsync(target, () => false,
            (_, _) => { noRead++; return Task.FromResult<WorkAttentionRunState?>(null); }, Navigate, CancellationToken.None);
        Check(!stale.Navigated && noRead == 0, "An old owner token is rejected before any read.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancellationObserved = false;
        try { await WorkAttentionActivation.ActivateAsync(target, () => true, Read, Navigate, cancelled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        Check(cancellationObserved, "Cancellation remains cancellation and never becomes an activation or retry.");
        return count;
    }
}
