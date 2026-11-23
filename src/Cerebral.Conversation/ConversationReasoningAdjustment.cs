using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    // This is host history metadata, never a model-created permission or a saved profile edit.
    private sealed record ReasoningAdjustmentPolicy(int SchemaVersion, string RunId, string ProfileId,
        long ProfileRevision, int MinimumTokens, int MaximumTokens, int DefaultTokens);
    private sealed record ReasoningGenerationReceipt(int SchemaVersion, string RunId, int CommandVersion, string ArgumentsSha256);

    private static string ReasoningArgumentsSha256(JsonElement arguments) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(arguments, Json))).ToLowerInvariant();

    private static void MatchPendingReasoningGeneration(HarnessState state, WorkflowEffect effect)
    {
        // Older in-flight effects retain their full pending arguments and exact legacy check.
        if (state.PendingTaskBudgetGeneration is { } taskReceipt)
        {
            if (effect.CommandId != Generate || effect.CommandVersion != taskReceipt.CommandVersion ||
                ReasoningArgumentsSha256(effect.Arguments) != taskReceipt.ArgumentsSha256)
                throw new ArgumentException("Host result does not match the pinned task budget generation arguments.");
            return;
        }
        if (state.PendingReasoningGeneration is not { } receipt) { MatchPending(state, effect); return; }
        if (effect.CommandId != Generate || effect.CommandVersion != receipt.CommandVersion ||
            ReasoningArgumentsSha256(effect.Arguments) != receipt.ArgumentsSha256)
            throw new ArgumentException("Host result does not match the pinned thinking-generation arguments.");
    }

    private static HarnessState ConsumePendingNormalGeneration(HarnessState state, WorkflowEffect effect)
    {
        if (state.ReasoningAdjustment is not null || state.TaskBudget is not null)
        {
            MatchPendingReasoningGeneration(state, effect);
            return state with { PendingArguments = null, PendingReasoningGeneration = null,
                PendingTaskBudgetGeneration = null };
        }
        if (state.LocalGenerationV2 is not null) MatchPending(state, effect);
        return state;
    }

    private const string ReasoningAdjustmentGuidance = """

        This task permits generation-local thinking adjustments within reasoningAdjustment in the host metadata.
        Use reasoning{tokens} before work needing a different allowance; choose a positive allowance for
        difficult analysis and a smaller one for routine work. Zero is allowed only when minimumTokens is zero.
        This changes subsequent thinking for this task, not saved profiles, model selection, tools or helpers.
        Do not repeat the current value. An allowance is a ceiling, not proof of correctness: read relevant
        definitions and confirm evidence before factual claims, including claims that a capability is absent.
        """;

    private static bool ValidReasoningCount(ReasoningAdjustmentPolicy policy, int count) =>
        count == 0 ? policy.MinimumTokens == 0 : count >= 128 && count <= policy.MaximumTokens;

    private static void ValidateReasoningPolicy(ReasoningAdjustmentPolicy policy, string runId)
    {
        if (policy.SchemaVersion != 1 || policy.RunId != runId || string.IsNullOrWhiteSpace(policy.ProfileId) ||
            policy.ProfileId.Length > 128 || policy.ProfileRevision < 0 || policy.MinimumTokens is not (0 or 128) ||
            policy.MaximumTokens is < 128 or > 32768 || !ValidReasoningCount(policy, policy.DefaultTokens))
            throw new ArgumentException("The reasoning policy is invalid or belongs to another accepted task.");
    }

    private static ReasoningAdjustmentPolicy ReadReasoningPolicy(JsonElement value, string runId)
    {
        ObjectFields(value, ["schemaVersion", "runId", "profileId", "profileRevision", "minimumTokens",
            "maximumTokens", "defaultTokens"], []);
        var policy = new ReasoningAdjustmentPolicy((int)Integer(value.GetProperty("schemaVersion"), 1, 1),
            RequiredText(value, "runId", 128), RequiredText(value, "profileId", 128),
            Integer(value.GetProperty("profileRevision"), 0, long.MaxValue),
            (int)Integer(value.GetProperty("minimumTokens"), 0, 128),
            (int)Integer(value.GetProperty("maximumTokens"), 128, 32768),
            (int)Integer(value.GetProperty("defaultTokens"), 0, 32768));
        ValidateReasoningPolicy(policy, runId);
        return policy;
    }

    private static HarnessState ReadReasoningAdjustmentAdmission(JsonElement result, HarnessState state, string runId, string profileId)
    {
        var fields = result.EnumerateObject().Where(property =>
            property.Name.Equals("reasoningAdjustment", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (fields.Length == 0) return state;
        if (fields.Length != 1 || fields[0].Name != "reasoningAdjustment")
            throw new ArgumentException("The host reasoning policy is ambiguous.");
        if (fields[0].Value.ValueKind == JsonValueKind.Null) return state;
        var policy = ReadReasoningPolicy(fields[0].Value, runId);
        if (policy.ProfileId != profileId)
            throw new ArgumentException("The host reasoning policy belongs to another agent profile.");
        if (state.LocalGenerationV2 is not null)
            throw new ArgumentException("The fixed local generation choice cannot adopt a reasoning override.");
        // Later history pages cannot expand a policy or opt an already-admitted legacy run in.
        if (state.HistoryLoaded)
        {
            if (state.ReasoningAdjustment != policy)
                throw new ArgumentException("History changed the accepted task reasoning policy.");
            return state;
        }
        return state with { ReasoningAdjustment = policy };
    }

    private static void ValidateReasoningAdjustmentState(HarnessState state, AgentHarnessSnapshot snapshot)
    {
        if (snapshot.State.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "reasoningAdjustment", "requestedReasoningTokens", "pendingReasoningGeneration" })
            {
                var fields = snapshot.State.EnumerateObject().Where(property =>
                    property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (fields.Length > 1 || fields.Length == 1 && fields[0].Name != name)
                    throw new ArgumentException("The saved reasoning state is ambiguous.");
            }
        if (snapshot.Submission.Input.ValueKind == JsonValueKind.Object &&
            snapshot.Submission.Input.TryGetProperty("requestedReasoningTokens", out var typedRequest) &&
            state.HistoryLoaded && state.RequestedReasoningTokens != (int)Integer(typedRequest, 0, 32768))
            throw new ArgumentException("The saved thinking choice differs from this task's typed selection.");
        if (state.ReasoningAdjustment is not { } policy)
        {
            if (state.RequestedReasoningTokens is not null || state.PendingReasoningGeneration is not null)
                throw new ArgumentException("A reasoning request has no accepted owner policy.");
            return;
        }
        ValidateReasoningPolicy(policy, snapshot.Run.Id);
        if (!state.HistoryLoaded || policy.ProfileId != snapshot.Submission.ProfileId || state.LocalGenerationV2 is not null ||
            state.RequestedReasoningTokens is { } requested && !ValidReasoningCount(policy, requested))
            throw new ArgumentException("The saved reasoning request does not match this accepted task.");
        // Check the raw marker too; case-insensitive deserialization cannot admit alias fields.
        var markers = snapshot.State.EnumerateObject().Where(property =>
            property.Name.Equals("reasoningAdjustment", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (markers.Length != 1 || markers[0].Name != "reasoningAdjustment" ||
            ReadReasoningPolicy(markers[0].Value, snapshot.Run.Id) != policy)
            throw new ArgumentException("The saved reasoning policy marker is ambiguous.");
        if (state.PendingReasoningGeneration is { } receipt)
        {
            ObjectFields(snapshot.State.GetProperty("pendingReasoningGeneration"),
                ["schemaVersion", "runId", "commandVersion", "argumentsSha256"], []);
            if (receipt.SchemaVersion != 1 || receipt.RunId != snapshot.Run.Id || receipt.CommandVersion != 3 ||
                state.Phase != "model" || state.PendingArguments is not null ||
                receipt.ArgumentsSha256 is not { Length: 64 } hash || hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new ArgumentException("The saved thinking-generation receipt is invalid or belongs to another run.");
        }
    }

    private static WorkflowStep RequestReasoningAdjustment(Input input, HarnessState state, JsonElement action)
    {
        if (state.ReasoningAdjustment is not { } policy || state.LocalGenerationV2 is not null)
            return Repair(input, state, "This task has no owner-approved thinking adjustment.");
        var count = (int)Integer(action.GetProperty("tokens"), 0, 32768);
        if (!ValidReasoningCount(policy, count))
            return Repair(input, state, "Choose a thinking allowance within the accepted minimum and maximum; positive values start at 128.");
        if (count == (state.RequestedReasoningTokens ?? policy.DefaultTokens))
            return Repair(input, state, "The task already uses that thinking allowance. Continue the requested work.");
        return RequestModel(input, state with { RequestedReasoningTokens = count });
    }

    private static string WithReasoningAdjustmentAnchors(HarnessState state, string anchors)
    {
        if (state.ReasoningAdjustment is not { } policy) return anchors;
        var node = JsonNode.Parse(anchors)!.AsObject();
        node["reasoningAdjustment"] = JsonSerializer.SerializeToNode(new
        {
            policy.MinimumTokens, policy.MaximumTokens, policy.DefaultTokens,
            requestedTokens = state.RequestedReasoningTokens ?? policy.DefaultTokens,
            observation = "Owner limits for this task; the host rechecks current policy, model and context before each generation."
        }, Json);
        return node.ToJsonString(Json);
    }

    private static JsonElement WithReasoningAdjustmentAction(HarnessState state, JsonElement schema)
    {
        if (state.ReasoningAdjustment is not { } policy || state.LocalGenerationV2 is not null) return schema;
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        var positive = new JsonObject { ["type"] = "integer", ["minimum"] = 128, ["maximum"] = policy.MaximumTokens };
        JsonNode count = positive;
        if (policy.MinimumTokens == 0)
            count = new JsonObject { ["oneOf"] = new JsonArray(new JsonObject { ["const"] = 0 }, positive) };
        node["oneOf"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["action"] = new JsonObject { ["const"] = "reasoning" }, ["tokens"] = count },
            ["required"] = new JsonArray("action", "tokens"), ["additionalProperties"] = false
        });
        return Element(node);
    }
}
