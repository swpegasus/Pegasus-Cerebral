using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

internal static class ConfigurationNormalControlChecks
{
    internal static int Run()
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.ApplyTheme(app);
        app.Startup += async (_, _) =>
        {
            var exit = 0;
            var root = Path.Combine(Path.GetTempPath(), "cerebral-normal-configuration-" + Guid.NewGuid().ToString("N"));
            IntegratorWindow? window = null;
            try
            {
                DesktopInstancePaths.UseSource(root, AppContext.BaseDirectory);
                window = new IntegratorWindow(false, isolatedSettingsFixture: true,
                    desktopPluginCatalog: new(Path.Combine(root, "plugins")), desktopPluginDataDirectory: Path.Combine(root, "plugin-data"),
                    seedBundledDesktopPlugins: false);
                var count = await window.CheckNormalConfigurationControlAsync(root);
                Console.WriteLine($"Normal Configuration control: {count} real same-user pipe/WPF and deterministic owner/retention checks passed; 25 page mounts; no Worker/service/model command or normal startup.");
            }
            catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
            finally
            {
                window?.Close();
                var full = Path.GetFullPath(root);
                var temporary = Path.GetFullPath(Path.GetTempPath());
                if (full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(full).StartsWith("cerebral-normal-configuration-", StringComparison.Ordinal) && Directory.Exists(full))
                    Directory.Delete(full, recursive: true);
                app.Shutdown(exit);
            }
        };
        return app.Run();
    }
}

