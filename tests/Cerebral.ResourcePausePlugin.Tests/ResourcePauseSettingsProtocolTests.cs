using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ResourcePausePlugin.Tests;

public static partial class Program
{
    private static void RunSettingsProtocolChecks()
    {
        SettingsDiscoveryAndReadFailuresStayClosed();
        SettingsSaveUncertaintyRequiresExplicitReload();
    }

    private static void SettingsDiscoveryAndReadFailuresStayClosed()
    {
        foreach (var failure in new[] { "describe-unavailable", "describe-malformed", "get-unavailable" })
        {
            var host = new FixtureHost { Reply = (operation, _, _) => Task.FromResult(operation == "models.list"
                ? Models(Model("chosen", "Chosen", true)) : throw new InvalidOperationException(operation)) };
            host.SettingsReply = (operation, arguments, token) =>
            {
                if (operation == CompleteSettingsContract.DescribeOperation && failure == "describe-unavailable")
                    return Task.FromResult(new DesktopServiceCommandResult(false, default, "fixture settings capabilities unavailable"));
                if (operation == CompleteSettingsContract.DescribeOperation && failure == "describe-malformed")
                {
                    var valid = host.DefaultSettingsReply(operation, arguments, token).Data
                        .Deserialize<CompleteSettingsCapabilities>(CerebralJson.Options)!;
                    return Task.FromResult(new DesktopServiceCommandResult(true,
                        CerebralJson.Element(valid with { PluginEnvelopeVersion = 99 })));
                }
                if (operation == CompleteSettingsContract.PluginGetOperation && failure == "get-unavailable")
                    return Task.FromResult(new DesktopServiceCommandResult(false, default, "fixture settings read unavailable"));
                return Task.FromResult(host.DefaultSettingsReply(operation, arguments, token));
            };
            Directory.CreateDirectory(host.DataDirectory);
            var legacyPath = Path.Combine(host.DataDirectory, "resource-pause.json");
            const string legacy = "fixture legacy preferences preserved byte for byte";
            File.WriteAllText(legacyPath, legacy);
            using var plugin = Create(host);
            var panel = plugin.ConfigurationPanels.Single(item => item.Id == "local-model").CreateView();
            var save = Find<Button>(panel, "ResourcePauseSavePreferences");
            var message = Find<TextBlock>(panel, "ResourcePausePreferenceStatus");
            PumpUntil(() => message.Text.Contains("unavailable", StringComparison.Ordinal));
            Check(!save.IsEnabled && host.SettingsState.Revision == 0 && host.SettingsState.Active is null &&
                  !host.Operations.Any(operation => operation is "settings.plugins.get" or "settings.plugins.save" or
                      CompleteSettingsContract.PluginSaveOperation || IsRoutingMutation(operation)) &&
                  File.ReadAllText(legacyPath) == legacy,
                failure + " preserves legacy data and disables saving/activation fallback without a settings or routing write");
            Check(failure == "get-unavailable"
                    ? host.Operations.Count(operation => operation == CompleteSettingsContract.PluginGetOperation) == 1
                    : !host.Operations.Contains(CompleteSettingsContract.PluginGetOperation),
                failure + " stops at the failed protocol boundary without another route or an automatic retry");
        }
    }

    private static void SettingsSaveUncertaintyRequiresExplicitReload()
    {
        var host = new FixtureHost { Reply = (operation, _, _) => Task.FromResult(operation == "models.list"
            ? Models(Model("chosen", "Chosen", true)) : throw new InvalidOperationException(operation)) };
        host.SettingsReply = (operation, arguments, token) =>
        {
            var result = host.DefaultSettingsReply(operation, arguments, token);
            return Task.FromResult(operation == CompleteSettingsContract.PluginSaveOperation
                ? new DesktopServiceCommandResult(false, default, "fixture lost save acknowledgement") : result);
        };
        using var plugin = Create(host);
        var panel = plugin.ConfigurationPanels.Single(item => item.Id == "local-model").CreateView();
        var selector = Find<ComboBox>(panel, "ResourcePause.LocalModelSelector");
        var save = Find<Button>(panel, "ResourcePauseSavePreferences");
        var message = Find<TextBlock>(panel, "ResourcePausePreferenceStatus");
        PumpUntil(() => save.IsEnabled);
        selector.SelectedValue = "chosen";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => message.Text.Contains("unavailable", StringComparison.Ordinal));
        Check(!save.IsEnabled && selector.SelectedValue as string == "chosen" && host.SettingsState.Revision == 1 &&
              host.SettingsState.Active?.Values.GetProperty("preferredLocalModelId").GetString() == "chosen" &&
              host.Operations.Count(operation => operation == CompleteSettingsContract.PluginSaveOperation) == 1 &&
              !host.Operations.Any(operation => operation is "settings.plugins.save" || IsRoutingMutation(operation)),
            "an unconfirmed save preserves the draft, acknowledges no success, disables another save and never replays or activates");
        host.SettingsReply = null;
        Find<Button>(panel, "ResourcePauseLoadPreferences").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => save.IsEnabled && message.Text.StartsWith("Saved preferences loaded", StringComparison.Ordinal));
        Check(selector.SelectedValue as string == "chosen" && host.SettingsState.Revision == 1 &&
              host.Operations.Count(operation => operation == CompleteSettingsContract.PluginSaveOperation) == 1,
            "explicit reload reconciles the actual saved revision through transfer.get without repeating the uncertain write");
    }
}
