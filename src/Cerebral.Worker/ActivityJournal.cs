using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed class ActivityJournal(StateStore store, EventHub events, Func<HostSettings>? settings = null)
{
    public const int MaxArgumentBytes = 8192;
    public const int MaxResultBytes = 16384;
    public static readonly TimeSpan DefaultProgressInterval = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, string[]> pendingSecrets = new();

    internal TimeSpan ProgressInterval { get; } = DefaultProgressInterval;

    internal ActivityJournal(StateStore store, EventHub events, Func<HostSettings>? settings, TimeSpan progressInterval)
        : this(store, events, settings)
    {
        if (progressInterval <= TimeSpan.Zero || progressInterval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(progressInterval));
        ProgressInterval = progressInterval;
    }

    public ActivityEntry? Start(ToolCall call, ToolExecutionContext context)
    {
        var arguments = ActivityDetails.Format(call.Arguments, MaxArgumentBytes, out var truncated, out var secrets);
        var entry = store.Locked(() =>
        {
            var actor = store.GetSessionMetadata(context.SessionId);
            if (actor is null) return null; // Unbound maintenance calls do not invent conversations.
            var root = store.Get<AgentJob>("job", context.JobId);
            var assignment = root;
            if (root is not null && (root.SessionId != actor.Id || root.ProfileId != context.Profile.Id))
                throw new InvalidOperationException("Activity context does not match its assignment.");
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (root?.ParentJobId is { } parent)
            {
                if (!visited.Add(root.Id) || visited.Count > 64) throw new InvalidOperationException("Invalid activity parent chain.");
                root = store.Get<AgentJob>("job", parent) ?? throw new InvalidOperationException("Activity parent assignment is unavailable.");
            }
            var session = root is null ? actor : store.GetSessionMetadata(root.SessionId)
                ?? throw new InvalidOperationException("Activity conversation is unavailable.");
            var latest = store.GetLatestUserRequestSequence(session.Id) ?? 0;
            if (assignment?.ParentJobId is not null)
                latest = assignment.RootTurnSequence ?? store.GetLatestUserRequestSequence(session.Id, assignment.CreatedAt) ?? 0;
            var row = new ActivityEntry
            {
                SessionId = session.Id, ActorSessionId = actor.Id, JobId = context.JobId,
                RootJobId = root?.Id ?? context.JobId, TurnSequence = Math.Max(root?.StartSequence ?? 0, latest),
                ProfileId = context.Profile.Id, ToolName = ExecutionReliability.Clip(call.Name, 256),
                Title = TitleFor(call), Arguments = arguments, Truncated = truncated
            };
            store.InsertActivity(row);
            if (settings is not null)
            {
                var retention = settings();
                store.TrimActivity(row.SessionId, retention.ActivityHistoryLimit, retention.ActivityHistoryDays, row.Sequence % 32 == 0);
            }
            return row;
        });
        if (entry is not null)
        {
            if (secrets.Length > 0) pendingSecrets[entry.Id] = secrets;
            Publish(entry);
        }
        return entry;
    }

    public void Finish(ActivityEntry entry, string state, string result, bool truncated = false)
    {
        if (state is not ("completed" or "failed" or "cancelled" or "uncertain")) throw new ArgumentException("Invalid activity completion state.");
        pendingSecrets.TryRemove(entry.Id, out var secrets);
        var text = ActivityDetails.FormatText(result, MaxResultBytes, secrets ?? [], out var shortened);
        var current = store.Locked(() =>
        {
            var row = store.Get<ActivityEntry>("activity", entry.Id) ?? throw new InvalidOperationException("Activity start record is unavailable.");
            if (row.Sequence != entry.Sequence) throw new InvalidOperationException("Activity identity changed.");
            row.State = state; row.Result = text; row.Truncated |= truncated || shortened; row.UpdatedAt = DateTimeOffset.UtcNow;
            store.Put("activity", row.Id, row); return row;
        });
        Publish(current);
    }

    public bool ReportProgress(ActivityEntry entry, TimeSpan elapsed)
    {
        var current = store.Locked(() =>
        {
            var row = store.Get<ActivityEntry>("activity", entry.Id);
            if (row is null || row.Sequence != entry.Sequence || row.State != "running") return null;
            row.Result = $"Still running · {FormatElapsed(elapsed)} elapsed";
            row.UpdatedAt = DateTimeOffset.UtcNow;
            store.Put("activity", row.Id, row);
            return row;
        });
        if (current is null) return false;
        Publish(current);
        return true;
    }

    public ActivityPage List(string sessionId, int turnSequence, long afterSequence = 0, int limit = 40)
    {
        RequireSession(sessionId);
        if (turnSequence < 0 || afterSequence < 0 || limit is < 1 or > 100) throw new ArgumentException("Invalid activity page.");
        var page = store.ReadActivity(sessionId, turnSequence, afterSequence, limit);
        foreach (var entry in page.Entries) ProjectInterrupted(entry);
        return page;
    }

    public List<ActivityTurnSummary> Turns(string sessionId)
    {
        RequireSession(sessionId);
        var turns = store.ReadActivityTurns(sessionId);
        foreach (var turn in turns) ProjectInterrupted(turn.Latest);
        return turns;
    }

    public void ReportFailure(Exception ex, string sessionId, string jobId)
    {
        ReportingFailure(ex);
        try
        {
            events.Publish("activity.error", jobId, new { sessionId, jobId,
                message = "Activity could not be recorded completely. The tool's own outcome is unchanged; inspect its job result." });
        }
        catch (Exception) { }
    }

    private static string TitleFor(ToolCall call)
    {
        string? Value(string name) => call.Arguments.ValueKind == JsonValueKind.Object &&
            call.Arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        string Safe(string value) => ExecutionReliability.Clip(new string(value.Where(x => !char.IsControl(x)).ToArray()), 120);
        if (call.Name == "context.compact") return "Context compaction";
        var operation = call.Name.Split('.').Last();
        if (operation.StartsWith("cortex_", StringComparison.Ordinal))
        {
            var action = Value("action");
            if (action is not null && (action.Length > 24 || action.Any(x => !char.IsAsciiLetter(x)))) action = null;
            var spaces = Value("namespace");
            if (spaces is null && call.Arguments.ValueKind == JsonValueKind.Object &&
                call.Arguments.TryGetProperty("namespaces", out var list) && list.ValueKind == JsonValueKind.Array)
                spaces = string.Join(", ", list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Take(3).Select(x => Safe(x.GetString()!)));
            return ExecutionReliability.Clip("Cortex " + operation["cortex_".Length..].Replace('_', ' ') +
                (action is null ? "" : " " + action) + (string.IsNullOrEmpty(spaces) ? "" : " - " + Safe(spaces)), 160);
        }
        if (call.Name == "process.run")
        {
            var arguments = "";
            if (call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty("arguments", out var values) && values.ValueKind == JsonValueKind.Array)
                arguments = string.Join(" ", values.EnumerateArray().Take(2).Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()).Where(x => x is "build" or "test" or "run" or "restore" or "--no-restore" or "--no-build" or "check"));
            return ExecutionReliability.Clip("Run " + Safe(Value("executableId") ?? "configured executable") + (arguments.Length == 0 ? "" : " " + arguments), 160);
        }
        if ((call.Name.StartsWith("file.", StringComparison.Ordinal) || call.Name.StartsWith("shared.", StringComparison.Ordinal)) && Value("path") is { } path)
            return ExecutionReliability.Clip(call.Name + " - " + Safe(path.Replace('\\', '/').Split('/').Last()), 160);
        return Safe(call.Name);
    }

    public int RecoverInterrupted() => store.RecoverActivity();
    private void RequireSession(string id)
    {
        if (store.GetSessionMetadata(id) is null) throw new ArgumentException("Conversation not found.");
    }
    private void ProjectInterrupted(ActivityEntry entry)
    {
        if (entry.State != "running" || entry.JobId is "direct" or "shared") return;
        var job = store.Get<AgentJob>("job", entry.JobId);
        if (job is not null && JobScheduler.IsActive(job)) return;
        entry.State = "uncertain";
        entry.Result = "Assignment ended before a tool completion was recorded. Inspect actual effects before retrying.";
    }
    private void Publish(ActivityEntry entry)
    {
        try { events.Publish("activity.updated", entry.JobId, entry); }
        catch (Exception ex) { ReportingFailure(ex); }
    }

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
        : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    internal static void ReportingFailure(Exception ex)
    {
        try { Console.Error.WriteLine("Activity recording unavailable (" + ex.GetType().Name + "); the tool's own outcome is unchanged."); }
        catch (Exception) { } // A closed diagnostic sink must not replace the actual tool outcome.
    }
}

