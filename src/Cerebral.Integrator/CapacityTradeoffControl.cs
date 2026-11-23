using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Core;

namespace Cerebral.Integrator;

/// <summary>The existing discrete preview control, with explicit reviewed configuration Apply.</summary>
internal sealed class CapacityTradeoffControl : UserControl, IDisposable
{
    private readonly TextBlock saved = Label("Select a model.");
    private readonly TextBlock preview = Label("");
    private readonly TextBlock availability = Label("");
    private readonly ComboBox mode = new() { ItemsSource = new[] { "fixed", "automatic" }, MinWidth = 140 };
    private readonly TextBox context = new() { MinWidth = 120 };
    private readonly TextBox slots = new() { MinWidth = 60 };
    private readonly TextBlock contextLabel = Label("Fixed context tokens");
    private readonly Slider slider = new()
    {
        Minimum = 0, Maximum = 1, TickFrequency = 1, IsSnapToTickEnabled = true,
        SmallChange = 1, LargeChange = 1, TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
        Margin = new Thickness(0, 8, 0, 8), IsEnabled = false
    };
    private readonly TextBox details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 120, MaxHeight = 280 };
    private readonly Button refresh = new() { Content = "Preview / read back", MinWidth = 120 };
    private readonly Button apply = new() { Content = "Review and apply…", IsEnabled = false, MinWidth = 120 };
    private readonly CapacityTradeoffPresenter presenter;
    private readonly Func<string, CancellationToken, Task<bool>> confirm;
    private readonly CancellationToken lifetime;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CapacityChoiceScope? scope;
    private ModelCapacityBounds? bounds;
    private bool binding, draft;
    internal CapacityTradeoffPresenter Presenter => presenter;

    internal CapacityTradeoffControl(ICapacityChoicesProvider? provider = null, TimeProvider? clock = null,
        Func<string, CancellationToken, Task<bool>>? confirm = null, CancellationToken lifetime = default)
    {
        presenter = new(provider ?? new UnavailableCapacityChoicesProvider(), clock ?? TimeProvider.System);
        this.confirm = confirm ?? ((_, _) => Task.FromResult(false)); this.lifetime = lifetime;
        var root = new StackPanel { MinWidth = 360 };
        root.Children.Add(Label("INFERENCE SLOTS"));
        root.Children.Add(saved);
        var manual = new WrapPanel();
        foreach (var element in new FrameworkElement[] { Label("Context mode"), mode, contextLabel, context, Label("Inference slots"), slots })
        { element.Margin = new Thickness(0, 4, 10, 4); manual.Children.Add(element); }
        root.Children.Add(manual);
        var endpoints = new DockPanel();
        var left = Label("More context per slot"); var right = Label("More inference slots");
        DockPanel.SetDock(right, Dock.Right); endpoints.Children.Add(right); endpoints.Children.Add(left);
        root.Children.Add(endpoints); root.Children.Add(slider); root.Children.Add(preview);
        root.Children.Add(Label("Saved chats and agent profiles are independent of physical inference slots. Additional work may wait for the separate scheduler ceiling."));
        root.Children.Add(details); root.Children.Add(availability);
        var actions = new WrapPanel(); actions.Children.Add(refresh); actions.Children.Add(apply); root.Children.Add(actions); Content = root;
        AutomationProperties.SetName(slider, "Context and concurrent inference slots");
        AutomationProperties.SetAutomationId(slider, "ModelContextConcurrencyPreview");
        AutomationProperties.SetAutomationId(apply, "ModelContextConcurrencyApply");
        AutomationProperties.SetAutomationId(details, "ModelContextConcurrencyEffects");
        presenter.Changed += Render;
        slider.ValueChanged += async (_, _) =>
        {
            if (binding || presenter.Applying || presenter.View is not { } view) return;
            var index = (int)Math.Round(slider.Value);
            if (index >= 0 && index < view.Preview.Candidates.Count)
                await ReadAsync(view.Preview.Candidates[index].Choice);
        };
        mode.SelectionChanged += (_, _) => DraftChanged();
        context.TextChanged += (_, _) => DraftChanged(); slots.TextChanged += (_, _) => DraftChanged();
        refresh.Click += async (_, _) =>
        {
            try { await ReadAsync(ManualChoice()); }
            catch (Exception error) when (CapacityTradeoffPresenter.Ordinary(error))
            { presenter.Invalidate(error.Message); }
        };
        apply.Click += async (_, _) => await presenter.ApplyAsync(this.confirm, lifetime);
        timer.Tick += (_, _) => presenter.Poll();
        Loaded += (_, _) => { timer.Start(); presenter.Poll(); };
        Unloaded += (_, _) => { timer.Stop(); presenter.Invalidate("Capacity view closed. Refresh before applying."); };
        Render();
    }

    internal async Task OpenAsync(CapacityChoiceScope next, SavedCapacitySetting fallback, CancellationToken token)
    {
        if (scope != next)
        {
            bounds = null;
            presenter.Invalidate("The selected model or connection changed. Its previous request remains owned until acknowledgement/readback.");
        }
        scope = next; binding = true;
        saved.Text = $"Saved model {next.ModelId} · {fallback.ContextTokens:N0} {(fallback.AutomaticContext ? "last/fallback" : "fixed")} context tokens; {fallback.Slots:N0} inference slots. Refreshing authoritative settings.";
        try { mode.SelectedItem = fallback.AutomaticContext ? "automatic" : "fixed"; context.Text = (fallback.AutomaticContext ? fallback.MaximumContextTokens : fallback.ContextTokens).ToString(CultureInfo.InvariantCulture); slots.Text = fallback.Slots.ToString(CultureInfo.InvariantCulture); }
        finally { binding = false; }
        // A null choice lets the server preserve and explain the actual saved mode/ceiling.
        draft = false; await presenter.ReadAsync(next, null, token);
    }
    internal void Invalidate(string reason)
    { scope = null; bounds = null; saved.Text = "Select the current machine and model."; presenter.Invalidate(reason); }
    private async Task ReadAsync(ModelCapacityChoice choice)
    {
        if (scope is not { } current) return;
        draft = false; await presenter.ReadAsync(current, choice, lifetime);
    }
    private ModelCapacityChoice ManualChoice()
    {
        if (!int.TryParse(context.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ||
            !int.TryParse(slots.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1 || amount < 0)
            throw new ArgumentException("Enter whole context/ceiling and slot values, then preview them.");
        var choice = mode.SelectedItem as string == "automatic"
            ? new ModelCapacityChoice("automatic", count, AutomaticContextCeilingTokens: amount)
            : new ModelCapacityChoice("fixed", count, FixedContextTokens: amount);
        // Bounds are the backend protocol's manual bounds, not the suggestion set.
        if (bounds is not null && !CapacityTradeoffPresentation.ValidChoice(choice, bounds))
            throw new ArgumentException("This manual choice is outside the Worker's compatibility bounds.");
        return choice;
    }
    private void DraftChanged()
    {
        if (binding || presenter.Applying) return;
        draft = true; presenter.Invalidate("Manual choice changed. Preview it to obtain exact effects before Apply.");
    }
    private void Render()
    {
        binding = true;
        try
        {
            var view = presenter.View; var busy = presenter.Applying || presenter.Reading;
            mode.IsEnabled = context.IsEnabled = slots.IsEnabled = !busy;
            contextLabel.Text = mode.SelectedItem as string == "automatic" ? "Automatic ceiling (0 = native limit)" : "Fixed context tokens";
            refresh.IsEnabled = !busy && scope is not null;
            apply.IsEnabled = !draft && presenter.CanApply;
            slider.IsEnabled = !busy && view is { Preview.Candidates.Count: > 1 };
            slider.Maximum = Math.Max(1, (view?.Preview.Candidates.Count ?? 0) - 1);
            if (view is not null)
            {
                bounds = view.Preview.SupportedBounds;
                saved.Text = view.SavedText; preview.Text = view.RequestedText; details.Text = view.Details;
                var choice = view.Preview.Choice;
                mode.SelectedItem = choice.ContextMode;
                context.Text = (choice.FixedContextTokens ?? choice.AutomaticContextCeilingTokens ?? 0).ToString(CultureInfo.InvariantCulture);
                slots.Text = choice.ParallelRequests.ToString(CultureInfo.InvariantCulture);
                var index = view.Preview.Candidates.Select((x, i) => (x, i)).FirstOrDefault(x => x.x.Choice == choice);
                slider.Value = index.x is null ? 0 : index.i;
                AutomationProperties.SetHelpText(slider, view.RequestedText);
            }
            else { preview.Text = ""; details.Text = ""; }
            availability.Text = presenter.Applying ? "Save pending. Controls are locked; no model load or unload is requested." :
                presenter.Reading ? "Reading the selected preview and complete effects…" : presenter.Message;
        }
        finally { binding = false; }
    }
    public void Dispose() { timer.Stop(); presenter.Dispose(); }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
}
