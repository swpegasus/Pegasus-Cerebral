using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;
using Cerebral.Tools;

internal static class PluginScaffoldTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        await RunReadAccessChecksAsync(root, check);
        var profile = new AgentProfile
        {
            Role = "lead", WorkerId = "local", AllowedRoots = [root],
            AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ContractToolName,
                PluginScaffoldToolExtension.ToolName, PluginScaffoldToolExtension.VerifyToolName],
            Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
        };
        var tool = new PluginScaffoldToolExtension(typeof(IDesktopPlugin).Assembly.Location,
            typeof(IServicePlugin).Assembly.Location);
        await using var routed = new ToolDispatcher(() => new HostSettings(), [tool]);
        var descriptors = tool.Tools.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var developmentPrerequisites = new[]
            { PluginScaffoldToolExtension.ContractToolName, "file.read", "file.edit", "process.run", "program.source.generate" };
        check(descriptors[PluginScaffoldToolExtension.ProjectsToolName].PrerequisiteTools.SequenceEqual(developmentPrerequisites) &&
              descriptors[PluginScaffoldToolExtension.ToolName].PrerequisiteTools.SequenceEqual(developmentPrerequisites) &&
              descriptors[PluginScaffoldToolExtension.VerifyToolName].PrerequisiteTools.SequenceEqual(
                  new[] { PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ContractToolName,
                      "file.read", "file.edit", "process.run" }) &&
              descriptors.Values.All(item => item.ActivationTools.Count == 0),
            "plugin project, scaffold, and verify descriptors retain source research, mutation, and build schemas without gating existing operations");
        check(descriptors[PluginScaffoldToolExtension.ToolName].Description.Contains("targetDirectory must be absent", StringComparison.Ordinal) &&
              descriptors[PluginScaffoldToolExtension.ToolName].Description.Contains("do not call file.mkdir", StringComparison.Ordinal),
            "plugin scaffold advertises its atomic absent-target contract before the model can pre-create the package directory");
        check(descriptors[PluginScaffoldToolExtension.ProjectsToolName].Description.Contains("Examining an existing example does not choose it for modification", StringComparison.Ordinal) &&
              descriptors[PluginScaffoldToolExtension.ProjectsToolName].Description.Contains("use its own absent scaffold target and distinct ID/name", StringComparison.Ordinal) &&
              descriptors[PluginScaffoldToolExtension.ToolName].Description.Contains("A rejected scaffold does not establish a project", StringComparison.Ordinal) &&
              descriptors[PluginScaffoldToolExtension.ToolName].Description.Contains("do not mkdir its components or invent source/project files", StringComparison.Ordinal) &&
              descriptors[PluginScaffoldToolExtension.ToolName].Description.Contains("Preserve the scaffold manifest entryType and exact entry class namespace/name", StringComparison.Ordinal),
            "plugin discovery and scaffold schemas distinguish reference examples, separate new identities and atomic rejection recovery");
        var inventorySchema = descriptors[PluginScaffoldToolExtension.ProjectsToolName].InputSchema;
        var inventoryFields = inventorySchema.GetProperty("properties");
        check(inventorySchema.GetProperty("maxProperties").GetInt32() == 1 &&
              inventoryFields.GetProperty("root").GetProperty("description").GetString()!.Contains("Exact existing generated plugin root", StringComparison.Ordinal) &&
              inventoryFields.GetProperty("targetPath").GetProperty("description").GetString()!.Contains("mutually exclusive", StringComparison.Ordinal) &&
              inventoryFields.GetProperty("continuation").GetProperty("description").GetString()!.Contains("partial page grants scaffold admission", StringComparison.Ordinal),
            "plugin inventory schema states the three exclusive modes without changing its runtime path checks");
        var minimal = new MinimalDesktopPlugin();
        check(minimal.Panels.Count == 0 && minimal.ComposerActions.Count == 0 &&
              minimal.CreateMessagePresentation(new("message", new("fixture", null), "user", "text", DateTimeOffset.UtcNow)) is null,
            "the optional desktop base contributes no unrequested panel, action or message presentation");
        var uninitializedRejected = false;
        try { _ = minimal.TestHost; }
        catch (InvalidOperationException) { uninitializedRejected = true; }
        check(uninitializedRejected, "the minimal base never fabricates a host before initialization");
        var nullHostRejected = false;
        try { minimal.Initialize(null!); }
        catch (ArgumentNullException) { nullHostRejected = true; }
        minimal.Dispose();
        var disposedRejected = false;
        try { _ = minimal.TestHost; }
        catch (ObjectDisposedException) { disposedRejected = true; }
        check(nullHostRejected && disposedRejected, "minimal plugin host access rejects null initialization and disposal");
        var textPath = Path.Combine(root, "private-text", "value.txt");
        DesktopPluginFiles.WriteUtf8Atomically(textPath, "café", 5);
        check(new FileInfo(textPath).Length == 5 && DesktopPluginFiles.ReadUtf8(textPath, 5) == "café",
            "optional SDK storage creates the private directory and writes exact UTF-8 bytes without a BOM");
        var lastGoodHash = SHA256.HashData(File.ReadAllBytes(textPath));
        var oversizedRejected = false;
        try { DesktopPluginFiles.WriteUtf8Atomically(textPath, "ééé", 5); }
        catch (IOException) { oversizedRejected = true; }
        check(oversizedRejected && SHA256.HashData(File.ReadAllBytes(textPath)).SequenceEqual(lastGoodHash),
            "multibyte oversize saves preserve the last good file");
        var lockedRejected = false;
        using (var locked = new FileStream(textPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { DesktopPluginFiles.WriteUtf8Atomically(textPath, "next", 5); }
            catch (IOException) { lockedRejected = true; }
            catch (UnauthorizedAccessException) { lockedRejected = true; }
        }
        check(lockedRejected && SHA256.HashData(File.ReadAllBytes(textPath)).SequenceEqual(lastGoodHash) &&
              Directory.GetFiles(Path.GetDirectoryName(textPath)!, "*.tmp").Length == 0,
            "real replacement I/O failure preserves the prior file and cleans temporary output");
        var oversizedReadRejected = false;
        try { DesktopPluginFiles.ReadUtf8(textPath, 4); }
        catch (IOException) { oversizedReadRejected = true; }
        check(oversizedReadRejected, "bounded SDK reads reject oversize files before allocating their contents");
        File.WriteAllBytes(textPath, [0xff]);
        var invalidUtf8Rejected = false;
        try { DesktopPluginFiles.ReadUtf8(textPath, 5); }
        catch (System.Text.DecoderFallbackException) { invalidUtf8Rejected = true; }
        check(invalidUtf8Rejected, "SDK storage exposes invalid UTF-8 instead of silently replacing characters");
        DesktopPluginFiles.WriteUtf8Atomically(textPath, "", 0);
        check(new FileInfo(textPath).Length == 0 && DesktopPluginFiles.ReadUtf8(textPath, 0) == "",
            "empty private text is a valid persisted value");
        var target = Path.Combine(root, "generated-plugin");
        var projectsDescription = tool.Tools.Single(descriptor => descriptor.Name == PluginScaffoldToolExtension.ProjectsToolName).Description;
        check(projectsDescription.Contains("discovery", StringComparison.OrdinalIgnoreCase) &&
              projectsDescription.Contains("{\"root\":\"<exact returned root>\"}", StringComparison.Ordinal) &&
              projectsDescription.Contains("before reading, editing, building, verifying, or presenting", StringComparison.Ordinal) &&
              projectsDescription.Contains("identityScaffoldReady=true", StringComparison.Ordinal) &&
              projectsDescription.Contains("A partial or truncated page never establishes absence", StringComparison.Ordinal),
            "plugin project discovery describes the exact-root second call and incomplete-inventory boundary before the first call");
        var result = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ToolName,
            CerebralJson.Element(new { pluginId = "example.generated", name = "Generated example", targetDirectory = target, component = "cross-surface" })),
            new(profile, "session", "job", root), CancellationToken.None);
        check(!result.Success && result.Content.Contains("plugin.projects", StringComparison.Ordinal) && !Directory.Exists(target),
            "plugin scaffold requires a same-assignment inventory before creating another source root");
        var initialInventory = await routed.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { })), new(profile, "session", "job", root), CancellationToken.None);
        using (var initialInventoryDocument = JsonDocument.Parse(initialInventory.Content))
        {
            var initialNext = initialInventoryDocument.RootElement.GetProperty("next").GetString()!;
            check(initialInventory.Success && !initialInventory.Truncated &&
                  initialNext.Contains("Discovery only: no project or build is selected yet", StringComparison.Ordinal) &&
                  initialNext.Contains("{\"root\":\"<exact returned root>\"}", StringComparison.Ordinal) &&
                  initialNext.Contains("If limited is true or this result is truncated", StringComparison.Ordinal) &&
                  initialNext.Contains("do not scaffold from that absence", StringComparison.Ordinal),
                "broad plugin discovery requires an exact listed-root second call and never treats an incomplete absence as authoritative");
        }
        var invalidParentInventory = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { root })), new(profile, "session", "parent-inventory", root), CancellationToken.None);
        check(!invalidParentInventory.Success && invalidParentInventory.Content.Contains("omit root", StringComparison.Ordinal) &&
              invalidParentInventory.Content.Contains("PLUGIN.md", StringComparison.Ordinal),
            "a parent-folder inventory rejection explains the exact new-plugin call without widening path admission");
        foreach (var relativeRoot in new[] { "{}", "generated-plugin", "." })
        {
            var invalidRoot = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ProjectsToolName,
                CerebralJson.Element(new { root = relativeRoot })), new(profile, "session", "bad-root", root), CancellationToken.None);
            check(!invalidRoot.Success && invalidRoot.Evidence is { Outcome: ToolExecutionOutcome.Rejected } &&
                  invalidRoot.Content.Contains("arguments={}", StringComparison.Ordinal) &&
                  invalidRoot.Content.Contains("omit root key", StringComparison.Ordinal),
                "relative plugin inventory input is rejected with the empty-object discovery call: " + relativeRoot);
        }
        var mixedInventory = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { root, targetPath = root })), new(profile, "session", "mixed-inventory", root), CancellationToken.None);
        check(!mixedInventory.Success && mixedInventory.Evidence is { Outcome: ToolExecutionOutcome.Rejected },
            "runtime inventory admission rejects root and targetPath together even when the caller bypasses schema validation");
        var malformedScaffold = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ToolName,
            CerebralJson.Element(new { name = "Generated example", targetDirectory = target, component = "cross-surface" })),
            new(profile, "session", "job", root), CancellationToken.None);
        check(!malformedScaffold.Success && !Directory.Exists(target), "malformed scaffolding produces no source mutation");
        result = await routed.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ToolName,
            CerebralJson.Element(new { pluginId = "example.generated", name = "Generated example", targetDirectory = target, component = "cross-surface" })),
            new(profile, "session", "job", root), CancellationToken.None);

        check(result.Success && result.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, ProviderId: "cerebral.plugin-sdk" },
            "a corrected scaffold preserves the preceding inventory decision and returns host-attributed success evidence");
        check(result.RequiredSuccessfulTools is not null && result.RequiredSuccessfulTools.SequenceEqual(new[] { "plugin.verify", "plugin.present" }),
            "desktop scaffolding declares verification and presentation as provider-owned completion milestones");
        check(File.Exists(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs")) &&
              File.Exists(Path.Combine(target, "Service", "GeneratedServicePlugin.cs")) &&
              File.Exists(Path.Combine(target, "sdk", "desktop", "Cerebral.Desktop.Plugins.dll")) &&
              File.Exists(Path.Combine(target, "sdk", "service", "Cerebral.Core.dll")),
            "cross-surface scaffold publishes source and exact SDK copies");
        using (var scaffoldDocument = JsonDocument.Parse(result.Content))
        {
            var response = scaffoldDocument.RootElement;
            check(response.GetProperty("guide").GetString() == Path.Combine(target, "PLUGIN.md") &&
                   response.GetProperty("desktop").GetProperty("build").GetProperty("executableId").GetString() == "dotnet" &&
                   response.GetProperty("desktopAuthoringContract").GetString() == ToolModelProjection.DesktopPluginAuthoringContractId &&
                   response.GetProperty("next").GetString()!.Contains("only relevant", StringComparison.Ordinal),
                "scaffolding returns its exact guide and runnable build without requiring optional SDK features");
        }
        check(Encoding.UTF8.GetByteCount(ToolModelProjection.DesktopPluginAuthoringSnapshot) <= 4096 &&
              ToolModelProjection.DesktopPluginAuthoringSnapshot.Contains("DesktopPluginBase", StringComparison.Ordinal) &&
              ToolModelProjection.DesktopPluginAuthoringSnapshot.Contains("public parameterless constructor", StringComparison.Ordinal) &&
              ToolModelProjection.DesktopPluginAuthoringSnapshot.Contains("inherit Initialize and Dispose", StringComparison.Ordinal) &&
              ToolModelProjection.DesktopPluginAuthoringSnapshot.Contains("corresponding base method", StringComparison.Ordinal) &&
              ToolModelProjection.DesktopPluginAuthoringSnapshot.Contains("Func<FrameworkElement>", StringComparison.Ordinal) &&
              ToolModelProjection.DesktopPluginAuthoringSnapshot.Contains("Environment.DataDirectory", StringComparison.Ordinal) &&
              ToolModelProjection.DesktopPluginAuthoringSnapshot.Contains("ReadUtf8(string path, int maximumBytes)", StringComparison.Ordinal) &&
              ToolModelProjection.DesktopPluginAuthoringSnapshot.Contains("WriteUtf8Atomically(string path, string text, int maximumBytes)", StringComparison.Ordinal) &&
              ToolModelProjection.DesktopPluginAuthoringSnapshot.Contains("Preserve the scaffold manifest entryType and exact entry class namespace/name", StringComparison.Ordinal) &&
              ToolModelProjection.DesktopPluginAuthoringSnapshot.Contains("update both source and source manifest, then rebuild", StringComparison.Ordinal) &&
              ToolModelProjection.DesktopPluginAuthoringSnapshot.Contains("existing projects are reference examples only", StringComparison.Ordinal) &&
              !ToolModelProjection.DesktopPluginAuthoringSnapshot.Contains("Pocket", StringComparison.OrdinalIgnoreCase),
            "the host-owned desktop authoring snapshot is compact, generic, and carries the exact lifecycle, view, environment, and storage invariants");
        var crossSurfaceGuide = await File.ReadAllTextAsync(Path.Combine(target, "PLUGIN.md"));
        check(crossSurfaceGuide.Contains("Build both returned component envelopes", StringComparison.Ordinal) &&
              crossSurfaceGuide.Contains("plugin.contract", StringComparison.Ordinal) &&
              crossSurfaceGuide.Contains("Service staging, activation, restart, testing, deactivation, and removal", StringComparison.Ordinal) &&
              crossSurfaceGuide.Contains("plugin.present", StringComparison.Ordinal) &&
              !crossSurfaceGuide.Contains("StatusBar", StringComparison.Ordinal) &&
              !crossSurfaceGuide.Contains("GetDraft", StringComparison.Ordinal) &&
              !crossSurfaceGuide.Contains("SetResponseBalanceAsync", StringComparison.Ordinal),
            "cross-surface guidance keeps both component lifecycles without embedding the optional desktop facet catalog");
        var unbuilt = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(!unbuilt.Success && unbuilt.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
              unbuilt.Content.Contains("Build required before verification", StringComparison.Ordinal) &&
              unbuilt.Content.Contains("process.run", StringComparison.Ordinal),
            "an unbuilt plugin receives an actionable build instruction without verification credit");
        var baseContract = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ContractToolName,
            CerebralJson.Element(new { topic = "desktop-base" })), new(profile, "session", "job", root), CancellationToken.None);
        using (var baseDocument = JsonDocument.Parse(baseContract.Content))
            check(baseContract.Success && baseDocument.RootElement.GetProperty("reference").GetProperty("signatures").GetString()!
                      .Contains("DesktopPanel(string Id, string Title, Func<FrameworkElement> CreateView)", StringComparison.Ordinal) &&
                  typeof(DesktopPanel).GetConstructors().Any(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType)
                      .SequenceEqual(new[] { typeof(string), typeof(string), typeof(Func<System.Windows.FrameworkElement>) })),
                "the small base contract supplies the real WPF sidebar panel constructor");
        var draftContract = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ContractToolName,
            CerebralJson.Element(new { topic = "draft" })), new(profile, "session", "job", root), CancellationToken.None);
        using (var draftDocument = JsonDocument.Parse(draftContract.Content))
        {
            var signatures = draftDocument.RootElement.GetProperty("reference").GetProperty("signatures").GetString()!;
            check(draftContract.Success && signatures.Contains("DesktopDraftSnapshot GetDraft();", StringComparison.Ordinal) &&
                  typeof(IDesktopPluginHost).GetMethod("GetDraft")?.ReturnType == typeof(DesktopDraftSnapshot) &&
                  !signatures.Contains("Submit", StringComparison.Ordinal) && !signatures.Contains("event", StringComparison.Ordinal) && signatures.Length < 1000,
                "read-only draft lookup publishes only the needed exact host shapes");
        }
        var storageContract = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ContractToolName,
            CerebralJson.Element(new { topic = "storage" })), new(profile, "session", "job", root), CancellationToken.None);
        using (var storageDocument = JsonDocument.Parse(storageContract.Content))
        {
            var reference = storageDocument.RootElement.GetProperty("reference");
            var signatures = reference.GetProperty("signatures").GetString()!;
            check(storageContract.Success &&
                  typeof(IDesktopPluginEnvironment).GetProperty("DataDirectory")?.PropertyType == typeof(string) &&
                  typeof(IDesktopPluginEnvironment).GetProperty("Surface")?.PropertyType == typeof(DesktopPluginSurface) &&
                  signatures.Contains("string DataDirectory { get; }", StringComparison.Ordinal) &&
                  reference.GetProperty("example").GetString()!.Contains("pluginHost as IDesktopPluginEnvironment", StringComparison.Ordinal) &&
                  !signatures.Contains("IDesktopProductEnvironment", StringComparison.Ordinal) && signatures.Length < 1000,
                "private storage lookup publishes the actual optional host interface without unrelated product paths");
            var summary = reference.GetProperty("summary").GetString()!;
            check(summary.Contains(ToolModelProjection.DesktopPluginStorageGuidance, StringComparison.Ordinal) &&
                  summary.Contains("Commit a save to disk before publishing the remembered value in state or UI, or reporting success.", StringComparison.Ordinal) &&
                  summary.Contains("A failed save keeps the last confirmed value and displays the actual error", StringComparison.Ordinal) &&
                  summary.Contains("do not clear that error or report success", StringComparison.Ordinal) &&
                  summary.Contains("Only while reading initial saved state, treat FileNotFoundException and DirectoryNotFoundException as normal empty first-use state", StringComparison.Ordinal) &&
                  summary.Contains("Every write exception is a failed save", StringComparison.Ordinal) &&
                  summary.Contains("never swallow it as first use or report Ready or success", StringComparison.Ordinal) &&
                  summary.Contains("Keep other read errors visible and preserve confirmed state", StringComparison.Ordinal),
                "the actual published storage topic carries the shared commit, failed-save preservation and first-use/read-error guidance");
        }
        var generatedDesktopSource = await File.ReadAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"));
        var generatedServiceSource = await File.ReadAllTextAsync(Path.Combine(target, "Service", "GeneratedServicePlugin.cs"));
        using (var storageDocument = JsonDocument.Parse(storageContract.Content))
        {
            var members = storageDocument.RootElement.GetProperty("reference").GetProperty("example").GetString()!;
            await File.WriteAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"),
                "using System.IO;\n" + generatedDesktopSource.Replace("\n}\n", "\n" + members + "\n}\n", StringComparison.Ordinal));
        }
        // A reused project can define this name in its own namespace. Selecting that helper is a
        // compiler error here, so the published example must resolve to the copied public SDK.
        var shadowHelper = Path.Combine(target, "Desktop", "DesktopPluginFiles.cs");
        await File.WriteAllTextAsync(shadowHelper, """
            namespace Cerebral.GeneratedPlugin;
            public static class DesktopPluginFiles
            {
                [System.Obsolete("Fixture local helper must not be selected.", true)]
                public static void WriteUtf8Atomically(string path, string text, int maximumBytes) => throw new System.NotSupportedException();
                [System.Obsolete("Fixture local helper must not be selected.", true)]
                public static string ReadUtf8(string path, int maximumBytes) => throw new System.NotSupportedException();
            }
            """);
        var storageBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        check(storageBuild.ExitCode == 0,
            "published storage members resolve to the copied SDK even when a local helper shadows its short name" + storageBuild.Failure);
        File.Delete(shadowHelper);
        await File.WriteAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"), generatedDesktopSource);
        check(!generatedDesktopSource.Contains("PluginId + \".status\"", StringComparison.Ordinal) &&
              generatedDesktopSource.Contains("GeneratedDesktopPlugin : DesktopPluginBase", StringComparison.Ordinal) &&
              !generatedDesktopSource.Contains("Initialize(", StringComparison.Ordinal) &&
              !generatedDesktopSource.Contains("Dispose(", StringComparison.Ordinal) &&
              !generatedDesktopSource.Contains("Panels", StringComparison.Ordinal) &&
              !generatedDesktopSource.Contains("ComposerActions", StringComparison.Ordinal) &&
              generatedServiceSource.Contains("PluginId + \".status\"", StringComparison.Ordinal) &&
              generatedServiceSource.Contains("PluginId + \".agent-status\"", StringComparison.Ordinal),
            "desktop scaffolds inherit the base lifecycle and empty contributions while an explicit service component exposes only its real contract names");
        var alternateRoot = Path.Combine(root, "alternate-plugin");
        var alternateDesktop = Path.Combine(alternateRoot, "Desktop");
        Directory.CreateDirectory(alternateDesktop);
        await File.WriteAllTextAsync(Path.Combine(alternateRoot, "PLUGIN.md"), "# Alternate plugin\n");
        await File.WriteAllTextAsync(Path.Combine(alternateDesktop, "desktop-plugin.json"),
            "{\"id\":\"example.alternate\",\"name\":\"Alternate example\",\"version\":\"1.0.0\"}");
        await File.WriteAllTextAsync(Path.Combine(alternateDesktop, "Alternate.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        var broadInventory = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { })), new(profile, "session", "job", root), CancellationToken.None);
        string selectedRoot;
        string selectedId;
        using (var broadInventoryDocument = JsonDocument.Parse(broadInventory.Content))
        {
            var projects = broadInventoryDocument.RootElement.GetProperty("projects").EnumerateArray().ToArray();
            var matching = projects.Skip(1).First();
            selectedRoot = matching.GetProperty("root").GetString()!;
            selectedId = matching.GetProperty("id").GetString()!;
            check(broadInventory.Success && !broadInventory.Truncated &&
                  !broadInventoryDocument.RootElement.GetProperty("limited").GetBoolean() && projects.Length >= 2 &&
                  !string.IsNullOrWhiteSpace(selectedId) && Path.IsPathFullyQualified(selectedRoot) &&
                  broadInventoryDocument.RootElement.GetProperty("next").GetString()!
                      .Contains("copy its absolute root verbatim", StringComparison.Ordinal),
                "unlimited, untruncated broad discovery exposes a non-first exact root without selecting or inferring its build coordinates");
        }
        var inventory = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { root = selectedRoot })), new(profile, "session", "job", root), CancellationToken.None);
        using (var inventoryDocument = JsonDocument.Parse(inventory.Content))
            check(inventory.Success && inventory.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, State: "inventoried" } &&
                   inventoryDocument.RootElement.GetProperty("count").GetInt32() == 1 &&
                   inventoryDocument.RootElement.GetProperty("scannedDirectories").GetInt32() == 1 &&
                   inventoryDocument.RootElement.GetProperty("next").GetString()!
                       .Contains("exact-root inspection revalidated the selected project", StringComparison.Ordinal) &&
                   inventoryDocument.RootElement.GetProperty("next").GetString()!
                       .Contains("authoritative process.run", StringComparison.Ordinal) &&
                   inventoryDocument.RootElement.GetProperty("projects").EnumerateArray().Any(item =>
                       string.Equals(item.GetProperty("root").GetString(), selectedRoot, StringComparison.OrdinalIgnoreCase) &&
                       item.GetProperty("id").GetString() == selectedId &&
                      item.GetProperty("components").EnumerateArray().All(component =>
                          component.GetProperty("build").GetProperty("executableId").GetString() == "dotnet" &&
                          Path.IsPathFullyQualified(component.GetProperty("build").GetProperty("workingDirectory").GetString()!) &&
                          component.GetProperty("build").GetProperty("arguments").EnumerateArray().Any(argument =>
                              argument.GetString()!.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))),
                "plugin project inventory returns an exact runnable build envelope before another scaffold is attempted");
        using (var identityGuidanceDocument = JsonDocument.Parse(inventory.Content))
        {
            var next = identityGuidanceDocument.RootElement.GetProperty("next").GetString()!;
            check(next.Contains("examining an existing example does not choose it for modification or make it the requested plugin", StringComparison.Ordinal) &&
                  next.Contains("If this inspected source is unrelated to the requested new named plugin", StringComparison.Ordinal) &&
                  next.Contains("keep this source as a reference and scaffold the requested plugin's own absent target with distinct ID/name", StringComparison.Ordinal) &&
                  !next.Contains("For a new named plugin when existing plugins must be preserved, keep this source as a reference", StringComparison.Ordinal) &&
                  next.Contains("complete, untruncated discovery", StringComparison.Ordinal),
                "actual exact-root inspection guidance keeps an unrelated inspected source separate from a new named plugin");
        }
        var generatedInventory = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        using (var inventoryDocument = JsonDocument.Parse(generatedInventory.Content))
            check(generatedInventory.Success &&
                  inventoryDocument.RootElement.GetProperty("desktopAuthoringContract").GetString() ==
                      ToolModelProjection.DesktopPluginAuthoringContractId &&
                  inventoryDocument.RootElement.GetProperty("projects")[0]
                .GetProperty("components").EnumerateArray().All(component =>
                    component.GetProperty("source").GetString() == Path.Combine(target,
                        component.GetProperty("component").GetString() == "desktop" ? "Desktop" : "Service",
                        component.GetProperty("component").GetString() == "desktop" ? "GeneratedDesktopPlugin.cs" : "GeneratedServicePlugin.cs")),
                "project inventory supplies the exact desktop and service source coordinates returned by scaffolding");
        var sourceManifestPath = Path.Combine(target, "Desktop", "desktop-plugin.json");
        var validSourceManifest = await File.ReadAllTextAsync(sourceManifestPath);
        await File.WriteAllTextAsync(sourceManifestPath, "{\"name\":\"invalid\nmanifest\"}");
        try
        {
            var repairInventory = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ProjectsToolName,
                CerebralJson.Element(new { root = target })), new(profile, "session", "source-json", root), CancellationToken.None);
            using var repairDocument = JsonDocument.Parse(repairInventory.Content);
            var repairComponent = repairDocument.RootElement.GetProperty("projects")[0].GetProperty("components")
                .EnumerateArray().Single(component => component.GetProperty("component").GetString() == "desktop");
            check(repairInventory.Success && !repairInventory.Truncated &&
                  repairComponent.GetProperty("source").GetString() == Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs") &&
                  repairComponent.GetProperty("build").GetProperty("executableId").GetString() == "dotnet" &&
                  repairComponent.GetProperty("id").ValueKind == JsonValueKind.Null &&
                  repairComponent.GetProperty("error").GetString()!.Contains("JSON", StringComparison.OrdinalIgnoreCase),
                "invalid source metadata preserves scoped repair geometry without inventing package identity or verification");
        }
        finally { await File.WriteAllTextAsync(sourceManifestPath, validSourceManifest); }
        var densityRoot = Path.Combine(root, "discovery-density");
        var densityProjects = Enumerable.Range(0, 30).Select(index => Path.Combine(densityRoot, "plugin-" + index)).ToArray();
        foreach (var densityProject in densityProjects)
        {
            var componentDirectory = Path.Combine(densityProject, "Desktop");
            Directory.CreateDirectory(componentDirectory);
            await File.WriteAllTextAsync(Path.Combine(densityProject, "PLUGIN.md"), "# Fixture\n");
            await File.WriteAllTextAsync(Path.Combine(componentDirectory, "desktop-plugin.json"),
                JsonSerializer.Serialize(new { id = "density." + Path.GetFileName(densityProject), name = Path.GetFileName(densityProject), version = "1.0.0" }));
            await File.WriteAllTextAsync(Path.Combine(componentDirectory, "Fixture.csproj"), "<Project />");
        }
        var denseInventory = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { })), new(profile, "session", "dense-discovery", root), CancellationToken.None);
        using (var denseDocument = JsonDocument.Parse(denseInventory.Content))
        {
            var rows = denseDocument.RootElement.GetProperty("projects").EnumerateArray().ToArray();
            check(denseInventory.Success && !denseInventory.Truncated &&
                  denseDocument.RootElement.GetProperty("discoverySummaryOnly").GetBoolean() &&
                  !denseDocument.RootElement.GetProperty("limited").GetBoolean() &&
                  densityProjects.All(path => rows.Any(row => row.GetProperty("root").GetString() == path)) &&
                  rows.SelectMany(row => row.GetProperty("components").EnumerateArray()).All(component =>
                      !component.TryGetProperty("build", out _) && !component.TryGetProperty("source", out _) && !component.TryGetProperty("manifest", out _)),
                "large complete discovery preserves every exact root without repeating build recipes or truncating the catalog");
        }
        var contract = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ContractToolName,
            CerebralJson.Element(new { topic = "mount-view" })), new(profile, "session", "job", root), CancellationToken.None);
        check(contract.Success && contract.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, State: "contract" } &&
               contract.Content.Contains("IDesktopMountPlugin", StringComparison.Ordinal) &&
               contract.Content.Contains("DesktopMountSlots.StatusBar", StringComparison.Ordinal) &&
               contract.Content.Contains("DesktopMountApplicability", StringComparison.Ordinal) &&
               contract.Content.Contains("DesktopMountSurfaces.Orchestrator", StringComparison.Ordinal) &&
               contract.Content.Contains("IDesktopConversationSettingsHost", StringComparison.Ordinal) &&
               contract.Content.Contains("forbiddenGuesses", StringComparison.Ordinal) &&
               !contract.Content.Contains("using Cerebral.Desktop.State;", StringComparison.Ordinal),
            "plugin contract lookup returns bounded compile-ready public mount shapes with an explicit Orchestrator scope and no invented SDK namespaces");
        using (var mountDocument = JsonDocument.Parse(contract.Content))
            await File.WriteAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"),
                mountDocument.RootElement.GetProperty("reference").GetProperty("example").GetString()!);
        var mountBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        check(mountBuild.ExitCode == 0,
            "the published Orchestrator-scoped mount example compiles against the copied public SDK" + mountBuild.Failure);
        await File.WriteAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"), generatedDesktopSource);
        var starterContract = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ContractToolName,
            CerebralJson.Element(new { topic = "new-conversation" })), new(profile, "session", "job", root), CancellationToken.None);
        using (var starterDocument = JsonDocument.Parse(starterContract.Content))
        {
            var starterExample = starterDocument.RootElement.GetProperty("reference").GetProperty("example").GetString()!;
            check(starterContract.Success && starterExample.Contains("DesktopMountSlots.NewConversationStart", StringComparison.Ordinal) &&
                  starterExample.Contains("ReplaceDraftAsync", StringComparison.Ordinal) &&
                  starterExample.Contains("Panels { get; } = [];", StringComparison.Ordinal) &&
                  !starterExample.Contains("Cerebral.Desktop.State", StringComparison.Ordinal),
                "new-conversation contract supplies the exact draft-replacement landing-page pattern");
            await File.WriteAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"), starterExample);
        }
        var starterBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        check(starterBuild.ExitCode == 0,
            "the published new-conversation contract example compiles against the copied public SDK" + starterBuild.Failure);
        await File.WriteAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"), generatedDesktopSource);
        var conversationSettingsLookup = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ContractToolName,
            CerebralJson.Element(new { topic = "conversation-settings" })), new(profile, "session", "job", root), CancellationToken.None);
        string conversationSettingsExample;
        using (var settingsDocument = JsonDocument.Parse(conversationSettingsLookup.Content))
        {
            var reference = settingsDocument.RootElement.GetProperty("reference");
            conversationSettingsExample = reference.GetProperty("example").GetString()!;
            var signatures = reference.GetProperty("signatures").GetString()!;
            check(conversationSettingsLookup.Success && conversationSettingsLookup.Evidence is
                { Outcome: ToolExecutionOutcome.Succeeded, State: "contract" } &&
                descriptors[PluginScaffoldToolExtension.ContractToolName].InputSchema.GetProperty("properties").GetProperty("topic")
                    .GetProperty("enum").EnumerateArray().Any(value => value.GetString() == "conversation-settings") &&
                Encoding.UTF8.GetByteCount(conversationSettingsLookup.Content) <= 16 * 1024,
                "the exact per-chat settings topic is discoverable and returns a bounded public contract");
            check(signatures.Contains(nameof(IDesktopConversationSettingsPlugin), StringComparison.Ordinal) &&
                signatures.Contains(nameof(DesktopConversationSettingsContext.AvailabilityToken), StringComparison.Ordinal) &&
                signatures.Contains(nameof(IDesktopConversationSettingsEditor.SaveAsync), StringComparison.Ordinal) &&
                signatures.Contains(nameof(IDesktopConversationSettingsEditor.Cancel), StringComparison.Ordinal) &&
                !signatures.Contains("ConversationPropertiesSnapshot", StringComparison.Ordinal) &&
                !signatures.Contains("ExecuteServiceCommand", StringComparison.Ordinal),
                "the optional settings contract exposes explicit target/lifetime/editor methods without Worker DTOs or generic commands");
            check(reference.GetProperty("summary").GetString()!.Contains("may differ from Host.GetConversation()", StringComparison.Ordinal) &&
                reference.GetProperty("summary").GetString()!.Contains("never automatically replay", StringComparison.Ordinal) &&
                conversationSettingsExample.Contains("DesktopPluginFiles.WriteUtf8Atomically", StringComparison.Ordinal),
                "per-chat settings guidance keeps sidebar targeting, uncertain-write recovery and real bounded storage explicit");
        }
        var navigationLookup = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ContractToolName,
            CerebralJson.Element(new { topic = "settings-navigation" })),
            new(profile, "session", "job", root), CancellationToken.None);
        string navigationExample;
        using (var navigationDocument = JsonDocument.Parse(navigationLookup.Content))
        {
            var reference = navigationDocument.RootElement.GetProperty("reference");
            navigationExample = reference.GetProperty("example").GetString()!;
            var signatures = reference.GetProperty("signatures").GetString()!;
            var summary = reference.GetProperty("summary").GetString()!;
            check(navigationLookup.Success && navigationLookup.Evidence is
                { Outcome: ToolExecutionOutcome.Succeeded, State: "contract" } &&
                descriptors[PluginScaffoldToolExtension.ContractToolName].InputSchema.GetProperty("properties")
                    .GetProperty("topic").GetProperty("enum").EnumerateArray()
                    .Any(value => value.GetString() == "settings-navigation") &&
                Encoding.UTF8.GetByteCount(navigationLookup.Content) <= 16 * 1024,
                "the plugin-owned Configuration navigation topic is discoverable and bounded");
            check(signatures.Contains(nameof(IDesktopPluginSettingsNavigationHost), StringComparison.Ordinal) &&
                signatures.Contains(nameof(IDesktopConfigurationPlugin), StringComparison.Ordinal) &&
                signatures.Contains(nameof(DesktopSettingsNavigationAvailability), StringComparison.Ordinal) &&
                summary.Contains("visible and ready", StringComparison.Ordinal) &&
                summary.Contains("never automatically replay", StringComparison.Ordinal) &&
                navigationExample.Contains("GetSettingsNavigationAvailability", StringComparison.Ordinal) &&
                navigationExample.Contains("DesktopOperationState.Completed", StringComparison.Ordinal),
                "settings navigation guidance distinguishes scoped availability, confirmed completion and uncertain effect");
        }
        await File.WriteAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"), navigationExample);
        var navigationBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        check(navigationBuild.ExitCode == 0,
            "the published settings navigation example compiles against the copied public SDK" + navigationBuild.Failure);
        await File.WriteAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"), generatedDesktopSource);
        check(DesktopPluginContract.Version == 1 && typeof(IDesktopPlugin).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(new[] { "CreateMessagePresentation", "Initialize", "get_ComposerActions", "get_Panels" }.OrderBy(name => name, StringComparer.Ordinal)) &&
            !typeof(DesktopPluginBase).GetInterfaces().Contains(typeof(IDesktopConversationSettingsPlugin)),
            "per-chat settings are additive and do not add required members to API-v1 plugins or their base class");
        using (var baseDocument = JsonDocument.Parse(baseContract.Content))
            await File.WriteAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"),
                baseDocument.RootElement.GetProperty("reference").GetProperty("example").GetString()!);
        var baseBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        check(baseBuild.ExitCode == 0,
            "the published minimal sidebar example compiles against the copied public SDK" + baseBuild.Failure);
        await File.WriteAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"), generatedDesktopSource);
        using (var draftDocument = JsonDocument.Parse(draftContract.Content))
            await File.WriteAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"),
                draftDocument.RootElement.GetProperty("reference").GetProperty("example").GetString()!);
        var draftBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        check(draftBuild.ExitCode == 0,
            "the published read-only draft panel compiles using only the public host and its own controls" + draftBuild.Failure);
        await File.WriteAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"), generatedDesktopSource);
        using (var desktop = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(target, "Desktop", "desktop-plugin.json"))))
            check(desktop.RootElement.GetProperty("id").GetString() == "example.generated" &&
                  desktop.RootElement.GetProperty("contract").GetString() == NativeDesktopPluginPackage.ContractId,
                "desktop scaffold carries the requested stable identity and contract");
        using (var service = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(target, "Service", "service-plugin.json"))))
            check(service.RootElement.GetProperty("id").GetString() == "example.generated" &&
                  service.RootElement.GetProperty("contract").GetString() == ServicePluginContract.Id,
                "service scaffold carries the same identity and service contract");
        var desktopBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        check(desktopBuild.ExitCode == 0 && File.Exists(Path.Combine(target, "Desktop", "bin", "Release", "net10.0-windows", "desktop-plugin.json")),
            "generated desktop scaffold compiles against the copied public SDK" + desktopBuild.Failure);
        var incomplete = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(!incomplete.Success && incomplete.Content.Contains("inert scaffold", StringComparison.Ordinal),
            "plugin verification rejects a built but untouched desktop scaffold");
        var builtManifest = Path.Combine(target, "Desktop", "bin", "Release", "net10.0-windows", "desktop-plugin.json");
        foreach (var wrongRoot in new[] { Path.Combine(target, "Desktop"), Path.GetDirectoryName(builtManifest)! })
        {
            var wrongRootResult = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
                CerebralJson.Element(new { root = wrongRoot })), new(profile, "session", "job", root), CancellationToken.None);
            check(!wrongRootResult.Success && wrongRootResult.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
                  wrongRootResult.Content.Contains("Retry plugin.verify with root=" + Path.GetFullPath(target), StringComparison.Ordinal),
                "verification of a component/output directory rejects it with the exact admitted project root");
        }
        var inertPresentationRejected = false;
        try { PluginScaffoldToolExtension.ValidateDesktopPresentation(profile, builtManifest, false); }
        catch (InvalidDataException error) { inertPresentationRejected = error.Message.Contains("inert scaffold", StringComparison.Ordinal); }
        check(inertPresentationRejected, "generated package presentation repeats the inert-source check before review");
        var spoofedInertSource = generatedDesktopSource.Replace(
            "public const string PluginId",
            "public const string ClaimedContribution = \"IDesktopMountPlugin\"; // IDesktopConfigurationPlugin\n    public const string PluginId",
            StringComparison.Ordinal);
        var spoofedInertSourcePath = Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs");
        await File.WriteAllTextAsync(spoofedInertSourcePath, spoofedInertSource);
        var spoofedInertBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        var spoofedInert = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        var spoofedPresentationRejected = false;
        try { PluginScaffoldToolExtension.ValidateDesktopPresentation(profile, builtManifest, false); }
        catch (InvalidDataException error) { spoofedPresentationRejected = error.Message.Contains("inert scaffold", StringComparison.Ordinal); }
        check(spoofedInertSource != generatedDesktopSource && spoofedInertBuild.ExitCode == 0 &&
              !spoofedInert.Success && spoofedInert.Content.Contains("inert scaffold", StringComparison.Ordinal) &&
              spoofedPresentationRejected,
            "compiled-entry verification rejects inert scaffolds whose comments and strings only name contribution interfaces" +
            spoofedInertBuild.Failure);
        var serviceBuild = await BuildAsync(Path.Combine(target, "Service", "Pegasus.Plugin.example.generated.Service.csproj"));
        check(serviceBuild.ExitCode == 0 && File.Exists(Path.Combine(target, "Service", "bin", "Release", "net10.0", "service-plugin.json")),
            "generated service scaffold compiles against the copied public SDK" + serviceBuild.Failure);
        var stillInert = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(!stillInert.Success && stillInert.Content.Contains("inert scaffold", StringComparison.Ordinal),
            "a complete cross-surface build still requires an implemented desktop contribution");
        var covariantPanelsSource = """
            using System.Windows;
            using System.Windows.Controls;
            using Cerebral.Desktop.Plugins;

            namespace Cerebral.GeneratedPlugin;

            public sealed class GeneratedDesktopPlugin : DesktopPluginBase
            {
                public override DesktopPanel[] Panels =>
                    [new("main", "Covariant panel", () => new TextBlock { Text = "Covariant panel ready" })];
            }
            """;
        await File.WriteAllTextAsync(spoofedInertSourcePath, covariantPanelsSource);
        var covariantPanelsBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        var covariantPanels = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(covariantPanelsBuild.ExitCode == 0 && covariantPanels.Success,
            "compiled-entry verification accepts a real SDK Panels override with a covariant SDK-element array" +
            covariantPanelsBuild.Failure + ": " + covariantPanels.Content);
        PluginScaffoldToolExtension.ValidateDesktopPresentation(profile, builtManifest, false);
        check(true, "presentation accepts the same compiled covariant Panels override without loading plugin code");

        async Task CheckCompiledContribution(string source, bool expected, string label)
        {
            await File.WriteAllTextAsync(spoofedInertSourcePath, source);
            var build = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
            check(build.ExitCode == 0, label + " compiles against the copied SDK" + build.Failure);
            var verification = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
                CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
            var presentationAccepted = false;
            try
            {
                PluginScaffoldToolExtension.ValidateDesktopPresentation(profile, builtManifest, false);
                presentationAccepted = true;
            }
            catch (InvalidDataException) { }
            check(verification.Success == expected && presentationAccepted == expected &&
                  (expected || verification.Content.Contains("inert scaffold", StringComparison.Ordinal)),
                label + " preserves the same compiled contribution decision in verify and presentation");
        }

        await CheckCompiledContribution(conversationSettingsExample, true,
            "the published lazy per-chat settings example against the exact copied SDK");
        await CheckCompiledContribution("""
            using Cerebral.Desktop.Plugins;
            namespace Cerebral.GeneratedPlugin;
            public interface IDesktopConversationSettingsPlugin { }
            public sealed class GeneratedDesktopPlugin : DesktopPluginBase, IDesktopConversationSettingsPlugin { }
            """, false, "a local same-name per-chat settings interface without the actual SDK identity");

        await CheckCompiledContribution(covariantPanelsSource.Replace(
            "public override DesktopPanel[] Panels",
            "public new DesktopPanel[] Panels", StringComparison.Ordinal), false,
            "a hidden SDK-element array property");
        await CheckCompiledContribution(covariantPanelsSource.Replace(
            "public sealed class GeneratedDesktopPlugin",
            "public class GeneratedDesktopPlugin", StringComparison.Ordinal).Replace(
            "public override DesktopPanel[] Panels",
            "public new virtual DesktopPanel[] Panels", StringComparison.Ordinal), false,
            "a new virtual SDK-element array property");
        await CheckCompiledContribution("""
            using System.Windows.Controls;
            using Cerebral.Desktop.Plugins;
            namespace Cerebral.GeneratedPlugin;
            public class LocalHiddenBase : DesktopPluginBase
            {
                public new virtual DesktopPanel[] Panels => [];
            }
            public sealed class GeneratedDesktopPlugin : LocalHiddenBase
            {
                public override DesktopPanel[] Panels =>
                    [new("main", "Hidden local slot", () => new TextBlock())];
            }
            """, false, "an override of a hidden local array slot");
        await CheckCompiledContribution("""
            using System.Windows.Controls;
            using Cerebral.Desktop.Plugins;
            namespace Cerebral.GeneratedPlugin;
            public class LocalContributingBase : DesktopPluginBase
            {
                public override DesktopPanel[] Panels =>
                    [new("main", "Inherited panel", () => new TextBlock())];
            }
            public sealed class GeneratedDesktopPlugin : LocalContributingBase;
            """, true, "an inherited real SDK covariant array override");
        await CheckCompiledContribution("""
            using Cerebral.Desktop.Plugins;
            namespace Cerebral.GeneratedPlugin;
            public sealed class GeneratedDesktopPlugin : DesktopPluginBase
            {
                public override DesktopAction[] ComposerActions =>
                    [new("action", "Covariant action", _ => Task.CompletedTask)];
            }
            """, true, "a real SDK ComposerActions covariant array override");
        await CheckCompiledContribution("""
            using Cerebral.Desktop.Plugins;
            namespace Cerebral.GeneratedPlugin
            {
                public sealed class LocalPanel;
                public sealed class GeneratedDesktopPlugin : DesktopPluginBase
                {
                    public new virtual LocalPanel[] Panels => [];
                }
            }
            """.Replace("public sealed class GeneratedDesktopPlugin", "public class GeneratedDesktopPlugin",
                StringComparison.Ordinal), false, "a same-name array with a local element type");

        // Compile an unrelated assembly with an SDK-looking type name, then change only
        // the covariant declaration's metadata parent. No fixture plugin is loaded.
        var spoofSdkDirectory = Path.Combine(target, "spoof-sdk");
        Directory.CreateDirectory(spoofSdkDirectory);
        var spoofSdkProject = Path.Combine(spoofSdkDirectory, "SpoofSdk.csproj");
        await File.WriteAllTextAsync(spoofSdkProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>Cerebral.Desktop.Spoofed</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(spoofSdkDirectory, "FakeBase.cs"),
            "namespace Cerebral.Desktop.Plugins; public class DesktopPluginBase;");
        var spoofSdkBuild = await BuildAsync(spoofSdkProject);
        check(spoofSdkBuild.ExitCode == 0, "a separate SDK-name spoof fixture compiles" + spoofSdkBuild.Failure);
        var desktopProjectPath = Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj");
        var originalDesktopProject = await File.ReadAllTextAsync(desktopProjectPath);
        var spoofSdkAssembly = Path.Combine(spoofSdkDirectory, "bin", "Release", "net10.0", "Cerebral.Desktop.Spoofed.dll");
        var spoofReference = "<ItemGroup><Reference Include=\"Cerebral.Desktop.Spoofed\"><HintPath>" +
            System.Security.SecurityElement.Escape(spoofSdkAssembly) +
            "</HintPath><Aliases>spoof</Aliases><Private>false</Private></Reference></ItemGroup>";
        await File.WriteAllTextAsync(desktopProjectPath,
            originalDesktopProject.Replace("</Project>", spoofReference + "</Project>", StringComparison.Ordinal));
        var metadataFixtureSource = "extern alias spoof;\n" + covariantPanelsSource.Replace(
            "public override DesktopPanel[] Panels", """
            public static Type UnusedSpoofType() => typeof(spoof::Cerebral.Desktop.Plugins.DesktopPluginBase);
            public sealed class LocalPanel;
            public static Type UnusedLocalType() => typeof(LocalPanel);
            public T GenericShape<T>() => default!;
            public override DesktopPanel[] Panels
            """, StringComparison.Ordinal);
        await CheckCompiledContribution(metadataFixtureSource, true, "a real override with unrelated reference types");
        var builtAssemblyPath = Path.Combine(Path.GetDirectoryName(builtManifest)!, "Pegasus.Plugin.example.generated.Desktop.dll");
        var correctAssemblyBytes = await File.ReadAllBytesAsync(builtAssemblyPath);

        async Task CheckTamperedDeclaration(string label, Action<byte[], PEReader, MetadataReader, MemberReferenceHandle> change)
        {
            var bytes = correctAssemblyBytes.ToArray();
            using (var pe = new PEReader(new MemoryStream(bytes, writable: false)))
            {
                var metadata = pe.GetMetadataReader();
                var entry = metadata.TypeDefinitions.Single(handle =>
                    metadata.GetString(metadata.GetTypeDefinition(handle).Name) == "GeneratedDesktopPlugin");
                var implementation = metadata.GetMethodImplementation(
                    metadata.GetTypeDefinition(entry).GetMethodImplementations().Single());
                check(implementation.MethodDeclaration.Kind == HandleKind.MemberReference &&
                      implementation.MethodBody.Kind == HandleKind.MethodDefinition,
                    label + " starts from a real compiler-emitted covariant override mapping");
                change(bytes, pe, metadata, (MemberReferenceHandle)implementation.MethodDeclaration);
            }
            await File.WriteAllBytesAsync(builtAssemblyPath, bytes);
            try
            {
                var verification = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
                    CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
                var rejectedPresentation = false;
                try { PluginScaffoldToolExtension.ValidateDesktopPresentation(profile, builtManifest, false); }
                catch (InvalidDataException) { rejectedPresentation = true; }
                check(!verification.Success && rejectedPresentation,
                    label + " is rejected by both verification and presentation");
            }
            finally { await File.WriteAllBytesAsync(builtAssemblyPath, correctAssemblyBytes); }
        }

        void SetDeclarationParent(byte[] bytes, PEReader pe, MetadataReader metadata, MemberReferenceHandle declaration,
            EntityHandle parent)
        {
            var rowSize = metadata.GetTableRowSize(TableIndex.MemberRef);
            var stringIndexSize = metadata.GetHeapSize(HeapIndex.String) > ushort.MaxValue ? 4 : 2;
            var blobIndexSize = metadata.GetHeapSize(HeapIndex.Blob) > ushort.MaxValue ? 4 : 2;
            var parentSize = rowSize - stringIndexSize - blobIndexSize;
            var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.MemberRef) +
                (MetadataTokens.GetRowNumber(declaration) - 1) * rowSize;
            var tag = parent.Kind == HandleKind.TypeReference ? 1u : 0u;
            var codedParent = ((uint)MetadataTokens.GetRowNumber(parent) << 3) | tag;
            if (parentSize == 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), checked((ushort)codedParent));
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), codedParent);
        }

        void SetTypeReferenceScope(byte[] bytes, PEReader pe, MetadataReader metadata, TypeReferenceHandle type,
            EntityHandle scope)
        {
            var rowSize = metadata.GetTableRowSize(TableIndex.TypeRef);
            var stringIndexSize = metadata.GetHeapSize(HeapIndex.String) > ushort.MaxValue ? 4 : 2;
            var scopeSize = rowSize - 2 * stringIndexSize;
            var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.TypeRef) +
                (MetadataTokens.GetRowNumber(type) - 1) * rowSize;
            var scopeTag = scope.Kind == HandleKind.AssemblyReference ? 2u : 3u;
            var codedScope = ((uint)MetadataTokens.GetRowNumber(scope) << 2) | scopeTag;
            if (scopeSize == 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), checked((ushort)codedScope));
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), codedScope);
        }

        foreach (var cyclicScope in new[] { false, true })
            await CheckTamperedDeclaration(cyclicScope ? "a cyclic SDK-element TypeRef scope" : "a nested SDK-element TypeRef scope",
                (bytes, pe, metadata, declaration) =>
                {
                    var panelType = metadata.TypeReferences.Single(handle =>
                        metadata.GetString(metadata.GetTypeReference(handle).Name) == "DesktopPanel");
                    var outerType = cyclicScope ? panelType : metadata.TypeReferences.Single(handle =>
                    {
                        var reference = metadata.GetTypeReference(handle);
                        return metadata.GetString(reference.Name) == "DesktopPluginBase" &&
                               reference.ResolutionScope.Kind == HandleKind.AssemblyReference &&
                               metadata.GetString(metadata.GetAssemblyReference(
                                   (AssemblyReferenceHandle)reference.ResolutionScope).Name) == "Cerebral.Desktop.Plugins";
                    });
                    SetTypeReferenceScope(bytes, pe, metadata, panelType, outerType);
                });

        await CheckTamperedDeclaration("an SDK-looking declaration from another assembly", (bytes, pe, metadata, declaration) =>
        {
            var fakeType = metadata.TypeReferences.Single(handle =>
            {
                var reference = metadata.GetTypeReference(handle);
                return metadata.GetString(reference.Name) == "DesktopPluginBase" &&
                       reference.ResolutionScope.Kind == HandleKind.AssemblyReference &&
                       metadata.GetString(metadata.GetAssemblyReference(
                           (AssemblyReferenceHandle)reference.ResolutionScope).Name) == "Cerebral.Desktop.Spoofed";
            });
            SetDeclarationParent(bytes, pe, metadata, declaration, fakeType);
        });
        await CheckTamperedDeclaration("a declaration on the wrong exact SDK type", (bytes, pe, metadata, declaration) =>
        {
            var panelType = metadata.TypeReferences.Single(handle =>
                metadata.GetString(metadata.GetTypeReference(handle).Name) == "DesktopPanel");
            SetDeclarationParent(bytes, pe, metadata, declaration, panelType);
        });
        await CheckTamperedDeclaration("a declaration on a local SDK-looking type", (bytes, pe, metadata, declaration) =>
        {
            var localType = metadata.TypeDefinitions.Single(handle =>
                metadata.GetString(metadata.GetTypeDefinition(handle).Name) == "LocalPanel");
            SetDeclarationParent(bytes, pe, metadata, declaration, localType);
        });
        await CheckTamperedDeclaration("a declaration whose collection type comes from a spoof assembly", (bytes, pe, metadata, declaration) =>
        {
            var listType = metadata.TypeReferences.Single(handle =>
                metadata.GetString(metadata.GetTypeReference(handle).Name) == "IReadOnlyList`1");
            var fakeAssembly = metadata.AssemblyReferences.Single(handle =>
                metadata.GetString(metadata.GetAssemblyReference(handle).Name) == "Cerebral.Desktop.Spoofed");
            SetTypeReferenceScope(bytes, pe, metadata, listType, fakeAssembly);
        });
        await CheckTamperedDeclaration("a declaration with a generic method signature", (bytes, pe, metadata, declaration) =>
        {
            var genericMethod = metadata.TypeDefinitions.SelectMany(handle =>
                metadata.GetTypeDefinition(handle).GetMethods()).Single(handle =>
                metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "GenericShape");
            var rowSize = metadata.GetTableRowSize(TableIndex.MemberRef);
            var blobIndexSize = metadata.GetHeapSize(HeapIndex.Blob) > ushort.MaxValue ? 4 : 2;
            var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.MemberRef) +
                MetadataTokens.GetRowNumber(declaration) * rowSize - blobIndexSize;
            var blobIndex = (uint)MetadataTokens.GetHeapOffset(metadata.GetMethodDefinition(genericMethod).Signature);
            if (blobIndexSize == 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), checked((ushort)blobIndex));
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), blobIndex);
        });

        foreach (var invalidBody in new[] { "private", "non-virtual", "static" })
            await CheckTamperedDeclaration("a " + invalidBody + " mapped array body", (bytes, pe, metadata, declaration) =>
            {
                var getter = metadata.TypeDefinitions.SelectMany(handle =>
                    metadata.GetTypeDefinition(handle).GetMethods()).Single(handle =>
                    metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "get_Panels");
                var rowSize = metadata.GetTableRowSize(TableIndex.MethodDef);
                var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.MethodDef) +
                    (MetadataTokens.GetRowNumber(getter) - 1) * rowSize + 6;
                var attributes = metadata.GetMethodDefinition(getter).Attributes;
                attributes = invalidBody switch
                {
                    "private" => (attributes & ~System.Reflection.MethodAttributes.MemberAccessMask) |
                        System.Reflection.MethodAttributes.Private,
                    "non-virtual" => attributes & ~System.Reflection.MethodAttributes.Virtual,
                    _ => attributes | System.Reflection.MethodAttributes.Static
                };
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), checked((ushort)attributes));
            });
        foreach (var invalidHeader in new byte[] { 0x00, 0x60, 0x25 })
            await CheckTamperedDeclaration("a declaration with noncanonical header " + invalidHeader, (bytes, pe, metadata, declaration) =>
            {
                var blob = metadata.GetMemberReference(declaration).Signature;
                var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Blob) +
                    MetadataTokens.GetHeapOffset(blob);
                var prefix = bytes[offset] < 0x80 ? 1 : bytes[offset] < 0xc0 ? 2 : 4;
                bytes[offset + prefix] = invalidHeader;
            });
        await CheckTamperedDeclaration("a malformed declaration signature", (bytes, pe, metadata, declaration) =>
        {
            var blob = metadata.GetMemberReference(declaration).Signature;
            var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Blob) +
                MetadataTokens.GetHeapOffset(blob);
            var prefix = bytes[offset] < 0x80 ? 1 : bytes[offset] < 0xc0 ? 2 : 4;
            bytes[offset + prefix] = 0xff;
        });
        await File.WriteAllTextAsync(desktopProjectPath, originalDesktopProject);

        var overloadOnlySource = """
            using Cerebral.Desktop.Plugins;

            namespace Cerebral.GeneratedPlugin;

            public abstract class LocalOverloadBase : DesktopPluginBase
            {
                public virtual DesktopMessagePresentation? CreateMessagePresentation(int value) => null;
            }

            public sealed class GeneratedDesktopPlugin : LocalOverloadBase
            {
                public override DesktopMessagePresentation? CreateMessagePresentation(int value) => null;
            }
            """;
        await File.WriteAllTextAsync(spoofedInertSourcePath, overloadOnlySource);
        var overloadOnlyBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        var overloadOnly = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        var overloadOnlyPresentationRejected = false;
        try { PluginScaffoldToolExtension.ValidateDesktopPresentation(profile, builtManifest, false); }
        catch (InvalidDataException error) { overloadOnlyPresentationRejected = error.Message.Contains("inert scaffold", StringComparison.Ordinal); }
        check(overloadOnlyBuild.ExitCode == 0 && !overloadOnly.Success &&
              overloadOnly.Content.Contains("inert scaffold", StringComparison.Ordinal) &&
              overloadOnlyPresentationRejected,
            "compiled-entry verification rejects a local same-name overload that does not override the SDK contribution slot" +
            overloadOnlyBuild.Failure);

        var genericBaseSource = """
            using Cerebral.Desktop.Plugins;

            namespace Cerebral.GeneratedPlugin;

            public abstract class LocalGenericBase<T> : DesktopPluginBase;
            public sealed class GeneratedDesktopPlugin : LocalGenericBase<int>;
            """;
        await File.WriteAllTextAsync(spoofedInertSourcePath, genericBaseSource);
        var genericBaseBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        var genericBase = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        var genericBasePresentationRejected = false;
        try { PluginScaffoldToolExtension.ValidateDesktopPresentation(profile, builtManifest, false); }
        catch (InvalidDataException error) { genericBasePresentationRejected = error.Message.Contains("generic base type", StringComparison.Ordinal); }
        check(genericBaseBuild.ExitCode == 0 && !genericBase.Success &&
              genericBase.Content.Contains("generic base type", StringComparison.Ordinal) &&
              genericBasePresentationRejected,
            "compiled-entry verification fails closed for a generic local base whose SDK slots cannot be proven" +
            genericBaseBuild.Failure);

        using (var mountVerificationDocument = JsonDocument.Parse(contract.Content))
            await File.WriteAllTextAsync(spoofedInertSourcePath,
                mountVerificationDocument.RootElement.GetProperty("reference").GetProperty("example").GetString()!);
        desktopBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        var specializedContribution = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(desktopBuild.ExitCode == 0 && specializedContribution.Success,
            "compiled-entry verification accepts an actual SDK contribution interface implementation" + desktopBuild.Failure);
        PluginScaffoldToolExtension.ValidateDesktopPresentation(profile, builtManifest, false);
        check(true, "presentation accepts the actual compiled contribution interface without loading plugin code");
        var desktopSourcePath = Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs");
        await File.WriteAllTextAsync(desktopSourcePath, """
            using System.Windows;
            using System.Windows.Controls;
            using Cerebral.Desktop.Plugins;

            namespace Cerebral.GeneratedPlugin;

            public sealed class GeneratedDesktopPlugin : IDesktopPlugin
            {
                public void Initialize(IDesktopPluginHost pluginHost) => ArgumentNullException.ThrowIfNull(pluginHost);
                public IReadOnlyList<DesktopPanel> Panels => [new("main", "Generated example", () =>
                    new TextBlock { Text = "Generated example ready", Margin = new Thickness(8) })];
                public IReadOnlyList<DesktopAction> ComposerActions { get; } = [];
                public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;
                public void Dispose() { }
            }
            """);
        desktopBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        var verified = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(desktopBuild.ExitCode == 0 && verified.Success && verified.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, State: "verified" } &&
              verified.Content.Contains("example.generated", StringComparison.Ordinal),
            "plugin verification confirms fresh implemented packages under one identity" + desktopBuild.Failure);
        PluginScaffoldToolExtension.ValidateDesktopPresentation(profile, builtManifest, false);
        check(true, "generated package presentation accepts the fresh implemented Release package without loading its code");
        var presentationProfile = new AgentProfile { Role = "lead", WorkerId = "local", AllowedRoots = [root],
            AllowedTools = ["file.read", "process.run"],
            Capabilities = ["filesystem.read", "process.execute", "desktop.plugins.manage"] };
        check(PluginScaffoldToolExtension.ValidateDesktopPresentationSource(profile, builtManifest, false) == target &&
              InterfacePathPolicy.ValidatePresentation(presentationProfile, "plugin", builtManifest).GeneratedSourceRoot == target,
            "actual compiled SDK Release admission returns its exact generated source root through the shared presentation validator");
        await PluginHelperPolicyTests.RunContextualPathsAsync(profile, presentationProfile, builtManifest, target, root, check);
        using (var verificationDocument = JsonDocument.Parse(verified.Content))
            check(verificationDocument.RootElement.GetProperty("desktop").GetProperty("packageFingerprint").GetString() ==
                NativeDesktopPluginPackage.ComputeFingerprint(builtManifest),
                "desktop verification returns the fingerprint of its exact compiled package bytes");
        var validBuiltManifest = await File.ReadAllTextAsync(builtManifest);
        var entryDiagnosticRoot = Path.Combine(root, "entrytype-diagnostic");
        Directory.CreateDirectory(entryDiagnosticRoot);
        var validEntryIdentity = NativeDesktopPluginPackage.Inspect(builtManifest);
        File.Copy(validEntryIdentity.AssemblyPath, Path.Combine(entryDiagnosticRoot, validEntryIdentity.Assembly));
        var entryDiagnosticManifest = Path.Combine(entryDiagnosticRoot, NativeDesktopPluginPackage.ManifestFileName);
        using (var entryDocument = JsonDocument.Parse(validBuiltManifest))
        {
            var manifestFields = entryDocument.RootElement.EnumerateObject()
                .ToDictionary(field => field.Name, field => field.Value.Clone(), StringComparer.Ordinal);
            foreach (var missingEntryType in new[] { validEntryIdentity.EntryType + ".Missing", new string('型', 256) })
            {
                manifestFields["entryType"] = CerebralJson.Element(missingEntryType);
                await File.WriteAllTextAsync(entryDiagnosticManifest, JsonSerializer.Serialize(manifestFields, CerebralJson.Options));
                var inspectedMissingEntry = NativeDesktopPluginPackage.Inspect(entryDiagnosticManifest);
                string? missingEntryDiagnostic = null;
                try { NativeDesktopPluginPackage.ValidateEntryPoint(inspectedMissingEntry); }
                catch (InvalidDataException error) { missingEntryDiagnostic = error.Message; }
                check(inspectedMissingEntry.EntryType == missingEntryType && missingEntryDiagnostic is not null &&
                      missingEntryDiagnostic.Contains(JsonSerializer.Serialize(missingEntryType), StringComparison.Ordinal) &&
                      missingEntryDiagnostic.Contains("absent from its built assembly", StringComparison.Ordinal) &&
                      missingEntryDiagnostic.Contains("Restore the source class/namespace", StringComparison.Ordinal) &&
                      missingEntryDiagnostic.Contains("correct the source manifest from actual source/assembly evidence", StringComparison.Ordinal) &&
                      missingEntryDiagnostic.EndsWith("rebuild, then verify again.", StringComparison.Ordinal) &&
                      Encoding.UTF8.GetByteCount(missingEntryDiagnostic) <= 2048,
                    missingEntryType.Length == 256
                        ? "primary metadata rejection preserves the exact validated maximum-length Unicode entry type and complete repair guidance within 2 KiB"
                        : "primary metadata rejection names the exact validated expected entry type and gives source/manifest, rebuild and verification recovery");
            }
        }
        await File.WriteAllTextAsync(entryDiagnosticManifest, validBuiltManifest);
        NativeDesktopPluginPackage.ValidateEntryPoint(NativeDesktopPluginPackage.Inspect(entryDiagnosticManifest));
        check(true, "the primary metadata validator still accepts the unchanged real compiled entry without loading or executing the plugin");
        await File.WriteAllTextAsync(builtManifest, "{\"name\":\"invalid\nmanifest\"}");
        try
        {
            var invalidJson = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
                CerebralJson.Element(new { root = target })), new(profile, "session", "bad-json", root), CancellationToken.None);
            check(!invalidJson.Success && invalidJson.Evidence is { Outcome: ToolExecutionOutcome.Rejected, State: "rejected" } &&
                  invalidJson.Content.Contains("JSON", StringComparison.OrdinalIgnoreCase),
                "malformed built manifest is a deterministic verification rejection rather than an uncertain effect");
        }
        finally { await File.WriteAllTextAsync(builtManifest, validBuiltManifest); }
        var sourceTimestamp = File.GetLastWriteTimeUtc(desktopSourcePath);
        File.SetLastWriteTimeUtc(desktopSourcePath, File.GetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(builtManifest)!, "Pegasus.Plugin.example.generated.Desktop.dll")).AddMinutes(1));
        var stalePresentationRejected = false;
        try { PluginScaffoldToolExtension.ValidateDesktopPresentation(profile, builtManifest, false); }
        catch (InvalidDataException error) { stalePresentationRejected = error.Message.Contains("predates", StringComparison.Ordinal); }
        finally { File.SetLastWriteTimeUtc(desktopSourcePath, sourceTimestamp); }
        check(stalePresentationRejected, "presentation rechecks freshness after an earlier successful verification");

        var validEntrySource = await File.ReadAllTextAsync(desktopSourcePath);
        await File.WriteAllTextAsync(desktopSourcePath, validEntrySource.Replace(
            "public void Initialize(IDesktopPluginHost pluginHost)",
            "public GeneratedDesktopPlugin(IDesktopPluginHost host) { }\npublic void Initialize(IDesktopPluginHost pluginHost)", StringComparison.Ordinal));
        desktopBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        var invalidConstructor = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(desktopBuild.ExitCode == 0 && !invalidConstructor.Success &&
              invalidConstructor.Content.Contains("public parameterless constructor", StringComparison.Ordinal),
            "metadata verification rejects compiled entries that the runtime cannot construct, without executing them" + desktopBuild.Failure);
        await File.WriteAllTextAsync(desktopSourcePath, validEntrySource);
        desktopBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        verified = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(desktopBuild.ExitCode == 0 && verified.Success,
            "repaired parameterless entry passes real rebuilt metadata verification" + desktopBuild.Failure);

        var directPreInitializationHostSource = """
            using System.Windows;
            using System.Windows.Controls;
            using Cerebral.Desktop.Plugins;

            namespace Cerebral.GeneratedPlugin;

            public sealed class GeneratedDesktopPlugin : DesktopPluginBase
            {
                public GeneratedDesktopPlugin() => _ = Environment.DataDirectory;
                public override IReadOnlyList<DesktopPanel> Panels =>
                    [new("main", "Generated example", () => new TextBlock { Text = "Generated example ready" })];
            }
            """;
        await File.WriteAllTextAsync(desktopSourcePath, directPreInitializationHostSource);
        desktopBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        var directPreInitializationHost = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(desktopBuild.ExitCode == 0 && !directPreInitializationHost.Success &&
              directPreInitializationHost.Content.Contains("after calling base.Initialize(pluginHost)", StringComparison.Ordinal),
            "metadata verification rejects direct SDK environment access during construction without executing the plugin" + desktopBuild.Failure);

        var pathCombinedPreInitializationHostSource = directPreInitializationHostSource
            .Replace("using System.Windows;", "using System.IO;\nusing System.Windows;", StringComparison.Ordinal)
            .Replace("public GeneratedDesktopPlugin() => _ = Environment.DataDirectory;",
                "private readonly string dataPath;\n                public GeneratedDesktopPlugin() => dataPath = Path.Combine(Environment.DataDirectory, \"milestones.json\");",
                StringComparison.Ordinal);
        await File.WriteAllTextAsync(desktopSourcePath, pathCombinedPreInitializationHostSource);
        desktopBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        var pathCombinedPreInitializationHost = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(desktopBuild.ExitCode == 0 && !pathCombinedPreInitializationHost.Success &&
              pathCombinedPreInitializationHost.Content.Contains("after calling base.Initialize(pluginHost)", StringComparison.Ordinal),
            "metadata verification rejects a constructor path built from Environment.DataDirectory without executing the plugin" + desktopBuild.Failure);

        var helperPreInitializationHostSource = directPreInitializationHostSource.Replace(
            "public GeneratedDesktopPlugin() => _ = Environment.DataDirectory;",
            "public GeneratedDesktopPlugin() => Configure();\n                private void Configure() => _ = Host.GetDraft();",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(desktopSourcePath, helperPreInitializationHostSource);
        desktopBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        var helperPreInitializationHost = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(desktopBuild.ExitCode == 0 && !helperPreInitializationHost.Success &&
              helperPreInitializationHost.Content.Contains("after calling base.Initialize(pluginHost)", StringComparison.Ordinal),
            "metadata verification follows bounded same-entry-type calls to reject indirect pre-initialization host access" + desktopBuild.Failure);

        var initializedHostSource = """
            using System.Windows;
            using System.Windows.Controls;
            using Cerebral.Desktop.Plugins;

            namespace Cerebral.GeneratedPlugin;

            public sealed class GeneratedDesktopPlugin : DesktopPluginBase
            {
                private int count;
                private string dataDirectory = "";
                public GeneratedDesktopPlugin() => InitializeDefaults();
                private void InitializeDefaults() => count = 1;
                public override void Initialize(IDesktopPluginHost pluginHost)
                {
                    base.Initialize(pluginHost);
                    dataDirectory = Environment.DataDirectory;
                }
                public override IReadOnlyList<DesktopPanel> Panels =>
                    [new("main", "Generated example", () => new TextBlock { Text = dataDirectory + count })];
            }
            """;
        await File.WriteAllTextAsync(desktopSourcePath, initializedHostSource);
        desktopBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        verified = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(desktopBuild.ExitCode == 0 && verified.Success,
            "metadata verification allows host-independent constructor helpers and environment access after base initialization" + desktopBuild.Failure);

        var opaqueUnmanagedHelperSource = """
            using System.Runtime.InteropServices;
            using System.Windows;
            using System.Windows.Controls;
            using Cerebral.Desktop.Plugins;

            namespace Cerebral.GeneratedPlugin;

            public sealed class GeneratedDesktopPlugin : DesktopPluginBase
            {
                private int count = 1;
                private string dataDirectory = "";
                [DllImport("fixture-native", EntryPoint = "fixture_probe")]
                private static extern int NativeProbe();
                public GeneratedDesktopPlugin() => _ = NativeProbe();
                public override void Initialize(IDesktopPluginHost pluginHost)
                {
                    base.Initialize(pluginHost);
                    dataDirectory = Environment.DataDirectory;
                }
                public override IReadOnlyList<DesktopPanel> Panels =>
                    [new("main", "Generated example", () => new TextBlock { Text = dataDirectory + count })];
            }
            """;
        await File.WriteAllTextAsync(desktopSourcePath, opaqueUnmanagedHelperSource);
        desktopBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        verified = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(desktopBuild.ExitCode == 0 && verified.Success,
            "metadata verification treats a valid constructor-reachable P/Invoke helper as opaque without executing it" + desktopBuild.Failure);

        var sourceServiceManifest = Path.Combine(target, "Service", "service-plugin.json");
        var serviceManifestText = await File.ReadAllTextAsync(sourceServiceManifest);
        var sourceServiceCode = Path.Combine(target, "Service", "GeneratedServicePlugin.cs");
        var serviceCodeText = await File.ReadAllTextAsync(sourceServiceCode);
        await File.WriteAllTextAsync(sourceServiceManifest, serviceManifestText.Replace("\"version\": \"1.0.0\"", "\"version\": \"1.0.1\"", StringComparison.Ordinal));
        await File.WriteAllTextAsync(sourceServiceCode, serviceCodeText.Replace("version = \"1.0.0\"", "version = \"1.0.1\"", StringComparison.Ordinal));
        serviceBuild = await BuildAsync(Path.Combine(target, "Service", "Pegasus.Plugin.example.generated.Service.csproj"));
        var mixedVersions = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(serviceBuild.ExitCode == 0 && !mixedVersions.Success && mixedVersions.Content.Contains("versions do not match", StringComparison.Ordinal),
            "plugin verification rejects a cross-surface package with mixed component versions" + serviceBuild.Failure);
        await File.WriteAllTextAsync(sourceServiceManifest, serviceManifestText);
        await File.WriteAllTextAsync(sourceServiceCode, serviceCodeText);
        serviceBuild = await BuildAsync(Path.Combine(target, "Service", "Pegasus.Plugin.example.generated.Service.csproj"));
        check(serviceBuild.ExitCode == 0, "service fixture returns to its matching version" + serviceBuild.Failure);

        await File.AppendAllTextAsync(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"), Environment.NewLine);
        desktopBuild = await BuildAsync(Path.Combine(target, "Desktop", "Pegasus.Plugin.example.generated.Desktop.csproj"));
        verified = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(desktopBuild.ExitCode == 0 && verified.Success,
            "plugin verification accepts an exact copied manifest after edited source is rebuilt" + desktopBuild.Failure);

        var builtServiceManifest = Path.Combine(target, "Service", "bin", "Release", "net10.0", "service-plugin.json");
        foreach (var component in new[] { "Desktop", "Service" })
        {
            var componentDirectory = Path.Combine(target, component);
            var incrementalSourceManifest = Path.Combine(componentDirectory,
                component == "Desktop" ? "desktop-plugin.json" : "service-plugin.json");
            var outputManifestPath = component == "Desktop" ? builtManifest : builtServiceManifest;
            var outputAssemblyPath = Path.Combine(Path.GetDirectoryName(outputManifestPath)!,
                "Pegasus.Plugin.example.generated." + component + ".dll");
            var projectPath = Path.Combine(componentDirectory, "Pegasus.Plugin.example.generated." + component + ".csproj");
            var originalManifestBytes = await File.ReadAllBytesAsync(incrementalSourceManifest);
            var originalManifestTime = File.GetLastWriteTimeUtc(incrementalSourceManifest);
            var assemblyTime = File.GetLastWriteTimeUtc(outputAssemblyPath);
            var assemblyHash = SHA256.HashData(await File.ReadAllBytesAsync(outputAssemblyPath));
            try
            {
                await File.AppendAllTextAsync(incrementalSourceManifest, Environment.NewLine + " ");
                File.SetLastWriteTimeUtc(incrementalSourceManifest, assemblyTime.AddMinutes(1));
                var notCopied = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
                    CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
                check(!notCopied.Success && notCopied.Content.Contains("does not match its source manifest", StringComparison.Ordinal),
                    component + " manifest-only edits remain rejected before the build copies their exact bytes");
                var incrementalBuild = await BuildAsync(projectPath);
                var copied = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
                    CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
                check(incrementalBuild.ExitCode == 0 &&
                      File.GetLastWriteTimeUtc(outputAssemblyPath) == assemblyTime &&
                      SHA256.HashData(await File.ReadAllBytesAsync(outputAssemblyPath)).SequenceEqual(assemblyHash) &&
                      File.GetLastWriteTimeUtc(incrementalSourceManifest) > assemblyTime && copied.Success,
                    component + " manifest-only incremental build verifies while leaving the compiled assembly unchanged" +
                    incrementalBuild.Failure + ": " + copied.Content);
                if (component == "Desktop")
                {
                    PluginScaffoldToolExtension.ValidateDesktopPresentation(profile, builtManifest, false);
                    check(true, "presentation accepts exact manifest-only incremental desktop output");
                }
            }
            finally
            {
                await File.WriteAllBytesAsync(incrementalSourceManifest, originalManifestBytes);
                File.SetLastWriteTimeUtc(incrementalSourceManifest, originalManifestTime);
                var restoreBuild = await BuildAsync(projectPath);
                check(restoreBuild.ExitCode == 0 &&
                      (await File.ReadAllBytesAsync(incrementalSourceManifest)).SequenceEqual(originalManifestBytes) &&
                      (await File.ReadAllBytesAsync(outputManifestPath)).SequenceEqual(originalManifestBytes),
                    component + " manifest freshness fixture restores its exact source/output bytes" + restoreBuild.Failure);
            }
        }

        var compiledDesktopAssembly = Path.Combine(Path.GetDirectoryName(builtManifest)!, "Pegasus.Plugin.example.generated.Desktop.dll");
        foreach (var inputName in new[] { "GeneratedDesktopPlugin.cs", "Pegasus.Plugin.example.generated.Desktop.csproj", "compile-settings.json" })
        {
            var inputPath = Path.Combine(target, "Desktop", inputName);
            var existed = File.Exists(inputPath);
            var originalTime = existed ? File.GetLastWriteTimeUtc(inputPath) : DateTime.MinValue;
            try
            {
                if (!existed) await File.WriteAllTextAsync(inputPath, "{}");
                File.SetLastWriteTimeUtc(inputPath, File.GetLastWriteTimeUtc(compiledDesktopAssembly).AddMinutes(1));
                var staleInput = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
                    CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
                var presentationRejected = false;
                try { PluginScaffoldToolExtension.ValidateDesktopPresentation(profile, builtManifest, false); }
                catch (InvalidDataException error) { presentationRejected = error.Message.Contains("predates", StringComparison.Ordinal); }
                check(!staleInput.Success && staleInput.Content.Contains("predates", StringComparison.Ordinal) && presentationRejected,
                    "the assembly freshness fence still rejects newer " + inputName + " in verify and presentation");
            }
            finally
            {
                if (existed) File.SetLastWriteTimeUtc(inputPath, originalTime);
                else File.Delete(inputPath);
            }
        }
        File.Delete(builtServiceManifest);
        serviceBuild = await BuildAsync(Path.Combine(target, "Service", "Pegasus.Plugin.example.generated.Service.csproj"));
        check(serviceBuild.ExitCode == 0 && File.Exists(builtServiceManifest),
            "generated builds restore a missing package manifest even when compilation is incremental" + serviceBuild.Failure);
        await File.AppendAllTextAsync(builtServiceManifest, Environment.NewLine + " ");
        var mismatched = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(!mismatched.Success && mismatched.Content.Contains("does not match its source manifest", StringComparison.Ordinal),
            "plugin verification rejects a built manifest whose bytes differ from its source");
        serviceBuild = await BuildAsync(Path.Combine(target, "Service", "Pegasus.Plugin.example.generated.Service.csproj"));
        verified = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.VerifyToolName,
            CerebralJson.Element(new { root = target })), new(profile, "session", "job", root), CancellationToken.None);
        check(serviceBuild.ExitCode == 0 && verified.Success,
            "a normal rebuild restores the exact verified package manifest" + serviceBuild.Failure);

        var repeatInventory = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { })), new(profile, "session", "job", root), CancellationToken.None);
        var repeated = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ToolName,
            CerebralJson.Element(new { pluginId = "example.generated", name = "Generated example", targetDirectory = target, component = "cross-surface" })),
            new(profile, "session", "job", root), CancellationToken.None);
        check(repeatInventory.Success && !repeated.Success && repeated.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
              repeated.Content.Contains("do not call file.mkdir", StringComparison.Ordinal) &&
              repeated.Content.Contains("plugin.projects", StringComparison.Ordinal) &&
              repeated.Content.Contains("different absent package directory", StringComparison.Ordinal) &&
              repeated.Content.Contains("no source was written", StringComparison.Ordinal),
            "plugin scaffold refuses to replace existing source and gives a safe absent-target recovery route");

        // A stale remembered Full Access credential must affect only an operation that
        // actually needs the protected application pipe. Ordinary project work continues,
        // while plugin source generation remains confined to the reviewed project root even
        // when the host has Full Access available for other operations.
        var staleAuthorization = Path.Combine(root, "stale-application-authorization");
        Directory.CreateDirectory(staleAuthorization);
        await File.WriteAllBytesAsync(Path.Combine(staleAuthorization, "cerebral-application-authorization.dat"),
            RandomNumberGenerator.GetBytes(64));
        profile.AllowedTools.Add("file.mkdir");
        profile.AllowedTools.Add("file.write");
        await using (var dispatcher = new ToolDispatcher(() => new HostSettings(), [tool], staleAuthorization))
        {
            var dispatcherContext = new ToolExecutionContext(profile, "dispatcher-session", "dispatcher-job", root);
            var dispatcherTarget = Path.Combine(root, "dispatcher-plugin");
            _ = await dispatcher.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
                CerebralJson.Element(new { })), dispatcherContext, default);
            var dispatcherScaffold = await dispatcher.ExecuteAsync(new(PluginScaffoldToolExtension.ToolName,
                CerebralJson.Element(new { pluginId = "example.dispatcher", name = "Dispatcher example", targetDirectory = dispatcherTarget, component = "desktop" })),
                dispatcherContext, default);
            check(dispatcherScaffold.Success && File.Exists(Path.Combine(dispatcherTarget, "Desktop", "GeneratedDesktopPlugin.cs")) &&
                  !Directory.Exists(Path.Combine(dispatcherTarget, "Service")),
                "project-scoped plugin scaffold does not depend on a remembered Full Access session");
            var desktopGuide = await File.ReadAllTextAsync(Path.Combine(dispatcherTarget, "PLUGIN.md"));
            using var desktopScaffoldDocument = JsonDocument.Parse(dispatcherScaffold.Content);
            var desktopNext = desktopScaffoldDocument.RootElement.GetProperty("next").GetString()!;
            check(desktopGuide.Contains("plugin.projects", StringComparison.Ordinal) &&
                   desktopGuide.Contains("exact returned root", StringComparison.Ordinal) &&
                   desktopGuide.Contains("before reading, editing, building, verifying, or presenting", StringComparison.Ordinal) &&
                   desktopGuide.Contains("do not scaffold from that absence", StringComparison.Ordinal) &&
                   desktopGuide.Contains("canonical Release build coordinates", StringComparison.Ordinal) &&
                  desktopGuide.Contains("plugin.contract", StringComparison.Ordinal) &&
                  desktopGuide.Contains("plugin.verify", StringComparison.Ordinal) &&
                  desktopGuide.Contains("plugin.present", StringComparison.Ordinal) &&
                  desktopGuide.Contains("public parameterless constructor host-independent", StringComparison.Ordinal) &&
                  desktopGuide.Contains("base.Initialize(pluginHost)", StringComparison.Ordinal) &&
                  desktopNext.Contains("requested desktop behavior", StringComparison.Ordinal) &&
                  !desktopNext.Contains("Service", StringComparison.Ordinal) &&
                  !desktopGuide.Contains("Service/", StringComparison.Ordinal) &&
                  !desktopGuide.Contains("service-plugin.json", StringComparison.Ordinal) &&
                  !desktopGuide.Contains("owner-controlled Worker", StringComparison.Ordinal) &&
                  !desktopGuide.Contains("StatusBar", StringComparison.Ordinal) &&
                  !desktopGuide.Contains("GetDraft", StringComparison.Ordinal) &&
                  !desktopGuide.Contains("DesktopComposerCommand", StringComparison.Ordinal) &&
                  !desktopGuide.Contains("SetResponseBalanceAsync", StringComparison.Ordinal) && desktopGuide.Length < 2500,
                "desktop PLUGIN.md is short, component-specific, and routes exact build and contract discovery without advertising service or optional facets");

            var dispatcherVerify = await dispatcher.ExecuteAsync(new(PluginScaffoldToolExtension.VerifyToolName,
                CerebralJson.Element(new { root = target })), dispatcherContext, default);
            check(dispatcherVerify.Success,
                "project-scoped plugin verification does not depend on a remembered Full Access session");

            var projectDirectory = Path.Combine(root, "dispatcher-project-directory");
            var projectMkdir = await dispatcher.ExecuteAsync(new("file.mkdir",
                CerebralJson.Element(new { path = projectDirectory })), dispatcherContext, default);
            using var firstMkdirDocument = JsonDocument.Parse(projectMkdir.Content);
            var repeatedMkdir = await dispatcher.ExecuteAsync(new("file.mkdir",
                CerebralJson.Element(new { path = projectDirectory })), dispatcherContext, default);
            using var repeatedMkdirDocument = JsonDocument.Parse(repeatedMkdir.Content);
            check(projectMkdir.Success && Directory.Exists(projectDirectory) &&
                  firstMkdirDocument.RootElement.GetProperty("ensured").GetBoolean() &&
                  !firstMkdirDocument.RootElement.GetProperty("existedBefore").GetBoolean() &&
                  !firstMkdirDocument.RootElement.TryGetProperty("created", out _) && repeatedMkdir.Success &&
                  repeatedMkdirDocument.RootElement.GetProperty("ensured").GetBoolean() &&
                  repeatedMkdirDocument.RootElement.GetProperty("existedBefore").GetBoolean() &&
                  !repeatedMkdirDocument.RootElement.TryGetProperty("created", out _),
                "ordinary mkdir truthfully distinguishes a newly ensured path from an idempotent existing-directory ensure");

            var existingWritePath = Path.Combine(root, "dispatcher-existing-write.txt");
            await File.WriteAllTextAsync(existingWritePath, "original");
            var existingWrite = await dispatcher.ExecuteAsync(new("file.write", CerebralJson.Element(new
            {
                path = existingWritePath, content = "replacement"
            })), dispatcherContext, default);
            check(!existingWrite.Success && existingWrite.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
                  existingWrite.Content.Contains("no write occurred", StringComparison.Ordinal) &&
                  existingWrite.Content.Contains("Call file.read for this exact destination now", StringComparison.Ordinal) &&
                  existingWrite.Content.Contains("collision=replace", StringComparison.Ordinal) &&
                  existingWrite.Content.Contains("exact expectedSha256", StringComparison.Ordinal) &&
                  existingWrite.Content.Contains("Never repeat collision=fail", StringComparison.Ordinal) &&
                  await File.ReadAllTextAsync(existingWritePath) == "original",
                "existing file.write rejection requires a fresh exact read and preserves the destination");

            var absentReplacePath = Path.Combine(root, "dispatcher-absent-replace.txt");
            var absentReplace = await dispatcher.ExecuteAsync(new("file.write", CerebralJson.Element(new
            {
                path = absentReplacePath, content = "replacement", collision = "replace", expectedSha256 = new string('0', 64)
            })), dispatcherContext, default);
            check(!absentReplace.Success && absentReplace.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
                  absentReplace.Content.Contains("existing destination", StringComparison.Ordinal) &&
                  absentReplace.Content.Contains("no write occurred", StringComparison.Ordinal) &&
                  absentReplace.Content.Contains("collision=fail", StringComparison.Ordinal) && !File.Exists(absentReplacePath),
                "collision=replace cannot create an absent destination through either write route");

            var mkdirTrapContext = dispatcherContext with { JobId = "mkdir-trap-job" };
            var mkdirTrapInventory = await dispatcher.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
                CerebralJson.Element(new { })), mkdirTrapContext, default);
            var mkdirTrap = Path.Combine(root, "precreated-plugin-target");
            var mkdirTrapSibling = Path.Combine(root, "precreated-plugin-recovery");
            var trapMkdir = await dispatcher.ExecuteAsync(new("file.mkdir", CerebralJson.Element(new { path = mkdirTrap })),
                mkdirTrapContext, default);
            var trappedScaffold = await dispatcher.ExecuteAsync(new(PluginScaffoldToolExtension.ToolName,
                CerebralJson.Element(new { pluginId = "example.mkdir-trap", name = "Mkdir trap", targetDirectory = mkdirTrap, component = "desktop" })),
                mkdirTrapContext, default);
            var recoveredScaffold = await dispatcher.ExecuteAsync(new(PluginScaffoldToolExtension.ToolName,
                CerebralJson.Element(new { pluginId = "example.mkdir-trap", name = "Mkdir trap", targetDirectory = mkdirTrapSibling, component = "desktop" })),
                mkdirTrapContext, default);
            check(mkdirTrapInventory.Success && trapMkdir.Success && !trappedScaffold.Success &&
                  trappedScaffold.Content.Contains("do not call file.mkdir", StringComparison.Ordinal) &&
                  trappedScaffold.Content.Contains("different absent package directory", StringComparison.Ordinal) &&
                  Directory.Exists(mkdirTrap) && !Directory.EnumerateFileSystemEntries(mkdirTrap).Any() &&
                  recoveredScaffold.Success && File.Exists(Path.Combine(mkdirTrapSibling, "PLUGIN.md")),
                "an accidental empty mkdir stays untouched while the same complete inventory can recover through a different absent sibling");

            var outsideRoot = Path.Combine(Path.GetDirectoryName(root)!, "cerebral-plugin-outside-" + Guid.NewGuid().ToString("N"));
            var outsideScaffold = await dispatcher.ExecuteAsync(new(PluginScaffoldToolExtension.ToolName,
                CerebralJson.Element(new { pluginId = "example.outside", name = "Outside", targetDirectory = outsideRoot, component = "desktop" })),
                dispatcherContext, default);
            check(!outsideScaffold.Success && !Directory.Exists(outsideRoot),
                "Full Access never widens plugin scaffold beyond the profile's reviewed project roots");

            var outsideDirectory = outsideRoot + "-mkdir";
            var outsideMkdir = await dispatcher.ExecuteAsync(new("file.mkdir",
                CerebralJson.Element(new { path = outsideDirectory })), dispatcherContext, default);
            check(IsOrdinaryScopeDenial(outsideMkdir, outsideDirectory),
                "an ordinary-user out-of-scope mutation is rejected before provider selection and never falls back natively: " +
                outsideMkdir.Content);
        }

        var serviceContext = new ToolExecutionContext(profile, "service-guide-session", "service-guide-job", root);
        _ = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { })), serviceContext, default);
        var serviceTarget = Path.Combine(root, "service-guide-plugin");
        var serviceScaffold = await tool.ExecuteAsync(new ToolCall(PluginScaffoldToolExtension.ToolName,
            CerebralJson.Element(new
            {
                pluginId = "example.service-guide", name = "Service guide", targetDirectory = serviceTarget, component = "service"
            })), serviceContext, default);
        check(serviceScaffold.Success && serviceScaffold.RequiredSuccessfulTools is not null &&
              serviceScaffold.RequiredSuccessfulTools.SequenceEqual(new[] { PluginScaffoldToolExtension.VerifyToolName }),
            "service-only scaffolding keeps verification as its sole provider-owned completion milestone");
        check(Directory.Exists(Path.Combine(serviceTarget, "Service")) && !Directory.Exists(Path.Combine(serviceTarget, "Desktop")),
            "service-only scaffolding creates only its requested component directory");
        var serviceGuide = await File.ReadAllTextAsync(Path.Combine(serviceTarget, "PLUGIN.md"));
        using var serviceScaffoldDocument = JsonDocument.Parse(serviceScaffold.Content);
        var serviceNext = serviceScaffoldDocument.RootElement.GetProperty("next").GetString()!;
        check(serviceGuide.Contains("Service staging, activation, restart, testing, deactivation, and removal", StringComparison.Ordinal) &&
              !serviceGuide.Contains("Desktop/", StringComparison.Ordinal) &&
              !serviceGuide.Contains("desktop-plugin.json", StringComparison.Ordinal) &&
              !serviceGuide.Contains("plugin.present", StringComparison.Ordinal) &&
              !serviceGuide.Contains("plugin.contract", StringComparison.Ordinal) &&
              serviceNext.Contains("requested service behavior", StringComparison.Ordinal) &&
              !serviceNext.Contains("plugin.present", StringComparison.Ordinal),
            "service PLUGIN.md retains owner lifecycle guidance without advertising desktop source or presentation");

        await RunPurposeNamingChecksAsync(Path.Combine(root, "purpose-naming"), check);
        await RunSourceIdentityChecksAsync(Path.Combine(root, "source-identity-checks"), check);
        await ServiceSdkReferenceTests.RunAsync(Path.Combine(root, "service-sdk-reference"), check);

        await PluginHelperPolicyTests.RunAuthoringAsync(root, check);
    }

    internal static async Task RunPurposeNamingChecksAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var profile = new AgentProfile
        {
            Role = "lead", WorkerId = "local", AllowedRoots = [root],
            AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ToolName,
                PluginScaffoldToolExtension.VerifyToolName],
            Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
        };
        var tool = new PluginScaffoldToolExtension(typeof(IDesktopPlugin).Assembly.Location,
            typeof(IServicePlugin).Assembly.Location);
        var context = new ToolExecutionContext(profile, "purpose-session", "purpose-job", root);
        Task<ToolResult> Call(string name, JsonElement arguments) =>
            tool.ExecuteAsync(new ToolCall(name, arguments), context, CancellationToken.None);
        var schema = tool.Tools.Single(item => item.Name == PluginScaffoldToolExtension.ToolName).InputSchema;
        check(schema.GetProperty("properties").GetProperty("description").GetProperty("maxLength").GetInt32() == 2000 &&
              !schema.GetProperty("required").EnumerateArray().Any(item => item.GetString() == "description"),
            "purpose description is bounded and optional for existing four-field callers");
        var inventory = await Call(PluginScaffoldToolExtension.ProjectsToolName, CerebralJson.Element(new { }));
        check(inventory.Success && !inventory.Truncated, "purpose fixture starts with complete source inventory");

        var target = Path.Combine(root, "named-cross-surface");
        foreach (object? description in new object?[] { null, "", "  ", 7, "line\nbreak", "line\u2028break", new string('x', 2001) })
        {
            var invalid = await Call(PluginScaffoldToolExtension.ToolName, CerebralJson.Element(new
            {
                pluginId = "pegasus.literal-purpose", name = "Literal purpose", targetDirectory = target,
                component = "cross-surface", description
            }));
            check(!invalid.Success && !Directory.Exists(target),
                "invalid explicit purpose is rejected before source publication");
        }
        using (var duplicate = JsonDocument.Parse("{\"pluginId\":\"pegasus.literal-purpose\",\"name\":\"Literal purpose\",\"targetDirectory\":\"" +
            target.Replace("\\", "\\\\", StringComparison.Ordinal) +
            "\",\"component\":\"cross-surface\",\"description\":\"first\",\"description\":\"second\"}"))
        {
            var invalid = await Call(PluginScaffoldToolExtension.ToolName, duplicate.RootElement.Clone());
            check(!invalid.Success && !Directory.Exists(target),
                "duplicate purpose property is rejected before source publication");
        }

        const string name = "Vision $(NoSuchProperty) 50% & <literal>";
        const string purpose = "Check @(Compile); preserve $(NoSuchProperty), 50% & <literal> as text.";
        var scaffold = await Call(PluginScaffoldToolExtension.ToolName, CerebralJson.Element(new
        {
            pluginId = "pegasus.literal-purpose", name, targetDirectory = target,
            component = "cross-surface", description = purpose
        }));
        check(scaffold.Success, "explicit purpose scaffold succeeds after invalid attempts without repeating inventory");
        using var response = JsonDocument.Parse(scaffold.Content);
        check(response.RootElement.GetProperty("description").GetString() == purpose,
            "scaffold receipt preserves the supplied purpose literally");
        foreach (var component in new[] { (Kind: "desktop", Folder: "Desktop"), (Kind: "service", Folder: "Service") })
        {
            var details = response.RootElement.GetProperty(component.Kind);
            var stem = "Pegasus.Plugin.pegasus.literal-purpose." + component.Folder;
            var project = details.GetProperty("project").GetString()!;
            var manifest = Path.Combine(target, component.Folder,
                component.Kind == "desktop" ? "desktop-plugin.json" : "service-plugin.json");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
            check(Path.GetFileName(project) == stem + ".csproj" &&
                  document.RootElement.GetProperty("assembly").GetString() == stem + ".dll" &&
                  document.RootElement.GetProperty("description").GetString() == purpose &&
                  document.RootElement.GetProperty("name").GetString() == name &&
                  details.GetProperty("build").GetProperty("arguments")[1].GetString() == project &&
                  details.GetProperty("builtManifest").GetString()!.EndsWith(Path.GetFileName(manifest), StringComparison.Ordinal),
                component.Kind + " project, manifest and returned build agree on the ID-derived name and purpose");
            var xml = System.Xml.Linq.XDocument.Load(project);
            check(Uri.UnescapeDataString(xml.Descendants("AssemblyTitle").Single().Value) == name &&
                  Uri.UnescapeDataString(xml.Descendants("Description").Single().Value) == purpose,
                component.Kind + " project metadata retains literal XML and MSBuild syntax");
            var build = await BuildAsync(project);
            check(build.ExitCode == 0, component.Kind + " named project compiles against its copied SDK" + build.Failure);
            var builtManifest = details.GetProperty("builtManifest").GetString()!;
            var assembly = Path.Combine(Path.GetDirectoryName(builtManifest)!, stem + ".dll");
            check(File.Exists(assembly) && File.ReadAllBytes(manifest).AsSpan().SequenceEqual(File.ReadAllBytes(builtManifest)),
                component.Kind + " build emits the named assembly and unchanged source manifest");
            using var stream = File.OpenRead(assembly);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var definition = metadata.GetAssemblyDefinition();
            check(metadata.GetString(definition.Name) == stem &&
                  AssemblyText(metadata, definition, "AssemblyTitleAttribute") == name &&
                  AssemblyText(metadata, definition, "AssemblyDescriptionAttribute") == purpose,
                component.Kind + " compiled metadata preserves the literal title and purpose");
        }
        var guide = await File.ReadAllTextAsync(Path.Combine(target, "PLUGIN.md"));
        check(guide.Contains("Pegasus.Plugin.pegasus.literal-purpose.Desktop.csproj", StringComparison.Ordinal) &&
              guide.Contains("Pegasus.Plugin.pegasus.literal-purpose.Service.csproj", StringComparison.Ordinal) &&
              guide.Contains(purpose, StringComparison.Ordinal),
            "generated guide gives exact named build coordinates and purpose");
        var hybrid = await Call(PluginScaffoldToolExtension.ProjectsToolName, CerebralJson.Element(new { root = target }));
        using (var inspected = JsonDocument.Parse(hybrid.Content))
            check(hybrid.Success && inspected.RootElement.GetProperty("projects")[0].GetProperty("components")
                  .EnumerateArray().Count() == 2 &&
                  inspected.RootElement.GetProperty("projects")[0].GetProperty("components")
                  .EnumerateArray().All(part => part.GetProperty("build").GetProperty("arguments")[1]
                      .GetString()!.Contains("Pegasus.Plugin.pegasus.literal-purpose.", StringComparison.Ordinal)),
                "existing named projects with stable generic entry classes recover both exact build envelopes");
        using (var desktopManifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(target, "Desktop", "desktop-plugin.json"))))
            check(desktopManifest.RootElement.GetProperty("entryType").GetString() ==
                  "Cerebral.GeneratedPlugin.GeneratedDesktopPlugin",
                "named assembly keeps the exact established desktop entry class");

        var next = new ToolExecutionContext(profile, "purpose-session", "purpose-next-job", root);
        Task<ToolResult> CallNext(string toolName, JsonElement arguments) =>
            tool.ExecuteAsync(new ToolCall(toolName, arguments), next, CancellationToken.None);
        check((await CallNext(PluginScaffoldToolExtension.ProjectsToolName, CerebralJson.Element(new { }))).Success,
            "a later assignment receives a fresh complete identity inventory");
        var duplicateTarget = Path.Combine(root, "duplicate-id");
        var duplicateId = await CallNext(PluginScaffoldToolExtension.ToolName, CerebralJson.Element(new
        {
            pluginId = "pegasus.literal-purpose", name = "Other display name", targetDirectory = duplicateTarget,
            component = "desktop"
        }));
        check(!duplicateId.Success && !Directory.Exists(duplicateTarget) &&
              duplicateId.Content.Contains("identity conflict", StringComparison.OrdinalIgnoreCase),
            "existing package identity refuses a second source before any write");
        var omittedTarget = Path.Combine(root, "omitted-service");
        var omitted = await CallNext(PluginScaffoldToolExtension.ToolName, CerebralJson.Element(new
        {
            pluginId = "pegasus.omitted-purpose", name = "Fallback service", targetDirectory = omittedTarget,
            component = "service"
        }));
        using (var omittedReceipt = JsonDocument.Parse(omitted.Content))
        using (var omittedManifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(omittedTarget, "Service", "service-plugin.json"))))
            check(omitted.Success && omittedReceipt.RootElement.GetProperty("description").GetString() ==
                  "Service plugin for Fallback service." &&
                  omittedManifest.RootElement.GetProperty("description").GetString() ==
                  "Service plugin for Fallback service." &&
                  omittedReceipt.RootElement.GetProperty("service").GetProperty("project").GetString() ==
                  Path.Combine(omittedTarget, "Service", "Pegasus.Plugin.pegasus.omitted-purpose.Service.csproj") &&
                  !Directory.Exists(Path.Combine(omittedTarget, "Desktop")),
                "omitted purpose keeps four-field callers working with a name-derived service fallback");

        var desktopContext = new ToolExecutionContext(profile, "purpose-session", "purpose-desktop-job", root);
        Task<ToolResult> CallDesktop(string toolName, JsonElement arguments) =>
            tool.ExecuteAsync(new ToolCall(toolName, arguments), desktopContext, CancellationToken.None);
        check((await CallDesktop(PluginScaffoldToolExtension.ProjectsToolName, CerebralJson.Element(new { }))).Success,
            "desktop-only fixture receives complete identity inventory");
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside-purpose-naming");
        var denied = await CallDesktop(PluginScaffoldToolExtension.ToolName, CerebralJson.Element(new
        {
            pluginId = "pegasus.outside-purpose", name = "Outside purpose", targetDirectory = outside,
            component = "desktop"
        }));
        check(!denied.Success && !Directory.Exists(outside),
            "purpose source cannot be published outside the reviewed scope");
        var desktopTarget = Path.Combine(root, "desktop-only");
        var desktopOnly = await CallDesktop(PluginScaffoldToolExtension.ToolName, CerebralJson.Element(new
        {
            pluginId = "pegasus.desktop-only", name = "Desktop only", targetDirectory = desktopTarget,
            component = "desktop", description = "One requested desktop action."
        }));
        using (var desktopReceipt = JsonDocument.Parse(desktopOnly.Content))
        using (var desktopPackage = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(desktopTarget, "Desktop", "desktop-plugin.json"))))
            check(desktopOnly.Success && desktopReceipt.RootElement.GetProperty("desktop").GetProperty("project").GetString() ==
                  Path.Combine(desktopTarget, "Desktop", "Pegasus.Plugin.pegasus.desktop-only.Desktop.csproj") &&
                  desktopPackage.RootElement.GetProperty("assembly").GetString() ==
                  "Pegasus.Plugin.pegasus.desktop-only.Desktop.dll" &&
                  desktopPackage.RootElement.GetProperty("description").GetString() == "One requested desktop action." &&
                  !Directory.Exists(Path.Combine(desktopTarget, "Service")),
                "desktop-only project, assembly and manifest retain one requested purpose");

        // A previously generated generic project remains a real, readable source layout.
        var legacy = Path.Combine(root, "legacy-layout");
        var legacyDesktop = Path.Combine(legacy, "Desktop");
        Directory.CreateDirectory(legacyDesktop);
        Directory.CreateDirectory(Path.Combine(legacy, "sdk", "desktop"));
        File.Copy(Path.Combine(target, "PLUGIN.md"), Path.Combine(legacy, "PLUGIN.md"));
        File.Copy(Path.Combine(target, "sdk", "desktop", "Cerebral.Desktop.Plugins.dll"),
            Path.Combine(legacy, "sdk", "desktop", "Cerebral.Desktop.Plugins.dll"));
        File.Copy(Path.Combine(target, "Desktop", "GeneratedDesktopPlugin.cs"),
            Path.Combine(legacyDesktop, "GeneratedDesktopPlugin.cs"));
        var newStem = "Pegasus.Plugin.pegasus.literal-purpose.Desktop";
        var oldStem = "Cerebral.GeneratedPlugin.Desktop";
        File.WriteAllText(Path.Combine(legacyDesktop, oldStem + ".csproj"),
            (await File.ReadAllTextAsync(Path.Combine(target, "Desktop", newStem + ".csproj")))
                .Replace(newStem, oldStem, StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(legacyDesktop, "desktop-plugin.json"),
            (await File.ReadAllTextAsync(Path.Combine(target, "Desktop", "desktop-plugin.json")))
                .Replace(newStem + ".dll", oldStem + ".dll", StringComparison.Ordinal));
        var before = File.ReadAllBytes(Path.Combine(legacyDesktop, oldStem + ".csproj"));
        var exact = await Call(PluginScaffoldToolExtension.ProjectsToolName, CerebralJson.Element(new { root = legacy }));
        using (var inspected = JsonDocument.Parse(exact.Content))
            check(exact.Success && inspected.RootElement.GetProperty("projects")[0].GetProperty("components")[0]
                  .GetProperty("build").GetProperty("arguments")[1].GetString() == Path.Combine(legacyDesktop, oldStem + ".csproj") &&
                  File.ReadAllBytes(Path.Combine(legacyDesktop, oldStem + ".csproj")).AsSpan().SequenceEqual(before),
                "legacy generic project is inspected without rename or source mutation");
        var sourcePath = Path.Combine(legacyDesktop, "GeneratedDesktopPlugin.cs");
        var legacyGuide = Path.Combine(legacy, "PLUGIN.md");
        File.Move(legacyGuide, legacyGuide + ".held");
        var missingGuide = await Call(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { targetPath = sourcePath }));
        check(!missingGuide.Success && missingGuide.Content.Contains("missing PLUGIN.md", StringComparison.Ordinal),
            "missing legacy guide remains an owned damaged source, not an absent target");
        File.Move(legacyGuide + ".held", legacyGuide);
        var legacyManifest = Path.Combine(legacyDesktop, "desktop-plugin.json");
        File.Move(legacyManifest, legacyManifest + ".held");
        var missingManifest = await Call(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { targetPath = sourcePath }));
        check(!missingManifest.Success && missingManifest.Content.Contains("malformed", StringComparison.Ordinal),
            "missing legacy manifest remains a damaged owned source");
        File.Move(legacyManifest + ".held", legacyManifest);
        File.Copy(Path.Combine(legacyDesktop, oldStem + ".csproj"), Path.Combine(legacyDesktop, "ambiguous.csproj"));
        var ambiguous = await Call(PluginScaffoldToolExtension.ProjectsToolName, CerebralJson.Element(new { root = legacy }));
        check(ambiguous.Content.Contains("multiple project files", StringComparison.Ordinal),
            "ambiguous component projects refuse a guessed build coordinate");
        File.Delete(Path.Combine(legacyDesktop, "ambiguous.csproj"));
    }

    private static string? AssemblyText(MetadataReader metadata, AssemblyDefinition assembly, string attribute)
    {
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var value = metadata.GetCustomAttribute(handle);
            if (value.Constructor.Kind != HandleKind.MemberReference) continue;
            var member = metadata.GetMemberReference((MemberReferenceHandle)value.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (metadata.GetString(type.Namespace) != "System.Reflection" || metadata.GetString(type.Name) != attribute) continue;
            var blob = metadata.GetBlobReader(value.Value);
            if (blob.ReadUInt16() != 1) throw new InvalidDataException("Assembly metadata attribute prolog is invalid.");
            return blob.ReadSerializedString();
        }
        return null;
    }


    public static async Task RunReadAccessChecksAsync(string root, Action<bool, string> check)
    {
        var parent = Path.GetFullPath(root);
        ScopedFilePolicy.RejectLinks(parent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The read-access fixture requires its owned parent.");
        var fixture = Path.Combine(parent, "plugin-projects-read-access");
        if (Directory.Exists(fixture) || File.Exists(fixture))
            throw new InvalidOperationException("The read-access fixture target must be absent.");
        Directory.CreateDirectory(fixture);
        try
        {
            var project = Path.Combine(fixture, "project");
            var reference = Path.Combine(fixture, "reference");
            var outside = Path.Combine(fixture, "outside");
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(reference);
            Directory.CreateDirectory(outside);
            var ordinaryProject = Path.Combine(reference, "Ordinary.csproj");
            var outsideProject = Path.Combine(outside, "Outside.csproj");
            await File.WriteAllTextAsync(ordinaryProject, "<Project />");
            await File.WriteAllTextAsync(outsideProject, "<Project />");
            var profile = new AgentProfile
            {
                Role = "lead", WorkerId = "local", AllowedRoots = [project],
                AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ToolName,
                    PluginScaffoldToolExtension.VerifyToolName, "file.read", "file.edit"],
                Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
            };
            var tool = new PluginScaffoldToolExtension(typeof(IDesktopPlugin).Assembly.Location,
                typeof(IServicePlugin).Assembly.Location);
            var setup = new ToolExecutionContext(profile, "read-access", "read-access-setup", project)
            {
                PathAccess = new(1, [new(project, ScopedPathAccess.Write)]), EnforceProfileScope = true
            };
            Task<ToolResult> Projects(ToolExecutionContext context, object arguments) =>
                tool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName, CerebralJson.Element(arguments)),
                    context, default);
            var plugin = Path.Combine(project, "reference-plugin");
            var setupInventory = await Projects(setup, new { });
            var seeded = await tool.ExecuteAsync(new(PluginScaffoldToolExtension.ToolName, CerebralJson.Element(new
            {
                pluginId = "fixture.read-access", name = "Read access fixture", targetDirectory = plugin, component = "desktop"
            })), setup, default);
            if (!setupInventory.Success || !seeded.Success)
                throw new InvalidOperationException("Writable plugin fixture setup failed.");
            var pluginProject = Path.Combine(plugin, "Desktop", "Cerebral.GeneratedPlugin.Desktop.csproj");
            var kept = Path.Combine(plugin, "kept.txt");
            await File.WriteAllTextAsync(kept, "original read-only content");
            var keptBytes = await File.ReadAllBytesAsync(kept);
            var keptSha = Convert.ToHexString(SHA256.HashData(keptBytes)).ToLowerInvariant();
            var scope = ScopedPathAccessPolicy.Normalize(new(1,
                [new(project, ScopedPathAccess.Write), new(reference, ScopedPathAccess.Read), new(plugin, ScopedPathAccess.Read)]));
            var readOnly = setup with { JobId = "read-access-inspect", PathAccess = scope };
            static bool Rejected(ToolResult result) =>
                !result.Success && result.Evidence?.Outcome == ToolExecutionOutcome.Rejected;
            static bool CompleteInventory(ToolResult result)
            {
                if (!result.Success || result.Truncated) return false;
                using var json = JsonDocument.Parse(result.Content);
                return json.RootElement.GetProperty("scaffoldReady").GetBoolean() &&
                    !json.RootElement.GetProperty("limited").GetBoolean();
            }
            SortedDictionary<string, string> Snapshot()
            {
                var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var directory in Directory.EnumerateDirectories(fixture, "*", SearchOption.AllDirectories))
                    result.Add("D:" + Path.GetRelativePath(fixture, directory), "");
                foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
                    result.Add("F:" + Path.GetRelativePath(fixture, file),
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
                return result;
            }
            var before = Snapshot();

            var ordinary = await Projects(readOnly, new { targetPath = ordinaryProject });
            using (var json = JsonDocument.Parse(ordinary.Content))
                check(ordinary.Success && !ordinary.Truncated && ordinary.Evidence?.Outcome == ToolExecutionOutcome.Succeeded &&
                      json.RootElement.GetProperty("targetPath").GetString() == ordinaryProject &&
                      !json.RootElement.GetProperty("targetMatched").GetBoolean(),
                    "read-only ordinary csproj inspection succeeds without inventing SDK ownership");

            var targeted = await Projects(readOnly, new { targetPath = pluginProject });
            using (var json = JsonDocument.Parse(targeted.Content))
                check(targeted.Success && !targeted.Truncated && json.RootElement.GetProperty("targetMatched").GetBoolean() &&
                      json.RootElement.GetProperty("targetComponent").GetString() == "desktop" &&
                      json.RootElement.GetProperty("projects")[0].GetProperty("root").GetString() == plugin,
                    "read-only plugin target lookup traverses admitted ancestors to its exact source root");

            var exact = await Projects(readOnly, new { root = plugin });
            using (var json = JsonDocument.Parse(exact.Content))
                check(exact.Success && !exact.Truncated && json.RootElement.GetProperty("count").GetInt32() == 1 &&
                      json.RootElement.GetProperty("projects")[0].GetProperty("root").GetString() == plugin &&
                      !json.RootElement.GetProperty("scaffoldReady").GetBoolean(),
                    "read-only exact-root inspection succeeds and cannot mint broad scaffold admission");

            var deniedTarget = await Projects(readOnly, new { targetPath = outsideProject });
            var deniedRoot = await Projects(readOnly, new { root = outside });
            check(Rejected(deniedTarget) && Rejected(deniedRoot) &&
                  deniedTarget.Content.Contains("outside", StringComparison.OrdinalIgnoreCase) &&
                  deniedRoot.Content.Contains("outside", StringComparison.OrdinalIgnoreCase),
                "plugin target and exact-root reads outside the conversation grants remain rejected");

            var fullAccessRead = await Projects(readOnly with { FullAccess = true }, new { targetPath = pluginProject });
            var writeDenied = false;
            try { _ = ScopedFilePolicy.PathInScope(kept, readOnly with { FullAccess = true }); }
            catch (UnauthorizedAccessException) { writeDenied = true; }
            check(fullAccessRead.Success && writeDenied,
                "a nested read-only grant overrides its writable ancestor even when Full Access is supplied");

            // Broad discovery still requires a writable reviewed base. Only the nested destination is read-only.
            var deniedWriteContext = readOnly with { JobId = "read-access-scaffold-denial" };
            var broad = await Projects(deniedWriteContext, new { });
            var forbidden = Path.Combine(plugin, "forbidden-scaffold");
            var deniedScaffold = await tool.ExecuteAsync(new(PluginScaffoldToolExtension.ToolName, CerebralJson.Element(new
            {
                pluginId = "fixture.read-access.denied", name = "Denied read access fixture",
                targetDirectory = forbidden, component = "desktop"
            })), deniedWriteContext, default);
            check(CompleteInventory(broad) && Rejected(deniedScaffold) &&
                  deniedScaffold.Content.Contains("not writable", StringComparison.OrdinalIgnoreCase) &&
                  !Directory.Exists(forbidden) && !File.Exists(forbidden),
                "complete writable-base discovery cannot authorize scaffolding into a read-only nested target");

            await using (var dispatcher = new ToolDispatcher(() => new HostSettings(), [tool]))
            {
                var read = await dispatcher.ExecuteAsync(new("file.read", CerebralJson.Element(new { path = kept })),
                    readOnly, default);
                var edit = await dispatcher.ExecuteAsync(new("file.edit", CerebralJson.Element(new
                {
                    path = kept, oldText = "original", newText = "changed", expectedSha256 = keptSha
                })), readOnly, default);
                check(read.Success && Rejected(edit) &&
                      edit.Content.Contains("not writable", StringComparison.OrdinalIgnoreCase) &&
                      (await File.ReadAllBytesAsync(kept)).SequenceEqual(keptBytes),
                    "an actual file.edit remains rejected by the read-only ceiling after a successful read");
            }

            var readOnlyRoot = readOnly with
            {
                JobId = "read-access-broad",
                PathAccess = ScopedPathAccessPolicy.Normalize(new(1, [new(project, ScopedPathAccess.Read)]))
            };
            var readOnlyBroad = await Projects(readOnlyRoot, new { });
            using (var json = JsonDocument.Parse(readOnlyBroad.Content))
                check(CompleteInventory(readOnlyBroad) && json.RootElement.GetProperty("count").GetInt32() == 1 &&
                      json.RootElement.GetProperty("projects")[0].GetProperty("root").GetString() == plugin,
                    "broad plugin discovery completes with an entirely read-only reviewed root");

            Task<ToolResult> Verify(ToolExecutionContext context, string selectedRoot) =>
                tool.ExecuteAsync(new(PluginScaffoldToolExtension.VerifyToolName, CerebralJson.Element(new { root = selectedRoot })),
                    context, default);
            var needsBuild = await Verify(readOnlyRoot, plugin);
            var deniedVerify = await Verify(readOnlyRoot, outside);
            check(Rejected(needsBuild) &&
                  needsBuild.Content.StartsWith("Build required before verification:", StringComparison.Ordinal) &&
                  !needsBuild.Content.Contains("not writable", StringComparison.OrdinalIgnoreCase) &&
                  Rejected(deniedVerify) && deniedVerify.Content.Contains("outside", StringComparison.OrdinalIgnoreCase),
                "read-only verification reaches package metadata diagnostics while out-of-scope verification stays rejected");

            var wrongComponent = await Verify(readOnlyRoot, Path.Combine(plugin, "Desktop"));
            check(Rejected(wrongComponent) && wrongComponent.Content ==
                  "The plugin root contains no generated Desktop or Service component. " +
                  "root must be the project directory containing PLUGIN.md and Desktop/Service, not a component or build-output directory." +
                  " Retry plugin.verify with root=" + plugin,
                "read-only wrong-component verification returns only the exact admitted parent root correction");
            check(before.SequenceEqual(Snapshot()) && profile.AllowedRoots.SequenceEqual([project]),
                "read-only inspections and refused writes preserve every fixture path, file hash, and profile root");
        }
        finally
        {
            var full = Path.GetFullPath(fixture);
            if (!ScopedFilePolicy.IsWithin(full, parent) ||
                !Path.GetFileName(full).Equals("plugin-projects-read-access", StringComparison.Ordinal))
                throw new InvalidOperationException("Read-access fixture cleanup target changed.");
            ScopedFilePolicy.RejectLinks(full);
            Directory.Delete(full, recursive: true);
        }
    }

    private static bool IsOrdinaryScopeDenial(ToolResult result, string destination) =>
        !result.Success && result.Evidence is { Outcome: ToolExecutionOutcome.Rejected, ProviderId: "cerebral" } &&
        result.Content == "Path is outside the profile's allowed roots." && !Directory.Exists(destination);

    public static async Task RunOrdinaryScopeDenialOnlyAsync(string root, Action<bool, string> check)
    {
        var authorization = Path.Combine(root, "stale-authorization");
        Directory.CreateDirectory(authorization);
        await File.WriteAllBytesAsync(Path.Combine(authorization, "cerebral-application-authorization.dat"),
            RandomNumberGenerator.GetBytes(64));
        var profile = new AgentProfile
        {
            Role = "lead", WorkerId = "local", AllowedRoots = [root], AllowedTools = ["file.mkdir"],
            Capabilities = ["filesystem.read", "filesystem.write"]
        };
        var tool = new PluginScaffoldToolExtension(typeof(IDesktopPlugin).Assembly.Location, typeof(IServicePlugin).Assembly.Location);
        await using var dispatcher = new ToolDispatcher(() => new HostSettings(), [tool], authorization);
        check(!dispatcher.FullAccessActive, "ordinary-user fixture has no active protected Full Access session");
        var context = new ToolExecutionContext(profile, "scope-denial-session", "scope-denial-job", root);
        var destination = Path.Combine(Path.GetDirectoryName(root)!, "cerebral-scope-denied-" + Guid.NewGuid().ToString("N"));
        var result = await dispatcher.ExecuteAsync(new("file.mkdir", CerebralJson.Element(new { path = destination })), context, default);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result.Success, result.Content, result.Evidence, destinationExists = Directory.Exists(destination),
            authorizationStatus = dispatcher.FullAccessStatus(),
            evidenceTier = "actual ordinary-user dispatcher scope rejection; no installed provider dispatch expected"
        }));
        check(IsOrdinaryScopeDenial(result, destination),
            "ordinary-user out-of-scope mkdir is rejected before protected provider selection or native creation");
    }

    public static Task RunSourceInventoryOnlyAsync(string root, Action<bool, string> check) =>
        RunSourceIdentityChecksAsync(root, check);

    private static async Task RunSourceIdentityChecksAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var profile = new AgentProfile
        {
            Role = "lead", WorkerId = "local", AllowedRoots = [root],
            AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ToolName],
            Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
        };
        PluginScaffoldToolExtension NewTool() => new(typeof(IDesktopPlugin).Assembly.Location, typeof(IServicePlugin).Assembly.Location);
        var tool = NewTool();
        ToolExecutionContext Context(string job, AgentProfile? selected = null) =>
            new(selected ?? profile, "identity-session", job, root);
        Task<ToolResult> Inventory(PluginScaffoldToolExtension selected, ToolExecutionContext context, string? exact = null) =>
            selected.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
                exact is null ? CerebralJson.Element(new { }) : CerebralJson.Element(new { root = exact })), context, default);
        Task<ToolResult> Scaffold(PluginScaffoldToolExtension selected, ToolExecutionContext context, string target,
            string id, string name) => selected.ExecuteAsync(new(PluginScaffoldToolExtension.ToolName,
                CerebralJson.Element(new { pluginId = id, name, targetDirectory = target, component = "desktop" })), context, default);
        static void Project(string directory, string id, string name)
        {
            Directory.CreateDirectory(Path.Combine(directory, "Desktop"));
            File.WriteAllText(Path.Combine(directory, "PLUGIN.md"), "# Fixture source\n");
            File.WriteAllText(Path.Combine(directory, "Desktop", "desktop-plugin.json"),
                JsonSerializer.Serialize(new { id, name, version = "1.0.0" }));
            File.WriteAllText(Path.Combine(directory, "Desktop", "Fixture.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(directory, "Desktop", "GeneratedDesktopPlugin.cs"), "// Identity fixture only.");
        }
        static bool Complete(ToolResult result)
        {
            using var document = JsonDocument.Parse(result.Content);
            return result.Success && !result.Truncated && !document.RootElement.GetProperty("limited").GetBoolean();
        }

        var firstProjectRoot = Path.Combine(root, "first-project-workspace");
        var firstProjectProfile = new AgentProfile
        {
            Role = "lead", WorkerId = "local", AllowedRoots = [firstProjectRoot],
            AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ToolName],
            Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
        };
        var firstProjectContext = new ToolExecutionContext(firstProjectProfile, "identity-session",
            "first-project-inventory", firstProjectRoot);
        var proposedRoot = await Inventory(tool, firstProjectContext, firstProjectRoot);
        check(!proposedRoot.Success && proposedRoot.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
              proposedRoot.Content.Contains(firstProjectRoot, StringComparison.Ordinal) &&
              proposedRoot.Content.Contains("plugin.projects with {} (omit root)", StringComparison.Ordinal) &&
              proposedRoot.Content.Contains("existing generated plugin containing PLUGIN.md", StringComparison.Ordinal) &&
              !Directory.Exists(firstProjectRoot),
            "a proposed missing exact plugin root rejects with actionable discovery guidance and no filesystem mutation");
        var firstTarget = Path.Combine(firstProjectRoot, "first-plugin");
        var missingTargetLookup = await tool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
            CerebralJson.Element(new { targetPath = firstProjectRoot })), firstProjectContext, default);
        using (var missingTargetDocument = JsonDocument.Parse(missingTargetLookup.Content))
            check(missingTargetLookup.Success && !missingTargetLookup.Truncated &&
                  !missingTargetDocument.RootElement.GetProperty("targetMatched").GetBoolean() &&
                  !Directory.Exists(firstProjectRoot),
                "a proposed target lookup reports no matching existing plugin without treating the path as broad discovery");
        check(!(await Scaffold(tool, firstProjectContext, firstTarget, "fixture.first-project", "First Project")).Success &&
              !Directory.Exists(firstProjectRoot),
            "a successful unmatched target lookup cannot replace complete broad scaffold admission");

        var missingRootInventory = await Inventory(tool, firstProjectContext);
        using (var missingRootDocument = JsonDocument.Parse(missingRootInventory.Content))
        {
            var missingResult = missingRootDocument.RootElement;
            check(missingRootInventory.Success && !missingRootInventory.Truncated &&
                  !missingResult.GetProperty("inventoryComplete").GetBoolean() &&
                  !missingResult.GetProperty("identitySummaryComplete").GetBoolean() &&
                  !missingResult.GetProperty("identityScaffoldReady").GetBoolean() &&
                  !missingResult.GetProperty("scaffoldReady").GetBoolean() &&
                  missingResult.GetProperty("limited").GetBoolean() &&
                  missingResult.GetProperty("incompleteReason").GetString()!.Contains(firstProjectRoot, StringComparison.Ordinal),
                "a missing reviewed workspace never establishes complete absence or scaffold admission");
        }
        check(!(await Scaffold(tool, firstProjectContext, firstTarget, "fixture.first-project", "First Project")).Success &&
              !Directory.Exists(firstProjectRoot),
            "incomplete missing-root discovery cannot create a reviewed workspace or plugin as a side effect");

        Directory.CreateDirectory(firstProjectRoot);
        var emptyRootInventory = await Inventory(tool, firstProjectContext);
        using (var emptyRootDocument = JsonDocument.Parse(emptyRootInventory.Content))
        {
            var emptyResult = emptyRootDocument.RootElement;
            check(Complete(emptyRootInventory) && emptyResult.GetProperty("inventoryComplete").GetBoolean() &&
                  emptyResult.GetProperty("identitySummaryComplete").GetBoolean() &&
                  emptyResult.GetProperty("identityScaffoldReady").GetBoolean() &&
                  emptyResult.GetProperty("scaffoldReady").GetBoolean() &&
                  emptyResult.GetProperty("identitySummary").GetArrayLength() == 0 &&
                  emptyResult.GetProperty("count").GetInt32() == 0 &&
                  !Directory.EnumerateFileSystemEntries(firstProjectRoot).Any(),
                "an existing empty reviewed workspace provides complete zero-source discovery without writing a file");
        }
        Directory.Delete(firstProjectRoot);
        var vanishedRootScaffold = await Scaffold(tool, firstProjectContext, firstTarget,
            "fixture.first-project", "First Project");
        check(!vanishedRootScaffold.Success &&
              vanishedRootScaffold.Content.Contains("inventory is incomplete", StringComparison.Ordinal) &&
              !Directory.Exists(firstProjectRoot),
            "a workspace disappearing after complete empty discovery fails the fresh writer scan before any directory creation");

        var existing = Path.Combine(root, "nested", "existing");
        Project(existing, "fixture.existing", "Existing Plugin");
        var existingManifest = Path.Combine(existing, "Desktop", "desktop-plugin.json");
        var unchangedManifest = await File.ReadAllTextAsync(existingManifest);
        var first = Context("new-chat");
        check(Complete(await Inventory(tool, first)), "fresh assignment receives complete bounded source discovery");
        var absentParent = Path.Combine(root, "no-mutation-parent");
        var duplicateIdTarget = Path.Combine(absentParent, "renamed-folder");
        var duplicateId = await Scaffold(tool, first, duplicateIdTarget, "fixture.existing", "Different title");
        check(!duplicateId.Success && duplicateId.Content.Contains("Plugin identity conflict", StringComparison.Ordinal) &&
              duplicateId.Content.Contains(existing, StringComparison.Ordinal) &&
              duplicateId.Content.Contains("fixture.existing", StringComparison.Ordinal) &&
              duplicateId.Content.Contains("Existing Plugin", StringComparison.Ordinal) && !Directory.Exists(absentParent),
            "same package ID in another folder rejects before any parent/source write and reports the exact existing identity/root");
        var duplicateNameTarget = Path.Combine(absentParent, "different-id");
        var duplicateName = await Scaffold(tool, first, duplicateNameTarget, "fixture.distinct", " existing plugin ");
        check(!duplicateName.Success && duplicateName.Content.Contains(existing, StringComparison.Ordinal) &&
              !Directory.Exists(absentParent) && await File.ReadAllTextAsync(existingManifest) == unchangedManifest,
            "display-name collision ignores case and surrounding spaces while preserving existing source and its stable identity");
        var distinct = await Scaffold(tool, first, Path.Combine(root, "distinct"), "fixture.distinct", "Distinct Plugin");
        check(distinct.Success, "correcting both identity fields after conflict can reuse valid discovery without another source project");

        static bool Ready(ToolResult result)
        {
            using var document = JsonDocument.Parse(result.Content);
            return result.Success && !result.Truncated && document.RootElement.GetProperty("scaffoldReady").GetBoolean();
        }

        var inspectedContext = Context("inspection-retains-scaffold-admission");
        var beforeInspection = await Inventory(tool, inspectedContext);
        check(Complete(beforeInspection) && Ready(beforeInspection), "complete broad discovery reports new-scaffold readiness");
        var exactInspection = await Inventory(tool, inspectedContext, existing);
        using (var inspectionDocument = JsonDocument.Parse(exactInspection.Content))
            check(Complete(exactInspection) && Ready(exactInspection) &&
                  !inspectionDocument.RootElement.GetProperty("discoverySummaryOnly").GetBoolean() &&
                  inspectionDocument.RootElement.GetProperty("next").GetString()!.Contains("did not establish or refresh broad", StringComparison.Ordinal),
                "exact inspection retains earlier broad readiness while exposing exact geometry and its admission provenance");
        var afterInspectionTarget = Path.Combine(root, "new-source-after-inspection");
        var afterInspection = await Scaffold(tool, inspectedContext, afterInspectionTarget,
            "fixture.after-inspection", "After Inspection");
        check(afterInspection.Success && Directory.Exists(afterInspectionTarget),
            "complete broad discovery followed by a read-only exact inspection admits one distinct scaffold");
        check(!Ready(await Inventory(tool, inspectedContext, existing)),
            "an exact read cannot restore broad admission consumed by an actual scaffold");
        var consumedTarget = Path.Combine(root, "no-restored-admission");
        check(!(await Scaffold(tool, inspectedContext, consumedTarget, "fixture.consumed", "Consumed Admission")).Success &&
              !Directory.Exists(consumedTarget), "consumed admission still requires a new complete broad discovery");

        var exactOnly = Context("exact-only");
        check(Complete(await Inventory(tool, exactOnly, existing)) && !Ready(await Inventory(tool, exactOnly, existing)),
            "successful exact inspection without preceding broad admission never mints readiness");
        var exactOnlyTarget = Path.Combine(root, "exact-only-new-source");
        check(!(await Scaffold(tool, exactOnly, exactOnlyTarget, "fixture.exact-only", "Exact Only")).Success &&
              !Directory.Exists(exactOnlyTarget), "exact-only inspection cannot authorize a new scaffold");

        var grantDenied = new AgentProfile
        {
            Role = "lead", WorkerId = "local", AllowedRoots = [root],
            AllowedTools = [PluginScaffoldToolExtension.ToolName],
            Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
        };
        // Helpers may author SDK source; a compactor remains an excluded role.
        var roleDenied = new AgentProfile
        {
            Role = "compactor", WorkerId = "local", AllowedRoots = [root],
            AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ToolName],
            Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
        };
        var exactFailures = new (string Name, JsonElement Arguments, AgentProfile? Profile)[]
        {
            ("relative-root", CerebralJson.Element(new { root = "relative-folder" }), null),
            ("nontext-root", CerebralJson.Element(new { root = false }), null),
            ("unknown-property", CerebralJson.Element(new { root = existing, unknown = true }), null),
            ("missing-root", CerebralJson.Element(new { root = Path.Combine(root, "missing-exact-root") }), null),
            ("nongenerated-root", CerebralJson.Element(new { root }), null),
            ("grant-denied", CerebralJson.Element(new { root = existing }), grantDenied),
            ("role-denied", CerebralJson.Element(new { root = existing }), roleDenied)
        };
        foreach (var failure in exactFailures)
        {
            var context = Context("exact-failure-" + failure.Name);
            check(Complete(await Inventory(tool, context)), failure.Name + " starts with complete broad admission");
            var rejected = await tool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName, failure.Arguments),
                Context(context.JobId, failure.Profile), default);
            check(!rejected.Success && rejected.Evidence?.Outcome == ToolExecutionOutcome.Rejected,
                failure.Name + " exact inspection rejects deterministically");
            var target = Path.Combine(root, "failed-exact-parent-" + failure.Name, "new-source");
            check(!(await Scaffold(tool, context, target, "fixture.failed-exact-" + failure.Name, "Failed Exact " + failure.Name)).Success &&
                  !Directory.Exists(Path.GetDirectoryName(target)),
                failure.Name + " clears preceding broad admission before any parent/source write");
        }

        var canceledExact = Context("canceled-exact");
        check(Complete(await Inventory(tool, canceledExact)), "canceled exact read starts with complete broad admission");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var observedCancellation = false;
            try
            {
                await tool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
                    CerebralJson.Element(new { root = existing })), canceledExact, canceled.Token);
            }
            catch (OperationCanceledException) { observedCancellation = true; }
            check(observedCancellation, "canceled exact inspection preserves cancellation semantics");
        }
        check(!Ready(await Inventory(tool, canceledExact, existing)),
            "an exact read cannot restore admission invalidated by earlier cancellation");
        var canceledTarget = Path.Combine(root, "canceled-exact-parent", "new-source");
        check(!(await Scaffold(tool, canceledExact, canceledTarget, "fixture.canceled-exact", "Canceled Exact")).Success &&
              !Directory.Exists(Path.GetDirectoryName(canceledTarget)),
            "canceled exact inspection clears preceding admission before any parent/source write");

        var brokenExact = Context("broken-exact-identity");
        check(Complete(await Inventory(tool, brokenExact)), "broken exact identity starts from complete broad coverage");
        await File.WriteAllTextAsync(existingManifest, "{");
        var brokenInspection = await Inventory(tool, brokenExact, existing);
        check(brokenInspection.Success && !Ready(brokenInspection) && brokenInspection.Content.Contains("GeneratedDesktopPlugin.cs", StringComparison.Ordinal),
            "unknown exact component identity clears admission while preserving repair coordinates");
        await File.WriteAllTextAsync(existingManifest, unchangedManifest);
        check(!Ready(await Inventory(tool, brokenExact, existing)), "repair inspection cannot resurrect admission after an unknown identity");

        var nextChat = Context("different-chat");
        var nextTarget = Path.Combine(root, "another-chat-folder");
        check(!(await Scaffold(tool, nextChat, nextTarget, "fixture.existing", "Other name")).Success,
            "another chat still requires its own broad discovery");
        check(Complete(await Inventory(tool, nextChat)), "different chat discovers the same source catalog");
        var nextConflict = await Scaffold(tool, nextChat, nextTarget, "fixture.existing", "Other name");
        check(!nextConflict.Success && nextConflict.Content.Contains(existing, StringComparison.Ordinal) && !Directory.Exists(nextTarget),
            "new assignment cannot bypass an existing package ID by choosing a different target folder");

        var stale = Context("stale-discovery");
        check(Complete(await Inventory(tool, stale)) && Ready(await Inventory(tool, stale, existing)),
            "preflight race fixture retains complete discovery through exact inspection");
        var lateProject = Path.Combine(root, "appeared-after-discovery");
        Project(lateProject, "fixture.late", "Late Plugin");
        var lateTarget = Path.Combine(root, "duplicate-late");
        var lateConflict = await Scaffold(tool, stale, lateTarget, "fixture.late", "Unrelated name");
        check(!lateConflict.Success && lateConflict.Content.Contains(lateProject, StringComparison.Ordinal) && !Directory.Exists(lateTarget),
            "fresh pre-write inventory catches a source added after the cached discovery result");

        var malformedContext = Context("malformed-after-exact");
        check(Complete(await Inventory(tool, malformedContext)) && Ready(await Inventory(tool, malformedContext, existing)),
            "late malformed identity starts with broad admission retained through an exact read");
        var lateManifest = Path.Combine(lateProject, "Desktop", "desktop-plugin.json");
        var lateManifestText = await File.ReadAllTextAsync(lateManifest);
        await File.WriteAllTextAsync(lateManifest, "{");
        var malformedTarget = Path.Combine(root, "late-malformed-parent", "new-source");
        var malformedDenied = await Scaffold(tool, malformedContext, malformedTarget, "fixture.late-malformed", "Late Malformed");
        check(!malformedDenied.Success && malformedDenied.Content.Contains("inventory is incomplete", StringComparison.Ordinal) &&
              !Directory.Exists(Path.GetDirectoryName(malformedTarget)),
            "newly unreadable identity after exact inspection rejects before any parent/source write");

        var replacedContext = Context("limited-broad-replaces-admission");
        await File.WriteAllTextAsync(lateManifest, lateManifestText);
        check(Complete(await Inventory(tool, replacedContext)), "broad replacement fixture starts with complete admission");
        await File.WriteAllTextAsync(lateManifest, "{");
        var replacementInventory = await Inventory(tool, replacedContext);
        check(replacementInventory.Success && !Ready(replacementInventory) &&
              !Ready(await Inventory(tool, replacedContext, existing)),
            "a later limited broad inventory replaces admission and exact inspection cannot mint it back");
        await File.WriteAllTextAsync(lateManifest, lateManifestText);

        var additionalRoot = Path.Combine(root, "second-admitted-root");
        var additionalProject = Path.Combine(additionalRoot, "existing");
        Project(additionalProject, "fixture.second-root", "Second-root Plugin");
        var firstRootOnly = Path.Combine(root, "distinct");
        var multiProfile = new AgentProfile
        {
            Role = "lead", WorkerId = "local", AllowedRoots = [firstRootOnly, additionalRoot],
            AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ToolName],
            Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
        };
        var multi = Context("multiple-roots", multiProfile);
        check(Complete(await Inventory(tool, multi)), "source inventory covers all admitted profile project roots");
        var multiTarget = Path.Combine(firstRootOnly, "second-root-duplicate");
        var multiConflict = await Scaffold(tool, multi, multiTarget, "fixture.second-root", "Different title");
        check(!multiConflict.Success && multiConflict.Content.Contains(additionalProject, StringComparison.Ordinal) && !Directory.Exists(multiTarget),
            "source collision in a different admitted root remains actionable and cannot be bypassed from the first root");
        check(Ready(await Inventory(tool, multi, firstRootOnly)), "exact inspection retains the original multiple-root admission");
        multiProfile.AllowedRoots = [firstRootOnly];
        var changedRoots = await Scaffold(tool, multi, multiTarget, "fixture.after-root-change", "After root change");
        check(!changedRoots.Success && changedRoots.Content.Contains("project roots changed", StringComparison.Ordinal) && !Directory.Exists(multiTarget),
            "changed profile roots require new broad discovery before any source write");

        var narrowed = Context("exact-observes-changed-roots", multiProfile);
        check(Complete(await Inventory(tool, narrowed)), "exact roots fixture starts from the narrowed complete root set");
        multiProfile.AllowedRoots = [firstRootOnly, additionalRoot];
        check(!Ready(await Inventory(tool, narrowed, firstRootOnly)),
            "exact inspection cannot retain admission when current reviewed roots differ");
        var narrowedTarget = Path.Combine(firstRootOnly, "changed-exact-parent", "new-source");
        check(!(await Scaffold(tool, narrowed, narrowedTarget, "fixture.changed-exact", "Changed Exact")).Success &&
              !Directory.Exists(Path.GetDirectoryName(narrowedTarget)),
            "an exact read of changed roots cannot restore reviewed-root admission");

        var longBranch = Path.Combine(root, "exact-truncation");
        var longProject = Path.Combine(longBranch, Path.Combine(Enumerable.Repeat(new string('s', 200), 30).ToArray()));
        Project(longProject, "fixture.exact-truncation", "Exact Truncation");
        var longProfile = new AgentProfile
        {
            Role = "lead", WorkerId = "local", AllowedRoots = [longProject],
            AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ToolName],
            Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
        };
        var longContext = Context("exact-truncation", longProfile);
        check(Complete(await Inventory(tool, longContext)), "long exact geometry has complete compact broad discovery");
        var longInspection = await Inventory(tool, longContext, longProject);
        check(longInspection.Success && longInspection.Truncated,
            "oversized exact build geometry is explicitly truncated");
        var longTarget = Path.Combine(longProject, "new-source");
        check(!(await Scaffold(tool, longContext, longTarget, "fixture.after-truncation", "After Truncation")).Success &&
              !Directory.Exists(longTarget), "truncated exact inspection clears preceding broad admission");
        Directory.Delete(longBranch, recursive: true);

        async Task CheckIncomplete(string lane, Action<string> prepare, string expectedReason, bool? truncated, bool limited = true)
        {
            var directory = Path.Combine(root, lane);
            Directory.CreateDirectory(directory);
            prepare(directory);
            var restricted = new AgentProfile
            {
                Role = "lead", WorkerId = "local", AllowedRoots = [directory],
                AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ToolName],
                Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
            };
            var context = Context(lane, restricted);
            var result = await Inventory(tool, context);
            using var document = JsonDocument.Parse(result.Content);
            check(result.Success && (!truncated.HasValue || result.Truncated == truncated.Value) &&
                  document.RootElement.GetProperty("limited").GetBoolean() == limited &&
                  document.RootElement.GetProperty("scaffoldReady").GetBoolean() == !limited &&
                  (!limited || document.RootElement.GetProperty("incompleteReason").GetString()!.Contains(expectedReason, StringComparison.Ordinal)),
                lane + " discovery explicitly reports incomplete identity coverage");
            var target = Path.Combine(directory, "new-source");
            var denied = await Scaffold(tool, context, target, "fixture.new-source", "New Source");
            check(!denied.Success && denied.Content.Contains("complete, untruncated", StringComparison.Ordinal) && !Directory.Exists(target),
                lane + " discovery cannot authorize scaffolding from an absent row");
            Directory.Delete(directory, recursive: true);
        }
        var unreadableRoot = Path.Combine(root, "unreadable-manifest");
        var unreadableProject = Path.Combine(unreadableRoot, "existing");
        Project(unreadableProject, "fixture.readable", "Readable desktop identity");
        Directory.CreateDirectory(Path.Combine(unreadableProject, "Service"));
        var lockedManifest = Path.Combine(unreadableProject, "Service", "service-plugin.json");
        File.WriteAllText(lockedManifest, "{\"id\":\"fixture.hidden\",\"name\":\"Locked service identity\"}");
        var unreadableProfile = new AgentProfile
        {
            Role = "lead", WorkerId = "local", AllowedRoots = [unreadableRoot],
            AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ToolName],
            Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
        };
        var unreadableContext = Context("locked-manifest", unreadableProfile);
        using (File.Open(lockedManifest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var unavailable = await Inventory(tool, unreadableContext);
            using var unavailableDocument = JsonDocument.Parse(unavailable.Content);
            check(unavailable.Success && unavailableDocument.RootElement.GetProperty("limited").GetBoolean() &&
                  unavailableDocument.RootElement.GetProperty("incompleteReason").GetString()!.Contains(lockedManifest, StringComparison.Ordinal),
                "unreadable component manifest cannot be hidden by a different readable component in the same source");
            var blockedTarget = Path.Combine(unreadableRoot, "new-source");
            check(!(await Scaffold(tool, unreadableContext, blockedTarget, "fixture.hidden", "Different title")).Success &&
                  !Directory.Exists(blockedTarget),
                "inaccessible identity coverage rejects new source writes before admission");
            var repair = await Inventory(tool, unreadableContext, unreadableProject);
            check(repair.Success && repair.Content.Contains("error", StringComparison.Ordinal) &&
                  repair.Content.Contains("fixture.readable", StringComparison.Ordinal),
                "exact-root repair inspection still returns readable coordinates plus an inaccessible component diagnostic");
        }
        Directory.Delete(unreadableRoot, recursive: true);
        await CheckIncomplete("malformed-identity", directory =>
        {
            Project(Path.Combine(directory, "broken"), "fixture.broken", "Broken Plugin");
            File.WriteAllText(Path.Combine(directory, "broken", "Desktop", "desktop-plugin.json"), "{");
        }, "Cannot establish plugin identity", false);
        await CheckIncomplete("project-count-limit", directory =>
        {
            for (var index = 0; index < 51; index++)
                Project(Path.Combine(directory, "p" + index), "fixture.limit-" + index, "Limit Plugin " + index);
        }, "Plugin source limit", null);

        var overfullRoot = Path.Combine(root, "continued-overfull-catalog");
        Directory.CreateDirectory(overfullRoot);
        try
        {
            for (var index = 0; index < 51; index++)
                Project(Path.Combine(overfullRoot, "p" + index), "fixture.overfull-" + index, "Overfull " + index);
            var overfullProfile = new AgentProfile
            {
                Role = "lead", WorkerId = "local", AllowedRoots = [overfullRoot],
                AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ToolName],
                Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
            };
            var overfullContext = Context("continued-overfull", overfullProfile);
            var overfullTool = NewTool();
            var firstOverfull = await Inventory(overfullTool, overfullContext);
            using var firstOverfullDocument = JsonDocument.Parse(firstOverfull.Content);
            var overfullToken = firstOverfullDocument.RootElement.GetProperty("continuation").GetString()!;
            var finalOverfull = await overfullTool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
                CerebralJson.Element(new { continuation = overfullToken })), overfullContext, default);
            using var finalOverfullDocument = JsonDocument.Parse(finalOverfull.Content);
            check(finalOverfull.Success && !finalOverfull.Truncated &&
                  finalOverfullDocument.RootElement.GetProperty("limited").GetBoolean() &&
                  !finalOverfullDocument.RootElement.GetProperty("scaffoldReady").GetBoolean() &&
                  finalOverfullDocument.RootElement.GetProperty("identitySummaryComplete").GetBoolean() &&
                  finalOverfullDocument.RootElement.GetProperty("identityScaffoldReady").GetBoolean() &&
                  finalOverfullDocument.RootElement.GetProperty("identitySummary").GetArrayLength() == 51 &&
                  finalOverfullDocument.RootElement.GetProperty("continuation").ValueKind == JsonValueKind.Null &&
                  finalOverfullDocument.RootElement.GetProperty("totalProjectsFound").GetInt32() == 51,
                "a completed traversal over fifty detailed rows returns every compact identity while preserving legacy limited flags");
            var duplicateOverfull = await Scaffold(overfullTool, overfullContext,
                Path.Combine(overfullRoot, "duplicate"), "fixture.overfull-0", "Other name");
            check(!duplicateOverfull.Success && duplicateOverfull.Content.Contains("Plugin identity conflict", StringComparison.Ordinal) &&
                  !Directory.Exists(Path.Combine(overfullRoot, "duplicate")),
                "a matching identity from an earlier page remains protected by the fresh complete writer scan");
            var distinctOverfull = await Scaffold(overfullTool, overfullContext,
                Path.Combine(overfullRoot, "distinct"), "fixture.new-overfull", "New Overfull");
            check(distinctOverfull.Success,
                "the complete compact identity variant can admit a distinct plugin beyond fifty detailed project rows");
        }
        finally { Directory.Delete(overfullRoot, recursive: true); }

        var deepRoot = Path.Combine(root, "deep-discovery");
        Directory.CreateDirectory(deepRoot);
        var deepProfile = new AgentProfile
        {
            Role = "lead", WorkerId = "local", AllowedRoots = [deepRoot],
            AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ToolName],
            Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
        };
        var deepContext = Context("depth-change", deepProfile);
        check(Complete(await Inventory(tool, deepContext)), "deep-project fixture starts from complete discovery");
        var deepProject = deepRoot;
        for (var index = 0; index < 7; index++) deepProject = Path.Combine(deepProject, "level");
        Project(deepProject, "fixture.deep", "Deep Plugin");
        var deepTarget = Path.Combine(deepRoot, "new-source");
        var deepConflict = await Scaffold(tool, deepContext, deepTarget, "fixture.deep", "Different name");
        check(!deepConflict.Success && deepConflict.Content.Contains("Plugin identity conflict", StringComparison.Ordinal) &&
              !Directory.Exists(deepTarget),
            "fresh writer rescan finds a deep source added after admission and rejects its duplicate identity");
        var deepDiscovery = await Inventory(tool, deepContext);
        using (var deepDocument = JsonDocument.Parse(deepDiscovery.Content))
            check(deepDiscovery.Success && !deepDiscovery.Truncated &&
                  deepDocument.RootElement.GetProperty("inventoryComplete").GetBoolean() &&
                  deepDocument.RootElement.GetProperty("identitySummaryComplete").GetBoolean() &&
                  deepDocument.RootElement.GetProperty("identityScaffoldReady").GetBoolean() &&
                  !deepDocument.RootElement.GetProperty("limited").GetBoolean() &&
                  deepDocument.RootElement.GetProperty("totalProjectsFound").GetInt32() == 1 &&
                  deepDocument.RootElement.GetProperty("identitySummary")[0].GetProperty("root").GetString() == deepProject,
                "broad discovery traverses beyond six levels and returns the exact deep source identity");
        var exactRepair = await Inventory(tool, deepContext, deepProject);
        check(Complete(exactRepair) && exactRepair.Content.Contains("fixture.deep", StringComparison.Ordinal),
            "exact existing-project inspection remains available after deep broad discovery");
        var deepDistinct = await Scaffold(tool, deepContext, deepTarget, "fixture.new-source", "New Source");
        check(deepDistinct.Success && File.Exists(Path.Combine(deepTarget, "PLUGIN.md")),
            "complete deep discovery admits a distinct source after the fresh writer scan");
        Directory.Delete(deepRoot, recursive: true);

        await CheckIncomplete("truncated-discovery", directory =>
        {
            var longParent = Path.Combine(directory,
                Path.Combine(Enumerable.Repeat(new string('x', 180), 4).ToArray()));
            for (var index = 0; index < 50; index++)
                Project(Path.Combine(longParent, "longer-project-folder-" + index), "fixture.truncation-" + index,
                    "Truncation " + index + new string('雪', 80));
        }, "", true);

        var concurrentRoot = Path.Combine(root, "concurrent");
        Directory.CreateDirectory(concurrentRoot);
        var concurrentProfile = new AgentProfile
        {
            Role = "lead", WorkerId = "local", AllowedRoots = [concurrentRoot],
            AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ToolName],
            Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
        };
        var otherTool = NewTool();
        var left = Context("concurrent-left", concurrentProfile);
        var right = Context("concurrent-right", concurrentProfile);
        check(Complete(await Inventory(tool, left)) && Complete(await Inventory(otherTool, right)),
            "two host extensions admit independent complete assignment discoveries");
        var results = await Task.WhenAll(
            Task.Run(() => Scaffold(tool, left, Path.Combine(concurrentRoot, "left"), "fixture.concurrent", "Concurrent Plugin")),
            Task.Run(() => Scaffold(otherTool, right, Path.Combine(concurrentRoot, "right"), "fixture.concurrent", "Concurrent Plugin")));
        check(results.Count(result => result.Success) == 1 &&
              results.Count(result => !result.Success && result.Content.Contains("Plugin identity conflict", StringComparison.Ordinal)) == 1 &&
              Directory.EnumerateDirectories(concurrentRoot).Count() == 1,
            "concurrent assignments/extensions with different target folders cannot both create the same source identity");

        var continuationRoot = Path.Combine(root, "bounded-continuation");
        Directory.CreateDirectory(continuationRoot);
        try
        {
            for (var index = 0; index < 4005; index++)
                Directory.CreateDirectory(Path.Combine(continuationRoot, "d" + index.ToString("D4")));
            var lateExisting = Path.Combine(continuationRoot, "d4004");
            Project(lateExisting, "fixture.continued-existing", "Continued Existing");
            var continuedProfile = new AgentProfile
            {
                Role = "lead", WorkerId = "local", AllowedRoots = [continuationRoot],
                AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName, PluginScaffoldToolExtension.ToolName],
                Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
            };
            var continuedTool = NewTool();
            var continuedContext = Context("continued-inventory", continuedProfile);
            var firstPage = await Inventory(continuedTool, continuedContext);
            using var firstDocument = JsonDocument.Parse(firstPage.Content);
            var token = firstDocument.RootElement.GetProperty("continuation").GetString()!;
            check(firstPage.Success && !firstPage.Truncated && token.Length == 32 &&
                  firstDocument.RootElement.GetProperty("limited").GetBoolean() &&
                  !firstDocument.RootElement.GetProperty("scaffoldReady").GetBoolean() &&
                  firstDocument.RootElement.GetProperty("scannedDirectories").GetInt32() == 2000,
                "the first bounded page reports incomplete coverage and grants no scaffold admission");
            var partialTarget = Path.Combine(continuationRoot, "partial-target");
            check(!(await Scaffold(continuedTool, continuedContext, partialTarget, "fixture.partial", "Partial" )).Success &&
                  !Directory.Exists(partialTarget),
                "a partial inventory cannot mutate a new plugin source");
            var otherJob = Context("foreign-continuation", continuedProfile);
            var foreign = await continuedTool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
                CerebralJson.Element(new { continuation = token })), otherJob, default);
            check(!foreign.Success && !Directory.Exists(partialTarget),
                "an opaque inventory continuation is bound to the original job");
            ToolResult current = firstPage;
            string? previousToken = null;
            string? inventoryId = null;
            for (var page = 0; page < 4; page++)
            {
                using var currentDocument = JsonDocument.Parse(current.Content);
                var pageInventoryId = currentDocument.RootElement.GetProperty("inventoryId").GetString();
                check(inventoryId is null || inventoryId == pageInventoryId,
                    "all continuation pages retain the same bounded scan identity");
                inventoryId = pageInventoryId;
                if (!currentDocument.RootElement.GetProperty("limited").GetBoolean()) break;
                var nextToken = currentDocument.RootElement.GetProperty("continuation").GetString();
                check(nextToken is not null && nextToken != previousToken,
                    "each resumable incomplete page returns a fresh page-bound token");
                previousToken = nextToken;
                current = await continuedTool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
                    CerebralJson.Element(new { continuation = nextToken })), continuedContext, default);
            }
            using var finalDocument = JsonDocument.Parse(current.Content);
            check(current.Success && !current.Truncated && Ready(current) &&
                  finalDocument.RootElement.GetProperty("identitySummaryComplete").GetBoolean() &&
                  finalDocument.RootElement.GetProperty("identitySummary").EnumerateArray().Any(project =>
                      project.GetProperty("root").GetString() == lateExisting &&
                      project.GetProperty("components")[0].GetProperty("id").GetString() == "fixture.continued-existing") &&
                  finalDocument.RootElement.GetProperty("totalDirectoriesScanned").GetInt32() > 2000 &&
                  finalDocument.RootElement.GetProperty("projects").EnumerateArray().Any(project =>
                      project.GetProperty("root").GetString() == lateExisting),
                "continuation reaches the late generated source and mints admission only after complete coverage");
            var appearedAfterRead = Path.Combine(continuationRoot, "d4003");
            Project(appearedAfterRead, "fixture.continued-late", "Continued Late");
            var rejected = await Scaffold(continuedTool, continuedContext,
                Path.Combine(continuationRoot, "duplicate-after-continuation"), "fixture.continued-late", "Other name");
            check(!rejected.Success && rejected.Content.Contains(appearedAfterRead, StringComparison.Ordinal) &&
                  !Directory.Exists(Path.Combine(continuationRoot, "duplicate-after-continuation")),
                "fresh segmented pre-write scan catches a source identity added after completed read pages");
            var allowed = await Scaffold(continuedTool, continuedContext,
                Path.Combine(continuationRoot, "new-distinct-source"), "fixture.continued-distinct", "Continued Distinct");
            check(allowed.Success, "fresh complete segmented pre-write scan permits a distinct source within existing bounds");
            var stalePage = await Inventory(continuedTool, continuedContext);
            using var staleDocument = JsonDocument.Parse(stalePage.Content);
            var staleToken = staleDocument.RootElement.GetProperty("continuation").GetString()!;
            continuedProfile.AllowedRoots = [Path.Combine(continuationRoot, "d0000")];
            var staleContinuation = await continuedTool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
                CerebralJson.Element(new { continuation = staleToken })), continuedContext, default);
            check(!staleContinuation.Success && staleContinuation.Content.Contains("reviewed roots changed", StringComparison.Ordinal),
                "a changed allowed-root set invalidates partial coverage instead of admitting a narrower continuation");
            continuedProfile.AllowedRoots = [continuationRoot];
            var cancelledPage = await Inventory(continuedTool, continuedContext);
            using var cancelledDocument = JsonDocument.Parse(cancelledPage.Content);
            var cancelledToken = cancelledDocument.RootElement.GetProperty("continuation").GetString()!;
            using var cancelledSource = new CancellationTokenSource();
            cancelledSource.Cancel();
            var cancellationObserved = false;
            try
            {
                await continuedTool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
                    CerebralJson.Element(new { continuation = cancelledToken })), continuedContext, cancelledSource.Token);
            }
            catch (OperationCanceledException) { cancellationObserved = true; }
            var afterCancellation = await continuedTool.ExecuteAsync(new(PluginScaffoldToolExtension.ProjectsToolName,
                CerebralJson.Element(new { continuation = cancelledToken })), continuedContext, default);
            check(cancellationObserved && !afterCancellation.Success,
                "cancellation clears the pending scan and its token cannot be reused");
        }
        finally { Directory.Delete(continuationRoot, recursive: true); }
    }

    internal static async Task<(int ExitCode, string Failure)> BuildAsync(string project)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "build", project, "--configuration", "Release", "--disable-build-servers", "--verbosity", "quiet" })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet for generated plugin validation.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var detail = ((await output) + Environment.NewLine + (await error)).Trim();
        if (detail.Length > 500) detail = detail[^500..];
        return (process.ExitCode, process.ExitCode == 0 ? "" : ": " + detail);
    }

    private sealed class MinimalDesktopPlugin : DesktopPluginBase
    {
        public IDesktopPluginHost TestHost => Host;
    }
}
