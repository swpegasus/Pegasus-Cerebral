using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly TextBlock projectKnowledgeSummary = Ui.Text("General conversation · Cortex: no project selected", 12, Ui.Muted);
    private readonly Dictionary<string, ProjectKnowledgeSettings> projectKnowledge = new(StringComparer.Ordinal);
    private readonly HashSet<string> expandedKnowledgeProposals = new(StringComparer.Ordinal);
    private string? draftProjectId;
    private int knowledgeConnectionVersion;

    private string? ConversationProjectId => session is null ? draftProjectId : session.ProjectId;

    private UIElement ProjectKnowledgeView()
    {
        AutomationProperties.SetAutomationId(projectKnowledgeSummary, "ConversationProjectKnowledge");
        AutomationProperties.SetName(projectKnowledgeSummary, "Conversation project and knowledge configuration");
        projectKnowledgeSummary.TextWrapping = TextWrapping.NoWrap;
        projectKnowledgeSummary.TextTrimming = TextTrimming.CharacterEllipsis;
        projectKnowledgeSummary.Margin = new Thickness(0);
        return projectKnowledgeSummary;
    }

    private string ProjectTitle(string projectId) =>
        navigation.Nodes.FirstOrDefault(x => x.Kind == "project" && x.Id == projectId)?.Title ?? "Unavailable project";

    private void UpdateProjectKnowledgeSummary()
    {
        if (ConversationProjectId is not { } projectId)
        {
            projectKnowledgeSummary.Text = "General conversation · Cortex: no project selected"; return;
        }
        var title = ProjectTitle(projectId);
        if (!projectKnowledge.TryGetValue(projectId, out var settings))
        {
            projectKnowledgeSummary.Text = title + " · Knowledge configuration not loaded"; return;
        }
        var configured = !string.IsNullOrWhiteSpace(settings.CortexServerId);
        projectKnowledgeSummary.Text = title + (configured ? " · Cortex: configured" : " · Cortex: not configured") +
            (configured ? settings.AutomaticRetrieval ? " · automatic retrieval" : " · retrieval on request" : "") +
            (settings.RequireKnowledgeApproval ? " · knowledge changes require approval" : " · knowledge approval disabled");
    }

    private async Task RefreshProjectKnowledgeAsync(bool force = false)
    {
        var projectId = ConversationProjectId;
        UpdateProjectKnowledgeSummary();
        if (!IsConnected || projectId is null || !force && projectKnowledge.ContainsKey(projectId)) return;
        var connectionVersion = knowledgeConnectionVersion;
        try
        {
            var settings = await Get<ProjectKnowledgeSettings>("projects.knowledge.get", new { projectId });
            if (connectionVersion != knowledgeConnectionVersion) return;
            projectKnowledge[projectId] = settings;
            if (ConversationProjectId == projectId) UpdateProjectKnowledgeSummary();
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (connectionVersion != knowledgeConnectionVersion || ConversationProjectId != projectId) return;
            projectKnowledgeSummary.Text = ProjectTitle(projectId) + " · Knowledge configuration unavailable";
            SetStatus("Could not read project knowledge configuration: " + ex.Message, true);
        }
    }

    private void ResetProjectKnowledge()
    {
        knowledgeConnectionVersion++;
        projectKnowledge.Clear(); expandedKnowledgeProposals.Clear(); draftProjectId = null;
        UpdateProjectKnowledgeSummary();
    }

    private string? ProjectForNewConversation()
    {
        var all = EffectiveNavigation();
        var node = all.FirstOrDefault(x => x.Id == selectedNavigationId);
        // A sidebar move is organization only. A conversation inherits its saved knowledge binding.
        if (node?.Kind == "conversation")
            return node.TargetId == CurrentConversationId ? ConversationProjectId :
                navigationSessions.FirstOrDefault(x => x.Id == node.TargetId)?.ProjectId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (node is not null && visited.Add(node.Id))
        {
            if (node.Kind == "project") return node.Id;
            node = all.FirstOrDefault(x => x.Id == node.ParentId);
        }
        return ConversationProjectId;
    }

    private async Task ChangeConversationProjectAsync(string? targetSessionId = null)
    {
        var connectionVersion = knowledgeConnectionVersion;
        targetSessionId ??= CurrentConversationId;
        var durableTarget = targetSessionId is not null && (targetSessionId == durableConversationId || durableConversations.ContainsKey(targetSessionId));
        if (durableTarget && targetSessionId != durableConversationId) throw new InvalidOperationException("Open this durable conversation before changing its next-message project.");
        if (durableTarget && (NativeWorkActive || NativeMutationUncertain || NativeMutationPending)) throw new InvalidOperationException("Finish or stop the current work, and confirm its outcome, before changing project scope.");
        if (pendingSubmission is not null && pendingSubmission.SessionId == targetSessionId)
            throw new InvalidOperationException("Retry the outstanding submission before changing its project so its accepted state can be reconciled.");
        var target = targetSessionId is null || durableTarget ? null : await Get<TaskSession>("sessions.get", new { id = targetSessionId });
        if (target is not null)
        {
            await RefreshJobs();
            var related = activeJobs.Where(x => x.SessionId == target.Id).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            bool added;
            do
            {
                added = false;
                foreach (var job in activeJobs)
                    if (job.ParentJobId is { } parentId && related.Contains(parentId)) added |= related.Add(job.Id);
            } while (added);
            if (activeJobs.Any(x => related.Contains(x.Id) && Active(x)))
                throw new InvalidOperationException("Finish or stop this conversation's work before changing its project.");
        }
        var oldProjectId = target is null ? draftProjectId : target.ProjectId;
        var choices = new List<(string Label, string? Id)> { ("General conversation · no project", null) };
        foreach (var project in navigation.Nodes.Where(x => x.Kind == "project").OrderBy(x => x.Title))
            choices.Add((project.Title + " · " + project.Id[..Math.Min(8, project.Id.Length)], project.Id));
        var picker = new ComboBox
        {
            ItemsSource = choices.Select(x => x.Label).ToList(),
            SelectedIndex = Math.Max(0, choices.FindIndex(x => x.Id == oldProjectId)),
            Margin = new Thickness(0, 10, 0, 16)
        };
        AutomationProperties.SetAutomationId(picker, "ConversationProjectChoice");
        AutomationProperties.SetName(picker, "Knowledge project");
        var dialog = new Window
        {
            Owner = this, Icon = Icon, Title = "Change conversation project", Width = 590, Height = 290,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize
        };
        var body = new StackPanel { Margin = new Thickness(22) };
        body.Children.Add(Ui.Text("Change conversation project", 22));
        body.Children.Add(Ui.Text("The selected project's knowledge settings apply to future work. Existing conversation history stays in this conversation. Its sidebar entry moves into the selected project.", 13, Ui.Muted));
        body.Children.Add(picker);
        var save = Ui.Button("Change project", () => { dialog.DialogResult = true; }, true);
        AutomationProperties.SetAutomationId(save, "ConfirmConversationProject");
        body.Children.Add(Ui.Bar(save, Ui.Button("Cancel", dialog.Close)));
        dialog.Content = body;
        if (connectionVersion != knowledgeConnectionVersion) throw new InvalidOperationException("Connection changed. Reopen Change project on the current connection.");
        if (dialog.ShowDialog() != true) return;
        if (connectionVersion != knowledgeConnectionVersion) throw new InvalidOperationException("Connection changed. Reopen Change project on the current connection.");
        var projectId = choices[picker.SelectedIndex].Id;
        if (projectId == oldProjectId) return;
        if (target is null)
        {
            draftProjectId = projectId;
            selectedNavigationId = CurrentConversationId is { } current ? NavigationIdForConversation(current) : null;
            pendingSubmission = null; RenderNavigation();
        }
        else
        {
            // The service atomically saves the binding and sidebar parent; a second navigation save would race it.
            var updated = await Get<TaskSession>("sessions.project", new { id = target.Id, projectId });
            if (session?.Id == target.Id) { session = updated; pendingSubmission = null; }
            await RefreshSessions();
        }
        await RefreshProjectKnowledgeAsync();
        SetStatus("Conversation project changed. Future work uses its configured knowledge access.");
    }

    private static string QuestionHeading(UserQuestion question)
    {
        if (question.KnowledgeProposalId is not null)
            return question.Answer switch
            {
                null => "KNOWLEDGE CHANGE · APPROVAL REQUIRED",
                "Approve this knowledge change" => "KNOWLEDGE APPROVAL RECORDED",
                "Reject this change" => "KNOWLEDGE CHANGE REJECTED",
                _ => "KNOWLEDGE REVISION REQUESTED"
            };
        return question.Assumed ? "ASSUMPTION USED" : question.Answer is not null ? "ANSWERED" : question.Required ? "ANSWER REQUIRED" : "OPTIONAL QUESTION";
    }

    private void AddQuestionReason(StackPanel card, UserQuestion question)
    {
        if (question.KnowledgeProposalId is null)
        {
            if (!string.IsNullOrWhiteSpace(question.Reason)) card.Children.Add(Ui.Text(question.Reason, 12, Ui.Muted));
            return;
        }
        if (!string.IsNullOrWhiteSpace(question.Reason))
        {
            var preview = new Expander
            {
                Header = "Proposed change", IsExpanded = expandedKnowledgeProposals.Contains(question.Id),
                Margin = new Thickness(0, 5, 0, 9),
                Content = MarkdownView.Render(question.Reason, error: error => SetStatus(error, true))
            };
            AutomationProperties.SetAutomationId(preview, "KnowledgeProposal-" + question.Id);
            AutomationProperties.SetName(preview, "Review the proposed knowledge change");
            preview.Expanded += (_, _) => expandedKnowledgeProposals.Add(question.Id);
            preview.Collapsed += (_, _) => expandedKnowledgeProposals.Remove(question.Id);
            card.Children.Add(preview);
        }
        card.Children.Add(Ui.Text(question.Answer is null
            ? "Review the proposed change before approving. Only “Approve this knowledge change” authorizes this exact change. Any other custom answer requests a revision."
            : "This records your decision. The service reports the actual knowledge operation and its outcome in Activity.", 12, Ui.Muted));
    }
}
