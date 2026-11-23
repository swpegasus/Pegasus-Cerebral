using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

public static class GoalCardPluginTests
{
    public static int Run(string assemblyPath)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        var type = Assembly.LoadFrom(Path.GetFullPath(assemblyPath)).GetType("Cerebral.GoalCardPlugin.GoalCardPlugin", true)!;
        using var plugin = (IDesktopPlugin)Activator.CreateInstance(type)!;
        var host = new Host();
        plugin.Initialize(host);
        Check(plugin.Panels.Count == 0 && plugin.ComposerActions.Count == 0, "Goal feature contributes only the required mount.");
        var mount = ((IDesktopMountPlugin)plugin).MountContributions.OfType<DesktopMountView>().Single();
        Check(mount.Descriptor.Slot == DesktopMountSlots.ComposerAccessories, "Goal uses an existing stable accessory slot.");
        var card = (Expander)mount.CreateView();
        Check(!card.IsExpanded && host.Reads == 0, "Collapsed card does not poll.");
        card.IsExpanded = true;
        var body = (StackPanel)card.Content;
        string[] Texts() => body.Children.OfType<TextBlock>().Select(block => block.Text).ToArray();
        Check(host.Reads == 1 && Texts().Contains("Build café.\nKeep notes."), "Expansion renders exact original Unicode request.");
        Check(Texts().Contains("Later messages and corrections") && Texts().Contains("Keep source files."), "Later instructions remain separately attributed.");
        var correct = body.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(button => Equals(button.Content, "Add goal correction"));
        correct.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(host.Draft.Text == "Unsent text\n\nGoal correction: " && host.Updates == 1 && host.Submissions == 0, "Correction preserves current draft and never submits.");
        host.Reject = true;
        var before = host.Draft;
        correct.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(host.Draft == before && host.Status.Contains("changed"), "Draft compare-and-swap rejection preserves text.");
        host.Reject = false;
        host.Conversation = host.Conversation with { Binding = new("second", "another-chat") };
        correct.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(host.Draft == before && host.Status.Contains("conversation changed", StringComparison.OrdinalIgnoreCase), "Old card cannot mutate another conversation.");
        host.Draft = new(host.Conversation.Binding, 8, "Other unsent text");
        host.Change();
        Check(Texts().Any(text => text.Contains("invalid ownership")), "Wrong conversation response is a visible failure.");
        host.ReplyConversationId = "another-chat";
        host.Change();
        Check(Texts().Contains("Build café.\nKeep notes."), "New conversation can refresh normally.");
        var slow = new TaskCompletionSource<DesktopServiceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Pending = slow.Task;
        host.Change();
        host.Conversation = host.Conversation with { Binding = new("third", null) };
        host.Pending = null;
        host.Change();
        Check(Texts().Single().Contains("first accepted request"), "Unsaved chat clears previous goal immediately.");
        slow.SetResult(host.Result());
        Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Check(!Texts().Contains("Build café.\nKeep notes."), "Late reply cannot populate a different chat.");
        plugin.Dispose();
        var reads = host.Reads;
        host.Change();
        Check(host.Reads == reads, "Dispose removes callbacks.");
        return checks;
    }

    private sealed class Host : IDesktopPluginHost, IDesktopServiceHost
    {
        public DesktopConversationSnapshot Conversation = new(new("first", "goal-chat"), "Goal", null, null, false);
        public DesktopDraftSnapshot Draft = new(new("first", "goal-chat"), 7, "Unsent text");
        public int Reads, Updates, Submissions;
        public bool Reject;
        public string Status = "", ReplyConversationId = "goal-chat";
        public Task<DesktopServiceCommandResult>? Pending;
        public event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged;
        public event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted { add { } remove { } }
        public DesktopConversationSnapshot GetConversation() => Conversation;
        public DesktopDraftSnapshot GetDraft() => Draft;
        public void Change() => ConversationChanged?.Invoke(this, new(Conversation));
        public DesktopServiceCommandResult Result() => new(true, JsonSerializer.SerializeToElement(new
        {
            conversationId = ReplyConversationId, revision = 40,
            original = new { eventId = "original", runId = "run", sequence = 1, text = "Build café.\nKeep notes.", truncated = false, steering = false, createdAt = DateTimeOffset.UtcNow },
            laterInstructions = new[] { new { eventId = "correction", runId = "run", sequence = 10, text = "Keep source files.", truncated = false, steering = true, createdAt = DateTimeOffset.UtcNow } },
            hasOlderInstructions = false
        }));
        public Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation, object arguments, CancellationToken cancellationToken = default)
        {
            if (operation != "conversations.goal") throw new Exception("Unexpected operation.");
            Reads++; return Pending ?? Task.FromResult(Result());
        }
        public Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate update, CancellationToken cancellationToken = default)
        {
            Updates++;
            if (Reject || update.Binding != Draft.Binding || update.ExpectedRevision != Draft.Revision)
                return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "Draft changed."));
            Draft = new(update.Binding, Draft.Revision + 1, update.Text);
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        }
        public Task<DesktopOperationResult> SubmitAsync(DesktopSubmission input, CancellationToken token = default) { Submissions++; throw new Exception("Unexpected submission."); }
        public Task<DesktopOperationResult> SteerAsync(DesktopSubmission input, CancellationToken token = default) => throw new Exception("Unexpected steering.");
        public Task<DesktopOperationResult> StopAsync(DesktopConversationBinding binding, CancellationToken token = default) => throw new Exception("Unexpected stop.");
        public void DismissInputGuidance() { }
        public void ReportStatus(string text) => Status = text;
        public void RefreshPresentation() { }
    }
}
