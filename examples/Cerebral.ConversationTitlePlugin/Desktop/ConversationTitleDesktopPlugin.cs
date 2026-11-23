using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;
using Cerebral.PackageSettings;

namespace Cerebral.ConversationTitlePlugin.Desktop;

public sealed class ConversationTitleDesktopPlugin : IDesktopPlugin, IDesktopConfigurationPlugin
{
    private IDesktopServiceHost? service;
    private PackagePreferenceSession<TitlePreferences>? preferences;
    private readonly CancellationTokenSource lifetime = new();
    private bool initialized, disposed;
    public IReadOnlyList<DesktopPanel> Panels => [];
    public IReadOnlyList<DesktopAction> ComposerActions => [];
    public IReadOnlyList<DesktopPanel> ConfigurationPanels => [new("conversation-title-settings", "Conversation titles", CreateSettings)];
    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;
    public void Initialize(IDesktopPluginHost value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (initialized || value is not IDesktopServiceHost ownerService) throw new InvalidOperationException("Conversation title preferences require the current owner settings interface.");
        initialized = true; service = ownerService;
        preferences = new(value, TitlePackageIdentity.PackageId, TitlePackageIdentity.PackageVersion, new TitleSettingsPolicy(),
            TitlePreferences.Read, value => value.Values(), () => TitlePreferences.Default, settingsVersion: TitleSettingsPolicy.SettingsVersion);
    }

    private FrameworkElement CreateSettings()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (preferences is null) throw new InvalidOperationException("Initialize conversation title preferences first.");
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "Automatic conversation names", FontSize = 20, Margin = new Thickness(0, 0, 0, 8) });
        content.Children.Add(new TextBlock { Text = "Choose which source profiles may name conversations and one existing local execution profile. Saved enablement stays inactive until both are configured. You can rename a conversation yourself at any time.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var enabled = new CheckBox { Content = "Enable automatic conversation names", Margin = new Thickness(0, 0, 0, 12) };
        content.Children.Add(enabled);
        var status = new TextBlock { Text = "Load profiles explicitly to review existing choices. Opening this panel does not run a model or change profiles.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        var load = new Button { Content = "Load existing profiles", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        content.Children.Add(load); content.Children.Add(status);
        content.Children.Add(new TextBlock { Text = "Source profiles", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) });
        var sources = new StackPanel();
        content.Children.Add(new ScrollViewer { Content = sources, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        content.Children.Add(new TextBlock { Text = "Local execution profile and exact revision", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
        var inference = new ComboBox { MinWidth = 280, DisplayMemberPath = nameof(ProfileChoice.Label), HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(inference);
        var available = new List<ProfileChoice>();
        var sourceChoices = new List<(string Id, CheckBox Box)>();
        void ShowSources(IEnumerable<string> selected)
        {
            var saved = selected.ToHashSet(StringComparer.Ordinal);
            sources.Children.Clear(); sourceChoices.Clear();
            foreach (var id in available.Select(p => p.Id).Concat(saved).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var profile = available.FirstOrDefault(p => p.Id == id);
                var box = new CheckBox { Content = profile is null ? "Saved source: " + id + " (unavailable)" : profile.Name + " (" + id + ")", IsChecked = saved.Contains(id), Margin = new Thickness(0, 0, 0, 5) };
                sourceChoices.Add((id, box)); sources.Children.Add(box);
            }
        }
        void ShowInference(string? id, long? revision)
        {
            var choices = available.Where(p => p.LocalExecution).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
            ProfileChoice? selected = null;
            if (id is not null && revision is not null)
            {
                selected = choices.FirstOrDefault(p => p.Id == id && p.Revision == revision);
                if (selected is null) { selected = new(id, "Saved inference reference (unavailable/stale)", revision.Value, false); choices.Add(selected); }
            }
            inference.ItemsSource = choices; inference.SelectedItem = selected;
        }
        TitlePreferences Draft()
        {
            var selected = inference.SelectedItem as ProfileChoice;
            return new(enabled.IsChecked == true, selected?.Id, selected?.Revision,
                sourceChoices.Where(p => p.Box.IsChecked == true).Select(p => p.Id));
        }
        void Hydrate(TitlePreferences value)
        {
            enabled.IsChecked = value.Enabled;
            ShowSources(value.SourceProfileIds); ShowInference(value.ProfileId, value.ProfileRevision);
        }
        load.Click += async (_, _) =>
        {
            if (disposed) return;
            load.IsEnabled = false;
            try
            {
                var reply = await service!.ExecuteServiceCommandAsync("agents.list", new { }, lifetime.Token);
                if (!reply.Success || reply.Data.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
                TitlePolicy.Bounded(reply.Data.GetRawText(), 2 * 1024 * 1024);
                var next = new List<ProfileChoice>(); var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in reply.Data.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString()!;
                    _ = new TitlePreferences(false, null, null, [id]);
                    if (!seen.Add(id)) throw new InvalidDataException();
                    var name = item.TryGetProperty("name", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString()! : id;
                    TitlePolicy.Bounded(name, 4096);
                    var revision = item.GetProperty("revision").GetInt64();
                    var local = revision > 0 && item.TryGetProperty("workerId", out var worker) && worker.GetString() == "local";
                    var route = item.TryGetProperty("modelWorkerId", out var modelWorker) && modelWorker.ValueKind == JsonValueKind.String
                        ? modelWorker.GetString() : null;
                    try { TitlePolicy.Identifier(id); } catch (ArgumentException) { local = false; }
                    next.Add(new(id, name, revision, local, route));
                }
                if (disposed) return;
                // Preserve any edits or explicit preference reload completed while the read awaited.
                var draft = Draft();
                available = next; Hydrate(draft);
                status.Text = "Existing profiles loaded. No selection or profile was changed. Select a current local revision explicitly if a saved reference is stale.";
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (!disposed) status.Text = "Profiles could not be read. Your saved references and draft are preserved; no profile or model was changed."; }
            finally { if (!disposed) load.IsEnabled = true; }
        };
        var editor = new PackagePreferenceEditor<TitlePreferences>(preferences, Draft, Hydrate, null, "ConversationTitles", savedMessage:
            "Preferences saved. Future eligible conversations use these choices. No naming runs from this panel.");
        return new ScrollViewer { Content = editor.Create(content), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private sealed record ProfileChoice(string Id, string Name, long Revision, bool LocalExecution, string? ModelRoute = null)
    {
        public string Label => Name + " (" + Id + ", revision " + Revision + (ModelRoute is null ? "" : ", model route " + ModelRoute) + ")";
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { lifetime.Cancel(); } catch (AggregateException) { }
        preferences?.Dispose(); lifetime.Dispose(); service = null;
    }
}
