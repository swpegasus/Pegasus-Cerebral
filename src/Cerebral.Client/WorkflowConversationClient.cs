using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Client;

/// <summary>Caller keeps RequestId stable across an explicit retry; it is also the accepted run ID.</summary>
public sealed record WorkflowConversationSubmission(string RequestId, string ProfileId, string DefinitionId,
    string DefinitionVersion, string UserText, JsonElement Input, string? AssignmentText = null, int StepLimit = 64)
{
    public IReadOnlyList<ContentPart> AcceptedContent { get; init; } = [];
    // Host-owned project placement is separate from a plug-in's validated definition input.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerProjectId { get; init; }
    /// <summary>Optional owner choice. The Worker admits the loaded model, definition and policy.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowLocalGenerationChoice? LocalGeneration { get; init; }
    /// <summary>Closed owner recipe, carried only by a client bound to one reviewed local Worker.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? OwnerVerification { get; init; }
}

/// <summary>Presentation only: no harness state, model prompt, grant snapshot or executable arguments.</summary>
public sealed record WorkflowConversationRun(string Id, string ConversationId, string ProfileId, string UserText, string DefinitionId, string DefinitionVersion,
    long Revision, WorkflowStatus Status, WorkflowQuestionSet? Questions, JsonElement? Result, string? WaitingReason,
    string? Error, bool CancelRequested, string? EffectId, WorkflowEffectState? EffectState, int Steps)
{
    public bool IsTerminal => Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled;
    public long ActiveMilliseconds { get; init; }
    public WorkflowActivityTotals? ActivityTotals { get; init; }
    public int ReservedExecutionMilliseconds { get; init; }
    /// <summary>Exact current effect identity for an explicit owner intervention.</summary>
    public string? EffectSha256 { get; init; }
}

/// <summary>One bounded event view, not a second history store or a model-context selection.</summary>
public sealed record WorkflowConversationSnapshot(string ConversationId, string? RunId, WorkflowConversationRun? Run,
    IReadOnlyList<CerebralEvent> Events, long SnapshotSequence, bool HasOlder, bool HasNewer,
    bool MutationUncertain, string? LastMutationRequestId)
{
    /// <summary>The exact visible request anchor, retained separately from page cursors and across accepted service continuations.</summary>
    public CerebralEvent? OriginalMessage { get; init; }
    // Source events retain their original model policy. These views never rewrite exclusion flags.
    public IEnumerable<CerebralEvent> Transcript => Events.Where(EventProjection.ShowInTranscript);
    public IEnumerable<CerebralEvent> Activity => Events.Where(EventProjection.ShowInActivity);
    public IEnumerable<CerebralEvent> Sidebar => Events.Where(EventProjection.ShowInSidebar);
}

public sealed class WorkflowConversationCommandException(string operation, string message) : InvalidOperationException(message)
{
    public string Operation { get; } = operation;
}

