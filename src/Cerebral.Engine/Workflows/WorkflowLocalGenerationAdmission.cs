using Cerebral.Contracts.Workflows;

namespace Cerebral.Engine.Workflows;

/// <summary>Copies the bounded generic declaration; a declaration alone never admits an operation.</summary>
internal static class WorkflowLocalGenerationAdmission
{
    internal static WorkflowLocalGenerationConsumer? Freeze(WorkflowLocalGenerationConsumer? consumer)
    {
        if (consumer is null) return null;
        consumer.Validate();
        return new(consumer.SchemaVersion, consumer.CommandId, consumer.CommandVersion, consumer.Route);
    }
}
