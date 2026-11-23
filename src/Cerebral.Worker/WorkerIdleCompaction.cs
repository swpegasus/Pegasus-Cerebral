using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker;

/// <summary>Worker-private preference carried by the existing extensible profile settings.</summary>
internal sealed record IdleCompactionPreference(int SchemaVersion = 1, bool Enabled = false, int TriggerPercent = 70)
{
    internal const string Key = "cerebral.idle-compaction";
    internal static IdleCompactionPreference Read(AgentProfile profile)
    {
        if (!profile.PluginSettings.TryGetValue(Key, out var value)) return new();
        var result = WorkerWorkflows.Read<IdleCompactionPreference>(value);
        if (result.SchemaVersion != 1 || result.TriggerPercent is < 50 or > 95)
            throw new ArgumentException("Idle compaction requires schemaVersion=1 and triggerPercent between 50 and 95.");
        return result;
    }
    internal static bool EnabledFor(AgentProfile profile)
    {
        try { return Read(profile).Enabled; }
        catch (Exception error) when (error is JsonException or ArgumentException) { return false; }
    }
}

public sealed partial class WorkerWorkflows
{
    internal const string IdleCompactionExecutionKey = "maintenance:idle-compaction";
    private readonly object idleGate = new();
    private readonly Dictionary<string, WorkflowRun> idlePending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReadyIdleCheckpoint> idleReady = new(StringComparer.Ordinal);
    private WorkerWorkflowEffects? idleEffects;
    private CancellationTokenSource? idleCancellation;
    private WorkflowRun? idleOwner;
    private int idleAdmissions;
    private bool idleBlocked;
    private sealed record ReadyIdleCheckpoint(ConversationIdleCheckpoint Candidate, long EngineEpoch,
        IdleCompactionPreference Preference);

    private sealed class IdleAdmission(WorkerWorkflows owner, ReadyIdleCheckpoint? ready) : IDisposable
    {
        internal ReadyIdleCheckpoint? Ready { get; set; } = ready;
        public void Dispose() { lock (owner.idleGate) owner.idleAdmissions--; }
    }

    private IdleAdmission BeginIdleAdmission(string conversationId)
    {
        CancellationTokenSource? cancellation;
        IdleAdmission result;
        lock (idleGate)
        {
            idleAdmissions++;
            idlePending.Remove(conversationId);
            idleReady.Remove(conversationId, out var ready);
            cancellation = idleCancellation;
            result = new(this, ready);
        }
        CancelIdle(cancellation);
        return result;
    }

    internal IDisposable BeginForegroundAdmission() => BeginIdleAdmission("");

    internal void InterruptIdleCompaction()
    {
        CancellationTokenSource? cancellation;
        lock (idleGate) cancellation = idleCancellation;
        CancelIdle(cancellation);
    }

    private static void CancelIdle(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void QueueIdleCompaction(WorkflowRun run)
    {
        if (idleEffects is null || run.Status != WorkflowStatus.Completed || run.Questions is not null ||
            run.Submission.DefinitionId != ConversationWorkflowModule.DefinitionId ||
            run.Submission.ParentRunId is not null || run.Submission.Caller?.Kind != EventActorKind.User ||
            legacy.Get<AgentProfile>("agent", run.Submission.ProfileId) is not { } profile ||
            !IdleCompactionPreference.EnabledFor(profile)) return;
        lock (idleGate)
        {
            if (idleBlocked || idleAdmissions != 0 ||
                idleReady.ContainsKey(run.Submission.ConversationId) || idleOwner?.Id == run.Id) return;
            // A later eligible completion replaces a disposable queued hint while foreground work
            // owns execution. Delayed publications cannot downgrade it to an earlier admission.
            var conversationId = run.Submission.ConversationId;
            if (idlePending.TryGetValue(conversationId, out var pending) &&
                (pending.Submission.ConversationSequence > run.Submission.ConversationSequence ||
                 pending.Submission.ConversationSequence == run.Submission.ConversationSequence &&
                 pending.Revision >= run.Revision)) return;
            // Bound new conversations independently of retained history; replacement uses the same slot.
            if (!idlePending.ContainsKey(conversationId) && idlePending.Count >= 16)
                idlePending.Remove(idlePending.Keys.First());
            idlePending[conversationId] = run;
        }
    }

    internal bool IdleCompactionUsesLocalModel
    {
        get
        {
            lock (idleGate) return idleOwner is { } run && IdleUsesLocalRoute(run);
        }
    }

    private static bool IdleUsesLocalRoute(WorkflowRun run)
    {
        var snapshot = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot);
        static bool Local(AgentProfile profile) => string.IsNullOrWhiteSpace(profile.ModelWorkerId) || profile.ModelWorkerId == "local";
        return Local(snapshot.Profile) || snapshot.Compactor is { } compactor && Local(compactor.Profile);
    }

