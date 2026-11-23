namespace Cerebral.Contracts.Agents;

/// <summary>Optional pure capability for a versioned harness that can consume a prepared-rejection signal.</summary>
public interface IAgentPreparedRejectionSupport
{
    bool CanRejectPrepared(AgentHarnessSnapshot snapshot);
}
