using Cerebral.Desktop.Plugins;
using Cerebral.Dictation;

namespace Cerebral.AiDictationPlugin;

/// <summary>One Start/Stop lease. Only a confirmed, exactly predicted self-write advances its cursor.</summary>
internal sealed class DictationDestinationRun(IDesktopTextEntry target, DictatedTextInsertion expected,
    Func<long?> readRevision, long? initialRevision)
{
    internal IDesktopTextEntry Target { get; } = target;
    internal DictatedTextInsertion Expected { get; private set; } = expected;
    internal bool StopRequested { get; set; }
    internal string? Interrupted { get; private set; }
    internal bool Writing { get; private set; }
    private long? revision = initialRevision;

    internal void Interrupt(string reason) { Interrupted ??= reason; StopRequested = true; }

    internal bool Matches()
    {
        if (Interrupted is not null || Target is IDesktopTextEntryLifetime { AvailabilityToken.IsCancellationRequested: true }) return false;
        return Expected.Matches(Target) && (revision is null || readRevision() == revision);
    }

    internal async Task<DesktopOperationResult> InsertPhraseAsync(DictationReview pending, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Matches())
        {
            Interrupt(Interrupted ?? "The destination text, cursor or revision changed. The pending phrase was retained.");
            return new(DesktopOperationState.Rejected, Interrupted);
        }
        if (revision == long.MaxValue)
        { Interrupt("The destination revision cannot advance safely. The pending phrase was retained."); return new(DesktopOperationState.Rejected, Interrupted); }
        Writing = true;
        try
        {
            var result = await pending.AcceptAsync(token, insertion =>
            {
                var predicted = new DictatedTextInsertion(Expected.Text[..Expected.Start] + insertion +
                    Expected.Text[(Expected.Start + Expected.Length)..], Expected.Start + insertion.Length, 0);
                var nextRevision = readRevision();
                if (Interrupted is not null || !predicted.Matches(Target) || revision is { } old && nextRevision != old + 1) return false;
                Expected = predicted;
                // Generic/UIA entries expose no revision. Never invent one from a readback.
                if (revision is not null) revision = nextRevision;
                return true;
            });
            if (result.State != DesktopOperationState.Completed)
                Interrupt(result.Message ?? "Insertion was not confirmed. Inspect the destination; the phrase was retained.");
            return result;
        }
        catch
        {
            Interrupt("Insertion was interrupted. Inspect the destination before recovering the retained phrase.");
            throw;
        }
        finally { Writing = false; }
    }
}
