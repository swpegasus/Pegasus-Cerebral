using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>Controlled plugin delegates on WPF; no shown window, live plugin, Worker or provider.</summary>
    public async Task<int> CheckConversationPluginSettingsAsync()
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run plugin Properties checks on the active WPF dispatcher.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        var context = new DesktopConversationSettingsContext("sidebar-target", "Saved title", "saved-profile",
            "saved-project", @"C:\Projects\SavedTarget", deadline.Token);
        DesktopHostedConversationSettings Contribution(
            Func<DesktopConversationSettingsContext, CancellationToken, Task<IDesktopConversationSettingsEditor>> open) =>
            new("fixture-settings", "draft", "Plugin draft", open);
        try
        {
            var opens = 0;
            DesktopConversationSettingsContext? received = null;
            var first = new PropertiesFixtureEditor();
            var contribution = Contribution((target, _) =>
            {
                opens++; received = target; return Task.FromResult<IDesktopConversationSettingsEditor>(first);
            });
            using (var section = new ConversationPluginSettingsEditor(contribution, context, () => true, deadline.Token))
            {
                Check(opens == 0 && !section.HasEditor && section.OpenButton.IsEnabled, "Properties does not eagerly open plugin settings.");
                await section.OpenAsync();
                Check(opens == 1 && section.HasEditor && !section.OpenButton.IsEnabled &&
                    section.SaveButton.IsEnabled && section.CancelButton.IsEnabled, "Configure opens only its plugin draft.");
                Check(received?.ConversationId == context.ConversationId && received.ProfileId == context.ProfileId &&
                    received.ProjectId == context.ProjectId && received.WorkingDirectory == context.WorkingDirectory,
                    "Plugin receives the exact sidebar target and saved metadata.");
                section.Measure(new Size(360, 500)); section.Arrange(new Rect(0, 0, 360, 500)); section.UpdateLayout();
                Check(section.IsMeasureValid && section.IsArrangeValid, "Plugin settings section lays out without showing a window.");
                await section.OpenAsync();
                Check(opens == 1, "An open editor is not replaced by a repeated Configure.");
                await section.SaveAsync();
                Check(first.Saves == 1 && section.Feedback.Text.Contains("saved", StringComparison.Ordinal) &&
                    section.HasEditor, "Completed persists only the plugin and retains its editor.");
                section.CancelEdits();
                Check(first.Cancels == 1 && first.Disposals == 1 && !section.HasEditor && section.OpenButton.IsEnabled,
                    "Cancel discards the draft and releases the editor, without undoing a completed save.");
            }

            var heldSave = new TaskCompletionSource<DesktopOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var saving = new PropertiesFixtureEditor { Save = _ => heldSave.Task };
            using (var section = new ConversationPluginSettingsEditor(Contribution((_, _) =>
                Task.FromResult<IDesktopConversationSettingsEditor>(saving)), context, () => true, deadline.Token))
            {
                await section.OpenAsync();
                var pending = section.SaveAsync();
                Check(saving.Saves == 1 && !section.SaveButton.IsEnabled && !section.CancelButton.IsEnabled,
                    "An in-flight save disables another mutation and draft cancellation.");
                Check(ReferenceEquals(pending, section.SaveAsync()) && saving.Saves == 1,
                    "Repeated save joins one exact pending operation.");
                heldSave.SetResult(new(DesktopOperationState.Accepted, "Queued by plugin"));
                await pending;
                Check(section.Feedback.Text.Contains("not yet confirmed", StringComparison.Ordinal),
                    "Accepted asynchronous work is not rendered as confirmed persistence.");
                await section.SaveAsync(); section.CancelEdits(); await section.OpenAsync();
                Check(saving.Saves == 1 && !section.OpenButton.IsEnabled && !section.HasEditor,
                    "Accepted asynchronous persistence cannot dispatch another mutation before reopening to inspect state.");
            }

            var uncertain = new PropertiesFixtureEditor
                { Save = _ => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Uncertain, "Lost receipt")) };
            using (var section = new ConversationPluginSettingsEditor(Contribution((_, _) =>
                Task.FromResult<IDesktopConversationSettingsEditor>(uncertain)), context, () => true, deadline.Token))
            {
                await section.OpenAsync(); await section.SaveAsync(); await section.SaveAsync();
                Check(uncertain.Saves == 1 && !section.SaveButton.IsEnabled &&
                    section.Feedback.Text.Contains("unconfirmed", StringComparison.Ordinal), "Uncertain mutation cannot automatically replay.");
                section.CancelEdits(); await section.OpenAsync();
                Check(!section.HasEditor && uncertain.Saves == 1 && !section.OpenButton.IsEnabled,
                    "Discarding a draft does not clear an uncertain mutation's reload requirement.");
            }
            var lost = new PropertiesFixtureEditor
                { Save = _ => Task.FromException<DesktopOperationResult>(new IOException("Fixture lost receipt")) };
            using (var section = new ConversationPluginSettingsEditor(Contribution((_, _) =>
                Task.FromResult<IDesktopConversationSettingsEditor>(lost)), context, () => true, deadline.Token))
            {
                await section.OpenAsync(); await section.SaveAsync(); await section.SaveAsync();
                Check(lost.Saves == 1 && !section.SaveButton.IsEnabled, "Save exception after dispatch retains uncertainty without replay.");
            }
            foreach (var state in new[] { DesktopOperationState.Rejected, DesktopOperationState.Failed })
            {
                var failure = new PropertiesFixtureEditor
                    { Save = _ => Task.FromResult(new DesktopOperationResult(state, "Fixture outcome")) };
                using var section = new ConversationPluginSettingsEditor(Contribution((_, _) =>
                    Task.FromResult<IDesktopConversationSettingsEditor>(failure)), context, () => true, deadline.Token);
                await section.OpenAsync(); await section.SaveAsync();
                Check(section.Feedback.Text.Contains(state == DesktopOperationState.Rejected ? "rejected" : "failed", StringComparison.Ordinal) &&
                    section.HasEditor, "Explicit failure remains visible inside its plugin section.");
            }
            var invalid = new PropertiesFixtureEditor
                { Save = _ => Task.FromResult(new DesktopOperationResult((DesktopOperationState)999)) };
            using (var section = new ConversationPluginSettingsEditor(Contribution((_, _) =>
                Task.FromResult<IDesktopConversationSettingsEditor>(invalid)), context, () => true, deadline.Token))
            {
                await section.OpenAsync(); await section.SaveAsync(); await section.SaveAsync();
                Check(invalid.Saves == 1 && section.Feedback.Text.Contains("unconfirmed", StringComparison.Ordinal),
                    "A malformed save outcome cannot prove success or authorize a retry.");
            }

            var binding = true; var targetCalls = 0;
            var stale = new PropertiesFixtureEditor();
            using (var section = new ConversationPluginSettingsEditor(Contribution((target, _) =>
                { targetCalls++; received = target; return Task.FromResult<IDesktopConversationSettingsEditor>(stale); }),
                context, () => binding, deadline.Token))
            {
                await section.OpenAsync(); binding = false; section.RefreshBinding();
                Check(received!.AvailabilityToken.IsCancellationRequested && !section.SaveButton.IsEnabled,
                    "Binding invalidation cancels the editor's exact availability token.");
                await section.SaveAsync(); binding = true; section.RefreshBinding(); await section.SaveAsync();
                Check(stale.Saves == 0 && targetCalls == 1 && !section.SaveButton.IsEnabled,
                    "Returning to a prior selection cannot revive a stale settings draft.");
            }
            using (var section = new ConversationPluginSettingsEditor(Contribution((_, _) =>
                Task.FromException<IDesktopConversationSettingsEditor>(new IOException("Fixture open failed"))),
                context, () => true, deadline.Token))
            {
                await section.OpenAsync();
                Check(!section.HasEditor && section.Feedback.Text.Contains("could not open", StringComparison.Ordinal) &&
                    section.OpenButton.IsEnabled, "An opening failure is isolated and leaves the draft unopened.");
            }
            var heldOpen = new TaskCompletionSource<IDesktopConversationSettingsEditor>(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new PropertiesFixtureEditor();
            var lateSection = new ConversationPluginSettingsEditor(Contribution((_, _) => heldOpen.Task), context, () => true, deadline.Token);
            var pendingOpen = lateSection.OpenAsync(); lateSection.Dispose();
            Check(late.Disposals == 0 && !lateSection.PendingCleanup.IsCompleted, "Close waits for a pending factory before cleanup.");
            heldOpen.SetResult(late); await pendingOpen; await lateSection.PendingCleanup;
            Check(late.Disposals == 1 && late.Cancels == 1 && !lateSection.HasEditor,
                "A factory that returns after close is discarded without mounting its view.");
            lateSection.Dispose();

            var drain = new TaskCompletionSource<DesktopOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var draining = new PropertiesFixtureEditor { Save = _ => drain.Task };
            var drainingSection = new ConversationPluginSettingsEditor(Contribution((_, _) =>
                Task.FromResult<IDesktopConversationSettingsEditor>(draining)), context, () => true, deadline.Token);
            await drainingSection.OpenAsync(); var pendingSave = drainingSection.SaveAsync(); drainingSection.Dispose();
            Check(draining.Disposals == 0 && !drainingSection.PendingCleanup.IsCompleted,
                "Close cancels availability but does not dispose an editor while its save is running.");
            drain.SetResult(new(DesktopOperationState.Completed, "Saved before cancellation was observed"));
            await pendingSave; await drainingSection.PendingCleanup;
            Check(draining.Disposals == 1 && draining.Cancels == 1,
                "A settled save permits exactly one final editor cleanup.");
            drainingSection.Dispose();

            var cancelFailure = new PropertiesFixtureEditor { ThrowCancel = true };
            using (var section = new ConversationPluginSettingsEditor(Contribution((_, _) =>
                Task.FromResult<IDesktopConversationSettingsEditor>(cancelFailure)), context, () => true, deadline.Token))
            {
                await section.OpenAsync(); section.CancelEdits();
                Check(cancelFailure.Disposals == 1 && section.Feedback.Text.Contains("could not discard", StringComparison.Ordinal),
                    "Cancel failure is visible and still releases the editor.");
            }

            using (var callbackOwner = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                var callbackEditor = new PropertiesFixtureEditor(); var callbacks = 0;
                using var section = new ConversationPluginSettingsEditor(Contribution((target, _) =>
                {
                    target.AvailabilityToken.Register(() => { callbacks++; throw new IOException("Fixture callback failed"); });
                    return Task.FromResult<IDesktopConversationSettingsEditor>(callbackEditor);
                }), context, () => true, callbackOwner.Token);
                await section.OpenAsync();
                callbackOwner.Cancel(); section.RefreshBinding();
                Check(callbacks == 1 && !section.SaveButton.IsEnabled &&
                    section.Feedback.Text.Contains("callback failed", StringComparison.Ordinal),
                    "A throwing plugin callback does not escape into owner cancellation.");
                section.Dispose(); await section.PendingCleanup;
                Check(callbackEditor.Disposals == 1, "Cancellation callback failure cannot prevent editor cleanup.");
            }
            using (var timeout = new ConversationPluginCancellation(TimeSpan.FromMilliseconds(100)))
            {
                _ = timeout.Token.Register(() => throw new IOException("Fixture timer callback failed"));
                while (!timeout.IsCancellationRequested) await Task.Delay(1, deadline.Token);
                // Cancel waits for any active timer cancellation; the recoverable callback is retained.
                timeout.Cancel();
                Check(timeout.CancellationFailure is not null, "A throwing deadline callback is contained rather than escaping the timer.");
            }

            // Exercise the actual Properties mount: core saves and plugin saves remain independent.
            var props = new ConversationPropertiesSnapshot(context.ConversationId, context.Title, context.ProfileId,
                "Saved agent", "lead", context.ProjectId, "bound", context.WorkingDirectory, [], [], [], false,
                "default", "profile", 0, null, null);
            var coreSaves = 0; var pluginOpens = 0;
            var mounted = new PropertiesFixtureEditor();
            using (var dialog = new ConversationPropertiesDialog(props, () => true,
                (_, _, _) => { coreSaves++; return Task.FromResult(CommandResult.Ok(props)); }, deadline.Token))
            {
                dialog.AttachPluginSettings([Contribution((target, _) =>
                    { pluginOpens++; received = target; return Task.FromResult<IDesktopConversationSettingsEditor>(mounted); })]);
                Check(dialog.PluginSettingsEditors.Count == 1 && pluginOpens == 0,
                    "Settings contributes the plugin without an eager factory or response preference command.");
                await dialog.SaveAsync();
                Check(coreSaves == 1 && pluginOpens == 0 && mounted.Saves == 0,
                    "Saving response balance cannot persist any unopened plugin draft.");
                var section = dialog.PluginSettingsEditors.Single();
                await section.OpenAsync(); await section.SaveAsync();
                Check(coreSaves == 1 && pluginOpens == 1 && mounted.Saves == 1 &&
                    received!.ConversationId == context.ConversationId, "Plugin Save is exact-target and independent of the core preference transaction.");
                dialog.Close(); await dialog.PendingOperations;
                Check(mounted.Disposals == 1, "Properties close drains and releases its plugin editors.");
            }
            Console.WriteLine($"CONVERSATION PLUGIN SETTINGS: PASS · {checks} deterministic WPF/delegate checks");
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            Console.Error.WriteLine("CONVERSATION PLUGIN SETTINGS: FAIL · " + error); return 1;
        }
    }

    private sealed class PropertiesFixtureEditor : IDesktopConversationSettingsEditor
    {
        public FrameworkElement View { get; } = new TextBox { Text = "Plugin-owned draft", AcceptsReturn = true };
        internal Func<CancellationToken, Task<DesktopOperationResult>> Save { get; init; } =
            _ => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        internal int Saves, Cancels, Disposals;
        internal bool ThrowCancel { get; init; }
        public Task<DesktopOperationResult> SaveAsync(CancellationToken cancellationToken = default)
            { Saves++; return Save(cancellationToken); }
        public void Cancel()
            { Cancels++; if (ThrowCancel) throw new IOException("Fixture cannot discard"); }
        public void Dispose() => Disposals++;
    }
}
