using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

internal sealed record SettingsExportAvailability(bool CompleteAvailable, string? Reason = null);

/// <summary>Display and envelope checks only. Core admits files; packages own their opaque settings.</summary>
internal static class SettingsExportReview
{
    internal static string Purpose(ConfigurationBackupDocument document)
    {
        if (document.Format != ConfigurationBackupContract.Format)
            throw new InvalidDataException("This file is not a Cerebral settings export.");
        if (document.SchemaVersion is >= 1 and <= ConfigurationBackupContract.SchemaVersion && document.SettingsTransferMode is null)
            return SettingsTransferPurpose.Portable;
        if (document.SchemaVersion == CompleteSettingsContract.BackupSchemaVersion &&
            document.SettingsTransferMode == SettingsTransferPurpose.Complete)
            return SettingsTransferPurpose.Complete;
        throw new InvalidDataException("This settings file has an unsupported or mismatched export purpose.");
    }

    internal static string Label(string purpose) => purpose switch
    {
        SettingsTransferPurpose.Portable => "Portable preset",
        SettingsTransferPurpose.Complete => "Complete settings",
        _ => throw new ArgumentException("Select a supported settings export purpose.")
    };

    internal static void Validate(ConfigurationBackupExport exported, string requestedPurpose)
    {
        if (exported is null || exported.Document is null || exported.Document.Host is null || exported.Document.Paths is null ||
            exported.Document.Agents is null || exported.Document.Models is null || exported.Attention is null ||
            Purpose(exported.Document) != requestedPurpose)
            throw new InvalidDataException("The settings export did not match the selected purpose.");
        if (requestedPurpose == SettingsTransferPurpose.Portable)
        {
            if (exported.CompleteSettingsCoverage is not null)
                throw new InvalidDataException("A portable export returned incompatible complete-settings coverage.");
            return;
        }
        var coverage = exported.CompleteSettingsCoverage;
        if (coverage is null || !coverage.Complete || coverage.ActivePluginDocuments < 0 ||
            coverage.PendingPluginDocuments < 0 || coverage.ExcludedFields < 0 ||
            coverage.LayoutCoverage != "opaque-renderer-state" || coverage.IncludedFacets is null ||
            coverage.IncludedFacets.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Complete settings coverage was not confirmed. No file was saved.");
    }

    internal static string Description(ConfigurationBackupExport exported)
    {
        var purpose = Purpose(exported.Document);
        if (purpose == SettingsTransferPurpose.Portable)
            return "Save the declared portable preferences. Review the included instructions, paths and access grants, and any settings left out below. Credentials and domain content remain in their owned stores.";
        var coverage = exported.CompleteSettingsCoverage!;
        return $"Save all covered nonsecret preferences: {coverage.ActivePluginDocuments} active plugin group(s) and {coverage.PendingPluginDocuments} pending group(s). " +
            $"Credential or domain exclusions: {coverage.ExcludedFields} field(s). A complete export is refused when eligible preferences lack compatible coverage.\n" +
            "Supported window and page layout is included. Package settings may contain private text and local paths; review their declared scope before saving. Coverage follows the fields declared by each package. Credentials, model files, conversations and other domain content are saved separately.";
    }

    internal static string CoreText(ConfigurationBackupDocument document, ConfigurationRestorePathOverrides? restorePaths = null)
    {
        var text = new StringBuilder();
        void Field(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) text.Append(label).Append(": ").AppendLine(value);
        }
        void List(string label, IEnumerable<string>? values)
        {
            var entries = (values ?? []).ToArray();
            if (entries.Length > 0) Field(label, string.Join(", ", entries));
        }
        var host = document.Host;
        text.AppendLine("Core preferences");
        Field("Concurrent work limit", host.MaxConcurrentJobs.ToString());
        Field("Queue capacity", host.QueueCapacity.ToString());
        Field("Job history count", host.JobHistoryLimit.ToString()); Field("Job history days", host.JobHistoryDays.ToString());
        Field("Interrupted workflow expiry (days)", host.WorkflowIdleExpiryDays?.ToString());
        Field("Activity history count", host.ActivityHistoryLimit.ToString()); Field("Activity history days", host.ActivityHistoryDays.ToString());
        Field("Reserved memory (MB)", host.ReservedMemoryMb.ToString()); Field("Default model idle time (minutes)", host.DefaultModelIdleMinutes.ToString());
        Field("Executable attempts per assignment", host.MaxExecutableAttemptsPerAssignment.ToString());
        Field("Default model", host.DefaultModelId); Field("Default inference machine", host.DefaultModelWorkerId);
        Field("Question timeout (seconds)", host.QuestionTimeoutSeconds.ToString()); Field("Question timeout action", host.QuestionTimeoutAction);
        Field("Engine history count", host.EngineEventHistoryLimit?.ToString()); Field("Engine history size (MB)", host.EngineEventHistoryMegabytes?.ToString());
        Field("Engine history days", host.EngineEventHistoryDays?.ToString()); Field("Processor limit", host.MaxLogicalProcessorCount?.ToString());
        List("Allowed processors", host.AllowedLogicalProcessors?.Select(x => x.ToString()));
        Field("Prompt cache diagnostics", host.PromptCacheDiagnosticsEnabled?.ToString());
        Field("Prompt cache change threshold (%)", host.PromptCacheDiagnosticsChangePercentagePoints?.ToString());
        if (document.InterfacePreferences is { } preferences)
        {
            Field("Enter sends", preferences.EnterSends?.ToString()); Field("Navigation visible", preferences.NavigationVisible?.ToString());
            Field("Panels visible", preferences.PanelsVisible?.ToString()); Field("Selected surface", preferences.SelectedSurface);
            Field("Appearance", preferences.AppearanceTheme); Field("Message time format", preferences.MessageTimeFormat);
            Field("Recent chat limit", preferences.RecentChatLimit?.ToString());
        }
        text.AppendLine();
        var paths = document.Paths;
        text.AppendLine("Included folder references");
        Field("Models", paths.ModelDirectory); Field("Projects", paths.ProjectsDirectory);
        Field("Shared files", paths.SharedDirectory); Field("Tools", paths.ToolsDirectory);
        text.AppendLine();
        foreach (var agent in document.Agents ?? [])
        {
            Field("Agent profile", agent.Name);
            Field("Role", agent.Role); Field("Purpose", agent.Purpose); Field("Instructions", agent.Instructions);
            List("Allowed folders", agent.AllowedRoots); List("Allowed tools", agent.AllowedTools);
            List("Capabilities", agent.Capabilities); List("Required model capabilities", agent.RequiredModelCapabilities);
            Field("Execution machine", agent.WorkerId); Field("Inference machine", agent.ModelWorkerId);
            text.AppendLine();
        }
        foreach (var prompt in document.Prompts ?? [])
        {
            Field("Shared instructions for " + prompt.Role, prompt.Content); text.AppendLine();
        }
        foreach (var skill in document.Skills ?? [])
        {
            Field("Skill", skill.Manifest.Name); Field("Description", skill.Manifest.Description);
            List("When to load", skill.Manifest.Triggers); Field("Included instructions", skill.Content);
            foreach (var binding in skill.Bindings) Field("Applies to", binding.Scope + (binding.Target is null ? "" : " · " + binding.Target));
            text.AppendLine();
        }
        foreach (var model in document.Models ?? [])
        {
            Field("Model registration", model.Name); Field("Model file", model.Path);
            Field("Image projector", model.VisionProjectorPath); Field("Source", model.SourceUrl);
            Field("Description", model.Description); Field("Chat template", model.ChatTemplate);
            List("Stop sequences", model.StopSequences); text.AppendLine();
        }
        if (document.Connections is { } connections)
        {
            foreach (var worker in connections.Workers)
            {
                Field("Machine", worker.Name); Field("Address", worker.Url); Field("Remote host", worker.SshHost);
                Field("Remote user", worker.SshUser); Field("Installation folder", worker.InstallDirectory); text.AppendLine();
            }
            foreach (var executable in connections.Executables) Field("Executable " + executable.Id, executable.Path);
            foreach (var server in connections.McpServers)
            {
                Field("MCP connection", server.Id); Field("Executable", server.Executable); Field("Address", server.Url);
                text.AppendLine();
            }
            Field("Shared-files machine", connections.SharedFilesWorkerId);
            List("Readable Cortex spaces", connections.CortexNamespaces);
            List("Writable Cortex spaces", connections.CortexWriteNamespaces);
        }
        if (restorePaths is not null)
        {
            text.AppendLine();
            text.AppendLine("Restore locations selected");
            Field("Models", restorePaths.ModelDirectory); Field("Projects", restorePaths.ProjectsDirectory);
            Field("Shared files", restorePaths.SharedDirectory); Field("Tools", restorePaths.ToolsDirectory);
            foreach (var entry in restorePaths.ModelFiles ?? new Dictionary<string, string>()) Field("Model file for " + entry.Key, entry.Value);
            foreach (var entry in restorePaths.ModelProjectorFiles ?? new Dictionary<string, string>()) Field("Image projector for " + entry.Key, entry.Value);
        }
        // Only declared public core fields are read. No agent PluginSettings, complete Settings
        // facet, appearance/layout Values, or package file bodies enter this generic renderer.
        return text.ToString().TrimEnd();
    }
}

