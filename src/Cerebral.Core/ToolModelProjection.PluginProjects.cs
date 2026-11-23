using System.Text;
using System.Text.Json;

namespace Cerebral.Core;

public static partial class ToolModelProjection
{
    private const int PluginInventoryProjectionBytes = 6 * 1024;

    private static bool TryReadablePluginProjectsPayload(JsonElement root, out ToolReadableProjection projection)
    {
        projection = new("", false);
        if (!root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array ||
            projects.GetArrayLength() > 50 || projects.EnumerateArray().Any(project => project.ValueKind != JsonValueKind.Object))
            return false;

        var header = new StringBuilder();
        AppendReadable(header, "Project count", root, "count");
        AppendReadable(header, "Directories scanned", root, "scannedDirectories");
        AppendReadable(header, "Inventory id", root, "inventoryId");
        AppendReadable(header, "Inventory job", root, "inventoryJobId");
        AppendReadable(header, "Inventory page", root, "inventoryPage");
        AppendReadable(header, "All reviewed roots traversed", root, "inventoryComplete");
        AppendReadable(header, "Total projects found across pages", root, "totalProjectsFound");
        AppendReadable(header, "Total directories scanned across pages", root, "totalDirectoriesScanned");
        AppendReadable(header, "Inventory limited", root, "limited");
        AppendReadable(header, "New scaffold ready", root, "scaffoldReady");
        AppendReadable(header, "Complete identity summary", root, "identitySummaryComplete");
        AppendReadable(header, "Identity-based scaffold ready", root, "identityScaffoldReady");
        AppendReadable(header, "Continue inventory with exact token", root, "continuation");
        // Recovery instructions must survive the harness's prefix excerpt of large catalogs.
        if (root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String)
            AppendReadable(header, "Next", root, "next");
        else
            header.AppendLine("Next: reuse a matching source. Inspect its exact root for the build envelope; scaffold only after complete, untruncated discovery if none matches. Full inventory details remain in the receipt.");
        AppendReadable(header, "Incomplete reason", root, "incompleteReason");

        var claimsCompactIdentity =
            (root.TryGetProperty("identitySummaryComplete", out var claimedComplete) && claimedComplete.ValueKind == JsonValueKind.True) ||
            (root.TryGetProperty("identityScaffoldReady", out var claimedReady) && claimedReady.ValueKind == JsonValueKind.True);
        var hasReportedTotal = root.TryGetProperty("totalProjectsFound", out var reportedTotal) &&
            reportedTotal.ValueKind != JsonValueKind.Null;
        var totalCount = -1;
        var validReportedTotal = !hasReportedTotal ||
            TryBoundedInt(root, "totalProjectsFound", 0, 1_600, out totalCount);
        var summaryOnly = root.TryGetProperty("discoverySummaryOnly", out var summary) && summary.ValueKind == JsonValueKind.True;
        if (!summaryOnly && projects.GetArrayLength() == 1 && !claimsCompactIdentity && validReportedTotal &&
            (!hasReportedTotal || totalCount == 1))
        {
            var project = projects[0];
            header.AppendLine();
            foreach (var field in new[] { ("Plugin", "id"), ("Name", "name"), ("Version", "version"), ("Root", "root"), ("Diagnostic", "error") })
                AppendReadable(header, field.Item1, project, field.Item2);
            foreach (var component in Components(project))
            {
                AppendReadable(header, "Component", component, "component");
                AppendReadable(header, "Component diagnostic", component, "error");
                AppendReadable(header, "Manifest", component, "manifest");
                AppendReadable(header, "Source", component, "source");
                if (component.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Object)
                    AppendBuild(header, build);
            }
            projection = new(header.ToString(), true);
            return true;
        }

        // A selection summary is deliberately separate from exact source/build inspection.
        // Keep failing roots first so a late malformed package remains repairable without guessing.
        JsonElement identities = default;
        var completeIdentityEnvelope =
            root.TryGetProperty("identitySummaryComplete", out var identityComplete) && identityComplete.ValueKind == JsonValueKind.True &&
            root.TryGetProperty("identityScaffoldReady", out var identityReady) && identityReady.ValueKind == JsonValueKind.True &&
            root.TryGetProperty("inventoryComplete", out var scanComplete) && scanComplete.ValueKind == JsonValueKind.True &&
            root.TryGetProperty("inventoryJobId", out var job) && job.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(job.GetString()) &&
            root.TryGetProperty("inventoryId", out var inventoryId) && inventoryId.ValueKind == JsonValueKind.String &&
            inventoryId.GetString() is { Length: 32 } scanId && scanId.All(char.IsAsciiHexDigit) &&
            TryBoundedInt(root, "inventoryPage", 1, 32, out _) &&
            TryBoundedInt(root, "totalDirectoriesScanned", 0, 64_000, out _) &&
            TryBoundedInt(root, "count", 0, 50, out var pageCount) && pageCount == projects.GetArrayLength() &&
            root.TryGetProperty("discoverySummaryOnly", out var broad) && broad.ValueKind == JsonValueKind.True &&
            root.TryGetProperty("limited", out var limited) && limited.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            root.TryGetProperty("scaffoldReady", out var legacyReady) &&
            legacyReady.ValueKind == (limited.ValueKind == JsonValueKind.True ? JsonValueKind.False : JsonValueKind.True) &&
            root.TryGetProperty("continuation", out var continuation) && continuation.ValueKind == JsonValueKind.Null &&
            hasReportedTotal && validReportedTotal &&
            root.TryGetProperty("identitySummary", out identities) && identities.ValueKind == JsonValueKind.Array &&
            identities.GetArrayLength() == totalCount && pageCount <= totalCount &&
            identities.EnumerateArray().All(ValidIdentity) && PageRowsIncluded(projects, identities) &&
            identities.EnumerateArray().Select(identity => Text(identity, "root")!)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == identities.GetArrayLength();
        var rows = completeIdentityEnvelope
            ? identities.EnumerateArray().Select(IdentityRow).ToArray()
            : projects.EnumerateArray().OrderByDescending(HasDiagnostic).Select(ProjectRow).ToArray();
        var hasInventoryLifecycle = root.TryGetProperty("inventoryComplete", out _) ||
            root.TryGetProperty("continuation", out _);
        var legacyTerminal = !hasInventoryLifecycle ||
            root.TryGetProperty("inventoryComplete", out var legacyScan) && legacyScan.ValueKind == JsonValueKind.True &&
            root.TryGetProperty("continuation", out var legacyToken) && legacyToken.ValueKind == JsonValueKind.Null &&
            root.TryGetProperty("limited", out var legacyLimited) && legacyLimited.ValueKind == JsonValueKind.False &&
            root.TryGetProperty("scaffoldReady", out var legacyScaffold) && legacyScaffold.ValueKind == JsonValueKind.True;
        var allDiscoveredRowsInThisResult = completeIdentityEnvelope ||
            !claimsCompactIdentity && legacyTerminal && validReportedTotal &&
            (!hasReportedTotal || totalCount == rows.Length);
        var headerComplete = JsonSerializer.SerializeToUtf8Bytes(header.ToString(), Json).Length <= PluginInventoryProjectionBytes - 512;
        if (!headerComplete)
        {
            header.Clear();
            AppendReadable(header, "Project count", root, "count");
            AppendReadable(header, "Inventory limited", root, "limited");
            AppendReadable(header, "Inventory id", root, "inventoryId");
            AppendReadable(header, "Inventory job", root, "inventoryJobId");
            AppendReadable(header, "Inventory page", root, "inventoryPage");
            AppendReadable(header, "All reviewed roots traversed", root, "inventoryComplete");
            AppendReadable(header, "New scaffold ready", root, "scaffoldReady");
            AppendReadable(header, "Complete identity summary", root, "identitySummaryComplete");
            AppendReadable(header, "Identity-based scaffold ready", root, "identityScaffoldReady");
            AppendReadable(header, "Continue inventory with exact token", root, "continuation");
            header.AppendLine("Projection incomplete: provider diagnostics or instructions exceed this summary's bound. Read the exact receipt before acting or inferring that a source identity is absent.");
        }

        string RenderRows(int count) => header + $"Shown source roots: {count} of {rows.Length}\n" +
            (count < rows.Length
                ? "Projection incomplete: more returned roots remain in this receipt. Fetch receipt pages for the rest; inspect an exact returned root before repair. Do not scaffold from a missing row.\n"
                : "This selection summary shows every returned root; the provider's limited/truncated status still controls new-scaffold admission.\n") +
            string.Join('\n', rows.Take(count));

        var selected = RenderRows(0);
        var displayed = 0;
        for (var count = 1; count <= rows.Length; count++)
        {
            var candidate = RenderRows(count);
            // Match the harness's JSON-encoded UTF-8 measurement, preserving whole coordinates.
            if (JsonSerializer.SerializeToUtf8Bytes(candidate, Json).Length > PluginInventoryProjectionBytes) break;
            selected = candidate;
            displayed = count;
        }
        if (completeIdentityEnvelope && (!headerComplete || displayed != rows.Length))
        {
            // The receipt retains every exact root. If long roots crowd out identity metadata,
            // show every identity with its stable array ordinal and require an exact receipt read
            // before an existing source is selected. Never claim completeness for a clipped row.
            var compact = new StringBuilder();
            compact.AppendLine($"Complete plugin identities: {totalCount} of {totalCount}; exact roots are retained in this tool receipt.");
            AppendReadable(compact, "Inventory id", root, "inventoryId");
            AppendReadable(compact, "Inventory job", root, "inventoryJobId");
            AppendReadable(compact, "Inventory page", root, "inventoryPage");
            AppendReadable(compact, "All reviewed roots traversed", root, "inventoryComplete");
            AppendReadable(compact, "Detailed rows limited", root, "limited");
            AppendReadable(compact, "Identity-based scaffold ready", root, "identityScaffoldReady");
            compact.AppendLine("Each 1-based row number refers to identitySummary[number-1] in the current tool receipt.");
            compact.AppendLine("Component aliases 'same plugin ID/name', 'same plugin ID', and 'same plugin name' mean exact equality to that numbered plugin row; distinct values are literal.");
            compact.AppendLine("For a match, call {\"action\":\"receipt\",\"effectId\":\"<current effectId>\",\"offset\":0}; follow nextOffset pages to identitySummary[number-1], then copy its exact root into plugin.projects{root}. Never guess a root; scaffold only if identity-based ready and no match.");
            var compactRows = identities.EnumerateArray().Select((identity, index) =>
                CompactIdentityRow(identity, index + 1)).ToArray();
            compact.AppendLine($"Shown plugin identities: {compactRows.Length} of {totalCount} (exact roots in receipt)");
            compact.Append(string.Join('\n', compactRows));
            var compactText = compact.ToString();
            if (JsonSerializer.SerializeToUtf8Bytes(compactText, Json).Length <= PluginInventoryProjectionBytes)
            {
                projection = new(compactText, true);
                return true;
            }
        }
        projection = new(selected, headerComplete && displayed == rows.Length && allDiscoveredRowsInThisResult);
        return true;

        static IEnumerable<JsonElement> Components(JsonElement project) =>
            project.TryGetProperty("components", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Where(component => component.ValueKind == JsonValueKind.Object)
                : [];

        static bool HasDiagnostic(JsonElement project) =>
            Text(project, "error") is not null || Components(project).Any(component => Text(component, "error") is not null);

        static string? Text(JsonElement owner, string field) =>
            owner.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;

        static bool TryBoundedInt(JsonElement owner, string field, int minimum, int maximum, out int value)
        {
            value = 0;
            return owner.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.Number &&
                element.TryGetInt32(out value) && value >= minimum && value <= maximum;
        }

        static bool ValidIdentity(JsonElement identity)
        {
            if (identity.ValueKind != JsonValueKind.Object ||
                string.IsNullOrWhiteSpace(Text(identity, "root")) ||
                string.IsNullOrWhiteSpace(Text(identity, "id")) ||
                string.IsNullOrWhiteSpace(Text(identity, "name")) ||
                !identity.TryGetProperty("components", out var components) ||
                components.ValueKind != JsonValueKind.Array || components.GetArrayLength() is < 1 or > 2)
                return false;
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var component in components.EnumerateArray())
            {
                if (component.ValueKind != JsonValueKind.Object) return false;
                var kind = Text(component, "component");
                if (kind is not ("desktop" or "service") || !kinds.Add(kind) ||
                    string.IsNullOrWhiteSpace(Text(component, "id")) ||
                    string.IsNullOrWhiteSpace(Text(component, "name")))
                    return false;
            }
            return true;
        }

        static bool PageRowsIncluded(JsonElement page, JsonElement identities)
        {
            foreach (var project in page.EnumerateArray())
            {
                var root = Text(project, "root");
                var id = Text(project, "id");
                var name = Text(project, "name");
                if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(id) ||
                    string.IsNullOrWhiteSpace(name) ||
                    !identities.EnumerateArray().Any(identity =>
                        string.Equals(Text(identity, "root"), root, StringComparison.Ordinal) &&
                        string.Equals(Text(identity, "id"), id, StringComparison.Ordinal) &&
                        string.Equals(Text(identity, "name"), name, StringComparison.Ordinal)))
                    return false;
            }
            return true;
        }

