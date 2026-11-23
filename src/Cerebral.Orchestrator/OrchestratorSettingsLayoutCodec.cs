using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

internal sealed record OrchestratorLayoutBounds(double Left, double Top, double Width, double Height);
internal sealed record OrchestratorLayoutWindow(OrchestratorLayoutBounds NormalBounds, bool Maximized);
internal sealed record OrchestratorLayoutValues(OrchestratorLayoutWindow Window,
    double WorkspacePreferredWidth, double ActivityPreferredWidth, double? ComposerPreferredHeight);

internal static class OrchestratorSettingsLayoutCodec
{
    internal const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 16
    };

    internal static OrchestratorLayoutValues? Read(JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(values.GetRawText()) > SettingsPreferenceContract.MaximumLayoutValuesUtf8Bytes)
            throw new InvalidDataException("The saved Main layout is invalid or exceeds its supported size.");
        RejectDuplicates(values);
        if (!values.EnumerateObject().Any()) return null; // Existing central reset convention.
        var value = values.Deserialize<OrchestratorLayoutValues>(Json) ?? throw new InvalidDataException("The saved Main layout is empty.");
        var bounds = value.Window.NormalBounds;
        if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) || Math.Abs(bounds.Left) > 100_000 || Math.Abs(bounds.Top) > 100_000 ||
            !PositiveBounded(bounds.Width) || !PositiveBounded(bounds.Height) ||
            !PositiveBounded(value.WorkspacePreferredWidth) || value.WorkspacePreferredWidth < 208 ||
            !double.IsFinite(value.ActivityPreferredWidth) || value.ActivityPreferredWidth is < 280 or > 520 ||
            (value.ComposerPreferredHeight is { } height && !PositiveBounded(height)))
            throw new InvalidDataException("Saved Main window and pane dimensions are outside the supported range.");
        return value;
    }

    internal static JsonElement Write(OrchestratorLayoutValues value)
    {
        var result = JsonSerializer.SerializeToElement(value, Json); Read(result); return result;
    }

    private static bool PositiveBounded(double value) => double.IsFinite(value) && value is > 0 and <= 100_000;
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
        {
            if (!names.Add(item.Name)) throw new InvalidDataException("The saved Main layout contains duplicate fields.");
            RejectDuplicates(item.Value);
        }
    }
}
