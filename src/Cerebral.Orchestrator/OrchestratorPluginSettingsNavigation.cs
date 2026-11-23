using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

/// <summary>Bounded, same-owner Configuration receiver navigation for a loaded desktop plugin.</summary>
public sealed partial class OrchestratorWindow
{
    private readonly SemaphoreSlim pluginSettingsNavigationGate = new(1, 1);
    private const string ConfigurationReceiverProduct = "Pegasus Cerebral Configuration Readback";
    private const string ConfigurationPanelPage = "ConfigurationTabDesktopPlugins";
    private const string ConfigurationPageSchema = "pegasus.configuration-page-capabilities/2";
    private const int ConfigurationReceiverMaximumResponseBytes = 32768;
    private bool ordinaryPluginSettingsNavigation;

    internal void EnableOrdinaryPluginSettingsNavigation() => ordinaryPluginSettingsNavigation = true;

    private DesktopSettingsNavigationAvailability PluginSettingsNavigationAvailability(string pluginId, string packageDirectory, string panelId)
    {
        Dispatcher.VerifyAccess();
        if (Lifetime.IsCancellationRequested || configurationLaunch is null && !ordinaryPluginSettingsNavigation || !IsConnected ||
            desktopPluginOwner?.MatchesOwner() != true || desktopPlugins is not { } runtime ||
            !ConfigurationPanelIdentifier(panelId))
            return new(false, configurationLaunch is null && !ordinaryPluginSettingsNavigation
                ? "Direct settings navigation is unavailable for this launch. Open Configuration > Plugins & tools > Desktop plugins, select the plugin and choose Configure…"
                : "Plugin settings are unavailable for the current connection.");
        var loaded = runtime.GetLoadedPackage(pluginId);
        if (loaded is null || !loaded.Value.Directory.Equals(packageDirectory, StringComparison.OrdinalIgnoreCase))
            return new(false, "This plugin is no longer active. Refresh plugins before opening its settings.");
        try
        {
            var selected = desktopPluginCatalog.List().SingleOrDefault(item => item.Id == pluginId);
            if (selected is null || !selected.Enabled || selected.Error is not null ||
                !selected.Directory.Equals(packageDirectory, StringComparison.OrdinalIgnoreCase))
                return new(false, "The selected plugin package changed. Refresh plugins before opening its settings.");
            if (configurationLaunch is { } mapping)
                RequireConfigurationLaunchBinding(mapping.Target);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            SecurityException or JsonException or ArgumentException or InvalidOperationException)
        {
            return new(false, "The Configuration mapping or plugin owner is unavailable. Refresh the connection before opening settings.");
        }
        // Panel declaration is checked by the current Configuration receiver. This preflight
        // only establishes that the pinned route and this plugin generation are available.
        return new(true);
    }

