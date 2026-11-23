using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Cerebral.Core;
using Cerebral.Engine.Activation;

namespace Cerebral.Worker;

internal interface IEngineCapabilityCheckClient
{
    Task<T> GetAsync<T>(string operation, object arguments, CancellationToken token);
}
internal sealed record EngineCheckProfilePin(string Id, int Revision, string Role, string WorkerId,
    string ModelId, string ModelWorkerId, int ContextTokens, int ResponseTokens, string ReasoningMode, int ReasoningTokens);
internal sealed record EngineCapabilityCheckPlan(int SchemaVersion, string CheckId, string TargetSlot,
    long ExpectedEpoch, string ExpectedPackageSha256, string ExpectedConfigurationSha256,
    string ExpectedOwnerInstallationId, string ExpectedDataDirectory, string DefinitionId, string DefinitionVersion,
    string RequestId, string ConversationId, EngineCheckProfilePin Lead, EngineCheckProfilePin Alpha, EngineCheckProfilePin Beta);
internal sealed record EngineCapabilityCheckRelease(string IssuerTaskId, string PlanSha256,
    DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc, string RuntimeFenceSha256, bool ControlledModelWindowReviewed);
internal sealed record EngineCapabilityCheckInspection(string CheckId, string TargetSlot, long ObservedEpoch,
    string? ActiveSlot, bool TargetSelected, bool TargetPackageAvailable, bool EnginePaused,
    bool ExactEnginePinsMatch, bool ExactProfilePinsMatch, bool CandidatePending, string State);
internal sealed record EngineCapabilityCheckReceipt(string CheckId, string RequestId, string ConversationId,
    string RunId, long Revision, string Status, bool SubmittedOnce, bool CompletionVerified);

