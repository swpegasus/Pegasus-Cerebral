using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Cerebral.ChatIconsPlugin;

/// <summary>Bounded keyboard-accessible symbol grid shared by inline and utility-window views.</summary>
internal sealed class NativeIconPicker : UserControl
{
    private readonly IReadOnlyList<ChatIconAsset> assets;
    private readonly Func<ChatIconAsset, Task> insert;
    private readonly Func<string, FrameworkElement> preview;
    private readonly Func<ChatIconAsset, Task>? copyReference;
    private readonly WrapPanel grid = new();
    private readonly TextBox search = new() { MaxLength = 128, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock count = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly List<ToggleButton> categories = [];
    private string category = "All";
    internal IReadOnlyList<string> VisibleAssetIds => grid.Children.OfType<Button>().Select(button => ((ChatIconAsset)button.Tag).Key).ToArray();

    internal NativeIconPicker(IReadOnlyList<ChatIconAsset> assets, Func<ChatIconAsset, Task> insert, Func<string, FrameworkElement> preview,
        Func<ChatIconAsset, Task>? copyReference = null)
    {
        if (assets.Count > AssetPack.MaximumIcons) throw new InvalidDataException("The icon picker exceeds its bounded catalog.");
        this.assets = assets; this.insert = insert; this.preview = preview; this.copyReference = copyReference;
        // Tab headers may use contrasting selected ink; content retains its utility-panel text brush.
        SetResourceReference(Control.ForegroundProperty, SystemColors.WindowTextBrushKey);
        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Search symbols", Margin = new Thickness(0, 0, 0, 3) });
        AutomationProperties.SetAutomationId(search, "ChatIcons.Search"); AutomationProperties.SetName(search, "Search symbols by name or meaning");
        search.TextChanged += (_, _) => Refresh(); top.Children.Add(search);
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var name in new[] { "All", "Messages", "Status", "Resources" })
        {
            var button = new ToggleButton { Content = name, Tag = name, IsChecked = name == "All", Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 5, 3) };
            ChatIconsToggleStyle.Apply(button);
            AutomationProperties.SetAutomationId(button, "ChatIcons.Category." + name);
            button.Click += (_, _) => { category = name; foreach (var choice in categories) choice.IsChecked = Equals(choice.Tag, name); Refresh(); };
            categories.Add(button); row.Children.Add(button);
        }
        top.Children.Add(row); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(count, Dock.Bottom); root.Children.Add(count);
        root.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 310, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root; Refresh();
    }

    internal void FocusSearch() { search.Focus(); search.SelectAll(); }

    private void Refresh()
    {
        grid.Children.Clear(); var term = search.Text.Trim();
        foreach (var asset in assets.Where(asset => Category(asset) == category || category == "All")
            .Where(asset => term.Length == 0 || (asset.Label + " " + asset.Description + " " + asset.Alias).Contains(term, StringComparison.CurrentCultureIgnoreCase)))
        {
            var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var icon = preview(asset.Alias); icon.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(icon);
            body.Children.Add(new TextBlock { Text = asset.Text, FontSize = 19, HorizontalAlignment = HorizontalAlignment.Center });
            body.Children.Add(new TextBlock { Text = asset.Label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 88 });
            var button = new Button { Content = body, Tag = asset, Width = 104, MinHeight = 90, Margin = new Thickness(0, 0, 6, 6), ToolTip = asset.Label + " · " + asset.Description };
            AutomationProperties.SetAutomationId(button, "ChatIcons.Asset." + asset.Key); AutomationProperties.SetName(button, "Insert " + asset.Label);
            button.Click += async (_, _) =>
            {
                IsEnabled = false;
                try { await insert(asset); }
                finally { IsEnabled = true; }
            };
            if (copyReference is not null)
            {
                var menu = new ContextMenu();
                var copy = new MenuItem { Header = "Copy icon reference" };
                copy.Click += async (_, _) => await copyReference(asset);
                menu.Items.Add(copy); button.ContextMenu = menu;
            }
            grid.Children.Add(button);
        }
        count.Text = grid.Children.Count == 0 ? "No symbols match this search." : $"{grid.Children.Count} symbols · choose one to insert";
    }

    private static string Category(ChatIconAsset asset) => asset.Alias.StartsWith("chat.", StringComparison.Ordinal) && asset.Alias != "chat.attachment" ? "Messages" :
        asset.Alias.StartsWith("status.", StringComparison.Ordinal) ? "Status" : "Resources";
}
