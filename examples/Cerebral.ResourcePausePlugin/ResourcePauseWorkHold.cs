using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ResourcePausePlugin;

public sealed partial class ResourcePausePlugin
{
    private const int MaximumHeldWork = 200;
    private readonly List<WorkHoldControlSet> workHoldControls = [];
    private readonly SemaphoreSlim workHoldRefreshGate = new(1, 1);
    private long workHoldTransitionVersion;
    private int mountedWorkHoldRefreshInFlight;

    private FrameworkElement CreateWorkHoldStatusItem()
    {
        var status = ActionButton("Work hold checking…", "ResourcePause.WorkHoldStatus");
        status.MinHeight = 24;
        status.Padding = new Thickness(8, 2, 8, 2);
        SetTogglePresentation(status, new("Work hold checking…", ResourceSignal.Unknown,
            "Unknown: waiting for the Worker to confirm intentional work-hold state.", true));
        var resolving = false;
        status.Click += async (_, _) =>
        {
            if (CallbacksRetired || resolving) return;
            resolving = true;
            try
            {
                await RunUiAsync(async () =>
                {
                    var observed = await RefreshWorkHoldAsync(lifetime.Token);
                    if (IsActiveWorkHold(observed.State))
                    {
                        if (observed.StartupPromptRequired || observed.Work.Any(item => item.RequiresReview))
                            ShowStatusBalloon(status, "Review captured work in Work hold settings before resuming.");
                        else if (observed.State == "held" && !observed.Work.Any(item => item.IsExecuting))
                            await ResolveWorkHoldAsync(cancel: false, lifetime.Token);
                        else ShowStatusBalloon(status, "Captured work is still settling. The hold remains in place.");
                    }
                    else await CreateWorkHoldAsync("stop-now", lifetime.Token);
                });
            }
            finally { resolving = false; }
        };
        TrackWorkHold(new(status, null, null, null, null, null));
        AttachStatusRefresh(status, RefreshMountedWorkHoldAsync);
        _ = RefreshMountedWorkHoldAsync(lifetime.Token);
        return status;
    }

    private FrameworkElement CreateWorkHoldPanel()
    {
        var root = PanelRoot();
        root.Children.Add(Heading("Work hold"));
        root.Children.Add(Explanation("Stop now durably pauses engine admission and interrupts only the work captured by the Worker; uncertain effects remain reviewable and are never replayed automatically. Finish current work waits for the current dispatched workflow operation or legacy assignment before holding. Startup actions appear only for this explicit saved hold, never for unrelated historical Waiting runs. Ordinary Resume engine cannot release this marker; use Resume held work with its exact identity."));
        var summary = StatusText("Checking intentional work-hold state…", "ResourcePause.WorkHoldSummary");
        summary.Margin = new Thickness(0, 0, 0, 10);
        var stop = ActionButton("Stop now", "ResourcePause.WorkHoldStopNow");
        var finish = ActionButton("Finish current work, then hold", "ResourcePause.WorkHoldFinishCurrent");
        var resume = ActionButton("Resume held work", "ResourcePause.WorkHoldResume");
        var cancel = ActionButton("Cancel captured work", "ResourcePause.WorkHoldCancel");
        var refresh = ActionButton("Refresh hold", "ResourcePause.WorkHoldRefresh");
        stop.Click += async (_, _) => await RunUiAsync(() => CreateWorkHoldAsync("stop-now", lifetime.Token));
        finish.Click += async (_, _) => await RunUiAsync(() => CreateWorkHoldAsync("finish-current", lifetime.Token));
        resume.Click += async (_, _) => await RunUiAsync(() => ResolveWorkHoldAsync(cancel: false, lifetime.Token));
        cancel.Click += async (_, _) => await RunUiAsync(() => ResolveWorkHoldAsync(cancel: true, lifetime.Token));
        refresh.Click += async (_, _) => await RunUiAsync(() => RefreshWorkHoldAsync(lifetime.Token));
        var create = new WrapPanel(); create.Children.Add(stop); create.Children.Add(finish);
        var resolve = new WrapPanel(); resolve.Children.Add(resume); resolve.Children.Add(cancel); resolve.Children.Add(refresh);
        root.Children.Add(create); root.Children.Add(resolve); root.Children.Add(summary);
        TrackWorkHold(new(null, summary, stop, finish, resume, cancel));
        var view = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AttachStatusRefresh(view, RefreshMountedWorkHoldAsync);
        _ = RefreshMountedWorkHoldAsync(lifetime.Token);
        return view;
    }

