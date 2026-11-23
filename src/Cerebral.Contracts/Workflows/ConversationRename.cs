namespace Cerebral.Contracts.Workflows;

/// <summary>Owner presentation metadata only. Neither title nor revision enters agent history or execution state.</summary>
public static class ConversationRenameContract
{
    public const int MaximumTitleScalars = 160;
    public const int MaximumTitleUtf8Bytes = 640;
}
public sealed record ConversationRenameRequest(string RequestId, string DefinitionId, string ConversationId,
    long ExpectedTitleRevision, string Title);
public sealed record ConversationRenameResult(string ConversationId, string Title, long TitleRevision);
