using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;

internal static class ServiceSdkReferenceTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var sdk = typeof(IServicePlugin).Assembly.Location;
        var profile = new AgentProfile
        {
            Role = "lead", WorkerId = "local", AllowedRoots = [root],
            AllowedTools = ["plugin.projects", "plugin.scaffold"],
            Capabilities = ["filesystem.read", "filesystem.write", "desktop.plugins.manage"]
        };
        var tool = new PluginScaffoldToolExtension(null, sdk);
        var context = new ToolExecutionContext(profile, "sdk-reference-fixture", "sdk-reference-fixture", root);
        _ = await tool.ExecuteAsync(new("plugin.projects", CerebralJson.Element(new { })), context, default);
        var target = Path.Combine(root, "reference-service");
        var scaffold = await tool.ExecuteAsync(new("plugin.scaffold", CerebralJson.Element(new
        {
            pluginId = "fixture.sdk-reference", name = "SDK reference fixture", component = "service", targetDirectory = target
        })), context, default);
        check(scaffold.Success, "isolated normal service scaffold succeeds");
        var service = Path.Combine(target, "Service");
        var cardPath = Path.Combine(service, "SERVICE-SDK.md");
        var copiedSdk = Path.Combine(target, "sdk", "service", "Cerebral.Core.dll");
        var card = await File.ReadAllTextAsync(cardPath);
        var bytes = await File.ReadAllBytesAsync(cardPath);
        var sdkHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(copiedSdk))).ToLowerInvariant();
        check(bytes.Length <= 4096 && !bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) && !card.Contains('\r') &&
              card.Contains(sdkHash, StringComparison.Ordinal) && card.Contains(typeof(IServicePlugin).Assembly.GetName().Version!.ToString(), StringComparison.Ordinal),
            "bounded LF UTF8 reference identifies the exact copied SDK");
        check(card.Contains("IReadOnlyList<IToolExtension> ToolExtensions", StringComparison.Ordinal) &&
              card.Contains("Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)", StringComparison.Ordinal) &&
              card.Contains("ToolDescriptor(string Name, string Description, JsonElement InputSchema, string Capability)", StringComparison.Ordinal) &&
              card.Contains("bool Truncated = false", StringComparison.Ordinal) &&
              card.Contains("ToolExecutionEvidence? Evidence", StringComparison.Ordinal) &&
              card.Contains("Unknown", StringComparison.Ordinal),
            "card publishes the actual selected service interfaces and constructor/evidence shapes");
        check(card.Contains("requiredHostFacets", StringComparison.Ordinal) &&
              card.Contains(ServicePluginContract.ScalarGenerationHostFacetV1, StringComparison.Ordinal) &&
              card.Contains("schemaVersion 2", StringComparison.Ordinal) &&
              card.Contains("Stage and activate reject unavailable facets", StringComparison.Ordinal) &&
              card.Contains("including installed603", StringComparison.Ordinal),
            "card documents the exact optional scalar host requirement and installed-host admission boundary");
        string serviceProject;
        using (var response = JsonDocument.Parse(scaffold.Content))
        {
            var serviceResult = response.RootElement.GetProperty("service");
            var reference = serviceResult.GetProperty("sdkReference");
            serviceProject = serviceResult.GetProperty("project").GetString()!;
            check(reference.GetProperty("state").GetString() == "available" &&
                  reference.GetProperty("path").GetString() == cardPath &&
                  reference.GetProperty("sdkSha256").GetString() == sdkHash &&
                  serviceProject == Path.Combine(service, "Pegasus.Plugin.fixture.sdk-reference.Service.csproj"),
                "scaffold returns pinned SDK reference and stable ID-derived project coordinates");
        }
        check((await File.ReadAllTextAsync(Path.Combine(service, "GeneratedServicePlugin.cs")))
              .StartsWith("// Read SERVICE-SDK.md", StringComparison.Ordinal),
            "the first source read discovers the local reference");

        var helper = new AgentProfile
        {
            Role = "helper", WorkerId = "local", AllowedRoots = [service],
            AllowedTools = ["file.read"], Capabilities = ["filesystem.read"]
        };
        await using (var dispatcher = new ToolDispatcher(() => new HostSettings()))
        {
            var helperContext = new ToolExecutionContext(helper, "reference-reader", "reference-reader", service);
            var read = await dispatcher.ExecuteAsync(new("file.read", CerebralJson.Element(new { path = cardPath })), helperContext, default);
            check(read.Success && !read.Truncated && read.Content.Contains(sdkHash, StringComparison.Ordinal),
                "a Service-only helper reads the complete card with its existing file.read grant");
            var outside = await dispatcher.ExecuteAsync(new("file.read", CerebralJson.Element(new { path = Path.Combine(target, "PLUGIN.md") })), helperContext, default);
            check(!outside.Success, "the package-root guide remains outside the helper scope");
        }

        async Task<string> State()
        {
            var inspected = await tool.ExecuteAsync(new("plugin.projects", CerebralJson.Element(new { root = target })), context, default);
            check(inspected.Success, "exact-project inspection succeeds without reference mutation");
            using var doc = JsonDocument.Parse(inspected.Content);
            return doc.RootElement.GetProperty("projects")[0].GetProperty("components")[0]
                .GetProperty("sdkReference").GetProperty("state").GetString()!;
        }
        check(await State() == "available", "exact inspection discovers the current SDK reference");
        File.Delete(cardPath);
        check(await State() == "missing" && !File.Exists(cardPath), "old projects report missing without automatic retrofit");
        await File.WriteAllTextAsync(cardPath, card + "\n");
        check(await State() == "stale" && await File.ReadAllTextAsync(cardPath) == card + "\n",
            "changed reference reports stale without rewriting it");
        await File.WriteAllTextAsync(cardPath, card, new UTF8Encoding(false));
        await using (var append = new FileStream(copiedSdk, FileMode.Append, FileAccess.Write)) append.WriteByte(0);
        check(await State() == "stale", "changed copied SDK bytes invalidate the reference pin");
        File.Copy(sdk, copiedSdk, overwrite: true);
        await File.WriteAllBytesAsync(copiedSdk, [0, 1, 2, 3]);
        check(await State() == "unavailable", "invalid copied SDK metadata reports unavailable without loading it");
        File.Copy(sdk, copiedSdk, overwrite: true);
        check(await State() == "available", "restored fixture bytes restore exact reference availability");
        var discovery = await tool.ExecuteAsync(new("plugin.projects", CerebralJson.Element(new { })), context, default);
        check(discovery.Success && !discovery.Content.Contains("sdkReference", StringComparison.Ordinal),
            "broad discovery remains compact and does not read reference bodies");

        // Isolate this fixture from ancestor output redirection. This is fixture setup,
        // not a product manifest/project change and not an update of any active trial.
        await File.WriteAllTextAsync(Path.Combine(target, "Directory.Build.props"),
            "<Project><PropertyGroup><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets></PropertyGroup></Project>");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = service, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "build", serviceProject, "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture compiler.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        var output = (await stdout) + (await stderr);
        check(process.ExitCode == 0 && File.Exists(Path.Combine(service, "bin", "Release", "net10.0", "Pegasus.Plugin.fixture.sdk-reference.Service.dll")),
            "real isolated generated service compiles against the SDK documented by the card: " + output);
    }
}