    private bool IdleUsesModel(string modelId)
    {
        lock (idleGate) return idleOwner is { } run &&
            WorkflowModelInterest.UsesLocalModel(run.Submission.SettingsSnapshot, modelId);
    }

    /// <summary>The scheduler owns the execution slot through terminal provider evidence.</summary>
    internal CancellationTokenSource? TryClaimIdleCompaction(CancellationToken stop, bool localResourcesPaused = false)
    {
        if (Engine?.IsPaused == true) return null;
        lock (idleGate)
        {
            if (idleEffects is null || idleBlocked || idleAdmissions != 0 || idleCancellation is not null ||
                idlePending.Count == 0) return null;
            var next = idlePending.FirstOrDefault(item => !localResourcesPaused || !IdleUsesLocalRoute(item.Value));
            if (next.Value is null) return null;
            idlePending.Remove(next.Key);
            idleOwner = next.Value;
            idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
            idleCancellation.CancelAfter(TimeSpan.FromMinutes(10));
            return idleCancellation;
        }
    }

    /// <returns>False retains provider ownership and blocks maintenance because completion is unknown.</returns>
    internal async Task<bool> RunIdleCompactionAsync(CancellationTokenSource cancellation)
    {
        WorkflowRun? completed;
        lock (idleGate) completed = idleOwner;
        var confirmed = true;
        var elapsed = Stopwatch.StartNew();
        var state = "skipped";
        var reason = "maintenance-unavailable";
        WorkerWorkflowEffects.CompactData? measured = null;
        try
        {
            if (completed is null || idleEffects is null || tools is null) return true;
            var token = cancellation.Token;
            token.ThrowIfCancellationRequested();
            var live = Store.Get(completed.Id);
            var summary = Store.GetConversation(ConversationWorkflowModule.DefinitionId, completed.Submission.ConversationId);
            if (live.Status != WorkflowStatus.Completed || live.Revision != completed.Revision)
            { reason = "completed-run-changed"; return true; }
            if (summary?.LatestRunId != completed.Id)
            { reason = "latest-run-changed"; return true; }
            var profile = legacy.Get<AgentProfile>("agent", completed.Submission.ProfileId);
            if (profile is null) { reason = "profile-unavailable"; return true; }
            var preference = IdleCompactionPreference.Read(profile);
            if (!preference.Enabled) { reason = "preference-disabled"; return true; }
            var settings = await WorkerExecutionSnapshot.CaptureAsync(legacy, profile, configuration, tools,
                packages.Capture(completed.Submission.DefinitionId), token, requireCompactor: true,
                projectId: completed.Submission.ProjectId,
                toolReceiptConsumer: WorkerToolContextProjectionAdmission.FreezeConsumer(
                    Registry.GetDefinition(completed.Submission.DefinitionId,
                        completed.Submission.DefinitionVersion).Descriptor)).ConfigureAwait(false);
            var currentSnapshot = WorkerExecutionSnapshot.Read(settings);
            currentSnapshot = currentSnapshot with { SelectedToolOwners = CaptureSelectedToolOwners(currentSnapshot) };
            currentSnapshot = currentSnapshot with
            {
                PackageBindings = packages.Capture(completed.Submission.DefinitionId,
                    SelectedWorkflowToolOwners(currentSnapshot))
            };
            settings = WorkerExecutionSnapshot.Bounded(CaptureWorkflowPortableSettings(currentSnapshot, profile));
            if (SnapshotFingerprint(settings) != SnapshotFingerprint(completed.Submission.SettingsSnapshot))
            { reason = "accepted-settings-changed"; return true; }
            var epoch = Engine?.Status().Epoch ?? 0;
            var head = Journal.GetRevision(completed.Submission.ConversationId);
            var savedArguments = Store.ReadLastConversationGeneration(completed.Id, head);
            if (savedArguments is null) { reason = "generation-envelope-unavailable"; return true; }
            var args = Read<WorkerWorkflowEffects.GenerateArguments>(savedArguments.Value);
            var baseline = Store.GetConversationCheckpoint(completed.Submission.ConversationId, completed.Submission.ConversationSequence);
            var window = idleEffects.ReadIdleWindow(completed, head, baseline, token);
            if (window is null) { reason = "bounded-history-unavailable"; return true; }
            if (ContextValidation.GetCoveredManifest(window).Count == 0)
            { reason = "older-history-empty"; return true; }
            // Keep the saved stable policy and dynamic required anchors as separate messages.
            args = args with { Context = window };
            var effectId = "idle-" + Guid.NewGuid().ToString("N");
            var outcome = await idleEffects.CompactAsync(new(effectId, completed.Submission, CerebralJson.Element(args))
                { RunId = completed.Id }, token, preference.TriggerPercent).ConfigureAwait(false);
            confirmed = outcome.State != WorkflowEffectState.Unknown;
            if (!confirmed)
            {
                state = "blocked"; reason = "provider-completion-unknown";
                lock (idleGate) idleBlocked = true;
                events.Publish("error", completed.Id, new { message =
                    "Idle compaction did not return confirmed provider completion. Its provider capacity remains quarantined and further maintenance is blocked; inspect the model cancellation diagnostic and explicitly stop the affected provider before recovery." });
                return false;
            }
            reason = outcome.State == WorkflowEffectState.Cancelled && !token.IsCancellationRequested ? "below-threshold" : "provider-" + outcome.State;
            if (outcome.State != WorkflowEffectState.Succeeded) return true;
            if (outcome.Data is not { } data) { reason = "compaction-receipt-unavailable"; return true; }
            if (token.IsCancellationRequested) { state = "discarded"; reason = "foreground-or-stop"; return true; }
            if (Journal.GetRevision(completed.Submission.ConversationId) != head)
            { reason = "conversation-head-changed"; return true; }
            if (Engine?.Status().Epoch != (Engine is null ? null : epoch))
            { reason = "engine-epoch-changed"; return true; }
            var compact = data.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options)!;
            if (!WorkerWorkflowEffects.IdleCheckpointFitsHistoryPage(window, compact.Checkpoint))
            { reason = "history-page-overflow"; return true; }
            measured = compact;
            var candidate = new ConversationIdleCheckpoint(completed.Id, completed.Revision, head,
                completed.Submission.ConversationSequence, SnapshotFingerprint(settings), window,
                compact.Checkpoint, baseline, data.Clone());
            lock (idleGate)
            {
                if (idleAdmissions == 0 && !token.IsCancellationRequested && !idleBlocked)
                {
                    if (idleReady.Count >= 16) idleReady.Remove(idleReady.Keys.First());
                    idleReady[completed.Submission.ConversationId] = new(candidate, epoch, preference);
                    state = "prepared"; reason = "measured-reduction";
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { state = "discarded"; reason = "foreground-or-stop"; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            state = "discarded"; reason = error.GetType().Name;
            events.Publish("error", completed?.Id, new { message = "Idle compaction was discarded: " + error.GetType().Name });
        }
        finally
        {
            lock (idleGate)
            {
                if (confirmed) { idleCancellation = null; idleOwner = null; }
            }
            if (confirmed) cancellation.Dispose();
            events.Publish("context.idle-compaction", completed?.Id, new
            {
                state, reason, elapsedMs = elapsed.ElapsedMilliseconds,
                conversationId = completed?.Submission.ConversationId,
                sourceRevision = completed?.Submission.ConversationSequence,
                beforeInputTokens = measured?.Before.InputTokens, afterInputTokens = measured?.After.InputTokens,
                effectiveContext = measured?.Before.EffectiveContext, providerCompletionConfirmed = confirmed
            });
        }
        return confirmed;
    }

    internal void DiscardIdleCompactionState()
    {
        lock (idleGate)
        {
            idlePending.Clear(); idleReady.Clear();
            idleCancellation?.Dispose(); idleCancellation = null; idleOwner = null;
        }
    }

    private static string SnapshotFingerprint(JsonElement settings) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(WorkerExecutionSnapshot.Read(settings), CerebralJson.Options)));

