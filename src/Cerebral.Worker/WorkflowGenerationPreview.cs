using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

/// <summary>A provisional public projection, never a model action or an execution receipt.</summary>
internal sealed record WorkflowGenerationPreviewValue(string Kind, string? Text = null,
    string? Operation = null, bool Truncated = false);

internal sealed record WorkflowGenerationPreviewIdentity(string ConversationId, string RequestId,
    string RunId, string EffectId, string AgentId);

internal sealed record WorkflowGenerationPreviewEvent(string Schema, string ConversationId, string RequestId,
    string RunId, string EffectId, string AgentId, string Phase, string? Kind, string? Text,
    string? Operation, bool Truncated, long ElapsedMilliseconds, string? Finish = null);

/// <summary>Recognizes only the public fields of the conversation action envelope. The bounded
/// temporary envelope is discarded at settlement and never written to the journal or event feed.</summary>
internal sealed class WorkflowGenerationPreviewParser(IEnumerable<string> operations)
{
    // The shipped final-action contract allows 32K UTF-16 characters. A JSON \u escape
    // can require six envelope characters per character; observation must cover that
    // existing answer boundary without increasing the model/harness admission limit.
    internal const int MaximumTextCharacters = 32 * 1024;
    internal const int MaximumEnvelopeCharacters = 6 * MaximumTextCharacters + 4096;
    internal const int MaximumEnvelopeBytes = 3 * MaximumEnvelopeCharacters;
    internal const int MaximumTextBytes = 4 * MaximumTextCharacters;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    // Match the reserved reasoning delimiters rejected by native terminal validation.
    // Inspect decoded text, and withhold any suffix that could finish a split delimiter.
    private static readonly string[] ReasoningTags = ["<think>", "</think>"];
    private readonly HashSet<string> acceptedOperations = new(operations, StringComparer.Ordinal);
    private readonly StringBuilder envelope = new();
    internal bool Disabled { get; private set; }

    internal void Append(string fragment)
    {
        if (Disabled || fragment.Length == 0) return;
        if (fragment.Length > MaximumEnvelopeCharacters - envelope.Length) { Disable(); return; }
        envelope.Append(fragment);
    }

    internal WorkflowGenerationPreviewValue? Read(bool requireComplete = false)
    {
        if (Disabled || envelope.Length == 0) return null;
        byte[] bytes;
        try { bytes = Utf8.GetBytes(envelope.ToString()); }
        // A provider can split a raw surrogate pair across callbacks. Wait for its next fragment.
        catch (EncoderFallbackException)
        { return !requireComplete && HasOnlyTrailingSurrogateSplit() ? null : Reject(); }
        if (bytes.Length > MaximumEnvelopeBytes) { Disable(); return null; }
        try
        {
            var reader = new Utf8JsonReader(bytes, requireComplete, new JsonReaderState(new JsonReaderOptions { MaxDepth = 32 }));
            if (!reader.Read()) return null;
            if (reader.TokenType != JsonTokenType.StartObject) return Reject();
            var fields = new HashSet<string>(StringComparer.Ordinal);
            string? property = null, action = null, text = null, operation = null;
            var arguments = false;
            var closed = false;
            while (reader.Read())
            {
                if (closed) return Reject();
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                { closed = true; property = null; continue; }
                if (reader.CurrentDepth != 1) continue;
                if (reader.TokenType == JsonTokenType.EndObject) continue; // End of the ignored arguments object.
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    property = reader.GetString();
                    if (property is not ("action" or "text" or "name" or "arguments") ||
                        !fields.Add(property)) return Reject();
                    continue;
                }
                switch (property)
                {
                    case "action" when reader.TokenType == JsonTokenType.String:
                        action = reader.GetString();
                        if (action is not ("final" or "tool")) return Reject();
                        break;
                    case "text" when reader.TokenType == JsonTokenType.String:
                        text = reader.GetString(); break;
                    case "name" when reader.TokenType == JsonTokenType.String:
                        operation = reader.GetString(); break;
                    case "arguments" when reader.TokenType == JsonTokenType.StartObject:
                        arguments = true; break;
                    default: return Reject();
                }
                property = null;
            }
            if (action == "final")
            {
                if (fields.Any(field => field is not ("action" or "text"))) return Reject();
                if (text is null && property == "text")
                    text = PartialString(bytes.AsSpan(checked((int)reader.BytesConsumed)));
                if (closed && string.IsNullOrEmpty(text)) return Reject();
                if (text?.Length > MaximumTextCharacters) return Reject();
                return text is { Length: > 0 } ? PublicText(text) : null;
            }
            if (action == "tool")
            {
                if (fields.Any(field => field is not ("action" or "name" or "arguments"))) return Reject();
                if (closed && (!arguments || operation is null)) return Reject();
                // Names must match the exact advertised catalog. Arguments are never projected.
                return operation is { Length: > 0 and <= 256 } && acceptedOperations.Contains(operation)
                    ? new("tool", Operation: operation) : null;
            }
            return null;
        }
        catch (JsonException) { return Reject(); }
        catch (ArgumentException) { return Reject(); }
        catch (InvalidOperationException) { return Reject(); }
    }

    private bool HasOnlyTrailingSurrogateSplit()
    {
        // Only a single high surrogate at the callback boundary can become valid later.
        for (var index = 0; index < envelope.Length; index++)
        {
            var value = envelope[index];
            if (char.IsLowSurrogate(value)) return false;
            if (!char.IsHighSurrogate(value)) continue;
            if (index == envelope.Length - 1) return true;
            if (!char.IsLowSurrogate(envelope[++index])) return false;
        }
        return false;
    }

    internal void Clear() => Disable();
    internal void Invalidate() => Disable();
    private void Disable() { envelope.Clear(); Disabled = true; }
    private WorkflowGenerationPreviewValue? Reject() { Disable(); return null; }

    private static string? PartialString(ReadOnlySpan<byte> remaining)
    {
        var offset = 0;
        while (offset < remaining.Length && remaining[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)':') offset++;
        remaining = remaining[offset..];
        if (remaining.Length < 1 || remaining[0] != (byte)'"') return null;
        // Read() has already validated every complete token. Only an unfinished string is
        // completed temporarily for decoding; at most one split escape/surrogate is held back.
        var candidate = new byte[remaining.Length + 1];
        remaining.CopyTo(candidate); candidate[^1] = (byte)'"';
        for (var trim = 0; trim <= Math.Min(12, remaining.Length - 1); trim++)
        {
            var length = remaining.Length - trim;
            candidate[length] = (byte)'"';
            try { return JsonSerializer.Deserialize<string>(candidate.AsSpan(0, length + 1)); }
            catch (JsonException) { }
        }
        return null;
    }

    private WorkflowGenerationPreviewValue? PublicText(string value)
    {
        foreach (var tag in ReasoningTags)
            if (value.Contains(tag, StringComparison.OrdinalIgnoreCase)) return Reject();
        var visibleLength = value.Length;
        foreach (var tag in ReasoningTags)
            for (var length = 1; length < tag.Length && length <= value.Length; length++)
                if (value.AsSpan().EndsWith(tag.AsSpan(0, length), StringComparison.OrdinalIgnoreCase))
                    visibleLength = Math.Min(visibleLength, value.Length - length);
        value = value[..visibleLength];
        if (value.Length == 0) return null;
        if (Utf8.GetByteCount(value) <= MaximumTextBytes) return new("final", Text: value);
        var characters = 0; var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaximumTextBytes) break;
            bytes += rune.Utf8SequenceLength; characters += rune.Utf16SequenceLength;
        }
        return new("final", Text: value[..characters], Truncated: true);
    }
}

