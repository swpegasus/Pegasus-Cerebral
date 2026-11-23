using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

/// <summary>The immutable Develop edition that binds failed plugin finalization to proved repair.</summary>
public sealed class ProgrammingFinalizationProgressHarness : IAgentHarness
{
    public const string Version = "1.3.25";
    private readonly ProgrammingAgentHarness policy = new();

    public AgentHarnessDescriptor Descriptor { get; } = new ProgrammingVerificationRetryHarness(runArguments: true)
        .Descriptor with
        {
            Version = Version,
            Description = "Develop one explicit .NET project with current checks and guarded plugin verification/presentation progress."
        };

    public WorkflowStep Advance(AgentHarnessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Submission.DefinitionId != ProgrammingAgentHarness.Id ||
            snapshot.Submission.DefinitionVersion != Version)
            return new(snapshot.State.Clone(), new CompleteWorkflow(CerebralJson.Element(new
                { summary = "Finalization progress protection requires the exact Develop " + Version + " edition." }), false));
        return policy.Advance(snapshot);
    }
}
