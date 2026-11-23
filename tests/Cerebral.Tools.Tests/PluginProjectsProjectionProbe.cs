using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;
using Cerebral.Tools;

// A one-shot, read-only measurement of the normal plugin.projects result and its actual model projection.
// This entry never uses the dispatcher, a model, or a scaffold/write tool.
internal static class PluginProjectsProjectionProbe
{
    public static async Task<int> RunAsync(string workspace)
    {
        var page = 0;
        try
        {
            if (!Path.IsPathFullyQualified(workspace) || !Directory.Exists(workspace))
                throw new ArgumentException("The workspace must be an existing absolute directory.");
            workspace = Path.GetFullPath(workspace);
            var profile = new AgentProfile
            {
                Role = "lead", WorkerId = "local", AllowedRoots = [workspace],
                AllowedTools = [PluginScaffoldToolExtension.ProjectsToolName],
                Capabilities = ["filesystem.read", "desktop.plugins.manage"]
            };
            var extension = new PluginScaffoldToolExtension(typeof(IDesktopPlugin).Assembly.Location,
                typeof(IServicePlugin).Assembly.Location);
            var context = new ToolExecutionContext(profile, Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), workspace);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            string? continuation = null;
            string? inventoryId = null;
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            for (page = 1; page <= 32; page++)
            {
                var arguments = continuation is null ? CerebralJson.Element(new { }) :
                    CerebralJson.Element(new { continuation });
                var result = await extension.ExecuteAsync(
                    new ToolCall(PluginScaffoldToolExtension.ProjectsToolName, arguments),
                    context, timeout.Token);
                if (!result.Success || result.Truncated)
                {
                    Print(new
                    {
                        status = "tool_incomplete", page, result.Success, result.Truncated,
                        reasonClass = result.Truncated ? "tool_output_truncated" : ClassifyReason(result.Content),
                        reason = result.Truncated ? null : SafeReason(result.Content, workspace)
                    });
                    return 2;
                }
                using var document = JsonDocument.Parse(result.Content);
                var root = document.RootElement;
                var returnedId = root.GetProperty("inventoryId").GetString();
                if (returnedId is null || !Guid.TryParseExact(returnedId, "N", out _) ||
                    (inventoryId is not null && inventoryId != returnedId) ||
                    root.GetProperty("inventoryPage").GetInt32() != page)
                    throw new InvalidDataException("Inventory sequence changed.");
                inventoryId = returnedId;
                var next = root.GetProperty("continuation");
                if (next.ValueKind == JsonValueKind.String)
                {
                    continuation = next.GetString();
                    if (continuation is null || !Guid.TryParseExact(continuation, "N", out _) ||
                        !seenTokens.Add(continuation) ||
                        root.GetProperty("inventoryComplete").GetBoolean())
                        throw new InvalidDataException("Inventory continuation is invalid.");
                    continue;
                }
                if (next.ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException("Inventory continuation has an unexpected shape.");

                var projects = root.GetProperty("projects");
                var identities = root.GetProperty("identitySummary");
                var reportedTotal = root.GetProperty("totalProjectsFound").GetInt32();
                var summaryCount = identities.ValueKind == JsonValueKind.Array ? identities.GetArrayLength() : 0;
                var pageCount = projects.GetArrayLength();
                var complete = root.GetProperty("inventoryComplete").GetBoolean();
                var identityComplete = root.GetProperty("identitySummaryComplete").GetBoolean();
                var identityReady = root.GetProperty("identityScaffoldReady").GetBoolean();
                var limited = root.GetProperty("limited").GetBoolean();
                var scaffoldReady = root.GetProperty("scaffoldReady").GetBoolean();
                var summaryValid = identities.ValueKind == JsonValueKind.Array &&
                    identities.EnumerateArray().All(ValidIdentity) &&
                    identities.EnumerateArray().Select(identity => identity.GetProperty("root").GetString()!)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() == summaryCount;
                var incompleteReason = root.GetProperty("incompleteReason");
                var reasonText = incompleteReason.ValueKind == JsonValueKind.String
                    ? incompleteReason.GetString() : null;
                var projection = ToolModelProjection.ReadableProjection(
                    PluginScaffoldToolExtension.ProjectsToolName, result.Content);
                var projectionBytes = JsonSerializer.SerializeToUtf8Bytes(projection.Text, CerebralJson.Options).Length;
                var summarySha256 = identities.ValueKind == JsonValueKind.Array
                    ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identities.GetRawText()))).ToLowerInvariant()
                    : null;
                var providerComplete = complete && identityComplete && identityReady && summaryValid &&
                    summaryCount == reportedTotal && pageCount <= reportedTotal;
                Print(new
                {
                    status = providerComplete ? "measured" : "provider_incomplete",
                    pages = page,
                    rootCount = profile.AllowedRoots.Count,
                    totalDirectoriesScanned = root.GetProperty("totalDirectoriesScanned").GetInt32(),
                    totalProjectsFound = reportedTotal,
                    returnedProjects = pageCount,
                    identitySummaryCount = summaryCount,
                    summaryValid,
                    summarySha256,
                    incompleteReasonClass = ClassifyReason(reasonText),
                    incompleteReason = SafeReason(reasonText, workspace),
                    inventoryComplete = complete,
                    identitySummaryComplete = identityComplete,
                    identityScaffoldReady = identityReady,
                    limited,
                    scaffoldReady,
                    projectionComplete = projection.Complete,
                    projectionEncodedBytes = projectionBytes,
                    projectionWithinSixKiB = projectionBytes <= 6 * 1024
                });
                return providerComplete ? 0 : 2;
            }
            Print(new { status = "page_limit", pages = 32 });
            return 2;
        }
        catch (Exception error)
        {
            // Provider errors may contain source paths; report only a bounded failure class.
            Print(new { status = "probe_failed", page, errorType = error.GetType().Name });
            return 2;
        }
    }

    private static bool ValidIdentity(JsonElement identity)
    {
        if (identity.ValueKind != JsonValueKind.Object ||
            !HasText(identity, "root") || !Path.IsPathFullyQualified(identity.GetProperty("root").GetString()!) ||
            !HasText(identity, "id") || !HasText(identity, "name") ||
            !identity.TryGetProperty("components", out var components) ||
            components.ValueKind != JsonValueKind.Array || components.GetArrayLength() is < 1 or > 2)
            return false;
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in components.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object || !HasText(component, "component"))
                return false;
            var kind = component.GetProperty("component").GetString()!;
            if (kind is not ("desktop" or "service") || !kinds.Add(kind) ||
                !HasText(component, "id") || !HasText(component, "name"))
                return false;
        }
        return true;
    }

    private static bool HasText(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(field.GetString());

    private static string ClassifyReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "none";
        if (reason.StartsWith("Source scan depth limit", StringComparison.Ordinal)) return "scan_depth_limit";
        if (reason.StartsWith("Bounded continuation directory limit", StringComparison.Ordinal)) return "directory_limit";
        if (reason.StartsWith("Bounded continuation page limit", StringComparison.Ordinal)) return "page_limit";
        if (reason.StartsWith("Plugin source has no readable component identity", StringComparison.Ordinal)) return "missing_component_identity";
        if (reason.StartsWith("Cannot establish plugin identity", StringComparison.Ordinal)) return "invalid_component_identity";
        if (reason.StartsWith("Cannot complete source inventory", StringComparison.Ordinal)) return "source_read_failure";
        if (reason.StartsWith("Cannot inspect reviewed project root", StringComparison.Ordinal)) return "root_read_failure";
        if (reason.StartsWith("Reviewed project root", StringComparison.Ordinal)) return "invalid_root";
        if (reason.StartsWith("No reviewed project roots", StringComparison.Ordinal)) return "missing_roots";
        if (reason.StartsWith("The complete source identity response exceeded", StringComparison.Ordinal)) return "identity_output_limit";
        if (reason.StartsWith("Detailed project rows", StringComparison.Ordinal)) return "detail_output_limit";
        return "other";
    }

    private static string? SafeReason(string? reason, string workspace)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var safe = reason.Replace(workspace, "<workspace>", StringComparison.OrdinalIgnoreCase);
        safe = Regex.Replace(safe, @"(?i)(?:[a-z]:\\|\\\\)[^\s'""<>]+", "<path>");
        safe = Regex.Replace(safe, "'[^']*'", "'<path>'");
        safe = Regex.Replace(safe, @"\s+", " ").Trim();
        return safe.Length <= 320 ? safe : safe[..320] + "…";
    }

    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
}
