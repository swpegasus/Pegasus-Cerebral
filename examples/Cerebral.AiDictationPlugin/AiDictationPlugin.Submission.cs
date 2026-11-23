using Cerebral.Desktop.Plugins;

namespace Cerebral.AiDictationPlugin;

public sealed partial class AiDictationPlugin
{
    private sealed record PreparedRearm(string RequestId, DesktopConversationBinding Binding, long Revision,
        IDesktopTextEntry Target, long ControlVersion);
    private PreparedRearm? preparedRearm;

    private bool SubmissionViewMatches(IDesktopTextEntry view, IDesktopTextEntry main, DesktopConversationBinding binding)
    {
        // The host owns both views. Equal IDs alone confer neither identity nor
        // write authority: the recording must already own this exact Main target,
        // current binding, revision, text and cursor. The view is never retained.
        var draft = host!.GetDraft();
        return IsCurrentTarget(main) && draft.Binding == binding && draft.Revision >= 0 &&
            draft.Text == main.Text && view.Id == main.Id && view.Text == draft.Text &&
            view.SelectionStart == main.SelectionStart && view.SelectionLength == main.SelectionLength;
    }

    public Task<DesktopOperationResult> PrepareSubmissionAsync(string requestId, IDesktopTextEntry target,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (requestId.Length > 256 || requestId.Contains('\0')) throw new ArgumentException("Invalid submission request identity.", nameof(requestId));
        return PrepareSubmissionCoreAsync(requestId, target, cancellationToken);
    }

    public void SubmissionAcknowledged(DesktopTextEntrySubmissionAcknowledgment acknowledgment)
    {
        ArgumentNullException.ThrowIfNull(acknowledgment);
        var prepared = preparedRearm;
        if (prepared is null || prepared.RequestId != acknowledgment.RequestId) return;
        preparedRearm = null;
        if (!CallbacksAvailable || submissionTarget is not null || controlActionPending ||
            prepared.ControlVersion != targetLockVersion || prepared.Binding != acknowledgment.Binding ||
            prepared.Revision != acknowledgment.SubmittedDraftRevision || prepared.Revision == long.MaxValue ||
            acknowledgment.EmptyDraftRevision != prepared.Revision + 1 || !autoTranscribeOnSilence ||
            !ReferenceEquals(ReadMainPromptTarget(), prepared.Target) || !IsCurrentTarget(prepared.Target) ||
            session is not { IsActive: false, HasPendingPhraseAudio: false } ready ||
            ready.Review.HasText || ready.Review.InsertionUnconfirmed) return;
        var draft = host!.GetDraft();
        if (draft.Binding != prepared.Binding || draft.Revision != acknowledgment.EmptyDraftRevision ||
            draft.Text.Length != 0 || prepared.Target.Text.Length != 0 ||
            prepared.Target.SelectionStart != 0 || prepared.Target.SelectionLength != 0) return;
        try
        {
            // The receipt rebases only onto the new empty composer. StartAutomatic
            // listens for new speech without focusing an editor or sending a message.
            ready.BeginDestinationRun(prepared.Target);
            automaticArmed = true; automaticPaused = false;
            StartAutomatic(prepared.Target);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { automaticArmed = false; automaticPaused = true; ReportControlError(error); }
    }
}
