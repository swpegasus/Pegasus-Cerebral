using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

internal sealed record CollaborationAdoptedProfile(string Id, string? Name, string? Role);

/// <summary>
/// Optional display provenance from the immutable accepted target submission, never a live profile
/// or the mutable final run payload. An unavailable header preserves legacy adoption without labels.
/// </summary>
internal static class CollaborationAdoptionProfile
{
    private const int MaximumSubmissionBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static CollaborationAdoptedProfile? Read(SqliteConnection connection,
        SqliteTransaction transaction, CollaborationTask task)
    {
        if (task.TargetRunId is null || task.TargetRunId != task.AttemptId || task.TargetRunRevision is not >= 1)
            return null;
        try
        {
            using var command = EventJournal.Command(connection, transaction, """
                SELECT typeof(submission), length(CAST(submission AS BLOB)),
                       CASE WHEN typeof(submission)='text' AND length(CAST(submission AS BLOB)) BETWEEN 1 AND $maximum
                            THEN CAST(submission AS BLOB) ELSE NULL END
                FROM workflow_runs
                WHERE id=$run AND request_id=$run AND conversation_id=$conversation AND profile_id=$profile
                  AND status='Completed' AND revision=$revision AND typeof(revision)='integer'
                LIMIT 2;
                """);
            command.Parameters.AddWithValue("$maximum", MaximumSubmissionBytes);
            command.Parameters.AddWithValue("$run", task.TargetRunId);
            command.Parameters.AddWithValue("$conversation", task.Request.TargetConversationId);
            command.Parameters.AddWithValue("$profile", task.TargetProfileId);
            command.Parameters.AddWithValue("$revision", task.TargetRunRevision.Value);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != "text" || reader.IsDBNull(1) ||
                reader.GetInt64(1) is < 1 or > MaximumSubmissionBytes || reader.IsDBNull(2))
                return null;
            var bytes = (byte[])reader.GetValue(2);
            if (bytes.Length != reader.GetInt64(1) || reader.Read()) return null;
            using var document = JsonDocument.Parse(StrictUtf8.GetString(bytes), new() { MaxDepth = 64 });
            return Project(document.RootElement, task);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or
            InvalidOperationException or FormatException or SqliteException)
        {
            // This is optional descriptive metadata, not a new admission or execution decision.
            return null;
        }
    }

    private static CollaborationAdoptedProfile? Project(JsonElement submission, CollaborationTask task)
    {
        if (submission.ValueKind != JsonValueKind.Object || HasDuplicateProperties(submission) ||
            Scalar(submission, "requestId", 128, 512) != task.AttemptId ||
            Scalar(submission, "conversationId", 128, 512) != task.Request.TargetConversationId ||
            Scalar(submission, "profileId", 128, 512) is not { } acceptedProfileId ||
            acceptedProfileId != task.TargetProfileId || !MatchesProject(submission, task.TargetProjectId) ||
            !submission.TryGetProperty("settingsSnapshot", out var snapshot) || !KnownWorkerHeader(snapshot) ||
            !snapshot.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object ||
            Scalar(profile, "id", 128, 512) != acceptedProfileId)
            return null;

        // Malformed optional scalar labels are omitted individually. No prompt/model/tool body is projected.
        return new(acceptedProfileId, Scalar(profile, "name", 256, 1024), Scalar(profile, "role", 64, 256));
    }

    private static bool KnownWorkerHeader(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object ||
            !snapshot.TryGetProperty("resolvedSettingsVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number is not (2 or 3 or 4))
            return false;
        // Recognize the private Worker envelope without depending on Worker or deserializing its private bodies.
        foreach (var name in new[] { "sharedInstructions", "roleInstructions" })
            if (!snapshot.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return false;
        foreach (var name in new[] { "tools", "capabilities", "allowedTools", "allowedRoots" })
            if (!snapshot.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return false;
        if (!snapshot.TryGetProperty("fullAccess", out var access) ||
            access.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            Scalar(snapshot, "routingFingerprint", 256, 1024) is null ||
            Encoding.UTF8.GetByteCount(snapshot.GetRawText()) > WorkflowContract.MaximumStateBytes)
            return false;
        // Version two predates skills; its absence is not fabricated from current settings.
        if (number is 3 or 4 && (!snapshot.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Array))
            return false;
        return true;
    }

    private static bool MatchesProject(JsonElement submission, string? expected)
    {
        if (!submission.TryGetProperty("projectId", out var project) || project.ValueKind == JsonValueKind.Null)
            return expected is null;
        return Scalar(submission, "projectId", 128, 512) is { } id && id == expected;
    }

    private static string? Scalar(JsonElement value, string name, int maximumCharacters, int maximumUtf8Bytes)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String ||
            field.GetString() is not { } text || string.IsNullOrWhiteSpace(text) ||
            text.Length > maximumCharacters || text.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(text) > maximumUtf8Bytes)
            return null;
        return text;
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
                if (!names.Add(field.Name) || HasDuplicateProperties(field.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }
}
