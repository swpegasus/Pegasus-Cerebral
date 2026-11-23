using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Contracts;

namespace Cerebral.Desktop;

public sealed partial class WorkflowWorkspace
{
    private readonly Dictionary<string, Expander> activityEntries = new(StringComparer.Ordinal);

    /// <summary>Owner UI inspection of one currently rendered journal entry; it never executes a tool.</summary>
    public void SetActivityDetailExpanded(string eventId, bool isExpanded)
    {
        if (disposed || !activityEntries.TryGetValue(eventId, out var entry) || !entry.IsDescendantOf(this))
            throw new InvalidOperationException("The activity entry is not in this conversation's current rendered window.");
        foreach (var view in activityViews.Where(view => view.Scroller.IsAncestorOf(entry)))
        {
            if (entry.Visibility != Visibility.Visible) { view.ReviewHistory = true; ApplyCompactActivity(view); }
            view.InputRevision++;
            view.Restoring = false;
            view.FollowEnd = false;
        }
        entry.IsExpanded = isExpanded;
        UpdateLayout();
        if (isExpanded) entry.BringIntoView(new Rect(0, 0, Math.Max(1, entry.ActualWidth), 24));
    }

    private const int MaximumActivityDetailCharacters = 16_384;
    private static readonly HashSet<string> ActivityCredentialKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "pwd", "secret", "clientsecret", "token", "accesstoken",
        "refreshtoken", "accesskey", "apikey", "authorization", "proxyauthorization",
        "cookie", "setcookie", "privatekey", "credential", "credentials", "connectionstring"
    };
    private static readonly HashSet<string> ActivitySecretKeys = new(ActivityCredentialKeys, StringComparer.OrdinalIgnoreCase)
    { "configuration", "config", "settings", "environment", "env" };
    private static readonly HashSet<string> ActivityDisplayFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "src", "dst", "source", "destination", "workingDirectory", "manifestPath",
        "projectPath", "projectDirectory", "executableId", "toolId", "operation", "op", "name",
        "id", "callId", "receiptId", "responseId", "providerReceiptId", "requestId", "jobId", "jid", "rid",
        "skillId", "skillName", "workflowId", "definitionId", "definitionVersion", "modelId", "adapterId", "workerId",
        "status", "state", "outcome", "success", "exitCode", "exit", "timeoutSeconds", "tmo",
        "startLine", "lineCount", "line", "count", "offset", "length", "bytes", "characters",
        "maxCharacters", "maxResults", "recursive", "overwrite", "encoding", "hash", "sha", "sha256",
        "message", "summary", "error", "reason", "content", "text", "stdout", "stderr", "out", "err",
        "truncated", "out_truncated", "err_truncated", "partial", "timedOut", "followUpRequired", "exists", "kind", "type", "providerId", "totalLines",
        "files", "entries", "items", "matches", "data", "result", "evidence", "names", "diff"
    };

    internal static string ActivityCommandText(CerebralEvent item)
    {
        if (!ActivityCommand(item, out var tool, out var arguments, out var outer)) return "";
        var lines = new List<string> { "Operation: " + FriendlyToolLabel(tool, arguments), "Recorded command: " + tool };
        // Generation arguments contain the entire prepared context, and are never UI command detail.
        if (tool is "cerebral.host:generate" or "cerebral.host:compact" or "cerebral.host:history")
        {
            lines.Add(tool == "cerebral.host:generate" ? "Input: accepted conversation and selected tools. The prepared prompt is hidden in this activity view for privacy." :
                "Input: retained conversation history. Its content is hidden in this activity view for privacy.");
            if (arguments.ValueKind == JsonValueKind.Object)
                foreach (var field in new[] { "modelId", "workerId", "maxTokens", "beforeSequence", "eventId", "offset" })
                    AddActivityValue(field, arguments, lines);
            return BoundActivityText(string.Join("\n", lines), ActivitySecrets(outer));
        }
        var secrets = ActivitySecrets(outer);
        AddActivityFields(arguments, lines, secrets, 0, command: true);
        if (outer.ValueKind == JsonValueKind.Object && outer.TryGetProperty("workingDirectory", out var cwd) &&
            cwd.ValueKind == JsonValueKind.String && !lines.Any(line => line.StartsWith("Working directory:", StringComparison.Ordinal)))
            lines.Add("Working directory: " + cwd.GetString());
        return BoundActivityText(string.Join("\n", lines), secrets);
    }

    internal static string ActivityResultText(CerebralEvent item, CerebralEvent? call = null)
    {
        var lines = new List<string>();
        var secrets = call?.Data is { } callData ? ActivitySecrets(callData) : [];
        var tool = item.Data is { ValueKind: JsonValueKind.Object } eventData &&
            eventData.TryGetProperty("toolName", out var toolName) && toolName.ValueKind == JsonValueKind.String
                ? toolName.GetString() : null;
        var command = item.Content.OfType<StructuredContentPart>()
            .FirstOrDefault(part => part.Schema == "workflow.command.result");
        if (command?.Value is { ValueKind: JsonValueKind.Object } value)
        {
            // Returned credentials can be echoed elsewhere in the same receipt, before their named field.
            IncludeActivitySecrets(value, secrets);
            foreach (var field in new[] { "state", "error", "providerReceiptId" })
                AddActivityValue(field, value, lines);
            if (value.TryGetProperty("data", out var resultData) && resultData.ValueKind == JsonValueKind.Object)
            {
                // Only actual tool results expose their returned content. Prepared generation,
                // history, and compaction payloads can include private model/context internals.
                if (tool == "cerebral.host:tool")
                    AddActivityFields(resultData, lines, secrets, 0, command: false);
                else if (tool == "cerebral.host:generate")
                {
                    if (resultData.TryGetProperty("prepared", out var prepared) && prepared.ValueKind == JsonValueKind.Object)
                        foreach (var field in new[] { "modelId", "adapterId", "inputTokens", "effectiveContextWindow" })
                            AddActivityValue(field, prepared, lines);
                    if (GenerationUsage(item) is { } usage)
                    {
                        if (usage.Input is { } input && usage.Output is { } output)
                            lines.Add($"Tokens: {input:N0} input / {output:N0} output");
                        else if (usage.Input is { } measuredInput) lines.Add($"Tokens: {measuredInput:N0} input");
                        else if (usage.Output is { } measuredOutput) lines.Add($"Tokens: {measuredOutput:N0} output");
                    }
                    foreach (var field in new[] { "elapsedMilliseconds", "firstTokenMilliseconds", "outputOmitted" })
                        AddActivityValue(field, resultData, lines);
                    if (resultData.TryGetProperty("generation", out var generation) && generation.ValueKind == JsonValueKind.Object &&
                        generation.TryGetProperty("finish", out var finish) && finish.ValueKind == JsonValueKind.String)
                    {
                        var recordedFinish = finish.GetString();
                        lines.Add(recordedFinish is "Length" or "length" ? "Generation stopped at its response limit; this attempt is incomplete." : "Generation finish: " + recordedFinish);
                        if (generation.TryGetProperty("text", out var publicText) && publicText.ValueKind == JsonValueKind.String &&
                            publicText.GetString()?.Length == 0)
                            lines.Add("No public response was adopted from this generation.");
                    }
                }
                else
                    foreach (var field in new[] { "message", "summary", "receiptId", "responseId", "beforeTokens", "afterTokens" })
                        AddActivityValue(field, resultData, lines);
            }
        }
        if (item.Data is { ValueKind: JsonValueKind.Object } details)
        {
            IncludeActivitySecrets(details, secrets);
            foreach (var field in new[] { "message", "error", "reason", "durationMilliseconds", "truncated" })
                AddActivityValue(field, details, lines);
        }
        return BoundActivityText(string.Join("\n", lines.Distinct(StringComparer.Ordinal)), secrets);
    }

    /// <summary>A compact view of recorded operation evidence, never model reasoning or inferred intent.</summary>
    internal static string ActivityInlineDetail(ActivityRow row)
    {
        if (row.Event.Type == CerebralEventTypes.ToolCall)
        {
            var command = ActivityCommandText(row.Event).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // Reuse the allowlisted command formatter; source bodies and secret values stay omitted.
            var targets = command.Skip(1).Where(line => !line.StartsWith("Recorded command:", StringComparison.Ordinal) &&
                !line.Contains("[File text omitted;", StringComparison.Ordinal)).ToArray();
            var detail = targets.Length > 0 ? string.Join(" | ", targets) :
                command.FirstOrDefault()?.Replace("Operation: ", "", StringComparison.Ordinal) ?? "No command target was recorded.";
            return BoundActivitySummary("Operation: " + detail);
        }
        if (row.Event.Type == CerebralEventTypes.ToolResult)
            return BoundActivitySummary("Result: " + ActivityResultExcerpt(ActivityResultText(row.Event, row.Call)));

        if (ActivityAuditText(row.Event) is { } audit) return BoundActivitySummary("Review: " + audit);

        var lines = new List<string>();
        if (row.Event.Data is { ValueKind: JsonValueKind.Object } data)
            foreach (var field in new[] { "error", "message", "summary", "reason" }) AddActivityValue(field, data, lines);
        foreach (var part in row.Event.Content.OfType<TextContentPart>())
            if (!string.IsNullOrWhiteSpace(part.Text)) lines.Add(part.Text);
        var recorded = BoundActivityText(string.Join("\n", lines), []);
        return BoundActivitySummary("Activity detail: " + ActivityResultExcerpt(recorded));
    }

    /// <summary>Public recorded audit fields only; never serialize the request, proposal, result envelope or model context.</summary>
    internal static string? ActivityAuditText(CerebralEvent item)
    {
        if (item.Type != "workflow.audit") return null;
        try
        {
            if (item.Data is not { ValueKind: JsonValueKind.Object } data || !AuditObject(data))
                return "Audit detail: Malformed or missing recorded audit data.";
            var secrets = ActivitySecrets(data);
            var lines = new List<string>();
            lines.Add(data.TryGetProperty("held", out var held) ? held.ValueKind switch
            {
                JsonValueKind.True => "Review effect: Workflow held",
                JsonValueKind.False => "Review effect: Did not block the workflow",
                _ => "Review effect: Unknown (malformed recorded value)"
            } : "Review effect: Not recorded");
            if (data.TryGetProperty("result", out var result) && AuditObject(result))
            {
                AddText(result, "disposition", "Disposition", 128);
                AddText(result, "summary", "Summary");
            }
            else lines.Add("Audit result: Malformed or missing recorded result.");
            // Preserve existing public diagnostics on older/failed records; objects remain omitted.
            foreach (var field in new[] { "error", "message", "summary", "reason" })
                if (data.TryGetProperty(field, out _)) AddText(data, field, ActivityFieldLabel(field));
            AddText(data, "purpose", "Checkpoint", 512);
            return BoundActivityText(string.Join("\n", lines), secrets);

            void AddText(JsonElement value, string field, string label, int maximum = int.MaxValue)
            {
                if (!value.TryGetProperty(field, out var part) || part.ValueKind == JsonValueKind.Null)
                {
                    lines.Add(label + ": Not recorded");
                    return;
                }
                if (part.ValueKind != JsonValueKind.String)
                {
                    lines.Add(label + ": Malformed recorded text");
                    return;
                }
                var text = part.GetString();
                if (string.IsNullOrWhiteSpace(text)) lines.Add(label + ": Not recorded");
                else if (text.Length > maximum) lines.Add(label + ": Recorded value exceeds its display limit");
                else lines.Add(label + ": " + BoundActivityText(text, secrets));
            }
        }
        catch (InvalidOperationException) { return "Audit detail: Malformed recorded JSON text."; }
        catch (ArgumentException) { return "Audit detail: Malformed recorded JSON text."; }

        static bool AuditObject(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (names.Count >= 64 || !names.Add(property.Name)) return false;
            return true;
        }
    }

    private static string ActivityResultExcerpt(string formatted)
    {
        var lines = formatted.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).ToArray();
        if (lines.Length == 0) return "No additional detail was recorded.";
        // Prefer an actual diagnostic or finding over receipt IDs and lifecycle bookkeeping.
        foreach (var prefix in new[] { "Error output:", "Error:", "Message:", "Summary:", "Output:", "Tokens:" })
        {
            var index = Array.FindIndex(lines, line => line.StartsWith(prefix, StringComparison.Ordinal));
            if (index < 0) continue;
            var detail = lines[index];
            if (detail.Length == prefix.Length && index + 1 < lines.Length) detail += " " + lines[index + 1];
            return PlainActivityExcerpt(detail, prefix.Length);
        }
        var first = lines.FirstOrDefault(line => !line.StartsWith("State:", StringComparison.Ordinal) &&
            !line.StartsWith("Receipt:", StringComparison.Ordinal) && !line.StartsWith("Duration (ms):", StringComparison.Ordinal)) ?? lines[0];
        return PlainActivityExcerpt(first);
    }

    private static string PlainActivityExcerpt(string detail, int labelLength = 0)
    {
        var value = detail[labelLength..].TrimStart();
        // Malformed/opaque JSON stays in the expanded receipt view rather than leaking a raw envelope inline.
        return value.StartsWith('{') || value.StartsWith('[') || value.StartsWith('"') ||
            value.Contains(@"\u0022", StringComparison.OrdinalIgnoreCase)
                ? "Structured output is available in the expanded detail."
                : detail;
    }

    private static bool ShowActivityEntry(CerebralEvent item)
    {
        if (item.Type != CerebralEventTypes.WorkflowTransition ||
            item.Data is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String) return true;
        // Execution bookkeeping stays in the durable journal; the user's activity list shows
        // actual commands/results and meaningful state changes instead of five empty rows per call.
        return reason.GetString() is not ("submitted" or "claimed" or "advanced" or "effect.dispatched" or "effect.finished");
    }

    private static bool ActivityCommand(CerebralEvent item, out string tool, out JsonElement arguments, out JsonElement outer)
    {
        tool = ""; arguments = outer = default;
        if (item.Type != CerebralEventTypes.ToolCall || item.Data is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("toolName", out var name) || name.ValueKind != JsonValueKind.String) return false;
        tool = name.GetString() ?? "";
        if (!data.TryGetProperty("arguments", out outer) || outer.ValueKind != JsonValueKind.Object) return true;
        arguments = outer;
        if (tool == "cerebral.host:tool" && outer.TryGetProperty("name", out var nestedName) && nestedName.ValueKind == JsonValueKind.String)
        {
            tool = nestedName.GetString() ?? "";
            if (outer.TryGetProperty("arguments", out var nested) && nested.ValueKind == JsonValueKind.Object) arguments = nested;
        }
        return true;
    }

    private static void AddActivityFields(JsonElement value, List<string> lines, List<string> secrets, int depth, bool command)
    {
        if (depth > 4 || lines.Count >= 200) return;
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in value.EnumerateArray().Take(64)) AddActivityFields(entry, lines, secrets, depth + 1, command);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var property in value.EnumerateObject().Take(64))
        {
            if (lines.Count >= 200) break;
            if (ActivitySecretKeys.Contains(ActivityKey(property.Name)))
            {
                lines.Add(ActivityFieldLabel(property.Name) + ": [Redacted]");
                continue;
            }
            if (command && property.Name is "content" or "newText" or "oldText" or "diff")
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    lines.Add(ActivityFieldLabel(property.Name) + $": [File text omitted; {property.Value.GetString()?.Length ?? 0:N0} characters]");
                continue;
            }
            if (command && property.Name == "arguments" && property.Value.ValueKind == JsonValueKind.Array)
            {
                lines.Add("Arguments: " + ActivityArguments(property.Value));
                continue;
            }
            if (!ActivityDisplayFields.Contains(property.Name)) continue;
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var text = property.Value.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (property.Name is "content" or "text")
                {
                    var candidate = text.StartsWith("Tool ", StringComparison.Ordinal) && text.Contains('\n')
                        ? text[(text.IndexOf('\n') + 1)..] : text;
                    var handled = false;
                    for (var decode = 0; decode < 3 && candidate.Length <= 262_144; decode++)
                    {
                        try
                        {
                            using var parsed = JsonDocument.Parse(candidate);
                            if (parsed.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                            {
                                IncludeActivitySecrets(parsed.RootElement, secrets);
                                var before = lines.Count;
                                AddActivityFields(parsed.RootElement, lines, secrets, depth + 1, command: false);
                                if (lines.Count == before) lines.Add("Structured output has no displayable fields. Inspect its retained receipt.");
                                handled = true;
                                break;
                            }
                            if (parsed.RootElement.ValueKind == JsonValueKind.String)
                            {
                                candidate = parsed.RootElement.GetString() ?? "";
                                text = candidate;
                                continue;
                            }
                            break;
                        }
                        catch (JsonException) { break; }
                    }
                    if (handled) continue;
                    lines.Add("Output:\n" + text);
                }
                else lines.Add(ActivityFieldLabel(property.Name) + ": " + text);
            }
            else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                AddActivityFields(property.Value, lines, secrets, depth + 1, command);
            else if (property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                if (property.Name is "truncated" or "out_truncated" or "err_truncated")
                {
                    AddActivityValue(property.Name, value, lines);
                    continue;
                }
                lines.Add(ActivityFieldLabel(property.Name) + ": " + property.Value.ToString());
            }
        }
    }

    private static void AddActivityValue(string field, JsonElement value, List<string> lines)
    {
        if (!value.TryGetProperty(field, out var part)) return;
        if (field is "outputOmitted" or "truncated" or "out_truncated" or "err_truncated")
        {
            if (part.ValueKind != JsonValueKind.True) return;
            lines.Add(field switch
            {
                "outputOmitted" => "Generated output was omitted from the retained result.",
                "out_truncated" => "Returned standard output was truncated; inspect the receipt for available detail.",
                "err_truncated" => "Returned error output was truncated; inspect the receipt for available detail.",
                _ => "Returned output was truncated; inspect the receipt for available detail."
            });
            return;
        }
        if (part.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            var text = part.ValueKind == JsonValueKind.String ? part.GetString() : part.ToString();
            if (!string.IsNullOrWhiteSpace(text)) lines.Add(ActivityFieldLabel(field) + ": " + text);
        }
    }

    private static string ActivityKey(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());

    private static string ActivityFieldLabel(string value) => value switch
    {
        "workingDirectory" => "Working directory", "executableId" => "Executable", "toolId" => "Tool",
        "exitCode" or "exit" => "Exit code", "startLine" => "Start line", "lineCount" => "Line count",
        "timeoutSeconds" => "Timeout (seconds)", "durationMilliseconds" or "elapsedMilliseconds" => "Duration (ms)",
        "firstTokenMilliseconds" => "First token (ms)", "providerReceiptId" or "receiptId" or "rid" => "Receipt",
        "stdout" or "out" => "Output", "stderr" or "err" => "Error output",
        _ => char.ToUpperInvariant(value[0]) + value[1..]
    };

    private static string ActivityArguments(JsonElement arguments)
    {
        var tokens = new List<string>();
        var hideNext = false;
        foreach (var arg in arguments.EnumerateArray().Take(64))
        {
            if (arg.ValueKind != JsonValueKind.String) { tokens.Add("[Non-text argument]"); continue; }
            var text = arg.GetString() ?? "";
            if (hideNext) { tokens.Add("[Redacted]"); hideNext = false; continue; }
            var separator = text.IndexOfAny(['=', ':']);
            var flag = text.StartsWith('-') || text.StartsWith('/');
            var key = separator >= 0 ? text[..separator] : text;
            if (flag && ActivityCredentialKeys.Contains(ActivityKey(key)))
            {
                if (separator >= 0) text = key + "=[Redacted]";
                else hideNext = true;
            }
            tokens.Add(text.Any(char.IsWhiteSpace) ? "\"" + text.Replace("\"", "\\\"") + "\"" : text);
        }
        return string.Join(" ", tokens);
    }

    private static void IncludeActivitySecrets(JsonElement value, List<string> secrets)
    {
        foreach (var secret in ActivitySecrets(value))
            if (!secrets.Contains(secret, StringComparer.Ordinal)) secrets.Add(secret);
    }

    private static List<string> ActivitySecrets(JsonElement value)
    {
        var secrets = new List<string>();
        void Visit(JsonElement item, int depth)
        {
            if (depth > 5 || secrets.Count >= 64) return;
            if (item.ValueKind == JsonValueKind.Object)
                foreach (var property in item.EnumerateObject().Take(64))
                {
                    if (ActivitySecretKeys.Contains(ActivityKey(property.Name)) && property.Value.ValueKind == JsonValueKind.String &&
                        property.Value.GetString() is { Length: > 0 } secret) secrets.Add(secret);
                    else Visit(property.Value, depth + 1);
                }
            else if (item.ValueKind == JsonValueKind.Array)
            {
                var hideNext = false;
                foreach (var part in item.EnumerateArray().Take(64))
                {
                    if (part.ValueKind != JsonValueKind.String) { Visit(part, depth + 1); continue; }
                    var text = part.GetString() ?? "";
                    if (hideNext) { if (text.Length > 0) secrets.Add(text); hideNext = false; continue; }
                    var separator = text.IndexOfAny(['=', ':']);
                    var key = separator >= 0 ? text[..separator] : text;
                    if ((text.StartsWith('-') || text.StartsWith('/')) && ActivityCredentialKeys.Contains(ActivityKey(key)))
                    {
                        if (separator >= 0 && text.Length > separator + 1) secrets.Add(text[(separator + 1)..]);
                        else hideNext = true;
                    }
                }
            }
        }
        if (value.ValueKind != JsonValueKind.Undefined) Visit(value, 0);
        return secrets.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string BoundActivityText(string text, IReadOnlyList<string> secrets)
    {
        if (text.Length > 262_144) return "Output is too large for this view. Inspect its retained receipt for full details.";
        foreach (var secret in secrets.OrderByDescending(secret => secret.Length))
            text = text.Replace(secret, "[Redacted]", StringComparison.Ordinal);
        try
        {
            var timeout = TimeSpan.FromMilliseconds(100);
            text = Regex.Replace(text, @"(?i)\b(authorization|proxy-authorization|cookie|set-cookie)\s*[:=]\s*[^\r\n]+", "$1: [Redacted]", RegexOptions.None, timeout);
            text = Regex.Replace(text, @"(?i)\b(password|passwd|pwd|client[_-]?secret|access[_-]?token|refresh[_-]?token|api[_-]?key|access[_-]?key|token)\s*[:=]\s*[^\s,;&]+", "$1=[Redacted]", RegexOptions.None, timeout);
            text = Regex.Replace(text, @"(?i)(https?://)[^/\s@]+@", "$1[Redacted]@", RegexOptions.None, timeout);
            text = Regex.Replace(text, @"(?i)([?&](?:api[_-]?key|access[_-]?key|access[_-]?token|token|password)=)[^&#\s]+", "$1[Redacted]", RegexOptions.None, timeout);
        }
        catch (RegexMatchTimeoutException) { return "Output could not be sanitized within the time limit. Inspect its retained receipt."; }
        text = new string(text.Where(character => !char.IsControl(character) || character is '\n' or '\r' or '\t').ToArray());
        const string suffix = "\n[Excerpt limited. Inspect the retained receipt for more detail.]";
        if (text.Length <= MaximumActivityDetailCharacters) return text;
        var length = MaximumActivityDetailCharacters - suffix.Length;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + suffix;
    }

    private static UIElement ActivityEventBody(CerebralEvent item, CerebralEvent? call = null)
    {
        var content = new StackPanel();
        content.Children.Add(Ui.Text(item.CreatedAt.ToLocalTime().ToString("h:mm:ss tt"), 11, Ui.Muted));
        var command = item.Type == CerebralEventTypes.ToolCall ? ActivityCommandText(item)
            : call is null ? "" : ActivityCommandText(call);
        if (command.Length > 0) content.Children.Add(ActivityDetailBox(command));
        if (item.Type == CerebralEventTypes.ToolResult)
        {
            var result = ActivityResultText(item, call);
            if (result.Length > 0) content.Children.Add(ActivityDetailBox(result, formatted: true));
        }
        else if (ActivityAuditText(item) is { } audit)
            content.Children.Add(ActivityDetailBox(audit));
        else
        {
            foreach (var part in item.Content.OfType<TextContentPart>())
                if (!string.IsNullOrWhiteSpace(part.Text)) content.Children.Add(MarkdownView.Render(BoundActivityText(part.Text, [])));
            if (item.Data is { ValueKind: JsonValueKind.Object } data)
            {
                var lines = new List<string>();
                foreach (var field in new[] { "message", "summary", "error", "reason" }) AddActivityValue(field, data, lines);
                if (lines.Count > 0) content.Children.Add(ActivityDetailBox(BoundActivityText(string.Join("\n", lines), [])));
            }
        }
        if (content.Children.Count == 1) content.Children.Add(Ui.Text("No further recorded detail.", 12, Ui.Muted));
        return content;
    }

    private static UIElement ActivityDetailBox(string text, bool formatted = false)
    {
        if (formatted && MarkdownView.HasFormatting(text)) return MarkdownView.Render(text);
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0, 3, 0, 6)
        };
        TextContextMenus.Attach(box);
        return box;
    }
}
