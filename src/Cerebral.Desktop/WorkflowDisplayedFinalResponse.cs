using Cerebral.Contracts;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop;

/// <summary>A final public response whose card has been mounted in this main transcript.</summary>
public sealed record WorkflowDisplayedFinalResponse(string ConversationId, string RunId, string MessageId, string Text);

internal static class WorkflowFinalResponseText
{
    internal static WorkflowDisplayedFinalResponse? Read(CerebralEvent item, string conversationId, string runId, string profileId)
    {
        if (item.Type != "workflow.result" || item.Projection.Human != HumanVisibility.Transcript ||
            item.ConversationId != conversationId || item.TurnId != runId || item.Actor.Kind != EventActorKind.Agent ||
            item.Actor.Id != profileId || item.Provenance.AgentId is { } agent && agent != profileId) return null;
        var parts = new List<string>();
        foreach (var part in item.Content)
        {
            // These are exactly the public response fields rendered by EventBody. Never interpret
            // arbitrary structured data, tool receipts, event Data or private generation previews.
            if (part is TextContentPart plain) parts.Add(plain.Text);
            else if (part is StructuredContentPart { Schema: "workflow.result" } structured &&
                structured.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                structured.Value.TryGetProperty("summary", out var summary) && summary.ValueKind == System.Text.Json.JsonValueKind.String)
                parts.Add(summary.GetString()!);
            else return null; // Do not send a partial or fabricated textual version of mixed media.
        }
        var text = string.Join("\n", parts);
        return string.IsNullOrWhiteSpace(text) || text.Length > DesktopComposerSuggestionContract.MaximumResponseCharacters || text.Contains('\0')
            ? null : new(conversationId, runId, item.Id, text);
    }
}
