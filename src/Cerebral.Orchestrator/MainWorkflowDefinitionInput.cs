using System.IO;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Orchestrator;

/// <summary>Private Main transport metadata. The selected workflow still owns its input schema.</summary>
internal static class MainWorkflowDefinitionInput
{
    internal const string Capability = "message.send.definition-input.v1";
    internal const int MaximumUtf8Bytes = 16 * 1024;
    internal const int MaximumDepth = 16;
    internal const int MaximumMergedUtf8Bytes = WorkflowContract.MaximumStateBytes;
    private static readonly HashSet<string> OwnedFields =
        new(["workingDirectory", "projectId", "responseSharePercent"], StringComparer.OrdinalIgnoreCase);

    internal static JsonElement Freeze(JsonElement value) => FreezeCore(value, allowMainFields: false);

    // Exact workflows own their whole schema. Main never supplies or interprets any of these fields.
    internal static JsonElement FreezeExact(JsonElement value) => FreezeCore(value, allowMainFields: true);

    private static JsonElement FreezeCore(JsonElement value, bool allowMainFields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("definitionInput must be a JSON object.", nameof(value));
        var raw = value.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > MaximumUtf8Bytes)
            throw new ArgumentException("definitionInput exceeds 16 KiB UTF-8.", nameof(value));
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            Validate(document.RootElement, root: true, allowMainFields: allowMainFields);
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new ArgumentException("definitionInput exceeds its JSON depth or encoding bound.", nameof(value), error);
        }
    }

    private static void Validate(JsonElement value, bool root = false, bool allowMainFields = false)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ArgumentException("definitionInput contains a duplicate property.");
                if (root && !allowMainFields && OwnedFields.Contains(property.Name))
                    throw new ArgumentException("definitionInput cannot replace Main-owned input fields.");
                Validate(property.Value, allowMainFields: allowMainFields);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Validate(item, allowMainFields: allowMainFields);
    }

    internal static JsonElement Merge(JsonElement baseInput, JsonElement? definitionInput)
    {
        // Keep the legacy serialization and property order byte-for-byte when no carrier was supplied.
        if (definitionInput is null) return baseInput;
        if (baseInput.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Main workflow input must be an object.", nameof(baseInput));
        var added = Freeze(definitionInput.Value);
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            foreach (var property in baseInput.EnumerateObject()) property.WriteTo(writer);
            foreach (var property in added.EnumerateObject()) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        if (bytes.Length > MaximumMergedUtf8Bytes)
            throw new ArgumentException("Merged workflow input exceeds the existing workflow state bound.");
        using var merged = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = MaximumDepth });
        Validate(merged.RootElement);
        return merged.RootElement.Clone();
    }

    internal static bool MatchesFrozenExtras(JsonElement frozenInput, JsonElement definitionInput)
    {
        var expected = Freeze(definitionInput);
        if (frozenInput.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(frozenInput.GetRawText()) > MaximumMergedUtf8Bytes) return false;
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            foreach (var property in frozenInput.EnumerateObject())
                if (!OwnedFields.Contains(property.Name)) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        if (bytes.Length > MaximumMergedUtf8Bytes) return false;
        using var extras = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = MaximumDepth });
        Validate(extras.RootElement);
        return JsonElement.DeepEquals(extras.RootElement, expected);
    }

    internal static bool MatchesFrozenExact(JsonElement frozenInput, JsonElement definitionInput) =>
        frozenInput.ValueKind == JsonValueKind.Object &&
        Encoding.UTF8.GetByteCount(frozenInput.GetRawText()) <= MaximumUtf8Bytes &&
        JsonElement.DeepEquals(frozenInput, FreezeExact(definitionInput));
}

/// <summary>One request's execution-context lease; delayed inherited contexts cannot reuse a closed lease.</summary>
internal sealed class MainWorkflowDefinitionInputLease : IDisposable
{
    private readonly object gate = new();
    private readonly JsonElement input;
    private readonly Func<bool> ownerCurrent;
    private readonly bool exact;
    private bool consumed, applied, closed;
    internal string RequestId { get; }
    internal JsonElement Input { get { lock (gate) return input.Clone(); } }
    internal bool Consumed { get { lock (gate) return consumed; } }
    internal bool Applied { get { lock (gate) return applied; } }
    internal bool IsClosed { get { lock (gate) return closed; } }

    internal MainWorkflowDefinitionInputLease(string requestId, JsonElement input, Func<bool> ownerCurrent,
        bool exact = false)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 || requestId.Any(char.IsControl))
            throw new ArgumentException("A bounded exact request ID is required.", nameof(requestId));
        RequestId = requestId;
        this.input = exact ? MainWorkflowDefinitionInput.FreezeExact(input) : MainWorkflowDefinitionInput.Freeze(input);
        this.ownerCurrent = ownerCurrent ?? throw new ArgumentNullException(nameof(ownerCurrent));
        this.exact = exact;
    }

    private void RequireLocked(string requestId)
    {
        // The owner predicate observes the original binding only; it performs no mutation or await.
        if (closed || requestId != RequestId || !ownerCurrent())
            throw new InvalidOperationException("definitionInput belongs to its exact active request and original owner.");
    }

    internal void Require(string requestId) { lock (gate) RequireLocked(requestId); }

    internal JsonElement Consume(string requestId)
    {
        lock (gate)
        {
            RequireLocked(requestId);
            if (consumed) throw new InvalidOperationException("definitionInput was already captured; use the frozen retained request.");
            consumed = true;
            return input.Clone();
        }
    }

    internal void ConfirmApplied(string requestId)
    {
        lock (gate)
        {
            RequireLocked(requestId);
            if (!consumed) throw new InvalidOperationException("definitionInput was not captured by the ordinary chat builder.");
            applied = true;
        }
    }

    private void CheckRetainedLocked(string requestId, JsonElement frozenInput)
    {
        RequireLocked(requestId);
        if (!(exact ? MainWorkflowDefinitionInput.MatchesFrozenExact(frozenInput, input) :
            MainWorkflowDefinitionInput.MatchesFrozenExtras(frozenInput, input)))
            throw new InvalidOperationException("definitionInput differs from the frozen retained request.");
    }

    internal void CheckRetained(string requestId, JsonElement frozenInput)
    { lock (gate) CheckRetainedLocked(requestId, frozenInput); }

    internal void ValidateRetained(string requestId, JsonElement frozenInput)
    {
        lock (gate)
        {
            CheckRetainedLocked(requestId, frozenInput);
            consumed = true;
            applied = true;
        }
    }

    public void Dispose() { lock (gate) closed = true; }
}
