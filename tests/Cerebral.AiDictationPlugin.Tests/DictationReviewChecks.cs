using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.AiDictationPlugin;
using Cerebral.Desktop.Plugins;
using Cerebral.Dictation;
using Cerebral.Core;

internal static partial class Program
{
    private static async Task<int> RunReviewChecksAsync(Dispatcher dispatcher, string? previewPath = null)
    {
        var checks = await RunDestinationChecksAsync(dispatcher) + await RunDirectDictationChecksAsync(dispatcher) + await RunManualDictationChecksAsync(dispatcher) + await RunDraftCompositionChecksAsync() + await RunResidentDictationChecksAsync(dispatcher);
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        foreach (var sample in new[]
        {
            ("Hello.", "Next sentence", "Hello. Next sentence"), ("Really?", "Yes.", "Really? Yes."),
            ("Done!", "Another", "Done! Another"), ("Hello. ", " Next", "Hello. Next"),
            ("Hello.\n", "Next", "Hello.\nNext"), ("Hello", ", there", "Hello, there"),
            ("(", "value)", "(value)"), ("Keep", "  ", "Keep")
        }) Check(DictatedTextInsertion.JoinSegments(sample.Item1, sample.Item2) == sample.Item3, "Segment joining: " + sample.Item3);

        using (var target = new NativeEntry(dispatcher))
        {
            target.Box.Text = "Keep this."; target.Box.SelectAll();
            var review = new DictationReview(); review.ChooseTarget(target);
            review.Append(target, DictatedTextInsertion.Capture(target), "Next sentence.");
            review.Append(target, DictatedTextInsertion.Capture(target), "Another?");
            Check(target.Text == "Keep this." && review.Text == "Next sentence. Another?", "Recognition and pauses only update plugin-local review.");
            review.Edit("Reviewed sentence. Another?");
            Check((await review.AcceptAsync(CancellationToken.None)).State == DesktopOperationState.Completed &&
                target.Text == "Keep this. Reviewed sentence. Another?" && !review.HasText,
                "Explicit Accept preserves selected original text and inserts edited review once.");
            review.ChooseTarget(target); review.Append(target, DictatedTextInsertion.Capture(target), "Must wait.");
            target.Box.Text += " Manual edit";
            var conflict = await review.AcceptAsync(CancellationToken.None);
            Check(conflict.State == DesktopOperationState.Rejected && review.HasText && target.Text.EndsWith(" Manual edit", StringComparison.Ordinal),
                "Destination edits reject insertion while preserving both the destination and transcript.");
            review.Clear(); target.Box.Text = "Line one\n"; target.Box.Select(target.Box.Text.Length, 0);
            review.ChooseTarget(target); review.Append(target, DictatedTextInsertion.Capture(target), "Line two"); review.Edit("\nEdited paragraph\n");
            await review.AcceptAsync(CancellationToken.None);
            Check(target.Text == "Line one\n\nEdited paragraph\n", "Reviewed line breaks are preserved without globally formatting destination text.");
        }

        var initial = new WindowsTextTargetSnapshot("chosen-identity", "Chosen fixture app", "Keep.", 5, 0);
        var native = new ReviewNativeTarget(initial);
        using (var chosen = new WindowsDictationTarget(native, initial))
        {
            var review = new DictationReview(); review.ChooseTarget(chosen);
            review.Append(chosen, DictatedTextInsertion.Capture(chosen), "Next sentence");
            Check((await review.AcceptAsync(CancellationToken.None)).State == DesktopOperationState.Completed && native.Snapshot.Text == "Keep. Next sentence" && native.Writes == 1,
                "Fake external destination receives only the explicitly accepted reviewed segment.");
            review.ChooseTarget(chosen); review.Append(chosen, DictatedTextInsertion.Capture(chosen), "Later.");
            native.Snapshot = native.Snapshot with { Text = native.Snapshot.Text + " Manual", Start = native.Snapshot.Text.Length + 7 };
            Check((await review.AcceptAsync(CancellationToken.None)).State == DesktopOperationState.Rejected && native.Writes == 1 && review.HasText,
                "External content/selection changes are refused before any write; lease never follows another focus.");
        }
        foreach (var failure in new[] { "identity changed", "password/protected target", "elevated or foreign user", "closed target" })
        {
            var blocked = new ReviewNativeTarget(initial) { ReadFailure = failure };
            using var chosen = new WindowsDictationTarget(blocked, initial);
            Check((await chosen.InsertAsync(" rejected")).State == DesktopOperationState.Rejected && blocked.Writes == 0,
                "Fake native admission failure never reaches insertion: " + failure);
        }
        var unconfirmed = new ReviewNativeTarget(initial) { WriteFailure = true };
        using (var chosen = new WindowsDictationTarget(unconfirmed, initial))
        {
            var review = new DictationReview(); review.ChooseTarget(chosen); review.Append(chosen, DictatedTextInsertion.Capture(chosen), "Maybe.");
            Check((await review.AcceptAsync(CancellationToken.None)).State == DesktopOperationState.Uncertain && review.InsertionUnconfirmed,
                "A lost external write receipt is unconfirmed and retains review.");
            await review.AcceptAsync(CancellationToken.None);
            Check(unconfirmed.Writes == 1 && review.HasText, "Unconfirmed insertion cannot be replayed by another Accept.");
        }

        var root = Path.Combine(Path.GetTempPath(), "cerebral-dictation-review-" + Guid.NewGuid().ToString("N"));
        var host = new TestHost { DataDirectory = root, Surface = DesktopPluginSurface.Orchestrator };
        try
        {
            using var target = new NativeEntry(dispatcher); target.Box.Text = "Existing."; target.Box.Select(9, 0);
            var capture = new FakeCapture(); var provider = new FakeProvider();
            using var session = new AiDictationSession(host, root, () => new(), _ => provider, capture,
                action => dispatcher.InvokeAsync(action).Task.Unwrap(), (_, _) => { }, reviewBeforeInsert: true);
            session.Review.ChooseTarget(target);
            await session.ToggleAsync(target, CancellationToken.None);
            await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check((await session.PrepareSubmissionAsync(target, CancellationToken.None)).State == DesktopOperationState.Rejected,
                "Send preparation never flushes an active capture or submits unreviewed text.");
            session.FinishCurrent(); await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            provider.Result.SetResult(Transcript("Next sentence.")); await session.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!session.IsActive && session.Review.Text == "Next sentence." && target.Text == "Existing.",
                "Stop finishes fake capture and transcribes locally into review, leaving the destination untouched.");
            Check((await session.PrepareSubmissionAsync(target, CancellationToken.None)).State == DesktopOperationState.Rejected,
                "A ready review also requires explicit Accept or Cancel before an internal Send.");
            await session.AcceptReviewAsync(CancellationToken.None);
            Check(target.Text == "Existing. Next sentence.", "Session Accept uses the captured target and punctuation boundary.");
            session.Review.ChooseTarget(target); session.Review.Append(target, DictatedTextInsertion.Capture(target), "Discard me.");
            session.Cancel(discardReview: true);
            Check(!session.Review.HasText && target.Text == "Existing. Next sentence.", "Cancel discards only unaccepted dictation, never earlier destination text.");

            foreach (var enabled in new[] { false, true })
            {
                var savedHost = new TestHost { DataDirectory = root, Surface = DesktopPluginSurface.Orchestrator };
                await savedHost.ExecuteServiceCommandAsync(CompleteSettingsContract.PluginSaveOperation,
                    new PluginSettingsTransferSaveRequest(new(AiDictationSettingsPolicy.Document(new() { AutoTranscribeOnSilence = enabled }), 0, OwnerValidated: true)));
                var quiet = new FakeCapture(); var providerCalls = 0;
                using var plugin = new AiDictationPlugin(quiet, _ => { providerCalls++; throw new InvalidOperationException("No launch capture is permitted."); });
                plugin.Initialize(savedHost); savedHost.Focus(target); savedHost.Focus(null); savedHost.Focus(target);
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                Check(plugin.ReadRuntimePreferences().AutoTranscribeOnSilence == enabled && !quiet.Started.Task.IsCompleted && providerCalls == 0,
                    "Saved Auto is remembered without capture/provider activation on launch or later focus changes.");
                var compact = (DictationMicrophoneView)plugin.MountContributions.OfType<DesktopMountView>().Single().CreateView();
                Check(((Panel)compact.Child).Children.OfType<CheckBox>().Count() == 0, "Main/status controls contain no permanent Auto checkbox.");
                compact.Update(new(DictationMicrophoneMode.Off, false, true, true, "Fixture", "", null) { Docked = true });
                Check(AutomationProperties.GetName(LogicalButtons(compact).Single(button => AutomationProperties.GetAutomationId(button) == "AiDictation.Microphone")) == "Start recording",
                    "Docked one-off action is explicitly Start while idle.");
                compact.Update(new(DictationMicrophoneMode.Listening, false, true, true, "Fixture", "", null) { Docked = true, RecordingRequested = true });
                Check(AutomationProperties.GetName(LogicalButtons(compact).Single(button => AutomationProperties.GetAutomationId(button) == "AiDictation.Microphone")) == "Stop recording" &&
                    LogicalButtons(compact).Count() == 2 && LogicalButtons(compact).Any(button => AutomationProperties.GetAutomationId(button) == "AiDictation.OpenControls"),
                    "Docked capture has automatic Start/Stop and an explicit one-off popout, with no status Insert action.");
                typeof(AiDictationPlugin).GetMethod("LockTarget", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, [target]);
                using var other = new NativeEntry(dispatcher); savedHost.Focus(other);
                Check(ReferenceEquals(typeof(AiDictationPlugin).GetField("lockedTarget", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(plugin), target) &&
                    !quiet.Started.Task.IsCompleted, "A different focused editor neither changes the chosen destination nor starts capture.");
                savedHost.Focus(null);
                typeof(AiDictationPlugin).GetMethod("ChooseCerebralTarget", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, []);
                Check(ReferenceEquals(typeof(AiDictationPlugin).GetField("lockedTarget", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(plugin), other),
                    "Only the explicit choose-editor action selects the last Cerebral editor, even after opening plugin review/settings.");
            }
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("cerebral-dictation-review-", StringComparison.Ordinal))
                throw new IOException("Unsafe dictation fixture cleanup path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }

        var grammarHost = new ReviewGrammarHost();
        var profiles = await DictationGrammar.ProfilesAsync(grammarHost, CancellationToken.None);
        Check(profiles.Count == 1 && profiles[0].Id == "grammar-existing", "Grammar choices include only an existing exact-purpose local helper profile.");
        var settings = new AiDictationSettings { GrammarCorrection = true, GrammarProfileId = profiles[0].Id, GrammarProfileRevision = profiles[0].Revision };
        var corrected = await DictationGrammar.CorrectAsync(grammarHost, settings, "Pegasus use WidgetAPI 12.", CancellationToken.None);
        Check(corrected.Text == "Pegasus uses WidgetAPI 12." && grammarHost.Requests.Single().Text == "Pegasus use WidgetAPI 12." &&
            grammarHost.Requests.Single().Purpose == "dictation.grammar", "Grammar gets dictated prose only under its dedicated exact profile/purpose; no target contents or history.");
        grammarHost.Output = "Pegasus uses WidgetAPI 13.";
        Check((await DictationGrammar.CorrectAsync(grammarHost, settings, "Pegasus use WidgetAPI 12.", CancellationToken.None)).Text == "Pegasus use WidgetAPI 12.",
            "Grammar cannot silently alter protected names, technical literals or numbers.");
        Check((await DictationGrammar.CorrectAsync(grammarHost, settings with { GrammarProfileId = "", GrammarProfileRevision = 0 }, "Original words", CancellationToken.None)).Text == "Original words",
            "Unavailable grammar retains recognized text without another provider or model fallback.");
        var windowPreferences = settings with { DockControls = true, AutoTranscribeOnSilence = true, AllowExternalTarget = true };
        Check(AiDictationSettingsPolicy.Decode(AiDictationSettingsPolicy.Encode(windowPreferences)) == windowPreferences,
            "Window options and exact grammar choice round-trip through plugin preferences.");
        var imported = new AiDictationSettingsPolicy().AdmitComplete(AiDictationSettingsPolicy.Document(windowPreferences),
            AiDictationSettingsPolicy.Encode(new()));
        Check(imported.Accepted && !AiDictationSettingsPolicy.Decode(imported.Values).AllowExternalTarget &&
            AiDictationSettingsPolicy.Decode(imported.Values).GrammarProfileId.Length == 0,
            "A settings transfer cannot enable Windows input permission or choose another machine's AI profile.");

        var starts = 0; var stops = 0; var cancels = 0; var accepts = 0;
        var view = new DictationReviewView(() => { starts++; return Task.CompletedTask; }, () => stops++, () => cancels++,
            () => { accepts++; return Task.CompletedTask; }, _ => { }, _ => throw new InvalidOperationException("No real target picking in tests."),
            () => { }, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
            () => Task.FromResult<IReadOnlyList<DictationGrammarProfile>>([]), _ => Task.CompletedTask, () => Task.CompletedTask);
        view.Update(new(DictationMicrophoneMode.Off, true, true, true, "Explicit destination", "", null), settings, "Editable review", true);
        view.Measure(new Size(DictationReviewView.WindowWidth, DictationReviewView.WindowHeight)); view.Arrange(new Rect(0, 0, DictationReviewView.WindowWidth, DictationReviewView.WindowHeight)); view.UpdateLayout();
        var footer = ((Panel)view.Child).Children.OfType<TextBlock>().First();
        Check(footer.TranslatePoint(new Point(0, footer.ActualHeight), view).Y <= DictationReviewView.WindowHeight,
            "The simplified control header and phrase guidance fit inside the compact window.");
        Check(starts == 0 && cancels == 0 && accepts == 0 && view.ReviewEditor.Text == "Editable review" && !view.ReviewEditor.IsReadOnly,
            "Offscreen review presentation never starts capture or accepts automatically.");
        var buttons = LogicalButtons(view).ToArray();
        Check(buttons.Count(button => AutomationProperties.GetName(button) == "Record one clip") == 1 &&
            buttons.Any(button => AutomationProperties.GetName(button) == "Play recording") && view.RequiredHeight == DictationReviewView.WindowHeight,
            "Floating one-off controls expose Record/Stop, Play and review/Insert without starting them on render.");
        view.ShowRecovery();
        Check(view.RequiredHeight == DictationReviewView.RecoveryWindowHeight,
            "One-off transcript review remains directly accessible.");
        buttons.Single(button => AutomationProperties.GetName(button) == "Insert retained phrase into chosen text box").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(accepts == 1 && starts == 0, "Explicit acceptance invokes only insertion review, never Start or Send.");
        view.Update(new(DictationMicrophoneMode.Listening, true, true, true, "Captured destination", "", null) { RecordingRequested = true }, settings, "", false);
        buttons.Single(button => AutomationProperties.GetName(button) == "Stop recording").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(stops == 1 && starts == 0 && accepts == 1 && view.RequiredHeight == DictationReviewView.WindowHeight,
            "The one-off Record action becomes Stop without inserting automatically.");
        view.Update(new(DictationMicrophoneMode.Off, true, true, true, "Fixture destination", "", null), settings, "", false);
        if (previewPath is not null)
        {
            view.Measure(new Size(DictationReviewView.WindowWidth, DictationReviewView.WindowHeight)); view.Arrange(new Rect(0, 0, DictationReviewView.WindowWidth, DictationReviewView.WindowHeight)); view.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)DictationReviewView.WindowWidth, (int)DictationReviewView.WindowHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var image = new FileStream(previewPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); encoder.Save(image);
        }
        return checks;
    }

    private static IEnumerable<Button> LogicalButtons(DependencyObject root)
    {
        if (root is Button button) yield return button;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in LogicalButtons(child)) yield return item;
    }

