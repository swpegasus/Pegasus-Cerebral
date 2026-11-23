using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

internal sealed record ReceiverAttachment(string PipeName, string InstanceId, string WorkerDataDirectory, string InstallationId);
internal sealed record ReceiverExpected(int ProcessId, string ProcessStartUtcTicks, ReceiverAttachment Attachment, string ConfigurationStateDirectory);
internal sealed record ReceiverDescriptor(string Product, int Protocol, string PipeName, string InstanceId, ReceiverExpected Expected);
internal sealed record ReceiverRequest(string Product, int Protocol, string InstanceId, string RequestId, string Operation,
    ReceiverExpected Expected, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReceiverPageCommand? Page = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PluginId = null)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
internal sealed record ReceiverLoadedReadback(string Scope, bool SettingsCurrent, int? SettingsRevision, bool ProfilesCurrent,
    int? ProfileCount, bool ModelCatalogCurrent, string? ModelWorkerId, int? ModelCount, DateTimeOffset? ModelCatalogReadAtUtc,
    bool ModelRefreshInProgress);
internal sealed record ReceiverSnapshot(string State, DateTimeOffset CapturedAtUtc, int ProcessId, string ProcessStartUtcTicks,
    ReceiverAttachment? Attachment, string ConfigurationStateDirectory, bool Connected, string InitialConnection,
    bool Visible, string? WindowHandle, ReceiverLoadedReadback Loaded);
internal sealed record ReceiverPluginOwnerBasePaths(string DataDirectory, string UserDataDirectory,
    string InstallDirectory, string BaseDirectory, bool Portable);
internal sealed record ReceiverPluginOwnerSnapshot(string Schema, DateTimeOffset CapturedAtUtc, int ProcessId,
    string ProcessStartUtcTicks, ReceiverAttachment Attachment, string ConfigurationStateDirectory,
    string Mode, string? OwnerBindingId, string CatalogRoot, string PluginDataRoot, ReceiverPluginOwnerBasePaths ProductPaths);
internal sealed record ReceiverResponse(string Product, int Protocol, string InstanceId, string? RequestId, string Operation,
    string State, ReceiverSnapshot? Data, string? ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReceiverPageSnapshot? PageData = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReceiverPageCapabilities? Capabilities = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReceiverPluginOwnerSnapshot? PluginOwnerData = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReceiverPluginRuntimeSnapshot? PluginRuntimeData = null);

internal static partial class ConfigurationReadbackProtocol
{
    internal const string Product = "Pegasus Cerebral Configuration Readback";
    internal const int Version = 1;
    internal const string PluginOwnerOperation = "plugin-owner.inspect";
    internal const string PluginOwnerSchema = "pegasus.configuration-plugin-owner/1";
    internal const int MaximumRequestBytes = 8192;
    internal const int MaximumResponseBytes = 32768;
    private static readonly JsonSerializerOptions Compact = new(CerebralJson.Options) { WriteIndented = false };

