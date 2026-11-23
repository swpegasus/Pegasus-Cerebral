using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private static bool QaSourceAuthoringTool(string name) => name is
        "file.write" or "file.edit" or "file.append" or "file.copy" or "file.mkdir" or
        "program.source.generate" or "process.run" or "plugin.verify" or "plugin.present";

    private static HarnessState AdoptQaSourceCatalog(Input input, HarnessState state, CatalogData catalog,
        WorkflowEffect effect, string runId)
    {
        var coordinate = catalog.PluginSource;
        if (coordinate is not null &&
            (coordinate.SchemaVersion != 1 || coordinate.PresentationId != input.AutomaticPluginTestPresentationId ||
             !PluginControlReferences.TryResolveReviewedPresentationId(runId, out var reviewed) ||
             reviewed != coordinate.PresentationId || !CanonicalStoredPath(coordinate.Root)))
            throw new ArgumentException("The catalog source coordinate does not match the exact automatic QA owner review.");
        var prior = state.PluginSource;
        if (prior is not null && (coordinate is null || prior.PresentationId != coordinate.PresentationId ||
            !PathEquals(prior.Root, coordinate.Root)))
            throw new ArgumentException("The retained automatic QA source coordinate changed or disappeared.");
        var reads = state.RetainedFileReads;
        if (prior is null && coordinate is not null &&
            RetainedReadKey(input, "plugin.projects", Element(new { root = coordinate.Root })) is { } inventoryKey)
            // Migration must perform the one newly required exact inspection. Clear only its
            // pre-coordinate cached read; later receipts and every unrelated read keep normal caching.
            reads = reads.Where(item => item.Key != inventoryKey)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return state with
        {
            RetainedFileReads = reads,
            QaSourceCatalogChecked = true,
            PluginSource = coordinate is null ? null : prior ?? new(coordinate.SchemaVersion,
                coordinate.PresentationId, coordinate.Root, effect.Id)
        };
    }

    private static void ValidateQaSourceOwner(Input input, HarnessState state, string runId)
    {
        if (state.PluginSource is { } source &&
            (source.PresentationId != input.AutomaticPluginTestPresentationId ||
             !PluginControlReferences.TryResolveReviewedPresentationId(runId, out var reviewed) ||
             reviewed != source.PresentationId))
            throw new ArgumentException("The saved source coordinate belongs to a different automatic QA owner review.");
    }

    private static void ValidateQaSourceState(HarnessState state)
    {
        if (state.PluginSource is not { } source) return;
        if (!state.QaSourceCatalogChecked || source.SchemaVersion != 1 || !CanonicalStoredPath(source.Root) ||
            !ValidEffectId(source.CatalogEffectId) || source.PresentationId is not { Length: 32 } ||
            !Guid.TryParseExact(source.PresentationId, "N", out var presentation) || presentation == Guid.Empty)
            throw new ArgumentException("The saved automatic QA source coordinate is invalid.");
        if (source.InspectionEffectId is { } inspected &&
            (!ValidEffectId(inspected) || !state.PluginBuildCandidates.Any(candidate =>
                candidate.Origin != "discovery" && PathEquals(candidate.Root, source.Root)) ||
             state.ToolReceipts.SingleOrDefault(receipt => receipt.EffectId == inspected) is not
                { Name: "plugin.projects", State: "Succeeded", Outcome: "Succeeded", Truncated: false }))
            throw new ArgumentException("The automatic QA source coordinate has no successful current-run exact inventory receipt.");
    }

    private static PluginSourceReference? AdoptQaSourceInventory(Input input, HarnessState state,
        WorkflowEffect effect, ToolData? data, bool inventoryParsed, IReadOnlyList<PluginBuildCandidate> candidates)
    {
        if (state.PluginSource is not { } source) return null;
        if (state.PendingName is not ("plugin.projects" or "plugin.scaffold")) return source;
        var matchingRecipes = candidates.Where(candidate => candidate.Origin != "discovery" &&
            PathEquals(candidate.Root, source.Root)).ToArray();
        if (state.PendingName == "plugin.projects" && inventoryParsed && matchingRecipes.Length > 0 &&
            PendingToolArguments(state) is { } arguments && TryActionPath(input, arguments, "root", out var root) &&
            PathEquals(root, source.Root) && effect.State == WorkflowEffectState.Succeeded &&
            data is { Truncated: false, Evidence: { ProviderId: "cerebral.plugin-sdk",
                Outcome: ToolExecutionOutcome.Succeeded, Partial: not true } })
            return source with { InspectionEffectId = effect.Id };
        return matchingRecipes.Length == 0 || !inventoryParsed
            ? source with { InspectionEffectId = null } : source;
    }

    private static bool CanPrepareQaInventorySchema(Input input, HarnessState state, IReadOnlyList<string> names) =>
        input.AutomaticPluginTestPresentationId is not null && state.PluginSource is { InspectionEffectId: null } &&
        !state.QaSourceInventorySchemaLookupUsed && names.Count == 1 && names[0] == "plugin.projects";

    private static string? QaSourcePrerequisiteDiagnostic(Input input, HarnessState state, string name, JsonElement arguments)
    {
        if (!QaSourceAuthoringTool(name) || state.PluginSource is not { } source ||
            !ToolTargetPaths(input, name, arguments).Any(path => PathWithin(path, source.Root)) ||
            source.InspectionEffectId is not null) return null;
        return "This action targets the exact source root retained by the automatic QA owner review, but this run has no " +
            "successful current exact-root inventory. Call plugin.projects with " +
            JsonSerializer.Serialize(new { root = source.Root }, Json) +
            " before source mutation, generation, build, verification or presentation. The coordinate is uninspected reference data, " +
            "not a build recipe, file preimage or permission. No requested action was dispatched.";
    }

    private static object? QaSourceAnchor(HarnessState state) => state.PluginSource is not { } source ? null : new
    {
        referenceOnly = true, source.SchemaVersion, source.PresentationId, source.Root,
        catalogReceipt = source.CatalogEffectId,
        exactInventoryReceipt = source.InspectionEffectId,
        inspected = source.InspectionEffectId is not null,
        next = source.InspectionEffectId is null
            ? "Call plugin.projects with this exact root before source mutation or build; current grants and paths are still checked."
            : "Use the current-run inspected component/build recipe; owner review of any changed generation remains separate."
    };
}
