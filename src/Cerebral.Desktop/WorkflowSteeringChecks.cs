using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Actual unshown Work controls with an in-memory owner transport. No Worker, model, connection or HWND.</summary>
internal static class WorkflowSteeringChecks
{
    private const string First = "guidance-first";
    private const string Second = "guidance-second";
    private const string Definition = "fixture.guidance:work";
    private const string Version = "2.0.0";
    private const string ExactText = "  Preserve the accepted goal.\r\nUse this additional owner guidance — unchanged.  ";

    internal static async Task<int> VerifyAsync()
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("A WPF dispatcher is required.");
        dispatcher.VerifyAccess();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Work guidance: " + label);
            checks++;
        }
        async Task Refuses(Func<Task> action, string label)
        {
            var refused = false;
            try { await action().WaitAsync(token); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or InvalidDataException)
            { refused = true; }
            Check(refused, label);
        }
        void RefusesAction(Action action, string label)
        {
            var refused = false;
            try { action(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException) { refused = true; }
            Check(refused, label);
        }
        async Task Send(Fixture fixture) => await fixture.Workspace.SendWorkGuidanceAsync().WaitAsync(token);
        async Task Open(Fixture fixture, string id = First) => _ = await fixture.Workspace.OpenRunSnapshotAsync(id, token);
        static TaskCompletionSource<CommandResult> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        using (var f = new Fixture())
        {
            await Open(f);
            Check(f.Host.Visibility == Visibility.Visible && f.Input.Text.Length == 0,
                "standalone Work exposes its separate empty guidance host");
            Check(AutomationProperties.GetName(f.Input).Length > 0 && f.Status.Text.Length > 0,
                "the actual guidance input has an accessible name and actionable initial status");
            var original = CerebralJson.Element(f.Runs[First].Submission).GetRawText();
            f.Input.Text = ExactText;
            Check(f.Send.IsEnabled && f.Clear.IsEnabled, "a valid draft for the selected waiting run enables both explicit actions");
            await Send(f);
            var sent = f.Steers.Single();
            Check(sent.ProfileId is null && sent.Operation == "jobs.workflow.steer",
                "guidance uses the generic owner route without impersonating the run profile");
            Check(sent.Arguments.GetProperty("runId").GetString() == First &&
                sent.Arguments.GetProperty("text").GetString() == ExactText &&
                !string.IsNullOrWhiteSpace(sent.Arguments.GetProperty("requestId").GetString()),
                "the exact captured run, new request ID and original text reach the owner adapter");
            Check(f.Descriptions == 1 && f.Calls.Single(x => x.Operation == "sdk.describe").Arguments.GetProperty("id").GetString() == Definition,
                "the first send reads the selected definition and uses its exact advertised version rather than the false decoy edition");
            Check(f.Accepted.Count == 1 && f.Workspace.Snapshot?.Run?.Status == WorkflowStatus.Waiting &&
                f.Calls.All(x => x.Operation is not ("jobs.workflow.resume" or "jobs.workflow.submit" or "jobs.workflow.answer")),
                "accepting guidance neither resumes waiting work nor submits or answers another request");
            Check(f.Input.Text.Length == 0 && !Locked(f.Input) && f.Status.Text.Contains("accepted", StringComparison.OrdinalIgnoreCase),
                "only a matching accepted receipt clears the draft and leaves a visible acceptance status");
            Check(CerebralJson.Element(f.Runs[First].Submission).GetRawText() == original &&
                f.Runs[First].Steering.Count == 0 && f.Calls.All(x => x.ProfileId is null),
                "the UI does not rewrite accepted input or manufacture run steering state from its receipt");

            // Exercise the routed button as well as the awaitable handler used by the other cases.
            f.Input.Text = "Button guidance";
            f.Send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Drain(dispatcher, token);
            Check(f.Steers.Count == 2 && f.Accepted.Count == 2 && f.Input.Text.Length == 0,
                "the real Send guidance button follows the same accepted owner-command path");
        }

        using (var f = new Fixture())
        {
            await Open(f); f.Input.Text = ExactText;
            await Open(f, Second);
            Check(f.Input.Text == ExactText && Locked(f.Input) && !f.Send.IsEnabled &&
                f.Workspace.Snapshot?.Run?.Id == Second,
                "selecting another run retains the unsent draft on its original target");
            await Refuses(() => Send(f), "the actual handler refuses to retarget an old draft");
            Check(f.Steers.Count == 0 && f.Input.Text == ExactText, "retarget refusal dispatches nothing and preserves all draft text");
            f.Clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(f.Input.Text.Length == 0 && !Locked(f.Input), "explicit Clear draft creates a new draft for the current selection");
            f.Input.Text = "Second run only"; await Send(f);
            Check(f.Steers.Single().Arguments.GetProperty("runId").GetString() == Second,
                "only an explicitly cleared and rewritten draft can target the second run");
        }

        using (var f = new Fixture())
        {
            await Open(f); f.Input.Text = ExactText;
            var describe = Gate();
            f.Override = (request, cancellation) => request.Operation == "sdk.describe" ? describe.Task.WaitAsync(cancellation) : null;
            var pending = Send(f);
            Check(f.Workspace.MutationPending && Locked(f.Input) && !f.Send.IsEnabled && !f.Clear.IsEnabled,
                "capability lookup is part of the pending mutation and locks the captured draft");
            await Refuses(() => Send(f), "a duplicate handler call cannot overlap a pending guidance send");
            await Refuses(() => Open(f, Second), "selection cannot change during the pending description/send");
            RefusesAction(f.Workspace.Reset, "reset cannot detach a pending guidance request");
            RefusesAction(f.Workspace.ClearWorkGuidanceDraft, "clear cannot discard the pending draft");
            Check(f.Steers.Count == 0 && f.Descriptions == 1, "pending fences do not issue another description or an early mutation");
            describe.SetResult(f.Description());
            await pending;
            Check(f.Steers.Single().Arguments.GetProperty("runId").GetString() == First &&
                f.Steers.Single().Arguments.GetProperty("text").GetString() == ExactText,
                "the draft and target captured before the asynchronous lookup remain exact");
        }

        using (var f = new Fixture())
        {
            await Open(f); f.Input.Text = ExactText;
            var read = Gate();
            f.Override = (request, cancellation) => request.Operation == "jobs.workflow.get" &&
                request.Arguments.GetProperty("id").GetString() == Second ? read.Task.WaitAsync(cancellation) : null;
            var selecting = Open(f, Second);
            await Refuses(() => Send(f), "an in-flight run selection prevents sending to the previously displayed run");
            Check(f.Steers.Count == 0 && f.Input.Text == ExactText, "a selection-read race cannot consume or redirect the draft");
            f.Override = null; read.SetResult(CommandResult.Ok(f.Runs[Second]));
            await selecting;
            Check(f.Workspace.Snapshot?.Run?.Id == Second && f.Input.Text == ExactText && !f.Send.IsEnabled,
                "the completed selection leaves the old draft visibly bound rather than moving it");
        }

        using (var f = new Fixture())
        {
            await Open(f);
            var read = Gate();
            f.Override = (request, cancellation) => request.Operation == "jobs.workflow.get" &&
                request.Arguments.GetProperty("id").GetString() == Second ? read.Task.WaitAsync(cancellation) : null;
            var staleSelection = Open(f, Second);
            f.Workspace.Reset();
            f.Override = null;
            await Open(f);
            f.Input.Text = "A fresh selection after reset";
            Check(f.Send.IsEnabled, "reset clears a superseded selection-read fence for newly opened work");
            await Send(f);
            read.SetResult(CommandResult.Ok(f.Runs[Second]));
            await Refuses(() => staleSelection, "a late pre-reset read cannot adopt its obsolete selection");
            Check(f.Workspace.Snapshot?.Run?.Id == First &&
                f.Steers.Single().Arguments.GetProperty("runId").GetString() == First,
                "new guidance remains bound to the post-reset selection after the old read completes");
        }

        using (var f = new Fixture { RejectNext = true })
        {
            await Open(f); f.Input.Text = ExactText;
            await Refuses(() => Send(f), "a confirmed owner refusal reaches the actual handler");
            Check(f.Accepted.Count == 0 && f.Input.Text == ExactText && !Locked(f.Input) && f.Clear.IsEnabled,
                "a confirmed refusal keeps an editable draft without claiming uncertainty or acceptance");
            Check(f.Status.Text.Contains("not accepted", StringComparison.OrdinalIgnoreCase), "the owner refusal has visible actionable status");
            await Send(f);
            Check(f.Accepted.Count == 1 && f.Steers.Count == 2, "the owner may explicitly send the retained draft after a confirmed refusal");
        }

        using (var f = new Fixture { LoseAcknowledgement = true })
        {
            await Open(f); f.Input.Text = ExactText;
            await Refuses(() => Send(f), "a lost acknowledgement is surfaced without an automatic replay");
            var original = f.Steers.Single().Arguments.GetRawText();
            Check(f.Accepted.Count == 1 && f.Steers.Count == 1 && f.Input.Text == ExactText &&
                f.Workspace.MutationPending && Locked(f.Input) && f.Send.Content?.ToString() == "Retry guidance",
                "an accepted-but-unacknowledged request retains its exact locked draft and explicit retry action");
            await f.Workspace.InspectRunAsync(First, token);
            Check(f.Steers.Count == 1 && f.Input.Text == ExactText && Locked(f.Input) && f.Workspace.MutationPending,
                "refresh reads saved work without retrying or clearing control uncertainty");
            var callsBeforeRefusedActions = f.Calls.Count;
            await Refuses(() => Open(f, Second), "uncertainty blocks selecting a different run");
            RefusesAction(f.Workspace.Reset, "uncertainty blocks replacing the connection binding");
            RefusesAction(f.Workspace.ClearWorkGuidanceDraft, "uncertainty blocks discarding the exact retained guidance");
            await Refuses(() => f.Workspace.SubmitNewWorkflowAsync(new("other-guidance-work", "fixture-profile",
                Definition, Version, "Different work", WorkflowContract.EmptyObject), token),
                "uncertainty blocks starting a different workflow mutation");
            Check(f.Calls.Count == callsBeforeRefusedActions && f.Steers.Count == 1 && f.Accepted.Count == 1,
                "refused navigation and mutation actions reach no transport and do not redispatch guidance");

            // Recovery of a known RID remains possible after the target completes or its package stops advertising support.
            f.Runs[First] = f.Runs[First] with { Status = WorkflowStatus.Completed, Revision = 8 };
            f.Advertised = false;
            await f.Workspace.InspectRunAsync(First, token);
            Check(f.Send.IsEnabled && f.Send.Content?.ToString() == "Retry guidance",
                "a terminal refresh retains recovery of the already dispatched request, without enabling new guidance");
            await Send(f);
            Check(f.Steers.Count == 2 && f.Steers[1].Arguments.GetRawText() == original &&
                f.Accepted.Count == 1 && f.Descriptions == 1,
                "explicit recovery reuses the exact RID/run/text and accepted receipt without another capability lookup");
            Check(f.Input.Text.Length == 0 && !f.Send.IsEnabled && !f.Workspace.MutationPending &&
                f.Workspace.Snapshot?.Run?.Status == WorkflowStatus.Completed,
                "resolved terminal recovery clears only the accepted draft and keeps new guidance disabled");
        }

        using (var f = new Fixture { Advertised = false })
        {
            await Open(f); f.Input.Text = ExactText;
            await Refuses(() => Send(f), "an exact edition advertising false refuses new guidance");
            Check(f.Steers.Count == 0 && f.Input.Text == ExactText && !f.Send.IsEnabled,
                "advertised unsupported preserves the draft without calling the backend mutation");
        }
        foreach (var unavailable in new[] { false, true })
        {
            using var f = new Fixture { Advertised = null, DescriptionUnavailable = unavailable };
            await Open(f); f.Input.Text = ExactText; await Send(f);
            Check(f.Steers.Count == 1 && f.Accepted.Count == 1,
                unavailable ? "an unsupported description operation leaves actual owner admission authoritative" :
                    "an absent optional capability flag leaves actual owner admission authoritative");
        }
        using (var f = new Fixture())
        {
            f.Runs[First] = f.Runs[First] with { Status = WorkflowStatus.Failed };
            await Open(f); f.Input.Text = ExactText;
            await Refuses(() => Send(f), "a terminal run cannot receive a new guidance request");
            Check(f.Steers.Count == 0 && f.Descriptions == 0, "terminal refusal happens before capability or mutation dispatch");
        }

        using (var f = new Fixture())
        {
            await Open(f);
            foreach (var invalid in new[] { " \r\n\t", "unsafe\0text", "\uD800", new string('\u00e9', 4096) + "x" })
            {
                f.Input.Text = invalid;
                await Refuses(() => Send(f), "invalid whitespace, NUL, Unicode or UTF-8 overflow is refused");
                Check(f.Steers.Count == 0 && f.Input.Text == invalid && !f.Send.IsEnabled,
                    "invalid guidance is preserved and never reaches the mutation adapter");
            }
            var boundary = new string('\u00e9', 4096);
            f.Input.Text = boundary; await Send(f);
            Check(f.Steers.Single().Arguments.GetProperty("text").GetString() == boundary,
                "exactly8192 UTF-8 bytes are accepted without truncation or UTF-16 length substitution");
        }

        foreach (var wrong in new[] { "run", "request", "malformed" })
        {
            using var f = new Fixture();
            await Open(f); f.Input.Text = ExactText;
            f.Override = (request, _) =>
            {
                if (request.Operation != "jobs.workflow.steer") return null;
                var rid = request.Arguments.GetProperty("requestId").GetString()!;
                return Task.FromResult(CommandResult.Ok(wrong == "malformed" ? (object)new { unexpected = true } :
                    new WorkflowSteeringReceipt(wrong == "request" ? "wrong-guidance-request" : rid,
                        wrong == "run" ? Second : First, 1, DateTimeOffset.UnixEpoch)));
            };
            await Refuses(() => Send(f), "a malformed or mismatched steering receipt cannot acknowledge the draft");
            var original = f.Steers.Single().Arguments.GetRawText();
            Check(f.Input.Text == ExactText && Locked(f.Input) && f.Send.Content?.ToString() == "Retry guidance",
                "an unverifiable receipt remains uncertain rather than falsely clearing the request");
            f.Override = null; await Send(f);
            Check(f.Steers.Count == 2 && f.Steers[1].Arguments.GetRawText() == original && f.Accepted.Count == 1,
                "receipt recovery retries the exact original request instead of allocating another identity");
        }

        using (var f = new Fixture(embedded: true))
        {
            await Open(f);
            Check(f.Host.Visibility == Visibility.Collapsed, "embedded Chat hides the Work-only guidance host");
            f.Input.Text = ExactText;
            await Refuses(() => Send(f), "the hidden handler also refuses embedded Chat invocation");
            Check(f.Steers.Count == 0 && f.Descriptions == 0 && f.Workspace.Snapshot?.Run?.UserText == "ORIGINAL REQUEST",
                "embedded refusal neither calls owner guidance nor changes the Chat request");
        }
        using (var f = new Fixture())
        {
            await Open(f); f.Input.Text = ExactText;
            f.Workspace.Dispose();
            await Refuses(() => Send(f), "a disposed workspace cannot dispatch guidance");
            Check(f.Steers.Count == 0 && f.Descriptions == 0, "disposal is checked before read or mutation transport");
        }
        using (var f = new Fixture())
        {
            await Open(f); f.Input.Text = ExactText;
            var read = Gate();
            f.Override = (request, cancellation) => request.Operation == "sdk.describe" ? read.Task.WaitAsync(cancellation) : null;
            var pending = Send(f);
            f.Workspace.Dispose();
            var cancelled = false;
            try { await pending; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && f.Steers.Count == 0, "disposing an in-flight description cancels it before steering can escape");
            read.TrySetResult(f.Description());
            await Drain(dispatcher, token);
            Check(f.Steers.Count == 0, "a late description completion cannot revive a disposed guidance action");
        }

        Console.WriteLine(JsonSerializer.Serialize(new { workflowSteeringChecks = checks,
            evidence = "deterministic actual unshown WPF Work controls and generic owner client with in-memory command delegates",
            serviceCommandsExecuted = false, modelCalls = 0, networkUsed = false, windowShown = false }));
        return checks;
    }

    /// <summary>Optional, separate shown-window fixture image. The caller chooses an unused absolute PNG path.</summary>
    internal static async Task RenderPreviewAsync(string absolutePngPath)
    {
        if (!Path.IsPathFullyQualified(absolutePngPath) ||
            !Path.GetExtension(absolutePngPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An explicit absolute PNG path is required.", nameof(absolutePngPath));
        var path = Path.GetFullPath(absolutePngPath);
        var application = Application.Current ?? throw new InvalidOperationException("A WPF Application is required.");
        application.Dispatcher.VerifyAccess();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var fixture = new Fixture { AllowPreviewReads = true };
        await fixture.Workspace.OpenRunSnapshotAsync(First, deadline.Token);
        fixture.Input.Text = "Keep the accepted goal and existing behavior.\r\nCheck the current result before the next supported step.";
        var savedMainWindow = application.MainWindow;
        var savedShutdownMode = application.ShutdownMode;
        var processMode = RenderOptions.ProcessRenderMode;
        var window = new Window
        {
            Title = "Work guidance — isolated fixture", Content = fixture.Workspace,
            Width = 1000, Height = 720, Background = Ui.Surface, Foreground = Ui.Ink,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000
        };
        var cleanupErrors = new List<Exception>();
        Exception? primary = null;
        var shown = false;
        void Cleanup(Action action)
        {
            try { action(); } catch (Exception error) { cleanupErrors.Add(error); }
        }
        try
        {
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Show(); shown = true;
            await Drain(application.Dispatcher, deadline.Token);
            window.UpdateLayout();
            var content = fixture.Workspace;
            if (PresentationSource.FromVisual(content) is not HwndSource || !window.IsVisible ||
                window.ShowActivated || window.ShowInTaskbar || fixture.Input.ActualHeight <= 0 ||
                fixture.Host.ActualHeight <= 0 || !fixture.Send.IsEnabled || fixture.Steers.Count != 0)
                throw new InvalidOperationException("The isolated shown Work fixture is not ready for a native-content preview.");
            var bounds = fixture.Host.TransformToAncestor(content).TransformBounds(new Rect(new Point(), fixture.Host.RenderSize));
            if (bounds.Left < -1 || bounds.Top < -1 || bounds.Right > content.ActualWidth + 1 || bounds.Bottom > content.ActualHeight + 1)
                throw new InvalidOperationException("The guidance controls are clipped in the shown preview fixture.");
            // Render(content) retains its layout offset; include both margins rather than clipping the footer.
            var margin = content.Margin;
            var captureWidth = content.ActualWidth + margin.Left + margin.Right;
            var captureHeight = content.ActualHeight + margin.Top + margin.Bottom;
            var statusBounds = fixture.Status.TransformToAncestor(content).TransformBounds(new Rect(new Point(), fixture.Status.RenderSize));
            statusBounds.Offset(margin.Left, margin.Top);
            if (fixture.Status.ActualWidth <= 0 || fixture.Status.ActualHeight <= 0 ||
                statusBounds.Left < 0 || statusBounds.Top < 0 ||
                statusBounds.Right > captureWidth || statusBounds.Bottom > captureHeight)
                throw new InvalidOperationException("The complete guidance status does not fit the preview capture bounds.");
            var dpi = VisualTreeHelper.GetDpi(content);
            var width = checked((int)Math.Ceiling(captureWidth * dpi.DpiScaleX));
            var height = checked((int)Math.Ceiling(captureHeight * dpi.DpiScaleY));
            if (width is < 1 or > 8192 || height is < 1 or > 8192)
                throw new InvalidOperationException("The shown preview exceeds its bounded capture dimensions.");
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            encoder.Save(output);
            if (RenderOptions.ProcessRenderMode != processMode)
                throw new InvalidOperationException("The fixture changed process rendering policy.");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            Cleanup(window.Close);
            Cleanup(() => application.MainWindow = savedMainWindow);
            Cleanup(() => application.ShutdownMode = savedShutdownMode);
        }
        if (primary is not null)
        {
            if (cleanupErrors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
            cleanupErrors.Insert(0, primary);
        }
        if (cleanupErrors.Count > 0) throw new AggregateException("Work guidance preview or cleanup failed.", cleanupErrors);
        Console.WriteLine(JsonSerializer.Serialize(new { workflowSteeringPreview = path,
            evidence = "one task-owned shown native WPF fixture rendered to PNG with controlled in-memory transport",
            shownWindows = shown ? 1 : 0, serviceCommandsExecuted = false, modelCalls = 0, networkUsed = false,
            steeringMutations = fixture.Steers.Count, processRenderModeChanged = false, actualAttachedUiAcceptance = false }));
    }

    private static bool Locked(TextBox input) => input.IsReadOnly || !input.IsEnabled;

    private static async Task Drain(Dispatcher dispatcher, CancellationToken token)
    {
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Dictionary<string, WorkflowRun> Runs = new(StringComparer.Ordinal);
        internal readonly List<CommandRequest> Calls = [];
        internal readonly Dictionary<string, WorkflowSteeringReceipt> Accepted = new(StringComparer.Ordinal);
        internal readonly WorkflowWorkspace Workspace;
        internal Func<CommandRequest, CancellationToken, Task<CommandResult>?>? Override;
        internal bool? Advertised = true;
        internal bool DescriptionUnavailable;
        internal bool AllowPreviewReads;
        internal bool RejectNext;
        internal bool LoseAcknowledgement;
        internal List<CommandRequest> Steers => Calls.Where(x => x.Operation == "jobs.workflow.steer").ToList();
        internal int Descriptions => Calls.Count(x => x.Operation == "sdk.describe");
        internal TextBox Input => Control<TextBox>("WorkflowGuidanceInput");
        internal Button Send => Control<Button>("WorkflowSendGuidance");
        internal Button Clear => Control<Button>("WorkflowClearGuidance");
        internal TextBlock Status => Control<TextBlock>("WorkflowGuidanceStatus");
        internal Border Host => Control<Border>("WorkflowGuidanceHost");

        internal Fixture(bool embedded = false)
        {
            foreach (var id in new[] { First, Second })
                Runs.Add(id, new WorkflowRun { Id = id, Revision = 7, Status = WorkflowStatus.Waiting,
                    WaitingReason = "Waiting for an explicit owner action.",
                    Submission = new(id, id + "-conversation", "fixture-profile", Definition, Version, "ORIGINAL REQUEST",
                        "PRIVATE ASSIGNMENT", WorkflowContract.EmptyObject, WorkflowContract.EmptyObject),
                    CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch });
            Workspace = new WorkflowWorkspace(Dispatch, () => [], (_, _) => { }, embedded);
        }

        internal CommandResult Description() => DescriptionUnavailable ? CommandResult.Fail("Fixture description operation unavailable.") :
            CommandResult.Ok(new { contract = WorkflowContract.Id, version = WorkflowContract.Version, definitions = new object[]
            {
                new { id = Definition, version = "1.0.0", supportsSteering = false },
                Advertised is { } supported ? new { id = Definition, version = Version, supportsSteering = supported } :
                    (object)new { id = Definition, version = Version }
            } });

        private Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(request);
            if (Override?.Invoke(request, token) is { } overridden) return overridden;
            if (request.Operation == "jobs.workflow.get")
                return Task.FromResult(CommandResult.Ok(Runs[request.Arguments.GetProperty("id").GetString()!]));
            if (request.Operation == "jobs.workflow.events.previous")
                return Task.FromResult(CommandResult.Ok(new { events = Array.Empty<CerebralEvent>(), snapshotSequence = 0L, nextBeforeSequence = (long?)null }));
            if (request.Operation == "jobs.workflow.events")
                return Task.FromResult(CommandResult.Ok(new { events = Array.Empty<CerebralEvent>(), snapshotSequence = 0L, nextAfterSequence = (long?)null }));
            if (request.Operation == "sdk.describe") return Task.FromResult(Description());
            if (AllowPreviewReads && request.Operation == "jobs.workflow.recent")
                return Task.FromResult(CommandResult.Ok(new { runs = Array.Empty<object>(), snapshotRow = 0L, nextBeforeRow = (long?)null }));
            if (request.Operation != "jobs.workflow.steer")
                throw new NotSupportedException("Unexpected steering fixture command: " + request.Operation);
            if (RejectNext) { RejectNext = false; return Task.FromResult(CommandResult.Fail("Fixture owner refused this guidance.")); }
            var argument = request.Arguments;
            var rid = argument.GetProperty("requestId").GetString()!;
            var id = argument.GetProperty("runId").GetString()!;
            if (!Accepted.TryGetValue(rid, out var receipt))
            {
                receipt = new(rid, id, Accepted.Count + 1, DateTimeOffset.UnixEpoch);
                Accepted.Add(rid, receipt);
            }
            if (LoseAcknowledgement)
            {
                LoseAcknowledgement = false;
                throw new IOException("Fixture accepted guidance but lost its acknowledgement.");
            }
            return Task.FromResult(CommandResult.Ok(receipt));
        }

        private T Control<T>(string id) where T : DependencyObject => Descendants(Workspace).OfType<T>()
            .Single(item => AutomationProperties.GetAutomationId(item) == id);

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var descendant in Descendants(child)) yield return descendant;
        }

        public void Dispose() => Workspace.Dispose();
    }
}
