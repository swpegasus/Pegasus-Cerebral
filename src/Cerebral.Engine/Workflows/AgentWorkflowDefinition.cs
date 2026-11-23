using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Engine.Workflows;

/// <summary>
/// Adapts one pure harness to the existing durable workflow pipeline. Registry admission owns name,
/// version and capability validation; the store still owns effect identity, revision and cancellation.
/// </summary>
public sealed class AgentWorkflowDefinition : IWorkflowDefinition, IWorkflowPreparedRejectionSupport
{
    private readonly IAgentHarness harness;
    public WorkflowDefinitionDescriptor Descriptor { get; }

    public AgentWorkflowDefinition(IAgentHarness harness)
    {
        this.harness = harness ?? throw new ArgumentNullException(nameof(harness));
        var descriptor = harness.Descriptor ?? throw new ArgumentException("A harness descriptor is required.", nameof(harness));
        ArgumentNullException.ThrowIfNull(descriptor.RequiredCapabilities);
        WorkflowSchema.ValidateSchema(descriptor.InputSchema);
        var toolReceiptConsumer = descriptor.ToolReceiptConsumer;
        if (toolReceiptConsumer is not null &&
            toolReceiptConsumer.SchemaVersion != WorkflowToolReceiptContextConsumerContract.SchemaVersion)
            throw new ArgumentException("Unsupported tool receipt-context consumer schema.", nameof(harness));
        Descriptor = new(descriptor.Id, descriptor.Version, descriptor.Description, descriptor.InputSchema.Clone(),
            Array.AsReadOnly(descriptor.RequiredCapabilities.ToArray()))
        {
            SupportsSteering = descriptor.SupportsSteering,
            // Preserve absence and explicit false; only the admitted harness can declare support.
            ToolReceiptConsumer = toolReceiptConsumer is null ? null :
                new WorkflowToolReceiptContextConsumer(toolReceiptConsumer.SchemaVersion, toolReceiptConsumer.Supported),
            LocalGenerationConsumer = WorkflowLocalGenerationAdmission.Freeze(descriptor.LocalGenerationConsumer)
        };
    }

    public WorkflowStep Advance(WorkflowRun snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Submission.DefinitionId != Descriptor.Id || snapshot.Submission.DefinitionVersion != Descriptor.Version)
            throw new WorkflowConflictException("The accepted workflow does not match this harness version.");
        if (snapshot.Status != WorkflowStatus.Running || snapshot.CancelRequested ||
            snapshot.Effect?.State is WorkflowEffectState.Prepared or WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown)
            throw new WorkflowConflictException("A harness can advance only a claimed run with no pending or uncertain effect.");
        WorkflowSchema.Validate(snapshot.Submission.Input, Descriptor.InputSchema);
        var step = harness.Advance(new AgentHarnessSnapshot(snapshot))
            ?? throw new InvalidOperationException("The harness returned no transition.");
        WorkflowSchema.Validate(step.State, WorkflowContract.EmptyObject);
        // Detach returned values as well: an implementation can retain its own JsonDocument or lists.
        // The normal registry/store then validate the requested action and commit it atomically.
        return new(step.State.Clone(), step.Next switch
        {
            InvokeWorkflowCommand command => command with { Arguments = command.Arguments.Clone() },
            AskWorkflowQuestions questions => questions with
            {
                Fields = Array.AsReadOnly(questions.Fields.Select(field => field with
                {
                    Choices = field.Choices is { } choices ? Array.AsReadOnly(choices.ToArray()) : null
                }).ToArray())
            },
            CompleteWorkflow complete => complete with { Result = complete.Result.Clone() },
            WaitForWorkflowRuns children => children with { RunIds = Array.AsReadOnly(children.RunIds.ToArray()) },
            _ => throw new InvalidOperationException("The harness returned an unsupported workflow action.")
        });
    }

    public bool CanRejectPrepared(WorkflowRun snapshot) =>
        harness is IAgentPreparedRejectionSupport support &&
        support.CanRejectPrepared(new AgentHarnessSnapshot(snapshot));
}
