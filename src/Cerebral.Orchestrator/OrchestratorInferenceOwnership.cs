using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly InferenceOwnershipPollState inferenceOwnershipPoll = new();
    private readonly Stopwatch inferenceOwnershipClock = Stopwatch.StartNew();
    private readonly TextBlock inferenceOwnershipStatusText = Ui.Text("Slot occupants unobserved", 11, Ui.Muted);
    private CancellationTokenSource? inferenceOwnershipRequest;
    private bool inferenceOwnershipVisibilityInitialized;

    private void InitializeInferenceOwnershipVisibility()
    {
        if (inferenceOwnershipVisibilityInitialized) return;
        inferenceOwnershipVisibilityInitialized = true;
        IsVisibleChanged += (_, _) => RefreshInferenceOwnershipVisibility();
        StateChanged += (_, _) => RefreshInferenceOwnershipVisibility();
        Closed += (_, _) =>
        {
            inferenceOwnershipRequest?.Cancel();
            inferenceOwnershipPoll.Dispose();
        };
        if (systemStatusBar is not null)
            systemStatusBar.IsVisibleChanged += (_, _) => RefreshInferenceOwnershipVisibility();
    }

    private void RefreshInferenceOwnershipVisibility()
    {
        inferenceOwnershipPoll.SetVisibility(IsLoaded && IsVisible,
            WindowState == WindowState.Minimized, SystemStatusVisible && systemStatusBar?.IsVisible == true);
        if (!inferenceOwnershipPoll.Eligible) inferenceOwnershipRequest?.Cancel();
    }

    private void ResetSystemInferenceOwnership(CerebralClient? client = null, string? installationId = null)
    {
        inferenceOwnershipRequest?.Cancel();
        if (client is null || installationId is null)
            inferenceOwnershipPoll.Unbind();
        else
            inferenceOwnershipPoll.Bind(client, installationId);
        ApplyInferenceOwnership(InferenceOwnershipPresentation.Unavailable(
            "Local model-work observation has not been read on this connection."));
        RefreshInferenceOwnershipVisibility();
    }

    private void ApplyInferenceOwnership(InferenceOwnershipPresentation presentation)
    {
        inferenceOwnershipStatusText.Text = presentation.Summary;
        inferenceOwnershipStatusText.ToolTip = presentation.Details;
    }

    private async Task RefreshSystemInferenceOwnershipAsync()
    {
        RefreshInferenceOwnershipVisibility();
        if (!inferenceOwnershipPoll.Eligible || Lifetime.IsCancellationRequested) return;
        if (!IsConnected)
        {
            ApplyInferenceOwnership(InferenceOwnershipPresentation.Unavailable("The Worker connection is offline."));
            return;
        }
        var client = Client;
        var installationId = systemWorkMetricsInstallationId;
        if (installationId is null || !SystemWorkMetricsBindingCurrent(client, installationId)) return;
        if (inferenceOwnershipPoll.Unsupported)
        {
            ApplyInferenceOwnership(InferenceOwnershipPresentation.Unavailable(
                "This Worker does not support local model-work observation."));
            return;
        }
        if (!inferenceOwnershipPoll.TryBegin(client, installationId, inferenceOwnershipClock.Elapsed, out var ticket))
            return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        inferenceOwnershipRequest = deadline;
        try
        {
            // Optional owner-local observation only. This cannot activate, load or prepare a model.
            var wire = await client.GetAsync<JsonElement>("metrics.inference.status", new { },
                cancellationToken: deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            var sample = ReadStatusInferenceOwnershipSample(wire, installationId);
            RefreshInferenceOwnershipVisibility();
            deadline.Token.ThrowIfCancellationRequested();
            var accepted = inferenceOwnershipPoll.Complete(ticket!, InferenceOwnershipReadOutcome.Sample,
                sample.ObserverGenerationId, sample.Revision);
            if (SystemWorkMetricsBindingCurrent(client, installationId) && inferenceOwnershipPoll.Eligible)
                ApplyInferenceOwnership(accepted ? sample.Presentation : InferenceOwnershipPresentation.Unavailable(
                    "The local model-work sample was superseded or could not be accepted."));
        }
        catch (InvalidOperationException error) when (error.Message == "Unknown command: metrics.inference.status")
        {
            RefreshInferenceOwnershipVisibility();
            if (deadline.IsCancellationRequested) return;
            if (inferenceOwnershipPoll.Complete(ticket!, InferenceOwnershipReadOutcome.Unsupported) &&
                SystemWorkMetricsBindingCurrent(client, installationId))
                ApplyInferenceOwnership(InferenceOwnershipPresentation.Unavailable(
                    "This Worker does not support local model-work observation."));
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested)
        {
            inferenceOwnershipPoll.Complete(ticket!, InferenceOwnershipReadOutcome.Unavailable);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            RefreshInferenceOwnershipVisibility();
            if (inferenceOwnershipPoll.Complete(ticket!, InferenceOwnershipReadOutcome.Unavailable) &&
                SystemWorkMetricsBindingCurrent(client, installationId))
                ApplyInferenceOwnership(InferenceOwnershipPresentation.Unavailable(
                    "Local model-work observation is temporarily unavailable."));
        }
        finally
        {
            // Also retire a request whose parse/cancellation failed before an adoption decision.
            inferenceOwnershipPoll.Complete(ticket!, InferenceOwnershipReadOutcome.Unavailable);
            if (ReferenceEquals(inferenceOwnershipRequest, deadline)) inferenceOwnershipRequest = null;
        }
    }

    private async Task NotifySystemInferenceOwnershipAsync(ServiceEvent item)
    {
        if (item.Type != "inference.observation.changed") return;
        var eventClient = Client;
        var eventInstallationId = systemWorkMetricsInstallationId;
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => Invalidate());
            return;
        }
        Invalidate();

        void Invalidate()
        {
            var installationId = systemWorkMetricsInstallationId;
            var generation = inferenceOwnershipPoll.ObserverGenerationId;
            if (Lifetime.IsCancellationRequested || installationId is null || generation is null ||
                eventInstallationId != installationId || !ReferenceEquals(eventClient, Client) ||
                !SystemWorkMetricsBindingCurrent(eventClient, installationId)) return;
            if (InferenceOwnershipProjection.TryReadInvalidation(item.Data, installationId, generation,
                    inferenceOwnershipPoll.Revision, out var revision))
                inferenceOwnershipPoll.Notify(generation, revision);
            // Invalidation has no counters and starts no request. The visible timer supplies
            // a full read with the same spacing and provides fallback when events are lost.
        }
    }
}
