using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

// Private owner-command projection; does not extend the public desktop plugin SDK.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ChatPluginInstallPermissionSnapshot(int SchemaVersion, string Id, string ProfileId,
    string? ProjectId, long Revision, bool AllowAutomaticInstall, DateTimeOffset? UpdatedAt,
    string Provenance, string? DecisionId)
{
    internal static ChatPluginInstallPermissionSnapshot Read(JsonElement body, string id, string profile, string? project)
    {
        ChatPluginInstallJson.Fields(body, "schemaVersion", "id", "profileId", "projectId", "revision",
            "allowAutomaticInstall", "updatedAt", "provenance", "decisionId");
        var value = body.Deserialize<ChatPluginInstallPermissionSnapshot>(CerebralJson.Options) ??
            throw new InvalidDataException("Chat install permission returned no snapshot.");
        if (value.SchemaVersion != 1 || value.Id != id || value.ProfileId != profile || value.ProjectId != project ||
            value.Revision < 0 || (value.Revision == 0
                ? value.AllowAutomaticInstall || value.UpdatedAt is not null || value.DecisionId is not null || value.Provenance != "default-off"
                : value.UpdatedAt is null || value.UpdatedAt.Value == default || value.Provenance != "installation-owner/chat-properties" ||
                  !Guid.TryParseExact(value.DecisionId, "N", out var decision) || decision == Guid.Empty))
            throw new InvalidDataException("The returned permission does not match this chat's owner identity and revision.");
        return value;
    }
}

internal static class ChatPluginInstallJson
{
    internal static void Fields(JsonElement body, params string[] expected)
    {
        if (body.ValueKind != JsonValueKind.Object || body.GetRawText().Length > 8192)
            throw new InvalidDataException("A bounded permission snapshot is required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in body.EnumerateObject())
            if (!seen.Add(field.Name) || !expected.Contains(field.Name, StringComparer.Ordinal))
                throw new InvalidDataException("Duplicate or unsupported permission field.");
        if (seen.Count != expected.Length) throw new InvalidDataException("The permission snapshot is incomplete.");
    }
}

/// <summary>Detached editor logic. Target and owner connection are captured, never taken from model arguments.</summary>
internal sealed class ChatPluginInstallPermissionEditorState(string id, string profileId, string? projectId,
    Func<bool> current, Func<CancellationToken, Task<JsonElement>> read,
    Func<long, bool, CancellationToken, Task<CommandResult>> save)
{
    internal ChatPluginInstallPermissionSnapshot? Snapshot { get; private set; }
    internal bool Busy { get; private set; }
    internal bool DesiredAllow { get; set; }
    internal string Status { get; private set; } = "Permission has not been read.";
    internal bool CanSave => !Busy && Snapshot is not null && current();

    internal async Task LoadAsync(CancellationToken token)
    {
        if (Busy) return;
        Busy = true; Snapshot = null; DesiredAllow = false;
        try
        {
            RequireCurrent(token);
            var body = await read(token);
            RequireCurrent(token);
            Snapshot = ChatPluginInstallPermissionSnapshot.Read(body, id, profileId, projectId);
            DesiredAllow = Snapshot.AllowAutomaticInstall;
            Status = Snapshot.Revision == 0 ? "Default off. New plugin requests require manual review."
                : DesiredAllow ? "Automatic install and update is authorized for new requests in this chat."
                : "Automatic install is off. Plugin requests require manual review.";
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Snapshot = null; DesiredAllow = false;
            Status = "Permission unavailable. Refresh before editing; installation still requires a verified current permission.";
        }
        finally { Busy = false; }
    }

    internal async Task SaveAsync(CancellationToken token)
    {
        if (!CanSave) return;
        var prior = Snapshot!;
        var requested = DesiredAllow;
        Busy = true;
        try
        {
            RequireCurrent(token);
            var response = await save(prior.Revision, requested, token);
            RequireCurrent(token);
            if (!response.Success) throw new InvalidOperationException("Permission save was not confirmed.");
            var returned = ChatPluginInstallPermissionSnapshot.Read(response.Data, id, profileId, projectId);
            var expectedRevision = prior.Revision == 0 || prior.AllowAutomaticInstall != requested
                ? checked(prior.Revision + 1) : prior.Revision;
            if (returned.Revision != expectedRevision || returned.AllowAutomaticInstall != requested ||
                (expectedRevision == prior.Revision
                    ? returned.DecisionId != prior.DecisionId || returned.UpdatedAt != prior.UpdatedAt
                    : returned.DecisionId == prior.DecisionId))
                throw new InvalidDataException("The permission save returned a different revision or decision.");
            // No replay after a lost/ambiguous reply. Refresh reads the original persisted decision.
            Snapshot = returned; DesiredAllow = returned.AllowAutomaticInstall;
            Status = requested ? "Saved for new requests in this chat; existing pending reviews remain unchanged."
                : "Saved off. New automatic claims are denied; an already started action retains its normal outcome.";
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Snapshot = null;
            Status = "Save not confirmed. Refresh before editing again; the save was not retried.";
        }
        finally { Busy = false; }
    }

    private void RequireCurrent(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!current()) throw new InvalidOperationException("The captured owner connection or chat binding changed.");
    }
}