/// <summary>
/// Serializes actions for one desktop conversation. The service owns history, scheduling and idempotency.
/// Reconnect only reads; no command is automatically retried, resumed, reconciled or cancelled.
/// Dispose/own the underlying CerebralClient separately. This adapter creates no background task.
/// </summary>
public sealed class WorkflowConversationClient
{
    public const int MaximumPageEvents = 200;
    public const int DefaultRecentTranscriptEvents = 8;
    public const int MaximumRecentPages = 4;
    public const int MaximumPageBytes = 4 * 1024 * 1024;
    private const int MaximumRunBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options) { WriteIndented = false };
    private readonly Func<CommandRequest, CancellationToken, Task<CommandResult>> command;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly int pageSize;
    private readonly bool verifiedLocalOwner;
    private WorkflowConversationSnapshot snapshot;
    private WorkflowConversationSubmission? lastSubmission;
    private WorkflowSubmission? acceptedSubmission;
    private string? lastSubmissionJson;
    private string? lastAnswerJson;
    private string? lastAnswerRequestId;
    private int pendingMutations;
    private int pendingExactInspections;

    public WorkflowConversationClient(string conversationId, CerebralClient client, int pageSize = 40)
        : this(conversationId, (request, token) => client.CommandAsync(request.Operation, request.Arguments,
            request.ProfileId, token), pageSize)
    { ArgumentNullException.ThrowIfNull(client); verifiedLocalOwner = client.HasExpectedLocalOwner; }

    public WorkflowConversationClient(string conversationId,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> command, int pageSize = 40)
    {
        Identifier(conversationId, nameof(conversationId));
        ArgumentNullException.ThrowIfNull(command);
        if (pageSize is < 1 or > MaximumPageEvents) throw new ArgumentOutOfRangeException(nameof(pageSize));
        this.command = command; this.pageSize = pageSize;
        snapshot = new(conversationId, null, null, Array.Empty<CerebralEvent>(), 0, false, false, false, null);
    }

    public WorkflowConversationSnapshot Snapshot => Volatile.Read(ref snapshot);

    /// <summary>Use notifications only to coalesce a refresh in the UI. Their payload is not authoritative state.</summary>
    public bool IsRelevant(ServiceEvent notification)
    {
        var current = Snapshot;
        if (notification.Type != "workflow.updated" || notification.JobId is null) return false;
        if (notification.JobId == current.RunId) return true;
        // These coordinates only wake a read. They never prove that a foreign run may be adopted.
        return current.Run is { } run && notification.Data.ValueKind == JsonValueKind.Object &&
            notification.Data.TryGetProperty("conversationId", out var conversation) && conversation.ValueKind == JsonValueKind.String &&
            conversation.GetString() == current.ConversationId &&
            notification.Data.TryGetProperty("definitionId", out var definition) && definition.ValueKind == JsonValueKind.String &&
            definition.GetString() == run.DefinitionId;
    }

    public Task<WorkflowConversationSnapshot> SubmitAsync(WorkflowConversationSubmission submission, CancellationToken token = default)
    {
        var frozen = FreezeSubmission(submission); // Snapshot before waiting for the serialized UI action gate.
        if (frozen.OwnerVerification is not null && !verifiedLocalOwner)
            throw new UnauthorizedAccessException("Owner verification requires an SDK client bound to the exact local Worker owner.");
        return SerializedMutation(async () =>
        {
            var serialized = JsonSerializer.Serialize(frozen, Json);
            if (lastSubmission?.RequestId == frozen.RequestId && lastSubmissionJson != serialized)
                throw new InvalidOperationException("A submission retry must keep its original request ID and exact input together.");
            if (snapshot.MutationUncertain && snapshot.RunId != frozen.RequestId)
                throw new InvalidOperationException("Refresh the uncertain run before starting different work.");
            if (snapshot.Run is { IsTerminal: false } active && active.Id != frozen.RequestId)
                throw new InvalidOperationException("Answer, cancel or finish the current run before starting another.");
            lastSubmission = frozen; lastSubmissionJson = serialized;
            var arguments = CerebralJson.Element(new
            {
                frozen.RequestId, snapshot.ConversationId, frozen.ProfileId, frozen.DefinitionId,
                frozen.DefinitionVersion, frozen.UserText, frozen.AssignmentText, frozen.AcceptedContent, frozen.Input, frozen.StepLimit
            });
            if (frozen.OwnerProjectId is { } ownerProjectId)
            {
                var envelope = JsonNode.Parse(arguments.GetRawText())!.AsObject();
                envelope["ownerProjectId"] = ownerProjectId;
                arguments = JsonSerializer.SerializeToElement(envelope, Json);
            }
            if (frozen.LocalGeneration is { } localGeneration)
            {
                var envelope = JsonNode.Parse(arguments.GetRawText())!.AsObject();
                envelope["localGeneration"] = JsonSerializer.SerializeToNode(localGeneration, Json);
                arguments = JsonSerializer.SerializeToElement(envelope, Json);
            }
            if (frozen.OwnerVerification is { } ownerVerification)
            {
                var envelope = JsonNode.Parse(arguments.GetRawText())!.AsObject();
                envelope["ownerVerification"] = JsonNode.Parse(ownerVerification.GetRawText());
                arguments = JsonSerializer.SerializeToElement(envelope, Json);
            }
            if (snapshot.RunId != frozen.RequestId)
                Publish(snapshot with { RunId = frozen.RequestId, Run = null, OriginalMessage = null, MutationUncertain = false });
            await Mutate("jobs.workflow.submit", arguments, frozen.RequestId, token).ConfigureAwait(false);
            await LatestEvents(token).ConfigureAwait(false);
        }, token);
    }

    /// <summary>Reopens an existing run; never submits missing work or starts a replacement run.</summary>
    public Task<WorkflowConversationSnapshot> OpenAsync(string runId, CancellationToken token = default) => SerializedInspection(async () =>
    {
        Identifier(runId, nameof(runId));
        if (snapshot.MutationUncertain && snapshot.RunId != runId)
            throw new InvalidOperationException("Resolve the uncertain selected run before opening another run in this adapter.");
        var run = await ReadRun(runId, token).ConfigureAwait(false);
        if (snapshot.RunId != runId)
            Publish(snapshot with { RunId = runId, Run = null, OriginalMessage = null, Events = Array.Empty<CerebralEvent>(), SnapshotSequence = 0, HasOlder = false, HasNewer = false });
        Adopt(run);
        await LatestEvents(token).ConfigureAwait(false);
    }, token);

    /// <summary>Reconnect queries saved state. Set latestEvents=false to retain the page being read.</summary>
    public Task<WorkflowConversationSnapshot> RefreshAsync(bool latestEvents = true, CancellationToken token = default)
    {
        var expectedRunId = SelectedRun();
        return SerializedInspection(async () =>
        {
            RequireSelectedRun(expectedRunId);
            Adopt(await ReadRun(expectedRunId, token).ConfigureAwait(false));
            if (latestEvents) await LatestEvents(token).ConfigureAwait(false);
        }, token);
    }

    /// <summary>
    /// Ordinary Chat can follow a later accepted service continuation after its selected run ends.
    /// The host guard protects following and latest-page refresh, is rechecked before adoption,
    /// and may execute outside its caller context.
    /// This only reads authoritative state; exact-run Open/Refresh/Inspect behavior remains separate.
    /// </summary>
    public Task<WorkflowConversationSnapshot> RefreshConversationAsync(Func<bool> canFollow,
        bool latestEvents = true, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(canFollow);
        var expectedRunId = SelectedRun();
        var wasUncertain = snapshot.MutationUncertain;
        return Serialized(async () =>
        {
            RequireSelectedRun(expectedRunId);
            Adopt(await ReadRun(expectedRunId, token).ConfigureAwait(false));
            bool Eligible() => snapshot.RunId == expectedRunId && snapshot.Run is { IsTerminal: true } &&
                !wasUncertain && !snapshot.MutationUncertain && Volatile.Read(ref pendingMutations) == 0 &&
                Volatile.Read(ref pendingExactInspections) == 0 && canFollow();
            if (latestEvents && snapshot.Run?.DefinitionId == ConversationControlClient.ChatDefinitionId && Eligible())
            {
                var origin = acceptedSubmission ?? throw new InvalidDataException("The selected conversation has no accepted run.");
                var head = await Read<ConversationSummary>("conversations.get",
                    CerebralJson.Element(new { id = snapshot.ConversationId }), 65_536, token).ConfigureAwait(false);
                if (head.Id != snapshot.ConversationId || head.ProfileId != origin.ProfileId || !Enum.IsDefined(head.Status))
                    throw new InvalidDataException("The service returned a foreign or invalid conversation head. The current transcript was retained.");
                try { Identifier(head.LatestRunId, nameof(head.LatestRunId)); }
                catch (ArgumentException error)
                { throw new InvalidDataException("The service returned an invalid conversation head run ID. The current transcript was retained.", error); }
                if (head.LatestRunId != expectedRunId && Eligible())
                {
                    var candidate = await ReadRun(head.LatestRunId, token).ConfigureAwait(false);
                    if (candidate.Submission.DefinitionId != origin.DefinitionId ||
                        candidate.Submission.DefinitionVersion != origin.DefinitionVersion ||
                        candidate.Submission.ProfileId != origin.ProfileId || candidate.Submission.ProjectId != origin.ProjectId ||
                        candidate.Submission.ConversationSequence <= origin.ConversationSequence)
                        throw new InvalidDataException("The newer conversation run does not match its accepted definition, owner or admission boundary. The current transcript was retained.");
                    if (candidate.Submission.Caller?.Kind == EventActorKind.Service && Eligible())
                    {
                        // Keep the pinned user request and page cache; the child owns only current
                        // progress/questions/actions. It does not replace the original visible request.
                        await RetainOriginalMessage(token).ConfigureAwait(false);
                        if (Eligible()) Adopt(candidate);
                    }
                }
            }
            if (latestEvents && canFollow()) await LatestEvents(token).ConfigureAwait(false);
        }, token);
    }

    public Task<WorkflowConversationSnapshot> LoadOlderAsync(CancellationToken token = default) => Serialized(async () =>
    {
        if (!snapshot.HasOlder || snapshot.Events.Count == 0) return;
        var before = snapshot.Events[0].Sequence;
        var page = await Read<PreviousEvents>("jobs.workflow.events.previous", CerebralJson.Element(new
        { id = SelectedRun(), beforeSequence = before, snapshotSequence = snapshot.SnapshotSequence, limit = pageSize }), MaximumPageBytes + 4096, token).ConfigureAwait(false);
        AdoptPage(page.Events, page.SnapshotSequence, page.NextBeforeSequence, previous: true, before, snapshot.SnapshotSequence);
    }, token);

    /// <summary>
    /// Expands the newest service page with a few pinned older pages until the desktop has a useful
    /// recent transcript. Activity-heavy turns can consume a whole raw page, so a single event page
    /// is not a useful conversation window. The result remains bounded and uses the same snapshot.
    /// </summary>
    public Task<WorkflowConversationSnapshot> ExpandRecentTranscriptAsync(
        int minimumTranscriptEvents = DefaultRecentTranscriptEvents,
        int maximumPages = MaximumRecentPages,
        CancellationToken token = default) => Serialized(async () =>
    {
        if (minimumTranscriptEvents is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(minimumTranscriptEvents));
        if (maximumPages is < 1 or > MaximumRecentPages) throw new ArgumentOutOfRangeException(nameof(maximumPages));
        if (snapshot.HasNewer) throw new InvalidOperationException("Return to the newest workflow page before expanding its recent transcript.");
        if (snapshot.Events.Count == 0) return;
        await RetainOriginalMessage(token).ConfigureAwait(false);
        if (!snapshot.HasOlder || snapshot.Events.Count(EventProjection.ShowInTranscript) >= minimumTranscriptEvents) return;

        var merged = snapshot.Events.ToList();
        var pages = 1;
        while (snapshot.HasOlder && pages < maximumPages &&
               merged.Count(EventProjection.ShowInTranscript) < minimumTranscriptEvents)
        {
            var before = snapshot.Events[0].Sequence;
            var pinned = snapshot.SnapshotSequence;
            var page = await Read<PreviousEvents>("jobs.workflow.events.previous", CerebralJson.Element(new
            { id = SelectedRun(), beforeSequence = before, snapshotSequence = pinned, limit = pageSize }),
                MaximumPageBytes + 4096, token).ConfigureAwait(false);
            AdoptPage(page.Events, page.SnapshotSequence, page.NextBeforeSequence, previous: true, before, pinned);
            merged.InsertRange(0, snapshot.Events);
            pages++;
        }

        if (merged.Count > pageSize * maximumPages || merged.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != merged.Count)
            throw new InvalidDataException("The recent workflow window exceeded its event bounds or repeated an event.");
        Publish(snapshot with { Events = merged.AsReadOnly(), HasNewer = false });
    }, token);

    public Task<WorkflowConversationSnapshot> LoadNewerAsync(CancellationToken token = default) => Serialized(async () =>
    {
        if (!snapshot.HasNewer || snapshot.Events.Count == 0) return;
        var after = snapshot.Events[^1].Sequence;
        var page = await Read<ForwardEvents>("jobs.workflow.events", CerebralJson.Element(new
        { id = SelectedRun(), afterSequence = after, snapshotSequence = snapshot.SnapshotSequence, limit = pageSize }), MaximumPageBytes + 4096, token).ConfigureAwait(false);
        AdoptPage(page.Events, page.SnapshotSequence, page.NextAfterSequence, previous: false, after, snapshot.SnapshotSequence);
    }, token);

    /// <summary>The form supplies its displayed question identity/revision. Never substitute the newest revision.</summary>
    public Task<WorkflowConversationSnapshot> AnswerAsync(WorkflowAnswerRequest answer, CancellationToken token = default) => SerializedMutation(async () =>
    {
        ArgumentNullException.ThrowIfNull(answer);
        Identifier(answer.RequestId, nameof(answer.RequestId));
        if (answer.RunId != SelectedRun()) throw new InvalidOperationException("Answer belongs to a different run.");
        var arguments = FreezeJson(CerebralJson.Element(answer), WorkflowContract.MaximumStateBytes);
        var serialized = JsonSerializer.Serialize(arguments, Json);
        var identicalRetry = lastAnswerRequestId == answer.RequestId && lastAnswerJson == serialized;
        if (lastAnswerRequestId == answer.RequestId && !identicalRetry)
            throw new InvalidOperationException("An answer retry must keep the same request ID and exact field values.");
        if (!identicalRetry)
        {
            var questions = snapshot.Run?.Questions;
            if (questions is null || questions.Id != answer.QuestionSetId || questions.Revision != answer.ExpectedQuestionRevision)
                throw new InvalidOperationException("This answer form is stale. Refresh the current questions before answering.");
            ValidateAnswers(answer, questions);
            if (snapshot.MutationUncertain) throw new InvalidOperationException("Refresh the uncertain operation before answering a different form.");
        }
        lastAnswerRequestId = answer.RequestId; lastAnswerJson = serialized;
        await Mutate("jobs.workflow.answer", arguments, answer.RequestId, token).ConfigureAwait(false);
        // An identical answer retry returns its original receipt, possibly older than current work.
        Adopt(await ReadRun(SelectedRun(), token).ConfigureAwait(false));
        await LatestEvents(token).ConfigureAwait(false);
    }, token);

    /// <summary>
    /// Replays a fully specified answer after reconnect when the selected run no longer exposes that
    /// form. The service receipt is authoritative: an identical accepted request returns its saved
    /// response, while a new, changed, stale or foreign request is rejected without mutation.
    /// </summary>
    public Task<WorkflowConversationSnapshot> RetryAnswerAsync(WorkflowAnswerRequest answer, CancellationToken token = default) => SerializedMutation(async () =>
    {
        ArgumentNullException.ThrowIfNull(answer);
        Identifier(answer.RequestId, nameof(answer.RequestId));
        Identifier(answer.QuestionSetId, nameof(answer.QuestionSetId));
        if (answer.RunId != SelectedRun()) throw new InvalidOperationException("Answer belongs to a different run.");
        if (snapshot.Run?.Questions is not null)
            throw new InvalidOperationException("The selected run still has an active answer form. Inspect and answer that exact form instead.");
        if (snapshot.MutationUncertain && snapshot.LastMutationRequestId != answer.RequestId)
            throw new InvalidOperationException("Refresh the uncertain operation before retrying a different answer.");
        ValidateAnswerEnvelope(answer);
        var arguments = FreezeJson(CerebralJson.Element(answer), WorkflowContract.MaximumStateBytes);
        var serialized = JsonSerializer.Serialize(arguments, Json);
        if (lastAnswerRequestId == answer.RequestId && lastAnswerJson != serialized)
            throw new InvalidOperationException("An answer retry must keep the same request ID and exact field values.");
        lastAnswerRequestId = answer.RequestId; lastAnswerJson = serialized;
        await Mutate("jobs.workflow.answer", arguments, answer.RequestId, token).ConfigureAwait(false);
        // A durable answer receipt may contain an older run revision. Adopt() never rolls the
        // current projection backwards; query current state after the receipt is reconciled.
        Adopt(await ReadRun(SelectedRun(), token).ConfigureAwait(false));
        await LatestEvents(token).ConfigureAwait(false);
    }, token);

    public Task<WorkflowConversationSnapshot> CancelAsync(CancellationToken token = default)
    {
        var expectedRunId = SelectedRun();
        return SerializedMutation(async () =>
        {
            RequireSelectedRun(expectedRunId);
            await Mutate("jobs.workflow.cancel", CerebralJson.Element(new { id = expectedRunId }), expectedRunId, token).ConfigureAwait(false);
            await LatestEvents(token).ConfigureAwait(false);
        }, token);
    }

    public Task<WorkflowConversationSnapshot> ResumeAsync(long expectedRevision, CancellationToken token = default)
    {
        var expectedRunId = SelectedRun();
        return SerializedMutation(async () =>
        {
            RequireSelectedRun(expectedRunId);
            var run = snapshot.Run;
            if (snapshot.MutationUncertain || run is null || run.Revision != expectedRevision || run.Status != WorkflowStatus.Waiting ||
                run.CancelRequested || run.Questions is not null || run.EffectState is WorkflowEffectState.Unknown or WorkflowEffectState.Dispatched)
                throw new InvalidOperationException("Refresh this run; only its current resumable revision can continue.");
            await Mutate("jobs.workflow.resume", CerebralJson.Element(new { id = expectedRunId, expectedRevision }), expectedRunId, token).ConfigureAwait(false);
            await LatestEvents(token).ConfigureAwait(false);
        }, token);
    }

    public Task<WorkflowConversationSnapshot> ReconcileAsync(CancellationToken token = default)
    {
        var expectedRunId = SelectedRun();
        return SerializedMutation(async () =>
        {
            RequireSelectedRun(expectedRunId);
            if (snapshot.MutationUncertain || snapshot.Run?.EffectState != WorkflowEffectState.Unknown)
                throw new InvalidOperationException("Refresh the run before inspecting its uncertain effect.");
            await Mutate("jobs.workflow.reconcile", CerebralJson.Element(new { id = expectedRunId }), expectedRunId, token).ConfigureAwait(false);
            await LatestEvents(token).ConfigureAwait(false);
        }, token);
    }

    /// <summary>Reject one exact never-dispatched intent and save the owner's correction.</summary>
    public Task<WorkflowConversationSnapshot> RejectPreparedAsync(WorkflowPreparedRejectRequest request,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expectedRunId = SelectedRun();
        return SerializedMutation(async () =>
        {
            RequireSelectedRun(expectedRunId);
            if (request.RunId != expectedRunId)
                throw new InvalidOperationException("Prepared rejection belongs to a different selected run.");
            await Mutate("jobs.workflow.prepared.reject", CerebralJson.Element(request), request.RequestId, token)
                .ConfigureAwait(false);
            await LatestEvents(token).ConfigureAwait(false);
        }, token);
    }

    /// <summary>Permit a later, separately reviewed exact prepared intent for one dispatch.</summary>
    public Task<WorkflowConversationSnapshot> PermitPreparedOnceAsync(WorkflowPreparedPermitRequest request,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expectedRunId = SelectedRun();
        return SerializedMutation(async () =>
        {
            RequireSelectedRun(expectedRunId);
            if (request.RunId != expectedRunId)
                throw new InvalidOperationException("Prepared permit belongs to a different selected run.");
            await Mutate("jobs.workflow.prepared.permit-once", CerebralJson.Element(request), request.RequestId, token)
                .ConfigureAwait(false);
            await LatestEvents(token).ConfigureAwait(false);
        }, token);
    }

    private async Task<WorkflowConversationSnapshot> Serialized(Func<Task> action, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { token.ThrowIfCancellationRequested(); await action().ConfigureAwait(false); return Snapshot; }
        finally { gate.Release(); }
    }

    private void RequireSelectedRun(string expectedRunId)
    {
        if (snapshot.RunId != expectedRunId)
            throw new InvalidOperationException("The selected workflow changed while this action waited. Refresh and use its exact run ID.");
    }

    private async Task<WorkflowConversationSnapshot> SerializedInspection(Func<Task> action, CancellationToken token)
    {
        Interlocked.Increment(ref pendingExactInspections);
        try { return await Serialized(action, token).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref pendingExactInspections); }
    }

    private async Task<WorkflowConversationSnapshot> SerializedMutation(Func<Task> action, CancellationToken token)
    {
        // Mark intent before waiting for the gate so a head refresh cannot redirect a queued action.
        Interlocked.Increment(ref pendingMutations);
        try { return await Serialized(action, token).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref pendingMutations); }
    }

    private async Task Mutate(string operation, JsonElement arguments, string requestId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Publish(snapshot with { LastMutationRequestId = requestId });
        try
        {
            var result = await Read<WorkflowRun>(operation, arguments, MaximumRunBytes, token).ConfigureAwait(false);
            ValidateRun(result, SelectedRun());
            Adopt(result);
        }
        catch (WorkflowConversationCommandException) { throw; }
        catch
        {
            // Once transport is called it may have committed. Keep the exact ID and require a query;
            // even caller cancellation is not evidence that the service rejected the mutation.
            Publish(snapshot with { MutationUncertain = true });
            throw;
        }
    }

    private async Task<WorkflowRun> ReadRun(string id, CancellationToken token)
    {
        var run = await Read<WorkflowRun>("jobs.workflow.get", CerebralJson.Element(new { id }), MaximumRunBytes, token).ConfigureAwait(false);
        ValidateRun(run, id); return run;
    }

    private async Task<T> Read<T>(string operation, JsonElement arguments, int maximumBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // ProfileId is deliberately absent. A desktop owner answer must not impersonate an agent.
        var result = await command(new(operation, arguments), token).ConfigureAwait(false);
        if (!result.Success) throw new WorkflowConversationCommandException(operation, result.Error ?? "The service rejected this operation.");
        var data = FreezeJson(result.Data, maximumBytes);
        try { return data.Deserialize<T>(Json) ?? throw new InvalidDataException("The service returned empty workflow data."); }
        catch (JsonException error) { throw new InvalidDataException("The service returned invalid workflow data.", error); }
    }

    private void ValidateRun(WorkflowRun run, string expectedId)
    {
        if (run.Id != expectedId || run.Submission is null || run.Submission.RequestId != expectedId ||
            run.Submission.ConversationId != snapshot.ConversationId || run.Submission.ConversationSequence is < 0 or long.MaxValue ||
            run.Revision < 1 || !Enum.IsDefined(run.Status) ||
            run.Effect is { } effect && !Enum.IsDefined(effect.State))
            throw new InvalidDataException("The service returned a foreign or invalid workflow identity.");
        WorkflowAcceptedContent.Validate(run.Submission.AcceptedContent);
        if (lastSubmission is { } submitted && submitted.RequestId == expectedId &&
            (submitted.ProfileId != run.Submission.ProfileId || submitted.DefinitionId != run.Submission.DefinitionId ||
             submitted.DefinitionVersion != run.Submission.DefinitionVersion || submitted.UserText != run.Submission.UserText ||
              submitted.OwnerProjectId is { } ownerProjectId && ownerProjectId != run.Submission.ProjectId ||
             submitted.LocalGeneration != run.Submission.LocalGeneration ||
             submitted.AssignmentText != run.Submission.AssignmentText || submitted.StepLimit != run.Submission.StepLimit ||
             JsonSerializer.Serialize(submitted.Input, Json) != JsonSerializer.Serialize(run.Submission.Input, Json) ||
             !JsonElement.DeepEquals(CerebralJson.Element(submitted.AcceptedContent), CerebralJson.Element(run.Submission.AcceptedContent))))
            throw new InvalidDataException("The saved run does not match this exact submitted request.");
    }

    private void Adopt(WorkflowRun run)
    {
        if (snapshot.Run is { } current && current.Id == run.Id && current.Revision > run.Revision) return;
        var questions = run.Status == WorkflowStatus.Waiting && !run.CancelRequested && run.Questions is { } set ? FreezeQuestions(set) : null;
        // A later hold (for example a changed package) can retain an already answered set. It is
        // history, not a form to reopen. The store starts question sets at revision one and
        // advances their revision when answers are committed, including an explicit optional skip.
        if (questions is { } answered && (answered.Answers.Count > 0 || answered.Revision > 1) &&
            answered.Fields.Where(x => x.Required).All(x => answered.Answers.ContainsKey(x.Id)))
            questions = null;
        var view = new WorkflowConversationRun(run.Id, run.Submission.ConversationId, run.Submission.ProfileId, run.Submission.UserText, run.Submission.DefinitionId,
            run.Submission.DefinitionVersion, run.Revision, run.Status, questions, run.Result?.Clone(), run.WaitingReason,
            run.Error, run.CancelRequested, run.Effect?.Id, run.Effect?.State, run.Steps)
        {
            ActiveMilliseconds = run.ActiveMilliseconds,
            ActivityTotals = run.ActivityTotals is { } totals && totals.IsValidFor(run.Submission.ConversationId, run.Id, run.Revision, run.Submission.ConversationSequence) ? totals : null,
            ReservedExecutionMilliseconds = run.Effect?.ReservedExecutionMilliseconds ?? 0,
            EffectSha256 = run.Effect is { } effect ? WorkflowPreparedEffectIdentity.Sha256(effect) : null
        };
        acceptedSubmission = run.Submission;
        Publish(snapshot with { RunId = run.Id, Run = view, MutationUncertain = false });
    }

    private async Task RetainOriginalMessage(CancellationToken token)
    {
        if (snapshot.OriginalMessage is not null) return;
        var accepted = acceptedSubmission ?? throw new InvalidDataException("The selected run has no accepted submission.");
        var pinned = snapshot.SnapshotSequence;
        var firstSequence = accepted.ConversationSequence + 1;
        if (firstSequence > pinned)
            throw new InvalidDataException("The selected event snapshot precedes its accepted submission.");
        var original = snapshot.Events.FirstOrDefault(item => item.Sequence == firstSequence);
        if (original is null)
        {
            // The atomic admission boundary identifies exactly one canonical event. A long tool run
            // must not require an unbounded scan or a fabricated card built from the submission DTO.
            var page = await Read<ForwardEvents>("jobs.workflow.events", CerebralJson.Element(new
            { id = SelectedRun(), afterSequence = accepted.ConversationSequence, snapshotSequence = pinned, limit = 1 }),
                MaximumPageBytes + 4096, token).ConfigureAwait(false);
            if (page.SnapshotSequence != pinned || page.Events is null || page.Events.Count != 1 ||
                page.NextAfterSequence is { } continuation && continuation != firstSequence)
                throw new InvalidDataException("The original workflow message changed snapshot or exceeded its exact event bound.");
            original = page.Events[0];
        }
        original.Validate();
        var caller = accepted.Caller ?? new EventActor(EventActorKind.User);
        ContentPart[] expectedContent = [new TextContentPart(accepted.UserText), .. accepted.AcceptedContent];
        if (original.ConversationId != snapshot.ConversationId || original.TurnId != SelectedRun() ||
            original.Sequence != firstSequence || original.Type != CerebralEventTypes.Message ||
            original.Actor != caller ||
            !(EventProjection.ShowInTranscript(original) ||
                original.Projection.Human == HumanVisibility.Activity &&
                accepted.Caller is { Kind: EventActorKind.Service, Id: "cerebral.plugin-tests" } &&
                accepted.DefinitionId == ConversationControlClient.ChatDefinitionId &&
                accepted.RequestId.StartsWith("plugin-tests-", StringComparison.Ordinal) &&
                Guid.TryParseExact(accepted.RequestId["plugin-tests-".Length..], "N", out _)) ||
            !JsonElement.DeepEquals(CerebralJson.Element(original.Content), CerebralJson.Element(expectedContent)))
            throw new InvalidDataException("The service returned a foreign or mismatched original workflow message.");
        Publish(snapshot with { OriginalMessage = original with
        { Content = Array.AsReadOnly(original.Content.ToArray()), Data = original.Data?.Clone() } });
    }

    private async Task LatestEvents(CancellationToken token)
    {
        // Receipt totals and the displayed recent page must describe the same boundary.
        // A later event is admitted on the next normal refresh, never mixed into older totals.
        var boundary = snapshot.Run?.ActivityTotals?.AsOfSequence;
        var arguments = boundary is { } sequence
            ? CerebralJson.Element(new { id = SelectedRun(), limit = pageSize, snapshotSequence = sequence })
            : CerebralJson.Element(new { id = SelectedRun(), limit = pageSize });
        var page = await Read<PreviousEvents>("jobs.workflow.events.previous", arguments,
            MaximumPageBytes + 4096, token).ConfigureAwait(false);
        if (boundary is { } expected && page.SnapshotSequence != expected)
            throw new InvalidDataException("The activity page does not match its receipt-total snapshot.");
        AdoptPage(page.Events, page.SnapshotSequence, page.NextBeforeSequence, previous: true, null, boundary);
    }

    private void AdoptPage(IReadOnlyList<CerebralEvent> events, long sequence, long? continuation, bool previous,
        long? cursor, long? expectedSnapshot)
    {
        if (events is null || events.Count > pageSize || sequence < 0 || expectedSnapshot is { } pinned && sequence != pinned)
            throw new InvalidDataException("The event page changed its snapshot or exceeded the requested bounds.");
        var frozen = new List<CerebralEvent>(events.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long last = 0; var bytes = 0;
        foreach (var item in events)
        {
            item.Validate();
            bytes += JsonSerializer.SerializeToUtf8Bytes(item, Json).Length;
            if (item.ConversationId != snapshot.ConversationId || item.Sequence <= last || item.Sequence > sequence ||
                !seen.Add(item.Id) || bytes > MaximumPageBytes ||
                cursor is { } position && (previous ? item.Sequence >= position : item.Sequence <= position))
                throw new InvalidDataException("The service returned a foreign, unordered or oversized event page.");
            last = item.Sequence;
            frozen.Add(item with { Content = Array.AsReadOnly(item.Content.ToArray()), Data = item.Data?.Clone() });
        }
        if (continuation is not null && (events.Count == 0 || continuation != (previous ? events[0].Sequence : events[^1].Sequence)))
            throw new InvalidDataException("The event continuation does not match the returned page.");
        Publish(snapshot with
        {
            Events = frozen.AsReadOnly(), SnapshotSequence = sequence,
            HasOlder = events.Count > 0 && (previous ? continuation is not null : events[0].Sequence > 1),
            HasNewer = events.Count > 0 && (previous ? events[^1].Sequence < sequence : continuation is not null)
        });
    }

    /// <summary>Validate and isolate a pending UI submission, including mutable attachment metadata.</summary>
    public static WorkflowConversationSubmission FreezeSubmission(WorkflowConversationSubmission value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Identifier(value.RequestId, nameof(value.RequestId)); Identifier(value.ProfileId, nameof(value.ProfileId));
        if (value.OwnerProjectId is not null) Identifier(value.OwnerProjectId, nameof(value.OwnerProjectId));
        if (string.IsNullOrWhiteSpace(value.DefinitionId) || value.DefinitionId.Length > 256 || value.DefinitionId.Any(char.IsWhiteSpace) ||
            string.IsNullOrWhiteSpace(value.DefinitionVersion) || value.DefinitionVersion.Length > 100)
            throw new ArgumentException("A discovered workflow definition and exact version are required.");
        var content = WorkflowAcceptedContent.Freeze(value.AcceptedContent);
        if (value.UserText is null || content.Count == 0 && string.IsNullOrWhiteSpace(value.UserText) || value.UserText.Length > 128 * 1024 || value.UserText.Contains('\0') ||
            value.AssignmentText?.Length > 128 * 1024 || value.AssignmentText?.Contains('\0') == true || value.StepLimit is < 1 or > WorkflowContract.MaximumSteps)
            throw new ArgumentException("Workflow text or step limit exceeds the supported bounds.");
        var input = FreezeJson(value.Input, WorkflowContract.MaximumStateBytes);
        JsonElement? ownerVerification = null;
        if (value.OwnerVerification is { } recipe)
        {
            ownerVerification = FreezeJson(recipe, 16 * 1024);
            if (ownerVerification.Value.ValueKind != JsonValueKind.Object ||
                !ownerVerification.Value.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var edition) || edition != 2)
                throw new ArgumentException("Owner verification requires the edition-two recipe contract.");
        }
        value.LocalGeneration?.Validate();
        var localGeneration = value.LocalGeneration is { } choice
            ? new WorkflowLocalGenerationChoice(choice.SchemaVersion, choice.Route,
                choice.ContextTokens, choice.VisibleOutputTokens) : null;
        var frozen = value with { Input = input, AcceptedContent = content,
            LocalGeneration = localGeneration, OwnerVerification = ownerVerification };
        _ = FreezeJson(CerebralJson.Element(frozen), MaximumRunBytes);
        return frozen;
    }

    private static WorkflowQuestionSet FreezeQuestions(WorkflowQuestionSet set)
    {
        if (string.IsNullOrWhiteSpace(set.Id) || set.Revision < 1 || set.Fields is null || set.Fields.Count is < 1 or > 16 || set.Answers is null)
            throw new InvalidDataException("The service returned an invalid question set.");
        _ = FreezeJson(CerebralJson.Element(set), WorkflowContract.MaximumStateBytes);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in set.Fields)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Id) || field.Id.Length > 128 || !ids.Add(field.Id) ||
                string.IsNullOrWhiteSpace(field.Label) || field.Label.Length > 2048 ||
                field.Multiple && field.Choices is null ||
                field.Description is { } description && (string.IsNullOrWhiteSpace(description) || description.Length > 8192 || description.Contains('\0')) ||
                field.Choices is { } choices && (choices.Count is < 1 or > 32 || choices.Distinct(StringComparer.Ordinal).Count() != choices.Count ||
                    choices.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 1024)))
                throw new InvalidDataException("The service returned invalid or duplicate question fields.");
        }
        if (set.Answers.Count > 16 || set.Answers.Any(x => !ids.Contains(x.Key) || !WorkflowQuestionAnswers.IsValid(set.Fields.Single(field => field.Id == x.Key), x.Value)))
            throw new InvalidDataException("The service returned invalid saved question answers.");
        return set with
        {
            Fields = Array.AsReadOnly(set.Fields.Select(field => field with
            { Choices = field.Choices is null ? null : Array.AsReadOnly(field.Choices.ToArray()) }).ToArray()),
            Answers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(set.Answers, StringComparer.Ordinal))
        };
    }

    private static void ValidateAnswers(WorkflowAnswerRequest answer, WorkflowQuestionSet questions)
    {
        ValidateAnswerEnvelope(answer);
        if (answer.Answers.Count == 0 && questions.Fields.Any(x => x.Required && !questions.Answers.ContainsKey(x.Id)))
            throw new ArgumentException("Answer required questions before submitting an empty optional response.");
        foreach (var (id, text) in answer.Answers)
        {
            var field = questions.Fields.SingleOrDefault(x => x.Id == id);
            if (field is null ||
                !WorkflowQuestionAnswers.IsValid(field, text))
                throw new ArgumentException("An answer is empty, oversized, unknown or outside the offered choices.");
        }
    }

    private static void ValidateAnswerEnvelope(WorkflowAnswerRequest answer)
    {
        if (answer.ExpectedQuestionRevision < 1 || answer.Answers is null || answer.Answers.Count > 16)
            throw new ArgumentException("Supply a positive question revision and at most 16 field answers.");
        foreach (var (id, text) in answer.Answers)
            if (string.IsNullOrEmpty(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')) ||
                string.IsNullOrWhiteSpace(text) || text.Length > 8192 || text.Contains('\0'))
                throw new ArgumentException("An answer is empty, oversized, or names an invalid field.");
    }

    private string SelectedRun() => snapshot.RunId ?? throw new InvalidOperationException("Open or submit a workflow first.");
    private void Publish(WorkflowConversationSnapshot value) => Volatile.Write(ref snapshot, value);
    private static void Identifier(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 100 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("Use a stable 1â€“100 character ASCII identifier.", name);
    }
    private static JsonElement FreezeJson(JsonElement value, int maximumBytes)
    {
        if (value.ValueKind == JsonValueKind.Undefined || JsonSerializer.SerializeToUtf8Bytes(value, Json).Length > maximumBytes)
            throw new InvalidDataException("Workflow data is undefined or exceeds the supported byte bound.");
        return value.Clone();
    }

    // Wire page DTOs stay transport-only; Client does not reference Engine or its SQLite store.
    private sealed record PreviousEvents(IReadOnlyList<CerebralEvent> Events, long SnapshotSequence, long? NextBeforeSequence);
    private sealed record ForwardEvents(IReadOnlyList<CerebralEvent> Events, long SnapshotSequence, long? NextAfterSequence);
}
