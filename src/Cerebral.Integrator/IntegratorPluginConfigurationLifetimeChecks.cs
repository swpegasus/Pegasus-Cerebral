using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Integrator;

// Deterministic editor-lifetime fixtures only. No window is shown and no service command is dispatched.
internal static class IntegratorPluginConfigurationLifetimeChecks
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-config-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DesktopPreferenceDiagnostics.UseProcessLocalDirectory(root);
        _ = DesktopInstancePaths.UseAttachedExisting(root, AppContext.BaseDirectory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Ui.ApplyTheme(app);
        var window = new IntegratorWindow(false, isolatedSettingsFixture: true,
            desktopPluginCatalog: new DesktopPluginCatalog(Path.Combine(root, "plugins")),
            desktopPluginDataDirectory: Path.Combine(root, "plugin-data"), seedBundledDesktopPlugins: false);
        var exit = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                var checks = await window.CheckPluginConfigurationLifetimeAsync(timeout.Token);
                Console.WriteLine("Plugin configuration lifetime: " + checks +
                    " deterministic unshown-WPF/local-callback checks passed; shownWindows=0, serviceCommands=0, network=false, modelCalls=0.");
                exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { window.Close(); app.Shutdown(exit); }
        }));
        try { return app.Run(); }
        finally
        {
            window.Close();
            var full = Path.GetFullPath(root);
            if (!ScopedFilePolicy.IsWithin(full, Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(full).StartsWith("cerebral-config-editor-", StringComparison.Ordinal))
                throw new InvalidOperationException("Editor fixture cleanup escaped its owned root.");
            ScopedFilePolicy.RejectLinks(full);
            Directory.Delete(full, recursive: true);
        }
    }
}

