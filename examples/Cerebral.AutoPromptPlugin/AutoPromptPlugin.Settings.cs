using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.PackageSettings;

namespace Cerebral.AutoPromptPlugin;

public sealed partial class AutoPromptPlugin
{
    private sealed record ProfileChoice(string Id, long Revision, string Name)
    {
        public string Label => Name + " (" + Id + ", revision " + Revision + ")";
    }
    private FrameworkElement CreateSettings()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (preferences is null) throw new InvalidOperationException("Initialize AutoPrompt first.");
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "AutoPrompt", FontSize = 20, Margin = new Thickness(0, 0, 0, 8) });
        content.Children.Add(new TextBlock
        {
            Text = "Suggest a next message from only the latest final assistant response. A suggestion appears only in an empty message box. Tab puts it in your draft for review; Send remains a separate action.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        content.Children.Add(new TextBlock
        {
            Text = harness.Available ? "Choose a dedicated local AutoPrompt profile and load its configured model explicitly. Suggestions stay disabled while work or model resources are held. Older Workers remain unsupported." :
                "Suggestions are unavailable in this version. Update Cerebral to a version with AutoPrompt generation support. Your saved preferences stay inactive.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        var enabled = new CheckBox { Content = "Enable next-message suggestions when available", Margin = new Thickness(0, 0, 0, 12) };
        content.Children.Add(enabled);
        content.Children.Add(new TextBlock { Text = "Dedicated AutoPrompt profile", Margin = new Thickness(0, 0, 0, 6) });
        var selection = new ComboBox { MinWidth = 240, DisplayMemberPath = nameof(ProfileChoice.Label), HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(selection);
        var choices = new List<ProfileChoice>();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        var load = new Button { Content = "Load existing AutoPrompt profiles", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        content.Children.Add(load); content.Children.Add(status);
        void Hydrate(AutoPromptPreferences value)
        {
            enabled.IsChecked = value.Enabled;
            var items = choices.ToList();
            ProfileChoice? selected = null;
            if (value.ProfileId is { } id && value.ProfileRevision is { } revision)
            {
                selected = items.FirstOrDefault(p => p.Id == id && p.Revision == revision);
                if (selected is null) { selected = new(id, revision, "Saved profile (unavailable or stale)"); items.Add(selected); }
            }
            selection.ItemsSource = items; selection.SelectedItem = selected;
        }
        AutoPromptPreferences Draft() => selection.SelectedItem is ProfileChoice selected
            ? new(enabled.IsChecked == true, selected.Id, selected.Revision) : new(enabled.IsChecked == true, null, null);
        load.Click += async (_, _) =>
        {
            if (disposed || service is null) return;
            load.IsEnabled = false;
            try
            {
                var result = await service.ExecuteServiceCommandAsync("agents.list", new { }, lifetime.Token);
                if (!result.Success || result.Data.ValueKind != JsonValueKind.Array || result.Data.GetRawText().Length > 2 * 1024 * 1024)
                    throw new InvalidDataException();
                var next = new List<ProfileChoice>(); var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var profile in result.Data.EnumerateArray())
                {
                    if (!profile.TryGetProperty("purpose", out var purpose) || purpose.GetString() != "autoprompt") continue;
                    if (!profile.TryGetProperty("role", out var role) || role.GetString() != "helper" ||
                        !profile.TryGetProperty("workerId", out var worker) || worker.GetString() != "local" ||
                        !profile.TryGetProperty("modelWorkerId", out var modelWorker) || modelWorker.GetString() != "local" ||
                        !profile.TryGetProperty("modelId", out var model) || model.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model.GetString())) continue;
                    var id = profile.GetProperty("id").GetString()!;
                    var revision = profile.GetProperty("revision").GetInt64();
                    _ = AutoPromptPreferences.Read(new AutoPromptPreferences(false, id, revision).Values());
                    if (!seen.Add(id)) throw new InvalidDataException();
                    var name = profile.GetProperty("name").GetString() ?? id;
                    if (name.Length > 512 || name.Any(char.IsControl)) throw new InvalidDataException();
                    next.Add(new(id, revision, name));
                }
                if (disposed) return;
                var draft = Draft(); choices = next; Hydrate(draft);
                status.Text = next.Count == 0 ? "No configured local AutoPrompt profile exists. Create one from the AutoPrompt suggestion template in Models and agents, select its local model, then load the profile here." :
                    "Select an exact current revision explicitly. Loading this list does not create or modify profiles.";
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (!disposed) status.Text = "Profiles could not be read. Your selection and saved preferences are preserved."; }
            finally { if (!disposed) load.IsEnabled = true; }
        };
        var editor = new PackagePreferenceEditor<AutoPromptPreferences>(preferences, Draft, Hydrate, null, "AutoPrompt",
            savedMessage: "Preferences saved. Suggestions require the selected current profile and compatible AutoPrompt generation support.");
        return new ScrollViewer { Content = editor.Create(content), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
