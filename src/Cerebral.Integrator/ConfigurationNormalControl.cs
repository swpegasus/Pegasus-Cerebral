using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

internal static class ConfigurationNormalControl
{
    internal const string Product = "Pegasus Cerebral Configuration Control";
    internal const string PipePrefix = "Pegasus.Cerebral.Configuration.Control.";
    internal const string DescriptorName = "configuration-control.json";
    internal static readonly IReadOnlySet<string> Operations = new HashSet<string>(StringComparer.Ordinal)
    { "instance.inspect", "capabilities.inspect", "snapshot.inspect", "pages.list", "page.inspect", "page.open", "window.activate" };
}

internal sealed record ConfigurationNormalExpected(string OwnerGeneration, long ConnectionEpoch, string ConnectionKey,
    bool Connected, string? PageId, long PageRevision, long RegistryRevision);

public sealed partial class IntegratorWindow
{
    private sealed record MountedControlPage(string Id, string AreaId, string Title, TabItem Area, TabControl Mount, TabItem Tab, FrameworkElement Content);
    private MountedControlPage[] normalControlPages = [];
    private long normalRegistryRevision;
    private ConfigurationPluginOwnerScope? normalControlOwner;
    private object? normalControlConnection;
    private string normalControlOwnerGeneration = "";
    private readonly SemaphoreSlim normalControlMutation = new(1, 1);
    private static readonly JsonSerializerOptions NormalExpectedJson = new(CerebralJson.Options)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };

    internal Task<SemanticControlServer> StartNormalControlAsync(string userDirectory,
        Func<string, bool, System.IO.Pipes.NamedPipeServerStream>? listenerFactory = null)
    {
        Dispatcher.VerifyAccess();
        if (ExistingWorkerAttachment is not null)
            throw new InvalidOperationException("Ordinary Configuration control cannot adopt an attached Worker.");
        RefreshNormalControlPages();
        var endpoint = SemanticControlProtocol.CreateEndpoint(ConfigurationNormalControl.Product, ConfigurationNormalControl.PipePrefix);
        return SemanticControlServer.StartAsync(endpoint, userDirectory, ConfigurationNormalControl.DescriptorName,
            request => ConfigurationNormalControl.Operations.Contains(request.Operation) ? null :
                SemanticControlProtocol.Result(request, "rejected", new { code = "operation_not_supported_in_mode" },
                    "This normal Configuration endpoint admits presentation inspection and exact page navigation only."),
            (request, token) => Dispatcher.InvokeAsync(() => HandleNormalConfigurationControlAsync(request, endpoint, token),
                DispatcherPriority.Normal, token).Task.Unwrap(), Lifetime.Token, listenerFactory);
    }

    private void RefreshNormalControlPages()
    {
        Dispatcher.VerifyAccess();
        var pages = new List<MountedControlPage>();
        foreach (var area in Tabs.Items.OfType<TabItem>())
        {
            if (area.Content is not TabControl mount) throw new InvalidOperationException("Configuration page mounts changed shape.");
            foreach (var tab in mount.Items.OfType<TabItem>())
            {
                var id = AutomationProperties.GetAutomationId(tab);
                if (!ConfigurationReadbackProtocol.Identifier(id, 128) || !id.StartsWith("ConfigurationTab", StringComparison.Ordinal) ||
                    tab.Content is not FrameworkElement content || pages.Count == 128 || pages.Any(page => page.Id == id))
                    throw new InvalidOperationException("Configuration page identity is missing, duplicated or exceeds its bound.");
                var title = tab.Header as string ?? "";
                pages.Add(new(id, AutomationProperties.GetAutomationId(area), title[..Math.Min(title.Length, 128)], area, mount, tab, content));
            }
        }
        if (!pages.SequenceEqual(normalControlPages))
        { normalControlPages = pages.ToArray(); normalRegistryRevision = checked(normalRegistryRevision + 1); }
    }

    private ConfigurationNormalExpected NormalControlExpected()
    {
        RefreshNormalControlPages();
        if (normalControlOwner?.MatchesOwner() != true || !ReferenceEquals(normalControlConnection, ServiceConnection))
        {
            normalControlOwner = new(ConfigurationPluginOwner(), ConfigurationPluginOwner);
            normalControlConnection = ServiceConnection;
            normalControlOwnerGeneration = Guid.NewGuid().ToString("N");
        }
        var page = SelectedConfigurationPagePresentation();
        return new(normalControlOwnerGeneration, queueConnectionEpoch, ConfigurationPluginSettingsChannel.ConnectionKey(ServiceConnectionIdentity),
            IsConnected, page.PageId, receiverPageRevision, normalRegistryRevision);
    }

    private object NormalControlPageSnapshot()
    {
        var presentation = SelectedConfigurationPagePresentation();
        var current = normalControlPages.SingleOrDefault(page => page.Id == presentation.PageId);
        var bound = current is not null && ReferenceEquals(Tabs.SelectedItem, current.Area) &&
            ReferenceEquals(current.Mount.SelectedItem, current.Tab) && ReferenceEquals(current.Tab.Content, current.Content);
        var ready = bound && presentation.Loaded && presentation.Visible;
        return new { id = presentation.PageId, revision = receiverPageRevision, registryRevision = normalRegistryRevision,
            identityConfirmed = bound, loaded = presentation.Loaded, visible = presentation.Visible,
            presentationState = ready ? "ready" : "partial", connected = IsConnected,
            domainActionCoverage = "not-admitted", readinessScope = "mounted-page-presentation" };
    }

    private static object PageActionCoverage(string id) => new
    {
        navigation = "supported", domainActions = "not-admitted", values = "not-exposed",
        ownerRoute = id switch
        {
            "ConfigurationTabSettings" => "local-Windows-startup",
            "ConfigurationTabUserConnector" => "local-user-connector-manager",
            "ConfigurationTabDesktopPlugins" => "local-plugin-owner-and-declared-panels",
            "ConfigurationTabAppearance" or "ConfigurationTabChats" => "Worker-interface.preferences-and-revisioned-settings",
            "ConfigurationTabBackup" => "Worker-configuration-preview-apply-status",
            "ConfigurationTabAccess" => "Worker-access.full-and-Cortex-authorization",
            "ConfigurationTabEngine" => "Worker-engine-epoch-operations",
            "ConfigurationTabDashboard" => "local-recovery-and-protected-management",
            "ConfigurationTabDiagnostics" => "Worker-owner-commands-excluded-from-UI-relay",
            "ConfigurationTabAbout" => "local-read-only-product-information",
            _ => "existing-Worker-owner-operations"
        },
        policy = id == "ConfigurationTabAbout" ? "local-read-only-no-domain-actions" :
            id is "ConfigurationTabAccess" or "ConfigurationTabUserConnector" or "ConfigurationTabPeerSharing" or
            "ConfigurationTabCortex" or "ConfigurationTabDashboard" or "ConfigurationTabBackup" or "ConfigurationTabDiagnostics"
            ? "security-sensitive-or-explicit-owner-decision-required" : "typed-domain-actions-pending"
    };

    private object NormalControlCapabilities() => new
    {
        schema = "pegasus.configuration-normal-control/1", protocol = SemanticControlProtocol.Version, mode = "normal-semantic",
        admittedOperations = ConfigurationNormalControl.Operations.Order(StringComparer.Ordinal).ToArray(),
        coverage = new { pageNavigation = "all-current-mounted-pages", domainActions = "not-admitted", genericControlInvocation = false,
            rawSettings = false, workerRelay = false, sensitiveValues = false },
        commands = ConfigurationNormalControl.Operations.Order(StringComparer.Ordinal).Select(operation => new
        { operation, arguments = operation == "page.open" ? new[] { "id", "expected" } : operation == "window.activate" ? new[] { "expected" } : Array.Empty<string>(),
            outcomeScope = "presentation-only" }).ToArray(),
        expectedFields = new[] { "ownerGeneration", "connectionEpoch", "connectionKey", "connected", "pageId", "pageRevision", "registryRevision" },
        receipts = SemanticControlServer.ReceiptCapabilities,
        note = "Discovery and page navigation do not authorize domain mutations, expose settings or start a Worker."
    };

    private static void NormalArguments(JsonElement arguments, params string[] names)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ConfigurationPageControlRefusal("arguments_invalid");
        var supplied = arguments.EnumerateObject().Select(property => property.Name).ToArray();
        if (supplied.Length != names.Length || supplied.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            supplied.Except(names, StringComparer.Ordinal).Any())
            throw new ConfigurationPageControlRefusal("arguments_invalid");
    }

    private void RequireNormalControlCurrent(ConfigurationNormalExpected expected, ConfigurationPluginOwnerScope capturedOwner, object capturedConnection)
    {
        if (Lifetime.IsCancellationRequested || Dispatcher.HasShutdownStarted || ExistingWorkerAttachment is not null ||
            !ReferenceEquals(normalControlOwner, capturedOwner) || !capturedOwner.MatchesOwner() ||
            !ReferenceEquals(ServiceConnection, capturedConnection) || expected != NormalControlExpected())
            throw new ConfigurationPageControlRefusal("owner_or_page_changed");
    }

    private async Task<SemanticControlResponse> HandleNormalConfigurationControlAsync(SemanticControlRequest request,
        SemanticControlEndpoint endpoint, CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        var effectStarted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (Lifetime.IsCancellationRequested || ExistingWorkerAttachment is not null) throw new ConfigurationPageControlRefusal("owner_unavailable");
            var observed = NormalControlExpected();
            var mutation = request.Operation is "page.open" or "window.activate";
            NormalArguments(request.Arguments, mutation ? request.Operation == "page.open" ? ["id", "expected"] : ["expected"] : []);
            if (!mutation)
            {
                object data = request.Operation switch
                {
                    "instance.inspect" => new { endpoint, mode = "normal-semantic", userStateDirectory = DesktopInstancePaths.Current.UserDataDirectory,
                        connectionTransport = ServiceConnection.Transport, expected = observed },
                    "capabilities.inspect" => new { endpoint, configurationControl = NormalControlCapabilities(), expected = observed, page = NormalControlPageSnapshot() },
                    "pages.list" => new { registryRevision = normalRegistryRevision, expected = observed,
                        pages = normalControlPages.Select(page => new { page.Id, page.AreaId, page.Title, selected = observed.PageId == page.Id,
                            actions = PageActionCoverage(page.Id) }).ToArray() },
                    _ => new { capturedAtUtc = DateTimeOffset.UtcNow, expected = observed, page = NormalControlPageSnapshot(),
                        connected = IsConnected, initialConnectionSettled = InitialConnectionSettled.IsCompleted,
                        settingsCurrent = settingsConnectionEpoch == queueConnectionEpoch && settingsConnectionEpoch >= 0,
                        settingsRevision = settingsConnectionEpoch == queueConnectionEpoch && settingsConnectionEpoch >= 0 ? (int?)settings.Revision : null,
                        windowHandle = new WindowInteropHelper(this).Handle == IntPtr.Zero ? null : $"0x{new WindowInteropHelper(this).Handle.ToInt64():X16}" }
                };
                return SemanticControlProtocol.Result(request, "completed", data);
            }
            if (!request.Arguments.TryGetProperty("expected", out var supplied)) throw new ConfigurationPageControlRefusal("expected_required");
            NormalArguments(supplied, "ownerGeneration", "connectionEpoch", "connectionKey", "connected", "pageId", "pageRevision", "registryRevision");
            var expected = supplied.Deserialize<ConfigurationNormalExpected>(NormalExpectedJson);
            if (expected is null || supplied.EnumerateObject().Count() != 7 || string.IsNullOrEmpty(expected.OwnerGeneration) ||
                expected.ConnectionKey is not { Length: 64 } || expected.ConnectionEpoch < 0 || expected.PageRevision < 1 || expected.RegistryRevision < 1)
                throw new ConfigurationPageControlRefusal("expected_invalid");
            if (!await normalControlMutation.WaitAsync(0, token)) throw new ConfigurationPageControlRefusal("mutation_busy");
            try
            {
                var owner = normalControlOwner!; var connection = ServiceConnection;
                RequireNormalControlCurrent(expected, owner, connection);
                if (request.Operation == "page.open")
                {
                    if (!request.Arguments.TryGetProperty("id", out var targetValue) || targetValue.ValueKind != JsonValueKind.String)
                        throw new ConfigurationPageControlRefusal("page_target_invalid");
                    var targetId = targetValue.GetString();
                    var target = normalControlPages.SingleOrDefault(page => page.Id == targetId) ?? throw new ConfigurationPageControlRefusal("page_target_unavailable");
                    effectStarted = true;
                    // Reuse the same stable-ID routing that clears search and synchronizes the rail.
                    SelectTab(target.Id["ConfigurationTab".Length..]);
                    var after = NormalControlExpected();
                    if (!owner.MatchesOwner() || !ReferenceEquals(ServiceConnection, connection) ||
                        after.OwnerGeneration != expected.OwnerGeneration || after.RegistryRevision != expected.RegistryRevision || after.PageId != target.Id ||
                        !ReferenceEquals(target.Mount.SelectedItem, target.Tab) || !ReferenceEquals(target.Tab.Content, target.Content))
                        return SemanticControlProtocol.Result(request, "uncertain", new { code = "navigation_unconfirmed", expected = after }, "Inspect the original owner before another action.");
                }
                else
                {
                    effectStarted = true; DesktopApplicationInstance.Show(this); Activate();
                }
                var resultExpected = NormalControlExpected();
                if (!owner.MatchesOwner() || !ReferenceEquals(ServiceConnection, connection) || resultExpected.OwnerGeneration != expected.OwnerGeneration)
                    return SemanticControlProtocol.Result(request, "uncertain", new { code = "owner_changed_after_navigation", expected = resultExpected });
                if (request.Operation == "window.activate" && (resultExpected.PageId != expected.PageId ||
                    resultExpected.PageRevision != expected.PageRevision || resultExpected.RegistryRevision != expected.RegistryRevision))
                    return SemanticControlProtocol.Result(request, "uncertain", new { code = "page_changed_after_activation", expected = resultExpected });
                var presentation = SelectedConfigurationPagePresentation();
                var confirmed = request.Operation == "window.activate" ? IsActive && IsVisible : presentation.Loaded && presentation.Visible;
                return SemanticControlProtocol.Result(request, confirmed ? "completed" : "accepted",
                    new { expected = resultExpected, page = NormalControlPageSnapshot(), windowActive = IsActive, effectScope = "presentation-only", workerMutation = false });
            }
            finally { normalControlMutation.Release(); }
        }
        catch (ConfigurationPageControlRefusal error) { return SemanticControlProtocol.Result(request, effectStarted ? "uncertain" : "rejected", new { code = error.Code }, error.Code); }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or OperationCanceledException)
        { return SemanticControlProtocol.Result(request, effectStarted ? "uncertain" : "rejected", new { code = "control_unconfirmed", failureType = error.GetType().Name }, "Inspect current Configuration state before another action."); }
    }

    internal void ReportNormalControlUnavailable() => SetStatus("Programmatic Configuration control is unavailable. Restart Configuration to reopen its endpoint.", true);
}
