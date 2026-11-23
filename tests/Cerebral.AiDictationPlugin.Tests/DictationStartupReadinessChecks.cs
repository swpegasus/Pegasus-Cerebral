using System.IO;
using Cerebral.AiDictationPlugin;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

internal static partial class Program
{
    // Source proposal only. Root runs this focused group with the grouped SDK build.
    private static async Task<int> RunStartupReadinessChecksAsync()
    {
        var checks = 0;
        void Verify(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        async Task<Exception> FailureAsync(Task work)
        {
            try { await work; } catch (Exception failure) { return failure; }
            throw new InvalidOperationException("Startup unexpectedly reported ready.");
        }
        TestHost Host() => new()
        {
            Surface = DesktopPluginSurface.Orchestrator,
            DataDirectory = Path.Combine(Path.GetTempPath(), "dictation-startup-readiness-" + Guid.NewGuid().ToString("N"))
        };
        var host = Host();
        await host.ExecuteServiceCommandAsync(CompleteSettingsContract.PluginSaveOperation,
            new PluginSettingsTransferSaveRequest(new(AiDictationSettingsPolicy.Document(new AiDictationSettings
                { AutoTranscribeOnSilence = true, InputBoostDb = 9 }), 0, OwnerValidated: true)));
        var revision = host.Settings.Revision;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SettingsReadGate = gate.Task;
        var capture = new FakeCapture(); var providers = 0;
        using (var plugin = new AiDictationPlugin(capture, _ => { providers++; throw new InvalidOperationException("No model load is permitted."); }))
        {
            var participant = (IDesktopPluginStartupReadiness)plugin;
            Verify(await FailureAsync(participant.WaitForStartupAsync(CancellationToken.None)) is InvalidOperationException,
                "An uninitialized plugin cannot report ready.");
            plugin.Initialize(host);
            var waiting = participant.WaitForStartupAsync(CancellationToken.None);
            Verify(!waiting.IsCompleted && host.SettingsReadRequests == 1, "Readiness waits for the already-started preference read.");
            using var cancelled = new CancellationTokenSource();
            var cancelledWait = participant.WaitForStartupAsync(cancelled.Token); cancelled.Cancel();
            Verify(await FailureAsync(cancelledWait) is OperationCanceledException && !waiting.IsCompleted && host.SettingsReadRequests == 1,
                "One cancelled waiter cannot report ready, cancel the shared passive read or trigger another read.");
            gate.SetResult(); await waiting;
            var acknowledged = plugin.ReadRuntimePreferences();
            Verify(acknowledged.AutoTranscribeOnSilence && acknowledged.InputBoostDb == 9 && host.Settings.Revision == revision,
                "Readiness completes only after acknowledged preferences are adopted without saving them.");
            await participant.WaitForStartupAsync(CancellationToken.None);
            Verify(host.SettingsReadRequests == 1 && !capture.Started.Task.IsCompleted && providers == 0 && host.Submissions == 0,
                "Repeated readiness checks are passive and never arm saved Auto, load a model or submit.");
        }

        var failedHost = Host(); var original = new IOException("Synthetic preference owner read failed.");
        failedHost.SettingsReadGate = Task.FromException(original);
        using (var failed = new AiDictationPlugin(new FakeCapture(), _ => throw new InvalidOperationException("No model load is permitted.")))
        {
            failed.Initialize(failedHost);
            var participant = (IDesktopPluginStartupReadiness)failed;
            var first = await FailureAsync(participant.WaitForStartupAsync(CancellationToken.None));
            var again = await FailureAsync(participant.WaitForStartupAsync(CancellationToken.None));
            Verify(ReferenceEquals(first, again) && ReferenceEquals(first.InnerException, original) && failedHost.SettingsReadRequests == 1,
                "The tracked startup task preserves the original read failure and never retries a failed generation.");
        }
        failedHost.SettingsReadGate = Task.CompletedTask;
        using (var fresh = new AiDictationPlugin(new FakeCapture(), _ => throw new InvalidOperationException("No model load is permitted.")))
        {
            fresh.Initialize(failedHost);
            await ((IDesktopPluginStartupReadiness)fresh).WaitForStartupAsync(CancellationToken.None);
            Verify(failedHost.SettingsReadRequests == 2, "An explicit host Retry may hydrate a fresh plugin instance.");
        }
        var retiringHost = Host();
        retiringHost.SettingsReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var retiring = new AiDictationPlugin(new FakeCapture(), _ => throw new InvalidOperationException("No model load is permitted."));
        retiring.Initialize(retiringHost);
        var retirementWait = ((IDesktopPluginStartupReadiness)retiring).WaitForStartupAsync(CancellationToken.None);
        retiring.Dispose();
        Verify(await FailureAsync(retirementWait) is OperationCanceledException or ObjectDisposedException,
            "Retiring an instance while preferences load cannot publish ready.");
        Verify(await FailureAsync(((IDesktopPluginStartupReadiness)retiring).WaitForStartupAsync(CancellationToken.None)) is ObjectDisposedException,
            "A disposed instance remains unavailable.");
        return checks;
    }
}