public sealed partial class IntegratorWindow
{
    private string? ChooseSettingsExportPurpose(SettingsExportAvailability availability)
    {
        var dialog = CreateSettingsExportPurposeDialog(availability, this, out var complete);
        return dialog.ShowDialog() == true ? complete.IsChecked == true ? SettingsTransferPurpose.Complete : SettingsTransferPurpose.Portable : null;
    }

    private static Window CreateSettingsExportPurposeDialog(SettingsExportAvailability availability, Window? owner, out RadioButton complete)
    {
        var dialog = new Window { Owner = owner, Icon = owner?.Icon ?? Branding.IconFor("Integrator"), Title = "Export settings", Width = 590, Height = 350,
            MinWidth = 430, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(Ui.Text("Choose what to export", 22));
        var portable = new RadioButton { Content = "Portable preset", IsChecked = true, Margin = new Thickness(0, 14, 0, 6) };
        complete = new RadioButton { Content = "Complete settings", IsEnabled = availability.CompleteAvailable, Margin = new Thickness(0, 12, 0, 6) };
        content.Children.Add(portable);
        content.Children.Add(Ui.Text("Declared portable preferences for transfer. Review any omitted settings before saving.", 13, Ui.Muted));
        content.Children.Add(complete);
        content.Children.Add(Ui.Text("All covered nonsecret preferences, including package-declared private text and local paths. Credentials and domain content are separate.", 13, Ui.Muted));
        if (availability.Reason is { } reason) content.Children.Add(Ui.Text(reason, 12, Ui.Muted));
        content.Children.Add(Ui.Bar(Ui.Button("Continue", () => dialog.DialogResult = true, true),
            Ui.Button("Cancel", () => dialog.DialogResult = false)));
        dialog.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        WindowSizing.Attach(dialog);
        return dialog;
    }
}
