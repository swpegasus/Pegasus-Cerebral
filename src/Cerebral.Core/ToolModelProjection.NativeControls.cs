using System.Text;
using System.Text.Json;

namespace Cerebral.Core;

public static partial class ToolModelProjection
{
    private static bool ReadableSnapshot(PluginControlSnapshot snapshot) =>
        snapshot.Id is { Length: > 0 and <= 129 } && snapshot.Texts is { Count: <= 20 } &&
        snapshot.Buttons is { Count: <= 20 } && snapshot.Fields is { Count: <= 8 } &&
        snapshot.Texts.All(item => item is { Length: <= 2048 } && !item.Contains('\0')) &&
        snapshot.Texts.Sum(item => item.Length) <= 4096 &&
        snapshot.Buttons.All(item => item is not null && item.Name is { Length: > 0 and <= 256 }) &&
        snapshot.Fields.All(item => item is not null && Guid.TryParseExact(item.Id, "N", out var identity) &&
            identity != Guid.Empty && item.Text is { Length: <= PluginControlTextEdits.MaximumCharacters } &&
            !item.Text.Contains('\0') && Encoding.UTF8.GetByteCount(item.Text) <= PluginControlTextEdits.MaximumUtf8Bytes) &&
        snapshot.Fields.Sum(item => item.Text.Length) <= PluginControlTextEdits.MaximumCharacters;

    private static bool TryReadableNativeControlPayload(JsonElement root, out ToolReadableProjection projection)
    {
        projection = null!;
        if (!root.TryGetProperty("observation", out var value) || value.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("operation", out var operation) || operation.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
            return false;
        try
        {
            var observed = value.Deserialize<PluginControlObservation>(CerebralJson.Options);
            if (observed?.Before is not { } before || !ReadableSnapshot(before) ||
                observed.After is { } after && !ReadableSnapshot(after)) return false;
            var text = new StringBuilder();
            AppendReadable(text, "Control receipt", root, "receipt");
            AppendReadable(text, "Presentation", root, "presentationId");
            AppendReadable(text, "Mount", root, "mountId");
            AppendReadable(text, "Source manifest", root, "sourceManifest");
            AppendReadable(text, "Installed generation", root, "installedGeneration");
            AppendReadable(text, "Installed manifest", root, "installedManifest");
            AppendReadable(text, "Package fingerprint", root, "packageFingerprint");
            if (root.TryGetProperty("installedGeneration", out var generation) &&
                generation.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(generation.GetString()))
                text.AppendLine("Source edits and builds do not change this installed generation. Reopen reloads this generation; a verified replacement requires plugin.present and separate owner review.");
            text.Append("Native effect: ").Append(root.TryGetProperty("nativeStatus", out var native)
                && native.ValueKind == JsonValueKind.String ? native.GetString() : status.GetString())
                .Append("; operation: ").AppendLine(operation.GetString());
            text.Append("Click delivered: ").Append(observed.ClickDelivered)
                .Append("; text set: ").Append(observed.TextSet)
                .Append("; new instance: ").AppendLine(observed.InstanceRecreated.ToString());
            Snapshot("Before", before);
            if (observed.After is { } current) Snapshot("After", current);
            if (root.TryGetProperty("assertion", out var assertion) && assertion.ValueKind == JsonValueKind.Object)
            {
                AppendReadable(text, "Assertion", assertion, "status");
                TextArray("Expected text", assertion, "expectedTexts");
                TextArray("Missing text", assertion, "missingTexts");
                AppendReadable(text, "Assertion diagnostic", assertion, "message");
            }
            else text.AppendLine("No postcondition declared; persistence is unverified.");
            AppendReadable(text, "Diagnostic", root, "error");
            text.AppendLine("Native completion confirms delivery only, not semantic correctness.");
            projection = new(text.ToString(), !before.Limited && before.Fields.All(field => !field.Limited) &&
                observed.After?.Limited != true && observed.After?.Fields.All(field => !field.Limited) != false);
            return true;

            void Snapshot(string label, PluginControlSnapshot snapshot)
            {
                text.Append(label).AppendLine(":");
                text.Append("  Editable fields: ").AppendLine(snapshot.Limited || snapshot.Fields.Any(field => field.Limited)
                    ? "omitted or incomplete" : snapshot.Fields.Count == 0 ? "none" : snapshot.Fields.Count.ToString());
                foreach (var item in snapshot.Texts) text.Append("  Text: ").AppendLine(item);
                foreach (var button in snapshot.Buttons)
                    text.Append("  Button: ").Append(button.Name).Append("; enabled: ").Append(button.Enabled)
                        .Append("; visible: ").AppendLine(button.Visible.ToString());
                foreach (var field in snapshot.Fields)
                    text.Append("  Editable field: ").Append(field.Id).Append("; enabled: ").Append(field.Enabled)
                        .Append("; visible: ").Append(field.Visible).Append("; read-only: ").Append(field.ReadOnly)
                        .Append("; limited: ").Append(field.Limited).Append("; value: ")
                        .AppendLine(JsonSerializer.Serialize(field.Text, CerebralJson.Options));
                if (snapshot.Limited) text.AppendLine("  Observation is limited.");
            }
            void TextArray(string label, JsonElement body, string property)
            {
                if (!body.TryGetProperty(property, out var entries) || entries.ValueKind != JsonValueKind.Array) return;
                foreach (var item in entries.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) text.Append(label).Append(": ").AppendLine(item.GetString());
            }
        }
        catch (JsonException) { return false; }
    }
}
