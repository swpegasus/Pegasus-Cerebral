using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>
/// Owner-only permission to keep one accepted model route after a default switch. This does not
/// replace current tool, package, Cortex, model-definition or remote-installation validation.
/// </summary>
internal static class RetainedModelBindingAuthorizations
{
    internal const string RecordKind = "retained-model-binding";
    internal const string Primary = "primary";
    internal const string Compactor = "compactor";
    private const string RevocationKind = "retained-model-binding-revocation";
    private const int Version = 1;

    internal sealed record Binding(string Kind, string ProfileId, string ModelId, string ModelWorkerId,
        string ModelHash, string CurrentRouteHash, int CommittedProfileRevision, long RevocationGeneration);
    internal sealed record Proof(string RunId, string SubmissionHash, string SnapshotHash,
        IReadOnlyList<Binding> Bindings);
    internal sealed record Observed(Proof Proof, long RunRevision, WorkflowStatus Status,
        string? EffectId, WorkflowEffectState? EffectState);
    private sealed record Stored(int SchemaVersion, long OwnerPauseEpoch, DateTimeOffset CreatedAt, Proof Proof);

    internal static Observed Capture(StateStore store, WorkflowRun run, WorkerExecutionSnapshot.Snapshot snapshot,
        IReadOnlyDictionary<string, AgentProfile> postSwitchProfiles)
    {
        if (run.Id != run.Submission.RequestId || snapshot.Profile.Id != run.Submission.ProfileId || snapshot.Model is null)
            throw new InvalidOperationException("An accepted workflow has no exact model admission identity.");
        var bindings = new List<Binding> { Bind(Primary, snapshot.Profile, snapshot.Model) };
        if (snapshot.Compactor is { } compactor) bindings.Add(Bind(Compactor, compactor.Profile, compactor.Model));
        return new(new(run.Id, Hash(run.Submission), Hash(run.Submission.SettingsSnapshot), bindings.ToArray()),
            run.Revision, run.Status, run.Effect?.Id, run.Effect?.State);

        Binding Bind(string kind, AgentProfile accepted, WorkerExecutionSnapshot.ModelBinding model)
        {
            if (accepted.WorkerId != "local" || accepted.ModelId != model.Id || accepted.ModelWorkerId != model.ModelWorkerId ||
                model.ModelWorkerId != "local" && string.IsNullOrWhiteSpace(model.ModelWorkerInstallationId) ||
                !postSwitchProfiles.TryGetValue(accepted.Id, out var current) || current.WorkerId != "local")
                throw new InvalidOperationException("An accepted workflow's retained model profile is unavailable or has no exact origin.");
            return new(kind, accepted.Id, model.Id, model.ModelWorkerId, Hash(model), RouteHash(current), current.Revision,
                Generation(store, accepted.Id));
        }
    }

    internal static bool Same(IReadOnlyList<Observed> expected, IReadOnlyList<Observed> current) =>
        JsonSerializer.Serialize(expected, CerebralJson.Options) == JsonSerializer.Serialize(current, CerebralJson.Options);

    // The caller holds StateStore.Locked while committing settings and profiles. No workflow row,
    // effect, accepted context, reservation or receipt is rewritten by this separate permission.
    internal static void Commit(StateStore store, IReadOnlyList<Observed> retained, long ownerPauseEpoch)
    {
        if (ownerPauseEpoch < 1 || retained.Count > 256) throw new InvalidOperationException("Invalid retained model authorization barrier.");
        foreach (var item in retained)
            store.Put(RecordKind, item.Proof.RunId, new Stored(Version, ownerPauseEpoch, DateTimeOffset.UtcNow, item.Proof));
    }

    internal static bool Allows(StateStore store, WorkflowSubmission submission, string kind,
        AgentProfile accepted, WorkerExecutionSnapshot.ModelBinding model, AgentProfile current)
    {
        var value = store.Get<Stored>(RecordKind, submission.RequestId);
        if (value is null) return false;
        if (value.SchemaVersion != Version || value.OwnerPauseEpoch < 1 || value.CreatedAt == default ||
            value.Proof is null || value.Proof.RunId != submission.RequestId || value.Proof.Bindings is null ||
            value.Proof.Bindings.Count is < 1 or > 2 ||
            value.Proof.Bindings.Any(x => x is null || x.Kind is not (Primary or Compactor) || x.CommittedProfileRevision < 1 || x.RevocationGeneration < 0 ||
                !ValidHash(x.ModelHash) || !ValidHash(x.CurrentRouteHash)) ||
            value.Proof.Bindings.Select(x => x.Kind).Distinct(StringComparer.Ordinal).Count() != value.Proof.Bindings.Count ||
            !ValidHash(value.Proof.SubmissionHash) || !ValidHash(value.Proof.SnapshotHash))
            throw new InvalidDataException("Stored retained model authorization is invalid. Repeat the explicit owner switch after review.");
        if (value.Proof.SubmissionHash != Hash(submission) || value.Proof.SnapshotHash != Hash(submission.SettingsSnapshot) ||
            accepted.WorkerId != "local" || current.WorkerId != "local" || accepted.Id != current.Id ||
            accepted.ModelId != model.Id || accepted.ModelWorkerId != model.ModelWorkerId)
            return false;
        return value.Proof.Bindings.Any(binding => binding.Kind == kind && binding.ProfileId == accepted.Id &&
            binding.ModelId == model.Id && binding.ModelWorkerId == model.ModelWorkerId && binding.ModelHash == Hash(model) &&
            binding.CurrentRouteHash == RouteHash(current) && binding.RevocationGeneration == Generation(store, accepted.Id));
    }

    // Ordinary profile route edits and deletion revoke this exception. Removing current tool/root
    // grants still uses the existing intersection; restoring a deleted ID must not revive permission.
    internal static void RevokeForProfile(StateStore store, string profileId)
    {
        store.Locked(() =>
        {
            store.Put(RevocationKind, profileId, checked(Generation(store, profileId) + 1));
            return true;
        });
    }

    internal static bool RouteChanged(AgentProfile before, AgentProfile after) => RouteHash(before) != RouteHash(after);
    private static long Generation(StateStore store, string profileId)
    {
        var value = store.Get<long>(RevocationKind, profileId);
        return value >= 0 ? value : throw new InvalidDataException("Retained model revocation generation is invalid.");
    }
    private static string RouteHash(AgentProfile profile) => Hash(new
        { profile.Id, profile.WorkerId, profile.ModelWorkerId, profile.ModelId, profile.Role, profile.CompactionProfileId });
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options)));
}
