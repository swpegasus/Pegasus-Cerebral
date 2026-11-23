using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

public static partial class Program
{
    private static int checks;
    private static object? nativePanelLayout;
    private static object? speechEnvironment;
    private static string? generatedPluginFixture;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "--connection-store-writer-child")
                return ConnectionStoreConcurrencyChecks.RunChild(args);
            if (args.Length == 1 && args[0] == "--connection-store-concurrency-only")
            {
                Console.WriteLine($"Connection CAS: {ConnectionStoreConcurrencyChecks.Run()} real-process synthetic checks passed; no user connection file was opened.");
                return 0;
            }
            _ = new Application();
            if (args.Length == 1 && args[0] == "--chat-activity-counts-only")
            {
                Console.WriteLine($"Chat activity counts: {ChatActivityCountChecks.Verify()} deterministic journal/DTO checks passed; no shown window, Worker, inference or network.");
                Console.WriteLine($"Activity attribution: {ActivityAttributionChecks.Run()} deterministic projection and unshown WPF checks passed; no provider, transport, database or visible window.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--activity-detail-only")
            {
                Console.WriteLine($"Activity details: {WorkflowActivityDetailChecks.Check()} deterministic formatter and iteration-state checks passed; no shown window, Worker, inference or network.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--isolated-text-host-only")
            {
                var frame = new DispatcherFrame(); Exception? failure = null; var count = 0;
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
                {
                    try { count = await IsolatedTextHostTests.RunAsync(); }
                    catch (Exception error) { failure = error; }
                    finally { frame.Continue = false; }
                }));
                Dispatcher.PushFrame(frame);
                if (failure is not null) throw new InvalidOperationException("Isolated text host fixture failed.", failure);
                Console.WriteLine($"{count} isolated text host checks passed; fake transport only, no Worker/network/model or UI launch.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--plugin-activation-update-only")
            {
                Console.WriteLine(PluginActivationUpdateTests.Run());
                return 0;
            }
            if (args.Length == 3 && args[0] == "--plugin-activation-update-child")
            {
                PluginActivationUpdateTests.RunChild(args[1], args[2]);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--plugin-settings-navigation-only")
            {
                var frame = new DispatcherFrame();
                Exception? failure = null;
                var result = 0;
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
                {
                    try { result = await PluginSettingsNavigationHostTests.RunAsync(); }
                    catch (Exception error) { failure = error; }
                    finally { frame.Continue = false; }
                }));
                Dispatcher.PushFrame(frame);
                if (failure is not null) throw new InvalidOperationException("Plugin settings navigation fixture failed.", failure);
                Console.WriteLine($"Plugin settings navigation: {result} deterministic scoped-host checks passed; no Configuration launch or receiver call.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--discovery-reconcile-only")
            {
                Console.WriteLine($"Discovery reconciliation: {LocalWorkerDiscoveryReconciliationChecks.Run().GetAwaiter().GetResult()} deterministic file/identity fixture checks passed; no live Worker or normal discovery changed.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--rich-content-only")
            {
                Console.WriteLine($"Rich content: {RichContentTests.Run()} deterministic native document/clipboard checks passed; no window, network or inference.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--desktop-icons-only")
            {
                Console.WriteLine($"Desktop icons: {DesktopIconTests.Run()} offscreen native asset/theme checks passed; no inference or network.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--background-render-only")
            {
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Console.WriteLine($"Background render: {BackgroundRenderScopeTests.Run()} native WPF checks passed; no inference or network.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--runtime-paths-only")
            {
                Console.WriteLine($"Runtime roots: {RuntimePathTests.Run()} deterministic topology checks passed; no registry writes, inference or network.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--connection-store-only")
            {
                Console.WriteLine($"Connection store: {ConnectionStoreChecks.Verify()} isolated file and identity-policy checks passed; no installed Worker, service or user connection was opened.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--instance-paths-only")
            {
                Console.WriteLine($"Desktop instance paths: {DesktopInstancePathsTests.Run()} deterministic process/file checks passed; no inference or network.");
                return 0;
            }
            if (args.FirstOrDefault() == "--instance-paths-child")
            {
                DesktopInstancePathsTests.RunChild(args);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--single-instance-only")
            {
                SingleInstanceFixture();
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, singleInstanceChecks = checks }));
                return 0;
            }
            if (args.Length == 1 && args[0] == "--managed-plugin-owner-only")
            {
                var result = ConfigurationManagedPluginOwnerBindingChecks.Run();
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, managedOwnerChecks = result.Checks,
                    reparseChecked = result.ReparseChecked, workerCommandsExecuted = false }));
                return 0;
            }
            if (args.Length == 2 && args[0] == "--goal-card-only")
            {
                Console.WriteLine($"Goal card: {GoalCardPluginTests.Run(args[1])} deterministic WPF/adapter checks passed; no inference or network.");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--prompt-review-only")
            {
                Console.WriteLine($"Prompt review: {PromptReviewPluginTests.Run(args[1])} deterministic WPF/adapter checks passed; no inference or network.");
                return 0;
            }
            if (args.FirstOrDefault() == "--appearance-theme-only")
            {
                AppearanceThemeTests.Run(Application.Current);
                return 0;
            }
            if (args.Length == 1 && args[0] is "--conversation-settings-runtime-only" or "--conversation-settings-runtime-themed-only")
            {
                var application = Application.Current;
                var themedHost = args[0] == "--conversation-settings-runtime-themed-only";
                // This diagnostic matches the real host theme-before-plugin order. Keep the cold
                // selector separate: a themed result cannot establish arbitrary WPF unload safety.
                if (themedHost) Ui.ApplyTheme(application);
                Console.WriteLine($"Conversation settings diagnostic host: {(themedHost ? "real shared theme applied before packages" : "cold bare Application")}; no shown window.");
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                application.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    var exit = 0;
                    try
                    {
                        var count = await ConversationSettingsRuntimeTests.RunAsync();
                        Console.WriteLine($"Conversation settings runtime: {count} native package-loading/offscreen WPF checks passed with deterministic in-process command replies; no shown window, Worker, inference or network.");
                    }
                    catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                    finally { application.Shutdown(exit); }
                }));
                return application.Run();
            }
            if (args.Length == 1 && args[0] == "--desktop-plugin-exit-retirement-only")
            {
                var application = Application.Current;
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                application.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    var exit = 0;
                    try { Console.WriteLine(await ConversationSettingsRuntimeTests.RunExitRetirementOnlyAsync()); }
                    catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                    finally { application.Shutdown(exit); }
                }));
                return application.Run();
            }
            if (args.Length == 3 && args[0] == "--desktop-plugin-exit-retirement-child")
            {
                var application = Application.Current;
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var finished = 0;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(15));
                    if (Volatile.Read(ref finished) == 0) Environment.Exit(124);
                });
                application.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    var exit = 0;
                    try { await ConversationSettingsRuntimeTests.RunExitRetirementChildAsync(args[1], args[2]); }
                    catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                    finally { application.Shutdown(exit); }
                }));
                try { return application.Run(); }
                finally { Volatile.Write(ref finished, 1); }
            }
            if (args.Length == 1 && args[0] == "--reconnect-only")
            {
                var application = Application.Current;
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                application.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    var exit = 0;
                    try
                    {
                        var count = await ConnectedWindowReconnectTests.RunAsync();
                        Console.WriteLine($"Connected window reconnect: {count} deterministic same-user named-pipe fixture and offscreen WPF checks passed; no shown window, Worker restart, models, inference or Source acceptance.");
                    }
                    catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                    finally { application.Shutdown(exit); }
                }));
                return application.Run();
            }
            if (args.Length == 1 && args[0] == "--generation-preview-only")
            {
                var application = Application.Current;
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                application.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    var exit = 0;
                    try
                    {
                        var count = await WorkflowGenerationPreviewChecks.VerifyAsync();
                        var metrics = WorkflowGenerationMetricChecks.Verify();
                        Console.WriteLine($"Generation metrics: {metrics} deterministic rate aggregation checks passed.");
                        Console.WriteLine($"Generation preview: {count} deterministic offscreen WPF/adapter checks passed; no shown window, Worker, inference or network.");
                    }
                    catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                    finally { application.Shutdown(exit); }
                }));
                return application.Run();
            }
            if (args.Length == 1 && args[0] == "--workflow-start-only")
            {
                var application = Application.Current;
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                application.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    var exit = 0;
                    try { await WorkflowStartChecks.VerifyAsync(); }
                    catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                    finally { application.Shutdown(exit); }
                }));
                return application.Run();
            }
            if (args.Length == 1 && args[0] == "--workflow-chat-only")
            {
                var application = Application.Current;
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                application.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    var exit = 0;
                    try
                    {
                        var count = await WorkflowChatWorkspaceChecks.VerifyAsync();
                        Console.WriteLine($"Native Chat: {count} deterministic WPF/adapter checks passed; no inference or network.");
                    }
                    catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                    finally { application.Shutdown(exit); }
                }));
                return application.Run();
            }
            if (args.FirstOrDefault() == "--validate-plugin-packages")
            {
                var root = Path.GetFullPath(args[1]);
                var packages = Directory.EnumerateFiles(root, "*.CerebralPlugin", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray();
                if (packages.Length == 0) throw new InvalidDataException("No .CerebralPlugin packages were found.");
                foreach (var package in packages)
                {
                    using var prepared = CerebralPluginPackage.Prepare(package);
                    if (prepared.Inspection.Package.Id != prepared.Inspection.Desktop.Id ||
                        prepared.Inspection.Package.Version != prepared.Inspection.Desktop.Version)
                        throw new InvalidDataException("Plugin package identity changed during admission.");
                }
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, packageCount = packages.Length,
                    packages = packages.Select(Path.GetFileName).ToArray(), nativeCodeLoaded = false }));
                return 0;
            }
            if (args.FirstOrDefault() == "--instance-child")
            {
                var identity = new DesktopApplicationIdentity(args[2],
                    args[3] == "-" ? null : args[3], args[4] == "-" ? null : args[4]);
                using var childInstance = new DesktopApplicationInstance(args[1], identity);
                var activationRequested = !childInstance.IsPrimary && childInstance.RequestActivation();
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    success = true,
                    isPrimary = childInstance.IsPrimary,
                    activationRequested,
                    identityMatches = childInstance.IdentityMatches,
                    ownerIsCurrent = childInstance.OwnerIsCurrent,
                    ownerProcessId = childInstance.OwnerProcessId,
                    ownerMode = childInstance.OwnerMode,
                    ownerDataRoot = childInstance.OwnerDataRoot,
                    ownerIdentity = childInstance.OwnerIdentity,
                    requestedMode = childInstance.RequestedMode,
                    requestedDataRoot = childInstance.RequestedDataRoot,
                    requestedIdentity = childInstance.RequestedIdentity
                }));
                return 0;
            }
            if (args.FirstOrDefault() == "--plugin-child")
            {
                NativeExample(args[1]);
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, nativeDllChecks = checks, nativePanelLayout, workerCommandsExecuted = false }));
                return 0;
            }
            if (args.FirstOrDefault() == "--review-plugin-child")
            {
                ReviewExample(args[1]);
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, reviewPluginChecks = checks, nativePanelLayout, workerCommandsExecuted = false }));
                return 0;
            }
            if (args.FirstOrDefault() == "--generated-plugin-child")
            {
                GeneratedPluginExample(args[1], args[2]);
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, generatedPluginChecks = checks,
                    nativePanelLayout, capturePath = Path.GetFullPath(args[2]), fixturePath = generatedPluginFixture,
                    workerCommandsExecuted = false }));
                return 0;
            }
            if (args.FirstOrDefault() == "--dictation-child")
            {
                DictationExample(args[1]);
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, dictationChecks = checks, nativePanelLayout, speechEnvironment, microphoneRecognitionExercised = false, workerCommandsExecuted = false }));
                return 0;
            }
#if CEREBRAL_BOOKS
            if (args.FirstOrDefault() == "--book-child")
            {
                // Direct development invocation accepts the actual built DLL; child invocations use the
                // temporary seeded catalog path so the real per-user plugin repository stays untouched.
                var bookChildChecks = File.Exists(args[1]) && Path.GetExtension(args[1]).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                    ? BookExampleInChild(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, DesktopPluginCatalog.ManifestFileName))
                    : CheckSeededBook(args[1]);
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, bookChecks = bookChildChecks, nativePanelLayout, workerCommandsExecuted = false }));
                return 0;
            }
#else
            if (args.FirstOrDefault() == "--book-child")
                throw new NotSupportedException("Books checks are not included in this build.");
