using System.IO;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private sealed record ExactWorkflowConversation(ConversationSummary Summary, string DefinitionId,
        string DefinitionVersion, string AcceptedRunId);
    private sealed record ExactWorkflowConversationPage(IReadOnlyList<ExactWorkflowConversation> Conversations,
        long SnapshotRow, long? NextBeforeRow);

    private readonly Dictionary<string, ExactWorkflowConversation> exactWorkflowConversations = new(StringComparer.Ordinal);
    private ExactWorkflowConversationPage? exactWorkflowConversationPage;
    private object? unsupportedExactNavigationConnection;
    private string? unsupportedExactNavigationIdentity;
    private int unsupportedExactNavigationSource = -1;
    private string? exactWorkflowNavigationConnectionIdentity;

    private bool ExactNavigationUnsupportedCurrent() =>
        ReferenceEquals(unsupportedExactNavigationConnection, ServiceConnection) &&
        unsupportedExactNavigationIdentity == ServiceConnectionIdentity &&
        unsupportedExactNavigationSource == knowledgeConnectionVersion;

    private async Task<ExactWorkflowConversationPage?> ReadExactNavigationPageAsync(string operation,
        object arguments)
    {
        var client = Client; var connection = ServiceConnection;
        var identity = ServiceConnectionIdentity; var source = knowledgeConnectionVersion;
        var response = await client.CommandAsync(operation, arguments, cancellationToken: Lifetime.Token);
        if (!ReferenceEquals(client, Client) || !ReferenceEquals(connection, ServiceConnection) ||
            identity != ServiceConnectionIdentity || source != knowledgeConnectionVersion)
            throw new InvalidOperationException("The exact workflow navigation connection changed during its read.");
        if (!response.Success)
        {
            // Only an exact old-Worker unknown command permits ordinary-Chat compatibility.
            if (response.Error == "Unknown command: " + operation)
            {
                exactWorkflowConversationPage = null;
                exactWorkflowConversations.Clear();
                exactWorkflowNavigationConnectionIdentity = null;
                if (!ExactNavigationUnsupportedCurrent())
                {
                    unsupportedExactNavigationConnection = ServiceConnection;
                    unsupportedExactNavigationIdentity = ServiceConnectionIdentity;
                    unsupportedExactNavigationSource = knowledgeConnectionVersion;
                    SetStatus("Exact workflow navigation needs a newer connected Worker. Ordinary chats remain available.", true);
                }
                return null;
            }
            throw new InvalidOperationException(response.Error);
        }
        return CerebralJson.Read<ExactWorkflowConversationPage>(response.Data);
    }

    private static void ValidateExactWorkflowConversation(ExactWorkflowConversation entry)
    {
        if (entry.Summary is not { } summary || string.IsNullOrWhiteSpace(summary.Id) ||
            string.IsNullOrWhiteSpace(summary.ProfileId) || string.IsNullOrWhiteSpace(summary.Title) ||
            string.IsNullOrWhiteSpace(entry.DefinitionId) || string.IsNullOrWhiteSpace(entry.DefinitionVersion) ||
            entry.DefinitionId == ConversationControlClient.ChatDefinitionId ||
            entry.AcceptedRunId != summary.LatestRunId)
            throw new InvalidDataException("The saved exact workflow conversation has conflicting navigation identity.");
    }

    private async Task<ExactWorkflowConversation?> FindExactWorkflowConversationAsync(string conversationId)
    {
        if (ExactNavigationUnsupportedCurrent()) return null;
        var page = await ReadExactNavigationPageAsync("conversations.workflow.recent",
            new { limit = 1, conversationId });
        if (page is null) return null; // Exact old-Worker unknown-command compatibility only.
        if (page.Conversations.Count > 1 || page.NextBeforeRow is not null || page.SnapshotRow < 0)
            throw new InvalidDataException("The exact workflow lookup returned an invalid bounded page.");
        var entry = page.Conversations.SingleOrDefault();
        if (entry is null) return null;
        ValidateExactWorkflowConversation(entry);
        if (entry.Summary.Id != conversationId)
            throw new InvalidDataException("The exact workflow lookup returned another conversation.");
        return entry;
    }

    private async Task<(ExactWorkflowConversationPage Recent, IReadOnlyList<ExactWorkflowConversation> Protected, bool Available)>
        ReadExactWorkflowNavigationAsync(int pageLimit, bool older, long expectedPage, int expectedConnection)
    {
        if (ExactNavigationUnsupportedCurrent()) return (new([], 0, null), Array.Empty<ExactWorkflowConversation>(), false);
        var arguments = new Dictionary<string, object> { ["limit"] = pageLimit, ["generalOnly"] = true };
        if (older && exactWorkflowConversationPage?.NextBeforeRow is { } before)
        { arguments["beforeRow"] = before; arguments["snapshotRow"] = exactWorkflowConversationPage.SnapshotRow; }
        // Another lane may still have older rows after this one is exhausted.
        var page = older && exactWorkflowConversationPage is { NextBeforeRow: null } exhaustedExact
            ? new ExactWorkflowConversationPage([], exhaustedExact.SnapshotRow, null)
            : await ReadExactNavigationPageAsync("conversations.workflow.recent", arguments);
        if (page is null) return (new([], 0, null), Array.Empty<ExactWorkflowConversation>(), false);
        if (expectedConnection != knowledgeConnectionVersion || expectedPage != durableNavigationVersion)
            return (page, Array.Empty<ExactWorkflowConversation>(), true);
        if (page.Conversations.Count > pageLimit) throw new InvalidDataException("The exact workflow list exceeded its requested page bound.");
        var protectedEntries = new List<ExactWorkflowConversation>();
        long? protectedBefore = null;
        do
        {
            var protectedArguments = new Dictionary<string, object> { ["limit"] = 200, ["snapshotRow"] = page.SnapshotRow };
            if (protectedBefore is { } cursor) protectedArguments["beforeRow"] = cursor;
            var protectedPage = await ReadExactNavigationPageAsync("conversations.workflow.protected", protectedArguments);
            if (protectedPage is null) return (new([], 0, null), Array.Empty<ExactWorkflowConversation>(), false);
            if (expectedConnection != knowledgeConnectionVersion || expectedPage != durableNavigationVersion)
                return (page, Array.Empty<ExactWorkflowConversation>(), true);
            if (protectedPage.Conversations.Count > 200 || protectedPage.SnapshotRow != page.SnapshotRow ||
                protectedPage.NextBeforeRow is { } next && (next < 1 || protectedBefore is { } prior && next >= prior))
                throw new InvalidDataException("The protected exact workflow list returned an invalid page.");
            protectedEntries.AddRange(protectedPage.Conversations);
            protectedBefore = protectedPage.NextBeforeRow;
        }
        while (protectedBefore is not null);
        foreach (var entry in page.Conversations.Concat(protectedEntries)) ValidateExactWorkflowConversation(entry);
        exactWorkflowConversationPage = page;
        exactWorkflowNavigationConnectionIdentity = ServiceConnectionIdentity;
        return (page, protectedEntries, true);
    }
}
