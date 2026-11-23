using System.Globalization;
using System.Text.Json;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    internal sealed record ManagedSlotPresentation(string OpenSlots, string Details)
    {
        internal int? LoadedModels { get; init; }
    }

    // These are protocol bounds for the current managed allocator, not configured or hardware capacity.
    private const int MaximumManagedSlotRows = 256;
    private const int MaximumManagedSlotsPerInstance = 16;
    private const int MaximumManagedSlotPayloadCharacters = 256 * 1024;
    private const string ManagedSlotScope = "local-managed-slot-observation; not hardware maximum or atomic admission";
    private static readonly string[] ManagedSlotEnvelopeFields =
        ["schema", "issuingInstallationId", "executionAdmissionOpen", "runtime", "remote", "scope"];
    private static readonly string[] ManagedSlotPageFields =
        ["schema", "sampledAt", "processGenerationId", "runtimeEpoch", "state", "truncated", "modelAdmissionOpen", "models"];
    private static readonly string[] ManagedSlotRowFields =
        ["modelId", "instanceId", "instanceState", "allocatedSlots", "managedAvailableSlots", "managedQuarantinedSlots"];

    internal static ManagedSlotPresentation ProjectManagedSlotStatus(JsonElement value, string expectedInstallationId)
    {
        try
        {
            if (!ManagedSlotGuid(expectedInstallationId, out _) || value.ValueKind != JsonValueKind.Object ||
                value.GetRawText().Length > MaximumManagedSlotPayloadCharacters ||
                !ManagedSlotFields(value, ManagedSlotEnvelopeFields, out var envelope) ||
                !ManagedSlotText(envelope["schema"], "cerebral.slot-metrics/1") ||
                !ManagedSlotText(envelope["issuingInstallationId"], expectedInstallationId) ||
                !ManagedSlotText(envelope["scope"], ManagedSlotScope) ||
                envelope["remote"].ValueKind != JsonValueKind.Null ||
                !ManagedSlotBoolean(envelope["executionAdmissionOpen"], out var executionAdmission) ||
                !ManagedSlotFields(envelope["runtime"], ManagedSlotPageFields, out var page) ||
                !ManagedSlotText(page["schema"], "cerebral.managed-model-slots/1") ||
                page["processGenerationId"].ValueKind != JsonValueKind.String ||
                !ManagedSlotGuid(page["processGenerationId"].GetString(), out _) ||
                page["runtimeEpoch"].ValueKind != JsonValueKind.Number ||
                !page["runtimeEpoch"].TryGetInt64(out var epoch) || epoch < 0 ||
                !ManagedSlotTimestamp(page["sampledAt"], out var sampled) ||
                !ManagedSlotBoolean(page["truncated"], out var truncated) ||
                !ManagedSlotNullableBoolean(page["modelAdmissionOpen"], out var modelAdmission) ||
                page["models"].ValueKind != JsonValueKind.Array ||
                page["models"].GetArrayLength() > MaximumManagedSlotRows ||
                page["state"].ValueKind != JsonValueKind.String)
                return UnavailableManagedSlots();

            var state = page["state"].GetString();
            if (state is not ("observed" or "partial" or "unavailable")) return UnavailableManagedSlots();
            if (state == "unavailable" && (truncated || page["models"].GetArrayLength() != 0 || modelAdmission is not null))
                return UnavailableManagedSlots();

            var models = new HashSet<string>(StringComparer.Ordinal);
            var instances = new HashSet<Guid>();
            var present = 0; var absent = 0; var unobserved = 0; var incomplete = 0; var counted = 0;
            var allocated = 0; var available = 0; var quarantined = 0;
            foreach (var row in page["models"].EnumerateArray())
            {
                if (!ManagedSlotFields(row, ManagedSlotRowFields, out var fields) ||
                    fields["modelId"].ValueKind != JsonValueKind.String ||
                    !ManagedSlotModelId(fields["modelId"].GetString(), out var modelId) || !models.Add(modelId) ||
                    fields["instanceId"].ValueKind != JsonValueKind.String ||
                    !ManagedSlotGuid(fields["instanceId"].GetString(), out var instanceId) || !instances.Add(instanceId) ||
                    fields["instanceState"].ValueKind != JsonValueKind.String ||
                    !ManagedSlotCount(fields["allocatedSlots"], out var rowAllocated) ||
                    !ManagedSlotCount(fields["managedAvailableSlots"], out var rowAvailable) ||
                    !ManagedSlotCount(fields["managedQuarantinedSlots"], out var rowQuarantined))
                    return UnavailableManagedSlots();

                var instanceState = fields["instanceState"].GetString();
                if (instanceState is "absent" or "unobserved")
                {
                    if (rowAllocated is not null || rowAvailable is not null || rowQuarantined is not null)
                        return UnavailableManagedSlots();
                    if (instanceState == "absent") absent++; else unobserved++;
                    continue;
                }
                if (instanceState != "present" || rowAllocated == 0 ||
                    rowAllocated is { } capacity &&
                    (rowAvailable is { } free && free > capacity || rowQuarantined is { } held && held > capacity) ||
                    rowAvailable is { } a && rowQuarantined is { } q &&
                    a + q > (rowAllocated ?? MaximumManagedSlotsPerInstance))
                    return UnavailableManagedSlots();

                present++;
                if (rowAllocated is not { } allocation || rowAvailable is not { } availability || rowQuarantined is not { } quarantine)
                { incomplete++; continue; }
                // Counters are sampled separately. An impossible combination remains unknown instead of being clamped.
                counted++; allocated += allocation; available += availability; quarantined += quarantine;
            }
            if (state == "observed" && (truncated || unobserved > 0)) return UnavailableManagedSlots();

            var complete = state == "observed" && incomplete == 0;
            var caption = SlotStatusPresentation.AllocatorCaption(complete ? available : null,
                complete ? allocated : null, complete ? quarantined : null);
            if (!complete) caption += state == "unavailable" ? " · unavailable" : " · partial";
            if (!executionAdmission) caption += " · workflow closed";
            if (modelAdmission == false) caption += " · model disabled";
            else if (modelAdmission is null) caption += " · model policy —";

            var counts = complete
                ? $"Managed local slots: available {available}; allocated {allocated}; quarantined {quarantined}.\n"
                : state == "unavailable"
                    ? "Managed allocator observations are unavailable; totals remain unknown.\n"
                    : counted == 0
                        ? "Complete managed local totals remain unknown; no complete present-instance counter observation is available.\n"
                        : $"Complete managed local totals remain unknown. Valid observed subset: available {available}; allocated {allocated}; quarantined {quarantined} across {counted} present instances.\n";
            var coverage = state == "unavailable" ? "" :
                $"Instance observations: {present} present, {absent} absent, {unobserved} unobserved; {incomplete} present instances have incomplete counters.\n";
            var details = counts + coverage +
                $"Local workflow admission: {(executionAdmission ? "open" : "closed")}. Local model admission: {(modelAdmission is true ? "open" : modelAdmission is false ? "disabled" : "unobserved")}.\n" +
                "Admission policy is separate from allocator availability. Free managed slots do not reserve work or prove native provider readiness.\n" +
                "Allocated minus free is not a generating count: preparation, cancellation, cleanup and quarantine are separate. Slot occupants come from an independently aged ownership observation.\n" +
                "Counters are individually sampled, not an atomic admission fence or a hardware concurrency maximum. Remote slot availability is unobserved.\n" +
                $"Sample state: {state}; truncated: {truncated}. Sampled: {sampled:O}; runtime epoch: {epoch}; ModelRuntime process generation: {page["processGenerationId"].GetString()}.";
            return new(caption, details) { LoadedModels = complete ? present : null };
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or
            FormatException or ArgumentException or OverflowException or ObjectDisposedException)
        { return UnavailableManagedSlots(); }
    }

    private static ManagedSlotPresentation UnavailableManagedSlots() => new(SlotStatusPresentation.UnknownAllocatorCaption,
        "Managed local slot metrics are unavailable or inconsistent for the verified Worker. " +
        "Allocator totals and admission policy remain unknown; remote availability is unobserved. No configured capacity or provider readiness is inferred.");

    private static bool ManagedSlotFields(JsonElement value, string[] names, out Dictionary<string, JsonElement> fields)
    {
        fields = new(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
            if (fields.Count >= names.Length || Array.IndexOf(names, property.Name) < 0 || !fields.TryAdd(property.Name, property.Value))
                return false;
        return fields.Count == names.Length;
    }

    private static bool ManagedSlotText(JsonElement value, string expected) =>
        value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    private static bool ManagedSlotGuid(string? value, out Guid result) =>
        Guid.TryParseExact(value, "N", out result) && result != Guid.Empty;

    private static bool ManagedSlotModelId(string? value, out string result)
    {
        result = value ?? "";
        return result.Length is >= 1 and <= 100 &&
            result.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static bool ManagedSlotBoolean(JsonElement value, out bool result)
    {
        result = value.ValueKind == JsonValueKind.True;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool ManagedSlotNullableBoolean(JsonElement value, out bool? result)
    {
        result = null;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (!ManagedSlotBoolean(value, out var observed)) return false;
        result = observed; return true;
    }

    private static bool ManagedSlotCount(JsonElement value, out int? result)
    {
        result = null;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) ||
            count is < 0 or > MaximumManagedSlotsPerInstance) return false;
        result = count; return true;
    }

    private static bool ManagedSlotTimestamp(JsonElement value, out DateTimeOffset sampled)
    {
        sampled = default;
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString();
        return text is { Length: >= 20 and <= 40 } && text[10] == 'T' &&
            (text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal)) &&
            value.TryGetDateTimeOffset(out sampled) && sampled.Offset == TimeSpan.Zero && sampled >= DateTimeOffset.UnixEpoch;
    }
}
