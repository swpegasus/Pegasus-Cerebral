using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Working-directory identity is separate from file permission grants.</summary>
internal static class ConversationRootPolicy
{
    internal static string? ReadDirectory(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Conversation input must be an object.");
        if (!input.TryGetProperty("workingDirectory", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException("The conversation working directory must be an absolute path.");
        return NormalizeDirectory(value.GetString());
    }

    internal static string? NormalizeDirectory(string? path)
    {
        if (path is null) return null;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The conversation working directory must be a bounded absolute path.");
        // Use the shared structural checks (including links) without treating a working directory
        // as a file grant. This context is never dispatched, persisted or used to execute a tool.
        var canonical = ScopedFilePolicy.PathInScope(path,
            new ToolExecutionContext(new AgentProfile(), "directory-validation", "directory-validation", FullAccess: true));
        if (File.Exists(canonical) && !Directory.Exists(canonical))
            throw new ArgumentException("The conversation working directory is a file.");
        return Path.TrimEndingDirectorySeparator(canonical);
    }

    internal static void ValidateContinuation(WorkflowSubmission? original, JsonElement input, string? current)
    {
        var requested = ReadDirectory(input);
        if (original is null) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (requested is null ? current is not null : current is null || !string.Equals(current, requested, comparison))
            throw new WorkflowConflictException("The chat working directory is currently " +
                (current is null ? "unbound (no directory selected)" : "'" + current + "'") +
                ". Refresh Chat Properties and submit a new request with that current working directory.");
    }
}

public sealed partial class WorkerWorkflows
{
    private void ValidateOwnerConversationDirectory(SubmitWorkflowRequest input, EventActor caller, string? parentRunId)
    {
        if (input.DefinitionId != ConversationWorkflowModule.DefinitionId || parentRunId is not null ||
            caller.Kind != EventActorKind.User) return;
        var original = Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, input.ConversationId);
        var current = original is null ? null : ConversationPathGrants.Root(legacy, Store, input.ConversationId,
            input.ProfileId, original.ProjectId);
        ConversationRootPolicy.ValidateContinuation(original, input.Input, current);
    }
}
