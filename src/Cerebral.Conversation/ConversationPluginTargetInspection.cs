using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    // At most one exact authoring proposal is deferred. Generated sinks resolve before raw
    // generation, so this reference contains the admitted plan, never generated source text.
    private sealed record PendingPluginTargetAction(string Name, JsonElement Arguments, string TargetPath,
        PendingToolGeneration? Generation = null, bool PreparingSchema = false);

    private static WorkflowStep? TryRequestPluginTargetInspection(Input input, HarnessState state,
        string name, JsonElement arguments, string runId, PendingToolGeneration? generation = null)
    {
        if (input.AutomaticPluginTestPresentationId is not null || state.PendingPluginTargetAction is not null ||
            !state.Discovery.Concat(state.Selected).Any(tool => tool.Name == "plugin.projects")) return null;
        var target = name switch
        {
            "file.write" or "file.edit" or "file.append" or "file.mkdir" or "program.source.generate" =>
                TryActionPath(input, arguments, "path", out var path) ? path : null,
            "file.copy" => TryActionPath(input, arguments, "destinationPath", out var path) ? path : null,
            "plugin.verify" => TryActionPath(input, arguments, "root", out var path) ? path : null,
            "plugin.present" => TryActionPath(input, arguments, "path", out var path) ? path : null,
            "process.run" when arguments.TryGetProperty("arguments", out var argv) && argv.ValueKind == JsonValueKind.Array &&
                argv.GetArrayLength() > 0 && argv[0].ValueKind == JsonValueKind.String &&
                string.Equals(argv[0].GetString(), "build", StringComparison.OrdinalIgnoreCase) =>
                ToolTargetPaths(input, name, arguments).FirstOrDefault(),
            _ => null
        };
        if (target is null || state.PluginBuildCandidates.Any(candidate => candidate.Origin != "discovery" &&
            PathWithin(target, candidate.Root))) return null;
        var selected = state.Selected.FirstOrDefault(tool => tool.Name == "plugin.projects");
        // Discovery contains names only. Resolve one exact schema before constructing publisher arguments.
        if (selected?.InputSchema is not { ValueKind: JsonValueKind.Object } schema)
        {
            var preparing = state with { PendingPluginTargetAction = new(name, arguments.Clone(), target, generation, PreparingSchema: true) };
            return RequestCatalog(input, preparing, ["plugin.projects"], prepareRelated: false);
        }
        // Older publishers do not advertise exact-target lookup. Do not invent its arguments.
        if (!SupportsPluginTargetLookup(schema)) return null;
        var deferred = state with { PendingPluginTargetAction = new(name, arguments.Clone(), target, generation) };
        return RequestTool(input, deferred, "plugin.projects", Element(new { targetPath = target }), runId);
    }

    private static WorkflowStep ContinuePluginTargetSchema(Input input, HarnessState state, string runId)
    {
        var deferred = state.PendingPluginTargetAction!;
        state = state with { PendingPluginTargetAction = null };
        if (TryRequestPluginTargetInspection(input, state, deferred.Name, deferred.Arguments, runId, deferred.Generation) is { } inspection)
            return inspection;
        return deferred.Generation is { } generation ? RequestGeneratedModel(input, state, generation) :
            RequestTool(input, state, deferred.Name, deferred.Arguments, runId, targetInspected: true);
    }

    private static WorkflowStep? GuardCompletedCanonicalBuild(Input input, HarnessState state,
        string name, JsonElement arguments)
    {
        if (name != "process.run" || state.PluginWork is not { } work ||
            !MatchesBuild(input, work.Build, arguments) || work.SuccessfulBuildGeneration != work.SourceGeneration ||
            work.BuildEffectId is null || (!input.UseRepeatedSuccessGuard &&
            (work.VerifiedGeneration != work.SourceGeneration || work.VerifyEffectId is null ||
             work.Build.Component == "desktop" && (work.PresentedGeneration != work.SourceGeneration || work.PresentEffectId is null))))
            return null;
        var next = work.VerifiedGeneration != work.SourceGeneration || work.VerifyEffectId is null
            ? "Complete any separately accepted check on these built bytes, then call plugin.verify on exact root " +
              JsonSerializer.Serialize(work.Build.Root, Json) + "."
            : work.Build.Component == "desktop" && (work.PresentedGeneration != work.SourceGeneration || work.PresentEffectId is null)
                ? "The current package is verified. Call plugin.present for exact built manifest " +
                  JsonSerializer.Serialize(work.Build.BuiltManifest, Json) + " with its retained package fingerprint."
                : "Current verification and presentation receipts remain valid. Inspect the retained receipts or finalize the completed work.";
        var diagnostic = input.UseRepeatedSuccessGuard
            ? "This exact canonical build already succeeded for current source generation " + work.SourceGeneration +
              " in receipt " + work.BuildEffectId + ". No repeated build was dispatched. " + next +
              " A confirmed source change permits a new build."
            : "This exact canonical build already succeeded for current source generation " + work.SourceGeneration +
              " in receipt " + work.BuildEffectId + ". Current verification and presentation receipts remain valid. " +
              "No repeated build was dispatched. Inspect the retained receipts or finalize the completed work; " +
              "a confirmed source change or existing explicit invalidation permits a new build.";
        if (work.CompletedBuildCorrections >= MaximumRepairs)
            return Finish(state, false, "Stopped after two corrections for a repeated completed canonical build. " + diagnostic);
        return Repair(input, state with { PluginWork = work with { CompletedBuildCorrections = work.CompletedBuildCorrections + 1 } }, diagnostic);
    }

    private static bool SupportsPluginTargetLookup(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties) &&
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("targetPath", out _);

    private static WorkflowStep ContinuePluginTargetInspection(Input input, HarnessState prior, HarnessState next,
        WorkflowEffect effect, ToolData? data, string runId)
    {
        var deferred = prior.PendingPluginTargetAction!;
        next = next with { PendingPluginTargetAction = null };
        if (!SuccessfulEffect(effect, data) || data is not { Truncated: false,
            Evidence: { ProviderId: "cerebral.plugin-sdk", Outcome: ToolExecutionOutcome.Succeeded, Partial: not true } } ||
            data.Evidence.RequestId is { } targetReceipt && targetReceipt != effect.Id ||
            !TryPluginTargetResult(input, PendingToolArguments(prior), data.Content, out var matched, out var root, out var component))
            return Repair(input, next, "The SDK exact-target inspection did not return a complete matching receipt. " +
                "No deferred authoring action was dispatched. Inspect its retained diagnostic or exact-inspect the established source root.");
        if (matched)
        {
            var candidate = next.PluginBuildCandidates.SingleOrDefault(candidate =>
                PathEquals(candidate.Root, root!) && candidate.Component == component && candidate.Origin != "discovery");
            if (candidate is null || !PathWithin(deferred.TargetPath, candidate.Root))
                return Repair(input, next, "The SDK exact-target receipt has no matching inspected component. No deferred authoring action was dispatched.");
            next = next with { PluginWork = next.PluginWork is { } current && SameBuild(current.Build, candidate)
                ? current : new(candidate) };
        }
        // The one-use non-project result cannot become a persistent bypass after a later write.
        return deferred.Generation is { } generation ? RequestGeneratedModel(input, next, generation) :
            RequestTool(input, next, deferred.Name, deferred.Arguments, runId, targetInspected: true);
    }

    private static bool TryPluginTargetResult(Input input, JsonElement? arguments, string content,
        out bool matched, out string? root, out string? component)
    {
        matched = false; root = null; component = null;
        if (arguments is not { } pending || !TryActionPath(input, pending, "targetPath", out var requested) ||
            Encoding.UTF8.GetByteCount(content) > MaximumBindingPayloadBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
            var body = document.RootElement;
            UniqueProperties(body);
            if (!TryText(body, "targetPath", MaximumBindingPathCharacters, out var returned) ||
                !TryCanonicalAbsolutePath(returned, out var canonical) || !PathEquals(requested, canonical) ||
                !body.TryGetProperty("targetMatched", out var marker) ||
                marker.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            matched = marker.ValueKind == JsonValueKind.True;
            if (!matched) return !body.TryGetProperty("projects", out var none) ||
                none.ValueKind == JsonValueKind.Array && none.GetArrayLength() == 0;
            if (!TryText(body, "targetComponent", 32, out component) || component is not ("desktop" or "service") ||
                !body.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array ||
                projects.GetArrayLength() != 1 || !TryText(projects[0], "root", MaximumBindingPathCharacters, out var rootText) ||
                !TryCanonicalAbsolutePath(rootText, out root) || !PathWithin(requested, root)) return false;
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return false; }
    }

    private static void ValidatePluginTargetAction(HarnessState state)
    {
        if (state.PendingPluginTargetAction is not { } pending) return;
        if (!CanonicalStoredPath(pending.TargetPath) || pending.Name is not ("file.write" or "file.edit" or
            "file.append" or "file.copy" or "file.mkdir" or "program.source.generate" or "process.run" or
            "plugin.verify" or "plugin.present") || pending.Arguments.ValueKind != JsonValueKind.Object ||
            state.Phase is not ("tool" or "describe") ||
            state.Phase == "tool" && state.PendingName != "plugin.projects")
            throw new ArgumentException("The deferred SDK target inspection is invalid.");
        if (pending.Generation is { } generation) ValidateGenerationRequest(generation.Request);
    }
}
