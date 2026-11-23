using System.Text.Json;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    public const string LocalGenerationVersion = "1.0.5-local-generation.1";
    private readonly bool useLocalGeneration;
    private enum LocalGenerationEdition { Enabled }
    private ConversationAgentHarness(LocalGenerationEdition edition) : this(CatalogPageEdition.Enabled)
    {
        useLocalGeneration = edition == LocalGenerationEdition.Enabled;
        Descriptor = Descriptor with
        {
            Version = LocalGenerationVersion,
            LocalGenerationConsumer = new(1, Generate, 2, "local.native.text")
        };
    }

    /// <summary>Explicit local-generation consumer; old definitions and accepted effects are unchanged.</summary>
    public static ConversationAgentHarness CreateLocalGenerationEdition() => new(LocalGenerationEdition.Enabled);

    private sealed record LocalGenerationState(int SchemaVersion, string RunId, WorkflowLocalGenerationChoice Choice);

    private static void ValidateLocalGenerationChoice(WorkflowLocalGenerationChoice choice)
    {
        choice.Validate();
    }

    private static void ValidateLocalGenerationState(HarnessState state, AgentHarnessSnapshot snapshot, bool enabled)
    {
        var choice = snapshot.Submission.LocalGeneration;
        if (choice is not null) ValidateLocalGenerationChoice(choice);
        if (choice is not null && !enabled)
            throw new ArgumentException("This accepted definition does not consume local generation.");
        if ((enabled && choice is not null) != (state.LocalGenerationV2 is not null))
            throw new ArgumentException("The saved local-generation marker does not match the accepted choice.");
        if (state.LocalGenerationV2 is { } local &&
            (local.SchemaVersion != 1 || local.RunId != snapshot.Run.Id || local.Choice != choice))
            throw new ArgumentException("The saved local-generation choice belongs to another run or accepted input.");
        if (state.LocalGenerationV2 is { } accepted && state.Phase == "model")
        {
            var pending = state.PendingArguments ?? throw new ArgumentException("The opted normal generation has no pending arguments.");
            ObjectFields(pending, ["messages", "responseSchema", "toolNames", "context", "responseLimit"], ["responseSharePercent"]);
            var limit = pending.GetProperty("responseLimit");
            if (limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out var output) ||
                output != accepted.Choice.VisibleOutputTokens ||
                pending.TryGetProperty("responseSharePercent", out var share) && share.ValueKind != JsonValueKind.Null)
                throw new ArgumentException("The saved normal-generation arguments do not match the accepted local choice.");
        }
        if (snapshot.State.ValueKind != JsonValueKind.Object) return;
        var fields = snapshot.State.EnumerateObject()
            .Where(p => p.Name.Equals("localGenerationV2", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (fields.Length > 1 || fields.Length == 1 && fields[0].Name != "localGenerationV2")
            throw new ArgumentException("The local-generation marker is ambiguous.");
        if (fields.Length == 0) return;
        var marker = fields[0].Value;
        ObjectFields(marker, ["schemaVersion", "runId", "choice"], []);
        ObjectFields(marker.GetProperty("choice"), ["schemaVersion", "route", "contextTokens", "visibleOutputTokens"], []);
    }

    private static void ValidateLocalGenerationInput(Input input, HarnessState state)
    {
        if (state.LocalGenerationV2 is not null && input.ResponseSharePercent is not null)
            throw new ArgumentException("Accepted local generation uses its explicit visible-token choice; response share must be absent.");
    }

    private static int ConversationCommandVersion(HarnessState state, string command) =>
        command == Compact && state.Phase == "compact" && state.TaskBudget is not null ? 2 :
        command == Generate && state.Phase == "model" && state.TaskBudget is not null ? 4 :
        command == Generate && state.Phase == "model" && state.LocalGenerationV2 is not null ? 2 :
        command == Generate && state.Phase == "model" && state.ReasoningAdjustment is not null ? 3 :
        command == Catalog && state.ToolCatalogPage is not null ? 2 : 1;

    private static bool ConversationEffectVersion(HarnessState state, WorkflowEffect effect) =>
        effect.CommandId == Compact && state.Phase == "compact"
            ? TaskBudgetCompactionEffectVersion(state, effect) :
        effect.CommandId == Generate && state.Phase == "model" && state.TaskBudget is not null
            ? effect.CommandVersion == 4 :
        effect.CommandId == Generate && state.Phase == "model" && state.LocalGenerationV2 is not null
            ? effect.CommandVersion == 2 :
        effect.CommandId == Generate && state.Phase == "model" && state.ReasoningAdjustment is not null
            ? effect.CommandVersion == 3 : CatalogEditionEffectVersion(state, effect);

    private static void ApplyLocalGenerationArguments(HarnessState state, IDictionary<string, object> arguments)
    {
        if (state.Phase != "model" || state.LocalGenerationV2 is not { } local) return;
        // This repeats the frozen host choice, never an executable policy or model-provided grant.
        arguments["responseLimit"] = local.Choice.VisibleOutputTokens;
    }
}
