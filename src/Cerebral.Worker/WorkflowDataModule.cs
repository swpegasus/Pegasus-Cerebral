using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;

namespace Cerebral.Worker;

/// <summary>Source lineage is resolved by host admission, separately from the new execution request.</summary>
public sealed record WorkflowDataRunInput(string SourceRequestId, string Id, string Version, string Hash, JsonElement Arguments);

/// <summary>One fixed pure interpreter for an exact host-frozen, project-owned data edition.
/// It contributes no effect handlers and never performs provider or model calls.</summary>
public sealed class WorkflowDataModule : IWorkflowModule
{
    public const string ModuleId = "cerebral.workflow-data";
    public const string ModuleVersion = "1.0.0";
    public const string DefinitionId = ModuleId + ":execute";
    public const string DefinitionVersion = "1.0";
    public string Id => ModuleId;
    public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new Definition()];
    public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
    public static ServicePluginPackageBinding PackageBinding => new(ModuleId, ModuleVersion, true,
        [new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, null, null,
            false, WorkerWorkflowEffects.ModuleVersion, true)]);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private static readonly JsonElement InputSchema = Parse("""
        {"type":"object","properties":{
          "sourceRequestId":{"type":"string","minLength":1,"maxLength":128},
          "id":{"type":"string","minLength":1,"maxLength":128},
          "version":{"type":"string","minLength":1,"maxLength":64},
          "hash":{"type":"string","minLength":64,"maxLength":64},
          "arguments":{"type":"object"}
        },"required":["sourceRequestId","id","version","hash","arguments"],"additionalProperties":false}
        """);
    private static readonly JsonElement StateSchema = Parse("""
        {"type":"object","properties":{"index":{"type":"integer","minimum":0,"maximum":16},"results":{"type":"object"}},
         "required":["index","results"],"additionalProperties":false}
        """);
    private static readonly JsonElement ToolDataSchema = Parse("""
        {"type":"object","properties":{
          "name":{"type":"string","minLength":1,"maxLength":256},"content":{"type":"string"},
          "truncated":{"type":"boolean"},"evidence":{},"followUpRequired":{"type":"boolean"},
          "requiredSuccessfulTools":{"oneOf":[{"type":"null"},{"type":"array","maxItems":8,"items":{"type":"string","minLength":1,"maxLength":256}}]},
          "generation":{}
        },"required":["name","content","truncated","evidence","followUpRequired"],"additionalProperties":false}
        """);
    private sealed record ExecutionState(int Index, Dictionary<string, JsonElement> Results);

    private sealed class Definition : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new(DefinitionId, DefinitionVersion,
            "Execute a bounded linear tool sequence from one exact host-frozen project data edition.", InputSchema, []);

        public WorkflowStep Advance(WorkflowRun run)
        {
            try
            {
                if (run.CancelRequested) return Failure(run, "Workflow data execution was cancelled.");
                WorkflowSchema.Validate(run.Submission.Input, InputSchema);
                var input = Read<WorkflowDataRunInput>(run.Submission.Input);
                if (string.IsNullOrWhiteSpace(input.SourceRequestId) || input.SourceRequestId.Contains('\0'))
                    throw new InvalidDataException("The source request binding is invalid.");
                // Input cannot supply this edition or a project grant. Only the admitting host inserts workflowData.
                WorkflowSchema.Validate(run.Submission.SettingsSnapshot, WorkflowContract.EmptyObject);
                if (!run.Submission.SettingsSnapshot.TryGetProperty("workflowData", out var frozen))
                    throw new InvalidDataException("The host-frozen workflow data edition is missing.");
                var entry = Read<WorkflowDataEntry>(frozen);
                WorkflowDataCatalog.Validate(entry.Edition);
                if (string.IsNullOrWhiteSpace(run.Submission.ProjectId) ||
                    !string.Equals(entry.ProjectId, run.Submission.ProjectId, StringComparison.Ordinal) ||
                    entry.Edition.Id != input.Id || entry.Edition.Version != input.Version ||
                    entry.Hash != input.Hash || WorkflowDataCatalog.Hash(entry.Edition) != entry.Hash)
                    throw new InvalidDataException("The frozen workflow data identity, project or content hash does not match.");
                WorkflowSchema.Validate(input.Arguments, entry.Edition.InputSchema);
                var edition = entry.Edition;
                var initial = run.State.ValueKind == JsonValueKind.Object && !run.State.EnumerateObject().Any();
                var state = initial ? new ExecutionState(0, new(StringComparer.Ordinal)) : ReadState(run.State, edition);
                if (initial)
                {
                    if (run.Signal.Kind != WorkflowSignalKind.Start || run.Effect is not null)
                        return Failure(run, "Workflow data execution has no matching initial transition.");
                }
                else
                {
                    if (state.Index >= edition.Stages.Count || run.Signal.Kind != WorkflowSignalKind.EffectResult ||
                        run.Effect is not { } effect)
                        return Failure(run, "Workflow data execution has no matching saved stage result.");
                    var stage = edition.Stages[state.Index];
                    var expected = ToolArguments(stage, input.Arguments, state.Results);
                    if (effect.CommandId != WorkerWorkflowEffects.ToolCommand || effect.CommandVersion != 1 ||
                        !JsonElement.DeepEquals(effect.Arguments, expected))
                        return Failure(run, "A saved effect belongs to another workflow data stage.", stage.Id);
                    if (effect.State != WorkflowEffectState.Succeeded || effect.Result is not { } result)
                        return Failure(run, "The workflow data stage has no confirmed successful tool result.", stage.Id);
                    var data = ReadToolData(result, stage.Tool);
                    if (data.Truncated || data.FollowUpRequired || data.Generation is not null ||
                        data.RequiredSuccessfulTools is { Count: > 0 })
                        return Failure(run, "The workflow data stage requires complete tool output without additional model or follow-up work.", stage.Id);
                    state.Results.Add(stage.Id, result.Clone());
                    state = state with { Index = state.Index + 1 };
                }

                var persisted = BoundedState(state);
                if (state.Index == edition.Stages.Count)
                {
                    var output = WorkflowDataCatalog.ResolveTemplate(edition.Output, input.Arguments, state.Results);
                    WorkflowSchema.Validate(output, edition.OutputSchema);
                    return new(persisted, new CompleteWorkflow(output));
                }
                var next = edition.Stages[state.Index];
                return new(persisted, new InvokeWorkflowCommand(WorkerWorkflowEffects.ToolCommand, 1,
                    ToolArguments(next, input.Arguments, state.Results)));
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or JsonException)
            {
                // Validation diagnostics describe the boundary, never an arbitrary provider result or effect.Error.
                var message = error.Message.Replace('\0', ' ');
                return Failure(run, message.Length <= 512 ? message : message[..512]);
            }
        }
    }

    private static ExecutionState ReadState(JsonElement value, WorkflowDataEdition edition)
    {
        WorkflowSchema.Validate(value, StateSchema);
        var state = Read<ExecutionState>(value);
        if (state.Results is null || state.Index > edition.Stages.Count || state.Results.Count != state.Index)
            throw new InvalidDataException("The saved workflow data stage index and completed results disagree.");
        for (var index = 0; index < state.Index; index++)
        {
            var stage = edition.Stages[index];
            if (!state.Results.TryGetValue(stage.Id, out var result))
                throw new InvalidDataException("The saved workflow data results are not an exact completed stage prefix.");
            var data = ReadToolData(result, stage.Tool);
            if (data.Truncated || data.FollowUpRequired || data.Generation is not null ||
                data.RequiredSuccessfulTools is { Count: > 0 })
                throw new InvalidDataException("The saved workflow data prefix contains incomplete tool output.");
        }
        return state;
    }

    private static WorkerWorkflowEffects.ToolData ReadToolData(JsonElement result, string tool)
    {
        WorkflowSchema.Validate(result, ToolDataSchema);
        var evidenceBody = result.GetProperty("evidence");
        if (evidenceBody.ValueKind != JsonValueKind.Null &&
            (evidenceBody.ValueKind != JsonValueKind.Object || !evidenceBody.TryGetProperty("outcome", out _) ||
             !evidenceBody.TryGetProperty("providerId", out var provider) || provider.ValueKind != JsonValueKind.String ||
             string.IsNullOrWhiteSpace(provider.GetString())))
            throw new InvalidDataException("Explicit tool evidence must identify its provider and outcome.");
        var data = Read<WorkerWorkflowEffects.ToolData>(result);
        if (data.Name != tool) throw new InvalidDataException("The tool result does not match the frozen stage tool.");
        if (data.Evidence is { } evidence && (evidence.Partial == true || evidence.Outcome != ToolExecutionOutcome.Succeeded))
            throw new InvalidDataException("The workflow data stage requires complete successful tool evidence.");
        return data;
    }

    private static JsonElement ToolArguments(WorkflowDataStage stage, JsonElement input,
        IReadOnlyDictionary<string, JsonElement> results)
    {
        var arguments = WorkflowDataCatalog.ResolveTemplate(stage.Arguments, input, results);
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Resolved workflow data tool arguments must be an object.");
        return JsonSerializer.SerializeToElement(new WorkerWorkflowEffects.ToolArguments(stage.Tool, arguments, stage.WorkingDirectory), Json);
    }

    private static JsonElement BoundedState(ExecutionState state)
    {
        var result = JsonSerializer.SerializeToElement(state, Json);
        WorkflowSchema.Validate(result, StateSchema);
        return result;
    }

    private static WorkflowStep Failure(WorkflowRun run, string error, string? stageId = null)
    {
        var state = WorkflowContract.EmptyObject;
        try { WorkflowSchema.Validate(run.State, WorkflowContract.EmptyObject); state = run.State.Clone(); }
        catch (Exception failure) when (failure is ArgumentException or JsonException) { }
        return new(state, new CompleteWorkflow(JsonSerializer.SerializeToElement(new { success = false, stageId }, Json),
            Success: false, Error: error));
    }

    private static T Read<T>(JsonElement value) => value.Deserialize<T>(Json) ??
        throw new InvalidDataException("Required workflow data is missing.");
    private static JsonElement Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