    private sealed class ReviewNativeTarget(WindowsTextTargetSnapshot snapshot) : IChosenWindowsTextTarget
    {
        public string Identity => Snapshot.Identity;
        internal WindowsTextTargetSnapshot Snapshot = snapshot;
        internal string? ReadFailure;
        internal bool WriteFailure;
        internal int Writes;
        public WindowsTextTargetSnapshot Read(CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (ReadFailure is { } failure) throw new InvalidOperationException(failure); return Snapshot; }
        public void ReplaceSelection(WindowsTextTargetSnapshot expected, string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Writes++;
            if (WriteFailure) throw new IOException("Simulated missing acknowledgement.");
            Snapshot = Snapshot with { Text = expected.Text[..expected.Start] + text + expected.Text[(expected.Start + expected.Length)..], Start = expected.Start + text.Length, Length = 0 };
        }
    }

    private sealed class ReviewGrammarHost : IDesktopPluginHost, IDesktopServiceHost, IDesktopIsolatedTextGenerationHost
    {
        internal readonly List<DesktopIsolatedTextRequest> Requests = [];
        internal string Output = "Pegasus uses WidgetAPI 12.";
        public DesktopConversationSnapshot GetConversation() => new(new("fixture", null), "Fixture", null, null, false);
        public DesktopDraftSnapshot GetDraft() => new(new("fixture", null), 0, "UNRELATED TARGET MUST NEVER BE SENT");
        public event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged { add { } remove { } }
        public event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted { add { } remove { } }
        public Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate value, CancellationToken token = default) => throw new InvalidOperationException();
        public Task<DesktopOperationResult> SubmitAsync(DesktopSubmission value, CancellationToken token = default) => throw new InvalidOperationException("Dictation must never submit.");
        public Task<DesktopOperationResult> SteerAsync(DesktopSubmission value, CancellationToken token = default) => throw new InvalidOperationException();
        public Task<DesktopOperationResult> StopAsync(DesktopConversationBinding value, CancellationToken token = default) => throw new InvalidOperationException();
        public void DismissInputGuidance() { }
        public void ReportStatus(string text) { }
        public void RefreshPresentation() { }
        public Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation, object arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopServiceCommandResult(true, CerebralJson.Element(new[]
            { new { id = "grammar-existing", revision = 3, name = "Grammar", purpose = "dictation.grammar", role = "helper", workerId = "local", modelWorkerId = "local", modelId = "existing" },
              new { id = "autoprompt", revision = 2, name = "Other", purpose = "autoprompt", role = "helper", workerId = "local", modelWorkerId = "local", modelId = "existing" } })));
        public Task<DesktopIsolatedTextAvailability> GetIsolatedTextAvailabilityAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DesktopIsolatedTextAvailability(true));
        public Task<DesktopIsolatedTextResult> GenerateIsolatedTextAsync(DesktopIsolatedTextRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return Task.FromResult(new DesktopIsolatedTextResult(DesktopOperationState.Completed, Output)); }
    }
}