    private WorkflowRun AdmitWithIdle(Func<WorkflowRun> create, IdleAdmission? admission)
    {
        if (admission?.Ready is null || Engine is null) return create();
        try
        {
            // Ready-checkpoint adoption reads the Worker store inside create(). Take its gate first,
            // but let DurableCreate own its complete transaction while the engine epoch stays fenced.
            // An ambient store transaction could defer that COMMIT beyond Dispatch, so refuse it.
            return legacy.WithTransactionFreeGate(() => Engine.Dispatch(() =>
            {
                if (admission.Ready?.EngineEpoch != Engine.Status().Epoch) admission.Ready = null;
                return create();
            }));
        }
        catch (Cerebral.Engine.Activation.EnginePausedException)
        { admission.Ready = null; return create(); }
    }

    private TResult SubmitWithIdleCore<TResult>(WorkflowSubmission submission, IdleAdmission? admission,
        Func<WorkflowSubmission, TResult> direct,
        Func<WorkflowSubmission, ConversationIdleCheckpoint, string, TResult> withCheckpoint)
    {
        var ready = admission?.Ready;
        if (ready is null || legacy.Get<AgentProfile>("agent", submission.ProfileId) is not { } profile)
            return direct(submission);
        try
        {
            if (IdleCompactionPreference.Read(profile) != ready.Preference) return direct(submission);
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return direct(submission); }
        return withCheckpoint(submission, ready.Candidate, SnapshotFingerprint(submission.SettingsSnapshot));
    }

