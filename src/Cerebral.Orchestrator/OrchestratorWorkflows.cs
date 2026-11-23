using System.Windows;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private WorkflowWorkspace? workflowWorkspace;

    private UIElement WorkflowWorkspacePage()
    {
        workflowWorkspace = new((request, token) => Client.CommandAsync(request.Operation, request.Arguments, request.ProfileId, token),
            () => profiles, (message, error) => SetStatus(message, error));
        Closed += (_, _) => workflowWorkspace.Dispose();
        return workflowWorkspace;
    }
}
