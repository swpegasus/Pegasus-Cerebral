using System.IO;

namespace Cerebral.Desktop.Plugins;

/// <summary>Replaces only one bounded portable-settings document; unrelated plugin data is never copied.</summary>
public sealed class PreparedCerebralPluginSettingsInstallation : IDisposable
{
    private readonly PreparedPluginFileInstallation installation;

    internal PreparedCerebralPluginSettingsInstallation(string dataRoot, string pluginId, byte[]? settings)
    {
        if (settings is null) { installation = new([]); return; }
        if (settings.Length > CerebralPluginPackageContract.MaximumSettingsBytes)
            throw new InvalidDataException("Portable plugin settings exceeded their bounded document size.");
        var destination = CerebralPluginPackage.ContainedResourcePath(Path.GetFullPath(dataRoot),
            pluginId + "/" + CerebralPluginPackageContract.CurrentSettingsFileName);
        installation = new([(destination, settings)]);
    }

    public void Publish() => installation.Publish();
    public void Commit() => installation.Commit();
    public void Dispose() => installation.Dispose();
}
