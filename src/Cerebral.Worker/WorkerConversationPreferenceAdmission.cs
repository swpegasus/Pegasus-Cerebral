using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflows
{
    private SubmitWorkflowRequest PrepareConversationPreferenceInput(SubmitWorkflowRequest input, AgentProfile? actor,
        EventActor? callerOverride, string? parentRunId, out long? propertiesRevision)
    {
        propertiesRevision = null;
        if (input.DefinitionId != ConversationWorkflowModule.DefinitionId || parentRunId is not null ||
            actor is not null || callerOverride is { Kind: not EventActorKind.User } ||
            input.Input.ValueKind != JsonValueKind.Object) return input;
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in input.Input.EnumerateObject())
            if (!properties.TryAdd(field.Name, field.Value.Clone()))
                throw new ArgumentException("Conversation input contains duplicate fields.");
        var prior = Store.TryGet(input.RequestId);
        var saved = prior is null ? ConversationProperties.ReadAdmissionSettings(legacy, input.ConversationId) : null;
        if (saved is not null) propertiesRevision = saved.Revision;
        var changed = false;
        if (!properties.ContainsKey("workingDirectory"))
        {
            var directory = DefaultConversationDirectory(input, prior is not null, saved);
            if (directory is not null)
            {
                properties["workingDirectory"] = CerebralJson.Element(directory);
                changed = true;
            }
        }
        if (properties.ContainsKey("responseSharePercent"))
            return changed ? input with { Input = CerebralJson.Element(properties) } : input;
        JsonElement? selected = null;
        if (prior is not null)
        {
            // Retries use the already accepted default, never a later owner preference. The original
            // request/caller/lineage and all remaining fields still undergo Store's exact comparison.
            if (prior.Submission.Input.TryGetProperty("responseSharePercent", out var frozen))
                selected = frozen.Clone();
        }
        else
        {
            if (saved?.ResponseSharePercent is { } share) selected = CerebralJson.Element(share);
        }
        if (selected is null) return changed ? input with { Input = CerebralJson.Element(properties) } : input;
        properties["responseSharePercent"] = selected.Value;
        return input with { Input = CerebralJson.Element(properties) };
    }

    private string? DefaultConversationDirectory(SubmitWorkflowRequest input, bool retry,
        ConversationProperties.Settings? saved)
    {
        if (retry)
            return ConversationRootPolicy.ReadDirectory(Store.TryGet(input.RequestId)!.Submission.Input);
        if (saved?.WorkingDirectoryOverrideSet == true) return saved.WorkingDirectory;
        if (Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, input.ConversationId) is { } original)
            return ConversationRootPolicy.ReadDirectory(original.Input);
        if (ConversationPathGrants.ReadStored(legacy, input.ConversationId)?.InitialPreparation is { } prepared)
            return prepared.Initial.WorkingDirectory;
        return null;
    }

    private void ValidateConversationPreferenceRevision(string conversationId, long? expectedRevision)
    {
        if (expectedRevision is { } revision &&
            ConversationProperties.ReadAdmissionSettings(legacy, conversationId).Revision != revision)
            throw new WorkflowConflictException("Chat settings changed during admission. Retry the new request with the current settings.");
    }
}