    private WorkflowRun SubmitWithIdle(WorkflowSubmission submission, IdleAdmission? admission) =>
        SubmitWithIdleCore(submission, admission, value => Store.Submit(value),
            (value, checkpoint, fingerprint) => Store.Submit(value, checkpoint, fingerprint));

    private WorkflowSubmissionCommitResult SubmitWithIdleAndCommittedRootUserInput(
        WorkflowSubmission submission, IdleAdmission? admission) =>
        SubmitWithIdleCore(submission, admission,
            value => Store.SubmitWithCommittedRootUserInput(value),
            (value, checkpoint, fingerprint) =>
                Store.SubmitWithCommittedRootUserInput(value, checkpoint, fingerprint));
}

public sealed partial class WorkerWorkflowEffects
{
    /// <summary>Fully cover a bounded older gap. A partial page must never become a skip-through cutoff.</summary>
    internal ContextWindow? ReadIdleWindow(WorkflowRun completed, long head,
        ConversationContextCheckpoint? baseline, CancellationToken token)
    {
        var journal = new EventJournal(Path.Combine(store.DirectoryPath, "engine.db"));
        var workflowStore = new WorkflowStore(journal);
        var cutoff = completed.Submission.ConversationSequence;
        var goal = workflowStore.ReadConversationGoal(completed.Submission.DefinitionId, completed.Submission.ConversationId);
        if (goal.Revision != head) return null;
        var original = journal.ReadExact(completed.Submission.ConversationId, goal.Original.EventId, head, token);
        var sources = new List<ContextContribution>();
        long? before = null;
        var coveredThrough = baseline?.SourceRevision ?? 0;
        var reached = false;
        for (var pageIndex = 0; pageIndex < 8; pageIndex++)
        {
            var page = journal.ReadPreviousPage(completed.Submission.ConversationId, before, 24, cutoff, token);
            foreach (var item in page.Events.Reverse())
            {
                if (item.Sequence <= coveredThrough) { reached = true; break; }
                if (!EventProjection.IncludeInModel(item) || item.Id == original.Id) continue;
                sources.Insert(0, ProjectConversationSource(item, journal, null, token));
            }
            before = page.NextBeforeSequence;
            if (before is null || before <= coveredThrough + 1) { reached = true; break; }
            if (sources.Count >= ContextValidation.MaximumSourceCount - 8) return null;
        }
        if (!reached) return null;
        if (EventProjection.IncludeInModel(original))
            sources.Insert(0, ExactIdleAnchor(original, "idle-goal-"));
        // Preserve the complete latest exchange and current-turn evidence without summarizing it.
        var recent = journal.ReadPreviousPage(completed.Submission.ConversationId, null, 128, head, token);
        if (recent.NextBeforeSequence is { } next && next > cutoff + 1) return null;
        foreach (var item in recent.Events.Where(item => item.Sequence > cutoff && EventProjection.IncludeInModel(item)))
        {
            // The saved generation envelope already contains required host policy/anchor snapshots.
            if (item.Id == original.Id || InternalContextOperation(item) is not null) continue;
            sources.Add(ExactIdleAnchor(item, "idle-current-"));
        }
        var window = new ContextWindow(head, baseline?.Checkpoint.ExclusionRevision ?? 0, sources,
            baseline?.Checkpoint);
        if (sources.Count + (baseline?.Checkpoint.CoveredSources.Count ?? 0) > ContextValidation.MaximumSourceCount ||
            JsonSerializer.SerializeToUtf8Bytes(window, ConversationJson).Length > WorkflowContract.MaximumStateBytes) return null;
        ContextValidation.ValidateWindow(window);
        // A candidate must remain readable through the existing bounded history command. Exact
        // required anchors are never clipped merely to create an optional maintenance candidate.
        if (!IdleCheckpointFitsHistoryPage(window, baseline?.Checkpoint)) return null;
        return window;
    }

    internal static bool IdleCheckpointFitsHistoryPage(ContextWindow window, ContextCheckpoint? checkpoint)
    {
        var required = window with
        {
            Contributions = window.Contributions.Where(source => source.Requirement == ContextRequirement.Required).ToArray(),
            Checkpoint = checkpoint
        };
        return Size(Element(new HistoryData(required, false, null, window.Revision))) <= MaximumConversationEffectBytes;
    }

    private static ContextContribution ExactIdleAnchor(CerebralEvent item, string prefix) =>
        new(prefix + item.Id, ModuleId, "conversation", item.Sequence, ContextRequirement.Required,
            [new(item.Actor.Kind == EventActorKind.Agent ? "assistant" : "user", item.Content.Select(part =>
                part is StructuredContentPart structured ? (ContentPart)new TextContentPart(
                    structured.Schema + ": " + structured.Value.GetRawText()) : part).ToArray())],
            item.Id, Complete: true, BeforeCurrentInput: true);
}
