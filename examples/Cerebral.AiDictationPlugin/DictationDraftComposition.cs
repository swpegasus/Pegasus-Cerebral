using Cerebral.Desktop.Plugins;
using Cerebral.Dictation;

namespace Cerebral.AiDictationPlugin;

// A read includes the exact current Main target/focus/availability, never a UIA target.
internal sealed record DictationDraftObservation(DesktopDraftSnapshot Draft, int Start, int Length, bool Available = true);

/// <summary>Replaces only one real recognition hypothesis; user text and earlier final phrases remain outside its span.</summary>
internal sealed class DictationDraftComposition
{
    private readonly Func<DictationDraftObservation> read;
    private readonly Func<DesktopDraftUpdate, CancellationToken, Task<DesktopOperationResult>> write;
    private readonly Func<string, CancellationToken, Task<string>>? correctFinal;
    private DictationDraftObservation expected;
    private DictatedTextInsertion anchor;
    private long sequence;
    private bool busy;
    internal bool Writing { get; private set; }
    internal bool HasWritten { get; private set; }
    internal string PendingText { get; private set; } = "";
    internal string? Interrupted { get; private set; }

    internal static DictationDraftComposition CreateMain(IDesktopPluginHost host, IDesktopTextEntry target,
        Func<bool> ownerCurrent, Func<string, CancellationToken, Task<string>>? correctFinal = null)
    {
        if (host is not IDesktopDefaultTextEntryHost { SupportsDraftPresentation: true } composer ||
            !ReferenceEquals(composer.DefaultTextEntry, target))
            throw new InvalidOperationException("This Main host does not support protected incremental dictation. One-off recording remains available.");
        DictationDraftObservation Read()
        {
            var draft = host.GetDraft();
            return new(draft, target.SelectionStart, target.SelectionLength, ownerCurrent() &&
                ReferenceEquals(composer.DefaultTextEntry, target) && draft.Text == target.Text &&
                target is not IDesktopTextEntryLifetime { AvailabilityToken.IsCancellationRequested: true });
        }
        return new(Read, host.ReplaceDraftAsync, correctFinal);
    }

    internal DictationDraftComposition(Func<DictationDraftObservation> read,
        Func<DesktopDraftUpdate, CancellationToken, Task<DesktopOperationResult>> write,
        Func<string, CancellationToken, Task<string>>? correctFinal = null)
    {
        this.read = read; this.write = write; this.correctFinal = correctFinal;
        expected = read();
        if (!Valid(expected)) throw new InvalidOperationException("The current Main draft and caret are unavailable for incremental dictation.");
        anchor = new(expected.Draft.Text, expected.Start, expected.Length);
    }

    // Owner event handlers call this for user edits, selection/focus/owner changes,
    // including changes that are later undone. A confirmed self-write alone rebases.
    internal void Interrupt(string reason) => Interrupted ??= reason;
    internal bool MatchesCurrent() => Interrupted is null && Matches(read());

    internal async Task<DesktopOperationResult> ApplyAsync(long eventSequence, string transcript, bool final, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Interrupted is not null) return new(DesktopOperationState.Rejected, Interrupted);
        if (busy) return Refuse("Recognition updates must be applied in order. The pending words were retained.");
        if (eventSequence <= sequence || eventSequence <= 0)
            return Refuse("An old recognition update was refused. Dictation stopped without replaying it.");
        if (transcript is null || transcript.Length > DictationReview.MaximumCharacters)
            return Refuse("The recognition update exceeds the dictation text limit.");
        sequence = eventSequence; PendingText = transcript.Trim(); busy = true;
        try
        {
            if (!Matches(read())) return Refuse("The Main draft, cursor or owner changed. The pending words were retained.");
            // Only final dictated prose reaches the already configured corrector;
            // surrounding user text never becomes model input and partials stay fast.
            if (final && correctFinal is not null && PendingText.Length != 0)
            {
                var corrected = await correctFinal(PendingText, token);
                token.ThrowIfCancellationRequested();
                if (corrected is null || corrected.Length > DictationReview.MaximumCharacters)
                    return Refuse("The corrected phrase exceeds the dictation text limit.");
                PendingText = corrected.Trim();
            }
            if (Interrupted is not null || !Matches(read()))
                return Refuse("The Main draft, cursor or owner changed during recognition. The pending words were retained.");
            var insertion = PendingText.Length == 0 ? anchor.Text.Substring(anchor.Start, anchor.Length) : anchor.Prepare(PendingText);
            var text = anchor.Text[..anchor.Start] + insertion + anchor.Text[(anchor.Start + anchor.Length)..];
            if (text.Length > DesktopPluginContract.MaxTextChars)
                return Refuse("The Main draft is full. The pending words were retained.");
            var caret = anchor.Start + insertion.Length;
            if (text != expected.Draft.Text)
            {
                if (expected.Draft.Revision == long.MaxValue) return Refuse("The draft revision cannot advance safely.");
                var update = new DesktopDraftUpdate(expected.Draft.Binding, expected.Draft.Revision, text)
                {
                    Presentation = new(new(expected.Start, expected.Length), new(caret, 0), FadeChangedText: true)
                };
                DesktopOperationResult result;
                Writing = true;
                try { result = await write(update, token); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { return Unconfirmed("The text-update receipt was lost. Inspect Main before recovering the retained words."); }
                finally { Writing = false; }
                if (result.State != DesktopOperationState.Rejected) HasWritten = true;
                if (result.State != DesktopOperationState.Completed)
                {
                    if (result.State != DesktopOperationState.Rejected)
                        return Unconfirmed("The text update was not confirmed. Inspect Main before recovering the retained words.");
                    return Refuse(result.Message ?? "Main refused the text update. The pending words were retained.");
                }
                DictationDraftObservation after;
                try { after = read(); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { return Unconfirmed("The updated Main draft could not be read back. Inspect it before recovering the retained words."); }
                if (Interrupted is not null || !Valid(after) || after.Draft.Binding != expected.Draft.Binding ||
                    after.Draft.Revision != expected.Draft.Revision + 1 || after.Draft.Text != text || after.Start != caret || after.Length != 0)
                    return Unconfirmed("Main did not confirm the predicted text, revision and caret. Dictation stopped without retrying.");
                expected = after;
            }
            if (final)
            {
                // A same-text final does not create a write, fade or revision. The
                // next phrase begins at the last confirmed cursor, not the old span.
                anchor = new(expected.Draft.Text, expected.Start, expected.Length);
                PendingText = "";
            }
            return new(DesktopOperationState.Completed);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Interrupt("Recognition or final correction was interrupted. The pending words were retained.");
            throw;
        }
        finally { busy = false; }
    }

    private bool Matches(DictationDraftObservation value) => Valid(value) && value.Draft == expected.Draft &&
        value.Start == expected.Start && value.Length == expected.Length;
    private static bool Valid(DictationDraftObservation value) => value.Available && value.Draft.Revision >= 0 &&
        value.Draft.Text.Length <= DesktopPluginContract.MaxTextChars && value.Start >= 0 && value.Length >= 0 &&
        value.Length <= value.Draft.Text.Length && value.Start <= value.Draft.Text.Length - value.Length;
    private DesktopOperationResult Refuse(string message)
    { Interrupt(message); return new(DesktopOperationState.Rejected, Interrupted); }
    private DesktopOperationResult Unconfirmed(string message)
    { Interrupt(message); return new(DesktopOperationState.Uncertain, Interrupted); }
}
