using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static class InstalledWorkerPipeLiveTests
{
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = CerebralClient.ForInstalledWorker();
        if (!client.IsLocalPipe) throw new InvalidOperationException("The installed Worker probe did not select the protected local pipe.");

        var health = await client.GetAsync<HostInfo>("health", cancellationToken: timeout.Token);
        if (health.Product != "Pegasus Cerebral" || health.Protocol != 1 ||
            health.Version.Split('+')[0] != "0.2.18" ||
            !Path.GetFullPath(health.DataDirectory).Equals(
                Path.GetFullPath(@"C:\ProgramData\Pegasus\Cerebral"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The protected pipe responded from the wrong product, protocol, version, or data root.");

        var discovery = await client.CommandAsync("sdk.discover", cancellationToken: timeout.Token);
        if (!discovery.Success) throw new InvalidOperationException("SDK discovery over the protected pipe failed: " + discovery.Error);

        var workflows = discovery.Data.GetProperty("workflows").EnumerateArray().ToArray();
        var chat = workflows.Any(item =>
            item.GetProperty("id").GetString() == ConversationWorkflowContract.DefinitionId &&
            item.GetProperty("version").GetString() == ConversationWorkflowContract.DefinitionVersion);
        if (!chat) throw new InvalidOperationException($"The installed Worker did not advertise the built-in {ConversationWorkflowContract.DefinitionId} workflow version {ConversationWorkflowContract.DefinitionVersion}. Advertised: " +
            string.Join(", ", workflows.Select(item => item.GetProperty("id").GetString() + "@" + item.GetProperty("version").GetString())));

        var metrics = await client.GetAsync<DesktopStatusMetrics>("metrics.status", cancellationToken: timeout.Token);
        if (metrics.SampledAt == default || metrics.ActiveAssignments < 0 || metrics.QueuedAssignments < 0 ||
            metrics.ActiveWorkflows < 0 || metrics.Tokens.ConfirmedInputTokens < 0 || metrics.Tokens.ConfirmedOutputTokens < 0 ||
            metrics.Resources.ProcessMemoryMb <= 0)
            throw new InvalidOperationException("The protected pipe returned invalid bounded desktop status metrics.");

        Console.WriteLine("Installed owner pipe live: health, chat workflow discovery, and desktop status metrics passed for Pegasus Cerebral " + health.Version + ".");
    }
}
