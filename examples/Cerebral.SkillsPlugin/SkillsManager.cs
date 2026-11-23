using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;
using Microsoft.Win32;

namespace Cerebral.SkillsPlugin;

internal sealed class SkillsManager : UserControl
{
    private readonly SkillsPlugin plugin;
    private readonly DataGrid grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended,
        SelectionUnit = DataGridSelectionUnit.FullRow, MinHeight = 120, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly TextBox search = new() { MinWidth = 180, Margin = new Thickness(5, 0, 10, 0) };
    private readonly ComboBox groups = new() { MinWidth = 145, Margin = new Thickness(5, 0, 8, 0) };
    private readonly ComboBox scope = new() { MinWidth = 135, ItemsSource = new[] { "Keep assignments", "global", "role", "profile", "project" }, SelectedIndex = 0 };
    private readonly TextBox target = new() { Width = 150, Margin = new Thickness(6, 0, 8, 0), IsEnabled = false };
    private readonly TextBlock targetHint = Text("Choose an exact scope; existing assignments are kept by default.", 11);
    private readonly TextBox groupName = new() { Width = 155, Margin = new Thickness(5, 0, 8, 0) };
    private readonly TextBox preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
    private readonly Label targetLabel;
    private readonly TextBlock roots = Text("", 11);
    private readonly TextBlock status = Text("", 12);
    private readonly TextBlock scanState = Text("", 11);
    private readonly List<Button> ownerButtons = [];
    private readonly Button review;
    private readonly Button export;
    private SkillsManagerSnapshot snapshot = new([], "", "", null, false, false, "Loading…", null, false, new Dictionary<string, string[]>());
    private bool updating, acting;

    internal SkillsManager(SkillsPlugin plugin)
    {
        this.plugin = plugin;
        Margin = new Thickness(12);
        SetResourceReference(BackgroundProperty, SystemColors.ControlBrushKey);
        SetResourceReference(ForegroundProperty, SystemColors.ControlTextBrushKey);
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(Text("Skills", 22));
        heading.Children.Add(Text("Discover locally. Install disabled. Enable for an explicit scope. Instruction content never adds tools or permissions.", 12));
        heading.Children.Add(roots);
        heading.Children.Add(scanState);
        review = Action("Review connection…", ReviewConnection, owner: false);
        heading.Children.Add(Row(Action("Refresh", plugin.RefreshAsync, owner: false), review));
        root.Children.Add(heading);

        var controls = new StackPanel { Margin = new Thickness(0, 9, 0, 8) };
        controls.Children.Add(Row(InputLabel("Search", search), search, InputLabel("Group", groups), groups,
            Action("Select group / results", () => { grid.SelectAll(); return Task.CompletedTask; }, owner: false)));
        controls.Children.Add(Row(Action("Install selected", () => ConfirmAndRun("Install or update the selected definitions? New installs stay disabled and unassigned.",
                () => plugin.InstallAsync(Selected()))),
            Action("Enable selected", Enable), Action("Disable selected", () => plugin.ConfigureAsync(Selected(), false, null)),
            Action("Uninstall selected…", Uninstall)));
        targetLabel = InputLabel("Scope target", target);
        controls.Children.Add(Row(InputLabel("Enable scope", scope), scope, targetLabel, target));
        controls.Children.Add(targetHint);
        export = Action("Export group…", Export, owner: false);
        controls.Children.Add(Row(InputLabel("Group name", groupName), groupName,
            Action("Save selection as group", () => plugin.SaveGroupAsync(groupName.Text.Trim(), Selected())),
            Action("Import group…", Import), export));
        Grid.SetRow(controls, 1); root.Children.Add(controls);

        grid.Columns.Add(Column("Skill", nameof(SkillManagerRow.Name), 1.4));
        grid.Columns.Add(Column("Version", nameof(SkillManagerRow.Version), .55));
        grid.Columns.Add(Column("Availability", nameof(SkillManagerRow.State), 2));
        grid.Columns.Add(Column("Group", nameof(SkillManagerRow.Group), 1));
        grid.Columns.Add(Column("Origin", nameof(SkillManagerRow.Origin), 1));
        grid.Columns.Add(Column("Assignments", nameof(SkillManagerRow.Assignments), 1.5));
        var body = new Grid();
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1.65, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(grid);
        Grid.SetColumn(preview, 2); body.Children.Add(preview);
        Grid.SetRow(body, 2); root.Children.Add(body);
        status.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(status, 3); root.Children.Add(status);
        Content = root;
        foreach (var input in new[] { search, target, groupName, preview })
        {
            input.SetResourceReference(Control.BackgroundProperty, SystemColors.WindowBrushKey);
            input.SetResourceReference(Control.ForegroundProperty, SystemColors.WindowTextBrushKey);
        }
        search.TextChanged += (_, _) => Filter();
        groups.SelectionChanged += (_, _) => { if (!updating) Filter(); };
        grid.SelectionChanged += (_, _) => { if (!updating) ShowPreview(); };
        AutomationProperties.SetName(search, "Search skill names, IDs, descriptions and groups");
        AutomationProperties.SetName(groups, "Filter skills by group");
        AutomationProperties.SetName(scope, "Assignment scope for enabling selected skills");
        AutomationProperties.SetName(groupName, "Name for the saved or exported group");
        AutomationProperties.SetName(grid, "Skill definitions and availability");
        AutomationProperties.SetName(preview, "Selected definition, validation and declared assets");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        void ScopeChanged()
        {
            var choice = scope.SelectedItem as string;
            target.IsEnabled = choice is "role" or "profile" or "project";
            targetLabel.Content = choice switch { "role" => "Role", "profile" => "Profile ID", "project" => "Project ID", _ => "Scope target" };
            targetHint.Text = choice switch
            {
                "role" => "Exact role: lead, helper or compactor.",
                "profile" => "Enter the exact existing profile ID. This does not add tool permissions.",
                "project" => "Enter the exact existing project ID. This does not add file permissions.",
                "global" => "Global applies to all eligible agents; enabling still leaves existing tool permissions unchanged.",
                _ => "Keep preserves current assignments. An unassigned skill needs an explicit scope before Enable."
            };
            target.ToolTip = targetHint.Text;
            AutomationProperties.SetName(target, choice == "role" ? "Role: lead, helper or compactor" :
                choice == "profile" ? "Exact profile ID" : choice == "project" ? "Exact project ID" : "Scope target, unused");
        }
        scope.SelectionChanged += (_, _) => ScopeChanged();
        ScopeChanged();
        Loaded += (_, _) => plugin.ViewLoaded();
        Unloaded += (_, _) => plugin.ViewUnloaded();
    }