    private async Task<IntentionalWorkHoldStatus> RefreshWorkHoldAsync(CancellationToken cancellationToken)
    {
        await workHoldRefreshGate.WaitAsync(cancellationToken);
        var version = Volatile.Read(ref workHoldTransitionVersion);
        try
        {
            var status = await ServiceAsync<IntentionalWorkHoldStatus>("engine.work-hold.status", new { }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateWorkHold(status);
            // A delayed observation must not replace the result of a newer local owner transition.
            if ((version & 1) == 0 && version == Volatile.Read(ref workHoldTransitionVersion))
                ApplyWorkHold(status);
            return status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if ((version & 1) == 0 && version == Volatile.Read(ref workHoldTransitionVersion))
                SetWorkHoldAttention("Intentional work-hold status could not be confirmed: " + error.Message);
            throw new ResourceTransitionException(error.Message, error);
        }
        finally { workHoldRefreshGate.Release(); }
    }

    private async Task RefreshMountedWorkHoldAsync(CancellationToken cancellationToken)
    {
        if (CallbacksRetired || (Volatile.Read(ref workHoldTransitionVersion) & 1) != 0 ||
            Interlocked.CompareExchange(ref mountedWorkHoldRefreshInFlight, 1, 0) != 0) return;
        try { _ = await RefreshWorkHoldAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ResourceTransitionException) { }
        finally { Volatile.Write(ref mountedWorkHoldRefreshInFlight, 0); }
    }

    private async Task CreateWorkHoldAsync(string mode, CancellationToken cancellationToken)
    {
        if (mode is not ("stop-now" or "finish-current")) throw new ArgumentException("Unknown work-hold mode.", nameof(mode));
        await ownerActionGate.WaitAsync(cancellationToken);
        Interlocked.Increment(ref workHoldTransitionVersion);
        try
        {
            var before = await ServiceAsync<IntentionalWorkHoldStatus>("engine.work-hold.status", new { }, cancellationToken);
            ValidateWorkHold(before);
            if (IsActiveWorkHold(before.State))
                throw new InvalidOperationException("An intentional work hold already exists. Resolve that exact hold before creating another.");
            var requestId = Guid.NewGuid().ToString("N");
            SetWorkHoldPending(mode == "stop-now" ? "Stopping captured work…" : "Finishing current work…");
            var after = await ServiceAsync<IntentionalWorkHoldStatus>("engine.work-hold", new
            {
                requestId,
                mode,
                expectedEpoch = before.EngineEpoch,
                expectedGeneration = before.Generation,
                expectedRevision = before.Revision
            }, cancellationToken);
            ValidateWorkHold(after);
            if (!string.Equals(after.RequestId, requestId, StringComparison.Ordinal) ||
                !string.Equals(after.Mode, mode, StringComparison.Ordinal) || before.Generation == long.MaxValue ||
                after.Generation != before.Generation + 1 || after.Revision <= 0 || !IsActiveWorkHold(after.State))
                throw new InvalidDataException("The Worker returned a work hold that does not match the exact admitted request.");
            ApplyWorkHold(after);
            ReportHostStatus(after.Detail);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SetWorkHoldAttention("The intentional work hold was not confirmed. No request was replayed. " + error.Message);
            throw new ResourceTransitionException(error.Message, error);
        }
        finally { Interlocked.Increment(ref workHoldTransitionVersion); ownerActionGate.Release(); }
    }

    private async Task ResolveWorkHoldAsync(bool cancel, CancellationToken cancellationToken)
    {
        await ownerActionGate.WaitAsync(cancellationToken);
        Interlocked.Increment(ref workHoldTransitionVersion);
        try
        {
            var before = await ServiceAsync<IntentionalWorkHoldStatus>("engine.work-hold.status", new { }, cancellationToken);
            ValidateWorkHold(before);
            if (!IsActiveWorkHold(before.State) || before.HoldId is null)
                throw new InvalidOperationException("There is no active intentional work hold to resolve.");
            if (!cancel && before.Work.Any(item => item.IsExecuting))
                throw new InvalidOperationException("Captured work is still executing. Refresh until it reaches the held boundary before resuming.");
            SetWorkHoldPending(cancel ? "Cancelling captured work…" : "Resuming held work…");
            var operation = cancel ? "engine.work-hold.cancel" : "engine.work-hold.resume";
            var after = await ServiceAsync<IntentionalWorkHoldStatus>(operation, new
            {
                holdId = before.HoldId,
                expectedEpoch = before.EngineEpoch,
                expectedGeneration = before.Generation,
                expectedRevision = before.Revision
            }, cancellationToken);
            ValidateWorkHold(after);
            if (!string.Equals(after.HoldId, before.HoldId, StringComparison.Ordinal) ||
                after.Generation != before.Generation || after.Revision <= before.Revision ||
                cancel && after.State is not ("cancelling" or "cancelled" or "faulted") ||
                !cancel && after.State is not ("resuming" or "resumed" or "faulted"))
                throw new InvalidDataException("The Worker returned a work-hold resolution that does not match the exact held generation.");
            ApplyWorkHold(after);
            ReportHostStatus(after.Detail);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SetWorkHoldAttention("The intentional work hold was not resolved. No request was replayed. " + error.Message);
            throw new ResourceTransitionException(error.Message, error);
        }
        finally { Interlocked.Increment(ref workHoldTransitionVersion); ownerActionGate.Release(); }
    }

    private void ApplyWorkHold(IntentionalWorkHoldStatus status)
    {
        if (CallbacksRetired) return;
        workHoldControls.RemoveAll(control => !control.IsAlive);
        var presentation = WorkHoldPresentation(status);
        foreach (var control in workHoldControls)
        {
            if (control.Status is { } button) SetTogglePresentation(button, presentation);
            if (control.Summary is { } summary) summary.Text = presentation.Detail;
            control.Apply(status);
        }
    }

    private static ResourcePresentation WorkHoldPresentation(IntentionalWorkHoldStatus status)
    {
        if (status.State == "faulted")
        {
            var detail = string.IsNullOrWhiteSpace(status.Fault)
                ? status.Detail
                : status.Fault + " " + status.Detail;
            return new("Work hold attention", ResourceSignal.Attention, "Needs attention: " + Bound(detail), true);
        }
        if (status.State is "holding" or "resuming" or "cancelling")
            return new("Work hold transitioning…", ResourceSignal.Pending, "Pending: " + status.Detail, false);
        if (status.StartupPromptRequired)
            return new("Held work needs review", ResourceSignal.Pending,
                $"Pending owner decision: {status.Work.Count} captured item(s) remain under the saved intentional hold. {status.Detail}", true);
        if (status.State == "held")
            return new("Captured work held", ResourceSignal.Ready,
                $"Engine admission is held with {status.Work.Count} captured item(s). Confirm remote work and resource drain before shutdown. {status.Detail}", true);
        return new("Hold all work", ResourceSignal.Ready, "Pause new engine work and capture current work for a durable hold. This does not stop every process or remote provider.", true);
    }

    private void SetWorkHoldPending(string label)
    {
        var presentation = new ResourcePresentation(label, ResourceSignal.Pending, "Pending: " + label, false);
        workHoldControls.RemoveAll(control => !control.IsAlive);
        foreach (var control in workHoldControls)
        {
            if (control.Status is { } button) SetTogglePresentation(button, presentation);
            if (control.Summary is { } summary) summary.Text = presentation.Detail;
            control.SetEnabled(false);
        }
    }

    private void SetWorkHoldAttention(string detail)
    {
        if (CallbacksRetired) return;
        var presentation = new ResourcePresentation("Work hold attention", ResourceSignal.Attention,
            "Needs attention: " + Bound(detail), true);
        workHoldControls.RemoveAll(control => !control.IsAlive);
        foreach (var control in workHoldControls)
        {
            if (control.Status is { } button) SetTogglePresentation(button, presentation);
            if (control.Summary is { } summary) summary.Text = presentation.Detail;
            control.SetEnabled(false);
        }
        ReportHostStatus(Bound(detail));
    }

    private void TrackWorkHold(WorkHoldControlSet control)
    {
        workHoldControls.RemoveAll(item => !item.IsAlive);
        workHoldControls.Add(control);
    }

    private void DisposeWorkHoldState()
    {
        workHoldControls.Clear();
    }

    private static bool IsActiveWorkHold(string state) => state is "holding" or "held" or "resuming" or "cancelling" or "faulted";

    private static void ValidateWorkHold(IntentionalWorkHoldStatus status)
    {
        if (status.Contract != "cerebral.owner-work-hold/1" || status.Generation < 0 || status.Revision < 0 || status.EngineEpoch < 0 ||
            status.State is not ("none" or "holding" or "held" or "resuming" or "cancelling" or "resumed" or "cancelled" or "faulted") ||
            status.Mode is not null and not ("stop-now" or "finish-current") || status.Work is null || status.Work.Count > MaximumHeldWork ||
            string.IsNullOrWhiteSpace(status.Detail) || status.Detail.Length > 700 || status.Fault?.Length > 700)
            throw new InvalidDataException("The Worker returned an invalid intentional work-hold status.");
        if (IsActiveWorkHold(status.State) && (!Guid.TryParseExact(status.HoldId, "N", out _) ||
            !Guid.TryParseExact(status.RequestId, "N", out _) || status.Mode is null))
            throw new InvalidDataException("The active work hold is missing its exact owner identity.");
        if (status.StartupPromptRequired && (!IsActiveWorkHold(status.State) || status.Work.Count == 0))
            throw new InvalidDataException("The startup work-hold prompt is not backed by captured work.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in status.Work)
        {
            if (item.Kind is not ("workflow" or "job") || !ValidHeldText(item.Id) || !ValidHeldText(item.ConversationId) ||
                !ValidHeldText(item.Status) || item.Revision is < 0 || item.EffectState?.Length > 128 ||
                !identities.Add(item.Kind + "\0" + item.Id))
                throw new InvalidDataException("The work-hold projection contains invalid or duplicate captured work.");
        }
    }

    private static bool ValidHeldText(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Contains('\0');

    private sealed class IntentionalWorkHoldStatus
    {
        public string Contract { get; init; } = "";
        public string? HoldId { get; init; }
        public string? RequestId { get; init; }
        public long Generation { get; init; }
        public long Revision { get; init; }
        public string? Mode { get; init; }
        public string State { get; init; } = "";
        public long EngineEpoch { get; init; }
        public bool EnginePaused { get; init; }
        public bool StartupPromptRequired { get; init; }
        public IReadOnlyList<IntentionalHeldWorkStatus> Work { get; init; } = [];
        public string? Fault { get; init; }
        public string Detail { get; init; } = "";
    }

    private sealed class IntentionalHeldWorkStatus
    {
        public string Kind { get; init; } = "";
        public string Id { get; init; } = "";
        public string ConversationId { get; init; } = "";
        public string Status { get; init; } = "";
        public long? Revision { get; init; }
        public string? EffectState { get; init; }
        public bool IsExecuting { get; init; }
        public bool RequiresReview { get; init; }
    }

    private sealed class WorkHoldControlSet(Button? status, TextBlock? summary, Button? stop, Button? finish, Button? resume, Button? cancel)
    {
        private readonly WeakReference<Button>? statusView = status is null ? null : new(status);
        private readonly WeakReference<TextBlock>? summaryView = summary is null ? null : new(summary);
        private readonly WeakReference<Button>? stopView = stop is null ? null : new(stop);
        private readonly WeakReference<Button>? finishView = finish is null ? null : new(finish);
        private readonly WeakReference<Button>? resumeView = resume is null ? null : new(resume);
        private readonly WeakReference<Button>? cancelView = cancel is null ? null : new(cancel);
        public Button? Status => Target(statusView); public TextBlock? Summary => Target(summaryView);
        public Button? Stop => Target(stopView); public Button? Finish => Target(finishView);
        public Button? Resume => Target(resumeView); public Button? Cancel => Target(cancelView);
        public bool IsAlive => Status is not null || Summary is not null || Stop is not null || Finish is not null || Resume is not null || Cancel is not null;
        public void Apply(IntentionalWorkHoldStatus status)
        {
            var active = IsActiveWorkHold(status.State);
            if (Stop is { } stop) stop.IsEnabled = !active;
            if (Finish is { } finish) finish.IsEnabled = !active;
            if (Resume is { } resume) resume.IsEnabled = (status.State is "held" or "faulted") && status.Work.All(item => !item.IsExecuting);
            if (Cancel is { } cancel) cancel.IsEnabled = active && status.State != "cancelling";
        }
        public void SetEnabled(bool enabled)
        {
            if (Stop is { } stop) stop.IsEnabled = enabled;
            if (Finish is { } finish) finish.IsEnabled = enabled;
            if (Resume is { } resume) resume.IsEnabled = enabled;
            if (Cancel is { } cancel) cancel.IsEnabled = enabled;
        }
        private static T? Target<T>(WeakReference<T>? reference) where T : class =>
            reference is not null && reference.TryGetTarget(out var value) ? value : null;
    }
}
