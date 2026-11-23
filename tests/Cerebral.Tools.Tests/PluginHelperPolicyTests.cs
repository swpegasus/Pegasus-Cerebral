using System.Text.Json;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;
using Cerebral.Tools;

internal static class PluginHelperPolicyTests
{
    public static async Task RunAvailabilityAsync(string parent, Action<bool, string> check)
    {
        var author = CreateTool();
        string[] authorTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ContractToolName,
            PluginScaffoldToolExtension.ToolName, PluginScaffoldToolExtension.VerifyToolName];
        var settings = new HostSettings();
        await using var dispatcher = new ToolDispatcher(() => settings,
            [author, new AvailabilityCatalogTool("plugin.status"), new AvailabilityCatalogTool("plugin.inventory")],
            stateDirectory: Path.Combine(parent, "sdk-availability-state"));
        var ordinary = new AgentProfile
        {
            Id = "fixture-ordinary-coder", Revision = 1, Role = "lead", WorkerId = "local", AllowedRoots = [parent],
            AllowedTools = ["file.read", "file.write", "process.run", "plugin.status", "plugin.inventory", .. authorTools],
            Capabilities = ["filesystem.read", "filesystem.write", "process.execute"]
        };
        var ordinaryBefore = JsonSerializer.Serialize(ordinary, CerebralJson.Options);
        async Task<HashSet<string>> Names(AgentProfile profile) =>
            (await dispatcher.ListAsync(profile, default)).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var ordinaryNames = await Names(ordinary);
        check(ordinaryNames.Contains("file.read") && ordinaryNames.Contains("file.write") && ordinaryNames.Contains("process.run") &&
              ordinaryNames.Contains("plugin.status") && ordinaryNames.Contains("plugin.inventory") &&
              !authorTools.Any(ordinaryNames.Contains),
            "standalone coding retains ordinary file/process and unrelated plugin status/inventory tools without SDK author contracts");
        foreach (var denial in new[] { "missing-management", "wrong-role", "nonlocal-execution" })
        {
            var profile = Copy(ordinary);
            if (denial != "missing-management") profile.Capabilities.Add("desktop.plugins.manage");
            if (denial == "wrong-role") profile.Role = "compactor";
            if (denial == "nonlocal-execution") profile.WorkerId = "fixture-remote-tools";
            var names = await Names(profile);
            check(!authorTools.Any(names.Contains) && names.Contains("plugin.status") && names.Contains("plugin.inventory"),
                "SDK availability preserves the complete author predicate without hiding unrelated read-only contracts: " + denial);
            var context = new ToolExecutionContext(profile, "availability-session", "availability-request", parent);
            foreach (var name in authorTools)
            {
                // Empty scaffold arguments cannot create a target if its author guard regresses.
                var arguments = name == PluginScaffoldToolExtension.ContractToolName
                    ? CerebralJson.Element(new { topic = "desktop-base" })
                    : name == PluginScaffoldToolExtension.VerifyToolName
                        ? CerebralJson.Element(new { root = parent }) : CerebralJson.Element(new { });
                var denied = await author.ExecuteAsync(new(name, arguments), context, default);
                check(!denied.Success && denied.Content.Contains("local lead or helper", StringComparison.Ordinal) &&
                      denied.Content.Contains("desktop.plugins.manage", StringComparison.Ordinal),
                    "direct SDK execution retains its author guard even when the tool is hidden: " + denial + "/" + name);
            }
        }
        foreach (var role in new[] { "lead", "helper" })
        {
            var profile = Copy(ordinary); profile.Role = role; profile.ModelWorkerId = "fixture-remote-inference";
            profile.Capabilities.Add("desktop.plugins.manage");
            var names = await Names(profile);
            check(authorTools.All(names.Contains) && names.Contains("file.read") && names.Contains("file.write") &&
                  names.Contains("process.run") && names.Contains("plugin.status") && names.Contains("plugin.inventory"),
                "an authorized local author sees its SDK contracts without changing model routing or ordinary grants: " + role);
        }
        var permitted = Copy(ordinary); permitted.Capabilities.Add("desktop.plugins.manage");
        var noWrite = Copy(permitted); noWrite.Capabilities.Remove("filesystem.write");
        var readNames = await Names(noWrite);
        check(!readNames.Contains(PluginScaffoldToolExtension.ToolName) &&
              authorTools.Where(name => name != PluginScaffoldToolExtension.ToolName).All(readNames.Contains),
            "author eligibility never replaces the normal filesystem.write requirement for scaffolding");
        var noRead = Copy(permitted); noRead.Capabilities.Remove("filesystem.read");
        var writeNames = await Names(noRead);
        check(writeNames.Contains(PluginScaffoldToolExtension.ToolName) &&
              !authorTools.Where(name => name != PluginScaffoldToolExtension.ToolName).Any(writeNames.Contains),
            "author eligibility never replaces normal filesystem.read requirements for inventory, contract or verification");
        var noProjectTool = Copy(permitted); noProjectTool.AllowedTools.Remove(PluginScaffoldToolExtension.ProjectsToolName);
        var narrowedNames = await Names(noProjectTool);
        check(!narrowedNames.Contains(PluginScaffoldToolExtension.ProjectsToolName) &&
              authorTools.Where(name => name != PluginScaffoldToolExtension.ProjectsToolName).All(narrowedNames.Contains),
            "normal exact tool-name grants still narrow an otherwise authorized SDK author");
        var direct = await dispatcher.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName, CerebralJson.Element(new { })),
            new(ordinary, "availability-session", "availability-request", parent), default);
        check(!direct.Success && direct.Content.Contains("desktop.plugins.manage", StringComparison.Ordinal),
            "a structured call through ToolDispatcher cannot bypass the hidden SDK execution guard");
        foreach (var collision in new[] { PluginScaffoldToolExtension.ProjectsToolName, "file.read" })
        {
            await using var invalid = new ToolDispatcher(() => settings, [author, new AvailabilityCatalogTool(collision)],
                stateDirectory: Path.Combine(parent, "sdk-availability-collision-state"));
            var refused = false;
            try { await invalid.ListAsync(ordinary, default); }
            catch (InvalidOperationException error) when (error.Message == "Tool extensions contain a duplicate tool name.") { refused = true; }
            check(refused, "all-extension duplicate/reserved-name checks run before hiding unauthorized SDK contracts: " + collision);
        }
        check(JsonSerializer.Serialize(ordinary, CerebralJson.Options) == ordinaryBefore,
            "availability and rejected direct calls do not add a role, root, capability or tool grant");
    }

    public static async Task RunAuthoringAsync(string parent, Action<bool, string> check)
    {
        var root = Path.Combine(parent, "helper-authoring-policy");
        Directory.CreateDirectory(root);
        var profile = new AgentProfile
        {
            Id = "fixture-scoped-helper", Revision = 1, Role = "helper", WorkerId = "local",
            ModelWorkerId = "fixture-remote-inference", AllowedRoots = [root],
            AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ContractToolName,
                PluginScaffoldToolExtension.ToolName, PluginScaffoldToolExtension.VerifyToolName],
            Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
        };
        var before = JsonSerializer.Serialize(profile, CerebralJson.Options);
        var context = new ToolExecutionContext(profile, "helper-provider-session", "helper-provider-child", root, FullAccess: true)
        {
            EnforceProfileScope = true,
            PathAccess = Scope(root, ScopedPathAccess.Write)
        };
        var tool = CreateTool();
        var projects = await tool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { })), context, default);
        var contract = await tool.ExecuteAsync(new(PluginScaffoldToolExtension.ContractToolName,
            CerebralJson.Element(new { topic = "desktop-base" })), context, default);
        var target = Path.Combine(root, "granted-helper");
        var scaffold = await tool.ExecuteAsync(Scaffold(target, "fixture.granted-helper"), context, default);
        check(projects.Success && contract.Success && scaffold.Success && File.Exists(Path.Combine(target, "PLUGIN.md")),
            "an explicitly granted local helper authors SDK source within its narrowed provider scope without root-user callbacks");
        check(JsonSerializer.Serialize(profile, CerebralJson.Options) == before,
            "helper authoring adds no tool, capability, root, role or model-routing grant");
        // This is a provider fixture with a narrowed helper context, not a live delegated workflow.
        foreach (var name in profile.AllowedTools)
        {
            var compactor = Copy(profile); compactor.Role = "compactor";
            var call = name switch
            {
                PluginScaffoldToolExtension.ProjectsToolName => new ToolCall(name, CerebralJson.Element(new { })),
                PluginScaffoldToolExtension.ContractToolName => new ToolCall(name, CerebralJson.Element(new { topic = "desktop-base" })),
                PluginScaffoldToolExtension.VerifyToolName => new ToolCall(name, CerebralJson.Element(new { root = target })),
                _ => Scaffold(Path.Combine(root, "denied-compactor"), "fixture.denied-compactor")
            };
            var denied = await tool.ExecuteAsync(call, context with { Profile = compactor }, default);
            check(!denied.Success && denied.Content.Contains("local lead or helper", StringComparison.Ordinal),
                "compactors remain excluded from SDK authoring: " + name);
        }
        check(!Directory.Exists(Path.Combine(root, "denied-compactor")),
            "a compactor's rejected scaffold creates no target");
        foreach (var missing in new[] { "filesystem.read", "desktop.plugins.manage" })
        {
            var narrowed = Copy(profile); narrowed.Capabilities.Remove(missing);
            var denied = await tool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
                CerebralJson.Element(new { })), context with { Profile = narrowed }, default);
            check(!denied.Success, "helper discovery preserves the explicit capability requirement: " + missing);
        }
        var noTool = Copy(profile); noTool.AllowedTools.Remove(PluginScaffoldToolExtension.ProjectsToolName);
        check(!(await tool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
                CerebralJson.Element(new { })), context with { Profile = noTool }, default)).Success,
            "helper discovery cannot bypass its exact tool grant");
        var noWrite = Copy(profile); noWrite.Capabilities.Remove("filesystem.write");
        var deniedWritePath = Path.Combine(root, "denied-write");
        check(!(await tool.ExecuteAsync(Scaffold(deniedWritePath, "fixture.denied-write"),
                context with { Profile = noWrite }, default)).Success && !Directory.Exists(deniedWritePath),
            "helper scaffolding without filesystem.write creates no source");
        var remoteTools = Copy(profile); remoteTools.WorkerId = "fixture-remote-tools";
        check(!(await tool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
                CerebralJson.Element(new { })), context with { Profile = remoteTools }, default)).Success,
            "remote inference does not authorize remote tool execution");
        var outside = Path.Combine(parent, "helper-outside-scope");
        var rejectedOutside = await tool.ExecuteAsync(Scaffold(outside, "fixture.outside-helper"), context, default);
        check(!rejectedOutside.Success && !Directory.Exists(outside),
            "Full Access does not widen a helper scaffold beyond its reviewed roots and host path ceiling");

        var readOnly = context with { PathAccess = Scope(root, ScopedPathAccess.Read) };
        var refreshed = await tool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { })), readOnly, default);
        var readOnlyTarget = Path.Combine(root, "read-only-scaffold");
        var rejectedReadOnly = await tool.ExecuteAsync(Scaffold(readOnlyTarget, "fixture.read-only-helper"), readOnly, default);
        check(refreshed.Success && !rejectedReadOnly.Success &&
              rejectedReadOnly.Content.Contains("not writable", StringComparison.Ordinal) && !Directory.Exists(readOnlyTarget),
            "a read-only helper inventory does not grant a scaffold write even with Full Access");
    }

    public static async Task RunContextualPathsAsync(AgentProfile authorProfile, AgentProfile presentationProfile,
        string manifest, string sourceRoot, string parent, Action<bool, string> check)
    {
        var author = Copy(authorProfile); author.Role = "helper"; author.ModelWorkerId = "fixture-remote-inference";
        var authorContext = ReadContext(author, sourceRoot);
        var verified = await CreateTool().ExecuteAsync(new(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = sourceRoot })), authorContext, default);
        check(verified.Success && verified.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, State: "verified" },
            "a scoped helper verifies the existing fresh compiled fixture without a new compiler or inference request");

        var helper = Copy(presentationProfile); helper.Role = "helper"; helper.ModelWorkerId = "fixture-remote-inference";
        var context = ReadContext(helper, sourceRoot);
        check(PluginScaffoldToolExtension.ValidateDesktopPresentationSource(context, manifest) == sourceRoot,
            "contextual source admission retains a read-only scope and returns the exact generated root");
        var foreign = context with { PathAccess = Scope(Path.Combine(parent, "foreign-source-scope"), ScopedPathAccess.Read) };
        check(Denied(() => PluginScaffoldToolExtension.ValidateDesktopPresentationSource(foreign, manifest)),
            "a broad profile and Full Access cannot replace the actual source path ceiling");
        var manifestOnly = context with { PathAccess = Scope(manifest, ScopedPathAccess.Read) };
        check(Denied(() => PluginScaffoldToolExtension.ValidateDesktopPresentationSource(manifestOnly, manifest)),
            "permission to the built manifest alone does not grant its generated source ancestors");
        var traversal = Path.Combine(Path.GetDirectoryName(manifest)!, "..", Path.GetFileName(manifest));
        check(Denied(() => PluginScaffoldToolExtension.ValidateDesktopPresentationSource(context, traversal)),
            "contextual source admission rejects traversal before source inspection");
        CheckLinkedSource(context, manifest, sourceRoot, parent, check);
        var absentPortable = Path.Combine(parent, "absent-portable-fixture", NativeDesktopPluginPackage.ManifestFileName);
        check(PluginScaffoldToolExtension.ValidateDesktopPresentationSource(helper, absentPortable, false) is null,
            "the legacy profile-only source overload retains its nullable non-SDK-layout behavior");

        var portable = Path.Combine(parent, "contextual-portable-package");
        Directory.CreateDirectory(portable);
        var identity = NativeDesktopPluginPackage.Inspect(manifest);
        File.Copy(identity.AssemblyPath, Path.Combine(portable, identity.Assembly));
        var portableManifest = Path.Combine(portable, NativeDesktopPluginPackage.ManifestFileName);
        File.Copy(manifest, portableManifest);
        var portableContext = context with { PathAccess = Scope(portable, ScopedPathAccess.Read) };
        check(PluginScaffoldToolExtension.ValidateDesktopPresentationSource(portableContext, portableManifest) is null,
            "an admitted imported package has no fabricated generated source root");
        check(Denied(() => PluginScaffoldToolExtension.ValidateDesktopPresentationSource(context, portableManifest)),
            "the contextual overload validates imported manifest scope before returning null");

        if (!OperatingSystem.IsWindows()) return;
        var admitted = InterfacePathPolicy.ValidateNativePluginPresentation(context, manifest);
        check(admitted.Path == manifest && admitted.GeneratedSourceRoot == sourceRoot,
            "native-plugin metadata eligibility accepts a local helper with remote inference and read-only actual scope");
        check(InterfacePathPolicy.AllowedKinds(helper).Count == 0 &&
              Denied(() => InterfacePathPolicy.ValidatePresentation(helper, "plugin", manifest)),
            "contextual helper eligibility does not widen legacy profile-only presentation behavior");
        check(Denied(() => InterfacePathPolicy.ValidateNativePluginPresentation(foreign, manifest)) &&
              Denied(() => InterfacePathPolicy.ValidateNativePluginPresentation(manifestOnly, manifest)),
            "native-plugin path validation retains both manifest and source-ancestor scope checks");
        var imported = InterfacePathPolicy.ValidateNativePluginPresentation(portableContext, portableManifest);
        check(imported.Path == portableManifest && imported.GeneratedSourceRoot is null,
            "the shared native path validator supports an admitted imported package without inventing source");

        foreach (var role in new[] { "compactor", "other" })
        {
            var ineligible = Copy(helper); ineligible.Role = role;
            check(Denied(() => InterfacePathPolicy.ValidateNativePluginPresentation(context with { Profile = ineligible }, manifest)),
                "native plugin eligibility excludes the unapproved role: " + role);
        }
        var remoteTools = Copy(helper); remoteTools.WorkerId = "fixture-remote-tools";
        check(Denied(() => InterfacePathPolicy.ValidateNativePluginPresentation(context with { Profile = remoteTools }, manifest)),
            "native plugin eligibility requires local tool execution");
        foreach (var tool in new[] { "file.read", "process.run" })
        {
            var noTool = Copy(helper); noTool.AllowedTools.Remove(tool);
            check(Denied(() => InterfacePathPolicy.ValidateNativePluginPresentation(context with { Profile = noTool }, manifest)),
                "native plugin eligibility retains the existing tool requirement: " + tool);
        }
        foreach (var capability in new[] { "filesystem.read", "process.execute", "desktop.plugins.manage" })
        {
            var noCapability = Copy(helper); noCapability.Capabilities.Remove(capability);
            check(Denied(() => InterfacePathPolicy.ValidateNativePluginPresentation(context with { Profile = noCapability }, manifest)),
                "native plugin eligibility retains the existing capability requirement: " + capability);
        }
    }

    private static void CheckLinkedSource(ToolExecutionContext context, string manifest,
        string sourceRoot, string parent, Action<bool, string> check)
    {
        var linkedRoot = Path.Combine(parent, "contextual-linked-source");
        var desktop = Path.Combine(linkedRoot, "Desktop");
        var output = Path.Combine(desktop, "bin", "Release", "net10.0-windows");
        Directory.CreateDirectory(output);
        var identity = NativeDesktopPluginPackage.Inspect(manifest);
        var copiedManifest = Path.Combine(output, NativeDesktopPluginPackage.ManifestFileName);
        File.Copy(manifest, copiedManifest);
        File.Copy(identity.AssemblyPath, Path.Combine(output, identity.Assembly));
        File.Copy(Path.Combine(sourceRoot, "Desktop", NativeDesktopPluginPackage.ManifestFileName),
            Path.Combine(desktop, NativeDesktopPluginPackage.ManifestFileName));
        try
        {
            File.CreateSymbolicLink(Path.Combine(desktop, "GeneratedDesktopPlugin.cs"),
                Path.Combine(sourceRoot, "Desktop", "GeneratedDesktopPlugin.cs"));
        }
        catch (Exception error) when (error is PlatformNotSupportedException or UnauthorizedAccessException ||
            error is IOException && (error.HResult & 0xffff) is 1314 or 50)
        {
            Console.WriteLine("Contextual source-link check skipped: symbolic-link creation is unavailable on this host.");
            return;
        }
        var linkedContext = context with { PathAccess = Scope(linkedRoot, ScopedPathAccess.Read) };
        check(Denied(() => PluginScaffoldToolExtension.ValidateDesktopPresentationSource(linkedContext, copiedManifest)),
            "contextual verification rejects a linked source file before following it outside the admitted source root");
    }

    private sealed class AvailabilityCatalogTool(string name) : IToolExtension
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } =
            [new(name, "Read-only catalog fixture.", CerebralJson.Element(new { type = "object" }), "filesystem.read")];
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The availability-only catalog fixture must never execute.");
    }

    private static PluginScaffoldToolExtension CreateTool() =>
        new(typeof(IDesktopPlugin).Assembly.Location, typeof(IServicePlugin).Assembly.Location);

    private static AgentProfile Copy(AgentProfile profile) =>
        CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));

    private static ToolCall Scaffold(string path, string id) =>
        new(PluginScaffoldToolExtension.ToolName,
            CerebralJson.Element(new { pluginId = id, name = id, targetDirectory = path, component = "service" }));

    private static ScopedPathAccessScope Scope(string path, ScopedPathAccess access) =>
        new(ScopedPathAccessPolicy.Version, [new(path, access)]);

    private static ToolExecutionContext ReadContext(AgentProfile profile, string root) =>
        new(profile, "contextual-provider-session", "contextual-provider-child", root, FullAccess: true)
        { EnforceProfileScope = true, PathAccess = Scope(root, ScopedPathAccess.Read) };

    private static bool Denied(Action action)
    {
        try { action(); return false; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
