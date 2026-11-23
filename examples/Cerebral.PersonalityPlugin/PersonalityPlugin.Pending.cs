using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Desktop.Plugins;

namespace Cerebral.PersonalityPlugin;
public sealed partial class PersonalityPlugin
{
    private FrameworkElement CreatePendingView(Func<Task> refreshNormal)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
        panel.Children.Add(Text("Imported preferences", 19));
        panel.Children.Add(Text("Review one retained import before applying it. Preparation may retain its style source, but leaves saved preferences and all imports unchanged. Apply replaces the saved preferences with the reviewed result and removes only the selected import.", 13));
        var selector = new ComboBox { DisplayMemberPath = nameof(PersonalityPendingInput.Label), SelectedValuePath = nameof(PersonalityPendingInput.Id),
            MinWidth = 280, Margin = new Thickness(0, 0, 0, 8), IsEnabled = false };
        var review = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 100, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 8) };
        Button Action(string label) => new() { Content = label, Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cleanup = Action("Remove previous style section"); var reload = Action("Reload imports"); var prepare = Action("Review / prepare"); var apply = Action("Apply selected import");
        var status = Text("", 12, DesktopThemeResourceKeys.MutedTextBrush);
        var buttons = new WrapPanel(); buttons.Children.Add(reload); buttons.Children.Add(prepare); buttons.Children.Add(apply); buttons.Children.Add(cleanup);
        foreach (var (view, id) in new (FrameworkElement, string)[] {
            (selector, "PersonalityPending"), (review, "PersonalityPendingReview"), (reload, "PersonalityPendingReload"),
            (prepare, "PersonalityPendingPrepare"), (apply, "PersonalityPendingApply"), (status, "PersonalityPendingStatus"), (cleanup, "PersonalityPendingCleanup") })
            AutomationProperties.SetAutomationId(view, id);
        panel.Children.Add(selector); panel.Children.Add(review); panel.Children.Add(buttons); panel.Children.Add(status);
        PersonalityPendingSnapshot? snapshot = null; PersonalityPendingPreparation? prepared = null;
        CancellationTokenSource? lifetime = null; int generation = 0; bool presenting = false;
        PersonalityEditor Editor() => new(service!, store!, () => ownerHost!.GetConversation().Binding);
        void Status(string message, bool error = false)
        {
            status.Text = Bounded(message);
            status.SetResourceReference(TextBlock.ForegroundProperty, error ? DesktopThemeResourceKeys.ErrorTextBrush : DesktopThemeResourceKeys.MutedTextBrush);
        }
        void Ready(bool ready)
        {
            reload.IsEnabled = lifetime is not null && !disposed;
            selector.IsEnabled = ready && snapshot?.Inputs.Count > 0;
            prepare.IsEnabled = selector.IsEnabled && selector.SelectedValue is string;
            apply.IsEnabled = ready && prepared is not null;
            cleanup.IsEnabled = ready && snapshot is not null &&
                (snapshot.Lead.Content.Contains(PersonalityInstructionLayer.StartMarker, StringComparison.Ordinal) ||
                 snapshot.Lead.Content.Contains(PersonalityInstructionLayer.EndMarker, StringComparison.Ordinal));
        }
        void Present(PersonalityPendingSnapshot loaded)
        {
            presenting = true; snapshot = loaded; prepared = null; review.Text = "";
            selector.ItemsSource = loaded.Inputs; selector.SelectedIndex = loaded.Inputs.Count > 0 ? 0 : -1;
            presenting = false; Ready(true);
        }
        async Task Reload()
        {
            if (lifetime is null) return;
            var token = lifetime.Token; var version = ++generation;
            snapshot = null; prepared = null; Ready(false); reload.IsEnabled = false; Status("Loading retained imports…");
            try
            {
                if (service is null || store is null || ownerHost is null) throw new NotSupportedException("This host does not expose imported personality preferences.");
                var loaded = await Editor().LoadPendingAsync(token);
                if (version != generation || token.IsCancellationRequested) return;
                Present(loaded); Status(loaded.Inputs.Count == 0 ? "No retained imports." : "Select an import and review its prepared result before applying.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (version != generation) return;
                snapshot = null; prepared = null; Ready(false); Status(error.Message, true);
            }
        }
        selector.SelectionChanged += (_, _) => {
            if (presenting) return;
            generation++; prepared = null; review.Text = ""; Ready(snapshot is not null);
            Status("Review the selected import before applying it.");
        };
        reload.Click += async (_, _) => await Reload();
        prepare.Click += async (_, _) => {
            if (snapshot is null || selector.SelectedValue is not string id || lifetime is null) return;
            var baseline = snapshot; var token = lifetime.Token; var version = ++generation;
            prepared = null; Ready(false); reload.IsEnabled = false; Status("Preparing the selected import for review…");
            try
            {
                var result = await Editor().PreparePendingAsync(baseline, id, token);
                if (version != generation || token.IsCancellationRequested) return;
                prepared = result;
                snapshot = new(result.Snapshot.Transfer, result.Snapshot.Inputs, result.Sources,
                    result.Snapshot.Lead, result.Snapshot.Legacy, result.Snapshot.Owner);
                var values = result.Values;
                var lines = new List<string> {
                    result.Input.Label, "Style enabled: " + (values.Enabled ? "Yes" : "No"),
                    "Selected style: " + (result.Preset?.Name ?? values.SelectedPresetId + " (definition unavailable)"),
                    "Current custom styles after Apply: " + values.CustomPresets.Count,
                    "", "Selected style text:", result.Preset?.Text ?? "(No prepared source.)" };
                foreach (var item in values.CustomPresets) {
                    lines.Add(""); lines.Add("Custom style: " + item.Name + " (" + item.Id + ")"); lines.Add(item.Text);
                }
                review.Text = string.Join(System.Environment.NewLine, lines); Ready(true);
                Status("Prepared for review. Apply explicitly stores this full result. Existing work keeps its saved style.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (version != generation) return;
                snapshot = null; prepared = null; Ready(false);
                Status(error.Message + " Reload imports to inspect actual state before another preparation.", true);
            }
        };
        apply.Click += async (_, _) => {
            if (prepared is null || lifetime is null) return;
            var baseline = prepared; var token = lifetime.Token; var version = ++generation;
            prepared = null; snapshot = null; Ready(false); reload.IsEnabled = false;
            Status("Applying the reviewed import and reconciling any recognized previous style section…");
            try
            {
                var result = await Editor().ApplyPendingAsync(baseline, token);
                if (version != generation || token.IsCancellationRequested) return;
                Present(result); Status("Applied the reviewed preferences. Only the selected import was removed. Existing work keeps its saved style.");
                await refreshNormal();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (version != generation) return;
                snapshot = null; prepared = null; Ready(false);
                Status(error.Message + " Reload imports to reconcile actual state; do not replay this Apply.", true);
            }
        };

        cleanup.Click += async (_, _) => {
            if (snapshot is null || lifetime is null) return;
            var baseline = snapshot; var token = lifetime.Token; var version = ++generation;
            snapshot = null; prepared = null; Ready(false); reload.IsEnabled = false;
            Status("Removing the recognized previous section while preserving current preferences and retained imports…");
            try
            {
                var result = await Editor().CleanPendingLegacyAsync(baseline, token);
                if (version != generation || token.IsCancellationRequested) return;
                Present(result); Status("Removed the previous section and restored the exact current preferences. Retained imports are unchanged; review the selected import next.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (version != generation) return;
                snapshot = null; prepared = null; Ready(false);
                Status(error.Message + " Reload imports to reconcile actual state; do not replay this cleanup.", true);
            }
        };

        panel.Loaded += async (_, _) => {
            if (lifetime is not null || disposed) return;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token); await Reload();
        };
        panel.Unloaded += (_, _) => {
            generation++; lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
            snapshot = null; prepared = null; Ready(false);
        };
        return panel;
    }
}
