using Cerebral.Core;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private async Task<object> SwitchDefaultModelCommandAsync(System.Text.Json.JsonElement args, bool workerAlias, CancellationToken ct)
    {
        var hasRetainedWaitingFlag = args.TryGetProperty("retainWaitingBindings", out var retainedWaitingFlag);
        var hasAcceptedFlag = args.TryGetProperty("retainAcceptedBindings", out var acceptedFlag);
        string[] fields = workerAlias ? ["modelId", "workerId"] : args.TryGetProperty("modelWorkerId", out _) ? ["modelId", "modelWorkerId"] : ["modelId"];
        if (hasRetainedWaitingFlag) fields = [.. fields, "retainWaitingBindings"];
        if (hasAcceptedFlag) fields = [.. fields, "retainAcceptedBindings"];
        RemoteWorkerManagement.RequireFields(args, fields);
        if (hasRetainedWaitingFlag && retainedWaitingFlag.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            throw new ArgumentException("retainWaitingBindings must be a Boolean owner decision.");
        if (hasAcceptedFlag && acceptedFlag.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            throw new ArgumentException("retainAcceptedBindings must be a Boolean owner decision.");
        var retainWaiting = hasRetainedWaitingFlag && retainedWaitingFlag.GetBoolean();
        var retainAccepted = hasAcceptedFlag && acceptedFlag.GetBoolean();
        if (retainWaiting && retainAccepted) throw new ArgumentException("Choose one retained-binding mode.");
        var modelId = Text(args, "modelId");
        var workerId = workerAlias ? Text(args, "workerId") :
            args.TryGetProperty("modelWorkerId", out _) ? Text(args, "modelWorkerId") : "local";
        if (!retainAccepted) return await SwitchDefaultModelAsync(modelId, workerId, retainWaiting, ct);
        // Only future admissions change. Keep the owner pause stable through preparation and commit;
        // existing accepted workflows continue to own their immutable model/context/receipt snapshots.
        return await WithOwnerEngineMutationAsync(token =>
        {
            var status = workflows?.Engine?.Status() ?? throw new InvalidOperationException("Retained admissions require a replaceable engine.");
            if (!status.Paused) throw new InvalidOperationException("Pause the engine before routing new admissions while retaining accepted work.");
            return SwitchDefaultModelAsync(modelId, workerId, false, token, status.Epoch);
        }, ct);
    }

    private Task<object> SwitchDefaultModelAsync(string modelId, string modelWorkerId, bool retainWaitingBindings, CancellationToken ct, long? acceptedBindingsEpoch = null)
    {
        Configuration.ValidateId(modelId); Configuration.ValidateId(modelWorkerId);
        if (retainWaitingBindings && modelWorkerId == "local")
            throw new InvalidOperationException("Retaining waiting local bindings is available only while switching new work to a verified remote model and keeping local resources paused.");
        return modelWorkerId == "local" ? SwitchLocalDefaultModelAsync(modelId, ct, acceptedBindingsEpoch) :
            SwitchRemoteDefaultModelAsync(modelId, modelWorkerId, retainWaitingBindings, ct, acceptedBindingsEpoch);
    }

    private async Task<object> SwitchLocalDefaultModelAsync(string modelId, CancellationToken ct, long? acceptedBindingsEpoch = null)
    {
        await modelFiles.WaitAsync(ct);
        try
        {
            Configuration.ValidateId(modelId);
            var candidate = store.Get<ModelRecord>("model", modelId) ?? throw new ArgumentException("Model not found.");
            if (candidate.MetadataSource == "fallback" || string.IsNullOrWhiteSpace(candidate.Architecture))
                await RefreshModelMetadataAsync(candidate, ct);
            var plan = store.Locked(() => BuildDefaultModelPlan(modelId, acceptedBindingsEpoch: acceptedBindingsEpoch));
            var oldWasLoaded = plan.OldModel is not null && runtime.IsLoaded(plan.OldModel.Id);
            var targetStartedBySwitch = false;
            var unloadedOld = false;
            var assignmentsCommitted = false;

            try
            {
                if (plan.OldModel is not null && plan.OldModel.Id != plan.Target.Id && oldWasLoaded && !HasModelAssignments(plan.OldModel.Id) && !HasPlacementAssignments("local", plan.OldModel.Id) && !plan.UnchangedProfiles.Any(x => x.ModelWorkerId == "local" && x.ModelId == plan.OldModel.Id))
                {
                    await runtime.UnloadAsync(plan.OldModel.Id, ct);
                    unloadedOld = true;
                }

                var targetLoad = await runtime.LoadTrackedAsync(plan.Target.Id, ct,
                    deferredProfileBudgetIds: plan.ExpectedRevisions.Keys.ToHashSet(StringComparer.Ordinal));
                targetStartedBySwitch = targetLoad.Started;
                try
                {
                    // Loading proves the weights/runtime path. Template application proves that Cerebral can
                    // assemble its system and user messages before any saved profile is changed.
                    _ = await runtime.CountTokensAsync(plan.Target.Id,
                        [new("system", "You are a Pegasus Cerebral agent."), new("user", "Confirm model readiness.")], ct);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("The model loaded, but its chat format is not ready for Pegasus Cerebral. Choose or correct its chat template in Advanced model settings. " + ex.Message, ex);
                }

                // The runtime may fit context during a cold load. Rebuild inherited budgets
                // from that exact confirmed result, retaining the original owner/profile guards.
                plan = store.Locked(() =>
                {
                    var verified = store.Get<ModelRecord>("model", plan.Target.Id)
                        ?? throw new InvalidOperationException("The selected model registration changed while it was loading.");
                    if (!plan.TargetFingerprint.MatchesConfirmedLoad(targetLoad.Model, verified))
                        throw new InvalidOperationException("The selected model settings or weight file changed while it was loading. Refresh and retry.");
                    _ = ValidateDefaultModelPlan(plan);
                    // Keep the payload identity observed before loading even if an external
                    // writer races the plan rebuild; the final commit must still reject it.
                    return BuildDefaultModelPlan(modelId, acceptedBindingsEpoch: acceptedBindingsEpoch) with
                    {
                        TargetFingerprint = plan.TargetFingerprint with { ContextTokens = verified.ContextTokens },
                        RetainedAccepted = plan.RetainedAccepted
                    };
                });
                foreach (var profile in plan.Profiles)
                {
                    var capabilityProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
                    capabilityProfile.WorkerId = "local";
                    await runtime.EnsureRequiredCapabilitiesAsync(capabilityProfile, ct);
                }
                ct.ThrowIfCancellationRequested();
                var committed = store.Locked(() => CommitDefaultModelPlan(plan));
                assignmentsCommitted = true;
                string? unloadWarning = null;
                var previousModelStillAssigned = plan.OldModel is not null && store.List<AgentProfile>("agent")
                    .Any(x => x.ModelWorkerId == "local" && x.ModelId == plan.OldModel.Id);
                if (plan.OldModel is not null && plan.OldModel.Id != plan.Target.Id && !unloadedOld && oldWasLoaded && !previousModelStillAssigned && !HasPlacementAssignments("local", plan.OldModel.Id))
                {
                    try { await runtime.UnloadAsync(plan.OldModel.Id, ct); }
                    catch (Exception ex) { unloadWarning = "The previous model remains loaded: " + ex.Message; }
                }
                events.Publish("agents.updated", null, new { modelId = plan.Target.Id, profileIds = committed.Select(x => x.Id).ToArray() });
                return new
                {
                    model = store.Get<ModelRecord>("model", plan.Target.Id),
                    profiles = committed,
                    previousModelId = plan.OldModel?.Id,
                    previousModelUnloaded = plan.OldModel is not null && plan.OldModel.Id != plan.Target.Id &&
                        oldWasLoaded && !runtime.IsLoaded(plan.OldModel.Id),
                    retainedAcceptedWorkflowIds = plan.RetainedAccepted?.Select(x => x.Proof.RunId).ToArray() ?? [],
                    warning = unloadWarning
                };
            }
            catch (Exception switchError)
            {
                if (assignmentsCommitted) throw new InvalidOperationException("The default model and agent assignments were saved, but the final status or previous-model cleanup did not complete. Refresh before retrying. " + switchError.Message, switchError);
                var recovery = new List<string>();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                if (targetStartedBySwitch && plan.Target.Id != plan.OldModel?.Id && !HasPlacementAssignments("local", plan.Target.Id))
                {
                    try { await runtime.UnloadAsync(plan.Target.Id, cleanup.Token); }
                    catch (Exception ex) { recovery.Add("The candidate model could not be unloaded: " + ex.Message); }
                }
                if (unloadedOld && plan.OldModel is not null && scheduler.GetResourcePauseStatus().State == LocalResourceState.Active)
                {
                    try { await runtime.LoadAsync(plan.OldModel.Id, cleanup.Token); recovery.Add("The previous model was restored."); }
                    catch (Exception ex) { recovery.Add("The previous model could not be restored automatically: " + ex.Message); }
                }
                var suffix = recovery.Count == 0 ? " Previous agent assignments were not changed." : " Previous agent assignments were not changed. " + string.Join(' ', recovery);
                throw new InvalidOperationException("Pegasus Cerebral could not use the selected model. " + switchError.Message + suffix, switchError);
            }
        }
        finally { modelFiles.Release(); }
    }

    private async Task<object> SwitchRemoteDefaultModelAsync(string modelId, string modelWorkerId,
        bool retainWaitingBindings, CancellationToken ct, long? acceptedBindingsEpoch = null)
    {
        await modelFiles.WaitAsync(ct);
        try
        {
            using var owner = await remoteModels.ConnectAsync(modelWorkerId, ct);
            var candidate = await owner.InspectAsync(modelId, ct);
            var plan = retainWaitingBindings
                ? scheduler.WithQuiescentLocalPause(null, pauseUpdatedAt =>
                    store.Locked(() => BuildDefaultModelPlan(modelId, modelWorkerId, candidate, true, pauseUpdatedAt)))
                : store.Locked(() => BuildDefaultModelPlan(modelId, modelWorkerId, candidate, acceptedBindingsEpoch: acceptedBindingsEpoch));
            var targetStarted = false;
            var unloadedOld = false;
            var committed = false;
            try
            {
                // A remote switch does not resume a paused local GPU. Unload an old local
                // model only if no retained assignment or pinned profile still needs it.
                if (plan.OldModel is not null && runtime.IsLoaded(plan.OldModel.Id) && !HasModelAssignments(plan.OldModel.Id) && !HasPlacementAssignments("local", plan.OldModel.Id) &&
                    !plan.UnchangedProfiles.Any(x => x.ModelWorkerId == "local" && x.ModelId == plan.OldModel.Id))
                { await runtime.UnloadAsync(plan.OldModel.Id, ct); unloadedOld = true; }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(20));
                var loaded = await owner.LoadTrackedAsync(modelId, timeout.Token);
                targetStarted = loaded.Started;
                _ = await owner.CountAsync(modelId,
                    [new("system", "You are a Pegasus Cerebral agent."), new("user", "Confirm model readiness.")], "off", timeout.Token);
                if (plan.Profiles.Any(x => ModelCapabilityPolicy.Requirements(x).Contains(ModelCapabilityIds.ImageInput)))
                {
                    var imageProof = await owner.VerifyImageAsync(modelId, timeout.Token);
                    if (imageProof.ModelId != modelId) throw new InvalidOperationException("Remote image readiness returned a different model identity.");
                }
                var verified = await owner.InspectAsync(modelId, timeout.Token);
                if (!plan.TargetFingerprint.MatchesConfirmedLoad(loaded.Model, verified))
                    throw new InvalidOperationException("The remote model settings changed while loading. Inspect its effective context and retry.");
                // A cold load can confirm the requested context without changing its numeric
                // fingerprint. Derive budgets from the loaded state in that case too, while
                // rejecting owner/profile changes captured before the readiness check.
                var preparedPlan = plan;
                DefaultModelPlan RebuildPlan(DateTimeOffset? pauseUpdatedAt) => store.Locked(() =>
                {
                    _ = ValidateDefaultModelPlan(preparedPlan, pauseUpdatedAt);
                    return BuildDefaultModelPlan(modelId, modelWorkerId, verified, retainWaitingBindings, pauseUpdatedAt, acceptedBindingsEpoch) with
                    {
                        // The remote readiness pass may take long enough for a waiting run to
                        // advance and return to Waiting. Keep the originally authorized barrier;
                        // CommitDefaultModelPlan revalidates these exact revisions and the same
                        // pause generation immediately before changing saved assignments.
                        WaitingBarrier = preparedPlan.WaitingBarrier,
                        RetainedAccepted = preparedPlan.RetainedAccepted
                    };
                });
                plan = retainWaitingBindings
                    ? scheduler.WithQuiescentLocalPause(preparedPlan.WaitingBarrier!.PauseUpdatedAt,
                        pauseUpdatedAt => RebuildPlan(pauseUpdatedAt))
                    : RebuildPlan(null);
                foreach (var profile in plan.Profiles) RemoteManagedWorkerSession.EnsureCapabilities(profile, verified);
                await owner.RevalidateAsync(timeout.Token);
                ct.ThrowIfCancellationRequested();
                List<AgentProfile> CommitPlan(DateTimeOffset? pauseUpdatedAt) => store.Locked(() =>
                    { owner.EnsureCurrent(); return CommitDefaultModelPlan(plan, verified, pauseUpdatedAt); });
                var profiles = retainWaitingBindings
                    ? scheduler.WithQuiescentLocalPause(plan.WaitingBarrier!.PauseUpdatedAt,
                        pauseUpdatedAt => CommitPlan(pauseUpdatedAt))
                    : CommitPlan(null);
                committed = true;
                events.Publish("agents.updated", null, new { modelId, modelWorkerId, profileIds = profiles.Select(x => x.Id).ToArray() });
                return new { model = verified, modelWorkerId, profiles, previousModelId = plan.PreviousModelId,
                    previousModelWorkerId = plan.PreviousModelWorkerId, previousModelUnloaded = unloadedOld,
                    retainedWaitingWorkflowIds = plan.WaitingBarrier?.Runs.Select(x => x.Id).ToArray() ?? [],
                    retainedAcceptedWorkflowIds = plan.RetainedAccepted?.Select(x => x.Proof.RunId).ToArray() ?? [],
                    warning = plan.PreviousModelWorkerId != "local" && (plan.PreviousModelWorkerId != modelWorkerId || plan.PreviousModelId != modelId)
                        ? "The previous remote model remains under its worker's normal idle policy." : null };
            }
            catch (Exception error)
            {
                if (committed) throw new InvalidOperationException("The remote default and agent assignments were saved, but final status could not be confirmed. Refresh before retrying. " + error.Message, error);
                var recovery = new List<string>();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                // Only a confirmed newly started model may be cleaned up. A lost load response
                // is uncertain and must not unload a model that another owner action could use.
                if (targetStarted && !HasPlacementAssignments(modelWorkerId, modelId))
                {
                    try { await owner.RevalidateAsync(cleanup.Token); await owner.UnloadAsync(modelId, cleanup.Token); }
                    catch (Exception ex) { recovery.Add("Candidate cleanup was not confirmed: " + ex.Message); }
                }
                if (unloadedOld && plan.OldModel is not null && scheduler.GetResourcePauseStatus().State == LocalResourceState.Active)
                {
                    try { await runtime.LoadAsync(plan.OldModel.Id, cleanup.Token); recovery.Add("The previous local model was restored."); }
                    catch (Exception ex) { recovery.Add("The previous local model could not be restored: " + ex.Message); }
                }
                throw new InvalidOperationException("The remote model could not become the Cerebral default. Previous saved assignments were preserved. " + error.Message + " " + string.Join(' ', recovery), error);
            }
        }
        finally { modelFiles.Release(); }
    }

    private DefaultModelPlan BuildDefaultModelPlan(string modelId, string modelWorkerId = "local", ModelRecord? remoteTarget = null,
        bool retainWaitingBindings = false, DateTimeOffset? retainedPauseUpdatedAt = null, long? acceptedBindingsEpoch = null)
    {
        var target = remoteTarget ?? store.Get<ModelRecord>("model", modelId) ?? throw new ArgumentException("Model not found.");
        if (target.ContextTokens < 512) throw new InvalidOperationException("Inspect or correct the selected model's context setting first.");
        if (string.Equals(target.DeclaredType?.Trim(), "adapter", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected GGUF declares itself as an adapter and cannot serve as the current Cerebral model.");
        if (!target.HasEmbeddedChatTemplate && string.IsNullOrWhiteSpace(target.ChatTemplate))
            throw new InvalidOperationException("The selected model has no embedded chat template. Choose its publisher format in Advanced model settings before using it for Cerebral.");
        var current = configuration.Get();
        var profiles = store.List<AgentProfile>("agent");
        var lead = SingleLead(profiles);
        var oldId = CurrentDefaultModelId(current, lead);
        var oldWorkerId = string.IsNullOrWhiteSpace(current.DefaultModelId) ? lead.ModelWorkerId : current.DefaultModelWorkerId;
        var affected = AffectedProfiles(profiles, lead, current.DefaultModelId, oldId, oldWorkerId);
        var affectedIds = affected.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var waitingBarrier = retainWaitingBindings
            ? CaptureWaitingSwitchBarrier(affectedIds, retainedPauseUpdatedAt ??
                throw new InvalidOperationException("The retained switch has no verified local resource pause generation."))
            : null;
        if (!retainWaitingBindings && acceptedBindingsEpoch is null && HasAffectedAssignments(affectedIds))
            throw new InvalidOperationException("Finish or stop the affected conversation and helper work before switching the default model.");

        var updates = new List<AgentProfile>();
        foreach (var existing in affected)
        {
            var profile = DefaultModelProfileSettings.ForModel(existing, target, modelWorkerId: modelWorkerId);
            updates.Add(profile);
        }
        var retainedAccepted = acceptedBindingsEpoch is { } epoch
            ? CaptureRetainedAcceptedBindings(affectedIds, epoch, PostSwitchProfiles(profiles, updates)) : null;
        return new(current.DefaultModelId, current.DefaultModelWorkerId, oldId, oldWorkerId, modelWorkerId, target,
            TargetModelFingerprint.Capture(target, modelWorkerId == "local"), oldWorkerId == "local" ? store.Get<ModelRecord>("model", oldId) : null, updates,
            profiles.Where(x => !affectedIds.Contains(x.Id)).ToList(), affected.ToDictionary(x => x.Id, x => x.Revision, StringComparer.Ordinal), waitingBarrier, acceptedBindingsEpoch, retainedAccepted);
    }

    private List<AgentProfile> CommitDefaultModelPlan(DefaultModelPlan plan, ModelRecord? remoteTarget = null,
        DateTimeOffset? retainedPauseUpdatedAt = null)
    {
        var target = remoteTarget ?? store.Get<ModelRecord>("model", plan.Target.Id) ?? throw new InvalidOperationException("The selected model registration changed while it was loading.");
        if (!plan.TargetFingerprint.Matches(target))
            throw new InvalidOperationException("The selected model settings or weight file changed while it was loading. Refresh and retry.");
        if (plan.TargetWorkerId == "local" ? !runtime.IsLoaded(target.Id) : target.State != "loaded") throw new InvalidOperationException("The selected model did not remain loaded long enough to apply the switch.");
        var settings = ValidateDefaultModelPlan(plan, retainedPauseUpdatedAt);
        foreach (var profile in plan.Profiles)
            if (plan.TargetWorkerId == "local") runtime.EnsureCurrentlyCompatible(profile, target);
            else RemoteManagedWorkerSession.EnsureCapabilities(profile, target);
        settings.DefaultModelId = target.Id;
        settings.DefaultModelWorkerId = plan.TargetWorkerId;
        configuration.Save(settings);
        foreach (var profile in plan.Profiles)
        {
            profile.Revision = plan.ExpectedRevisions[profile.Id] + 1;
            store.Put("agent", profile.Id, profile);
        }
        if (plan.AcceptedBindingsEpoch is { } epoch)
            RetainedModelBindingAuthorizations.Commit(store, plan.RetainedAccepted!, epoch);
        return plan.Profiles;
    }

    private HostSettings ValidateDefaultModelPlan(DefaultModelPlan plan, DateTimeOffset? retainedPauseUpdatedAt = null)
    {
        var settings = configuration.Get();
        if (!string.Equals(settings.DefaultModelId, plan.ExpectedDefaultModelId, StringComparison.Ordinal) || settings.DefaultModelWorkerId != plan.ExpectedDefaultWorkerId)
            throw new InvalidOperationException("The Pegasus Cerebral default changed while the model was loading. Refresh and retry the intended model.");
        var currentProfiles = store.List<AgentProfile>("agent");
        var currentLead = SingleLead(currentProfiles);
        var currentOldId = CurrentDefaultModelId(settings, currentLead);
        var currentAffected = AffectedProfiles(currentProfiles, currentLead, settings.DefaultModelId, currentOldId, plan.PreviousModelWorkerId);
        var expectedIds = plan.ExpectedRevisions.Keys.ToHashSet(StringComparer.Ordinal);
        if (!expectedIds.SetEquals(currentAffected.Select(x => x.Id)))
            throw new InvalidOperationException("The Cerebral lead or default-following agent list changed while the model was loading. Refresh and retry.");
        if (plan.AcceptedBindingsEpoch is { } epoch)
        {
            var retained = CaptureRetainedAcceptedBindings(expectedIds, epoch, PostSwitchProfiles(currentProfiles, plan.Profiles));
            if (plan.RetainedAccepted is null || !RetainedModelBindingAuthorizations.Same(plan.RetainedAccepted, retained))
                throw new InvalidOperationException("The exact accepted admissions changed while the default route was preparing. Refresh and repeat the explicit retained switch.");
        }
        else if (plan.WaitingBarrier is null)
        {
            if (HasAffectedAssignments(expectedIds))
                throw new InvalidOperationException("Work started while the model was loading. The switch was not applied.");
        }
        else
        {
            if (retainedPauseUpdatedAt != plan.WaitingBarrier.PauseUpdatedAt)
                throw new InvalidOperationException("The retained switch did not preserve its verified local resource pause generation.");
            var currentBarrier = CaptureWaitingSwitchBarrier(expectedIds, retainedPauseUpdatedAt.Value);
            if (!SameWaitingSwitchBarrier(plan.WaitingBarrier, currentBarrier))
                throw new InvalidOperationException("The retained waiting work or local resource pause changed while the remote model was loading. The switch was not applied.");
        }
        foreach (var current in currentAffected)
        {
            if (!plan.ExpectedRevisions.TryGetValue(current.Id, out var expected) || current.Revision != expected)
                throw new InvalidOperationException($"Profile '{current.Name}' changed while the model was loading. Review it and retry.");
        }
        return settings;
    }

    private static AgentProfile SingleLead(IReadOnlyCollection<AgentProfile> profiles)
    {
        var leads = profiles.Where(x => x.Role == "lead").Take(2).ToList();
        return leads.Count switch
        {
            1 => leads[0],
            0 => throw new InvalidOperationException("Set up Cerebral before selecting its default model."),
            _ => throw new InvalidOperationException("Multiple Cerebral lead profiles are configured. Resolve them before switching the default model.")
        };
    }

    private static string CurrentDefaultModelId(HostSettings settings, AgentProfile lead) =>
        string.IsNullOrWhiteSpace(settings.DefaultModelId) ? lead.ModelId : settings.DefaultModelId;

    private static List<AgentProfile> AffectedProfiles(IReadOnlyCollection<AgentProfile> profiles, AgentProfile lead, string configuredDefaultId, string currentDefaultId, string currentDefaultWorkerId) =>
        profiles.Where(x => x.Id == lead.Id || x.FollowsDefaultModel ||
            string.IsNullOrWhiteSpace(configuredDefaultId) && x.WorkerId == "local" && x.ModelWorkerId == currentDefaultWorkerId && x.ModelId == currentDefaultId).ToList();

    private bool HasAffectedAssignments(IReadOnlySet<string> profileIds)
    {
        if (store.ActiveJobs().Any(x => profileIds.Contains(x.ProfileId))) return true;
        if (workflows is null) return false;
        long cursor = 0;
        while (true)
        {
            var page = workflows.Store.ListActivePage(8, cursor);
            if (page.Runs.Any(x => !x.CancelRequested && profileIds.Contains(x.Submission.ProfileId))) return true;
            if (page.NextAfterRow is not { } next) return false;
            cursor = next;
        }
    }

    private static IReadOnlyDictionary<string, AgentProfile> PostSwitchProfiles(IReadOnlyList<AgentProfile> current, IReadOnlyList<AgentProfile> updates)
    {
        var result = current.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var update in updates)
        {
            var value = CerebralJson.Read<AgentProfile>(CerebralJson.Element(update));
            value.Revision = result[update.Id].Revision + 1;
            result[update.Id] = value;
        }
        return result;
    }

    private IReadOnlyList<RetainedModelBindingAuthorizations.Observed> CaptureRetainedAcceptedBindings(
        IReadOnlySet<string> profileIds, long epoch, IReadOnlyDictionary<string, AgentProfile> postSwitchProfiles)
    {
        var engine = workflows?.Engine ?? throw new InvalidOperationException("Retained admissions require a replaceable engine.");
        var status = engine.Status();
        if (!status.Paused || status.Epoch != epoch)
            throw new InvalidOperationException("The owner engine pause changed while preparing the default route.");
        if (store.ActiveJobs().Any(x => profileIds.Contains(x.ProfileId)))
            throw new InvalidOperationException("Finish or stop affected legacy helper work before changing future admissions.");
        long cursor = 0;
        var retained = new List<RetainedModelBindingAuthorizations.Observed>();
        while (true)
        {
            var page = workflows!.Store.ListActivePage(8, cursor);
            foreach (var run in page.Runs.Where(x => !x.CancelRequested))
            {
                var affectsPrimary = profileIds.Contains(run.Submission.ProfileId);
                var affectsCompactor = run.Submission.SettingsSnapshot.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    run.Submission.SettingsSnapshot.TryGetProperty("compactor", out var compact) &&
                    compact.ValueKind == System.Text.Json.JsonValueKind.Object && compact.TryGetProperty("profile", out var compactProfile) &&
                    compactProfile.ValueKind == System.Text.Json.JsonValueKind.Object && compactProfile.TryGetProperty("id", out var compactId) &&
                    compactId.ValueKind == System.Text.Json.JsonValueKind.String && profileIds.Contains(compactId.GetString()!);
                if (!affectsPrimary && !affectsCompactor) continue;
                var snapshot = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot);
                if (snapshot.Profile.Id != run.Submission.ProfileId || snapshot.Model is null ||
                    snapshot.Model.Id != snapshot.Profile.ModelId)
                    throw new InvalidOperationException("An accepted conversation has no exact frozen model binding; its route cannot be retained.");
                retained.Add(RetainedModelBindingAuthorizations.Capture(store, run, snapshot, postSwitchProfiles));
                if (retained.Count > MaximumRetainedWaitingWorkflows)
                    throw new InvalidOperationException("Too many accepted workflows to retain in one default switch.");
            }
            if (page.NextAfterRow is not { } after) return retained.OrderBy(x => x.Proof.RunId, StringComparer.Ordinal).ToArray();
            cursor = after;
        }
    }

    private WaitingSwitchBarrier CaptureWaitingSwitchBarrier(IReadOnlySet<string> profileIds, DateTimeOffset pauseUpdatedAt)
    {
        if (store.ActiveJobs().Any(x => profileIds.Contains(x.ProfileId)))
            throw new InvalidOperationException("Finish or stop the affected helper work before switching the default model for new admissions.");

        var retained = new List<RetainedWaitingWorkflow>();
        if (workflows is not null)
        {
            long cursor = 0;
            while (true)
            {
                var page = workflows.Store.ListActivePage(8, cursor);
                foreach (var run in page.Runs.Where(x => !x.CancelRequested && profileIds.Contains(x.Submission.ProfileId)))
                {
                    if (run.Status != WorkflowStatus.Waiting || run.Effect?.State is WorkflowEffectState.Prepared or WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown)
                        throw new InvalidOperationException("Only quiescent waiting conversations with no pending or uncertain effect may retain their local binding.");
                    WorkerExecutionSnapshot.Snapshot snapshot;
                    try { snapshot = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot); }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
                    { throw new InvalidOperationException("A waiting conversation has no supported frozen model binding. Finish or stop it before switching the default.", error); }
                    if (snapshot.Profile.Id != run.Submission.ProfileId || snapshot.Profile.ModelWorkerId != "local" ||
                        snapshot.Model is null || snapshot.Model.ModelWorkerId != "local")
                        throw new InvalidOperationException("Only a waiting conversation with an exact frozen local-model binding may be retained by this switch.");
                    if (retained.Count >= MaximumRetainedWaitingWorkflows)
                        throw new InvalidOperationException($"At most {MaximumRetainedWaitingWorkflows} waiting conversations may be retained by one default switch.");
                    retained.Add(new(run.Id, run.Revision, run.Status, run.CancelRequested, run.Effect?.Id, run.Effect?.State));
                }
                if (page.NextAfterRow is not { } next) break;
                cursor = next;
            }
        }
        return new(pauseUpdatedAt, retained.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray());
    }

    private static bool SameWaitingSwitchBarrier(WaitingSwitchBarrier expected, WaitingSwitchBarrier current) =>
        expected.PauseUpdatedAt == current.PauseUpdatedAt && expected.Runs.SequenceEqual(current.Runs);

    private bool HasPlacementAssignments(string workerId, string modelId)
    {
        bool Uses(AgentProfile profile) => profile.ModelWorkerId == workerId && profile.ModelId == modelId;
        if (store.ActiveJobs().Any(x => Uses(x.Profile) || x.Profile.CompactionProfileId is { } id && store.Get<AgentProfile>("agent", id) is { } compactor && Uses(compactor))) return true;
        if (workflows is null) return false;
        long cursor = 0;
        while (true)
        {
            var page = workflows.Store.ListActivePage(8, cursor);
            foreach (var run in page.Runs.Where(x => !x.CancelRequested))
            {
                var snapshot = run.Submission.SettingsSnapshot;
                if (snapshot.TryGetProperty("resolvedSettingsVersion", out _))
                {
                    var accepted = WorkerExecutionSnapshot.Read(snapshot);
                    if (Uses(accepted.Profile) || accepted.Compactor is { } compactor && Uses(compactor.Profile)) return true;
                }
                else if (Optional(snapshot, "modelWorkerId") == workerId && Optional(snapshot, "modelId") == modelId) return true;
            }
            if (page.NextAfterRow is not { } next) return false;
            cursor = next;
        }
    }

    private const int MaximumRetainedWaitingWorkflows = 256;
    private sealed record RetainedWaitingWorkflow(string Id, long Revision, WorkflowStatus Status, bool CancelRequested,
        string? EffectId, WorkflowEffectState? EffectState);
    private sealed record WaitingSwitchBarrier(DateTimeOffset PauseUpdatedAt, IReadOnlyList<RetainedWaitingWorkflow> Runs);
    private sealed record DefaultModelPlan(string ExpectedDefaultModelId, string ExpectedDefaultWorkerId, string PreviousModelId, string PreviousModelWorkerId, string TargetWorkerId, ModelRecord Target, TargetModelFingerprint TargetFingerprint, ModelRecord? OldModel,
        List<AgentProfile> Profiles, List<AgentProfile> UnchangedProfiles, Dictionary<string, int> ExpectedRevisions,
        WaitingSwitchBarrier? WaitingBarrier, long? AcceptedBindingsEpoch,
        IReadOnlyList<RetainedModelBindingAuthorizations.Observed>? RetainedAccepted);

    internal sealed record TargetModelFingerprint(string Path, long SizeBytes, long FileLength, long LastWriteUtcTicks,
        int ContextTokens, int RecommendedContextTokens, int ParallelRequests, int GpuLayers,
        string? ChatTemplate, IReadOnlyList<string> StopSequences, bool HasEmbeddedChatTemplate, string? DeclaredType,
        string? VisionProjectorPath, bool LocalFile, bool AutomaticContextSizing, int MaximumContextTokens, string Architecture)
    {
        public static TargetModelFingerprint Capture(ModelRecord model, bool localFile = true)
        {
            var path = localFile ? System.IO.Path.GetFullPath(model.Path) : model.Path;
            var file = localFile ? new FileInfo(path) : null;
            return new(path, model.SizeBytes, file?.Exists == true ? file.Length : -1, file?.Exists == true ? file.LastWriteTimeUtc.Ticks : 0,
                model.ContextTokens, model.RecommendedContextTokens, model.ParallelRequests, model.GpuLayers,
                model.ChatTemplate, model.StopSequences.ToArray(), model.HasEmbeddedChatTemplate, model.DeclaredType, model.VisionProjectorPath, localFile,
                model.AutomaticContextSizing, model.MaximumContextTokens, model.Architecture);
        }

        // Only the runtime's exact load result may explain an automatic context change.
        // Everything else, including the local payload identity and later owner edits,
        // must still match both the pre-load fingerprint and the confirmed load result.
        public bool MatchesConfirmedLoad(ModelRecord loaded, ModelRecord verified) =>
            Matches(loaded, ignoreContext: AutomaticContextSizing) &&
            Capture(loaded, LocalFile).Matches(verified);

        public bool Matches(ModelRecord model, bool ignoreContext = false)
        {
            var comparison = LocalFile && OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var path = LocalFile ? System.IO.Path.GetFullPath(model.Path) : model.Path;
            var file = LocalFile ? new FileInfo(path) : null;
            return Path.Equals(path, comparison) && SizeBytes == model.SizeBytes &&
                FileLength == (file?.Exists == true ? file.Length : -1) && LastWriteUtcTicks == (file?.Exists == true ? file.LastWriteTimeUtc.Ticks : 0) &&
                (ignoreContext || ContextTokens == model.ContextTokens) && RecommendedContextTokens == model.RecommendedContextTokens &&
                AutomaticContextSizing == model.AutomaticContextSizing && MaximumContextTokens == model.MaximumContextTokens && Architecture == model.Architecture &&
                ParallelRequests == model.ParallelRequests && GpuLayers == model.GpuLayers &&
                string.Equals(ChatTemplate, model.ChatTemplate, StringComparison.Ordinal) &&
                StopSequences.SequenceEqual(model.StopSequences, StringComparer.Ordinal) &&
                HasEmbeddedChatTemplate == model.HasEmbeddedChatTemplate &&
                string.Equals(DeclaredType, model.DeclaredType, StringComparison.Ordinal) &&
                string.Equals(VisionProjectorPath, model.VisionProjectorPath, comparison);
        }
    }
}

public static class DefaultModelProfileSettings
{
    public static AgentProfile ForModel(AgentProfile existing, ModelRecord target, int? recommendedContext = null, string modelWorkerId = "local")
    {
        var profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(existing));
        profile.ModelId = target.Id;
        profile.ModelWorkerId = modelWorkerId;
        profile.FollowsDefaultModel = true;
        var budget = AgentBudgetPolicy.Resolve(profile, target, recommendedContext);
        profile.ContextTokens = budget.ContextTokens;
        profile.ResponseTokens = budget.ResponseTokens;
        profile.ReasoningMode = budget.ReasoningMode;
        profile.ReasoningTokens = budget.ReasoningTokens;
        Configuration.ValidateProfile(profile);
        if (modelWorkerId == "local") ModelCapabilityPolicy.EnsureConfigured(profile, target);
        else RemoteManagedWorkerSession.EnsureCapabilities(profile, target, configurationOnly: true);
        return profile;
    }
}
