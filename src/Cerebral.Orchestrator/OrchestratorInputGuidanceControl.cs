using System.Runtime.CompilerServices;
using System.Text.Json;
using Cerebral.Desktop;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private static readonly JsonSerializerOptions InputGuidanceObservationJson = new(Cerebral.Core.CerebralJson.Options) { WriteIndented = false };
    private sealed class InputGuidanceIdentity { internal string Id { get; } = Guid.NewGuid().ToString("N"); }
    private static readonly ConditionalWeakTable<FrameworkElement, InputGuidanceIdentity> InputGuidanceIdentities = new();
    private FrameworkElement RequireInputGuidance(string? expectedId = null)
    {
        if (desktopInputGuidanceHost.Visibility != Visibility.Visible ||
            desktopInputGuidanceHost.Child is not ScrollViewer { Content: FrameworkElement view })
            throw new InvalidOperationException("There is no current input-guidance review.");
        var id = InputGuidanceIdentities.GetValue(view, _ => new()).Id;
        if (expectedId is not null && expectedId != id)
            throw new InvalidOperationException("The exact inspected input-guidance review changed. Inspect again.");
        return view;
    }

    internal DesktopControlResponse HandleInputGuidanceLayoutObservation(DesktopControlRequest request,
        CancellationToken cancellationToken)
    {
        Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        Lifetime.Token.ThrowIfCancellationRequested();
        EnsureNoExtraArguments(request.Arguments, ["id"]);
        var owner = desktopPluginOwner ?? throw new InvalidOperationException("The input-guidance owner is unavailable.");
        if (!owner.ObserveMatchesOwner())
            throw new InvalidOperationException("The input-guidance owner changed. Inspect the current view after its owner is restored.");
        var expectedId = request.Arguments.TryGetProperty("id", out _) ? RequiredString(request.Arguments, "id", 32) : null;
        var view = RequireInputGuidance(expectedId);
        var id = InputGuidanceIdentities.GetValue(view, _ => new()).Id;
        // No render scope, layout/dispatcher realization, template application or control
        // mutation belongs in this branch. Capture only the tree's already-current state.
        var observation = InputGuidanceLayoutObservation.Capture(view, this);
        cancellationToken.ThrowIfCancellationRequested();
        Lifetime.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(desktopPluginOwner, owner) || !owner.ObserveMatchesOwner() ||
            !ReferenceEquals(view, RequireInputGuidance(id)))
            throw new InvalidOperationException("The input-guidance view or owner changed during observation. Inspect again.");
        // Preserve the snapshot's compact byte-count contract when embedding it in the response.
        return new(request.RequestId, request.Operation, "completed", JsonSerializer.SerializeToElement(
            new { id, ownerBound = true, ownerCurrent = true, observation }, InputGuidanceObservationJson));
    }

    private DesktopControlResponse HandleInputGuidanceControl(DesktopControlRequest request)
    {
        EnsureNoExtraArguments(request.Arguments, request.Operation switch
        {
            "input-guidance.invoke" => ["id", "name"],
            "input-guidance.setText" => ["id", "fieldId", "expectedText", "text"],
            _ => []
        });
        using var scope = DesktopBackgroundRenderScope.Begin(this);
        UpdateLayout();
        var view = RequireInputGuidance(request.Operation == "input-guidance.inspect" ? null : RequiredString(request.Arguments,"id",32));
        var id = InputGuidanceIdentities.GetValue(view, _ => new()).Id;
        if (request.Operation == "input-guidance.invoke")
        {
            var button = FindPluginControlButton(view, RequiredString(request.Arguments,"name",256));
            if (!button.IsEnabled || !button.IsVisible)
                throw new InvalidOperationException("The reviewed action is unavailable.");
            var provider = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider ??
                throw new InvalidOperationException("The reviewed action has no native invocation pattern.");
            // Delivery only: asynchronous model work or draft replacement has its own visible receipt.
            provider.Invoke();
            return DesktopControlProtocol.Result(request,"accepted",new{id,conversationId=CurrentConversationId,message="Review action delivered. Inspect its result before continuing."});
        }
        if (request.Operation == "input-guidance.setText")
        {
            var input = Cerebral.Core.PluginControlTextEdits.Copy("setText",
                RequiredString(request.Arguments,"fieldId",32),
                RequiredString(request.Arguments,"expectedText",Cerebral.Core.PluginControlTextEdits.MaximumCharacters,true),
                RequiredString(request.Arguments,"text",Cerebral.Core.PluginControlTextEdits.MaximumCharacters,true)) ?? throw new InvalidOperationException("The review text edit is incomplete.");
            var edit = PreparePluginControlTextEdit(view,input);
            edit.Provider.SetValue(input.Text);
            return DesktopControlProtocol.Result(request,edit.Field.Text == input.Text ? "completed" : "failed",
                new{id,fieldId=input.FieldId,characters=edit.Field.Text.Length});
        }
        return DesktopControlProtocol.Result(request,"completed",new
        {
            id,conversationId=CurrentConversationId,
            normalComposerVisible=conversationComposer?.Visibility==Visibility.Visible,
            controls=PluginControlSnapshot(id,view)
        });
    }
}
