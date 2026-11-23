using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private static bool IsBroadInventoryArguments(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object && (!arguments.EnumerateObject().Any() ||
            arguments.EnumerateObject().Count() == 1 && arguments.TryGetProperty("continuation", out var continuation) &&
            continuation.ValueKind == JsonValueKind.String && continuation.GetString() is { Length: 32 } token &&
            token.All(char.IsAsciiHexDigit));

    private static bool HasInventoryIdentityEnvelope(JsonElement root) => root.EnumerateObject().Any(property =>
        property.Name is "identitySummaryComplete" or "identityScaffoldReady" or "identitySummary" or
            "inventoryJobId" or "inventoryComplete" or "inventoryId" or "inventoryPage" or
            "totalProjectsFound" or "totalDirectoriesScanned" or "continuation");

    private static bool CompleteIdentitySummaryAdmission(HarnessState state, WorkflowEffect effect,
        ToolData data, JsonElement root, JsonElement arguments, out string diagnostic)
    {
        diagnostic = "The provider result does not prove complete identity coverage for this run. Inspect its job, count and traversal fields; use a supplied continuation or report the exact missing precondition.";
        // This explicit optional contract never reinterprets legacy limited/scaffoldReady fields.
        // The provider owns complete job-scoped scanning and rechecks it at the actual writer.
        if (!IsBroadInventoryArguments(arguments) ||
            data.Evidence is not { ProviderId: "cerebral.plugin-sdk" } evidence ||
            evidence.RequestId is { } requestId && requestId != effect.Id ||
            !root.TryGetProperty("identitySummaryComplete", out var complete) || complete.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("identityScaffoldReady", out var ready) || ready.ValueKind != JsonValueKind.True ||
            !TryText(root, "inventoryJobId", 128, out var jobId) || jobId != state.AdmissionRecoveryRunId ||
            !root.TryGetProperty("inventoryComplete", out var traversed) || traversed.ValueKind != JsonValueKind.True ||
            !TryText(root, "inventoryId", 32, out var inventoryId) || inventoryId.Length != 32 || !inventoryId.All(char.IsAsciiHexDigit) ||
            !root.TryGetProperty("inventoryPage", out var pageValue) || pageValue.ValueKind != JsonValueKind.Number ||
            !pageValue.TryGetInt32(out var page) || page is < 1 or > 32 ||
            !root.TryGetProperty("discoverySummaryOnly", out var summary) || summary.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("continuation", out var next) || next.ValueKind != JsonValueKind.Null ||
            !root.TryGetProperty("identitySummary", out var identities) || identities.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("totalProjectsFound", out var total) || total.ValueKind != JsonValueKind.Number ||
            !total.TryGetInt32(out var count) ||
            count != identities.GetArrayLength() || data.Content.Length > 32768)
            return false;

        diagnostic = "Reported source identity metadata is incomplete or inconsistent. Inspect the exact receipt/root before inferring that a requested identity is absent.";
        var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            foreach (var identity in identities.EnumerateArray())
            {
                ObjectFields(identity, ["root", "id", "name", "components"], []);
                if (!TryText(identity, "root", MaximumBindingPathCharacters, out var path) ||
                    !TryCanonicalAbsolutePath(path, out var canonical) || !roots.Add(canonical) ||
                    !IdentityText(identity, "id") || !IdentityText(identity, "name") ||
                    identity.GetProperty("components") is not { ValueKind: JsonValueKind.Array } components ||
                    components.GetArrayLength() is < 1 or > 2) return false;
                var kinds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var component in components.EnumerateArray())
                {
                    ObjectFields(component, ["component", "id", "name"], []);
                    if (!TryText(component, "component", 32, out var kind) || kind is not ("desktop" or "service") ||
                        !kinds.Add(kind) || !IdentityText(component, "id") || !IdentityText(component, "name")) return false;
                }
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
        // A full raw aggregate with only a clipped model summary still cannot establish absence.
        // Keep one complete retained reference, rather than unioning partial historical pages.
        var projection = ToolModelProjection.ReadableProjection("plugin.projects", data.Content);
        if (projection.Complete && ProjectionComplete(projection.Text)) return true;
        diagnostic = "The provider scan reports complete identity coverage, but the bounded model projection omitted identity details. Fetch exact receipt pages for those details; scaffold remains unavailable.";
        return false;
    }

    private static bool IdentityText(JsonElement value, string name) =>
        TryText(value, name, 256, out var text) && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl);
}
