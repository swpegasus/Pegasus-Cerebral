using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

/// <summary>Stable mount IDs, scoped to Configuration. Labels and positions may change without changing the saved selection.</summary>
internal sealed class ConfigurationTabPreferences : IDisposable
{
    private readonly TabControl tabs;
    private readonly string key;
    private readonly Action<string, string> write;
    private readonly Action<string> error;
    private readonly DispatcherTimer timer;
    private readonly INotifyCollectionChanged items;
    private string? desired;
    private string? persisted;
    private string? pending;
    private bool hydrating;
    private bool disposed;
    private bool initialized;
    private bool collectionChanged;

    public ConfigurationTabPreferences(TabControl tabs, Func<string, string?> read, Action<string, string> write, Action<string> error)
    {
        this.tabs = tabs; this.write = write; this.error = error;
        var mount = AutomationProperties.GetAutomationId(tabs);
        if (string.IsNullOrWhiteSpace(mount)) throw new ArgumentException("A stable Configuration tab mount ID is required.");
        key = "Integrator.NestedTab." + mount;
        desired = persisted = read(key);
        timer = new(DispatcherPriority.Background, tabs.Dispatcher) { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += TimerTick;
        tabs.SelectionChanged += SelectionChanged;
        items = (INotifyCollectionChanged)tabs.Items;
        items.CollectionChanged += ItemsChanged;
    }

    internal string MountId => AutomationProperties.GetAutomationId(tabs);
    internal string? DesiredSelection => desired;

    internal void HydrateSelection(string? selection)
    {
        if (disposed) return;
        timer.Stop(); pending = null; desired = persisted = selection;
        if (selection is null)
        {
            hydrating = true;
            try { if (tabs.Items.Count > 0) tabs.SelectedIndex = 0; }
            finally { hydrating = false; }
        }
        Restore();
    }

    public void Restore()
    {
        if (disposed) return;
        timer.Stop(); hydrating = true;
        try
        {
            var matches = tabs.Items.OfType<TabItem>().Where(x => AutomationProperties.GetAutomationId(x) == desired).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Configuration tab IDs must be unique within a mount.");
            if (matches.Length == 1) tabs.SelectedItem = matches[0];
            else if (tabs.SelectedIndex < 0 && tabs.Items.Count > 0) tabs.SelectedIndex = 0;
            initialized = true;
        }
        finally { hydrating = false; collectionChanged = false; }
        if (pending is not null) timer.Start();
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, tabs) || disposed || hydrating || collectionChanged || !initialized) return;
        // Removing a selected plugin may raise SelectionChanged before the collection notification.
        if (e.RemovedItems.OfType<TabItem>().Any(removedTab => !tabs.Items.Contains(removedTab))) return;
        pending = (tabs.SelectedItem as TabItem) is { } item ? AutomationProperties.GetAutomationId(item) : null;
        if (!string.IsNullOrEmpty(pending)) desired = pending;
        timer.Stop(); timer.Start();
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (disposed || !initialized) return;
        // Plugin removal/remount can temporarily select a fallback. Keep the desired mount until the collection settles.
        collectionChanged = true; timer.Stop();
        _ = tabs.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (!disposed) Restore(); }));
    }

    private void TimerTick(object? sender, EventArgs e) { timer.Stop(); Flush(); }

    public void Flush()
    {
        timer.Stop();
        if (disposed || hydrating || string.IsNullOrEmpty(pending)) return;
        var value = pending; pending = null;
        if (value == persisted) return;
        try { write(key, value); desired = persisted = value; }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            pending = value; error("The Configuration tab selection could not be saved: " + exception.Message);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        Flush(); disposed = true; timer.Stop();
        tabs.SelectionChanged -= SelectionChanged; items.CollectionChanged -= ItemsChanged; timer.Tick -= TimerTick;
    }
}

public sealed partial class IntegratorWindow
{
    private readonly List<ConfigurationTabPreferences> nestedTabPreferences = [];

    private void InitializeNestedTabPreferences(bool persistWindowPreferences, Func<string, string?>? read = null, Action<string, string>? write = null)
    {
        // Isolated diagnostics must not read or write the owner's navigation preferences.
        if (!persistWindowPreferences) return;
        read ??= UiPreferenceStore.ReadString; write ??= UiPreferenceStore.WriteString;
        if (persistCentralSettingsLayout)
            nestedTabPreferences.Add(new(Tabs, read, write, text => SetStatus(text, true)));

        void Add(TabControl parent)
        {
            foreach (var tab in parent.Items.OfType<TabItem>())
                if (tab.Content is TabControl nested)
                {
                    nestedTabPreferences.Add(new(nested, read, write, text => SetStatus(text, true)));
                    Add(nested);
                }
        }
        Add(Tabs);
        // No preference writes during construction, diagnostics or hydration. Dynamic plugin mounts restore the same stable ID.
        Loaded += (_, _) => { foreach (var state in nestedTabPreferences) state.Restore(); };
        Closing += (_, _) => { foreach (var state in nestedTabPreferences) state.Flush(); };
        Closed += (_, _) => { foreach (var state in nestedTabPreferences) state.Dispose(); };
    }
}