using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

/// <summary>Develop edition with a guarded correction for repeated missing restore assets.</summary>
public sealed class ProgrammingPrerequisiteProgressHarness : IAgentHarness
{
    public const string Version = "1.3.27";
    private readonly ProgrammingAgentHarness policy = new();

    public AgentHarnessDescriptor Descriptor { get; } = new ProgrammingVerificationRetryHarness(runArguments: true)
        .Descriptor with
        {
            Version = Version,
            Description = "Develop one explicit .NET project with guarded plugin verification, exact pending owner-review evidence, and missing-assets build progress."
        };

    public WorkflowStep Advance(AgentHarnessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Submission.DefinitionId != ProgrammingAgentHarness.Id ||
            snapshot.Submission.DefinitionVersion != Version)
            return new(snapshot.State.Clone(), new CompleteWorkflow(CerebralJson.Element(new
                { summary = "Prerequisite progress protection requires the exact Develop " + Version + " edition." }), false));
        return policy.Advance(snapshot);
    }
}
