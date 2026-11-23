using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

/// <summary>The immutable Develop edition that joins verified package evidence to one queued owner review.</summary>
public sealed class ProgrammingInterfacePresentationHarness : IAgentHarness
{
    public const string Version = "1.3.26";
    private readonly ProgrammingAgentHarness policy = new();

    public AgentHarnessDescriptor Descriptor { get; } = new ProgrammingVerificationRetryHarness(runArguments: true)
        .Descriptor with
        {
            Version = Version,
            Description = "Develop one explicit .NET project with guarded plugin verification and exact pending owner-review evidence."
        };

    public WorkflowStep Advance(AgentHarnessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Submission.DefinitionId != ProgrammingAgentHarness.Id ||
            snapshot.Submission.DefinitionVersion != Version)
            return new(snapshot.State.Clone(), new CompleteWorkflow(CerebralJson.Element(new
                { summary = "Interface presentation protection requires the exact Develop " + Version + " edition." }), false));
        return policy.Advance(snapshot);
    }
}
