using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Contracts.Collaboration;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private const int CommunicationReviewPageSize = 16;
    private long communicationReviewPageGeneration;

    private sealed record CommunicationReviewOwner(CerebralClient MainClient, object Connection,
        string ConversationId, DesktopConversationBinding Binding, string ConnectionIdentity,
        int ConnectionVersion, long RefreshGeneration);

    private bool CommunicationReviewCurrent(CommunicationReviewOwner owner) =>
        IsConnected && !Lifetime.IsCancellationRequested && ReferenceEquals(owner.MainClient, Client) &&
        ReferenceEquals(owner.Connection, ServiceConnection) && owner.ConversationId == CurrentConversationId &&
        owner.Binding == PluginBinding() && owner.ConnectionIdentity == ServiceConnectionIdentity &&
        owner.ConnectionVersion == knowledgeConnectionVersion && interfaceReviewRefresh.IsCurrent(owner.RefreshGeneration);

    private async Task RefreshConversationCommunicationAsync(long refreshGeneration, string conversationId,
        DesktopConversationBinding binding, string connectionIdentity, int connectionVersion)
    {
        if (!IsConnected || !interfaceReviewRefresh.IsCurrent(refreshGeneration) || CurrentConversationId != conversationId ||
            PluginBinding() != binding || ServiceConnectionIdentity != connectionIdentity || knowledgeConnectionVersion != connectionVersion)
            return;
        var owner = new CommunicationReviewOwner(Client, ServiceConnection, conversationId, binding,
            connectionIdentity, connectionVersion, refreshGeneration);
        var content = new StackPanel();
        var section = new Expander
        {
            Header = "Chat communication permissions", IsExpanded = true, Margin = new Thickness(0, 3, 0, 6),
            Content = new Border { Child = content, Padding = new Thickness(12), BorderThickness = new Thickness(1),
                BorderBrush = Ui.Line, Background = Ui.Surface, CornerRadius = new CornerRadius(5) }
        };
        AutomationProperties.SetAutomationId(section, "ConversationCommunicationReview");
        interfaceRequests.Children.Add(section);
        await LoadConversationCommunicationPageAsync(owner, section, content, null, null);
    }

    private async Task LoadConversationCommunicationPageAsync(CommunicationReviewOwner owner, Expander section, StackPanel content,
        ConversationCommunicationPair? pendingAfterPair, ConversationCommunicationPair? currentAfterPair)
    {
        if (!CommunicationReviewCurrent(owner)) return;
        var pageGeneration = ++communicationReviewPageGeneration;
        bool Current() => CommunicationReviewCurrent(owner) && pageGeneration == communicationReviewPageGeneration;
        section.Visibility = Visibility.Visible;
        content.Children.Clear();
        content.Children.Add(Ui.Text("Loading communication permissions…", 12, Ui.Muted));
        try
        {
            using var captured = CreateIndependentClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            if (!Current()) return;
            var result = await captured.CommandAsync("collaboration.permissions.list", new
                { pendingOnly = true, limit = CommunicationReviewPageSize, afterPair = pendingAfterPair },
                cancellationToken: timeout.Token);
            if (!Current()) return;
            if (!result.Success)
            {
                var unsupported = result.Error == "Unknown command: collaboration.permissions.list";
                ShowCommunicationReviewIssue(owner, section, content, pageGeneration, unsupported
                    ? "This Worker does not support chat communication review. Connect to the matching current Worker, then refresh."
                    : "Communication permissions could not be read. Refresh before making a decision.");
                return;
            }
            var pendingPage = CerebralJson.Read<ConversationCommunicationPage>(result.Data);
            ValidateCommunicationReviewPage(pendingPage, null, pendingAfterPair, pendingOnly: true);
            var currentResult = await captured.CommandAsync("collaboration.permissions.list", new
                { conversationId = owner.ConversationId, limit = CommunicationReviewPageSize, afterPair = currentAfterPair },
                cancellationToken: timeout.Token);
            if (!Current()) return;
            if (!currentResult.Success)
            {
                ShowCommunicationReviewIssue(owner, section, content, pageGeneration,
                    "Communication permissions could not be read. Refresh before making a decision.");
                return;
            }
            var currentPage = CerebralJson.Read<ConversationCommunicationPage>(currentResult.Data);
            ValidateCommunicationReviewPage(currentPage, owner.ConversationId, currentAfterPair);
            if (!Current()) return;
            NoteCommunicationAttention(pendingPage);
            RenderConversationCommunicationPage(owner, section, content, pendingPage, currentPage, pageGeneration,
                pendingAfterPair, currentAfterPair);
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (Current()) ShowCommunicationReviewIssue(owner, section, content, pageGeneration,
                "Communication permissions are unavailable. Check the connection and refresh before making a decision.");
        }
    }

    private void RenderConversationCommunicationPage(CommunicationReviewOwner owner, Expander section, StackPanel content,
        ConversationCommunicationPage pendingPage, ConversationCommunicationPage currentPage, long pageGeneration,
        ConversationCommunicationPair? pendingAfterPair, ConversationCommunicationPair? currentAfterPair)
    {
        if (!CommunicationReviewCurrent(owner) || pageGeneration != communicationReviewPageGeneration) return;
        var permissions = pendingPage.Permissions.Concat(currentPage.Permissions.Where(permission => permission.Allowed))
            .GroupBy(permission => permission.Pair).Select(group => group.MaxBy(permission => permission.Revision)!).ToArray();
        var laterPage = pendingAfterPair is not null || currentAfterPair is not null;
        if (permissions.Length == 0 && pendingPage.NextAfterPair is null && currentPage.NextAfterPair is null && !laterPage)
        {
            content.Children.Clear();
            section.Visibility = Visibility.Collapsed;
            return;
        }
        content.Children.Clear();
        content.Children.Add(Ui.Text("Pending requests across all chats · Allowed pairs for the selected chat", 12, Ui.Muted));
        content.Children.Add(Ui.Text("Permission applies to both chats. It allows communication only; history, files, tools and project access keep their existing permissions.", 12, Ui.Muted));
        if (permissions.Length == 0)
            content.Children.Add(Ui.Text("No pending requests or allowed pairs on this page.", 12, Ui.Muted));
        foreach (var permission in permissions)
        {
            var capturedPermission = permission;
            var pairId = permission.Pair.FirstConversationId + "-" + permission.Pair.SecondConversationId;
            var card = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            card.Children.Add(Ui.Text(CommunicationConversationLabel(permission.Pair.FirstConversationId) + " ↔ " +
                CommunicationConversationLabel(permission.Pair.SecondConversationId), 14));
            card.Children.Add(Ui.Text(permission.PendingRequest is { } pending
                ? "Pending request from " + CommunicationConversationLabel(pending.SourceConversationId)
                : permission.Allowed ? "Communication allowed between these two chats." : "Communication is not allowed between these two chats.",
                12, Ui.Muted));
            if (permission.PendingRequest is not null || permission.Allowed)
            {
                var buttons = new WrapPanel();
                void AddDecision(string label, bool allowed)
                {
                    var button = Ui.Button(label, () => Run(() => DecideConversationCommunicationAsync(
                        owner, section, content, capturedPermission, allowed, pageGeneration)));
                    AutomationProperties.SetAutomationId(button, "Communication" + label + "-" + pairId);
                    buttons.Children.Add(button);
                }
                if (permission.PendingRequest is not null) { AddDecision("Allow", true); AddDecision("Deny", false); }
                else AddDecision("Revoke", false);
                card.Children.Add(buttons);
            }
            content.Children.Add(card);
        }
        var navigation = new WrapPanel();
        var refresh = Ui.Button("Refresh permissions", () => Run(() => LoadConversationCommunicationPageAsync(owner, section, content, null, null)));
        AutomationProperties.SetAutomationId(refresh, "CommunicationRefresh");
        navigation.Children.Add(refresh);
        if (pendingPage.NextAfterPair is { } nextPending)
        {
            var more = Ui.Button("More pending requests", () => Run(() => LoadConversationCommunicationPageAsync(owner, section, content, nextPending, currentAfterPair)));
            AutomationProperties.SetAutomationId(more, "CommunicationMorePending");
            navigation.Children.Add(more);
        }
        if (currentPage.NextAfterPair is { } nextCurrent)
        {
            var more = Ui.Button("More saved pairs for this chat", () => Run(() => LoadConversationCommunicationPageAsync(owner, section, content, pendingAfterPair, nextCurrent)));
            AutomationProperties.SetAutomationId(more, "CommunicationMoreCurrent");
            navigation.Children.Add(more);
        }
        content.Children.Add(navigation);
    }

    private async Task DecideConversationCommunicationAsync(CommunicationReviewOwner owner, Expander section, StackPanel content,
        ConversationCommunicationPermission permission, bool allowed, long pageGeneration)
    {
        bool Current() => CommunicationReviewCurrent(owner) && pageGeneration == communicationReviewPageGeneration;
        if (!Current()) throw new InvalidOperationException("The chat or connection changed. Refresh its permission review before deciding.");
        if (!content.IsEnabled) return;
        content.IsEnabled = false;
        try
        {
            // Capture an independent owner transport before any await. Never dispatch through a replacement Client.
            using var captured = CreateIndependentClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var request = new ConversationCommunicationSetAllowedRequest(permission.Pair, permission.Revision,
                allowed, permission.PendingRequest?.RequestId);
            if (!Current()) throw new InvalidOperationException("The permission review changed before dispatch. Refresh before deciding.");
            var result = await captured.CommandAsync("collaboration.permissions.decide", request, cancellationToken: timeout.Token);
            if (!Current()) return;
            if (!result.Success)
            {
                ShowCommunicationReviewIssue(owner, section, content, pageGeneration,
                    "This decision was not accepted. Refresh to review the current permission and request revision.");
                return;
            }
            var saved = CerebralJson.Read<ConversationCommunicationPermission>(result.Data);
            if (saved.Pair != permission.Pair || saved.Revision != checked(permission.Revision + 1) ||
                saved.Allowed != allowed || saved.PendingRequest is not null)
                throw new InvalidDataException("The permission receipt does not match the displayed decision.");
            if (!Current()) return;
            await RefreshInterfaceRequests();
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (Current()) ShowCommunicationReviewIssue(owner, section, content, pageGeneration,
                "The decision could not be confirmed. Refresh the stored permission before trying another decision.");
        }
        finally { if (Current()) content.IsEnabled = true; }
    }

    private void ShowCommunicationReviewIssue(CommunicationReviewOwner owner, Expander section, StackPanel content,
        long pageGeneration, string message)
    {
        if (!CommunicationReviewCurrent(owner) || pageGeneration != communicationReviewPageGeneration) return;
        content.Children.Clear();
        content.Children.Add(Ui.Text(message, 12, Ui.ErrorText));
        var refresh = Ui.Button("Refresh permissions", () => Run(() => LoadConversationCommunicationPageAsync(owner, section, content, null, null)));
        AutomationProperties.SetAutomationId(refresh, "CommunicationRefresh");
        content.Children.Add(refresh);
    }

    private string CommunicationConversationLabel(string id) =>
        durableConversations.TryGetValue(id, out var summary) && !string.IsNullOrWhiteSpace(summary.Title)
            ? summary.Title.Replace('\r', ' ').Replace('\n', ' ') + " (" + id + ")" : id;

    private static void ValidateCommunicationReviewPage(ConversationCommunicationPage page, string? conversationId,
        ConversationCommunicationPair? afterPair, bool pendingOnly = false)
    {
        if (page is null || page.Permissions is null || page.Permissions.Count > CommunicationReviewPageSize)
            throw new InvalidDataException("Communication permission page exceeds the review bound.");
        var pairs = new HashSet<ConversationCommunicationPair>();
        foreach (var permission in page.Permissions)
        {
            if (permission is null || permission.Pair is not { } pair || !CommunicationReviewIdentifier(pair.FirstConversationId) ||
                !CommunicationReviewIdentifier(pair.SecondConversationId) ||
                StringComparer.Ordinal.Compare(pair.FirstConversationId, pair.SecondConversationId) >= 0 ||
                (conversationId is not null && pair.FirstConversationId != conversationId && pair.SecondConversationId != conversationId) ||
                (pendingOnly && permission.PendingRequest is null) ||
                permission.Revision < 1 || !pairs.Add(pair) || permission.UpdatedAt is null ||
                (permission.PendingRequest is { } pending && (permission.Allowed || !CommunicationReviewIdentifier(pending.RequestId) ||
                    pending.RequestedAt == default ||
                    (pending.SourceConversationId != pair.FirstConversationId && pending.SourceConversationId != pair.SecondConversationId))))
                throw new InvalidDataException("Communication permission page does not match this chat's review.");
        }
        if (page.NextAfterPair is { } next && (page.Permissions.Count == 0 || next != page.Permissions[^1].Pair || next == afterPair))
            throw new InvalidDataException("Communication permission continuation does not match the current page.");
    }

    private static bool CommunicationReviewIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
}
