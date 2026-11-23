using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Tools;

public sealed partial class ToolDispatcher
{
    private static ToolDescriptor[] SharedBuiltins => Builtins.Where(x => x.Name is "file.list" or "file.read" or "file.inspect" or "file.write" or "file.append" or "file.edit" or "file.mkdir")
        .Select(x => x with { Name = x.Name.Replace("file.", "shared.", StringComparison.Ordinal), Capability = x.Capability == "filesystem.read" ? "shared.read" : "shared.write", Description = "Shared folder on the configured host. Use relative paths, '.' for its root. " + x.Description.Replace("file.", "shared.", StringComparison.Ordinal) }).ToArray();

    private async Task<ToolResult> ExecuteSharedAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        var descriptor = SharedBuiltins.FirstOrDefault(x => x.Name == call.Name) ?? throw new ArgumentException("Unknown shared file command.");
        ToolPolicy.Grant(context.Profile, descriptor.Name, descriptor.Capability); ValidateSchema(call.Arguments, descriptor.InputSchema);
        var relative = ToolPolicy.Text(call.Arguments, "path");
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split(['/', '\\']).Any(x => x == "..")) throw new UnauthorizedAccessException("Shared paths must be relative and stay inside the shared folder.");
        var settings = _settings();
        if (settings.SharedFilesWorkerId != "local")
        {
            if (context.PathAccess is not null)
                throw new UnauthorizedAccessException("This chat has local path grants; remote shared-folder access requires a separately supported resource scope.");
            var worker = settings.Workers.FirstOrDefault(x => x.Id == settings.SharedFilesWorkerId) ?? throw new InvalidOperationException("Shared file host is not configured.");
            using var client = new CerebralClient(worker);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var info = await client.GetAsync<HostInfo>("health", cancellationToken: timeout.Token);
            if (info.Product != "Pegasus Cerebral" || info.Protocol != 1) throw new InvalidOperationException("Shared file host identity or protocol mismatch.");
            return await client.GetAsync<ToolResult>("shared.call", new { profileId = context.Profile.Id, name = call.Name, arguments = call.Arguments }, cancellationToken: timeout.Token);
        }
        if (!Path.IsPathFullyQualified(settings.SharedDirectory)) throw new InvalidOperationException("Configure the shared folder in Integrator.");
        var directory = Path.GetFullPath(settings.SharedDirectory); ToolPolicy.RejectLinks(directory);
        var path = Path.GetFullPath(Path.Combine(directory, relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
        if (!ToolPolicy.IsWithin(path, directory)) throw new UnauthorizedAccessException("Shared path escaped the configured folder.");
        if (context.PathAccess is { } scope)
        {
            ScopedPathAccessPolicy.Require(path, scope, descriptor.Capability == "shared.read" ? ScopedPathAccess.Read : ScopedPathAccess.Write);
            if (!Directory.Exists(directory))
            {
                if (descriptor.Capability == "shared.read") throw new DirectoryNotFoundException("The shared folder does not exist.");
                ScopedPathAccessPolicy.Require(directory, scope, ScopedPathAccess.Write);
            }
        }
        Directory.CreateDirectory(directory);
        var builtin = "file." + call.Name[7..]; var args = JsonNode.Parse(call.Arguments.GetRawText())!.AsObject(); args["path"] = path;
        var profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(context.Profile));
        profile.AllowedRoots = [directory]; profile.AllowedTools = [builtin]; profile.Capabilities = [descriptor.Capability == "shared.read" ? "filesystem.read" : "filesystem.write"];
        var result = await ExecuteAsync(new(builtin, CerebralJson.Element(args)), context with { Profile = profile, WorkingDirectory = directory }, ct);
        // Shared listings return portable relative paths; callers must never reuse a host's absolute path on another machine.
        if (result.Success)
        {
            try
            {
                var content = JsonNode.Parse(result.Content); RewritePaths(content, directory);
                return result with { Content = content?.ToJsonString(CerebralJson.Options) ?? result.Content };
            }
            catch (JsonException) { }
        }
        return result;
    }
    private static void RewritePaths(JsonNode? node, string directory)
    {
        if (node is JsonArray array) { foreach (var item in array) RewritePaths(item, directory); }
        if (node is not JsonObject obj) return;
        foreach (var pair in obj.ToArray())
        {
            if (pair.Key == "path" && pair.Value is JsonValue value && value.TryGetValue<string>(out var path) && Path.IsPathFullyQualified(path) && ToolPolicy.IsWithin(path, directory)) obj[pair.Key] = Path.GetRelativePath(directory, path).Replace('\\', '/');
            else RewritePaths(pair.Value, directory);
        }
    }
}
