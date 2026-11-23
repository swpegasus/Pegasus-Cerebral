using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private const int MaximumDiagnosticSections = 32;

    private object GetLongFormStatus(string sessionId)
    {
        _ = store.GetSessionMetadata(sessionId) ?? throw new ArgumentException("Session not found.");
        var state = store.Get<LongFormState>("longform", sessionId);
        if (state is null)
            return new
            {
                sessionId,
                tracked = false,
                message = "This conversation has no persisted long-form workflow."
            };

        var sections = state.Sections.Take(MaximumDiagnosticSections).Select((section, index) => new
        {
            index,
            title = Bound(section.Title, 120),
            purpose = Bound(section.Purpose, 500),
            targetWords = Math.Max(0, section.TargetWords),
            completedAtWords = section.CompletedAtWords
        }).ToList();
        return new
        {
            sessionId = state.SessionId,
            tracked = true,
            sourceJobId = Bound(state.SourceJobId, 256),
            sourceRequestSequence = Math.Max(0, state.SourceRequestSequence),
            revision = Math.Max(0, state.Revision),
            path = Bound(state.Path, 32768),
            targetLabel = Bound(state.TargetLabel, 512),
            targetWords = Math.Max(0, state.TargetWords),
            wordsPerPage = Math.Max(0, state.WordsPerPage),
            verifiedWords = Math.Max(0, state.VerifiedWords),
            remainingWords = Math.Max(0, state.TargetWords - state.VerifiedWords),
            verifiedLines = Math.Max(0, state.VerifiedLines),
            verifiedBytes = Math.Max(0, state.VerifiedBytes),
            sha256 = ValidHash(state.Sha256),
            currentSectionIndex = Math.Clamp(state.CurrentSectionIndex, 0, state.Sections.Count),
            sectionCount = state.Sections.Count,
            sections,
            sectionsTruncated = state.Sections.Count > sections.Count,
            complete = LongFormWorkflow.Complete(state),
            pendingAppend = PendingAppendMetadata(state),
            updatedAt = state.UpdatedAt
        };
    }

    // Pending append content and the recent prose tail deliberately never cross this diagnostics boundary.
    private static object? PendingAppendMetadata(LongFormState state)
    {
        var pending = state.PendingAppend;
        if (pending is null) return null;
        var content = pending.Content ?? "";
        return new
        {
            jobId = Bound(pending.JobId, 256),
            step = Math.Max(0, pending.Step),
            path = Bound(pending.Path, 32768),
            expectedSha256 = ValidHash(pending.ExpectedSha256),
            expectedBytes = Math.Max(0, pending.ExpectedBytes),
            fingerprint = ValidHash(pending.Fingerprint),
            contentCharacters = content.Length,
            createdAt = pending.CreatedAt
        };
    }

    private static string? ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;

    private static string? Bound(string? value, int maximum) => value is null || value.Length <= maximum ? value : value[..maximum];
}
