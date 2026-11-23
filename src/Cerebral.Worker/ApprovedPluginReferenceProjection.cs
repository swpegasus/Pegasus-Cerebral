using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>
/// Adds a reference to an already owner-approved package to one exact SDK project inventory.
/// The reference is discovery metadata only; interface.status and plugin.controls retain their
/// normal authorization and current-package checks.
/// </summary>
internal static class ApprovedPluginReferenceProjection
{
    internal const string PropertyName = "approvedControlReference";
    private const int MaximumInterfaceRequests = 100;
    private const int MaximumToolContentBytes = 32_768;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record Inventory(string Root, string SourceManifest, string Id, string Version);
    private sealed record Reference(int SchemaVersion, string RequestId, string Status, string SourceRoot,
        string SourceManifest, string PackageFingerprint, IReadOnlyList<string> MountIds);

    internal static ToolResult Project(StateStore store, string toolName, JsonElement arguments,
        ToolExecutionContext context, ToolResult result, bool reconcile, CancellationToken cancellationToken)
    {
        if (toolName != "plugin.projects" || !result.Success || result.Truncated ||
            result.Evidence is not { Outcome: ToolExecutionOutcome.Succeeded, ProviderId: "cerebral.plugin-sdk" } evidence ||
            evidence.Partial == true || evidence.State != "inventoried")
            return result;

        JsonElement providerBody;
        string sanitizedContent;
        bool hadReservedProperty;
        try
        {
            if (!TryReadBody(result.Content, out providerBody, out sanitizedContent, out hadReservedProperty))
                return result;
        }
        catch (Exception error) when (Ordinary(error))
        {
            // An invalid SDK inventory cannot be adopted by Conversation and needs no host decoration.
            return result;
        }

        // This property is host-owned. Once a complete inventory has been parsed, never return a value
        // supplied by the provider, including when optional package validation or cancellation fails.
        var sanitized = hadReservedProperty ? result with { Content = sanitizedContent } : result;
        Inventory inventory;
        try
        {
            if (!TryReadInventory(arguments, providerBody, out inventory)) return sanitized;
        }
        catch (Exception error) when (Ordinary(error))
        {
            return sanitized;
        }
        if (reconcile || cancellationToken.IsCancellationRequested) return sanitized;

        Reference? reference;
        try { reference = FindReference(store, context, inventory, cancellationToken); }
        catch (Exception error) when (Ordinary(error))
        {
            // An optional stale or malformed reference never changes the confirmed SDK inventory outcome.
            return sanitized;
        }
        if (reference is null) return sanitized;

        try
        {
            var body = JsonNode.Parse(sanitized.Content)!.AsObject();
            body[PropertyName] = JsonSerializer.SerializeToNode(reference, CerebralJson.Options);
            var content = body.ToJsonString(CerebralJson.Options);
            // The provider's complete inventory remains useful if the optional host projection does not fit.
            if (System.Text.Encoding.UTF8.GetByteCount(content) > MaximumToolContentBytes)
                return sanitized;
            return result with { Content = content };
        }
        catch (Exception error) when (Ordinary(error))
        {
            return sanitized;
        }
    }

