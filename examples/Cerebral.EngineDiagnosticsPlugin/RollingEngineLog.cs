using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Core;

namespace Cerebral.EngineDiagnosticsPlugin;

/// <summary>One owner-local JSONL log. Age, bytes, entries and individual record size are all bounded.
/// Maintenance uses one bounded temporary file; the data root never belongs to an engine/code slot.</summary>
internal sealed class RollingEngineLog
{
    internal const long DefaultMaximumBytes = 32L * 1024 * 1024;
    internal const int MaximumRecords = 8192;
    internal const int MaximumRecordBytes = 32768;
    private static readonly JsonSerializerOptions LogJson = new(CerebralJson.Options) { WriteIndented = false };
    private readonly object gate = new();
    private readonly string root, path, temporary;
    private readonly long maximumBytes;
    private readonly int maximumRecords;
    private readonly Func<DateTimeOffset> clock;
    private long written, pruned, rejected;
    private DateTimeOffset? oldest;
    private int records;
    private string? fault;
    internal string PathName => path;

    internal RollingEngineLog(string dataRoot, long maximumBytes = DefaultMaximumBytes,
        int maximumRecords = MaximumRecords, Func<DateTimeOffset>? clock = null)
    {
        root = System.IO.Path.GetFullPath(dataRoot);
        if (maximumBytes is < 4096 or > DefaultMaximumBytes || maximumRecords is < 1 or > MaximumRecords)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.maximumBytes = maximumBytes; this.maximumRecords = maximumRecords;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        path = System.IO.Path.Combine(root, "engine-debug.jsonl");
        temporary = System.IO.Path.Combine(root, "engine-debug.rewrite");
    }

