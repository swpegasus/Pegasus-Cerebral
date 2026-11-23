using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Desktop;

public static class UserConnectorProcess
{
    public static UserConnectorManager Manager { get; } = new();
    public static Task<ConnectorStatus> StartAsync(CancellationToken token = default) =>
        Manager.StartAsync(ManagementProcess.WorkerPath, token);
}
