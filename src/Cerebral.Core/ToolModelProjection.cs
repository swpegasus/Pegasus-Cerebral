using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Cerebral.Core;

public sealed record ToolReadableProjection(string Text, bool Complete);

/// <summary>
/// Builds the small status receipt placed back into model context after a tool effect. The host retains
/// the exact effect result separately; this projection is deliberately optimized for the common case
/// where the model only needs to know whether one command completed and where to fetch diagnostics.
/// </summary>
public static partial class ToolModelProjection
{
    public const string DesktopPluginAuthoringContractId = "cerebral.desktop-authoring/v1";
    public const string DesktopPluginThemeGuidance = "DesktopThemeResourceKeys.TextBrush and MutedTextBrush are string resource keys, not Brush objects. Never assign or cast these keys to Foreground. Call textBlock.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.TextBrush) AFTER its initializer; use the same method with MutedTextBrush for secondary text. SetResourceReference is a method, not an initializer member. Buttons inherit host styles; use TextBlock with TextWrapping=Wrap for long labels. Thickness accepts one or four values, never two. Do not invent resource names or hard-code light/dark text colors.";
    public const string DesktopPluginStorageGuidance = "Commit a save to disk before publishing the remembered value in state or UI, or reporting success. A failed save keeps the last confirmed value and displays the actual error; do not clear that error or report success. Only while reading initial saved state, treat FileNotFoundException and DirectoryNotFoundException as normal empty first-use state. Every write exception is a failed save; never swallow it as first use or report Ready or success. Keep other read errors visible and preserve confirmed state.";
    public const string DesktopPluginAuthoringSnapshot = """
        Host-owned desktop plugin authoring contract v1:
        - The scaffold compiles with Nullable=enable and TreatWarningsAsErrors=true; warnings are build errors.
        - Derive the entry class from DesktopPluginBase. Keep a public parameterless constructor; the host constructs the entry and calls Initialize afterward, so do not use constructor dependency injection.
        - Preserve the scaffold manifest entryType and exact entry class namespace/name. A feature, panel or display name does not require renaming that class. If deliberately changing the entry type, update both source and source manifest, then rebuild. For a new named plugin when existing plugins must be preserved, use its own successful scaffold; existing projects are reference examples only.
        - For the basic case, inherit Initialize and Dispose. If an override is genuinely required for additional resources, call the corresponding base method: public override void Initialize(IDesktopPluginHost pluginHost) must call base.Initialize(pluginHost) before any Host or Environment access, including through helpers. A Dispose override must call base.Dispose(). Do not add an environment field or cast the host when deriving from DesktopPluginBase.
        - Define Panels only when the requested feature needs a sidebar. Panels is getter-only; never assign it in Initialize. Use public override IReadOnlyList<DesktopPanel> Panels => [new("main", "Title", CreateView)]; DesktopPanel is a descriptor: DesktopPanel(string id, string title, Func<FrameworkElement> createView). Its factory returns a concrete WPF FrameworkElement tree such as StackPanel, Grid, or UserControl.
        - WPF StackPanel has no Spacing property; use child Margin for spacing.
        - Create per-view controls as initialized local variables inside the view factory; use local or lambda handlers that capture those exact controls. Do not read view controls from Initialize: the view may not exist and may be recreated. If control fields are genuinely necessary, treat them as optional until the view is created and guard their use.
        - Use the inherited Environment only after initialization, such as inside the view factory or its event handlers. For private mutable text, compose a path beneath Environment.DataDirectory. DesktopPluginFiles exposes public static string ReadUtf8(string path, int maximumBytes) and public static void WriteUtf8Atomically(string path, string text, int maximumBytes); limits count UTF-8 bytes and must be 0 through 16 MiB. Choose a finite feature-appropriate bound, such as 1 MiB for a short note; int.MaxValue is rejected. PackageDirectory contains replaceable code, not mutable data; do not guess an operating-system data path.
        - Leave unrequested contribution collections inherited and empty. Do not redeclare SDK interfaces or lifecycle members merely to make an inert scaffold compile.
        """ + "\n- " + DesktopPluginStorageGuidance + "\n- " + DesktopPluginThemeGuidance;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaximumMessageCharacters = 512;
    private const int MaximumDiagnosticScanCharacters = 16 * 1024;
    private static readonly Regex DiagnosticSummary = new(
        @"(?im)^\s*(?<count>\d+)\s+(?<kind>Error|Warning)\(s\)\s*$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private static readonly HashSet<string> StatusOnlyTools = new(StringComparer.Ordinal)
    {
        "conversation.response-balance",
        "file.copy",
        "file.edit",
        "file.mkdir",
        "file.write",
        "process.run"
    };

    /// <summary>
    /// Unknown and plugin-defined tools keep their bounded payload by default. Only host-known commands
    /// whose ordinary result is a completion acknowledgement use the status-only projection.
    /// </summary>
    public static bool IncludesPayload(string toolName) => !StatusOnlyTools.Contains(toolName);

    /// <summary>
    /// The receipt header remains structured, but source and contract text are model input, not a
    /// JSON string to decode again. Durable effect results retain their original typed representation.
    /// </summary>
    public static string Render(ToolModelStatus status)
    {
        var header = JsonSerializer.Serialize(status with { Content = null, Message = null }, Json);
        var diagnostic = status.Message is null ? "" : "\nDiagnostic: " + status.Message;
        return header + diagnostic + (status.Content is null ? "" : "\nPayload:\n" + status.Content);
    }

    public static string ReadablePayload(string tool, string content) => ReadableProjection(tool, content).Text;

    /// <summary>
    /// Projects a retained tool result into the bounded text shown to the model. <see cref="ToolReadableProjection.Complete"/>
    /// describes this projected text, not the larger provider JSON envelope that remains available by receipt.
    /// </summary>
    public static ToolReadableProjection ReadableProjection(string tool, string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.TrimStart()[0] != '{') return new(content, true);
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (tool == "plugin.scaffold" && root.TryGetProperty("root", out _))
            {
                var readable = new StringBuilder();
                AppendReadable(readable, "Created plugin", root, "pluginId");
                AppendReadable(readable, "Name", root, "name");
                AppendReadable(readable, "Component", root, "component");
                AppendReadable(readable, "Source root", root, "root");
                AppendReadable(readable, "Read guide", root, "guide");
                AppendReadable(readable, "Required desktop entry shapes", root, "entryContract");
                foreach (var component in new[] { "desktop", "service" })
                    if (root.TryGetProperty(component, out var paths) && paths.ValueKind == JsonValueKind.Object)
                    {
                        AppendReadable(readable, component + " source", paths, "source");
                        AppendReadable(readable, component + " project", paths, "project");
                        AppendReadable(readable, component + " built manifest", paths, "builtManifest");
                        if (paths.TryGetProperty("build", out var build)) AppendBuild(readable, build);
                    }
                AppendReadable(readable, "Next", root, "next");
                return new(readable.ToString(), true);
            }
            if (tool == "plugin.projects" && TryReadablePluginProjectsPayload(root, out var inventory)) return inventory;
            if (tool == "plugin.controls" && TryReadableNativeControlPayload(root, out var nativeControl)) return nativeControl;
            if (tool == "file.list" && TryReadableDirectoryPayload(root, out var directory)) return directory;
            if (tool == "file.read" && root.TryGetProperty("content", out var source) && source.ValueKind == JsonValueKind.String)
                return ReadableFilePayload(root, source.GetString() ?? "");
            if (tool == "plugin.contract" && root.TryGetProperty("reference", out var reference) && reference.ValueKind == JsonValueKind.Object)
            {
                var readable = new StringBuilder();
                AppendReadable(readable, "Topic", root, "topic");
                AppendReadable(readable, "Contract", root, "contract");
                AppendReadable(readable, "API version", root, "apiVersion");
                AppendReadable(readable, "Summary", reference, "summary");
                AppendReadable(readable, "Theme", root, "theme");
                AppendReadable(readable, "Public signatures", reference, "signatures");
                AppendReadable(readable, "Complete example", reference, "example");
                AppendReadable(readable, "Editing guidance", root, "editing");
                return new(readable.ToString(), true);
            }
        }
        catch (JsonException)
        {
            // Providers may return ordinary text; never reinterpret escapes inside that text.
        }
        return new(content, true);
    }

    private static bool TryReadableDirectoryPayload(JsonElement root, out ToolReadableProjection projection)
    {
        projection = new("", false);
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("skipped", out var skipped) || !skipped.TryGetInt32(out var skippedCount) || skippedCount < 0 ||
            !root.TryGetProperty("visited", out var visited) || !visited.TryGetInt32(out var visitedCount) || visitedCount < 0)
            return false;

        var lines = new List<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                path.GetString() is not { } text || text.Any(char.IsControl) ||
                !entry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return false;
            if (type.GetString() == "directory") lines.Add("DIR " + text);
            else if (type.GetString() == "file" && entry.TryGetProperty("bytes", out var bytes) &&
                     bytes.TryGetInt64(out var byteCount) && byteCount >= 0)
                lines.Add("FILE " + text + " (" + byteCount.ToString(CultureInfo.InvariantCulture) + " bytes)");
            else return false;
        }

        string RenderEntries(int count) =>
            "Directory entries: " + count + " of " + lines.Count + "; visited: " + visitedCount + "; skipped: " + skippedCount +
            (count < lines.Count
                ? "\nProjection incomplete: remaining returned entries are available in this receipt; inspect a narrower directory before editing."
                : "\nProjection includes all returned entries; the receipt status reports any traversal limit.") +
            (count == 0 ? "" : "\n" + string.Join('\n', lines.Take(count)));

        var selected = RenderEntries(0);
        var displayed = 0;
        for (var count = 1; count <= lines.Count; count++)
        {
            var candidate = RenderEntries(count);
            // Preserve whole paths and an honest omission marker within the durable text envelope.
            if (JsonSerializer.SerializeToUtf8Bytes(candidate, Json).Length > 6 * 1024) break;
            selected = candidate;
            displayed = count;
        }
        projection = new(selected, displayed == lines.Count);
        return true;
    }

    private static ToolReadableProjection ReadableFilePayload(JsonElement root, string source)
    {
        var startLine = root.TryGetProperty("startLine", out var start) && start.TryGetInt32(out var parsedStart)
            ? parsedStart : 1;
        // The producer counts terminal empty segments as line coordinates. Preserve them so
        // displayed ranges and continuation cover all returned normalized source text.
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var totalLines = root.TryGetProperty("totalLines", out var total) && total.TryGetInt32(out var parsedTotal)
            ? parsedTotal : startLine + Math.Max(0, lines.Length - 1);

        string RenderLines(int count)
        {
            var endLine = startLine + Math.Max(0, count - 1);
            var returnedLinesOmitted = count < lines.Length;
            var readable = new StringBuilder();
            if (root.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String) readable.AppendLine("Path: " + path.GetString());
            if (root.TryGetProperty("sha256", out var hash) && hash.ValueKind == JsonValueKind.String) readable.AppendLine("SHA-256: " + hash.GetString());
            readable.AppendLine($"Displayed source lines: {startLine}-{endLine} of {totalLines}");
            if (returnedLinesOmitted)
                readable.AppendLine($"Continuation: call file.read with startLine={endLine + 1} and lineCount={Math.Min(60, Math.Max(1, totalLines - endLine))}. Do not reread this range.");
            else if (endLine < totalLines)
                readable.AppendLine($"Additional file lines {endLine + 1}-{totalLines} exist outside this returned range; read only if the accepted request needs them.");
            else readable.AppendLine("Continuation: none; this requested source range is complete.");
            readable.AppendLine("Source:");
            readable.Append(string.Join('\n', lines.Take(count)));
            return readable.ToString();
        }

        if (lines.Length == 0) return new(RenderLines(0), true);
        var selected = RenderLines(1);
        var selectedLines = 1;
        for (var count = 2; count <= lines.Length; count++)
        {
            var candidate = RenderLines(count);
            // Bound the serialized durable envelope as well as the readable model projection.
            if (JsonSerializer.SerializeToUtf8Bytes(candidate, Json).Length > 6 * 1024) break;
            selected = candidate;
            selectedLines = count;
        }
        return new(selected, selectedLines == lines.Length);
    }

    private static void AppendBuild(StringBuilder builder, JsonElement build)
    {
        if (build.ValueKind != JsonValueKind.Object) return;
        AppendReadable(builder, "Executable ID", build, "executableId");
        AppendReadable(builder, "Working directory", build, "workingDirectory");
        AppendReadable(builder, "Expected built manifest", build, "builtManifest");
        if (build.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Array)
        {
            builder.AppendLine("Separate arguments:");
            foreach (var argument in arguments.EnumerateArray())
                if (argument.ValueKind == JsonValueKind.String) builder.AppendLine(argument.GetString());
        }
    }

    private static void AppendReadable(StringBuilder builder, string label, JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var value)) return;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };
        if (text is null) return;
        if (text.Contains('\n') || text.Contains('\r'))
        {
            builder.AppendLine(label + ":");
            builder.AppendLine(text);
        }
        else builder.AppendLine(label + ": " + text);
    }

    public static ToolModelStatus Create(string receipt, string tool, string effectState, string content,
        bool truncated, ToolExecutionEvidence? evidence, string? error, string? projectedContent = null,
        bool projectedContentTruncated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(content);

        var status = Status(effectState, evidence?.Outcome);
        var (errors, warnings) = DiagnosticCounts(content);
        return new(receipt, tool, status,
            CommandsCompleted: status == "completed" ? 1 : 0,
            CommandsFailed: status is "failed" or "rejected" ? 1 : 0,
            ExitCode: evidence?.ExitCode,
            Errors: errors,
            Warnings: warnings,
            Partial: truncated || evidence?.Partial == true,
            DetailsAvailable: true,
            Message: SelectMessage(status, error, content, evidence),
            Content: projectedContent,
            ContentTruncated: projectedContent is null ? null : projectedContentTruncated)
        {
            Sha256 = status == "completed" && !truncated && evidence?.Partial != true &&
                tool is "file.write" or "file.edit" ? MutationHash(content) : null
        };
    }

    private static string? MutationHash(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("sha256", out var value) || value.ValueKind != JsonValueKind.String)
                return null;
            var hash = value.GetString();
            return hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit) ? hash : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Status(string effectState, ToolExecutionOutcome? outcome)
    {
        if (effectState.Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
            return outcome is null or ToolExecutionOutcome.Succeeded ? "completed" : "unconfirmed";
        if (effectState.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            return outcome == ToolExecutionOutcome.Rejected ? "rejected" : "failed";
        if (effectState.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)) return "cancelled";
        if (effectState.Equals("Prepared", StringComparison.OrdinalIgnoreCase) ||
            effectState.Equals("Dispatched", StringComparison.OrdinalIgnoreCase))
            return "in_progress";
        return "unconfirmed";
    }

    private static (int? Errors, int? Warnings) DiagnosticCounts(string content)
    {
        int? errors = null, warnings = null;
        ScanText(BoundedTail(content), ref errors, ref warnings);
        if (content.Length == 0 || content[0] is not ('{' or '[')) return (errors, warnings);
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
            ScanJson(document.RootElement, ref errors, ref warnings, 0);
        }
        catch (JsonException)
        {
            // Ordinary command output is not required to be JSON.
        }
        return (errors, warnings);
    }

    private static void ScanJson(JsonElement value, ref int? errors, ref int? warnings, int depth)
    {
        if (depth > 16) return;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var count) && count >= 0)
                {
                    if (property.NameEquals("errorCount") || property.NameEquals("errors")) errors = count;
                    if (property.NameEquals("warningCount") || property.NameEquals("warnings")) warnings = count;
                }
                ScanJson(property.Value, ref errors, ref warnings, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) ScanJson(item, ref errors, ref warnings, depth + 1);
        }
        else if (value.ValueKind == JsonValueKind.String)
            ScanText(BoundedTail(value.GetString() ?? ""), ref errors, ref warnings);
    }

    private static void ScanText(string text, ref int? errors, ref int? warnings)
    {
        try
        {
            foreach (Match match in DiagnosticSummary.Matches(text))
            {
                if (!int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                    continue;
                if (match.Groups["kind"].Value.Equals("Error", StringComparison.OrdinalIgnoreCase)) errors = count;
                else warnings = count;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Counts are optional. A timeout must never prevent the actual receipt from being retained.
        }
    }

    private static string BoundedTail(string text) => text.Length <= MaximumDiagnosticScanCharacters
        ? text : text[^MaximumDiagnosticScanCharacters..];

    private static string? CompactMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var compact = message.Trim().Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return compact.Length <= MaximumMessageCharacters ? compact : compact[..MaximumMessageCharacters];
    }

    private static string? SelectMessage(string status, string? error, string content, ToolExecutionEvidence? evidence)
    {
        // The workflow bridge may preserve these diagnostics separately before shortening
        // provider JSON. Keep its bounded distinct lines instead of flattening them to one.
        var compactError = FirstActionableDiagnostic(error ?? "") ?? CompactMessage(error);
        if (status is not ("failed" or "rejected")) return compactError;
        var diagnostic = FirstActionableDiagnostic(content) ?? ConfirmedStructuredFailureDiagnostic(content, evidence);
        // Closed tool validation often returns a plain sentence, not a compiler-shaped line.
        // Keep that correction rather than replacing it with a generic receipt lookup message.
        var trimmed = content.TrimStart();
        if (diagnostic is null && trimmed.Length > 0 && trimmed[0] is not ('{' or '['))
            diagnostic = CompactMessage(content);
        // A bridge may have retained only the first error line. Prefer the bounded
        // same-error detail when it also identifies the invalid switch.
        return diagnostic is not null && (compactError is null || IsGenericRetainedResultMessage(compactError) ||
            diagnostic.StartsWith(compactError + "\n", StringComparison.Ordinal))
            ? diagnostic
            : compactError;
    }

    private static bool IsGenericRetainedResultMessage(string message) =>
        message.Contains("Inspect its retained result", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Inspect the retained result", StringComparison.OrdinalIgnoreCase);

    private static string? FirstActionableDiagnostic(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        if (content.TrimStart() is { } trimmed && trimmed.Length > 0 && trimmed[0] is '{' or '[')
        {
            try
            {
                using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
                var diagnostic = FirstActionableDiagnostic(document.RootElement, 0);
                if (diagnostic is not null) return diagnostic;
            }
            catch (JsonException)
            {
                // Fall through to the ordinary text scan.
            }
        }
        return FirstActionableLine(content);
    }

    private static string? FirstActionableDiagnostic(JsonElement value, int depth)
    {
        if (depth > 16) return null;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var diagnostic = FirstActionableDiagnostic(property.Value, depth + 1);
                if (diagnostic is not null) return diagnostic;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var diagnostic = FirstActionableDiagnostic(item, depth + 1);
                if (diagnostic is not null) return diagnostic;
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
            return FirstActionableLine(value.GetString() ?? "");
        return null;
    }

    private static string? FirstActionableLine(string text)
    {
        var bounded = text.Length <= MaximumDiagnosticScanCharacters
            ? text
            : text[..MaximumDiagnosticScanCharacters];
        var diagnostics = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var awaitingInvalidSwitch = false;
        foreach (var line in bounded.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (awaitingInvalidSwitch && line.StartsWith("Switch:", StringComparison.OrdinalIgnoreCase))
            {
                // MSBuild prints the rejected switch on a separate line. The full
                // command line stays in the retained receipt, outside model context.
                if (seen.Add(line)) diagnostics.Add(CompactMessage(line)!);
                awaitingInvalidSwitch = false;
                if (diagnostics.Count == 4) break;
                continue;
            }
            if (line.Contains(": error ", StringComparison.OrdinalIgnoreCase) ||
                line.Contains(" error CS", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("error ", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Missing tool argument", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("path is outside", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("access is denied", StringComparison.OrdinalIgnoreCase))
            {
                // MSBuild repeats each error in its summary and appends the project path to
                // every source location. Preserve unique source diagnostics, not that noise.
                var actionable = line;
                awaitingInvalidSwitch = line.Contains("error MSB1001: Unknown switch.", StringComparison.OrdinalIgnoreCase);
                var projectSuffix = actionable.LastIndexOf(" [", StringComparison.Ordinal);
                if (projectSuffix >= 0 && (actionable.EndsWith(".csproj]", StringComparison.OrdinalIgnoreCase) ||
                    actionable.EndsWith(".fsproj]", StringComparison.OrdinalIgnoreCase) ||
                    actionable.EndsWith(".vbproj]", StringComparison.OrdinalIgnoreCase)))
                    actionable = actionable[..projectSuffix];
                if (seen.Add(actionable)) diagnostics.Add(CompactMessage(actionable)!);
                if (diagnostics.Count == 4) break;
            }
        }
        return diagnostics.Count == 0 ? null : string.Join("\n", diagnostics);
    }
}

public sealed record ToolModelStatus(
    string Receipt,
    string Tool,
    string Status,
    int CommandsCompleted,
    int CommandsFailed,
    int? ExitCode,
    int? Errors,
    int? Warnings,
    bool Partial,
    bool DetailsAvailable,
    string? Message,
    string? Content,
    bool? ContentTruncated)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Sha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ExitCode { get; init; } = ExitCode;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Errors { get; init; } = Errors;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Warnings { get; init; } = Warnings;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Message { get; init; } = Message;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Content { get; init; } = Content;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ContentTruncated { get; init; } = ContentTruncated;
}