    internal void Update(SkillsManagerSnapshot value)
    {
        snapshot = value;
        roots.Text = "Editable: " + value.EditableRoot + "\nBundled (read-only): " + value.BundledRoot;
        scanState.Text = "Last scan: " + (value.LastScan?.ToLocalTime().ToString("g") ?? "not completed") +
            " · " + (value.Watching ? "Watching while this manager is open" : "Watcher paused") +
            " · Worker: " + (value.InstallationId ?? "unavailable");
        status.Text = value.Message;
        var group = groups.SelectedItem as string ?? "All groups";
        updating = true;
        try
        {
            groups.ItemsSource = new[] { "All groups" }.Concat(value.Rows.Select(x => x.Group).Concat(value.Groups.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)).ToArray();
            groups.SelectedItem = ((string[])groups.ItemsSource).Contains(group, StringComparer.Ordinal) ? group : "All groups";
        }
        finally { updating = false; }
        Filter();
        SetActions();
    }

    private void Filter()
    {
        if (updating) return;
        var selected = grid.SelectedItems.OfType<SkillManagerRow>().Select(x => x.Entry.Key).ToHashSet(StringComparer.Ordinal);
        var query = search.Text.Trim();
        var group = groups.SelectedItem as string;
        var rows = snapshot.Rows.Where(x => (group is null or "All groups" || x.Group == group ||
            snapshot.Groups.TryGetValue(group, out var members) && members.Contains(x.Id, StringComparer.Ordinal)) &&
            (query.Length == 0 || (x.Name + " " + x.Id + " " + x.Detail + " " + x.Group + " " + x.Origin)
                .Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        updating = true;
        try
        {
            grid.ItemsSource = rows;
            foreach (var row in rows.Where(x => selected.Contains(x.Entry.Key))) grid.SelectedItems.Add(row);
        }
        finally { updating = false; }
        ShowPreview();
    }

    private void ShowPreview()
    {
        var selected = grid.SelectedItems.OfType<SkillManagerRow>().ToArray();
        if (selected.Length != 1)
        {
            preview.Text = selected.Length == 0
                ? "Select a definition to inspect its metadata, validation state and body. Use Select group / results for bulk actions."
                : selected.Length + " definitions selected. Bulk actions apply only to this explicit selection.";
            return;
        }
        var row = selected[0];
        var assets = row.Entry.Document?.Assets ?? [];
        preview.Text = row.Name + "\nID: " + row.Id + "\nVersion: " + row.Version + "\n" + row.State +
            "\n" + row.Detail + "\nSource: " + row.Entry.Directory + "\n" +
            (row.Entry.Document?.Attribution ?? "") + "\nDeclared assets (always included in export):\n" +
            string.Join("\n", assets.Select(x => x.Path + " · " + x.Bytes + " bytes")) +
            "\n\n" + (row.Entry.Document?.Content ?? "No valid document is published.");
    }

    private IReadOnlyList<SkillManagerRow> Selected()
    {
        var rows = grid.SelectedItems.OfType<SkillManagerRow>().ToArray();
        if (rows.Length == 0) throw new InvalidOperationException("Select one or more skills.");
        if (rows.Select(x => x.Id).Where(x => x.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() <
            rows.Count(x => x.Id.Length != 0)) throw new InvalidOperationException("Select one source for each skill ID.");
        return rows;
    }
    private Task Enable()
    {
        var selected = Selected();
        var choice = scope.SelectedItem as string ?? "Keep assignments";
        var binding = choice == "Keep assignments" ? null : SkillsPlugin.ValidateBinding(
            new(choice, choice == "global" ? null : target.Text.Trim()));
        return ConfirmAndRun("Enable " + selected.Count + " selected skills with " + choice +
            (binding?.Target is { } id ? ":" + id : "") + "? Existing permissions remain unchanged.",
            () => plugin.ConfigureAsync(selected, true, binding));
    }

    private Task Uninstall()
    {
        var rows = Selected().Where(x => x.Catalog is not null).ToArray();
        if (rows.Length == 0) throw new InvalidOperationException("Select an installed skill. Discovered-only files are not installed.");
        var dialog = new Window { Title = "Uninstall selected skills", Width = 510, Height = 240,
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var removeFiles = new CheckBox { Content = "Also remove unchanged import-owned data files", Margin = new Thickness(0, 12, 0, 12) };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Text("Remove " + rows.Length + " selected catalog records? Bundled, unknown and modified files are preserved. Discovery will not reinstall them.", 13));
        panel.Children.Add(removeFiles);
        panel.Children.Add(DialogButtons(dialog, "Uninstall"));
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? plugin.UninstallAsync(rows, removeFiles.IsChecked == true) : Task.CompletedTask;
    }

    private Task ReviewConnection()
    {
        var id = snapshot.InstallationId ?? throw new InvalidOperationException("Connect to a current Worker first.");
        return ConfirmAndRun("Use Worker " + id + " for this selected folder index? Every catalog update/removal still requires matching source, revision and content. Foreign records stay protected.",
            () => plugin.ReviewConnectionAsync(id));
    }

    private async Task Import()
    {
        var picker = new OpenFileDialog { Title = "Preview a content-only skills group", Filter = "Skills group (*.zip)|*.zip", CheckFileExists = true };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        using var archive = SkillGroups.Open(picker.FileName);
        var review = await plugin.ReviewImportAsync(archive);
        var dialog = new Window { Title = "Import group · " + archive.Name, Width = 800, Height = 490,
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new Grid { Margin = new Thickness(14) };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var policy = new ComboBox { ItemsSource = Enum.GetValues<SkillConflictChoice>(), SelectedItem = SkillConflictChoice.Keep, MinWidth = 140 };
        AutomationProperties.SetName(policy, "Action for existing skill ID conflicts");
        panel.Children.Add(Row(InputLabel("Existing ID conflicts", policy), policy));
        var details = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 10) };
        Grid.SetRow(details, 1); panel.Children.Add(details);
        var buttons = DialogButtons(dialog, "Import reviewed group");
        Grid.SetRow(buttons, 2); panel.Children.Add(buttons);
        SkillImportPlan? plan = null;
        void Replan()
        {
            try
            {
                plan = SkillGroups.Plan(archive, review.Owner.Skills, (SkillConflictChoice)policy.SelectedItem);
                details.Text = "Content-only definitions and declared passive assets. No credentials, permissions, commands or scripts are installed.\n" +
                    "Replacement preserves current enablement and assignments. Old imported/bundled source files are retained.\n\n" +
                    string.Join("\n", plan.Items.Select(x => x.Member.Document.Manifest.Id + " → " + x.Manifest.Id + ": " + x.Decision +
                        (x.Existing is null ? "" : " [existing " + x.Existing.Version + ", revision " + x.Existing.Revision + ", " + x.Existing.Source + "]")));
            }
            catch (Exception error) { plan = null; details.Text = error.Message; }
        }
        policy.SelectionChanged += (_, _) => Replan();
        Replan(); dialog.Content = panel;
        if (dialog.ShowDialog() != true || plan is null) return;
        var report = await plugin.ImportAsync(archive, plan, review.Owner, review.Existing);
        var result = new Window { Title = "Group import outcomes · " + archive.Name, Width = 800, Height = 490,
            MinWidth = 600, MinHeight = 300, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var resultBody = new DockPanel { Margin = new Thickness(14) };
        var close = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(14, 5, 14, 5),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        close.Click += (_, _) => result.Close();
        DockPanel.SetDock(close, Dock.Bottom); resultBody.Children.Add(close);
        resultBody.Children.Add(CreateOutcomeViewer(report));
        result.Content = resultBody; result.ShowDialog();
    }

    internal static FrameworkElement CreateOutcomeViewer(SkillImportReport report)
    {
        if (report.Outcomes.Count > 128) throw new InvalidDataException("Import outcomes exceed the admitted group bound.");
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var warning = Text(report.FollowupWarning ?? "", 12);
        warning.Visibility = string.IsNullOrEmpty(report.FollowupWarning) ? Visibility.Collapsed : Visibility.Visible;
        warning.Margin = new Thickness(0, 0, 0, 9);
        AutomationProperties.SetName(warning, "Separate follow-up refresh or local summary warning");
        root.Children.Add(warning);
        var all = new TextBox
        {
            Text = string.Join(Environment.NewLine, report.Outcomes), IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10)
        };
        AutomationProperties.SetName(all, "Complete group import item outcomes");
        Grid.SetRow(all, 1); root.Children.Add(all);
        return root;
    }

    private Task Export()
    {
        var rows = Selected();
        var name = string.IsNullOrWhiteSpace(groupName.Text) ? (groups.SelectedItem as string ?? "Selected skills") : groupName.Text.Trim();
        if (name == "All groups") name = "Selected skills";
        var picker = new SaveFileDialog { Title = "Export selected definitions and declared assets", Filter = "Skills group (*.zip)|*.zip",
            FileName = "skills-group.zip", AddExtension = true, DefaultExt = ".zip", OverwritePrompt = true };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return Task.CompletedTask;
        var expected = File.Exists(picker.FileName)
            ? SkillFiles.FileHash(picker.FileName, checked((int)SkillDocument.MaximumGroupBytes + 1024 * 1024)) : null;
        plugin.Export(name, rows, picker.FileName, expected);
        return Task.CompletedTask;
    }

    private Button Action(string title, Func<Task> action, bool owner = true)
    {
        var button = new Button { Content = title, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 3, 6, 3), ToolTip = title };
        button.Click += async (_, _) =>
        {
            if (acting) return;
            acting = true; SetActions();
            try { await action(); }
            catch (OperationCanceledException) { plugin.SetMessage("The manager operation was cancelled locally. An admitted command may still need outcome inspection."); }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            { plugin.SetMessage(error.Message); MessageBox.Show(Window.GetWindow(this), SkillFolderCatalog.Bound(error.Message), "Skills"); }
            finally { acting = false; SetActions(); }
        };
        if (owner) ownerButtons.Add(button);
        return button;
    }
    private void SetActions()
    {
        foreach (var button in ownerButtons)
            button.IsEnabled = !acting && !snapshot.Busy && snapshot.InstallationId is not null && !snapshot.NeedsOwnerReview;
        review.IsEnabled = !acting && !snapshot.Busy && snapshot.InstallationId is not null;
        review.Visibility = snapshot.NeedsOwnerReview ? Visibility.Visible : Visibility.Collapsed;
        export.IsEnabled = !acting && !snapshot.Busy;
    }
    private Task ConfirmAndRun(string text, Func<Task> action) =>
        MessageBox.Show(Window.GetWindow(this), text, "Skills", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
            ? action() : Task.CompletedTask;
    private static FrameworkElement DialogButtons(Window dialog, string confirm)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = confirm, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6), IsCancel = true };
        ok.Click += (_, _) => dialog.DialogResult = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        row.Children.Add(ok); row.Children.Add(cancel); return row;
    }
    private static Label InputLabel(string title, UIElement control)
    {
        var label = new Label { Content = title, Target = control, Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 5, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetLabeledBy(control, label);
        return label;
    }

    private static DataGridTextColumn Column(string title, string binding, double width) =>
        new() { Header = title, Binding = new Binding(binding), Width = new DataGridLength(width, DataGridLengthUnitType.Star) };
    private static TextBlock Text(string value, double size) =>
        new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
    private static WrapPanel Row(params FrameworkElement[] values)
    {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var value in values) { value.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(value); }
        return row;
    }
}
