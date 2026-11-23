using System.Collections;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Orchestrator;

internal static class OrchestratorTrayHideNoticeChecks
{
    internal static int Verify()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        IDictionary launch = new Hashtable();
        Check(!OrchestratorTray.ClaimHideNotice(launch, enabled: false, hideCompleted: true) && launch.Count == 0,
            "A fixture or disabled hint must not consume the launch's first close.");
        Check(!OrchestratorTray.ClaimHideNotice(launch, enabled: true, hideCompleted: false) && launch.Count == 0,
            "A blocked or incomplete hide must not consume the launch hint.");
        Check(OrchestratorTray.ClaimHideNotice(launch, enabled: true, hideCompleted: true),
            "The first successful hide in a launch should admit the informational hint.");
        Check(!OrchestratorTray.ClaimHideNotice(launch, enabled: true, hideCompleted: true),
            "A later tray instance in the same launch must stay silent.");
        Check(OrchestratorTray.ClaimHideNotice(new Hashtable(), enabled: true, hideCompleted: true),
            "A new launch should be able to show its first informational hint.");

        var pending = new List<WorkAttentionTarget>();
        var first = new WorkAttentionTarget(1, "owner-a", "main-a", "main-a", "run-0", 1,
            WorkAttentionKind.Completion, WorkflowStatus.Completed);
        Check(!OrchestratorTray.AddPendingWorkAttention(pending, first with { RunId = "" }) && pending.Count == 0,
            "An invalid target must not enter the neutral list.");
        Check(OrchestratorTray.AddPendingWorkAttention(pending, first) && pending.Single() == first,
            "The original exact target must enter the neutral list.");
        Check(!OrchestratorTray.AddPendingWorkAttention(pending, first) && pending.Count == 1,
            "A repeated notice must not displace or duplicate its exact target.");
        for (var i = 1; i <= OrchestratorTray.MaximumPendingWorkAttention; i++)
            OrchestratorTray.AddPendingWorkAttention(pending, first with { RunId = $"run-{i}" });
        Check(pending.Count == OrchestratorTray.MaximumPendingWorkAttention &&
              !pending.Contains(first) && pending[^1].RunId == $"run-{OrchestratorTray.MaximumPendingWorkAttention}",
            "The bounded list must evict the oldest exact target without changing the newest one.");
        var reviewed = pending[0];
        Check(pending.Remove(reviewed) && !pending.Contains(reviewed) && pending.Count == OrchestratorTray.MaximumPendingWorkAttention - 1,
            "Review must remove only the exact selected target.");
        return checks;
    }
}
