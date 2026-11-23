using Cerebral.Desktop.Plugins;
using Cerebral.Dictation;

namespace Cerebral.AiDictationPlugin;

/// <summary>Only dictated prose is reviewed here. Destination text never becomes grammar/model input.</summary>
internal sealed class DictationReview
{
    private IDesktopTextEntry? target;
    private DictatedTextInsertion? insertion;
    internal string Text { get; private set; } = "";
    internal long Revision { get; private set; }
    internal bool HasText => !string.IsNullOrWhiteSpace(Text);
    internal bool InsertionUnconfirmed { get; private set; }
    internal IDesktopTextEntry? Target => target;
    internal const int MaximumCharacters = 32768;

    internal void RetainForInspection(IDesktopTextEntry destination, DictatedTextInsertion captured, string text, bool mayAlreadyBeInserted)
    {
        if (text.Length > MaximumCharacters) throw new InvalidOperationException("The retained recognition exceeds its text limit.");
        target = destination; insertion = captured; Text = text; Revision++;
        InsertionUnconfirmed |= mayAlreadyBeInserted;
    }

    internal void Append(IDesktopTextEntry destination, DictatedTextInsertion captured, string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return;
        if (target is not null && !ReferenceEquals(target, destination))
            throw new InvalidOperationException("Choose a destination for the existing review before recording into another one.");
        var joined = DictatedTextInsertion.JoinSegments(Text, segment);
        if (joined.Length > MaximumCharacters) throw new InvalidOperationException("The dictation review is full. Accept or clear it before another segment.");
        target ??= destination; insertion ??= captured;
        Text = joined; Revision++;
    }

    internal void Edit(string text)
    {
        if (text.Length > MaximumCharacters) throw new ArgumentException("The dictated review is too long.");
        if (text == Text) return;
        Text = text; Revision++;
    }

    internal void ChooseTarget(IDesktopTextEntry destination)
    {
        if (InsertionUnconfirmed) throw new InvalidOperationException("The previous insertion is unconfirmed. Inspect the destination, then clear this review before another insertion.");
        target = destination; insertion = DictatedTextInsertion.Capture(destination); Revision++;
    }

    internal async Task<DesktopOperationResult> AcceptAsync(CancellationToken token, Func<string, bool>? confirmInsertion = null)
    {
        token.ThrowIfCancellationRequested();
        if (InsertionUnconfirmed) return new(DesktopOperationState.Uncertain, "The prior insertion is unconfirmed and will not be retried. Inspect the destination first.");
        if (!HasText || target is null || insertion is null)
            return new(DesktopOperationState.Rejected, "Choose a destination and review the transcript first.");
        if (target is IDesktopTextEntryLifetime { AvailabilityToken.IsCancellationRequested: true } || !insertion.Matches(target))
            return new(DesktopOperationState.Rejected, "The selected text or cursor changed. The transcript is retained; select the destination again before inserting.");
        var revision = Revision;
        InsertionUnconfirmed = true; // An exception after dispatch never permits replay.
        var prepared = insertion.PrepareReviewed(Text);
        var result = await target.InsertAsync(prepared, token);
        if (result.State == DesktopOperationState.Completed && confirmInsertion is not null)
        {
            try
            {
                if (!confirmInsertion(prepared))
                    return new(DesktopOperationState.Uncertain, "The destination did not confirm the predicted text and cursor. Dictation stopped; inspect it before recovering the retained phrase.");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { return new(DesktopOperationState.Uncertain, "Insertion confirmation was lost. Dictation stopped; inspect the destination before recovering the retained phrase."); }
        }
        if (result.State == DesktopOperationState.Rejected) InsertionUnconfirmed = false;
        if (result.State == DesktopOperationState.Completed && Revision != revision)
            return new(DesktopOperationState.Uncertain, "Pending text changed while insertion was in progress. Inspect the destination before recovery; insertion will not repeat.");
        // Accepted/uncertain external writes are never replayed as a second insertion.
        if (result.State == DesktopOperationState.Completed && Revision == revision) Clear();
        return result;
    }

    internal void Clear() { Text = ""; target = null; insertion = null; InsertionUnconfirmed = false; Revision++; }
}