#endif
            if (args.FirstOrDefault() == "--new-plugins-child")
            {
                NewPluginExamples(args[1], args[2]);
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, newPluginChecks = checks, nativePanelLayout, workerCommandsExecuted = false, modelInvoked = false }));
                return 0;
            }
            if (args.FirstOrDefault() == "--collaboration-plugin-child")
            {
                CollaborationPluginExample(args[1], args.Skip(2).Contains("--shown", StringComparer.Ordinal));
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, collaborationPluginChecks = checks,
                    nativePanelLayout, workerCommandsExecuted = true, modelInvoked = false }));
                return 0;
            }
            if (args.FirstOrDefault() == "--dependency-runtime-child")
            {
                DependencyRuntimeChild(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, dependencyRuntimeChecks = checks }));
                return 0;
            }
            if (args.FirstOrDefault() == "--plugin-reopen-only")
            {
                PluginReopenFixture(args[1]);
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, pluginReopenChecks = checks,
                    workerCommandsExecuted = false, modelInvoked = false }));
                return 0;
            }
            if (args.FirstOrDefault() == "--plugin-reopen-child")
            {
                PluginReopenRuntimeChild(args[1]);
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, pluginReopenChecks = checks,
                    workerCommandsExecuted = false, modelInvoked = false }));
                return 0;
            }
            if (args.FirstOrDefault() == "--plugin-stress")
            {
                DesktopPluginStress(args[1], args.Length >= 3 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 100);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--plugin-invocations-only")
            {
                Console.WriteLine(PluginInvocationTests.Run());
                return 0;
            }
            if (args.Length == 2 && args[0] == "--plugin-invocation-child")
            {
                PluginInvocationTests.RunChild(args[1]);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--plugin-publication-leases-only")
            {
                Console.WriteLine($"Plugin publication leases: {PluginPublicationLeaseTests.Run()} actual cross-process file/lease and offscreen WPF checks passed; no native plugin code, network or inference.");
                return 0;
            }
            if (args.Length == 3 && args[0] == "--plugin-publication-lease-child")
            {
                PluginPublicationLeaseTests.RunChild(args);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--plugin-package-retirement-only")
            {
                Console.WriteLine($"Plugin package retirement: {PluginPackageRetirementTests.Run()} isolated filesystem/Windows-handle checks passed; no owner catalog, native entrypoint execution, network or inference.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--plugin-update-transaction-only")
            {
                Console.WriteLine($"Plugin update transaction: {PluginUpdateTransactionTests.Run()} isolated filesystem/lock fixtures passed; no native plugin execution, network or inference.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--native-plugin-management-only")
            {
                Console.WriteLine($"Native plugin management: {NativePluginManagementCatalogTests.Run()} temporary real-catalog/runtime and exact-CAS checks passed; no owner catalog, network or inference.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--plugin-package-policy-only")
            {
                DesktopDependencyFixtures();
                DesktopExclusiveProviderFixtures();
                PluginPackageFixtures();
                Console.WriteLine($"Plugin package policy: {checks} deterministic package/dependency/resource fixtures passed; no native plugin execution, network or inference.");
                return 0;
            }
            TokenBalanceFixtures();
            TextEntryIndicatorFixtures();
            WritingAssistanceFixtures();
            MountRegistryFixtures();
            DesktopDependencyFixtures();
            DesktopExclusiveProviderFixtures();
            PluginPackageFixtures();
            AdapterFixtures();
            SingleInstanceFixture();
            if (args.Length >= 1) DesktopDependencyRuntimeFixture(args[0]);
            var nativeChecks = args.Length >= 1 ? NativeExampleInChild(args[0]) : (int?)null;
            var dictationChecks = args.Length >= 2 ? DictationExampleInChild(args[1]) : (int?)null;
#if CEREBRAL_BOOKS
            const bool booksIncluded = true;
            var bookChecks = args.Length >= 3 ? BookExampleInChild(args[2]) : (int?)null;
#else
            const bool booksIncluded = false;
            if (args.Length >= 3 && args[2] != "-")
                throw new NotSupportedException("Books checks are not included; use '-' for the third manifest argument.");
            int? bookChecks = null;
            Console.WriteLine("Books desktop-plugin checks: not included in this build.");
#endif
            var reviewPluginChecks = args.Length >= 4 ? ReviewExampleInChild(args[3]) : (int?)null;
            var fixtureChecks = checks;
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, adapterFixtureChecks = fixtureChecks, nativeDllChecks = nativeChecks,
                dictationChecks, booksIncluded, bookChecks, reviewPluginChecks, nativePanelLayout, speechEnvironment, microphoneRecognitionExercised = false, workerCommandsExecuted = false }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }

    private static void MountRegistryFixtures()
    {
        static DesktopMountedContribution Mount(string plugin, string id, string slot, DesktopMountKind kind,
            int order = 0, string? replaces = null, DesktopMountSurfaces surfaces = DesktopMountSurfaces.Orchestrator,
            IReadOnlyList<string>? roles = null) => new(plugin, new DesktopMountDescriptor(id, slot)
            {
                Order = order,
                Replaces = replaces,
                Applicability = new() { Surfaces = surfaces, MessageRoles = roles ?? [] }
            }, kind, new object());
        static bool Rejected(Action action)
        {
            try { action(); return false; }
            catch (InvalidDataException) { return true; }
        }

        var orchestrator = DesktopMountRegistry.Capabilities(DesktopPluginSurface.Orchestrator);
        var configuration = DesktopMountRegistry.Capabilities(DesktopPluginSurface.Configuration);
        Assert(orchestrator.Count == DesktopMountSlots.Known.Count &&
               orchestrator.Single(x => x.Slot == DesktopMountSlots.WorkspaceTree).Supported &&
               orchestrator.Single(x => x.Slot == DesktopMountSlots.NewConversationStart) is { Supported: true, Kinds.Count: 1 } &&
               orchestrator.Single(x => x.Slot == DesktopMountSlots.StatusBar) is { Supported: true, Kinds.Count: 1 } &&
               orchestrator.Single(x => x.Slot == DesktopMountSlots.Menus) is { Supported: true, Kinds.Count: 1 } &&
               configuration.Count(x => x.Supported) == 1 &&
               configuration.Single(x => x.Slot == DesktopMountSlots.ConfigurationPages).Supported,
            "Mount discovery must enumerate every stable slot while advertising only actually mounted surface capabilities.");

        var ordered = DesktopMountRegistry.Resolve(
        [
            Mount("z.plugin", "later-id", DesktopMountSlots.ComposerActions, DesktopMountKind.Action, 20),
            Mount("a.plugin", "same-order-b", DesktopMountSlots.ComposerActions, DesktopMountKind.Action, 10),
            Mount("b.plugin", "same-order-a", DesktopMountSlots.ComposerActions, DesktopMountKind.Action, 10),
            Mount("config.only", "ignored", DesktopMountSlots.ConfigurationPages, DesktopMountKind.View, surfaces: DesktopMountSurfaces.Configuration)
        ], DesktopPluginSurface.Orchestrator);
        Assert(ordered.Select(x => x.QualifiedId).SequenceEqual(["a.plugin:same-order-b", "b.plugin:same-order-a", "z.plugin:later-id"]),
            "Active mounts must sort by explicit order and then stable qualified ID, while other-surface contributions stay inactive.");

        var replaced = DesktopMountRegistry.Resolve(
        [
            Mount("base.plugin", "tree", DesktopMountSlots.WorkspaceTree, DesktopMountKind.View, 10),
            Mount("chosen.plugin", "tree", DesktopMountSlots.WorkspaceTree, DesktopMountKind.View, 20, "base.plugin:tree")
        ], DesktopPluginSurface.Orchestrator);
        Assert(replaced.Count == 1 && replaced[0].QualifiedId == "chosen.plugin:tree",
            "One explicit replacement must remove its exact target while retaining the selected provider.");
        Assert(Rejected(() => DesktopMountRegistry.Resolve(
        [
            Mount("base.plugin", "tree", DesktopMountSlots.WorkspaceTree, DesktopMountKind.View),
            Mount("one.plugin", "tree", DesktopMountSlots.WorkspaceTree, DesktopMountKind.View, replaces: "base.plugin:tree"),
            Mount("two.plugin", "tree", DesktopMountSlots.WorkspaceTree, DesktopMountKind.View, replaces: "base.plugin:tree")
        ], DesktopPluginSurface.Orchestrator)),
            "Two providers cannot silently replace the same target.");
        Assert(Rejected(() => DesktopMountRegistry.Resolve(
            [Mount("one.plugin", "tree", DesktopMountSlots.WorkspaceTree, DesktopMountKind.View, replaces: "absent.plugin:tree")],
            DesktopPluginSurface.Orchestrator)),
            "A replacement target must exist in the same active slot.");
        Assert(Rejected(() => DesktopMountRegistry.Resolve(
        [
            Mount("one.plugin", "tree", DesktopMountSlots.WorkspaceTree, DesktopMountKind.View, replaces: "two.plugin:tree"),
            Mount("two.plugin", "tree", DesktopMountSlots.WorkspaceTree, DesktopMountKind.View, replaces: "one.plugin:tree")
        ], DesktopPluginSurface.Orchestrator)),
            "Replacement cycles must fail before any view is mounted.");
        Assert(Rejected(() => DesktopMountRegistry.Resolve(
        [
            Mount("same.plugin", "duplicate", DesktopMountSlots.ComposerActions, DesktopMountKind.Action),
            Mount("same.plugin", "duplicate", DesktopMountSlots.ComposerActions, DesktopMountKind.Action)
        ], DesktopPluginSurface.Orchestrator)),
            "Qualified mount IDs must be unique within a slot.");
        Assert(Rejected(() => DesktopMountRegistry.Resolve(
            [Mount("bad.plugin", "menu", DesktopMountSlots.Menus, DesktopMountKind.View)],
            DesktopPluginSurface.Orchestrator)),
            "The tray menu slot must reject views because it mounts actions only.");
        Assert(Rejected(() => DesktopMountRegistry.Resolve(
        [
            Mount("one.plugin", "render", DesktopMountSlots.MessageRenderers, DesktopMountKind.MessageRenderer, roles: ["assistant"]),
            Mount("two.plugin", "render", DesktopMountSlots.MessageRenderers, DesktopMountKind.MessageRenderer, roles: ["assistant"])
        ], DesktopPluginSurface.Orchestrator)),
            "Overlapping message replacements require an explicit selected provider.");
        var disjoint = DesktopMountRegistry.Resolve(
        [
            Mount("one.plugin", "render", DesktopMountSlots.MessageRenderers, DesktopMountKind.MessageRenderer, roles: ["assistant"]),
            Mount("two.plugin", "render", DesktopMountSlots.MessageRenderers, DesktopMountKind.MessageRenderer, roles: ["user"])
        ], DesktopPluginSurface.Orchestrator);
        Assert(disjoint.Count == 2, "Disjoint message-renderer applicability may coexist.");

        var accessoryContent = new Border { Width = 2000, Height = 400 };
        var accessory = DesktopMountLayout.AccessoryFrame(accessoryContent);
        accessory.Measure(new Size(1000, 1000)); accessory.Arrange(new Rect(0, 0, accessory.DesiredSize.Width, accessory.DesiredSize.Height));
        Assert(accessory.ActualWidth <= DesktopMountLayout.AccessoryMaximumWidth &&
               accessory.ActualHeight <= DesktopMountLayout.AccessoryMaximumHeight,
            "Composer accessories must remain inside the host's bounded layout frame.");
        accessory.Child = null;
        var treeContent = new Border { Width = 800, Height = 1200 };
        var tree = DesktopMountLayout.WorkspaceTreeFrame("Fixture tree", treeContent);
        tree.Measure(new Size(400, 1000)); tree.Arrange(new Rect(0, 0, 400, tree.DesiredSize.Height)); tree.UpdateLayout();
        var scroll = (ScrollViewer)tree.Content;
        Assert(scroll.ActualHeight <= DesktopMountLayout.WorkspaceTreeMaximumHeight &&
               ReferenceEquals(scroll.Content, treeContent),
            "A plugin workspace hierarchy must remain a bounded collapsible branch beside the core tree.");
        scroll.Content = null; tree.Content = null;
    }

    private static void PluginPackageFixtures()
    {
        var root = Path.Combine(Path.GetTempPath(), "PegasusCerebral.PluginPackageTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            static DesktopProductPaths ProductPaths(string root) => new(
                Path.Combine(root, "install"), Path.Combine(root, "data"), Path.Combine(root, "user"),
                Path.Combine(root, "projects"), Path.Combine(root, "models"), Path.Combine(root, "tools"),
                Path.Combine(root, "shared"));
            static void WriteResources(string path, params CerebralPluginPackageResource[] resources) =>
                File.WriteAllText(path, JsonSerializer.Serialize(new CerebralPluginResourceManifest
                    { SchemaVersion = 1, Resources = resources.ToList() }, CerebralJson.Options));

            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            File.Copy(typeof(SkillManifest).Assembly.Location, Path.Combine(source, "Fixture.Plugin.dll"), overwrite: false);
            File.WriteAllText(Path.Combine(source, DesktopPluginCatalog.ManifestFileName), JsonSerializer.Serialize(new DesktopPluginManifest
            {
                SchemaVersion = 1, Contract = DesktopPluginContract.Id, ApiVersion = DesktopPluginContract.Version,
                Id = "fixture.package", Name = "Fixture package", Version = "1.2.3", Description = "Package fixture",
                Assembly = "Fixture.Plugin.dll", EntryType = "Fixture.Plugin"
            }, CerebralJson.Options));
            File.WriteAllText(Path.Combine(source, CerebralPluginPackageContract.DefaultSettingsFileName), "{\"style\":\"default\"}");
            var modelResource = new CerebralPluginPackageResource
                { Id = "fixture.package.model", Kind = "model", Source = "resources/model", Destination = "fixture.package/model" };
            var dataResource = new CerebralPluginPackageResource
                { Id = "fixture.package.data", Kind = "data", Source = "resources/data", Destination = "fixture.package/model" };
            WriteResources(Path.Combine(source, CerebralPluginPackageContract.ResourceManifestFileName), modelResource, dataResource);
            var manyFiles = Path.Combine(source, "native-fixtures"); Directory.CreateDirectory(manyFiles);
            for (var index = 0; index < 513; index++)
                File.WriteAllText(Path.Combine(manyFiles, $"fixture-{index:D3}.txt"), "fixture");
            var sourceCatalog = new DesktopPluginCatalog(Path.Combine(root, "source-catalog"));
            var installed = sourceCatalog.Install(Path.Combine(source, DesktopPluginCatalog.ManifestFileName));
            Assert(Directory.EnumerateFiles(installed.Directory, "*", SearchOption.AllDirectories).Count() > 512,
                "Native package admission uses the raised self-contained package file limit consistently.");
            var sourcePluginData = Path.Combine(root, "source-plugin-data");
            var sourceData = Path.Combine(sourcePluginData, installed.Id); Directory.CreateDirectory(sourceData);
            File.WriteAllText(Path.Combine(sourceData, CerebralPluginPackageContract.CurrentSettingsFileName), "{\"style\":\"current\"}");
            var sourcePaths = ProductPaths(Path.Combine(root, "source-products"));
            var sourceModel = Path.Combine(sourcePaths.ModelsDirectory, "fixture.package", "model"); Directory.CreateDirectory(sourceModel);
            var modelBytes = Enumerable.Range(0, 8192).Select(index => (byte)(index * 31)).ToArray();
            File.WriteAllBytes(Path.Combine(sourceModel, "weights.gguf"), modelBytes);
            var sourceProductData = Path.Combine(sourcePaths.DataDirectory, "fixture.package", "model"); Directory.CreateDirectory(sourceProductData);
            File.WriteAllText(Path.Combine(sourceProductData, "seed.db"), "portable-data");
            var output = Path.Combine(root, "Fixture" + CerebralPluginPackageContract.Extension);
            CerebralPluginPackage.Export(installed, output, "current", sourcePaths, sourcePluginData);
            Assert(File.Exists(output), "Plugin export publishes one .CerebralPlugin container.");
            using (var archive = ZipFile.OpenRead(output))
            {
                var modelEntry = archive.GetEntry("plugin/resources/model/weights.gguf") ??
                    throw new InvalidDataException("Export omitted the declared model resource.");
                Assert(modelEntry.Length == modelBytes.Length && modelEntry.CompressedLength == modelEntry.Length,
                    "Declared resource payloads are stored without recompressing model bytes.");
            }
            using (var prepared = CerebralPluginPackage.Prepare(output))
            {
                Assert(prepared.Inspection.Package.Id == installed.Id && prepared.Inspection.Package.SettingsMode == "current" &&
                       prepared.Inspection.Package.SchemaVersion == 2 && prepared.Inspection.Package.Resources?.Count == 2 &&
                       prepared.Inspection.Desktop.Version == "1.2.3" && prepared.Settings is not null,
                    "Plugin preparation validates matching schema-2 resource, desktop identity, and preset contracts.");
                Assert(File.Exists(Path.Combine(Path.GetDirectoryName(prepared.DesktopManifestPath)!,
                           CerebralPluginPackageContract.ResourceManifestFileName)) &&
                       !Directory.Exists(Path.Combine(Path.GetDirectoryName(prepared.DesktopManifestPath)!, "resources")),
                    "The catalog-ready desktop payload retains its resource descriptor without retaining a second resource copy.");

                var targetPaths = ProductPaths(Path.Combine(root, "target-products"));
                var aliasedPaths = targetPaths with { DataDirectory = targetPaths.ModelsDirectory };
                var aliasRejected = false;
                try { using var _ = prepared.BeginResourceInstallation(aliasedPaths); }
                catch (InvalidDataException) { aliasRejected = true; }
                Assert(aliasRejected, "Resolved resource destinations cannot overlap when configured product roots alias each other.");
                var targetModel = Path.Combine(targetPaths.ModelsDirectory, "fixture.package", "model"); Directory.CreateDirectory(targetModel);
                File.WriteAllText(Path.Combine(targetModel, "prior.txt"), "prior-model");
                var blockedData = Path.Combine(targetPaths.DataDirectory, "fixture.package", "model");
                Directory.CreateDirectory(Path.GetDirectoryName(blockedData)!); File.WriteAllText(blockedData, "blocking-file");
                var publicationFailed = false;
                try
                {
                    using var transaction = prepared.BeginResourceInstallation(targetPaths);
                    transaction.Publish();
                }
                catch (IOException) { publicationFailed = true; }
                Assert(publicationFailed && File.ReadAllText(Path.Combine(targetModel, "prior.txt")) == "prior-model" &&
                       File.ReadAllText(blockedData) == "blocking-file",
                    "A later resource publication failure restores every earlier destination in the transaction.");
                File.Delete(blockedData);

                using (var transaction = prepared.BeginResourceInstallation(targetPaths))
                {
                    transaction.Publish();
                }
                Assert(File.Exists(Path.Combine(targetModel, "prior.txt")) && !File.Exists(Path.Combine(targetModel, "weights.gguf")),
                    "Disposing an uncommitted resource transaction restores the previous product resource.");

                using (var transaction = prepared.BeginResourceInstallation(targetPaths))
                {
                    transaction.Publish(); transaction.Commit();
                }
                Assert(File.ReadAllBytes(Path.Combine(targetModel, "weights.gguf")).SequenceEqual(modelBytes) &&
                       File.ReadAllText(Path.Combine(blockedData, "seed.db")) == "portable-data",
                    "Committing a resource transaction publishes all declared resources to DesktopProductPaths.");

                var targetCatalog = new DesktopPluginCatalog(Path.Combine(root, "target-catalog"));
                var imported = targetCatalog.Install(prepared.DesktopManifestPath);
                prepared.ApplyPortableSettings(Path.Combine(root, "target-data"));
                Assert(!imported.Enabled && File.ReadAllText(Path.Combine(root, "target-data", imported.Id,
                           CerebralPluginPackageContract.CurrentSettingsFileName)).Contains("current", StringComparison.Ordinal),
                    "Plugin import remains disabled and applies only its bounded portable settings document.");
                Assert(File.Exists(Path.Combine(imported.Directory, CerebralPluginPackageContract.ResourceManifestFileName)) &&
                       !Directory.Exists(Path.Combine(imported.Directory, "resources")),
                    "The installed native generation contains its descriptor but no duplicate resource payload.");

                Directory.Delete(targetModel, true); Directory.CreateDirectory(targetModel);
                File.WriteAllText(Path.Combine(targetModel, "catalog-failure.txt"), "restore-me");
                var catalogFailed = false;
                try
                {
                    using var transaction = prepared.BeginResourceInstallation(targetPaths);
                    transaction.Publish();
                    _ = targetCatalog.Install(prepared.DesktopManifestPath);
                    transaction.Commit();
                }
                catch (IOException) { catalogFailed = true; }
                Assert(catalogFailed && File.ReadAllText(Path.Combine(targetModel, "catalog-failure.txt")) == "restore-me",
                    "Catalog admission failure rolls a published resource transaction back to its prior contents.");
            }

            var mismatch = Path.Combine(root, "Mismatch" + CerebralPluginPackageContract.Extension);
            File.Copy(output, mismatch);
            using (var archive = ZipFile.Open(mismatch, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry(CerebralPluginPackageContract.ManifestName)!;
                CerebralPluginPackageManifest package;
                using (var input = entry.Open()) package = JsonSerializer.Deserialize<CerebralPluginPackageManifest>(input, CerebralJson.Options)!;
                entry.Delete(); package.Resources![0].Destination = "different/model";
                using var target = archive.CreateEntry(CerebralPluginPackageContract.ManifestName).Open();
                JsonSerializer.Serialize(target, package, CerebralJson.Options);
            }
            var mismatchRejected = false;
            try { using var _ = CerebralPluginPackage.Prepare(mismatch); }
            catch (InvalidDataException) { mismatchRejected = true; }
            Assert(mismatchRejected, "Schema-2 admission rejects outer resource declarations that differ from the installed descriptor.");

            var unknownField = Path.Combine(root, "UnknownField" + CerebralPluginPackageContract.Extension);
            File.Copy(output, unknownField);
            using (var archive = ZipFile.Open(unknownField, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry(CerebralPluginPackageContract.ManifestName)!;
                JsonObject document;
                using (var input = entry.Open()) document = JsonNode.Parse(input)!.AsObject();
                entry.Delete();
                document["resources"]!.AsArray()[0]!.AsObject()["unexpected"] = true;
                using var target = archive.CreateEntry(CerebralPluginPackageContract.ManifestName).Open();
                JsonSerializer.Serialize(target, document, CerebralJson.Options);
            }
            var unknownRejected = false;
            try { using var _ = CerebralPluginPackage.Prepare(unknownField); }
            catch (InvalidDataException) { unknownRejected = true; }
            Assert(unknownRejected, "Schema-2 resource objects reject unknown fields before extraction or installation.");

            var traversal = Path.Combine(root, "Traversal" + CerebralPluginPackageContract.Extension);
            File.Copy(output, traversal);
            using (var archive = ZipFile.Open(traversal, ZipArchiveMode.Update)) archive.CreateEntry("../escape.txt");
            var rejected = false;
            try { using var _ = CerebralPluginPackage.Prepare(traversal); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected && !File.Exists(Path.Combine(root, "escape.txt")), "Plugin packages reject traversal entries before extraction.");

            var contentOnly = Path.Combine(root, "ContentOnly" + CerebralPluginPackageContract.Extension);
            using (var archive = ZipFile.Open(contentOnly, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("named/skill.json").Open())) writer.Write("{}");
                using (var writer = new StreamWriter(archive.CreateEntry("named/SKILL.md").Open())) writer.Write("# Named skill");
            }
            using (var archive = BoundedContentZipArchive.OpenRead(contentOnly))
            {
                Assert(archive.Files.Count == 2, "Content-only ZIPs pass shared structural admission without a code payload.");
                var skillEntry = archive.Files.Single(entry => entry.FullName == "named/SKILL.md");
                Assert(System.Text.Encoding.UTF8.GetString(archive.ReadEntryBytes(skillEntry, 32 * 1024)) == "# Named skill",
                    "Shared member reads use a bounded stream from the retained archive.");
                var memberLimitRejected = false;
                try { _ = archive.ReadEntryBytes(skillEntry, 1); }
                catch (InvalidDataException) { memberLimitRejected = true; }
                Assert(memberLimitRejected, "Shared member reads refuse an entry over the caller's byte limit.");
                using (var other = BoundedContentZipArchive.OpenRead(contentOnly))
                {
                    var foreignEntryRejected = false;
                    try { _ = archive.ReadEntryBytes(other.Files[0], 32 * 1024); }
                    catch (InvalidDataException) { foreignEntryRejected = true; }
                    Assert(foreignEntryRejected, "Shared member reads refuse an entry from another archive handle.");
                }
                var extracted = Path.Combine(root, "content-extracted"); Directory.CreateDirectory(extracted);
                archive.ExtractToNewDirectory(extracted);
                Assert(File.ReadAllText(Path.Combine(extracted, "named", "SKILL.md")) == "# Named skill",
                    "The validated archive extracts from the retained read handle into an empty owned root.");
                var nonemptyRejected = false;
                try { archive.ExtractToNewDirectory(extracted); }
                catch (InvalidDataException) { nonemptyRejected = true; }
                Assert(nonemptyRejected, "Shared extraction refuses a destination with existing content.");
            }
            var contentAsPluginRejected = false;
            try { using var _ = CerebralPluginPackage.Prepare(contentOnly); }
            catch (InvalidDataException) { contentAsPluginRejected = true; }
            Assert(contentAsPluginRejected, "Content-only archives cannot masquerade as code-plugin packages.");

            var explicitDirectory = Path.Combine(root, "ExplicitDirectory.zip");
            using (var archive = ZipFile.Open(explicitDirectory, ZipArchiveMode.Create))
            {
                archive.CreateEntry("named/");
                archive.CreateEntry("named/SKILL.md");
            }
            var directoryRejected = false;
            try { using var _ = BoundedContentZipArchive.OpenRead(explicitDirectory); }
            catch (InvalidDataException) { directoryRejected = true; }
            Assert(directoryRejected, "Shared ZIP admission preserves the plugin policy against explicit directory entries.");

            var caseCollision = Path.Combine(root, "CaseCollision.zip");
            using (var archive = ZipFile.Open(caseCollision, ZipArchiveMode.Create))
            {
                archive.CreateEntry("named/SKILL.md");
                archive.CreateEntry("Named/skill.md");
            }
            var caseRejected = false;
            try { using var _ = BoundedContentZipArchive.OpenRead(caseCollision); }
            catch (InvalidDataException) { caseRejected = true; }
            Assert(caseRejected, "Shared ZIP admission refuses case-insensitive duplicate file paths.");

            var linkedEntry = Path.Combine(root, "LinkedEntry.zip");
            using (var archive = ZipFile.Open(linkedEntry, ZipArchiveMode.Create))
                archive.CreateEntry("named/SKILL.md").ExternalAttributes = unchecked((int)0xA0000000);
            var linkRejected = false;
            try { using var _ = BoundedContentZipArchive.OpenRead(linkedEntry); }
            catch (InvalidDataException) { linkRejected = true; }
            Assert(linkRejected, "Shared ZIP admission refuses Unix symbolic-link entries.");

            foreach (var (name, member) in new[]
                     {
                         ("Reserved", "named/CON.txt"), ("ReservedSpace", "named/CON .txt"),
                         ("TrailingDot", "named/alias."),
                         ("InvalidCharacter", "named/bad?.md"), ("LongUtf8Segment", "named/" + new string('é', 130))
                     })
            {
                var badPath = Path.Combine(root, name + ".zip");
                using (var archive = ZipFile.Open(badPath, ZipArchiveMode.Create)) archive.CreateEntry(member);
                var badNameRejected = false;
                try { using var _ = BoundedContentZipArchive.OpenRead(badPath); }
                catch (InvalidDataException) { badNameRejected = true; }
                Assert(badNameRejected, "Shared ZIP admission refuses Windows-aliased, invalid, and overlong UTF-8 names: " + name);
            }

            var fileDirectoryCollision = Path.Combine(root, "FileDirectoryCollision.zip");
            using (var archive = ZipFile.Open(fileDirectoryCollision, ZipArchiveMode.Create))
            {
                archive.CreateEntry("named");
                archive.CreateEntry("named/SKILL.md");
            }
            var collisionRejected = false;
            try { using var _ = BoundedContentZipArchive.OpenRead(fileDirectoryCollision); }
            catch (InvalidDataException) { collisionRejected = true; }
            Assert(collisionRejected, "Shared ZIP admission refuses a file that also names a parent directory.");

            var longTarget = Path.Combine(root, "LongTarget.zip");
            using (var archive = ZipFile.Open(longTarget, ZipArchiveMode.Create))
                archive.CreateEntry("named/" + new string('a', 220));
            using (var archive = BoundedContentZipArchive.OpenRead(longTarget))
            {
                var destination = Path.Combine(root, "long-target-empty"); Directory.CreateDirectory(destination);
                var targetRejected = false;
                try { archive.ExtractToNewDirectory(destination); }
                catch (InvalidDataException) { targetRejected = true; }
                Assert(targetRejected && !Directory.EnumerateFileSystemEntries(destination).Any(),
                    "Shared ZIP extraction refuses an overlong canonical target before writing directories or files.");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void SingleInstanceFixture()
    {
        var applicationId = "Pegasus.Cerebral.Test." + Guid.NewGuid().ToString("N");
        var ownerIdentity = DesktopApplicationIdentity.SourceLocal(@"C:\fixture-one");
        using var owner = new DesktopApplicationInstance(applicationId, ownerIdentity);
        Assert(owner.IsPrimary && owner.OwnerIsCurrent && owner.OwnerProcessId == Environment.ProcessId &&
               owner.OwnerMode == "source-local" && owner.OwnerDataRoot == ownerIdentity.DataRoot,
            "The first desktop process must publish its exact live mode, canonical data root and process generation.");
        var activations = 0;
        owner.Listen(Dispatcher.CurrentDispatcher, () => activations++);
        (bool IsPrimary, bool ActivationRequested, bool IdentityMatches, bool OwnerIsCurrent,
            int? OwnerProcessId, string? OwnerMode, string? OwnerDataRoot, string OwnerIdentity,
            string RequestedMode, string? RequestedDataRoot, string RequestedIdentity) Launch(DesktopApplicationIdentity identity)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--instance-child");
            start.ArgumentList.Add(applicationId);
            start.ArgumentList.Add(identity.Mode);
            start.ArgumentList.Add(identity.DataRoot ?? "-");
            start.ArgumentList.Add(identity.Endpoint ?? "-");
            using var child = Process.Start(start) ?? throw new IOException("Could not start the isolated single-instance check.");
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(15_000))
            {
                child.Kill(true); child.WaitForExit();
                throw new TimeoutException("The isolated single-instance check exceeded 15 seconds.");
            }
            var text = output.GetAwaiter().GetResult();
            var errors = error.GetAwaiter().GetResult();
            if (child.ExitCode != 0) throw new InvalidOperationException("Single-instance child failed: " + errors + text);
            using var result = JsonDocument.Parse(text);
            return (result.RootElement.GetProperty("isPrimary").GetBoolean(),
                result.RootElement.GetProperty("activationRequested").GetBoolean(),
                result.RootElement.GetProperty("identityMatches").GetBoolean(),
                result.RootElement.GetProperty("ownerIsCurrent").GetBoolean(),
                result.RootElement.GetProperty("ownerProcessId").GetInt32(),
                result.RootElement.GetProperty("ownerMode").GetString(),
                result.RootElement.GetProperty("ownerDataRoot").GetString(),
                result.RootElement.GetProperty("ownerIdentity").GetString()!,
                result.RootElement.GetProperty("requestedMode").GetString()!,
                result.RootElement.GetProperty("requestedDataRoot").GetString(),
                result.RootElement.GetProperty("requestedIdentity").GetString()!);
        }
        var matching = Launch(DesktopApplicationIdentity.SourceLocal(@"c:\fixture-one\"));
        Assert(!matching.IsPrimary && matching.ActivationRequested && matching.IdentityMatches &&
               matching.OwnerIsCurrent && matching.OwnerProcessId == Environment.ProcessId &&
               matching.OwnerMode == "source-local" &&
               string.Equals(matching.OwnerDataRoot, ownerIdentity.DataRoot, StringComparison.OrdinalIgnoreCase) &&
               matching.RequestedMode == "source-local" &&
               string.Equals(matching.RequestedDataRoot, ownerIdentity.DataRoot, StringComparison.OrdinalIgnoreCase),
            "A matching canonical mode/data-root launch must validate the live owner generation and request activation.");
        WaitForDispatcher(() => activations == 1,
            "The owning desktop did not receive the second process's activation signal.");
        Assert(activations == 1,
            "A second desktop launch must reactivate the existing process exactly once.");
        var mismatched = Launch(DesktopApplicationIdentity.InstalledService(@"C:\fixture-two"));
        Assert(!mismatched.IsPrimary && !mismatched.ActivationRequested && !mismatched.IdentityMatches &&
               mismatched.OwnerIsCurrent && mismatched.OwnerMode == "source-local" &&
               mismatched.RequestedMode == "installed-service" &&
               mismatched.OwnerIdentity.Contains(@"C:\fixture-one", StringComparison.OrdinalIgnoreCase) &&
               mismatched.RequestedIdentity.Contains(@"C:\fixture-two", StringComparison.OrdinalIgnoreCase),
            "A different mode/data-root launch must refuse activation and retain both bounded diagnostic identities.");
        WaitForDispatcher(() => true, "The dispatcher did not remain available after the rejected activation.");
        Assert(activations == 1, "A mismatched data-root launch must not activate the existing desktop.");

        static bool Rejected(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
        }
        Assert(Rejected(() => _ = new DesktopApplicationIdentity("source-local", "relative", null)) &&
               Rejected(() => _ = new DesktopApplicationIdentity("unknown", @"C:\fixture", null)),
            "Desktop instance identities must reject relative roots and unknown launch modes before publication.");
    }

    private static void DesktopDependencyFixtures()
    {
        var root = Path.Combine(Path.GetTempPath(), "PegasusCerebral.DesktopDependencyTests-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "installed"); var sources = Path.Combine(root, "sources");
        Directory.CreateDirectory(sources);
        var emptyCatalog = new DesktopPluginCatalog(Path.Combine(root, "absent-plugins"));
        var emptyBinding = new DesktopConversationBinding("zero-plugin", "fixture");
        var emptyDraft = new DesktopDraftSnapshot(emptyBinding, 0, "");
        var emptyHost = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
            () => new(emptyBinding, "Zero-plugin baseline", null, null, false), () => emptyDraft,
            (update, _) => { emptyDraft = new(emptyBinding, emptyDraft.Revision + 1, update.Text); return Done(); },
            (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), _ => { });
        using (var emptyRuntime = new DesktopPluginRuntime(emptyCatalog, emptyHost, Dispatcher.CurrentDispatcher))
        {
            emptyRuntime.LoadEnabled();
            Assert(emptyCatalog.List().Count == 0 && emptyRuntime.Status.Count == 0 && emptyRuntime.Panels.Count == 0 &&
                   emptyRuntime.ComposerActions.Count == 0 && emptyRuntime.TextEntryActions.Count == 0 &&
                   emptyRuntime.InputModes.Count == 0 && emptyRuntime.AgentTemplates.Count == 0,
                "An absent optional plugin repository leaves the native desktop host usable with an empty contribution set.");
        }
        string Package(string id, string version, object[] dependencies)
        {
            var directory = Path.Combine(sources, id); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "plugin.dll"), "Admission fixture only; never loaded.");
            var manifest = new
            {
                schemaVersion = 2, contract = DesktopPluginContract.Id, apiVersion = DesktopPluginContract.Version,
                id, name = id, version, description = "Dependency fixture", assembly = "plugin.dll",
                entryType = "Fixture.Plugin", dependencies
            };
            var path = Path.Combine(directory, DesktopPluginCatalog.ManifestFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, CerebralJson.Options)); return path;
        }
        static object Required(string id, string minimum, string maximum) =>
            new { id, minimumVersion = minimum, maximumVersionExclusive = maximum, optional = false };
        try
        {
            var catalog = new DesktopPluginCatalog(installed);
            var provider = Package("fixture.provider", "1.2.0", []);
            var consumer = Package("fixture.consumer", "1.0.0", [Required("fixture.provider", "1.0.0", "2.0.0")]);
            catalog.Install(provider); catalog.Install(consumer);
            var missingRejected = false;
            try { catalog.SetEnabled("fixture.consumer", true); } catch (InvalidOperationException) { missingRejected = true; }
            Assert(missingRejected, "A desktop consumer cannot enable while its required package is disabled.");
            catalog.SetEnabled("fixture.provider", true); catalog.SetEnabled("fixture.consumer", true);
            Assert(catalog.List().Count(item => item.Enabled) == 2 && catalog.Inspect(consumer).Dependencies?.Count == 1,
                "Version-compatible desktop dependencies are admitted and retained as typed manifest data.");
            var providerDisableRejected = false;
            try { catalog.SetEnabled("fixture.provider", false); } catch (InvalidOperationException) { providerDisableRejected = true; }
            Assert(providerDisableRejected, "A required provider cannot be disabled while an enabled consumer depends on it.");
            catalog.SetEnabled("fixture.consumer", false); catalog.SetEnabled("fixture.provider", false);

            var cycleA = Package("fixture.cycle-a", "1.0.0", [new { id = "fixture.cycle-b", exactVersion = "1.0.0", optional = false }]);
            var cycleB = Package("fixture.cycle-b", "1.0.0", [new { id = "fixture.cycle-a", exactVersion = "1.0.0", optional = false }]);
            catalog.Install(cycleA); catalog.Install(cycleB);
            Directory.CreateDirectory(Path.Combine(installed, ".settings"));
            File.WriteAllText(Path.Combine(installed, ".settings", "fixture.cycle-b.json"), "{\"enabled\":true}");
            var cycleRejected = false;
            try { catalog.SetEnabled("fixture.cycle-a", true); } catch (InvalidOperationException) { cycleRejected = true; }
            Assert(cycleRejected, "Desktop dependency cycles are rejected before any plugin entrypoint can load.");

            var optional = Package("fixture.optional", "1.0.0", [new { id = "fixture.absent", exactVersion = "1.0.0", optional = true }]);
            catalog.Install(optional); catalog.SetEnabled("fixture.optional", true);
            Assert(catalog.List().Single(item => item.Id == "fixture.optional").Enabled,
                "An unavailable optional dependency does not prevent explicit plugin enablement.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void DesktopExclusiveProviderFixtures()
    {
        var root = Path.Combine(Path.GetTempPath(), "PegasusCerebral.DesktopExclusiveProviderTests-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "installed"); var sources = Path.Combine(root, "sources");
        Directory.CreateDirectory(sources);
        string Package(string id, bool defaultEnabled, int priority)
        {
            var directory = Path.Combine(sources, id); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "plugin.dll"), "Admission fixture only; never loaded.");
            var manifest = new
            {
                schemaVersion = 3, contract = DesktopPluginContract.Id, apiVersion = DesktopPluginContract.Version,
                id, name = id, version = "1.0.0", description = "Exclusive provider fixture", assembly = "plugin.dll",
                entryType = "Fixture.Plugin", dependencies = Array.Empty<object>(), defaultEnabled,
                exclusiveGroup = "fixture.input", exclusivePriority = priority
            };
            var path = Path.Combine(directory, DesktopPluginCatalog.ManifestFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, CerebralJson.Options)); return path;
        }
        try
        {
            var catalog = new DesktopPluginCatalog(installed);
            var preferred = Package("fixture.preferred", true, 100);
            var fallback = Package("fixture.fallback", false, 10);
            Assert(catalog.SeedBundled(preferred) is { Enabled: true } && catalog.SeedBundled(fallback) is { Enabled: false },
                "Bundled alternative providers honor their manifest default instead of enabling every installed provider.");
            catalog.SetEnabled("fixture.fallback", true);
            Assert(catalog.List().Single(item => item.Id == "fixture.fallback").Enabled &&
                   !catalog.List().Single(item => item.Id == "fixture.preferred").Enabled,
                "Enabling one provider disables the previously active provider in the same exclusive group.");
            catalog.SetEnabled("fixture.preferred", true);
            Assert(catalog.List().Single(item => item.Id == "fixture.preferred").Enabled &&
                   !catalog.List().Single(item => item.Id == "fixture.fallback").Enabled,
                "A user can switch back to the preferred provider without leaving two active input handlers.");

            var settings = Path.Combine(installed, ".settings");
            File.WriteAllText(Path.Combine(settings, "fixture.preferred.json"), "{\"enabled\":true}");
            File.WriteAllText(Path.Combine(settings, "fixture.fallback.json"), "{\"enabled\":true}");
            var reconciled = catalog.ReconcileExclusiveGroups();
            Assert(reconciled.SequenceEqual(["fixture.fallback"]) &&
                   catalog.List().Single(item => item.Id == "fixture.preferred").Enabled &&
                   !catalog.List().Single(item => item.Id == "fixture.fallback").Enabled,
                "Startup reconciliation keeps the highest-priority provider when older settings enabled both alternatives.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void DesktopDependencyRuntimeFixture(string sourceManifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "PegasusCerebral.DesktopDependencyRuntimeTests-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "installed"); var sources = Path.Combine(root, "sources");
        Directory.CreateDirectory(sources);
        string Package(string id, IReadOnlyList<DesktopPluginDependency> dependencies)
        {
            var directory = Path.Combine(sources, id); Directory.CreateDirectory(directory);
            CopyPackage(Path.GetDirectoryName(Path.GetFullPath(sourceManifest))!, directory);
            var path = Path.Combine(directory, DesktopPluginCatalog.ManifestFileName);
            var manifest = JsonSerializer.Deserialize<DesktopPluginManifest>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            manifest.SchemaVersion = 2; manifest.Id = id; manifest.Name = id; manifest.Dependencies = dependencies.ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, CerebralJson.Options)); return path;
        }
        try
        {
            var catalog = new DesktopPluginCatalog(installed);
            catalog.Install(Package("fixture.runtime-provider", []));
            catalog.Install(Package("fixture.runtime-consumer", [new DesktopPluginDependency
            {
                Id = "fixture.runtime-provider", MinimumVersion = "1.0.0", MaximumVersionExclusive = "2.0.0"
            }]));
            catalog.SetEnabled("fixture.runtime-provider", true); catalog.SetEnabled("fixture.runtime-consumer", true);
            RunDependencyRuntimeChild(installed, 2);
            Assert(true, "Desktop runtime initializes a required provider before its enabled consumer through real plugin DLLs.");
            catalog.SetEnabled("fixture.runtime-consumer", false); catalog.SetEnabled("fixture.runtime-provider", false);
            RunDependencyRuntimeChild(installed, 0);
            Assert(true, "Dependency-aware startup omits consumers and providers after saved disablement.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void RunDependencyRuntimeChild(string installed, int expected)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--dependency-runtime-child"); start.ArgumentList.Add(installed);
        start.ArgumentList.Add(expected.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var child = Process.Start(start) ?? throw new IOException("Could not start the dependency runtime child.");
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(30_000)) { child.Kill(true); child.WaitForExit(); throw new TimeoutException("The dependency runtime child exceeded 30 seconds."); }
        var text = output.GetAwaiter().GetResult(); var errors = error.GetAwaiter().GetResult();
        if (child.ExitCode != 0) throw new InvalidOperationException("Dependency runtime child failed: " + errors + text);
        using var result = JsonDocument.Parse(text);
        if (!result.RootElement.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Dependency runtime child returned no success evidence.");
    }

    private static void DependencyRuntimeChild(string installed, int expected)
    {
        var catalog = new DesktopPluginCatalog(installed);
        var binding = new DesktopConversationBinding("dependency-runtime", "fixture");
        var draft = new DesktopDraftSnapshot(binding, 0, "");
        var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
            () => new(binding, "Dependency fixture", null, null, false), () => draft,
            (update, _) => { draft = new(binding, draft.Revision + 1, update.Text); return Done(); },
            (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), _ => { });
        using var runtime = new DesktopPluginRuntime(catalog, host, Dispatcher.CurrentDispatcher);
        runtime.LoadEnabled();
        Assert(runtime.Status.Count == expected && runtime.Status.All(item => item.Loaded) && runtime.Panels.Count == expected,
            "Dependency runtime loaded the exact expected package set.");
    }

    private static void PluginReopenFixture(string sourceManifest)
    {
        sourceManifest = Path.GetFullPath(sourceManifest);
        if (!File.Exists(sourceManifest) || !Path.GetFileName(sourceManifest).Equals(DesktopPluginCatalog.ManifestFileName,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pass an existing desktop-plugin.json fixture.", nameof(sourceManifest));
        var root = Path.Combine(Path.GetTempPath(), "PegasusCerebral.PluginReopenTests-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "installed"); var sources = Path.Combine(root, "sources");
        Directory.CreateDirectory(sources);
        string Package(string id, IReadOnlyList<DesktopPluginDependency> dependencies)
        {
            var directory = Path.Combine(sources, id); Directory.CreateDirectory(directory);
            CopyPackage(Path.GetDirectoryName(sourceManifest)!, directory);
            var path = Path.Combine(directory, DesktopPluginCatalog.ManifestFileName);
            var manifest = JsonSerializer.Deserialize<DesktopPluginManifest>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            manifest.SchemaVersion = 2; manifest.Id = id; manifest.Name = id;
            manifest.Dependencies = dependencies.ToList(); manifest.DefaultEnabled = null;
            manifest.ExclusiveGroup = null; manifest.ExclusivePriority = null;
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, CerebralJson.Options));
            return path;
        }
        try
        {
            var catalog = new DesktopPluginCatalog(installed);
            catalog.Install(Package("fixture.reopen-target", []));
            catalog.Install(Package("fixture.reopen-unrelated", []));
            catalog.Install(Package("fixture.reopen-consumer", [new DesktopPluginDependency
            {
                Id = "fixture.reopen-target", MinimumVersion = "1.0.0", MaximumVersionExclusive = "2.0.0"
            }]));
            foreach (var id in new[] { "fixture.reopen-target", "fixture.reopen-unrelated", "fixture.reopen-consumer" })
                catalog.SetEnabled(id, true);
            RunPluginReopenChild(installed);
        }
        finally
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static void RunPluginReopenChild(string installed)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--plugin-reopen-child"); start.ArgumentList.Add(installed);
        using var child = Process.Start(start) ?? throw new IOException("Could not start the plugin reopen runtime child.");
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(30_000)) { child.Kill(true); child.WaitForExit(); throw new TimeoutException("The plugin reopen child exceeded 30 seconds."); }
        var text = output.GetAwaiter().GetResult(); var errors = error.GetAwaiter().GetResult();
        if (child.ExitCode != 0) throw new InvalidOperationException("Plugin reopen child failed: " + errors + text);
        using var result = JsonDocument.Parse(text);
        if (!result.RootElement.GetProperty("success").GetBoolean())
            throw new InvalidOperationException("Plugin reopen child returned no success evidence.");
        checks += result.RootElement.GetProperty("pluginReopenChecks").GetInt32();
    }

    private static void PluginReopenRuntimeChild(string installed)
    {
        static bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException) { return true; }
        }
        var catalog = new DesktopPluginCatalog(Path.GetFullPath(installed));
        var binding = new DesktopConversationBinding("plugin-reopen", "fixture");
        var draft = new DesktopDraftSnapshot(binding, 0, "");
        var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
            () => new(binding, "Plugin reopen fixture", null, null, false), () => draft,
            (update, _) => { draft = new(binding, draft.Revision + 1, update.Text); return Done(); },
            (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), _ => { });
        using var runtime = new DesktopPluginRuntime(catalog, host, Dispatcher.CurrentDispatcher);
        runtime.LoadEnabled();
        var changed = 0; runtime.Changed += (_, _) => changed++;
        DesktopHostedPanel Panel(string id) => runtime.Panels.Single(item => item.PluginId == id);
        DesktopPluginInfo PackageInfo(string id) => catalog.List().Single(item => item.Id == id);

        var targetBefore = Panel("fixture.reopen-target").View;
        var unrelatedBefore = Panel("fixture.reopen-unrelated").View;
        var consumerBefore = Panel("fixture.reopen-consumer").View;
        var targetPackage = PackageInfo("fixture.reopen-target");
        Assert(Rejects(() => runtime.ValidateCanReopenExact(targetPackage.Id, targetPackage.Directory, targetPackage.Version)) &&
               ReferenceEquals(Panel(targetPackage.Id).View, targetBefore) &&
               ReferenceEquals(Panel("fixture.reopen-unrelated").View, unrelatedBefore) &&
               ReferenceEquals(Panel("fixture.reopen-consumer").View, consumerBefore) && changed == 0,
            "Exact reopen rejects a loaded dependent before disposing any plugin instance.");

        var consumerPackage = PackageInfo("fixture.reopen-consumer");
        runtime.ValidateCanReopenExact(consumerPackage.Id, consumerPackage.Directory, consumerPackage.Version);
        var consumerResult = runtime.ReopenExact(consumerPackage.Id, consumerPackage.Directory, consumerPackage.Version);
        Assert(consumerResult.InstanceRecreated && consumerResult.Error is null &&
               !ReferenceEquals(Panel(consumerPackage.Id).View, consumerBefore) &&
               ReferenceEquals(Panel(targetPackage.Id).View, targetBefore) &&
               ReferenceEquals(Panel("fixture.reopen-unrelated").View, unrelatedBefore) && changed == 1,
            "Exact reopen creates a new plugin and mounted view while retaining its provider and unrelated plugin.");

        catalog.SetEnabled(consumerPackage.Id, false); runtime.LoadEnabled();
        var afterDisable = changed;
        targetBefore = Panel(targetPackage.Id).View;
        unrelatedBefore = Panel("fixture.reopen-unrelated").View;
        runtime.ValidateCanReopenExact(targetPackage.Id, targetPackage.Directory, targetPackage.Version);
        var targetResult = runtime.ReopenExact(targetPackage.Id, targetPackage.Directory, targetPackage.Version);
        Assert(targetResult.InstanceRecreated && targetResult.Error is null &&
               !ReferenceEquals(Panel(targetPackage.Id).View, targetBefore) &&
               ReferenceEquals(Panel("fixture.reopen-unrelated").View, unrelatedBefore) && changed == afterDisable + 1,
            "A dependency-free exact reopen disposes and reloads only the reviewed package once.");

        var targetAfter = Panel(targetPackage.Id).View;
        var unrelatedAfter = Panel("fixture.reopen-unrelated").View;
        Assert(Rejects(() => runtime.ValidateCanReopenExact(targetPackage.Id, Path.Combine(installed, "wrong"), targetPackage.Version)) &&
               Rejects(() => runtime.ValidateCanReopenExact(targetPackage.Id, targetPackage.Directory, "0.0.0")) &&
               ReferenceEquals(Panel(targetPackage.Id).View, targetAfter) &&
               ReferenceEquals(Panel("fixture.reopen-unrelated").View, unrelatedAfter) && changed == afterDisable + 1,
            "Mismatched package paths and versions fail before touching the reviewed or unrelated instances.");
    }

    private static void DesktopPluginStress(string sourceManifest, int iterations)
    {
        if (iterations is < 10 or > 2_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        sourceManifest = Path.GetFullPath(sourceManifest);
        var root = Path.Combine(Path.GetTempPath(), "PegasusCerebral.DesktopPluginStress-" + Guid.NewGuid().ToString("N"));
        var sources = Path.Combine(root, "sources");
        var installed = Path.Combine(root, "installed");
        Directory.CreateDirectory(sources);
        string Package(string id, string entryType)
        {
            var source = Path.Combine(sources, id);
            CopyPackage(Path.GetDirectoryName(sourceManifest)!, source);
            var path = Path.Combine(source, DesktopPluginCatalog.ManifestFileName);
            var manifest = JsonSerializer.Deserialize<DesktopPluginManifest>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            manifest.SchemaVersion = 2; manifest.Id = id; manifest.Name = id; manifest.EntryType = entryType;
            manifest.Dependencies = []; manifest.DefaultEnabled = null; manifest.ExclusiveGroup = null; manifest.ExclusivePriority = null;
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, CerebralJson.Options));
            return path;
        }
        try
        {
            var original = JsonSerializer.Deserialize<DesktopPluginManifest>(File.ReadAllText(sourceManifest),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var catalog = new DesktopPluginCatalog(installed);
            catalog.Install(Package("stress.valid", original.EntryType));
            catalog.Install(Package("stress.failing", "Missing.StressPlugin"));
            catalog.SetEnabled("stress.valid", true); catalog.SetEnabled("stress.failing", true);
            var binding = new DesktopConversationBinding("stress-generation", "stress-conversation");
            var draft = new DesktopDraftSnapshot(binding, 0, "Stress fixture");
            var reports = new List<string>();
            var healthyCallbacks = 0;
            var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
                () => new(binding, "Stress fixture", null, null, false), () => draft,
                (update, _) => { draft = new(binding, draft.Revision + 1, update.Text); return Done(); },
                (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), reports.Add, pluginDataRoot: Path.Combine(root, "data"));
            host.ConversationChanged += (_, _) => throw new InvalidOperationException("Deliberate plugin callback failure.");
            host.ConversationChanged += (_, _) => healthyCallbacks++;
            using var runtime = new DesktopPluginRuntime(catalog, host, Dispatcher.CurrentDispatcher);
            var progress = 0;
            runtime.LoadProgress += (_, _) => progress++;
            runtime.LoadEnabled();
            Assert(runtime.Status.Single(item => item.Id == "stress.valid").Loaded &&
                   !runtime.Status.Single(item => item.Id == "stress.failing").Loaded,
                "A failing desktop package is quarantined while an independent package remains loaded.");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var before = GC.GetTotalMemory(true);
            var stopwatch = Stopwatch.StartNew();
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                runtime.LoadEnabled();
                host.PublishConversationChanged(force: true);
                DrainDispatcher();
                if (!runtime.Status.Single(item => item.Id == "stress.valid").Loaded ||
                    runtime.Status.Single(item => item.Id == "stress.failing").Loaded)
                    throw new InvalidOperationException("Plugin isolation changed during the stress loop.");
            }
            stopwatch.Stop();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var growth = Math.Max(0, GC.GetTotalMemory(true) - before);
            Assert(healthyCallbacks == iterations && reports.Count(message => message.Contains("callback failure", StringComparison.OrdinalIgnoreCase)) == iterations,
                "A failing plugin callback cannot prevent later plugin listeners from receiving the same bounded event.");
            Assert(growth < 128L * 1024 * 1024,
                "Repeated failed package admission exceeded the bounded 128 MiB managed-memory growth limit.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, iterations, elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                averageMilliseconds = stopwatch.Elapsed.TotalMilliseconds / iterations, managedGrowthBytes = growth, progressEvents = progress,
                healthyCallbacks, failureReports = reports.Count }, CerebralJson.Options));
        }
        finally
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static void TokenBalanceFixtures()
    {
        var balance = new TokenBalanceControl();
        var changes = 0;
        balance.ResponseTokensChanged += (_, _) => changes++;
        balance.Configure(16_384, 4_096, 1_024, true, "Custom agent default");
        Assert(balance.TotalTokens == 16_384 && balance.ResponseTokens == 4_096 && balance.PromptTokens == 11_136,
            "The balance must divide one fixed context window into prompt/history, response, reasoning, and safety reserves.");
        balance.ResponseTokens = 2_048;
        Assert(changes == 1 && balance.PromptTokens == 13_184,
            "Changing the response reservation must return the same tokens to prompt/history without changing total capacity.");
        balance.Configure(8_192, 2_048, 0, true, "Conversation preference");
        balance.ResponseTokens = 2_050;
        var selectedShare = Math.Clamp(
            (int)Math.Round(100d * balance.ResponseTokens / balance.TotalTokens, MidpointRounding.AwayFromZero),
            AgentTokenLimits.MinimumResponseSharePercent, AgentTokenLimits.MaximumResponseSharePercent);
        var profile = new AgentProfile { ContextTokens = balance.TotalTokens, ReasoningMode = "off", ReasoningTokens = AgentTokenLimits.MinimumReasoningTokens };
        balance.Configure(profile.ContextTokens, AgentTokenLimits.ResolveResponseShare(profile, selectedShare), 0, true, "Conversation preference");
        Assert(changes == 2 && selectedShare == 25 && balance.ResponseTokens == 2_048,
            "Projecting a raw slider move through the persisted whole percentage must snap to the exact service response budget without a recursive change event.");
        balance.Configure(8_192, 10_000, 0, false, "Automatic model-derived default");
        Assert(changes == 2 && balance.TotalTokens == 8_192 && balance.ResponseTokens == 7_935 && balance.PromptTokens == 129 && !balance.IsBalanceEditable,
            "Programmatic automatic-mode configuration must clamp safely, remain read-only, and avoid reporting a user change.");
    }

    private static void TextEntryIndicatorFixtures()
    {
        var indicator = new TextEntryActionIndicator("fixture");
        var button = new Button { Content = "🎙 Dictate" };
        indicator.Register("pegasus.dictation", "dictate", button);
        indicator.Report(new("pegasus.dictation", "dictate", DesktopTextEntryActivityState.Starting,
            "Starting dictation…", false, "Starting dictation…"));
        Assert(!button.IsEnabled && Equals(button.Content, "Starting dictation…") && indicator.Status.Visibility == Visibility.Visible &&
               indicator.HasPendingAction && indicator.PendingMessage == "Starting dictation…",
            "Starting feedback must be visible beside the target, prevent a duplicate start, and hold submission even before Active is reported.");
        indicator.Report(new("pegasus.dictation", "dictate", DesktopTextEntryActivityState.Listening,
            "Listening for this field…", true, "⏹ Stop dictation") { InputLevel = .55 });
        Assert(button.IsEnabled && Equals(button.Content, "⏹ Stop dictation") && indicator.Status.Text.Contains("this field", StringComparison.Ordinal) &&
               indicator.HasPendingAction &&
               FindAutomation<StackPanel>(indicator.View, "TextEntryInputLevel-fixture").Visibility == Visibility.Visible,
            "Listening feedback must turn the exact field action into a stop control and show its live input level.");
        indicator.Report(new("pegasus.dictation", "dictate", DesktopTextEntryActivityState.Result,
            "Inserted recognized text. Listening…", true, "⏹ Stop dictation"));
        Assert(Equals(button.Content, "⏹ Stop dictation") && indicator.Status.Text.StartsWith("Inserted", StringComparison.Ordinal),
            "A recognition result must remain visible while dictation continues.");
        indicator.Report(new("another.plugin", "dictate", DesktopTextEntryActivityState.Error, "Wrong target", false));
        Assert(indicator.Status.Text.StartsWith("Inserted", StringComparison.Ordinal),
            "One plugin must not overwrite another action's text-entry status.");
        indicator.Report(new("pegasus.dictation", "dictate", DesktopTextEntryActivityState.Error,
            "Microphone unavailable.", false));
        Assert(button.IsEnabled && Equals(button.Content, "🎙 Dictate") && indicator.Status.Text == "Microphone unavailable." &&
               !indicator.HasPendingAction,
            "A start failure must restore the action, release the submission barrier, and leave its error visible at the target.");
        indicator.Reset();
        Assert(indicator.Status.Visibility == Visibility.Collapsed && Equals(button.Content, "🎙 Dictate") && !indicator.HasPendingAction,
            "Removing a transient target must clear its action status and submission barrier.");
    }

    private static T FindAutomation<T>(DependencyObject root, string id) where T : FrameworkElement
    {
        if (root is T match && AutomationProperties.GetAutomationId(match) == id) return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            try { return FindAutomation<T>(child, id); } catch (KeyNotFoundException) { }
        throw new KeyNotFoundException(id);
    }

    private static void WritingAssistanceFixtures()
    {
        var editor = new TextBox();
        TextContextMenus.EnableWritingAssistance(editor);
        Assert(SpellCheck.GetIsEnabled(editor) && editor.ContextMenu is not null &&
               AutomationProperties.GetHelpText(editor).Contains("underlined in red", StringComparison.OrdinalIgnoreCase),
            "Prompt writing assistance must use the local Windows spell checker and expose right-click corrections.");
    }

    private static void AdapterFixtures()
    {
        var binding = new DesktopConversationBinding("one", null);
        var draft = new DesktopDraftSnapshot(binding, 4, "User draft");
        var submissions = new List<string>(); var replacements = 0; var stops = 0; string? clipboardText = null;
        var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
            () => new(binding, "New conversation", null, null, false), () => draft,
            (update, _) => { replacements++; draft = new(binding, draft.Revision + 1, update.Text); return Done(); },
            (input, _) => { submissions.Add(input.RequestId); return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Uncertain, "Fixture lost receipt", input.RequestId)); },
            (input, _) => { submissions.Add(input.RequestId); return Done(); },
            (_, _) => { stops++; return Done(); }, _ => { },
            setClipboardText: (text, _) => { clipboardText = text; return Done(); });
        using var configurationScope = host.CreateScope("fixture.configuration", "Configuration fixture", Environment.CurrentDirectory);
        Assert(configurationScope is not IDesktopConversationSettingsHost,
            "A scoped host without real response-settings callbacks must not advertise the optional settings contract.");
        int? selectedResponseShare = null;
        var settingsHost = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
            () => new(binding, "New conversation", null, null, false), () => draft,
            (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), _ => { },
            responseBalance: () => new(binding, 40, 32_768, 4_096),
            setResponseBalance: (expected, share, _) =>
            {
                Assert(expected == binding, "The scoped settings host must preserve the expected conversation binding.");
                selectedResponseShare = share;
                return Done();
            });
        using var settingsScope = settingsHost.CreateScope("fixture.settings", "Settings fixture", Environment.CurrentDirectory);
        Assert(settingsScope is IDesktopConversationSettingsHost,
            "A scoped host with both response-settings callbacks must advertise the optional settings contract.");
        var settings = (IDesktopConversationSettingsHost)settingsScope;
        Assert(settings.GetResponseBalance() == new DesktopResponseBalanceSnapshot(binding, 40, 32_768, 4_096) &&
               settings.SetResponseBalanceAsync(binding, 55).Result.State == DesktopOperationState.Completed && selectedResponseShare == 55,
            "The advertised settings contract must forward its real getter and setter callbacks.");
        var mountCapabilities = ((IDesktopMountHost)host).GetMountCapabilities();
        Assert(mountCapabilities.Single(x => x.Slot == DesktopMountSlots.WorkspaceTree).Supported &&
               mountCapabilities.Single(x => x.Slot == DesktopMountSlots.NewConversationStart).Supported &&
               !mountCapabilities.Single(x => x.Slot == DesktopMountSlots.ActivityRenderers).Supported,
            "A plugin can discover the exact mounted and planned slot boundary before contributing UI.");
        var focusHost = (IDesktopTextEntryFocusHost)host;
        var focusEntry = new AdapterTextEntry(); var focusEvents = 0;
        focusHost.FocusedTextEntryChanged += (_, args) =>
        {
            focusEvents++;
            Assert(ReferenceEquals(args.Entry, focusHost.FocusedTextEntry), "Focused-entry events must expose the host's current target.");
        };
        host.PublishFocusedTextEntry(focusEntry); host.PublishFocusedTextEntry(focusEntry); host.PublishFocusedTextEntry(null);
        Assert(focusEvents == 2 && focusHost.FocusedTextEntry is null,
            "The optional focus host publishes real target changes once and explicitly reports focus leaving editable fields.");
        IDesktopTextEntry? currentDefault = null;
        host.ConfigureDefaultTextEntry(() => currentDefault);
        using var defaultScope = host.CreateScope("fixture.default-entry", "Default entry fixture", Environment.CurrentDirectory);
        var defaultHost = (IDesktopDefaultTextEntryHost)defaultScope;
        Assert(defaultHost.DefaultTextEntry is null, "A Main host without a current composer must return no default target.");
        currentDefault = focusEntry;
        Assert(ReferenceEquals(defaultHost.DefaultTextEntry, focusEntry),
            "The optional default-entry host must forward only the current host-owned target.");
        currentDefault = null;
        Assert(defaultHost.DefaultTextEntry is null, "A retired composer must disappear from the scoped default-entry view.");
        defaultScope.Dispose();
        var disposedDefaultRejected = false;
        try { _ = defaultHost.DefaultTextEntry; }
        catch (ObjectDisposedException) { disposedDefaultRejected = true; }
        Assert(disposedDefaultRejected, "A disposed plugin scope must not read the default-entry host.");
        var configurationDefaultHost = (IDesktopDefaultTextEntryHost)new DesktopPluginHostAdapter(
            Dispatcher.CurrentDispatcher, _ => { });
        Assert(configurationDefaultHost.DefaultTextEntry is null,
            "Configuration must not expose a Main composer default target.");

        var other = new DesktopConversationBinding("two", null);
        Assert(host.SubmitAsync(new(other, "request-one", "Hello")).Result.State == DesktopOperationState.Rejected && submissions.Count == 0, "A different unsaved-conversation generation must reject before submitting.");
        Assert(host.ReplaceDraftAsync(new(binding, 3, "Wrong")).Result.State == DesktopOperationState.Rejected && replacements == 0, "A stale draft must not overwrite user input.");
        Assert(host.ReplaceDraftAsync(new(other, 4, "Wrong")).Result.State == DesktopOperationState.Rejected && replacements == 0, "Draft edits must also match conversation generation.");
        Assert(host.ReplaceDraftAsync(new(binding, 4, "Reviewed draft")).Result.State == DesktopOperationState.Completed && draft.Text == "Reviewed draft", "Current draft edits reach the ordinary host callback.");
        var request = new DesktopSubmission(binding, "retained-request-id", "Do the work");
        Assert(host.SubmitAsync(request).Result.State == DesktopOperationState.Uncertain, "Uncertain host outcomes remain uncertain.");
        _ = host.SubmitAsync(request).Result;
        Assert(submissions.SequenceEqual(["retained-request-id", "retained-request-id"]), "The adapter must preserve request identity through retries.");
        Assert(host.SubmitAsync(request with { RequestId = "" }).Result.State == DesktopOperationState.Rejected, "Empty request identity must be rejected.");
        Assert(host.SubmitAsync(request with { Text = new string('x', DesktopPluginContract.MaxTextChars + 1) }).Result.State == DesktopOperationState.Rejected, "Submissions must obey the advertised text limit.");
        Assert(host.StopAsync(other).Result.State == DesktopOperationState.Rejected && stops == 0, "A stop must remain bound to the intended conversation.");
        var clipboardHost = (IDesktopClipboardHost)host;
        Assert(clipboardHost.SetClipboardTextAsync("fixture clipboard").Result.State == DesktopOperationState.Completed && clipboardText == "fixture clipboard" &&
               clipboardHost.SetClipboardTextAsync(new string('x', DesktopPluginContract.MaxTextChars + 1)).Result.State == DesktopOperationState.Rejected,
            "Plugin clipboard requests must use the bounded host callback instead of writing process-global clipboard state directly.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { host.StopAsync(binding, cancelled.Token).GetAwaiter().GetResult(); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { Assert(stops == 0, "Cancelled host actions must not execute."); }
        var visibleMessages = 0; var refreshes = 0;
        host.LeadMessageCommitted += (_, args) => { visibleMessages++; Assert(args.Message.Truncated, "Oversized committed event must identify its excerpt."); };
        host.PresentationRefreshRequested += (_, _) => refreshes++;
        host.PublishLeadMessageCommitted(new("u1", binding, "user", "Prompt", DateTimeOffset.UtcNow));
        host.PublishLeadMessageCommitted(new("a1", other, "assistant", "Other conversation", DateTimeOffset.UtcNow));
        Assert(visibleMessages == 0, "Only current visible lead replies may trigger the committed-reply event.");
        host.PublishLeadMessageCommitted(new("a2", binding, "assistant", new string('x', DesktopPluginContract.MaxTextChars + 1), DateTimeOffset.UtcNow));
        Assert(visibleMessages == 0, "Committed-reply callbacks must wait until the current render has finished.");
        DrainDispatcher();
        Assert(visibleMessages == 1, "A current lead reply should be emitted once when published once.");
        var currentBinding = binding;
        host.PublishLeadMessageCommitted(new("late", binding, "assistant", "Old conversation", DateTimeOffset.UtcNow));
        binding = new("switched-before-notification", null); DrainDispatcher();
        Assert(visibleMessages == 1, "A queued old reply must be discarded after the conversation changes.");
        binding = currentBinding;
        var conversationEvents = 0;
        host.ConversationChanged += (_, _) => conversationEvents++;
        host.PublishConversationChanged(); host.PublishConversationChanged();
        Assert(conversationEvents == 0, "Conversation callbacks cannot run inside a half-completed selection.");
        DrainDispatcher(); Assert(conversationEvents == 1, "Repeated same-turn conversation notifications should coalesce.");
        host.PublishConversationChanged(); DrainDispatcher();
        Assert(conversationEvents == 1, "Unchanged conversation metadata should not repeatedly restart plugin work.");
        host.PublishConversationChanged(force: true); DrainDispatcher();
        Assert(conversationEvents == 2, "A restored service connection must be able to refresh plugins without changing the conversation.");
        host.RefreshPresentation(); Assert(refreshes == 1, "A presentation request must remain a UI event.");
        var moves = 0; string? renamed = null;
        Assert(!host.WorkspaceAvailable, "Workspace navigation must be explicitly configured by the application.");
        host.ConfigureWorkspace(new(
            (_, _) => Task.FromResult(new DesktopConversationList([
                new("first", new string('t', 600), null), new("second", "Second", "project-one"), new("third", "Third", null)])),
            (id, _) => { moves++; binding = new("selected-generation", id); return Done(); },
            (_, _) => { moves++; binding = new("new-generation", null); return Done(); },
            (_, title, _) => { renamed = title; return Done(); }));
        Assert(host.WorkspaceAvailable, "The configured optional workspace surface must be advertised.");
        var list = host.ListConversationsAsync(2).Result;
        Assert(list.Conversations.Count == 2 && list.Truncated && list.Conversations[0].Title.Length == 500, "Workspace metadata must stay bounded and identify omitted entries.");
        Assert(host.SelectConversationAsync(other, "second").Result.State == DesktopOperationState.Rejected && moves == 0, "Late plugin navigation must not replace a newer user selection.");
        Assert(host.NewConversationAsync(other).Result.State == DesktopOperationState.Rejected && moves == 0, "New-conversation actions must also match the expected generation.");
        Assert(host.RenameConversationAsync(binding, "Unsaved").Result.State == DesktopOperationState.Rejected && renamed is null, "An unsaved conversation must not issue a rename.");
        var previousBinding = binding;
        Assert(host.SelectConversationAsync(binding, "second").Result.State == DesktopOperationState.Completed && moves == 1, "Valid plugin navigation reaches the normal application callback.");
        Assert(host.RenameConversationAsync(previousBinding, "Wrong").Result.State == DesktopOperationState.Rejected && renamed is null, "Renaming must keep the original conversation binding.");
        Assert(host.RenameConversationAsync(binding, "  Reviewed title  ").Result.State == DesktopOperationState.Completed && renamed == "Reviewed title", "A current rename passes the normalized title to the normal host flow.");
        Assert(host.NewConversationAsync(binding, "project-one").Result.State == DesktopOperationState.Completed && moves == 2 && binding.SessionId is null, "New conversation uses the configured host callback.");
    }

    private sealed class AdapterTextEntry : IDesktopTextEntry
    {
        public string Id => "adapter-focus";
        public string Purpose => "adapter focus fixture";
        public string Text => "";
        public int SelectionStart => 0;
        public int SelectionLength => 0;
        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken = default) => Done();
    }

    private static int NativeExampleInChild(string sourceManifest)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "PegasusCerebral.DesktopPluginTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var catalog = new DesktopPluginCatalog(fixture);
            Directory.CreateDirectory(Path.Combine(fixture, ".settings"));
            File.WriteAllText(Path.Combine(fixture, ".settings", "pegasus.workspace-actions.json"), "{\"enabled\":true}");
            var package = catalog.Install(Path.GetFullPath(sourceManifest));
            Assert(!package.Enabled, "A newly imported native package must clear any old enable flag before becoming visible.");
            ManifestFixtures(catalog, package.ManifestPath);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--plugin-child"); start.ArgumentList.Add(fixture);
            using var child = Process.Start(start) ?? throw new IOException("Could not start the isolated native DLL check.");
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30_000))
            {
                child.Kill(true); child.WaitForExit();
                throw new TimeoutException("The isolated native DLL check exceeded 30 seconds.");
            }
            var text = output.GetAwaiter().GetResult(); var errors = error.GetAwaiter().GetResult();
            if (child.ExitCode != 0) throw new InvalidOperationException("Native DLL check failed: " + errors + text);
            using var result = JsonDocument.Parse(text);
            nativePanelLayout = result.RootElement.GetProperty("nativePanelLayout").Clone();
            return result.RootElement.GetProperty("nativeDllChecks").GetInt32();
        }
        finally
        {
            // This exact fresh fixture belongs to the test; the child has exited before package deletion.
            var resolved = Path.GetFullPath(fixture);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("PegasusCerebral.DesktopPluginTests-", StringComparison.Ordinal) && Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }

    private static int ReviewExampleInChild(string sourceManifest)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "PegasusCerebral.ReviewDesktopTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var catalog = new DesktopPluginCatalog(fixture);
            var package = catalog.Install(Path.GetFullPath(sourceManifest));
            Assert(package.Id == "example.review" && !package.Enabled,
                "The review desktop component installs disabled under the shared package identity.");
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--review-plugin-child"); start.ArgumentList.Add(fixture);
            using var child = Process.Start(start) ?? throw new IOException("Could not start the isolated review desktop check.");
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30_000))
            {
                child.Kill(true); child.WaitForExit();
                throw new TimeoutException("The isolated review desktop check exceeded 30 seconds.");
            }
            var text = output.GetAwaiter().GetResult(); var errors = error.GetAwaiter().GetResult();
            if (child.ExitCode != 0) throw new InvalidOperationException("Review desktop check failed: " + errors + text);
            using var result = JsonDocument.Parse(text);
            nativePanelLayout = result.RootElement.GetProperty("nativePanelLayout").Clone();
            return result.RootElement.GetProperty("reviewPluginChecks").GetInt32();
        }
        finally
        {
            if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        }
    }

    private static int DictationExampleInChild(string sourceManifest)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "PegasusCerebral.DictationPluginTests-" + Guid.NewGuid().ToString("N"));
        var updateSource = Path.Combine(Path.GetTempPath(), "PegasusCerebral.DictationPluginUpdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture); Directory.CreateDirectory(updateSource);
        try
        {
            var catalog = new DesktopPluginCatalog(fixture);
            var seeded = catalog.SeedBundled(Path.GetFullPath(sourceManifest));
            Assert(seeded is { Enabled: false, Id: "pegasus.dictation" }, "Windows Dictation must install as an available fallback while AI Dictation remains the default provider.");
            catalog.SetEnabled("pegasus.dictation", false);
            Assert(catalog.SeedBundled(Path.GetFullPath(sourceManifest)) is { Enabled: false }, "A later startup must preserve an explicit disable choice.");

            CopyPackage(Path.GetDirectoryName(Path.GetFullPath(sourceManifest))!, updateSource);
            var updateManifestPath = Path.Combine(updateSource, DesktopPluginCatalog.ManifestFileName);
            var updateManifest = JsonSerializer.Deserialize<DesktopPluginManifest>(File.ReadAllText(updateManifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var installedVersion = Version.Parse(updateManifest.Version);
            var newerVersion = $"{installedVersion.Major}.{installedVersion.Minor}.{Math.Max(0, installedVersion.Build) + 1}";
            updateManifest.Version = newerVersion; File.WriteAllText(updateManifestPath, JsonSerializer.Serialize(updateManifest, CerebralJson.Options));
            File.WriteAllText(Path.Combine(updateSource, "upgrade.marker"), newerVersion);
            var updated = catalog.SeedBundled(updateManifestPath);
            Assert(updated is { Enabled: false } && updated.Version == newerVersion,
                "A newer bundled package must replace the old package while preserving disablement.");
            Assert(File.ReadAllText(Path.Combine(updated!.Directory, "upgrade.marker")) == newerVersion,
                "A newer bundled package must publish the replacement package contents.");
            var stale = Path.Combine(fixture, ".retired-pegasus.dictation-fixture"); Directory.CreateDirectory(stale); File.WriteAllText(Path.Combine(stale, "old.dll"), "old");
            Assert(catalog.SeedBundled(Path.GetFullPath(sourceManifest)) is { } retained && retained.Version == newerVersion && !Directory.Exists(stale),
                "A later seed must clean a bounded retired package without downgrading the installed plugin.");

            catalog.Remove("pegasus.dictation");
            Assert(catalog.SeedBundled(Path.GetFullPath(sourceManifest)) is null && catalog.List().All(x => x.Id != "pegasus.dictation"),
                "Removing a bundled plugin must suppress later automatic reseeding.");
            var installed = catalog.Install(Path.GetFullPath(sourceManifest));
            Assert(!installed.Enabled, "An explicit reinstall must clear the bundled removal marker and remain disabled for review.");
            catalog.SetEnabled(installed.Id, true);
            var explicitUpdate = catalog.Update(updateManifestPath);
            Assert(explicitUpdate is { Enabled: true } && explicitUpdate.Version == newerVersion &&
                File.ReadAllText(Path.Combine(explicitUpdate.Directory, "upgrade.marker")) == newerVersion,
                "A reviewed newer owner package must atomically publish an immutable generation while preserving enablement.");
            var nonIncreasingUpdateRejected = false;
            try { _ = catalog.Update(updateManifestPath); }
            catch (InvalidOperationException) { nonIncreasingUpdateRejected = true; }
            Assert(nonIncreasingUpdateRejected, "A repeated or downgraded native package cannot masquerade as an update.");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--dictation-child"); start.ArgumentList.Add(fixture);
            using var child = Process.Start(start) ?? throw new IOException("Could not start the isolated dictation plugin check.");
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30_000))
            {
                child.Kill(true); child.WaitForExit();
                throw new TimeoutException("The isolated dictation plugin check exceeded 30 seconds.");
            }
            var text = output.GetAwaiter().GetResult(); var errors = error.GetAwaiter().GetResult();
            if (child.ExitCode != 0) throw new InvalidOperationException("Dictation plugin check failed: " + errors + text);
            using var result = JsonDocument.Parse(text);
            nativePanelLayout = result.RootElement.GetProperty("nativePanelLayout").Clone();
            speechEnvironment = result.RootElement.GetProperty("speechEnvironment").Clone();
            return result.RootElement.GetProperty("dictationChecks").GetInt32();
        }
        finally
        {
            foreach (var path in new[] { fixture, updateSource })
            {
                var resolved = Path.GetFullPath(path);
                if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(resolved).StartsWith("PegasusCerebral.DictationPlugin", StringComparison.Ordinal) && Directory.Exists(resolved))
                    Directory.Delete(resolved, true);
            }
        }
    }

    private static void DictationExample(string fixture)
    {
        var catalog = new DesktopPluginCatalog(fixture);
        var dataRoot = Path.Combine(fixture, "plugin-data");
        var configurationHost = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher, _ => { }, dataRoot);
        using (var configuration = new DesktopPluginRuntime(catalog, configurationHost, Dispatcher.CurrentDispatcher))
        {
            configuration.LoadEnabled();
            var configurationStatus = configuration.Status.Single();
            Assert(configurationStatus.Loaded, "The dictation DLL must load in Configuration through the public SDK. " + configurationStatus.Error);
            Assert(configuration.Panels.Count == 1 && configuration.ComposerActions.Count == 0 && configuration.TextEntryActions.Count == 0,
                "Configuration must expose only the dictation setup panel.");
            var panel = configuration.Panels.Single().View;
            var mount = new Border { Child = panel }; mount.Measure(new Size(800, 600)); mount.Arrange(new Rect(0, 0, 800, 600)); mount.UpdateLayout();
            nativePanelLayout = new { hostWidth = mount.ActualWidth, hostHeight = mount.ActualHeight, panelWidth = panel.ActualWidth, panelHeight = panel.ActualHeight,
                verticalScroll = panel is ScrollViewer { VerticalScrollBarVisibility: ScrollBarVisibility.Auto }, horizontalScrollDisabled = panel is ScrollViewer { HorizontalScrollBarVisibility: ScrollBarVisibility.Disabled } };
            Assert(mount.ActualWidth == 800 && mount.ActualHeight == 600 && panel.ActualWidth > 0 && panel.ActualHeight > 0,
                "The DPI-independent dictation setup panel must receive the available host bounds.");
            Assert(panel is ScrollViewer { VerticalScrollBarVisibility: ScrollBarVisibility.Auto, HorizontalScrollBarVisibility: ScrollBarVisibility.Disabled },
                "The setup panel must scroll vertically without introducing a horizontal scrollbar.");
            var trainingButton = (Button)FindAutomationElement(panel, "OpenWindowsSpeechTraining");
            var speechCenterButton = (Button)FindAutomationElement(panel, "OpenWindowsSpeechRecognitionCenter");
            var microphoneTestButton = (Button)FindAutomationElement(panel, "TestDictationMicrophone");
            var trainingStatus = (TextBlock)FindAutomationElement(panel, "WindowsSpeechTrainingStatus");
            var trainingGuidance = (TextBlock)FindAutomationElement(panel, "WindowsSpeechTrainingGuidance");
            var voiceControls = (CheckBox)FindAutomationElement(panel, "EnableDictationVoiceControls");
            var voiceGuidance = (TextBlock)FindAutomationElement(panel, "DictationVoiceControlGuidance");
            var microphoneLevel = (ProgressBar)FindAutomationElement(panel, "DictationMicrophoneLevel");
            var microphoneLevelText = (TextBlock)FindAutomationElement(panel, "DictationMicrophoneLevelText");
            var microphoneSignal = (TextBlock)FindAutomationElement(panel, "DictationMicrophoneSignal");
            Assert(trainingButton.IsEnabled && trainingButton.ActualWidth > 0 && speechCenterButton.IsEnabled && speechCenterButton.ActualWidth > 0 &&
                speechCenterButton.Content?.ToString()?.Contains("Speech Recognition Center", StringComparison.Ordinal) == true &&
                trainingButton.Content?.ToString()?.Contains("Train Windows speech profile", StringComparison.Ordinal) == true &&
                microphoneTestButton.Content?.ToString()?.Contains("Test microphone and profile", StringComparison.Ordinal) == true &&
                trainingStatus.ActualWidth > 0 && trainingStatus.Text.Contains("current speech profile", StringComparison.OrdinalIgnoreCase) &&
                trainingStatus.Text.Contains("no separate training save", StringComparison.OrdinalIgnoreCase) &&
                trainingGuidance.Text.Contains("completion flag", StringComparison.OrdinalIgnoreCase),
                "Speech training must expose a clear Windows-owned workflow, its save boundary, the next profile test, and the completion-verification limit.");
            Assert(microphoneLevel.Minimum == 0 && microphoneLevel.Maximum == 100 && microphoneLevel.Value == 0 &&
                microphoneLevel.ActualWidth > 0 && microphoneLevelText.Text == "Idle" && microphoneSignal.Text == "Signal: idle",
                "The dictation panel must expose an idle 0-100 live input meter and actionable signal state without opening a microphone during tests.");
            Assert(voiceControls.IsChecked == false && voiceControls.ActualWidth > 0 &&
                voiceGuidance.Text.Contains("Cerebral command send message", StringComparison.Ordinal) &&
                voiceGuidance.Text.Contains("Cerebral command stop work", StringComparison.Ordinal) &&
                voiceGuidance.Text.Contains("instead of being sent to the model", StringComparison.OrdinalIgnoreCase),
                "Voice controls must be explicit, disabled by default, visibly documented, and consumed before model context.");
            mount.Child = null;
        }

        var binding = new DesktopConversationBinding("dictation-fixture", null);
        var draft = new DesktopDraftSnapshot(binding, 0, "Existing text");
        var orchestratorHost = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
            () => new(binding, "Fixture conversation", null, null, false), () => draft,
            (update, _) => { draft = new(binding, draft.Revision + 1, update.Text); return Done(); },
            (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), _ => { }, DesktopPluginSurface.Orchestrator, dataRoot);
        using (var orchestrator = new DesktopPluginRuntime(catalog, orchestratorHost, Dispatcher.CurrentDispatcher))
        {
            orchestrator.LoadEnabled();
            Assert(orchestrator.Panels.Count == 0 && orchestrator.ComposerActions.Count == 0 && orchestrator.TextEntryActions.Count == 1,
                "The Orchestrator must expose only the Dictate text-entry action.");
            var action = orchestrator.TextEntryActions.Single();
            Assert(action.Id == "dictate" && action.Label.Contains("Dictate", StringComparison.Ordinal) && action.ToolTip?.Contains("stop", StringComparison.OrdinalIgnoreCase) == true,
                "The microphone action must clearly identify toggle start/stop behavior.");
        }

        var package = catalog.List().Single(); var assemblyPath = Path.GetFullPath(Path.Combine(package.Directory, catalog.Inspect(package.ManifestPath).Assembly));
        var context = new DiagnosticLoadContext(assemblyPath);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);
        var diagnostics = assembly.GetType("Cerebral.DictationPlugin.DictationPluginDiagnostics", true)!;
        var deterministicJson = (string)diagnostics.GetMethod("RunDeterministicChecks", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [dataRoot])!;
        using (var deterministic = JsonDocument.Parse(deterministicJson))
        {
            Assert(deterministic.RootElement.GetProperty("success").GetBoolean(), "Dictation correction and settings diagnostics must pass.");
            checks += deterministic.RootElement.GetProperty("checks").GetInt32();
        }
        var environmentJson = (string)diagnostics.GetMethod("InspectSpeechEnvironment", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        using var environment = JsonDocument.Parse(environmentJson); speechEnvironment = environment.RootElement.Clone();
        Assert(!environment.RootElement.GetProperty("microphoneOpened").GetBoolean(), "Recognizer discovery must not open or claim a live microphone check.");
        context.Unload();
    }

    private static void CopyPackage(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
    }

    private static FrameworkElement FindAutomationElement(DependencyObject parent, string id)
    {
        if (parent is FrameworkElement element && AutomationProperties.GetAutomationId(element) == id) return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            try { return FindAutomationElement(VisualTreeHelper.GetChild(parent, index), id); }
            catch (InvalidOperationException) { }
        throw new InvalidOperationException("Missing desktop plugin control: " + id);
    }

    private static void ManifestFixtures(DesktopPluginCatalog catalog, string path)
    {
        var original = File.ReadAllText(path);
        try
        {
            foreach (var change in new Action<DesktopPluginManifest>[]
            {
                value => value.ApiVersion = 99,
                value => value.Assembly = "../outside.dll",
                value => value.Id = "aux",
                value => value.Id = "plugin.",
                value => value.Name = ""
            })
            {
                var manifest = JsonSerializer.Deserialize<DesktopPluginManifest>(original, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                change(manifest); File.WriteAllText(path, JsonSerializer.Serialize(manifest, CerebralJson.Options));
                var rejected = false;
                try { _ = catalog.Inspect(path); }
                catch (InvalidDataException) { rejected = true; }
                Assert(rejected, "Invalid native package metadata must be rejected before enablement.");
            }
        }
        finally { File.WriteAllText(path, original); }
    }

    private static void NativeExample(string fixture)
    {
        var catalog = new DesktopPluginCatalog(fixture);
        var binding = new DesktopConversationBinding("native-fixture", "session-one");
        var draft = new DesktopDraftSnapshot(binding, 0, "First line\nSecond line");
        var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
            () => new(binding, "Fixture conversation", "project-one", @"C:\Fixture\Project", false), () => draft,
            (update, _) => { draft = new(binding, draft.Revision + 1, update.Text); return Done(); },
            (_, _) => throw new InvalidOperationException("This example check must not submit work."),
            (_, _) => throw new InvalidOperationException("This example check must not steer work."),
            (_, _) => throw new InvalidOperationException("This example check must not stop work."), _ => { });
        using var runtime = new DesktopPluginRuntime(catalog, host, Dispatcher.CurrentDispatcher);
        runtime.LoadEnabled();
        Assert(runtime.Panels.Count == 0 && runtime.ComposerActions.Count == 0, "Disabled packages must not load native contributions.");
        catalog.SetEnabled("pegasus.workspace-actions", true); runtime.LoadEnabled();
        Assert(runtime.Status.Single().Loaded, "The real example DLL must load through the SDK interface.");
        Assert(runtime.Panels.Count == 1 && runtime.ComposerActions.Count == 2 && runtime.WorkspaceTrees.Count == 1,
            "The descriptor-first example contributes its native sidebar, supplemental workspace tree and ordered actions.");
        var configurationHost = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher, _ => { }, Path.Combine(fixture, "configuration-data"));
        using (var configurationRuntime = new DesktopPluginRuntime(catalog, configurationHost, Dispatcher.CurrentDispatcher))
        {
            configurationRuntime.LoadEnabled();
            var templates = configurationRuntime.AgentTemplates;
            var template = templates.Single(item => item.Id == "project-planner");
            Assert(configurationRuntime.Panels.Count == 0 && configurationRuntime.ComposerActions.Count == 0 &&
                templates.Count == 1 && template.ExecutionRole == "helper" && template.AutomaticBudgets,
                "Configuration exposes a plugin agent template as behavioral defaults without Orchestrator UI contributions.");
        }
        var panel = runtime.Panels.Single().View;
        var mount = new Border { Child = panel };
        mount.Measure(new Size(600, 500)); mount.Arrange(new Rect(0, 0, 600, 500)); mount.UpdateLayout();
        // WPF margins consume the allocation outside ActualWidth/ActualHeight; the plugin intentionally has 12px margins.
        var expectedWidth = mount.ActualWidth - panel.Margin.Left - panel.Margin.Right;
        var expectedHeight = mount.ActualHeight - panel.Margin.Top - panel.Margin.Bottom;
        nativePanelLayout = new { hostWidth = mount.ActualWidth, hostHeight = mount.ActualHeight, panelWidth = panel.ActualWidth, panelHeight = panel.ActualHeight,
            horizontalMargin = panel.Margin.Left + panel.Margin.Right, verticalMargin = panel.Margin.Top + panel.Margin.Bottom };
        Assert(mount.ActualWidth == 600 && mount.ActualHeight == 500 && panel.IsMeasureValid && panel.IsArrangeValid &&
            panel.ActualWidth == expectedWidth && panel.ActualHeight == expectedHeight && expectedWidth > 0 && expectedHeight > 0,
            $"The native panel must fill its host allocation after margins. Actual {panel.ActualWidth}x{panel.ActualHeight}; expected {expectedWidth}x{expectedHeight}.");
        Assert(((StackPanel)panel).Children.OfType<FrameworkElement>().All(child => child.ActualWidth > 0 && child.ActualHeight > 0),
            "The native panel controls must receive nonempty layout bounds.");
        var capture = DesktopPanelCapture.CapturePng(panel);
        using (var captureStream = new MemoryStream(capture, writable: false))
        {
            var frame = BitmapFrame.Create(captureStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert(frame.PixelWidth == (int)Math.Ceiling(panel.ActualWidth) && frame.PixelHeight == (int)Math.Ceiling(panel.ActualHeight) &&
                frame.PixelWidth <= DesktopPanelCapture.MaximumDimension && frame.PixelHeight <= DesktopPanelCapture.MaximumDimension,
                "The shared desktop capture renders the actual mounted native panel into one bounded PNG.");
        }
        var compactStatus = new TextBlock
        {
            Text = "Session Vitals · ready",
            Padding = new Thickness(4, 2, 4, 2),
            Background = Brushes.Navy,
            Foreground = Brushes.White
        };
        compactStatus.Measure(new Size(320, 31));
        compactStatus.Arrange(new Rect(0, 0, Math.Ceiling(compactStatus.DesiredSize.Width), Math.Ceiling(compactStatus.DesiredSize.Height)));
        compactStatus.UpdateLayout();
        Assert(compactStatus.ActualWidth > 0 && compactStatus.ActualHeight is > 0 and < 32,
            "The compact status fixture must exercise a legitimately rendered contribution below the former 32px panel threshold.");
        var compactCapture = DesktopPanelCapture.CapturePng(compactStatus);
        using (var compactStream = new MemoryStream(compactCapture, writable: false))
        {
            var frame = BitmapFrame.Create(compactStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert(frame.PixelWidth == (int)Math.Ceiling(compactStatus.ActualWidth) &&
                   frame.PixelHeight == (int)Math.Ceiling(compactStatus.ActualHeight) && frame.PixelHeight < 32,
                "The shared desktop capture must retain a mounted one-line status view as a decodable PNG.");
        }
        mount.Child = null;
        var quote = runtime.ComposerActions.Single(item => item.Id == "quote-draft");
        quote.ExecuteAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert(draft.Text == "> First line" + Environment.NewLine + "> Second line", "The real plugin quote action must edit through the revision-checked host.");
        var message = new DesktopMessageSnapshot("message-one", binding, "assistant", "**Original answer**", DateTimeOffset.UtcNow);
        var normal = runtime.Present(message);
        Assert(normal?.Decoration is not null && normal.Replacement is null, "Default native presentation should add controls around the ordinary renderer.");
        var changed = 0; runtime.Changed += (_, _) => changed++;
        var nativePanel = (StackPanel)panel;
        nativePanel.Children.OfType<CheckBox>().Single().IsChecked = true;
        Assert(changed == 1, "A native format control must request a presentation refresh.");
        var recursiveChanges = 0;
        EventHandler recursiveRefresh = (_, _) => { recursiveChanges++; host.RefreshPresentation(); };
        runtime.Changed += recursiveRefresh; host.RefreshPresentation(); runtime.Changed -= recursiveRefresh;
        Assert(recursiveChanges == 1, "A presentation refresh from mounting must not synchronously recurse.");
        var plain = runtime.Present(message);
        Assert(plain?.Replacement is TextBox { Text: "**Original answer**", IsReadOnly: true } && message.Text == "**Original answer**", "Native replacement must retain the original message.");
        Assert(runtime.Present(message with { Truncated = true }) is null, "The example must not replace or mislabel an excerpt as the complete original.");
        var packageBeforeUpdate = catalog.List().Single();
        var hotUpdate = Path.Combine(Path.GetTempPath(), "PegasusCerebral.HotPluginUpdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hotUpdate);
        try
        {
            CopyPackage(packageBeforeUpdate.Directory, hotUpdate);
            var updateManifestPath = Path.Combine(hotUpdate, DesktopPluginCatalog.ManifestFileName);
            var updateManifest = JsonSerializer.Deserialize<DesktopPluginManifest>(File.ReadAllText(updateManifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var currentVersion = Version.Parse(updateManifest.Version);
            updateManifest.Version = $"{currentVersion.Major}.{currentVersion.Minor}.{Math.Max(0, currentVersion.Build) + 1}";
            File.WriteAllText(updateManifestPath, JsonSerializer.Serialize(updateManifest, CerebralJson.Options));
            var updated = catalog.Update(updateManifestPath);
            Assert(updated.Enabled && updated.Directory != packageBeforeUpdate.Directory && Directory.Exists(packageBeforeUpdate.Directory),
                "An enabled plugin update must atomically select a new immutable generation while the running generation remains available for release.");
            WaitForDispatcher(() => runtime.ComposerActions.SingleOrDefault(item => item.Id == "quote-draft") is { } current &&
                !ReferenceEquals(current.ExecuteAsync, quote.ExecuteAsync),
                "The running desktop did not observe the active plugin-generation change.");
            var replacementQuote = runtime.ComposerActions.Single(item => item.Id == "quote-draft");
            Assert(!ReferenceEquals(quote.ExecuteAsync, replacementQuote.ExecuteAsync) && runtime.Status.Single() is { Loaded: true, Error: null },
                "The running desktop must release the old contribution and load the selected generation without a process restart.");
            try { quote.ExecuteAsync(CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Expected stale action rejection after hot replacement."); }
            catch (InvalidOperationException) { }

            File.WriteAllText(Path.Combine(hotUpdate, "reviewed-refresh.marker"), "same-version rebuild");
            var refreshed = catalog.RefreshReviewed(updateManifestPath);
            Assert(refreshed.Enabled && refreshed.Version == updated.Version && refreshed.Directory != updated.Directory &&
                   File.ReadAllText(Path.Combine(refreshed.Directory, "reviewed-refresh.marker")) == "same-version rebuild",
                "An explicitly reviewed same-version rebuild must publish new package bytes as a fresh immutable generation.");
            WaitForDispatcher(() => runtime.ComposerActions.SingleOrDefault(item => item.Id == "quote-draft") is { } current &&
                !ReferenceEquals(current.ExecuteAsync, replacementQuote.ExecuteAsync),
                "The running desktop did not observe the reviewed same-version generation change.");
            try { replacementQuote.ExecuteAsync(CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Expected stale action rejection after same-version refresh."); }
            catch (InvalidOperationException) { }
            Assert(Directory.EnumerateDirectories(Path.Combine(fixture, ".packages", packageBeforeUpdate.Id)).Count() <= 3,
                "Repeated hot replacement retains only the active and the small set of still-locked previous generations for later cleanup.");
        }
        finally { if (Directory.Exists(hotUpdate)) Directory.Delete(hotUpdate, true); }
        catalog.SetEnabled("pegasus.workspace-actions", false); runtime.LoadEnabled();
        Assert(runtime.Panels.Count == 0 && runtime.ComposerActions.Count == 0 && runtime.WorkspaceTrees.Count == 0 &&
               runtime.Present(message) is null, "Disable must detach all native contributions.");
        try { quote.ExecuteAsync(CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Expected stale action rejection."); }
        catch (InvalidOperationException) { Assert(draft.Revision == 1, "A retained action cannot execute after disable."); }
        var package = catalog.List().Single();
        var manifest = catalog.Inspect(package.ManifestPath); manifest.EntryType = "Missing.Plugin.Type";
        File.WriteAllText(package.ManifestPath, JsonSerializer.Serialize(manifest, CerebralJson.Options));
        catalog.SetEnabled(package.Id, true); runtime.LoadEnabled();
        Assert(runtime.Status.Single().Error is not null && runtime.Panels.Count == 0, "A real DLL with an unavailable entry point must report failure without contributions.");
    }

    private static void ReviewExample(string fixture)
    {
        var catalog = new DesktopPluginCatalog(fixture);
        var binding = new DesktopConversationBinding("review-plugin-fixture", "review-conversation");
        var draft = new DesktopDraftSnapshot(binding, 4, "A small passage for durable review.");
        var ordinarySubmissions = 0; var workflowSubmissions = 0; var status = "";
        string? submittedPlugin = null; DesktopWorkflowSubmission? submitted = null;
        var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
            () => new(binding, "Review fixture", "project-review", @"C:\Fixture\Review", false), () => draft,
            (_, _) => Done(), (_, _) => { ordinarySubmissions++; return Done(); }, (_, _) => Done(), (_, _) => Done(),
            value => status = value,
            submitWorkflow: (pluginId, value, _) =>
            {
                workflowSubmissions++; submittedPlugin = pluginId; submitted = value;
                return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Accepted,
                    "Review started. Answer the requested fields in the conversation.", value.RequestId, value.RequestId));
            });
        using var runtime = new DesktopPluginRuntime(catalog, host, Dispatcher.CurrentDispatcher);
        runtime.LoadEnabled();
        Assert(runtime.Panels.Count == 0 && runtime.ComposerActions.Count == 0,
            "The review desktop component contributes nothing before owner enablement.");
        catalog.SetEnabled("example.review", true); runtime.LoadEnabled();
        Assert(runtime.Status.Single() is { Loaded: true, Error: null } && runtime.Panels.Single().Id == "review" &&
               runtime.ComposerActions.Single().Id == "start-review",
            "The enabled real review DLL contributes one namespaced panel and action.");

        var firstPanel = runtime.Panels.Single().View;
        Assert(firstPanel.Parent is null, "The plugin supplies an unparented WPF view for host-owned mounting.");
        var mount = new Border { Child = firstPanel };
        mount.Measure(new Size(600, 500)); mount.Arrange(new Rect(0, 0, 600, 500)); mount.UpdateLayout();
        nativePanelLayout = new { hostWidth = mount.ActualWidth, hostHeight = mount.ActualHeight,
            panelWidth = firstPanel.ActualWidth, panelHeight = firstPanel.ActualHeight };
        Assert(mount.ActualWidth == 600 && mount.ActualHeight == 500 && firstPanel.ActualWidth > 0 && firstPanel.ActualHeight > 0 &&
               VisualText(firstPanel).Contains("durable example.review workflow", StringComparison.Ordinal),
            "The review panel lays out in a bounded host and explains its durable workflow.");
        mount.Child = null;

        var action = runtime.ComposerActions.Single();
        action.ExecuteAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert(workflowSubmissions == 1 && ordinarySubmissions == 0 && submittedPlugin == "example.review" && submitted is not null,
            "The review action uses the private native-workflow route instead of an ordinary Chat submission.");
        Assert(submitted!.Target is { DefinitionId: "example.review:review", DefinitionVersion: "1.0.0", StepLimit: 8 } target &&
               target.Input.GetProperty("text").GetString() == draft.Text,
            "The action targets the exact installed service workflow and freezes the current draft as typed input.");
        Assert(submitted.VisibleText == "Review this draft." && !submitted.Context.Contains(draft.Text, StringComparison.Ordinal) &&
               draft.Text == "A small passage for durable review." && status.Contains("Review started", StringComparison.Ordinal),
            "Private routing stays out of the composer while the visible request and original draft remain intact.");

        draft = draft with { Revision = 5, Text = "" };
        action.ExecuteAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert(workflowSubmissions == 1 && status.Contains("Enter a draft", StringComparison.Ordinal),
            "An empty draft is handled locally without starting incomplete work.");
        catalog.SetEnabled("example.review", false); runtime.LoadEnabled();
        Assert(runtime.Panels.Count == 0 && runtime.ComposerActions.Count == 0,
            "Disabling the review desktop component detaches all of its contributions.");
        try { action.ExecuteAsync(CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Expected stale review action rejection."); }
        catch (InvalidOperationException) { Assert(workflowSubmissions == 1, "A retained action cannot run after package disablement."); }
    }

    private static void GeneratedPluginExample(string sourceManifest, string capturePath)
    {
        sourceManifest = Path.GetFullPath(sourceManifest); capturePath = Path.GetFullPath(capturePath);
        if (!File.Exists(sourceManifest) || !Path.GetFileName(sourceManifest).Equals(DesktopPluginCatalog.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Select the generated package's exact desktop-plugin.json.", sourceManifest);
        if (!capturePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The generated plugin capture must use an absolute PNG destination.", nameof(capturePath));
        var fixture = Path.Combine(Path.GetTempPath(), "PegasusCerebral.GeneratedPluginTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture); generatedPluginFixture = fixture;
        try
        {
            var catalog = new DesktopPluginCatalog(fixture);
            var package = catalog.Install(sourceManifest);
            Assert(package.Id == "acceptance.cerebral.generated-review" && !package.Enabled,
                "The generated package passes the same executable-code-free catalog admission and starts disabled.");
            catalog.SetEnabled(package.Id, true);
            var binding = new DesktopConversationBinding("generated-plugin-acceptance", "generated-conversation");
            var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
                () => new(binding, "Generated plugin acceptance", null, null, false),
                () => new(binding, 0, ""), (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), _ => { });
            using var runtime = new DesktopPluginRuntime(catalog, host, Dispatcher.CurrentDispatcher);
            runtime.LoadEnabled();
            Assert(runtime.Status.Single() is { Loaded: true, Error: null },
                "The generated assembly loads through the real native desktop runtime.");
            var contribution = runtime.Panels.Single();
            Assert(contribution.Id == "generated-review" && contribution.Title == "Generated Review",
                "The generated plugin contributes the requested panel identity.");
            var panel = contribution.View;
            var mount = new Border { Padding = new Thickness(12), Child = panel, Background = Brushes.White };
            var window = new Window
            {
                Title = "Pegasus Cerebral · generated plugin acceptance", Width = 600, Height = 500,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                ShowActivated = false, Left = -20_000, Top = -20_000, Content = mount, Background = Brushes.White
            };
            try
            {
                window.Show(); DrainDispatcher(); window.UpdateLayout(); DrainDispatcher();
                Assert(window.IsVisible && new WindowInteropHelper(window).Handle != nint.Zero &&
                    PresentationSource.FromVisual(panel) is HwndSource,
                    "The generated contribution is mounted in a real shown WPF HWND before capture.");
                Assert(VisualText(panel).Contains("Built by Cerebral", StringComparison.Ordinal),
                    "The mounted generated panel contains its requested visible acceptance marker.");
                var capture = DesktopPanelCapture.CapturePng(panel);
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!); File.WriteAllBytes(capturePath, capture);
                using var input = new MemoryStream(capture, writable: false);
                var frame = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Assert(frame.PixelWidth is >= 32 and <= DesktopPanelCapture.MaximumDimension &&
                    frame.PixelHeight is >= 32 and <= DesktopPanelCapture.MaximumDimension,
                    "The actual mounted generated panel produces one decodable bounded PNG.");
                var stride = checked(frame.PixelWidth * 4); var pixels = new byte[checked(stride * frame.PixelHeight)];
                frame.CopyPixels(pixels, stride, 0);
                var first = pixels.AsSpan(0, 4).ToArray();
                Assert(Enumerable.Range(1, pixels.Length / 4 - 1).Any(index =>
                    !pixels.AsSpan(index * 4, 4).SequenceEqual(first)),
                    "The retained capture contains visible rendered content rather than a uniform blank frame.");
                nativePanelLayout = new { windowShown = true, hostWidth = mount.ActualWidth, hostHeight = mount.ActualHeight,
                    panelWidth = panel.ActualWidth, panelHeight = panel.ActualHeight, frame.PixelWidth, frame.PixelHeight };
            }
            finally
            {
                mount.Child = null; window.Close(); DrainDispatcher();
            }
            catalog.SetEnabled(package.Id, false); runtime.LoadEnabled();
            Assert(runtime.Panels.Count == 0, "Disabling the generated package detaches its native contribution.");
        }
        finally
        {
            // The child still maps the native DLL until process exit. Its parent owns exact bounded cleanup after
            // the child terminates; deleting here would make a successful UI check fail with a Windows sharing error.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
    }

    private static string VisualText(DependencyObject root)
    {
        var text = new System.Text.StringBuilder();
        void Visit(DependencyObject node)
        {
            if (node is TextBlock block) text.AppendLine(block.Text);
            if (node is TextBox box) text.AppendLine(box.Text);
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) Visit(VisualTreeHelper.GetChild(node, index));
        }
        Visit(root); return text.ToString();
    }

    private static void NewPluginExamples(string programmingPackage, string promptReviewPackage)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-new-plugin-tests-" + Guid.NewGuid().ToString("N"));
        var binding = new DesktopConversationBinding("new-plugin-fixture", null); var draft = new DesktopDraftSnapshot(binding, 0, "Build a small fixture.");
        Task<DesktopServiceCommandResult> Service(string operation, object arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); object value = operation switch
            {
                "programming.status" => new { directory = Path.Combine(root, "programming"), projectCount = 0 },
                "programming.list" => Array.Empty<object>(),
                "agents.list" => Array.Empty<object>(),
                _ => throw new InvalidOperationException("Unexpected fixture command: " + operation)
            };
            return Task.FromResult(new DesktopServiceCommandResult(true, JsonSerializer.SerializeToElement(value, CerebralJson.Options)));
        }
        var orchestratorHost = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
            () => new(binding, "Fixture conversation", "fixture-project", @"C:\Fixture\Project", false), () => draft,
            (update, _) => { draft = new(binding, draft.Revision + 1, update.Text); return Done(); }, (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), _ => { },
            DesktopPluginSurface.Orchestrator, Path.Combine(root, "orchestrator-data"), Service);
        static IDesktopPluginHost Scope(DesktopPluginHostAdapter source, string id, string name, string package) =>
            (IDesktopPluginHost)(typeof(DesktopPluginHostAdapter).GetMethod("CreateScope", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(source, [id, name, Path.GetFullPath(package)]) ?? throw new InvalidOperationException("Plugin scope was not created."));
        static (DiagnosticLoadContext Context, IDesktopPlugin Plugin) Load(string package)
        {
            var manifest = JsonSerializer.Deserialize<DesktopPluginManifest>(File.ReadAllText(Path.Combine(Path.GetFullPath(package), DesktopPluginCatalog.ManifestFileName)),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Plugin manifest is missing.");
            var assemblyPath = Path.Combine(Path.GetFullPath(package), manifest.Assembly); var context = new DiagnosticLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath); var plugin = (IDesktopPlugin?)Activator.CreateInstance(assembly.GetType(manifest.EntryType, true)!) ?? throw new InvalidDataException("Plugin entry point is unavailable.");
            return (context, plugin);
        }
        var programming = Load(programmingPackage); var promptReview = Load(promptReviewPackage);
        var programmingHost = Scope(orchestratorHost, "pegasus.programming", "Programming", programmingPackage);
        var promptReviewHost = Scope(orchestratorHost, "pegasus.prompt-review", "Prompt Review", promptReviewPackage);
        try
        {
            programming.Plugin.Initialize(programmingHost); promptReview.Plugin.Initialize(promptReviewHost);
            var panels = programming.Plugin.Panels.Concat(promptReview.Plugin.Panels).ToArray();
            var programmingActions = programming.Plugin.ComposerActions.ToArray();
            var modes = ((IDesktopInputModePlugin)promptReview.Plugin).InputModes.ToArray();
            var templates = ((IDesktopAgentTemplatePlugin)programming.Plugin).AgentTemplates.Concat(((IDesktopAgentTemplatePlugin)promptReview.Plugin).AgentTemplates).ToArray();
            Assert(panels.Length == 1 && panels[0].Title == "Programming", "Programming contributes the only new right-sidebar panel.");
            Assert(programmingActions.Length == 1 && programmingActions[0].Id == "programming.develop" && programmingActions[0].Label == "Develop",
                "Programming exposes one explicit composer route into its durable development harness.");
            Assert(modes.Length == 1 && modes[0].Id == "understanding" && modes[0].QuickToggle,
                "Prompt Review contributes the only composer interception as a checkbox-capable request-refinement gate.");
            Assert(templates.Select(x => x.Id).ToHashSet(StringComparer.Ordinal).SetEquals(["programming-engineer", "programming-reviewer", "prompt-clarifier"]),
                "Programming and Prompt Review expose separate reusable agent templates.");
            Assert(templates.Single(x => x.Id == "programming-engineer").RequiredTools.Contains("plugin.verify", StringComparer.Ordinal) &&
                   templates.Single(x => x.Id == "programming-engineer").RequiredTools.Contains("plugin.present", StringComparer.Ordinal),
                "The Programming engineer carries the plugin verification and owner-review handoff tools.");
            var promptDiagnostics = promptReview.Plugin.GetType().Assembly.GetType("Cerebral.PromptReviewPlugin.PromptReviewPluginDiagnostics", true)!;
            var promptDiagnosticJson = (string)promptDiagnostics.GetMethod("RunDeterministicChecks", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
            using (var promptDiagnostic = JsonDocument.Parse(promptDiagnosticJson))
                Assert(promptDiagnostic.RootElement.GetProperty("success").GetBoolean() &&
                       promptDiagnostic.RootElement.GetProperty("checks").GetInt32() == 6 &&
                       !promptDiagnostic.RootElement.GetProperty("modelInvoked").GetBoolean(),
                    "Prompt Review removes leaked reviewer-only limits while preserving explicit current data and a user-authored no-tools constraint.");
            var panel = panels.Single().CreateView(); DrainDispatcher(); var mount = new Border { Child = panel }; mount.Measure(new Size(420, 640)); mount.Arrange(new Rect(0, 0, 420, 640)); mount.UpdateLayout();
            var tree = FindAutomationElement(panel, "PegasusProgramming.ProjectTree") as TreeView;
            Assert(panel.ActualWidth > 0 && panel.ActualHeight > 0 && tree is not null, "The programming hierarchy fills a resizable sidebar host.");
            nativePanelLayout = new { panelWidth = panel.ActualWidth, panelHeight = panel.ActualHeight };
            mount.Child = null;
            Assert(((IDesktopConfigurationPlugin)programming.Plugin).ConfigurationPanels.Count() == 1 && templates.Length == 3,
                "Configuration receives module setup and all three agent templates without core programming UI.");
        }
        finally
        {
            programming.Plugin.Dispose(); promptReview.Plugin.Dispose();
            ((IDisposable)programmingHost).Dispose(); ((IDisposable)promptReviewHost).Dispose();
            programming.Context.Unload(); promptReview.Context.Unload();
        }
    }

    private static void CollaborationPluginExample(string sourceManifest, bool shown)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-collaboration-plugin-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Window? window = null;
        try
        {
            var catalog = new DesktopPluginCatalog(Path.Combine(root, "packages"));
            var installed = catalog.SeedBundled(Path.GetFullPath(sourceManifest));
            Assert(installed is { Id: "pegasus.collaboration", Enabled: true, Error: null },
                "The collaboration sidebar seeds as an enabled removable package.");
            var binding = new DesktopConversationBinding("collaboration-fixture", "source-chat");
            var draft = new DesktopDraftSnapshot(binding, 0, "Keep the chat transcript visible.");
            var calls = new List<string>();
            var revision = 3L;
            var taskStatus = "Running";
            DateTimeOffset? adoptedAt = null;
            object TaskValue() => new
            {
                id = "task-fixture",
                request = new
                {
                    requestId = "request-fixture",
                    sourceConversationId = "source-chat",
                    targetConversationId = "worker-chat",
                    instruction = "Review the collaboration interface while the source transcript stays visible."
                },
                attemptId = "attempt-fixture",
                status = taskStatus,
                revision,
                targetRunRevision = 9L,
                waitingReason = (string?)null,
                error = (string?)null,
                adoptedAt
            };
            Task<DesktopServiceCommandResult> Service(string operation, object arguments, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                calls.Add(operation);
                object value = operation switch
                {
                    "collaboration.list" => new { tasks = new[] { TaskValue() }, snapshotRow = 1L, nextBeforeRow = (long?)null },
                    "collaboration.events" => new
                    {
                        events = new[]
                        {
                            new { taskId = "task-fixture", sequence = 1L, type = "submitted", status = taskStatus,
                                at = DateTimeOffset.Parse("2026-09-30T12:00:00-07:00"), data = new { } }
                        },
                        snapshotSequence = 1L,
                        nextAfterSequence = (long?)null
                    },
                    "collaboration.result" => new { taskId = "task-fixture", attemptId = "attempt-fixture", targetRunRevision = 9L,
                        offset = 0, content = "The worker completed the bounded fixture review.", nextOffset = (int?)null, complete = true, sha256 = new string('a', 64) },
                    "collaboration.steer" => CompleteSteering(),
                    "collaboration.adopt" => CompleteAdoption(),
                    _ => throw new InvalidOperationException("Unexpected collaboration fixture command: " + operation)
                };
                return Task.FromResult(new DesktopServiceCommandResult(true, JsonSerializer.SerializeToElement(value, CerebralJson.Options)));
            }
            object CompleteSteering() { revision++; taskStatus = "Completed"; return TaskValue(); }
            object CompleteAdoption() { revision++; adoptedAt = DateTimeOffset.Parse("2026-09-30T12:05:00-07:00"); return TaskValue(); }

            var statuses = new List<string>();
            var navigated = new List<string>();
            var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
                () => new(binding, "Source chat", "fixture-project", @"C:\Fixture\Project", false), () => draft,
                (update, _) => { draft = new(binding, draft.Revision + 1, update.Text); return Done(); },
                (_, _) => throw new InvalidOperationException("The collaboration panel must not submit Chat."),
                (_, _) => throw new InvalidOperationException("The collaboration panel must not steer Chat."),
                (_, _) => throw new InvalidOperationException("The collaboration panel must not stop Chat."),
                statuses.Add, DesktopPluginSurface.Orchestrator, Path.Combine(root, "data"), Service);
            host.ConfigureWorkspace(new(
                (_, _) => Task.FromResult(new DesktopConversationList([
                    new("source-chat", "Source chat", "fixture-project"), new("worker-chat", "Worker chat", "fixture-project")
                ])),
                (id, _) => { navigated.Add(id); return Done(); },
                (_, _) => Done(),
                (_, _, _) => Done()));
            using var runtime = new DesktopPluginRuntime(catalog, host, Dispatcher.CurrentDispatcher);
            runtime.LoadEnabled();
            Assert(runtime.Status.Single() is { Id: "pegasus.collaboration", Loaded: true, Error: null },
                "The actual collaboration package loads through the desktop runtime.");
            Assert(runtime.Panels.Count == 1 && runtime.Panels[0].Title == "Collaboration" &&
                   runtime.ConversationWorkspaces.Count == 0 &&
                   runtime.Present(new("fixture-message", binding, "assistant", "Visible assistant reply", DateTimeOffset.UtcNow)) is null,
                "The plugin contributes one sidebar and cannot replace the conversation workspace or transcript renderer.");

            var panel = runtime.Panels.Single().View;
            var transcript = new TextBox
            {
                Text = "You: Keep going until the plugin is testable.\n\nCerebral: The visible chat log remains here.",
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16)
            };
            AutomationProperties.SetAutomationId(transcript, "CollaborationFixture.VisibleChatLog");
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 480 });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
            layout.Children.Add(transcript);
            var tabs = new TabControl { Margin = new Thickness(4) };
            tabs.Items.Add(new TabItem { Header = "Collaboration", Content = panel, IsSelected = true });
            Grid.SetColumn(tabs, 1); layout.Children.Add(tabs);
            window = new Window { Title = "Pegasus Cerebral collaboration interface acceptance", Width = 1180, Height = 760,
                Content = layout, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            if (shown) { window.Show(); window.Activate(); }
            layout.Measure(new Size(1160, 720)); layout.Arrange(new Rect(0, 0, 1160, 720)); layout.UpdateLayout();
            DrainDispatcher();
            Assert(transcript.ActualWidth >= 480 && panel.ActualWidth >= 300 && transcript.IsVisible == shown,
                "The plugin and the ordinary chat transcript retain independent visible layout columns.");
            Assert(calls.Contains("collaboration.list") && calls.Contains("collaboration.events"),
                "The mounted panel reads the bounded collaboration list and task activity through the service host.");
            var taskList = FindAutomationElement(panel, "Collaboration.TaskList") as ListBox;
            Assert(taskList is { Items.Count: 1, SelectedIndex: 0 } && VisualText(panel).Contains("Review the collaboration interface", StringComparison.Ordinal),
                "The real WPF sidebar displays the retained task instruction and selects it for review.");

            var openWorker = (Button)FindAutomationElement(panel, "Collaboration.OpenTarget");
            openWorker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); DrainDispatcher();
            Assert(navigated.SequenceEqual(["worker-chat"]), "Open worker chat uses the host's normal transcript navigation path.");
            var steeringBox = (TextBox)FindAutomationElement(panel, "Collaboration.Steering");
            steeringBox.Text = "Keep the final result concise.";
            ((Button)FindAutomationElement(panel, "Collaboration.SendSteering")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DrainDispatcher();
            Assert(calls.Contains("collaboration.steer") && VisualText(panel).Contains("The worker completed the bounded fixture review.", StringComparison.Ordinal),
                "Steering updates the task by revision and the bounded completed result appears in the sidebar.");
            ((Button)FindAutomationElement(panel, "Collaboration.Adopt")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DrainDispatcher();
            Assert(calls.Contains("collaboration.adopt") && VisualText(panel).Contains("Adopted into the source chat", StringComparison.Ordinal),
                "The owner can adopt the exact completed attempt without hiding Chat.");
            Assert(!calls.Contains("collaboration.submit") && statuses.All(value =>
                       !value.Contains("failed", StringComparison.OrdinalIgnoreCase) &&
                       !value.Contains("error", StringComparison.OrdinalIgnoreCase)),
                "Mounting and inspecting the panel creates no agent work and reports no host failure.");

            var acceptance = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")), ".runtime", "acceptance");
            Directory.CreateDirectory(acceptance);
            var capturePath = Path.Combine(acceptance, "collaboration-panel-current.png");
            File.WriteAllBytes(capturePath, DesktopPanelCapture.CapturePng(layout));
            nativePanelLayout = new { chatWidth = transcript.ActualWidth, panelWidth = panel.ActualWidth,
                height = panel.ActualHeight, capturePath, shown };
        }
        finally
        {
            window?.Close();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The collectible native load context can retain its mapped DLL until this short-lived test
                // process exits. The parent acceptance cleanup removes only the exact owned fixture afterward.
            }
        }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.InvokeAsync(() => frame.Continue = false, DispatcherPriority.ContextIdle);
        Dispatcher.PushFrame(frame);
    }

    private static void WaitForDispatcher(Func<bool> condition, string message)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            DrainDispatcher();
            if (condition()) return;
            Thread.Sleep(20);
        }
        throw new TimeoutException(message);
    }

    private static Task<DesktopOperationResult> Done() => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }

    private sealed class DiagnosticLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);
        protected override Assembly? Load(AssemblyName name)
        {
            var sdk = typeof(IDesktopPlugin).Assembly;
            if (name.Name == sdk.GetName().Name) return sdk;
            var path = resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}
