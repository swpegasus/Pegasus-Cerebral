using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Contracts.Agents;

/// <summary>A package-owned, versioned agent policy admitted through the ordinary workflow registry.</summary>
public sealed record AgentHarnessDescriptor(string Id, string Version, string Description,
    JsonElement InputSchema, IReadOnlyList<string> RequiredCapabilities)
{
    public bool SupportsSteering { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowToolReceiptContextConsumer? ToolReceiptConsumer { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowLocalGenerationConsumer? LocalGenerationConsumer { get; init; }
}

/// <summary>
/// One detached value snapshot. It contains accepted data and prior outcomes, not clients, dispatchers,
/// database handles or authority to execute effects. Mutating the source collections cannot change it.
/// </summary>
public sealed class AgentHarnessSnapshot
{
    public WorkflowRun Run { get; }
    public WorkflowSubmission Submission => Run.Submission;
    public JsonElement State => Run.State;
    public WorkflowSignal Signal => Run.Signal;
    public WorkflowEffect? Effect => Run.Effect;
    public WorkflowQuestionSet? Questions => Run.Questions;
    public int Steps => Run.Steps;
    public int EffectiveStepLimit => Submission.EffectiveStepLimit;
    public int RemainingSteps => Math.Max(0, EffectiveStepLimit - Steps);
    public long ActiveMilliseconds => Run.ActiveMilliseconds;
    public long RemainingActiveMilliseconds => Math.Max(0L, (long)Submission.ActiveTimeLimitSeconds * 1000 - ActiveMilliseconds);
    public IReadOnlyDictionary<string, int> FailureLimits => Submission.FailureLimits;
    public IReadOnlyDictionary<string, int> FailedEffects => Run.FailedEffects;

    public AgentHarnessSnapshot(WorkflowRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Run = run with
        {
            Submission = run.Submission with
            {
                Input = run.Submission.Input.Clone(),
                SettingsSnapshot = run.Submission.SettingsSnapshot.Clone(),
                FailureLimits = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(run.Submission.FailureLimits, StringComparer.Ordinal))
            },
            Steering = Array.AsReadOnly(run.Steering.ToArray()),
            State = run.State.Clone(),
            Signal = run.Signal with { Data = run.Signal.Data.Clone() },
            Effect = run.Effect is { } effect ? effect with
            {
                Arguments = effect.Arguments.Clone(), Result = effect.Result?.Clone()
            } : null,
            Questions = run.Questions is { } questions ? questions with
            {
                Fields = Array.AsReadOnly(questions.Fields.Select(field => field with
                {
                    Choices = field.Choices is { } choices ? Array.AsReadOnly(choices.ToArray()) : null
                }).ToArray()),
                Answers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(questions.Answers, StringComparer.Ordinal))
            } : null,
            Result = run.Result?.Clone(),
            FailedEffects = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(run.FailedEffects, StringComparer.Ordinal)),
            PreparedInterventions = run.PreparedInterventions is { } interventions
                ? Array.AsReadOnly(interventions.ToArray()) : null
        };
    }
}

/// <summary>
/// Pure, bounded policy evaluation. Return a requested workflow action; never call a model/tool,
/// mutate a domain store or start background work here. The host may reevaluate before committing.
/// Native implementations are trusted code; this interface is not a process sandbox.
/// </summary>
public interface IAgentHarness
{
    AgentHarnessDescriptor Descriptor { get; }
    WorkflowStep Advance(AgentHarnessSnapshot snapshot);
}

/// <summary>
/// Optional package export. The host wraps these Contracts-only policies as ordinary workflow
/// definitions, then performs the same package identity, version and capability admission.
/// </summary>
public interface IAgentHarnessModule
{
    IReadOnlyList<IAgentHarness> Harnesses { get; }
}