        static string ProjectRow(JsonElement project)
        {
            var fields = new List<string>();
            foreach (var field in new[] { ("Root", "root"), ("Plugin", "id"), ("Name", "name"), ("Version", "version"), ("Diagnostic", "error") })
                if (Text(project, field.Item2) is { } text)
                    fields.Add(field.Item1 + ": " + text);
            foreach (var component in Components(project))
            {
                if (Text(component, "component") is { } kind) fields.Add("Component: " + kind);
                if (Text(component, "error") is { } diagnostic) fields.Add("Component diagnostic: " + diagnostic);
            }
            // Metadata controls remain visible as escapes; ordinary filesystem paths are unchanged.
            return string.Join(" | ", fields).Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
        }

        static string IdentityRow(JsonElement project)
        {
            var fields = new List<string>();
            foreach (var field in new[] { ("Root", "root"), ("Plugin", "id"), ("Name", "name") })
                if (Text(project, field.Item2) is { } value) fields.Add(field.Item1 + ": " + value);
            foreach (var component in Components(project))
            {
                var id = Text(component, "id");
                var name = Text(component, "name");
                fields.Add("Component: " + (Text(component, "component") ?? "unknown"));
                if (id != Text(project, "id")) fields.Add("Component plugin: " + id);
                if (name != Text(project, "name")) fields.Add("Component name: " + name);
            }
            return string.Join(" | ", fields).Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
        }

        static string CompactIdentityRow(JsonElement project, int ordinal)
        {
            static string Quoted(string? value) => JsonSerializer.Serialize(value, Json);
            var pluginId = Text(project, "id");
            var pluginName = Text(project, "name");
            var components = Components(project).Select(component =>
            {
                var componentId = Text(component, "id");
                var componentName = Text(component, "name");
                var sameId = string.Equals(componentId, pluginId, StringComparison.Ordinal);
                var sameName = string.Equals(componentName, pluginName, StringComparison.Ordinal);
                var kind = Text(component, "component");
                if (sameId && sameName) return $"{kind} (same plugin ID/name)";
                return $"{kind} " + (sameId ? "(same plugin ID)" : "id=" + Quoted(componentId)) + " " +
                    (sameName ? "(same plugin name)" : "name=" + Quoted(componentName));
            });
            return $"{ordinal}. plugin id={Quoted(pluginId)} name={Quoted(pluginName)}; components: " +
                string.Join("; ", components);
        }
    }
}
