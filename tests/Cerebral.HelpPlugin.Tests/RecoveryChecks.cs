using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;
using Cerebral.HelpPlugin;

internal static partial class Program
{
    private static int RunRecovery(string output)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources[DesktopThemeResourceKeys.TextBrush] = new SolidColorBrush(Color.FromRgb(30, 41, 59));
        app.Resources[DesktopThemeResourceKeys.MutedTextBrush] = new SolidColorBrush(Color.FromRgb(75, 85, 99));
        var host = new InertHost();
        using var plugin = new HelpPlugin();
        plugin.Initialize(host);
        var view = plugin.MountContributions.OfType<DesktopMountView>().Single().CreateView();
        Layout(view, 1120, 900);
        Check(HelpRecoveryCatalog.Entries.Count is >= 14 and <= HelpRecoveryCatalog.MaximumEntries, "bounded complete recovery catalog");
        Check(((IList<HelpRecoveryEntry>)HelpRecoveryCatalog.Entries).IsReadOnly, "immutable recovery entries");
        Check(HelpRecoveryCatalog.List().Count == HelpRecoveryCatalog.Entries.Count &&
            ((IList<HelpRecoverySummary>)HelpRecoveryCatalog.List()).IsReadOnly, "bounded immutable summaries");
        foreach (var entry in HelpRecoveryCatalog.Entries)
        {
            Check(HelpRecoveryCatalog.Read(new[] { entry.Id }).Single() == entry, "exact read " + entry.Id);
            Check(((IList<HelpRecoveryAction>)entry.Actions).IsReadOnly &&
                ((IList<HelpRecoverySource>)entry.Sources).IsReadOnly, "nested immutable " + entry.Id);
        }
        var first = HelpRecoveryCatalog.Entries[0].Id;
        void Reject(Action action, string name)
        { var refused = false; try { action(); } catch (ArgumentException) { refused = true; } Check(refused, name); }
        Reject(() => HelpRecoveryCatalog.Read(Array.Empty<string>()), "reject empty read");
        Reject(() => HelpRecoveryCatalog.Read(HelpRecoveryCatalog.Entries.Take(5).Select(entry => entry.Id).ToArray()), "reject oversized read without truncation");
        Reject(() => HelpRecoveryCatalog.Read(new[] { first, first }), "reject duplicate IDs");
        Reject(() => HelpRecoveryCatalog.Read(null!), "reject null read");
        Reject(() => HelpRecoveryCatalog.Get(null!), "reject null ID");
        Reject(() => HelpRecoveryCatalog.Get(first.ToUpperInvariant()), "reject case aliases");
        Reject(() => HelpRecoveryCatalog.Get("../audit"), "reject path-shaped ID");
        Reject(() => HelpRecoveryCatalog.Get("help.issue." + new string('a', 100)), "reject excessive ID");
        Reject(() => HelpRecoveryCatalog.Get("help.issue.absent"), "reject unknown ID");
        Reject(() => HelpRecoveryCatalog.Read(new[] { first, "help.issue.absent" }), "reject mixed request atomically");
        Check(((IList<HelpRecoveryEntry>)HelpRecoveryCatalog.Read(HelpRecoveryCatalog.Entries.Take(4).Select(entry => entry.Id).ToArray())).IsReadOnly, "maximum exact read is immutable");
        var optional = HelpRecoveryCatalog.Get("help.issue.audit.optional");
        Check(optional.Severity == HelpRecoverySeverity.Information && optional.BlockingCondition.Contains("Required=false") &&
            optional.Actions.All(action => action.Availability == HelpRecoveryAvailability.SupportedReadOnly), "optional absence does not prescribe policy mutation");
        foreach (var entry in HelpRecoveryCatalog.Entries.Where(entry => entry.Id.StartsWith("help.issue.audit.") && entry != optional))
        {
            Check(entry.BlockingCondition.Contains("Required=true") && entry.Actions.Any(action => action.Availability == HelpRecoveryAvailability.Pending),
                "required audit condition and real support gap " + entry.Id);
        }
        Find<HelpArchitectureView>(view, "HelpArchitecture").Explore("core");
        Find<Button>(view, "HelpCommonIssues").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(view, 1120, 900);
        Check(Title(view) == "Common issues", "Overview opens common issues");
        var recovery = Find<HelpRecoveryView>(view, "HelpRecovery");
        var selector = Find<ComboBox>(view, "HelpRecoverySelector");
        Check(recovery.SelectedIssueId == first && selector.Items.Count == HelpRecoveryCatalog.Entries.Count, "initial issue and complete selector");
        foreach (var summary in HelpRecoveryCatalog.List())
        {
            selector.SelectedItem = summary;
            Layout(view, 1120, 900);
            var entry = HelpRecoveryCatalog.Get(summary.Id);
            Check(recovery.SelectedIssueId == entry.Id &&
                Descendants(recovery).OfType<TextBlock>().Any(block => block.Text == "Why this appears: " + entry.Reason) &&
                Descendants(recovery).OfType<TextBlock>().Any(block => block.Text == "When it blocks: " + entry.BlockingCondition), "exact native reason/blocking readback " + entry.Id);
            foreach (var action in entry.Actions)
                Check(AutomationProperties.GetItemStatus(Find<GroupBox>(view, "HelpRecoveryAction." + entry.Id + "." + action.Id)) == action.Availability.ToString(),
                    "action availability " + entry.Id + "." + action.Id);
            Check(!Descendants(recovery).OfType<Button>().Any(button => button.Content is string), "no executable recovery action buttons " + entry.Id);
        }
        selector.SelectedItem = HelpRecoveryCatalog.List().Single(entry => entry.Id == "help.issue.audit.optional");
        Capture(view, output, "recovery-optional-wide", 1120, 900);
        selector.SelectedItem = HelpRecoveryCatalog.List().Single(entry => entry.Id == "help.issue.audit.required-missing");
        Capture(view, output, "recovery-required-wide", 1120, 1100);
        Capture(view, output, "recovery-required-narrow", 460, 1000);
        Select(view, "overview");
        Check(Find<HelpArchitectureView>(view, "HelpArchitecture").ScopeId == "core", "map scope survives recovery navigation");
        Select(view, "reference");
        Find<Button>(view, "HelpCommonIssues").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
        Check(Title(view) == "Common issues", "Reference recovery opens common issues");
        Check(HelpCatalog.Pages.SelectMany(page => page.Sections).Count(section => section.Kind == HelpSectionKind.TutorialStep) == 9 &&
            HelpCatalog.Pages.SelectMany(page => page.Sections).Count(section => section.Kind == HelpSectionKind.Screenshot) == 8, "intentional content placeholders preserved");
        Select(view, "assistant");
        Check(!Find<TextBox>(view, "HelpAssistantInput").IsEnabled && !Find<Button>(view, "HelpAssistantSend").IsEnabled, "assistant remains disconnected");
        Check(HelpArchitectureCatalog.Nodes.Count == 30 && HelpArchitectureCatalog.Edges.Count == 50, "architecture unchanged");
        Check(host.Calls == 0, "all recovery browsing and reads make zero host calls");
        var evidence = new { schema = "cerebral.help.recovery-offscreen/1", passed = true, checks,
            tier = "deterministic catalog plus programmatic native WPF offscreen interaction/renderer fixture with inert host",
            entries = HelpRecoveryCatalog.Entries.Count, captures, hostCalls = host.Calls, modelCalls = 0,
            registeredAiReader = false, runtimeActivation = false, physicalKeyboardScreenReader = "not performed" };
        File.WriteAllText(Path.Combine(output, "recovery-offscreen-evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "recovery-catalog.json"), JsonSerializer.Serialize(new { schemaVersion = HelpRecoveryCatalog.SchemaVersion,
            contentVersion = HelpRecoveryCatalog.ContentVersion, entries = HelpRecoveryCatalog.Entries }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks, entries = HelpRecoveryCatalog.Entries.Count, captures = captures.Count, hostCalls = host.Calls }));
        app.Shutdown();
        return 0;
    }
}
