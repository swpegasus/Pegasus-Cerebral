using System.IO;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;
using Cerebral.PackageSettings;

namespace Cerebral.ChatIconsPlugin;

public sealed partial class ChatIconsPlugin
{
    private readonly CancellationTokenSource settingsLifetime = new();
    private PackageSettingsOwnerClient? settingsClient;
    private readonly ChatIconsPresentationSettings presentationSettings = new();

    private void ConnectSettings(IDesktopPluginHost host)
    {
        if (host is IDesktopServiceHost service)
            settingsClient = new(service, new(PluginId, "global"), ChatIconsSettingsPolicy.PackageVersion);
        _ = RefreshCentralSettingsAsync();
    }

    private async Task RefreshCentralSettingsAsync()
    {
        if (settingsClient is null || settingsLifetime.IsCancellationRequested) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(settingsLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var state = await settingsClient.ReadAsync(timeout.Token);
            if (settingsLifetime.IsCancellationRequested) return;
            if (state.Active is { } active)
            {
                var admitted = new ChatIconsSettingsPolicy().Admit(active, null, false);
                if (!admitted.Accepted) { ReportHostStatus("Saved icon format is unsupported; existing presentation choices are preserved."); return; }
                SetCentralPreferences(state, ChatIconsSettingsPolicy.Decode(admitted.Values));
            }
        }
        catch (OperationCanceledException) when (settingsLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!settingsLifetime.IsCancellationRequested)
                ReportHostStatus("Shared icon settings are unavailable; existing local choices remain available.");
        }
    }

    private void SetCentralPreferences(PluginSettingsState state, ChatIconPreferences value)
    {
        if (settingsLifetime.IsCancellationRequested) return;
        if (presentationSettings.Apply(state.Revision, value)) RefreshHostPresentation();
    }

    private IReadOnlyDictionary<string, string>? SettingsChoiceGlyphs()
    {
        if (packDirectory is null) return null;
        try
        {
            var pack = AssetPack.Load(packDirectory);
            ValidatePickerPack(pack);
            return pack.Assets.ToDictionary(asset => asset.Key, asset => asset.Text, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or
            ArgumentException or FormatException or System.Xml.XmlException or KeyNotFoundException)
        { return null; } // Optional presentation enhancement; known ID labels remain usable.
    }

    private ChatIconPreferences PrepareExistingLocalChoices()
    {
        if (dataDirectory is null) throw new InvalidOperationException("Existing local icon choices are unavailable.");
        return ChatIconsLegacySettings.Read(dataDirectory);
    }
    private Task<DesktopOperationResult> CopyMeaningReferencesAsync(CancellationToken token)
    {
        if (Host is not IDesktopClipboardHost clipboard) throw new InvalidOperationException("Clipboard is unavailable.");
        var value = LoadMeaningMappings();
        var reference = "pegasus.chat-icons.meanings/1\n" +
            string.Join("\n", value.Select(pair => pair.Key + "=" + pair.Value));
        return clipboard.SetClipboardTextAsync(reference, token);
    }
}

internal static class ChatIconsLegacySettings
{
    internal static ChatIconPreferences Read(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new InvalidOperationException("An absolute icon data directory is required.");
        var roleFile = Path.Combine(directory, RoleSettings.FileName);
        var meaningFile = Path.Combine(directory, SemanticIconSettings.FileName);
        // Explicit review only. Never write, delete or rename either legacy file.
        return ChatIconsSettingsPolicy.PrepareLegacy(
            File.Exists(roleFile) ? DesktopPluginFiles.ReadUtf8(roleFile, 8192) : null,
            File.Exists(meaningFile) ? DesktopPluginFiles.ReadUtf8(meaningFile, 8192) : null);
    }
}

/// <summary>Late startup/picker reads cannot replace a newer acknowledged editor preference.</summary>
internal sealed class ChatIconsPresentationSettings
{
    private readonly object gate = new();
    private long revision = -1;
    private ChatIconPreferences? current;
    internal ChatIconPreferences? Current { get { lock (gate) return current; } }
    internal bool Apply(long nextRevision, ChatIconPreferences value)
    {
        lock (gate)
        {
            if (nextRevision < revision) return false;
            var equal = current is not null && System.Text.Json.JsonElement.DeepEquals(
                ChatIconsSettingsPolicy.Encode(current), ChatIconsSettingsPolicy.Encode(value));
            if (nextRevision == revision && !equal)
                throw new InvalidDataException("The icon preference revision is inconsistent; the last acknowledged choices are preserved.");
            revision = nextRevision; current = value;
            return !equal;
        }
    }
}
