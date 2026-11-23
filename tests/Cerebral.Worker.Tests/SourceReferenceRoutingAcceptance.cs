using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Bounded actual Source/native-Cortex read canary; explicit owner opt-in can repair this lead's read grant.</summary>
internal static class SourceReferenceRoutingAcceptance
{
    public static async Task RunAsync(string dataDirectory, bool enableLookupGrant = false)
    {
        var source = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pegasus", "Cerebral", "Source"));
        if (!OperatingSystem.IsWindows() || !Path.GetFullPath(dataDirectory).Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This read canary is restricted to the existing Source root.");
        var file = new FileInfo(Path.Combine(source, "Desktop", "connection.json"));
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > 16_384)
            throw new InvalidOperationException("Unexpected Source declaration.");
        using var declaration = JsonDocument.Parse(await File.ReadAllTextAsync(file.FullName));
        var root = declaration.RootElement;
        if (root.GetProperty("transport").GetString() != "pipe" ||
            !Path.GetFullPath(root.GetProperty("localDataDirectory").GetString()!).Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source is not its local owner pipe.");
        var endpoint = root.GetProperty("localEndpoint").Deserialize<LocalWorkerEndpoint>(CerebralJson.Options)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var client = new CerebralClient(endpoint);
        var health = await client.GetAsync<HostInfo>("health", cancellationToken: deadline.Token);
        if (!health.LocalRun || !Path.GetFullPath(health.DataDirectory).Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The endpoint does not own Source.");
        var settings = await client.GetAsync<HostSettings>("settings.get", cancellationToken: deadline.Token);
        var engine = await client.GetAsync<JsonElement>("engine.status", cancellationToken: deadline.Token);
        var auditor = await client.GetAsync<JsonElement>("laya.status", cancellationToken: deadline.Token);
        var profiles = await client.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: deadline.Token);
        var lead = profiles.Single(profile => profile.Id == "7980ce1190f648a8b832b7516a67adbc");
        if (lead.ModelWorkerId != "local" || lead.ModelId != "source-qwen35-9b" || lead.WorkerId != "local")
            throw new InvalidOperationException("The acceptance lead must remain local execution/local 5080 inference.");
        var provider = settings.McpServers.Single(server => server.Enabled && server.CortexNative is not null);
        // Required marks a portable definition whose owner binding was omitted. A present
        // native binding selects a single transport; its failure never falls back to MCP.
        provider.CortexNative!.Validate();
        if (!string.IsNullOrEmpty(provider.Url) || provider.Arguments is null || provider.Arguments.Count != 0)
            throw new InvalidOperationException("The Source native provider must have no alternate MCP endpoint or arguments.");
        var report = await client.GetAsync<CortexConnectionReport>("cortex.connection.inspect",
            new { serverId = provider.Id }, cancellationToken: deadline.Token);
        if (report.State != "ready" || report.Transport != "cortex-native-pipe" || report.McpIdentity is not null ||
            report.NativeIdentity?.Contract != CortexNativeProtocol.Contract || report.NativeIdentity.ClientId != "cerebral-source")
            throw new InvalidOperationException("Source native Cortex is not authenticated and ready: " + report.State);
        if (enableLookupGrant)
        {
            // This explicit owner command mirrors Integrator's Knowledge reads choice.
            // Preserve every profile field, host namespace and durable-write grant.
            var capability = "mcp." + provider.Id;
            if (!lead.Capabilities.Contains(capability, StringComparer.Ordinal)) lead.Capabilities.Add(capability);
            if (!lead.AllowedTools.Contains("*", StringComparer.Ordinal))
                foreach (var tool in new[] { "cortex_search", "cortex_read" })
                    if (!lead.AllowedTools.Contains("mcp." + provider.Id + "." + tool, StringComparer.Ordinal))
                        lead.AllowedTools.Add("mcp." + provider.Id + "." + tool);
            await client.GetAsync<AgentProfile>("agents.save", lead, cancellationToken: deadline.Token);
            Console.WriteLine("Owner-enabled Cortex knowledge reads for the exact Source lead; namespaces and durable-write grants preserved.");
        }
        var listed = await client.GetAsync<List<ToolDescriptor>>("tools.list", new { profileId = lead.Id }, cancellationToken: deadline.Token);
        var searchName = "mcp." + provider.Id + ".cortex_search";
        var readName = "mcp." + provider.Id + ".cortex_read";
        if (!listed.Any(tool => tool.Name == searchName) || !listed.Any(tool => tool.Name == readName))
            throw new InvalidOperationException("Source lead is missing exact Cortex lookup tools. Provider=" + provider.Id +
                "; discovery=" + string.Join(",", listed.Where(tool => tool.Name.StartsWith("mcp.", StringComparison.Ordinal)).Select(tool => tool.Name)) +
                "; grants=" + string.Join(",", lead.AllowedTools.Where(name => name == "*" || name.StartsWith("mcp.", StringComparison.Ordinal))) +
                "; capabilities=" + string.Join(",", lead.Capabilities.Where(name => name.StartsWith("mcp.", StringComparison.Ordinal))));
        async Task<JsonElement> Call(string name, object arguments)
        {
            var result = await client.GetAsync<ToolResult>("tools.call",
                new { profileId = lead.Id, name, arguments }, cancellationToken: deadline.Token);
            if (!result.Success || result.Truncated) throw new InvalidOperationException("Lookup failed or was truncated: " + name);
            return JsonDocument.Parse(result.Content).RootElement.Clone();
        }
        var guideReads = new List<JsonElement>();
        foreach (var guideId in new[] { "Knowledge.Cerebral.Guide", "Knowledge.Cerebral.Guide.PluginAuthoring",
            "Knowledge.Cerebral.Guide.ToolRouting", "Knowledge.Cerebral.Guide.RecoveryCompaction" })
        {
            _ = await Call(searchName, new { namespaces = new[] { "knowledge.shared" },
                text = guideId, limit = 3, maxUtf8Bytes = 4096, format = "compact" });
            guideReads.Add(await Call(readName, new { @namespace = "knowledge.shared", recordType = "knowledge",
                recordId = guideId, maxUtf8Bytes = 8192 }));
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            actualSource = true, transport = "cortex-native-pipe", mcpFallback = false, modelCalls = 0,
            lead = new { lead.Id, lead.ModelId, lead.ModelWorkerId, lead.ContextTokens, lead.ResponseTokens },
            helpers = profiles.Where(profile => profile.Role == "helper").Select(profile => new { profile.Id, profile.Name, profile.Purpose }),
            configuredReadNamespaces = settings.CortexNamespaces,
            tools = new[] { searchName, readName },
            guides = guideReads.Select(read =>
            {
                var exact = read.GetProperty("structuredContent");
                var record = exact.GetProperty("record");
                var body = record.GetProperty("content").GetString()!;
                return new { recordId = exact.GetProperty("recordId").GetString(),
                    revision = exact.GetProperty("revision").GetInt32(),
                    version = body.Split('\n').Single(line => line.StartsWith("Version:", StringComparison.Ordinal)).Trim(),
                    utf8Bytes = System.Text.Encoding.UTF8.GetByteCount(body),
                    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant() };
            }), engine, auditor
        }, CerebralJson.Options));
    }
}
