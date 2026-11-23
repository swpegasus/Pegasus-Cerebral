using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private static void SetSettingsControlHelp(FrameworkElement control, string help)
    {
        control.ToolTip = help;
        ToolTipService.SetShowOnDisabled(control, true);
        AutomationProperties.SetHelpText(control, help);
    }

    private static string InterfacePreferenceHelp(string name) => name switch
    {
        InterfacePreferencePolicy.AppearanceTheme =>
            "Color mode for interfaces connected to this Worker. Default: System follows Windows. " +
            "A selection previews here immediately and is saved automatically when the Worker acknowledges it.",
        InterfacePreferencePolicy.MessageTimeFormat =>
            "Message display for interfaces connected to this Worker. Default: 12-hour; 24-hour is also available. " +
            "A selection saves automatically. Message timestamps are unchanged.",
        InterfacePreferencePolicy.RecentChatLimit =>
            $"Number of recent general chats shown, from {RecentChatPolicy.MinimumLimit} to {RecentChatPolicy.MaximumLimit}; default {RecentChatPolicy.DefaultLimit}. " +
            "Saved for this Worker when you press Enter or leave the field. Older chats, pins and projects remain stored.",
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private void RefreshInterfacePreferenceControls()
    {
        var available = CanEditInterfacePreferences;
        var reason = available ? "" : !IsConnected
            ? "Unavailable: connect to the Worker to read and save these preferences."
            : !ReferenceEquals(Client, appearancePreferencesClient)
                ? "Unavailable: the current Worker's saved preferences have not been read. Reconnect to read them."
                : "Unavailable: this Worker does not support interface preferences. Use a compatible Worker to save them.";
        foreach (var (control, name) in new (FrameworkElement Control, string Name)[]
        {
            (appearanceTheme, InterfacePreferencePolicy.AppearanceTheme),
            (messageTimeFormat, InterfacePreferencePolicy.MessageTimeFormat),
            (recentChatLimitInput, InterfacePreferencePolicy.RecentChatLimit)
        })
        {
            control.IsEnabled = available;
            SetSettingsControlHelp(control, InterfacePreferenceHelp(name) + (reason.Length == 0 ? "" : "\n" + reason));
        }
    }

    private Button WorkerSettingsButton(string label, Func<Task> action)
    {
        var button = Ui.Button(label, () => Run(action));
        void Refresh()
        {
            button.IsEnabled = IsConnected;
            SetSettingsControlHelp(button, IsConnected
                ? "Edits the connected Worker's saved settings. Valid changes save automatically; Done waits for the save result. Closing does not undo an acknowledged save."
                : "Unavailable: connect to the Worker before editing its saved settings.");
        }
        ServiceStatusChanged += (_, _) => Refresh();
        Refresh();
        return button;
    }

    private static string SettingsFieldHelp(string key)
    {
        // These are factory defaults, not a replacement for the connected Worker's displayed value.
        var defaults = new HostSettings();
        var description = key switch
        {
            "folders.projects" => "Default folder for new project work. The installation chooses its initial path.",
            "folders.models" => "Folder scanned for local model files. The installation chooses its initial path.",
            "folders.tools" => "Folder for shared portable executables and runtimes. The installation chooses its initial path.",
            "folders.shared" => "Folder for shared files. The installation chooses its initial path.",
            "folders.runtime" => "Path to llama-server. The installation chooses its initial runtime path.",
            "resources.ownerQueue" => $"Maximum unfinished owner jobs, including running and waiting work. Factory default: {defaults.QueueCapacity} jobs.",
            "resources.peerQueue" => $"Maximum unfinished jobs admitted from paired installations. Factory default: {defaults.PeerQueueCapacity} jobs.",
            "resources.concurrent" => $"Maximum jobs running at once. Factory default: {defaults.MaxConcurrentJobs} jobs. Model capacity and other admission checks still apply.",
            "resources.ownerSlots" => $"Execution slots reserved while owner work is waiting; peers may borrow them while the owner is idle. Factory default: {defaults.ReservedOwnerSlots} slot.",
            "resources.workflowExpiry" => $"Idle days before a safely interrupted workflow expires. Factory default: {defaults.WorkflowIdleExpiryDays} days. Zero disables expiry. Work awaiting an answer or confirmation of an external action stays available.",
            "resources.memory" => $"System memory to keep free, in MiB (1 MiB = 1,048,576 bytes). Factory default: {defaults.ReservedMemoryMb} MiB.",
            "resources.processors" => "Logical processor IDs. Factory default: an empty list uses available processors except processor 0. Explicitly include 0 to permit it.",
            "resources.processorLimit" => $"Maximum logical processor count. Factory default: {defaults.MaxLogicalProcessorCount}, meaning no additional count limit on the selected processors.",
            "resources.modelIdle" => $"Idle unload delay, in minutes, for models inheriting the Worker default. Factory default: {defaults.DefaultModelIdleMinutes} minutes; 0 means never unload for idleness.",
            "resources.attempts" => $"Maximum launches of the same executable during one accepted assignment. Factory default: {defaults.MaxExecutableAttemptsPerAssignment} attempts.",
            "history.jobs" => $"Maximum jobs shown in history. Factory default: {defaults.JobHistoryLimit} jobs. Active admission and required recovery records are separate.",
            "history.jobDays" => $"Age window for job history, in days. Factory default: {defaults.JobHistoryDays} days.",
            "history.tools" => $"Completed tool-call journal rows retained per conversation. Factory default: {defaults.ActivityHistoryLimit} rows.",
            "history.toolDays" => $"Maximum age of completed tool-call journal rows, in days. Factory default: {defaults.ActivityHistoryDays} days.",
            "history.engineEvents" => $"Rolling Engine diagnostic events retained per conversation. Factory default: {defaults.EngineEventHistoryLimit} events.",
            "history.engineMemory" => $"Rolling Engine diagnostic payload per conversation, in MiB. Factory default: {defaults.EngineEventHistoryMegabytes} MiB.",
            "history.engineDays" => $"Maximum age of rolling Engine diagnostic events, in days. Factory default: {defaults.EngineEventHistoryDays} days.",
            "history.cacheCollection" => $"Optional numeric prompt-cache diagnostics kept for a rolling 24 hours. Factory default: {(defaults.PromptCacheDiagnosticsEnabled ? "enabled" : "disabled")}.",
            "history.cacheThreshold" => $"Meaningful diagnostic change threshold. Reuse uses percentage points; token and prefill changes use relative percentages with minimum changes. Factory default: {defaults.PromptCacheDiagnosticsChangePercentagePoints}%.",
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        var effect = key.StartsWith("folders.", StringComparison.Ordinal)
            ? "Editing the saved path does not move files or restart an existing model runtime. Subsequent operations use the saved path."
            : key is "resources.processors" or "resources.processorLimit"
                ? "Processor changes take effect after the Worker restarts."
                : key.StartsWith("history.", StringComparison.Ordinal)
                    ? "Saved limits apply to rolling views and collection. Conversations and required recovery state are separate."
                    : "Saved limits are used by subsequent Worker scheduling and admission decisions.";
        return description + "\nScope: connected Worker. " + effect;
    }
}
