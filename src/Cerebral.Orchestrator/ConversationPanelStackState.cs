namespace Cerebral.Orchestrator;

/// <summary>Local accordion state: at most one Main/team source is expanded. Transitions never submit work or answer questions.</summary>
internal sealed class ConversationPanelStackState
{
    private string? owner;
    private string? conversation;
    private string? followedTask;
    private string? followedSourceRun;
    private string? followedCoordinator;
    private readonly HashSet<string> returnedAttention = new(StringComparer.Ordinal);
    private string? expandedPanel;

    internal bool MainExpanded { get; private set; } = true;
    internal long SelectionVersion { get; private set; }
    internal bool IsFollowingSourceRun(string runId) => followedSourceRun == runId;

    internal void Reset()
    {
        owner = conversation = followedTask = followedSourceRun = followedCoordinator = null;
        returnedAttention.Clear(); expandedPanel = null; MainExpanded = true; SelectionVersion++;
    }

    internal void Bind(string ownerIdentity, string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (owner == ownerIdentity && conversation == conversationId) return;
        Reset(); owner = ownerIdentity; conversation = conversationId;
    }

    internal bool IsExpanded(string id) => expandedPanel is not null && expandedPanel == id;

    internal void SetExpanded(string id, bool value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (value) { if (expandedPanel != id || MainExpanded) SelectionVersion++; expandedPanel = id; MainExpanded = false; }
        // A docking bar cannot close itself; another positive selection replaces it.
    }

    internal void SetMainExpanded(bool value)
    {
        if (!value) return; // Exactly one docking bar remains selected.
        if (MainExpanded != value || value && expandedPanel is not null) SelectionVersion++;
        MainExpanded = value;
        if (value) expandedPanel = null;
    }

    // A bare Send, a discovered historical task, or an unbound source run is not a handoff.
    internal bool AcceptHandoff(string ownerIdentity, string conversationId, string currentMainRunId,
        string taskId, string? sourceRunId, string coordinatorRunId, bool acceptedCurrentGraph,
        bool preserveMainForDraftOrFocus)
    {
        if (owner != ownerIdentity || conversation != conversationId || !acceptedCurrentGraph ||
            string.IsNullOrWhiteSpace(sourceRunId) || sourceRunId != currentMainRunId ||
            string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(coordinatorRunId)) return false;
        if (followedTask == taskId && followedSourceRun == sourceRunId && followedCoordinator == coordinatorRunId)
            return false; // Background refresh must not undo the reader's own expansion choices.
        followedTask = taskId; followedSourceRun = sourceRunId; followedCoordinator = coordinatorRunId;
        returnedAttention.Clear();
        // An accepted transition opens the coordinator once. Draft presence or
        // residual focus is not manual selection; refreshes still preserve it.
        SetExpanded(coordinatorRunId, true);
        return true;
    }

    internal bool ReturnToMain(string taskId, string sourceRunId, string attentionIdentity)
    {
        if (followedTask != taskId || followedSourceRun != sourceRunId ||
            string.IsNullOrWhiteSpace(attentionIdentity) || !returnedAttention.Add(attentionIdentity)) return false;
        if (returnedAttention.Count > 64) returnedAttention.Remove(returnedAttention.First());
        SetMainExpanded(true);
        return true;
    }

    internal bool ReturnCurrentMain(string sourceRunId, string attentionIdentity) =>
        followedTask is not null && ReturnToMain(followedTask, sourceRunId, attentionIdentity);
}