    private static Reference? FindReference(StateStore store, ToolExecutionContext context, Inventory inventory,
        CancellationToken cancellationToken)
    {
        var requests = new InterfaceRequests(store, new EventHub());
        var candidates = requests.List(context.SessionId, MaximumInterfaceRequests);
        // Hitting the page bound cannot prove uniqueness; fail closed rather than selecting a recent row.
        if (candidates.Count == MaximumInterfaceRequests) return null;

        Reference? found = null;
        foreach (var listed in candidates)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            if (listed.Source != "workflow" || listed.Kind != "plugin" || listed.Status != "completed" ||
                listed.Error is not null || listed.FailureStage is not null ||
                !SamePath(listed.GeneratedSourceRoot, inventory.Root) ||
                !SamePath(listed.Path, inventory.SourceManifest) ||
                !ValidFingerprint(listed.PackageFingerprint))
                continue;

            try
            {
                _ = requests.GetForContext(listed.Id, context,
                    InterfaceToolExtension.StatusToolName, "filesystem.read");
                var current = requests.GetForContext(listed.Id, context,
                    "plugin.controls", "desktop.plugins.manage");
                if (current.Source != "workflow" || current.Kind != "plugin" || current.Status != "completed" ||
                    current.Error is not null || current.FailureStage is not null ||
                    !SamePath(current.GeneratedSourceRoot, inventory.Root) ||
                    !SamePath(current.Path, inventory.SourceManifest) ||
                    !ValidFingerprint(current.PackageFingerprint) ||
                    current.Plugin is not { SchemaVersion: 1, ControlTestsAllowed: true } plugin)
                    continue;

                var installed = InterfacePluginReceiptPolicy.Validate(current, plugin);
                if (installed.Id != inventory.Id || installed.Version != inventory.Version ||
                    installed.Mounts.Count == 0 ||
                    NativeDesktopPluginPackage.ComputeFingerprint(current.Path) != current.PackageFingerprint ||
                    NativeDesktopPluginPackage.ComputeFingerprint(installed.ManifestPath) != current.PackageFingerprint)
                    continue;
                if (cancellationToken.IsCancellationRequested) return null;

                var candidate = new Reference(1, current.Id, "completed", inventory.Root,
                    inventory.SourceManifest, current.PackageFingerprint!,
                    installed.Mounts.Select(mount => mount.Id).ToArray());
                if (found is not null) return null;
                found = candidate;
            }
            catch (Exception error) when (Ordinary(error))
            {
                // Retired generations, revoked grants and damaged retained rows are not usable references.
            }
        }
        return found;
    }

    private static bool TryReadBody(string content, out JsonElement root, out string sanitizedContent,
        out bool hadReservedProperty)
    {
        root = default; sanitizedContent = content; hadReservedProperty = false;
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
        root = document.RootElement.Clone();
        hadReservedProperty = root.EnumerateObject().Any(property => property.Name == PropertyName);
        if (!hadReservedProperty) return true;
        using var output = new MemoryStream();
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == PropertyName) continue;
            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }
        writer.WriteEndObject(); writer.Flush();
        sanitizedContent = System.Text.Encoding.UTF8.GetString(output.ToArray());
        return true;
    }

    private static bool TryReadInventory(JsonElement arguments, JsonElement root, out Inventory inventory)
    {
        inventory = null!;
        if (root.ValueKind != JsonValueKind.Object || !UniqueProperties(root) ||
            arguments.ValueKind != JsonValueKind.Object) return false;
        var argumentProperties = arguments.EnumerateObject().ToArray();
        if (argumentProperties.Length != 1 || argumentProperties[0].Name != "root" ||
            argumentProperties[0].Value.ValueKind != JsonValueKind.String ||
            !TryCanonicalPath(argumentProperties[0].Value.GetString(), out var requestedRoot))
            return false;

        if (!Boolean(root, "discoverySummaryOnly", false) || !Boolean(root, "limited", false) ||
            !Integer(root, "count", 1) || !Null(root, "incompleteReason") ||
            !Null(root, "targetPath") || !Null(root, "targetMatched") || !Null(root, "targetComponent") ||
            !Text(root, "desktopAuthoringContract", ToolModelProjection.DesktopPluginAuthoringContractId) ||
            !root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array ||
            projects.GetArrayLength() != 1)
            return false;

        var project = projects[0];
        if (project.ValueKind != JsonValueKind.Object || !UniqueProperties(project) ||
            !TryText(project, "root", out var sourceRoot) || !TryCanonicalPath(sourceRoot, out sourceRoot) ||
            !SamePath(sourceRoot, requestedRoot) || !TryText(project, "id", out var projectId) ||
            !TryText(project, "name", out _) || !TryText(project, "version", out var projectVersion) ||
            !Null(project, "error") || !project.TryGetProperty("components", out var components) ||
            components.ValueKind != JsonValueKind.Array || components.GetArrayLength() is < 1 or > 8)
            return false;

        JsonElement desktop = default; var desktopCount = 0;
        foreach (var component in components.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object || !UniqueProperties(component) ||
                !TryText(component, "component", out var componentName))
                return false;
            if (componentName == "desktop") { desktop = component; desktopCount++; }
        }
        if (desktopCount != 1 || !Null(desktop, "error") ||
            !Text(desktop, "id", projectId) || !Text(desktop, "version", projectVersion) ||
            !TryText(desktop, "name", out _) ||
            !TryText(desktop, "manifest", out var sourceDefinition) || !TryCanonicalPath(sourceDefinition, out sourceDefinition) ||
            !TryText(desktop, "source", out var sourceFile) || !TryCanonicalPath(sourceFile, out sourceFile) ||
            !Within(sourceDefinition, sourceRoot) || !Within(sourceFile, sourceRoot) ||
            !desktop.TryGetProperty("build", out var build) || build.ValueKind != JsonValueKind.Object || !UniqueProperties(build) ||
            !TryText(build, "executableId", out _) || !TryText(build, "workingDirectory", out var workingDirectory) ||
            !TryCanonicalPath(workingDirectory, out workingDirectory) || !Within(workingDirectory, sourceRoot) ||
            !TryText(build, "builtManifest", out var builtManifest) || !TryCanonicalPath(builtManifest, out builtManifest) ||
            !Within(builtManifest, sourceRoot) ||
            !Path.GetFileName(builtManifest).Equals(NativeDesktopPluginPackage.ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
            !build.TryGetProperty("arguments", out var buildArguments) || buildArguments.ValueKind != JsonValueKind.Array ||
            buildArguments.GetArrayLength() is < 1 or > 16 ||
            buildArguments.EnumerateArray().Any(argument => argument.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(argument.GetString())))
            return false;

        inventory = new(sourceRoot, builtManifest, projectId, projectVersion);
        return true;
    }

    private static bool UniqueProperties(JsonElement value)
    {
        var properties = value.EnumerateObject().ToArray();
        return properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == properties.Length;
    }

    private static bool TryText(JsonElement value, string name, out string text)
    {
        text = "";
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString())) return false;
        text = property.GetString()!;
        return text.Length <= 32_768 && !text.Any(char.IsControl);
    }

    private static bool Text(JsonElement value, string name, string expected) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        property.GetString() == expected;
    private static bool Boolean(JsonElement value, string name, bool expected) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);
    private static bool Integer(JsonElement value, string name, int expected) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var actual) && actual == expected;
    private static bool Null(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Null;

    private static bool TryCanonicalPath(string? value, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32_768 || value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) || value.Split(['/', '\\']).Any(part => part is "." or ".."))
            return false;
        try { canonical = Path.GetFullPath(value); }
        catch (Exception error) when (Ordinary(error)) { return false; }
        return canonical.Equals(value, PathComparison);
    }

    private static bool SamePath(string? left, string? right) => left is not null && right is not null &&
        left.Equals(right, PathComparison);

    private static bool Within(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathFullyQualified(relative);
    }

    private static bool ValidFingerprint(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Ordinary(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
