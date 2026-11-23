using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>Deterministic WPF + owner-command stub checks; no window, Worker, provider, or saved preferences are used.</summary>
    public async Task<int> CheckConversationPropertiesAsync()
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run Properties checks on the active WPF dispatcher.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var checks = await CheckChatReferencesAsync(deadline.Token);
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        void RejectSnapshot(ConversationPropertiesSnapshot value, string label)
        {
            try { ConversationPropertiesSnapshot.Validate(value, "properties-target"); }
            catch (InvalidDataException) { checks++; return; }
            throw new InvalidOperationException(label);
        }
        static IEnumerable<FrameworkElement> GeneralFields(ConversationPropertiesDialog dialog) =>
            ((StackPanel)((ScrollViewer)((TabItem)dialog.PropertyTabs.Items[0]).Content).Content).Children.OfType<FrameworkElement>();
        static string GeneralField(ConversationPropertiesDialog dialog, string name) =>
            GeneralFields(dialog).OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == name).Text;

        var originalLegacy = showingLegacyNavigation;
        const string target = "properties-target";
        var originalSummary = durableConversations.GetValueOrDefault(target);
        var selected = CurrentConversationId;
        var snapshot = new ConversationPropertiesSnapshot(target, "Saved exact title", "saved-lead", "Saved Cerebral", "lead",
            "saved-project", "bound", @"C:\Projects\ExactChat", [@"C:\Projects"], ["filesystem.read", "diagnostics"],
            ["file.read", "fixture.*"], false, "saved", "conversation", 7, DateTimeOffset.UnixEpoch, 30);
        try
        {
            ConversationPropertiesSnapshot.Validate(snapshot, target);
            var closedDialog = new ConversationPropertiesDialog(snapshot, () => true,
                (_, _, _) => Task.FromResult(CommandResult.Ok(snapshot)), deadline.Token);
            closedDialog.Dispose(); closedDialog.Dispose(); closedDialog.Close();
            Check(!Application.Current.Windows.Cast<Window>().Contains(closedDialog),
                "Disposal closes an unshown dialog and later shutdown/close cannot cancel a disposed lifetime.");
            RejectSnapshot(snapshot with { Id = "foreign" }, "A foreign response identity must reject.");
            RejectSnapshot(snapshot with { Root = null }, "A bound root requires its exact path.");
            RejectSnapshot(snapshot with { RootState = "unbound" }, "An unbound root cannot contain a path.");
            RejectSnapshot(snapshot with { ResponseSharePercent = 51 }, "Response balance must stay within the private contract.");
            RejectSnapshot(snapshot with { SettingsSource = "profile" }, "The source label must match the saved override.");
            RejectSnapshot(snapshot with { PropertyState = "default" }, "Default state cannot invent a saved revision.");
            RejectSnapshot(snapshot with { Tools = null! }, "Malformed permission lists must reject.");
            showingLegacyNavigation = false;
            durableConversations[target] = new(target, snapshot.Title, snapshot.ProfileId, snapshot.ProjectId,
                "properties-run", WorkflowStatus.Completed, DateTimeOffset.UnixEpoch);
            var menu = NavigationMenu(new NavigationNode { Id = "properties-navigation", Kind = "conversation", TargetId = target, Title = "Sidebar alias" });
            var entry = menu.Items.OfType<MenuItem>().Single(x => AutomationProperties.GetAutomationId(x) == "ConversationProperties");
            Check(entry.IsEnabled && CurrentConversationId == selected, "Sidebar Properties must bind the saved target without selecting it.");
            Check(ConversationActionsMenu().Items.OfType<MenuItem>().Any(x => AutomationProperties.GetAutomationId(x) == "ConversationProperties"),
                "Header overflow retains the accessible Properties entry.");
            Check(!CanOpenConversationProperties(null), "An absent chat cannot open Properties.");

            var calls = new List<(long Revision, int? Share)>();
            var held = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => true, (revision, share, token) =>
            {
                calls.Add((revision, share)); return held.Task.WaitAsync(token);
            }, deadline.Token))
            {
                Check(dialog.PropertyTabs.Items.OfType<TabItem>().Select(x => x.Header?.ToString()).SequenceEqual(["General", "Settings", "Permissions"]),
                    "Properties exposes exactly the three requested tabs.");
                Check(GeneralField(dialog, "Name") == snapshot.Title && GeneralField(dialog, "Agent") == snapshot.ProfileName &&
                    GeneralField(dialog, "Project") == "Project name unavailable · see Details" && GeneralField(dialog, "Working folder") == snapshot.Root,
                    "General shows exact saved names and a friendly missing project name; its identity is retained in Details.");
                Check(GeneralFields(dialog).OfType<TextBox>().Select(x => AutomationProperties.GetName(x)).SequenceEqual(
                    ["Name", "Agent", "Project", "Working folder", "Chat reference"]),
                    "General leads with the basic saved fields in the requested order.");
                var details = GeneralFields(dialog).OfType<Expander>().Single();
                Check(!details.IsExpanded && AutomationProperties.GetAutomationId(details) == "ConversationPropertiesGeneralDetails" &&
                    ((StackPanel)details.Content).Children.OfType<TextBox>().Any(x => x.Text == target) &&
                    ((StackPanel)details.Content).Children.OfType<TextBox>().Any(x => x.Text == snapshot.ProjectId),
                    "Technical identity and revisions stay available in collapsed Details.");
                Check(!dialog.CopyReferenceButton.IsEnabled && GeneralField(dialog, "Chat reference").Contains("non-GUID", StringComparison.Ordinal),
                    "A saved opaque ID never receives an invented GUID reference.");
                Check(dialog.SaveButton.Visibility == Visibility.Collapsed, "General does not offer a response-balance Save action.");
                dialog.PropertyTabs.SelectedIndex = 1;
                Check(dialog.SaveButton.Visibility == Visibility.Visible, "Settings exposes its prominent response-balance Save action.");
                dialog.PropertyTabs.SelectedIndex = 2;
                Check(dialog.SaveButton.Visibility == Visibility.Collapsed, "Permissions preserves its separate controls without the settings Save action.");
                dialog.PropertyTabs.SelectedIndex = 1;
                Check(GeneralFields(dialog).OfType<TextBox>().Where(x => x != dialog.WorkingDirectoryInput).All(x => x.IsReadOnly),
                    "General identity fields remain read-only while the working folder has its own owner save.");
                var content = (FrameworkElement)dialog.Content;
                content.Measure(new Size(720, 540)); content.Arrange(new Rect(0, 0, 720, 540)); content.UpdateLayout();
                Check(content.IsMeasureValid && content.IsArrangeValid && dialog.PropertyTabs.Items.OfType<TabItem>().All(x => x.Content is ScrollViewer),
                    "Bounded tab pages retain scrollable themed content without showing a window.");
                dialog.AutomaticResponse.IsChecked = false; dialog.ResponseShare.SelectedItem = 17;
                var pending = dialog.SaveAsync();
                Check(calls.SequenceEqual([(7L, (int?)17)]) && !dialog.SaveButton.IsEnabled,
                    "Save sends only the reviewed exact revision and share and disables duplicate dispatch.");
                var duplicate = dialog.SaveAsync();
                Check(ReferenceEquals(pending, duplicate) && calls.Count == 1, "A second save joins the pending operation without another command.");
                held.SetResult(CommandResult.Ok(snapshot with { Revision = 8, ResponseSharePercent = 17, UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) }));
                await pending;
                Check(dialog.Accepted?.Id == target && dialog.Accepted.ResponseSharePercent == 17 && dialog.Accepted.Revision == 8,
                    "A matching CAS receipt is accepted for the exact targeted chat.");
                await dialog.SaveAsync();
                Check(calls.Count == 1, "A completed dialog cannot publish another mutation.");
            }

            var guidId = "67af96c33bcd4e1b9fe04fdc8d3ad6ae";
            using (var dialog = new ConversationPropertiesDialog(snapshot with { Id = guidId }, () => true,
                (_, _, _) => Task.FromResult(CommandResult.Ok(snapshot)), deadline.Token, "Known project name"))
            {
                Check(GeneralField(dialog, "Chat reference") == "cerebral://67af96c3-3bcd-4e1b-9fe0-4fdc8d3ad6ae" &&
                    dialog.CopyReferenceButton.IsEnabled && GeneralField(dialog, "Project") == "Known project name",
                    "GUID chats expose the canonical reference and a readily available project name.");
            }

            var pathCalls = new List<(long Revision, string? Path)>();
            var pathReply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var moved = @"D:\Named work\Moved project";
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => true,
                (_, _, _) => throw new InvalidOperationException("A folder edit must not dispatch response balance."), deadline.Token,
                saveWorkingDirectory: (revision, directory, token) =>
                { pathCalls.Add((revision, directory)); return pathReply.Task.WaitAsync(token); }))
            {
                ConversationPathGrantsEditor? paths = null;
                dialog.AttachPathEditor(token => paths = new ConversationPathGrantsEditor(snapshot.Id, snapshot.ProfileId,
                    snapshot.ProjectId, snapshot.RootState, snapshot.Root, () => dialog.ChildSettingsBindingCurrent,
                    (_, _) => Task.FromResult(CerebralJson.Element(new ConversationPathSnapshot(snapshot.Id, snapshot.ProfileId,
                        snapshot.ProjectId, snapshot.RootState, snapshot.Root, 0, null, [],
                        ConversationPathSnapshot.Effective(snapshot.Root, [])))),
                    (_, _, _, _) => throw new InvalidOperationException("Folder changes must not save permission drafts."),
                    token, dialog.EditWorkingDirectory));
                var pathEditor = paths ?? throw new InvalidOperationException("The Properties path editor was not attached.");
                await pathEditor.LoadAsync(); pathEditor.AddDraft(@"C:\Reference", "read");
                dialog.PropertyTabs.SelectedIndex = 2;
                pathEditor.EditWorkingDirectoryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(dialog.PropertyTabs.SelectedIndex == 0 && pathEditor.DraftRows.Single().PathInput.Text == @"C:\Reference" && pathCalls.Count == 0,
                    "Permissions links to the same General folder editor without saving or replacing its independent permission draft.");
                Check(!dialog.WorkingDirectoryInput.IsReadOnly && dialog.WorkingDirectoryInput.Text == snapshot.Root &&
                    !dialog.SaveWorkingDirectoryButton.IsEnabled,
                    "The saved folder is editable, with no unsolicited save or folder selection on opening Properties.");
                dialog.WorkingDirectoryInput.Text = "relative-folder";
                await dialog.SaveWorkingDirectoryAsync();
                Check(pathCalls.Count == 0 && dialog.Feedback.Text.Contains("absolute", StringComparison.Ordinal),
                    "A relative working folder rejects before the owner command without creating anything.");
                dialog.AutomaticResponse.IsChecked = false; dialog.ResponseShare.SelectedItem = 17;
                dialog.WorkingDirectoryInput.Text = moved + "\\";
                var pending = dialog.SaveWorkingDirectoryAsync();
                Check(pathCalls.SequenceEqual([(7L, (string?)moved)]) && !dialog.SaveWorkingDirectoryButton.IsEnabled &&
                    !dialog.ChildSettingsBindingCurrent && !pathEditor.AddButton.IsEnabled &&
                    pathEditor.DraftRows.Single().PathInput.Text == @"C:\Reference",
                    "Only the explicit named canonical location and exact properties revision are sent; stale child contexts stop.");
                Check(ReferenceEquals(pending, dialog.SaveWorkingDirectoryAsync()) && pathCalls.Count == 1,
                    "A repeated folder Save joins the pending command and never dispatches a duplicate.");
                pathReply.SetResult(CommandResult.Ok(snapshot with { Root = moved, Revision = 8,
                    UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) }));
                await pending;
                Check(dialog.Accepted?.Root == moved && dialog.Accepted.ResponseSharePercent == snapshot.ResponseSharePercent &&
                    dialog.Feedback.Text.Contains("paused work was not retried", StringComparison.Ordinal),
                    "The matching path receipt preserves the separate response draft and states that only new work uses it.");
                Check((int?)dialog.ResponseShare.SelectedItem == 17 && !dialog.SaveButton.IsEnabled,
                    "Independent unsaved response choices remain visible, and the old properties revision cannot be reused.");
            }
            var clearedCalls = 0;
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => true,
                (_, _, _) => throw new InvalidOperationException("Unexpected response save."), deadline.Token,
                saveWorkingDirectory: (revision, directory, _) =>
                {
                    clearedCalls++;
                    Check(revision == 7 && directory is null, "Clearing sends explicit null under the exact revision.");
                    return Task.FromResult(CommandResult.Ok(snapshot with { Root = null, RootState = "unbound", Revision = 8,
                        UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) }));
                }))
            {
                dialog.WorkingDirectoryInput.Text = "";
                await dialog.SaveWorkingDirectoryAsync();
                Check(clearedCalls == 1 && dialog.Accepted is { Root: null, RootState: "unbound" },
                    "A confirmed clear remains unbound and creates no replacement folder name.");
            }
            var uncertainPathCalls = 0;
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => true,
                (_, _, _) => throw new InvalidOperationException("Unexpected response save."), deadline.Token,
                saveWorkingDirectory: (_, _, _) =>
                { uncertainPathCalls++; return Task.FromException<CommandResult>(new IOException("Fixture lost folder reply")); }))
            {
                dialog.WorkingDirectoryInput.Text = moved;
                await dialog.SaveWorkingDirectoryAsync(); await dialog.SaveWorkingDirectoryAsync();
                Check(uncertainPathCalls == 1 && dialog.Accepted is null && !dialog.SaveWorkingDirectoryButton.IsEnabled &&
                    dialog.WorkingDirectoryInput.Text == moved && dialog.Feedback.Text.Contains("unconfirmed", StringComparison.Ordinal),
                    "An uncertain path save keeps its draft and cannot retry, resume or retarget accepted work.");
            }
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => true,
                (_, _, _) => Task.FromResult(CommandResult.Ok(snapshot with { Revision = 8, ResponseSharePercent = 17,
                    UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) })), deadline.Token,
                saveWorkingDirectory: (_, _, _) => throw new InvalidOperationException("Response-only saves must omit the folder change.")))
            {
                dialog.WorkingDirectoryInput.Text = moved;
                dialog.AutomaticResponse.IsChecked = false; dialog.ResponseShare.SelectedItem = 17;
                await dialog.SaveAsync();
                Check(dialog.Accepted?.Root == snapshot.Root && dialog.WorkingDirectoryInput.Text == moved,
                    "Saving response balance preserves the effective working folder and the independent unsent folder draft.");
            }
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => true,
                (_, _, _) => throw new InvalidOperationException("Unexpected response save."), deadline.Token,
                saveWorkingDirectory: (_, _, _) => Task.FromResult(CommandResult.Ok(snapshot with { Revision = 8,
                    UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) }))))
            {
                dialog.WorkingDirectoryInput.Text = moved;
                await dialog.SaveWorkingDirectoryAsync();
                Check(dialog.Accepted is null && !dialog.SaveWorkingDirectoryButton.IsEnabled &&
                    dialog.Feedback.Text.Contains("unconfirmed", StringComparison.Ordinal),
                    "A receipt for another effective folder cannot confirm this save or enable a retry.");
            }

            var automaticCalls = 0;
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => true, (revision, share, _) =>
            {
                automaticCalls++;
                Check(revision == 7 && share is null, "Automatic response sends an explicit null override.");
                return Task.FromResult(CommandResult.Ok(snapshot with { Revision = 8, ResponseSharePercent = null,
                    SettingsSource = "profile", UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) }));
            }, deadline.Token))
            {
                dialog.AutomaticResponse.IsChecked = true;
                Check(!dialog.ResponseShare.IsEnabled, "Automatic response disables the percentage picker.");
                await dialog.SaveAsync();
                Check(automaticCalls == 1 && dialog.Accepted?.ResponseSharePercent is null && dialog.Accepted?.SettingsSource == "profile",
                    "A matching reset receipt inherits the profile.");
            }
            var defaults = snapshot with { PropertyState = "default", SettingsSource = "profile", Revision = 0, UpdatedAt = null, ResponseSharePercent = null };
            using (var dialog = new ConversationPropertiesDialog(defaults, () => true, (_, _, _) => Task.FromResult(CommandResult.Ok(defaults)), deadline.Token))
            {
                await dialog.SaveAsync();
                Check(dialog.Accepted?.Revision == 0, "An unchanged profile default accepts the backend's no-op revision.");
            }
            var binding = true; var staleCalls = 0;
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => binding, (_, _, _) =>
                { staleCalls++; return Task.FromResult(CommandResult.Ok(snapshot)); }, deadline.Token))
            {
                binding = false; dialog.RefreshControls();
                Check(!dialog.SaveButton.IsEnabled, "A changed connection or selection disables Save.");
                await dialog.SaveAsync();
                binding = true; dialog.RefreshControls(); await dialog.SaveAsync();
                Check(staleCalls == 0 && dialog.Accepted is null && !dialog.SaveButton.IsEnabled,
                    "Invalidation is sticky; reconnecting cannot reuse an old modal binding.");
            }
            var lostCalls = 0;
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => true, (_, _, _) =>
                { lostCalls++; return Task.FromException<CommandResult>(new IOException("Fixture lost reply")); }, deadline.Token))
            {
                await dialog.SaveAsync(); await dialog.SaveAsync();
                Check(lostCalls == 1 && dialog.Accepted is null && !dialog.SaveButton.IsEnabled &&
                    dialog.Feedback.Text.Contains("unconfirmed", StringComparison.Ordinal),
                    "A lost reply preserves uncertainty and cannot automatically replay.");
            }
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => true,
                (_, _, _) => Task.FromResult(CommandResult.Fail("Fixture stale revision")), deadline.Token))
            {
                await dialog.SaveAsync();
                Check(dialog.Accepted is null && !dialog.SaveButton.IsEnabled &&
                    dialog.Feedback.Text.Contains("rejected", StringComparison.Ordinal),
                    "An explicit CAS rejection requires a fresh read before another save.");
            }
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => true,
                (_, _, _) => Task.FromResult(CommandResult.Ok(snapshot with { Id = "foreign" })), deadline.Token))
            {
                await dialog.SaveAsync();
                Check(dialog.Accepted is null && !dialog.SaveButton.IsEnabled &&
                    dialog.Feedback.Text.Contains("unconfirmed", StringComparison.Ordinal),
                    "A malformed save receipt cannot publish local preferences or authorize a retry.");
            }
            using var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => true, async (_, _, token) =>
            {
                started.SetResult(); await Task.Delay(Timeout.Infinite, token); return CommandResult.Ok(snapshot);
            }, ownerCancellation.Token))
            {
                var pending = dialog.SaveAsync();
                await started.Task.WaitAsync(deadline.Token); ownerCancellation.Cancel(); await pending;
                Check(dialog.Accepted is null && !dialog.SaveButton.IsEnabled &&
                    dialog.Feedback.Text.Contains("unconfirmed", StringComparison.Ordinal),
                    "Cancellation after dispatch retains the uncertain outcome and releases the pending save.");
            }
            var afterDispatchBinding = true;
            var afterDispatch = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => afterDispatchBinding,
                (_, _, _) => afterDispatch.Task, deadline.Token))
            {
                dialog.AutomaticResponse.IsChecked = false; dialog.ResponseShare.SelectedItem = 17;
                var pending = dialog.SaveAsync();
                afterDispatchBinding = false;
                afterDispatch.SetResult(CommandResult.Ok(snapshot with { Revision = 8, ResponseSharePercent = 17,
                    UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) }));
                await pending;
                Check(dialog.Accepted is null && !dialog.SaveButton.IsEnabled &&
                    dialog.Feedback.Text.Contains("unconfirmed", StringComparison.Ordinal),
                    "A reply after binding changed cannot adopt a stale core preference snapshot.");
            }
            using (var dialog = new ConversationPropertiesDialog(snapshot, () => true,
                (_, _, _) => Task.FromResult(CommandResult.Fail(new string('x', 3_000))), deadline.Token))
            {
                await dialog.SaveAsync();
                Check(dialog.Feedback.Text.Length == 2_000 && dialog.Feedback.Text.EndsWith("…", StringComparison.Ordinal),
                    "Core command failure feedback remains bounded without hiding its rejected status.");
            }
            Console.WriteLine($"CONVERSATION PROPERTIES: PASS · {checks} deterministic WPF/transport checks");
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            Console.Error.WriteLine("CONVERSATION PROPERTIES: FAIL · " + error);
            return 1;
        }
        finally
        {
            showingLegacyNavigation = originalLegacy;
            if (originalSummary is null) durableConversations.Remove(target); else durableConversations[target] = originalSummary;
        }
    }
}
