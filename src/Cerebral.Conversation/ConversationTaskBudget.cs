using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    // History is authored by the Worker from the authenticated caller and frozen profile.
    // A model action cannot change this admission or its task's typed owner choices.
    private sealed record TaskBudgetAdmission(int SchemaVersion, string RunId, string ProfileId,
        long ProfileRevision, string Origin, int MaxContextTokens, int MaxResponseTokens,
        int CurrentContextTokens, int DefaultContextTokens, int DefaultResponseTokens,
        bool ContextSelectable, bool ResponseSelectable, bool AllowSelection);
    private sealed record TaskBudgetGenerationReceipt(int SchemaVersion, string RunId,
        int CommandVersion, string ArgumentsSha256);

    private static TaskBudgetAdmission ReadTaskBudget(JsonElement value, string runId, string profileId)
    {
        ObjectFields(value, ["schemaVersion", "runId", "profileId", "profileRevision", "origin",
            "maxContextTokens", "maxResponseTokens", "currentContextTokens", "defaultContextTokens",
            "defaultResponseTokens", "contextSelectable", "responseSelectable", "allowSelection"], []);
        static bool Boolean(JsonElement field) => field.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException("A task budget permission must be a Boolean.")
        };
        var admission = new TaskBudgetAdmission(
            (int)Integer(value.GetProperty("schemaVersion"), 1, 1),
            RequiredText(value, "runId", 128), RequiredText(value, "profileId", 128),
            Integer(value.GetProperty("profileRevision"), 0, long.MaxValue),
            RequiredText(value, "origin", 32),
            (int)Integer(value.GetProperty("maxContextTokens"), 512, 1048576),
            (int)Integer(value.GetProperty("maxResponseTokens"), 128, 131072),
            (int)Integer(value.GetProperty("currentContextTokens"), 512, 1048576),
            (int)Integer(value.GetProperty("defaultContextTokens"), 512, 1048576),
            (int)Integer(value.GetProperty("defaultResponseTokens"), 128, 131072),
            Boolean(value.GetProperty("contextSelectable")),
            Boolean(value.GetProperty("responseSelectable")),
            Boolean(value.GetProperty("allowSelection")));
        if (admission.SchemaVersion != 1 || admission.RunId != runId || admission.ProfileId != profileId ||
            admission.ProfileRevision < 0 || admission.Origin is not ("owner" or "coordinator" or "worker") ||
            admission.MaxContextTokens is < 512 or > 1048576 || admission.MaxResponseTokens is < 128 or > 131072 ||
            admission.CurrentContextTokens is < 512 or > 1048576 ||
            admission.DefaultContextTokens is < 512 or > 1048576 ||
            admission.DefaultResponseTokens is < 128 or > 131072 ||
            admission.DefaultContextTokens > admission.MaxContextTokens ||
            admission.DefaultResponseTokens > admission.MaxResponseTokens ||
            admission.DefaultResponseTokens >= admission.DefaultContextTokens ||
            admission.AllowSelection && admission.Origin == "owner" ||
            admission.AllowSelection && !admission.ContextSelectable && !admission.ResponseSelectable)
            throw new ArgumentException("The host task budget admission is invalid or belongs to another task.");
        return admission;
    }

    private static HarnessState ReadTaskBudgetAdmission(JsonElement result, HarnessState state, Input input,
        string runId, string profileId)
    {
        var fields = result.EnumerateObject().Where(property =>
            property.Name.Equals("taskBudget", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (fields.Length == 0)
        {
            if (!state.HistoryLoaded && (input.RequestedContextTokens is not null ||
                input.RequestedResponseTokens is not null || input.MaxContextTokens is not null ||
                input.MaxResponseTokens is not null))
                throw new ArgumentException("A typed budget request has no accepted owner policy.");
            return state;
        }
        if (fields.Length != 1 || fields[0].Name != "taskBudget" || fields[0].Value.ValueKind == JsonValueKind.Null)
            throw new ArgumentException("The host task budget policy is ambiguous.");
        var admission = ReadTaskBudget(fields[0].Value, runId, profileId);
        if (state.LocalGenerationV2 is not null)
            throw new ArgumentException("A fixed local generation choice cannot adopt a task budget policy.");
        if (state.HistoryLoaded)
        {
            if (state.TaskBudget != admission)
                throw new ArgumentException("History changed the accepted task budget policy.");
            return state;
        }
        if (input.MaxContextTokens is { } childContext && childContext != admission.MaxContextTokens ||
            input.MaxResponseTokens is { } childOutput && childOutput != admission.MaxResponseTokens ||
            input.RequestedContextTokens is { } context &&
                (!admission.ContextSelectable || context > admission.MaxContextTokens ||
                    context > admission.CurrentContextTokens) ||
            input.RequestedResponseTokens is { } output &&
                (!admission.ResponseSelectable || output > admission.MaxResponseTokens) ||
            input.ResponseSharePercent is not null && input.RequestedResponseTokens is not null)
            throw new ArgumentException("The typed budget request exceeds this task's owner and child bounds.");
        var selectedContext = input.RequestedContextTokens ?? admission.DefaultContextTokens;
        var selectedResponse = input.RequestedResponseTokens ??
            (input.ResponseSharePercent is { } share
                ? (int)((long)selectedContext * share / 100) : admission.DefaultResponseTokens);
        if (selectedResponse > admission.MaxResponseTokens)
            throw new ArgumentException("The selected response share exceeds the accepted owner or child output bound.");
        if (selectedResponse >= selectedContext)
            throw new ArgumentException("The selected response allowance leaves no task input room.");
        return state with { TaskBudget = admission, SelectedContextTokens = selectedContext,
            SelectedResponseTokens = selectedResponse };
    }

    private static void ValidateTaskBudgetState(HarnessState state, AgentHarnessSnapshot snapshot)
    {
        if (snapshot.State.ValueKind != JsonValueKind.Object) return;
        foreach (var name in new[] { "taskBudget", "selectedContextTokens", "selectedResponseTokens",
                     "pendingTaskBudgetGeneration" })
        {
            var fields = snapshot.State.EnumerateObject().Where(property =>
                property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (fields.Length > 1 || fields.Length == 1 && fields[0].Name != name)
                throw new ArgumentException("The saved task budget state is ambiguous.");
        }
        if (state.TaskBudget is not { } policy)
        {
            if (state.SelectedContextTokens is not null || state.SelectedResponseTokens is not null ||
                state.PendingTaskBudgetGeneration is not null)
                throw new ArgumentException("A saved budget request has no accepted policy.");
            return;
        }
        if (!state.HistoryLoaded || policy.RunId != snapshot.Run.Id ||
            policy.ProfileId != snapshot.Submission.ProfileId ||
            state.LocalGenerationV2 is not null ||
            state.SelectedContextTokens is not { } context || context > policy.MaxContextTokens || context < 512 ||
            state.SelectedResponseTokens is not { } output || output > policy.MaxResponseTokens || output < 128 ||
            output >= context ||
            snapshot.Submission.Input.TryGetProperty("requestedContextTokens", out var requestedContext) &&
                context != (int)Integer(requestedContext, 512, 1048576) ||
            snapshot.Submission.Input.TryGetProperty("requestedResponseTokens", out var requestedOutput) &&
                output != (int)Integer(requestedOutput, 128, 131072) ||
            snapshot.Submission.Input.TryGetProperty("responseSharePercent", out var responseShare) &&
                output != (int)((long)context * Integer(responseShare, 5, 50) / 100))
            throw new ArgumentException("The saved task budget differs from accepted owner and child bounds.");
        var raw = snapshot.State.GetProperty("taskBudget");
        if (ReadTaskBudget(raw, snapshot.Run.Id, snapshot.Submission.ProfileId) != policy)
            throw new ArgumentException("The saved task budget policy marker changed.");
        if (state.PendingTaskBudgetGeneration is { } receipt &&
            (receipt.SchemaVersion != 1 || receipt.RunId != snapshot.Run.Id || receipt.CommandVersion != 4 ||
             state.Phase != "model" || state.PendingArguments is not null ||
             receipt.ArgumentsSha256 is not { Length: 64 } hash ||
             hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new ArgumentException("The pending task budget receipt is invalid.");
    }

    private static WorkflowStep RequestTaskBudget(Input input, HarnessState state, JsonElement action)
    {
        if (state.TaskBudget is not { AllowSelection: true } policy)
            return Repair(input, state, "This task has no owner-approved autonomous budget selection.");
        var context = state.SelectedContextTokens ?? policy.DefaultContextTokens;
        var output = state.SelectedResponseTokens ?? policy.DefaultResponseTokens;
        if (action.TryGetProperty("contextTokens", out var contextValue))
        {
            if (!policy.ContextSelectable || input.RequestedContextTokens is not null)
                return Repair(input, state, "The task context is fixed by its owner.");
            context = (int)Integer(contextValue, 512, 1048576);
            if (input.ResponseSharePercent is { } share)
                output = (int)((long)context * share / 100);
        }
        if (action.TryGetProperty("responseTokens", out var responseValue))
        {
            if (!policy.ResponseSelectable || input.RequestedResponseTokens is not null)
                return Repair(input, state, "The task response allowance is fixed by its owner.");
            output = (int)Integer(responseValue, 128, 131072);
        }
        if (context > policy.MaxContextTokens || output > policy.MaxResponseTokens || output >= context)
            return Repair(input, state, "Choose task allowances within the owner and child bounds, leaving input room.");
        if (context == state.SelectedContextTokens && output == state.SelectedResponseTokens)
            return Repair(input, state, "The task already uses those allowances. Continue the work.");
        return RequestModel(input, state with { SelectedContextTokens = context, SelectedResponseTokens = output });
    }

    private static string WithTaskBudgetAnchors(HarnessState state, string anchors)
    {
        if (state.TaskBudget is not { } policy) return anchors;
        var node = JsonNode.Parse(anchors)!.AsObject();
        node["taskBudget"] = JsonSerializer.SerializeToNode(new
        {
            policy.Origin, policy.MaxContextTokens, policy.MaxResponseTokens,
            policy.CurrentContextTokens, contextTokens = state.SelectedContextTokens,
            responseTokens = state.SelectedResponseTokens, policy.ContextSelectable,
            policy.ResponseSelectable, policy.AllowSelection,
            observation = "Context is the total window. Actual prepared fit and current owner policy are rechecked before inference."
        }, Json);
        return node.ToJsonString(Json);
    }

    private static JsonElement WithTaskBudgetAction(Input input, HarnessState state, JsonElement schema)
    {
        if (state.TaskBudget is not { AllowSelection: true } policy || state.LocalGenerationV2 is not null)
            return schema;
        var contextEnabled = policy.ContextSelectable && input.RequestedContextTokens is null;
        var responseEnabled = policy.ResponseSelectable && input.RequestedResponseTokens is null &&
            input.ResponseSharePercent is null;
        if (!contextEnabled && !responseEnabled) return schema;
        var properties = new JsonObject { ["action"] = new JsonObject { ["const"] = "budget" } };
        if (contextEnabled) properties["contextTokens"] =
            new JsonObject { ["type"] = "integer", ["minimum"] = 512,
                ["maximum"] = Math.Min(policy.MaxContextTokens, policy.CurrentContextTokens) };
        if (responseEnabled) properties["responseTokens"] =
            new JsonObject { ["type"] = "integer", ["minimum"] = 128, ["maximum"] = policy.MaxResponseTokens };
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        var anyOf = new JsonArray();
        if (contextEnabled) anyOf.Add(new JsonObject { ["required"] = new JsonArray("contextTokens") });
        if (responseEnabled) anyOf.Add(new JsonObject { ["required"] = new JsonArray("responseTokens") });
        node["oneOf"]!.AsArray().Add(new JsonObject { ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray("action"), ["additionalProperties"] = false,
            ["anyOf"] = anyOf });
        return Element(node);
    }

    private static void ApplyTaskBudgetArguments(HarnessState state, IDictionary<string, object> arguments)
    {
        if (state.Phase is not ("model" or "compact") || state.TaskBudget is null) return;
        // In a compact envelope these are the next owner generation's fit requirements,
        // not the summarizer's independent output allowance.
        arguments["contextLimit"] = state.SelectedContextTokens!.Value;
        arguments["responseLimit"] = state.SelectedResponseTokens!.Value;
        arguments.Remove("responseSharePercent");
    }

    private static bool TaskBudgetCompactionEffectVersion(HarnessState state, WorkflowEffect effect)
    {
        if (state.TaskBudget is null) return effect.CommandVersion == 1;
        if (effect.CommandVersion is not (1 or 2) || effect.Arguments.ValueKind != JsonValueKind.Object)
            return false;
        var typed = effect.CommandVersion == 2;
        string[] allowed = typed
            ? ["messages", "contextLimit", "responseLimit", "responseSchema", "toolNames", "context", "reasoningTokens"]
            : ["messages", "responseLimit", "responseSharePercent", "responseSchema", "toolNames", "context"];
        var names = effect.Arguments.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Any(name => !allowed.Contains(name, StringComparer.Ordinal)) ||
            !effect.Arguments.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array ||
            !effect.Arguments.TryGetProperty("context", out var window) ||
            !JsonElement.DeepEquals(window, Element(Window(state))))
            return false;
        // Retained v1 receipts keep their original closed shape and candidate checks.
        // They never acquire v2 fit authority or cause their effect to be replayed.
        if (!typed) return true;
        if (!effect.Arguments.TryGetProperty("contextLimit", out var context) ||
            !context.TryGetInt32(out var contextLimit) || contextLimit != state.SelectedContextTokens ||
            !effect.Arguments.TryGetProperty("responseLimit", out var response) ||
            !response.TryGetInt32(out var responseLimit) || responseLimit != state.SelectedResponseTokens)
            return false;
        var expectedReasoning = state.ReasoningAdjustment is not null ? state.RequestedReasoningTokens : null;
        return effect.Arguments.TryGetProperty("reasoningTokens", out var reasoning)
            ? reasoning.TryGetInt32(out var reasoningTokens) && reasoningTokens == expectedReasoning
            : expectedReasoning is null;
    }

}
