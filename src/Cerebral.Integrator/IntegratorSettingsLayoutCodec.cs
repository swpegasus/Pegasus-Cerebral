using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.Integrator;

internal sealed record IntegratorSettingsBounds(double Left, double Top, double Width, double Height);
internal sealed record IntegratorSettingsWindow(IntegratorSettingsBounds NormalBounds, bool Maximized);
internal sealed record IntegratorSettingsPaneSizes(double? NavigationPreferredWidth, double MachinesTopShare, double ToolsTopShare);
internal sealed record IntegratorSettingsLayoutValues(IntegratorSettingsWindow Window, string? SelectedPageId,
    IReadOnlyDictionary<string, string> MountSelections, IntegratorSettingsPaneSizes? PaneSizes = null);

internal static class IntegratorSettingsLayoutCodec
{
    internal const int SchemaVersion = 2;
    internal const double DefaultMachinesTopShare = 1d / 2.4d;
    internal const double DefaultToolsTopShare = .5d;
    // Legacy parsing is closed independently; a v2 pane field is never accepted as historical v1.
    private sealed record LegacyValues(IntegratorSettingsWindow Window, string? SelectedPageId,
        IReadOnlyDictionary<string, string> MountSelections);
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 32
    };

    internal static IntegratorSettingsPaneSizes DefaultPaneSizes() => new(null, DefaultMachinesTopShare, DefaultToolsTopShare);

    internal static IntegratorSettingsLayoutValues? Read(JsonElement values)
    {
        ValidateObject(values);
        if (!values.EnumerateObject().Any()) return null; // Stored {} is an explicit native reset in either supported format.
        var value = values.Deserialize<IntegratorSettingsLayoutValues>(Json) ?? throw new InvalidDataException("The saved window layout is empty.");
        ValidateCommon(value.Window, value.SelectedPageId, value.MountSelections);
        if (value.PaneSizes is not { } panes ||
            panes.NavigationPreferredWidth is { } width && (!double.IsFinite(width) || width is < 160 or > 420) ||
            !ValidShare(panes.MachinesTopShare) || !ValidShare(panes.ToolsTopShare))
            throw new InvalidDataException("Saved pane preferences must be finite and within the supported layout range.");
        return value;
    }

    internal static JsonElement UpgradeValidatedV1ToV2(JsonElement raw)
    {
        ValidateObject(raw);
        if (!raw.EnumerateObject().Any()) return raw.Clone();
        var legacy = raw.Deserialize<LegacyValues>(Json) ?? throw new InvalidDataException("The saved legacy layout is empty.");
        ValidateCommon(legacy.Window, legacy.SelectedPageId, legacy.MountSelections);
        return Write(new(legacy.Window, legacy.SelectedPageId, legacy.MountSelections, DefaultPaneSizes()));
    }

    internal static JsonElement Write(IntegratorSettingsLayoutValues value)
    {
        // Keep existing internal three-argument source callers usable; wire v2 still requires explicit pane values.
        var result = JsonSerializer.SerializeToElement(value with { PaneSizes = value.PaneSizes ?? DefaultPaneSizes() }, Json);
        Read(result);
        return result;
    }

    private static bool ValidShare(double value) => double.IsFinite(value) && value is > 0 and < 1;

    private static void ValidateObject(JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(values.GetRawText()) > SettingsPreferenceContract.MaximumLayoutValuesUtf8Bytes)
            throw new InvalidDataException("The saved window layout is invalid or exceeds its supported size.");
        RejectDuplicates(values);
    }

    private static void ValidateCommon(IntegratorSettingsWindow window, string? selectedPageId,
        IReadOnlyDictionary<string, string> mountSelections)
    {
        var bounds = window.NormalBounds;
        // Validate the raw geometry before any monitor fitting or legacy projection.
        if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) || !double.IsFinite(bounds.Width) ||
            !double.IsFinite(bounds.Height) || Math.Abs(bounds.Left) > 100_000 || Math.Abs(bounds.Top) > 100_000 ||
            bounds.Width is <= 0 or > 100_000 || bounds.Height is <= 0 or > 100_000)
            throw new InvalidDataException("Saved window bounds must be finite, positive and within the supported desktop range.");
        if (selectedPageId is { } page) ValidateId(page);
        if (mountSelections.Count > 128) throw new InvalidDataException("The saved layout contains too many page mounts.");
        foreach (var pair in mountSelections) { ValidateId(pair.Key); ValidateId(pair.Value); }
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Contains('\0'))
            throw new InvalidDataException("Saved page and mount identities must be bounded stable IDs.");
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            {
                if (!names.Add(item.Name)) throw new InvalidDataException("The saved layout contains duplicate fields.");
                RejectDuplicates(item.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}

