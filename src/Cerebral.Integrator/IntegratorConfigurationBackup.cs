using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;
using Microsoft.Win32;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private async Task BackupConfiguration()
    {
        var client = CaptureSettingsPresetClient();
        var availability = await client.DescribeCompleteSettingsAsync(Lifetime.Token);
        var purpose = ChooseSettingsExportPurpose(availability);
        if (purpose is null) return;
        var backup = await client.ExportBackupAsync(purpose, Lifetime.Token);
        if (!ReviewConfiguration("Review " + SettingsExportReview.Label(purpose).ToLowerInvariant(), backup.Attention,
                SettingsExportReview.Description(backup), PresetCoreReviewText(backup.Document), "Save backup…")) return;
        if (!client.IsCurrent) throw new InvalidOperationException("The connection changed. Open Backup again.");
        var picker = new SaveFileDialog
        {
            Title = purpose == SettingsTransferPurpose.Complete ? "Save complete settings" : "Save Cerebral configuration", Filter = "Cerebral configuration (*.json)|*.json",
            FileName = purpose == SettingsTransferPurpose.Complete ? "Pegasus-Cerebral-complete-settings.json" : "Pegasus-Cerebral-configuration.json", AddExtension = true, OverwritePrompt = true
        };
        if (picker.ShowDialog(this) != true) return;
        if (!client.IsCurrent) throw new InvalidOperationException("The connection changed. Open Backup again.");
        // Selection and any overwrite confirmation belong to the signed-in user. The service
        // never acquires a broad file-write grant merely to produce a portable settings document.
        await SaveConfigurationDocumentAsync(backup.Document, picker.FileName);
        SetStatus(SettingsExportReview.Label(purpose) + " saved to " + Path.GetFullPath(picker.FileName));
    }

    private sealed record PendingRestore(ConfigurationRestoreClient Operation, CerebralClient Client, long Connection, CerebralClient OwnerClient);
    private PendingRestore? pendingRestore;
    private bool restoreCloseHandlerAdded;

    private async Task RestoreConfiguration()
    {
        if (pendingRestore?.Operation.Snapshot.Uncertain == true)
        { await CheckLastRestore(); return; }
        var connection = queueConnectionEpoch;
        var ownerClient = Client;
        var client = CreateIndependentClient();
        var retained = false;
        try
        {
        void RequireConnection()
        {
            if (connection != queueConnectionEpoch || !IsConnected || !ReferenceEquals(ownerClient, Client))
                throw new InvalidOperationException("The service connection changed. Open Restore again for the intended service; this review was not applied.");
        }
        var picker = new OpenFileDialog { Title = "Restore Cerebral configuration", Filter = "Cerebral configuration (*.json)|*.json", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        RequireConnection();
        await using var file = new FileStream(picker.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length > ConfigurationBackupContract.MaximumUtf8Bytes) throw new InvalidDataException("The configuration backup exceeds the supported size.");
        var bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, Lifetime.Token);
        RequireConnection();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        // This parse supplies labels only. Send the original JSON to the service so unknown fields
        // cannot disappear through a permissive desktop deserialization before strict admission.
        var typed = CerebralJson.Read<ConfigurationBackupDocument>(document.RootElement);
        if (typed.Paths is null) throw new InvalidDataException("The backup has no configuration paths.");
        var paths = new FieldsDialog(this, "Restore locations", "Update references for this computer. Restoring settings does not move files or create these folders.")
            .Add("models", "Model repository", typed.Paths.ModelDirectory)
            .Add("projects", "Projects", typed.Paths.ProjectsDirectory)
            .Add("shared", "Shared files", typed.Paths.SharedDirectory)
            .Add("tools", "Portable tools", string.IsNullOrWhiteSpace(typed.Paths.ToolsDirectory) ? settings.ToolsDirectory : typed.Paths.ToolsDirectory);
        var componentFields = (typed.Models ?? []).Where(x => x.VisionProjectorPath is not null).Take(128).ToArray();
        for (var index = 0; index < componentFields.Length; index++)
            paths.Add("component-" + index, componentFields[index].Name + " — image projector (blank follows repository mapping)", "");
        if (paths.ShowDialog() != true) return;
        RequireConnection();
        var componentPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < componentFields.Length; index++)
            if (paths.Value("component-" + index) is { Length: > 0 } componentPath)
                componentPaths.Add(componentFields[index].Id, componentPath);
        var mapping = new ConfigurationRestorePathOverrides(paths.Value("models"), paths.Value("projects"), paths.Value("shared"), paths.Value("tools"),
            ModelProjectorFiles: componentPaths.Count == 0 ? null : componentPaths);
        var preview = await client.GetAsync<ConfigurationRestorePreview>("configuration.restore.preview", new { document = document.RootElement, pathOverrides = mapping }, cancellationToken: Lifetime.Token);
        RequireConnection();
        var description = $"Add {preview.AddedAgents.Count} agents and {preview.AddedModels.Count} models; update {preview.UpdatedAgents.Count} agents and {preview.UpdatedModels.Count} models. " +
            $"Restore {(preview.UpdatedPromptRoles?.Count ?? 0)} shared or role prompts. Other entries remain. Finish active work and unload models before applying.\n\n" +
            $"Models: {mapping.ModelDirectory}\nProjects: {mapping.ProjectsDirectory}\nShared files: {mapping.SharedDirectory}\nPortable tools: {mapping.ToolsDirectory}";
        if (!ReviewConfiguration("Review configuration restore", preview.Attention, description,
                PresetCoreReviewText(typed, mapping), "Apply restore")) return;
        RequireConnection();
        retained = true; // The common admission helper owns this independent client from here.
        await ApplyReviewedConfigurationRestoreAsync(preview, client, connection, ownerClient);
        }
        finally { if (!retained) client.Dispose(); }
    }

    private async Task ApplyReviewedConfigurationRestoreAsync(ConfigurationRestorePreview preview, CerebralClient client,
        long connection, CerebralClient ownerClient)
    {
        var retained = false;
        try
        {
            if (!IsConnected || connection != queueConnectionEpoch || !ReferenceEquals(ownerClient, Client))
                throw new InvalidOperationException("The service connection changed. This restore review was not applied.");
            if (pendingRestore?.Operation.Snapshot.Uncertain == true)
                throw new InvalidOperationException("Check the last restore on its original service before applying another restore.");
            var operation = new ConfigurationRestoreClient(
                new ConfigurationRestoreApplyRequest(preview.PreviewId, preview.ExpectedHostRevision, ReviewedFreeText: true), client);
            pendingRestore?.Client.Dispose();
            var pending = new PendingRestore(operation, client, connection, ownerClient);
            pendingRestore = pending; retained = true;
            if (!restoreCloseHandlerAdded)
            {
                restoreCloseHandlerAdded = true;
                Closed += (_, _) => pendingRestore?.Client.Dispose();
            }
            using var applyTimeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            applyTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await operation.ApplyOnceAsync(applyTimeout.Token); }
            catch (ConfigurationRestoreRejectedException)
            {
                if (!operation.Snapshot.Uncertain)
                { pendingRestore = null; retained = false; throw; }
            }
            catch (Exception) when (operation.Snapshot.Uncertain) { }
            // Configuration files and named presets share the same one-apply receipt/reconciliation path.
            await ReconcileRestore(pending);
        }
        finally { if (!retained) client.Dispose(); }
    }

    private async Task CheckLastRestore()
    {
        if (pendingRestore is not { } pending)
        {
            SetStatus("No restore was submitted in this window. Retained receipts can be queried by preview ID through configuration.restore.status.");
            return;
        }
        await ReconcileRestore(pending);
    }

    private async Task ReconcileRestore(PendingRestore pending)
    {
        string? statusFailure = null;
        ConfigurationRestoreStatus? status = null;
        using var statusTimeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        statusTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { status = await pending.Operation.RefreshAsync(statusTimeout.Token); }
        catch (Exception error) { statusFailure = error.Message; }
        var snapshot = pending.Operation.Snapshot;
        if (snapshot.Result is null)
        {
            var retention = status is null ? "" : $" Receipts retain up to {status.MaximumRetainedReceipts} operations and {status.RetentionDays} days; absence does not prove nonexecution.";
            SetStatus($"Restore outcome is unconfirmed for {snapshot.Request.PreviewId}. Check last restore on the original service before starting another restore." +
                retention + (statusFailure is null ? "" : " Status could not be read: " + statusFailure));
            return;
        }
        var result = snapshot.Result;
        var notification = snapshot.Receipt?.NotificationOutcome;
        var warning = notification == "failed" ? " Settings committed, but the service refresh notification failed." :
            notification == "pending" || statusFailure is not null ? " Settings committed; refresh notification status is unconfirmed." : "";
        if (pending.Connection != queueConnectionEpoch || !IsConnected || !ReferenceEquals(pending.OwnerClient, Client))
        {
            SetStatus($"The previously connected service confirmed restore revision {result.HostRevision}. Reconnect to it to inspect settings." + warning);
            return;
        }
        if (isolatedSettingsPresetFixture)
        {
            SetStatus($"Settings restored at revision {result.HostRevision}." + warning);
            return;
        }
        try
        {
            await RefreshSettings();
            if (pending.Connection != queueConnectionEpoch) return;
            await RefreshAgents();
            if (pending.Connection != queueConnectionEpoch) return;
            await RefreshModels();
            if (pending.Connection != queueConnectionEpoch) return;
            await RefreshInstructionTargets();
            if (pending.Connection != queueConnectionEpoch) return;
            RefreshSettingsSummary();
            SetStatus($"Configuration restored at revision {result.HostRevision}. Models remain unloaded. Review connections marked as needing configuration." + warning);
        }
        catch (Exception error)
        {
            SetStatus($"Configuration committed at revision {result.HostRevision}; the display could not refresh: {error.Message}" + warning);
        }
    }

    private bool ReviewConfiguration(string title, IReadOnlyList<ConfigurationBackupAttention> attention,
        string description, string reviewText, string action)
    {
        var dialog = CreateSettingsPresetReviewDialog(title, attention, description, action, reviewText, this);
        dialog.Width = 740; dialog.Height = 620; dialog.MinWidth = 480; dialog.MinHeight = 360;
        return dialog.ShowDialog() == true;
    }
}
