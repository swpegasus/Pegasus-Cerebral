using System.IO;
using System.Windows.Automation;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

/// <summary>Fixture policy and unshown STA controls only; no connector, Worker or configuration effects.</summary>
public static class OrchestratorConnectorStartupChecks
{
    public static async Task<string> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks++; }
        async Task Rejects<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException(message);
        }
        MainConnectorStartup Eligible()
        { var value = new MainConnectorStartup(); value.NoteReadyWorker(true, false, false); return value; }

        var beforeReady = new MainConnectorStartup(); var beforeOps = new Fixture();
        Check(await beforeReady.CompleteAsync(MainStartupMode.Installed, beforeOps, () => { }, default) is null &&
            beforeOps.StatusCalls == 0, "No connector work before an eligible Worker-ready observation.");
        foreach (var setting in new[] { (false, false, false), (true, true, false), (true, false, true) })
        {
            var attempt = new MainConnectorStartup(); var ops = new Fixture();
            attempt.NoteReadyWorker(setting.Item1, setting.Item2, setting.Item3);
            Check(await attempt.CompleteAsync(MainStartupMode.Installed, ops, () => { }, default) is null &&
                ops.StatusCalls == 0 && ops.StartCalls == 0, "Manual/source/portable eligibility never queries or starts the connector.");
        }
        foreach (var mode in new[] { MainStartupMode.AttachedExisting, MainStartupMode.SourceLocal })
        {
            var ops = new Fixture();
            Check(await Eligible().CompleteAsync(mode, ops, () => { }, default) is null && ops.StatusCalls == 0,
                "Attached/source completion does not acquire installed connector authority.");
        }
        foreach (var branch in new[] { "saved installed-user binding", "migrated installed-user binding" })
        {
            var events = new List<string>(); var ops = new Fixture { Events = events, Status = Status("stopped", 1) };
            var attempt = new MainConnectorStartup(); events.Add("worker-ready"); attempt.NoteReadyWorker(true, false, false);
            var result = await attempt.CompleteAsync(MainStartupMode.Installed, ops, () => events.Add("owner"), default);
            Check(result is { State: "running", Warning: false } && ops.StatusCalls == 1 && ops.StartCalls == 1 &&
                events.IndexOf("worker-ready") < events.IndexOf("status") && events.IndexOf("status") < events.IndexOf("start"),
                branch + " uses one connector-only attempt after the supplied Worker-ready observation.");
            Check(await attempt.CompleteAsync(MainStartupMode.Installed, ops, () => { }, default) is null && ops.StartCalls == 1,
                "Completed Main startup does not repeat a connector start.");
        }

        var running = new Fixture(); var runningResult = await Eligible().CompleteAsync(MainStartupMode.Installed, running, () => { }, default);
        Check(runningResult is { State: "running", Warning: false } && running.StartCalls == 0 &&
            runningResult.Message.Contains("Provider readiness is checked separately", StringComparison.Ordinal),
            "Authenticated connector running is reused without claiming provider readiness.");
        foreach (var state in new[] { Status("not-configured", 0), Status("stopped", 0) })
        {
            var ops = new Fixture { Status = state };
            Check(await Eligible().CompleteAsync(MainStartupMode.Installed, ops, () => { }, default) is { Warning: false } &&
                ops.StartCalls == 0, "An unconfigured/empty connector is not provisioned or started.");
        }
        foreach (var state in new[] { "starting", "stopping", "unmanaged", "mismatch", "invalid-config", "unavailable", "future-unknown-state" })
        {
            var ops = new Fixture { Status = Status(state, 1) };
            Check(await Eligible().CompleteAsync(MainStartupMode.Installed, ops, () => { }, default) is { Warning: true } &&
                ops.StartCalls == 0, "Unconfirmed connector state is visible and never launches a replacement: " + state);
        }

        var statusFailure = new Fixture { StatusFailure = new UnauthorizedAccessException("sensitive fixture detail") };
        var failedStatus = await Eligible().CompleteAsync(MainStartupMode.Installed, statusFailure, () => { }, default);
        Check(failedStatus is { State: "unavailable", Warning: true } && statusFailure.StartCalls == 0 &&
            !failedStatus.Message.Contains("sensitive", StringComparison.Ordinal), "Optional status failure exposes fixed host text without another start.");
        var uncertainAttempt = Eligible(); var uncertainOps = new Fixture
        { Status = Status("stopped", 1), StartFailure = new InvalidOperationException("Startup confirmation interrupted") };
        var uncertain = await uncertainAttempt.CompleteAsync(MainStartupMode.Installed, uncertainOps, () => { }, default);
        Check(uncertain is { State: "unconfirmed", Warning: true } && uncertainOps.StartCalls == 1,
            "An interrupted start is unconfirmed, not success or stopped.");
        Check(await uncertainAttempt.CompleteAsync(MainStartupMode.Installed, uncertainOps, () => { }, default) is null &&
            uncertainOps.StartCalls == 1, "Uncertain accepted startup is not replayed.");
        var stoppedReply = new Fixture { Status = Status("stopped", 1), StartReply = Status("stopped", 1) };
        Check(await Eligible().CompleteAsync(MainStartupMode.Installed, stoppedReply, () => { }, default) is { State: "stopped", Warning: true },
            "An unconfirmed stopped start reply stays a warning.");

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var cancelledOps = new Fixture();
        await Rejects<OperationCanceledException>(() => Eligible().CompleteAsync(MainStartupMode.Installed, cancelledOps, () => { }, cancelled.Token),
            "Caller cancellation must propagate before connector work.");
        Check(cancelledOps.StatusCalls == 0, "Pre-cancelled startup has no connector operation.");
        using var wrappedCancellation = new CancellationTokenSource(); var cancelledAttempt = Eligible();
        var wrappedOps = new Fixture { Status = Status("stopped", 1), AfterStart = wrappedCancellation.Cancel,
            StartFailure = new InvalidOperationException("Manager wrapped interrupted wait") };
        await Rejects<OperationCanceledException>(() => cancelledAttempt.CompleteAsync(MainStartupMode.Installed, wrappedOps, () => { }, wrappedCancellation.Token),
            "Wrapped manager cancellation must remain required cancellation.");
        Check(cancelledAttempt.Outcome is null && wrappedOps.StartCalls == 1 &&
            await cancelledAttempt.CompleteAsync(MainStartupMode.Installed, wrappedOps, () => { }, default) is null,
            "Caller cancellation neither reports optional success nor retries an accepted start.");

        var lostOwner = false; var ownerAttempt = Eligible();
        var changed = new Fixture { Status = Status("stopped", 1), AfterStatus = () => lostOwner = true };
        await Rejects<UnauthorizedAccessException>(() => ownerAttempt.CompleteAsync(MainStartupMode.Installed, changed,
            () => { if (lostOwner) throw new UnauthorizedAccessException("Required owner changed"); }, default),
            "An owner fence after status must not be swallowed by the optional connector catch.");
        Check(ownerAttempt.Outcome is null && changed.StartCalls == 0, "Changed required owner prevents connector launch and warning downgrade.");
        var lostAfterStart = false; var startedAttempt = Eligible();
        var started = new Fixture { Status = Status("stopped", 1), AfterStart = () => lostAfterStart = true };
        await Rejects<UnauthorizedAccessException>(() => startedAttempt.CompleteAsync(MainStartupMode.Installed, started,
            () => { if (lostAfterStart) throw new UnauthorizedAccessException("Owner changed after accepted start"); }, default),
            "Owner loss after accepted start remains required failure.");
        Check(startedAttempt.Outcome is null && started.StartCalls == 1 &&
            await startedAttempt.CompleteAsync(MainStartupMode.Installed, started, () => { }, default) is null,
            "Owner loss cannot replay the accepted start.");

        var pending = new TaskCompletionSource<ConnectorStatus>();
        var pendingOps = new Fixture { StatusTask = pending.Task }; var pendingAttempt = Eligible();
        var first = pendingAttempt.CompleteAsync(MainStartupMode.Installed, pendingOps, () => { }, default);
        Check(await pendingAttempt.CompleteAsync(MainStartupMode.Installed, pendingOps, () => { }, default) is null && pendingOps.StatusCalls == 1,
            "A concurrent completion cannot duplicate the in-flight status/start attempt.");
        pending.SetResult(Status("running", 1));
        Check(first.IsCompleted, "The deterministic in-flight fixture settles before unshown STA control construction.");
        await first;

        var splash = CerebralStartupWindow.ForInstalled();
        try
        {
            splash.SetWarning(uncertain!.Message); splash.SetProgress("Connected · Fixture Worker ready");
            var firstWarning = OrchestratorWindow.ConnectorStartupWarning(uncertainAttempt.Outcome);
            splash.SetProgress("Selected Main model · Fixture prepared");
            var remountedWarning = OrchestratorWindow.ConnectorStartupWarning(uncertainAttempt.Outcome);
            Check(firstWarning is not null && remountedWarning is not null &&
                firstWarning.Text == "Connector startup warning" && remountedWarning.Text == firstWarning.Text &&
                AutomationProperties.GetAutomationId(remountedWarning) == "UserConnectorStartupWarning" &&
                Equals(remountedWarning.ToolTip, uncertain.Message), "Later progress/remount retains a fixed nonmodal connector startup warning.");
            Check(splash.DiagnosticText.Contains("unconfirmed", StringComparison.Ordinal) &&
                OrchestratorWindow.ConnectorStartupWarning(runningResult) is null,
                "Existing startup details retain the warning; successful connector startup adds no warning item.");
        }
        finally { splash.Close(); }
        return checks + " focused connector-startup assertions passed (fixtures and unshown STA controls only; no live connector/Worker/provider operation).";
    }

    private static ConnectorStatus Status(string state, int providers) => new(state, "http://127.0.0.1:6124", "Fixture", providers);

    private sealed class Fixture : IServiceStartupOperations
    {
        internal ConnectorStatus Status = OrchestratorConnectorStartupChecks.Status("running", 1);
        internal ConnectorStatus? StartReply;
        internal Exception? StatusFailure, StartFailure;
        internal Action? AfterStatus, AfterStart;
        internal Task<ConnectorStatus>? StatusTask;
        internal List<string>? Events;
        internal int StatusCalls, StartCalls;
        public Task<ConnectorStatus> ConnectorStatusAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); StatusCalls++; Events?.Add("status"); AfterStatus?.Invoke();
            if (StatusFailure is not null) throw StatusFailure;
            return StatusTask ?? Task.FromResult(Status);
        }
        public Task<ConnectorStatus> StartConnectorAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); StartCalls++; Events?.Add("start"); AfterStart?.Invoke();
            if (StartFailure is not null) throw StartFailure;
            return Task.FromResult(StartReply ?? (Status with { State = "running" }));
        }
        public Task<HostInfo?> ProbeWorkerAsync(string url, string key, CancellationToken token) => throw new InvalidOperationException("HTTP Worker probe is forbidden in this fixture.");
        public Task<bool> IsWorkerInstalledAsync(CancellationToken token) => throw new InvalidOperationException("Worker installation lookup is forbidden in this fixture.");
        public Task StartInstalledWorkerAsync(CancellationToken token) => throw new InvalidOperationException("Worker start is forbidden in this fixture.");
        public Task StartLocalWorkerAsync(string url, CancellationToken token) => throw new InvalidOperationException("Local Worker start is forbidden in this fixture.");
    }
}