    internal static bool Identifier(string? value, int max) => !string.IsNullOrEmpty(value) && value.Length <= max &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        !value.Any(char.IsControl);
    internal static int Size<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Compact).Length;

    internal static string? Validate(ReceiverRequest request, ReceiverDescriptor descriptor)
    {
        if (Size(request) > MaximumRequestBytes) return "request_too_large";
        if (request.Product != Product || request.Protocol != Version) return "protocol_mismatch";
        if (!Identifier(request.RequestId, 128)) return "request_id_invalid";
        if (request.InstanceId != descriptor.InstanceId) return "generation_mismatch";
        if (request.Extra is { Count: > 0 }) return "request_fields_invalid";
        if (request.Operation is not ("snapshot.inspect" or "capabilities.inspect" or "page.inspect" or "page.open" or "plugin.panel.open" or PluginOwnerOperation or PluginRuntimeOperation))
            return "operation_not_supported";
        if (request.Operation == PluginRuntimeOperation)
        {
            if (!RuntimePluginId(request.PluginId)) return "plugin_runtime_target_invalid";
        }
        else if (request.PluginId is not null) return "plugin_runtime_arguments_invalid";
        if (request.Operation is "snapshot.inspect" or "capabilities.inspect" or "page.inspect" or PluginOwnerOperation or PluginRuntimeOperation)
        {
            if (request.Page is not null) return "page_arguments_invalid";
        }
        else if (!ConfigurationPageControl.ValidCommand(request.Operation, request.Page)) return "page_arguments_invalid";
        var expected = request.Expected;
        if (expected is null || expected.Attachment is null || expected.ProcessId <= 0 ||
            !long.TryParse(expected.ProcessStartUtcTicks, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0 ||
            !Identifier(expected.Attachment.PipeName, 160) || !Identifier(expected.Attachment.InstanceId, 32) ||
            !Text(expected.Attachment.WorkerDataDirectory, 4096) || !Text(expected.Attachment.InstallationId, 256) ||
            !Text(expected.ConfigurationStateDirectory, 4096)) return "expected_identity_invalid";
        return expected == descriptor.Expected ? null : "identity_mismatch";
    }

    internal static async Task<ReceiverResponse> DispatchAsync(ReceiverRequest request, ReceiverDescriptor descriptor,
        Func<CancellationToken, Task<ReceiverSnapshot>> capture, CancellationToken token)
        => await DispatchAsync(request, descriptor, capture, null, null, token).ConfigureAwait(false);

    internal static async Task<ReceiverResponse> DispatchAsync(ReceiverRequest request, ReceiverDescriptor descriptor,
        Func<CancellationToken, Task<ReceiverSnapshot>> capture,
        Func<CancellationToken, Task<ReceiverPageSnapshot>>? inspectPage,
        Func<string, ReceiverPageCommand, CancellationToken, Task<ReceiverPageSnapshot>>? openPage,
        CancellationToken token)
        => await DispatchAsync(request, descriptor, capture, inspectPage, openPage, null, token).ConfigureAwait(false);

    internal static async Task<ReceiverResponse> DispatchAsync(ReceiverRequest request, ReceiverDescriptor descriptor,
        Func<CancellationToken, Task<ReceiverSnapshot>> capture,
        Func<CancellationToken, Task<ReceiverPageSnapshot>>? inspectPage,
        Func<string, ReceiverPageCommand, CancellationToken, Task<ReceiverPageSnapshot>>? openPage,
        Func<CancellationToken, Task<ReceiverPluginOwnerSnapshot>>? capturePluginOwner,
        CancellationToken token)
        => await DispatchAsync(request, descriptor, capture, inspectPage, openPage, capturePluginOwner, null, token).ConfigureAwait(false);

    internal static async Task<ReceiverResponse> DispatchAsync(ReceiverRequest request, ReceiverDescriptor descriptor,
        Func<CancellationToken, Task<ReceiverSnapshot>> capture,
        Func<CancellationToken, Task<ReceiverPageSnapshot>>? inspectPage,
        Func<string, ReceiverPageCommand, CancellationToken, Task<ReceiverPageSnapshot>>? openPage,
        Func<CancellationToken, Task<ReceiverPluginOwnerSnapshot>>? capturePluginOwner,
        Func<string, CancellationToken, Task<ReceiverPluginRuntimeSnapshot>>? capturePluginRuntime,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var code = Validate(request, descriptor);
        if (code is not null) return Result("rejected", null, code);
        if (request.Operation == PluginRuntimeOperation)
        {
            if (capturePluginRuntime is null) return Result("unavailable", null, "plugin_runtime_unavailable");
            ReceiverPluginRuntimeSnapshot runtime;
            try { runtime = await capturePluginRuntime(request.PluginId!, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (ConfigurationPageControlRefusal refusal) { return Result("rejected", null, refusal.Code); }
            catch (Exception) { return Result("unavailable", null, "plugin_runtime_unavailable"); }
            token.ThrowIfCancellationRequested();
            if (!CompletePluginRuntime(runtime, descriptor.Expected, request.PluginId!))
                return Result("unavailable", null, "plugin_runtime_unavailable");
            var runtimeResponse = Result("completed", null, null, pluginRuntime: runtime);
            return Size(runtimeResponse) <= MaximumResponseBytes ? runtimeResponse
                : Result("unavailable", null, "plugin_runtime_response_too_large");
        }
        if (request.Operation == PluginOwnerOperation)
        {
            if (capturePluginOwner is null) return Result("unavailable", null, "plugin_owner_unavailable");
            ReceiverPluginOwnerSnapshot owner;
            try { owner = await capturePluginOwner(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (ConfigurationPageControlRefusal refusal) { return Result("rejected", null, refusal.Code); }
            catch (Exception) { return Result("unavailable", null, "plugin_owner_unavailable"); }
            token.ThrowIfCancellationRequested();
            if (!CompletePluginOwner(owner, descriptor.Expected))
                return Result("unavailable", null, "plugin_owner_unavailable");
            var ownerResponse = Result("completed", null, null, pluginOwner: owner);
            return Size(ownerResponse) <= MaximumResponseBytes ? ownerResponse
                : Result("unavailable", null, "plugin_owner_response_too_large");
        }
        if (request.Operation != "snapshot.inspect" && (inspectPage is null || openPage is null))
            return Result("unavailable", null, "page_control_unavailable");
        if (request.Operation == "capabilities.inspect")
            return Bounded(Result("completed", null, null, capabilities: ConfigurationPageControl.Capabilities));
        if (request.Operation != "snapshot.inspect")
        {
            ReceiverPageSnapshot page;
            try
            {
                page = request.Operation == "page.inspect"
                    ? await inspectPage!(token).ConfigureAwait(false)
                    : await openPage!(request.Operation, request.Page!, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (ConfigurationPageControlRefusal refusal) { return Result("rejected", null, refusal.Code); }
            catch (ConfigurationPageNavigationUncertain uncertain) { return Result("uncertain", null, uncertain.Code); }
            catch (Exception) { return Result("unavailable", null, "page_control_unavailable"); }
            token.ThrowIfCancellationRequested();
            if (request.Operation == "plugin.panel.open" &&
                !ConfigurationPageControl.SamePluginGeneration(page.PluginPackageDirectory, page.PluginPackageVersion,
                    request.Page!.ExpectedPluginDirectory, request.Page.ExpectedPluginVersion))
                return Result("uncertain", null, "plugin_generation_unconfirmed");
            var navigationReady = request.Operation switch
            {
                "page.open" => page.PageId == request.Page!.TargetPageId && page.Loaded && page.Visible,
                "plugin.panel.open" => page.PluginId == request.Page!.PluginId &&
                    page.SelectedPluginPanelId == request.Page.PanelId && page.PluginPanelAvailable &&
                    page.PluginPanelSelectionConfirmed && page.PluginPanelReady,
                _ => true // An inspect completes its read even while the reported UI is not ready.
            };
            return Bounded(Result(navigationReady ? "completed" : "accepted", null, null, page));
        }
        ReceiverSnapshot snapshot;
        try { snapshot = await capture(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return Result("unavailable", null, "snapshot_unavailable"); }
        token.ThrowIfCancellationRequested();
        var response = Result("completed", snapshot, null);
        return Size(response) <= MaximumResponseBytes ? response : Result("unavailable", null, "snapshot_too_large");

        ReceiverResponse Bounded(ReceiverResponse response) => Size(response) <= MaximumResponseBytes
            ? response : Result("unavailable", null, "page_response_too_large");
        ReceiverResponse Result(string state, ReceiverSnapshot? data, string? error,
            ReceiverPageSnapshot? page = null, ReceiverPageCapabilities? capabilities = null,
            ReceiverPluginOwnerSnapshot? pluginOwner = null, ReceiverPluginRuntimeSnapshot? pluginRuntime = null) =>
            new(Product, Version, descriptor.InstanceId, Identifier(request.RequestId, 128) ? request.RequestId : null,
                request.Operation, state, data, error, page, capabilities, pluginOwner, pluginRuntime);
    }

    private static bool CompletePluginOwner(ReceiverPluginOwnerSnapshot? owner, ReceiverExpected expected) =>
        owner is not null && owner.Schema == PluginOwnerSchema && owner.CapturedAtUtc != default &&
        owner.ProcessId == expected.ProcessId && owner.ProcessStartUtcTicks == expected.ProcessStartUtcTicks &&
        owner.Attachment == expected.Attachment && owner.ConfigurationStateDirectory == expected.ConfigurationStateDirectory &&
        (owner.Mode == "self-owned" && owner.OwnerBindingId is null ||
         owner.Mode == "managed" && owner.OwnerBindingId is { Length: 64 } id && id.All(Uri.IsHexDigit)) &&
        Text(owner.CatalogRoot, int.MaxValue) && Text(owner.PluginDataRoot, int.MaxValue) &&
        owner.ProductPaths is { } paths && Text(paths.DataDirectory, int.MaxValue) &&
        Text(paths.UserDataDirectory, int.MaxValue) && Text(paths.InstallDirectory, int.MaxValue) &&
        Text(paths.BaseDirectory, int.MaxValue);

    internal static string Classify(bool closed, bool bindingMatches, bool connected, string initial,
        bool settings, bool profiles, bool catalog, bool refreshing) =>
        closed ? "closed" : !bindingMatches ? "mismatched" : initial == "pending" ? "connecting" :
        initial != "connected" ? "failed" : !connected ? "disconnected" :
        !settings || !profiles || !catalog || refreshing ? "partial" : "ready";
}

public sealed partial class IntegratorWindow
{
    // Only observation failures add this passive diagnostic; ordinary startup and activation stay independent.
    private readonly TextBlock receiverObservationDiagnostic = new()
    {
        Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
    };

    internal void ReportReceiverObservationUnavailable()
    {
        if (Lifetime.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
        void Show()
        {
            try
            {
                if (Lifetime.IsCancellationRequested) return;
                receiverObservationDiagnostic.Text = "Connection observation is unavailable. Configuration settings can still be used.";
                receiverObservationDiagnostic.Foreground = Ui.ErrorText;
                receiverObservationDiagnostic.Visibility = Visibility.Visible;
            }
            catch (Exception) { /* Optional observation diagnostics cannot break Configuration. */ }
        }
        if (Dispatcher.CheckAccess()) Show();
        else
        {
            try { _ = Dispatcher.InvokeAsync(Show); }
            catch (InvalidOperationException) { }
        }
    }

    internal ConfigurationReadbackServer? StartReceiverObservation(ExistingWorkerDesktopOptions options)
    {
        if (ExistingWorkerAttachment is not { } attached || attached.Endpoint != options.Endpoint ||
            attached.WorkerDataDirectory != options.WorkerDataDirectory || attached.InstallationId != options.InstallationId)
        {
            ReportReceiverObservationUnavailable();
            return null;
        }
        return ConfigurationReadbackServer.TryStart(options, CaptureReceiverSnapshot, CaptureReceiverPage,
            OpenReceiverPage, Dispatcher, Lifetime.Token,
            ReportReceiverObservationUnavailable, CaptureReceiverPluginOwner, CaptureReceiverPluginRuntime);
    }

    internal ReceiverPluginOwnerSnapshot CaptureReceiverPluginOwner(ReceiverExpected expected)
    {
        RequireReceiverPageOwner(expected);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var attachment = ExistingWorkerAttachment
            ?? throw new InvalidOperationException("The applied Worker binding is unavailable.");
        if (desktopCatalog is null || string.IsNullOrWhiteSpace(desktopCatalog.RootDirectory) ||
            string.IsNullOrWhiteSpace(desktopPluginDataRoot))
            throw new InvalidOperationException("The applied plugin storage is unavailable.");
        var paths = ConfigurationProductPaths();
        // Read applied objects only. In particular, do not create a plugin data directory,
        // enumerate the catalog, invoke a plugin, refresh caches, or dispatch a Worker request.
        return new(ConfigurationReadbackProtocol.PluginOwnerSchema, DateTimeOffset.UtcNow, process.Id,
            process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            new(attachment.Endpoint.PipeName, attachment.Endpoint.InstanceId, attachment.WorkerDataDirectory, attachment.InstallationId),
            DesktopInstancePaths.Current.DataDirectory, managedPluginOwnerBinding is null ? "self-owned" : "managed",
            managedPluginOwnerBinding?.OwnerBindingId, desktopCatalog.RootDirectory, desktopPluginDataRoot,
            new(paths.DataDirectory, paths.UserDataDirectory, paths.InstallDirectory, paths.BaseDirectory, paths.Portable));
    }

    internal ReceiverSnapshot CaptureReceiverSnapshot(ReceiverExpected expected)
    {
        Dispatcher.VerifyAccess();
        var attachment = ExistingWorkerAttachment is { } bound
            ? new ReceiverAttachment(bound.Endpoint.PipeName, bound.Endpoint.InstanceId, bound.WorkerDataDirectory, bound.InstallationId)
            : null;
        var matches = attachment == expected.Attachment;
        var initial = InitialConnectionSettled;
        var initialState = !initial.IsCompleted ? "pending" : initial.IsCanceled ? "canceled" :
            initial.IsFaulted ? "faulted" : initial.Result.Connected ? "connected" : "failed";
        var settingsCurrent = settingsConnectionEpoch == queueConnectionEpoch && settingsConnectionEpoch >= 0;
        var profiles = CurrentComputeProfiles;
        var machine = SelectedModelWorkerId;
        var catalogCurrent = computeLibraries.TryGetValue(machine, out var catalog) &&
            catalog.Connection == queueConnectionEpoch && loadedModelWorkerId == machine;
        var handle = new WindowInteropHelper(this).Handle;
        // These are already adopted cache scalars. No refresh, diagnostics, settings body, or Worker call occurs.
        var loaded = new ReceiverLoadedReadback("cached-settings-profiles-selected-model-catalog",
            settingsCurrent, settingsCurrent ? settings.Revision : null, profiles is not null, profiles?.Count,
            catalogCurrent, catalogCurrent ? machine : null, catalogCurrent ? catalog!.Models.Count : null,
            catalogCurrent ? catalog!.ReadAt : null, refreshingModelLibrary);
        return new(ConfigurationReadbackProtocol.Classify(Lifetime.IsCancellationRequested, matches, ServiceConnected,
                initialState, settingsCurrent, profiles is not null, catalogCurrent, refreshingModelLibrary),
            DateTimeOffset.UtcNow, Environment.ProcessId, expected.ProcessStartUtcTicks, attachment,
            expected.ConfigurationStateDirectory, ServiceConnected, initialState, IsVisible,
            handle == IntPtr.Zero ? null : $"0x{handle.ToInt64():X16}", loaded);
    }
}
