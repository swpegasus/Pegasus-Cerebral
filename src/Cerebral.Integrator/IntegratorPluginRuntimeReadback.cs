using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Integrator;

internal sealed record ReceiverPluginRuntimeLoadStatus(string Id, bool Loaded, string? Error);
internal sealed record ReceiverPluginRuntimePackage(string Directory, string Version);
internal sealed record ReceiverPluginRuntimeSnapshot(string Schema, ReceiverPluginOwnerSnapshot Owner,
    string PluginId, string Scope, bool RuntimeAvailable, string State,
    ReceiverPluginRuntimeLoadStatus? Status, ReceiverPluginRuntimePackage? LoadedPackage, bool? Retiring);

internal static partial class ConfigurationReadbackProtocol
{
    internal const string PluginRuntimeOperation = "plugin-runtime.inspect";
    internal const string PluginRuntimeSchema = "pegasus.configuration-plugin-runtime/1";

    internal static bool RuntimePluginId(string? id)
    {
        if (id is null) return false;
        try { NativeDesktopPluginPackage.ValidateId(id); return true; }
        catch (Exception error) when (error is ArgumentException or System.IO.InvalidDataException) { return false; }
    }

    internal static ReceiverPluginRuntimeSnapshot RuntimeSnapshot(ReceiverPluginOwnerSnapshot owner, string pluginId,
        DesktopPluginLoadStatus? status, (string Directory, string Version)? package)
    {
        if (!RuntimePluginId(pluginId) || status is not null && status.Id != pluginId ||
            (status?.Loaded == true) != (package is not null))
            throw new InvalidOperationException("The current runtime observation is inconsistent.");
        var row = status is null ? null : new ReceiverPluginRuntimeLoadStatus(status.Id, status.Loaded, status.Error);
        var loaded = package is { } actual ? new ReceiverPluginRuntimePackage(actual.Directory, actual.Version) : null;
        // The pinned SDK reports retiring instances in Status, but does not expose a
        // structured retiring bit. Never parse a diagnostic string to manufacture it.
        // Absent means no loaded or retiring entry in THIS current window runtime;
        // an unloaded diagnostic remains ambiguous and cannot clear a retirement gate.
        return new(PluginRuntimeSchema, owner, pluginId, "current-window-runtime", true,
            row is null ? "absent" : row.Loaded ? "active" : "inactive-with-diagnostic",
            row, loaded, row is null || row.Loaded ? false : null);
    }

    private static bool CompletePluginRuntime(ReceiverPluginRuntimeSnapshot? value, ReceiverExpected expected, string pluginId)
    {
        if (value is null || value.Schema != PluginRuntimeSchema || value.Scope != "current-window-runtime" ||
            !value.RuntimeAvailable || value.PluginId != pluginId || !CompletePluginOwner(value.Owner, expected))
            return false;
        return value.State switch
        {
            "absent" => value.Status is null && value.LoadedPackage is null && value.Retiring == false,
            "active" => value.Status is { Loaded: true } row && row.Id == pluginId && value.Retiring == false &&
                value.LoadedPackage is { } package && Text(package.Directory, int.MaxValue) && Text(package.Version, 256),
            "inactive-with-diagnostic" => value.Status is { Loaded: false } stopped && stopped.Id == pluginId &&
                value.LoadedPackage is null && value.Retiring is null,
            _ => false
        };
    }
}

public sealed partial class IntegratorWindow
{
    internal ReceiverPluginRuntimeSnapshot CaptureReceiverPluginRuntime(ReceiverExpected expected, string pluginId)
    {
        RequireReceiverPageOwner(expected);
        if (!ConfigurationReadbackProtocol.RuntimePluginId(pluginId))
            throw new ConfigurationPageControlRefusal("plugin_runtime_target_invalid");
        var runtime = configurationPlugins ?? throw new InvalidOperationException("The current Configuration runtime is unavailable.");
        var owner = CaptureReceiverPluginOwner(expected);
        var rows = runtime.Status.Where(item => item.Id == pluginId).Take(2).ToArray();
        if (rows.Length > 1) throw new InvalidOperationException("The current runtime returned duplicate target status.");
        var package = runtime.GetLoadedPackage(pluginId);
        // These public SDK reads use the existing adopted runtime only. No catalog
        // enumeration, reload, plugin callback, worker call, mount or save occurs.
        if (!ReferenceEquals(runtime, configurationPlugins))
            throw new ConfigurationPageControlRefusal("owner_binding_changed");
        return ConfigurationReadbackProtocol.RuntimeSnapshot(owner, pluginId, rows.SingleOrDefault(), package);
    }
}