    internal void Initialize() { lock (gate) { Guard(); Prune(clock(), maximumBytes); fault = null; } }
    internal void GuardSettingsRoot() { lock (gate) Guard(); }
    internal string? Fault { get { lock (gate) return fault; } }
    internal object Snapshot()
    {
        lock (gate) return new Dictionary<string, object?>
        {
            ["logPath"] = path, ["retentionHours"] = 24, ["maximumBytes"] = maximumBytes, ["maximumRecords"] = maximumRecords,
            ["writtenRecords"] = written, ["prunedRecords"] = pruned, ["rejectedRecords"] = rejected,
            ["bytes"] = File.Exists(path) ? new FileInfo(path).Length : 0, ["fault"] = fault
        };
    }
    internal bool Append(EngineDiagnosticRecord record, long queueDroppedRecords)
    {
        lock (gate)
        {
            if (fault is not null) { rejected++; return false; }
            try
            {
                Guard();
                var now = clock();
                if (string.IsNullOrWhiteSpace(record.ChatId) || record.ChatId.Length > 128 ||
                    record.RequestId.Length > 128 || record.EffectId.Length > 128 || record.RunId?.Length > 128 ||
                    record.AgentId.Length > 128 || record.WorkerId.Length > 128 || record.ModelId.Length > 128 ||
                    record.Phase.Length > 64 || record.Outcome?.Length > 256 ||
                    record.At <= now.AddHours(-24) || record.At > now.AddMinutes(5)) { rejected++; return false; }
                if (record.Text is { Length: > 2048 }) record = record with { Text = record.Text[..2048], TextTruncated = true };
                var text = Redact(record.Text);
                var bytes = Serialize(record, text, queueDroppedRecords);
                if (bytes.Length > MaximumRecordBytes || bytes.Length > maximumBytes)
                {
                    bytes = Serialize(record with { TextTruncated = true }, null, queueDroppedRecords);
                    if (bytes.Length > MaximumRecordBytes || bytes.Length > maximumBytes) { rejected++; return false; }
                }
                var size = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (size + bytes.Length > maximumBytes || records >= maximumRecords || oldest <= now.AddHours(-24))
                    Prune(now, Math.Max(bytes.Length, maximumBytes * 3 / 4));
                size = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (size + bytes.Length > maximumBytes || records >= maximumRecords)
                    Prune(now, Math.Max(0, maximumBytes - bytes.Length), maximumRecords - 1);
                using (var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    output.Write(bytes);
                written++; records++; oldest = oldest is null || record.At < oldest ? record.At : oldest;
                return true;
            }
            catch (Exception error) when (Ordinary(error)) { rejected++; fault = "log-storage-failed"; return false; }
        }
    }
    internal void Maintain()
    {
        lock (gate)
        {
            if (fault is not null || oldest is null || oldest > clock().AddHours(-24)) return;
            try { Guard(); Prune(clock(), maximumBytes); }
            catch (Exception error) when (Ordinary(error)) { fault = "log-maintenance-failed"; }
        }
    }
    private static byte[] Serialize(EngineDiagnosticRecord record, string? text, long dropped) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1, ["sequence"] = record.Sequence, ["at"] = record.At, ["chatId"] = record.ChatId,
            ["requestId"] = record.RequestId, ["runId"] = record.RunId, ["effectId"] = record.EffectId,
            ["agentId"] = record.AgentId, ["workerId"] = record.WorkerId, ["modelId"] = record.ModelId, ["phase"] = record.Phase,
            ["text"] = text, ["textTruncated"] = record.TextTruncated || record.Text is not null && text is null,
            ["inputTokens"] = record.InputTokens, ["responseTokens"] = record.ResponseTokens, ["reasoningTokens"] = record.ReasoningTokens,
            ["contextTokens"] = record.ContextTokens, ["generatedTokens"] = record.GeneratedTokens,
            ["elapsedMilliseconds"] = record.ElapsedMilliseconds, ["outcome"] = record.Outcome, ["queueDroppedRecords"] = dropped
        }, LogJson) + "\n");

    private static string? Redact(string? text)
    {
        if (text is null) return null;
        try
        {
            return Regex.Replace(text, @"(?i)\bbearer\s+[a-z0-9._~+/=-]+|\bsk-[a-z0-9_-]{12,}",
                "<redacted>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(25));
        }
        catch (RegexMatchTimeoutException) { return null; }
    }
    private void Prune(DateTimeOffset now, long budget, int? entryLimit = null)
    {
        if (!File.Exists(path)) { oldest = null; records = 0; return; }
        // A corrupt/external oversized file is never read or silently deleted.
        if (new FileInfo(path).Length > maximumBytes)
            throw new IOException("Diagnostic file exceeds its admitted size.");
        var retained = new Queue<(byte[] Bytes, DateTimeOffset At)>();
        long size = 0; long removed = 0;
        using (var input = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: false))
        {
            while (ReadBoundedLine(input) is { } line)
            {
                byte[] bytes;
                DateTimeOffset at;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (!document.RootElement.TryGetProperty("chatId", out var chat) || chat.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(chat.GetString()) || chat.GetString()!.Length > 128 ||
                        !document.RootElement.TryGetProperty("at", out var timestamp) || !timestamp.TryGetDateTimeOffset(out at) ||
                        at <= now.AddHours(-24) || at > now.AddMinutes(5)) { removed++; continue; }
                    bytes = Encoding.UTF8.GetBytes(line + "\n");
                    if (bytes.Length > MaximumRecordBytes || bytes.Length > budget) { removed++; continue; }
                }
                catch (JsonException) { removed++; continue; }
                retained.Enqueue((bytes, at)); size += bytes.Length;
                while (size > budget || retained.Count > (entryLimit ?? maximumRecords))
                {
                    size -= retained.Dequeue().Bytes.Length; removed++;
                }
            }
        }
        if (removed > 0)
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                foreach (var item in retained) output.Write(item.Bytes);
            Guard();
            File.Move(temporary, path, overwrite: true);
        }
        records = retained.Count; oldest = retained.Count == 0 ? null : retained.Min(x => x.At); pruned += removed;
    }
    private static string? ReadBoundedLine(TextReader reader)
    {
        var line = new StringBuilder(); var overflow = false; var any = false;
        while (reader.Read() is var c && c != -1)
        {
            any = true;
            if (c == '\n') break;
            if (line.Length < MaximumRecordBytes) line.Append((char)c); else overflow = true;
        }
        return !any ? null : overflow ? "" : line.ToString().TrimEnd('\r');
    }
    private void Guard()
    {
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Diagnostic storage cannot traverse links.");
        Directory.CreateDirectory(root);
        foreach (var file in new[] { path, temporary })
            if (File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Diagnostic storage file is a link.");
    }
    private static bool Ordinary(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
