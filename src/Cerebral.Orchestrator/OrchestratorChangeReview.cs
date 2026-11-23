using System.IO;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private sealed record PluginChangeCorrection(
        string RequestId, string SessionId, string ManifestPath, string? PackageFingerprint,
        string PluginId, string PluginName, string PluginVersion,
        DesktopDraftSnapshot Draft, string ConnectionIdentity, int ConnectionVersion);

    private sealed record PendingPluginSourceReviewState(bool CanInstall, string SourceLine, string Explanation);

    private async Task<WorkflowRun?> ReadPendingPluginSourceRunAsync(InterfaceRequest request)
    {
        try { return await Get<WorkflowRun>("jobs.workflow.get", new { id = request.JobId }); }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    private async Task<bool> VerifyPendingPluginSourceForClickAsync(InterfaceRequest request,
        InterfaceReviewStateIdentity? expectedReviewIdentity, bool allowVerifiedWork = false)
    {
        var sourceRun = await ReadPendingPluginSourceRunAsync(request);
        if (expectedReviewIdentity is not null) RequireCurrentReview(expectedReviewIdentity, request);
        var sourceReview = PendingPluginSourceReview(request, sourceRun, ConversationProjectId, allowVerifiedWork);
        if (sourceReview.CanInstall) return true;
        SetStatus(sourceReview.Explanation, true);
        await RefreshInterfaceRequests();
        return false;
    }

    private static PendingPluginSourceReviewState PendingPluginSourceReview(InterfaceRequest request,
        WorkflowRun? sourceRun, string? selectedProjectId, bool allowVerifiedWork = false)
    {
        static string ShortId(string value) => value[..Math.Min(value.Length, 8)];
        var status = sourceRun?.Status.ToString().ToLowerInvariant() ?? "unavailable";
        var sourceLine = $"Request {ShortId(request.Id)} · source run {ShortId(request.JobId)}: {status}.";
        var blocker = SemanticInterfaceOriginBlocker(request, sourceRun, selectedProjectId, allowVerifiedWork);
        if (blocker is null)
            return new(true, sourceLine, "Awaiting your review. Installation has not been approved.");
        var explanation = sourceRun?.Status switch
        {
            WorkflowStatus.Failed => "This source run failed, so this earlier request cannot be installed. Use a separate eligible review in this chat, or submit a new request from a new run.",
            WorkflowStatus.Cancelled or WorkflowStatus.Cancelling => "This source run was cancelled, so this request cannot be installed. Submit a new request from a new run.",
            _ when sourceRun is null => "Cerebral cannot verify this request's source run. Refresh this review after reconnecting; installation is unavailable until its source is verified.",
            _ => "This request's source run no longer matches the selected chat or is otherwise ineligible. Open the original chat and review a current eligible request."
        };
        return new(false, sourceLine, explanation);
    }

    private PluginChangeCorrection CapturePluginChangeCorrection(InterfaceRequest request,
        DesktopPluginManifest manifest, DesktopConversationBinding binding, string connection, int connectionVersion) =>
        new(request.Id, request.SessionId, request.Path, request.PackageFingerprint,
            manifest.Id, manifest.Name, manifest.Version,
            new(binding, pluginDraftRevision, prompt.Text), connection, connectionVersion);

    private static StackPanel PendingPluginChangeDetails(InterfaceRequest request, DesktopPluginManifest manifest,
        DesktopPluginInfo? installed, string libraryRoot, string dataRoot, bool installAvailable = true)
    {
        var details = new StackPanel();
        details.Children.Add(Ui.Text($"{manifest.Name} {manifest.Version} · {manifest.Id}", 15));
        if (!string.IsNullOrWhiteSpace(manifest.Description))
            details.Children.Add(Ui.Text(manifest.Description, 12));
        if (installAvailable)
        {
            var operation = installed is null ? "Install and enable"
                : installed.Version == manifest.Version ? "Refresh reviewed build and enable"
                : "Update package and enable";
            details.Children.Add(Ui.Text("Proposed action: " + operation, 13));
        }
        if (installed is not null)
            details.Children.Add(Ui.Text($"Currently installed: {installed.Version} · {(installed.Enabled ? "enabled" : "disabled")}", 12, Ui.Muted));
        details.Children.Add(Ui.Text("Reviewed manifest: " + request.Path, 12));
        if (request.GeneratedSourceRoot is { Length: > 0 } source)
            details.Children.Add(Ui.Text("Authoring source: " + source, 12, Ui.Muted));
        details.Children.Add(Ui.Text("Plugin library: " + libraryRoot, 12));
        details.Children.Add(Ui.Text("Plugin data: " + Path.Combine(dataRoot, manifest.Id), 12));
        if (installAvailable)
            details.Children.Add(Ui.Text(
                "Approval copies this reviewed package into the local desktop plugin library, enables it, and loads its supported contributions. " +
                "Package installation preserves the authoring source and existing plugin data.", 12));
        if (!string.IsNullOrWhiteSpace(manifest.ExclusiveGroup))
            details.Children.Add(Ui.Text(
                "Provider group: " + manifest.ExclusiveGroup +
                ". Enabling this plugin disables other enabled providers in that group.", 12));
        details.Children.Add(Ui.Text("Scope: this local desktop plugin package. Native code runs with your Windows user permissions.", 12));
        details.Children.Add(Ui.Text(DesktopPluginCatalog.NativeTrustNotice, 12, Ui.Muted));
        if (request.PackageFingerprint is { Length: > 0 } fingerprint)
            details.Children.Add(Ui.Text("Reviewed package fingerprint: " + fingerprint, 11, Ui.Muted));
        if (installed?.Error is { Length: > 0 } error)
            details.Children.Add(Ui.Text("Installed package needs attention: " + error, 12, Ui.ErrorText));
        details.Children.Add(Ui.Text(installAvailable
            ? "Correct request adds editable instructions to the composer. The package stays pending until you explicitly approve it."
            : "Prepare a new request adds editable instructions to the composer for a separate run. It cannot approve or retry this earlier request.",
            12, Ui.Muted));
        return details;
    }

    private static bool PluginChangeCorrectionIsCurrent(PluginChangeCorrection capture, InterfaceRequest request,
        bool serviceIsLocal, DesktopConversationBinding binding, string connection, int connectionVersion) =>
        serviceIsLocal && request.Kind == "plugin" && request.Status == "pending" &&
        request.Id == capture.RequestId && request.SessionId == capture.SessionId &&
        request.Path == capture.ManifestPath && request.PackageFingerprint == capture.PackageFingerprint &&
        binding == capture.Draft.Binding && binding.SessionId == capture.SessionId &&
        connection == capture.ConnectionIdentity && connectionVersion == capture.ConnectionVersion;

    private static Task<DesktopOperationResult> PlacePluginChangeCorrectionAsync(PluginChangeCorrection capture,
        InterfaceRequest request, DesktopPluginHostAdapter host, bool serviceIsLocal,
        DesktopConversationBinding binding, string connection, int connectionVersion, CancellationToken cancellationToken,
        bool newRequest = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PluginChangeCorrectionIsCurrent(capture, request, serviceIsLocal, binding, connection, connectionVersion))
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected,
                "This review belongs to a changed connection, conversation, or package request. Refresh its original review before correcting it."));

        var correction = newRequest
            ? $"Prepare a new plugin request for {capture.PluginName} ({capture.PluginId}, {capture.PluginVersion}).\n" +
              $"Earlier request {capture.RequestId} cannot be installed from this review. Do not retry, approve, or install that request.\n" +
              $"Reviewed manifest for context: {capture.ManifestPath}\n" +
              "Create a distinct request from a new run after making these changes:\n"
            : $"Correction for the proposed plugin {capture.PluginName} ({capture.PluginId}, {capture.PluginVersion}).\n" +
              $"Reviewed manifest: {capture.ManifestPath}\n" +
              "Keep installation pending. Change the proposal as follows:\n";
        // Preserve the exact captured draft, including whitespace. The existing host performs the
        // binding/revision comparison immediately before replacing it; a later edit is never overwritten.
        var text = capture.Draft.Text + (capture.Draft.Text.Length == 0 ? "" : "\n\n") + correction;
        if (text.Length > DesktopPluginContract.MaxTextChars)
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected,
                "Your draft has no room for this correction. Keep it intact and shorten it before trying again."));
        return host.ReplaceDraftAsync(new(capture.Draft.Binding, capture.Draft.Revision, text), cancellationToken);
    }

    private async Task<DesktopOperationResult> CorrectPluginChangeRequestAsync(InterfaceRequest request,
        PluginChangeCorrection capture, bool newRequest = false)
    {
        Dispatcher.VerifyAccess();
        if (desktopPluginHost is null)
            throw new InvalidOperationException("The current conversation composer is unavailable.");
        var result = await PlacePluginChangeCorrectionAsync(capture, request, desktopPluginHost, ServiceIsLocal,
            PluginBinding(), ServiceConnectionIdentity, knowledgeConnectionVersion, Lifetime.Token, newRequest);
        if (result.State != DesktopOperationState.Completed)
        {
            SetStatus(result.Message ?? "The correction was not placed in the composer.", true);
            return result;
        }
        SelectPage("chat");
        prompt.CaretIndex = prompt.Text.Length;
        prompt.Focus();
        SetStatus(newRequest
            ? "New-request draft added for editing. Nothing was submitted or installed; the earlier request remains pending and cannot be installed from this review."
            : "Correction added to the composer for editing. Nothing was submitted; plugin review remains pending.");
        return result;
    }
}