public sealed partial class IntegratorWindow
{
    internal async Task<int> CheckNormalConfigurationControlAsync(string root)
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        var outputs = new List<string>();
        await using var server = await StartNormalControlAsync(root);
        async Task<SemanticControlResponse> Call(string operation, object? arguments = null, string? id = null)
        {
            var result = await SemanticControlClient.ExecuteAsync(server.DescriptorPath, ConfigurationNormalControl.Product,
                ConfigurationNormalControl.PipePrefix, typeof(Program).Assembly.Location, operation, id ?? Guid.NewGuid().ToString("N"),
                JsonSerializer.Serialize(arguments ?? new { }, CerebralJson.Options));
            outputs.Add(JsonSerializer.Serialize(result, CerebralJson.Options)); return result;
        }
        var descriptor = await SemanticControlIdentity.ReadEndpointAsync(server.DescriptorPath,
            ConfigurationNormalControl.Product, ConfigurationNormalControl.PipePrefix, CancellationToken.None);
        Check(descriptor == server.Endpoint && ExistingWorkerAttachment is null && !ServiceConnected,
            "The real normal descriptor must bind this independent offline inspection window.");
        var caps = await Call("capabilities.inspect");
        Check(caps.Success && caps.Data.GetProperty("configurationControl").GetProperty("admittedOperations").GetArrayLength() == 7 &&
            !caps.Data.GetProperty("configurationControl").GetProperty("coverage").GetProperty("rawSettings").GetBoolean(),
            "Discovery must name exactly seven presentation operations and exclude raw settings.");
        var schemaBefore = NormalControlExpected();
        Check(!schemaBefore.Connected && schemaBefore.ConnectionEpoch == 0,
            "The closed-schema regression must exercise actual false/zero expectation values.");
        var schemaExpected = CerebralJson.Element(schemaBefore);
        var expectedJson = schemaExpected.GetRawText();
        var invalidArguments = new List<(string Operation, string Json)>
        {
            ("page.open", "{\"id\":\"ConfigurationTabDashboard\",\"id\":\"ConfigurationTabSettings\",\"expected\":" + expectedJson + "}"),
            ("page.open", "{\"id\":\"ConfigurationTabDashboard\",\"expected\":" + expectedJson + ",\"expected\":" + expectedJson + "}"),
            ("page.open", "{\"expected\":" + expectedJson + "}"),
            ("window.activate", "{\"expected\":" + expectedJson + ",\"expected\":" + expectedJson + "}"),
            ("page.inspect", "{\"expected\":" + expectedJson + ",\"expected\":" + expectedJson + "}")
        };
        foreach (var omitted in new[] { "connected", "connectionEpoch" })
        {
            var duplicate = omitted == "connected" ? "connectionEpoch" : "connected";
            var fields = schemaExpected.EnumerateObject().Where(property => property.Name != omitted)
                .Select(property => JsonSerializer.Serialize(property.Name) + ":" + property.Value.GetRawText()).ToList();
            fields.Add(JsonSerializer.Serialize(duplicate) + ":" + schemaExpected.GetProperty(duplicate).GetRawText());
            invalidArguments.Add(("page.open", "{\"id\":\"ConfigurationTabDashboard\",\"expected\":{" + string.Join(",", fields) + "}}"));
        }
        foreach (var invalid in invalidArguments)
        {
            using var document = JsonDocument.Parse(invalid.Json);
            var visibleBefore = IsVisible;
            // Direct handler admission is intentionally tested independently of the
            // shared pipe framing, which also rejects duplicate JSON properties.
            var rejection = await HandleNormalConfigurationControlAsync(new(server.Endpoint.InstanceId, Guid.NewGuid().ToString("N"),
                invalid.Operation, document.RootElement.Clone()), server.Endpoint, CancellationToken.None);
            Check(rejection.State == "rejected" && rejection.Data.GetProperty("code").GetString() == "arguments_invalid" &&
                NormalControlExpected() == schemaBefore && IsVisible == visibleBefore,
                "Duplicate or omitted required argument keys must refuse before any presentation effect: " + invalid.Operation);
        }
        var pages = await Call("pages.list");
        var ids = pages.Data.GetProperty("pages").EnumerateArray().Select(page => page.GetProperty("id").GetString()!).ToArray();
        var expectedIds = new[] { "Settings", "Appearance", "Chats", "Backup", "Machines", "Models", "Agents", "Instructions",
            "DesktopPlugins", "ServiceExtensions", "Skills", "ToolsAndMcp", "Queue", "Background", "Cortex", "UserConnector",
            "PeerSharing", "Folders", "Resources", "Retention", "Access", "Engine", "Dashboard", "Diagnostics", "About" }
            .Select(suffix => "ConfigurationTab" + suffix).Order(StringComparer.Ordinal).ToArray();
        Check(ids.Length == 25 && ids.Distinct().Count() == 25 && ids.Order(StringComparer.Ordinal).SequenceEqual(expectedIds),
            "The actual registry must include all 25 unique mounted pages, including About.");
        var aboutArea = Tabs.Items.OfType<TabItem>().Single(area => AutomationProperties.GetAutomationId(area) == "ConfigurationAreaAbout");
        var aboutMount = aboutArea.Content as TabControl;
        var aboutPage = aboutMount?.Items.OfType<TabItem>().Single(page => AutomationProperties.GetAutomationId(page) == "ConfigurationTabAbout");
        Check(ReferenceEquals(Tabs.Items.OfType<TabItem>().Last(), aboutArea) && aboutPage?.Content is ScrollViewer,
            "About must be a mounted page in the final navigation category, available while offline.");
        var aboutScroll = (ScrollViewer)aboutPage!.Content;
        var aboutContent = aboutScroll.Content as StackPanel
            ?? throw new InvalidOperationException("The shared About content is unavailable.");
        var aboutText = aboutContent.Children.OfType<TextBlock>().Select(block => block.Text).ToArray();
        var hostAssembly = typeof(IntegratorWindow).Assembly;
        var expectedProduct = hostAssembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
            ?? hostAssembly.GetName().Name ?? Branding.ApplicationName;
        var expectedVersion = hostAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? hostAssembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? hostAssembly.GetName().Version?.ToString() ?? "not recorded";
        Check(!ServiceConnected && aboutScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto &&
            aboutText.Contains("Product: " + expectedProduct, StringComparer.Ordinal) &&
            aboutText.Contains("Version " + expectedVersion, StringComparer.Ordinal),
            "Offline About must identify the actual hosting product and build rather than a chat title or shared-library hardcode.");
        Check(aboutText.Contains("Created by Shayne Winter", StringComparer.Ordinal) &&
            aboutText.Contains("Development assistance: OpenAI Codex and Pegasus Cerebral.", StringComparer.Ordinal) &&
            aboutText.Contains("Pegasus Community License 1.0", StringComparer.Ordinal) &&
            aboutText.Any(text => text.Contains("below US$10 million", StringComparison.Ordinal)) &&
            aboutText.Any(text => text.Contains("at or above US$10 million", StringComparison.Ordinal) &&
                text.Contains("payment alone does not create permission", StringComparison.Ordinal)) &&
            aboutText.Any(text => text.Contains("Third-party software, models, runtimes and artwork retain their own terms.", StringComparison.Ordinal)),
            "About must retain the approved creator, development credits and truthful license threshold and third-party distinction.");
        var aboutLinks = aboutContent.Children.OfType<TextBlock>()
            .SelectMany(block => block.Inlines.OfType<System.Windows.Documents.Hyperlink>()).ToArray();
        var expectedLinks = new[] { "https://cerebral.shaynewinter.com/",
            "https://github.com/swpegasus/Pegasus-Cerebral", "https://shaynewinter.com/" };
        Check(aboutLinks.Length == 3 && aboutLinks.All(link => link.NavigateUri is { IsAbsoluteUri: true, Scheme: "https" } &&
                !string.IsNullOrWhiteSpace(AutomationProperties.GetName(link))) &&
            aboutLinks.Select(link => link.NavigateUri!.AbsoluteUri).Order(StringComparer.Ordinal)
                .SequenceEqual(expectedLinks.Order(StringComparer.Ordinal)),
            "About must expose exactly the three named fixed HTTPS links without launching a browser.");
        var aboutActions = pages.Data.GetProperty("pages").EnumerateArray()
            .Single(page => page.GetProperty("id").GetString() == "ConfigurationTabAbout").GetProperty("actions");
        Check(aboutActions.GetProperty("ownerRoute").GetString() == "local-read-only-product-information" &&
            aboutActions.GetProperty("policy").GetString() == "local-read-only-no-domain-actions" &&
            aboutActions.GetProperty("domainActions").GetString() == "not-admitted",
            "About discovery must describe local read-only information without Worker or domain-action authority.");
        settingsNavigation.Search.Text = "no-page-matches-this-fixture";
        var searchPages = await Call("pages.list");
        Check(searchPages.Data.GetProperty("pages").GetArrayLength() == 25 && settingsNavigation.VisiblePages.Count == 0,
            "Discovery must use actual mounts independently of search results.");
        var expected = searchPages.Data.GetProperty("expected").Clone();
        var opening = await Call("page.open", new { id = "ConfigurationTabDashboard", expected });
        Check(opening.State == "accepted" && !opening.Data.GetProperty("page").GetProperty("visible").GetBoolean() &&
            settingsNavigation.Search.Text.Length == 0,
            "Hidden offline page selection must report acceptance without claiming visible completion and clear stale search.");
        Show();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var settled = await Call("page.inspect");
        Check(settled.Data.GetProperty("page").GetProperty("presentationState").GetString() == "ready" && !ServiceConnected,
            "Inspection must confirm actual loaded/visible recovery content independently of Worker connectivity.");
        var activation = await Call("window.activate", new { expected = settled.Data.GetProperty("expected").Clone() });
        Check((activation.State is "accepted" or "completed") &&
            (activation.State != "completed" || activation.Data.GetProperty("windowActive").GetBoolean()),
            "Activation completion must reflect the actual active window rather than delivery alone.");
        foreach (var id in ids)
        {
            expected = (await Call("snapshot.inspect")).Data.GetProperty("expected").Clone();
            var opened = await Call("page.open", new { id, expected });
            Check((opened.State is "accepted" or "completed") && opened.Data.GetProperty("page").GetProperty("id").GetString() == id &&
                (opened.State != "completed" || opened.Data.GetProperty("page").GetProperty("loaded").GetBoolean() &&
                    opened.Data.GetProperty("page").GetProperty("visible").GetBoolean()), "Page navigation must confirm the exact target and truthful readiness: " + id);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        expected = (await Call("page.inspect")).Data.GetProperty("expected").Clone();
        SelectTab("Settings"); SelectTab("Diagnostics");
        var awayBack = await Call("page.open", new { id = "ConfigurationTabDashboard", expected });
        Check(awayBack.State == "rejected" && SelectedConfigurationPagePresentation().PageId == "ConfigurationTabDiagnostics",
            "Away-and-back selection must invalidate the old page revision without navigating.");
        Check((await Call("page.open", new { id = "ConfigurationTabSettings" })).State == "rejected",
            "Page mutations require an exact observed expected object.");
        foreach (var operation in new[] { "settings.save", "plugin.panel.open", "control.invoke", "engine.activate", "access.full.enable" })
            Check((await Call(operation)).State == "rejected", "Domain/security actions must be rejected before effects: " + operation);

        expected = (await Call("page.inspect")).Data.GetProperty("expected").Clone();
        var requestId = Guid.NewGuid().ToString("N");
        var first = await Call("page.open", new { id = "ConfigurationTabSettings", expected }, requestId);
        SelectTab("Dashboard");
        var retry = await Call("page.open", new { id = "ConfigurationTabSettings", expected }, requestId);
        Check(JsonSerializer.Serialize(first, CerebralJson.Options) == JsonSerializer.Serialize(retry, CerebralJson.Options) &&
            SelectedConfigurationPagePresentation().PageId == "ConfigurationTabDashboard", "A retained retry must return its original receipt without navigating again.");
        Check((await Call("page.open", new { id = "ConfigurationTabModels", expected }, requestId)).State == "conflict",
            "Changed input under an accepted request ID must conflict.");

        var compute = (TabControl)Tabs.Items.OfType<TabItem>().Single(area => AutomationProperties.GetAutomationId(area) == "ConfigurationAreaModelsAndAgents").Content;
        expected = (await Call("page.inspect")).Data.GetProperty("expected").Clone();
        var added = AddNestedTab(compute, "Owned temporary page", new Border(), "NormalControlFixture");
        try
        {
            Check((await Call("pages.list")).Data.GetProperty("pages").GetArrayLength() == 26 &&
                (await Call("page.open", new { id = "ConfigurationTabSettings", expected })).State == "rejected",
                "A real mount addition must appear in discovery and invalidate the previous registry revision.");
        }
        finally { compute.Items.Remove(added); }
        Check((await Call("pages.list")).Data.GetProperty("pages").GetArrayLength() == 25, "Removed mounts must leave discovery.");

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var clientField = typeof(ConnectedWindow).GetField("client", flags)!;
        var online = typeof(ConnectedWindow).GetProperty("IsConnected", flags)!;
        var originalClient = clientField.GetValue(this); var originalOnline = online.GetValue(this);
        using var ownerA = new CerebralClient("http://127.0.0.1:1");
        using var ownerB = new CerebralClient("http://127.0.0.1:1");
        try
        {
            // Deterministic in-memory owner replacement only: no command is sent to these clients.
            clientField.SetValue(this, ownerA); online.SetValue(this, true);
            expected = (await Call("page.inspect")).Data.GetProperty("expected").Clone();
            clientField.SetValue(this, ownerB);
            Check((await Call("page.open", new { id = "ConfigurationTabModels", expected })).State == "rejected",
                "A replacement client at the same endpoint must invalidate the old owner generation.");
            expected = (await Call("page.inspect")).Data.GetProperty("expected").Clone();
            online.SetValue(this, false); SetStatus("Owned fixture offline");
            online.SetValue(this, true); SetStatus("Owned fixture online");
            Check((await Call("page.open", new { id = "ConfigurationTabModels", expected })).State == "rejected",
                "An observed offline/online cycle must retire old navigation authority without caller polling.");
            expected = (await Call("page.inspect")).Data.GetProperty("expected").Clone();
            ConnectionChanged();
            Check((await Call("page.open", new { id = "ConfigurationTabModels", expected })).State == "rejected",
                "A connection epoch change must invalidate old navigation authority.");
        }
        finally { clientField.SetValue(this, originalClient); online.SetValue(this, originalOnline); }

        var originalKey = settings.AccessKey;
        try
        {
            settings.AccessKey = "private-settings-marker-not-for-control";
            foreach (var operation in new[] { "instance.inspect", "capabilities.inspect", "snapshot.inspect", "pages.list", "page.inspect" }) _ = await Call(operation);
            Check(outputs.All(output => !output.Contains(settings.AccessKey, StringComparison.Ordinal)), "No discovery or snapshot may expose private settings bodies.");
        }
        finally { settings.AccessKey = originalKey; }
        using (var anonymous = new NamedPipeClientStream(".", server.Endpoint.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Anonymous))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            var refused = false;
            try
            {
                await anonymous.ConnectAsync(deadline.Token);
                await LocalWorkerProtocol.WriteAsync(anonymous, new SemanticControlRequest(server.Endpoint.InstanceId, Guid.NewGuid().ToString("N"),
                    "page.open", CerebralJson.Element(new { id = "ConfigurationTabSettings", expected = NormalControlExpected() })), deadline.Token);
                var response = await LocalWorkerProtocol.ReadAsync<SemanticControlResponse>(anonymous, deadline.Token);
                refused = response.State == "rejected" && response.Data.GetProperty("code").GetString() is "caller_not_authorized" or "caller_identity_unavailable";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refused = true; }
            Check(refused, "The real anonymous native caller must be refused before navigation.");
        }
        Check(!ServiceConnected && ExistingWorkerAttachment is null && !File.Exists(Path.Combine(root, "engine.db")) &&
            !File.Exists(Path.Combine(root, "Desktop", "connection.json")), "Offline control must not establish a Worker, database or saved connection.");
        var descriptorPath = server.DescriptorPath;
        await server.DisposeAsync();
        Check(!File.Exists(descriptorPath), "Normal Configuration shutdown must withdraw its exact descriptor lease.");
        count += await CheckSharedReceiptRetentionAsync(root);
        return count;
    }

    private static async Task<int> CheckSharedReceiptRetentionAsync(string root)
    {
        var effectCount = 0;
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endpoint = SemanticControlProtocol.CreateEndpoint(ConfigurationNormalControl.Product, ConfigurationNormalControl.PipePrefix);
        await using var server = await SemanticControlServer.StartAsync(endpoint, root, "receipt-fixture.json", _ => null,
            async (request, _) =>
            {
                if (request.Operation == "fixture.hold") { Interlocked.Increment(ref effectCount); entered.TrySetResult(); await released.Task; }
                return SemanticControlProtocol.Result(request, "completed");
            });
        // Exercise the actual cache with deterministic tasks, avoiding 1024 native process
        // observations. Real normal transport/auth is exercised by the preceding calls.
        var execute = typeof(SemanticControlServer).GetMethod("ExecuteOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task<SemanticControlResponse> Execute(SemanticControlRequest request) => (Task<SemanticControlResponse>)execute.Invoke(server, [request, CancellationToken.None])!;
        var heldRequest = new SemanticControlRequest(endpoint.InstanceId, "retained-active-fixture", "fixture.hold", CerebralJson.Element(new { }));
        var held = Execute(heldRequest); await entered.Task;
        try
        {
            for (var index = 0; index < SemanticControlServer.CompletedRetentionTarget + 8; index++)
                _ = await Execute(new(endpoint.InstanceId, "inspection-" + index, "fixture.inspect", CerebralJson.Element(new { })));
            if (!ReferenceEquals(held, Execute(heldRequest)) || effectCount != 1)
                throw new InvalidOperationException("Inspection pressure evicted or replayed an active receipt.");
        }
        finally { released.TrySetResult(); }
        _ = await held; return 1;
    }
}
