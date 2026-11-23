using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

public sealed partial class WorkflowWorkspace
{
    private readonly TextBox workGuidance = Ui.Input(multiline: true);
    private readonly TextBlock workGuidanceStatus = Ui.Text("", 12, Ui.Muted);
    private Button workGuidanceSend = null!;
    private Button workGuidanceClear = null!;
    private GuidanceTarget? workGuidanceTarget;
    private ConversationControlClient? workGuidanceControl;
    private bool? workGuidanceSupported;
    private bool workSelectionPending;
    private bool WorkGuidanceUncertain => workGuidanceControl?.Snapshot.Uncertain == true;

    private FrameworkElement CreateWorkGuidance()
    {
        workGuidance.MinHeight = 62; workGuidance.MaxHeight = 128; workGuidance.MaxLength = 8192;
        AutomationProperties.SetAutomationId(workGuidance, "WorkflowGuidanceInput");
        AutomationProperties.SetName(workGuidance, "Guidance for selected work");
        AutomationProperties.SetHelpText(workGuidance, "A separate Work draft, up to 8 KB of text. Sending does not resume waiting work.");
        workGuidanceSend = Ui.Button("Send guidance", SendWorkGuidance, true);
        workGuidanceClear = Ui.Button("Clear draft", ClearWorkGuidanceDraft);
        AutomationProperties.SetAutomationId(workGuidanceSend, "WorkflowSendGuidance");
        AutomationProperties.SetAutomationId(workGuidanceClear, "WorkflowClearGuidance");
        AutomationProperties.SetAutomationId(workGuidanceStatus, "WorkflowGuidanceStatus");
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("Guidance for selected work", 13));
        panel.Children.Add(workGuidance);
        panel.Children.Add(Ui.Bar(workGuidanceSend, workGuidanceClear));
        panel.Children.Add(workGuidanceStatus);
        workGuidance.TextChanged += (_, _) => UpdateWorkGuidance();
        var host = new Border { Child = panel, BorderBrush = Ui.Accent, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0), Margin = new Thickness(0, 8, 0, 0),
            Visibility = embedded ? Visibility.Collapsed : Visibility.Visible };
        AutomationProperties.SetAutomationId(host, "WorkflowGuidanceHost");
        return host;
    }

    private GuidanceTarget? CurrentGuidanceTarget() => conversation?.Snapshot.Run is { } run
        ? new(run.Id, run.ConversationId, run.DefinitionId, run.DefinitionVersion, connectionEpoch) : null;

    private bool GuidanceSelectionMatches() => !disposed && !embedded && !workSelectionPending &&
        workGuidanceTarget is not null && workGuidanceTarget == CurrentGuidanceTarget() &&
        (recent.SelectedItem is not WorkflowRunListItem item || item.Run.Id == workGuidanceTarget.RunId);

    private void UpdateWorkGuidance()
    {
        if (workGuidanceSend is null) return;
        var selected = CurrentGuidanceTarget();
        // One unsent draft keeps its original target. Selecting other work cannot silently send it there.
        if (!disposed && !embedded && !workSelectionPending && !mutationPending && !WorkGuidanceUncertain &&
            workGuidance.Text.Length == 0 && selected != workGuidanceTarget)
        {
            workGuidanceTarget = selected; workGuidanceSupported = null;
            workGuidanceControl = selected is null ? null : new(selected.ConversationId, BoundCommand());
            workGuidanceStatus.Text = "";
        }
        var matches = GuidanceSelectionMatches();
        var eligible = matches && conversation?.Snapshot.MutationUncertain == false &&
            (WorkGuidanceUncertain || conversation.Snapshot.Run is { IsTerminal: false, CancelRequested: false } run &&
                run.Status != WorkflowStatus.Cancelling);
        workGuidance.IsReadOnly = !matches || mutationPending || WorkGuidanceUncertain;
        workGuidanceSend.Content = WorkGuidanceUncertain ? "Retry guidance" : "Send guidance";
        workGuidanceSend.IsEnabled = eligible && !mutationPending && (WorkGuidanceUncertain || workGuidanceSupported != false) &&
            (WorkGuidanceUncertain || ValidWorkGuidance(workGuidance.Text));
        workGuidanceClear.IsEnabled = !disposed && !mutationPending && !WorkGuidanceUncertain && workGuidance.Text.Length > 0;
        if (workGuidance.Text.Length > 0 && !matches && !mutationPending && !WorkGuidanceUncertain)
            SetWorkGuidanceStatus("Draft kept for the previously selected work. Return to that work or clear this draft.");
        else if (workGuidanceStatus.Text.Length == 0 || workGuidanceStatus.Text.StartsWith("Draft kept for the previously selected work.", StringComparison.Ordinal))
            SetWorkGuidanceStatus(selected is null ? "Open work to send guidance." :
                "Read at the next supported step. Sending guidance does not resume waiting work.");
    }

    private static bool ValidWorkGuidance(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0')) return false;
        try { return new UTF8Encoding(false, true).GetByteCount(text) <= 8192; }
        catch (EncoderFallbackException) { return false; }
    }

    private void SetWorkGuidanceStatus(string text, bool error = false)
    {
        workGuidanceStatus.Text = text;
        workGuidanceStatus.Foreground = error ? Ui.ErrorText : Ui.Muted;
    }

    private void RequireSettledWorkGuidance()
    {
        if (WorkGuidanceUncertain)
            throw new InvalidOperationException("Retry the unconfirmed Work guidance unchanged before changing its target or connection.");
    }

    internal void ClearWorkGuidanceDraft()
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (mutationPending) throw new InvalidOperationException("Wait for the pending request before clearing its draft.");
        RequireSettledWorkGuidance();
        workGuidanceTarget = null; workGuidanceControl = null; workGuidanceSupported = null;
        workGuidanceStatus.Text = ""; workGuidance.Clear(); UpdateWorkGuidance();
    }

    private async void SendWorkGuidance()
    {
        try { await SendWorkGuidanceAsync(); }
        catch (Exception) { /* The awaitable handler reports failures beside its draft; disposal cannot surface a late event exception. */ }
    }

    internal async Task SendWorkGuidanceAsync()
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        var current = conversation;
        var target = workGuidanceTarget;
        var expected = generation;
        var control = workGuidanceControl;
        var text = workGuidance.Text;
        try
        {
            if (!GuidanceSelectionMatches() || current is null || target is null || control is null)
                throw new InvalidOperationException("Open the work that owns this guidance draft before sending it.");
            var retry = control.Snapshot.Uncertain;
            if (!retry && (current.Snapshot.Run is not { IsTerminal: false, CancelRequested: false } run || run.Status == WorkflowStatus.Cancelling))
                throw new InvalidOperationException("This work has ended or is stopping. Guidance was not sent.");
            if (current.Snapshot.MutationUncertain || mutationPending)
                throw new InvalidOperationException("Wait for the pending Work request before sending guidance.");
            if (!ValidWorkGuidance(text))
                throw new ArgumentException("Enter guidance of up to 8 KB. Blank text and invalid characters cannot be sent.");
            if (!retry && workGuidanceSupported == false)
                throw new InvalidOperationException("This workflow advertises that guidance is unsupported.");
            var request = retry ? control.Snapshot.Steering ?? throw new InvalidOperationException("The retained guidance request is missing.") :
                new WorkflowSteeringRequest(Guid.NewGuid().ToString("N"), target.RunId, text);
            if (request.RunId != target.RunId || request.Text != text)
                throw new InvalidOperationException("Retry the exact retained guidance and target unchanged.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await MutateAsync(async () =>
            {
                SetWorkGuidanceStatus(retry ? "Retrying the same guidance request…" : "Checking guidance availability…");
                if (!retry)
                    workGuidanceSupported = await ReadWorkGuidanceCapabilityAsync(target, deadline.Token);
                // The descriptor is reference data. The owner still checks current support and authority on dispatch.
                if (disposed || current != conversation || expected != generation || !GuidanceSelectionMatches())
                    throw new InvalidOperationException("The Work selection changed. Guidance was not sent.");
                if (!retry && workGuidanceSupported == false)
                    throw new InvalidOperationException("This workflow advertises that guidance is unsupported.");
                if (!retry && (current.Snapshot.Run is not { IsTerminal: false, CancelRequested: false } latest || latest.Status == WorkflowStatus.Cancelling))
                    throw new InvalidOperationException("This work has ended or is stopping. Guidance was not sent.");
                // An explicit exact-RID retry only recovers the original receipt, even after work ends.
                var receipt = retry ? (await control.RetryAsync(deadline.Token)).Steering
                    ?? throw new InvalidDataException("The retained guidance receipt is missing.") :
                    await control.SteerAsync(request, deadline.Token);
                if (!disposed && current == conversation && expected == generation)
                {
                    if (workGuidance.Text == text) workGuidance.Clear();
                    SetWorkGuidanceStatus("Guidance accepted for the next supported step. Waiting work remains waiting.");
                }
                return receipt;
            }, guidance: true);
        }
        catch (Exception error)
        {
            if (!disposed)
            {
                SetWorkGuidanceStatus(WorkGuidanceUncertain
                    ? "Delivery is unconfirmed and may already have been accepted. Draft kept. Retry guidance sends the same request; nothing is retried automatically."
                    : "Guidance was not accepted. Draft kept. " + error.Message, error: true);
                UpdateControls();
            }
            throw;
        }
    }

    private async Task<bool?> ReadWorkGuidanceCapabilityAsync(GuidanceTarget target, CancellationToken token)
    {
        // Older hosts may omit this optional flag. Unknown support uses the ordinary owner rejection path.
        var response = await BoundCommand()(new("sdk.describe", CerebralJson.Element(new { id = target.DefinitionId })), token);
        if (!response.Success) return null;
        if (Encoding.UTF8.GetByteCount(response.Data.GetRawText()) > 128 * 1024 ||
            !response.Data.TryGetProperty("definitions", out var definitions) || definitions.ValueKind != JsonValueKind.Array ||
            definitions.GetArrayLength() > 256) throw new InvalidDataException("The workflow description is invalid or too large.");
        var matches = definitions.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("id", out var id) && id.GetString() == target.DefinitionId &&
            item.TryGetProperty("version", out var version) && version.GetString() == target.DefinitionVersion).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("The workflow description repeats the selected definition version.");
        if (matches.Length == 0) return null;
        if (!matches[0].TryGetProperty("supportsSteering", out var supported)) return null;
        return supported.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false,
            _ => throw new InvalidDataException("The workflow guidance capability is invalid.") };
    }

    private sealed record GuidanceTarget(string RunId, string ConversationId, string DefinitionId, string DefinitionVersion, long ConnectionEpoch);
}