public sealed partial class IntegratorWindow
{
    internal async Task<int> CheckPluginConfigurationLifetimeAsync(CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run editor checks on the active WPF dispatcher.");
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Plugin editor lifetime: " + message);
            checks++;
        }
        using var firstClient = new CerebralClient("http://127.0.0.1:1");
        using var replacementClient = new CerebralClient("http://127.0.0.1:2");
        var originalItems = desktopPluginItems;
        var originalEpoch = queueConnectionEpoch;
        object? runtime = new object();
        var ownerState = new ConfigurationPluginOwnerState(firstClient, "fixture-owner", 1, true);
        var owner = new ConfigurationPluginOwnerScope(ownerState, () => ownerState);
        var package = new DesktopPluginInfo("fixture.editor", "Fixture settings", "1.0.0", "Fixture", true,
            "fixture-package", "fixture-package/desktop-plugin.json", null);
        DesktopPluginInfo? selected = package;
        (string Directory, string Version)? loaded = (package.Directory, package.Version);
        var status = new TextBlock { Text = "Ready" };
        IReadOnlyList<DesktopHostedPanel> panels = [new(package.Id, "settings", "General", status)];
        DesktopPluginConfigurationState? Read() => DesktopPluginConfigurationState.Capture(
            runtime, owner, owner.MatchesOwner(),
            desktopPluginItems.SingleOrDefault(item => item.Id == package.Id), loaded, panels);
        Window? editor = null;
        TabControl? tabs = null;
        var closed = 0;
        var saves = 0;
        var revision = 0;
        var uncertain = false;
        void Open()
        {
            CloseDesktopPluginConfiguration();
            RefreshDesktopPluginCatalog(() => selected is null ? [] : [selected]);
            var captured = Read() ?? throw new InvalidOperationException("Fixture admission is invalid.");
            tabs = CreateDesktopPluginConfigurationTabs(package.Id, captured.Panels);
            editor = new Window { Content = tabs, ShowActivated = false, ShowInTaskbar = false };
            editor.Closed += (_, _) => closed++;
            TrackDesktopPluginConfiguration(editor, package.Id, new(captured, Read));
        }
        void Refresh()
        {
            token.ThrowIfCancellationRequested();
            RefreshConfigurationDesktopPlugins(() => selected is null ? [] : [selected]);
        }
        async Task Save(bool loseReply = false)
        {
            if (uncertain) return; // The fake editor retains an uncertain outcome; a refresh cannot replay it.
            saves++;
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            revision++;
            Refresh(); // Same synchronous presentation notification as a save callback.
            SetStatus("Fixture save returned");
            if (loseReply) { uncertain = true; status.Text = "Save outcome unknown; inspect before retry."; }
            else status.Text = "Saved revision " + revision;
        }
        try
        {
            Open();
            var firstWindow = editor;
            var firstTabs = tabs;
            var firstPanel = panels[0].View;
            Check(PresentationSource.FromVisual(editor!) is null && !editor!.IsVisible,
                "fixture window remains unshown with no native presentation source");
            await Save();
            Check(saves == 1 && revision == 1 && status.Text == "Saved revision 1",
                "one committed fake save returns its acknowledgement");
            Check(ReferenceEquals(desktopPluginConfigurationWindow, firstWindow) && closed == 0 &&
                ReferenceEquals(firstWindow!.Content, firstTabs) &&
                ReferenceEquals(firstTabs!.Items.OfType<TabItem>().Single().Content, firstPanel),
                "presentation refresh retains the same editor, tab and acknowledgement control");
            await Save();
            Check(saves == 2 && revision == 2 && status.Text == "Saved revision 2" &&
                ReferenceEquals(desktopPluginConfigurationWindow, firstWindow),
                "a distinct next save uses the same current owner and editor");
            selected = package with { Description = "Updated display metadata" };
            Refresh();
            Check(ReferenceEquals(desktopPluginConfigurationWindow, firstWindow) && closed == 0,
                "display-only catalog metadata does not replace a current editor");
            await Save(loseReply: true);
            Refresh();
            await Save();
            Check(saves == 3 && revision == 3 && uncertain && status.Text.Contains("unknown", StringComparison.Ordinal) &&
                ReferenceEquals(desktopPluginConfigurationWindow, firstWindow) && closed == 0,
                "presentation refresh preserves uncertain acknowledgement state without replay");

            // The Manage -> Refresh list action replaces the catalog directly, without Runtime.Changed
            // or a successful Run status notification. Exercise that exact replacement boundary.
            var directWindow = desktopPluginConfigurationWindow;
            var directSaves = saves;
            var directStatus = Status.Text;
            RefreshDesktopPluginCatalog(() => [package with { Description = "Direct display refresh" }]);
            Check(ReferenceEquals(desktopPluginConfigurationWindow, directWindow) &&
                status.Text.Contains("unknown", StringComparison.Ordinal) && saves == directSaves && Status.Text == directStatus,
                "direct catalog refresh retains the same owner/editor and uncertain state without status, replay or runtime event");
            RefreshDesktopPluginCatalog(() =>
            [
                package,
                new("fixture.unrelated", "Unrelated", "1.0.0", "Another package", false, "other", "other-manifest", null)
            ]);
            Check(ReferenceEquals(desktopPluginConfigurationWindow, directWindow) && saves == directSaves,
                "direct refresh of an unrelated package retains the admitted editor");

            foreach (var change in new[] { "disabled", "removed", "version", "generation", "manifest", "invalid" })
            {
                CloseDesktopPluginConfiguration();
                runtime = new object();
                ownerState = new(firstClient, "fixture-owner", 1, true);
                owner = new(ownerState, () => ownerState);
                selected = package;
                loaded = (package.Directory, package.Version);
                panels = [new(package.Id, "settings", "General", new TextBlock { Text = "Retained draft" })];
                Open();
                var beforeClose = closed;
                var beforeSaves = saves;
                var beforeStatus = Status.Text;
                selected = change switch
                {
                    "disabled" => package with { Enabled = false },
                    "removed" => null,
                    "version" => package with { Version = "1.1.0" },
                    "generation" => package with { Directory = "replacement-package" },
                    "manifest" => package with { ManifestPath = "replacement-manifest" },
                    _ => package with { Error = "Invalid changed package" }
                };
                // Keep the old runtime/package projection: an explicit list refresh need not reload it.
                RefreshDesktopPluginCatalog(() => selected is null ? [] : [selected]);
                Check(desktopPluginConfigurationWindow is null && desktopPluginConfigurationScope is null &&
                    configuredDesktopPluginId is null && closed == beforeClose + 1,
                    "direct catalog replacement revokes the stale editor immediately: " + change);
                Check(saves == beforeSaves && Status.Text == beforeStatus,
                    "direct refresh invalidation neither replays a save nor relies on a status event: " + change);
            }

            // Every case is a new admitted editor. Only the modeled current identity is varied.
            foreach (var change in new[] { "disabled", "removed", "invalid catalog", "updated version", "updated generation",
                "manifest changed", "not loaded", "loaded version mismatch", "loaded directory mismatch", "runtime changed",
                "owner scope changed", "client changed", "owner identity changed", "owner epoch changed", "offline",
                "panel replaced", "panel removed", "panel ID changed" })
            {
                CloseDesktopPluginConfiguration();
                runtime = new object();
                ownerState = new(firstClient, "fixture-owner", 1, true);
                owner = new(ownerState, () => ownerState);
                selected = package;
                loaded = (package.Directory, package.Version);
                panels = [new(package.Id, "settings", "General", new TextBlock { Text = "Current draft" })];
                Open();
                var previousClosed = closed;
                var previousSaves = saves;
                switch (change)
                {
                    case "disabled": selected = package with { Enabled = false }; break;
                    case "removed": selected = null; break;
                    case "invalid catalog": selected = package with { Error = "Fixture admission error" }; break;
                    case "updated version":
                        selected = package with { Version = "1.1.0" }; loaded = (package.Directory, "1.1.0"); break;
                    case "updated generation":
                        selected = package with { Directory = "replacement-package" }; loaded = ("replacement-package", package.Version); break;
                    case "manifest changed": selected = package with { ManifestPath = "replacement-manifest" }; break;
                    case "not loaded": loaded = null; break;
                    case "loaded version mismatch": loaded = (package.Directory, "other"); break;
                    case "loaded directory mismatch": loaded = ("other", package.Version); break;
                    case "runtime changed": runtime = new object(); break;
                    case "owner scope changed": owner = new(ownerState, () => ownerState); break;
                    case "client changed": ownerState = ownerState with { Client = replacementClient }; break;
                    case "owner identity changed": ownerState = ownerState with { Identity = "replacement-owner" }; break;
                    case "owner epoch changed": ownerState = ownerState with { Epoch = 2 }; break;
                    case "offline": ownerState = ownerState with { Online = false }; break;
                    case "panel replaced": panels = [new(package.Id, "settings", "General", new TextBlock())]; break;
                    case "panel removed": panels = []; break;
                    case "panel ID changed": panels = [new(package.Id, "replacement", "General", panels[0].View)]; break;
                }
                Refresh();
                Check(desktopPluginConfigurationWindow is null && configuredDesktopPluginId is null &&
                    desktopPluginConfigurationScope is null && closed == previousClosed + 1,
                    "real invalidation closes and releases the editor: " + change);
                Check(saves == previousSaves, "invalidation never dispatches or replays a save: " + change);
            }

            foreach (var drift in new[] { "client", "identity", "epoch", "offline" })
            {
                runtime = new object();
                ownerState = new(firstClient, "fixture-owner", 1, true);
                owner = new(ownerState, () => ownerState);
                selected = package; loaded = (package.Directory, package.Version);
                panels = [new(package.Id, "settings", "General", new TextBlock())];
                Open();
                var beforeClose = closed;
                ownerState = drift switch
                {
                    "client" => ownerState with { Client = replacementClient },
                    "identity" => ownerState with { Identity = "replacement" },
                    "epoch" => ownerState with { Epoch = 2 },
                    _ => ownerState with { Online = false }
                };
                SetStatus("Fixture owner status changed");
                Check(desktopPluginConfigurationWindow is null && closed == beforeClose + 1,
                    "actual status event closes owner drift without awaiting a presentation refresh: " + drift);
            }

            runtime = new object();
            ownerState = new(firstClient, "fixture-owner", 1, true);
            owner = new(ownerState, () => ownerState);
            selected = package; loaded = (package.Directory, package.Version);
            panels = [new(package.Id, "settings", "General", new TextBlock())];
            Open();
            var closeCount = closed;
            CloseDesktopPluginConfiguration();
            Check(closed == closeCount + 1 && desktopPluginConfigurationScope is null,
                "explicit user-close cleanup still releases editor admission");
            Open();
            closeCount = closed;
            ConnectionChanged();
            Check(closed == closeCount + 1 && queueConnectionEpoch == originalEpoch + 1 &&
                desktopPluginConfigurationWindow is null,
                "actual host connection-change hook closes immediately after advancing its epoch");
            Open();
            closeCount = closed;
            DisposeConfigurationDesktopPlugins();
            Check(closed == closeCount + 1 && desktopPluginConfigurationWindow is null,
                "normal host shutdown still closes the editor");
            Check(PresentationSource.FromVisual(this) is null && !IsVisible,
                "owner fixture never shows a window or starts its Loaded connection route");
            return checks;
        }
        finally
        {
            CloseDesktopPluginConfiguration();
            desktopPluginItems = originalItems;
            queueConnectionEpoch = originalEpoch;
        }
    }
}