internal static class ActivityDetails
{
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    { "password", "passwd", "pwd", "secret", "clientsecret", "token", "accesstoken", "refreshtoken", "accesskey", "apikey",
      "authorization", "proxyauthorization", "cookie", "setcookie", "privatekey", "credential", "credentials", "connectionstring",
      "configuration", "config", "settings", "environment", "env" };
    private static string Key(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());

    public static string Format(JsonElement value, int maximum, out bool truncated, out string[] secrets)
    {
        var wasTruncated = false; var hidden = new List<string>();
        object? Visit(JsonElement item, int depth)
        {
            if (depth > 8) { wasTruncated = true; return "[Nested detail omitted]"; }
            if (item.ValueKind == JsonValueKind.Object)
            {
                var result = new Dictionary<string, object?>();
                foreach (var property in item.EnumerateObject())
                {
                    if (result.Count >= 64) { wasTruncated = true; break; }
                    if (SecretKeys.Contains(Key(property.Name)))
                    {
                        CollectSecrets(property.Value, hidden, 0); result[property.Name] = "[Redacted]";
                    }
                    else result[property.Name] = Visit(property.Value, depth + 1);
                }
                return result;
            }
            if (item.ValueKind == JsonValueKind.Array)
            {
                if (item.GetArrayLength() > 64) wasTruncated = true;
                return item.EnumerateArray().Take(64).Select(x => Visit(x, depth + 1)).ToArray();
            }
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString() ?? "";
                if (text.Length > maximum * 2) { text = text[..(maximum * 2)]; wasTruncated = true; }
                return text;
            }
            return item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : item.Clone();
        }
        var projected = JsonSerializer.Serialize(Visit(value, 0));
        secrets = hidden.Distinct(StringComparer.Ordinal).ToArray();
        var formatted = CleanText(projected, maximum, secrets, out var bounded);
        truncated = wasTruncated || bounded;
        return formatted;
    }

    private static void CollectSecrets(JsonElement value, List<string> secrets, int depth)
    {
        if (depth > 4 || secrets.Count >= 64) return;
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 32768 } text) secrets.Add(text);
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject().Take(64)) CollectSecrets(property.Value, secrets, depth + 1);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray().Take(64)) CollectSecrets(item, secrets, depth + 1);
    }

    public static string FormatText(string text, int maximum, IReadOnlyList<string> secrets, out bool truncated)
    {
        if (text.Length <= 262144)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var formatted = Format(document.RootElement, maximum, out var formattedTruncated, out _);
                var result = CleanText(formatted, maximum, secrets, out var bounded);
                truncated = formattedTruncated || bounded; return result;
            }
            catch (JsonException) { }
        }
        return CleanText(text, maximum, secrets, out truncated);
    }

    private static string CleanText(string text, int maximum, IReadOnlyList<string> secrets, out bool truncated)
    {
        // Remove known argument secrets before shortening, including tool output that echoes arguments.
        foreach (var secret in secrets.OrderByDescending(x => x.Length))
        {
            text = text.Replace(secret, "[Redacted]", StringComparison.Ordinal);
            var escaped = JsonSerializer.Serialize(secret);
            text = text.Replace(escaped[1..^1], "[Redacted]", StringComparison.Ordinal);
        }
        try
        {
            text = Regex.Replace(text, @"(?i)\b(authorization|proxy-authorization)\s*[:=]\s*[^\r\n]+", "$1: [Redacted]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            text = Regex.Replace(text, @"(?i)\b(password|passwd|pwd|client[_-]?secret|access[_-]?token|refresh[_-]?token|api[_-]?key|access[_-]?key|token)\s*[:=]\s*[^\s,;]+", "$1=[Redacted]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException) { truncated = true; return "[Output omitted because it could not be sanitized within the time limit.]"; }
        truncated = Encoding.UTF8.GetByteCount(text) > maximum;
        return CortexContext.BoundUtf8(text, maximum);
    }
}