/// <summary>At most four changed snapshots per second, plus a settlement/invalid-draft clear.
/// No timer, durable event, prompt, private reasoning or raw action object is retained.</summary>
internal sealed class WorkflowGenerationPreview
{
    internal const string EventType = "workflow.generation.preview";
    internal const string Schema = "cerebral.generation-preview/1";
    private const long CadenceMilliseconds = 250;
    private readonly object gate = new();
    private readonly WorkflowGenerationPreviewIdentity identity;
    private readonly WorkflowGenerationPreviewParser parser;
    private readonly Action<WorkflowGenerationPreviewEvent> publish;
    private readonly Func<long> elapsed;
    private WorkflowGenerationPreviewValue? last;
    private long lastPublished;
    private bool finished;

    internal WorkflowGenerationPreview(WorkflowGenerationPreviewIdentity identity, IEnumerable<string> operations,
        Action<WorkflowGenerationPreviewEvent> publish, Func<long> elapsed)
    { this.identity = identity; parser = new(operations); this.publish = publish; this.elapsed = elapsed; }

    internal void Append(string fragment)
    {
        lock (gate)
        {
            if (finished) return;
            parser.Append(fragment);
            if (parser.Disabled) { ClearDraft("unavailable"); return; }
            // Validation and retraction are immediate; cadence limits only publication.
            var value = parser.Read();
            if (parser.Disabled) { ClearDraft("unavailable"); return; }
            var now = Math.Max(0, elapsed());
            if (last is not null && now - lastPublished < CadenceMilliseconds) return;
            if (value is null || value == last) return;
            if (!TryPublish(Event("draft", value, now))) return;
            last = value; lastPublished = now;
        }
    }

    internal void Invalidate()
    {
        lock (gate)
        {
            if (finished) return;
            finished = true; parser.Invalidate();
            ClearDraft("unavailable");
        }
    }

    internal void Finish(ModelGenerationFinish finish)
    {
        lock (gate)
        {
            if (finished) return;
            if (finish != ModelGenerationFinish.Stop)
            {
                // An incomplete or uncertain generation cannot leave a provisional answer visible.
                finished = true; parser.Clear();
                ClearDraft("unavailable");
                return;
            }
            try
            {
                // Only a complete public envelope may flush its final callback tail. The
                // conversation's canonical result and required receipts remain authoritative.
                if (!parser.Disabled)
                {
                    var value = parser.Read(requireComplete: true);
                    if (parser.Disabled) ClearDraft("unavailable");
                    else if (value is not null && value != last)
                    {
                        var now = Math.Max(0, elapsed());
                        if (!TryPublish(Event("draft", value, now))) return;
                        last = value; lastPublished = now;
                    }
                }
                if (last is not null)
                    TryPublish(Event("settled", null, Math.Max(0, elapsed()), finish.ToString()));
            }
            finally { finished = true; parser.Clear(); last = null; }
        }
    }

    private void ClearDraft(string phase)
    {
        if (last is null) return;
        last = null;
        TryPublish(Event(phase, null, Math.Max(0, elapsed())));
    }

    private bool TryPublish(WorkflowGenerationPreviewEvent value)
    {
        try { publish(value); return true; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            // A failed optional observer detaches without changing generation or cleanup.
            finished = true; parser.Invalidate(); last = null;
            return false;
        }
    }

    private WorkflowGenerationPreviewEvent Event(string phase, WorkflowGenerationPreviewValue? value, long milliseconds,
        string? finish = null) => new(Schema, identity.ConversationId, identity.RequestId, identity.RunId,
            identity.EffectId, identity.AgentId, phase, value?.Kind, value?.Text, value?.Operation,
            value?.Truncated ?? false, milliseconds, finish);
}

public sealed partial class EventHub
{
    internal bool HasServiceSubscribers => !subscribers.IsEmpty;
}
