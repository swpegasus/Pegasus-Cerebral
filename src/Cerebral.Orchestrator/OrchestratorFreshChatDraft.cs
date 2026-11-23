using System.IO;
using System.Text.Json;
using System.Net.Http;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private sealed record InitialChatPathGrant(string Path, string Access);
    private sealed record FreshChatSelection(string? ProfileId, int? ProfileRevision, InitialChatPathGrant[] Grants);
    private sealed record FreshChatDraft(string ConversationId, FreshChatSelection Selection,
        CerebralClient? Client, object Connection, string Identity, int Source, int SelectionVersion);
    private sealed record FreshChatPreparation(FreshChatDraft Draft, WorkflowConversationSubmission Request,
        string ProfileId, int ProfileRevision, string? ProjectId, string? WorkingDirectory,
        InitialChatPathGrant[] Grants, CerebralClient? Client, object Connection, string Identity, int Source, int SelectionVersion);
    private FreshChatDraft? freshChatDraft;
    // Kept after acceptance: this is the initial preparation's provenance, not a replacement owner.
    private FreshChatPreparation? freshChatPreparation;
    private string freshChatAuthorityState = "intent";
    private bool freshChatAuthoritySaveAttempted;
    private long? freshChatPreparedRevision;
    private bool freshChatInitialAccepted;
    private bool freshChatInitialDispatchAttempted;
    private string? InitialAcceptedProfileId => freshChatInitialAccepted &&
        freshChatPreparation is { } initial && FreshChatPreparationBindingCurrent(initial) ? initial.ProfileId : null;

    private void MarkInitialChatDispatchAttempt(WorkflowConversationSubmission request)
    {
        if (KeepInitialChatRequest(request)) freshChatInitialDispatchAttempted = true;
    }

    private void MarkInitialChatAccepted(string requestId)
    {
        if (freshChatPreparation is { } prepared && prepared.Draft.ConversationId == durableConversationId &&
            prepared.Request.RequestId == requestId)
        {
            freshChatInitialAccepted = true;
            freshChatAuthorityState = "accepted";
        }
    }

    private static InitialChatPathGrant[] ReadInitialChatGrants(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("initialPathGrants", out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 32)
            throw new ArgumentException("Choose at most 32 initial path grants.");
        var grants = new List<InitialChatPathGrant>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateArray())
        {
            EnsureSemanticArguments(item, "path", "access");
            if (item.EnumerateObject().Count() != 2 ||
                !item.TryGetProperty("path", out var pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("access", out var accessValue) || accessValue.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Each initial path grant needs a path and access.");
            var path = pathValue.GetString()!;
            var access = accessValue.GetString()!;
            if (path.Length is 0 or > 2048 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path) ||
                path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
                path.IndexOf(':', 2) >= 0 || path.Split(new[] { '\\', '/' }).Any(x => x is "." or "..") ||
                access is not ("read" or "write"))
                throw new ArgumentException("Use a fully qualified local path and read or write access.");
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!paths.Add(path)) throw new ArgumentException("Initial path grants must have unique normalized paths.");
            grants.Add(new(path, access));
        }
        return grants.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static FreshChatSelection ReadFreshChatSelection(JsonElement arguments)
    {
        var hasProfile = arguments.TryGetProperty("profileId", out var profile);
        var hasRevision = arguments.TryGetProperty("profileRevision", out var revision);
        if (hasProfile != hasRevision)
            throw new ArgumentException("profileId and profileRevision must be supplied together.");
        string? id = null; int? expected = null;
        if (hasProfile)
        {
            if (profile.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(profile.GetString()) ||
                profile.GetString()!.Length > 128 || profile.GetString()!.Any(char.IsControl) ||
                revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt32(out var number) || number < 1)
                throw new ArgumentException("Choose an existing local lead or helper and its persisted positive revision.");
            id = profile.GetString(); expected = number;
        }
        if (!hasProfile && arguments.TryGetProperty("initialPathGrants", out _))
            throw new ArgumentException("Initial path grants require an explicit profile and persisted revision.");
        return new(id, expected, ReadInitialChatGrants(arguments));
    }

    private static AgentProfile ResolveFreshChatProfile(IReadOnlyList<AgentProfile> available, FreshChatSelection selection)
    {
        var matches = available.Where(x => x.Id == selection.ProfileId).Take(2).ToArray();
        if (matches.Length != 1 || matches[0].WorkerId != "local" || matches[0].Role is not ("lead" or "helper") ||
            matches[0].Revision != selection.ProfileRevision)
            throw new InvalidOperationException("The selected local lead or helper no longer has the expected persisted revision.");
        return matches[0];
    }

    private void InstallFreshChatDraft(FreshChatSelection? selection)
    {
        freshChatDraft = new(durableConversationId!, selection ?? new(null, null, []),
            SemanticAttachClient(), ServiceConnection, ServiceConnectionIdentity,
            knowledgeConnectionVersion, conversationSelectionVersion);
        freshChatPreparation = null; freshChatAuthorityState = "intent";
        freshChatAuthoritySaveAttempted = false; freshChatPreparedRevision = null; freshChatInitialAccepted = false; freshChatInitialDispatchAttempted = false;
    }

    private bool FreshChatDraftBindingCurrent(FreshChatDraft draft) =>
        draft.ConversationId == durableConversationId && draft.SelectionVersion == conversationSelectionVersion &&
        ReferenceEquals(draft.Client, SemanticAttachClient()) && ReferenceEquals(draft.Connection, ServiceConnection) &&
        draft.Identity == ServiceConnectionIdentity && draft.Source == knowledgeConnectionVersion &&
        NativePreferenceConnectionCurrent(draft.Connection, draft.Identity);

    private AgentProfile? FreshChatSelectedProfile()
    {
        if (freshChatDraft is { } draft && draft.ConversationId == durableConversationId &&
            draft.Selection.ProfileId is { } id)
            return FreshChatDraftBindingCurrent(draft)
                ? profiles.SingleOrDefault(x => x.Id == id && x.Revision == draft.Selection.ProfileRevision &&
                    x.WorkerId == "local" && (x.Role is "lead" or "helper")) : null;
        return leadProfile is { WorkerId: "local", Role: "lead" } lead ? lead : null;
    }

    // Discovery, currentness and detailed choice admission use the same selected-profile policy.
    private bool FreshChatProfileAdmissible(AgentProfile profile) =>
        FreshChatSelectedProfile() is { } selected && ReferenceEquals(selected, profile) && ReferenceEquals(TryConversationProfile(), profile);

    private object FreshChatProfileSelectionSnapshot()
    {
        var selected = TryConversationProfile();
        var saved = NativeChatProfileId;
        var draft = freshChatDraft is { } current && current.ConversationId == durableConversationId ? current : null;
        var explicitDraft = saved is null && draft?.Selection.ProfileId is not null;
        return new
        {
            origin = nativeChat?.Snapshot is { } snapshot && snapshot.ConversationId == durableConversationId && snapshot.Run is not null ? "accepted_run" :
                durableConversationId is { } id && durableConversations.ContainsKey(id) ? "saved_conversation" :
                attachmentReservedOwner is { } reserved && reserved.ConversationId == durableConversationId ? "reserved_owner" :
                retainedNativeSubmission is not null && RetainedNativeOwnerCurrent() ? "retained_request" :
                InitialAcceptedProfileId is not null ? "accepted_owner" : explicitDraft ? "explicit_draft" : "default_local_lead",
            profileId = saved ?? (explicitDraft ? draft?.Selection.ProfileId : selected?.Id),
            expectedProfileRevision = explicitDraft ? draft?.Selection.ProfileRevision : null,
            initialAccepted = freshChatInitialAccepted && freshChatPreparation?.Draft.ConversationId == durableConversationId,
            currentProfileRevision = selected?.Revision,
            available = selected is not null,
            role = selected?.Role,
            workerId = selected?.WorkerId
        };
    }

    private object FreshChatPathAuthoritySnapshot()
    {
        var current = freshChatPreparation is { } prepared && prepared.Draft.ConversationId == durableConversationId
            ? prepared : null;
        var draft = freshChatDraft?.ConversationId == durableConversationId ? freshChatDraft : null;
        return new
        {
            state = current is null ? draft?.Selection.ProfileId is not null ? "intent" : "legacy_profile_authority" : freshChatAuthorityState,
            firstRequestId = current?.Request.RequestId,
            profileId = current?.ProfileId ?? draft?.Selection.ProfileId,
            expectedProfileRevision = current is null ? draft?.Selection.ProfileRevision : current.ProfileRevision,
            projectId = current?.ProjectId ?? ConversationProjectId,
            workingDirectory = current?.WorkingDirectory,
            preparedRevision = current is null ? null : freshChatPreparedRevision,
            initialPathGrants = current?.Grants ?? draft?.Selection.Grants ?? [],
            firstSubmitAttempted = current is not null && freshChatInitialDispatchAttempted,
            bindingCurrent = current is not null && FreshChatPreparationBindingCurrent(current),
            evidence = current is not null && freshChatInitialAccepted ? "accepted_submission_and_initial_paths_readback" :
                current is not null && freshChatAuthorityState == "accepted_owner_observed" ? "accepted_owner_readback_requires_submission_receipt" :
                current is not null && freshChatPreparedRevision is not null ? "initial_paths_readback" : "intent_only"
        };
    }

    private void CaptureFreshChatPreparation(WorkflowConversationSubmission request, AgentProfile profile,
        string? mainProjectId, string? mainWorkingDirectory, bool exactDefinition)
    {
        if (freshChatDraft?.Selection.ProfileId is null || nativeChat?.Snapshot?.Run is not null || freshChatInitialAccepted && freshChatPreparation?.Draft.ConversationId == durableConversationId) return;
        if (freshChatPreparation is { } existing)
        {
            if (!ReferenceEquals(existing.Request, request))
                throw new InvalidOperationException("Retry the original first request or start a new chat.");
            return;
        }
        var draft = freshChatDraft;
        if (draft is null || draft.ConversationId != durableConversationId)
            throw new InvalidOperationException("Start a new chat before preparing its first request.");
        if (draft.Selection.ProfileId is not null && !FreshChatDraftBindingCurrent(draft))
            throw new InvalidOperationException("The draft belongs to its original connection. Start a new chat after reconnecting.");
        if (!FreshChatProfileAdmissible(profile) || profile.Revision < 1)
            throw new InvalidOperationException("The selected draft profile is unavailable.");
        string? ReadNullable(string name) => request.Input.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException("The frozen request workspace is invalid.")
            : null;
        // Exact plugin input owns all of its field names. Main's first-chat path authority comes
        // from its independently resolved project and root, never from a plugin schema payload.
        var root = exactDefinition ? mainWorkingDirectory : ReadNullable("workingDirectory");
        if (root is not null) root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        freshChatPreparation = new(draft, request, profile.Id, profile.Revision,
            exactDefinition ? mainProjectId : ReadNullable("projectId"), root,
            draft.Selection.Grants.ToArray(), SemanticAttachClient(), ServiceConnection, ServiceConnectionIdentity,
            knowledgeConnectionVersion, conversationSelectionVersion);
        freshChatAuthorityState = "intent";
    }

    private bool FreshChatPreparationBindingCurrent(FreshChatPreparation prepared) =>
        prepared.Draft.ConversationId == durableConversationId && prepared.SelectionVersion == conversationSelectionVersion &&
        ReferenceEquals(prepared.Client, SemanticAttachClient()) && ReferenceEquals(prepared.Connection, ServiceConnection) &&
        prepared.Identity == ServiceConnectionIdentity && prepared.Source == knowledgeConnectionVersion &&
        NativePreferenceConnectionCurrent(prepared.Connection, prepared.Identity) && RetainedNativeOwnerCurrent();

    private bool TryInitialAcceptedRoot(out string? root)
    {
        root = null;
        if (!freshChatInitialAccepted || freshChatPreparation is not { } prepared ||
            !FreshChatPreparationBindingCurrent(prepared)) return false;
        root = prepared.WorkingDirectory;
        return true;
    }

    private bool KeepInitialChatRequest(WorkflowConversationSubmission request) =>
        freshChatPreparation is { } prepared && ReferenceEquals(prepared.Request, request) &&
        prepared.Draft.ConversationId == durableConversationId;

    private static object FreshChatInitialTuple(FreshChatPreparation prepared) => new
    {
        firstRequestId = prepared.Request.RequestId, profileId = prepared.ProfileId,
        expectedProfileRevision = prepared.ProfileRevision, projectId = prepared.ProjectId,
        workingDirectory = prepared.WorkingDirectory
    };

    private static bool InitialChatReadbackMatches(JsonElement value, FreshChatPreparation prepared, bool acceptedOwner = false)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count()) return false;
        bool Text(JsonElement item, string name, string? expected) => item.TryGetProperty(name, out var actual) &&
            (expected is null ? actual.ValueKind == JsonValueKind.Null :
                actual.ValueKind == JsonValueKind.String && actual.GetString() == expected);
        if (!Text(value, "id", prepared.Draft.ConversationId) || !Text(value, "profileId", prepared.ProfileId) ||
            !Text(value, "projectId", prepared.ProjectId) || !Text(value, "root", prepared.WorkingDirectory) ||
            !Text(value, "rootState", acceptedOwner ? prepared.WorkingDirectory is null ? "unbound" : "bound" : "prepared") ||
            !Text(value, "initialState", acceptedOwner ? "accepted" : "prepared") ||
            !value.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var number) || (acceptedOwner ? number < 1 : number != 1) ||
            !value.TryGetProperty("initial", out var initial) || initial.ValueKind != JsonValueKind.Object ||
            initial.EnumerateObject().Count() != 5 ||
            !Text(initial, "firstRequestId", prepared.Request.RequestId) || !Text(initial, "profileId", prepared.ProfileId) ||
            !Text(initial, "projectId", prepared.ProjectId) || !Text(initial, "workingDirectory", prepared.WorkingDirectory) ||
            !initial.TryGetProperty("expectedProfileRevision", out var expected) || expected.ValueKind != JsonValueKind.Number || !expected.TryGetInt32(out var profileRevision) ||
            profileRevision != prepared.ProfileRevision) return false;
        // After accepted-owner readback the SDK must resolve its original RID; later grant revisions do not reprepare it.
        if (acceptedOwner) return true;
        if (!value.TryGetProperty("grants", out var grants) || grants.ValueKind != JsonValueKind.Array ||
            grants.GetArrayLength() != prepared.Grants.Length) return false;
        var returned = grants.EnumerateArray().ToArray();
        for (var i = 0; i < returned.Length; i++)
            if (returned[i].ValueKind != JsonValueKind.Object || returned[i].EnumerateObject().Count() != 2 ||
                !Text(returned[i], "path", prepared.Grants[i].Path) || !Text(returned[i], "access", prepared.Grants[i].Access)) return false;
        return true;
    }

    private async Task EnsureFreshChatAuthorityAsync(WorkflowConversationSubmission request, CancellationToken token)
    {
        // Accepted retry and every later turn use the saved immutable owner, never fresh preparation.
        if (freshChatDraft?.Selection.ProfileId is null || nativeChat?.Snapshot?.Run is not null || freshChatInitialAccepted && freshChatPreparation?.Draft.ConversationId == durableConversationId) return;
        var prepared = freshChatPreparation;
        if (prepared is null || !ReferenceEquals(prepared.Request, request))
            throw new InvalidOperationException("The first request has no frozen path preparation. Start a new chat.");
        // SDK submission may have committed despite a lost acknowledgement. Its exact retained retry
        // must reach accepted-RID lookup before any fresh profile or path-preparation admission.
        if (freshChatInitialDispatchAttempted && FreshChatPreparationBindingCurrent(prepared)) return;
        void Current()
        {
            token.ThrowIfCancellationRequested();
            RequireSemanticAttachCurrent();
            if (!FreshChatPreparationBindingCurrent(prepared))
                throw new InvalidOperationException("The first request belongs to its original connection. Start a new chat.");
        }
        void ProfileCurrent()
        {
            Current();
            if (TryConversationProfile() is not { } profile || profile.Id != prepared.ProfileId || profile.Revision != prepared.ProfileRevision)
                throw new InvalidOperationException("The first request's persisted profile changed. Start a new chat.");
        }
        Current();
        using var captured = nativePreferenceCommandOverride is null ? CreateIndependentClient() : null;
        var command = NativePreferenceCommand(captured);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(15));
        async Task<bool> Read()
        {
            var reply = await command("conversations.paths.get", new { id = prepared.Draft.ConversationId }, bounded.Token);
            Current();
            if (!reply.Success) return false;
            if (InitialChatReadbackMatches(reply.Data, prepared, acceptedOwner: true))
            {
                freshChatInitialDispatchAttempted = true;
                freshChatAuthorityState = "accepted_owner_observed";
                return true;
            }
            if (!InitialChatReadbackMatches(reply.Data, prepared)) return false;
            ProfileCurrent();
            freshChatPreparedRevision = 1; freshChatAuthorityState = "prepared"; return true;
        }
        try
        {
            // An uncertain earlier attempt is read first. Only an explicit retry can reach this method again.
            if (freshChatAuthoritySaveAttempted && await Read()) return;
            ProfileCurrent();
            freshChatAuthoritySaveAttempted = true;
            freshChatAuthorityState = "held";
            try
            {
                var reply = await command("conversations.paths.save", new
                {
                    id = prepared.Draft.ConversationId, expectedRevision = 0,
                    grants = prepared.Grants, initial = FreshChatInitialTuple(prepared)
                }, bounded.Token);
                Current();
                if (!reply.Success) throw new InvalidOperationException("The initial path preparation was refused.");
            }
            catch (Exception error) when (error is InvalidOperationException or HttpRequestException or IOException or
                ArgumentException or JsonException or TimeoutException or OperationCanceledException)
            {
                // A failure response does not prove rollback. Confirm only the exact original initial tuple.
                try { if (await Read()) return; }
                catch (Exception readError) when (readError is InvalidOperationException or HttpRequestException or IOException or
                    ArgumentException or JsonException or TimeoutException or OperationCanceledException)
                { throw new InvalidOperationException("No generation was submitted. Initial path authority is held; retry the same request or start a new chat.", new AggregateException(error, readError)); }
                throw new InvalidOperationException("No generation was submitted. Initial path authority is unconfirmed; retry this same request or start a new chat.", error);
            }
            if (!await Read())
                throw new InvalidOperationException("No generation was submitted. Initial path authority did not match the original request; retry it or start a new chat.");
        }
        catch
        {
            freshChatAuthorityState = "held"; freshChatPreparedRevision = null;
            throw;
        }
    }
}
