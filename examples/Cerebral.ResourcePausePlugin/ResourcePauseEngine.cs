using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ResourcePausePlugin;

public sealed partial class ResourcePausePlugin
{
    private const int ActiveSummaryPageSize = 200;
    private const int MaximumActiveSummaryPages = 6;
    private readonly List<EngineControlSet> engineControls = [];
    private EngineViewStatus? currentEngine;
    private long engineTransitionVersion;
    private int mountedEngineRefreshInFlight;

    private FrameworkElement CreateEngineStatusItem()
    {
        var root = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var status = ActionButton("Engine checking…", "ResourcePause.EngineStatusToggle");
        status.MinHeight = 24; status.Padding = new Thickness(8, 2, 8, 2);
        SetTogglePresentation(status, new("Engine checking…", ResourceSignal.Unknown,
            "Unknown: waiting for the Worker to confirm engine status.", false));
        var switching = false;
        status.Click += async (_, _) =>
        {
            if (CallbacksRetired || switching) return;
            switching = true; status.IsEnabled = false;
            try
            {
                var observed = await ReadEngineAsync(lifetime.Token); EnsureHealthyEngine(observed);
                var slot = observed.Active!.Slot == "A" ? "B" : "A";
                var alternate = observed.Slots.SingleOrDefault(item => item.Slot == slot);
                if (alternate is not { Available: true, BuildAllowed: true, Package: not null, Error: null })
                {
                    ApplyEngine(observed); ShowStatusBalloon(status, "Engine " + slot + " is unavailable."); return;
                }
                await SwitchEngineAfterDrainAsync(slot, lifetime.Token, resumeAfterSwitch: true);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (ResourceTransitionException) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            { SetEngineAttention("Engine switch could not be started: " + error.Message); }
            finally { switching = false; }
        };
        root.Children.Add(status);
        AttachStatusRefresh(root, RefreshMountedEngineAsync);
        TrackEngine(new(status, null, null, null, null, null));
        _ = RefreshMountedEngineAsync(lifetime.Token);
        return root;
    }

    private FrameworkElement CreateEnginePanel()
    {
        var root = PanelRoot();
        root.Children.Add(Heading("Engine"));
        root.Children.Add(Explanation("The engine chooses and validates workflow actions. Pause is durable and takes effect at a safe engine boundary; it does not cancel work or mark an uncertain effect complete. Switching first holds the engine, waits only for the exact work observed running after that hold, then activates the selected A/B slot and leaves it paused for review."));
        var summary = StatusText("Checking engine state…", "ResourcePause.EngineSummary");
        summary.Margin = new Thickness(0, 0, 0, 10);
        var selector = EngineSelector("ResourcePause.EngineSlot");
        selector.MinWidth = 220; selector.Margin = new Thickness(0, 0, 0, 8);
        var pauseResume = ActionButton("Pause engine", "ResourcePause.EnginePauseResume");
        var change = ActionButton("Switch after current work", "ResourcePause.EngineSwitch");
        var rollback = ActionButton("Rollback candidate", "ResourcePause.EngineRollback");
        var refresh = ActionButton("Refresh engine", "ResourcePause.EngineRefresh");
        pauseResume.Click += async (_, _) => await RunUiAsync(() => ToggleEngineAsync(lifetime.Token));
        change.Click += async (_, _) =>
        {
            if (selector.SelectedValue is string slot) await RunUiAsync(() => SwitchEngineAfterDrainAsync(slot, lifetime.Token));
        };
        rollback.Click += async (_, _) => await RunUiAsync(() => RollbackEngineAsync(lifetime.Token));
        refresh.Click += async (_, _) => await RunUiAsync(() => RefreshEngineAsync(lifetime.Token));
        var row = new WrapPanel(); row.Children.Add(pauseResume); row.Children.Add(change); row.Children.Add(rollback); row.Children.Add(refresh);
        root.Children.Add(selector); root.Children.Add(row); root.Children.Add(summary);
        TrackEngine(new(null, selector, change, pauseResume, rollback, summary));
        _ = RunUiAsync(() => RefreshEngineAsync(lifetime.Token));
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static ComboBox EngineSelector(string automationId)
    {
        var selector = new ComboBox
        {
            DisplayMemberPath = nameof(EngineSlotChoice.Display),
            SelectedValuePath = nameof(EngineSlotChoice.Slot),
            IsEnabled = false
        };
        AutomationProperties.SetAutomationId(selector, automationId);
        return selector;
    }

    private async Task<EngineViewStatus> RefreshEngineAsync(CancellationToken cancellationToken)
    {
        var version = Volatile.Read(ref engineTransitionVersion);
        try
        {
            var status = await ReadEngineAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if ((version & 1) == 0 && version == Volatile.Read(ref engineTransitionVersion))
                ApplyEngine(status);
            return status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if ((version & 1) == 0 && version == Volatile.Read(ref engineTransitionVersion))
                SetEngineAttention("Engine status could not be confirmed: " + error.Message);
            throw new ResourceTransitionException(error.Message, error);
        }
    }

    private async Task RefreshMountedEngineAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref mountedEngineRefreshInFlight, 1, 0) != 0) return;
        try { _ = await RefreshEngineAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ResourceTransitionException) { }
        finally { Volatile.Write(ref mountedEngineRefreshInFlight, 0); }
    }

    private Task<EngineViewStatus> ReadEngineAsync(CancellationToken cancellationToken) =>
        ServiceAsync<EngineViewStatus>("engine.status", new { }, cancellationToken);

    private async Task ToggleEngineAsync(CancellationToken cancellationToken)
    {
        await ownerActionGate.WaitAsync(cancellationToken);
        BeginEngineTransition();
        try
        {
            var before = await ReadEngineAsync(cancellationToken); EnsureHealthyEngine(before);
            SetEnginePending(before.Paused ? "Engine resuming…" : "Engine pausing…");
            var operation = before.Paused ? "engine.resume" : "engine.pause";
            var after = await ServiceAsync<EngineViewStatus>(operation, new { expectedEpoch = before.Epoch }, cancellationToken);
            EnsureHealthyEngine(after);
            if (after.Epoch != before.Epoch || after.Paused == before.Paused)
                throw new InvalidOperationException(operation + " did not confirm the requested state at the expected epoch.");
            ApplyEngine(after);
            ReportHostStatus(after.Paused ? "Engine paused at a safe boundary." : "Engine resumed.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SetEngineAttention("Engine transition was not confirmed: " + error.Message);
            throw new ResourceTransitionException(error.Message, error);
        }
        finally { EndEngineTransition(); ownerActionGate.Release(); }
    }

    private async Task SwitchEngineAfterDrainAsync(string slot, CancellationToken cancellationToken, bool resumeAfterSwitch = false)
    {
        if (slot is not ("A" or "B")) throw new ArgumentException("Choose engine slot A or B.", nameof(slot));
        await ownerActionGate.WaitAsync(cancellationToken);
        BeginEngineTransition();
        try
        {
            var engine = await ReadEngineAsync(cancellationToken); EnsureHealthyEngine(engine);
            if (resumeAfterSwitch)
            {
                var workHold = await ServiceAsync<IntentionalWorkHoldStatus>("engine.work-hold.status", new { }, cancellationToken);
                ValidateWorkHold(workHold);
                if (IsActiveWorkHold(workHold.State))
                    throw new InvalidOperationException("Resolve the captured work hold before switching and resuming the engine.");
                if (workHold.EngineEpoch != engine.Epoch)
                    throw new InvalidOperationException("Engine state changed while checking work-hold ownership. Refresh before switching.");
            }
            var candidate = engine.Slots.SingleOrDefault(item => item.Slot == slot)
                ?? throw new InvalidOperationException("The selected engine slot is unavailable.");
            if (!candidate.Available || !candidate.BuildAllowed || candidate.Package is null || candidate.Error is not null)
                throw new InvalidOperationException("The selected engine slot is not an available non-active activation candidate.");
            if (engine.Active!.Slot == slot) throw new InvalidOperationException("The selected engine slot is already active.");
            SetEnginePending("Holding engine admission…");
            if (!engine.Paused)
            {
                engine = await ServiceAsync<EngineViewStatus>("engine.pause", new { expectedEpoch = engine.Epoch }, cancellationToken);
                EnsureHeldEngine(engine);
            }
            var epoch = engine.Epoch;
            var tracked = await CaptureRunningWorkAsync(cancellationToken);
            if (tracked.Count > 0)
            {
                SetEnginePending($"Waiting for {tracked.Count} running item(s)…");
                for (var attempt = 0; attempt < 600; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var observed = await ReadEngineAsync(cancellationToken);
                    if (observed.Epoch != epoch || !observed.Paused)
                        throw new InvalidOperationException("Engine state changed while current work was draining. No activation was dispatched.");
                    var running = await CaptureRunningWorkAsync(cancellationToken);
                    tracked.IntersectWith(running);
                    if (tracked.Count == 0) { engine = observed; break; }
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    if (attempt == 599) throw new TimeoutException("Current work did not reach a non-running state within ten minutes. The engine remains paused.");
                }
            }
            engine = await ReadEngineAsync(cancellationToken);
            if (engine.Epoch != epoch || !engine.Paused || engine.ActiveEvaluations != 0)
                throw new InvalidOperationException("The final quiescent engine barrier was not confirmed. No activation was dispatched.");
            EnsureHealthyEngine(engine);
            var activated = await ServiceAsync<EngineViewStatus>("engine.activate", new { slot, expectedEpoch = epoch }, cancellationToken);
            EnsureHeldEngine(activated);
            if (activated.Epoch <= epoch || activated.Active?.Slot != slot || !activated.CandidatePending)
                throw new InvalidOperationException("Engine activation returned without the expected candidate identity. Refresh before taking another action.");
            if (resumeAfterSwitch)
            {
                SetEnginePending($"Resuming engine {slot}…");
                var resumed = await ServiceAsync<EngineViewStatus>("engine.resume", new { expectedEpoch = activated.Epoch }, cancellationToken);
                EnsureHealthyEngine(resumed);
                if (resumed.Paused || resumed.Epoch < activated.Epoch || resumed.Active?.Slot != slot || resumed.Active.Id != activated.Active.Id)
                    throw new InvalidDataException("The alternate engine resume was not confirmed. Refresh before taking another action.");
                ApplyEngine(resumed);
                ReportHostStatus(resumed.CandidatePending ? $"Engine {slot} resumed; candidate verification is still pending." : $"Engine {slot} is confirmed ready.");
            }
            else
            {
                ApplyEngine(activated);
                ReportHostStatus($"Engine slot {slot} is active and paused for review. Resume it explicitly when ready.");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SetEngineAttention("Engine switch was not confirmed. The established engine hold was preserved when possible. " + error.Message);
            throw new ResourceTransitionException(error.Message, error);
        }
        finally { EndEngineTransition(); ownerActionGate.Release(); }
    }

    private async Task RollbackEngineAsync(CancellationToken cancellationToken)
    {
        await ownerActionGate.WaitAsync(cancellationToken);
        BeginEngineTransition();
        try
        {
            var before = await ReadEngineAsync(cancellationToken); EnsureHeldEngine(before);
            if (!before.CandidatePending) throw new InvalidOperationException("There is no pending engine candidate to roll back.");
            SetEnginePending("Rolling back engine candidate…");
            var after = await ServiceAsync<EngineViewStatus>("engine.rollback", new { expectedEpoch = before.Epoch }, cancellationToken);
            EnsureHeldEngine(after);
            if (after.Epoch <= before.Epoch || after.CandidatePending)
                throw new InvalidOperationException("Engine rollback did not confirm the known-good generation.");
            ApplyEngine(after); ReportHostStatus("Engine rolled back and remains paused.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SetEngineAttention("Engine rollback was not confirmed: " + error.Message);
            throw new ResourceTransitionException(error.Message, error);
        }
        finally { EndEngineTransition(); ownerActionGate.Release(); }
    }

    private void BeginEngineTransition() => Interlocked.Increment(ref engineTransitionVersion);
    private void EndEngineTransition() => Interlocked.Increment(ref engineTransitionVersion);

    private async Task<HashSet<string>> CaptureRunningWorkAsync(CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        long after = 0;
        for (var page = 0; page < MaximumActiveSummaryPages; page++)
        {
            var workflows = await ServiceAsync<WorkflowSummaryPage>("jobs.workflow.active-summaries",
                new { limit = ActiveSummaryPageSize, afterRow = after }, cancellationToken);
            if (workflows.Runs is null || workflows.Runs.Count > ActiveSummaryPageSize)
                throw new InvalidDataException("Workflow active summaries exceeded their page bound.");
            foreach (var run in workflows.Runs)
            {
                if (!ValidId(run.Id) || string.IsNullOrWhiteSpace(run.Status))
                    throw new InvalidDataException("Workflow active summaries contained an invalid identity or status.");
                if (run.Status is "running" or "cancelling") result.Add("workflow:" + run.Id);
            }
            if (workflows.NextAfterRow is null) break;
            if (workflows.NextAfterRow <= after || page == MaximumActiveSummaryPages - 1)
                throw new InvalidDataException("Workflow active summaries could not be bounded without omitting live work.");
            after = workflows.NextAfterRow.Value;
        }

        after = 0;
        for (var page = 0; page < MaximumActiveSummaryPages; page++)
        {
            var jobs = await ServiceAsync<LegacySummaryPage>("queue.active-summaries",
                new { limit = ActiveSummaryPageSize, afterRow = after }, cancellationToken);
            if (jobs.Jobs is null || jobs.Jobs.Count > ActiveSummaryPageSize)
                throw new InvalidDataException("Legacy active summaries exceeded their page bound.");
            foreach (var job in jobs.Jobs)
            {
                if (!ValidId(job.Id) || string.IsNullOrWhiteSpace(job.Status))
                    throw new InvalidDataException("Legacy active summaries contained an invalid identity or status.");
                if (job.Status == "running") result.Add("legacy:" + job.Id);
            }
            if (jobs.NextAfterRow is null) break;
            if (jobs.NextAfterRow <= after || page == MaximumActiveSummaryPages - 1)
                throw new InvalidDataException("Legacy active summaries could not be bounded without omitting live work.");
            after = jobs.NextAfterRow.Value;
        }
        return result;
    }

    private static void EnsureHealthyEngine(EngineViewStatus status)
    {
        if (status.Epoch < 1 || status.Active is null || !ValidId(status.Active.Id) || status.Active.Slot is not ("A" or "B") ||
            status.Slots is null || status.Slots.Count is < 1 or > 2 || status.Slots.Select(slot => slot.Slot).Distinct(StringComparer.Ordinal).Count() != status.Slots.Count ||
            status.Slots.Any(slot => slot.Slot is not ("A" or "B")) || !string.IsNullOrWhiteSpace(status.Error))
            throw new InvalidDataException("The Worker did not report a healthy, identifiable engine generation.");
    }

    private static void EnsureHeldEngine(EngineViewStatus status)
    {
        EnsureHealthyEngine(status);
        if (!status.Paused) throw new InvalidOperationException("The Worker did not confirm the durable engine hold.");
    }

    private void ApplyEngine(EngineViewStatus status)
    {
        if (CallbacksRetired) return;
        EnsureHealthyEngine(status); currentEngine = status;
        engineControls.RemoveAll(control => !control.IsAlive);
        var choices = status.Slots.Where(slot => slot.Available && slot.BuildAllowed && slot.Package is not null && slot.Error is null && slot.Slot != status.Active!.Slot)
            .Select(slot => new EngineSlotChoice(slot.Slot, slot.Package!.Id)).ToArray();
        var presentation = status.CandidatePending
            ? new ResourcePresentation($"Engine {status.Active!.Slot} review", ResourceSignal.Pending,
                $"Pending: engine {status.Active.Id} in slot {status.Active.Slot} is active but unconfirmed at epoch {status.Epoch}. Resume to evaluate it or rollback while paused.", true)
            : status.Paused
                ? new($"Engine {status.Active!.Slot} paused", ResourceSignal.Pending,
                    $"Paused: engine {status.Active.Id} is waiting at a held boundary at epoch {status.Epoch}.", true)
                : new ResourcePresentation($"Engine {status.Active!.Slot} active", ResourceSignal.Ready,
                    $"Ready: engine {status.Active.Id} is active at epoch {status.Epoch}.", true);
        foreach (var control in engineControls)
        {
            if (control.Status is { } button) SetTogglePresentation(button, presentation);
            if (control.Summary is { } text) text.Text = presentation.Detail;
            if (control.Selector is { } selector)
            {
                var selected = selector.SelectedValue as string;
                selector.ItemsSource = choices;
                selector.SelectedValue = choices.Any(choice => choice.Slot == selected) ? selected : choices.FirstOrDefault()?.Slot;
                selector.IsEnabled = choices.Length > 0;
            }
            if (control.Switch is { } change) change.IsEnabled = choices.Length > 0;
            if (control.PauseResume is { } pauseResume)
            {
                pauseResume.Content = status.Paused ? "Resume engine" : "Pause engine";
                pauseResume.IsEnabled = true;
            }
            if (control.Rollback is { } rollback) rollback.IsEnabled = status.Paused && status.CandidatePending;
        }
    }

    private void SetEnginePending(string label)
    {
        var presentation = new ResourcePresentation(label, ResourceSignal.Pending, "Pending: " + label, false);
        engineControls.RemoveAll(control => !control.IsAlive);
        foreach (var control in engineControls)
        {
            if (control.Status is { } button) SetTogglePresentation(button, presentation);
            if (control.Summary is { } text) text.Text = presentation.Detail;
            control.SetEnabled(false);
        }
    }

    private void SetEngineAttention(string detail)
    {
        if (CallbacksRetired) return;
        currentEngine = null;
        var presentation = new ResourcePresentation("Engine attention", ResourceSignal.Attention, "Needs attention: " + Bound(detail), true);
        engineControls.RemoveAll(control => !control.IsAlive);
        foreach (var control in engineControls)
        {
            if (control.Status is { } button) SetTogglePresentation(button, presentation);
            if (control.Summary is { } text) text.Text = presentation.Detail;
            control.SetEnabled(false);
        }
        ReportHostStatus(Bound(detail));
    }

    private void TrackEngine(EngineControlSet control)
    {
        engineControls.RemoveAll(item => !item.IsAlive);
        engineControls.Add(control);
    }

    private void DisposeExtendedState()
    {
        DisposeStatusRefresh();
        currentEngine = null;
        engineControls.Clear();
        DisposeWorkHoldState();
    }

    private sealed record EngineSlotChoice(string Slot, string Id) { public string Display => $"Slot {Slot} · {Id}"; }
    private sealed class EngineViewStatus
    {
        public bool Paused { get; init; }
        public long Epoch { get; init; }
        public int ActiveEvaluations { get; init; }
        public bool CandidatePending { get; init; }
        public EnginePackage? Active { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<EngineSlot> Slots { get; init; } = [];
    }
    private sealed class EnginePackage { public string Slot { get; init; } = ""; public string Id { get; init; } = ""; }
    private sealed class EngineSlot
    {
        public string Slot { get; init; } = "";
        public bool Available { get; init; }
        public bool BuildAllowed { get; init; }
        public EnginePackage? Package { get; init; }
        public string? Error { get; init; }
    }
    private sealed class WorkflowSummaryPage
    {
        public IReadOnlyList<WorkflowSummary> Runs { get; init; } = [];
        public long? NextAfterRow { get; init; }
    }
    private sealed class WorkflowSummary { public string Id { get; init; } = ""; public string Status { get; init; } = ""; }
    private sealed class LegacySummaryPage
    {
        public IReadOnlyList<LegacySummary> Jobs { get; init; } = [];
        public long? NextAfterRow { get; init; }
    }
    private sealed class LegacySummary { public string Id { get; init; } = ""; public string Status { get; init; } = ""; }

    private sealed class EngineControlSet(Button? status, ComboBox? selector, Button? change, Button? pauseResume,
        Button? rollback, TextBlock? summary)
    {
        private readonly WeakReference<Button>? statusView = status is null ? null : new(status);
        private readonly WeakReference<ComboBox>? selectorView = selector is null ? null : new(selector);
        private readonly WeakReference<Button>? switchView = change is null ? null : new(change);
        private readonly WeakReference<Button>? pauseResumeView = pauseResume is null ? null : new(pauseResume);
        private readonly WeakReference<Button>? rollbackView = rollback is null ? null : new(rollback);
        private readonly WeakReference<TextBlock>? summaryView = summary is null ? null : new(summary);
        public Button? Status => Target(statusView); public ComboBox? Selector => Target(selectorView);
        public Button? Switch => Target(switchView); public Button? PauseResume => Target(pauseResumeView);
        public Button? Rollback => Target(rollbackView); public TextBlock? Summary => Target(summaryView);
        public bool IsAlive => Status is not null || Selector is not null || Switch is not null || PauseResume is not null || Rollback is not null || Summary is not null;
        public void SetEnabled(bool enabled)
        {
            if (Selector is { } selector) selector.IsEnabled = enabled;
            if (Switch is { } change) change.IsEnabled = enabled;
            if (PauseResume is { } pauseResume) pauseResume.IsEnabled = enabled;
            if (Rollback is { } rollback) rollback.IsEnabled = enabled;
        }
        private static T? Target<T>(WeakReference<T>? reference) where T : class =>
            reference is not null && reference.TryGetTarget(out var value) ? value : null;
    }
}
