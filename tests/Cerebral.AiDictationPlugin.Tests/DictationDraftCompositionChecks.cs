using Cerebral.AiDictationPlugin;
using Cerebral.Desktop.Plugins;

internal static partial class Program
{
    // Deterministic source-only checks until the coherent SDK/plugin batch is admitted.
    private static async Task<int> RunDraftCompositionChecksAsync()
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        var target = new DraftCompositionFixture("Keep. Tail", 5);
        var grammarCalls = 0;
        var composition = new DictationDraftComposition(() => target.Current, target.Write,
            (words, _) => { grammarCalls++; Check(words == "one phrase", "Only final dictated prose reaches correction."); return Task.FromResult("One phrase."); });
        await composition.ApplyAsync(1, "won", false, default);
        Check(target.Current.Draft.Text == "Keep. won Tail" && target.Current.Start == 9 && target.Writes == 1 && grammarCalls == 0,
            "A genuine partial inserts at the captured cursor with spacing, outside text preserved and no grammar call.");
        await composition.ApplyAsync(2, "one phrase", false, default);
        Check(target.Current.Draft.Text == "Keep. one phrase Tail" && target.Writes == 2,
            "A revised hypothesis replaces only its prior tentative span without duplicating it.");
        await composition.ApplyAsync(3, "one phrase", true, default);
        Check(target.Current.Draft.Text == "Keep. One phrase. Tail" && grammarCalls == 1 && composition.PendingText.Length == 0,
            "Configured correction applies once on the protected final phrase.");
        var next = new DraftCompositionFixture("Keep.", 5);
        var sequential = new DictationDraftComposition(() => next.Current, next.Write);
        await sequential.ApplyAsync(1, "first", false, default);
        await sequential.ApplyAsync(2, "first", true, default);
        Check(next.Writes == 1, "An unchanged final has no synthetic write, revision or fade.");
        await sequential.ApplyAsync(3, "second", false, default);
        Check(next.Current.Draft.Text == "Keep. first second" && next.Writes == 2,
            "The next utterance begins after the confirmed final and preserves it.");
        Check((await sequential.ApplyAsync(3, "second", true, default)).State == DesktopOperationState.Rejected && next.Writes == 2,
            "An old/replayed event cannot insert another copy.");
        var selected = new DraftCompositionFixture("Original", 0, 8);
        var selection = new DictationDraftComposition(() => selected.Current, selected.Write);
        await selection.ApplyAsync(1, "add", false, default);
        await selection.ApplyAsync(2, "", true, default);
        Check(selected.Current.Draft.Text == "Original", "Selection and an empty final preserve original user text while retracting only tentative words.");
        foreach (var conflict in new[] { "edit", "caret", "revision", "focus" })
        {
            var changed = new DraftCompositionFixture("Keep.", 5);
            var guarded = new DictationDraftComposition(() => changed.Current, changed.Write);
            await guarded.ApplyAsync(1, "partial", false, default);
            changed.Current = conflict switch
            {
                "edit" => changed.Current with { Draft = changed.Current.Draft with { Text = "User edit", Revision = 3 } },
                "caret" => changed.Current with { Start = 0 },
                "revision" => changed.Current with { Draft = changed.Current.Draft with { Revision = 4 } },
                _ => changed.Current with { Available = false }
            };
            var before = changed.Current;
            Check((await guarded.ApplyAsync(2, "final", true, default)).State == DesktopOperationState.Rejected &&
                changed.Current == before && changed.Writes == 1 && guarded.PendingText == "final",
                "A user " + conflict + " invalidates composition, preserving current text and pending words.");
        }
        var unknown = new DraftCompositionFixture("Keep.", 5) { BadConfirmation = true };
        var uncertain = new DictationDraftComposition(() => unknown.Current, unknown.Write);
        Check((await uncertain.ApplyAsync(1, "maybe", false, default)).State == DesktopOperationState.Uncertain && unknown.Writes == 1,
            "Completed without exact caret/revision/text confirmation becomes uncertain.");
        await uncertain.ApplyAsync(2, "maybe", true, default);
        Check(unknown.Writes == 1 && uncertain.PendingText == "maybe", "An uncertain write cannot be retried by a later recognition event.");
        var correcting = new DraftCompositionFixture("Keep.", 5);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new DictationDraftComposition(() => correcting.Current, correcting.Write, (_, _) => { entered.SetResult(); return finish.Task; });
        var correction = delayed.ApplyAsync(1, "final", true, default);
        await entered.Task; correcting.Current = correcting.Current with { Start = 0 }; finish.SetResult("Final.");
        Check((await correction).State == DesktopOperationState.Rejected && correcting.Writes == 0,
            "Cursor changes during final correction are rechecked before dispatch.");
        return checks;
    }

    private sealed class DraftCompositionFixture(string text, int start, int length = 0)
    {
        internal DictationDraftObservation Current = new(new(new("composition-fixture", null), 1, text), start, length);
        internal int Writes;
        internal bool BadConfirmation;
        internal Task<DesktopOperationResult> Write(DesktopDraftUpdate update, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Writes++;
            if (update.Presentation is not { } presentation || Current.Draft.Binding != update.Binding || Current.Draft.Revision != update.ExpectedRevision ||
                Current.Start != presentation.ExpectedSelection.Start || Current.Length != presentation.ExpectedSelection.Length)
                return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "Fixture CAS conflict."));
            Current = new(new(Current.Draft.Binding, Current.Draft.Revision + 1, update.Text), BadConfirmation ? 0 : presentation.Selection.Start, presentation.Selection.Length);
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        }
    }
}