/// <summary>One A/B-parameterized owner check; independent historical helper roles remain unchanged.</summary>
internal static class EngineCapabilityCheck
{
    private const string RootTask = "10000000-0000-4000-8000-000000000003";
    internal static string PlanSha256(EngineCapabilityCheckPlan plan)
    {
        Validate(plan);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan,
            new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false })));
    }

    internal static void Validate(EngineCapabilityCheckPlan p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.SchemaVersion != 1 || p.TargetSlot is not ("A" or "B") || p.ExpectedEpoch < 0 ||
            !Uuid(p.CheckId) || !Uuid(p.RequestId) || !Uuid(p.ConversationId) ||
            p.RequestId != p.CheckId || !Digest(p.ExpectedPackageSha256) || !Digest(p.ExpectedConfigurationSha256) ||
            string.IsNullOrWhiteSpace(p.ExpectedOwnerInstallationId) || p.ExpectedOwnerInstallationId.Length > 128 ||
            !Path.IsPathFullyQualified(p.ExpectedDataDirectory) ||
            p.DefinitionId != "pegasus.conversation:chat" || string.IsNullOrWhiteSpace(p.DefinitionVersion) || p.DefinitionVersion.Length > 64)
            throw new ArgumentException("The parameterized check requires bounded fresh identities and exact owner/engine pins.");
        foreach (var pin in new[] { p.Lead, p.Alpha, p.Beta })
            if (pin is null || string.IsNullOrWhiteSpace(pin.Id) || pin.Id.Length > 128 || pin.Revision < 1 ||
                pin.WorkerId != "local" || string.IsNullOrWhiteSpace(pin.ModelId) || pin.ModelId.Length > 128 ||
                string.IsNullOrWhiteSpace(pin.ModelWorkerId) || pin.ModelWorkerId.Length > 128 ||
                pin.ContextTokens is < 512 or > 1_048_576 || pin.ResponseTokens is < 1 or > 32768 ||
                pin.ReasoningMode is not ("off" or "on") || pin.ReasoningTokens < 0)
                throw new ArgumentException("The check profile references require exact accepted revisions and bounded route/budgets.");
        if (p.Lead.Role != "lead" || p.Alpha.Role != "helper" || p.Beta.Role != "helper" ||
            p.Alpha.Id != "00-engine-check-alpha" || p.Beta.Id != "00-engine-check-beta" ||
            new[] { p.Lead.Id, p.Alpha.Id, p.Beta.Id }.Distinct(StringComparer.Ordinal).Count() != 3)
            throw new ArgumentException("The check keeps a lead and the two independent historical helper roles.");
    }

    internal static async Task<EngineCapabilityCheckInspection> InspectAsync(IEngineCapabilityCheckClient client,
        EngineCapabilityCheckPlan plan, CancellationToken token)
    {
        Validate(plan);
        var host = await client.GetAsync<HostInfo>("health", new { }, token).ConfigureAwait(false);
        if (host.Product != "Pegasus Cerebral" || host.Protocol != 1 || !host.Coordinator ||
            host.InstallationId != plan.ExpectedOwnerInstallationId || !host.LocalRun ||
            !string.Equals(Path.GetFullPath(host.DataDirectory), Path.GetFullPath(plan.ExpectedDataDirectory),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("This check does not own the exact selected local data root.");
        var status = await client.GetAsync<EngineStatus>("engine.status", new { }, token).ConfigureAwait(false);
        var profiles = await client.GetAsync<List<AgentProfile>>("agents.list", new { }, token).ConfigureAwait(false);
        if (profiles.Count > 256) throw new InvalidDataException("The check profile catalog exceeded its bounded inspection.");
        var profileMatch = new[] { plan.Lead, plan.Alpha, plan.Beta }.All(pin =>
            profiles.Count(x => x.Id == pin.Id) == 1 && Match(profiles.Single(x => x.Id == pin.Id), pin));
        var package = status.Slots.SingleOrDefault(x => x.Slot == plan.TargetSlot);
        var selected = status.Active?.Slot == plan.TargetSlot;
        var engineMatch = selected && status.Epoch == plan.ExpectedEpoch && status.Error is null &&
            status.Active?.PackageSha256 == plan.ExpectedPackageSha256 &&
            status.Active.ConfigurationSha256 == plan.ExpectedConfigurationSha256;
        return new(plan.CheckId, plan.TargetSlot, status.Epoch, status.Active?.Slot, selected,
            package is { Available: true, Package: not null, Error: null }, status.Paused, engineMatch, profileMatch,
            status.CandidatePending, engineMatch && profileMatch ? "exact-pins-observed" : "target-or-profile-unavailable");
    }

    internal static async Task<EngineCapabilityCheckReceipt> SubmitOnceAsync(IEngineCapabilityCheckClient client,
        EngineCapabilityCheckPlan plan, EngineCapabilityCheckRelease release, Action consumeOnce, CancellationToken token)
    {
        Validate(plan); ArgumentNullException.ThrowIfNull(release); ArgumentNullException.ThrowIfNull(consumeOnce);
        var now = DateTimeOffset.UtcNow;
        if (release.IssuerTaskId != RootTask || release.PlanSha256 != PlanSha256(plan) ||
            !Digest(release.RuntimeFenceSha256) || !release.ControlledModelWindowReviewed ||
            release.IssuedUtc > now.AddSeconds(5) || release.ExpiresUtc <= now ||
            release.ExpiresUtc <= release.IssuedUtc || release.ExpiresUtc - release.IssuedUtc > TimeSpan.FromMinutes(15))
            throw new UnauthorizedAccessException("A fresh exact Root-reviewed model-window release is required.");
        var observed = await InspectAsync(client, plan, token).ConfigureAwait(false);
        if (!observed.ExactEnginePinsMatch || !observed.ExactProfilePinsMatch || !observed.TargetPackageAvailable ||
            observed.CandidatePending || observed.EnginePaused)
            throw new InvalidOperationException("The selected engine/profile pins are not runnable. No switch/load/profile change was attempted.");
        token.ThrowIfCancellationRequested();
        consumeOnce(); // The CLI uses immutable CreateNew consumption before the sole submitting call.
        var text = $"Coordinate two independent helpers at their exact current accepted revisions. " +
            $"Delegate to {plan.Alpha.Id} revision {plan.Alpha.Revision} to compute 2 + 3 and report ALPHA_RESULT: 5. " +
            $"Delegate to {plan.Beta.Id} revision {plan.Beta.Revision} to count words in 'small stable engine' and report BETA_RESULT: 3. " +
            "Wait for both actual child results and synthesize them. Directly answering without both child contexts does not satisfy the check.";
        var run = await client.GetAsync<WorkflowRun>("jobs.workflow.submit",
            new SubmitWorkflowRequest(plan.RequestId, plan.ConversationId, plan.Lead.Id, plan.DefinitionId,
                plan.DefinitionVersion, text, null, WorkflowContract.EmptyObject), token).ConfigureAwait(false);
        if (run.Id is not { Length: > 0 and <= 128 } || run.Revision < 1 || !Enum.IsDefined(run.Status) ||
            run.Submission.RequestId != plan.RequestId || run.Submission.ConversationId != plan.ConversationId ||
            run.Submission.ProfileId != plan.Lead.Id || run.Submission.DefinitionId != plan.DefinitionId ||
            run.Submission.DefinitionVersion != plan.DefinitionVersion)
            throw new InvalidDataException("The accepted check did not match its exact submitted identity; it was not replayed.");
        return Receipt(plan, run, true);
    }

    internal static async Task<EngineCapabilityCheckReceipt> ReadRetainedAsync(IEngineCapabilityCheckClient client,
        EngineCapabilityCheckPlan plan, string runId, CancellationToken token)
    {
        Validate(plan);
        if (string.IsNullOrWhiteSpace(runId) || runId.Length > 128) throw new ArgumentException("A bounded exact retained run ID is required.");
        var owner = await client.GetAsync<HostInfo>("health", new { }, token).ConfigureAwait(false);
        if (owner.Product != "Pegasus Cerebral" || owner.Protocol != 1 || !owner.Coordinator || !owner.LocalRun ||
            owner.InstallationId != plan.ExpectedOwnerInstallationId ||
            !string.Equals(Path.GetFullPath(owner.DataDirectory), Path.GetFullPath(plan.ExpectedDataDirectory),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Retained evidence must come from the exact local check owner.");
        var run = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = runId }, token).ConfigureAwait(false);
        if (run.Id != runId || run.Submission.RequestId != plan.RequestId ||
            run.Submission.ConversationId != plan.ConversationId || run.Submission.ProfileId != plan.Lead.Id ||
            run.Submission.DefinitionId != plan.DefinitionId || run.Submission.DefinitionVersion != plan.DefinitionVersion)
            throw new InvalidDataException("The retained result belongs to another check.");
        var receipt = Receipt(plan, run, false);
        if (run.Status != WorkflowStatus.Completed || !AcceptedProfileMatches(run, plan.Lead)) return receipt;
        var childIds = new HashSet<string>(StringComparer.Ordinal);
        long after = 0; long? snapshot = null;
        for (var pageNumber = 0; pageNumber < 4; pageNumber++)
        {
            var page = await client.GetAsync<JournalPage>("jobs.workflow.events",
                snapshot is null ? (object)new { id = runId, limit = 200, afterSequence = after }
                    : new { id = runId, limit = 200, afterSequence = after, snapshotSequence = snapshot.Value }, token).ConfigureAwait(false);
            if (page.Events is null || page.Events.Count > 200 || page.SnapshotSequence < 0 ||
                JsonSerializer.SerializeToUtf8Bytes(page, CerebralJson.Options).Length > 1024 * 1024)
                throw new InvalidDataException("The bounded check journal page exceeded its contract.");
            if (snapshot is not null && snapshot != page.SnapshotSequence)
                throw new InvalidDataException("The retained journal snapshot changed.");
            snapshot = page.SnapshotSequence;
            var previous = after;
            foreach (var item in page.Events)
            {
                item.Validate();
                if (item.ConversationId != plan.ConversationId || item.Sequence <= previous || item.Sequence > snapshot)
                    throw new InvalidDataException("The check journal page changed identity or sequence.");
                previous = item.Sequence;
                Collect(item);
            }
            if (childIds.Count > 8) throw new InvalidDataException("The check child wave exceeded its bound.");
            if (page.NextAfterSequence is not { } cursor) break;
            if (cursor <= after || cursor != previous || cursor >= snapshot)
                throw new InvalidDataException("The check journal returned an invalid bounded cursor.");
            after = cursor;
            if (pageNumber == 3) return receipt; // Incomplete bounded observation stays unverified.
        }
        if (childIds.Count != 2) return receipt;
        var children = new List<WorkflowRun>();
        foreach (var id in childIds)
            children.Add(await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id }, token).ConfigureAwait(false));
        var expected = new[] { (plan.Alpha, "ALPHA_RESULT: 5"), (plan.Beta, "BETA_RESULT: 3") };
        var verified = children.All(child => child.Status == WorkflowStatus.Completed && child.Submission.ParentRunId == run.Id) &&
            expected.All(pair => children.Count(child => child.Submission.ProfileId == pair.Item1.Id &&
                AcceptedProfileMatches(child, pair.Item1) &&
                child.Result is { } result && JsonSerializer.SerializeToUtf8Bytes(result, CerebralJson.Options).Length <= 32768 &&
                result.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
                text.GetString()!.Trim() == pair.Item2) == 1);
        return receipt with { CompletionVerified = verified };

        void Collect(CerebralEvent item)
        {
            // Only the Engine's confirmed delegation result can identify child work.
            // Arbitrary runIds in model text, tool arguments or unrelated event data are not proof.
            if (item.Type != CerebralEventTypes.ToolResult || item.TurnId != run.Id ||
                item.Provenance.JobId != run.Id || item.Provenance.Source != EventSourceKind.Tool ||
                item.Provenance.SourceId != WorkerWorkflowEffects.DelegateCommand ||
                item.Actor.Kind != EventActorKind.Tool || item.Actor.Id != WorkerWorkflowEffects.DelegateCommand ||
                item.Data is not { } data) return;
            var tool = data.Deserialize<ToolResultData>(CerebralJson.Options);
            if (tool is not { Success: true, Truncated: false } || tool.ToolName != WorkerWorkflowEffects.DelegateCommand ||
                tool.CallId != item.CorrelationId) return;
            foreach (var part in item.Content.OfType<StructuredContentPart>().Where(x => x.Schema == "workflow.command.result"))
            {
                var result = part.Value.Deserialize<WorkflowCommandResult>(CerebralJson.Options);
                if (result?.State != WorkflowEffectState.Succeeded || result.Data is not { } payload ||
                    payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("runIds", out var ids) ||
                    ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > 8) continue;
                foreach (var id in ids.EnumerateArray())
                {
                    if (id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 and <= 128 } value)
                        throw new InvalidDataException("The retained delegation result contained an invalid child identity.");
                    childIds.Add(value);
                }
            }
        }
    }

    private static bool AcceptedProfileMatches(WorkflowRun run, EngineCheckProfilePin pin)
    {
        try { var snapshot = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot);
            return snapshot.Profile.Id == pin.Id && Match(snapshot.Profile, pin) &&
                snapshot.Model is { } model && model.Id == pin.ModelId && model.ModelWorkerId == pin.ModelWorkerId &&
                (model.ModelWorkerId == "local" || !string.IsNullOrWhiteSpace(model.ModelWorkerInstallationId)); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException or OverflowException)
        { return false; } // Older or unrecognized retained settings are unavailable proof.
    }

    private static EngineCapabilityCheckReceipt Receipt(EngineCapabilityCheckPlan p, WorkflowRun run, bool submitted) =>
        new(p.CheckId, p.RequestId, p.ConversationId, run.Id, run.Revision, run.Status.ToString(), submitted, false);
    // Parent completion alone cannot prove both child contexts/results; the bounded journal review remains separate.
    private static bool Match(AgentProfile p, EngineCheckProfilePin pin) =>
        p.Revision == pin.Revision && p.Role == pin.Role && p.WorkerId == pin.WorkerId && p.ModelId == pin.ModelId &&
        p.ModelWorkerId == pin.ModelWorkerId && p.ContextTokens == pin.ContextTokens && p.ResponseTokens == pin.ResponseTokens &&
        p.ReasoningMode == pin.ReasoningMode && p.ReasoningTokens == pin.ReasoningTokens;
    private static bool Uuid(string x) => x is { Length: 32 } && x.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Digest(string x) => x is { Length: 64 } && x.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