    private async Task<DesktopOperationResult> OpenPluginSettingsAsync(string pluginId, string packageDirectory,
        string panelId, CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        var available = PluginSettingsNavigationAvailability(pluginId, packageDirectory, panelId);
        if (!available.Available) return new(DesktopOperationState.Rejected, available.Message);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime.Token);
        token = linked.Token;
        if (!await pluginSettingsNavigationGate.WaitAsync(0, token))
            return new(DesktopOperationState.Rejected, "Another Configuration navigation is in progress.");
        var launchAttempted = false;
        try
        {
            available = PluginSettingsNavigationAvailability(pluginId, packageDirectory, panelId);
            if (!available.Available) return new(DesktopOperationState.Rejected, available.Message);
            var mainPackage = desktopPlugins?.GetLoadedPackage(pluginId);
            if (mainPackage is null || !mainPackage.Value.Directory.Equals(packageDirectory, StringComparison.OrdinalIgnoreCase))
                return new(DesktopOperationState.Rejected, "The plugin generation changed before opening settings.");
            var expectedVersion = mainPackage.Value.Version;
            if (configurationLaunch is not { } mapping)
                return await OpenOrdinaryPluginSettingsAsync(pluginId, packageDirectory, panelId, expectedVersion, token);
            var launch = await LaunchMappedConfigurationAsync(token);
            if (launch.Code == "configuration_launch_busy")
                return new(DesktopOperationState.Rejected, "Configuration is already opening. Inspect it before another action.");
            if (launch.Status == "unconfirmed")
                return new(DesktopOperationState.Uncertain, "Configuration may have opened. Inspect it before another action.");
            if (launch.Status != "launch-requested")
                return new(DesktopOperationState.Failed, "Configuration could not be opened for this Worker: " + launch.Code);
            launchAttempted = true;
            var receiver = await WaitForConfigurationReceiverAsync(mapping.Target, token);
            var startup = await WaitForConfigurationStartupAsync(
                readToken => CallConfigurationReceiverAsync(receiver, "snapshot.inspect", null, readToken),
                receiver.Expected, TimeSpan.FromSeconds(8), token);
            if (!startup.Available) return new(DesktopOperationState.Rejected, startup.Message);
            var outcome = await NavigateConfigurationPanelAsync(receiver, mapping.OwnerBinding,
                pluginId, packageDirectory, expectedVersion, panelId, token);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            return launchAttempted
                ? new(DesktopOperationState.Uncertain, "Configuration navigation was interrupted after launch. Inspect the panel before another action.")
                : new(DesktopOperationState.Rejected, "Settings navigation was cancelled before launch.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            TimeoutException or JsonException or SecurityException or ArgumentException or InvalidOperationException or Win32Exception)
        {
            return new(launchAttempted ? DesktopOperationState.Uncertain : DesktopOperationState.Failed,
                launchAttempted
                    ? "Configuration opened, but the exact settings panel could not be confirmed. Inspect it before another action."
                    : "Configuration navigation failed before launch: " + error.Message);
        }
        finally { pluginSettingsNavigationGate.Release(); }
    }

    private async Task<DesktopOperationResult> OpenOrdinaryPluginSettingsAsync(string pluginId,
        string packageDirectory, string panelId, string version, CancellationToken token)
    {
        var client = Client;
        var connection = ServiceConnection;
        var owner = desktopPluginOwner;
        var identity = ConfigurationPluginSettingsChannel.ConnectionKey(ServiceConnectionIdentity);
        var health = await client.GetAsync<HostInfo>("health", cancellationToken: token);
        connection.ValidateHost(health);
        if (string.IsNullOrWhiteSpace(health.InstallationId))
            return new(DesktopOperationState.Rejected, "The current Worker did not identify its installation. Reconnect before opening settings.");

        bool Current() => IsConnected && ReferenceEquals(client, Client) && ReferenceEquals(connection, ServiceConnection) &&
            ReferenceEquals(owner, desktopPluginOwner) && PluginSettingsNavigationAvailability(pluginId, packageDirectory, panelId).Available &&
            desktopPlugins?.GetLoadedPackage(pluginId) is { } current && current.Version == version &&
            current.Directory.Equals(packageDirectory, StringComparison.OrdinalIgnoreCase);
        if (!Current()) return new(DesktopOperationState.Rejected, "The connection or plugin changed before opening settings.");
        var request = new ConfigurationPluginSettingsRequest(Guid.NewGuid().ToString("N"), pluginId, panelId,
            packageDirectory, version, connection.Transport, identity, health.InstallationId);
        var reply = await ConfigurationPluginSettingsChannel.OpenAsync(DesktopInstancePaths.Current.UserDataDirectory,
            request, () => Dispatcher.InvokeAsync(Current).Task, token);
        if (!Current() || reply.State == "unconfirmed")
            return new(DesktopOperationState.Uncertain, "Configuration may have opened. Inspect its AI Dictation or plugin settings before trying again.");
        return reply.State switch
        {
            "opened" => new(DesktopOperationState.Completed, "The requested plugin settings panel is open and ready."),
            "changed" => new(DesktopOperationState.Rejected, "Configuration is connected to a different Worker or plugin generation. Refresh both windows before opening settings."),
            _ => new(DesktopOperationState.Failed, "Configuration could not open these settings. Open Configuration > Plugins & tools > Desktop plugins and check the selected plugin.")
        };
    }

    private enum ConfigurationStartupReadiness { Waiting, Ready, Unavailable }

    private static ConfigurationStartupReadiness ReadConfigurationStartupReadiness(
        ConfigurationReceiverResponse response, ConfigurationReceiverExpected expected)
    {
        if (response.State != "completed" || response.Data is not { ValueKind: JsonValueKind.Object } data)
            throw new InvalidDataException("Configuration startup observation is incomplete.");
        try
        {
            var attachment = data.GetProperty("attachment");
            if (data.GetProperty("processId").GetInt32() != expected.ProcessId ||
                data.GetProperty("processStartUtcTicks").GetString() != expected.ProcessStartUtcTicks ||
                data.GetProperty("configurationStateDirectory").GetString() != expected.ConfigurationStateDirectory ||
                attachment.GetProperty("pipeName").GetString() != expected.Attachment.PipeName ||
                attachment.GetProperty("instanceId").GetString() != expected.Attachment.InstanceId ||
                attachment.GetProperty("workerDataDirectory").GetString() != expected.Attachment.WorkerDataDirectory ||
                attachment.GetProperty("installationId").GetString() != expected.Attachment.InstallationId)
                throw new InvalidDataException("Configuration startup observation belongs to another owner.");
            var state = data.GetProperty("state").GetString();
            var initial = data.GetProperty("initialConnection").GetString();
            if (state == "connecting" && initial == "pending") return ConfigurationStartupReadiness.Waiting;
            if (initial != "connected" || !data.GetProperty("connected").GetBoolean())
                return ConfigurationStartupReadiness.Unavailable;
            if (state == "partial") return ConfigurationStartupReadiness.Waiting;
            if (state != "ready") return ConfigurationStartupReadiness.Unavailable;
            var loaded = data.GetProperty("loaded");
            if (loaded.GetProperty("scope").GetString() != "cached-settings-profiles-selected-model-catalog" ||
                !loaded.GetProperty("settingsCurrent").GetBoolean() ||
                !loaded.GetProperty("profilesCurrent").GetBoolean() ||
                !loaded.GetProperty("modelCatalogCurrent").GetBoolean() ||
                loaded.GetProperty("modelRefreshInProgress").GetBoolean())
                throw new InvalidDataException("Configuration reports ready with incomplete adopted settings.");
            return data.GetProperty("visible").GetBoolean()
                ? ConfigurationStartupReadiness.Ready : ConfigurationStartupReadiness.Waiting;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or
            FormatException or OverflowException)
        { throw new InvalidDataException("Configuration startup observation is malformed.", error); }
    }

    private static async Task<DesktopSettingsNavigationAvailability> WaitForConfigurationStartupAsync(
        Func<CancellationToken, Task<ConfigurationReceiverResponse>> inspect,
        ConfigurationReceiverExpected expected, TimeSpan maximumWait, CancellationToken token)
    {
        if (maximumWait <= TimeSpan.Zero || maximumWait > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(maximumWait));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(maximumWait);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var snapshot = await inspect(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                var readiness = ReadConfigurationStartupReadiness(snapshot, expected);
                if (readiness == ConfigurationStartupReadiness.Ready) return new(true);
                if (readiness == ConfigurationStartupReadiness.Unavailable)
                    return new(false, "Configuration is not ready for settings navigation. Inspect its connection before trying again.");
                // Only bounded observations repeat. No page or panel mutation has been submitted.
                // Initial hydration may select its setup page; wait for that work before taking a CAS snapshot.
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(false, "Configuration is still preparing its settings. Wait for the window to finish loading, then try again."); }
    }

    private async Task<DesktopOperationResult> NavigateConfigurationPanelAsync(ConfigurationReceiverDescriptor receiver,
        ConfigurationManagedPluginOwnerBinding ownerBinding, string pluginId, string packageDirectory,
        string expectedVersion, string panelId, CancellationToken token)
    {
        // Reads may be retried; each navigation mutation is submitted once with a fresh request ID.
        var caps = await CallConfigurationReceiverAsync(receiver, "capabilities.inspect", null, token);
        if (caps.State != "completed" || caps.Capabilities?.Schema != ConfigurationPageSchema ||
            caps.Capabilities.ExactPluginPanelSelection != true || caps.Capabilities.ExactPluginGeneration != true ||
            caps.Capabilities.Operations is null || !caps.Capabilities.Operations.Contains("plugin.panel.open", StringComparer.Ordinal) ||
            !caps.Capabilities.Operations.Contains("plugin-owner.inspect", StringComparer.Ordinal) ||
            !caps.Capabilities.Operations.Contains("plugin-runtime.inspect", StringComparer.Ordinal) ||
            caps.Capabilities.PageTargets is null || !caps.Capabilities.PageTargets.Contains(ConfigurationPanelPage, StringComparer.Ordinal))
            return new(DesktopOperationState.Rejected, "This Configuration receiver cannot select an exact plugin settings panel.");
        var owner = await CallConfigurationReceiverAsync(receiver, "plugin-owner.inspect", null, token);
        if (!ConfigurationOwnerMatches(owner, receiver.Expected, ownerBinding.OwnerBindingId,
            ownerBinding.CatalogRoot, ownerBinding.PluginDataRoot))
            return new(DesktopOperationState.Uncertain, "The Configuration receiver's plugin owner could not be matched to this Main. Inspect it before another action.");
        var runtime = await CallConfigurationReceiverAsync(receiver, "plugin-runtime.inspect", null, token, pluginId);
        if (runtime.State != "completed" || !ConfigurationRuntimeMatches(runtime.PluginRuntimeData,
            receiver.Expected, ownerBinding.OwnerBindingId, ownerBinding.CatalogRoot, ownerBinding.PluginDataRoot,
            pluginId, packageDirectory, expectedVersion))
            return new(DesktopOperationState.Uncertain,
                "Configuration has a different or unavailable plugin generation. Inspect its plugin status before another action.");
        var page = RequirePage(await CallConfigurationReceiverAsync(receiver, "page.inspect", null, token), receiver);
        if (!await PluginSettingsOwnerCurrentAsync(pluginId, packageDirectory, expectedVersion, panelId))
            return new(DesktopOperationState.Rejected, "The plugin or Configuration owner changed before navigation.");
        if (page.PageId != ConfigurationPanelPage)
        {
            var openPage = new ConfigurationReceiverPageCommand(ConfigurationPanelPage, null,
                page.PageId ?? "", page.Revision);
            ConfigurationReceiverResponse selected;
            try
            {
                selected = await SubmitConfigurationMutationAsync(receiver, ownerBinding, "page.open", openPage, token)
                    ?? throw new UnauthorizedAccessException("The Configuration owner changed before page selection.");
            }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or InvalidDataException)
            { return new(DesktopOperationState.Uncertain, "Configuration page selection may have occurred. Inspect it before another action."); }
            if (selected.State == "rejected") return new(DesktopOperationState.Rejected, "Configuration page selection was rejected: " + selected.ErrorCode);
            if (selected.State is not ("completed" or "accepted"))
                return new(DesktopOperationState.Uncertain, "Configuration page selection was not confirmed.");
            page = selected.State == "completed" ? RequirePage(selected, receiver)
                : RequirePage(await CallConfigurationReceiverAsync(receiver, "page.inspect", null, token), receiver);
            if (page.PageId != ConfigurationPanelPage)
                return new(DesktopOperationState.Uncertain, "The requested Configuration page is not selected after its navigation receipt.");
        }
        if (!await PluginSettingsOwnerCurrentAsync(pluginId, packageDirectory, expectedVersion, panelId))
            return new(DesktopOperationState.Rejected, "The plugin or Configuration owner changed before panel selection.");
        var openPanel = new ConfigurationReceiverPageCommand(null, pluginId, page.PageId!, page.Revision,
            panelId, packageDirectory, expectedVersion);
        ConfigurationReceiverResponse opened;
        try
        {
            opened = await SubmitConfigurationMutationAsync(receiver, ownerBinding, "plugin.panel.open", openPanel, token)
                ?? throw new UnauthorizedAccessException("The Configuration owner changed before panel selection.");
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or InvalidDataException)
        { return new(DesktopOperationState.Uncertain, "The settings panel may have opened. Inspect it before another action."); }
        if (opened.State == "rejected") return new(DesktopOperationState.Rejected, "The requested plugin panel was rejected: " + opened.ErrorCode);
        if (opened.State is not ("completed" or "accepted"))
            return new(DesktopOperationState.Uncertain, "The settings panel selection was not confirmed.");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (!await PluginSettingsOwnerCurrentAsync(pluginId, packageDirectory, expectedVersion, panelId))
                return new(DesktopOperationState.Uncertain, "The plugin owner changed after panel selection. Inspect the current Configuration window.");
            ConfigurationReceiverPageSnapshot current;
            try { current = RequirePage(await CallConfigurationReceiverAsync(receiver, "page.inspect", null, token), receiver); }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or InvalidDataException)
            { return new(DesktopOperationState.Uncertain, "The selected panel could not be inspected after navigation."); }
            if (current.State == "ready" && current.Loaded && current.Visible &&
                current.PageId == ConfigurationPanelPage && current.PluginId == pluginId &&
                current.PluginPackageDirectory is not null &&
                current.PluginPackageDirectory.Equals(packageDirectory, StringComparison.OrdinalIgnoreCase) &&
                current.PluginPackageVersion == expectedVersion &&
                current.SelectedPluginPanelId == panelId && current.PluginPanelAvailable && current.PluginPanelReady &&
                current.PluginPanelSelectionConfirmed)
                return new(DesktopOperationState.Completed, "The requested plugin settings panel is open and ready.");
            if (current.PluginId is not null && current.PluginId != pluginId)
                return new(DesktopOperationState.Uncertain, "Another plugin panel was selected after navigation.");
            await Task.Delay(100, token);
        }
        return new(DesktopOperationState.Uncertain, "The panel selection was accepted, but its visible ready state was not confirmed.");
    }

    private async Task<bool> PluginSettingsOwnerCurrentAsync(string pluginId, string packageDirectory,
        string expectedVersion, string panelId) =>
        await Dispatcher.InvokeAsync(() => PluginSettingsNavigationAvailability(pluginId, packageDirectory, panelId).Available &&
            desktopPlugins?.GetLoadedPackage(pluginId) is { } current && current.Version == expectedVersion &&
            current.Directory.Equals(packageDirectory, StringComparison.OrdinalIgnoreCase)).Task;

    private static ConfigurationReceiverPageSnapshot RequirePage(ConfigurationReceiverResponse response,
        ConfigurationReceiverDescriptor receiver)
    {
        if (response.State != "completed" || response.PageData is not { } page ||
            page.ProcessId != receiver.Expected.ProcessId || page.ProcessStartUtcTicks != receiver.Expected.ProcessStartUtcTicks ||
            page.Revision < 1)
            throw new InvalidDataException("The Configuration page readback is incomplete or belongs to another receiver.");
        return page;
    }

    private async Task<ConfigurationReceiverDescriptor> WaitForConfigurationReceiverAsync(ExistingWorkerDesktopOptions target,
        CancellationToken token)
    {
        var path = Path.Combine(target.DesktopDataDirectory, "Desktop", "configuration-readback.json");
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The Configuration receiver descriptor is a linked file.");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length is < 1 or > 16384) throw new InvalidDataException("The Configuration receiver descriptor has an invalid size.");
                var bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, token);
                using var document = JsonDocument.Parse(bytes);
                RejectDuplicateProperties(document.RootElement);
                var value = document.RootElement.Deserialize<ConfigurationReceiverDescriptor>(CerebralJson.Options)
                    ?? throw new InvalidDataException("The Configuration receiver descriptor is empty.");
                if (value.Expected is null || value.Expected.Attachment is null ||
                    value.Expected.ConfigurationStateDirectory is null || value.Expected.Attachment.WorkerDataDirectory is null ||
                    value.Extra is { Count: > 0 } || value.Expected.Extra is { Count: > 0 } ||
                    value.Expected.Attachment.Extra is { Count: > 0 } ||
                    value.Product != ConfigurationReceiverProduct || value.Protocol != 1 ||
                    value.InstanceId is not { Length: 32 } || !value.InstanceId.All(Uri.IsHexDigit) ||
                    value.PipeName != "Pegasus.Cerebral.Configuration.Readback." + value.InstanceId ||
                    value.Expected.ProcessId <= 0 ||
                    !long.TryParse(value.Expected.ProcessStartUtcTicks, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0 ||
                    !value.Expected.ConfigurationStateDirectory.Equals(target.DesktopDataDirectory, StringComparison.OrdinalIgnoreCase) ||
                    value.Expected.Attachment.PipeName != target.Endpoint.PipeName ||
                    value.Expected.Attachment.InstanceId != target.Endpoint.InstanceId ||
                    !value.Expected.Attachment.WorkerDataDirectory.Equals(target.WorkerDataDirectory, StringComparison.OrdinalIgnoreCase) ||
                    value.Expected.Attachment.InstallationId != target.InstallationId)
                    throw new InvalidDataException("The Configuration receiver descriptor does not match this pinned installation and Worker.");
                return value;
            }
            catch (FileNotFoundException) when (deadline.Elapsed < TimeSpan.FromSeconds(4)) { }
            catch (DirectoryNotFoundException) when (deadline.Elapsed < TimeSpan.FromSeconds(4)) { }
            await Task.Delay(100, token);
            if (deadline.Elapsed >= TimeSpan.FromSeconds(4))
                throw new TimeoutException("The Configuration receiver was not available after the mapped launch.");
        }
    }

    private async Task<ConfigurationReceiverResponse> CallConfigurationReceiverAsync(ConfigurationReceiverDescriptor receiver,
        string operation, ConfigurationReceiverPageCommand? page, CancellationToken token, string? pluginId = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var pipe = new NamedPipeClientStream(".", receiver.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(1500, deadline.Token);
        var peer = InstalledWorkerWindows.ObserveServerProcess(pipe);
        var expectedTicks = long.Parse(receiver.Expected.ProcessStartUtcTicks, CultureInfo.InvariantCulture);
        using var current = Process.GetCurrentProcess();
        var currentSid = WindowsIdentity.GetCurrent().User?.Value;
        if (peer.ProcessId != receiver.Expected.ProcessId ||
            DateTime.FromFileTimeUtc(peer.CreationTimeFileTime).Ticks != expectedTicks ||
            peer.AccountSid != currentSid || peer.SessionId != current.SessionId ||
            !peer.Executable.Equals(ConfigurationLaunchEntry.ResolveManagedHost(), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The connected Configuration receiver is not the current pinned user process.");
        var mappedEntry = configurationLaunch?.Entry.Path
            ?? throw new UnauthorizedAccessException("The Configuration receiver has no pinned managed entry.");
        var argv = WindowsProcessArguments.ReadNativeArgv(peer.ProcessId);
        if (!ConfigurationManagedEntryMatches(argv, peer.Executable, mappedEntry) ||
            InstalledWorkerWindows.ObserveServerProcess(pipe) != peer)
            throw new UnauthorizedAccessException("The connected Configuration receiver does not run the pinned managed entry.");
        var requestId = Guid.NewGuid().ToString("N");
        var request = new ConfigurationReceiverRequest(ConfigurationReceiverProduct, 1, receiver.InstanceId,
            requestId, operation, receiver.Expected, page, pluginId);
        await LocalWorkerProtocol.WriteAsync(pipe, request, deadline.Token);
        var response = await LocalWorkerProtocol.ReadAsync<ConfigurationReceiverResponse>(pipe, deadline.Token);
        if (response.Extra is { Count: > 0 } || response.Capabilities?.Extra is { Count: > 0 } ||
            response.Product != ConfigurationReceiverProduct || response.Protocol != 1 ||
            response.InstanceId != receiver.InstanceId || response.RequestId != requestId || response.Operation != operation ||
            JsonSerializer.SerializeToUtf8Bytes(response, CerebralJson.Options).Length > ConfigurationReceiverMaximumResponseBytes)
            throw new InvalidDataException("The Configuration receiver returned an invalid or unrelated response.");
        return response;
    }

    private static bool ConfigurationPanelIdentifier(string? id) => id is { Length: > 0 and <= 128 } &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool ConfigurationManagedEntryMatches(string[] argv, string executable, string mappedEntry) =>
        argv.Length >= 2 && argv[0].Equals(executable, StringComparison.OrdinalIgnoreCase) &&
        argv[1].Equals(mappedEntry, StringComparison.OrdinalIgnoreCase);

    private static bool ConfigurationOwnerMatches(ConfigurationReceiverResponse response,
        ConfigurationReceiverExpected expected, string ownerBindingId, string catalogRoot, string pluginDataRoot) =>
        response.State == "completed" && response.PluginOwnerData is { } owner &&
        owner.Schema == "pegasus.configuration-plugin-owner/1" && owner.Mode == "managed" &&
        owner.OwnerBindingId == ownerBindingId && owner.ProcessId == expected.ProcessId &&
        owner.ProcessStartUtcTicks == expected.ProcessStartUtcTicks && owner.Attachment == expected.Attachment &&
        owner.ConfigurationStateDirectory is not null &&
        owner.ConfigurationStateDirectory.Equals(expected.ConfigurationStateDirectory, StringComparison.OrdinalIgnoreCase) &&
        owner.CatalogRoot is not null && owner.CatalogRoot.Equals(catalogRoot, StringComparison.OrdinalIgnoreCase) &&
        owner.PluginDataRoot is not null && owner.PluginDataRoot.Equals(pluginDataRoot, StringComparison.OrdinalIgnoreCase);

    private async Task<ConfigurationReceiverResponse?> SubmitConfigurationMutationAsync(
        ConfigurationReceiverDescriptor receiver, ConfigurationManagedPluginOwnerBinding binding,
        string operation, ConfigurationReceiverPageCommand page, CancellationToken token)
    {
        var owner = await CallConfigurationReceiverAsync(receiver, "plugin-owner.inspect", null, token);
        return await SubmitIfConfigurationOwnerMatchesAsync(owner, receiver.Expected,
            binding.OwnerBindingId, binding.CatalogRoot, binding.PluginDataRoot,
            () => CallConfigurationReceiverAsync(receiver, operation, page, token));
    }

    private static async Task<ConfigurationReceiverResponse?> SubmitIfConfigurationOwnerMatchesAsync(
        ConfigurationReceiverResponse owner, ConfigurationReceiverExpected expected,
        string ownerBindingId, string catalogRoot, string pluginDataRoot,
        Func<Task<ConfigurationReceiverResponse>> submit)
    {
        if (!ConfigurationOwnerMatches(owner, expected, ownerBindingId, catalogRoot, pluginDataRoot)) return null;
        return await submit();
    }

    private static bool ConfigurationRuntimeMatches(ConfigurationReceiverPluginRuntimeSnapshot? runtime,
        ConfigurationReceiverExpected expected, string ownerBindingId, string catalogRoot, string pluginDataRoot,
        string pluginId, string packageDirectory, string expectedVersion) =>
        runtime is { Schema: "pegasus.configuration-plugin-runtime/1", Scope: "current-window-runtime",
            RuntimeAvailable: true, State: "active", Retiring: false,
            Status: { Loaded: true } status, LoadedPackage: { } package, Owner: { } owner } &&
        runtime.PluginId == pluginId && status.Id == pluginId &&
        package.Directory is not null && package.Directory.Equals(packageDirectory, StringComparison.OrdinalIgnoreCase) &&
        package.Version == expectedVersion && owner.OwnerBindingId == ownerBindingId &&
        owner.ProcessId == expected.ProcessId && owner.ProcessStartUtcTicks == expected.ProcessStartUtcTicks &&
        owner.Attachment == expected.Attachment && owner.ConfigurationStateDirectory is not null &&
        owner.ConfigurationStateDirectory.Equals(expected.ConfigurationStateDirectory, StringComparison.OrdinalIgnoreCase) &&
        owner.CatalogRoot is not null && owner.CatalogRoot.Equals(catalogRoot, StringComparison.OrdinalIgnoreCase) &&
        owner.PluginDataRoot is not null && owner.PluginDataRoot.Equals(pluginDataRoot, StringComparison.OrdinalIgnoreCase);

    internal static int CheckPluginSettingsGenerationFixture()
    {
        const string id = "fixture.plugin", directory = "C:\\fixture\\plugin-generation", version = "1.2.3";
        const string bindingId = "fixture-binding", catalog = "C:\\fixture\\catalog", data = "C:\\fixture\\data";
        var attachment = new ConfigurationReceiverAttachment("fixture-pipe", "fixture-instance", "C:\\fixture\\worker", "fixture-install");
        var expected = new ConfigurationReceiverExpected(1234, "5678", attachment, "C:\\fixture\\configuration");
        var owner = new ConfigurationReceiverPluginOwnerSnapshot("pegasus.configuration-plugin-owner/1",
            DateTimeOffset.UtcNow, expected.ProcessId, expected.ProcessStartUtcTicks, attachment,
            expected.ConfigurationStateDirectory, "managed", bindingId, catalog, data, null);
        var valid = new ConfigurationReceiverPluginRuntimeSnapshot("pegasus.configuration-plugin-runtime/1", owner,
            id, "current-window-runtime", true, "active", new(id, true, null), new(directory, version), false);
        bool Match(ConfigurationReceiverPluginRuntimeSnapshot? value) =>
            ConfigurationRuntimeMatches(value, expected, bindingId, catalog, data, id, directory, version);
        var count = 0;
        void Check(bool condition)
        { if (!condition) throw new InvalidOperationException("The exact plugin generation admission fixture failed."); count++; }
        Check(Match(valid));
        Check(!Match(null));
        Check(!Match(valid with { PluginId = "other.plugin" }));
        Check(!Match(valid with { State = "inactive-with-diagnostic" }));
        Check(!Match(valid with { Retiring = true }));
        Check(!Match(valid with { Status = new(id, false, "retiring") }));
        Check(!Match(valid with { LoadedPackage = new("C:\\fixture\\older-generation", version) }));
        Check(!Match(valid with { LoadedPackage = new(directory, "1.2.2") }));
        Check(!Match(valid with { Owner = owner with { OwnerBindingId = "other-binding" } }));
        Check(!Match(valid with { Owner = owner with { CatalogRoot = "C:\\fixture\\other-catalog" } }));
        Check(!Match(valid with { Owner = owner with { PluginDataRoot = "C:\\fixture\\other-data" } }));
        Check(!Match(valid with { Owner = owner with { ProcessId = 4321 } }));
        return count;
    }

    internal static int CheckPluginSettingsOwnerFixture()
    {
        const string bindingId = "fixture-binding", catalog = "C:\\fixture\\catalog", data = "C:\\fixture\\data";
        var attachment = new ConfigurationReceiverAttachment("fixture-pipe", "fixture-instance", "C:\\fixture\\worker", "fixture-install");
        var expected = new ConfigurationReceiverExpected(1234, "5678", attachment, "C:\\fixture\\configuration");
        var owner = new ConfigurationReceiverPluginOwnerSnapshot("pegasus.configuration-plugin-owner/1",
            DateTimeOffset.UtcNow, expected.ProcessId, expected.ProcessStartUtcTicks, attachment,
            expected.ConfigurationStateDirectory, "managed", bindingId, catalog, data, null);
        var valid = new ConfigurationReceiverResponse(ConfigurationReceiverProduct, 1, "fixture-instance", "fixture-request",
            "plugin-owner.inspect", "completed", null, null, null, null, owner, null);
        var count = 0;
        void Check(bool condition)
        { if (!condition) throw new InvalidOperationException("The Configuration owner mutation admission fixture failed."); count++; }
        void CheckAdmission(ConfigurationReceiverResponse candidate, bool permitted)
        {
            var sends = 0;
            var result = SubmitIfConfigurationOwnerMatchesAsync(candidate, expected, bindingId, catalog, data, () =>
            {
                sends++;
                return Task.FromResult(candidate);
            }).GetAwaiter().GetResult();
            Check(permitted ? sends == 1 && result == candidate : sends == 0 && result is null);
        }
        CheckAdmission(valid, true);
        CheckAdmission(valid with { PluginOwnerData = owner with { OwnerBindingId = "other-binding" } }, false);
        CheckAdmission(valid with { PluginOwnerData = owner with { CatalogRoot = "C:\\fixture\\other-catalog" } }, false);
        CheckAdmission(valid with { PluginOwnerData = owner with { PluginDataRoot = "C:\\fixture\\other-data" } }, false);
        CheckAdmission(valid with { PluginOwnerData = owner with { ProcessId = 4321 } }, false);
        CheckAdmission(valid with { State = "rejected" }, false);
        Check(ConfigurationManagedEntryMatches(["C:\\dotnet\\dotnet.exe", "C:\\app\\Cerebral.Integrator.dll"],
            "C:\\dotnet\\dotnet.exe", "C:\\app\\Cerebral.Integrator.dll"));
        Check(!ConfigurationManagedEntryMatches(["C:\\dotnet\\dotnet.exe", "C:\\app\\other.dll"],
            "C:\\dotnet\\dotnet.exe", "C:\\app\\Cerebral.Integrator.dll"));
        Check(!ConfigurationManagedEntryMatches(["C:\\other\\dotnet.exe", "C:\\app\\Cerebral.Integrator.dll"],
            "C:\\dotnet\\dotnet.exe", "C:\\app\\Cerebral.Integrator.dll"));
        Check(!ConfigurationManagedEntryMatches(["C:\\dotnet\\dotnet.exe"],
            "C:\\dotnet\\dotnet.exe", "C:\\app\\Cerebral.Integrator.dll"));
        return count;
    }

    internal static int CheckPluginSettingsPeerArgv()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The current executable path is unavailable.");
        var managedEntry = typeof(OrchestratorWindow).Assembly.Location;
        var argv = WindowsProcessArguments.ReadNativeArgv(Environment.ProcessId);
        if (!ConfigurationManagedEntryMatches(argv, executable, managedEntry))
            throw new InvalidOperationException("The native process arguments do not identify this managed entry.");
        return 1;
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("The Configuration receiver descriptor has duplicate fields.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private sealed record ConfigurationReceiverAttachment(string PipeName, string InstanceId,
        string WorkerDataDirectory, string InstallationId)
    { [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; } }
    private sealed record ConfigurationReceiverExpected(int ProcessId, string ProcessStartUtcTicks,
        ConfigurationReceiverAttachment Attachment, string ConfigurationStateDirectory)
    { [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; } }
    private sealed record ConfigurationReceiverDescriptor(string Product, int Protocol, string PipeName,
        string InstanceId, ConfigurationReceiverExpected Expected)
    { [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; } }
    private sealed record ConfigurationReceiverPageCommand(string? TargetPageId, string? PluginId,
        string ExpectedPageId, long ExpectedRevision, string? PanelId = null,
        string? ExpectedPluginDirectory = null, string? ExpectedPluginVersion = null);
    private sealed record ConfigurationReceiverRequest(string Product, int Protocol, string InstanceId,
        string RequestId, string Operation, ConfigurationReceiverExpected Expected,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConfigurationReceiverPageCommand? Page = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PluginId = null);
    private sealed record ConfigurationReceiverCapabilities(string Schema, string[] Operations, string[] PageTargets,
        bool ExactPluginPanelSelection, bool ExactPluginGeneration)
    { [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; } }
    private sealed record ConfigurationReceiverPageSnapshot(string State, DateTimeOffset CapturedAtUtc,
        int ProcessId, string ProcessStartUtcTicks, string? PageId, long Revision, bool Loaded, bool Visible,
        string? PluginId, string? PluginPanelId, bool PluginPanelLoaded, bool PluginPanelVisible,
        bool PluginPanelReady, string? PluginPanelErrorCode, string? SelectedPluginPanelId,
        bool PluginPanelAvailable, bool PluginPanelSelectionConfirmed,
        string? PluginPackageDirectory, string? PluginPackageVersion)
    { [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; } }
    private sealed record ConfigurationReceiverPluginOwnerSnapshot(string Schema, DateTimeOffset CapturedAtUtc,
        int ProcessId, string ProcessStartUtcTicks, ConfigurationReceiverAttachment Attachment,
        string ConfigurationStateDirectory, string Mode, string? OwnerBindingId,
        string CatalogRoot, string PluginDataRoot, JsonElement? ProductPaths)
    { [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; } }
    private sealed record ConfigurationReceiverPluginRuntimeStatus(string Id, bool Loaded, string? Error);
    private sealed record ConfigurationReceiverPluginRuntimePackage(string Directory, string Version);
    private sealed record ConfigurationReceiverPluginRuntimeSnapshot(string Schema,
        ConfigurationReceiverPluginOwnerSnapshot Owner, string PluginId, string Scope,
        bool RuntimeAvailable, string State, ConfigurationReceiverPluginRuntimeStatus? Status,
        ConfigurationReceiverPluginRuntimePackage? LoadedPackage, bool? Retiring);
    private sealed record ConfigurationReceiverResponse(string Product, int Protocol, string InstanceId,
        string? RequestId, string Operation, string State, JsonElement? Data, string? ErrorCode,
        // The receiver omits these optional payloads for unrelated operations. Defaults keep
        // the wire contract compatible with strict required-constructor-parameter parsing.
        ConfigurationReceiverPageSnapshot? PageData = null, ConfigurationReceiverCapabilities? Capabilities = null,
        ConfigurationReceiverPluginOwnerSnapshot? PluginOwnerData = null, ConfigurationReceiverPluginRuntimeSnapshot? PluginRuntimeData = null)
    { [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; } }
}
