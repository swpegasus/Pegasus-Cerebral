using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private const string MainChatTimingCaseEnvironment = "CEREBRAL_MAIN_CHAT_TIMING_CASE";
    private static int mainChatTimingArmClaimed;
    private MainChatInterfaceTimingProbe? mainChatTimingProbe;
    private bool mainChatTimingTerminalQueued;
    private Action<WorkflowPresentationMark>? mainChatTimingObserver;
    private EventHandler? mainChatTimingRenderHandler;

    private void TryArmMainChatTiming()
    {
        try
        {
            if (!IsNativeChat || mainChatTimingProbe is not null || nativeChat is null) return;
            var caseId = Environment.GetEnvironmentVariable(MainChatTimingCaseEnvironment);
            if (caseId is null || caseId.Length != 32 ||
                caseId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                Interlocked.Exchange(ref mainChatTimingArmClaimed, 1) != 0) return;
            var workspace = nativeChat;
            var probe = new MainChatInterfaceTimingProbe(caseId, Stopwatch.GetTimestamp,
                result => { _ = Task.Run(() => WriteMainChatTimingResult(result)); },
                () => DetachMainChatTiming(workspace));
            mainChatTimingObserver = mark =>
            {
                try
                {
                    if (probe.ObservePresentation(mark) && mainChatTimingRenderHandler is null)
                    {
                        EventHandler? handler = null;
                        handler = (_, _) =>
                        {
                            try { probe.MarkRenderSchedulingCallback(); }
                            finally { CompositionTarget.Rendering -= handler; mainChatTimingRenderHandler = null; }
                        };
                        mainChatTimingRenderHandler = handler;
                        CompositionTarget.Rendering += handler;
                    }
                }
                catch { /* The timing observer must never alter chat presentation. */ }
            };
            workspace.PresentationMarked += mainChatTimingObserver;
            mainChatTimingProbe = probe;
            mainChatTimingTerminalQueued = false;
        }
        catch { /* An opt-in timing arm cannot change ordinary Send. */ }
    }

    private void DetachMainChatTiming(WorkflowWorkspace workspace)
    {
        void Detach()
        {
            if (mainChatTimingObserver is { } observer) workspace.PresentationMarked -= observer;
            mainChatTimingObserver = null;
            if (mainChatTimingRenderHandler is { } handler) CompositionTarget.Rendering -= handler;
            mainChatTimingRenderHandler = null;
        }
        if (Dispatcher.CheckAccess()) Detach();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(Detach));
    }

    private void CloseMainChatTiming(MainChatTimingEnd end)
    {
        var probe = mainChatTimingProbe;
        if (probe is null) return;
        mainChatTimingProbe = null;
        mainChatTimingTerminalQueued = false;
        try { probe.Finish(end); }
        catch { /* A diagnostic may not affect the selected conversation. */ }
    }

    private void QueueMainChatTimingTerminal(string conversationId, string requestId)
    {
        var probe = mainChatTimingProbe;
        if (probe is null || probe.RequestId != requestId || mainChatTimingTerminalQueued) return;
        mainChatTimingTerminalQueued = true;
        var selection = conversationSelectionVersion;
        var connection = ServiceConnection;
        var identity = ServiceConnectionIdentity;
        var workspace = nativeChat;
        MainChatTimingTerminalDeferral.Queue(Dispatcher,
            () =>
            {
                if (!ReferenceEquals(mainChatTimingProbe, probe)) return false;
                mainChatTimingTerminalQueued = false;
                if (selection != conversationSelectionVersion || !ReferenceEquals(workspace, nativeChat) ||
                    durableConversationId != conversationId)
                { CloseMainChatTiming(MainChatTimingEnd.SelectionChanged); return false; }
                if (!ReferenceEquals(connection, ServiceConnection) || identity != ServiceConnectionIdentity)
                { CloseMainChatTiming(MainChatTimingEnd.ConnectionChanged); return false; }
                return true;
            },
            () => workspace?.Snapshot, conversationId, requestId, CloseMainChatTiming,
            () => { mainChatTimingTerminalQueued = false; CloseMainChatTiming(MainChatTimingEnd.Disposed); });
    }

    private static void WriteMainChatTimingResult(MainChatTimingResult result)
    {
        try
        {
            var root = DesktopPreferenceDiagnostics.DiagnosticsDirectory;
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "main-chat-timing-" + result.CaseId + ".json");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            file.Flush(flushToDisk: true);
        }
        catch { /* CreateNew prevents overwriting a prior case; failure is diagnostic-only. */ }
    }
}
