using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly DesktopInterfaceOutcomeJournal interfaceOutcomeJournal =
        new(Path.Combine(DesktopInstancePaths.Current.UserDataDirectory, "InterfaceOutcomes"));
    private string? interfaceOutcomeServiceKey;
    private readonly HashSet<string> interfaceOutcomeDeliveries = new(StringComparer.Ordinal);

    private HashSet<string> AddInterfaceOutcomeRecoveryCards(IReadOnlyList<InterfaceRequest> requests,
        string sessionId)
    {
        var retainedIds = new HashSet<string>(StringComparer.Ordinal);
        if (!ServiceIsLocal || interfaceOutcomeServiceKey is not { } serviceKey) return retainedIds;
        foreach (var entry in interfaceOutcomeJournal.List(serviceKey, sessionId))
        {
            var current = requests.SingleOrDefault(request => request.Id == entry.Id);
            if (entry.Completion is not null && current?.Status is "completed" or "failed")
            {
                try { interfaceOutcomeJournal.Confirm(entry, current); continue; }
                catch (Exception error) { SetStatus("Recorded plugin outcome needs inspection: " + error.Message, true); }
            }
            retainedIds.Add(entry.Id); // Do not expose another install action for an uncertain local attempt.
            var card = new StackPanel();
            card.Children.Add(Ui.Text(entry.Completion is null ? "Unconfirmed plugin review" : "Plugin outcome confirmation", 16));
            card.Children.Add(Ui.Text("Chat " + entry.SessionId + " · request " + entry.Id, 11, Ui.Muted));
            card.Children.Add(Ui.Text(entry.Completion is null
                ? "This desktop recorded the attempt, but no classified native outcome was retained. Inspect the existing request and plugin; this attempt will not be executed again."
                : "The desktop retained its " + (entry.Completion.Success ? "successful" : "failed") +
                    " outcome. Confirming it reads the original request and delivers only its receipt.", 12));
            if (entry.Completion is not null)
            {
                var binding = PluginBinding();
                var connection = ServiceConnectionIdentity;
                var connectionVersion = knowledgeConnectionVersion;
                var button = Ui.Button("Confirm recorded outcome", () => Run(() =>
                    RecoverInterfaceOutcomeAsync(entry, binding, connection, connectionVersion)), true);
                AutomationProperties.SetAutomationId(button, "ConfirmOutcome-" + entry.Id);
                card.Children.Add(button);
            }
            var expander = new Expander
            {
                Header = "Plugin review · confirmation pending", IsExpanded = true,
                Content = new Border { Child = card, Padding = new Thickness(12), BorderThickness = new Thickness(1),
                    BorderBrush = Ui.Line, Background = Ui.Surface, CornerRadius = new CornerRadius(5) },
                Margin = new Thickness(0, 3, 0, 6)
            };
            AutomationProperties.SetAutomationId(expander, "InterfaceOutcome-" + entry.Id);
            interfaceRequests.Children.Add(expander);
        }
        return retainedIds;
    }

    private async Task RecoverInterfaceOutcomeAsync(DesktopInterfaceOutcome entry,
        Cerebral.Desktop.Plugins.DesktopConversationBinding binding, string connection, long connectionVersion)
    {
        void RequireOwner()
        {
            if (!IsConnected || !ServiceIsLocal || CurrentConversationId != entry.SessionId ||
                interfaceOutcomeServiceKey != entry.ServiceKey || PluginBinding() != binding ||
                ServiceConnectionIdentity != connection || knowledgeConnectionVersion != connectionVersion)
                throw new InvalidOperationException("Reopen the original local chat and connection before confirming its recorded outcome.");
        }
        RequireOwner();
        if (!interfaceOutcomeDeliveries.Add(entry.Id))
            throw new InvalidOperationException("This exact outcome is already being confirmed; no second delivery was started.");
        try
        {
            using var ownerClient = CreateIndependentClient();
            await DesktopInterfaceOutcomeDelivery.RecoverAsync(interfaceOutcomeJournal, entry,
                async (id, ct) =>
                {
                    RequireOwner();
                    var request = await ownerClient.GetAsync<InterfaceRequest>("interface.get",
                        new { id, sessionId = entry.SessionId }, cancellationToken: ct);
                    RequireOwner();
                    return request;
                },
                async (completion, ct) =>
                {
                    RequireOwner();
                    return await ownerClient.GetAsync<InterfaceRequest>("interface.complete",
                        completion.CommandArguments(), cancellationToken: ct);
                }, Lifetime.Token);
            if (CurrentConversationId == entry.SessionId)
            {
                SetStatus("The recorded plugin outcome is confirmed. Installation and controls were not repeated.");
                await RefreshInterfaceRequests();
            }
        }
        catch (Exception error)
        {
            SetStatus("Plugin outcome confirmation is still pending: " + error.Message, true);
            throw;
        }
        finally { interfaceOutcomeDeliveries.Remove(entry.Id); }
    }
}
